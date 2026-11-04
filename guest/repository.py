"""Credential-free Git operations inside the task. No network or provider tokens.
The backend sends a bounded Git bundle; this helper never clones a remote URL.
"""
import base64,json,os,pathlib,re,subprocess,sys,tempfile,signal,threading,resource
ROOT=pathlib.Path('/workspace/repo')
LIMIT=8*1024*1024

def emit(value): print(json.dumps(value,ensure_ascii=True),flush=True)
def file_limit():resource.setrlimit(resource.RLIMIT_FSIZE,(16*1024*1024,16*1024*1024))
def bounded(args,cwd,env,timeout,limit,truncate=False,stderr=subprocess.STDOUT):
    process=subprocess.Popen(args,cwd=cwd,env=env,stdout=subprocess.PIPE,stderr=stderr,start_new_session=True,preexec_fn=file_limit)
    output=bytearray();overflow=False
    def drain():
        nonlocal overflow
        while data:=process.stdout.read(8192):
            left=max(0,limit-len(output));output.extend(data[:left]);overflow=overflow or len(data)>left
    reader=threading.Thread(target=drain,daemon=True);reader.start()
    try:
        try:code=process.wait(timeout=timeout)
        except subprocess.TimeoutExpired:code=124
    finally:
        try:os.killpg(process.pid,signal.SIGKILL)
        except ProcessLookupError:pass
        process.wait();reader.join(timeout=3);process.stdout.close()
    if reader.is_alive():raise ValueError('Output stream did not close')
    if overflow and not truncate:raise ValueError('Output exceeds limit')
    return code,bytes(output)+(b'\n[Output truncated]' if overflow else b'')
def git(*args,timeout=30):
    env={'PATH':'/usr/local/bin:/usr/bin:/bin','HOME':'/tmp','LANG':'C.UTF-8','GIT_CONFIG_NOSYSTEM':'1','GIT_CONFIG_GLOBAL':'/dev/null','GIT_TERMINAL_PROMPT':'0','GIT_NO_REPLACE_OBJECTS':'1','GIT_OPTIONAL_LOCKS':'0'}
    code,output=bounded(['git','-c','core.hooksPath=/dev/null','-c','core.fsmonitor=false','-c','protocol.allow=never','-c','protocol.file.allow=always','-c','gc.auto=0',*args],ROOT,env,timeout,256000,stderr=subprocess.DEVNULL)
    if code:raise ValueError('Git operation failed')
    return output

def main(config):
    mode=config['mode'];branch=config['branch'];base=config['baseSha']
    if not re.fullmatch(r'agent-desk/[a-f0-9]{32}',branch) or not re.fullmatch(r'[a-f0-9]{40}',base):raise ValueError('Invalid repository binding')
    if ROOT.is_symlink():raise ValueError('Repository path is a symlink')
    if mode=='install':
        if ROOT.exists():
            if not (ROOT/'.git').is_dir() or (ROOT/'.git').is_symlink():raise ValueError('Existing repository path is not a normal checkout')
            git('merge-base','--is-ancestor',base,'HEAD');emit({'type':'installed'});return
        ROOT.mkdir(mode=0o700)
        try:
            with tempfile.TemporaryDirectory(prefix='portal-git-') as scratch:
                bundle=pathlib.Path(scratch)/'seed.bundle';emit({'type':'ready'});total=0
                with bundle.open('wb') as out:
                    while True:
                        frame=json.loads(sys.stdin.readline(100000))
                        if frame['type']=='end':break
                        if frame['type']!='chunk':raise ValueError('Unexpected input')
                        data=base64.b64decode(frame['data'],validate=True);total+=len(data)
                        if total>LIMIT:raise ValueError('Bundle too large')
                        out.write(data)
                git('init');git('bundle','verify',str(bundle));git('fetch','--no-tags',str(bundle),'refs/heads/'+branch)
                if git('rev-parse','FETCH_HEAD').decode().strip()!=base:raise ValueError('Unexpected checkout commit')
                git('checkout','-b',branch,'FETCH_HEAD');git('config','user.name','Agent Desk');git('config','user.email','agent-desk@users.noreply.local')
                # No credentials or reachable remotes are installed in the sandbox.
                emit({'type':'installed'})
        except Exception:
            # Leave failed initialization inspectable; never overwrite an existing tree.
            raise
        return
    if mode!='capture' or not ROOT.is_dir() or not (ROOT/'.git').is_dir() or (ROOT/'.git').is_symlink():raise ValueError('Repository is unavailable')
    head=git('rev-parse','HEAD').decode().strip()
    if not re.fullmatch(r'[a-f0-9]{40}',head):raise ValueError('Invalid HEAD')
    if git('symbolic-ref','--short','HEAD').decode().strip()!=branch:
        emit({'type':'capture','ok':False,'reason':'Use the assigned run branch before publishing.'});return
    if git('status','--porcelain=v1','--untracked-files=all'):
        emit({'type':'capture','ok':False,'reason':'Commit or remove all uncommitted/untracked changes before publishing.'});return
    git('merge-base','--is-ancestor',base,head)
    command=config.get('checkCommand','')
    if not command or len(command)>1000:raise ValueError('Missing required check command')
    env={'PATH':'/usr/local/bin:/usr/bin:/bin','HOME':'/tmp','LANG':'C.UTF-8','TMPDIR':'/tmp'}
    code,output=bounded(['/bin/bash','-lc',command],ROOT,env,180,16000,True)
    checks=output.decode('utf-8','replace')
    if code or git('rev-parse','HEAD').decode().strip()!=head or git('status','--porcelain=v1','--untracked-files=all'):
        emit({'type':'capture','ok':False,'reason':'Required checks failed or changed HEAD/working files. Commit fixes and resume.','checks':checks});return
    if head==base:emit({'type':'capture','ok':False,'reason':'No commits to publish.','checks':checks});return
    with tempfile.TemporaryDirectory(prefix='portal-git-') as scratch:
        bundle=pathlib.Path(scratch)/'result.bundle';git('bundle','create',str(bundle),'HEAD','^'+base)
        if bundle.stat().st_size>LIMIT:raise ValueError('Publication exceeds the 8 MiB bundle limit')
        emit({'type':'capture','ok':True,'head':head,'checks':checks})
        with bundle.open('rb') as source:
            while data:=source.read(49152):emit({'type':'chunk','data':base64.b64encode(data).decode()})
        emit({'type':'end','bytes':bundle.stat().st_size})
if __name__=='__main__':
    try:main(json.loads(sys.stdin.readline(20000)))
    except Exception:
        emit({'type':'error','reason':'Repository preparation or verification failed. No publishing was authorized.'});sys.exit(1)
