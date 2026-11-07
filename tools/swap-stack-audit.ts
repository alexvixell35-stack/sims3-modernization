// Which ts3fixes method swaps can sit on a saved Sim task stack (docs/242). A swap there is only safe with a stub: the load
// remap (SavedStacks.cs) resumes a saved frame in the stub, which holds EA's IL. A swap with no stub, or one the table does
// not know, resets Sims at load.
//   node tools/swap-stack-audit.ts                gate, no game files: every Patch in mods/ts3fixes/src is in the table with
//                                                 the same stub; a row that can yield has a stub; no table row is stale
//   node tools/swap-stack-audit.ts --recompute    local: rewrites docs/data/swap-stack-audit.csv from the managed call graph
//                                                 (game-copy/analysis/callgraph/index.json, game-copy/assemblies); new rows' purpose
//                                                 comes from the comment above the hook class, reviewed rows keep theirs
//   node tools/swap-stack-audit.ts --self-check   gate plus unit checks on small fixtures
// "Can yield" = the method, or anything it calls (virtual calls: every override), reaches a yield primitive
// (managed-callgraph.ts YIELD_IC / YIELD_M). Delegates are not followed, so "no" means no direct path. shortcut: a delegate
// that yields stays "no"; the stub is what protects a row, and every Patch but the ones in kNoStub has one.
import { readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import assert from 'node:assert/strict';

const ROOT = resolve(import.meta.dirname, '..');
const SRC = join(ROOT, 'mods', 'ts3fixes', 'src');
const CSV = join(ROOT, 'docs', 'data', 'swap-stack-audit.csv');
const HEAD = 'hook,target,method,file,stub,yields,proof,purpose,action';

export type Patch = { hook: string; target: string; method: string; file: string; stub: string };
export type Row = Patch & { yields: string; proof: string; purpose: string; action: string };

// a target held in a variable: variable name in that file -> the class it names
const TARGET_VARS: Record<string, string> = {
  'RefTable.cs:t': 'ScriptCore.ReferenceHashtable', 'CachedReflection.cs:pc': 'ScriptCore.PersistedClass',
  'CachedReflection.cs:db': 'ScriptCore.PersistedClassDB', 'FewerPlans.cs:helper': 'LotManager+RunStuckCheckHelper',
  'RouteSources.cs:watch': 'VelvetRopes+WatchDoor', 'PlayTrace.cs:flow': 'Sims3.UI.GameEntry.PlayFlowInWorldFlow',
  'UiCaches.cs:bar': 'MotivesPanel+MotiveBar', 'UiCaches.cs:panel': 'MotivesPanel', 'EaVacation.cs:sVacBack': 'ParentsLeavingTownSituation+ParentsAreBack',
  'Fixes.cs:t': 'mod-subclass:VisitCommunityLot', 'typeof:AutonomyT': 'Sims3.Gameplay.Autonomy.Autonomy',
  'ErrorTrapSpread.cs:inv': 'mod:ErrorTrap', 'OverwatchNotice.cs:reset': 'mod:Overwatch', 'OverwatchProbe.cs:sc': 'mod:Overwatch',
  'HitchCause.cs:sHcTimer': 'AlarmManager+Timer', 'HitchCause.cs:simReset': 'mod:Overwatch', 'HitchCause.cs:inst': 'mod:Overwatch', 'HitchCause.cs:cp': 'mod:Overwatch', 'HitchCause.cs:place': 'mod:Overwatch', 'OverwatchSpread.cs:sOwType': 'mod:Overwatch', 'OverwatchSpread.cs:alarmBase': 'mod:Overwatch', 'OverwatchStuck.cs:simEx': 'mod:Overwatch', 'OverwatchStuck.cs:simTypes': 'mod:Overwatch',
};
// reviewed in docs/242: hooks that may have no stub
const kNoStub = new Set<string>();

// top-level commas only
export function splitArgs(s: string): string[] {
  const out: string[] = []; let d = 0, cur = '', q = false;
  for (const ch of s) {
    if (ch === '"') q = !q;
    if (!q && '([{'.includes(ch)) d++;
    if (!q && ')]}'.includes(ch)) d--;
    if (!q && ch === ',' && d === 0) { out.push(cur.trim()); cur = ''; } else cur += ch;
  }
  if (cur.trim()) out.push(cur.trim());
  return out;
}

// Patch(target, "m", hook, "r", "s"), Patch(target, "m", args, hook, "r", "s") and Hook(failed, target, "m", args, hook, "r", "s");
// forwarders with names in variables are skipped
export function parsePatches(text: string, file: string): Patch[] {
  const out: Patch[] = [];
  const clean = text.replace(/^\s*\/\/.*$/gm, '');
  for (const m of clean.matchAll(/(?<![\w.])(?:Patch|Hook)\(/g)) {
    if (/static\s+\w+\s+$/.test(clean.slice(Math.max(0, m.index! - 30), m.index))) continue;
    let d = 1, i = m.index! + m[0].length;
    for (let q = false; d > 0 && i < clean.length; i++) { if (clean[i] === '"') q = !q; if (!q && clean[i] === '(') d++; if (!q && clean[i] === ')') d--; }
    const a = splitArgs(clean.slice(m.index! + m[0].length, i - 1).replace(/\s+/g, ' '));
    if (m[0].startsWith('Hook')) a.shift();
    if (a.length !== 5 && a.length !== 6) continue;
    if (!a[1].startsWith('"')) continue; // forwarder: Patch(target, method, ...)
    const [hookE, rep, stub] = a.slice(-3);
    const hook = /typeof\((.+)\)/.exec(hookE)?.[1] ?? hookE.replace(/\[.*$/, '[]');
    const tv = a[0], ty = /^typeof\((.+?)\)$/.exec(tv)?.[1], tm = ty && !tv.includes('GetNestedType') ? TARGET_VARS[`typeof:${ty}`] ?? ty : (tv.includes('GetNestedType') ? `${/typeof\((.+?)\)/.exec(tv)![1]}+${/"(\w+)"/.exec(tv)![1]}` : TARGET_VARS[`${file}:${tv}`] ?? tv);
    out.push({ hook: `${hook}.${rep.replace(/"/g, '')}`, target: tm, method: a[1].replace(/"/g, ''), file, stub: stub === 'null' ? '' : stub.replace(/"/g, '') });
  }
  return out;
}

export function sourcePatches(dir: string): Patch[] {
  return readdirSync(dir).filter((f) => f.endsWith('.cs')).sort().flatMap((f) => parsePatches(readFileSync(join(dir, f), 'utf8'), f));
}

const csvCell = (s: string) => (/[",\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s);
export function toCsv(rows: Row[]): string {
  return [HEAD, ...rows.map((r) => [r.hook, r.target, r.method, r.file, r.stub, r.yields, r.proof, r.purpose, r.action].map(csvCell).join(','))].join('\n') + '\n';
}
export function parseCsv(text: string): Row[] {
  const out: Row[] = [];
  for (const line of text.split(/\r?\n/).slice(1).filter(Boolean)) {
    const c: string[] = []; let cur = '', q = false;
    for (let i = 0; i < line.length; i++) {
      const ch = line[i];
      if (q) { if (ch === '"' && line[i + 1] === '"') { cur += '"'; i++; } else if (ch === '"') q = false; else cur += ch; }
      else if (ch === '"') q = true; else if (ch === ',') { c.push(cur); cur = ''; } else cur += ch;
    }
    c.push(cur);
    const [hook, target, method, file, stub, yields, proof, purpose, action] = c;
    out.push({ hook, target, method, file, stub, yields, proof, purpose, action });
  }
  return out;
}

export function gate(patches: Patch[], rows: Row[]): string[] {
  const bad: string[] = [], key = (p: Patch) => `${p.hook}|${p.target}|${p.method}`;
  const known = new Map(rows.map((r) => [key(r), r])), now = new Set(patches.map(key));
  for (const p of patches) {
    const r = known.get(key(p));
    if (!r) { bad.push(`new swap not in the table: ${p.target}.${p.method} (${p.hook}, ${p.file}): run --recompute and review (docs/242)`); continue; }
    if (r.stub !== p.stub) bad.push(`stub changed: ${p.hook}: table "${r.stub}", source "${p.stub}"`);
    if (!p.stub && !kNoStub.has(p.hook)) bad.push(`swap with no stub: ${p.target}.${p.method} (${p.hook}): a saved frame there is refused at load`);
  }
  for (const r of rows) if (!now.has(key(r))) bad.push(`stale row: ${r.hook} ${r.target}.${r.method} is no longer patched`);
  return bad;
}

// ---- --recompute (local, needs the game's assemblies) ----
const FIX_FILES = /^(EaFixes|EaVacation|Fixes|RouteFixes|Compaction|DeadRefs|PortalAdaptorLeak|PosturePop|SeasonImposter|WakePark|RouteSources|UnseenFallbackFinish|ReplanDefer)\.cs$/;
function purposeOf(p: Patch): string {
  const cls = p.hook.split('.')[0].replace('[]', ''), lines = readFileSync(join(SRC, p.file), 'utf8').split(/\r?\n/);
  let i = lines.findIndex((l) => new RegExp(`class ${cls}\\b`).test(l));
  const cm: string[] = [];
  while (--i >= 0 && /^\s*\/\//.test(lines[i])) cm.unshift(lines[i].trim().replace(/^\/\/\s*/, ''));
  return (cm.join(' ').slice(0, 110) || p.file.replace('.cs', '')).replace(/\s+\S*$/, '');
}

async function recompute(): Promise<void> {
  const cg = await import('./managed-callgraph.ts');
  const ix = JSON.parse(readFileSync(join(ROOT, 'game-copy', 'analysis', 'callgraph', 'index.json'), 'utf8'));
  const ms = ix.methods as { k: string; ic?: string; c: number[] }[];
  const YIELD_IC = /::(Simulator_SleepImpl|SACS_AcquireDriverImpl|GameUtils_WaitFor\w*|GameUtils_StartupYield|OnlineFeaturesHost_WaitForDownload)$/;
  const YIELD_M = /^(Sims3\.SimIFace\.Simulator::(Sleep|Yield\w*)\(|Sims3\.SimIFace\.StateMachineClient::RequestState\()/;
  const rev = new Map<number, number[]>(), add = (to: number, from: number) => { const l = rev.get(to); if (l) l.push(from); else rev.set(to, [from]); };
  const ov = new Map<number, number[]>(), ovOf = (t: number) => { let o = ov.get(t); if (!o) { o = cg.virtualTargets(t).ea; ov.set(t, o); } return o; };  // BCL targets (object, collections) not expanded
  ms.forEach((m, i) => {
    for (let k = 0; k < m.c.length; k += 2) {
      const t = m.c[k], kind = ix.kinds[m.c[k + 1]];
      if (kind === 'dlg') continue;
      add(t, i);
      if (kind === 'virt' || kind === 'iface' || kind === 'cons') for (const o of ovOf(t)) add(o, i);
    }
  });
  const next = new Map<number, number>(), q: number[] = [];
  ms.forEach((m, i) => { if ((m.ic && YIELD_IC.test(m.ic)) || YIELD_M.test(m.k)) { next.set(i, -1); q.push(i); } });
  for (let h = 0; h < q.length; h++) for (const c of rev.get(q[h]) ?? []) if (!next.has(c)) { next.set(c, q[h]); q.push(c); }
  const short = (k: string) => k.replace(/\(.*$/s, '').replace(/^.*\.(\w+(\+\w+)*::)/, '$1');
  const byKey = (p: Patch) => {
    const t = p.target.replace(/^Sims3\.\w+\.(\w+\.)*(?=[A-Z]\w*\.[A-Z]\w*$)/, '').replace(/[.+]/g, '[.+]');
    const re = new RegExp(`(^|[.+])${t}::${p.method}\\(`), hits: number[] = [];
    ms.forEach((m, i) => { if (re.test(m.k)) hits.push(i); });
    return hits;
  };
  const old = new Map<string, Row>();
  try { for (const r of parseCsv(readFileSync(CSV, 'utf8'))) old.set(`${r.hook}|${r.target}|${r.method}`, r); } catch { /* first run */ }
  const rows: Row[] = sourcePatches(SRC).map((p) => {
    const was = old.get(`${p.hook}|${p.target}|${p.method}`);
    const rowOf = (yields: string, proof: string): Row => ({ ...p, yields, proof, purpose: was?.purpose || purposeOf(p), action: was?.action || (yields === 'no' ? 'keep' : 'keep, stub remap (SavedStacks.cs)') });
    if (p.target.startsWith('mod')) return rowOf('mod', 'mod target, not in the game index; stub covers it');
    const hits = byKey(p);
    if (!hits.length) return rowOf('?', `not found in the index: ${p.target}::${p.method}`);
    const hit = hits.find((i) => next.has(i));
    if (hit === undefined) return rowOf('no', `no path to a yield in ${hits.length} overload(s)`);
    const path: number[] = [];
    for (let n = hit; n !== -1 && path.length < 40; n = next.get(n)!) path.push(n);
    const s = path.map((i) => short(ms[i].k));
    return rowOf('yes', s.length > 4 ? `${s.slice(0, 2).join(' > ')} > .. ${s.length - 3} .. > ${s.at(-1)}` : s.join(' > '));
  });
  writeFileSync(CSV, toCsv(rows));
  const n = (y: string) => rows.filter((r) => r.yields === y).length;
  console.log(`swap-stack-audit: ${rows.length} swaps, yes ${n('yes')}, no ${n('no')}, mod ${n('mod')}, unresolved ${n('?')}`);
}

function runGate(): number {
  const bad = gate(sourcePatches(SRC), parseCsv(readFileSync(CSV, 'utf8')));
  for (const b of bad) console.log(`FAIL ${b}`);
  if (!bad.length) console.log('swap-stack-audit: every swap is in the table, every yielding swap has a stub');
  return bad.length ? 1 : 0;
}

function selfCheck(): void {
  assert.deepEqual(splitArgs('typeof(A), "m,x", new Type[] { typeof(B), typeof(C) }, x'), ['typeof(A)', '"m,x"', 'new Type[] { typeof(B), typeof(C) }', 'x']);
  const src = `static void Patch(Type target, string method, Type hook, string replacement, string stub) { Patch(target, method, null, hook, replacement, stub); }
    // Patch(typeof(Commented), "Out", typeof(H), "H", "O");
    Patch(typeof(Sim), "DoRoute", typeof(RouteHook), "RouteH", "RouteO");
    Hook(failed, typeof(Lot), "Open", null, typeof(H4), "OpenH", "OpenO");
    Patch(typeof(Sim.Inner), "Run",
          new Type[] { typeof(Sim) }, typeof(H2), "RunH", null);
    Patch(typeof(Lot).GetNestedType("Help", BindingFlags.NonPublic), "Go", typeof(H3), "GoH", "GoO");`;
  const p = parsePatches(src, 'X.cs');
  assert.deepEqual(p.map((x) => `${x.target}.${x.method}/${x.hook}/${x.stub}`), ['Sim.DoRoute/RouteHook.RouteH/RouteO', 'Lot.Open/H4.OpenH/OpenO', 'Sim.Inner.Run/H2.RunH/', 'Lot+Help.Go/H3.GoH/GoO']);
  const row = (x: Patch): Row => ({ ...x, yields: 'yes', proof: 'a > b, "q"', purpose: 'p', action: 'keep' });
  assert.deepEqual(parseCsv(toCsv([row(p[0])]))[0], row(p[0]));
  assert.equal(gate(p.slice(0, 1), [row(p[0])]).length, 0);
  assert.match(gate(p.slice(0, 1), [])[0], /new swap not in the table/);          // a new swap can't slip in
  assert.match(gate([p[2]], [row(p[2])])[0], /swap with no stub/);                 // nor one with no stub
  assert.match(gate([], [row(p[0])])[0], /stale row/);
  assert.match(gate(p.slice(0, 1), [{ ...row(p[0]), stub: 'Other' }])[0], /stub changed/);
  assert.equal(runGate(), 0, 'repo gate');
  console.log('swap-stack-audit self-check ok');
}

if (process.argv.includes('--self-check')) selfCheck();
else if (process.argv.includes('--recompute')) await recompute();
else process.exit(runGate());
