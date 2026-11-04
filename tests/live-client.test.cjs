const {test}=require('node:test');
const assert=require('node:assert/strict');
const vm=require('node:vm');
const fs=require('node:fs');
function harness(responses,elements={},theme=null){
 const requests=[],listeners={};const root={innerHTML:'',addEventListener(name,fn){listeners[name]=fn;}};
 const context={document:{getElementById:id=>id==='live-app'?root:elements[id]??null,querySelectorAll:()=>[]},window:{addEventListener(){},portalTheme:theme},setInterval(){},location:{hash:''},confirm:()=>false,crypto:require('node:crypto').webcrypto,
 fetch:async(url,options)=>{requests.push({url,options});const result=await responses.shift();if(result instanceof Error)throw result;return {ok:result.status<400,status:result.status,json:async()=>result.data};}};
 vm.createContext(context);
 vm.runInContext(fs.readFileSync('dist/live.js','utf8').replace(/load\(\);\s*$/,''),context);
 vm.runInContext("state.session={authenticated:false,csrfToken:'valid-csrf'};globalThis.client={api,mutate,state,loadWorkspace,loadExports,exportedEntries,fileList,previewWorkspace,runAction,clearPrivateState,appendEvent,appendAgentText,renderRepositoryForm,publicationPanel,githubInstallationForm};",context);
 return {...context.client,requests,root,listeners,location:context.location};
}
test('wrong password preserves the anonymous CSRF token for a second attempt',async()=>{
 const c=harness([{status:401,data:{error:'Invalid username or password.'}},{status:200,data:{authenticated:true}}]);
 await assert.rejects(c.api('/login',{method:'POST',body:{username:'x',password:'wrong'}}));
 assert.equal(c.state.session.csrfToken,'valid-csrf');
 await c.api('/login',{method:'POST',body:{username:'x',password:'correct'}});
 assert.equal(c.requests[1].options.headers['X-CSRF-TOKEN'],'valid-csrf');
});
test('network retry reuses the idempotency key, successful distinct intent gets a new key',async()=>{
 const c=harness([new Error('network disconnected'),{status:202,data:{id:'one'}},{status:202,data:{id:'two'}}]);const body={title:'same'};
 await assert.rejects(c.mutate('/runs',body));await c.mutate('/runs',body);await c.mutate('/runs',body);
 assert.equal(c.requests[0].options.headers['Idempotency-Key'],c.requests[1].options.headers['Idempotency-Key']);
 assert.notEqual(c.requests[1].options.headers['Idempotency-Key'],c.requests[2].options.headers['Idempotency-Key']);
});
test('expired session clears private state and gets a fresh anonymous CSRF token',async()=>{
 const c=harness([{status:401,data:null},{status:200,data:{authenticated:false,csrfToken:'fresh'}}]);c.state.runs=[{id:'private'}];c.state.drafts={secret:'note'};
 await assert.rejects(c.api('/runs'));assert.equal(c.state.runs.length,0);assert.equal(Object.keys(c.state.drafts).length,0);assert.equal(c.state.session.csrfToken,'fresh');assert.match(c.root.innerHTML,/login-form/);
});

test('suspended Files never wakes or reads until explicit action',async()=>{
 const panel={innerHTML:''};const c=harness([],{"files-panel":panel});c.state.tab='files';c.state.run={id:'one',state:'review',platformPhase:'Suspended'};
 await c.loadWorkspace('');assert.equal(c.requests.length,0);assert.match(panel.innerHTML,/Open workspace/);assert.match(panel.innerHTML,/no AI turn/);
});
test('live Files uses direct endpoint and escapes filenames',async()=>{
 const panel={innerHTML:''};const c=harness([{status:200,data:{entries:[{name:'<img onerror=evil>',kind:'file',size:1}],skipped:0,truncated:false}}],{"files-panel":panel});
 c.state.tab='files';c.state.run={id:'one',state:'browsing',platformPhase:'Running'};await c.loadWorkspace('src');
 assert.equal(c.requests[0].url,'/api/runs/one/workspace?path=src');assert.match(panel.innerHTML,/&lt;img/);assert.doesNotMatch(panel.innerHTML,/<img/);assert.match(panel.innerHTML,/Save export/);
});


test('folder list sorts folders first and escapes file attributes',()=>{
 const c=harness([]);const html=c.fileList([{name:'a.txt',kind:'file',size:20},{name:'z-folder',kind:'directory'},{name:'x\" onclick=evil',kind:'file',size:1}], 'src');
 assert.ok(html.indexOf('z-folder')<html.indexOf('a.txt'));assert.match(html,/data-path="src\/x&quot;/);assert.doesNotMatch(html,/data-path="src\/x" onclick/);
});
test('Open workspace stays actionable when the Files tab renders during a click',async()=>{
 const panel={innerHTML:''};const c=harness([],{"files-panel":panel});c.state.tab='files';c.state.busy=true;c.state.run={id:'one',state:'review',platformPhase:'Suspended'};
 await c.loadWorkspace();assert.match(panel.innerHTML,/data-action="browse"/);assert.doesNotMatch(panel.innerHTML,/data-action="browse"[^>]*disabled/);assert.equal(c.requests.length,0);
});
test('a stale folder response cannot replace newer folder contents',async()=>{
 let finish;const slow=new Promise(resolve=>finish=resolve),panel={innerHTML:''};
 const c=harness([slow,{status:200,data:{entries:[{name:'new.txt',kind:'file',size:1}],skipped:0}}],{"files-panel":panel});c.state.tab='files';c.state.run={id:'one',state:'browsing',platformPhase:'Running'};
 const earlier=c.loadWorkspace('old');await c.loadWorkspace('new');finish({status:200,data:{entries:[{name:'old.txt',kind:'file',size:1}],skipped:0}});await earlier;
 assert.match(panel.innerHTML,/new.txt/);assert.doesNotMatch(panel.innerHTML,/old.txt/);
});
test('saved copies use virtual folders and never open a live sandbox',async()=>{
 const panel={innerHTML:''};const c=harness([{status:200,data:{snapshot:{turn:1,capturedAt:'2026-11-03T18:00:00Z',files:[{path:'src/main.py',size:12},{path:'src/lib/helper.py',size:10},{path:'README.md',size:4}]}}}],{"files-panel":panel});
 c.state.tab='exports';c.state.run={id:'one',state:'completed',terminal:true};await c.loadExports('src');
 assert.equal(c.requests.length,1);assert.equal(c.requests[0].url,'/api/runs/one/files');assert.equal(c.requests[0].options.method,'GET');assert.match(panel.innerHTML,/main.py/);assert.match(panel.innerHTML,/lib/);assert.doesNotMatch(panel.innerHTML,/README.md/);
});
test('terminal Files explains deletion without exposing a wake button',async()=>{
 const panel={innerHTML:''};const c=harness([],{"files-panel":panel});c.state.tab='files';c.state.run={id:'one',state:'completed',platformPhase:'Deleted',terminal:true};await c.loadWorkspace();
 assert.equal(c.requests.length,0);assert.doesNotMatch(panel.innerHTML,/data-action="browse"/);assert.match(panel.innerHTML,/saved copies/);
});
test('start submits only the actual form values and navigates without a second required request',async()=>{
 const c=harness([{status:202,data:{id:'created',title:'CSV tool'}}]),submit={disabled:false};c.state.form={title:'CSV tool',instructions:'Create a CSV parser.',template:'codex-task'};
 await c.listeners.submit({preventDefault(){},target:{id:'run-form',querySelector:()=>submit}});
 assert.equal(c.requests.length,1);assert.deepEqual(JSON.parse(c.requests[0].options.body),{title:'CSV tool',instructions:'Create a CSV parser.',template:'codex-task'});assert.equal(c.location.hash,'/runs/created');assert.equal(c.state.form.instructions,'');
});
test('failed start preserves the user draft',async()=>{
 const c=harness([new Error('network')]);c.state.form={title:'Keep me',instructions:'Draft text',template:'codex-task'};
 await c.listeners.submit({preventDefault(){},target:{id:'run-form',querySelector:()=>({})}});assert.equal(c.state.form.instructions,'Draft text');assert.equal(c.state.error,'network');
});
test('destructive actions require confirmation',async()=>{
 const c=harness([]);c.state.run={id:'one',state:'review'};await c.runAction('finish');await c.runAction('cancel');assert.equal(c.requests.length,0);
});
test('private drafts and pending file responses are invalidated on sign-out',()=>{
 const c=harness([]);c.state.form.instructions='private';c.state.drafts={one:'private'};const request=c.state.fileRequest;c.clearPrivateState();assert.equal(c.state.form.instructions,'');assert.equal(Object.keys(c.state.drafts).length,0);assert.ok(c.state.fileRequest>request);
});


test('consecutive command output chunks stay in one expandable block',()=>{
 const pre={textContent:'first '},last={dataset:{kind:'stdout'},querySelector:()=>pre};
 const events={lastElementChild:last,querySelector:()=>null,scrollHeight:0,scrollTop:0,clientHeight:0};
 const c=harness([],{events});c.appendEvent({kind:'stdout',text:'second <script>literal</script>'});
 assert.equal(pre.textContent,'first second <script>literal</script>');
});
test('Codex output and result remain grouped with their command',()=>{
 const pre={textContent:'python hello.py'},last={dataset:{kind:'command'},querySelector:()=>pre};
 const events={lastElementChild:last,querySelector:()=>null,scrollHeight:0,scrollTop:0,clientHeight:0};
 const c=harness([],{events});c.appendEvent({kind:'command-output',text:'Hello'});c.appendEvent({kind:'command-output',text:' world'});c.appendEvent({kind:'command-result',text:'Exit 0'});
 assert.equal(pre.textContent,'python hello.py\n\nHello world\n\nExit 0');
});
test('theme toggle works while busy without changing drafts, requests or the view',async()=>{
 let toggles=0;const c=harness([],{}, {toggle(){toggles++;}});c.state.busy=true;c.state.drafts={one:'Keep this feedback'};c.root.innerHTML='current conversation';
 const target={dataset:{action:'theme'}};
 await c.listeners.click({target:{closest:selector=>selector==='[data-action]'?target:null}});
 assert.equal(toggles,1);assert.equal(c.requests.length,0);assert.equal(c.state.busy,true);assert.equal(c.state.drafts.one,'Keep this feedback');assert.equal(c.root.innerHTML,'current conversation');
});
test('agent display removes the reported three trailing newlines, not its raw stream',()=>{
 const text={textContent:''},node={querySelector:()=>text},c=harness([]),raw='I’ll verify its contents with a Python assertion.\n\n\n';
 c.appendAgentText(node,raw);assert.equal(text.textContent,raw.trimEnd());assert.equal(node.agentText,raw);assert.equal(node.hidden,false);
});
test('trailing whitespace in deltas is preserved when later text arrives',()=>{
 const text={textContent:''},node={querySelector:()=>text},c=harness([]);
 c.appendAgentText(node,'Hello ');assert.equal(text.textContent,'Hello');
 c.appendAgentText(node,'world.\n\n');assert.equal(text.textContent,'Hello world.');
 c.appendAgentText(node,'Next paragraph.\n\n\n');assert.equal(text.textContent,'Hello world.\n\nNext paragraph.');
 assert.equal(node.agentText,'Hello world.\n\nNext paragraph.\n\n\n');
});
test('whitespace-only agent deltas stay hidden until visible content arrives',()=>{
 const text={textContent:''},node={querySelector:()=>text},c=harness([]);
 c.appendAgentText(node,'\n\n');assert.equal(node.hidden,true);
 c.appendAgentText(node,'Visible now');assert.equal(node.hidden,false);assert.equal(text.textContent,'\n\nVisible now');
});
test('agent display preserves internal code indentation and literal HTML',()=>{
 const text={textContent:''},node={querySelector:()=>text},c=harness([]),body='Example:\n\n```python\nif True:\n    print("<script>literal</script>")\n```';
 c.appendAgentText(node,body+'\n\n');assert.equal(text.textContent,body);
});
test('agent chunks pass through presentation trimming when appended to a message',()=>{
 const text={textContent:'Before '},last={dataset:{kind:'agent'},querySelector:()=>text};
 const events={lastElementChild:last,querySelector:()=>null,scrollHeight:0,scrollTop:0,clientHeight:0},c=harness([],{events});
 c.appendEvent({kind:'agent',text:'the tool.\n\n\n'});assert.equal(text.textContent,'Before the tool.');assert.equal(last.agentText,'Before the tool.\n\n\n');
});


test('repository form defaults to manual review and escapes repository names',()=>{
 const panel={innerHTML:''};const c=harness([],{"repository-form":panel});c.state.form.template='codex-task';c.state.catalog={githubEnabled:true};c.state.repositories=[{id:1,fullName:'x/<bad>',defaultBranch:'main'}];c.state.repoForm.id='1';c.renderRepositoryForm();assert.match(panel.innerHTML,/value="manual" selected/);assert.match(panel.innerHTML,/x\/&lt;bad&gt;/);assert.doesNotMatch(panel.innerHTML,/<bad>/);assert.match(panel.innerHTML,/Required check/);
});
test('publish review shows escaped patch and only enables the current checked commit',()=>{
 const panel={innerHTML:'',dataset:{},hidden:true};const c=harness([],{"publication-panel":panel});const run={state:'review',turn:2,repository:{fullName:'owner/repo',baseBranch:'main',publishMode:'manual',checkCommand:'python -m unittest'},publishBranch:'agent-desk/run',publication:{state:'ready',head:'abc123',checkedTurn:2,diff:'<script>bad</script>',checks:'passed'}};c.publicationPanel(run);assert.match(panel.innerHTML,/Push & create draft PR/);assert.match(panel.innerHTML,/&lt;script&gt;/);assert.doesNotMatch(panel.innerHTML,/<script>/);c.publicationPanel({...run,turn:3});assert.doesNotMatch(panel.innerHTML,/data-action="publish"/);c.publicationPanel({...run,state:'publishing'});assert.doesNotMatch(panel.innerHTML,/data-action="publish"/);assert.match(panel.innerHTML,/Publishing/);
});
test('logout clears repository form and selected repositories',()=>{
 const c=harness([]);c.state.repositories=[{id:1}];c.state.repoForm.id='1';c.state.repoForm.publishMode='auto';c.clearPrivateState();assert.equal(c.state.repositories,null);assert.equal(c.state.repoForm.id,'');assert.equal(c.state.repoForm.publishMode,'manual');
});

test('all-repository installation requires an unchecked explicit consent field',()=>{
 const c=harness([]);c.state.githubInstallations=[{id:42,account:'owner',repositorySelection:'all'}];let html=c.githubInstallationForm();assert.match(html,/all current and future repositories/);assert.match(html,/name="allowAllRepositories" required/);assert.doesNotMatch(html,/checkbox[^>]*checked/);c.state.githubInstallations[0].repositorySelection='selected';html=c.githubInstallationForm();assert.doesNotMatch(html,/name="allowAllRepositories"/);assert.match(html,/Use this installation/);
});
