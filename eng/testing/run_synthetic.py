"""Actual application traffic, deterministic faults, independent read-only audit and owned cleanup."""
from __future__ import annotations
import argparse
from datetime import datetime,timezone
import hashlib
import http.client
import json
import os
from pathlib import Path
import socket
import subprocess
import time
from urllib.parse import urlsplit,urlencode
from evidence import ROOT,digest,dotnet_executable,evidence_path,source,write_json
from fault_proxy import FaultProxy,FaultRule
from isolation import OwnedRun,private_environment,reject_links
from local_postgres import LocalPostgres

def read(path): return json.loads(path.read_text(encoding='utf-8-sig'))

def request_json(target,path,session=None,body=None,method='GET',timeout=5,operator_token=None):
    url=urlsplit(target); connection=http.client.HTTPConnection(url.hostname,url.port,timeout=timeout)
    headers={}
    if session: headers={'X-Exam-Device-Id':session['deviceId'],'X-Exam-Device-Credential':session['credential']}
    if operator_token:headers['Authorization']='Bearer '+operator_token
    if body is not None: headers['Content-Type']='application/json'
    try:
        connection.request(method,path,json.dumps(body).encode() if body is not None else None,headers)
        response=connection.getresponse(); content=response.read()
        if response.status!=200: raise RuntimeError('Synthetic HTTP request was rejected')
        return json.loads(content)
    finally: connection.close()

def ready(process,target,manifest,configuration):
    deadline=time.monotonic()+30
    while time.monotonic()<deadline:
        if process.poll() is not None: raise RuntimeError('Owned application process exited early')
        try:
            value=request_json(target,'/health/ready')
            if (value.get('status'),value.get('serverId'),value.get('nodeId'),value.get('serverEpoch'))==(
                'ready',manifest['serverId'],configuration['ExamServer']['NodeId'],manifest['serverEpoch']): return
        except (OSError,RuntimeError,http.client.HTTPException,json.JSONDecodeError): pass
        time.sleep(.1)
    raise TimeoutError('Application identity/readiness check did not pass')

def load_evidence(summary,expected_count,duration):
    if summary['sessions']!=expected_count or summary['durationSeconds']!=duration or summary['questionCount']<120 or summary['interrupted'] or not summary['localScenarioChecksPassed'] or not summary['operations']:
        raise ValueError('Load evidence is incomplete or smaller than requested')
    if any(v['count']<=0 or v['failed'] or v.get('latencyTargetMet') is False for v in summary['operations'].values()):
        raise ValueError('Load operations did not meet their declared checks')

def download(target,path,session):
    url=urlsplit(target);connection=http.client.HTTPConnection(url.hostname,url.port,timeout=5)
    try:
        connection.request('GET',path,headers={'X-Exam-Device-Id':session['deviceId'],'X-Exam-Device-Credential':session['credential']})
        response=connection.getresponse()
        if response.status!=200:raise ValueError('Synthetic download rejected')
        return response.read()
    finally:connection.close()

def validate_synthetic(path:Path):
    report=read(path)
    if report.get('schemaVersion')!=1 or report.get('status')!='passed' or report.get('synthetic') is not True or report.get('productionCapacityCertified') is not False:
        raise ValueError('Incomplete synthetic result')
    requested=report['requested'];count=requested['sessions']
    if not 1<=count<=300 or requested['questionCount']<120 or not 1<=requested['durationSeconds']<=86400:raise ValueError('Invalid requested scope')
    if not report['executables'] or not report.get('lifecycle') or report['databaseCleanup'] not in ('passed','External fixture retained; cleanup requires its original owner'):
        raise ValueError('Missing lifecycle or executable evidence')
    artifacts=[s['evidence'] for s in report['scenarios'] if 'evidence' in s]+[report['audit']]
    if len({a['path'] for a in artifacts})!=len(artifacts):raise ValueError('Duplicate evidence')
    for artifact in artifacts:
        file=evidence_path(path.parent,artifact['path'])
        if digest(file)!=artifact['sha256']:raise ValueError('Evidence modified')
        value=read(file)
        if value.get('sourceCommit')!=report['sourceCommit'] or value.get('examFingerprint')!=report['examFingerprint'] or value.get('coordinatorRunId',value.get('runId'))!=report['runId']:
            raise ValueError('Evidence belongs to another run or examination')
        if file.name!='database-audit.json':load_evidence(value,next(s['sessions'] for s in report['scenarios'] if s.get('evidence')==artifact),requested['durationSeconds'])
        elif (not value['passed'] or value['expectedParticipants']!=count or value['questionCount']!=requested['questionCount'] or value['invalidAnswerSets'] or
              any(value['counts'][k]!=count for k in ('actual','submitted','finals','uniqueReceipts','gradingTasks')) or
              value['counts']['completionEvents']!=1 or value['counts']['pendingCollectionChecks']!=0):
            raise ValueError('Independent audit does not match requested scope')
    names=[s['scenario'] for s in report['scenarios']]
    if requested['mode']=='faults':
        if set(names)!={'response-loss','owned-process-restart','network-timeout','download-truncation','download-corruption'}:raise ValueError('Missing required fault')
        for scenario in report['scenarios']:
            if scenario.get('triggered') is False or any(event['upstreamStatus']!=200 for event in scenario.get('events',[])):raise ValueError('Fault did not reach successful upstream work')
            if scenario['scenario']=='owned-process-restart':
                if scenario.get('receiptRetained') is not True:raise ValueError('Restart did not retain receipt')
            elif scenario['scenario']=='response-loss':
                if scenario.get('originalReceiptRetained') is not True or scenario.get('retries',0)<20 or len(scenario.get('events',[]))!=1:raise ValueError('Response-loss replay was not proven')
            elif scenario.get('recovered') is not True or scenario.get('triggered') is not True or len(scenario.get('events',[]))!=1:
                raise ValueError('Fault recovery was not proven')
    else:
        bursts=[n for n in (10,50,100,200,300) if n<=count] or [count]
        required=[('burst',n) for n in bursts]+([(s,count) for s in ('steady','commands','download','final')] if requested['mode']=='separate' else [('mixed',count)])
        if [(s['scenario'],s['sessions']) for s in report['scenarios']]!=required:raise ValueError('Scenario scope was silently reduced')
    grading=report['grading']
    if grading['currentGradingVersion']!=1 or grading['finalSubmissionCount']!=count or grading['gradedAtCurrentVersion']!=count:raise ValueError('Current-version grading not complete')
    return report

def run_fixture(fixture:Path,output:Path,count:int,duration:int,mode:str,port:int=0,cleanup=False):
    fixture=reject_links(fixture)
    for name in ('setup.json','sessions.json','local-server.json','synthetic-node.pfx','synthetic-node.password'):reject_links(fixture/name)
    if output.exists(): raise ValueError('Report directory must be new')
    dotnet=dotnet_executable()
    if not dotnet: raise ValueError('Compatible dotnet unavailable')
    setup_dll=ROOT/'tests/Load/Exam.LoadSetup/bin/Release/net10.0/Exam.LoadSetup.dll'
    server_dll=ROOT/'tests/Load/Exam.SyntheticHost/bin/Release/net10.0/Exam.SyntheticHost.dll'
    load_dll=ROOT/'tests/Load/Exam.LoadTests/bin/Release/net10.0/Exam.LoadTests.dll'
    application_dll=server_dll.with_name('Exam.Server.dll')
    cli_dll=ROOT/'src/Exam.Cli/bin/Release/net10.0/Exam.Cli.dll'
    if any(not file.is_file() for file in (setup_dll,server_dll,application_dll,load_dll,cli_dll)): raise ValueError('Release build required')
    configuration=read(fixture/'local-server.json'); manifest=read(fixture/'sessions.json'); setup=read(fixture/'setup.json')
    if (setup.get('synthetic') is not True or manifest.get('synthetic') is not True or count!=len(manifest['sessions']) or count<1 or
        any(len({s[k] for s in manifest['sessions']})!=count for k in ('deviceId','candidateId','participationId')) or manifest['questionCount']!=setup['questionCount'] or manifest['questionCount']<120):
        raise ValueError('Only matching, distinct synthetic identities are accepted')
    subprocess.run([dotnet,str(setup_dll),'--validate-fixture',str(fixture)],cwd=ROOT,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,check=True,timeout=30)
    with socket.socket() as probe: probe.bind(('127.0.0.1',port)); port=probe.getsockname()[1]
    identity=source(); output.mkdir(parents=True)
    with OwnedRun() as owned:
        report={'schemaVersion':1,'runId':owned.run_id,**identity,'startedAt':datetime.now(timezone.utc).isoformat(),'synthetic':True,'productionCapacityCertified':False,
                'requested':{'sessions':count,'questionCount':manifest['questionCount'],'durationSeconds':duration,'mode':mode},
                'examFingerprint':hashlib.sha256(setup['examId'].encode()).hexdigest(),
                'executables':{p.stem:digest(p) for p in (setup_dll,server_dll,application_dll,load_dll,cli_dll)},'scenarios':[],'status':'failed',
                'notExecuted':['T01/T02/T03','Real devices and network','Dual-host failover','Full examination duration and maximum real paper']}
        environment=private_environment(configuration)
        environment.update(ASPNETCORE_ENVIRONMENT='Testing',DOTNET_ENVIRONMENT='Testing',Database__ApplyMigrations='true',Logging__LogLevel__Default='Warning',EXAM_SYNTHETIC_FIXTURE=str(fixture),EXAM_SYNTHETIC_CONTENT_ROOT=str(ROOT/'src/Exam.Server'))
        target=f'http://127.0.0.1:{port}'
        command=[dotnet,str(server_dll),'--port',str(port)]
        server=None
        try:
            server=owned.start(command,ROOT,environment); ready(server,target,manifest,configuration)
            if mode in ('load','all','separate'):
                plans=[('burst',n) for n in (10,50,100,200,300) if n<=count]
                if not plans: plans=[('burst',count)]
                plans += [('steady',count),('commands',count),('download',count),('final',count)] if mode=='separate' else [('mixed',count)]
                for index,(scenario,size) in enumerate(plans,1):
                    selected=json.loads(json.dumps(manifest)); selected['sessions']=selected['sessions'][:size]
                    for session in selected['sessions']: session['initialAnswerVersion']=index*100000
                    session_file=owned.path(f'{scenario}-{size}.sessions.json'); write_json(session_file,selected)
                    destination=owned.path(f'load-{index}')
                    args=[dotnet,str(load_dll),'--target',target,'--sessions',str(session_file),'--scenario',scenario,'--output',str(destination),
                          '--confirm-synthetic','--allow-loopback-http','--duration-seconds',str(duration)]
                    process=owned.start(args,ROOT,private_environment())
                    try: process.wait(timeout=duration+600)
                    finally:
                        if process.poll() is None: owned.stop(process)
                    summary=read(destination/'summary.json'); load_evidence(summary,size,duration)
                    if process.returncode!=0: raise RuntimeError('Load process failed')
                    public={k:summary[k] for k in ('runId','scenario','sessions','questionCount','durationSeconds','operations','localScenarioChecksPassed','interrupted')}
                    public.update(coordinatorRunId=owned.run_id,sourceCommit=identity['sourceCommit'],examFingerprint=report['examFingerprint'])
                    artifact=output/f'{scenario}-{size}.json'; write_json(artifact,public)
                    report['scenarios'].append({'scenario':scenario,'sessions':size,'exitCode':process.returncode,'evidence':{'path':artifact.name,'sha256':digest(artifact)}})
            if mode=='faults':
                session=manifest['sessions'][0]
                answers={f'q{q}':{'kind':'Selected','optionIds':[['A','B','C','D'][q%4]]} for q in range(1,manifest['questionCount']+1)}
                for fault in ('truncate','corrupt'):
                    with FaultProxy(target,FaultRule(urlsplit(session['resourcePath']).path,fault)) as proxy:
                        rejected=False
                        try:
                            bytes_received=download(proxy.url,session['resourcePath'],session)
                            rejected=hashlib.sha256(bytes_received).hexdigest()!=session['expectedResourceSha256']
                        except http.client.IncompleteRead:rejected=True
                        if not rejected:raise ValueError('Corrupt or incomplete download accepted')
                        proxy.require_triggered()
                        normal=download(target,session['resourcePath'],session)
                        if hashlib.sha256(normal).hexdigest()!=session['expectedResourceSha256']:raise ValueError('Resource failed after fault recovery')
                        report['scenarios'].append({'scenario':'download-truncation' if fault=='truncate' else 'download-corruption','triggered':True,'recovered':True,'events':proxy.events})
                submission='synthetic-final-'+owned.run_id
                final={key:session[key] for key in ('examId','candidateId','participationId','entryVersion','deviceId','authorizationVersion','paperVersion')}
                final.update(requestId=submission,submissionId=submission,frozenAnswers=answers)
                with FaultProxy(target,FaultRule('/api/v1/submissions/final','drop-response')) as proxy:
                    try: request_json(proxy.url,'/api/v1/submissions/final',session,final,'POST'); raise ValueError('Response-loss injection failed')
                    except http.client.RemoteDisconnected: pass
                    proxy.require_triggered()
                    receipt=request_json(target,'/api/v1/submissions/final',session,final,'POST')['result']
                    for _ in range(20):
                        if request_json(target,'/api/v1/submissions/final',session,final,'POST')['result']!=receipt: raise ValueError('Replay returned a different receipt')
                    report['scenarios'].append({'scenario':'response-loss','triggered':True,'retries':20,'originalReceiptRetained':True,'events':proxy.events})
                owned.stop(server); server=owned.start(command,ROOT,environment); ready(server,target,manifest,configuration)
                query=urlencode({k:session[k] for k in ('examId','candidateId','participationId','entryVersion','deviceId','authorizationVersion')})
                if request_json(target,'/api/v1/submissions/receipts?'+query,session)['result']!=receipt: raise ValueError('Restart lost an acknowledged receipt')
                report['scenarios'].append({'scenario':'owned-process-restart','receiptRetained':True})
                with FaultProxy(target,FaultRule('/api/v1/commands/time','timeout',delay_seconds=.5)) as proxy:
                    try: request_json(proxy.url,'/api/v1/commands/time',session,timeout=.05); raise ValueError('Timeout injection failed')
                    except (TimeoutError,socket.timeout): pass
                    proxy.require_triggered(); request_json(target,'/api/v1/commands/time',session)
                    report['scenarios'].append({'scenario':'network-timeout','triggered':True,'recovered':True,'events':proxy.events})
                for index,other in enumerate(manifest['sessions'][1:],1):
                    body={key:other[key] for key in ('examId','candidateId','participationId','entryVersion','deviceId','authorizationVersion','paperVersion')}
                    body.update(requestId=submission+str(index),submissionId=submission+str(index),frozenAnswers=answers)
                    request_json(target,'/api/v1/submissions/final',other,body,'POST')
            deadline=time.monotonic()+30
            while True:
                grading=request_json(target,'/api/v1/grading/'+setup['examId']+'/status',operator_token=manifest['operatorToken'])['result']
                if grading['currentGradingVersion']==1 and grading['finalSubmissionCount']==count and grading['gradedAtCurrentVersion']==count and not grading['duplicateSubmissionIds']:break
                if time.monotonic()>=deadline:raise TimeoutError('Current-version synthetic grading did not complete')
                time.sleep(.1)
            report['grading']={k:grading[k] for k in ('currentGradingVersion','finalSubmissionCount','gradedAtCurrentVersion','readiness')}
            cli_environment=private_environment();cli_environment.update(EXAM_OPERATOR_TOKEN=manifest['operatorToken'],EXAM_ALLOW_INSECURE_LOOPBACK='true')
            cli_result=subprocess.run([dotnet,str(cli_dll),'grading','status',setup['examId'],'--server',target,'--error-format','json'],cwd=ROOT,env=cli_environment,capture_output=True,timeout=30)
            if cli_result.returncode!=0:raise ValueError('Real grading CLI process failed')
            cli_value=json.loads(cli_result.stdout)['result']
            if any(cli_value[k]!=grading[k] for k in ('currentGradingVersion','finalSubmissionCount','gradedAtCurrentVersion')):raise ValueError('CLI grading scope differs from actual API')
            report['cliGrading']={'exitCode':0,'scopeMatched':True}
            audit_path=owned.path('audit.json')
            subprocess.run([dotnet,str(setup_dll),'--audit',str(fixture),str(audit_path)],cwd=ROOT,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,check=True,timeout=30)
            audit=read(audit_path)
            if not audit['passed'] or audit['expectedParticipants']!=count: raise ValueError('Independent database audit failed')
            audit.update(runId=owned.run_id,sourceCommit=identity['sourceCommit'],examFingerprint=report['examFingerprint'])
            write_json(output/'database-audit.json',audit)
            report['audit']={'path':'database-audit.json','sha256':digest(output/'database-audit.json')}
            report['status']='passed'
        except BaseException as error:
            report['errorType']=type(error).__name__
        finally:
            if server is not None: owned.stop(server)
            report['lifecycle']=owned.events
            if cleanup:
                try:
                    subprocess.run([dotnet,str(setup_dll),'--cleanup-owned',str(fixture)],cwd=ROOT,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,check=True,timeout=30)
                    report['databaseCleanup']='passed'
                except Exception as error: report['databaseCleanup']=type(error).__name__;report['status']='failed'
            else: report['databaseCleanup']='External fixture retained; cleanup requires its original owner'
            if source()!=identity: report['status']='failed';report['errorType']='SourceChanged'
            report['finishedAt']=datetime.now(timezone.utc).isoformat()
            write_json(output/'synthetic-report.json',report)
        if report['status']=='passed':validate_synthetic(output/'synthetic-report.json')
        return 0 if report['status']=='passed' else 1

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--postgres-bin',type=Path)
    parser.add_argument('--fixture',type=Path)
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--sessions',type=int,default=3)
    parser.add_argument('--duration-seconds',type=int,default=4)
    parser.add_argument('--port',type=int,default=0)
    parser.add_argument('--mode',choices=('load','faults','separate'),default='load')
    args=parser.parse_args()
    if not 1<=args.sessions<=300 or not 1<=args.duration_seconds<=86400: parser.error('Invalid requested scope')
    if args.fixture: return run_fixture(args.fixture,args.output,args.sessions,args.duration_seconds,args.mode,args.port)
    if not args.postgres_bin: parser.error('--postgres-bin or --fixture is required')
    with LocalPostgres(args.postgres_bin) as database,OwnedRun() as fixture_owner:
        fixture=fixture_owner.path('fixture'); environment=private_environment();environment['EXAM_LOAD_POSTGRES']=database.connection
        setup=ROOT/'tests/Load/Exam.LoadSetup/bin/Release/net10.0/Exam.LoadSetup.dll'
        process=fixture_owner.start([dotnet_executable(),str(setup),'--create-isolated',str(args.sessions),str(fixture)],ROOT,environment)
        process.wait(timeout=120)
        if process.returncode: raise RuntimeError('Isolated fixture provisioning failed')
        return run_fixture(fixture,args.output,args.sessions,args.duration_seconds,args.mode,args.port,cleanup=True)

if __name__=='__main__':
    try: raise SystemExit(main())
    except Exception as error: print(f'Synthetic checks failed: {type(error).__name__}');raise SystemExit(1)
