#!/usr/bin/env python3
"""Codex in AX; model HTTP is carried over the private guest stdio bridge.
No personal provider token or refresh credential ever enters this process.
"""
import base64, http.server, json, os, queue, secrets, signal, subprocess, sys, threading, uuid

OUT = threading.Lock()
PENDING = {}
LOCK = threading.Lock()
KEY = secrets.token_hex(32)  # local capability, NOT a provider credential
PROC = None

def emit(value):
    with OUT: print(json.dumps(value, ensure_ascii=True), flush=True)

class ModelProxy(http.server.BaseHTTPRequestHandler):
    protocol_version = 'HTTP/1.1'
    def log_message(self, *_): pass
    def do_POST(self):
        if self.path not in ('/v1/responses', '/v1/responses/compact') or self.headers.get('Authorization') != 'Bearer '+KEY:
            self.send_error(403); return
        try: length = int(self.headers.get('Content-Length','0'))
        except ValueError: self.send_error(400); return
        if not 0 < length <= 2*1024*1024: self.send_error(413); return
        body = self.rfile.read(length)
        if len(body)!=length: return
        ident = uuid.uuid4().hex; replies=queue.Queue(maxsize=128)
        with LOCK:
            if PENDING: self.send_error(429); return
            PENDING[ident]=replies
        try:
            emit({'type':'model-request','id':ident,'path':self.path,'body':base64.b64encode(body).decode()})
            first=replies.get(timeout=260)
            if first.get('type')!='model-header': raise ValueError('Missing header')
            self.send_response(first['status']);self.send_header('Content-Type',first['contentType']);self.send_header('Connection','close');self.end_headers();self.close_connection=True
            while True:
                reply=replies.get(timeout=260)
                if reply['type']=='model-end': break
                if reply['type']!='model-chunk': raise ValueError('Unexpected frame')
                self.wfile.write(base64.b64decode(reply['data'],validate=True));self.wfile.flush()
        except (OSError,ValueError,queue.Empty): self.close_connection=True
        finally:
            with LOCK: PENDING.pop(ident,None)

def incoming():
    try:
        for line in sys.stdin:
            if len(line)>100000: break
            reply=json.loads(line)
            with LOCK: target=PENDING.get(reply.get('id'))
            if target: target.put(reply,timeout=5)
    except Exception: pass
    stop()

def stop(*_):
    if PROC and PROC.poll() is None:
        try: os.killpg(PROC.pid,signal.SIGKILL)
        except ProcessLookupError: pass

def main():
    global PROC
    config=json.loads(sys.stdin.readline(20000))
    cwd=config.get('cwd','/workspace')
    if cwd not in ('/workspace','/workspace/repo'):raise ValueError('Invalid cwd')
    home='/workspace/.agent-desk/codex'
    os.makedirs(home,mode=0o700,exist_ok=True)
    server=http.server.ThreadingHTTPServer(('127.0.0.1',0),ModelProxy)
    threading.Thread(target=server.serve_forever,daemon=True).start()
    env={'PATH':'/usr/local/bin:/usr/bin:/bin','HOME':home,'CODEX_HOME':home,'PORTAL_MODEL_KEY':KEY,'TMPDIR':'/tmp'}
    flags={'model_provider':'portal','model_providers.portal.name':'Agent Desk account broker',
           'model_providers.portal.base_url':'http://127.0.0.1:'+str(server.server_port)+'/v1',
           'model_providers.portal.env_key':'PORTAL_MODEL_KEY','model_providers.portal.wire_api':'responses',
           'model_providers.portal.requires_openai_auth':False,'model_providers.portal.supports_websockets':False,
           'model_providers.portal.request_max_retries':0,'model_providers.portal.stream_max_retries':0,
           'features.multi_agent':False,'features.apps':False,'features.plugins':False,'features.remote_plugin':False,
           'features.hooks':False,'features.shell_snapshot':False,'web_search':'disabled',
           'analytics.enabled':False,'feedback.enabled':False,'cli_auth_credentials_store':'ephemeral'}
    args=['/usr/local/bin/codex','app-server','--listen','stdio://']
    for k,v in flags.items(): args.extend(['-c',k+'='+json.dumps(v)])
    PROC=subprocess.Popen(args,cwd=cwd,env=env,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.DEVNULL,text=True,bufsize=1,start_new_session=True)
    signal.signal(signal.SIGTERM,stop);signal.signal(signal.SIGINT,stop)
    threading.Thread(target=incoming,daemon=True).start()
    def send(i,method,params):
        data={'method':method,'params':params}
        if i is not None:data['id']=i
        PROC.stdin.write(json.dumps(data)+'\n');PROC.stdin.flush()
    send(1,'initialize',{'clientInfo':{'name':'agent_desk_private_lab','title':'Agent Desk','version':'0.2.0'},'capabilities':{'experimentalApi':False}})
    thread=None;delta_items=set();exit_code=1
    try:
        for line in PROC.stdout:
            if len(line)>4*1024*1024: raise ValueError('Oversized protocol message')
            message=json.loads(line)
            if 'error' in message:
                emit({'type':'error','text':'Codex protocol request failed (step '+str(message.get('id'))+').'});break
            if message.get('id')==1:
                send(None,'initialized',{})
                params={'cwd':cwd,'model':config['model'],'approvalPolicy':'never','sandbox':'danger-full-access',
                        'developerInstructions':'Work inside /workspace. The gVisor sandbox is the external isolation boundary. Network access from commands is disabled. Keep user deliverables in non-hidden paths; do not modify .agent-desk session state. Report actual results, not assumed success. '+config.get('repositoryInstructions','')}
                if config.get('threadId'):params.update({'threadId':config['threadId'],'excludeTurns':True})
                send(2,'thread/resume' if config.get('threadId') else 'thread/start',params)
            elif message.get('id')==2:
                thread=message['result']['thread']['id'];emit({'type':'thread','id':thread})
                send(3,'turn/start',{'threadId':thread,'effort':'low','input':[{'type':'text','text':config['message']}]})
            method=message.get('method');p=message.get('params',{})
            if 'id' in message and method: # No interactive approvals/custom host tools in this first slice.
                PROC.stdin.write(json.dumps({'id':message['id'],'error':{'code':-32601,'message':'Interactive host requests are not enabled.'}})+'\n');PROC.stdin.flush()
            if method=='item/agentMessage/delta':
                delta_items.add(p.get('itemId'));emit({'type':'agent','text':p.get('delta','')})
            elif method=='item/commandExecution/outputDelta':emit({'type':'command-output','text':p.get('delta','')[:16000]})
            elif method=='item/started' and p.get('item',{}).get('type')=='commandExecution':
                emit({'type':'command','text':str(p['item'].get('command',''))[:16000]})
            elif method=='item/completed':
                item=p.get('item',{});kind=item.get('type')
                if kind=='agentMessage':
                    if item.get('id') not in delta_items:emit({'type':'agent','text':item.get('text','')[:16000]})
                    emit({'type':'agent','text':'\n\n'})  # Keep adjacent messages readable in the streamed transcript.
                elif kind=='commandExecution':emit({'type':'command-result','text':'Command '+str(item.get('status'))+'; exit '+str(item.get('exitCode'))})
                elif kind=='fileChange':emit({'type':'changes','text':'File changes: '+', '.join(str(c.get('path','')) for c in item.get('changes',[]))[:15000]})
            elif method=='turn/completed':
                status=p.get('turn',{}).get('status');emit({'type':'result','status':status});exit_code=0 if status=='completed' else 1;break
        return exit_code
    finally: stop();server.shutdown()

if __name__=='__main__':
    try: sys.exit(main())
    except Exception:
        stop();emit({'type':'error','text':'Codex sandbox driver failed; no successful completion was inferred.'});sys.exit(2)
