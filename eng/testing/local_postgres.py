"""Optional disposable PostgreSQL 18 instance. No Windows services or shared databases."""
from __future__ import annotations
import os
from pathlib import Path
import secrets
import socket
import subprocess
import time
from isolation import OwnedRun,private_environment,reject_links

class LocalPostgres:
    def __init__(self,bin_directory:Path):
        self.bin=reject_links(bin_directory)
        if any(not (self.bin/(name+('.exe' if os.name=='nt' else ''))).is_file() for name in ('initdb','postgres','psql','pg_isready')):
            raise ValueError('A complete PostgreSQL binary directory is required')
        version=subprocess.check_output([self.command('postgres'),'--version'],stderr=subprocess.DEVNULL).decode()
        if ' 18.' not in version: raise ValueError('PostgreSQL 18 is required')
        self.owner=OwnedRun('codex-f01-native-'); self.data=self.owner.path('postgres-source')
        self.password=secrets.token_urlsafe(32); self.process=None
        with socket.socket() as port: port.bind(('127.0.0.1',0)); self.port=port.getsockname()[1]
        self.environment=private_environment()
        self.environment.update(PGHOST='127.0.0.1',PGPORT=str(self.port),PGUSER='postgres',PGDATABASE='postgres',PGPASSWORD=self.password)
        self.connection=f'Host=127.0.0.1;Port={self.port};Database=postgres;Username=postgres;Password={self.password};SSL Mode=Disable'

    def command(self,name): return str(self.bin/(name+('.exe' if os.name=='nt' else '')))

    def start(self):
        try:
            secret=self.owner.path('password'); secret.write_text(self.password,encoding='ascii')
            subprocess.run([self.command('initdb'),'-D',str(self.data),'-U','postgres','--auth-host=scram-sha-256','--auth-local=trust','--pwfile',str(secret),'--encoding=UTF8','--locale=C'],env=self.environment,
                           stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,check=True,timeout=60)
            secret.unlink()
            # Existing native standby tests intentionally omit a replication password.
            # This exception belongs only to this disposable, loopback-only cluster.
            hba=self.data/'pg_hba.conf'
            hba.write_text('host replication postgres 127.0.0.1/32 trust\n'+hba.read_text(),encoding='ascii')
            with (self.data/'postgresql.conf').open('a',encoding='ascii') as stream:
                stream.write(f"\nlisten_addresses='127.0.0.1'\nport={self.port}\nmax_connections=64\nwal_level=replica\nmax_wal_senders=10\narchive_mode=off\ncluster_name='exam-integration-tests'\n")
            self.process=self.owner.start([self.command('postgres'),'-D',str(self.data)],self.owner.root,self.environment)
            deadline=time.monotonic()+30
            while time.monotonic()<deadline:
                if self.process.poll() is not None: raise RuntimeError('Disposable database exited early')
                if subprocess.run([self.command('pg_isready')],env=self.environment,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL).returncode==0: return self
                time.sleep(.1)
            raise TimeoutError('Disposable database never became ready')
        except BaseException: self.close();raise

    def sql(self,query: str):
        result=subprocess.run([self.command('psql'),'-X','-t','-A','-v','ON_ERROR_STOP=1'],input=query,env=self.environment,capture_output=True,text=True,check=True,timeout=30)
        return result.stdout.strip()

    def close(self):
        if self.process is not None:
            reject_links(self.data).relative_to(self.owner.root)
            if __import__('json').loads(self.owner.marker.read_text())!={'runId':self.owner.run_id}: raise ValueError('Database owner changed')
            status=subprocess.run([self.command('pg_ctl'),'-D',str(self.data),'status'],env=self.environment,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,timeout=10)
            # Native recovery tests may restart the same owned cluster with pg_ctl.
            if status.returncode==0:
                actual=self.sql("SELECT current_setting('data_directory')")
                if Path(actual).resolve()!=self.data: raise ValueError('Database directory identity changed')
                subprocess.run([self.command('pg_ctl'),'-D',str(self.data),'-m','fast','stop'],env=self.environment,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,check=True,timeout=30)
            elif status.returncode!=3: raise RuntimeError('Cannot confirm owned database lifecycle')
        self.owner.close()

    def __enter__(self): return self.start()
    def __exit__(self,*_): self.close()
