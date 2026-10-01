import copy
import json
from pathlib import Path
import sys
import tempfile
import unittest
sys.path.insert(0,str(Path(__file__).resolve().parents[2]/'eng/testing'))
from evidence import digest
from validate_ci import package_receipts

class CiReceiptChecks(unittest.TestCase):
    def test_missing_mixed_modified_and_incomplete_package_job_receipts_fail(self):
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory);identity={'sourceCommit':'b'*40,'sourceFingerprint':'c'*64}
            manifest={'schemaVersion':1,**identity,'mode':'candidate','workingTreeDirty':False,'runtimeIdentifier':'win-x64','selfContained':True,
                      'files':{'synthetic':{'bytes':1,'sha256':'d'*64}},'components':[{'name':name} for name in ('Exam.Server','Exam.Management','Exam.Student','Exam.Admin','Exam.Cli')]}
            for defect in ('valid','missing','modified','foreign','rebuild','smoke','smoke-source','smoke-exit'):
                for suffix in ('a','b'):
                    folder=root/('package-'+suffix);folder.mkdir(exist_ok=True)
                    value=copy.deepcopy(manifest)
                    if defect=='foreign' and suffix=='b':value['sourceCommit']='e'*40
                    path=folder/'package-manifest.json';path.write_text(json.dumps(value))
                    (folder/'complete.json').write_text(json.dumps({'schemaVersion':1,'manifestSha256':'f'*64 if defect=='modified' else digest(path)}))
                manifest_sha=digest(root/'package-a/package-manifest.json')
                rebuilt={'schemaVersion':1,'status':'failed' if defect=='rebuild' else 'passed',**identity,'manifestSha256':manifest_sha,'comparedFiles':1,'excludedFiles':['package-manifest.json','complete.json']}
                smoke={'schemaVersion':1,'status':'passed',**identity,'manifestSha256':manifest_sha,'checks':
                    [{'component':'Exam.Cli','operation':operation,'exitCode':0} for operation in ('help','version','artifact-info')]+
                    [{'component':name,'operation':'unconfigured-deployment-rejected','exitCode':1,'expectedRefusal':True} for name in ('Exam.Server','Exam.Management')]}
                if defect=='smoke':smoke['checks'].pop()
                if defect=='smoke-source':smoke['sourceFingerprint']='e'*64
                if defect=='smoke-exit':smoke['checks'][0]['exitCode']=1
                (root/'rebuild.json').write_text(json.dumps(rebuilt));(root/'package-smoke.json').write_text(json.dumps(smoke))
                if defect=='missing':(root/'package-b/complete.json').unlink()
                if defect=='valid':self.assertEqual(1,package_receipts(root,identity))
                else:
                    with self.subTest(defect=defect),self.assertRaises((ValueError,FileNotFoundError)):package_receipts(root,identity)

if __name__=='__main__':unittest.main()
