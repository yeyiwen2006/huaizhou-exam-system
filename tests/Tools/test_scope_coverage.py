import copy
import json
from pathlib import Path
import sys
import tempfile
import unittest
sys.path.insert(0,str(Path(__file__).resolve().parents[2]/'eng/testing'))
from evidence import ROOT,digest,write_json
from run_synthetic import load_evidence,validate_synthetic

class ScopeChecks(unittest.TestCase):
    def test_coverage_paths_and_explicit_consumer_gaps_are_reviewable(self):
        data=json.loads((ROOT/'docs/test-coverage.json').read_text(encoding='utf-8'))
        self.assertEqual(1,data['schemaVersion']);rows=data['rows']
        self.assertGreaterEqual(len(rows),50);self.assertEqual(len(rows),len({r['id'] for r in rows}))
        required={'requirement','action','gui','cli','sharedApi','successTest','failureTest','category','automatedEvidence','manualCheck','status','gap'}
        for row in rows:
            self.assertTrue(required<=row.keys());self.assertTrue((ROOT/row['successTest']).exists())
            if row['gui']:self.assertTrue((ROOT/row['gui']).is_file())

    def test_markdown_matrix_matches_machine_rows_and_current_business_entrypoints(self):
        rows=json.loads((ROOT/'docs/test-coverage.json').read_text(encoding='utf-8'))['rows']
        markdown=(ROOT/'docs/gui-cli-coverage.md').read_text(encoding='utf-8')
        table={parts[0]:parts for line in markdown.splitlines() if line.startswith('| COV-')
               for parts in [[cell.strip() for cell in line.strip('|').split('|')]]}
        self.assertEqual({r['id'] for r in rows},set(table))
        for row in rows:
            self.assertEqual([row['id'],row['action'],row['gui'] or '未接线/需消费方核对',
                              row['cli'] or '专用命令未接线',row['status']],table[row['id']])
            self.assertTrue((ROOT/row['failureTest']).exists())
        expected={24:'activation approve',25:'activation revoke',26:'devices show',27:'rooms create',
                  28:'rooms rename',29:'rooms move',30:'rooms delete',36:'roster list',43:'stream',
                  44:'decisions keep',45:'commands resend',46:'monitor events',47:'monitor watch'}
        index={int(r['id'].split('-')[1]):r for r in rows}
        for number,command in expected.items():
            self.assertIn(command,index[number]['cli'])
            self.assertNotIn('未实现',index[number]['status'])
            self.assertNotIn('未接线',index[number]['status'])
        self.assertIn('只处理收齐',index[47]['status'])
        self.assertIn('申请标识',index[24]['cli'])

    def test_reduced_duration_sessions_missing_progress_and_failed_load_are_rejected(self):
        valid={'sessions':300,'durationSeconds':120,'questionCount':120,'interrupted':False,'localScenarioChecksPassed':True,
               'operations':{'progress':{'count':300,'failed':0,'latencyTargetMet':True}}}
        load_evidence(valid,300,120)
        for defect in ('count','duration','questions','interrupted','failed','missing','latency'):
            value=copy.deepcopy(valid)
            if defect=='count':value['sessions']=3
            elif defect=='duration':value['durationSeconds']=4
            elif defect=='questions':value['questionCount']=119
            elif defect=='interrupted':value['interrupted']=True
            elif defect=='failed':value['operations']['progress']['failed']=1
            elif defect=='missing':value['operations']={}
            else:value['operations']['progress']['latencyTargetMet']=False
            with self.subTest(defect=defect),self.assertRaises(ValueError):load_evidence(value,300,120)

    def test_independent_audit_foreign_run_hash_and_missing_faults_fail(self):
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory);run='a'*32
            audit={'sourceCommit':'b'*40,'examFingerprint':'c'*64,'runId':run,'passed':True,'expectedParticipants':3,'questionCount':120,'invalidAnswerSets':0,
                   'counts':{k:3 for k in ('actual','submitted','finals','uniqueReceipts','gradingTasks')}}
            audit['counts'].update(completionEvents=1,pendingCollectionChecks=0)
            scenarios=[{'scenario':'response-loss','triggered':True,'originalReceiptRetained':True,'retries':20,'events':[{'upstreamStatus':200}]},
                       {'scenario':'owned-process-restart','receiptRetained':True},*[
                       {'scenario':n,'triggered':True,'recovered':True,'events':[{'upstreamStatus':200}]} for n in ('network-timeout','download-truncation','download-corruption')]]
            report={'schemaVersion':1,'status':'passed','synthetic':True,'productionCapacityCertified':False,'runId':run,'sourceCommit':'b'*40,'examFingerprint':'c'*64,
                    'requested':{'sessions':3,'questionCount':120,'durationSeconds':4,'mode':'faults'},'executables':{'synthetic':'d'*64},'lifecycle':[{'action':'stopped'}],
                    'databaseCleanup':'passed','scenarios':scenarios,'grading':{'currentGradingVersion':1,'finalSubmissionCount':3,'gradedAtCurrentVersion':3}}
            for defect in ('valid','audit-count','foreign-run','hash','missing-fault','lost-receipt','grading','scope'):
                a,r=copy.deepcopy(audit),copy.deepcopy(report)
                if defect=='audit-count':a['counts']['finals']=2
                elif defect=='foreign-run':a['runId']='e'*32
                elif defect=='missing-fault':r['scenarios'].pop()
                elif defect=='lost-receipt':r['scenarios'][1]['receiptRetained']=False
                elif defect=='grading':r['grading']['gradedAtCurrentVersion']=2
                elif defect=='scope':r['requested']['sessions']=300
                path=root/'database-audit.json';path.write_text(json.dumps(a))
                r['audit']={'path':path.name,'sha256':'f'*64 if defect=='hash' else digest(path)}
                final=root/'synthetic-report.json';final.write_text(json.dumps(r))
                if defect=='valid':validate_synthetic(final)
                else:
                    with self.subTest(defect=defect),self.assertRaises(ValueError):validate_synthetic(final)

if __name__=='__main__':unittest.main()
