"""Explicit Windows/PostgreSQL smoke test in a new, preserved .artifacts directory."""
from pathlib import Path
import argparse
import json
import os
import secrets
import ssl
import subprocess
import sys
import time
import urllib.error
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'eng' / 'development'))
import start_local


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--no-build', action='store_true')
    args = parser.parse_args()
    directory = ROOT / '.artifacts' / ('management-check-' + uuid.uuid4().hex[:10])
    # The launcher owns this new session, including the database. Nothing is copied from
    # the user's running environment and no existing database is changed or removed.
    log_path = directory.with_suffix('.log')
    log_path.parent.mkdir(exist_ok=True)
    command = [sys.executable, '-X', 'utf8', str(ROOT / 'eng/development/start_local.py'),
               '--data-dir', str(directory), '--no-clients']
    if args.no_build:
        command.append('--no-build')
    with log_path.open('w', encoding='utf-8') as log:
        process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=log, stderr=log,
                                   cwd=ROOT, creationflags=subprocess.CREATE_NO_WINDOW)
        try:
            deadline = time.monotonic() + 360
            while not (directory / 'connection.json').exists():
                if process.poll() is not None or time.monotonic() > deadline:
                    raise RuntimeError(f'测试入口启动失败，日志：{log_path}')
                time.sleep(.5)
            connection = json.loads((directory / 'connection.json').read_text(encoding='utf-8'))
            certificate = directory / 'local-node-a.cer'
            opener = urllib.request.build_opener(urllib.request.ProxyHandler({}),
                urllib.request.HTTPSHandler(context=ssl.create_default_context(cafile=str(certificate))))
            session = None

            def call(base, route, body=None, method=None):
                headers = {'Content-Type': 'application/json'}
                if session:
                    headers.update(Authorization='Bearer ' + session,
                        **{'X-Exam-Server-Id': connection['serverId'], 'X-Exam-Node-Id': 'local-node-a'})
                request = urllib.request.Request(base + route,
                    data=None if body is None else json.dumps(body).encode('utf-8'), headers=headers, method=method)
                try:
                    with opener.open(request, timeout=120) as response:
                        envelope = json.load(response)
                except urllib.error.HTTPError as error:
                    envelope = json.load(error)
                    raise AssertionError(f'{route}: {envelope.get("error")} {envelope.get("result")}') from error
                assert not envelope.get('error'), envelope
                return envelope.get('result', envelope.get('data'))

            username = 'management.smoke'
            password = 'Test!' + secrets.token_urlsafe(24)
            call(connection['adminUrl'], '/api/v1/operators/register',
                 {'username': username, 'displayName': '管理回归测试', 'password': password})
            manager = connection['managementUrl']
            session = call(manager, '/management/v1/login', {'username': username, 'password': password})['token']
            status = call(manager, '/management/v1/service/status')
            assert status['processStatus'] == 'running' and status['readiness'] == 'ready', status

            def operation(route, extra=None, method='POST'):
                body = dict(requestId=uuid.uuid4().hex, serverId=connection['serverId'], nodeId='local-node-a', actorId=username)
                body.update(extra or {})
                result = call(manager, '/management/v1/service/' + route, body, method)
                assert result['phase'] == 'completed', result
                return result

            checked = operation('checks')
            assert checked['checks'] and all(row['passed'] for row in checked['checks']), checked
            operation('stop')
            assert call(manager, '/management/v1/service/status')['processStatus'] == 'stopped'
            # Login must still work with both business endpoints stopped.
            session = call(manager, '/management/v1/login', {'username': username, 'password': password})['token']
            configuration = call(manager, '/management/v1/service/configuration')
            changed_port = start_local.free_port(0)
            saved = dict(configuration, httpsPort=changed_port, dataDirectory=str(directory / 'business-data' / 'configured'))
            operation('configuration', dict(expectedVersion=configuration['version'], configuration=saved), 'PUT')
            operation('start')
            settings = json.loads((directory / 'apps/student/server.json').read_text(encoding='utf-8'))
            assert settings['port'] == changed_port, settings
            assert call(manager, '/management/v1/service/status')['readiness'] == 'ready'
            operation('stop')
            backup = operation('backups', dict(destinationDirectory=str(directory / 'backups')))['backup']
            operation('start')
            call(connection['adminUrl'], '/api/v1/operators/register',
                 {'username': 'after.snapshot', 'displayName': '备份之后的测试账号', 'password': password})
            operation('stop')
            restored = operation('restores', dict(backupId=backup['backupId'], sourceDirectory=backup['destinationDirectory']))
            assert restored['restore']['recoveryVerified'] and restored['restore']['businessReady'], restored
            selected = json.loads((directory / 'management/active-database.json').read_text(encoding='utf-8'))
            assert selected['databaseName'].startswith('exam_local_restore_'), selected
            local = json.loads((directory / 'local-settings.json').read_text(encoding='utf-8'))
            environment = start_local.private_environment()
            environment.update(PGHOST='127.0.0.1', PGPORT=str(local['databasePort']), PGUSER='postgres',
                               PGPASSWORD=local['databasePassword'], PGCLIENTENCODING='UTF8')
            for name, expected in [('exam_local_debug', '2'), (selected['databaseName'], '1')]:
                environment['PGDATABASE'] = name
                assert start_local.sql(start_local.postgres_bin(None), environment,
                    'SELECT count(*) FROM exam.operator_accounts') == expected
            session = call(manager, '/management/v1/login', {'username': username, 'password': password})['token']
            assert call(manager, '/management/v1/service/status')['readiness'] == 'ready'
            print('管理登录、真实状态、停止后登录、自检、配置、重新启动、备份恢复及原数据库保留通过。', flush=True)
            print(f'测试数据和日志：{directory}', flush=True)
        finally:
            if process.poll() is None:
                process.communicate(b'q\n', timeout=45)
            if sys.exc_info()[0] is None:
                assert process.returncode == 0, f'测试入口未正常退出：{log_path}'


if __name__ == '__main__':
    if os.name != 'nt':
        raise SystemExit('需要 Windows 和 PostgreSQL 18。')
    if hasattr(sys.stdout, 'reconfigure'):
        sys.stdout.reconfigure(encoding='utf-8')
    main()
