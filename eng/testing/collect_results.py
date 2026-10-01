"""Bind module, synthetic and package evidence to one clean source identity."""
import argparse
import json
from pathlib import Path
import sys
from evidence import aggregate,source,write_json
from run_synthetic import validate_synthetic
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'packaging'))
from package import compare,digest,MANIFEST,validate_smoke,verify

def collect(tests,profiles,synthetic,packages,rebuild,smoke):
    summary=aggregate(tests,profiles);identity=source()
    if identity['workingTreeDirty'] or any(summary[k]!=identity[k] for k in ('sourceCommit','sourceFingerprint')):raise ValueError('Current clean source evidence required')
    if len(packages)!=2:raise ValueError('Two rebuilt candidates required')
    records=[validate_synthetic(path) for path in synthetic]+[verify(path) for path in packages]
    if not records or any(r['workingTreeDirty'] or (r['sourceCommit'],r['sourceFingerprint'])!=(identity['sourceCommit'],identity['sourceFingerprint']) for r in records):
        raise ValueError('Evidence uses different or dirty source')
    if {r['requested']['mode'] for r in records if 'requested' in r} != {'load','faults'}:raise ValueError('Normal load and fault evidence both required')
    if json.loads(rebuild.read_text(encoding='utf-8'))!=compare(*packages):raise ValueError('Rebuild evidence differs from actual packages')
    validate_smoke(json.loads(smoke.read_text(encoding='utf-8')),identity,digest(packages[0]/MANIFEST))
    return {'schemaVersion':1,**identity,'status':'passed','profiles':profiles,'moduleRuns':summary['runs'],
            'syntheticRuns':[r['runId'] for r in records if 'runId' in r],'packageCount':len(packages),
            'notExecuted':['T01/T02/T03','Fresh Windows target without installed runtime','Full real-device examination and dual-host failover acceptance']}

if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--tests',type=Path,action='append',required=True);parser.add_argument('--require-profile',action='append',required=True)
    parser.add_argument('--synthetic',type=Path,action='append',required=True);parser.add_argument('--package',type=Path,action='append',required=True)
    parser.add_argument('--rebuild',type=Path,required=True);parser.add_argument('--smoke',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True);args=parser.parse_args()
    try:write_json(args.output,collect(args.tests,args.require_profile,args.synthetic,args.package,args.rebuild,args.smoke))
    except Exception as error:parser.exit(1,'Evidence collection failed: '+type(error).__name__+'\n')
