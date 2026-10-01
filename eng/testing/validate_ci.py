"""Require current-source module jobs and verified package-job receipts in CI."""
import argparse
import json
import sys
from pathlib import Path
from evidence import aggregate,digest,source,write_json
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'packaging'))
from package import COMPLETE,MANIFEST,validate_smoke

def package_receipts(root,identity):
    manifests=[]
    for suffix in ('a','b'):
        directory=root/('package-'+suffix)
        path=directory/'package-manifest.json';manifest=json.loads(path.read_text(encoding='utf-8'))
        marker=json.loads((directory/'complete.json').read_text(encoding='utf-8'))
        if marker!={'schemaVersion':1,'manifestSha256':digest(path)}:raise ValueError('Stale package completion receipt')
        if manifest['schemaVersion']!=1 or manifest['mode']!='candidate' or manifest['workingTreeDirty'] or manifest['runtimeIdentifier']!='win-x64' or not manifest['selfContained'] or not manifest['files']:
            raise ValueError('Package job did not produce a clean supported candidate')
        if (manifest['sourceCommit'],manifest['sourceFingerprint'])!=(identity['sourceCommit'],identity['sourceFingerprint']):raise ValueError('Package evidence uses different source')
        if {c['name'] for c in manifest['components']}!={'Exam.Server','Exam.Management','Exam.Student','Exam.Admin','Exam.Cli'}:raise ValueError('Missing package component')
        manifests.append(manifest)
    if manifests[0]!=manifests[1]:raise ValueError('Rebuilt manifests differ')
    rebuilt=json.loads((root/'rebuild.json').read_text());smoke=json.loads((root/'package-smoke.json').read_text())
    if any(v.get('schemaVersion')!=1 or v.get('status')!='passed' or v.get('sourceCommit')!=identity['sourceCommit'] for v in (rebuilt,smoke)):
        raise ValueError('Missing package comparison or smoke evidence')
    manifest_sha=digest(root/'package-a'/MANIFEST)
    if rebuilt!= {'schemaVersion':1,'status':'passed','sourceCommit':identity['sourceCommit'],'sourceFingerprint':identity['sourceFingerprint'],
        'manifestSha256':manifest_sha,'comparedFiles':len(manifests[0]['files']),'excludedFiles':[MANIFEST,COMPLETE]}:raise ValueError('Package job evidence is incomplete')
    validate_smoke(smoke,identity,manifest_sha)
    return len(manifests[0]['files'])

def main():
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('jobs',type=Path);parser.add_argument('--output',type=Path,required=True);args=parser.parse_args()
    identity=source();summary=aggregate([args.jobs/'windows-e01-evidence/run-report.json',args.jobs/'postgres-e01-evidence/run-report.json'],['core','wpf','postgres'])
    if identity['workingTreeDirty'] or any(summary[k]!=identity[k] for k in ('sourceCommit','sourceFingerprint')):raise ValueError('CI evidence does not match checkout')
    summary['packageFiles']=package_receipts(args.jobs/'package-e01-evidence',identity)
    write_json(args.output,summary)

if __name__=='__main__':
    try:main()
    except Exception as error:print('CI evidence rejected: '+type(error).__name__);raise SystemExit(1)
