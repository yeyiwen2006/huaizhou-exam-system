"""Persistent, loopback-only manual development session; owns only its child processes."""
from __future__ import annotations

import argparse
from contextlib import contextmanager
import json
import os
from pathlib import Path
import secrets
import shutil
import socket
import ssl
import subprocess
import sys
import time
import urllib.error
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'eng' / 'testing'))
from isolation import private_environment, windows_job

PROJECTS = {
    'host': ('eng/development/Exam.LocalHost/Exam.LocalHost.csproj', 'net10.0'),
    'admin': ('src/Exam.Admin/Exam.Admin.csproj', 'net10.0-windows'),
    'student': ('src/Exam.Student/Exam.Student.csproj', 'net10.0-windows10.0.19041.0')
}


def dump(path: Path, value: dict) -> None:
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding='utf-8')


def free_port(preferred: int) -> int:
    with socket.socket() as probe:
        try:
            probe.bind(('127.0.0.1', preferred))
        except OSError:
            probe.bind(('127.0.0.1', 0))
        return probe.getsockname()[1]


def postgres_bin(requested: Path | None) -> Path:
    candidates = [requested] if requested else []
    if not requested:
        executable = shutil.which('postgres')
        if executable:
            candidates.append(Path(executable).parent)
        installation = Path(os.environ.get('ProgramFiles', 'C:/Program Files')) / 'PostgreSQL'
        if installation.is_dir():
            candidates.extend(sorted(installation.glob('*/bin'), reverse=True))
    for candidate in candidates:
        if candidate and all((candidate / (name + '.exe')).is_file() for name in ('postgres', 'initdb', 'pg_ctl', 'psql')):
            version = subprocess.check_output([str(candidate / 'postgres.exe'), '--version'],
                                              creationflags=subprocess.CREATE_NO_WINDOW).decode('utf-8')
            if ' 18.' in version:
                return candidate.resolve()
    raise RuntimeError('未找到 PostgreSQL 18。安装后重试，或通过 --postgres-bin 指定其 bin 目录。')


@contextmanager
def session_lock(directory: Path):
    import msvcrt
    with (directory / 'session.lock').open('a+b') as stream:
        stream.seek(0)
        if not stream.read(1):
            stream.write(b'0')
            stream.flush()
        stream.seek(0)
        try:
            msvcrt.locking(stream.fileno(), msvcrt.LK_NBLCK, 1)
        except OSError as error:
            raise RuntimeError('这个调试环境已在运行，请使用原启动窗口。') from error
        try:
            yield
        finally:
            stream.seek(0)
            msvcrt.locking(stream.fileno(), msvcrt.LK_UNLCK, 1)


class Session:
    def __init__(self, directory: Path):
        self.directory = directory
        self.children = []
        self.logs = []
        self.jobs = {}

    def start(self, command: list[str], name: str, environment: dict):
        log = (self.directory / 'logs' / (name + '.log')).open('ab')
        self.logs.append(log)
        process = subprocess.Popen(command, cwd=ROOT, env=environment, stdout=log, stderr=log,
                                   creationflags=subprocess.CREATE_NO_WINDOW)
        self.children.append(process)
        self.jobs[process] = windows_job(process)
        return process

    def close_children(self):
        for process in reversed(self.children):
            if process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=10)
            job = self.jobs.pop(process, None)
            if job:
                kernel, handle = job
                kernel.TerminateJobObject(handle, 1)
                kernel.CloseHandle(handle)
        self.children.clear()

    def close(self):
        self.close_children()
        for log in self.logs:
            log.close()


def run(command: list[str], log: Path, environment: dict | None = None, timeout: int = 180) -> None:
    with log.open('ab') as stream:
        result = subprocess.run(command, cwd=ROOT, env=environment, stdout=stream, stderr=stream,
                                timeout=timeout, creationflags=subprocess.CREATE_NO_WINDOW)
    if result.returncode:
        raise RuntimeError(f'操作失败，请查看日志：{log}')


def sql(binary: Path, environment: dict, query: str) -> str:
    result = subprocess.run([str(binary / 'psql.exe'), '-X', '-t', '-A', '-v', 'ON_ERROR_STOP=1'],
                            input=query.encode('utf-8'), capture_output=True,
                            env=environment, timeout=15, creationflags=subprocess.CREATE_NO_WINDOW)
    if result.returncode:
        raise RuntimeError('调试数据库操作失败，请查看数据库日志。')
    return result.stdout.decode('utf-8').strip()


def wait_database(process, binary: Path, environment: dict) -> None:
    deadline = time.monotonic() + 30
    while time.monotonic() < deadline:
        if process.poll() is not None:
            raise RuntimeError('调试数据库提前退出，请查看 logs/database.log。')
        try:
            if sql(binary, environment, 'SELECT 1') == '1':
                return
        except (RuntimeError, subprocess.SubprocessError):
            pass
        time.sleep(.25)
    raise RuntimeError('调试数据库启动超时，请查看 logs/database.log。')


def wait_server(process, url: str, certificate: Path | None = None) -> dict:
    handlers = [urllib.request.ProxyHandler({})]
    if certificate:
        handlers.append(urllib.request.HTTPSHandler(context=ssl.create_default_context(cafile=str(certificate))))
    opener = urllib.request.build_opener(*handlers)
    last_status = None
    deadline = time.monotonic() + 45
    while time.monotonic() < deadline:
        if process.poll() is not None:
            raise RuntimeError('调试后台提前退出，请查看 logs/server-a.log 或 server-b.log。')
        try:
            with opener.open(url + '/health/ready', timeout=2) as response:
                result = json.load(response)
            if result.get('status') == 'ready' and result.get('localDebug') is True:
                return result
        except urllib.error.HTTPError as error:
            try:
                last_status = json.load(error)
            except ValueError:
                last_status = {'status': error.code}
        except (OSError, urllib.error.URLError, ValueError) as error:
            last_status = {'connection': str(error)}
        time.sleep(.25)
    raise RuntimeError(f'调试后台尚未就绪：{last_status}；请查看 logs/server-a.log 或 server-b.log。')


def prepare(directory: Path, args) -> dict:
    directory.resolve().relative_to(ROOT / '.artifacts')
    for path in (directory, *directory.parents):
        if path.is_symlink() or (hasattr(path, 'is_junction') and path.is_junction()):
            raise ValueError('调试目录不能是链接或联接点。')
        if path == ROOT:
            break
    marker = directory / '.owner.json'
    expected = {'kind': 'exam-local-debug', 'projectRoot': str(ROOT)}
    if directory.exists() and not marker.exists() and any(directory.iterdir()):
        raise ValueError('调试目录已有其他内容，请另选 .artifacts 内的新目录。')
    directory.mkdir(parents=True, exist_ok=True)
    if marker.exists():
        if json.loads(marker.read_text(encoding='utf-8')) != expected:
            raise ValueError('调试目录属于其他项目，未改动其中的数据。')
    else:
        dump(marker, expected)
    (directory / 'logs').mkdir(exist_ok=True)
    settings_path = directory / 'local-settings.json'
    if settings_path.exists():
        settings = json.loads(settings_path.read_text(encoding='utf-8'))
        for argument, name in ((args.https_port, 'httpsPort'), (args.admin_port, 'adminPort')):
            if argument is not None and argument != settings[name]:
                raise ValueError('已有调试环境保留原地址；使用另一个 --data-dir 创建不同端口的环境。')
        return settings
    settings = {'serverId': 'local-debug-' + uuid.uuid4().hex,
                'databasePort': free_port(5448), 'databasePassword': secrets.token_urlsafe(32),
                'httpsPort': free_port(args.https_port or 5001),
                'adminPort': free_port(args.admin_port or 5089), 'certificatePassword': secrets.token_urlsafe(32)}
    if len({settings['databasePort'], settings['httpsPort'], settings['adminPort']}) != 3:
        raise ValueError('调试端口重复，请指定不同端口。')
    dump(settings_path, settings)
    return settings


def build(directory: Path, no_build: bool):
    dotnet = shutil.which('dotnet')
    if not dotnet:
        raise RuntimeError('未找到 .NET SDK，请安装 global.json 指定的 .NET 10 SDK。')
    output = {}
    for name, (relative, framework) in PROJECTS.items():
        project = ROOT / relative
        if not no_build:
            print(f'正在编译{dict(host="调试后台", admin="考务端", student="学生端")[name]}…', flush=True)
            run([dotnet, 'build', str(project), '-c', 'Debug', '--nologo', '-v', 'minimal',
                 '-p:RestoreLockedMode=true'], directory / 'logs' / 'build.log', private_environment(), timeout=300)
        source = project.parent / 'bin' / 'Debug' / framework
        if not source.is_dir():
            raise RuntimeError('没有可用的 Debug 构建，请去掉 --no-build 后重试。')
        destination = directory / 'apps' / name
        shutil.copytree(source, destination, dirs_exist_ok=True)
        output[name] = destination
    return dotnet, output


def execute(args) -> int:
    if os.name != 'nt':
        raise RuntimeError('本机调试入口需要 Windows 桌面环境。')
    binary = postgres_bin(args.postgres_bin)
    directory = (args.data_dir or ROOT / '.artifacts' / 'local-debug').absolute()
    settings = prepare(directory, args)
    with session_lock(directory):
        dotnet, apps = build(directory, args.no_build)
        environment = private_environment()
        environment.update(PGHOST='127.0.0.1', PGPORT=str(settings['databasePort']), PGUSER='postgres',
                           PGDATABASE='postgres', PGPASSWORD=settings['databasePassword'], PGCLIENTENCODING='UTF8')
        cluster = directory / 'postgres'
        if not (cluster / 'PG_VERSION').is_file():
            password = directory / 'init-password.tmp'
            password.write_text(settings['databasePassword'], encoding='utf-8')
            try:
                print('正在准备独立的 UTF-8 测试数据库…', flush=True)
                run([str(binary / 'initdb.exe'), '-D', str(cluster), '-U', 'postgres',
                     '--auth-host=scram-sha-256', '--auth-local=scram-sha-256', '--pwfile', str(password),
                     '--encoding=UTF8', '--locale=C'], directory / 'logs' / 'initialize.log', environment)
            finally:
                password.unlink(missing_ok=True)
        database_session = Session(directory)
        app_session = Session(directory)
        database = None
        try:
            database = database_session.start([str(binary / 'postgres.exe'), '-D', str(cluster),
                '-c', 'listen_addresses=127.0.0.1', '-c', f'port={settings["databasePort"]}',
                '-c', 'max_connections=64'], 'database', environment)
            wait_database(database, binary, environment)
            if sql(binary, environment, "SELECT 1 FROM pg_database WHERE datname='exam_local_debug'") != '1':
                sql(binary, environment, 'CREATE DATABASE exam_local_debug ENCODING \'UTF8\'')
            config = str(directory / 'local-settings.json')
            host = str(apps['host'] / 'Exam.LocalHost.dll')
            run([dotnet, host, '--prepare', config], directory / 'logs' / 'certificates.log', private_environment())
            # Each endpoint has its own node identity/certificate, but both use one local database.
            # This satisfies the desktop trust protocol without claiming replicated persistence.
            first = app_session.start([dotnet, host, '--config', config, '--node', 'a'], 'server-a', private_environment())
            admin_url = f'http://127.0.0.1:{settings["adminPort"]}'
            wait_server(first, admin_url)
            first_https = f'https://127.0.0.1:{settings["httpsPort"]}'
            wait_server(first, first_https, directory / 'local-node-a.cer')
            second = app_session.start([dotnet, host, '--config', config, '--node', 'b'], 'server-b', private_environment())
            wait_server(second, f'https://127.0.0.2:{settings["httpsPort"]}', directory / 'local-node-b.cer')
            shutil.copyfile(directory / 'student-server.json', apps['student'] / 'server.json')
            dump(directory / 'connection.json', {'adminUrl': admin_url, 'studentHost': '127.0.0.1',
                 'studentPort': settings['httpsPort'], 'serverId': settings['serverId'], 'localDebug': True})
            print(f'\n后台已就绪。考务端地址：127.0.0.1，端口 {settings["adminPort"]}（已自动配置）', flush=True)
            print(f'学生端地址：127.0.0.1，端口 {settings["httpsPort"]}（已自动配置）', flush=True)
            print(f'测试数据目录：{directory}', flush=True)
            print('两个回环节点共用一个测试数据库，供本机业务调试；不代表主备部署。', flush=True)
            if args.check:
                print('HTTP、两个 HTTPS 端点和数据库启动检查通过。', flush=True)
                return 0
            client_environment = private_environment()
            exports = directory / 'exports'
            exports.mkdir(exist_ok=True)
            client_environment.update(EXAM_ADMIN_SERVER_URL=admin_url,
                                      EXAM_ADMIN_DATA_DIRECTORY=str(directory / 'admin-data'),
                                      EXAM_LOCAL_EXPORT_DIRECTORY=str(exports),
                                      EXAM_STUDENT_DATA_DIRECTORY=str(directory / 'student-data'))
            clients = {}

            def open_client(name):
                if name in clients and clients[name].poll() is None:
                    print('该客户端已打开。')
                    return
                command = [str(apps[name] / ('Exam.Admin.exe' if name == 'admin' else 'Exam.Student.exe'))]
                if name == 'student':
                    command.append('--local-debug')
                clients[name] = app_session.start(command, name, client_environment)

            if not args.no_clients:
                open_client('admin')
            print('首次使用请在考务端注册账号，再创建考试、导入试题和考生名单。')
            print('输入 A 打开考务端，S 打开学生端，Q 停止本次调试。数据会保留，便于下次继续。')
            while True:
                command = input('> ').strip().lower()
                if command in ('q', 'quit', 'exit'):
                    break
                if command in ('a', 's'):
                    open_client('admin' if command == 'a' else 'student')
                else:
                    print('请输入 A、S 或 Q。')
                if first.poll() is not None or second.poll() is not None or database.poll() is not None:
                    raise RuntimeError('有后台进程退出，请查看本次调试日志。')
            return 0
        finally:
            app_session.close()
            try:
                if database and database.poll() is None:
                    # Read the PID from the owned directory instead of round-tripping a Windows
                    # Unicode path through psql's console encoding. The original process handle
                    # is still alive; this is never a global process-name or service stop.
                    pid = (cluster / 'postmaster.pid').read_bytes().splitlines()[0].decode('ascii')
                    if int(pid) != database.pid:
                        raise RuntimeError('数据库进程与本次启动不匹配，未发送停库命令。')
                    run([str(binary / 'pg_ctl.exe'), '-D', str(cluster), '-m', 'fast', '-w', 'stop'],
                        directory / 'logs' / 'shutdown.log', environment, timeout=30)
            finally:
                database_session.close()
            print('本次启动的进程已停止，已保存的测试数据保留在当前项目内。', flush=True)


def main():
    parser = argparse.ArgumentParser(description='启动持久保存的本机考试调试环境。')
    parser.add_argument('--postgres-bin', type=Path)
    parser.add_argument('--data-dir', type=Path, help='必须位于当前项目的 .artifacts 内')
    parser.add_argument('--https-port', type=int)
    parser.add_argument('--admin-port', type=int)
    parser.add_argument('--no-build', action='store_true', help='复用本目录已有 Debug 构建')
    parser.add_argument('--no-clients', action='store_true', help='启动后不自动打开考务端')
    parser.add_argument('--check', action='store_true', help='后台就绪检查通过后退出，不打开客户端')
    args = parser.parse_args()
    try:
        return execute(args)
    except (KeyboardInterrupt, EOFError):
        print('\n调试已结束。')
        return 0
    except (OSError, ValueError, RuntimeError, subprocess.SubprocessError) as error:
        print(f'启动未完成：{error}', file=sys.stderr)
        return 1


if __name__ == '__main__':
    if hasattr(sys.stdout, 'reconfigure'):
        sys.stdout.reconfigure(encoding='utf-8')
        sys.stderr.reconfigure(encoding='utf-8')
    raise SystemExit(main())
