// One profiling run on a loaded world, the same for every world so results compare:
//   node tools/harness/profile-world.ts --tag sv [--minutes 10] [--ab gohome,visit,... --slice 2] [--taskstall 250]
// --taskstall MS: also run the probe's /taskstall watch (docs/04 F17; S3PP SimulatorStageProfiler must be off); stalls go to
// probe-out/<probe session>-taskstall.jsonl.
// Needs the probe server (ts3probe.py serve) and a world in live mode (cycle.ts --save X). For the whole run:
// autopilot at x3 with popups cleared, keep_alive + decline_requests every 30 s, and the native stutter profiler
// (separate Frida session). Samplers in turn: managed C# stack (minutes 1-4), native sim-thread stack (5-8).
// --ab: A/B in one load. The named fixes (patches switches; `routeworker` / `routebudget` / `routeasync` = ts3native [RouteWorker] / [RouteBudget] / [RouteAsync] via /native) switch every --slice minutes: a warm-up slice
// (on, not counted), then off,on,on,off repeating (ABBA; --ab-on-first: on,off,off,on); use minutes = slice x (1 + multiple of 4). Slice boundaries go to <tag>-ab.json and profile-compare.mjs splits the per-second rows into <tag>:on and <tag>:off.
// The samplers are skipped in this mode (they would mix both sides); hitches, iterations, routes, GC and stuck Sims remain.
// --fixes-off a,b,c: switch those ts3fixes patches (--ab names, e.g. gohome,rabbithole,visit) OFF for the whole run while paused, log `fixes off: ... (verified)` after reading them back; previous state restored at the end and on a crash.
// --frame-lead '{"service":"World/Service","ms":50,"min":20,"lotAdds":20}': frames > ms led by that main-slot service (and
// [LotStream] lot adds) counted at the end by frame-lead.ts, repro windows included: FAIL verdict below min (docs/169).
// --fixes-require 'name>=1,name<5000': ts3fixes counter rules (agent `patches` names, tools/harness/fixes-require.ts) checked at the end:
//   `PASS: fixes counters ...` or `FAIL: fixes counters: ...` (exit 1). With --ab the patches counters are also snapshot at every
//   switch into <tag>-ab-fixes.jsonl (the last line adds the spread / unseen logs).
// --hitch-tasks '["<regex>>=N", ...]' (JSON list, tools/harness/hitch-tasks.ts): at the end each rule needs N hitch lines whose
//   docs/166 task names match the regex: `PASS: hitch tasks ...` or `FAIL: hitch tasks: ...` (exit 1); docs/169 loop repros.
// --fixes-on a,b,c: the same, switched ON (e.g. autshadow: tuning kAutShadow is off, so pilot jobs turn it on here and every other job keeps it off).
// --until-compares N: P4 shadow runs: every 30 s count the compares in Bin/ts3native-shadow.jsonl (classify() of shadow-compare-report.ts: not M4, not M5) and end the window early at >= N; N reached before 5 min -> target 2N; --minutes is the cap.
// --seed N: seed_rng {seed: N} while paused, just before the unpause that starts the window (docs/27 §B); game RNG put back at the end.
// --repro ID: run the steps of that tools/harness/repros.json entry (agent commands, waitS after each) from the window start,
// e.g. the lot streaming pan (lotstream-pan, docs/28 §5.5). The stack samplers are skipped (they suspend the sim thread);
// with --mainstack too, only the main-thread stacks run, for the whole window (they never suspend the sim thread).
// A repro with `loop` (repro-loop.ts, docs/168 §6) runs its steps, then the loop body per arm (ABBA) until its fixed-count stop
// rule holds or loop.maxMinutes passes, and then ends the window early: `stopped at N events after M min` (queue.ts puts it in
// the row) and <tag>-stop.json ({secs, ...}: the hitch reports take it as the run length). --minutes is the cap.
// Early end (stop rule or --until-compares): the stutter profiler gets <tag>-stop.flag (its -P stopFile), unhooks and closes its
// files exactly as on unload, then its repl is closed; the frames session stays loaded until the game closes, as at a normal end.
// --mainstack main|all|TID: in the native sampler window also sample the main thread (or every non-sim game thread, or one id)
// through /threadstack right after each sim sample, into <tag>-mainstack.jsonl (docs/20 §7); never suspends the sim thread.
// --inroom: also run tools/frida/inroom-reads.js (docs/111: lot tree root-border dumps, SetBounds / QuadTreeBuild reads, in-room
// context flags) from the window start for the whole run, into <tag>-inroom.json (inroom-report.ts) and <tag>-inroom.log; the
// stack samplers are skipped (they suspend the sim thread, where its tree dumps run).
// --watchstalk: run tools/frida/watch-stalk.js instead of the stutter profiler (docs/113: route +0x1F4 watchpoints, then Stalker
// on sampled sim-thread Route_PlanLocked calls) into <tag>-watchstalk.json (watch-stalk-report.ts) and <tag>-watchstalk.log; no
// <tag>-<secs>-* profiler files (it hooks Route_PlanLocked too, and Stalker would follow into its listeners); samplers skipped.
// --watchstalk-worker: with --watchstalk, the stalk phase follows the route worker instead of the sim thread (docs/113 §8).
// --livereads2: also run tools/frida/live-reads2.js (docs/114: O+0x58, footprint grid, grid writer threads, 0xC6BAB0 points,
// follow slot +0x38 callers, houseboat start frame) from the window start, into <tag>-livereads2.json (live-reads2-report.ts) and
// <tag>-livereads2.log; the stack samplers are skipped (its grid dumps and point calls run on the sim thread).
// --f4b: also run tools/frida/f4b-reads.js (docs/99 §12.6: CopyNodes node y / +0x20 / path length, path slot +0x100 prepends, agent
// y vs terrain) from the window start, into <tag>-f4b.json (f4b-report.ts) and <tag>-f4b.log; the stack samplers are skipped
// (its terrain call runs on the sim thread).
// --vres: callvirt inline-cache snapshots through the probe agent's /vcache every 60 s, union in <tag>-vres.json (docs/133,
// tools/virtual-resolution-report.ts); the stack samplers are skipped. --vres-dry N: the same with N methods per snapshot.
// --svcsplit: frame-times.js times the states inside SimService_Update, frames.csv svcTop,svcTopMs (docs/247 §9).
// --no-samplers: skip the stack samplers without any other probe (docs/133 §5: the int3-only bisect job matches bp-vres).
// --no-prof: no stutter profiler session (no <tag>-<secs>-* files). queue.ts sets it for [ProbeSnap] jobs: a second Frida
// agent injecting while the int3 sampler storms the sim thread with breakpoints crashed the game in that agent's exception
// path (docs/67 §7, bp-rare-winter-pond 2026-10-19).
// --travel WORLD: before anything else, travel there with save-matrix.ts --cases travel --to WORLD --stay (university, future,
// or a vacation world: france | china | egypt; the trip code and its fail-fast checks are save-matrix's; it saves the loaded
// save as a Matrix-* copy first) and run the window in the destination world (docs/117 §2.11). A failed trip fails the run.
// Frames (docs/162): presented-frame times from tools/harness/frame-times.js (CModule hook on the main thread, no JS lock, no
// suspension) in their own Frida session into <tag>-<secs>-frames.csv; on unless --no-frames, --no-prof or --watchstalk.
// The stutter profiler also writes <tag>-<secs>-freezes.jsonl (sim publish gaps >= 50 ms), and the sync-plan lines of
// Bin/ts3native.log (`route async: sync plan ...` + its script stack) are stamped with epoch ms every second into
// <tag>-nativelog.jsonl; freeze-report.ts joins them.
// --prof-opts JSON: extra stutter-profile.js -P fields (docs/168 §4 profiler cost control): '{"stages":false}' = iteration
// boundary, catch-up steps and publish gaps only (no stage, GC, route or task hooks), '{"tasks":false}' = no per-task attribution
// (docs/166). out and hitchMs stay the harness's.
// --since-load N / --frames-shared FILE (queue.ts reuseSession and profile.repros, docs/170): N windows of this game load ran
// before this one. Written to <tag>-session.json, so the hitch reports exclude the 15 s profiler start edge instead of the first
// minute when N > 0; a repro's "lead-in" first step is cut to 15 s then. FILE is the load's frame log (one kept frames session per
// load): the first window starts it, every window copies its own span into <tag>-<secs>-frames.csv at the end.
// --camera-pan: a slow scripted camera pan through the agent's camera_pan (8 lots around the densest one, 120 s per pass, there
// and back) for the whole window; no window focus, no input. Off by default: the camera decides which Sims are seen (EA LOD,
// unseen throttles), so pan runs only compare with pan runs.
// Output: probe-out/profile/<tag>-{600-seconds.csv,600-hitches.jsonl,600.log,upkeep.log,managed.*,simstack.jsonl,mainstack.jsonl,stuck.json,houseboats.json,
// rng.json (seed reply, sim minutes at window start and end), activity.jsonl (every Sim's interaction per 30 s check; rng-compare.ts, then lot, at home, community lot, lowest need, routing; gate2-report.ts)}.
import { spawn } from 'node:child_process';
import { appendFileSync, closeSync, createWriteStream, existsSync, mkdirSync, openSync, readFileSync, readSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { classify } from './shadow-compare-report.ts';
import { parseArgs } from 'node:util';
import { checkRequire, nativeWindowStep, numericFields, parseRequire, stepTally } from './fixes-require.ts';
import { checkHitchTasks, parseHitchTasks } from './hitch-tasks.ts';
import { checkTag, parseRule } from './frame-lead.ts';
import { isSetup, runLoop, runStep, stopLine, untilUnlocked, withRule } from './repro-loop.ts';
import { loadStory } from './story-fuzzer.ts';
import { parseRows, summarize as fixedWorkSummary } from './fixedwork-report.ts';
import { abFixesClash, counterSwitches, nativeExports, nativeModeFields, nativeNames, nativeValues } from './native-switches.ts';
import { addArmColumns } from './arm-ms.ts';
import { leadInCut, sliceFrames } from './session-reuse.ts';
import { reproSave } from './repro-save.ts';
import { focusGame, giveFocusBack } from './focus-game.ts';
import { mayBeStuck } from './stuck-rule.ts';
import { clockStep, watchGame } from './game-alive.ts';
import { startMachineLoad, summarize } from './machine-load.ts';
import { BIN, GAME_EXE } from './game-exe.ts';

const { values } = parseArgs({ options: { svcsplit: { type: 'boolean' }, tag: { type: 'string' }, minutes: { type: 'string', default: '10' }, ab: { type: 'string' }, fixedwork: { type: 'string' }, 'ab-on-first': { type: 'boolean' }, 'fixes-off': { type: 'string' }, 'fixes-on': { type: 'string' }, 'until-compares': { type: 'string' }, slice: { type: 'string', default: '2' }, taskstall: { type: 'string' }, seed: { type: 'string' }, repro: { type: 'string' }, mainstack: { type: 'string' }, inroom: { type: 'boolean' }, watchstalk: { type: 'boolean' }, 'watchstalk-worker': { type: 'boolean' }, livereads2: { type: 'boolean' }, f4b: { type: 'boolean' }, vres: { type: 'boolean' }, 'vres-dry': { type: 'string' }, 'no-samplers': { type: 'boolean' }, 'no-env': { type: 'boolean' }, 'quit-jobs': { type: 'boolean' }, 'no-env-guard': { type: 'boolean' }, 'no-prof': { type: 'boolean' }, 'no-frames': { type: 'boolean' }, 'camera-pan': { type: 'boolean' }, 'prof-opts': { type: 'string' }, travel: { type: 'string' }, 'fixes-require': { type: 'string' }, 'frame-lead': { type: 'string' }, 'hitch-tasks': { type: 'string' }, 'since-load': { type: 'string' }, 'frames-shared': { type: 'string' }, 'loop-rule': { type: 'string' }, story: { type: 'string' } } });
if (!values.tag) throw new Error('--tag required');
const profOpts = values['prof-opts'] ? JSON.parse(values['prof-opts']) : {}; // fail before the game is touched on bad JSON
if (values['vres-dry'] !== undefined && !/^[1-9]\d*$/.test(values['vres-dry'])) throw new Error('--vres-dry must be a positive integer');
if (values.mainstack !== undefined && !/^(main|all|\d+)$/.test(values.mainstack)) throw new Error('--mainstack must be main, all or a thread id');
const ROOT = resolve(import.meta.dirname, '..', '..');
const OUT = join(ROOT, 'probe-out', 'profile');
mkdirSync(OUT, { recursive: true });
const reproEntry: any = values.repro ? (JSON.parse(readFileSync(join(ROOT, 'tools', 'harness', 'repros.json'), 'utf8')) as any[]).find(r => r.id === values.repro) : { steps: [] };
if (!reproEntry) throw new Error(`--repro: no entry ${values.repro} in tools/harness/repros.json`);
// --since-load N > 0 (queue.ts session reuse, docs/170): no load before this window, so a "lead-in" first step that waits out the
// load minute is cut to the 15 s profiler start edge (session-reuse.ts leadInCut)
const sinceLoad = values['since-load'] === undefined ? null : Number(values['since-load']);
if (sinceLoad !== null && !(Number.isInteger(sinceLoad) && sinceLoad >= 0)) throw new Error('--since-load must be an integer >= 0');
const reproSteps: any[] = sinceLoad ? leadInCut(reproEntry.steps ?? []) : reproEntry.steps ?? [];
if (!reproEntry.steps && !reproEntry.loop) throw new Error(`--repro ${values.repro}: no steps and no loop`);
// --loop-rule '{until?, maxMinutes?}' (queue.ts run.profile.loopRule, docs/168 §7): the A/B queue's stop rule over the repro's own
const reproLoop = reproEntry.loop ? withRule(reproEntry.loop, values['loop-rule'] ? JSON.parse(values['loop-rule']) : undefined) : null;
// --story CONFIG (JSON or file, docs/171 §7): the story fuzzer drives 8-12 Sims for the window after any --repro steps, into
// <tag>-story.jsonl; config and templates checked here, before the game is touched. Stack samplers off (they suspend the sim
// thread); --no-env and --no-env-guard keep season / moon prepares (no 30 s test_env guard).
const story = values.story ? loadStory(values.story, { seed: values.seed, envFree: !!values['no-env'] || !!values['no-env-guard'] }) : null;
if (story && reproLoop) throw new Error('--story: not with a loop repro');
if (story) values['no-samplers'] = true;
// one A/B mechanism per run; side probes (non-kept Frida sessions) would run on to the cap after an early end
if (reproLoop && (values.ab || values['until-compares'] || values.inroom || values.watchstalk || values.livereads2 || values.f4b || values.vres || values['vres-dry'])) throw new Error(`--repro ${values.repro} has a loop: not with --ab, --until-compares or a side probe`);
const mins = Number(values.minutes), secs = mins * 60, pre = join(OUT, `${values.tag}`);
// <tag>-session.json: windows after the first one of a game load (queue.ts reuseSession / profile.repros) keep their first minute;
// worst-hitches.ts runContext then excludes only the 15 s profiler start edge (docs/170 §2). No file = right after a load.
rmSync(`${pre}-session.json`, { force: true });
if (sinceLoad !== null) writeFileSync(`${pre}-session.json`, JSON.stringify({ sinceLoad, framesShared: values['frames-shared'] ?? null }, null, 1));
const sleep = (ms: number) => new Promise(r => setTimeout(r, ms));
const stamp = () => new Date().toISOString().slice(11, 19);
const get = async (p: string, to = 20000) => (await fetch(`http://127.0.0.1:47891/${p}`, { signal: AbortSignal.timeout(to) })).json();
// every agent call runs on the sim thread: <tag>-calls.jsonl keeps its epoch span so the hitch reports can exclude it (docs/161 §6)
writeFileSync(`${pre}-calls.jsonl`, '');
// timeoutMs: a repro step's own reply timeout (step.timeoutS), e.g. cas_close after a slow category sync (save-write-loop, 2026-10-23)
const ag = async (cmd: string, args?: object, timeoutMs = 8000): Promise<any> => {
  const q = new URLSearchParams({ cmd, timeout: String(timeoutMs) });
  if (args) q.set('args', JSON.stringify(args));
  const t0 = Date.now();
  try { return await get(`agent?${q}`, Math.max(20000, timeoutMs + 5000)); } catch (e) { return { ok: false, error: String(e) }; } finally { appendFileSync(`${pre}-calls.jsonl`, JSON.stringify({ cmd, t0, t1: Date.now() }) + '\n'); }
};

if (values.travel) {
  if (!/^[a-z]+$/i.test(values.travel)) throw new Error('--travel must be a world name (university, future, france, china, egypt)');
  console.log(`${stamp()} travel to ${values.travel} (save-matrix.ts --stay)`);
  const code = await new Promise<number | null>(res => spawn(process.execPath, [join(ROOT, 'tools', 'harness', 'save-matrix.ts'), '--cases', 'travel', '--to', values.travel!, '--stay'], { stdio: 'inherit' }).on('exit', res));
  if (code !== 0) throw new Error(`travel to ${values.travel} failed: save-matrix.ts exit ${code}`);
}
const st = await ag('game_state');
if (st.result?.sub_state !== 'LiveModeState') throw new Error(`not in live mode: ${JSON.stringify(st)}`);
console.log(`${stamp()} ${values.tag}: ${JSON.stringify(st.result)}`);
const alive = watchGame();
// runs are measured unfocused (Rodney, 2026-10-17): the game takes focus at launch; give it back to the window that was in
// front when the queue started, then record the state (a focused run is not comparable with unfocused ones)
console.log(`${stamp()} focus: ${giveFocusBack()}`);
console.log(`${stamp()} game window ${focusGame() ? 'FOCUSED (not comparable with unfocused runs)' : 'not focused (standard)'}`);
// set up while paused (Rodney, 2026-10-17): test_env, seed, profiler attach happen with the world still; the measured
// window starts when autopilot unpauses to x3 right after the profiler spawn (seeded draws only start there)
await ag('autopilot', { enabled: true, speed: 'none', dialogs: true });
const paused = await ag('set_speed', { speed: 'pause' });
if (!paused.ok) throw new Error(`pause before setup failed: ${paused.error}`);
// world conditions that skew measurements (agent test_env, docs/TESTING.md): the save's season, weather and moon play out;
// winter / extreme cold (Sims freeze solid) becomes spring and a near full moon becomes first quarter. Before seeding so
// a correction does not shift the seeded draws; the clock guard below repeats it every 30 s. --no-env turns it off.
// --no-env-guard (queue profile.envGuard: false, story runs over sim days, docs/171 §16): the start correction only, then
// seasons, weather and moons pass as in play (R-EP-3); keep the guard for short timing jobs
const env = values['no-env'] ? null : await ag('test_env', {});
if (env && !env.ok) throw new Error(`test_env failed: ${env.error}`); // an agent without test_env would silently test another season
if (env) console.log(`${stamp()} test_env: ${JSON.stringify(env.result)}`);
// --quit-jobs (queue profile.quitJobs): every active-household Sim with a job quits it (agent career quit), so career events
// (film Actor / Director, the cheese rumour, 2026-11-06 al261 / al263) cannot pause a long run. In the running game only.
if (values['quit-jobs']) {
  const sims = (await ag('list_sims', {})).result ?? [];
  const out: string[] = [];
  for (const s of sims) {
    const r = await ag('career', { sim: String(s.id), action: 'quit' });
    if (r.ok || !/has no job/.test(r.error ?? '')) out.push(`${s.name}: ${r.ok ? 'quit' : r.error}`);
  }
  console.log(`${stamp()} quit_jobs: ${out.length ? out.join('; ') : 'nobody had a job'} (${sims.length} in the household)`);
}
// --fixes-off / --fixes-on: patches switches (same agent command as --ab) off / on for the whole run; read back, restored in restoreFixes
if (values['fixes-require']) parseRequire(values['fixes-require']); // a bad rule fails before the run
if (reproEntry.stepsRequire) parseRequire(reproEntry.stepsRequire);
const fixesRequire = [values['fixes-require'], reproEntry.fixesRequire].filter(Boolean).join(','); // the repro's own pass line (repros.json fixesRequire) on top of the job's
if (reproEntry.fixesRequire) parseRequire(reproEntry.fixesRequire);
const stepAnswers: { cmd: string; r: any }[] = []; // repro step answers, for the repro's stepsRequire
const hitchTasks: string[] = values['hitch-tasks'] ? JSON.parse(values['hitch-tasks']) : [];
parseHitchTasks(hitchTasks);
if (values['frame-lead']) parseRule(values['frame-lead']);
const fixesOff = values['fixes-off'] ? values['fixes-off'].split(',') : [];
const fixesOn = values['fixes-on'] ? values['fixes-on'].split(',') : [];
if ([...fixesOff, ...fixesOn].some(n => !/^[a-z0-9]+$/.test(n))) throw new Error('--fixes-off / --fixes-on must be comma-separated switch names');
const fixesSet: Record<string, boolean> = { ...Object.fromEntries(fixesOff.map(n => [n, false])), ...Object.fromEntries(fixesOn.map(n => [n, true])) };
const fixesNames = Object.keys(fixesSet);
const fixesPrev: Record<string, boolean> = {};
let fixesRestored = !fixesNames.length;
const restoreFixes = async () => { if (fixesRestored) return; fixesRestored = true; await ag('patches', fixesPrev); };
if (fixesNames.length) {
  const both = abFixesClash(values.ab ? values.ab.split(',') : [], fixesOn, fixesOff);
  if (both.length) throw new Error(`--fixes-off / --fixes-on / --ab name ${both.join(',')} twice`);
  const before = await ag('patches');
  const unknown = fixesNames.filter(n => typeof before.result?.[n] !== 'boolean'); // native / counter switches and typos are not patches switches
  if (!before.ok || unknown.length) throw new Error(`--fixes-off / --fixes-on: not a patches switch: ${unknown.join(',') || before.error}`);
  for (const n of fixesNames) fixesPrev[n] = before.result[n];
  const after = await ag('patches', fixesSet);
  if (fixesNames.some(n => after.result?.[n] !== fixesSet[n])) { await restoreFixes(); throw new Error(`--fixes-off / --fixes-on: switches not set: ${JSON.stringify(after.result && Object.fromEntries(fixesNames.map(n => [n, after.result[n]])))}`); }
  if (fixesOff.length) console.log(`${stamp()} fixes off: ${fixesOff.join(',')} (verified)`);
  if (fixesOn.length) console.log(`${stamp()} fixes on: ${fixesOn.join(',')} (verified)`);
  for (const ev of ['uncaughtException', 'unhandledRejection'] as const) process.on(ev, e => { console.error(e); restoreFixes().finally(() => process.exit(1)); });
}
// --until-compares: counted from the game's shadow log (incremental read); the window then ends early through the normal end path
let stopped = false;
const untilN = values['until-compares'] ? Number(values['until-compares']) : 0;
if (values['until-compares'] && !(Number.isInteger(untilN) && untilN > 0)) throw new Error('--until-compares must be a positive integer');
const shadowFile = join(BIN, 'ts3native-shadow.jsonl');
let shadowOff = 0, shadowCarry = '', compares = 0, target = untilN;
function countCompares() {
  try {
    const b = readFileSync(shadowFile);
    if (b.length < shadowOff) { shadowOff = 0; shadowCarry = ''; compares = 0; } // the game restarted the log
    const lines = (shadowCarry + b.subarray(shadowOff).toString('utf8')).split('\n');
    shadowOff = b.length; shadowCarry = lines.pop()!;
    for (const l of lines) if (l.trim() && classify(JSON.parse(l)).cls < 'M4') compares++;
  } catch { /* no log yet or a half-written line: counted next time */ }
}
function untilCheck() {
  if (!untilN) return;
  countCompares();
  const el = (Date.now() - t0) / 60000;
  console.log(`${stamp()} compares ${compares} (target ${target}), ${Math.max(0, mins - el).toFixed(1)} min left`);
  if (compares < target) return;
  if (el < 5) { target = 2 * compares; console.log(`${stamp()} ${compares} compares before 5 min: target raised to ${target}`); return; }
  stopWindow(`stopped at ${compares} compares after ${el.toFixed(1)} min (target ${untilN})`);
}
// early end of the window, the same for every stop rule: loops and waits see `stopped`; the profiler runs its dispose work on
// the stop flag (unhook, last second row, files closed), then its repl is closed (15 s at most for the flag to be seen)
const stopFlag = `${pre}-stop.flag`;
rmSync(stopFlag, { force: true }); rmSync(`${pre}-stop.json`, { force: true }); // a rerun with the same tag
let profHalted = () => {};
const profHaltSeen = new Promise<void>(r => { profHalted = r; });
function stopWindow(line: string, extra: object = {}) {
  if (stopped) return;
  stopped = true;
  console.log(`${stamp()} ${line}`);
  writeFileSync(`${pre}-stop.json`, JSON.stringify({ secs: Math.round((Date.now() - t0) / 1000), capSecs: secs, line, ...extra }, null, 1));
  if (!prof) return;
  writeFileSync(stopFlag, '');
  Promise.race([profHaltSeen, sleep(15000)]).then(() => prof.kill());
}
const waitUntil = async (t: number) => { while (!stopped && Date.now() < t) await sleep(Math.min(1000, t - Date.now())); };
const rng: any = { seed: values.seed === undefined ? null : Number(values.seed), reply: null, simMinutes: {} };
if (rng.seed !== null) {
  const r = await ag('seed_rng', { seed: rng.seed });
  if (!r.ok) throw new Error(`seed_rng failed: ${r.error}`); // agent without seed_rng: a seeded run would be unseeded
  rng.reply = r.result;
  console.log(`${stamp()} seed_rng: ${JSON.stringify(r.result)}`);
}
rng.simMinutes.start = (await ag('time')).result?.minutes_total ?? null;
// fail fast (docs/TESTING.md): game time must move unless the game says it is paused; 2026-10-17 bp-p0-sync froze after its
// save step twice and still "passed" with 39-47 stuck Sims. 2 checks 30 s apart without movement = FAIL.
// Build/Buy/CAS/Edit Town pause the clock by design (game-alive.ts clockStep): there the agent answering is the progress check
let pauseDeclared = false; // a repro step with "pausesClock": true|false sets it (reproStep)
const clockGuard = (async () => {
  const st = { last: rng.simMinutes.start as number | null, misses: 0 } as Parameters<typeof clockStep>[0];
  for (const end = Date.now() + secs * 1000; !stopped && Date.now() < end;) {
    await sleep(30000);
    if (env && !values['no-env-guard']) {  // keep skew sources out during the run (idempotent; only acts on winter, extreme cold or a full moon)
      const g = (await ag('test_env', { min_days_to_full: 1 }).catch(() => null))?.result;
      if (g?.changed?.length) console.log(`${stamp()} test_env guard: ${g.changed.join(', ')} (season ${g.season}, moon ${g.moon_phase})`);
    }
    const t = (await ag('time').catch(() => null))?.result ?? null;
    const sub = t && (/^Pause/i.test(t.speed) || t.minutes_total === st.last) ?(await ag('game_state').catch(() => null))?.result?.sub_state : undefined;
    const why = clockStep(st, t, { subState: sub, declared: pauseDeclared });
    if (!why) continue;
    console.log(`${stamp()} FAIL: ${why} (fail-fast guard)`);
    // pause bits, game dt, speed and window state at the trip (docs/85 §6: minimize pause = bit 2, device pause = bit 4)
    const cs = await ag('clock_state');
    console.log(`${stamp()} clock_state at the trip: ${JSON.stringify(cs.ok ? cs.result : cs.error)}`);
    // which modal holds the clock (title, buttons) and whether a world switch is under way (story-smoke 2026-10-23)
    const pd = await ag('pending_dialogs').catch(() => null), gs = await ag('game_state').catch(() => null);
    console.log(`${stamp()} at the trip: dialog ${JSON.stringify(pd?.ok ? pd.result : pd?.error).slice(0, 400)}; sub_state ${gs?.result?.sub_state ?? '?'}, world ${gs?.result?.load_file ?? '?'}`);
    // what the autopilot did meanwhile: a dialog answered in a loop shows here (2026-11-03: a wish dialog under the age-up dialog,
    // 210 answers in 60 s, seen only in the upkeep log)
    const ap = await ag('autopilot').catch(() => null), cl: string[] = ap?.result?.cleared ?? [];
    console.log(`${stamp()} at the trip: autopilot ${ap?.ok ? `${cl.length} answers since the last upkeep${cl.length ? ` (last: ${cl[cl.length - 1].slice(0, 120)})` : ''}, blocked ${ap.result.blocked ?? 'none'}${ap.result.restarted ? ', restarted (task had stopped)' : ''}` : ap?.error}`);
    prof?.kill();
    await restoreFixes();
    process.exit(1);
  }
})();
const boats0 = (await ag('houseboats')).result ?? []; // empty outside Isla Paradiso
if (values.taskstall) console.log(`${stamp()} taskstall: ${JSON.stringify(await get(`taskstall?start=1&ms=${Number(values.taskstall)}`))}`);

const prof = values.watchstalk || values['no-prof'] ? null : spawn('python', ['-m', 'frida_tools.repl', '-n', GAME_EXE, '-l', join(ROOT, 'tools', 'frida', 'stutter-profile.js'),
  '-P', JSON.stringify({ ...profOpts, out: `${pre}-${secs}`.replace(/\\/g, '/'), hitchMs: 50, ...((reproLoop || untilN) && { stopFile: stopFlag.replace(/\\/g, '/') }) }), '-q', '-t', String(secs)], { stdio: ['ignore', 'pipe', 'pipe'] });
if (prof) {
  const plog = createWriteStream(`${pre}-${secs}.log`); prof.stdout.pipe(plog); prof.stderr.pipe(plog);
  prof.stdout.on('data', (d: Buffer) => { if (String(d).includes('[ts3prof] stopped early')) profHalted(); });
}
const profDone = prof ? new Promise(r => prof.on('exit', r)) : Promise.resolve();
// Frida 17's Interceptor patches a page as: VirtualProtect RWX, write, VirtualProtect back to RX (gum_memory_patch_code_pages_via_mprotect).
// Two Frida sessions patching the same page at once race: one sets RX between the other's VirtualProtect and its write, and that write
// faults (docs/67 §21, Crash R: the profiler's publish hook 0x593590 vs frame-times.js readpair 0x593440 / drain 0x5937B0, page
// 0x593000). So the side sessions below start only once the profiler is done installing (or gave up, or 30 s passed).
const profInstalled = prof ? new Promise<void>(r => {
  let seen = '', done = false;
  const end = () => { done = true; r(); };
  const on = (d: Buffer) => { seen += d; if (/\[ts3prof\] hooks installed|NOT PROFILING/.test(seen)) end(); };
  prof.stdout.on('data', on); prof.stderr.on('data', on); prof.on('exit', end);
  setTimeout(() => { if (!done) { console.log(`${stamp()} stutter profiler: no install line in 30 s; starting the side sessions anyway`); end(); } }, 30000).unref();
}) : Promise.resolve();
// fail fast: the profiler stops itself after -t; one still running 90 s past that is hung, and the run would wait on it forever
setTimeout(() => { if (prof && prof.exitCode === null && prof.signalCode === null) { console.log(`${stamp()} FAIL: stutter profiler still running 90 s past -t ${secs}; killed (fail-fast guard)`); prof.kill(); } }, (secs + 90) * 1000).unref();

await ag('autopilot', { enabled: true, speed: 'ultra', dialogs: true }); // window start: unpause to x3
const t0 = Date.now(), at = (s: number) => t0 + s * 1000;
// --inroom / --watchstalk: own Frida session like the stutter profiler; the script writes <tag>-<name>.json itself and prints a line every 30 s
// keep: never unload the session while the game runs; the repl exits when the game closes. Used for frames (a CModule on the
// main thread's per-frame hot path): the one end-of-profile crash (freeze-pan 2026-10-22, eip outside every module) hit
// right when that session unloaded.
const side = (on: boolean | undefined, file: string, name: string, extra = {}, keep = false) => on ? new Promise(r => {
  const args = ['-m', 'frida_tools.repl', '-n', GAME_EXE, '-l', join(ROOT, 'tools', 'frida', file),
    // +5 s: unload apart from the stutter profiler's session (two agents unloading together, docs/99 §12.7 / I-99-62)
    '-P', JSON.stringify({ out: pre.replace(/\\/g, '/'), minutes: mins, ...extra }), '-q', '-t', String(keep ? secs + 86400 : secs + 5)];
  if (keep) {
    // own log file as stdout/stderr (no pipe to this process, which exits before the game closes) and no exit wait
    const fd = openSync(`${pre}-${name}.log`, 'a');
    const k = spawn('python', args, { stdio: ['ignore', fd, fd], detached: true, windowsHide: true });
    k.unref(); closeSync(fd);
    const end = Date.now() + (secs + 5) * 1000; // or at an early end: the session itself stays loaded either way
    const iv = setInterval(() => { if (stopped || Date.now() >= end) { clearInterval(iv); r(undefined); } }, 1000);
    return;
  }
  const p = spawn('python', args, { stdio: ['ignore', 'pipe', 'pipe'] });
  const log = createWriteStream(`${pre}-${name}.log`);
  p.stdout.on('data', (d: Buffer) => { log.write(d); for (const l of String(d).split(/\r?\n/)) if (l.includes(`[${name}]`)) console.log(`${stamp()} ${l.trim()}`); });
  p.stderr.pipe(log);
  p.on('exit', r);
  setTimeout(() => { if (p.exitCode === null && p.signalCode === null) { console.log(`${stamp()} FAIL: ${file} still running 90 s past -t ${secs}; killed (fail-fast guard)`); p.kill(); } }, (secs + 90) * 1000).unref();
}) : Promise.resolve();
await profInstalled; // no two sessions patching code at once (above)
const inroomDone = side(values.inroom, 'inroom-reads.js', 'inroom');
const watchDone = side(values.watchstalk, 'watch-stalk.js', 'watchstalk', values['watchstalk-worker'] ? { worker: true } : {});
const lr2Done = side(values.livereads2, 'live-reads2.js', 'livereads2');
const f4bDone = side(values.f4b, 'f4b-reads.js', 'f4b');
// frame-times.js lives in tools/harness; side() loads from tools/frida
const framesOn = !values['no-frames'] && !values['no-prof'] && !values.watchstalk;
// --frames-shared FILE (queue.ts, one per game load): the kept frames session writes the load's frames there; the first window of
// the load starts it, later ones find it running (a second session would refuse the hooked prologue) and every window copies
// its own span into <tag>-<secs>-frames.csv at the end (sliceFrames), so readers see one file per run as before
const framesShared = values['frames-shared'];
const framesDone = side(framesOn && !(framesShared && existsSync(framesShared)), join('..', 'harness', 'frame-times.js'), 'frames', { out: (framesShared ?? `${pre}-${secs}-frames.csv`).replace(/\\/g, '/'), svcsplit: !!values.svcsplit }, true);
// --camera-pan: the agent moves the camera from a sim-thread task (camera_pan), so the scripted pan stops while the sim is frozen;
// a player's camera keeps moving then (docs/162 §4)
if (values['camera-pan']) {
  const r = await ag('camera_pan', { lots: 8, height: 25, seconds: 120, passes: Math.ceil(secs / 120) + 1 });
  console.log(`${stamp()} camera pan: ${r.ok ? `${r.result.lengthM} m, ${r.result.passes} passes of ${r.result.passMs / 1000} s` : `FAILED ${r.error}`}`);
}
// sync-plan lines of ts3native.log get an epoch stamp (the log has none): new bytes read every second, so a line's t is at most ~1 s
// after it was written
const NLOG = join(BIN, 'ts3native.log');
writeFileSync(`${pre}-nativelog.jsonl`, '');
const nlogDone = (async () => {
  let off = (() => { try { return statSync(NLOG).size; } catch { return 0; } })(), carry = '';
  while (!stopped && Date.now() - t0 < secs * 1000) {
    await sleep(1000);
    try {
      const size = statSync(NLOG).size;
      if (size < off) { off = 0; carry = ''; } // a new log (game restarted)
      if (size === off) continue;
      const fd = openSync(NLOG, 'r'), b = Buffer.alloc(size - off);
      readSync(fd, b, 0, b.length, off); closeSync(fd); off = size;
      const lines = (carry + b.toString('latin1')).split(/\r?\n/); carry = lines.pop()!;
      const keep = lines.filter(l => /^route async: (sync plan|script stack)/.test(l));
      if (keep.length) appendFileSync(`${pre}-nativelog.jsonl`, keep.map(line => JSON.stringify({ t: Date.now(), line }) + '\n').join(''));
    } catch { /* no plugin log: nothing to stamp */ }
  }
})();
// --vres (docs/133): a callvirt inline-cache snapshot through the probe agent every 60 s from the window start, then a last one
// that writes the union to <tag>-vres.json; nothing is hooked. --vres-dry N: the same, but each snapshot walks only N methods
// (no call / target following; docs/133 §5 bisect). A snapshot is many short /vcache calls (one tick-aligned slice of <= 20 ms
// each, 50 ms apart), so the agent's script thread is never held for long: bp-vres (2026-10-20) held it with one synchronous
// call past game-alive's 60 s heartbeat limit, and the harness killed a running game.
const vresDry = Number(values['vres-dry'] ?? 0), vresOn = values.vres || values['vres-dry'] !== undefined;
const vresSnapshot = async (q: string) => {
  let fails = 0, slowest = 0, r: any;
  for (const t = Date.now(); !stopped;) {
    const s0 = Date.now();
    try { r = await get(`vcache?snap=1&budget=45&slice=20&wait=100${vresDry ? `&dry=${vresDry}` : ''}${q}`, 10000); fails = 0; }
    catch (e: any) { if (++fails >= 2) throw new Error(`two slices in a row failed: ${e.message}`); continue; }
    slowest = Math.max(slowest, Date.now() - s0);
    if (r.done) return Object.assign(r, { slowest });
    if (Date.now() - t > 60000) throw new Error(`not done after 60 s (agent budget is 45 s): ${r.methods} methods, ${r.queued} queued`);
    await sleep(50);
  }
  return Object.assign(r ?? {}, { slowest });
};
const vresLine = (r: any) => `${r.ms} ms wall, ${r.workMs} ms work in ${r.slices} slices (${r.tickAligned} tick-aligned, slowest call ${r.slowest} ms), ${r.images} images, ${r.methods} methods walked, ${r.decoded} decoded, ${r.filled} filled caches, ${r.sites} sites, +${r.newPairs} pairs${r.partial ? ' (budget hit: partial)' : ''}${r.dry ? ` (dry ${r.dry})` : ''}`;
const vresDone = vresOn ? (async () => {
  let fails = 0;
  for (let s = 60; s < secs - 20 && !stopped; s += 60) {
    await sleep(Math.max(0, at(s) - Date.now()));
    try {
      const r = await vresSnapshot('');
      fails = 0;
      console.log(`${stamp()} [vres] snapshot ${r.snap}: ${vresLine(r)}; ${Math.ceil((secs - s) / 60)} min left`);
    } catch (e: any) {
      console.log(`${stamp()} [vres] snapshot failed: ${e.message}`);
      if (++fails >= 2) { console.log(`${stamp()} FAIL: [vres] two snapshots in a row failed (fail-fast guard)`); break; }
    }
  }
  const out = `${pre}-vres.json`.replace(/\\/g, '/');
  try {
    const r = await vresSnapshot('');
    const w = await get(`vcache?out=${encodeURIComponent(out)}`, 30000);
    console.log(`${stamp()} [vres] last snapshot ${r.snap}: ${vresLine(r)}; wrote ${w.wrote}`);
  } catch (e: any) { console.log(`${stamp()} FAIL: [vres] last snapshot / dump: ${e.message}`); }
})() : Promise.resolve();
const abNames = values.ab ? values.ab.split(',') : [];
const slices: { from: number; on: boolean }[] = [];
// a rerun with the same tag must not append to the last run's snapshots (wait-ab read run 1 twice, 2026-10-20)
// ... nor leave the last run's slice table: a run stopped before its end writes none, and wait-ab.ts paired the old table with
// the new (cut) snapshots (2026-11-03, noise stops)
if (abNames.length) { writeFileSync(`${pre}-ab-native.jsonl`, ''); writeFileSync(`${pre}-ab-fixes.jsonl`, ''); rmSync(`${pre}-ab.json`, { force: true }); }
// ts3native switch tables (nativeNames, counterSwitches, nativeValues: native_set repro steps and --ab): native-switches.ts,
// shared with session-reuse.ts, which puts them back between jobs that share one game load (docs/170 §2)
async function nativeSet(args: Record<string, unknown>): Promise<{ ok: boolean; result?: unknown; error?: string }> {
  const done: Record<string, number> = {};
  for (const [n, v] of Object.entries(args ?? {})) {
    const val = Number(v === true ? 1 : v === false ? 0 : v);
    if (!Number.isInteger(val)) return { ok: false, error: `native_set: ${n} value ${v} is not an integer` };
    if (nativeNames.includes(n)) await get(`native?${n}=${val}`);
    else if (n in counterSwitches || n in nativeValues || n in nativeExports) {
      // read back: a probe that writes 0/1 only (before 2026-10-25) left HoldKeyed=2 / p6warm=2 unset without an error
      const w = await get(`nativeexport?name=${nativeExports[n] ?? `ts3native_counters&offset=${counterSwitches[n] ?? nativeValues[n]}`}&value=${val}`);
      if (w?.value !== val) return { ok: false, error: `native_set: ${n}=${val} did not stick (reads ${w?.value})` };
    }
    else return { ok: false, error: `native_set: unknown ts3native switch ${n}` };
    done[n] = val;
  }
  return { ok: true, result: done };
}
const toggler = (async () => {
  if (!abNames.length) return;
  for (let i = 0; i * Number(values.slice) * 60 < secs; i++) {
    // slice 0 is warm-up (on, not counted); then off,on,on,off (ABBA) so a drift in world activity hits both sides alike;
    // --ab-on-first: on,off,off,on (docs/136 §2: the first counted slice is a heavy stretch, so pair jobs by start side)
    const on = i === 0 || [false, true, true, false][(i - 1) % 4] !== !!values['ab-on-first'];
    await sleep(Math.max(0, at(i * Number(values.slice) * 60) - Date.now()));
    // ts3native counters at every switch (end of the previous slice): wait-ab.ts splits the sim-thread waits per side
    await get('native').then(c => appendFileSync(`${pre}-ab-native.jsonl`, JSON.stringify({ i, s: (Date.now() - t0) / 1000, c }) + '\n')).catch(() => {});
    // ts3fixes counters too (2026-10-22: the spreads pair had no proof the nightly passes ran inside counted slices)
    await ag('patches').then(r => appendFileSync(`${pre}-ab-fixes.jsonl`, JSON.stringify({ i, s: (Date.now() - t0) / 1000, c: numericFields(r.result) }) + '\n')).catch(() => {});
    const patchNames = abNames.filter(n => !nativeNames.includes(n) && !(n in counterSwitches) && n !== 'fwnull');
    // per-slice read-back (2026-11-02: early gates must not test an --ab switch, it alternates per slice): a switch that did not
    // take stops the run with FAIL (the 'native_set: ... did not stick' text is what queue.ts smokeProblems looks for)
    const bad: string[] = [], want = on ? 1 : 0;
    if (patchNames.length) {
      const r = await ag('patches', Object.fromEntries(patchNames.map(n => [n, on])));
      for (const n of patchNames) if (r.result?.[n] !== on) bad.push(`${n}=${on} did not stick (reads ${r.result?.[n]})`);
    }
    for (const n of abNames.filter(n => nativeNames.includes(n))) {
      const v = Number((await get(`native?${n}=${want}`))?.[nativeModeFields[n]]);
      if (v !== want) bad.push(`${n}=${want} did not stick (reads ${v})`);
    }
    for (const n of abNames.filter(n => n in counterSwitches)) {
      const w = await get(`nativeexport?name=ts3native_counters&offset=${counterSwitches[n]}&value=${want}`);
      if (w?.value !== want) bad.push(`${n}=${want} did not stick (reads ${w?.value})`);
    }
    if (bad.length) {
      console.log(`${stamp()} FAIL: --ab slice ${i} read-back: native_set: ${bad.join('; ')}`);
      await restoreFixes(); process.exit(1);
    }
    slices.push({ from: (Date.now() - t0) / 1000, on, ...(i === 0 && { warmup: true }) });
    console.log(`${stamp()} ${abNames.join(',')} ${on ? 'on' : 'off'} (read back)`);
  }
})();
// X4 fixed-work bench (docs/244, --fixedwork <calls per slice>): every slice sends the agent's fixed_work (one fixed managed loop on
// the sim thread, ~20 ms) <calls> times, evenly spread, and logs the time with the slice's arm to <tag>-ab-fixedwork.jsonl;
// fixedwork-report.ts reads it (the report line is printed at the end of the run). Off unless the job asks; needs --ab. --ab fwnull
// = no switch at all (every slice runs the same code: the same-arm noise check of the measurement smoke).
const fwCalls = values.fixedwork ? Math.max(1, Number(values.fixedwork) || 0) : 0;
const fwFile = `${pre}-ab-fixedwork.jsonl`;
if (fwCalls) { if (!abNames.length) throw new Error('--fixedwork needs --ab (it times per arm)'); writeFileSync(fwFile, ''); }
const fwLoop = (async () => {
  if (!fwCalls) return;
  const sliceS = Number(values.slice) * 60;
  let fails = 0;
  for (let i = 0; i * sliceS < secs; i++) for (let k = 0; k < fwCalls; k++) {
    await sleep(Math.max(0, at(i * sliceS + (k + 0.5) * sliceS / fwCalls) - Date.now()));
    if (Date.now() >= at(secs)) return;
    const sl = slices[i]; // the toggler pushed slice i at its start (read back); none = it has not switched yet, skip
    if (!sl) continue;
    const r = await ag('fixed_work');
    if (!r.ok || typeof r.result?.usPer100 !== 'number') {
      if (++fails >= 2) { console.log(`${stamp()} FAIL: fixed_work failed twice in a row: ${String(r.error).split('\n')[0].slice(0, 200)} (agent package without the command?)`); await restoreFixes(); process.exit(1); }
      continue;
    }
    fails = 0;
    appendFileSync(fwFile, JSON.stringify({ i, s: (Date.now() - t0) / 1000, on: sl.on, ...(i === 0 && { warmup: true }), us: r.result.usPer100, sum: r.result.sum, resUs: r.result.resUs, warm: r.result.warm, totalUs: r.result.totalUs }) + '\n');
  }
})();
const reproFail = async (why: string) => {
  console.log(`${stamp()} FAIL: ${why}`);
  for (const c of reproEntry.cleanup ?? []) { const cr = await ag(c.cmd, c.args); console.log(`${stamp()} cleanup ${c.cmd}: ${cr.ok ? 'ok' : 'ERROR ' + cr.error}`); }
  process.exit(1);
};
// the note goes into the log line: worst-hitches.ts labels the step's window by its arm ("fire off #3" -> reflect_call off)
const nativeMarks = new Map<string, any>(); // native_mark / native_check windows (fixes-require.ts nativeWindowStep)
const reproStep = async (s: any) => {
  const call = () => s.cmd === 'native_set' ? nativeSet(s.args)
    : s.cmd === 'native_mark' || s.cmd === 'native_check' ? nativeWindowStep(s, () => get('native'), nativeMarks)
    : ag(s.cmd, s.args, s.timeoutS ? s.timeoutS * 1000 : undefined);
  if (s.pausesClock !== undefined) pauseDeclared = !!s.pausesClock; // the step pauses the clock on purpose until a later step says false
  let r = await call();
  if (s.cmd === 'set_speed') r = await untilUnlocked(call, r, { sleep, now: Date.now, log: l => console.log(`${stamp()} ${l}`),
    dialog: async () => JSON.stringify((await ag('pending_dialogs').catch(() => null))?.result ?? null).slice(0, 300) });
  // an async_log reply is the record of what the sim thread did (a method touch's outcome, docs/526 section 8): logged whole
  console.log(`${stamp()} repro ${values.repro} step ${s.cmd}${s.note ? ` [${s.note}]` : ''}${isSetup(s) ? ' (setup)' : ''}: ${JSON.stringify(r).slice(0, s.cmd === 'async_log' ? 4000 : 200)}`);
  appendFileSync(`${pre}-repro.jsonl`, JSON.stringify({ t: stamp(), cmd: s.cmd, args: s.args, note: s.note, r }) + '\n'); // full result: the log line is cut
  stepAnswers.push({ cmd: s.tally ?? s.cmd, r }); // "tally": count this step under its own name in stepsRequire (per-arm visits)
  // fail fast, as repro.ts: a failed setup step means the rest of the run measures nothing (2026-10-19 bp-ab-wildflowertask
  // ended PASS after wildflower_drain failed); "optional": true steps may fail. The queue closes the game after the job.
  if (!r.ok && !s.optional) await reproFail(`repro step ${s.cmd} failed: ${String(r.error).split('\n')[0].slice(0, 300)}`);
  return r;
};
const reproRun = (async () => {
  for (const s of reproSteps) await runStep(s, { ag, step: reproStep, sleep, now: Date.now, halted: () => stopped || Date.now() >= at(secs) }) // fixed waitS or adaptive waitUntil, then until (repro-loop.ts)
    .catch((e: Error) => reproFail(`repro ${e.message}`));
  if (story) await story.run({ ag, sleep, halted: () => stopped || Date.now() >= at(secs), pre }).catch(e => reproFail(e.message)); // household creation failed: stop the job
  if (!reproLoop) return;
  // firings inside the first minute are excluded by the hitch classifier (bp-overwatch-ab-loop2 2026-10-23: all 20 fell in it)
  // shortcut: 65 s even in a reused session (whose start edge is only 15 s), upgrade when reuse measurement matters
  if (Date.now() < at(65)) { console.log(`${stamp()} loop waits for the end of the excluded first minute`); await sleep(at(65) - Date.now()); }
  const capMs =Math.min(reproLoop.maxMinutes ?? mins, mins) * 60000 - (Date.now() - t0); // the loop cap counts from the window start
  // in-load arms as --ab slices (docs/189 §16): slice 0 = the lead-in (warm-up, not counted), then one slice per firing with a
  // ts3native snapshot at its start, so wait-ab.ts and hitch-ab.ts split the whole arm window per arm (<tag>-ab.json loop: true)
  writeFileSync(`${pre}-ab-native.jsonl`, '');
  const loopSnap = () => get('native').then(c => appendFileSync(`${pre}-ab-native.jsonl`, JSON.stringify({ i: slices.length, s: (Date.now() - t0) / 1000, c }) + '\n')).catch(() => {});
  await loopSnap(); slices.push({ from: (Date.now() - t0) / 1000, on: false, warmup: true } as typeof slices[number]);
  const onArm = async (arm: string) => { await loopSnap(); slices.push({ from: (Date.now() - t0) / 1000, on: arm === 'on' }); };
  try {
    const r = await runLoop(reproLoop, capMs, { ag, step: reproStep, sleep, now: Date.now, halted: () => stopped || Date.now() >= at(secs), onArm });
    if (r.why !== 'window') stopWindow(stopLine(r, reproLoop.until), { loop: r, until: reproLoop.until });
    else console.log(`${stamp()} ${stopLine(r, reproLoop.until)}`);
  } catch (e: any) { await reproFail(e.message); }
})();
// Stuck Sims, as watchdog.ts judges them: same spot and interaction for 6 checks (3 min), or routing without moving for 3.
// Flagged Sims recently teleported by a shortcut are marked, so every shortcut gets checked for leaving Sims stuck.
const simSeen = new Map<string, { key: string; checks: number; reported: boolean }>();
const stuck: any[] = [];
async function checkStuck() {
  const r = await ag('sims_activity');
  if (!r.ok) return;
  appendFileSync(`${pre}-activity.jsonl`, JSON.stringify({ t: Date.now() - t0, sims: r.result.map((s: any) => [s.name, s.interaction, s.lot, s.at_home, s.community, s.min_need, s.routing]) }) + '\n');
  const moved: Record<string, string> = (await ag('patches')).result?.moved ?? {}; // every Sim a shortcut moved
  for (const sim of r.result) {
    const key = `${sim.x},${sim.z},${sim.interaction},${sim.routing}`;
    const prev = simSeen.get(sim.id);
    const e = prev && prev.key === key ? { key, checks: prev.checks + 1, reported: prev.reported } : { key, checks: 1, reported: false };
    simSeen.set(sim.id, e);
    if (!e.reported && mayBeStuck(sim.interaction, !!sim.routing, !!sim.in_transit) && e.checks >= (sim.routing ? 3 : 6)) {
      e.reported = true;
      stuck.push({ ...sim, checks: e.checks, shortcut: moved[sim.name] ?? false });
    }
  }
}
writeFileSync(`${pre}-upkeep.log`, '');
writeFileSync(`${pre}-machine.jsonl`, '');
const stopLoad = startMachineLoad(`${pre}-machine.jsonl`); // other processes and CPU clock (machine-load.ts)
writeFileSync(`${pre}-activity.jsonl`, '');
const upkeep = (async () => {
  while (!stopped && Date.now() - t0 < secs * 1000) {
    const k = await ag('keep_alive', { threshold: -50 }); await ag('decline_requests');
    const ap = await ag('autopilot');
    await checkStuck();
    const cleared: string[] = ap.ok ? ap.result.cleared : [];
    appendFileSync(`${pre}-upkeep.log`, `${stamp()} ${k.ok ? `topped ${k.result.topped_up.length} repaired ${k.result.repaired?.length ?? 0}` : k.error} ${ap.ok ? `speedfix ${ap.result.speed_fixes} dialogs ${cleared.length}${cleared.length ? ` (${cleared.join('; ')})` : ''}${ap.result.restarted ? ' autopilot restarted' : ''}` : ''}\n`);
    // which game dialog the autopilot answered, beside the story steps (docs/171 §13)
    if (story && cleared.length) appendFileSync(`${pre}-story.jsonl`, JSON.stringify({ kind: 'dialog', t: Date.now(), answered: cleared, ...(ap.result.restarted && { restarted: true }) }) + '\n');
    untilCheck();
    if (stopped) break;
    await sleep(30000);
  }
})();

const run = (args: string[], file: string) => new Promise(r => {
  const p = spawn(process.execPath, args, { stdio: ['ignore', 'pipe', 'inherit'] });
  p.stdout.pipe(createWriteStream(file)); p.on('exit', r);
});
// none with --taskstall: two samplers suspending the sim thread deadlocked the probe agent (bp-detour, 2026-10-13)
const window = abNames.length || values.taskstall || values.repro || values.inroom || values.watchstalk || values.livereads2 || values.f4b || vresOn || values['no-samplers'] ? 0 : Math.min(180, Math.floor(secs / 2) - 60); // 3 min each in a 10 min run; none in --ab mode
const mainSample = async (t: number) => {
  try {
    const r = await get(`threadstack?which=${values.mainstack}&frames=24`, 5000);
    appendFileSync(`${pre}-mainstack.jsonl`, r.threads.filter((x: any) => x.stack).map((x: any) =>
      JSON.stringify({ t, thread: x.thread, role: x.role, name: x.name, start: x.start, module: x.module, stack: x.stack }) + '\n').join(''));
  } catch { /* sample lost */ }
};
// --repro with --mainstack: main-thread stacks only (they never suspend the sim thread), the whole window, so the repro steps are covered
// also with --no-samplers: main-thread stacks for the whole window (frame-cause runs; the main thread is suspended per sample, so
// such a run gives causes, not frame rates)
const mainOnly = !!((values.repro || values['no-samplers']) && values.mainstack && !abNames.length && !values.taskstall);
if (values.mainstack) writeFileSync(`${pre}-mainstack.jsonl`, '');
const mainOnlyRun = mainOnly ? (async () => {
  console.log(`${stamp()} mainstack only (${values.repro ? 'repro' : 'no-samplers'} mode): ${secs - 15} s`);
  for (const end = at(secs - 15); !stopped && Date.now() < end;) { const t = Date.now(); await mainSample(t); await sleep(Math.max(0, 200 - (Date.now() - t))); }
})() : Promise.resolve();
await waitUntil(at(60));
console.log(`${stamp()} managed sampler ${window} s`);
// both samplers suspend the sim thread: <tag>-samplers.jsonl keeps each window's epoch span (hitch reports exclude it, docs/161 §6)
writeFileSync(`${pre}-samplers.jsonl`, '');
const samplerSpan = (name: string, t0: number) => appendFileSync(`${pre}-samplers.jsonl`, JSON.stringify({ name, t0, t1: Date.now() }) + '\n');
if (window) { const t = Date.now(); await run([join(ROOT, 'tools', 'probe', 'managed-sample.mjs'), String(window), '5', `${pre}-managed.jsonl`], `${pre}-managed.txt`); samplerSpan('managed', t); }
await waitUntil(at(secs / 2));
console.log(`${stamp()} native sampler ${window} s`);
writeFileSync(`${pre}-simstack.jsonl`, '');
if (values.mainstack && !window && !mainOnly) console.log(`${stamp()} mainstack skipped: no sampler window in this mode`);
let simLost = 0; // an empty simstack.jsonl went unnoticed for 3 days (2026-10-16-sampler-hang.md): print the loss
const nativeT0 = Date.now();
for (const end = Date.now() + window * 1000; !stopped && Date.now() < end;) {
  const t = Date.now();
  try {
    const r = await get('simstack?frames=24', 5000);
    if (r.stack?.length) appendFileSync(`${pre}-simstack.jsonl`, JSON.stringify({ t, stack: r.stack }) + '\n'); // busy or not found: no line
    else simLost++;
  } catch { simLost++; /* sample lost */ }
  // one line per sampled thread: the simstack {t, stack} plus thread tags; 'all' takes ~0.1 s per round (fewer sim samples, never more)
  if (values.mainstack) await mainSample(t);
  await sleep(Math.max(0, 200 - (Date.now() - t)));
}
if (window) { samplerSpan('native', nativeT0); console.log(`${stamp()} native sampler done: ${simLost} sim samples lost`); }
await Promise.all([upkeep, profDone, toggler, fwLoop, reproRun, mainOnlyRun, inroomDone, watchDone, lr2Done, f4bDone, vresDone, framesDone, nlogDone]);
if (framesOn && framesShared) { await sleep(1000); console.log(`${stamp()} frames: ${sliceFrames(framesShared, `${pre}-${secs}-frames.csv`, t0, Date.now())} rows of the load's frame log copied`); } // drained every 250 ms
if (values['camera-pan']) await ag('camera_pan', { stop: true });
stopLoad();
await restoreFixes();
console.log(`${stamp()} ${summarize(`${pre}-machine.jsonl`).line}`);
rng.simMinutes.end = (await ag('time')).result?.minutes_total ?? null;
if (rng.seed !== null) rng.restored = (await ag('seed_rng')).result?.restored ?? false;
writeFileSync(`${pre}-rng.json`, JSON.stringify(rng, null, 1));
if (values.taskstall) { try { const r = await get('taskstall?stop=1&n=0'); console.log(`${stamp()} taskstall: ${JSON.stringify(r.counts ?? r)}`); } catch (e) { console.log(`${stamp()} taskstall stop failed: ${e.message}`); } }
if (abNames.length) await get('native').then(c => appendFileSync(`${pre}-ab-native.jsonl`, JSON.stringify({ i: slices.length, s: (Date.now() - t0) / 1000, c }) + '\n')).catch(() => {});
if (abNames.length) await ag('patches').then(r => appendFileSync(`${pre}-ab-fixes.jsonl`, JSON.stringify({ i: slices.length, s: (Date.now() - t0) / 1000, c: numericFields(r.result),
  logs: Object.fromEntries(Object.entries(r.result ?? {}).filter(([k]) => /_log$/.test(k))) }) + '\n')).catch(() => {});
if (fixesRequire) {
  const fails = checkRequire(fixesRequire, (await ag('patches')).result);
  console.log(`${stamp()} ${fails.length ? `FAIL: fixes counters: ${fails.join('; ')}` : `PASS: fixes counters ${fixesRequire}`}`);
  if (fails.length) process.exitCode = 1;
}
if (reproEntry.stepsRequire) { // repro step answers (fixes-require.ts stepTally): the repro's case fired, not only a counter moved
  const fails = checkRequire(reproEntry.stepsRequire, stepTally(stepAnswers));
  console.log(`${stamp()} ${fails.length ? `FAIL: repro steps: ${fails.join('; ')}` : `PASS: repro steps ${reproEntry.stepsRequire}`}`);
  if (fails.length) process.exitCode = 1;
}
if (values['frame-lead']) { // main-thread repro pass rule (frame-lead.ts, docs/169): frames led by the target service, lot adds
  const r = checkTag(OUT, values.tag!, parseRule(values['frame-lead']));
  console.log(`${stamp()} ${r.fails.length ? `FAIL: frame lead: ${r.fails.join('; ')}` : `PASS: frame lead: ${r.line}`}`);
  if (r.fails.length) process.exitCode = 1;
}
if (hitchTasks.length) {
  let hl: any[] = [];
  try { hl = readFileSync(`${pre}-${secs}-hitches.jsonl`, 'utf8').split(/\r?\n/).filter(Boolean).map(l => JSON.parse(l)); } catch { /* no profiler file: every rule fails */ }
  const { fails, counts } = checkHitchTasks(hitchTasks, hl);
  console.log(`${stamp()} ${fails.length ? `FAIL: hitch tasks: ${fails.join('; ')}` : `PASS: hitch tasks ${hitchTasks.map((r, i) => `${r} (${counts[i]})`).join(', ')}`}`);
  if (fails.length) process.exitCode = 1;
}
if (abNames.length) { writeFileSync(`${pre}-ab.json`,JSON.stringify({ names: abNames, t0, slices }, null, 1)); /* t0: profile start (epoch ms), to join <tag>-machine.jsonl */ await ag('patches', Object.fromEntries(abNames.filter(n => n !== 'fwnull').map(n => [n, true]))); }
if (fwCalls) { const fw = fixedWorkSummary(parseRows(readFileSync(fwFile, 'utf8')), 1, secs); console.log(`${stamp()} ${fw.line}`); }
// per-arm ms/s (arm-ms.ts): slice + arm columns on the seconds rows (arm 1 on, 0 off, -1 not counted)
if (slices.length && (abNames.length || reproLoop)) {
  const sf = `${pre}-${secs}-seconds.csv`;
  try { writeFileSync(sf, addArmColumns(readFileSync(sf, 'utf8'), reproLoop ? slices.map(sl => ({ ...sl, loop: true })) : slices)); }
  catch (e: any) { console.log(`${stamp()} arm columns not added to ${sf}: ${e.message}`); }
}
if (reproLoop && slices.length) { // the last firing's end snapshot, then the arm slices (loop: hitch-ab keeps every firing slice whole)
  await get('native').then(c => appendFileSync(`${pre}-ab-native.jsonl`, JSON.stringify({ i: slices.length, s: (Date.now() - t0) / 1000, c }) + '\n')).catch(() => {});
  writeFileSync(`${pre}-ab.json`, JSON.stringify({ names: [`loop:${values.repro}`], loop: true, slices }, null, 1));
}
writeFileSync(`${pre}-stuck.json`, JSON.stringify(stuck, null, 1));
if (stuck.length) await reproSave(ag, values.tag!, stuck);
console.log(`${stamp()} stuck Sims: ${stuck.length}${stuck.length ? ` (${stuck.map(x => `${x.name}${x.shortcut ? ` [teleported: ${x.shortcut}]` : ''} on ${x.interaction}`).join('; ')})` : ''}`);
if (boats0.length) {
  const boats1 = (await ag('houseboats')).result ?? [];
  const moved = boats1.filter((b: any) => { const o = boats0.find((x: any) => x.lot === b.lot); return !o || Math.hypot(o.x - b.x, o.z - b.z) > 1; });
  writeFileSync(`${pre}-houseboats.json`, JSON.stringify({ start: boats0, end: boats1 }, null, 1));
  console.log(`${stamp()} houseboats: ${boats1.length}, moved: ${moved.map((b: any) => `${b.lot} (${b.household ?? 'empty'})`).join(', ') || 'none'}`);
}
await alive.check();
console.log(`${stamp()} done; game ${(await ag('game_state')).ok ? 'alive' : 'NOT answering'}`);
