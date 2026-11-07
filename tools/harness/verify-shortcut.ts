// Quick check of a trip shortcut on a loaded world (much shorter than a profile run):
//   node tools/harness/verify-shortcut.ts --name rabbithole[,visit,...] [--tries 20] [--minutes 5] [--on a,b]
// Runs at x3 with popups cleared and checks every 30 s: shortcut counters (patches command: <name>_shortcuts,
// <name>_no_spot) and stuck Sims as watchdog.ts judges them, marking Sims a shortcut recently placed. Passes once each
// named shortcut has fired --tries times with no stuck teleported Sim and at least 2 min elapsed; fails on a stuck teleported
// Sim, more than --max-stuck stuck Sims of any kind, or when --minutes run out. Exit code 0 = pass. Needs the probe server and a world in live mode.
import { parseArgs } from 'node:util';
import { reproSave } from './repro-save.ts';
import { mayBeStuck } from './stuck-rule.ts';
import { watchGame } from './game-alive.ts';

const { values } = parseArgs({ options: { name: { type: 'string' }, tries: { type: 'string', default: '20' }, minutes: { type: 'string', default: '5' }, 'max-stuck': { type: 'string', default: '5' }, off: { type: 'string' }, on: { type: 'string' } } });
if (!values.name) throw new Error('--name required (gohome, rabbithole, ...)');
const maxStuck = Number(values['max-stuck']);
const names = values.name.split(','), tries = Number(values.tries), limit = Number(values.minutes) * 60000;
const sleep = (ms: number) => new Promise(r => setTimeout(r, ms));
const stamp = () => new Date().toISOString().slice(11, 19);
const ag = async (cmd: string, args?: object): Promise<any> => {
  const q = new URLSearchParams({ cmd, timeout: '8000' });
  if (args) q.set('args', JSON.stringify(args));
  try { return await (await fetch(`http://127.0.0.1:47891/agent?${q}`, { signal: AbortSignal.timeout(20000) })).json(); } catch (e) { return { ok: false, error: String(e) }; }
};

const alive = watchGame();
await ag('autopilot', { enabled: true, speed: 'ultra', dialogs: true });
// --off a,b: switch those shortcuts off for this run (a baseline for the stuck count)
if (values.off) await ag('patches', Object.fromEntries(values.off.split(',').map(n => [n, false])));
if (values.on) await ag('patches', Object.fromEntries(values.on.split(',').map(n => [n, true]))); // --on a,b: opt-in switches of the shortcut under test
const start = (await ag('patches')).result ?? {};
const base = Object.fromEntries(names.map(n => [n, { shortcuts: start[`${n}_shortcuts`] ?? 0, noSpot: start[`${n}_no_spot`] ?? 0 }]));
const seen = new Map<string, { key: string; checks: number; reported: boolean }>();
const stuck: string[] = [];
const t0 = Date.now();
let verdict = 'FAIL: no checks ran';
while (Date.now() - t0 < limit) {
  await sleep(30000);
  const pr = await ag('patches'), act = await ag('sims_activity');
  if (!pr.ok || !act.ok) { console.log(`${stamp()} no answer: ${pr.error ?? act.error}`); continue; } // not "no stuck Sims"
  const p = pr.result ?? {};
  const moved: Record<string, string> = p.moved ?? {}; // every Sim a shortcut moved
  const sims = act.result ?? [];
  for (const s of sims) {
    const key = `${s.x},${s.z},${s.interaction},${s.routing}`;
    const prev = seen.get(s.id);
    const e = prev && prev.key === key ? { key, checks: prev.checks + 1, reported: prev.reported } : { key, checks: 1, reported: false };
    seen.set(s.id, e);
    if (!e.reported && mayBeStuck(s.interaction, !!s.routing, !!s.in_transit) && e.checks >= (s.routing ? 3 : 6)) {
      e.reported = true;
      const teleported = s.name in moved;
      stuck.push(`${s.name}${teleported ? ' [teleported]' : ''} on ${s.interaction}`);
    }
  }
  const got = names.map(n => ({ n, shortcuts: (p[`${n}_shortcuts`] ?? 0) - base[n].shortcuts, noSpot: (p[`${n}_no_spot`] ?? 0) - base[n].noSpot }));
  console.log(`${stamp()} ${got.map(g => `${g.n}: ${g.shortcuts} shortcuts, ${g.noSpot} without a safe spot`).join('; ')}; stuck: ${stuck.length ? stuck.join('; ') : 'none'}`);
  if (stuck.some(s => s.includes('[teleported]'))) { verdict = 'FAIL: a teleported Sim is stuck'; break; }
  // any Sims stuck count too: a shortcut can strand Sims it never moved (e.g. ones waiting on a car it moved)
  if (stuck.length > maxStuck) { verdict = `FAIL: ${stuck.length} stuck Sims (more than --max-stuck ${maxStuck}); compare with the shortcut off`; break; }
  if (got.every(g => g.shortcuts >= tries) && Date.now() - t0 >= 120000) { verdict = 'PASS'; break; }
  verdict = `FAIL: too few tries for ${got.filter(g => g.shortcuts < tries).map(g => g.n).join(', ')} within the time limit (try another world or save)`;
}
// keep the situation for a direct retest of the fix (load the repro save instead of waiting for it to recur)
if (stuck.length) await reproSave(ag, `verify-${names[0]}`, stuck.map(x => ({ sim: x })));
await alive.check();
console.log(`${stamp()} ${verdict}; recent: ${((await ag('patches')).result?.recent ?? []).slice(-6).join(' | ')}`);
process.exit(verdict === 'PASS' ? 0 : 1);
