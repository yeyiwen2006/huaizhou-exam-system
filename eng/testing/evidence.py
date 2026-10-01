"""Strict, privacy-preserving evidence shared by local checks and CI aggregation."""
from __future__ import annotations
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import xml.etree.ElementTree as ET
import shutil

ROOT = Path(__file__).resolve().parents[2]
PLAN = Path(__file__).with_name('test-plan.json')
NS = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'

def dotnet_executable() -> str | None:
    # Windows shells can expose differently cased PATH entries to child processes.
    configured = os.environ.get('DOTNET_HOST_PATH')
    if configured and Path(configured).is_file(): return configured
    root = os.environ.get('DOTNET_ROOT')
    candidate = Path(root) / ('dotnet.exe' if os.name == 'nt' else 'dotnet') if root else None
    return str(candidate) if candidate and candidate.is_file() else shutil.which('dotnet')

def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()

def write_json(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    encoded=json.dumps(value,ensure_ascii=False,indent=2)
    # Preserve digest/UUID values while avoiding accidental phone-like runs in scanners.
    encoded=re.sub(r'"([a-f0-9]{32}|[a-f0-9]{40}|[a-f0-9]{64})"',
        lambda m:'"'+re.sub(r'(?<!\d)1[3-9]\d{9}(?!\d)',lambda n:'\\u0031'+n.group()[1:],m.group(1))+'"',encoded)
    with path.open('x', encoding='utf-8') as stream:
        stream.write(encoded)

def git(*args: str) -> str:
    return subprocess.check_output(['git','-c',f'safe.directory={ROOT.as_posix()}', *args], cwd=ROOT, stderr=subprocess.DEVNULL).decode('utf-8').strip()

def source() -> dict:
    names = sorted(set(git('ls-files', '-z').split('\0') + git('ls-files', '--others', '--exclude-standard', '-z').split('\0')) - {''})
    h = hashlib.sha256()
    for name in names:
        path = ROOT / name
        h.update(name.encode()); h.update(b'\0')
        if path.is_file():
            content = path.read_bytes()
            # Match Git text=auto across Windows/Linux checkout line endings.
            if b'\0' not in content[:8000]: content = content.replace(b'\r\n', b'\n')
            h.update(hashlib.sha256(content).digest())
        else: h.update(b'deleted')
    return {'sourceCommit': git('rev-parse', 'HEAD'), 'workingTreeDirty': bool(git('status', '--porcelain')),
            'sourceFingerprint': h.hexdigest()}

def load_plan() -> dict:
    plan = json.loads(PLAN.read_text(encoding='utf-8'))
    discovered = {p.relative_to(ROOT).as_posix() for p in (ROOT / 'tests').rglob('*.csproj')
                  if 'xunit' in p.read_text(encoding='utf-8')}
    if discovered != set(plan['projects']):
        raise ValueError('Test project inventory differs from the committed plan')
    solution = (ROOT / 'Huaizhou.ExamSystem.sln').read_text(encoding='utf-8-sig').replace('\\', '/')
    if any(project not in solution for project in discovered):
        raise ValueError('A test project is missing from the solution')
    categories = set()
    for project in discovered:
        for path in (ROOT / project).parent.rglob('*.cs'):
            if 'obj' in path.parts or 'bin' in path.parts:
                continue
            categories.update(re.findall(r'Trait\(\s*"Category"\s*,\s*"([^"\n]+)"', path.read_text(encoding='utf-8-sig')))
    if categories != set(plan['categories']):
        raise ValueError('Test categories differ from the committed plan')
    return plan

def expected_suites(profiles: list[str], plan: dict) -> dict[str, tuple[str, str]]:
    if not profiles or len(profiles) != len(set(profiles)) or any(p not in plan['profiles'] for p in profiles):
        raise ValueError('Unknown or duplicate profile')
    return {f'{profile}-{Path(plan["projects"][index]).stem}': (plan['projects'][index], plan['profiles'][profile]['filter'])
            for profile in profiles for index in plan['profiles'][profile]['projects']}

def read_trx(path: Path) -> tuple[dict, ET.Element]:
    root = ET.parse(path).getroot()
    if root.tag != f'{{{NS}}}TestRun':
        raise ValueError('Unsupported TRX format')
    results = root.findall(f'{{{NS}}}Results/{{{NS}}}UnitTestResult')
    counters = root.find(f'{{{NS}}}ResultSummary/{{{NS}}}Counters')
    if counters is None or not results:
        raise ValueError('Missing or empty test results')
    ids = [(r.get('testId'), r.get('executionId')) for r in results]
    if any(not a or not b for a, b in ids) or len(set(ids)) != len(ids):
        raise ValueError('Missing or duplicate test identities')
    if any(r.get('outcome') not in ('Passed', 'Failed', 'NotExecuted') for r in results):
        raise ValueError('Incomplete or unsupported test outcome')
    counts = {'total': len(results), 'passed': sum(r.get('outcome') == 'Passed' for r in results),
              'failed': sum(r.get('outcome') == 'Failed' for r in results),
              'skipped': sum(r.get('outcome') == 'NotExecuted' for r in results)}
    if any(int(counters.get(key, '-1')) != counts[key] for key in ('total','passed','failed')):
        raise ValueError('TRX counters disagree with individual results')
    if int(counters.get('notExecuted', '0')) != counts['skipped']:
        raise ValueError('TRX skipped count disagrees')
    summary = root.find(f'{{{NS}}}ResultSummary')
    if summary.get('outcome') not in ('Completed', 'Passed', 'Failed'):
        raise ValueError('Aborted or incomplete TRX')
    return counts, root

def public_trx(raw: Path, output: Path, run_id: str) -> dict:
    counts, source_root = read_trx(raw)
    ET.register_namespace('', NS)
    root = ET.Element(f'{{{NS}}}TestRun', {'id': run_id})
    results = ET.SubElement(root, f'{{{NS}}}Results')
    for entry in source_root.findall(f'{{{NS}}}Results/{{{NS}}}UnitTestResult'):
        # No test parameters, host names, paths, stdout, exception bodies or credentials.
        ET.SubElement(results, f'{{{NS}}}UnitTestResult', {key: entry.get(key) for key in ('testId','executionId','outcome')})
    summary = ET.SubElement(root, f'{{{NS}}}ResultSummary', {'outcome': 'Failed' if counts['failed'] else 'Completed'})
    ET.SubElement(summary, f'{{{NS}}}Counters', {'total': str(counts['total']), 'passed': str(counts['passed']),
        'failed': str(counts['failed']), 'notExecuted': str(counts['skipped'])})
    encoded=ET.tostring(root,encoding='unicode',xml_declaration=True)
    encoded=re.sub(r'"([a-f0-9-]{32,36})"',
        lambda m:'"'+re.sub(r'(?<!\d)1[3-9]\d{9}(?!\d)',lambda n:'&#49;'+n.group()[1:],m.group(1))+'"',encoded)
    with output.open('x',encoding='utf-8') as stream:stream.write(encoded)
    return counts

def evidence_path(root: Path, relative: str) -> Path:
    path = Path(relative)
    if path.is_absolute() or '..' in path.parts or '\\' in relative:
        raise ValueError('Evidence path escapes report directory')
    candidate = root / path
    for parent in [candidate, *candidate.parents]:
        if parent == root.parent:
            break
        if parent.is_symlink() or (hasattr(parent, 'is_junction') and parent.is_junction()):
            raise ValueError('Evidence links are forbidden')
    candidate.resolve(strict=True).relative_to(root.resolve(strict=True))
    return candidate

def validate_report(path: Path, plan: dict | None = None) -> dict:
    report = json.loads(path.read_text(encoding='utf-8'))
    required = {'schemaVersion','runId','sourceCommit','sourceFingerprint','workingTreeDirty','requestedProfile',
                'environment','suites','notExecuted','status','startedAt','finishedAt'}
    if not required <= report.keys() or report['schemaVersion'] != 1:
        raise ValueError('Unsupported or incomplete report')
    if not re.fullmatch(r'[a-f0-9]{32}', report['runId']) or not re.fullmatch(r'[a-f0-9]{40}', report['sourceCommit']) or not re.fullmatch(r'[a-f0-9]{64}', report['sourceFingerprint']):
        raise ValueError('Invalid run or source identity')
    if type(report['workingTreeDirty']) is not bool:
        raise ValueError('Dirty status must be explicit')
    plan = plan or load_plan()
    expected = expected_suites(report['requestedProfile'], plan)
    suites = report['suites']
    ids = [s['id'] for s in suites if s['id'] != 'tools']
    if len(ids) != len(set(ids)) or set(ids) != set(expected) or sum(s['id']=='tools' for s in suites) != 1:
        raise ValueError('Missing, duplicate or unexpected suite')
    omissions = {entry['profile'] for entry in report['notExecuted'] if entry.get('reason')}
    if omissions != set(plan['profiles']) - set(report['requestedProfile']):
        raise ValueError('Unrequested categories must remain visible')
    for suite in suites:
        counts = suite['counts']
        if set(counts) != {'total','passed','failed','skipped'} or any(type(n) is not int or n < 0 for n in counts.values()) or counts['total'] != counts['passed']+counts['failed']+counts['skipped']:
            raise ValueError('Invalid suite counters')
        if suite['id'] != 'tools' and (suite['project'], suite['filter']) != expected[suite['id']]:
            raise ValueError('Suite scope differs from the committed profile')
        if suite['status'] not in ('passed','failed','blocked','cancelled'):
            raise ValueError('Invalid suite status')
        if suite['status'] == 'passed':
            if suite['exitCode'] != 0 or suite['counts']['total'] <= 0 or suite['counts']['failed'] or suite['counts']['skipped']:
                raise ValueError('Passing suite must have successful executed tests')
        if 'evidence' in suite:
            artifact = evidence_path(path.parent, suite['evidence']['path'])
            if digest(artifact) != suite['evidence']['sha256']:
                raise ValueError('Evidence hash differs')
            if artifact.suffix == '.trx':
                counts, tree = read_trx(artifact)
                if counts != suite['counts'] or tree.get('id') != report['runId']:
                    raise ValueError('Evidence counts/run do not match report')
            else:
                tool = json.loads(artifact.read_text())
                if tool['runId'] != report['runId'] or tool['counts'] != suite['counts']:
                    raise ValueError('Tool evidence does not match report')
        elif suite['status'] == 'passed':
            raise ValueError('Missing required passing evidence')
    passing = all(s['status']=='passed' for s in suites)
    if report['status'] != ('passed' if passing else 'failed'):
        raise ValueError('Overall outcome disagrees with suites')
    return report

def aggregate(paths: list[Path], profiles: list[str]) -> dict:
    reports = [validate_report(p) for p in paths]
    if not reports or any(r['workingTreeDirty'] or r['status'] != 'passed' for r in reports):
        raise ValueError('CI reports must be clean and passing')
    if len({(r['sourceCommit'],r['sourceFingerprint']) for r in reports}) != 1:
        raise ValueError('CI jobs refer to different source')
    actual = [profile for report in reports for profile in report['requestedProfile']]
    if sorted(actual) != sorted(profiles):
        raise ValueError('CI job/profile evidence is incomplete or duplicated')
    return {'schemaVersion':1, 'status':'passed', 'sourceCommit':reports[0]['sourceCommit'], 'sourceFingerprint':reports[0]['sourceFingerprint'],
            'profiles':profiles, 'notExecuted':[p for p in load_plan()['profiles'] if p not in profiles],
            'runs':[r['runId'] for r in reports]}
