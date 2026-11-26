// Run after `git merge <agent-branch>` stops on conflicts: resolves PLAN mapping-table rows (prefers the row marked done) and the Ghidra symbols CSV (union, drops later rows that rename an existing address).
// Also the known agent-branch shapes, in any conflicted file: counters array size `int[N]` (max N), string-name lists `{ "a", ... }` (union, order kept),
// `n != "x" && ...` default-exclusion chains (union), tuning XML `<kName value=".." />` lines (union), PatchHandlers.cs counter-name lines (keep both),
// docs/registry.md rows (keep both, next-free rows recomputed by tools/registry.ts fix-next, collisions printed), docs/51 ledger sections with a § number
// ours already has (incoming ones renumbered after ours, skipping § numbers reserved with `registry.ts reserve ledger`; `ledger §N` / `docs/51 §N` fixed in the incoming branch's own lines). Anything else stays a conflict.
// Fully resolved files are `git add`ed.   node tools/agent-merge.cjs [--self-check]
// After the merge commit: node tools/agent-merge.cjs finish <branch>   (mandatory, docs/283) the branch must be in HEAD with no merge in progress, then
// tools/harness/cleanup.ts worktrees --apply --branch <branch> --target HEAD removes its worktree; any blocker = exit 1 with the reason.
const fs=require('fs'),path=require('path'),os=require('os'),{execSync,execFileSync}=require('child_process');
function res(f,fn){if(!fs.existsSync(f))return 0;const t=fs.readFileSync(f,'utf8'),eol=t.includes('\r\n')?'\r\n':'\n';const L=t.split(/\r?\n/);let n=0,from=0;for(;;){const s=L.findIndex((l,i)=>i>=from&&l.startsWith('<<<<<<<'));if(s<0)break;const m=L.findIndex((l,i)=>i>s&&l==='=======');const e=L.findIndex((l,i)=>i>m&&l.startsWith('>>>>>>>'));if(m<0||e<0)break;let a=L.slice(s+1,m);const p=a.findIndex(l=>l.startsWith('|||||||'));if(p>=0)a=a.slice(0,p);const out=fn(a,L.slice(m+1,e),L,m,e);if(out){L.splice(s,e-s+1,...out);n++;from=s+out.length}else from=e+1}if(n){fs.writeFileSync(f,L.join(eol));console.log(f,'resolved',n)}return n}
const id=l=>(l.match(/^\s*\| (M-[A-Z0-9]+|N\d+) \|/)||[])[1]??l;

// ---- line shapes: tokens merged inside one line, and lines kept from both sides
const TOK=/int\[(\d+)\]|\{ ("[^"]*"(?:, "[^"]*")*) \}|\b(\w+) != "[^"]*"(?: && \3 != "[^"]*")*/g;
function toks(l){const t=[];const key=l.replace(TOK,(m,i,list,v)=>{t.push(i!=null?{k:'i',n:+i}:list!=null?{k:'l',s:[...list.matchAll(/"([^"]*)"/g)].map(x=>x[1])}:{k:'c',v,s:[...m.matchAll(/"([^"]*)"/g)].map(x=>x[1])});return '\0'+(i!=null?'I':list!=null?'L':'C'+v)});return {key,t}}
const union=(a,b)=>[...a,...b.filter(x=>!a.includes(x))];
function mergeTok(la,lb){const A=toks(la).t,B=toks(lb).t;let k=0;return la.replace(TOK,()=>{const x=A[k],y=B[k++];if(x.k==='i')return `int[${Math.max(x.n,y.n)}]`;const s=union(x.s,y.s);return x.k==='l'?`{ ${s.map(q=>`"${q}"`).join(', ')} }`:s.map(q=>`${x.v} != "${q}"`).join(' && ')})}
const XML=/^\s*<(k\w+) value="[^"]*" \/>\s*$/;
const ROW=/^\|/;
function keepBothFor(f){const ph=/PatchHandlers\.cs$/.test(f),reg=/registry\.md$/.test(f);return l=>!l.trim()||XML.test(l)||(reg&&ROW.test(l))||(ph&&(/^\s*(if \(c\.Length > \d+\)\s*)?\{?\s*r\["\w+"\]\s*=/.test(l)||/^\s*if \(c\.Length > \d+\)\s*$/.test(l)||/^\s*[{}]\s*(\/\/.*)?$/.test(l)))}
const FIXHEAD='## ts3fixes counters';
function keyOf(l,reg){if(reg&&l.startsWith(FIXHEAD))return 'FIXHEAD';const x=toks(l);return x.t.length?x.key:l}
function pairMerge(la,lb,reg){if(la===lb)return la;if(reg&&la.startsWith(FIXHEAD)){const n=l=>+(l.match(/int\[(\d+)\]/)||[0,0])[1];return n(lb)>n(la)?lb:la}return mergeTok(la,lb)}
const rowNum=l=>{const m=l.match(/^\| (?:v)?(\d+)/);return m?+m[1]:Infinity};
// LCS on line keys; matched pairs merge, unmatched runs keep ours then theirs, only for keep-both lines.
function mergeLines(f,a,b){
  const reg=/registry\.md$/.test(f),keep=keepBothFor(f);
  if(reg){const nx=l=>/^\| \*\*/.test(l);a=a.filter(l=>!nx(l));b=b.filter(l=>!nx(l))}
  const ka=a.map(l=>keyOf(l,reg)),kb=b.map(l=>keyOf(l,reg));
  const D=[...Array(a.length+1)].map(()=>new Array(b.length+1).fill(0));
  for(let i=a.length-1;i>=0;i--)for(let j=b.length-1;j>=0;j--)D[i][j]=ka[i]===kb[j]?D[i+1][j+1]+1:Math.max(D[i+1][j],D[i][j+1]);
  const out=[];let i=0,j=0,ua=[],ub=[];
  const flush=()=>{if(![...ua,...ub].every(keep))return false;const xa=ua.map(l=>(l.match(XML)||[])[1]).filter(Boolean),xb=ub.filter(l=>XML.test(l));
    if(xb.some(l=>xa.includes(l.match(XML)[1])&&!ua.includes(l)))return false; // same tunable, different values
    let run=[...ua,...ub.filter(l=>!l.trim()||!ua.includes(l))];if(reg&&run.every(l=>!l.trim()||ROW.test(l)))run=run.filter(l=>l.trim()).sort((x,y)=>rowNum(x)-rowNum(y));out.push(...run);ua=[];ub=[];return true};
  while(i<a.length||j<b.length){
    if(i<a.length&&j<b.length&&ka[i]===kb[j]){if(!flush())return null;out.push(pairMerge(a[i],b[j],reg));i++;j++}
    else if(j>=b.length||(i<a.length&&D[i+1][j]>=D[i][j+1]))ua.push(a[i++]);else ub.push(b[j++]);
  }
  return flush()?out:null;
}

// ---- docs/51 ledger: both sides appended `## N.` sections
const LEDGER='docs/51-inference-ledger.md',ren=new Map();
const head=l=>(l.match(/^## (\d+)\./)||[])[1];
// § numbers other agents reserved (tools/registry.ts reserve ledger; machine file in the main checkout): renumbering skips them
function reservedLedger(){try{const c=path.resolve(execFileSync('git',['rev-parse','--git-common-dir'],{encoding:'utf8'}).trim()),main=path.basename(c)==='.git'?path.dirname(c):process.cwd();
  return new Set(JSON.parse(fs.readFileSync(path.join(main,'docs','data','registry-reservations.json'),'utf8')).reservations.filter(r=>r.kind==='ledger').map(r=>r.from))}catch{return new Set()}}
const fixRefs=l=>l.replace(/\b(ledger|docs\/51) §(\d+)/g,(m,w,n)=>ren.has(+n)?`${w} §${ren.get(+n)}`:m);
function ledger(a,b,L,m,e){
  const first=x=>x.find(l=>l.trim());if(!head(first(a)||'')||!head(first(b)||''))return null;
  const used=L.filter((l,i)=>(i<=m||i>=e)&&head(l)).map(l=>+head(l));
  if(b.some(l=>head(l)&&used.includes(+head(l)))){const rs=reservedLedger();let nx=Math.max(...used);for(const l of b)if(head(l)){do nx++;while(rs.has(nx));ren.set(+head(l),nx)}}
  const b2=b.map(l=>head(l)&&ren.has(+head(l))?l.replace(/^## \d+\./,`## ${ren.get(+head(l))}.`):fixRefs(l));
  return [...a,...(a[a.length-1]?.trim()?['']:[]),...b2];
}

function main(){
  const git=(...a)=>execFileSync('git',a,{encoding:'utf8'}).trim();
  const conflicted=()=>git('diff','--name-only','--diff-filter=U').split('\n').filter(Boolean);
  const files=conflicted(),touched=new Set();
  res('PLAN.md',(a,b)=>a.map(la=>{const lb=b.find(x=>id(x)===id(la));return lb&&/\*\*done\*\*/.test(lb)&&!/\*\*done\*\*/.test(la)?lb:la}).concat(b.filter(x=>!a.some(y=>id(y)===id(x)))));
  const csv='tools/ghidra/symbols/ts3w-1.67.symbols.csv';
  if(fs.existsSync(csv)){res(csv,(a,b)=>{const s=new Set(a);return [...a,...b.filter(l=>!s.has(l))]});
  const L=fs.readFileSync(csv,'utf8').split(/\n/);const by={};L.forEach((l,i)=>{if(i){const k=l.split(',')[0].toLowerCase();if(k)(by[k]??=[]).push(i)}});const drop=new Set();for(const ix of Object.values(by))if(new Set(ix.map(i=>L[i].split(',')[1])).size>1)ix.slice(1).forEach(i=>drop.add(i));fs.writeFileSync(csv,L.filter((_,i)=>!drop.has(i)).join('\n'));if(drop.size)console.log('csv clashes dropped',drop.size)}
  for(const f of files){if(f==='PLAN.md'||f===csv)continue;if(res(f,f===LEDGER?ledger:(a,b)=>mergeLines(f,a,b)))touched.add(f)}
  if(ren.size){ // incoming branch's own lines only: lines in MERGE_HEAD's copy that HEAD's copy lacks
    console.log('ledger renumbered (incoming):',[...ren].map(([x,y])=>`§${x} -> §${y}`).join(', '));
    const show=(r,f)=>{try{return new Set(execFileSync('git',['show',`${r}:${f}`],{encoding:'utf8',stdio:['ignore','pipe','ignore']}).split(/\r?\n/))}catch{return new Set()}};
    const base=git('merge-base','HEAD','MERGE_HEAD');
    for(const f of git('diff','--name-only',base,'MERGE_HEAD').split('\n').filter(Boolean)){
      if(!fs.existsSync(f))continue;const t=fs.readFileSync(f,'utf8'),eol=t.includes('\r\n')?'\r\n':'\n',th=show('MERGE_HEAD',f),ou=show('HEAD',f);
      const L=t.split(/\r?\n/).map(l=>th.has(l)&&!ou.has(l)?fixRefs(l):l);const u=L.join(eol);if(u!==t){fs.writeFileSync(f,u);touched.add(f);console.log(f,'ledger refs fixed')}}
  }
  for(const f of touched){
    if(/registry\.md$/.test(f)&&!/^<<<<<<< /m.test(fs.readFileSync(f,'utf8')))process.stdout.write(execFileSync(process.execPath,[path.join(__dirname,'registry.ts'),'fix-next',f],{encoding:'utf8'}));
    if(!/^(<<<<<<<|>>>>>>>) /m.test(fs.readFileSync(f,'utf8')))git('add','--',f);
  }
  const left=conflicted();console.log(left.length?'STILL CONFLICTED: '+left.join(' '):'clean');
  const mb=(()=>{try{return (fs.readFileSync(path.join(git('rev-parse','--absolute-git-dir'),'MERGE_MSG'),'utf8').match(/^Merge (?:remote-tracking )?branch '([^']+)'/)||[])[1]}catch{return ''}})();
  if(mb)console.log(`after the merge commit: node tools/agent-merge.cjs finish ${mb}   (mandatory: removes its worktree, docs/283)`);
}

function selfCheck(){
  let bad=0;const ok=(c,m)=>{console.log(c?'ok  ':'FAIL',m);if(!c)bad++};
  for(const style of ['merge','diff3']){
    const d=fs.mkdtempSync(path.join(os.tmpdir(),'agent-merge-check-'));
    const g=(...a)=>execFileSync('git',['-c','user.name=t','-c','user.email=t@example.invalid','-c','merge.conflictStyle='+style,'-c','core.autocrlf=false',...a],{cwd:d,encoding:'utf8',stdio:['ignore','pipe','pipe']});
    const w=(f,t)=>{fs.mkdirSync(path.dirname(path.join(d,f)),{recursive:true});fs.writeFileSync(path.join(d,f),t)};
    const r=f=>fs.readFileSync(path.join(d,f),'utf8');
    const PH='mods/ts3agent/src/plugin/Handlers/PatchHandlers.cs';
    const ph=(n,names,cl)=>`class P\n{\n    static object Patches(Args a)\n    {\n            int[] c = d.GetData("ts3fixes.counters") as int[] ?? new int[${n}];\n            string[] names = { "gohome", "visit", "jobyield"${names} };\n            foreach (string n in names)\n            {\n                r[n] = sw.TryGetValue(n, out on) ? on : n != "savecleanup" && n != "jobyield"${names.replace(/, "(\w+)"/g,' && n != "$1"')};\n            }\n            if (c.Length > 250) { r["jobyield_steps"] = c[248]; }\n${cl}            r["log"] = d.GetData("x");\n    }\n}\n`;
    const fx=n=>`static int[] PatchCounters()\n{\n    if (c == null) { c = new int[${n}]; AppDomain.CurrentDomain.SetData("ts3fixes.counters", c); }\n    return c;\n}\n`;
    const bf=x=>`const TUNING = \`\n    <kJobYield value="False" />\n${x}  </Current_Tuning>\n\`;\n`;
    const reg=(n,before,fixRows,natRows,nf,nv)=>`# Registry\n\n${FIXHEAD} (\`int[${n}]\`${before})\n\n| Slots | Owner | Meaning |\n|---|---|---|\n| 248-254 | x | y |\n${fixRows}| **${nf}+** | next free | reserve here  |\n\nAdding slots: text.\n\n## ts3native counters (\`Ts3nCounters\`)\n\n| Version | ts3native | Offsets | Fields |\n|---|---|---|---|\n| v110 | 0.36.39 | 0x4628-0x4647 | b |\n${natRows}| **v${nv}** | next | reserve here |\n\nv110 total: 10 int32.\n`;
    const led=x=>`# 51. Ledger\n\n## 315. Additions base\n\n| row |\n${x}`;
    const shared=(o,t)=>`a\n${o}b\nc\nd\ne\n${t}f\n`;
    w(PH,ph(255,'',''));w('mods/ts3fixes/src/Fixes.cs',fx(255));w('tools/setup/build-fixes.ts',bf(''));w('docs/registry.md',reg(255,'','','',255,111));
    w(LEDGER,led(''));w('docs/shared.md',shared('',''));w('other.txt','x\n');
    g('init','-q','-b','master');g('config','core.autocrlf','false');g('add','-A');g('commit','-qm','base');g('branch','theirs');
    w(PH,ph(260,', "overwatchstuckslice"','            if (c.Length > 259) { r["owslice_ticks"] = c[255]; }\n'));w('mods/ts3fixes/src/Fixes.cs',fx(260));
    w('tools/setup/build-fixes.ts',bf('    <kOverwatchStuckSlice value="False" />\n'));
    w('docs/registry.md',reg(260,'; `int[255]` before ows','| 255-259 | ows | slice |\n','| v111 | 0.36.40 | 0x4648-0x465F | ours |\n',260,112));
    w(LEDGER,led('\n## 316. Additions ours\n\n| ours row |\n'));w('docs/shared.md',shared('ours: ledger §316\n',''));w('other.txt','y\n');
    g('commit','-qam','ours');g('checkout','-q','theirs');
    w(PH,ph(280,', "seasonmarkercache", "safespotyield"','            if (c.Length > 279) { r["smc_hits"] = c[270]; }\n'));w('mods/ts3fixes/src/Fixes.cs',fx(280));
    w('tools/setup/build-fixes.ts',bf('    <kSeasonMarkerCache value="False" />\n    <kSafeSpotYield value="False" />\n'));
    w('docs/registry.md',reg(280,'; `int[255]` before smc','| 270-279 | smc | markers |\n','| v111 | 0.36.40 | 0x4648-0x4653 | theirs |\n',280,112));
    w(LEDGER,led('\n## 316. Additions theirs\n\nsee ledger §316 and §316 here\n\n## 317. More theirs\n\n| row |\n'));
    w('docs/shared.md',shared('','theirs: ledger §316, docs/51 §317, docs/51 §50\n'));w('docs/theirs-note.md','new: ledger §317\n');w('other.txt','z\n');
    g('add','-A');g('commit','-qm','theirs');g('checkout','-q','master');
    try{g('merge','-q','theirs');}catch{}
    // a third agent holds §318 (registry.ts reserve ledger, untracked machine file): the renumbering skips it
    w('docs/data/registry-reservations.json',JSON.stringify({reservations:[{id:'ledger-318',kind:'ledger',from:318,to:318,by:'x',what:'y'}]}));
    const out=execFileSync(process.execPath,[__filename],{cwd:d,encoding:'utf8'});
    const p=r(PH),R=r('docs/registry.md'),l=r(LEDGER);
    ok(p.includes('new int[280]')&&r('mods/ts3fixes/src/Fixes.cs').includes('new int[280]')&&!r('mods/ts3fixes/src/Fixes.cs').includes('<<<'),`${style}: counters array size -> max (280)`);
    ok(p.includes('{ "gohome", "visit", "jobyield", "overwatchstuckslice", "seasonmarkercache", "safespotyield" }'),`${style}: switch-name list union in order`);
    ok(p.includes('n != "jobyield" && n != "overwatchstuckslice" && n != "seasonmarkercache" && n != "safespotyield";'),`${style}: default-exclusion chain union`);
    ok(p.includes('r["owslice_ticks"]')&&p.includes('r["smc_hits"]')&&!p.includes('<<<'),`${style}: PatchHandlers counter-name lines kept both`);
    ok(['kJobYield','kOverwatchStuckSlice','kSeasonMarkerCache','kSafeSpotYield'].every(k=>r('tools/setup/build-fixes.ts').includes(k)),`${style}: tuning XML union`);
    ok(R.includes('| 255-259 | ows')&&R.includes('| 270-279 | smc')&&R.indexOf('255-259')<R.indexOf('270-279')&&R.includes('| **280+** | next free')&&R.includes('`int[280]`')&&!R.includes('<<<'),`${style}: registry slot rows both kept, next free 280+, heading int[280]`);
    ok(R.includes('| ours |')&&R.includes('| theirs |')&&R.includes('| **v112** | next | after v111 (+0x4660)')&&/COLLISION: ts3native v111 taken 2 times/.test(out),`${style}: registry v111 collision kept both, reported, next v112`);
    ok(/## 316\. Additions ours[\s\S]*## 317\. Additions theirs[\s\S]*see ledger §317 and §316 here[\s\S]*## 319\. More theirs/.test(l)&&!l.includes('<<<'),`${style}: ledger incoming sections renumbered 317, 319 (318 reserved)`);
    ok(r('docs/shared.md').includes('ours: ledger §316')&&r('docs/shared.md').includes('theirs: ledger §317, docs/51 §319, docs/51 §50'),`${style}: refs fixed only in the incoming branch's lines`);
    ok(r('docs/theirs-note.md')==='new: ledger §319\n',`${style}: refs fixed in an incoming-only file`);
    ok(r('other.txt').includes('<<<<<<<')&&/STILL CONFLICTED: other\.txt$/m.test(out.trim()),`${style}: unknown conflict left alone, only it still conflicted`);
    fs.rmSync(d,{recursive:true,force:true});
  }
  { const d=fs.mkdtempSync(path.join(os.tmpdir(),'agent-merge-finish-'));const g=(...a)=>execFileSync('git',['-c','user.name=t','-c','user.email=t@example.invalid',...a],{cwd:d,encoding:'utf8',stdio:['ignore','pipe','pipe']});
    g('init','-q','-b','master');fs.writeFileSync(path.join(d,'a'),'a');g('add','-A');g('commit','-qm','a');g('checkout','-qb','side');fs.writeFileSync(path.join(d,'b'),'b');g('add','-A');g('commit','-qm','b');g('checkout','-q','master');
    const r=require('child_process').spawnSync(process.execPath,[__filename,'finish','side'],{cwd:d,encoding:'utf8'});
    ok(r.status===1&&/side is not merged into HEAD/.test(r.stderr),'finish refuses a branch not merged into HEAD');fs.rmSync(d,{recursive:true,force:true});
    const re=/^Merge (?:remote-tracking )?branch '([^']+)'/;ok(("Merge branch 'bugfix/mandatory-cleanup-1117' into merge/master-1117c".match(re)||[])[1]==='bugfix/mandatory-cleanup-1117'&&!re.test("Merge commit 'abc123'"),'MERGE_MSG branch regex: real match + non-match'); }
  if(bad)process.exit(1);
}
// finish: merged + committed, then the branch's worktree goes (cleanup.ts: clean, pushed/merged, not used by a chain, junction-safe)
function finish(b,cwd=process.cwd()){
  const g=(...a)=>execFileSync('git',a,{cwd,encoding:'utf8',stdio:['ignore','pipe','pipe'],timeout:300000}).trim();
  if(!b){console.error('usage: node tools/agent-merge.cjs finish <branch>');return 2}
  if(fs.existsSync(path.join(g('rev-parse','--absolute-git-dir'),'MERGE_HEAD'))){console.error('finish: a merge is in progress: commit it first');return 1}
  try{execFileSync('git',['merge-base','--is-ancestor',b,'HEAD'],{cwd,stdio:'ignore',timeout:300000})}catch{console.error(`finish: ${b} is not merged into HEAD (${g('rev-parse','--abbrev-ref','HEAD')})`);return 1}
  const r=require('child_process').spawnSync(process.execPath,[path.join(__dirname,'harness','cleanup.ts'),'worktrees','--apply','--branch',b,'--target',g('rev-parse','HEAD')],{cwd,stdio:'inherit',timeout:1800000});
  if(r.status!==0){console.error(`finish: worktree cleanup for ${b} failed (exit ${r.status ?? r.signal}): see the keep/REFUSED line above; fix it, then rerun finish`);return 1}
  // the lane's own game-copy build copies (superseded by the merge rebuild; hashes stay in builds.json); a keep line is not a failure
  const l=require('child_process').spawnSync(process.execPath,[path.join(__dirname,'harness','cleanup.ts'),'lane','--apply','--branch',b],{cwd,stdio:'inherit',timeout:600000});
  console.log(`finish: ${b} merged, worktree cleaned${l.status===0?'':`; lane build cleanup exit ${l.status ?? l.signal} (see above)`}`);return 0
}
if(process.argv[2]==='finish')process.exit(finish(process.argv[3]));
else process.argv.includes('--self-check')?selfCheck():main();
