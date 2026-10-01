#!/usr/bin/env python3
"""Run a published API against a temporary PostgreSQL cluster; no real captures."""
import json, os, pwd, socket, subprocess as sp, sys, tempfile, time
from pathlib import Path
from urllib.request import build_opener, ProxyHandler
from urllib.error import HTTPError
artifact = Path(sys.argv[1]).resolve()
assert not (artifact/'wwwroot').exists(), 'API artifact contains dashboard'
pgbin = Path(os.environ.get('PG_BIN', '/usr/lib/postgresql/18/bin'))
def port():
    with socket.socket() as s:
        s.bind(('127.0.0.1', 0)); return s.getsockname()[1]
with tempfile.TemporaryDirectory(prefix='api-only-') as folder:
    root=Path(folder); root.chmod(0o755)
    db=root/'pg'; db.mkdir(); user=pwd.getpwnam('postgres'); os.chown(db,user.pw_uid,user.pw_gid)
    def pg(*args):
        return sp.run(['runuser','-u','postgres','--',*map(str,args)],check=True,stdout=sp.DEVNULL)
    pg(pgbin/'initdb','-D',db/'data','-A','trust','--no-locale')
    dbport=port(); apiport=port()
    pg(pgbin/'pg_ctl','-D',db/'data','-l',db/'log','-o',f'-k {db} -h 127.0.0.1 -p {dbport}','-w','start')
    process=None
    try:
        captures=root/'captures'; captures.mkdir(); (captures/'fixture.webp').write_bytes(b'fixture')
        # Even a leftover webroot must not be exposed by the API process.
        (root/'wwwroot').mkdir(); (root/'wwwroot/index.html').write_text('must not be served')
        env=dict(os.environ, ASPNETCORE_URLS=f'http://127.0.0.1:{apiport}',
            ConnectionStrings__DefaultConnection=f'Host=127.0.0.1;Port={dbport};Database=postgres;Username=postgres',
            VisualMonitor__Enabled='false',VisualMonitor__CaptureOnStartup='false',
            VisualMonitor__ScreenshotRoot=str(captures), SiteBuilder__Root=str(root/'builder'),Swagger__Enabled='true')
        with (root/'api.log').open('w+') as log:
            process=sp.Popen(['dotnet',str(artifact/'CheckWeb.Backend.dll')],cwd=root,env=env,stdout=log,stderr=log)
            client=build_opener(ProxyHandler({}))
            def get(path):
                try:
                    with client.open(f'http://127.0.0.1:{apiport}'+path,timeout=5) as response:
                        return response.status,response.read()
                except HTTPError as e: return e.code,e.read()
            try:
                for _ in range(100):
                    assert process.poll() is None, 'API exited'
                    try:
                        if get('/api/health')[0]==200: break
                    except OSError: pass
                    time.sleep(.2)
                else: raise AssertionError('Startup timeout')
                assert get('/api/sites')[0]==200
                assert get('/swagger/index.html')[0]==200
                code,document=get('/swagger/v1/swagger.json'); assert code==200
                spec=json.loads(document); assert '/api/sites' in spec['paths'] and '/api/wan' not in spec['paths']
                assert get('/api/wan')[0]==404
                code,payload=get('/api/builder/sites'); assert code==200 and len(json.loads(payload))==3
                for path in ['/','/index.html','/js/app.js','/builder/tvu.json','/previews/tvu.html','/wan/index.html']:
                    assert get(path)[0]==404,path
                assert get('/captures/fixture.webp')==(200,b'fixture')
                print('PASS: API, DB, embedded builder presets, captures; dashboard paths return 404 even with leftover wwwroot.')
            except Exception:
                log.seek(0); print(log.read()); raise
    finally:
        if process is not None and process.poll() is None: process.terminate(); process.wait(timeout=30)
        pg(pgbin/'pg_ctl','-D',db/'data','-m','immediate','-w','stop')
