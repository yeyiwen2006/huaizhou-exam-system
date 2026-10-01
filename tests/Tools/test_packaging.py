"""Adversarial package integrity tests use independent artifact metadata observations."""
import copy
import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import unittest
sys.path.insert(0,str(Path(__file__).resolve().parents[2]/'eng/testing'))
spec=importlib.util.spec_from_file_location('e01_package',Path(__file__).resolve().parents[2]/'eng/packaging/package.py')
p=importlib.util.module_from_spec(spec);spec.loader.exec_module(p)

class PackageChecks(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory(prefix='exam-e01-package-');self.root=Path(self.temp.name)
        self.identity={'sourceCommit':'b'*40,'sourceFingerprint':'c'*64,'workingTreeDirty':False}
        self.locks={'synthetic/packages.lock.json':'d'*64}
        self.lock_hash=p.hashlib.sha256(json.dumps(self.locks,sort_keys=True).encode()).hexdigest()
        self.artifacts={}
        for name in p.COMPONENTS:
            directory=self.root/name;directory.mkdir()
            required=[name+'.exe',name+'.dll',name+'.runtimeconfig.json',name+'.deps.json','hostfxr.dll','hostpolicy.dll','coreclr.dll']
            if name in ('Exam.Admin','Exam.Student'):required+=['PresentationFramework.dll','wpfgfx_cor3.dll']
            if name=='Exam.Student':required+=['e_sqlite3.dll']
            for filename in required:(directory/filename).write_bytes(b'synthetic artifact')
            self.artifacts[name]={'schemaVersion':1,'assemblyName':name,'version':'1.0.0.0','informationalVersion':'1.0.0+synthetic',
                'metadata':{'E01SourceCommit':self.identity['sourceCommit'],'E01SourceFingerprint':self.identity['sourceFingerprint'],'E01LockFingerprint':self.lock_hash}}
        path=self.root/'Exam.Server/migrations/synthetic.sql';path.parent.mkdir();path.write_text('SELECT 1;')
        self.manifest={'schemaVersion':1,**self.identity,'mode':'candidate','runtimeIdentifier':'win-x64','configuration':'Release','selfContained':True,'sdk':'synthetic-sdk',
            'lockFingerprint':self.lock_hash,'dependencyLocks':self.locks,'components':[{'name':n,'entry':f'{n}/{n}.exe','assembly':copy.deepcopy(self.artifacts[n])} for n in p.COMPONENTS],
            'migrations':{'src/synthetic.sql':p.digest(path)},'externalDependencies':{},'files':p.inventory(self.root)}
        self.save()

    def tearDown(self):self.temp.cleanup()
    def probe(self,root,name):return self.artifacts[name]
    def save(self):
        (self.root/p.MANIFEST).write_text(json.dumps(self.manifest))
        (self.root/p.COMPLETE).write_text(json.dumps({'schemaVersion':1,'manifestSha256':p.digest(self.root/p.MANIFEST)}))
    def check(self):return p.verify(self.root,self.probe)

    def test_complete_independent_metadata_is_required(self):
        self.check();self.artifacts['Exam.Management']['metadata']['E01SourceCommit']='e'*40
        with self.assertRaises(ValueError):self.check()

    def test_missing_extra_and_equal_length_tampered_files_are_rejected(self):
        path=self.root/'Exam.Student/Exam.Student.dll';original=path.read_bytes()
        for defect in ('missing','tampered','extra'):
            with self.subTest(defect=defect):
                path.write_bytes(original)
                if defect=='missing':path.unlink()
                elif defect=='tampered':path.write_bytes(b'x'*len(original))
                else:(self.root/'unexpected.txt').write_text('synthetic extra')
                with self.assertRaises(ValueError):self.check()
                (self.root/'unexpected.txt').unlink(missing_ok=True)

    def test_unsupported_rid_format_dirty_candidate_and_missing_component_fail(self):
        original=copy.deepcopy(self.manifest)
        for defect in ('rid','schema','dirty','management','duplicate','lock','entry','traversal'):
            with self.subTest(defect=defect):
                self.manifest=copy.deepcopy(original)
                if defect=='rid':self.manifest['runtimeIdentifier']='linux-x64'
                elif defect=='schema':self.manifest['schemaVersion']=0
                elif defect=='dirty':self.manifest['workingTreeDirty']=True
                elif defect=='management':self.manifest['components'].pop(1)
                elif defect=='duplicate':self.manifest['components'][1]=copy.deepcopy(self.manifest['components'][0])
                elif defect=='lock':self.manifest['dependencyLocks']['synthetic/packages.lock.json']='f'*64
                elif defect=='entry':self.manifest['components'][0]['entry']='../entry.exe'
                else:self.manifest['files']['../outside']={'bytes':0,'sha256':'0'*64}
                self.save()
                with self.assertRaises(ValueError):self.check()

    def test_native_file_and_completion_marker_are_mandatory(self):
        (self.root/p.COMPLETE).unlink()
        with self.assertRaises(FileNotFoundError):self.check()
        self.save();(self.root/'Exam.Student/e_sqlite3.dll').unlink()
        self.manifest['files']=p.inventory(self.root);self.save()
        with self.assertRaises(ValueError):self.check()

    def test_existing_destination_and_rebuild_difference_fail(self):
        with self.assertRaises(ValueError):p.build(self.root)
        with tempfile.TemporaryDirectory() as other:
            second=Path(other)/'copy';p.shutil.copytree(self.root,second)
            self.assertEqual('passed',p.compare(self.root,second,self.probe)['status'])
            (second/'Exam.Cli/Exam.Cli.dll').write_text('changed')
            with self.assertRaises(ValueError):p.compare(self.root,second,self.probe)

if __name__=='__main__':unittest.main()
