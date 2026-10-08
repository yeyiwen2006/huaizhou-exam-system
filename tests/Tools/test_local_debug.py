"""Launcher startup diagnostics using synthetic process and HTTP responses."""
import io
from pathlib import Path
import sys
import unittest
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'eng' / 'development'))
import start_local


class LocalDebugStartupTests(unittest.TestCase):
    def test_migration_block_explains_preserved_data_and_compatible_recovery(self):
        process = Mock()
        process.poll.return_value = 3
        with self.assertRaises(RuntimeError) as caught:
            start_local.wait_server(process, 'http://127.0.0.1:1')
        message = str(caught.exception)
        self.assertIn('数据库需要升级', message)
        self.assertIn('升级前的调试程序', message)
        self.assertIn('考试及收卷', message)
        self.assertIn('现有测试数据已保留', message)

    def test_other_exit_codes_do_not_claim_a_migration_block(self):
        for code in (-1, 0, 1, 2):
            with self.subTest(code=code):
                process = Mock()
                process.poll.return_value = code
                with self.assertRaisesRegex(RuntimeError, '后台提前退出'):
                    start_local.wait_server(process, 'http://127.0.0.1:1')

    def test_ready_local_host_still_starts_normally(self):
        process = Mock()
        process.poll.return_value = None
        with patch.object(start_local.urllib.request, 'build_opener') as build_opener:
            build_opener.return_value.open.return_value.__enter__.return_value = io.StringIO(
                '{"status":"ready","localDebug":true}')
            result = start_local.wait_server(process, 'http://127.0.0.1:1')
        self.assertEqual({'status': 'ready', 'localDebug': True}, result)


if __name__ == '__main__':
    unittest.main()
