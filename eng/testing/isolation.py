"""Only handles and paths created by this run can be stopped or cleaned."""
from __future__ import annotations
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import time
import uuid
import signal

def windows_job(process):
    import ctypes
    from ctypes import wintypes
    class Basic(ctypes.Structure):
        _fields_=[('processTime',ctypes.c_longlong),('jobTime',ctypes.c_longlong),('flags',wintypes.DWORD),
                  ('minWorking',ctypes.c_size_t),('maxWorking',ctypes.c_size_t),('activeLimit',wintypes.DWORD),
                  ('affinity',ctypes.c_size_t),('priority',wintypes.DWORD),('scheduling',wintypes.DWORD)]
    class Io(ctypes.Structure):
        _fields_=[(name,ctypes.c_ulonglong) for name in ('reads','writes','other','readBytes','writeBytes','otherBytes')]
    class Extended(ctypes.Structure):
        _fields_=[('basic',Basic),('io',Io),('processMemory',ctypes.c_size_t),('jobMemory',ctypes.c_size_t),('peakProcess',ctypes.c_size_t),('peakJob',ctypes.c_size_t)]
    kernel=ctypes.WinDLL('kernel32',use_last_error=True)
    kernel.CreateJobObjectW.argtypes=[ctypes.c_void_p,wintypes.LPCWSTR];kernel.CreateJobObjectW.restype=wintypes.HANDLE
    kernel.SetInformationJobObject.argtypes=[wintypes.HANDLE,ctypes.c_int,ctypes.c_void_p,wintypes.DWORD]
    kernel.AssignProcessToJobObject.argtypes=[wintypes.HANDLE,wintypes.HANDLE]
    kernel.TerminateJobObject.argtypes=[wintypes.HANDLE,wintypes.UINT]
    kernel.CloseHandle.argtypes=[wintypes.HANDLE]
    handle=kernel.CreateJobObjectW(None,None);limits=Extended();limits.basic.flags=0x2000
    if not handle or not kernel.SetInformationJobObject(handle,9,ctypes.byref(limits),ctypes.sizeof(limits)) or not kernel.AssignProcessToJobObject(handle,int(process._handle)):
        if handle:kernel.CloseHandle(handle)
        process.kill();process.wait(timeout=15)
        raise OSError('Cannot establish owned process job')
    return kernel,handle

def reject_links(path: Path) -> Path:
    for current in [path,*path.parents]:
        if current.is_symlink() or (hasattr(current,'is_junction') and current.is_junction()):
            raise ValueError('Run resources must not traverse links')
    return path.resolve()

def private_environment(settings: dict | None = None) -> dict:
    allowed={'systemroot','windir','path','pathext','temp','tmp','comspec','userprofile','appdata','localappdata',
             'programfiles','programfiles(x86)','programdata','systemdrive','dotnet_root','dotnet_cli_home','nuget_packages'}
    result={k:v for k,v in os.environ.items() if k.lower() in allowed}
    def flatten(values,prefix=''):
        for key,value in values.items():
            name=prefix+'__'+key if prefix else key
            if isinstance(value,dict): flatten(value,name)
            elif isinstance(value,list): flatten({str(i):v for i,v in enumerate(value)},name)
            else: result[name]=str(value).lower() if isinstance(value,bool) else str(value)
    flatten(settings or {})
    result['DOTNET_CLI_TELEMETRY_OPTOUT']='1'
    return result

class OwnedRun:
    def __init__(self,prefix='exam-e01-'):
        if prefix not in ('exam-e01-','codex-f01-native-'): raise ValueError('Unknown isolation prefix')
        self.run_id=uuid.uuid4().hex
        self.root=reject_links(Path(tempfile.mkdtemp(prefix=prefix)))
        self.marker=self.root/'.owner.json'
        self.marker.write_text(json.dumps({'runId':self.run_id}),encoding='ascii')
        self.handles=[]; self.streams=[]; self.events=[]; self.closed=False;self.jobs={}

    def path(self,relative: str) -> Path:
        value=Path(relative)
        if value.is_absolute() or '..' in value.parts: raise ValueError('Run path escapes owned directory')
        path=self.root/value
        reject_links(path).relative_to(self.root)
        return path

    def start(self,command:list[str],cwd:Path,environment:dict|None=None):
        if self.closed: raise ValueError('Run is closed')
        log=self.path(f'process-{len(self.handles)}.log').open('xb'); self.streams.append(log)
        kwargs={'creationflags':subprocess.CREATE_NO_WINDOW} if os.name=='nt' else {'start_new_session':True}
        process=subprocess.Popen(command,cwd=cwd,env=environment,stdout=log,stderr=log,**kwargs)
        if os.name=='nt':self.jobs[process]=windows_job(process)
        self.handles.append(process)
        self.events.append({'action':'started','processIndex':len(self.handles)-1})
        return process

    def stop(self,process):
        if not any(handle is process for handle in self.handles): raise ValueError('Process was not created by this run')
        if process.poll() is None:
            process.terminate()
            try: process.wait(timeout=15)
            except subprocess.TimeoutExpired: process.kill(); process.wait(timeout=15)
        if os.name=='nt' and process in self.jobs:
            kernel,handle=self.jobs.pop(process);kernel.TerminateJobObject(handle,1);kernel.CloseHandle(handle)
        elif os.name!='nt':
            try:os.killpg(process.pid,signal.SIGTERM)
            except ProcessLookupError:pass
        self.events.append({'action':'stopped','processIndex':self.handles.index(process)})

    def close(self):
        if self.closed: return
        for process in reversed(self.handles): self.stop(process)
        for stream in self.streams: stream.close()
        if reject_links(self.root)!=self.root or json.loads(self.marker.read_text())!={'runId':self.run_id}:
            raise ValueError('Run ownership changed; file cleanup refused')
        # Audit every descendant before a recursive removal, including directory links.
        for child in self.root.rglob('*'): reject_links(child).relative_to(self.root)
        for attempt in range(20):
            try:shutil.rmtree(self.root);break
            except PermissionError:
                if attempt==19:raise
                time.sleep(.1)
        self.closed=True

    def __enter__(self): return self
    def __exit__(self,*_): self.close()
