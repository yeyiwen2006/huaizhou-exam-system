"""Run committed profiles; always publish an honest report, even on failed prerequisites."""
from __future__ import annotations
import argparse
from datetime import datetime, timezone
import io
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
import uuid
from evidence import ROOT, digest, dotnet_executable, expected_suites, load_plan, public_trx, source, validate_report, write_json
from isolation import OwnedRun, private_environment

def now(): return datetime.now(timezone.utc).isoformat()

def dependency(profile):
    if profile == 'postgres-latex': return dependency('postgres') or dependency('latex')
    if profile == 'windows': return None if os.name == 'nt' else 'Windows desktop host required'
    if profile == 'postgres': return None if os.environ.get('EXAM_TEST_POSTGRES') else 'Isolated PostgreSQL connection is unavailable'
    if profile == 'postgres-physical':
        native = os.environ.get('EXAM_TEST_NATIVE_PHYSICAL') == '1' and all(os.environ.get(key) for key in
            ('EXAM_TEST_NATIVE_SOURCE_DIRECTORY','EXAM_TEST_NATIVE_POSTGRES_BIN','EXAM_TEST_POSTGRES'))
        return None if native else 'Dedicated physical recovery environment is unavailable'
    if profile == 'latex': return None if shutil.which('xelatex') else 'XeLaTeX is unavailable'
    return None

def execution_suites(profiles, plan):
    expected = expected_suites(profiles, plan)
    # Native recovery may restart the shared owned source under its test-process lifetime.
    # Run it last: stopping that test process must not invalidate a later database/import suite.
    return dict(sorted(expected.items(), key=lambda item: item[0].startswith('postgres-physical-')))

def run(profiles: list[str], output: Path, timeout: int = 900) -> int:
    plan = load_plan(); expected = execution_suites(profiles, plan)
    if output.exists(): raise ValueError('Output directory must be new')
    identity = source(); output.mkdir(parents=True)
    dotnet = dotnet_executable()
    probe = subprocess.run([dotnet,'--version'], cwd=ROOT, capture_output=True, text=True) if dotnet else None
    version = probe.stdout.strip() if probe and probe.returncode == 0 else 'unavailable'
    run_id = uuid.uuid4().hex
    report = {'schemaVersion':1,'runId':run_id,**identity,'requestedProfile':profiles,
              'environment':{'platform':os.name, 'dotnet':version, 'python':__import__('platform').python_version()},
              'suites':[], 'notExecuted':[{'profile':p,'reason':'Outside requested profile; explicit separate execution required'}
              for p in plan['profiles'] if p not in profiles], 'startedAt':now(), 'finishedAt':None, 'status':'failed'}
    # unittest output stays private; the public evidence contains real counters only.
    suite = unittest.defaultTestLoader.discover(str(ROOT/'tests/Tools'), pattern='test_*.py')
    result = unittest.TextTestRunner(stream=io.StringIO()).run(suite)
    counts = {'total':result.testsRun, 'passed':result.testsRun-len(result.failures)-len(result.errors)-len(result.skipped),
              'failed':len(result.failures)+len(result.errors), 'skipped':len(result.skipped)}
    write_json(output/'tools.json', {'runId':run_id, 'counts':counts})
    report['suites'].append({'id':'tools', 'status':'passed' if result.wasSuccessful() and counts['total'] and not counts['skipped'] else 'failed',
        'exitCode':0 if result.wasSuccessful() else 1, 'counts':counts, 'evidence':{'path':'tools.json','sha256':digest(output/'tools.json')}})
    interrupted = False
    try:
        for suite_id,(project,filters) in expected.items():
            profile = next(p for p in profiles if suite_id.startswith(p+'-') and suite_id == p+'-'+Path(project).stem)
            entry = {'id':suite_id,'project':project,'filter':filters,'status':'blocked','exitCode':None,
                     'counts':{'total':0,'passed':0,'failed':0,'skipped':0}, 'startedAt':now()}
            report['suites'].append(entry)
            reason = dependency(plan['profiles'][profile].get('requires'))
            if interrupted: reason = 'Previous execution was cancelled'
            if not dotnet or version == 'unavailable': reason = 'Compatible dotnet SDK is unavailable'
            if reason: entry['reason'] = reason; entry['finishedAt']=now(); continue
            with OwnedRun() as private_run:
                private=str(private_run.root)
                command = [dotnet,'test',project,'--no-build','--no-restore','--configuration','Release','-m:1',
                    '--filter',filters,'--logger','trx;LogFileName=result.trx','--results-directory',private,
                    '--blame-hang-timeout',f'{min(timeout,300 if profile in ("latex","postgres-physical","import-e2e") else 90)}s','--blame-hang-dump-type','none']
                try:
                    environment=private_environment()
                    environment.update({k:v for k,v in os.environ.items() if k.startswith('EXAM_TEST_')})
                    environment['DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER']='1'
                    completed = private_run.start(command,ROOT,environment)
                    try: completed.wait(timeout=timeout)
                    finally: private_run.stop(completed)
                    entry['exitCode']=completed.returncode
                    raw=Path(private)/'result.trx'
                    destination=output/suite_id; destination.mkdir()
                    entry['counts']=public_trx(raw,destination/'result.trx',run_id)
                    entry['evidence']={'path':f'{suite_id}/result.trx','sha256':digest(destination/'result.trx')}
                    entry['status']='passed' if completed.returncode==0 and entry['counts']['total']>0 and not entry['counts']['failed'] and not entry['counts']['skipped'] else 'failed'
                except KeyboardInterrupt:
                    interrupted=True; entry['status']='cancelled'; entry['reason']='User cancelled the run'
                except Exception as error:
                    entry['status']='failed'; entry['reason']=type(error).__name__
            entry['finishedAt']=now()
            print(f'{suite_id}: {entry["status"]} ({entry["counts"]["total"]} tests)',flush=True)
    finally:
        for suite_id,(project,filters) in expected.items():
            if not any(s['id']==suite_id for s in report['suites']):
                report['suites'].append({'id':suite_id,'project':project,'filter':filters,'status':'cancelled','exitCode':None,
                    'counts':{'total':0,'passed':0,'failed':0,'skipped':0},'reason':'Execution interrupted'})
        report['finishedAt']=now()
        report['status']='passed' if all(s['status']=='passed' for s in report['suites']) else 'failed'
        if source() != identity:
            report['status']='failed'
            report['suites'][0]['status']='failed'; report['suites'][0]['reason']='Source changed during execution'
        write_json(output/'run-report.json',report)
        summary=['# Test check report', '', f'Run: {run_id}', f'Status: {report["status"]}',
                 '', *[f'- {s["id"]}: {s["status"]}; tests={s["counts"]["total"]}' for s in report['suites']],
                 '', *[f'- Not executed: {s["profile"]}; {s["reason"]}' for s in report['notExecuted']]]
        (output/'summary.md').write_text('\n'.join(summary)+'\n',encoding='utf-8')
    validate_report(output/'run-report.json',plan)
    return 0 if report['status']=='passed' else 1

if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--profile',action='append',required=True)
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--suite-timeout',type=int,default=900)
    parser.add_argument('--postgres-bin',type=Path)
    parser.add_argument('--native-physical',action='store_true')
    args=parser.parse_args()
    if not 1<=args.suite_timeout<=7200: parser.error('Suite timeout must be 1..7200 seconds')
    try:
        if args.postgres_bin:
            from local_postgres import LocalPostgres
            with LocalPostgres(args.postgres_bin) as database:
                os.environ['EXAM_TEST_POSTGRES']=database.connection
                if args.native_physical:
                    if os.name!='nt': raise ValueError('Native physical profile requires Windows')
                    os.environ.update(EXAM_TEST_NATIVE_PHYSICAL='1',EXAM_TEST_NATIVE_WAL_ARCHIVE='1',EXAM_TEST_NATIVE_SOURCE_DIRECTORY=str(database.data),EXAM_TEST_NATIVE_POSTGRES_BIN=str(database.bin))
                raise SystemExit(run(args.profile,args.output,args.suite_timeout))
        raise SystemExit(run(args.profile,args.output,args.suite_timeout))
    except (ValueError,OSError) as error: parser.exit(2,f'Check infrastructure rejected input: {type(error).__name__}\n')
