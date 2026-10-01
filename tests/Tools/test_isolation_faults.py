import hashlib
import http.client
from http.server import BaseHTTPRequestHandler,ThreadingHTTPServer
import json
from pathlib import Path
import socket
import sys
import threading
import unittest
sys.path.insert(0,str(Path(__file__).resolve().parents[2]/'eng/testing'))
from fault_proxy import FaultProxy,FaultRule
from isolation import OwnedRun,private_environment

class IsolationTests(unittest.TestCase):
    def test_concurrent_runs_and_wrong_process_or_path_are_rejected(self):
        with OwnedRun() as first,OwnedRun() as second:
            self.assertNotEqual(first.root,second.root)
            with self.assertRaises(ValueError): first.path('../other')
            with self.assertRaises(ValueError): first.stop(object())
            process=first.start([sys.executable,'-c','import time; time.sleep(60)'],Path.cwd(),private_environment())
            with self.assertRaises(ValueError): second.stop(process)
            first.stop(process); first.stop(process); self.assertIsNotNone(process.poll())
        self.assertFalse(first.root.exists()); self.assertFalse(second.root.exists())

    def test_changed_owner_refuses_cleanup(self):
        run=OwnedRun(); run.marker.write_text('{}')
        with self.assertRaises(ValueError): run.close()
        self.assertTrue(run.root.exists())
        run.marker.write_text(json.dumps({'runId':run.run_id})); run.close()

    def test_production_environment_is_not_inherited(self):
        from unittest.mock import patch
        with patch.dict('os.environ',{'EXAM_OPERATOR_TOKEN':'synthetic-sensitive','Kestrel__Endpoints__X__Url':'https://example.invalid'}):
            env=private_environment(); self.assertNotIn('EXAM_OPERATOR_TOKEN',env); self.assertNotIn('Kestrel__Endpoints__X__Url',env)

class FaultTests(unittest.TestCase):
    def setUp(self):
        owner=self; self.saved=0
        class Handler(BaseHTTPRequestHandler):
            def log_message(self,*_): pass
            def do_GET(self):
                owner.saved+=1; body=b'synthetic-file-bytes'
                self.send_response(200); self.send_header('Content-Length',str(len(body))); self.end_headers(); self.wfile.write(body)
        self.server=ThreadingHTTPServer(('127.0.0.1',0),Handler); self.thread=threading.Thread(target=self.server.serve_forever,daemon=True); self.thread.start()
        self.target=f'http://127.0.0.1:{self.server.server_port}'

    def tearDown(self): self.server.shutdown();self.server.server_close();self.thread.join()

    def request(self,target,path='/trigger',timeout=1):
        from urllib.parse import urlsplit
        url=urlsplit(target); conn=http.client.HTTPConnection(url.hostname,url.port,timeout=timeout)
        try: conn.request('GET',path);response=conn.getresponse();return response.read()
        finally: conn.close()

    def test_drop_response_happens_after_upstream_work_and_retry_recovers(self):
        with FaultProxy(self.target,FaultRule('/trigger','drop-response')) as proxy:
            with self.assertRaises(http.client.RemoteDisconnected): self.request(proxy.url)
            self.assertEqual(1,self.saved)
            self.assertEqual(b'synthetic-file-bytes',self.request(proxy.url)); proxy.require_triggered()
            self.assertEqual(2,self.saved)

    def test_truncation_corruption_and_timeout_are_real(self):
        for mode in ('truncate','corrupt','timeout'):
            with self.subTest(mode=mode),FaultProxy(self.target,FaultRule('/trigger',mode,delay_seconds=.1)) as proxy:
                if mode=='truncate':
                    with self.assertRaises(http.client.IncompleteRead): self.request(proxy.url)
                elif mode=='timeout':
                    with self.assertRaises((TimeoutError,socket.timeout)): self.request(proxy.url,timeout=.02)
                else: self.assertNotEqual(hashlib.sha256(b'synthetic-file-bytes').digest(),hashlib.sha256(self.request(proxy.url)).digest())
                proxy.require_triggered()

    def test_non_trigger_and_non_loopback_are_rejected(self):
        with self.assertRaises(ValueError): FaultProxy('http://192.0.2.1',FaultRule('/trigger','drop-response'))
        with FaultProxy(self.target,FaultRule('/trigger','drop-response')) as proxy:
            self.request(proxy.url,'/other')
            with self.assertRaises(ValueError): proxy.require_triggered()

if __name__=='__main__': unittest.main()
