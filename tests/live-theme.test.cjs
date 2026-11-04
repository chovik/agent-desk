const {test}=require('node:test');
const assert=require('node:assert/strict');
const vm=require('node:vm');
const fs=require('node:fs');

function harness({saved=null,dark=false,blocked=false}={}){
 const listeners={},changes=[],storage=new Map(saved==null?[]:[['agent-desk-theme',saved]]);
 const media={matches:dark,addEventListener(name,fn){listeners['media:'+name]=fn;}};
 const document={documentElement:{dataset:{}}};
 const window={matchMedia:()=>media,addEventListener(name,fn){listeners[name]=fn;},dispatchEvent(e){changes.push(e.type);}};
 const localStorage={getItem(key){if(blocked)throw Error('disabled');return storage.get(key)??null;},setItem(key,value){if(blocked)throw Error('disabled');storage.set(key,value);}};
 vm.runInNewContext(fs.readFileSync('dist/live-theme.js','utf8'),{window,document,localStorage,Event:class{constructor(type){this.type=type;}}});
 return {theme:window.portalTheme,root:document.documentElement,listeners,media,changes,storage};
}
test('first visit follows system appearance before the application loads',()=>{
 assert.equal(harness().root.dataset.theme,'light');
 assert.equal(harness({dark:true}).root.dataset.theme,'dark');
});
test('a saved choice overrides system appearance and survives reload',()=>{
 const c=harness({saved:'light',dark:true});assert.equal(c.theme.current(),'light');
 c.theme.toggle();assert.equal(c.root.dataset.theme,'dark');assert.equal(c.storage.get('agent-desk-theme'),'dark');
 assert.equal(harness({saved:c.storage.get('agent-desk-theme')}).root.dataset.theme,'dark');
 c.theme.toggle();assert.equal(c.root.dataset.theme,'light');
});
test('invalid stored values fall back to the system without being injected',()=>{
 assert.equal(harness({saved:'<script>',dark:true}).root.dataset.theme,'dark');
});
test('blocked storage does not prevent loading or toggling the theme',()=>{
 const c=harness({blocked:true});c.theme.toggle();assert.equal(c.root.dataset.theme,'dark');c.theme.toggle();assert.equal(c.root.dataset.theme,'light');
});
test('system changes are followed only until an explicit choice',()=>{
 const c=harness();c.media.matches=true;c.listeners['media:change']();assert.equal(c.root.dataset.theme,'dark');
 c.theme.toggle();c.listeners['media:change']();assert.equal(c.root.dataset.theme,'light');
});
test('theme updates and storage clearing synchronize across tabs',()=>{
 const c=harness();c.listeners.storage({key:'unrelated',newValue:'dark'});assert.equal(c.root.dataset.theme,'light');
 c.listeners.storage({key:'agent-desk-theme',newValue:'dark'});assert.equal(c.root.dataset.theme,'dark');
 c.listeners.storage({key:'agent-desk-theme',newValue:null});assert.equal(c.root.dataset.theme,'light');
 c.theme.toggle();c.listeners.storage({key:null,newValue:null});assert.equal(c.root.dataset.theme,'light');
 assert.ok(c.changes.every(x=>x==='portal-theme-change'));
});
test('theme boot script is external and loads before styles and deferred application',()=>{
 const html=fs.readFileSync('dist/live.html','utf8');
 assert.ok(html.indexOf('src="/live-theme.js"')<html.indexOf('href="/live.css"'));
 assert.match(html,/<script src="\/live-theme.js"><\/script>/);
 assert.match(fs.readFileSync('scripts/prepare-images.sh','utf8'),/cp -a dist guest/);
 for(const file of ['live.html','live.css','live.js','live-theme.js'])assert.ok(fs.existsSync('dist/'+file));
});
test('light and dark semantic text colors meet normal-text contrast',()=>{
 const css=fs.readFileSync('dist/live.css','utf8');
 const palette=selector=>Object.fromEntries([...css.match(selector)[1].matchAll(/--([\w-]+):(#\w+)/g)].map(m=>[m[1],m[2]]));
 const light=palette(/:root\{([^}]+)\}/),dark={...light,...palette(/:root\[data-theme=dark\]\{([^}]+)\}/)};
 const lum=hex=>{let h=hex.slice(1);if(h.length===3)h=[...h].map(c=>c+c).join('');const rgb=[0,2,4].map(i=>parseInt(h.slice(i,i+2),16)/255).map(v=>v<=.04045?v/12.92:((v+.055)/1.055)**2.4);return rgb[0]*.2126+rgb[1]*.7152+rgb[2]*.0722;};
 const pairs=[['ink','bg'],['ink','surface'],['body-text','surface'],['muted','surface'],['muted','surface-alt'],['accent','accent-soft'],['on-accent','accent'],['on-accent','accent-hover'],...['danger','warning','info','success'].map(x=>[x,x+'-soft'])];
 for(const [name,colors] of Object.entries({light,dark}))for(const [fg,bg] of pairs){const a=lum(colors[fg]),b=lum(colors[bg]),ratio=(Math.max(a,b)+.05)/(Math.min(a,b)+.05);assert.ok(ratio>=4.5,`${name} ${fg}/${bg}: ${ratio.toFixed(2)}`);}
});
test('theme variables are defined and never directly self-referential',()=>{
 const css=fs.readFileSync('dist/live.css','utf8'),defined=new Set([...css.matchAll(/(--[\w-]+):/g)].map(m=>m[1]));
 for(const m of css.matchAll(/var\((--[\w-]+)\)/g))assert.ok(defined.has(m[1]),m[1]);
 for(const m of css.matchAll(/(--[\w-]+):var\((--[\w-]+)\)/g))assert.notEqual(m[1],m[2]);
});
