"""Explicit two-way source sync. Compare contents directly; never copy Git or runtime data."""
from __future__ import annotations

import argparse
from contextlib import contextmanager
from datetime import datetime
import json
import os
from pathlib import Path
import subprocess
import sys
import uuid

ROOT = Path(__file__).resolve().parents[2]
PRIVATE_ROOTS = {'.git', '.artifacts', 'artifacts', '.vs', 'data', 'storage', 'backups',
                 'imports', 'exports', 'logs', 'secrets', 'certificates', 'private-exams',
                 'real-exams', '机考单题文件', '机考答案文件'}
SOURCE_ROOTS = {'src', 'tests', 'eng', 'docs', 'schemas', 'samples', 'deploy', 'packaging', '.github'}
SOURCE_SUFFIXES = {'.cs', '.csproj', '.props', '.targets', '.xaml', '.py', '.ps1', '.md',
                   '.yml', '.yaml', '.sql', '.sh', '.cmd', '.bat', '.manifest', '.resx', '.sln'}
ROOT_NAMES = {'start-local.cmd', 'sync-code.cmd'}
PRIVATE_SUFFIXES = {'.pfx', '.p12', '.pem', '.key', '.db', '.sqlite', '.dump', '.backup', '.log'}


def filesystem_path(path: Path) -> Path:
    """Use Win32 extended paths without requiring a machine-wide long-path setting."""
    if os.name != 'nt':
        return path
    value = str(path.absolute())
    if value.startswith('\\\\?\\'):
        return path
    if value.startswith('\\\\'):
        return Path('\\\\?\\UNC\\' + value[2:])
    return Path('\\\\?\\' + value)


def safe_path(root: Path, relative: str) -> Path:
    part = Path(relative)
    if part.is_absolute() or '..' in part.parts or ':' in relative:
        raise ValueError('同步路径必须位于项目目录内。')
    path = root / part
    # Check ancestors before resolving, so junctions cannot silently redirect a write.
    for current in (path, *path.parents):
        native = filesystem_path(current)
        if native.is_symlink() or native.is_junction():
            raise ValueError(f'同步路径包含链接，未处理：{relative}')
        if current == root:
            break
    path.relative_to(root)
    native = filesystem_path(path)
    if native.exists() and not native.is_file():
        raise ValueError(f'同步文件与目录冲突：{relative}')
    return native


def public_path(relative: str) -> bool:
    parts = Path(relative).parts
    lower = [part.lower() for part in parts]
    name = lower[-1]
    return not (any(p in PRIVATE_ROOTS | {'bin', 'obj', '__pycache__'} for p in lower)
                or name.startswith('.env') or name in {'server.json', 'local-settings.json', 'management-connection.json'}
                or name.startswith('appsettings.') and ('.local.' in name or '.development.' in name)
                or Path(name).suffix in PRIVATE_SUFFIXES or '.db-' in name or '.sqlite-' in name)


def git(root: Path, *args: str, check: bool = True) -> subprocess.CompletedProcess:
    return subprocess.run(['git', '-c', 'core.longpaths=true', '-C', str(root), *args], capture_output=True, check=check)


def names(result: subprocess.CompletedProcess) -> set[str]:
    return {item.decode('utf-8') for item in result.stdout.split(b'\0') if item}


def source_files(root: Path) -> set[str]:
    tracked = names(git(root, 'ls-files', '-z'))
    added = names(git(root, 'ls-files', '--others', '--exclude-standard', '-z'))
    new_code = {name for name in added if name in ROOT_NAMES or
                Path(name).parts[0] in SOURCE_ROOTS and
                (Path(name).suffix.lower() in SOURCE_SUFFIXES or
                 Path(name).name in {'packages.lock.json', 'packages.win-x64.lock.json'})}
    return {name for name in tracked | new_code if public_path(name)}


def read(path: Path) -> bytes | None:
    return path.read_bytes() if path.exists() else None


def same(a: bytes | None, b: bytes | None) -> bool:
    if a == b:
        return True
    # Git normalizes text line endings. Preserve each actual file's bytes when copying.
    return a is not None and b is not None and b'\0' not in a and b'\0' not in b and \
        a.replace(b'\r\n', b'\n') == b.replace(b'\r\n', b'\n')


def write(path: Path, content: bytes) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + '.sync-' + uuid.uuid4().hex + '.tmp')
    try:
        temporary.write_bytes(content)
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


@contextmanager
def lock(path: Path):
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open('a+b') as stream:
        stream.seek(0)
        if not stream.read(1):
            stream.write(b'0')
            stream.flush()
        stream.seek(0)
        if os.name == 'nt':
            import msvcrt
            try:
                msvcrt.locking(stream.fileno(), msvcrt.LK_NBLCK, 1)
            except OSError as error:
                raise RuntimeError('另一项同步正在运行，请等它完成。') from error
        else:
            import fcntl
            fcntl.flock(stream, fcntl.LOCK_EX | fcntl.LOCK_NB)
        try:
            yield
        finally:
            if os.name == 'nt':
                stream.seek(0)
                msvcrt.locking(stream.fileno(), msvcrt.LK_UNLCK, 1)


def synchronize(development: Path, testing: Path, dry_run: bool = False) -> int:
    development, testing = development.resolve(), testing.resolve()
    if development == testing or development in testing.parents or testing in development.parents:
        raise ValueError('同步的两个项目必须是彼此独立的目录。')
    for root in (development, testing):
        if not (root / 'Huaizhou.ExamSystem.sln').is_file():
            raise ValueError(f'不是考试系统项目目录：{root}')
        # Validate the root itself before using an ignored directory for state/backups.
        safe_path(root, 'README.md')
    state = testing / '.artifacts' / 'folder-sync'
    safe_path(testing, '.artifacts/folder-sync/state.json')
    pair = {'development': str(development), 'testing': str(testing)}
    with lock(state / 'sync.lock'):
        manifest_path = state / 'state.json'
        previous = json.loads(manifest_path.read_text(encoding='utf-8')) if manifest_path.exists() else None
        if previous and (previous.get('version') != 1 or previous.get('pair') != pair):
            raise ValueError('同步记录属于其他目录，未执行覆盖。')
        candidates = source_files(development) | source_files(testing) | set(previous['paths'] if previous else [])
        candidates = sorted(name for name in candidates if public_path(name))
        baseline = state / 'baseline'
        operations, snapshots, conflicts = [], {}, []
        for relative in candidates:
            left_path, right_path = safe_path(development, relative), safe_path(testing, relative)
            left, right = read(left_path), read(right_path)
            if same(left, right):
                snapshots[relative] = left
                continue
            if previous and relative in previous['paths']:
                base = read(safe_path(baseline, relative))
            elif previous:
                base = None
            else:
                left_base = git(development, 'show', 'HEAD:' + relative, check=False)
                right_base = git(testing, 'show', 'HEAD:' + relative, check=False)
                if left_base.returncode == 0 and right_base.returncode == 0 and same(left_base.stdout, right_base.stdout):
                    base = left_base.stdout
                elif left_base.returncode != 0 and right_base.returncode != 0:
                    base = None
                else:
                    conflicts.append(relative)
                    continue
            if same(left, base):
                operations.append((development, relative, left, right))
                snapshots[relative] = right
            elif same(right, base):
                operations.append((testing, relative, right, left))
                snapshots[relative] = left
            else:
                conflicts.append(relative)
        if conflicts:
            print('两边存在不同改动，本次未同步任何文件。请合并以下文件，再重新同步：')
            for relative in conflicts:
                print('  ' + relative)
            return 2
        for target, relative, before, after in operations:
            action = '删除' if after is None else '新增' if before is None else '更新'
            print(f'{action} {target.name}/{relative}')
        print(f'项目文件 {len(candidates)} 个，待同步 {len(operations)} 个；试题、运行数据和 Git 目录不参与。')
        if dry_run:
            return 0
        backup = state / 'backups' / (datetime.now().strftime('%Y%m%d-%H%M%S') + '-' + uuid.uuid4().hex[:8])
        journal = []
        for target, relative, before, after in operations:
            path = safe_path(target, relative)
            if read(path) != before:
                raise RuntimeError(f'同步过程中发现新改动，已停止：{relative}')
            if before is not None:
                write(safe_path(backup / target.name, relative), before)
            journal.append({'target': target.name, 'path': relative, 'previouslyExisted': before is not None,
                            'deleted': after is None})
            write(backup / 'changes.json', json.dumps(journal, ensure_ascii=False, indent=2).encode('utf-8'))
            if after is None:
                path.unlink()
            else:
                write(path, after)
        for relative, content in snapshots.items():
            path = safe_path(baseline, relative)
            if content is None:
                path.unlink(missing_ok=True)
            elif read(path) != content:
                write(path, content)
        write(manifest_path, json.dumps({'version': 1, 'pair': pair, 'paths': candidates},
                                       ensure_ascii=False, indent=2).encode('utf-8'))
        print('同步完成。' + (f'被替换的原文件保存在 {backup}' if operations else '两边已一致。'))
        return 0


def main() -> int:
    parser = argparse.ArgumentParser(description='双向同步两个考试系统目录的代码，保留各自的试题和运行数据。')
    default_testing = ROOT if ROOT.name == 'exam-system-test' else ROOT.parent / 'exam-system-test'
    default_development = ROOT if ROOT.name == 'exam-system' else ROOT.parent / 'exam-system'
    parser.add_argument('--development', type=Path, default=default_development)
    parser.add_argument('--testing', type=Path, default=default_testing)
    parser.add_argument('--check', action='store_true', help='只显示待同步文件，不写入项目文件')
    args = parser.parse_args()
    try:
        return synchronize(args.development, args.testing, args.check)
    except (OSError, ValueError, RuntimeError, subprocess.SubprocessError) as error:
        print(f'同步未完成：{error}', file=sys.stderr)
        return 1


if __name__ == '__main__':
    if hasattr(sys.stdout, 'reconfigure'):
        sys.stdout.reconfigure(encoding='utf-8')
        sys.stderr.reconfigure(encoding='utf-8')
    raise SystemExit(main())
