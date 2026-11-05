// Quiet save-reload measurement (docs/204 §2): does unloading the world and loading the same save again IN THE SAME PROCESS
// give back the address space a full restart gives back? Correctness job: phase timers and memory reads only, no profiler.
//   node tools/harness/save-reload.ts --world BP [--play 25] [--after 10] [--no-visits] [--no-direct] [--name SaveReload-BP]
//     needs a world in live mode (queue.ts loaded the Baseline save; it is only read). Steps:
//     1. realistic autopilot play --play min (x3, dialogs cleared, requests declined); a Buy mode and a CAS visit at 1/3 and
//        2/3 of the time (the store-cas-browse sequence: the game's own panel calls, nothing bought or kept)
//     2. mark sr-pre; (a) normal save: save_game --name (a new file: the Baseline is never written); mark sr-save
//     3. (b) quiet reload, menu route: save_game (overwrites --name), main_menu (EA's Quit to Main Menu without the dialog),
//        load_save --name from the menu; mark sr-reload; play --after min; mark sr-reload10
//     4. (b2, unless --no-direct) direct route: save_game, load_save reload:true (EA's in-game Load = ReloadWorld state, no menu
//        step); mark sr-direct; play --after min; mark sr-direct10
//   node tools/harness/save-reload.ts --control --world BP [--after 10]
//     (d) full-restart reference: queue.ts started the game fresh and loaded --name; mark sr-ctl; play --after min; mark sr-ctl10;
//     restart ms = now - TS3W.exe start (upper bound: includes the queue's menu wait and settle)
//   node tools/harness/save-reload.ts --self-check
// Marks: agent mem_mark (one "diag: mem #N mark ev=<tag>" line in ts3native.log, needs [MemDiag] Enabled=1 + [UserDiag]) plus the
// probe's /memory (commit, private, largest free, mapped, Boehm heap). Output probe-out/save-reload/<world>-<mode>-<UTC>.json;
// node tools/harness/save-reload-report.ts compares the play and control files per world. Windows-only by nature; times UTC.
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync, openSync, readSync, closeSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { parseArgs } from 'node:util';
import { stallGuard } from './game-alive.ts';
import { parseLog } from '../mem-report.ts';
import { BIN, GAME_NAME } from './game-exe.ts';

const ROOT = resolve(import.meta.dirname, '..', '..');
const LOG = join(BIN, 'ts3native.log');
export const OUT = join(ROOT, 'probe-out', 'save-reload');

// ---------------------------------------------------------------- pure parts (covered by --self-check)

// [UserDiag] "save: end #N result R after X ms" lines in a log slice (the native save time; result 0 = the game saved, every good save in the logs)
export function saveEnds(text: string): { n: number; result: number; ms: number }[] {
  return [...text.matchAll(/^save: end #(\d+) result (\d+) after (\d+) ms/gm)].map(m => ({ n: +m[1], result: +m[2], ms: +m[3] }));
}
// the diag: mem fields of the last mark sample with this tag (mem_mark logs "diag: mem #N mark ev=<tag>")
export function markFields(logText: string, tag: string): Record<string, number | null> | null {
  return parseLog(logText).samples.filter(s => s.tag === 'mark' && s.ev === tag).at(-1)?.v ?? null;
}
export const scratchOk = (name: string) => /^[\w.-]+$/.test(name) && !/^baseline-/i.test(name);

function selfCheck() {
  const log = 'ts3native 0.36.118 2026-10-30 01:00:00\nsave: end #1 result 1 after 8123 ms\nx\nsave: end #2 result 0 after 99 ms\n'
    + 'diag: mem #4 mark ev=sr-pre priv=1900 big=600? rc=300? t=60\ndiag: mem #5 mark ev=sr-save priv=1910 big=590 t=90\n'
    + 'diag: memsus #4 mark ev=sr-pre cc=3 lag=10 t=61\ndiag: mem #9 mark ev=sr-pre priv=1950 big=580 t=200\n';
  assert.deepEqual(saveEnds(log), [{ n: 1, result: 1, ms: 8123 }, { n: 2, result: 0, ms: 99 }]);
  assert.deepEqual(markFields(log, 'sr-pre'), { priv: 1950, big: 580 }); // the last sample with that tag wins
  assert.equal(markFields(log, 'sr-save')?.big, 590);
  assert.equal(markFields(log, 'sr-none'), null);
  assert.ok(scratchOk('SaveReload-BP') && !scratchOk('Baseline-BP-Spring') && !scratchOk('a b'));
  const pk = peakOf({ largestFreeMB: 900, commitMB: 2000 }, [{ largestFreeMB: 880, commitMB: 2050, freeMB: 1500 }, { largestFreeMB: 640, commitMB: 2300, freeMB: 1300 }, null]);
  assert.deepEqual([pk.reads, pk.minBig, pk.bigNeedMB, pk.minFree, pk.commitPeakMB], [3, 640, 260, 1300, 300]);
  assert.equal(peakOf(null, []).bigNeedMB, null);
  console.log('save-reload self-check ok');
}

// ---------------------------------------------------------------- game side

const { values } = parseArgs({ options: {
  world: { type: 'string' }, play: { type: 'string', default: '25' }, after: { type: 'string', default: '10' }, name: { type: 'string' },
  control: { type: 'boolean' }, 'no-visits': { type: 'boolean' }, 'no-direct': { type: 'boolean' }, port: { type: 'string', default: '47891' },
  cycles: { type: 'string', default: '1' }, // menu-route reloads in a row (memory stress; marks sr-reload, sr-reload-2 ...)
  'self-check': { type: 'boolean' },
} });
if (values['self-check']) { selfCheck(); process.exit(0); }
if (!values.world || !/^\w+$/.test(values.world)) { console.error('usage: save-reload.ts --world BP [--control] [--play 25] [--after 10] [--name SaveReload-BP] | --self-check'); process.exit(2); }
const NAME = values.name ?? `SaveReload-${values.world}`;
if (!scratchOk(NAME)) { console.error(`--name ${NAME}: letters, digits, . _ - and never Baseline-*`); process.exit(2); }
const PORT = Number(values.port), base = `http://127.0.0.1:${PORT}`;
const sleep = (ms: number) => new Promise(r => setTimeout(r, ms));
const log = (m: string) => console.log(`${new Date().toISOString().slice(11, 19)} ${m}`);
const ag = async (cmd: string, args?: object): Promise<any> => {
  const q = new URLSearchParams({ cmd, timeout: '8000' });
  if (args) q.set('args', JSON.stringify(args));
  try { return await (await fetch(`${base}/agent?${q}`, { signal: AbortSignal.timeout(20000) })).json(); } catch (e) { return { ok: false, error: String(e) }; }
};
const get = async (p: string) => { try { return await (await fetch(`${base}/${p}`, { signal: AbortSignal.timeout(20000) })).json(); } catch { return null; } };
const logSize = () => (existsSync(LOG) ? statSync(LOG).size : 0);
const logFrom = (off: number) => {
  if (!existsSync(LOG)) return '';
  const fd = openSync(LOG, 'r'), n = Math.max(0, statSync(LOG).size - off), b = Buffer.alloc(n);
  readSync(fd, b, 0, n, off); closeSync(fd);
  return b.toString('latin1');
};

const out: any = { world: values.world, mode: values.control ? 'control' : 'play', save: NAME, started: new Date().toISOString(), phases: {}, saves: [], marks: {}, visits: [], errors: [] };
const file = join(OUT, `${values.world}-${out.mode}-${out.started.replace(/[:.]/g, '').slice(0, 15)}Z.json`);
const flush = () => { mkdirSync(OUT, { recursive: true }); writeFileSync(file, JSON.stringify(out, null, 1)); };

// wait until cond() is true; fail-fast guard every 30 s (2 misses = fail), cap capS
async function until(what: string, capS: number, cond: () => Promise<boolean>, everyMs = 500) {
  const g = stallGuard(what, { port: PORT }), t0 = Date.now();
  for (;;) {
    if (await cond()) return Date.now() - t0;
    const why = await g();
    if (why) throw new Error(why);
    if (Date.now() - t0 > capS * 1000) throw new Error(`${what}: not done after ${capS} s`);
    await sleep(everyMs);
  }
}
// Steam GC_black_list_spacing 0x0119482C and the [GC] BlackListCapKB counters (docs/232 Run 8q); nulls when unreadable
async function gcState() {
  const rd = await get('read?addr=0x0119482C&len=4'), n = await get('native');
  const keys = ['gcCapKB', 'gcStrictN', 'gcLenientN', 'gcSwitchSec', 'gcBlackOk', 'gcExpandStreakMax', 'gwExpands', 'gwKB'];
  return { spacingKB: rd?.hex ? Buffer.from(rd.hex, 'hex').readUInt32LE(0) >> 10 : null, ...Object.fromEntries(keys.map(k => [k, n?.[k] ?? null])) };
}
async function mark(tag: string) {
  const r = await ag('mem_mark', { tag });
  if (!r.ok || !r.result?.accepted) throw new Error(`mem_mark ${tag}: ${r.error ?? 'not accepted ([MemDiag] / [UserDiag] off?)'}`);
  out.marks[tag] = { at: new Date().toISOString(), memory: await get('memory'), gc: await gcState() };
  const m = out.marks[tag].memory, g = out.marks[tag].gc;
  log(`mark ${tag}: commit ${m?.commitMB} private ${m?.privateMB} largest free ${m?.largestFreeMB} MB; bl spacing ${g.spacingKB} KB, strict/lenient ${g.gcStrictN}/${g.gcLenientN}, black ok ${g.gcBlackOk}, streak max ${g.gcExpandStreakMax}`);
  flush();
}
// realistic play: autopilot x3 (dialogs cleared), requests declined every 30 s; Buy + CAS visits at the given minutes
async function play(min: number, visitsAt: number[] = []) {
  await ag('autopilot', { enabled: true, speed: 'ultra', dialogs: true });
  const g = stallGuard(`play ${min} min`, { port: PORT }), t0 = Date.now(), todo = [...visitsAt];
  while (Date.now() - t0 < min * 60000) {
    const why = await g();
    if (why) throw new Error(why);
    if (todo.length && Date.now() - t0 >= todo[0] * 60000) { todo.shift(); await visit(); }
    await ag('decline_requests');
    await sleep(Math.min(30000, Math.max(1000, min * 60000 - (Date.now() - t0))));
  }
}
// the store-cas-browse sequence (repros.json): CAS open, 4 panels, discard close; Buy open, 3 categories, close. A failed step is
// recorded and the visit goes on (the measurement is the memory around the reload, not the visit).
async function visit() {
  const steps: [string, object | undefined, number][] = [['cas_open', undefined, 30], ['cas_category', { category: 'hair' }, 10],
    ['cas_category', { category: 'everyday' }, 10], ['cas_category', { category: 'tops' }, 10], ['cas_category', { category: 'basics' }, 10],
    ['cas_close', undefined, 20], ['buy_open', undefined, 15], ['buy_category', { category: 'Comfort' }, 10],
    ['buy_category', { category: 'Surfaces' }, 10], ['buy_category', { category: 'Decor' }, 10], ['buy_close', undefined, 15]];
  const v = { at: new Date().toISOString(), failed: [] as string[] };
  for (const [cmd, args, waitS] of steps) {
    const r = await ag(cmd, args);
    if (!r.ok) v.failed.push(`${cmd}: ${r.error}`);
    await sleep(waitS * 1000);
  }
  const st = await ag('game_state');
  if (st.ok && st.result?.sub_state !== 'LiveModeState') { v.failed.push(`left in ${st.result?.sub_state}`); await ag('enter_live_mode'); await sleep(10000); }
  out.visits.push(v); flush();
  log(`visit: ${v.failed.length ? v.failed.join('; ') : 'CAS + Buy ok'}`);
}
// address-space peak of a phase (docs/204 §3 threshold): the probe's /memory read back to back (the probe walks the game from
// outside, so it reads while the game thread is busy saving or loading; ~0.3-1 s per read [inferred], so a peak shorter than
// that can be missed: a lower bound of the need). need = largest free before - lowest largest free during.
export function peakOf(before: any, rows: any[]) {
  const big = rows.map(r => r?.largestFreeMB).filter((x): x is number => typeof x === 'number');
  const com = rows.map(r => r?.commitMB).filter((x): x is number => typeof x === 'number');
  const free = rows.map(r => r?.freeMB).filter((x): x is number => typeof x === 'number');
  const minBig = big.length ? Math.min(...big) : null, maxCom = com.length ? Math.max(...com) : null;
  return { reads: rows.length, bigBefore: before?.largestFreeMB ?? null, minBig, bigNeedMB: minBig !== null && typeof before?.largestFreeMB === 'number' ? before.largestFreeMB - minBig : null,
    minFree: free.length ? Math.min(...free) : null, commitBefore: before?.commitMB ?? null, maxCommit: maxCom,
    commitPeakMB: maxCom !== null && typeof before?.commitMB === 'number' ? maxCom - before.commitMB : null };
}
async function sampler() {
  const before = await get('memory'), rows: any[] = [];
  let on = true;
  const loop = (async () => { while (on) { const m = await get('memory'); if (m) rows.push(m); await sleep(200); } })();
  return async () => { on = false; await loop; return peakOf(before, rows); };
}

// one save, timed: wall ms from save_game to the native "save: end" line (or save_in_progress seen true, then false), native ms from the log
async function save(label: string) {
  await ag('autopilot', { enabled: false }); // it would unpause during the save
  await ag('set_speed', { speed: 'pause' });
  for (let i = 0; i < 5 && (await ag('auto_dialog')).result?.open; i++) await sleep(1500);
  const off = logSize();
  const stop = await sampler();
  const t0 = Date.now(), r = await ag('save_game', { name: NAME }); // always by name: after a load SaveName can be empty (EA then opens the Save As dialog); the same name overwrites, as EA's AutoSave does
  if (!r.ok) throw new Error(`save ${label}: ${r.error}`);
  let seen = false;
  const wallMs = await until(`save ${label}`, 900, async () => {
    const st = await ag('game_state');
    if (st.ok && st.result.save_in_progress) seen = true;
    // the sim thread answers nothing while it saves, so a poll rarely lands with save_in_progress true: the native
    // "save: end" line in the log slice is the sure sign (bp-save-reload 2026-11-02 05:24 waited 900 s on a 7 s save).
    // The agent's async_log line is not: its task can finish before the game's save state runs (08:41: wall 49 ms on a
    // 9.7 s save, then the mem_mark hit the busy sim thread), so it is not read here.
    return saveEnds(logFrom(off)).length > 0 || (seen && st.ok && !st.result.save_in_progress);
  });
  await sleep(2000); // the save: end line follows the script side by a moment
  const peak = await stop();
  const end = saveEnds(logFrom(off)).at(-1) ?? null;
  out.saves.push({ label, wallMs, nativeMs: end?.ms ?? null, result: end?.result ?? null, peak });
  log(`save ${label}: wall ${wallMs} ms, native ${end?.ms ?? '?'} ms, result ${end?.result ?? '?'}; largest free ${peak.bigBefore} -> low ${peak.minBig} MB (need ${peak.bigNeedMB}), commit peak +${peak.commitPeakMB} MB, ${peak.reads} reads`);
  // 0 = saved: the game's save returns an error code (saveslim.cpp SlimStart "0 = saved"; 150 of 150 "save: end" lines in
  // probe-out/queue logs say 0, 2026-11-03). The old check wanted 1 and failed every save.
  if (end && end.result !== 0) throw new Error(`save ${label}: native result ${end.result} (0 = saved)`);
  out.phases[`${label}Ms`] = Date.now() - t0;
  flush();
}
// loaded = the agent's world flag set (OnWorldLoadFinished) and live mode
const inLive = async () => (await get('agentstatus'))?.worldLoaded === true && (await ag('game_state')).result?.sub_state === 'LiveModeState';

try {
  const st = await ag('game_state');
  if (!st.ok || st.result.sub_state !== 'LiveModeState') throw new Error(`needs a world in live mode (${st.error ?? st.result?.sub_state})`);
  if (values.control) {
    if (!String(st.result.load_file ?? '').includes(NAME)) throw new Error(`control needs ${NAME} loaded, not ${st.result.load_file}`);
    try {
      const startedAt = execFileSync('powershell', ['-NoProfile', '-Command', `(Get-Process ${GAME_NAME}).StartTime.ToUniversalTime().ToString('o')`], { timeout: 15000 }).toString().trim();
      out.phases.restartMs = Date.now() - Date.parse(startedAt);
    } catch (e) { out.errors.push(`process start time: ${String(e).split('\n')[0]}`); }
    await mark('sr-ctl');
    await play(Number(values.after));
    await mark('sr-ctl10');
  } else {
    const P = Number(values.play);
    await play(P, values['no-visits'] ? [] : [P / 3, (2 * P) / 3]);
    await mark('sr-pre');
    await save('normal'); // (a): a new scratch file; every later save overwrites it
    await mark('sr-save');
    // (b) menu route, --cycles times (cycle 1 keeps the old phase keys and marks)
    for (let c = 1; c <= Number(values.cycles); c++) {
      const sfx = c === 1 ? '' : `-${c}`;
      const tc = Date.now();
      await save(`reloadSave${sfx}`);
      const stopM = await sampler();
      const r1 = await ag('main_menu');
      if (!r1.ok) throw new Error(`main_menu: ${r1.error}`);
      out.phases[`menuMs${sfx}`] = await until('unload to the main menu', 600, async () => (await get('agentstatus'))?.worldLoaded === false && (await ag('game_state')).result?.in_world === false);
      let tl = Date.now(), lr = await ag('load_save', { file: `${NAME}.sims3` });
      for (let i = 0; !lr.ok && /already in a world|no reply/.test(lr.error ?? '') && i < 20; i++) { await sleep(3000); lr = await ag('load_save', { file: `${NAME}.sims3` }); }
      if (!lr.ok) throw new Error(`load_save: ${lr.error}`);
      out.phases[`menuToLoadMs${sfx}`] = Date.now() - tl;
      tl = Date.now();
      out.phases[`loadMs${sfx}`] = await until('reload', 900, inLive, 1000);
      out.phases[`reloadCycleMs${sfx}`] = Date.now() - tc;
      (out.reloadPeak ??= {})[`menu${sfx}`] = await stopM(); // unload + load: the compaction itself must not run out of address space
      log(`menu route${sfx}: unload ${out.phases[`menuMs${sfx}`]} ms, load ${out.phases[`loadMs${sfx}`]} ms, cycle ${out.phases[`reloadCycleMs${sfx}`]} ms (save included)`);
      await ag('quiet_ui', { memories_off: true });
      await mark(`sr-reload${sfx}`);
      await play(Number(values.after));
      await mark(`sr-reload10${sfx}`);
    }
    if (!values['no-direct']) { // (b2) EA's in-game Load (ReloadWorld): unload and load with no main menu step
      const td = Date.now();
      await save('directSave');
      const stopD = await sampler();
      const r2 = await ag('load_save', { file: `${NAME}.sims3`, reload: true });
      if (!r2.ok) throw new Error(`load_save reload: ${r2.error}`);
      await until('direct reload: world unloading', 300, async () => (await get('agentstatus'))?.worldLoaded === false, 250);
      out.phases.directLoadMs = await until('direct reload', 900, inLive, 1000);
      out.phases.directCycleMs = Date.now() - td;
      out.reloadPeak.direct = await stopD();
      log(`direct route: cycle ${out.phases.directCycleMs} ms (save included)`);
      await mark('sr-direct');
      await play(Number(values.after));
      await mark('sr-direct10');
    }
  }
} catch (e) {
  out.errors.push(String(e).split('\n')[0]);
  log(`FAIL: ${out.errors.at(-1)}`);
  process.exitCode = 1;
}
// the diag: mem fields per mark (rc, gcHeap, pools, ...); memsus answers lag a few seconds
await sleep(15000);
const text = existsSync(LOG) ? readFileSync(LOG, 'latin1') : '';
for (const tag of Object.keys(out.marks)) out.marks[tag].diag = markFields(text, tag);
out.ended = new Date().toISOString();
flush();
log(`${process.exitCode ? 'FAIL' : 'PASS'}: ${file}`);
process.exit(process.exitCode ?? 0); // a save that threw leaves its sampler running; node would sit until the queue gives up (bp-save-reload 2026-11-02 sat 33 min)
