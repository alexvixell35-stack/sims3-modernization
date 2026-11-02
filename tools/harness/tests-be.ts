// Baseline tests B-E (docs/02 section 1) on a loaded world, measured instead of by stopwatch:
//   node tools/harness/tests-be.ts --tag sv-dxvk [--only B,C,D,E] [--minutes 30] [--lot "Name"] [--dry-run]
//   node tools/harness/tests-be.ts --self-check            (parsing and statistics only; no game)
// Needs the probe server (ts3probe.py serve) and a world in live mode (cycle.ts --save X). Runs at speed 3 with autopilot.
//   B  frame times: 60 s at speed 3, camera untouched. Hooks the per-frame function 0xEC9F00 (tools/harness/frame-times.js,
//      own Frida session, CModule hook: no JS lock on the render thread, no thread suspended) and runs the sim-thread stutter
//      profiler (tools/frida/stutter-profile.js) alongside: FPS, frame time p50/p95/p99/p99.9/max, frames > 33/50/100 ms per
//      minute, and which long frames overlap a sim-thread hitch (same QPC clock). Without the profiler (S3PP
//      SimulatorStageProfiler on, or the probe started with --profile) B still reports frames, hitches null.
//   C  travel hitch: the active Sim visits a community lot (--lot, default the one with most Sims on it) and goes home,
//      frames recorded throughout; per leg the trip time, frame stats and the worst frames around the arrival.
//   D  memory: /memory (commit, private, largest free block) plus the process's private bytes (Task Manager "Commit size")
//      at 0, 1/3, 2/3 and all of --minutes at speed 3, with keep_alive / decline_requests every 30 s.
//   E  save time: save_game as TestE-<tag> (never overwrites the loaded save) until save_in_progress is false; save size.
// Output: probe-out/tests/<tag>-<B|C|D|E>.json (+ <tag>-<B|C>-frames.csv) and one summary line per test.
import { execFileSync, spawn } from 'node:child_process';
import { existsSync, mkdirSync, readdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { join, resolve } from 'node:path';
import { parseArgs } from 'node:util';
import assert from 'node:assert/strict';
import { focusGame } from './focus-game.ts';
import { GAME_EXE, GAME_NAME } from './game-exe.ts';
import { GUARD_MS, watchGame } from './game-alive.ts';

const ROOT = resolve(import.meta.dirname, '..', '..');
const OUT = join(ROOT, 'probe-out', 'tests');
const SAVES = join(process.env.TS3_USER ?? join(homedir(), 'Documents', 'Electronic Arts', 'The Sims 3'), 'Saves');
const SLOW_MS = 50, LONG_MS = [33, 50, 100], B_SECONDS = 60, TRIP_TIMEOUT_S = 300, SAVE_TIMEOUT_S = 600;

// ---- parsing and statistics (covered by --self-check) ----
// t = epoch ms, q = QPC ms at the frame's start (frame-times.js); the frame spans [q - ms, q].
type Frame = { t: number; q: number; ms: number; work: number; fg: number };
export function parseFrames(csv: string): Frame[] {
  const [head, ...rows] = csv.split('\n');
  const col = Object.fromEntries(head.trim().split(',').map((h, i) => [h, i]));
  return rows.filter(l => l.trim()).map(l => {
    const v = l.split(',').map(Number);
    return { t: v[col.epochMs], q: v[col.qpcMs], ms: v[col.frameMs], work: v[col.workMs], fg: v[col.fg] };
  }).filter(f => Number.isFinite(f.t) && Number.isFinite(f.ms));
}
export function frameStats(frames: Frame[]) {
  if (!frames.length) return null;
  const ms = frames.map(f => f.ms).sort((a, b) => a - b);
  const pct = (p: number) => ms[Math.max(0, Math.ceil(p * ms.length) - 1)]; // nearest rank
  const secs = ms.reduce((a, b) => a + b, 0) / 1000;
  const slow = ms.filter(x => x > SLOW_MS).length;
  const r = (x: number) => Math.round(x * 100) / 100;
  return { frames: ms.length, seconds: r(secs), fps: r(ms.length / secs), medianMs: pct(0.5), p95Ms: pct(0.95), p99Ms: pct(0.99),
    p999Ms: pct(0.999), maxMs: ms.at(-1)!, slowFrames: slow, slowPerSec: r(slow / secs),
    longPerMin: Object.fromEntries(LONG_MS.map(l => [`over${l}`, r(ms.filter(x => x > l).length / secs * 60)])),
    focusedShare: r(frames.filter(f => f.fg).length / frames.length) };
}
// Long frames (> SLOW_MS) against sim-thread hitches from stutter-profile.js (t = QPC ms at the iteration's end, wallMs).
// A frame and a hitch correlate when their intervals overlap.
export function correlate(frames: Frame[], hitches: { t: number; wallMs: number }[]) {
  const long = frames.filter(f => f.ms > SLOW_MS && Number.isFinite(f.q));
  const hit = (f: Frame, h: { t: number; wallMs: number }) => h.t - h.wallMs < f.q && h.t > f.q - f.ms;
  const both = long.filter(f => hitches.some(h => hit(f, h)));
  return { longFrames: long.length, withSimHitch: both.length, simHitches: hitches.length,
    simHitchesWithLongFrame: hitches.filter(h => long.some(f => hit(f, h))).length,
    worst: long.sort((a, b) => b.ms - a.ms).slice(0, 10).map(f => ({ ms: f.ms, at: new Date(f.t).toISOString().slice(11, 23),
      simHitchMs: hitches.filter(h => hit(f, h)).map(h => h.wallMs) })) };
}
// Busiest community lot by Sims on it, not the lot the active Sim stands on.
export function pickLot(lots: { name: string; type: string }[], sims: { lot: string | null }[], exclude: string | null) {
  const count = (n: string) => sims.filter(s => s.lot === n).length;
  return lots.filter(l => l.type.startsWith('community') && l.name !== exclude)
    .reduce<{ name: string; type: string } | null>((best, l) => !best || count(l.name) > count(best.name) ? l : best, null)?.name ?? null;
}

if (process.argv.includes('--self-check')) {
  const f = parseFrames('epochMs,qpcMs,frameMs,workMs,fg\n1000,500,16.5,2,1\n1016,560,60,3,1\n\n1076,576.7,16.7,2,0\nbad,line\n');
  assert.equal(f.length, 3);
  assert.deepEqual(f[1], { t: 1016, q: 560, ms: 60, work: 3, fg: 1 });
  const s = frameStats([10, 20, 30, 40, 100].map((ms, i) => ({ t: i, q: i, ms, work: 0, fg: i % 2 })))!;
  assert.equal(s.medianMs, 30); assert.equal(s.p95Ms, 100); assert.equal(s.p999Ms, 100); assert.equal(s.maxMs, 100);
  assert.equal(s.slowFrames, 1); assert.equal(s.seconds, 0.2); assert.equal(s.slowPerSec, 5); assert.equal(s.focusedShare, 0.4);
  assert.deepEqual(s.longPerMin, { over33: 600, over50: 300, over100: 0 });
  // frame [500, 560] overlaps the hitch [540, 600]; the hitch [100, 180] overlaps no long frame
  const c = correlate(f, [{ t: 600, wallMs: 60 }, { t: 180, wallMs: 80 }]);
  assert.equal(c.longFrames, 1); assert.equal(c.withSimHitch, 1); assert.equal(c.simHitches, 2); assert.equal(c.simHitchesWithLongFrame, 1);
  assert.deepEqual(c.worst[0].simHitchMs, [60]);
  assert.equal(frameStats([]), null);
  assert.equal(pickLot([{ name: 'Park', type: 'community:Park' }, { name: 'Gym', type: 'community:Gym' }, { name: 'Home', type: 'residential' }],
    [{ lot: 'Gym' }, { lot: 'Gym' }, { lot: 'Park' }, { lot: 'Home' }, { lot: 'Home' }, { lot: 'Home' }], null), 'Gym');
  assert.equal(pickLot([{ name: 'Gym', type: 'community:Gym' }], [], 'Gym'), null);
  console.log('self-check ok');
  process.exit(0);
}

const { values } = parseArgs({ options: { tag: { type: 'string' }, only: { type: 'string', default: 'B,C,D,E' },
  minutes: { type: 'string', default: '30' }, lot: { type: 'string' }, 'dry-run': { type: 'boolean' }, 'self-check': { type: 'boolean' } } });
if (!values.tag) throw new Error('--tag required');
const tag = values.tag, only = values.only!.toUpperCase().split(',').map(s => s.trim());
if (only.some(t => !'BCDE'.includes(t) || t.length !== 1)) throw new Error(`--only takes B,C,D,E, got ${values.only}`);
const minutes = Number(values.minutes);
if (!(minutes > 0)) throw new Error('--minutes must be > 0');
mkdirSync(OUT, { recursive: true });

const sleep = (ms: number) => new Promise(r => setTimeout(r, ms));
const stamp = () => new Date().toISOString().slice(11, 19);
const get = async (p: string, to = 20000) => (await fetch(`http://127.0.0.1:47891/${p}`, { signal: AbortSignal.timeout(to) })).json();
const ag = async (cmd: string, args?: object, to = 20000): Promise<any> => {
  const q = new URLSearchParams({ cmd, timeout: String(Math.min(8000, to)) });
  if (args) q.set('args', JSON.stringify(args));
  try { return await get(`agent?${q}`, to); } catch (e) { return { ok: false, error: String(e) }; }
};
const write = (test: string, data: object, summary: string) => {
  writeFileSync(join(OUT, `${tag}-${test}.json`), JSON.stringify({ tag, test, at: new Date().toISOString(), summary, ...data }, null, 1));
  console.log(`${stamp()} ${tag} ${test}: ${summary}`);
};

// Frame capture in its own Frida session, stopped by a line on stdin (EOF too, so a dying harness still unhooks cleanly).
const PY = `import frida, json, sys
s = frida.attach('${GAME_EXE}')
sc = s.create_script(open(sys.argv[1], encoding='utf-8').read())
sc.on('message', lambda m, d: print(m.get('payload', m), flush=True))
sc.load()
sc.exports_sync.init('late', json.loads(sys.argv[2]))
print('ready', flush=True)
sys.stdin.readline()
sc.unload()
s.detach()`;
async function capture(test: string) {
  const out = join(OUT, `${tag}-${test}-frames.csv`);
  const p = spawn('python', ['-c', PY, join(import.meta.dirname, 'frame-times.js'), JSON.stringify({ out: out.replace(/\\/g, '/') })],
    { stdio: ['pipe', 'pipe', 'pipe'] });
  let log = '';
  const done = new Promise(r => p.on('exit', r));
  await new Promise<void>((ok, fail) => {
    const to = setTimeout(() => fail(new Error(`frame hook not ready in 30 s: ${log}`)), 30000);
    const on = (d: Buffer) => { log += d; if (log.includes('ready')) { clearTimeout(to); ok(); } };
    p.stdout.on('data', on); p.stderr.on('data', on);
    p.on('exit', c => { clearTimeout(to); if (!log.includes('ready')) fail(new Error(`frame hook failed (exit ${c}): ${log.trim()}`)); });
  });
  return { stop: async () => { p.stdin.end('\n'); await done; return { frames: parseFrames(readFileSync(out, 'utf8')), log: log.trim() }; } };
}
const fmt = (s: ReturnType<typeof frameStats>) => s ? `${s.fps} FPS, p50 ${s.medianMs} / p95 ${s.p95Ms} / p99 ${s.p99Ms} / p99.9 ${s.p999Ms} / max ${s.maxMs} ms, ` +
  `per min ${LONG_MS.map(l => `>${l} ms ${s.longPerMin[`over${l}`]}`).join(' ')}, focused ${Math.round(s.focusedShare * 100)}%` : 'no frames';

if (values['dry-run']) {
  console.log(`dry run, tag ${tag}, tests ${only.join(',')}; nothing is sent to the game. Would:`);
  console.log(`  check game_state is LiveModeState; autopilot {enabled, speed ultra, dialogs}`);
  if (only.includes('B')) console.log(`  B: hook 0xEC9F00 via tools/harness/frame-times.js + stutter-profile.js for ${B_SECONDS} s -> ${tag}-B.json, ${tag}-B-frames.csv, ${tag}-B-sim-hitches.jsonl`);
  if (only.includes('C')) console.log(`  C: sim_visit_lot active Sim -> ${values.lot ?? 'busiest community lot'}, then sim_go_home (each <= ${TRIP_TIMEOUT_S} s), frames throughout -> ${tag}-C.json`);
  if (only.includes('D')) console.log(`  D: /memory + private bytes at ${[0, 1, 2, 3].map(i => Math.round(minutes * i / 3 * 10) / 10).join('/')} min, upkeep every 30 s -> ${tag}-D.json`);
  if (only.includes('E')) console.log(`  E: save_game {name: "TestE-${tag}"}, wait for save_in_progress false (<= ${SAVE_TIMEOUT_S} s), size of ${join(SAVES, `TestE-${tag}.sims3`)} -> ${tag}-E.json`);
  process.exit(0);
}

const st = await ag('game_state');
if (st.result?.sub_state !== 'LiveModeState') throw new Error(`not in live mode: ${JSON.stringify(st)}`);
console.log(`${stamp()} ${tag}: ${JSON.stringify(st.result)}`);
const alive = watchGame();
console.log(`${stamp()} game window ${focusGame() ? 'focused' : 'NOT focused (fps capped, B/C not comparable)'}`);
await ag('autopilot', { enabled: true, speed: 'ultra', dialogs: true });

if (only.includes('B')) {
  // Frame hook first, profiler second, and the profiler (stops itself after -t) must detach first: each python host
  // injects its own frida-agent, and when the first-loaded agent unloads before a later one, the later one's unload
  // kills the process (crashed the game at the end of B twice; dummy 32-bit process, two no-op scripts: FIFO detach
  // crashes every time, LIFO never, 2026-10-13).
  const cap = await capture('B');
  // sim-thread hitches on the same QPC clock, as profile-world.ts runs it; its own session, stops itself after -t
  const simOut = join(OUT, `${tag}-B-sim`);
  const prof = spawn('python', ['-m', 'frida_tools.repl', '-n', GAME_EXE, '-l', join(ROOT, 'tools', 'frida', 'stutter-profile.js'),
    '-P', JSON.stringify({ out: simOut.replace(/\\/g, '/'), hitchMs: SLOW_MS }), '-q', '-t', String(B_SECONDS + 5)], { stdio: ['ignore', 'pipe', 'pipe'] });
  let profLog = '';
  prof.stdout.on('data', d => profLog += d); prof.stderr.on('data', d => profLog += d);
  let profExited = false;
  const profDone = new Promise(r => prof.on('exit', () => { profExited = true; r(null); }));
  // fail fast: the profiler stops itself after -t; one still running 60 s past that is hung and would block forever
  setTimeout(() => { if (!profExited) { profLog += '\nNOT PROFILING: killed, still running 60 s past -t (fail-fast guard)\n'; prof.kill(); } }, (B_SECONDS + 5 + 30 + 60) * 1000).unref();
  for (const due = Date.now() + 30000; !profLog.includes('hooks installed') && !profExited && Date.now() < due;) await sleep(250);
  const t0 = Date.now(); // B window starts once the profiler is hooked: its attach hitches stay out of the stats
  await sleep(B_SECONDS * 1000);
  await profDone;
  const cap0 = await cap.stop();
  const frames = cap0.frames.filter(f => f.t >= t0 && f.t <= t0 + B_SECONDS * 1000), log = cap0.log;
  const s = frameStats(frames);
  const simOk = profLog.includes('hooks installed') && !profLog.includes('NOT PROFILING') && existsSync(`${simOut}-hitches.jsonl`);
  const hitches = simOk ? readFileSync(`${simOut}-hitches.jsonl`, 'utf8').split('\n').filter(l => l.trim()).map(l => JSON.parse(l)) : null;
  const corr = hitches ? correlate(frames, hitches) : null;
  write('B', { stats: s, correlation: corr, captureLog: log, simProfilerLog: profLog.trim().slice(-2000) }, fmt(s) + (corr
    ? `; long frames ${corr.longFrames}, ${corr.withSimHitch} overlap a sim hitch (${corr.simHitchesWithLongFrame} of ${corr.simHitches} sim hitches show as a long frame)`
    : '; sim hitches not recorded (stutter profiler did not start, see simProfilerLog)'));
}

if (only.includes('C')) {
  const sim = (await ag('tick_status')).result?.sim;
  const sims = (await ag('sims_activity')).result ?? [];
  const startLot = sims.find((x: any) => x.name === sim)?.lot ?? null;
  const target = values.lot ?? pickLot((await ag('list_lots')).result ?? [], sims, startLot);
  if (!sim || !target) write('C', { sim, target }, `skipped: ${!sim ? 'no active Sim' : 'no community lot'}`);
  else {
    const legs: any[] = [];
    const cap = await capture('C');
    for (const [leg, cmd] of [['out', 'sim_visit_lot'], ['home', 'sim_go_home']] as const) {
      const t0 = Date.now();
      const q = await ag(cmd, cmd === 'sim_visit_lot' ? { sim, lot: target } : { sim });
      const dest = cmd === 'sim_visit_lot' ? q.result?.lot : q.result?.home;
      if (!q.ok || !dest || q.result?.queued === false) { legs.push({ leg, error: q.error ?? `not queued: ${JSON.stringify(q.result)}` }); break; }
      let arrived = 0, idle = 0, nextCheck = Date.now() + GUARD_MS, dropped = '';
      while (!arrived && Date.now() - t0 < TRIP_TIMEOUT_S * 1000) {
        await sleep(2000); // shortcut: arrival known to ~2 s; the arrival window below is wider than that
        const me = ((await ag('sims_activity')).result ?? []).find((x: any) => x.name === sim);
        if (me?.lot === dest) arrived = Date.now();
        // fail fast: every 30 s the Sim must still have an interaction (the trip); none twice in a row (~1 min) = dropped
        else if (me && Date.now() >= nextCheck) {
          nextCheck = Date.now() + GUARD_MS;
          idle = me.interaction ? 0 : idle + 1;
          if (idle >= 2) { dropped = `trip dropped: ${sim} idle (no interaction) for ~${2 * GUARD_MS / 1000} s, not at ${dest} (fail-fast guard)`; break; }
        }
      }
      legs.push({ leg, cmd, dest, t0, arrived: arrived || null, tripS: arrived ? (arrived - t0) / 1000 : null, ...(dropped ? { error: dropped } : {}) });
      if (!arrived) break;
      await sleep(10000); // the lot settles: streaming hitches often land just after arrival
    }
    const { frames, log } = await cap.stop();
    for (const l of legs.filter(l => l.t0)) {
      const end = l.arrived ? l.arrived + 10000 : l.t0 + TRIP_TIMEOUT_S * 1000;
      l.stats = frameStats(frames.filter(f => f.t >= l.t0 && f.t <= end));
      if (l.arrived) {
        const near = frames.filter(f => Math.abs(f.t - l.arrived) <= 5000);
        l.arrivalSlowFrames = near.filter(f => f.ms > SLOW_MS).length;
        l.arrivalWorst = near.sort((a, b) => b.ms - a.ms).slice(0, 5).map(f => ({ ms: f.ms, sFromArrival: (f.t - l.arrived) / 1000 }));
      }
    }
    write('C', { sim, from: startLot, target, legs, captureLog: log }, legs.map(l => l.error ? `${l.leg}: ${l.error}` : !l.arrived
      ? `${l.leg} to ${l.dest}: not arrived in ${TRIP_TIMEOUT_S} s` : `${l.leg} to ${l.dest} ${l.tripS} s, worst at arrival ${l.arrivalWorst[0]?.ms ?? '-'} ms, ` +
      `${l.arrivalSlowFrames} frames > ${SLOW_MS} ms within 5 s, trip ${fmt(l.stats)}`).join(' | '));
  }
}

if (only.includes('D')) {
  // Task Manager's "Commit size" is the process's private bytes; /memory's commitMB also counts images and mapped files.
  const privateBytes = () => { try {
    return Math.round(Number(execFileSync('powershell', ['-NoProfile', '-Command', `(Get-Process '${GAME_NAME}').PrivateMemorySize64`], { timeout: 15000 }).toString().trim()) / 1048576);
  } catch { return null; } };
  const samples: any[] = [];
  const t0 = Date.now();
  for (let i = 0; i <= 3; i++) {
    const due = t0 + minutes * 60000 * i / 3;
    while (Date.now() < due - 1000) { // upkeep as in profile-world.ts, so the run keeps going at speed 3
      await ag('keep_alive', { threshold: -50 }); await ag('decline_requests'); await ag('autopilot');
      await sleep(Math.min(30000, Math.max(0, due - Date.now())));
    }
    let m: any = null;
    try { m = await get('memory', 60000); } catch (e) { m = { error: String(e) }; }
    const time = (await ag('time')).result;
    samples.push({ min: Math.round((Date.now() - t0) / 6000) / 10, commitMB: m.commitMB, privateMB: m.privateMB, largestFreeMB: m.largestFreeMB,
      freeMB: m.freeMB, reserveOnlyMB: m.reserveOnlyMB, privateBytesMB: privateBytes(), game: time ? `day ${time.day_number} ${time.clock} ${time.speed}` : null, error: m.error });
    console.log(`${stamp()} D ${JSON.stringify(samples.at(-1))}`);
  }
  write('D', { minutes, samples }, samples.map(s => `${s.min} min: commit ${s.commitMB} / private ${s.privateMB} / priv bytes ${s.privateBytesMB} / largest free ${s.largestFreeMB} MB`).join(' | '));
}

if (only.includes('E')) {
  const name = `TestE-${tag}`, dir = join(SAVES, `${name}.sims3`);
  const t0 = Date.now();
  const q = await ag('save_game', { name });
  let sawSaving = false, done = 0, unanswered = 0, idle = 0, nextCheck = Date.now() + GUARD_MS, never = '';
  while (q.ok && !done && Date.now() - t0 < SAVE_TIMEOUT_S * 1000) {
    await sleep(250);
    const s = await ag('game_state', undefined, 5000);
    if (!s.ok) { unanswered++; continue; } // the sim thread is busy saving
    if (s.result.save_in_progress) sawSaving = true;
    // shortcut: a save quicker than one poll is caught by the file being written after t0
    else if (sawSaving || (existsSync(dir) && statSync(dir).mtimeMs > t0)) done = Date.now();
    // fail fast: answering, not saving, nothing written, at two checks ~30 s apart = the save never started
    else if (Date.now() >= nextCheck && ++idle >= 2) { never = `save never started: not in progress and no file written for ~${2 * GUARD_MS / 1000} s (fail-fast guard)`; break; }
    if (Date.now() >= nextCheck) nextCheck = Date.now() + GUARD_MS;
  }
  const files = existsSync(dir) ? readdirSync(dir).map(f => statSync(join(dir, f))) : [];
  const bytes = files.reduce((a, f) => a + f.size, 0);
  const lastWriteS = files.length ? (Math.max(...files.map(f => f.mtimeMs)) - t0) / 1000 : null;
  const ok = q.ok && !!done && files.length > 0;
  write('E', { name, dir, ok, error: q.error, saveS: done ? (done - t0) / 1000 : null, lastWriteS, sizeMB: Math.round(bytes / 1048576 * 100) / 100,
    unansweredPolls: unanswered }, ok ? `saved in ${(done - t0) / 1000} s (last file write ${lastWriteS} s), ${Math.round(bytes / 1048576 * 100) / 100} MB, ${unanswered} polls unanswered`
    : `FAILED: ${q.error ?? (done ? 'no save files' : never || `still saving after ${SAVE_TIMEOUT_S} s`)}`);
}
await alive.check();
console.log(`${stamp()} done; game ${(await ag('game_state')).ok ? 'alive' : 'NOT answering'}`);
