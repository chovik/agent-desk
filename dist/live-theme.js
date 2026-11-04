// Applied before the stylesheet/body to avoid a light flash on a saved dark theme.
// Only a non-sensitive appearance preference is stored in this browser.
(() => {
 'use strict';
 const key='agent-desk-theme',valid=value=>value==='light'||value==='dark';
 const system=window.matchMedia('(prefers-color-scheme: dark)');
 let preference=null;
 try{const saved=localStorage.getItem(key);if(valid(saved))preference=saved;}catch{}
 function current(){return preference??(system.matches?'dark':'light');}
 function apply(){document.documentElement.dataset.theme=current();window.dispatchEvent(new Event('portal-theme-change'));}
 window.portalTheme={current,toggle(){preference=current()==='dark'?'light':'dark';try{localStorage.setItem(key,preference);}catch{}apply();}};
 system.addEventListener('change',()=>{if(!preference)apply();});
 window.addEventListener('storage',event=>{if(event.key===key||event.key===null){preference=valid(event.newValue)?event.newValue:null;apply();}});
 apply();
})();
