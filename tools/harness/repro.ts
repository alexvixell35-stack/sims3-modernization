// Repro library runner: reproduce a bug on demand from a saved situation, then check whether a fix handles it.
//   node tools/harness/repro.ts list
//   node tools/harness/repro.ts run <id> [--no-load] [--set a=true,b=false]   (needs the probe server; loads the entry's save unless --no-load)
//   node tools/harness/repro.ts run-many <id,id,...> [--no-load] [--set ...]  several repros in one load: same save, areas at
//     least 150 m apart and no shared Sims, else the conflicting ones are left for another batch; all watched together
// Entries live in tools/harness/repros.json: { id, bug, fix, save, steps: [{cmd, args, waitS?}], watch, expect }.
//   watch.kind "stuck":  watch these Sims (or all, if "sims" is empty) for watch.minutes; PASS when none is stuck
//                        (same spot and interaction for 6 checks of 30 s, or routing without moving for 3).
//   watch.kind "counter": PASS when patches.<watch.counter> rose by at least watch.min within watch.minutes.
//   watch.kind "native": PASS when the ts3native counter deltas (steps + watch.minutes) meet every watch.checks entry.
// Steps are agent commands (place_sim, sim_go_home, do_interaction, patches {name:false}, ...), run in order.
import { execFileSync } from 'node:child_process';
import { readFileSync, rmSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { mayBeStuck } from './stuck-rule.ts';
import { stallGuard, watchGame } from './game-alive.ts';

const ROOT = resolve(import.meta.dirname, '..', '..');
const LIB = JSON.parse(readFileSync(join(ROOT, 'tools', 'harness', 'repros.json'), 'utf8')) as any[];
const [cmd, id] = process.argv.slice(2);
const sleep = (ms: number) => new Promise(r => setTimeout(r, ms));
const stamp = () => new Date().toISOString().slice(11, 19);
const ag = async (c: string, args?: object): Promise<any> => {
  const q = new URLSearchParams({ cmd: c, timeout: '8000' });
  if (args) q.set('args', JSON.stringify(args));
  try { return await (await fetch(`http://127.0.0.1:47891/agent?${q}`, { signal: AbortSignal.timeout(20000) })).json(); } catch (e) { return { ok: false, error: String(e) }; }
};

if (cmd === 'list' || !cmd) {
  for (const r of LIB) console.log(`${r.id.padEnd(28)} ${r.save.padEnd(26)} ${r.bug}`);
  process.exit(0);
}
// run-many: merge compatible entries into one virtual entry (one save, areas apart, no shared Sims)
let r: any;
if (cmd === 'run-many') {
  const want = (id ?? '').split(',').map(x => LIB.find(e => e.id === x)).filter(Boolean);
  const batch: any[] = [], later: string[] = [];
  const simsOf = (e: any) => new Set([...(e.watch?.sims ?? []), ...(e.steps ?? []).map((s: any) => s.args?.sim).filter(Boolean)]);
  for (const e of want) {
    const clash = batch.find(b => b.save !== e.save
      || (b.area && e.area && Math.hypot(b.area[0] - e.area[0], b.area[1] - e.area[1]) < 150)
      || [...simsOf(e)].some(n => simsOf(b).has(n)));
    if (clash) later.push(e.id); else batch.push(e);
  }
  if (!batch.length) { console.error('no matching entries'); process.exit(2); }
  if (later.length) console.log(`not in this batch (same place, Sim or another save): ${later.join(', ')}`);
  const counters = batch.filter(e => e.watch.kind === 'counter' || e.watch.kind === 'native');
  if (counters.length) console.log(`counter / native watches run separately: ${counters.map(e => e.id).join(', ')}`);
  const stuckOnes = batch.filter(e => e.watch.kind !== 'counter' && e.watch.kind !== 'native');
  r = { id: stuckOnes.map(e => e.id).join('+'), bug: `${stuckOnes.length} repros together`, fix: '-', save: batch[0].save,
    steps: stuckOnes.flatMap(e => e.steps ?? []),
    watch: { kind: 'stuck', sims: stuckOnes.flatMap(e => e.watch.sims ?? []), minutes: Math.max(...stuckOnes.map(e => e.watch.minutes)) },
    expect: 'every repro passes' };
} else r = LIB.find(x => x.id === id);
if ((cmd !== 'run' && cmd !== 'run-many') || !r) { console.error('usage: repro.ts list | run <id> | run-many <id,id,...> [--no-load] [--set ...]'); process.exit(2); }

console.log(`${stamp()} ${r.id}: ${r.bug}\n  fix under test: ${r.fix}`);
if (!process.argv.includes('--no-load'))
  execFileSync(process.execPath, [join(ROOT, 'tools', 'harness', 'cycle.ts'), '--save', `${r.save}.sims3`], { stdio: 'inherit' });
const alive = watchGame(); // FAIL as soon as the game dies, never a PASS on a dead game
await ag('autopilot', { enabled: true, speed: 'ultra', dialogs: true });
// --set a=true,b=false: switch fixes for this run (patches command), e.g. to bisect which one causes a bug
const set = process.argv.indexOf('--set');
if (set > 0) {
  const sw = Object.fromEntries(process.argv[set + 1].split(',').map(kv => { const [k, v] = kv.split('='); return [k, v !== 'false']; }));
  console.log(`${stamp()} switches ${JSON.stringify(sw)}: ${(await ag('patches', sw)).ok ? 'ok' : 'failed'}`);
}
// tell stuck-scan.ts which Sims this repro uses, so it does not report them as new cases
const ACTIVE = join(ROOT, 'probe-out', 'repro-active.json');
writeFileSync(ACTIVE, JSON.stringify({ id: r.id, sims: [...new Set([...(r.watch?.sims ?? []), ...(r.steps ?? []).map((x: any) => x.args?.sim).filter(Boolean)])] }));
process.on('exit', () => rmSync(ACTIVE, { force: true }));
// counter baseline before the steps: a step can trigger the counted event itself (bp-overwatch-stuckcheck)
const counterStart = r.watch?.kind === 'counter' ? (await ag('patches')).result?.[r.watch.counter] ?? 0 : 0;
// watch.kind "native": ts3native counters (/native) before the steps and after watch.minutes; PASS when every delta check holds,
// checks: [{name, atLeast | atMost | equals | equalsName (same delta as that counter)}] (pet-guard-cancel: T-D5)
const native = async (): Promise<any> => {
  try { return await (await fetch('http://127.0.0.1:47891/native', { signal: AbortSignal.timeout(20000) })).json(); } catch { return null; }
};
const nativeStart = r.watch?.kind === 'native' ? await native() : null;
// "memory": true on the entry logs the probe's /memory (commitMB, privateMB, largest free block) before the steps and after each,
// plus allocations >= 32 MB from /allocations that are new or gone since the previous log (docs/08 M9)
let allocs: Map<string, any> | null = null;
const mem = async (what: string) => {
  if (!r.memory) return;
  try {
    const m: any = await (await fetch('http://127.0.0.1:47891/memory', { signal: AbortSignal.timeout(20000) })).json();
    console.log(`${stamp()} memory ${what}: commit ${m.commitMB} private ${m.privateMB} largest free ${m.largestFreeMB} MB`);
  } catch (e) { console.log(`${stamp()} memory ${what}: ? (${e})`); }
  try {
    const a: any = await (await fetch('http://127.0.0.1:47891/allocations?minMB=32', { signal: AbortSignal.timeout(20000) })).json();
    const now = new Map<string, any>(a.allocations.map((x: any) => [`${x.base}/${x.reservedMB}`, x]));
    if (allocs) {
      for (const [k, x] of now) if (!allocs.has(k)) console.log(`${stamp()}   new alloc ${x.base} ${x.reservedMB} MB reserved, ${x.committedMB} committed, ${x.type}${x.owner ? ` ${x.owner}` : ''}`);
      for (const [k, x] of allocs) if (!now.has(k)) console.log(`${stamp()}   gone alloc ${x.base} ${x.reservedMB} MB ${x.type}`);
    } else console.log(`${stamp()}   ${a.count} allocations >= 32 MB`);
    allocs = now;
  } catch (e) { console.log(`${stamp()}   allocations ${what}: ? (${e})`); }
};
await mem('before');
// "cleanup": [{cmd, args?}] runs before a FAIL exit, e.g. window_restore so a failed clock-minimize-pause never leaves the
// game minimized (and paused) for the next job
const fail = async (msg: string) => {
  console.log(msg);
  for (const c of r.cleanup ?? []) { const res = await ag(c.cmd, c.args); console.log(`${stamp()} cleanup ${c.cmd}: ${res.ok ? JSON.stringify(res.result).slice(0, 160) : 'ERROR ' + res.error}`); }
  process.exit(1);
};
let prev: any = null;
for (const s of r.steps ?? []) {
  // "keysFromPrev": true fills args.keys with the keys of the previous step's items (e.g. cas_parts -> wear_parts)
  const args = s.keysFromPrev && prev?.items ? { ...s.args, keys: prev.items.map((x: any) => x.key) } : s.args;
  const res = await ag(s.cmd, args);
  console.log(`${stamp()} step ${s.cmd} ${JSON.stringify(args ?? {}).slice(0, 200)}: ${res.ok ? JSON.stringify(res.result).slice(0, 160) : 'ERROR ' + res.error}`);
  if (!res.ok && !s.optional) await fail('FAIL: step failed');
  if (res.ok) prev = res.result;
  if (s.waitS) await sleep(s.waitS * 1000);
  // "until": poll an agent command until every condition holds (fail-fast: give up after maxS with a FAIL), e.g. the
  // warm-up waits for temperature instead of a fixed time and the save step for save_in_progress false (2026-10-17)
  if (s.until) {
    const u = s.until, t0 = Date.now(), get = (o: any, path: string) => path.split('.').reduce((x, k) => x?.[k], o);
    const ok = (res: any) => res?.ok && u.all.every((c: any) => { const v = get(res.result, c.path);
      return c.equals !== undefined ? v === c.equals : c.atLeast !== undefined ? v >= c.atLeast : c.atMost !== undefined ? v <= c.atMost : !!v; });
    let last: any = null;
    const guard = stallGuard(`${s.cmd} until`); // progress every 30 s, two misses fail (docs/236 #18)
    while (!ok(last = await ag(u.cmd, u.args ?? {}))) {
      const why = await guard();
      if (why) await fail(`${stamp()} FAIL: ${why}`);
      if (Date.now() - t0 > (u.maxS ?? 300) * 1000) await fail(`${stamp()} FAIL: ${s.cmd} until ${JSON.stringify(u.all)} not met in ${u.maxS ?? 300} s: ${JSON.stringify(last?.result ?? last?.error).slice(0, 200)}`);
      await sleep((u.everyS ?? 5) * 1000);
    }
    console.log(`${stamp()}   until met after ${((Date.now() - t0) / 1000).toFixed(0)} s: ${JSON.stringify(last.result).slice(0, 160)}`);
  }
  await mem(`after ${s.cmd}`);
}

const w = r.watch ?? { kind: 'none' }, t0 = Date.now(); // no watch: a steps-only repro (e.g. making a base save)
let verdict = w.kind === 'none' ? 'PASS: steps done (no watch)' : '';
if (w.kind === 'counter') {
  const start = counterStart;
  while (Date.now() - t0 < w.minutes * 60000) {
    await sleep(15000);
    const n = ((await ag('patches')).result?.[w.counter] ?? 0) - start;
    console.log(`${stamp()} ${w.counter} +${n}`);
    if (n >= w.min) { verdict = 'PASS'; break; }
  }
  verdict ||= `FAIL: ${w.counter} rose less than ${w.min} in ${w.minutes} min`;
} else if (w.kind === 'native') {
  while (Date.now() - t0 < w.minutes * 60000) await sleep(Math.min(30000, w.minutes * 60000 - (Date.now() - t0))); // game death ends it (watchGame)
  const end = await native();
  if (!nativeStart || !end || nativeStart.error || end.error) verdict = `FAIL: /native unreadable (${nativeStart?.error ?? end?.error ?? 'no answer'})`;
  else {
    const d = (n: string) => (end[n] ?? 0) - (nativeStart[n] ?? 0);
    const miss = (w.checks as any[]).flatMap(c => [c.name, c.equalsName]).filter(n => n !== undefined && !(n in end)); // docs/236: absent is not 0
    console.log(`${stamp()} native deltas: ${(w.checks as any[]).map(c => `${c.name} ${d(c.name)}`).join(', ')}`);
    const bad = (w.checks as any[]).filter(c => {
      const v = d(c.name);
      return c.equalsName !== undefined ? v !== d(c.equalsName) : c.equals !== undefined ? v !== c.equals
        : c.atLeast !== undefined ? v < c.atLeast : c.atMost !== undefined ? v > c.atMost : !v;
    });
    verdict = miss.length ? `UNKNOWN: counters missing from /native: ${[...new Set(miss)].join(', ')}` : bad.length ? `FAIL: ${bad.map(c => JSON.stringify(c)).join('; ')}` : 'PASS';
  }
} else if (w.kind !== 'none') {
  const seen = new Map<string, { key: string; checks: number }>();
  const stuck = new Set<string>();
  let answered = 0;
  while (Date.now() - t0 < w.minutes * 60000) {
    await sleep(30000);
    const act = await ag('sims_activity');
    if (act.ok) answered++;
    if (!act.ok) { console.log(`${stamp()} stuck: ? (no answer: ${act.error})`); continue; } // a failed call is not "no stuck Sims"
    for (const s of act.result) {
      if (w.sims?.length && !w.sims.includes(s.name)) continue;
      const key = `${s.x},${s.z},${s.interaction},${s.routing}`;
      const p = seen.get(s.id);
      const e = p && p.key === key ? { key, checks: p.checks + 1 } : { key, checks: 1 };
      seen.set(s.id, e);
      if (mayBeStuck(s.interaction, !!s.routing, !!s.in_transit) && e.checks >= (s.routing ? 3 : 6)) stuck.add(`${s.name} on ${s.interaction} at ${s.lot ?? 'street'}`);
    }
    console.log(`${stamp()} stuck: ${stuck.size ? [...stuck].join('; ') : 'none'}`);
  }
  verdict = stuck.size ? `FAIL: ${stuck.size} stuck` : answered < 2 ? `UNKNOWN: sims_activity answered ${answered} times, stuck check not done` : 'PASS'; // docs/236
}
await alive.check();
console.log(`${stamp()} ${r.id}: ${verdict} (expected: ${r.expect})`);
process.exit(verdict.startsWith('PASS') ? 0 : 1);
