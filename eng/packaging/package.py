"""Locked Windows packages, actual PE metadata, strict integrity and deterministic rebuilding."""
from __future__ import annotations
import argparse
import hashlib
import json
import os
import re
from pathlib import Path
import shutil
import subprocess
import sys
import uuid
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'testing'))
from evidence import ROOT,digest,dotnet_executable,source,write_json
from isolation import reject_links,OwnedRun,private_environment

LAYOUT=ROOT/'packaging/layout.json'
COMPONENTS=('Exam.Server','Exam.Management','Exam.Student','Exam.Admin','Exam.Cli')
MANIFEST='package-manifest.json'
COMPLETE='complete.json'

def read(path): return json.loads(path.read_text(encoding='utf-8-sig'))

def inventory(root:Path):
    reject_links(root)
    files={}
    for path in sorted(root.rglob('*')):
        reject_links(path)
        if path.is_file():
            relative=path.relative_to(root).as_posix()
            if relative in (MANIFEST,COMPLETE): continue
            files[relative]={'bytes':path.stat().st_size,'sha256':digest(path)}
    return files

def lock_fingerprint():
    files=sorted(ROOT.rglob('packages*.lock.json'))
    files=[p for p in files if not any(x in p.relative_to(ROOT).parts for x in ('bin','obj','.artifacts','.git'))]
    data={p.relative_to(ROOT).as_posix():digest(p) for p in files}
    if len(data)<len(COMPONENTS): raise ValueError('Missing dependency locks')
    return hashlib.sha256(json.dumps(data,sort_keys=True).encode()).hexdigest(),data

def probe_assembly(root:Path,name:str):
    # Execute the self-contained CLI; the probe parses PE metadata without loading target code.
    executable=root/'Exam.Cli/Exam.Cli.exe'
    result=subprocess.run([str(executable),'artifact-info',str(root/name/(name+'.dll'))],cwd=root,
        env=private_environment(),capture_output=True,text=True,encoding='utf-8',timeout=20)
    if result.returncode: raise ValueError('Artifact metadata probe failed')
    return json.loads(result.stdout)

def check_metadata(value,name,identity,locks):
    if value.get('schemaVersion')!=1 or value.get('assemblyName')!=name or not value.get('version') or not value.get('informationalVersion'):
        raise ValueError('Unsupported assembly metadata')
    expected={'E01SourceCommit':identity['sourceCommit'],'E01SourceFingerprint':identity['sourceFingerprint'],'E01LockFingerprint':locks}
    if value.get('metadata')!=expected: raise ValueError('Component has a different source or dependency identity')

def verify(root:Path,probe=probe_assembly,require_complete=True):
    root=reject_links(root)
    manifest=read(root/MANIFEST)
    if manifest.get('schemaVersion')!=1 or manifest.get('runtimeIdentifier')!='win-x64' or manifest.get('configuration')!='Release' or manifest.get('selfContained') is not True:
        raise ValueError('Unsupported package format or target')
    identity={key:manifest[key] for key in ('sourceCommit','sourceFingerprint','workingTreeDirty')}
    if manifest.get('mode') not in ('candidate','development') or (manifest['mode']=='candidate' and identity['workingTreeDirty']):
        raise ValueError('Candidate requires a clean commit')
    if type(identity['workingTreeDirty']) is not bool or not re.fullmatch('[a-f0-9]{40}',identity['sourceCommit']) or not re.fullmatch('[a-f0-9]{64}',identity['sourceFingerprint']): raise ValueError('Invalid source identity')
    components=manifest['components']
    if len(components)!=len(COMPONENTS) or {c['name'] for c in components}!=set(COMPONENTS): raise ValueError('Missing or duplicate component')
    actual=inventory(root)
    declared=manifest['files']
    if not declared or actual!=declared: raise ValueError('Package has missing, extra or modified files')
    for relative in declared:
        path=Path(relative)
        if path.is_absolute() or '..' in path.parts or '\\' in relative or path.parts[0] not in COMPONENTS:
            raise ValueError('Manifest path escapes its component')
    required=('hostfxr.dll','hostpolicy.dll','coreclr.dll')
    for component in components:
        name=component['name']
        if component['entry']!=f'{name}/{name}.exe': raise ValueError('Component entry differs')
        required_files=[f'{name}/{name}.exe',f'{name}/{name}.dll',f'{name}/{name}.runtimeconfig.json',f'{name}/{name}.deps.json',*[f'{name}/{x}' for x in required]]
        if name in ('Exam.Admin','Exam.Student'): required_files += [f'{name}/PresentationFramework.dll',f'{name}/wpfgfx_cor3.dll']
        if name=='Exam.Student': required_files += [f'{name}/e_sqlite3.dll']
        if any(p not in declared for p in required_files): raise ValueError('Missing managed/native entry resources')
        metadata=probe(root,name)
        check_metadata(metadata,name,identity,manifest['lockFingerprint'])
        if metadata!=component['assembly']: raise ValueError('Manifest does not match actual PE metadata')
    if not manifest.get('migrations') or any(f'Exam.Server/migrations/{Path(p).name}' not in declared or declared[f'Exam.Server/migrations/{Path(p).name}']['sha256']!=sha for p,sha in manifest['migrations'].items()):
        raise ValueError('Migration resources do not match manifest')
    recalculated=hashlib.sha256(json.dumps(manifest['dependencyLocks'],sort_keys=True).encode()).hexdigest()
    if recalculated!=manifest['lockFingerprint']: raise ValueError('Dependency lock digest disagrees')
    if require_complete and read(root/COMPLETE)!={'schemaVersion':1,'manifestSha256':digest(root/MANIFEST)}:
        raise ValueError('Missing or stale completion marker')
    return manifest

def build(output:Path,development=False):
    output=reject_links(output)
    if output.exists(): raise ValueError('Output must be new')
    if os.name!='nt': raise ValueError('Windows desktop build host required')
    identity=source()
    if identity['workingTreeDirty'] and not development: raise ValueError('Candidate requires a clean working tree')
    dotnet=dotnet_executable()
    if not dotnet: raise ValueError('SDK unavailable')
    sdk=subprocess.check_output([dotnet,'--version'],cwd=ROOT,text=True).strip()
    if sdk!=read(ROOT/'global.json')['sdk']['version']: raise ValueError('SDK differs from global.json')
    locks,lock_files=lock_fingerprint()
    layout=read(LAYOUT)
    if tuple(layout['components'])!=COMPONENTS: raise ValueError('Unsupported component layout')
    stage=output.parent/(output.name+'.partial-'+uuid.uuid4().hex)
    stage.mkdir(parents=True)
    identity_args=[f'-p:{name}={value}' for name,value in (
        ('E01SourceCommit',identity['sourceCommit']),('E01SourceFingerprint',identity['sourceFingerprint']),
        ('E01LockFingerprint',locks),('SourceRevisionId',identity['sourceCommit']),('ContinuousIntegrationBuild','true'),('DebugType','None'))]
    components=[]
    try:
        for name in COMPONENTS:
            project=f'src/{name}/{name}.csproj'
            destination=stage/name
            common=['--runtime','win-x64','-p:Configuration=Release','-p:SelfContained=true',*identity_args]
            subprocess.run([dotnet,'restore',project,'--locked-mode',*common],cwd=ROOT,check=True)
            # Recompile each component and its references for every package build.
            subprocess.run([dotnet,'build',project,'--no-restore','--no-incremental','--configuration','Release','--runtime','win-x64','--self-contained','true',*identity_args],cwd=ROOT,check=True)
            subprocess.run([dotnet,'publish',project,'--no-restore','--configuration','Release','--runtime','win-x64','--self-contained','true','--output',str(destination),*identity_args],cwd=ROOT,check=True)
            print(f'Published {name}',flush=True)
        migrations={}
        for path in sorted((ROOT/'src/Exam.Infrastructure/Migrations').glob('*.sql')):
            migrations[path.relative_to(ROOT).as_posix()]=digest(path)
            destination=stage/'Exam.Server/migrations'/path.name
            destination.parent.mkdir(exist_ok=True);shutil.copyfile(path,destination)
        for name in COMPONENTS:
            metadata=probe_assembly(stage,name); check_metadata(metadata,name,identity,locks)
            components.append({'name':name,'entry':f'{name}/{name}.exe','assembly':metadata})
        if lock_fingerprint()!=(locks,lock_files) or source()!=identity: raise ValueError('Source or locks changed during publishing')
        manifest={'schemaVersion':1,**identity,'mode':'development' if development else 'candidate','runtimeIdentifier':'win-x64','configuration':'Release',
            'selfContained':True,'sdk':sdk,'lockFingerprint':locks,'dependencyLocks':lock_files,'components':components,'migrations':migrations,
            'externalDependencies':layout['externalDependencies'],'files':inventory(stage),
            'validationScope':['Locked self-contained publishing and actual assembly metadata','Integrity only; T01/T02/T03 and clean machine acceptance are separate']}
        write_json(stage/MANIFEST,manifest)
        verify(stage,require_complete=False)
        write_json(stage/COMPLETE,{'schemaVersion':1,'manifestSha256':digest(stage/MANIFEST)})
        if output.exists(): raise ValueError('Output appeared during build')
        stage.rename(output)
        return verify(output)
    except BaseException:
        # Keep partial files for diagnosis; never create a successful completion marker.
        if (stage/COMPLETE).exists(): (stage/COMPLETE).unlink()
        raise

def compare(first:Path,second:Path,probe=probe_assembly):
    a,b=verify(first,probe),verify(second,probe)
    fixed=('sourceCommit','sourceFingerprint','workingTreeDirty','runtimeIdentifier','configuration','selfContained','sdk','lockFingerprint','components','migrations','externalDependencies')
    if any(a[k]!=b[k] for k in fixed) or a['files']!=b['files']: raise ValueError('Rebuild differs')
    return {'schemaVersion':1,'status':'passed','sourceCommit':a['sourceCommit'],'sourceFingerprint':a['sourceFingerprint'],
            'manifestSha256':digest(first/MANIFEST),'comparedFiles':len(a['files']),'excludedFiles':[MANIFEST,COMPLETE]}

def validate_smoke(value,identity,manifest_sha):
    if value.get('schemaVersion')!=1 or value.get('status')!='passed' or any(value.get(k)!=identity[k] for k in ('sourceCommit','sourceFingerprint')) or value.get('manifestSha256')!=manifest_sha:
        raise ValueError('Smoke evidence uses a different package')
    checks=value['checks'];expected={('Exam.Cli','help'),('Exam.Cli','version'),('Exam.Cli','artifact-info'),('Exam.Server','unconfigured-deployment-rejected'),('Exam.Management','unconfigured-deployment-rejected')}
    if len(checks)!=5 or {(c['component'],c['operation']) for c in checks}!=expected:raise ValueError('Smoke scope is incomplete')
    for check in checks:
        if type(check['exitCode']) is not int or (check['component']=='Exam.Cli' and check['exitCode']!=0) or (check['component']!='Exam.Cli' and (check['exitCode']==0 or check.get('expectedRefusal') is not True)):
            raise ValueError('Smoke result is not successful')

def smoke(root:Path,output:Path):
    root=reject_links(root)
    manifest=verify(root)
    if output.exists(): raise ValueError('Smoke report must be new')
    env=private_environment();env.update(DOTNET_MULTILEVEL_LOOKUP='0',DOTNET_ROOT=str(root/'Exam.Cli'))
    results=[]
    for args in (['help'],['version'],['artifact-info',str(root/'Exam.Management/Exam.Management.dll')]):
        process=subprocess.run([str(root/'Exam.Cli/Exam.Cli.exe'),*args],env=env,cwd=root,capture_output=True,text=True,encoding='utf-8',timeout=20)
        if process.returncode!=0 or not process.stdout.strip(): raise ValueError('Packaged CLI failed to start')
        results.append({'component':'Exam.Cli','operation':args[0],'exitCode':0})
    for name,expected in (('Exam.Server','ConnectionStrings:Exam'),('Exam.Management','管理 HTTPS 端口无效')):
        environment=private_environment();environment.update(ASPNETCORE_ENVIRONMENT='Production',DOTNET_ENVIRONMENT='Production',DOTNET_MULTILEVEL_LOOKUP='0')
        process=subprocess.run([str(root/name/(name+'.exe'))],env=environment,cwd=root/name,capture_output=True,timeout=20)
        errors=process.stderr.decode('utf-8',errors='ignore')+process.stderr.decode('gbk',errors='ignore')
        if process.returncode==0 or expected not in errors:raise ValueError('Packaged service did not enforce its deployment boundary')
        results.append({'component':name,'operation':'unconfigured-deployment-rejected','exitCode':process.returncode,'expectedRefusal':True})
    # Product WPF startup and service provisioning need their own trust/configuration.
    write_json(output,{'schemaVersion':1,'sourceCommit':manifest['sourceCommit'],'sourceFingerprint':manifest['sourceFingerprint'],'manifestSha256':digest(root/MANIFEST),'status':'passed','checks':results,
        'notExecuted':['Fresh Windows machine without installed runtime','Provisioned GUI workflow and production certificate-store deployment','T01/T02/T03']})

def main():
    parser=argparse.ArgumentParser(description=__doc__);commands=parser.add_subparsers(dest='action',required=True)
    b=commands.add_parser('build');b.add_argument('--output',type=Path,required=True);b.add_argument('--development',action='store_true')
    v=commands.add_parser('verify');v.add_argument('root',type=Path)
    c=commands.add_parser('compare');c.add_argument('first',type=Path);c.add_argument('second',type=Path);c.add_argument('--output',type=Path,required=True)
    s=commands.add_parser('smoke');s.add_argument('root',type=Path);s.add_argument('--output',type=Path,required=True)
    args=parser.parse_args()
    if args.action=='build':build(args.output,args.development)
    elif args.action=='verify':verify(args.root);print('Package integrity passed')
    elif args.action=='compare':write_json(args.output,compare(args.first,args.second))
    else:smoke(args.root,args.output)

if __name__=='__main__':
    try:main()
    except Exception as error: print('Package check failed: '+type(error).__name__);raise SystemExit(1)
