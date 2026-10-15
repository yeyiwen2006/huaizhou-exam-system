"""Run selected integration tests in a disposable PostgreSQL instance, then remove it."""
from __future__ import annotations
import argparse
import os
from pathlib import Path
import subprocess
from local_postgres import LocalPostgres
from isolation import private_environment

ROOT = Path(__file__).resolve().parents[2]

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--postgres-bin', type=Path, default=Path(r'C:\Program Files\PostgreSQL\18\bin'))
    parser.add_argument('--filter', default='Category=Postgres')
    parser.add_argument('--no-build', action='store_true')
    args = parser.parse_args()
    with LocalPostgres(args.postgres_bin) as database:
        environment = private_environment()
        environment['EXAM_TEST_POSTGRES'] = database.connection
        environment['DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER'] = '1'
        command = ['dotnet', 'test', 'tests/Integration/Exam.IntegrationTests/Exam.IntegrationTests.csproj',
                   '-c', 'Release', '--no-restore', '-m:1', '--filter', args.filter,
                   '--blame-hang-timeout', '90s', '--blame-hang-dump-type', 'none']
        if args.no_build: command.append('--no-build')
        print(f'Isolated PostgreSQL on loopback port {database.port}; archive_mode=off.', flush=True)
        # OwnedRun supplies a Windows job: cancellation also stops child test hosts.
        process = database.owner.start(command, ROOT, environment)
        try:
            code = process.wait(timeout=1800)
        finally:
            database.owner.stop(process)
        # Only this run's test output is displayed; its private directory is removed on exit.
        log = database.owner.path('process-1.log')
        print(log.read_text(encoding='utf-8', errors='replace'), end='')
        return code

if __name__ == '__main__':
    raise SystemExit(main())
