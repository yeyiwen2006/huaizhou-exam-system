"""Exercise real synchronization without touching either user's checkout."""
from contextlib import redirect_stdout
import io
from pathlib import Path
import subprocess
import tempfile
import unittest
import shutil
import os
import stat
from sync_code import synchronize, filesystem_path


class SynchronizationTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='exam-sync-test-')
        self.addCleanup(self.cleanup)
        self.development = Path(self.temporary.name) / 'exam-system'
        self.testing = Path(self.temporary.name) / 'exam-system-test'
        for root in (self.development, self.testing):
            root.mkdir()
            self.put(root, 'Huaizhou.ExamSystem.sln', 'test solution\n')
            self.put(root, 'README.md', '说明\n')
            self.put(root, 'src/测试.cs', '原代码\n')
            self.put(root, '.gitignore', '.artifacts/\n')
            self.command(root, 'init', '-q')
            self.command(root, 'config', 'core.longpaths', 'true')
            self.command(root, 'add', '.')
            self.command(root, '-c', 'user.name=Sync Test', '-c', 'user.email=sync@example.invalid', 'commit', '-qm', 'base')

    @staticmethod
    def put(root, name, text):
        path = filesystem_path(root / name)
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding='utf-8')

    def cleanup(self):
        target = Path(self.temporary.name).resolve()
        target.relative_to(Path(tempfile.gettempdir()).resolve())
        if not target.name.startswith('exam-sync-test-'):
            raise ValueError('Unexpected test directory')
        def remove_readonly(function, path, error):
            if isinstance(error, PermissionError):
                os.chmod(path, stat.S_IWRITE)
                function(path)
            else:
                raise error
        shutil.rmtree(filesystem_path(target), onexc=remove_readonly)
        self.temporary.cleanup()

    @staticmethod
    def command(root, *args):
        subprocess.run(['git', '-C', str(root), *args], check=True, capture_output=True)

    def sync(self):
        with redirect_stdout(io.StringIO()):
            return synchronize(self.development, self.testing)

    def test_both_directions_keep_private_material_and_git_configuration(self):
        configurations = [(root / '.git/config').read_bytes() for root in (self.development, self.testing)]
        self.put(self.testing, '机考单题文件/试题.tex', '私人试题')
        self.put(self.testing, '机考答案文件/答案.json', '私人答案')
        self.put(self.testing, '.artifacts/local-debug/account.json', '测试账号')
        self.assertEqual(self.sync(), 0)
        self.put(self.development, 'src/测试.cs', '原目录的新代码\n')
        self.put(self.testing, 'docs/新说明.md', '测试副本的新说明\n')
        self.assertEqual(self.sync(), 0)
        self.assertEqual((self.testing / 'src/测试.cs').read_text(encoding='utf-8'), '原目录的新代码\n')
        self.assertEqual((self.development / 'docs/新说明.md').read_text(encoding='utf-8'), '测试副本的新说明\n')
        self.assertEqual((self.testing / '机考单题文件/试题.tex').read_text(encoding='utf-8'), '私人试题')
        self.assertFalse((self.development / '机考单题文件').exists())
        self.assertFalse((self.development / '.artifacts/local-debug').exists())
        self.assertEqual(configurations, [(root / '.git/config').read_bytes() for root in (self.development, self.testing)])

    def test_conflict_stops_all_updates(self):
        self.assertEqual(self.sync(), 0)
        self.put(self.development, 'src/测试.cs', '开发目录改动\n')
        self.put(self.testing, 'src/测试.cs', '测试目录改动\n')
        self.put(self.development, 'docs/独立改动.md', '不能被部分同步\n')
        self.assertEqual(self.sync(), 2)
        self.assertEqual((self.development / 'src/测试.cs').read_text(encoding='utf-8'), '开发目录改动\n')
        self.assertEqual((self.testing / 'src/测试.cs').read_text(encoding='utf-8'), '测试目录改动\n')
        self.assertFalse((self.testing / 'docs/独立改动.md').exists())

    def test_nested_private_directories_are_excluded_even_when_tracked(self):
        private_files = (
            'docs/机考单题文件/说明.md',
            'src/机考答案文件/说明.md',
            'tests/private-exams/说明.md',
            'eng/.artifacts/说明.md',
            'docs/exports/说明.md',
            'docs/management-connection.json',
        )
        for relative in private_files:
            self.put(self.testing, relative, '人工构造的私有测试材料\n')
            self.command(self.testing, 'add', '-f', '--', relative)
        self.put(self.testing, 'docs/公开说明.md', '公共项目说明\n')
        self.assertEqual(self.sync(), 0)
        self.assertTrue((self.development / 'docs/公开说明.md').exists())
        for relative in private_files:
            self.assertFalse((self.development / relative).exists())
            self.assertTrue((self.testing / relative).exists())
            self.assertFalse((self.testing / '.artifacts/folder-sync/baseline' / relative).exists())

    def test_deletion_conflicts_with_edit_and_backs_up_original_file(self):
        self.assertEqual(self.sync(), 0)
        (self.development / 'src/测试.cs').unlink()
        self.put(self.testing, 'src/测试.cs', '尚未完成的改动\n')
        self.assertEqual(self.sync(), 2)
        self.put(self.testing, 'src/测试.cs', '原代码\n')
        self.assertEqual(self.sync(), 0)
        self.assertFalse((self.testing / 'src/测试.cs').exists())
        backups = list((self.testing / '.artifacts/folder-sync/backups').glob('*/exam-system-test/src/测试.cs'))
        self.assertEqual(len(backups), 1)
        self.assertEqual(backups[0].read_text(encoding='utf-8'), '原代码\n')

    def test_unicode_path_longer_than_260_characters(self):
        relative = 'src/' + '/'.join(['较长目录' + 'x' * 26] * 8) + '/测试.cs'
        self.put(self.development, relative, '长路径中文代码\n')
        self.assertGreater(len(str(self.development / relative)), 260)
        self.assertEqual(self.sync(), 0)
        self.assertEqual(filesystem_path(self.testing / relative).read_text(encoding='utf-8'), '长路径中文代码\n')


if __name__ == '__main__':
    unittest.main()
