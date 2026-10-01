"""Deterministic request barriers on an owned loopback proxy, never production network changes."""
from __future__ import annotations
from dataclasses import dataclass
import http.client
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import ipaddress
import socket
import threading
import time
from urllib.parse import urlsplit

class FaultRule:
    path: str
    mode: str
    occurrence: int=1
    delay_seconds: float=2

FaultRule = dataclass(frozen=True)(FaultRule)

class FaultProxy:
    def __init__(self,upstream:str,rule:FaultRule):
        target=urlsplit(upstream)
        if target.scheme!='http' or target.username or target.password or target.query or target.fragment or target.path not in ('','/') or not ipaddress.ip_address(target.hostname).is_loopback:
            raise ValueError('Fault upstream must be an explicit HTTP loopback origin')
        if rule.mode not in ('drop-response','timeout','truncate','corrupt') or rule.occurrence<1 or not 0<rule.delay_seconds<=30 or not rule.path.startswith('/'):
            raise ValueError('Invalid deterministic fault rule')
        self.target=target; self.rule=rule; self.matches=0; self.events=[]; self.lock=threading.Lock(); self.stop_event=threading.Event()
        owner=self
        class Handler(BaseHTTPRequestHandler):
            protocol_version='HTTP/1.1'
            def log_message(self,*_): pass
            def execute(self):
                size=int(self.headers.get('Content-Length','0'))
                if size<0 or size>64*1024*1024: self.send_error(413); return
                body=self.rfile.read(size)
                with owner.lock:
                    selected=False
                    if self.path.split('?',1)[0]==owner.rule.path:
                        owner.matches+=1; selected=owner.matches==owner.rule.occurrence
                connection=http.client.HTTPConnection(owner.target.hostname,owner.target.port,timeout=30)
                try:
                    headers={k:v for k,v in self.headers.items() if k.lower() not in ('host','connection','transfer-encoding')}
                    connection.request(self.command,self.path,body,headers)
                    response=connection.getresponse(); payload=response.read()
                    if selected:
                        with owner.lock: owner.events.append({'mode':owner.rule.mode,'occurrence':owner.matches,'upstreamStatus':response.status,'upstreamBytes':len(payload)})
                        if owner.rule.mode=='drop-response':
                            self.close_connection=True; self.connection.shutdown(socket.SHUT_RDWR); return
                        if owner.rule.mode=='timeout': owner.stop_event.wait(owner.rule.delay_seconds)
                        if owner.rule.mode=='corrupt' and payload: payload=bytes([payload[0]^1])+payload[1:]
                    self.send_response(response.status)
                    for key,value in response.getheaders():
                        if key.lower() not in ('content-length','transfer-encoding','connection','server','date'): self.send_header(key,value)
                    self.send_header('Content-Length',str(len(payload))); self.send_header('Connection','close'); self.end_headers()
                    if selected and owner.rule.mode=='truncate': payload=payload[:-1]
                    self.wfile.write(payload); self.close_connection=True
                except (OSError,http.client.HTTPException): self.close_connection=True
                finally: connection.close()
            do_GET=execute; do_POST=execute; do_PUT=execute; do_PATCH=execute; do_DELETE=execute
        self.server=ThreadingHTTPServer(('127.0.0.1',0),Handler)
        self.server.daemon_threads=True
        self.thread=threading.Thread(target=self.server.serve_forever,daemon=True)
        self.thread.start(); self.url=f'http://127.0.0.1:{self.server.server_port}'

    def require_triggered(self):
        if len(self.events)!=1: raise ValueError('Requested fault did not trigger exactly once')

    def close(self):
        self.stop_event.set(); self.server.shutdown(); self.server.server_close(); self.thread.join(timeout=5)
        if self.thread.is_alive(): raise RuntimeError('Fault proxy did not stop')

    def __enter__(self): return self
    def __exit__(self,*_): self.close()
