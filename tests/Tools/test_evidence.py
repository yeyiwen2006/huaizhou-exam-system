"""All inputs are synthetic; test the evidence boundary by corrupting real XML/files."""
import copy
import json
import re
from pathlib import Path
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET

sys.path.insert(0,str(Path(__file__).resolve().parents[2]/'eng/testing'))
import evidence as e
from run_checks import dependency

class EvidenceTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory(); self.root=Path(self.temp.name)
        self.plan={'projects':['synthetic.csproj'], 'profiles':{'core':{'projects':[0],'filter':'Category!=Heavy'},'heavy':{'projects':[0],'filter':'Category=Heavy'}}}
        self.run='a'*32

    def tearDown(self): self.temp.cleanup()

    def trx(self, count=1, outcome='Passed'):
        root=ET.Element('{'+e.NS+'}TestRun',id=self.run)
        results=ET.SubElement(root,'{'+e.NS+'}Results')
        for index in range(count):
            entry=ET.SubElement(results,'{'+e.NS+'}UnitTestResult',testId=f't{index}',executionId=f'e{index}',outcome=outcome,computerName='private-host')
            ET.SubElement(entry,'{'+e.NS+'}Output').text='synthetic-private-response'
        summary=ET.SubElement(root,'{'+e.NS+'}ResultSummary',outcome='Completed')
        ET.SubElement(summary,'{'+e.NS+'}Counters',total=str(count),passed=str(count if outcome=='Passed' else 0),failed=str(count if outcome=='Failed' else 0),notExecuted=str(count if outcome=='NotExecuted' else 0))
        path=self.root/'raw.trx'; ET.ElementTree(root).write(path,encoding='utf-8'); return path

    def report(self):
        raw=self.trx(); artifact=self.root/'result.trx'; counts=e.public_trx(raw,artifact,self.run)
        e.write_json(self.root/'tools.json',{'runId':self.run,'counts':counts})
        suites=[{'id':'core-synthetic','project':'synthetic.csproj','filter':'Category!=Heavy','status':'passed','exitCode':0,'counts':counts,
                 'evidence':{'path':'result.trx','sha256':e.digest(artifact)}},
                {'id':'tools','status':'passed','exitCode':0,'counts':counts,'evidence':{'path':'tools.json','sha256':e.digest(self.root/'tools.json')}}]
        return {'schemaVersion':1,'runId':self.run,'sourceCommit':'b'*40,'sourceFingerprint':'c'*64,'workingTreeDirty':False,
                'requestedProfile':['core'],'environment':{},'suites':suites,'notExecuted':[{'profile':'heavy','reason':'Not requested'}],
                'startedAt':'synthetic-start','finishedAt':'synthetic-finish','status':'passed'}

    def validate(self,report):
        path=self.root/'report.json'; path.write_text(json.dumps(report)); return e.validate_report(path,self.plan)

    def test_sanitized_evidence_has_no_raw_output_or_host(self):
        report=self.report(); self.validate(report)
        text=(self.root/'result.trx').read_text()
        self.assertNotIn('private',text); self.assertNotIn('computerName',text)

    def test_digest_and_trx_escaping_preserves_exact_identities(self):
        value='abc'+'1'+'3'+'0'*9+'f'*18
        path=self.root/'identity.json';e.write_json(path,{'runId':value})
        self.assertEqual(value,json.loads(path.read_text())['runId'])
        self.assertIsNone(re.search(r'(?<!\d)1[3-9]\d{9}(?!\d)',path.read_text()))
        raw=self.trx();tree=ET.parse(raw);tree.getroot().set('id',value)
        entry=tree.getroot().find('{'+e.NS+'}Results/{'+e.NS+'}UnitTestResult');entry.set('testId',value);entry.set('executionId',value)
        tree.write(raw,encoding='utf-8');output=self.root/'sanitized.trx';e.public_trx(raw,output,value)
        _,observed=e.read_trx(output)
        self.assertEqual(value,observed.get('id'));self.assertEqual(value,observed.find('{'+e.NS+'}Results/{'+e.NS+'}UnitTestResult').get('testId'))
        self.assertIsNone(re.search(r'(?<!\d)1[3-9]\d{9}(?!\d)',output.read_text()))

    def test_invalid_xml_empty_incomplete_and_duplicate_results_fail(self):
        for mode in ('broken','empty','duplicate','counter','aborted','unknown'):
            with self.subTest(mode=mode):
                path=self.trx(0 if mode=='empty' else 1)
                if mode=='broken': path.write_text('<broken>')
                else:
                    tree=ET.parse(path); root=tree.getroot()
                    if mode=='duplicate': root.find('{'+e.NS+'}Results').append(copy.deepcopy(root.find('.//{'+e.NS+'}UnitTestResult')))
                    if mode=='counter': root.find('.//{'+e.NS+'}Counters').set('total','42')
                    if mode=='aborted': root.find('{'+e.NS+'}ResultSummary').set('outcome','Aborted')
                    if mode=='unknown': root.find('.//{'+e.NS+'}UnitTestResult').set('outcome','Pending')
                    tree.write(path)
                with self.assertRaises((ValueError,ET.ParseError)): e.read_trx(path)

    def test_missing_fields_suites_scope_exit_code_and_omissions_fail(self):
        original=self.report()
        for mode in ('field','suite','duplicate','filter','exit','skip','omission','unknown','status'):
            report=copy.deepcopy(original)
            if mode=='field': report.pop('sourceFingerprint')
            if mode=='suite': report['suites'].pop(0)
            if mode=='duplicate': report['suites'].append(copy.deepcopy(report['suites'][0]))
            if mode=='filter': report['suites'][0]['filter']='FullyQualifiedName~OneTest'
            if mode=='exit': report['suites'][0]['exitCode']=1
            if mode=='skip': report['suites'][0]['counts']['skipped']=1
            if mode=='omission': report['notExecuted']=[]
            if mode=='unknown': report['requestedProfile']=['unknown']
            if mode=='status': report['status']='failed'
            with self.subTest(mode=mode),self.assertRaises(ValueError): self.validate(report)

    def test_missing_stale_hash_or_foreign_run_evidence_fails(self):
        original=self.report()
        for mode in ('missing','hash','run','path'):
            report=copy.deepcopy(original)
            entry=report['suites'][0]
            if mode=='missing': entry['evidence']['path']='missing.trx'
            if mode=='hash': entry['evidence']['sha256']='0'*64
            if mode=='run': report['runId']='d'*32
            if mode=='path': entry['evidence']['path']='../result.trx'
            with self.subTest(mode=mode),self.assertRaises((ValueError,OSError)): self.validate(report)

    def test_tool_evidence_cannot_be_swapped(self):
        report=self.report(); artifact=self.root/'tools.json'
        artifact.write_text(json.dumps({'runId':'f'*32,'counts':report['suites'][1]['counts']}))
        report['suites'][1]['evidence']['sha256']=e.digest(artifact)
        with self.assertRaises(ValueError): self.validate(report)

    def test_missing_dependency_is_blocked(self):
        from unittest.mock import patch
        with patch.dict('os.environ',{},clear=True):
            self.assertIsNotNone(dependency('postgres')); self.assertIsNotNone(dependency('postgres-physical'))

    def test_real_plan_covers_solution_projects_and_categories(self): self.assertEqual(6,len(e.load_plan()['projects']))

    def test_aggregation_rejects_failed_dirty_mixed_or_missing_jobs(self):
        from unittest.mock import patch
        first=self.report(); second=copy.deepcopy(first); second['requestedProfile']=['heavy']; second['suites'][0]['id']='heavy-synthetic'; second['suites'][0]['filter']='Category=Heavy'; second['notExecuted']=[{'profile':'core','reason':'Not requested'}]
        for mode in ('missing','dirty','failed','mixed','duplicate'):
            reports=[copy.deepcopy(first),copy.deepcopy(second)]
            if mode=='missing': reports.pop()
            if mode=='dirty': reports[0]['workingTreeDirty']=True
            if mode=='failed': reports[0]['status']='failed'
            if mode=='mixed': reports[0]['sourceCommit']='e'*40
            if mode=='duplicate': reports.append(copy.deepcopy(second))
            with patch.object(e,'validate_report',side_effect=reports),patch.object(e,'load_plan',return_value=self.plan),self.subTest(mode=mode),self.assertRaises(ValueError):
                e.aggregate([self.root/'report.json']*len(reports),['core','heavy'])

if __name__=='__main__': unittest.main()
