// PR #324 dead-Sim passes under Dalexlol's leak stress (#40, PR #326 driver): leak-stress-drip.py on townies, then a France
// round trip (save-matrix.ts --travel --trips 1 --mem-marks), N times, one load. The queue job pins the ts3fixes package
// (fixesPackage) that sets the switches; this script only reads them back and judges.
//   node tools/harness/deadsim-stress.ts --expect on|off [--trips 3] [--stress-min 5] [--seed 12351] [--max 30]
//   node tools/harness/deadsim-stress.ts --self-check     verdict rules, no game
// Per trip: drip for --stress-min minutes (its schedule continues across trips: the start file's mtime is moved back by
// the stress time already run, so deaths / movers / occults come due), drip stopped, then the trip. Before the first trip
// and after each: agent `patches` (switches, counters, applied lines), [934-936] household-kept counters (reflect_get on
// Ts3Fixes.Fixes.sCountersCache), /memory (commit, free, largest free block), SimDescriptions in the world (list_sims world),
// the active household (id, members) -> probe-out/deadsim/<tag>.jsonl and one 'deadsim: trip N ...' console line.
// FAIL (exit 1): a switch not as --expect (the 8 under test) or not off (seasonsdeadsim, casuihandlers, simrefsdeadsim),
// a pass that threw or says 'differs ... off' / 'not loaded, off', a trip that failed, the active household id or member
// count changed, a [934-936] counter unreadable on the ON arm; ON arm: REQUIRED counters still 0 at the end (Dalexlol's
// al355/al358 hit them every run); OFF arm: any counter of the 11 passes above 0. The live-SimDescription heap census of
// his runs has no tool here: memsus alive / diag mem gc-after lines in ts3native.log are the memory side.
import { spawn, spawnSync } from 'node:child_process';
import { appendFileSync, closeSync, mkdirSync, openSync, utimesSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { parseArgs } from 'node:util';
import assert from 'node:assert/strict';

export const UNDER_TEST = ['errortrapsims', 'errortraplookup', 'errortrapctl', 'celebritydeadsim', 'ghostreactdeadsim', 'dreamnodedeadsim', 'overwatchdeadsim', 'outfitwaitsaved'];
export const STAY_OFF = ['seasonsdeadsim', 'casuihandlers', 'simrefsdeadsim'];
// > 0 in every one of Dalexlol's stress runs (PR #324: al355, al358); ghost / overwatch / dream-node were "up to", outfitwaitsaved al359 only: reported
export const REQUIRED = ['errortrapsims_dropped', 'errortraplookup_dropped', 'errortrapctl_dropped', 'celebritydeadsim_unregistered'];
export const COUNTERS = [...REQUIRED, 'errortrapsims_left', 'ghostreactdeadsim_removed', 'dreamnodedeadsim_removed', 'overwatchdeadsim_removed', 'outfitwaitsaved_destroyed',
  'seasonsdeadsim_dead', 'seasonsdeadsim_replaced', 'casuihandlers_closed', 'casuihandlers_dups', 'simrefsdeadsim_talkingto', 'simrefsdeadsim_pusher', 'simrefsdeadsim_reactors', 'simrefsdeadsim_occultowners'];
export const KEPT = { 934: 'dreamnode_hhkept', 935: 'overwatch_hhkept', 936: 'outfitwaitsaved_hhkept' } as const;

type Snap = { patches: any; kept: Record<string, number | null>; hh: { id: string; members: number } | null };
// every FAIL reason for one snapshot (final: the end-of-run counter rules too)
export function judge(on: boolean, s: Snap, hh0: Snap['hh'], final: boolean): string[] {
  const f: string[] = [], p = s.patches;
  if (!p) return ['no patches reply'];
  for (const n of UNDER_TEST) if (p[n] !== on) f.push(`switch ${n}=${p[n]} (want ${on})`);
  for (const n of STAY_OFF) if (p[n] !== false) f.push(`switch ${n}=${p[n]} (must stay off)`);
  for (const l of [].concat(p.applied ?? []) as string[]) if (/^(errortrap|celebrity|seasons|ghostreact|casui|dreamnode|overwatch|simrefs|outfitwaitsaved)[a-z]*: .*(threw|differs|not loaded)/.test(String(l))) f.push(`applied: ${l}`);
  if (!s.hh || !hh0 || s.hh.id !== hh0.id || s.hh.members !== hh0.members) f.push(`active household changed: ${JSON.stringify(hh0)} -> ${JSON.stringify(s.hh)}`);
  if (on) for (const [k, v] of Object.entries(s.kept)) if (!(typeof v === 'number' && v >= 0)) f.push(`${k} unreadable`);
  if (final && on) for (const k of REQUIRED) if (!(p[k] > 0)) f.push(`${k}=${p[k]} (want > 0)`);
  if (final && !on) for (const k of COUNTERS) if (p[k] > 0) f.push(`${k}=${p[k]} with the switches off`);
  return f;
}

const { values } = parseArgs({ options: {
  expect: { type: 'string' }, trips: { type: 'string', default: '3' }, 'stress-min': { type: 'string', default: '5' },
  seed: { type: 'string', default: '12351' }, max: { type: 'string', default: '30' }, port: { type: 'string', default: '47891' },
  'self-check': { type: 'boolean' },
} });

if (values['self-check']) {
  const sw = (on: boolean) => Object.fromEntries([...UNDER_TEST.map(n => [n, on]), ...STAY_OFF.map(n => [n, false])]);
  const hh = { id: '0x1', members: 4 }, kept = { dreamnode_hhkept: 0, overwatch_hhkept: 2, outfitwaitsaved_hhkept: 0 };
  const req = Object.fromEntries(REQUIRED.map(k => [k, 5]));
  assert.deepEqual(judge(true, { patches: { ...sw(true), ...req, applied: ['errortrapsims: ok'] }, kept, hh }, hh, true), []);
  assert.match(judge(true, { patches: { ...sw(true), ...req, errortrapctl: false }, kept, hh }, hh, false)[0], /switch errortrapctl=false/);
  assert.match(judge(true, { patches: { ...sw(true), ...req, simrefsdeadsim: true }, kept, hh }, hh, false)[0], /must stay off/);
  assert.match(judge(true, { patches: { ...sw(true), ...req, applied: ['overwatchdeadsim: pass threw x'] }, kept, hh }, hh, false)[0], /applied: overwatchdeadsim/);
  assert.deepEqual(judge(true, { patches: { ...sw(true), applied: ['errortrapspread: ok'] }, kept, hh }, hh, false), []); // not one of ours; mid-run zeros are fine
  assert.match(judge(true, { patches: sw(true), kept, hh }, hh, true).join(), /errortrapsims_dropped=undefined \(want > 0\)/);
  assert.match(judge(true, { patches: { ...sw(true), ...req }, kept: { ...kept, overwatch_hhkept: null }, hh }, hh, true)[0], /overwatch_hhkept unreadable/);
  assert.match(judge(true, { patches: { ...sw(true), ...req }, kept, hh: { id: '0x1', members: 3 } }, hh, false)[0], /active household changed/);
  assert.deepEqual(judge(false, { patches: sw(false), kept: { dreamnode_hhkept: null, overwatch_hhkept: null, outfitwaitsaved_hhkept: null }, hh }, hh, true), []);
  assert.match(judge(false, { patches: { ...sw(false), errortrapctl_dropped: 3 }, kept, hh }, hh, true)[0], /errortrapctl_dropped=3 with the switches off/);
  console.log('deadsim-stress self-check: ok');
  process.exit(0);
}
if (values.expect !== 'on' && values.expect !== 'off') { console.error('--expect on|off'); process.exit(2); }
const ON = values.expect === 'on', TRIPS = Number(values.trips), STRESS_MS = Number(values['stress-min']) * 60000;
const ROOT = resolve(import.meta.dirname, '..', '..'), OUT = join(ROOT, 'probe-out', 'deadsim');
mkdirSync(OUT, { recursive: true });
const tag = `${values.expect}-${new Date().toISOString().replace(/[:.]/g, '-').slice(0, 19)}`;
const jsonl = join(OUT, `${tag}.jsonl`), startFile = join(OUT, `${tag}.start`), outFile = join(OUT, `${tag}.out`);
const base = `http://127.0.0.1:${values.port}`;
const sleep = (ms: number) => new Promise(r => setTimeout(r, ms));
const log = (m: string) => console.log(`${new Date().toISOString().slice(11, 19)} ${m}`);
async function get(path: string, ms = 30000): Promise<any> { return (await fetch(`${base}/${path}`, { signal: AbortSignal.timeout(ms) })).json(); }
async function ag(cmd: string, args?: object): Promise<any> {
  const q = new URLSearchParams({ cmd, timeout: '8000' });
  if (args) q.set('args', JSON.stringify(args));
  try { return await get(`agent?${q}`, 20000); } catch (e) { return { ok: false, error: String(e) }; }
}

async function snap(label: string): Promise<Snap> {
  const patches = (await ag('patches')).result ?? null;
  const kept: Record<string, number | null> = {};
  for (const [i, k] of Object.entries(KEPT)) { const r = await ag('reflect_get', { path: `Ts3Fixes.Fixes.sCountersCache[${i}]` }); kept[k] = r.ok && typeof r.result === 'number' ? r.result : null; }
  const hhs = (await ag('list_households')).result;
  const a = Array.isArray(hhs) ? hhs.find((h: any) => h.active) : null;
  const sims = (await ag('list_sims', { scope: 'world' })).result;
  const mem = await get('memory', 60000).catch(() => null);
  const row = { t: new Date().toISOString(), label, kept, hh: a ? { id: a.id, members: a.members } : null, simDescsInWorld: Array.isArray(sims) ? sims.length : null,
    mem: mem && { commitMB: mem.commitMB, freeMB: mem.freeMB, largestFreeMB: mem.largestFreeMB, managed: mem.monoHeapMB ?? mem.managedMB ?? null },
    counters: patches && Object.fromEntries(COUNTERS.map(k => [k, patches[k]])) };
  appendFileSync(jsonl, JSON.stringify(row) + '\n');
  log(`deadsim: ${label}: ${COUNTERS.map(k => `${k}=${patches?.[k]}`).join(' ')} ${Object.entries(kept).map(([k, v]) => `${k}=${v ?? '-'}`).join(' ')} simDescsInWorld=${row.simDescsInWorld} commitMB=${row.mem?.commitMB} largestFreeMB=${row.mem?.largestFreeMB} hh=${JSON.stringify(row.hh)}`);
  return { patches, kept, hh: row.hh };
}

// leak-stress-drip.py for one stress phase; done = the drip's stop line in the .out file, then a kill as the backstop
async function stress(doneMs: number) {
  writeFileSync(outFile, '');
  closeSync(openSync(startFile, 'a'));
  const t = (Date.now() - doneMs) / 1000; utimesSync(startFile, t, t); // schedule continues from earlier phases
  const py = spawn('python', ['-I', join(ROOT, 'tools', 'harness', 'leak-stress-drip.py'), startFile, outFile, '--start', '0', '--seed', values.seed!, '--max', values.max!], { stdio: ['ignore', 'pipe', 'pipe'] });
  py.stdout.on('data', d => process.stdout.write(`  drip: ${d}`)); py.stderr.on('data', d => process.stdout.write(`  drip ERR: ${d}`));
  await ag('autopilot', { enabled: true, speed: 'none', dialogs: true });
  await ag('set_speed', { speed: 'normal' });
  await sleep(STRESS_MS);
  appendFileSync(outFile, `${tag} done\n`);
  const t0 = Date.now();
  while (py.exitCode === null && Date.now() - t0 < 60000) await sleep(1000);
  if (py.exitCode === null) { log('drip did not stop within 60 s: killed'); spawnSync('taskkill', ['/T', '/F', '/PID', String(py.pid)]); }
  await ag('set_speed', { speed: 'pause' });
}

const fails: string[] = [];
const s0 = await snap('start');
fails.push(...judge(ON, s0, s0.hh, false).map(x => `start: ${x}`));
if (fails.length) { log(`FAIL deadsim: ${fails.join('; ')}`); process.exit(1); } // wrong package or switches: nothing to measure
let last = s0;
for (let t = 1; t <= TRIPS; t++) {
  log(`deadsim: trip ${t}/${TRIPS}: stress ${values['stress-min']} min`);
  await stress((t - 1) * STRESS_MS);
  const r = spawnSync(process.execPath, [join(ROOT, 'tools', 'harness', 'save-matrix.ts'), '--travel', '--cases', 'travel', '--to', 'france', '--trips', '1', '--mem-marks', '--port', values.port!], { stdio: 'inherit', timeout: 30 * 60000 });
  if (r.status !== 0) { fails.push(`trip ${t}: save-matrix travel exit ${r.status ?? r.signal}`); break; }
  last = await snap(`trip ${t}`);
  const f = judge(ON, last, s0.hh, t === TRIPS);
  fails.push(...f.map(x => `trip ${t}: ${x}`));
  if (f.some(x => /^switch|applied|household/.test(x))) break;
}
log(`deadsim: kept by the household rule ${Object.entries(last.kept).map(([k, v]) => `${k}=${v ?? '-'}`).join(' ')}; rows ${jsonl}`);
if (fails.length) { log(`FAIL deadsim: ${fails.join('; ')}`); process.exit(1); }
log(`PASS deadsim ${values.expect}: ${TRIPS} trips, switches ${ON ? 'on' : 'off'} read back${ON ? `, ${REQUIRED.join(', ')} > 0` : ', counters 0'}, active household unchanged`);
