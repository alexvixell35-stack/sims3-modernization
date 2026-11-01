// Unattended game-test queue: runs a list of jobs back to back, one game start per job, and records one result row each.
//   node tools/harness/queue.ts tools/harness/queues/<name>.json [--from <job id>] [--only id,id] [--dry-run] [--no-smoke] [--stop-on-fail]
//   node tools/harness/queue.ts --self-check          queue parsing, ini merge, verdict and usage parsing, content stash round trip, no game
//   node tools/harness/queue.ts --restore-content     put CC/Store packages back from the stash manifest of an unfinished run
// Queue file: a JSON list of jobs (or { "jobs": [...] }):
//   { id, note?, asi: "tested" | "<path from repo root>", ini?: { "Section.Key": value }, fixes?: { install?: [ids], uninstall?: [ids] },
//     save?: "<name>[.sims3]" | "menu", content?: "as-is" | "no-cc" | "no-store", run: {repro: id} | {matrix: [args]} | {saveReload: [save-reload.ts args]} | {soak: {minutes, speed?}} | {menuMemory: {waitS?, draws?}}
//     | {profile: {minutes, seed?, repro?, mainstack?}} | {routelock: {minutes, speed?}} | {age: {days, checkpoint, keepAlive?, save?}}, gameArgs?: ["-disablestoryprogression"], defaultIni?: { "Config.SimulatorRandomSeed": n },
//     s3ppIni?: { "Patches.MonoTypeEHashProbe": 0 }, s3ssToml?: { "patches.LotStreamingOptimizations.enabled": false }, minCounters?: 20, timeoutMin?,
//     pin?: false | { cpus?: "4-15", priority?: "High", others?: boolean | [names] } }   (also queue-level beside "jobs"; pin.ts)
// pinReason: why a job pins a game-copy asi / fixesPackage (release or baseline arm, published-release check, a job about one old
// build); without it, an asi older than the source or a game-copy package older than the ts3fixes source gets a WARN pin line.
// pin (default on, TS3_PIN=off): while the game is up TS3W.exe runs on the game CPUs at High priority and heavy user apps on
// the rest, put back after each job and on exit (pin.ts). Every job samples machine load (machine-load.ts) into
// <queue>-<date>-<job>-machine.jsonl: row.machine (full line), row.noise (low|medium|high) and the short form in the md note.
// Timing verdict (docs/168 §10, run-verdict.ts rowTiming): row.timing valid | valid-half | valid-single | invalid + row.timingWhy (and per
// profile.repros window rr.timing), from the row's noise, verdict, the measurement tools of the ini it ran with and the A/B
// iterations per arm; shown in the md note. hitch-ab.ts --reuse collects valid runs as timing evidence.
// Profile (timing) jobs: ~2.5 min in, other CPU >= 10% or a clock drop over the samples so far stops the job ("noisy machine
// early: ..."); noiseCheck: false skips that for correctness-only jobs. After the run every enabled feature in ACTIVITY must
// show its counter rising and profile.fixesOn its 'fixes on: ... (verified)' line, else the run is void. Profile jobs whose ids
// differ only in the last -part get a WARN pair line (queue log, --dry-run) when profOpts or noSamplers differ (fixesOff left out: on/off arms differ there by design).
// Night runs (TS3_NIGHT=1) and --close-spotify stop Spotify at the start; daytime runs leave it alone (Rodney 2026-10-26).
// Measurement smoke (Rodney 2026-10-25, AGENTS.md Rules): when the selected timing jobs (profile, noiseCheck not false) sum to
// more than SMOKE_OVER_MIN minutes, a real run first runs one smoke job (queue-level "smoke": {job}, else the first timing job
// cut to SMOKE_MIN minutes and its first profile.repros item, id <id>-smoke) as a child queue <queue>-smoke, and stops before
// the long run unless: no timing job has a heavy tool on (hitch-ab.ts toolsOn + docs/data/tool-costs.json: RouteSnap audits,
// samplers, Frida agents, ...), the smoke job passed, every A/B or armed-loop window splits per arm in hitch-ab.ts (runIters)
// and wait-ab.ts, ts3native.log has [P6Waits] arm 0 and 1 lines when the job sets P6Waits.Enabled, and its log has no
// native_set read-back failure. A pass is cached in probe-out/queue/smoke-cache.json by queue file + smoke asi sha256 + last
// tools/harness commit. --no-smoke skips it (correctness-only queues; a queue without timing jobs never smokes).
// Hold (Rodney 2026-10-25): before each job and the smoke job, while probe-out/queue/hold exists (a crash-triage build, hold.ts)
// the queue waits (line every 5 min, warning at 60 min); probe-out/queue/job-running names the running job for hold.ts wait-job-end.
// Noisy rerun: a timing job (profile, noiseCheck not false) stopped by the early noise check or with a high end verdict runs once
// more at the end of the same run as <id>-rerun; both rows are kept and the md note says which one counts.
// Build check (docs/TESTING.md "Build verification", tools/builds.ts, docs/data/builds.json): before every game start Bin/ts3native.asi
// must be the job's asi (sha256; DB entry and version when the DB knows it) and the installed ts3fixes.package tuning digest must be
// fixesBuild's (DB name, job or queue level) or by default build/ts3fixes.package's (and byte-equal to it); after start-up the
// first line of ts3native.log must name that version. A miss FAILs the job with the reason, nothing run; row.buildCheck holds the hashes.
// Save guard: the Baseline-*/Story-* save the job loads and ApexTest-Town are hashed before and after every job (row.saveHashes);
// a change FAILs the job and stops the queue (2026-10-24: Baseline-BP-Spring was overwritten by a manual Save).
// minCounters: the job refuses (error row, nothing measured) when /native after the load reports an older counters version
// than this, i.e. the asi lacks the module the ini switches on (verify.json: [Verify] needs v20). A missing asi refuses
// before the game is touched. The full /native before and after goes to probe-out/queue/<queue>-<date>-<job>-native.json
// (verify-verdicts.ts reads it with the profile files to fill docs/51). With ini Verify.Enabled the dump after the load also
// zeroes the [Verify] maxima (/native?vfresetmax=1, read first), so `after` - `before` and the after maxima cover play only
// (the dump says maxReset: true).
// nativeRequire: "name>=n,..." over the /native after - before of the job (arrays as name.i, e.g. sypPlans.1 = failed sync plans);
// logRequire: { "<regex>": min lines } over the job's ts3native.log copy. Either failing turns the row FAIL (repro event checks, docs/169).
// logForbid: { "<regex>": max lines } over the same copy (0 = the line must not appear, e.g. a replay audit MISMATCH); more turns the row FAIL.
// routingFailFast: { ms, scope: "unseen" | "all", allow?: [{ match, reason, ref }] } (Rodney 2026-10-26, routing-failfast.ts): from the profile
// start, every 30 s, routing above ms (unseen fallback lines; with scope all also sync plans, own-job waits, route-led hitches)
// stops the job at once with FAIL: routing fail-fast: ...; triaged false positives go into routing-failfast-allow.json.
// earlyGates: [{ afterSec, counter, op, value, why }] (Rodney 2026-10-31, profile jobs): live /native paths (autshadow.req) or ts3fixes
// counters (fixes:autshadow_gm6_faults, + sums) read every GUARD_MS from the profile start; past afterSec a gate that does not hold
// stops the job at once with FAIL: early gate: <why> (<counter>=<v>) (see earlyGateGuard / gateFail).
// stopQueueOnFail: true (job or queue level) or --stop-on-fail: after a FAIL row the queue stops with 'STOP: <job> failed (stopQueueOnFail): ...'
// (chain scripts grep it); a noisy timing job whose -rerun was queued does not stop it.
// scriptErrorsMax: N = at most N new ScriptError_*.xml in the user folder since the job start (known EA errors, known-script-errors.json,
// not counted); more turns the row FAIL (docs/191 bp-cc-load-smoke: synthetic CC loads, places and wears without script errors).
// profile: profile-world.ts --tag <queue>-<date>-<job> (--seed: seed_rng at the window start, docs/27); rng-compare.ts reads the rows.
// routelock: autopilot (default ultra) + the probe's /routelock watch for `minutes` (watchdog.ts runs alongside), then stop;
// the summary (G11 movable ms/s, unlocked gate entries, lock hold/wait) goes in the row as `routelock`, the full report with
// per-second series to probe-out/queue/<queue>-<date>-<job>-routelock.json (docs/18 S1).
// age: age-save.ts on the loaded save, saved under a NEW name (save, default <job id>-<date>; never Baseline-*, never an existing
// save), checkpoints <name>-dayN; per checkpoint (and the loaded save first): folder MB, bytes per resource type (save-stats.ts),
// the ts3native.log "save: result" total ms, /memory, sim day -> row.checkpoints and probe-out/queue/<queue>-<date>-<job>-age.csv
// (age-report.ts prints the same summary as the md row).
// menuMemory.draws: n RandomGen.NextDouble() draws at the menu into the row (rngDraws), for the docs/27 Default.ini seed check.
// gameArgs go to TS3W.exe (ts3probe.py serve --launch -- <args>); defaultIni is merged into Bin/Default.ini for the job, then put back;
// s3ppIni the same for Bin/Sims3Performance.ini (S3PP switches, e.g. its Mono caches on/off); s3ssToml the same for
// <user>/S3SS/S3SS.toml ("<table>.<key>", e.g. patches.LotStreamingOptimizations.enabled; S3SS reads it at game start);
// dxvkConf the same for Bin/dxvk.conf (plain "key = value" lines, e.g. "d3d9.textureMemory": 64; DXVK reads it at start).
// profile.repro: profile-world.ts --repro <id> runs that repros.json entry's steps at the window start (no stack samplers).
// profile.untilCompares: N -> profile-world.ts --until-compares (end the window early once N shadow compares exist; 2N if N comes before 5 min; minutes is the cap).
// profile.repro with a `loop` (repro-loop.ts, docs/168 §6): profile.minutes is the cap; the window ends once the repro's fixed-count
// stop rule holds; row.stop and the verdict keep 'stopped at N events after M min (...)' (or 'loop cap reached: ..., rule not met').
// profile.frameLead: {service, ms?, min, lotAdds?} -> profile-world.ts --frame-lead (frame-lead.ts pass rule: frames > ms led by that main-slot service, docs/169).
// profile.fixesOff: "name,name" -> profile-world.ts --fixes-off (those switches off for the whole run).
// profile.fixesReport: "prefix,prefix": every ts3fixes `patches` key starting with one (numbers before/after, lists as read at the
// end, e.g. unseenfbpre_callers) goes into row.fixActivity and the `ts3fixes activity:` line; every fixesOn switch name is a prefix too.
// profile.fixesRequire: "name>=1,name<5000" -> profile-world.ts --fixes-require (ts3fixes counter rules checked at the end; FAIL verdict when one fails).
// profile.hitchTasks: ["<regex>[@ms]>=N" | "<regex>[@ms]<N"] -> profile-world.ts --hitch-tasks (hitch lines naming the expected script task, docs/169; FAIL verdict when one fails).
// profile.fixesOn: "name,name" -> profile-world.ts --fixes-on (those switches on for the whole run; e.g. autshadow, whose tuning is off).
// profile.ab: "name,name" -> profile-world.ts --ab (runtime switches alternate on/off in one load), profile.slice: minutes per side.
// profile.abOnFirst: true -> --ab-on-first (counted slices on,off,off,on instead of off,on,on,off).
// profile.mainstack: main | all | <thread id> -> profile-world.ts --mainstack (main-thread stacks beside the sim sampler, docs/20 §7).
// profile.taskstall: MS -> profile-world.ts --taskstall (stage stalls over MS ms with their managed stack; S3PP SimulatorStageProfiler must be off).
// profile.inroom: true -> profile-world.ts --inroom (tools/frida/inroom-reads.js, in-room routing live reads, docs/111; inroom-report.ts reads <tag>-inroom.json).
// profile.watchstalk: true -> profile-world.ts --watchstalk (tools/frida/watch-stalk.js instead of the stutter profiler, docs/113; watch-stalk-report.ts reads <tag>-watchstalk.json);
//   "worker" -> also --watchstalk-worker (the stalk phase follows the route worker's Route_PlanLocked, docs/113 §8; needs RouteAsync on).
// profile.livereads2: true -> profile-world.ts --livereads2 (tools/frida/live-reads2.js, routing live reads pass 2, docs/114; live-reads2-report.ts reads <tag>-livereads2.json).
// profile.f4b: true -> profile-world.ts --f4b (tools/frida/f4b-reads.js, F-4b live read, docs/99 §12.6; f4b-report.ts reads <tag>-f4b.json).
// profile.vres: true -> profile-world.ts --vres (docs/133: callvirt inline-cache snapshots through the probe agent's /vcache; virtual-resolution-report.ts reads <tag>-vres.json).
// profile.vresDry: N -> profile-world.ts --vres-dry N (the same snapshots, N methods each, no call following: docs/133 §5 bisect).
// profile.noSamplers: true -> profile-world.ts --no-samplers (no stack samplers; profile.noProf: true -> --no-prof (no stutter
// profiler session) also without [ProbeSnap]). Both make a bisect job match bp-vres apart from the one probe under test.
// profile.noFrames: true -> --no-frames (no frame-times.js session); profile.cameraPan: true -> --camera-pan (docs/162 §4).
// profile.loopRule: {until?, maxMinutes?} -> --loop-rule: a loop repro's stop rule for this job only (A/B queues: minFirings, docs/168 §7).
// profile.profOpts: {stages?: false, tasks?: false} -> --prof-opts (stutter-profile.js -P fields; docs/168 §4 profiler cost control).
// profile.noEnv: true -> profile-world.ts --no-env (no test_env guard: the repro sets winter, weather or a full moon itself, docs/117).
// profile.envGuard: false -> profile-world.ts --no-env-guard (test_env at the start only; seasons then pass as in play: story runs, docs/171 §16).
// profile.story: {seed?, stories?, censusOnly?, ...} -> profile-world.ts --story '<json>' (story fuzzer on a Story-* scratch copy, docs/171 §7).
// ini ProbeSnap.Enabled=1 -> profile-world.ts --no-prof (no second Frida agent beside the int3 sampler: docs/67 §7).
// Call-site coverage (docs/133 §7): every job that is not a timing job (timing = profile with noiseCheck not false) gets
// ini ProbeSnap.CallSites=1 (self-disarming int3 sites, ts3native >= 0.36.64; older builds ignore the key) unless it sets
// ProbeSnap.Enabled or ProbeSnap.CallSites itself or says callSites: false (job level; true forces it on a timing job);
// never on worldimage jobs, and never on a profile job that attaches a second Frida agent (stutter profiler unless noProf,
// or inroom / watchstalk / livereads2 / f4b): one attaching while int3 sites trap crashed the game (docs/67 §7). The auto key
// is then dropped with one queue log line; callSites: true or an ini ProbeSnap.CallSites != 0 on such a job is a parse error.
// After the job its ts3native.log copy is merged into probe-out/callsites/db.json (tools/callsite-db.ts; one line in the queue log).
// profile.travel: "university" | "future" | "france" | "china" | "egypt" -> profile-world.ts --travel (save-matrix.ts travel case with
//   --stay first, then the window and repro in that world, docs/117 §2.11); the job's timeoutMin must cover the trip (~30 min).
// profile.repros: [id | {id, minutes?, fixesRequire?, hitchTasks?, nativeRequire?, frameLead?}] (docs/170 §1): those repros back to
//   back in one game load, one profile-world.ts window each with its own tag <job tag>-<id> (own queue log <tag>.log, profiler
//   files, stop rule, arms, ts3native.log slice <tag>-HHMM-ts3native.log), so every report reads them as separate runs; item
//   fields override the job's profile fields; nativeRequire per item over that window's /native delta. Between two windows the
//   carry-over check of reuseSession runs (its limits, default REUSE_DEFAULTS) and reloads when it fails. row.repros[] per repro.
// reuseSession: true | {maxJobs, simDelta, membersDelta, lotObjectsDelta, vasMB, minLargestFreeMB, simHours, stuck, onFail} (job or
//   queue level, default off; session-reuse.ts, docs/170 §2): consecutive profile jobs with the same game start (asi + sha, ini,
//   fixes, save, content, gameArgs, defaultIni, s3ppIni, s3ssToml, dxvkConf, pin) share the running game; only runtime switches
//   (patches, native_set, --ab, fixesOn/Off) differ. After each window every switch goes back to its value after the load and a
//   world snapshot is compared with the one after the load; past a limit, after maxJobs windows, a stuck Sim or a failed cleanup
//   the next job gets a fresh start. A crash or failed window ends the session: onFail auto = TS3_NIGHT=1 (night-run.ts) goes
//   on with a fresh start, daytime stops the queue. row.session says 'fresh load' or 'reused session (#n since load)' and the
//   drift line; the hitch reports drop the first minute only after a load or reload (<tag>-session.json, worst-hitches.ts).
// Per job: cycle.ts --restart; copy the job's asi to Bin; write Bin/ts3native.ini = repo native/ts3native/ts3native.ini + job.ini
// (+ [CrashForensics] Light=1 unless the job sets it, runs a release asi or says releaseDefaults: true, docs/67 §18)
// (fresh each job, so settings never leak); install-fixes.ts changes; start `ts3probe.py serve --launch` as a child; cycle.ts --save
// (or wait for the menu); run; read /memory and /native before and after; close the game; undo the installer changes.
// content (default "as-is"): with the game closed, move the CC sets offload-phase0.ts switches (fixes.json user-cc and
// store-repacks-test include/exclude, applied to the live Mods folder; no-cc = both, no-store = the Store repacks only) to
// CONTENT_STASH outside Mods, manifest written first; moved back after the job; caches cleared after each move (install-fixes
// cache-clear). ts3agent/ts3fixes/ts3-nointro and files other fixes installed (install-fixes state.json) are never moved. The
// queue refuses to start while a manifest exists (--restore-content). cycle.ts --restart sees the Mods change, clears scriptCache.
// ccSet (docs/191, e.g. "game-copy/cc-synth/cc-1k"): a tools/cc-gen.ts set; its .package files MOVE (same volume: renames, never
// a second copy of a multi-GB set) into Mods/Packages/cc-synth before the load and back after the job, through the same
// manifest-first stashContent/restoreContent (direction reversed: the set dir is the source, the manifest sits in Mods/Packages/
// cc-synth), cache-clear both ways. An interrupted run: --restore-content moves them back; the queue refuses to start until then.
// ccSet may also be a list of sets (docs/200 §10): each set's files move to Mods/Packages/cc-synth/<set>/ under one manifest.
// ccLots (docs/200 §10, e.g. "game-copy/cc-synth/lots-cc"): tools/cc-lot-gen.ts ebf_*.package lots COPIED into Documents/Library
// (manifest first, next to the content stash) and deleted after the job; never overwrites a Library file; --restore-content removes them.
// Reuses the other harness tools as child processes (cycle.ts, repro.ts, save-matrix.ts, watchdog.ts, crash-ledger.ts, ai-usage.ts).
// Crash (TS3W.exe gone or "game died"): crash-ledger triage goes in the row, the queue goes on; it stops when the same job has
// crashed twice (today's rows of this queue count, so a resume remembers) or two jobs crash in a row.
// After each job: `ai-usage.ts G <job id>`; at >= 80% in either window the queue pauses and prints the --from line to resume.
// Output: probe-out/queue/<queue>-<date>.jsonl (one row per job, times UTC), docs/data/results/<date>-queue-<queue>.md (table, UTC + PHT).
// After each job the game's Bin/ts3native.log is copied to probe-out/queue/<queue>-<date>-<job>-<HHMM>-ts3native.log (row.nativeLog),
// and Bin/ts3native-shadow.jsonl (P4 shadow records) to ...-<HHMM>-shadow.jsonl (row.shadowLog) when the job wrote one;
// Bin/autshadow.jsonl ([AutShadow] records; emptied at job start) to ...-<HHMM>-autshadow.jsonl (row.autshadowLog) when not empty.
// At the end the Bin ts3native.asi and ts3native.ini found at the start are put back as a pair (copies in probe-out/queue/bin-before/;
// until 2026-10-24 the asi came from native/ts3native/out/, which left an old build next to a job ini in Bin). The restored asi is
// named from docs/data/builds.json; a WARN line says when it is not a release build. Windows-only by nature (TS3W.exe, taskkill).
import { execFileSync, spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { appendFileSync, copyFileSync, cpSync, createWriteStream, existsSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, renameSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { homedir, tmpdir } from 'node:os';
import { basename, dirname, join, normalize, resolve, sep } from 'node:path';
import { parseArgs } from 'node:util';
import { CSV_HEADER, csvLine, summarize, type AgeRow } from './age-report.ts';
import { ledgerReport } from './mono-ledger-report.ts';
import { saveStats } from './save-stats.ts';
import { matchKnown } from './known-script-errors.ts';
import { dumpHungGame, GUARD_MS } from './game-alive.ts';
import { checkCfg as checkFailFast, describe as describeFailFast, routingGuard, type FailFast } from './routing-failfast.ts';
import { checkRequire, flatDelta, parseRequire } from './fixes-require.ts';
import { parseHitchTasks } from './hitch-tasks.ts';
import { parseRule } from './frame-lead.ts';
import { checkLoop, ruleText, STOP_LINE, withRule, type Loop } from './repro-loop.ts';
import { drift, driftLine, failMode, logCopy, parseReuse, readSwitches, REUSE_DEFAULTS, reproItems, restoreSwitches, reuseBlock, snapshot, stableKey, subTag, type Extra, type ReproItem, type ReuseOpts, type WorldSnap } from './session-reuse.ts';
import { saveForeground } from './focus-game.ts';
import { parseStory, smokeFails, storyFile } from './story-report.ts';
import { clearJob, markJob, waitHold } from './hold.ts';
import { dayFails } from './story-day.ts';
import { load as loadRun, type Run } from './freeze-report.ts';
import { describePin, gameCpus, pinConfig, pinStatePending, restorePins, startPinner, type PinOpts } from './pin.ts';
import { earlyNoise, startMachineLoad, summarize as loadSummary } from './machine-load.ts';
import { jsonlRows, queueFile, rowTiming, timingText } from './run-verdict.ts';
import { loadRun as hitchRun, runIters, toolCosts, toolsOn, withBaseIni } from './hitch-ab.ts';
import { mergeFile as mergeCallsites } from '../callsite-db.ts';
import { asiCheck, asiVersion, fixesCheck, loadDb, logVersionCheck, packageTuning, type Build } from '../builds.ts';

const { values, positionals } = parseArgs({ allowPositionals: true, options: {
  'dry-run': { type: 'boolean' }, from: { type: 'string' }, only: { type: 'string' }, 'self-check': { type: 'boolean' }, 'restore-content': { type: 'boolean' }, 'no-smoke': { type: 'boolean' }, 'stop-on-fail': { type: 'boolean' }, 'close-spotify': { type: 'boolean' }, port: { type: 'string', default: '47891' },
} });
const ROOT = resolve(import.meta.dirname, '..', '..');
const BIN = process.env.TS3_BIN ?? 'C:\\Program Files (x86)\\Steam\\steamapps\\common\\The Sims 3\\Game\\Bin';
const H = (f: string) => join(ROOT, 'tools', 'harness', f);
const TESTED_ASI = join(ROOT, 'native', 'ts3native', 'out', 'ts3native.asi');
const REPO_INI = join(ROOT, 'native', 'ts3native', 'ts3native.ini');
const OUT = join(ROOT, 'probe-out', 'queue');
const USER = process.env.TS3_USER ?? join(homedir(), 'Documents', 'Electronic Arts', 'The Sims 3'); // as cycle.ts / install-fixes.ts
// next to the user folder (same volume, so moves are renames), outside Mods so the game never mounts it
const CONTENT_STASH = process.env.TS3_CONTENT_STASH ?? join(dirname(USER), 'The Sims 3 content-stash');
const CC_DIR = join(USER, 'Mods', 'Packages', 'cc-synth'); // ccSet packages while a job runs (manifest.json here = installed)
const LIBRARY = join(USER, 'Library'), LOTS_MANIFEST = join(dirname(USER), 'The Sims 3 cc-lots.json'); // ccLots copies while a job runs
const FIXES_STATE = join(process.env.LOCALAPPDATA ?? join(homedir(), '.local', 'share'), 'ts3-fixes', 'state.json');
const base = `http://127.0.0.1:${values.port}`;
const sleep = (ms: number) => new Promise(r => setTimeout(r, ms));
const stamp = () => new Date().toISOString().slice(11, 19);
const log = (m: string) => console.log(`${stamp()} queue: ${m}`);

// ---------------------------------------------------------------- pure parts (covered by --self-check)
type Job = { id: string; note?: string; asi: string; ini?: Record<string, string | number | boolean>; fixes?: { install?: string[]; uninstall?: string[] };
  save?: string; content?: string; ccSet?: string | string[]; ccLots?: string; run: { repro?: string; saveReload?: string[]; matrix?: string[]; soak?: { minutes: number; speed?: string }; menuMemory?: { waitS?: number; draws?: number };
    profile?: { minutes: number; seed?: number; repro?: string; mainstack?: string; ab?: string; abOnFirst?: boolean; slice?: number; taskstall?: number; inroom?: boolean; watchstalk?: boolean | 'worker'; livereads2?: boolean; f4b?: boolean; vres?: boolean; noEnv?: boolean; envGuard?: boolean; travel?: string }; routelock?: { minutes: number; speed?: string }; worldimage?: { captures: number; settleS?: number; gapS?: number; speed?: string }; age?: { days: number; checkpoint: number; keepAlive?: number | 'off'; save?: string } }; gameArgs?: string[]; defaultIni?: Record<string, string | number | boolean>; s3ppIni?: Record<string, string | number | boolean>; s3ssToml?: Record<string, string | number | boolean>; dxvkConf?: Record<string, string | number | boolean>; minCounters?: number; noiseCheck?: boolean; callSites?: boolean; releaseDefaults?: boolean; nativeRequire?: string; logRequire?: Record<string, number>; logForbid?: Record<string, number>; routingFailFast?: FailFast; earlyGates?: EarlyGate[]; stopQueueOnFail?: boolean; scriptErrorsMax?: number; timeoutMin?: number; pin?: false | PinOpts;
  saveMatrix?: { fixes?: 'all' | 'shipped' }; fixesBuild?: string; fixesPackage?: string; pinReason?: string; rerunOf?: string; shadowCheck?: boolean };

export function parseQueue(json: unknown): Job[] {
  const jobs = Array.isArray(json) ? json : (json as any)?.jobs;
  if (!Array.isArray(jobs) || !jobs.length) throw new Error('queue: expected a non-empty list of jobs (or { "jobs": [...] })');
  const seen = new Set<string>();
  for (const [i, j] of jobs.entries()) {
    const at = `job ${i + 1}${j?.id ? ` (${j.id})` : ''}`;
    if (typeof j?.id !== 'string' || !/^[\w.-]+$/.test(j.id)) throw new Error(`${at}: id must be letters, digits, . _ -`);
    if (seen.has(j.id)) throw new Error(`${at}: duplicate id`);
    seen.add(j.id);
    if (typeof j.asi !== 'string' || !j.asi) throw new Error(`${at}: asi must be "tested" or a path`);
    if (j.fixesBuild === undefined && !Array.isArray(json) && (json as any).fixesBuild !== undefined) j.fixesBuild = (json as any).fixesBuild; // queue-level default
    if (j.fixesBuild !== undefined && !(typeof j.fixesBuild === 'string' && /^[\w.+-]+$/.test(j.fixesBuild))) throw new Error(`${at}: fixesBuild must be a ts3fixes name from docs/data/builds.json`);
    for (const k of Object.keys(j.ini ?? {})) if (!/^\w+\.\w+$/.test(k)) throw new Error(`${at}: ini key "${k}" must be Section.Key`);
    for (const k of Object.keys(j.defaultIni ?? {})) if (!/^\w+\.\w+$/.test(k)) throw new Error(`${at}: defaultIni key "${k}" must be Section.Key`);
    for (const k of Object.keys(j.s3ppIni ?? {})) if (!/^\w+\.\w+$/.test(k)) throw new Error(`${at}: s3ppIni key "${k}" must be Section.Key`);
    for (const k of Object.keys(j.s3ssToml ?? {})) if (!/^\w+(\.\w+)+$/.test(k)) throw new Error(`${at}: s3ssToml key "${k}" must be table.key (e.g. patches.LotStreamingOptimizations.enabled)`);
    for (const k of Object.keys(j.dxvkConf ?? {})) if (!/^\w+\.\w+$/.test(k)) throw new Error(`${at}: dxvkConf key "${k}" must be component.option (e.g. d3d9.textureMemory)`);
    if (j.gameArgs && !(Array.isArray(j.gameArgs) && j.gameArgs.every((x: unknown) => typeof x === 'string' && x.startsWith('-')))) throw new Error(`${at}: gameArgs must be a list of -switches`);
    for (const k of ['install', 'uninstall']) if (j.fixes?.[k] && !Array.isArray(j.fixes[k])) throw new Error(`${at}: fixes.${k} must be a list of ids`);
    const kinds = Object.keys(j.run ?? {}).filter(k => ['repro', 'matrix', 'soak', 'menuMemory', 'profile', 'routelock', 'worldimage', 'age', 'saveReload'].includes(k));
    if (kinds.length !== 1 || Object.keys(j.run).length !== 1) throw new Error(`${at}: run needs exactly one of repro, matrix, soak, menuMemory, profile, routelock, worldimage, age, saveReload`);
    if (j.run.saveReload !== undefined && !(Array.isArray(j.run.saveReload) && j.run.saveReload.every((x: unknown) => typeof x === 'string'))) throw new Error(`${at}: run.saveReload must be a list of save-reload.ts args`);
    if (j.run.worldimage) {
      const w = j.run.worldimage;
      if (!(Number.isInteger(w.captures) && w.captures > 0)) throw new Error(`${at}: run.worldimage.captures must be a positive integer`);
      for (const k of ['settleS', 'gapS']) if (w[k] !== undefined && !(Number(w[k]) >= 0)) throw new Error(`${at}: run.worldimage.${k} must be >= 0 seconds`);
      if (w.speed && !['normal', 'fast', 'ultra'].includes(w.speed)) throw new Error(`${at}: run.worldimage.speed must be normal, fast or ultra`);
      if (!j.save || j.save === 'menu') throw new Error(`${at}: worldimage needs a save to load`);
      if (Number(j.ini?.['WorldImage.Enabled']) !== 1) throw new Error(`${at}: worldimage needs ini WorldImage.Enabled=1`);
      // images hold the game's whole memory, personal data included: only under the repo's ignored game-copy/ folder
      if (j.ini['WorldImage.OutDir'] !== undefined && !underGameCopy(String(j.ini['WorldImage.OutDir']))) throw new Error(`${at}: WorldImage.OutDir must be under game-copy/ (images hold personal data)`);
      if (Number(j.ini['WorldImage.MaxCaptures'] ?? 5) < w.captures) throw new Error(`${at}: WorldImage.MaxCaptures is below run.worldimage.captures`);
    }
    if (j.run.age) {
      const a = j.run.age;
      if (!(Number.isInteger(a.days) && a.days > 0)) throw new Error(`${at}: run.age.days must be a positive integer`);
      if (!(Number.isInteger(a.checkpoint) && a.checkpoint >= 0)) throw new Error(`${at}: run.age.checkpoint must be an integer >= 0 (0 = none)`);
      if (a.keepAlive !== undefined && a.keepAlive !== 'off' && typeof a.keepAlive !== 'number') throw new Error(`${at}: run.age.keepAlive must be a number or "off"`);
      if (a.save !== undefined && !(typeof a.save === 'string' && /^[\w.-]+$/.test(a.save))) throw new Error(`${at}: run.age.save must be letters, digits, . _ -`);
      // age-save.ts saves under this name: it must never be a baseline (the loaded save is only read)
      if (/^baseline-/i.test(a.save ?? j.id)) throw new Error(`${at}: run.age.save (default the job id) must not start with Baseline-`);
      if (!j.save || j.save === 'menu') throw new Error(`${at}: age needs a save to load`);
    }
    if (j.run.repro !== undefined && typeof j.run.repro !== 'string') throw new Error(`${at}: run.repro must be a repro id`);
    if (j.run.matrix !== undefined && !Array.isArray(j.run.matrix)) throw new Error(`${at}: run.matrix must be a list of save-matrix.ts args`);
    // saveMatrix.fixes -> save-matrix.ts --fixes: "shipped" = release mode (save fixes as the shipped ini set them, routing features compared)
    if (j.saveMatrix !== undefined && (!j.run.matrix || !['all', 'shipped'].includes(String(j.saveMatrix?.fixes)))) throw new Error(`${at}: saveMatrix needs run.matrix and fixes "all" or "shipped"`);
    if (j.run.soak && !(j.run.soak.minutes > 0)) throw new Error(`${at}: run.soak.minutes must be > 0`);
    if (j.run.profile && !(j.run.profile.minutes > 0)) throw new Error(`${at}: run.profile.minutes must be > 0`);
    if (j.run.profile?.seed !== undefined && !Number.isInteger(j.run.profile.seed)) throw new Error(`${at}: run.profile.seed must be an integer`);
    if (j.run.profile?.repro !== undefined && typeof j.run.profile.repro !== 'string') throw new Error(`${at}: run.profile.repro must be a repro id`);
    if (j.run.profile?.ab !== undefined && !/^[a-z0-9]+(,[a-z0-9]+)*$/.test(String(j.run.profile.ab))) throw new Error(`${at}: run.profile.ab must be comma-separated switch names`);
    if (j.run.profile?.fixesReport !== undefined && !/^[a-z0-9_]+(,[a-z0-9_]+)*$/.test(String(j.run.profile.fixesReport))) throw new Error(`${at}: run.profile.fixesReport must be comma-separated counter name prefixes`);
    if (j.run.profile?.fixesOff !== undefined && !/^[a-z0-9]+(,[a-z0-9]+)*$/.test(String(j.run.profile.fixesOff))) throw new Error(`${at}: run.profile.fixesOff must be comma-separated switch names`);
    if (j.run.profile?.fixesRequire !== undefined) parseRequire(String(j.run.profile.fixesRequire));
    if (j.run.profile?.hitchTasks !== undefined) parseHitchTasks(j.run.profile.hitchTasks);
    if (j.run.profile?.frameLead !== undefined) parseRule(JSON.stringify(j.run.profile.frameLead));
    if (j.run.profile?.fixesOn !== undefined && !/^[a-z0-9]+(,[a-z0-9]+)*$/.test(String(j.run.profile.fixesOn))) throw new Error(`${at}: run.profile.fixesOn must be comma-separated switch names`);
    if (j.run.profile?.untilCompares !== undefined && !(Number.isInteger(j.run.profile.untilCompares) && j.run.profile.untilCompares > 0)) throw new Error(`${at}: run.profile.untilCompares must be a positive integer`);
    if (j.run.profile?.abOnFirst !== undefined && (typeof j.run.profile.abOnFirst !== 'boolean' || !j.run.profile.ab)) throw new Error(`${at}: run.profile.abOnFirst must be true/false and needs run.profile.ab`);
    if (j.run.profile?.slice !== undefined && !(Number(j.run.profile.slice) > 0)) throw new Error(`${at}: run.profile.slice must be > 0 minutes`);
    if (j.run.profile?.inroom !== undefined && typeof j.run.profile.inroom !== 'boolean') throw new Error(`${at}: run.profile.inroom must be true or false`);
    if (j.run.profile?.watchstalk !== undefined && typeof j.run.profile.watchstalk !== 'boolean' && j.run.profile.watchstalk !== 'worker') throw new Error(`${at}: run.profile.watchstalk must be true, false or "worker"`);
    if (j.run.profile?.livereads2 !== undefined && typeof j.run.profile.livereads2 !== 'boolean') throw new Error(`${at}: run.profile.livereads2 must be true or false`);
    if (j.run.profile?.f4b !== undefined && typeof j.run.profile.f4b !== 'boolean') throw new Error(`${at}: run.profile.f4b must be true or false`);
    if (j.run.profile?.vres !== undefined && typeof j.run.profile.vres !== 'boolean') throw new Error(`${at}: run.profile.vres must be true or false`);
    if (j.run.profile?.vresDry !== undefined && !(Number.isInteger(j.run.profile.vresDry) && j.run.profile.vresDry > 0)) throw new Error(`${at}: run.profile.vresDry must be a positive integer`);
    for (const k of ['noSamplers', 'noProf', 'noFrames', 'cameraPan']) if (j.run.profile?.[k] !== undefined && typeof j.run.profile[k] !== 'boolean') throw new Error(`${at}: run.profile.${k} must be true or false`);
    if (j.run.profile?.loopRule !== undefined && (typeof j.run.profile.loopRule !== 'object' || !j.run.profile.repro)) throw new Error(`${at}: run.profile.loopRule must be {until?, maxMinutes?} with run.profile.repro`);
    if (j.run.profile?.noEnv !== undefined && typeof j.run.profile.noEnv !== 'boolean') throw new Error(`${at}: run.profile.noEnv must be true or false`);
    if (j.run.profile?.envGuard !== undefined && typeof j.run.profile.envGuard !== 'boolean') throw new Error(`${at}: run.profile.envGuard must be true or false`);
    if (j.run.profile?.story !== undefined && (typeof j.run.profile.story !== 'object' || j.run.profile.story === null || Array.isArray(j.run.profile.story))) throw new Error(`${at}: run.profile.story must be a story config object (docs/171 §7)`);
    if (j.run.profile?.travel !== undefined && !['university', 'future', 'france', 'china', 'egypt'].includes(j.run.profile.travel)) throw new Error(`${at}: run.profile.travel must be university, future, france, china or egypt`);
    if (j.run.profile?.mainstack !== undefined &&!/^(main|all|\d+)$/.test(String(j.run.profile.mainstack))) throw new Error(`${at}: run.profile.mainstack must be main, all or a thread id`);
    if (j.run.menuMemory?.draws !== undefined && !(Number.isInteger(j.run.menuMemory.draws) && j.run.menuMemory.draws > 0)) throw new Error(`${at}: run.menuMemory.draws must be a positive integer`);
    if (j.run.soak?.speed && !['normal', 'fast', 'ultra'].includes(j.run.soak.speed)) throw new Error(`${at}: run.soak.speed must be normal, fast or ultra`);
    if (j.run.routelock && !(j.run.routelock.minutes > 0)) throw new Error(`${at}: run.routelock.minutes must be > 0`);
    if (j.run.routelock?.speed && !['normal', 'fast', 'ultra'].includes(j.run.routelock.speed)) throw new Error(`${at}: run.routelock.speed must be normal, fast or ultra`);
    if (j.run.menuMemory && j.save && j.save !== 'menu') throw new Error(`${at}: menuMemory runs at the menu, set save "menu" or leave it out`);
    if (!j.save && !j.run.repro && !j.run.menuMemory) throw new Error(`${at}: save is required (a save name or "menu")`);
    if (j.noiseCheck !== undefined && typeof j.noiseCheck !== 'boolean') throw new Error(`${at}: noiseCheck must be true or false`);
    if (j.callSites !== undefined && typeof j.callSites !== 'boolean') throw new Error(`${at}: callSites must be true or false`);
    if (j.releaseDefaults !== undefined && typeof j.releaseDefaults !== 'boolean') throw new Error(`${at}: releaseDefaults must be true or false`);
    // fixesPackage: a shipped ts3fixes.package (path from the repo root) installed over cycle.ts's build/ copy before each start (repro jobs: the start-path controls, release-validate *-v016)
    if (j.fixesPackage !== undefined && (typeof j.fixesPackage !== 'string' || !j.fixesPackage || j.fixesBuild !== undefined || !(j.run?.profile || j.run?.soak || j.run?.repro)))
      throw new Error(`${at}: fixesPackage must be a path, only on profile/soak/repro jobs, not with fixesBuild`);
    if (j.pinReason !== undefined && (typeof j.pinReason !== 'string' || !j.pinReason.trim())) throw new Error(`${at}: pinReason must be a non-empty text (why this job pins its asi/package)`);
    if (fridaAttach(j) && (j.callSites || Number(j.ini?.['ProbeSnap.CallSites']))) throw new Error(`${at}: ProbeSnap.CallSites with a second Frida agent (${fridaAttach(j)}): int3 traps + agent attach crashed the game (docs/67 §7); set run.profile.noProf (and drop the side probe) or callSites false`);
    if (j.timeoutMin !== undefined && !(j.timeoutMin > 0)) throw new Error(`${at}: timeoutMin must be > 0`);
    if (j.minCounters !== undefined && !(Number.isInteger(j.minCounters) && j.minCounters > 0)) throw new Error(`${at}: minCounters must be a positive integer (a counters version)`);
    if (j.nativeRequire !== undefined) parseRequire(j.nativeRequire);
    if (j.logRequire !== undefined) for (const [re, n] of Object.entries(j.logRequire)) { new RegExp(re); if (!(Number.isInteger(n) && n > 0)) throw new Error(`${at}: logRequire "${re}" must map to a positive integer`); }
    if (j.logForbid !== undefined) for (const [re, n] of Object.entries(j.logForbid)) { new RegExp(re); if (!(Number.isInteger(n) && n >= 0)) throw new Error(`${at}: logForbid "${re}" must map to an integer >= 0`); }
    if (j.routingFailFast !== undefined) {
      checkFailFast(j.routingFailFast, at);
      if (!j.run.profile) throw new Error(`${at}: routingFailFast needs run.profile (it starts at the profile start)`);
      if (j.routingFailFast.scope === 'all' && (j.run.profile.ab || (j.run.profile as any).loopRule)) throw new Error(`${at}: routingFailFast scope "all" on an --ab / loop job: the off arm's plans and hitches would trip it; use scope "unseen"`);
    }
    if (j.scriptErrorsMax !== undefined && !(Number.isInteger(j.scriptErrorsMax) && j.scriptErrorsMax >= 0)) throw new Error(`${at}: scriptErrorsMax must be an integer >= 0`);
    if (j.stopQueueOnFail === undefined && !Array.isArray(json) && (json as any).stopQueueOnFail !== undefined) j.stopQueueOnFail = (json as any).stopQueueOnFail; // queue-level default
    if (j.stopQueueOnFail !== undefined && typeof j.stopQueueOnFail !== 'boolean') throw new Error(`${at}: stopQueueOnFail must be true or false`);
    if (j.earlyGates !== undefined) {
      checkGates(j.earlyGates, `${at}: earlyGates`);
      if (!j.run.profile) throw new Error(`${at}: earlyGates needs run.profile (they start at the profile start)`);
    }
    const ccDir = (x: unknown) => typeof x === 'string' && /^game-copy\/cc-synth\/[\w.-]+$/.test(x);
    if (j.ccSet !== undefined && !(ccDir(j.ccSet) || (Array.isArray(j.ccSet) && j.ccSet.length && j.ccSet.every(ccDir) && new Set(j.ccSet.map(x => basename(x))).size === j.ccSet.length))) throw new Error(`${at}: ccSet must be "game-copy/cc-synth/<set>" (a tools/cc-gen.ts output dir) or a list of distinct ones`);
    if (j.ccLots !== undefined && !ccDir(j.ccLots)) throw new Error(`${at}: ccLots must be "game-copy/cc-synth/<dir>" (a tools/cc-lot-gen.ts output dir)`);
    if (j.content !== undefined && !Object.hasOwn(CONTENT, j.content)) throw new Error(`${at}: content must be one of ${Object.keys(CONTENT).join(', ')}`);
    if (j.pin === undefined && !Array.isArray(json) && (json as any).pin !== undefined) j.pin = (json as any).pin; // queue-level default
    if (j.pin !== undefined && j.pin !== false && typeof j.pin !== 'object') throw new Error(`${at}: pin must be false or { cpus?, priority?, others? }`);
    try { pinConfig(j.pin); } catch (e) { throw new Error(`${at}: pin: ${(e as Error).message}`); }
    if (j.reuseSession === undefined && !Array.isArray(json) && (json as any).reuseSession !== undefined) j.reuseSession = (json as any).reuseSession; // queue-level default
    parseReuse(j.reuseSession, `${at}: reuseSession`);
    if (j.run.profile?.repros !== undefined) {
      reproItems(j.run.profile.repros, `${at}: run.profile.repros`);
      for (const k of ['repro', 'ab', 'untilCompares', 'travel', 'loopRule']) if (j.run.profile[k] !== undefined) throw new Error(`${at}: run.profile.repros takes no profile.${k} (each repro is its own window)`);
    }
  }
  return jobs;
}

// ts3fixes.applied (agent `patches` .applied): the AutShadow / AutLedger lines and every refused patch, for the job row
// (docs/51 §188: a refused hook used to leave no trace in the row)
export const fixesApplied = (applied: unknown): string[] => (Array.isArray(applied) ? applied.map(String) : [])
  .filter(l => /^(Aut(Shadow|Ledger)\b|tuned: )/.test(l) || (/^[\w.+]+\.\w+: /.test(l) && !/: ok$/.test(l))).slice(0, 40);

// tuning-gated patches (ts3fixes Fixes.ApplyTuned, docs/124 §17.8): a tuning is on but its patch did not go in, so the switch
// reads on while the code path is absent (golive-g7 round 1). Every profile/soak job with ts3fixes fails on such a line.
export const tunedFails = (applied: unknown): string[] => (Array.isArray(applied) ? applied.map(String) : [])
  .filter(l => /^tuned: .* MISSING$/.test(l)).map(l => `ts3fixes tuning-gated patch missing: ${l.slice(7)} (docs/124 §17.8)`);

// shadow jobs ([AutShadow] Enabled=1 and the autshadow switch on): a job without cuts or snapshot entries, or with ts3fixes
// "off for the session", measured nothing (mono-cost2 2026-10-29: four shadow jobs PASSED with cuts 0 entries 0, docs/67)
// Mode R (AutShadow.Mode 1, docs/122 §14.13): also FAIL when ts3fixes compared 0 pairs (autshadow_a0..a3 deltas from the
// row's fixActivity counters; none present = 0) or the worker spent implausibly little per run (< max(50 us, 2% of the
// reference per run) with reference runs): mono-cost4 ip-autshadow-r "passed" with a5 953, worker 14 us per run
export function shadowFails(ini: Record<string, unknown>, fixesOn: string | undefined, c0: any, c1: any, applied: string[],
  fix?: Record<string, { before?: number; after?: number }>): string[] {
  if (!Number(ini['AutShadow.Enabled']) || !/\bautshadow\b/.test(fixesOn ?? '')) return [];
  const out: string[] = [], a = c1?.autshadow ?? {};
  const cuts = Number(a.cuts ?? 0) - Number(c0?.autshadow?.cuts ?? 0), entries = Number(a.entries ?? 0);
  if (!(cuts > 0) || !(entries > 0)) out.push(`[AutShadow] no data: cuts ${cuts}, entries ${entries}`);
  const off = applied.find(l => /^AutShadow: off for the session/.test(l));
  if (off) out.push(off);
  if (Number(ini['AutShadow.Mode']) === 1) {
    const dv = (k: string) => Number(fix?.[k]?.after ?? 0) - Number(fix?.[k]?.before ?? 0);
    const cls = Object.keys(fix ?? {}).filter(k => /^autshadow_a[0-3]$/.test(k));
    const cmp = cls.reduce((sum, k) => sum + dv(k), 0);
    if (!(cmp > 0)) out.push(`[AutShadow] Mode R void: ${cmp} compared pairs (${cls.length ? cls.join('+') : 'no autshadow_a0..a3 counters'}; a4 ${dv('autshadow_a4')}, a5 ${dv('autshadow_a5')})`);
    // since mono-cost7 (A1-A3 0 on BP and IP, docs/122 §14.16) any worker answer that differs from the reference is a regression
    const mis = ['autshadow_a1', 'autshadow_a2', 'autshadow_a3'].filter(k => dv(k) > 0);
    if (mis.length) out.push(`[AutShadow] Mode R mismatch: ${mis.map(k => `${k.slice(10).toUpperCase()} ${dv(k)}`).join(', ')} (must be 0)`);
    const n = (k: string) => Number(a[k] ?? 0) - Number(c0?.autshadow?.[k] ?? 0);
    const refRuns = n('refRuns'), workRuns = n('workRuns');
    if (refRuns > 0) {
      const refPer = n('refUs') / refRuns, workPer = workRuns > 0 ? n('workUs') / workRuns : 0, floor = Math.max(50, 0.02 * refPer);
      if (workPer < floor) out.push(`[AutShadow] Mode R void: worker ${workPer.toFixed(1)} us per run (${workRuns} runs) < ${floor.toFixed(0)} us (reference ${refPer.toFixed(0)} us per run; overrun ${n('overrun')}, overrunIdle ${n('overrunIdle')})`);
    }
  }
  return out;
}

// earlyGates (Rodney 2026-10-31, fail fast: two void rain-h11 jobs burned ~12 min each with G-M6 skipping every definition):
// [{ afterSec, counter, op, value, why }] checked every GUARD_MS from the profile child's start while the game runs; once
// afterSec has passed a gate must hold at every check, else the child stops and the job FAILs with
// `early gate: <why> (<counter>=<v>)`. counter = a live /native path (`autshadow.req`, `autshadow.rainArm`, arrays `x.0`) or a
// ts3fixes `patches` counter as `fixes:<name>`; terms joined by + are summed (`fixes:autshadow_a0+fixes:autshadow_a1`).
// Values are absolute, not deltas since the load: the G-M6 sweep counts before the first read (shortcut: a reuseSession job
// would see the previous window's counts; gate deltas if such a queue needs gates).
export type EarlyGate = { afterSec: number; counter: string; op: string; value: number; why: string };
const GATE_OPS = ['>=', '<=', '==', '!=', '>', '<'];
export function checkGates(gs: unknown, at: string): void {
  if (!Array.isArray(gs) || !gs.length) throw new Error(`${at} must be a non-empty list`);
  for (const g of gs as any[]) {
    if (!(g?.afterSec > 0) || typeof g.counter !== 'string' || !/^(fixes:)?[\w.]+(\+(fixes:)?[\w.]+)*$/.test(g.counter)
      || !GATE_OPS.includes(g.op) || typeof g.value !== 'number' || typeof g.why !== 'string' || !g.why.trim())
      throw new Error(`${at}: ${JSON.stringify(g)} must be { afterSec > 0, counter: "path" | "fixes:name" (+ sums), op: ${GATE_OPS.join(' ')}, value: number, why }`);
  }
}
// one term's value; undefined = that source did not answer this round (skip), null = answered but no such counter
function gateTerm(t: string, native: any, fixes: any): number | null | undefined {
  const fx = t.startsWith('fixes:'), src = fx ? fixes : native;
  if (src == null) return undefined;
  const v = fx ? src[t.slice(6)] : t.split('.').reduce((o: any, k) => o?.[k], src);
  return typeof v === 'number' ? v : typeof v === 'boolean' ? Number(v) : null;
}
// '' = all due gates hold (or a source did not answer), else the FAIL reason of the first failing gate
export function gateFail(gs: EarlyGate[], elapsedSec: number, native: any, fixes: any): string {
  for (const g of gs) {
    if (elapsedSec < g.afterSec) continue;
    const vs = g.counter.split('+').map(t => gateTerm(t, native, fixes));
    if (vs.some(v => v === undefined)) continue;
    if (vs.some(v => v === null)) return `early gate: ${g.why} (${g.counter} missing)`;
    const v = (vs as number[]).reduce((s, x) => s + x, 0), w = g.value;
    const ok = g.op === '>=' ? v >= w : g.op === '<=' ? v <= w : g.op === '==' ? v === w : g.op === '!=' ? v !== w : g.op === '>' ? v > w : v < w;
    if (!ok) return `early gate: ${g.why} (${g.counter}=${v}) [want ${g.op}${w} from ${g.afterSec} s]`;
  }
  return '';
}
export const describeGates = (gs: EarlyGate[]) => `early gates every ${GUARD_MS / 1000} s from the profile start: ${gs.map(g => `${g.counter}${g.op}${g.value} from ${g.afterSec} s`).join(', ')}; a miss stops the job (FAIL: early gate: ...)`;

// ---- worldimage (docs/123 §8.6): capture job pieces
const GAME_COPY = join(ROOT, 'game-copy');
export const underGameCopy = (p: string) => normalize(resolve(ROOT, p)).toLowerCase().startsWith(normalize(GAME_COPY).toLowerCase() + sep);
// a worldimage job's ini: OutDir (game-copy\worldimage) and Helper (the repo's helper build, run in place, so nothing extra
// goes into Bin) filled in when the job leaves them out; other jobs unchanged
// docs/133 §7: the coverage key queue.ts adds (see the header); {} when the job is a timing job, opts out or sets it itself;
// never on worldimage jobs (memory captures restored elsewhere: no int3 bytes or handler state in them)
// docs/67 §7: which Frida agent profile-world.ts attaches beside the probe agent ('' = none); ProbeSnap.Enabled forces --no-prof
export const fridaAttach = (j: Job): string => {
  const p = j.run.profile; if (!p) return '';
  const side = (['inroom', 'watchstalk', 'livereads2', 'f4b'] as const).filter(k => p[k]);
  return [...(!p.noProf && !p.watchstalk && !Number(j.ini?.['ProbeSnap.Enabled']) ? ['stutter profiler'] : []), ...side].join(', ');
};
const callSitesWanted = (j: Job) => !j.run.worldimage && (j.callSites ?? !(j.run.profile && j.noiseCheck !== false)) && j.ini?.['ProbeSnap.CallSites'] === undefined && !Number(j.ini?.['ProbeSnap.Enabled']);
export const callSitesIni = (j: Job): Record<string, number> => callSitesWanted(j) && !fridaAttach(j) ? { 'ProbeSnap.CallSites': 1 } : {};
// the one queue log line when the auto key is dropped for a Frida agent ('' = nothing to say)
export const callSitesOffNote = (j: Job) => callSitesWanted(j) && fridaAttach(j) ? `ProbeSnap.CallSites off: ${fridaAttach(j)} attaches a Frida agent (docs/67 §7)` : '';
// docs/67 §18 (Rodney 2026-10-25): light crash forensics in every job, timing jobs too (no game-path detour; heavy guards stay
// behind Enabled=1 and count as heavy tools); a job sets CrashForensics.Light 0 to drop it; worldimage captures stay as they were.
// Release tests run the shipped ini as is: no Light on a release asi (game-copy/ts3native.asi.rel*: release-check, -validate,
// -vs-baseline, -reload, snap-release, tool-cost) or a job with "releaseDefaults": true
export const releaseDefaults = (j: Job) => !!j.releaseDefaults || /ts3native\.asi\.rel\d/i.test(j.asi);
export const lightIni = (j: Job): Record<string, number> => j.run.worldimage || releaseDefaults(j) ? {} : { 'CrashForensics.Light': 1 };
// the ini overrides a row records (job ini + the coverage key + Light; worldimage's filled-in paths stay out of the row)
export const rowIni = (j: Job) => ({ ...callSitesIni(j), ...lightIni(j), ...j.ini });
export const jobIni = (j: Job): Record<string, string | number | boolean> | undefined => !j.run.worldimage ? (Object.keys(rowIni(j)).length ? rowIni(j) : j.ini)
  : { 'WorldImage.OutDir': join(GAME_COPY, 'worldimage'), 'WorldImage.Helper': join(ROOT, 'native', 'worldimage', 'out', 'worldimage.exe'), ...j.ini,
    ...(j.ini?.['WorldImage.OutDir'] ? { 'WorldImage.OutDir': resolve(ROOT, String(j.ini['WorldImage.OutDir'])) } : {}) };
// header.txt (native/worldimage/wimage.h): done once the last record `loose` is there
export function wiHeader(text: string) {
  const h = { done: false, loose: null as number | null, regions: 0, storedMB: 0, unreadablePages: 0, dumpMs: 0, threads: 0, modules: 0, tlsAllocated: 0, lockOwners: [] as string[] };
  for (const l of text.split(/\r?\n/)) {
    const [k, ...a] = l.trim().split(/\s+/);
    if (k === 'stats') Object.assign(h, { regions: Number(a[0]), storedMB: Math.round(Number(a[1]) / 1048576), unreadablePages: Number(a[2]), dumpMs: Number(a[3]) });
    else if (k === 'loose') Object.assign(h, { done: true, loose: Number(a[0]) });
    else if (k === 'thread') h.threads++;
    else if (k === 'module') h.modules++;
    else if (k === 'tls' && a[2] === '1') h.tlsAllocated++;
    else if (k === 'lock' && a[3] && a[3] !== '0') h.lockOwners.push(`${a[0]}:${a[3]}`); // lock global : owner tid at the cut (I-123-17)
  }
  return h;
}

// content variants: fixes.json sets moved out of Mods (offload-phase0.ts: nocc-* = neither set, cc-* = no Store repacks)
const CONTENT: Record<string, string[]> = { 'as-is': [], 'no-cc': ['user-cc', 'store-repacks-test'], 'no-store': ['store-repacks-test'] };
const NEVER_MOVE = /(^|\/)(ts3agent|ts3fixes|ts3-nointro)\.package$/i;
type ContentSet = { include: string[]; exclude: string[] };
const contentSets = (variant: string): ContentSet[] => {
  const fixes = JSON.parse(readFileSync(join(ROOT, 'tools', 'setup', 'fixes.json'), 'utf8')) as { id: string; config?: ContentSet }[];
  return CONTENT[variant].map(id => fixes.find(f => f.id === id)!.config!);
};
// files other fixes installed (absolute paths in install-fixes state.json): never moved
const fixFiles = (statePath: string): Set<string> => {
  if (!existsSync(statePath)) return new Set();
  const st = JSON.parse(readFileSync(statePath, 'utf8')) as Record<string, { files: { path: string }[] }>;
  return new Set(Object.entries(st).filter(([id]) => !['user-cc', 'store-repacks-test'].includes(id))
    .flatMap(([, r]) => r.files.map(f => normalize(f.path).toLowerCase())));
};
// the sets' .package files in the live Mods folder (Mods-relative, '/'), selected like install-fixes.ts user-cc does from the backup
export function contentFiles(mods: string, sets: ContentSet[], keep: Set<string> = new Set()): string[] {
  const out = new Set<string>();
  for (const c of sets) for (const inc of c.include) {
    if (!existsSync(join(mods, inc))) continue;
    for (const n of readdirSync(join(mods, inc), { recursive: true }).map(String)) {
      const rel = `${inc}/${n.replace(/\\/g, '/')}`;
      if (!rel.toLowerCase().endsWith('.package') || c.exclude.some(x => rel.startsWith(x)) || NEVER_MOVE.test(rel)) continue;
      if (!keep.has(normalize(join(mods, rel)).toLowerCase())) out.add(rel);
    }
  }
  return [...out].sort();
}
const move = (from: string, to: string) => {
  mkdirSync(dirname(to), { recursive: true });
  try { renameSync(from, to); } catch (e: any) { if (e.code !== 'EXDEV') throw e; copyFileSync(from, to); rmSync(from); }
};
const manifestOf = (stash: string) => join(stash, 'manifest.json');
const ccSetFiles = (dir: string) => readdirSync(dir).filter(f => f.toLowerCase().endsWith('.package')).sort(); // cc-gen writes a flat dir
// one set: its own dir, flat; several: their parent dir, files as <set>/<file> (Mods/Packages/cc-synth/<set>/; Resource.cfg reads 5 levels)
export const ccPlan = (set: string | string[], root = ROOT) => typeof set === 'string' ? { dir: join(root, set), files: ccSetFiles(join(root, set)) }
  : { dir: join(root, 'game-copy', 'cc-synth'), files: set.flatMap(s => ccSetFiles(join(root, s)).map(f => `${basename(s)}/${f}`)) };
const ccSetReady = (set: string | string[]) => [set].flat().every(s => existsSync(join(ROOT, s)) && ccSetFiles(join(ROOT, s)).length > 0);
const lotFiles = (dir: string) => readdirSync(dir).filter(f => /^ebf_0x[0-9a-f]{16}\.package$/i.test(f)).sort();
// ccLots: manifest first, then copies; refuses when a manifest exists or a Library file of the same name exists
export function installLots(dir: string, lib = LIBRARY, manifest = LOTS_MANIFEST): string[] {
  if (existsSync(manifest)) throw new Error(`ccLots manifest exists: ${manifest} (run --restore-content)`);
  const files = lotFiles(dir), clash = files.filter(f => existsSync(join(lib, f)));
  if (clash.length) throw new Error(`ccLots: already in ${lib}: ${clash.join(', ')}`);
  mkdirSync(lib, { recursive: true });
  writeFileSync(manifest, JSON.stringify({ time: new Date().toISOString(), lib, from: dir, files }, null, 1));
  for (const f of files) copyFileSync(join(dir, f), join(lib, f));
  return files;
}
// deletes only listed copies that still equal their source; manifest goes when nothing is left
export function removeLots(manifest = LOTS_MANIFEST): { removed: number; problems: string[] } {
  const m = JSON.parse(readFileSync(manifest, 'utf8')) as { lib: string; from: string; files: string[] }, problems: string[] = [];
  let removed = 0;
  for (const f of m.files) {
    const at = join(m.lib, f);
    if (!existsSync(at)) continue;
    if (existsSync(join(m.from, f)) && !readFileSync(at).equals(readFileSync(join(m.from, f)))) { problems.push(`${f} differs from ${m.from}: kept`); continue; }
    try { rmSync(at); removed++; } catch (e) { problems.push(`${f}: ${String(e).split('\n')[0]}`); }
  }
  if (!problems.length) rmSync(manifest);
  return { removed, problems };
}
// manifest first, so an interrupted move can be undone from it
export function stashContent(mods: string, stash: string, files: string[], meta: object = {}): void {
  if (existsSync(manifestOf(stash))) throw new Error(`content stash manifest exists: ${manifestOf(stash)} (run --restore-content)`);
  mkdirSync(stash, { recursive: true });
  writeFileSync(manifestOf(stash), JSON.stringify({ ...meta, time: new Date().toISOString(), mods, files }, null, 1));
  for (const rel of files) move(join(mods, rel), join(stash, rel));
}
// moves back what is in the stash and never overwrites a file in Mods; manifest and stash go only when nothing is left behind
export function restoreContent(stash: string): { restored: number; problems: string[] } {
  const m = JSON.parse(readFileSync(manifestOf(stash), 'utf8')) as { mods: string; files: string[] };
  let restored = 0;
  const problems: string[] = [];
  for (const rel of m.files) {
    const from = join(stash, rel), to = join(m.mods, rel);
    if (!existsSync(from)) continue; // not moved yet when the run stopped, or already back
    if (existsSync(to)) { problems.push(`in Mods and in the stash, stash copy kept: ${rel}`); continue; }
    try { move(from, to); restored++; } catch (e) { problems.push(`${rel}: ${String(e).split('\n')[0]}`); }
  }
  const left = readdirSync(stash, { recursive: true, withFileTypes: true })
    .filter(e => e.isFile() && !(e.name === 'manifest.json' && normalize(e.parentPath) === normalize(stash)));
  if (!problems.length && left.length) problems.push(`${left.length} files in ${stash} not in the manifest; stash kept`);
  if (!problems.length) rmSync(stash, { recursive: true, force: true });
  return { restored, problems };
}

// repo ini text + "Section.Key": value overrides; keeps comments and line endings, adds missing keys and sections
export function mergeIni(text: string, set: Record<string, string | number | boolean> = {}): string {
  const nl = text.includes('\r\n') ? '\r\n' : '\n';
  const lines = text.split(/\r?\n/);
  for (const [k, raw] of Object.entries(set)) {
    const dot = k.lastIndexOf('.'), sec = k.slice(0, dot), key = k.slice(dot + 1); // the section may hold dots (TOML tables)
    const v = typeof raw === 'boolean' ? (raw ? '1' : '0') : String(raw);
    const s = lines.findIndex(l => l.trim().toLowerCase() === `[${sec.toLowerCase()}]`);
    if (s < 0) { if (lines.at(-1) === '') lines.pop(); lines.push('', `[${sec}]`, `${key}=${v}`, ''); continue; }
    let e = lines.findIndex((l, i) => i > s && l.trim().startsWith('['));
    if (e < 0) e = lines.length;
    const at = lines.findIndex((l, i) => i > s && i < e && new RegExp(`^\\s*${key}\\s*=`, 'i').test(l));
    if (at >= 0) { lines[at] = `${key}=${v}`; continue; }
    let last = e - 1;
    while (last > s && !lines[last].trim()) last--;
    lines.splice(last + 1, 0, `${key}=${v}`);
  }
  return lines.join(nl);
}

// dxvk.conf has no sections ("[x.exe]" is an app filter), so keys are whole names: replace "key = v" or append it
export function mergeConf(text: string, set: Record<string, string | number | boolean> = {}): string {
  const nl = text.includes('\r\n') ? '\r\n' : '\n';
  const lines = text.split(/\r?\n/);
  if (lines.at(-1) === '') lines.pop();
  for (const [k, v] of Object.entries(set)) {
    const at = lines.findIndex(l => new RegExp(`^\\s*${k.replace('.', '\\.')}\\s*=`, 'i').test(l));
    if (at >= 0) lines[at] = `${k} = ${v}`; else lines.push(`${k} = ${v}`);
  }
  return lines.join(nl) + nl;
}

// TOML wants true/false, not mergeIni's 1/0
export const tomlSet = (set: Record<string, string | number | boolean>) => Object.fromEntries(Object.entries(set).map(([k, v]) => [k, typeof v === 'boolean' ? String(v) : v]));

// ts3native counter fields per ini section (names from tools/probe/agent.js nativeCounters)
const COUNTERS: Record<string, RegExp> = {
  RouteCache: /^(enabled|calls|hits|misses|stored|otherFails)$/, RouteWorker: /^(worker\w*|waitUs|fallbacks|watchdogs|invoke\w*|hostCalls|planO\w*)$/,
  RouteAsync: /^async\w*$/, RouteBudget: /^budget\w*$/, SaveWrite: /^save\w*$/, Decompress: /^decompress\w*$/, Alloc: /^alloc\w*$/,
  WaterJps: /^waterJps\w*$/, SaveFlush: /^flush\w*$/, SaveCompress: /^compress\w*$/, HeightCache: /^height\w*$/, GcThreadCheck: /^gc\w*$/,
  LotStream: /^(ls[A-Z]\w*|lot[A-Z]\w*|pump[A-Z]\w*|sync[A-Z]\w*|objScene\w*)$/, Verify: /^vf\w*$/,
};
// minCounters gate on the /native answer after the load: '' = go, else why the job refuses
export function counterGate(c: Record<string, unknown> | null, min?: number): string {
  if (min === undefined) return '';
  if (!c) return `minCounters ${min}: /native did not answer`;
  const v = Number(c.version);
  return Number.isFinite(v) && v >= min ? '' : `minCounters ${min}: the asi reports counters v${c.version ?? '?'} (module for this ini missing; build it first)`;
}
export function pickCounters(c: Record<string, unknown> | null, sections: string[]): Record<string, unknown> | null {
  if (!c) return null;
  const res = sections.map(s => COUNTERS[s]).filter(Boolean);
  return Object.fromEntries(Object.entries(c).filter(([k]) => k === 'version' || k === 'error' || res.some(r => r.test(k))));
}
// last `save: result` line (savewrite.cpp) in a ts3native.log slice
export function lastSave(text: string): { result: number; totalMs: number } | null {
  const m = [...text.matchAll(/^save: result (\d+) travel \S+ total (\d+) ms/gm)].at(-1);
  return m ? { result: Number(m[1]), totalMs: Number(m[2]) } : null;
}
// age-save.ts "HH:MM:SS saved <name>" -> name (checkpoints <name>-dayN, then the final <name>)
export const savedName = (line: string) => line.match(/^\d\d:\d\d:\d\d saved (\S+)\s*$/)?.[1] ?? null;
// nativeRequire: flatDelta (fixes-require.ts, shared with the native_mark / native_check repro steps)
// logRequire: { "<regex>": min lines } over this job's ts3native.log copy; failed rules as text
export function logFails(text: string, req: Record<string, number>): string[] {
  return Object.entries(req).flatMap(([re, min]) => { const n = text.split(/\r?\n/).filter(l => new RegExp(re).test(l)).length; return n >= min ? [] : [`log /${re}/ ${n} lines (want >= ${min})`]; });
}
// ts3native.ini value of [sec] key (number), NaN when absent
export function iniNum(ini: string, sec: string, key: string): number {
  const b = ini.split(/^\s*\[/m).find(x => x.toLowerCase().startsWith(sec.toLowerCase() + ']')) ?? '';
  return Number(b.match(new RegExp(`^\\s*${key}\\s*=\\s*(-?\\d+)\\b`, 'im'))?.[1] ?? NaN);
}
// pre-run self check: any "FAIL ..." hook line ts3native wrote so far means a feature the job relies on is off, run void
// (2026-10-22/23: 4 runs PASSed with "FAIL route worker:", Frida prologue on the runtime_invoke slot); '' when fine
// baseline save integrity (2026-10-24: Baseline-BP-Spring was overwritten by a manual Save and no run noticed): the Baseline-* /
// Story-* save the job loads and ApexTest-Town are hashed before and after every job; a change fails the job and stops the queue
export function guardedSaves(save: string): string[] {
  const f = (x: string) => (/\.sims3$/i.test(x) ? x : `${x}.sims3`);
  return [...new Set([save, 'ApexTest-Town'].filter(x => /^(baseline|story)-/i.test(x) || /^apextest-town(\.sims3)?$/i.test(x)).map(f))];
}
export function hashSave(dir: string): string { // a save is a folder; 'missing' when absent
  if (!existsSync(dir)) return 'missing';
  const h = createHash('sha256');
  for (const n of readdirSync(dir, { recursive: true }).map(String).sort()) {
    const p = join(dir, n);
    if (statSync(p).isFile()) h.update(n.replace(/\\/g, '/')).update('\0').update(readFileSync(p)).update('\0');
  }
  return h.digest('hex').slice(0, 16);
}
export const hashSaves = (savesDir: string, names: string[]): Record<string, string> => Object.fromEntries(names.map(n => [n, hashSave(join(savesDir, n))]));
export const saveDrift = (before: Record<string, string>, after: Record<string, string>) =>
  Object.keys(before).filter(n => after[n] !== before[n]).map(n => `${n} ${before[n]} -> ${after[n] ?? 'not hashed'}`);
// noisy timing job (early stop or a high end verdict): run it once more at the end of this queue run as <id>-rerun
export function noisyRerun(j: Job, row: { noise?: string; error?: string | null }, ids: string[]): Job | null {
  if (!j.run.profile || j.noiseCheck === false || j.rerunOf) return null;
  if (row.noise !== 'high' && !/noisy machine early/.test(row.error ?? '')) return null;
  const id = `${j.id}-rerun`;
  return ids.includes(id) ? null : { ...j, id, rerunOf: j.id, note: `rerun of ${j.id} (noisy machine)${j.note ? `: ${j.note}` : ''}` };
}
// which row of a noisy pair counts (results page note)
export const countsNote = (row: { rerun?: string; rerunOf?: string; noise?: string; error?: string | null }) =>
  row.rerun ? `noisy: does not count, rerun as ${row.rerun}` : row.rerunOf ? (row.noise === 'high' || /noisy machine early/.test(row.error ?? '') ? `rerun of ${row.rerunOf}: noisy again, neither counts` : `rerun of ${row.rerunOf}: counts instead of it`) : '';

// ---- measurement smoke (header): which jobs are timing, how long, the smoke job, heavy tools, the smoke verdict
export const SMOKE_OVER_MIN = 60, SMOKE_MIN = 10;
export const isTimingJob = (j: Job) => !!j.run.profile && j.noiseCheck !== false;
const profMinutes = (p: any): number => p.repros?.length ? p.repros.reduce((s: number, r: any) => s + Number((typeof r === 'object' && r.minutes) || p.minutes), 0) : Number(p.minutes);
export const timingMinutes = (jobs: Job[]) => jobs.filter(isTimingJob).reduce((s, j) => s + profMinutes(j.run.profile), 0);
export function smokeJob(q: unknown, jobs: Job[]): Job | null {
  const declared = !Array.isArray(q) && (q as any)?.smoke;
  if (declared) return parseQueue([{ id: 'smoke', ...declared }])[0];
  const t = jobs.find(isTimingJob);
  if (!t) return null;
  const p: any = { ...t.run.profile, minutes: Math.min(t.run.profile!.minutes, SMOKE_MIN) }, r = p.repros?.[0];
  if (r) p.repros = [typeof r === 'string' ? r : { ...r, minutes: Math.min(r.minutes ?? p.minutes, SMOKE_MIN) }];
  return { ...t, id: `${t.id}-smoke`, reuseSession: false, run: { profile: p } } as Job;
}
// heavy measurement tools a job has on: the timing verdict's rule (run-verdict.ts), checked before the run instead of after
// (per profile.repros window, its item merged in, as rowTiming does)
export function heavyTools(j: Job, repoIniText: string, costs = toolCosts()): string[] {
  const p: any = j.run.profile, ps = p?.repros?.length ? p.repros.map((r: any) => ({ ...p, ...(typeof r === 'object' ? r : {}), repro: typeof r === 'string' ? r : r.id })) : [p];
  return [...new Set(ps.flatMap((q: any) => toolsOn(withBaseIni({ ini: j.ini, run: { profile: q } }, repoIniText))).map((t: string) => t.replace(/ \(on arm\)$/, '')))]
    .filter(t => costs.tools[t]?.class === 'heavy');
}
// one profile window of the smoke: arms = A/B or armed loop; iters = hitch-ab runIters, wait = wait-ab seconds per side
export type SmokeWin = { tag: string; arms: boolean; iters: { on: number; off: number } | null; invalid: string | null; wait: { on: number; off: number } | null };
export function smokeProblems(x: { row: any; wins: SmokeWin[]; log: string; p6: boolean; nativeLog: string }): string[] {
  if (!x.row) return ['no smoke row (child queue ended before the job did)'];
  const out: string[] = [];
  if (!x.row.pass) out.push(`smoke job failed: ${[...(x.row.verdict ?? []), x.row.error].filter(Boolean).join('; ').slice(0, 300) || 'no reason'}`);
  for (const w of x.wins) {
    if (w.invalid) out.push(`${w.tag}: run invalid: ${w.invalid}`);
    if (!w.arms) continue;
    if (!w.iters || w.iters.on < 1 || w.iters.off < 1) out.push(`${w.tag}: hitch-ab per-arm split missing (${w.iters ? `on ${w.iters.on} / off ${w.iters.off}` : 'no slices'})`);
    if (!w.wait || !(w.wait.on > 0 && w.wait.off > 0)) out.push(`${w.tag}: wait-ab per-arm split missing (${w.wait ? `on ${w.wait.on} s / off ${w.wait.off} s` : 'no output'})`);
  }
  if (x.p6) for (const a of [0, 1]) if (!x.nativeLog.includes(`p6 waits arm ${a} `)) out.push(`[P6Waits] no 'p6 waits arm ${a}' line in ts3native.log`);
  const rb = /native_set: [^\n]*(did not stick|not an integer|unknown ts3native switch)[^\n]*/.exec(x.log);
  if (rb) out.push(`switch read-back: ${rb[0].slice(0, 200)}`);
  return out;
}

export function hookFailLine(log: string): string {
  const m = log.match(/^FAIL [^\r\n]*/m);
  return m ? `self check: ts3native hook failed, run void (${m[0].slice(0, 160)})` : '';
}
// route worker hook state from /native invokeHook: it installs at the first worker plan, not at start-up (2026-10-23 sv-soak-v014 /
// ip-rvb-v014: asi.rel0360 hooked after the Frida profiler had rewritten 0xE5A600, invokeHook -1, all plans sync, both PASSed);
// 'wait' until a plan ran, '' when hooked or not asked for
export function routeHookState(ini: string, n: any): string {
  if (iniNum(ini, 'RouteAsync', 'Enabled') !== 1 || iniNum(ini, 'RouteAsync', 'Mode') < 1 || !n || !('invokeHook' in n)) return '';
  const v = Number(n.invokeHook);
  return v > 0 ? '' : v < 0 ? 'self check: route worker hook failed (invokeHook -1): async off, run void' : 'wait';
}
// post-run self check: [RouteAsync] on but no async job started = the async arm never ran ('' when fine)
export function asyncIdle(ini: string, c0: any, c1: any): string {
  if (iniNum(ini, 'RouteAsync', 'Enabled') !== 1 || iniNum(ini, 'RouteAsync', 'Mode') < 1 || !c1 || !('asyncStarted' in c1)) return '';
  const d = Number(c1.asyncStarted) - Number(c0?.asyncStarted ?? 0);
  return d > 0 ? '' : `self check: [RouteAsync] on but ${d} async route jobs started (invokeHook ${c1.invokeHook ?? '?'}): async off, run void`;
}
// post-run self check of a profile job, beside asyncIdle: every enabled feature must show work. ini keys (all >= 1) -> /native
// counters whose summed rise must be > 0; an asi without the counter (older version) is skipped. Counter names: tools/probe/agent.js
// nativeCounters (rgEntries v16, usfDeferred v74, rcDeferred/rcWould v104; RefcountGuardMode 0 counts rcWould only)
// ts3fixes rows (fix set): switch on in the agent's `patches` reply -> its `patches` counters (PatchHandlers.cs) must rise, but
// only when the job's sim time covered the game event that runs the fix (when: '3am' = the 3 AM Overwatch/stuck-check alarms,
// 'load' = ErrorTrap's pass a sim-minute after a world load, counted since the session's load); else "not reached", never void.
// standDown: an applied-log line that turns the fix off for this install (mod missing, other build, upstream marker): skip.
// NOTE 2026-10-24: a hitch analysis concluded these fixes never ran because their task names never showed in hitch lines
// (>= 300 ms) while the live counters showed them running. Task names in hitch lines are not evidence of a fix being off: a fix
// that works makes its ticks short, so it drops out of the hitch lines. Judge by these counters.
// record: counters only (before/after in the row and the verdict line), never a void or a note (docs/163 §10.1: stuck-check
// outcomes 297-301 for reset parity on every job with stuckcheckown or stuckcheckcap on)
export const ACTIVITY: { what: string; on: string[]; counters: string[]; fix?: string; when?: '3am' | 'load'; standDown?: RegExp; record?: boolean }[] = [
  { what: '[RouteGate] Enforce', on: ['RouteGate.Enabled', 'RouteGate.Enforce'], counters: ['rgEntries'] },
  { what: '[Discourage] DeferFootprint', on: ['RouteAsync.Enabled', 'RouteAsync.Mode', 'Discourage.Enabled', 'Discourage.DeferFootprint'], counters: ['usfDeferred'] },
  { what: '[RouteAsync] RefcountGuard', on: ['RouteAsync.Enabled', 'RouteAsync.Mode', 'RouteAsync.RefcountGuard'], counters: ['rcDeferred', 'rcWould'] },
  { what: 'ts3fixes overwatchchain', on: [], fix: 'overwatchchain', when: '3am', counters: ['owchain_alarms', 'owspread_types_run'], standDown: /^overwatchspread: .*\boff\b/ },
  { what: 'ts3fixes stuckcheckasync', on: [], fix: 'stuckcheckasync', when: '3am', counters: ['stuckcheckasync_queued', 'stuckcheckasync_run'] },
  { what: 'ts3fixes errortrapspread', on: [], fix: 'errortrapspread', when: 'load', counters: ['etspread_passes'], standDown: /^errortrapspread: .*\boff\b/ },
  ...['stuckcheckown', 'stuckcheckcap'].map(fix => ({ what: `ts3fixes ${fix} outcomes`, on: [], fix, when: '3am' as const, record: true,
    counters: ['stuckcheckcap_marked', 'stuckcheck_noplan', 'stuckcheck_notstuck', 'stuckcheck_rearmed', 'stuckcheck_reset'] })),
];
export function featureIdle(ini: string, c0: any, c1: any): string[] {
  return ACTIVITY.filter(a => !a.fix && a.on.every(k => iniNum(ini, k.split('.')[0], k.split('.')[1]) >= 1) && c1 && a.counters.every(k => k in c1)).flatMap(a => {
    const d = a.counters.reduce((s, k) => s + Number(c1[k]) - Number(c0?.[k] ?? 0), 0);
    return d > 0 ? [] : [`self check: ${a.what} on but ${a.counters.join('+')} rose ${d}: feature idle, run void`];
  });
}
// did sim time t0 -> t1 (agent `time`: minutes_total, clock "HH:MM") pass the event, with `margin` sim minutes for the fix to run?
// 3am: the next 03:00 after t0 (exactly 03:00 at t0 counts as the next day's: fired or not is unknown); load: >= margin minutes ran
export function eventCovered(when: '3am' | 'load', t0: any, t1: any, margin = 10): boolean {
  const e = Number(t1?.minutes_total) - Number(t0?.minutes_total), m = /^(\d+):(\d+)$/.exec(String(t0?.clock ?? ''));
  if (!(e >= 0)) return false;
  if (when === 'load') return e >= margin;
  if (!m) return false;
  const dist = ((180 - (Number(m[1]) * 60 + Number(m[2]))) % 1440 + 1440) % 1440 || 1440;
  return e >= dist + margin;
}
// post-run check of the ts3fixes rows (p0/p1 = agent `patches` results before/after, t0/t1 = agent `time`): fails = void lines,
// notes = "not reached" / stood-down lines, counters = before/after of every row's counters (recorded in the queue row)
// counters: every row the agent reports, on or not. The agent's switch value is an explicit switch or its own guess for an unset
// one (PatchHandlers.cs reads unset overwatchchain/stuckcheckasync/errortrapspread as false although the shipped tuning has them
// on), so gating the record on it dropped owchain_alarms on every release-defaults job (docs/196 §4 step 1).
// judged (fails, notes) only when on from the job's configuration (profile fixesOn/fixesOff), else the switch read after the run:
// profile-world.ts restores the switches before p1 is read, so a --fixes-on switch reads off there (docs/163 §10.2)
// cfg.report: comma prefixes (profile.fixesReport + fixesOn names): matching `patches` keys are recorded too (counters: numbers,
// lists: arrays as read at the end), so a job shows why a fix did not act (docs/202 §3.6.3: D reject counters, refused callers)
export function fixActivity(p0: any, p1: any, t0: any, t1: any, cfg: { fixesOn?: string; fixesOff?: string; report?: string } = {}) {
  const fails: string[] = [], notes: string[] = [], counters: Record<string, { before: number | null; after: number }> = {}, lists: Record<string, unknown[]> = {};
  const pre = cfg.report?.split(',').filter(Boolean) ?? [];
  for (const k of p1 && pre.length ? Object.keys(p1).filter(k => pre.some(x => k.startsWith(x))) : []) {
    if (typeof p1[k] === 'number') counters[k] = { before: p0 && typeof p0[k] === 'number' ? p0[k] : null, after: p1[k] };
    else if (Array.isArray(p1[k])) lists[k] = p1[k];
  }
  const applied: string[] = Array.isArray(p1?.applied) ? p1.applied.map(String) : [];
  const on = new Set(cfg.fixesOn?.split(',') ?? []), off = new Set(cfg.fixesOff?.split(',') ?? []);
  for (const a of ACTIVITY.filter(x => x.fix && p1 && x.counters.every(k => k in p1))) {
    for (const k of a.counters) counters[k] = { before: p0 && k in p0 ? Number(p0[k]) : null, after: Number(p1[k]) };
    if (a.record || !(on.has(a.fix!) || (p1[a.fix!] === true && !off.has(a.fix!)))) continue;
    const down = a.standDown && applied.find(l => a.standDown!.test(l));
    if (down) { notes.push(`${a.what}: stood down (${down.slice(0, 100)})`); continue; }
    // load: the pass may predate the before read (load, then job), so the session total counts; 3am: this job's rise
    const d = a.counters.reduce((s, k) => s + Number(p1[k]) - (a.when === 'load' ? 0 : Number(p0?.[k] ?? 0)), 0);
    if (d > 0) continue;
    if (eventCovered(a.when!, t0, t1)) fails.push(`self check: ${a.what} on but ${a.counters.join('+')} ${a.when === 'load' ? 'is' : 'rose'} ${d} although sim time ${t0?.clock}+${Math.round(Number(t1?.minutes_total) - Number(t0?.minutes_total))} min covered ${a.when === '3am' ? '03:00' : 'the after-load pass'}: fix idle, run void`);
    else notes.push(`${a.what}: not reached (sim ${t0?.clock ?? '?'} +${Math.round(Number(t1?.minutes_total) - Number(t0?.minutes_total)) || 0} min, no ${a.when === '3am' ? '03:00' : 'after-load pass'})`);
  }
  return { fails, notes, counters, lists };
}
// fixActivity's cfg: the job profile's fixesOn plus every repro item's own fixesOn (on in some window), the job's fixesOff;
// report = fixesReport + those fixesOn names
export const jobFixesCfg = (p: any) => {
  const fixesOn = [p?.fixesOn, ...(p?.repros ?? []).map((r: any) => r?.fixesOn)].filter(Boolean).join(',') || undefined;
  return { fixesOn, fixesOff: p?.fixesOff, report: [p?.fixesReport, fixesOn].filter(Boolean).join(',') || undefined };
};
// ts3native.log copies to merge into the call-site DB: each repro slice (own game start after a reload) and the row's copy;
// mergeLog is idempotent per run, so slices of one start add only their growth
export const callsiteLogs = (row: any): string[] => [...new Set<string>([...(row.repros ?? []).map((r: any) => r.nativeLog), row.nativeLog].filter(Boolean))];
// profile.fixesOn: profile-world.ts prints `fixes on: <names> (verified)` after reading the switches back; '' when it did
export const fixesOnFail = (fixesOn: string | undefined, out: string) => !fixesOn || out.includes(`fixes on: ${fixesOn} (verified)`) ? ''
  : `self check: profile.fixesOn ${fixesOn} without its 'fixes on: ... (verified)' line, run void`;
// pairs (Measurement cost rule (ii), PLAN.md 2026-10-24): profile jobs whose ids differ only in the last -part (sv-soak-v014 /
// sv-soak-cand), or that hitch-ab pairs (ABBA <base>-off-N / <base>-on-N, two-load <base>-offfirst / <base>-onfirst), must carry
// the same profiler tools; a difference is a warning (a known cost may excuse it), never a refusal. fixesOff stays out: the
// on/off arms differ there by design.
const pairStems = (id: string) => [`L:${id.replace(/-[^-]*$/, '')}`, ...(/^(.*)-(on|off)(first)?(-\d+)?$/.exec(id)?.slice(1, 2).map(b => `B:${b}`) ?? [])];
export function pairWarnings(jobs: Job[]): string[] {
  const ps = jobs.filter(j => j.run.profile && j.id.includes('-')), out: string[] = [];
  const f = (v: unknown) => (v === undefined ? '-' : JSON.stringify(v)), cut = (s: string) => (s.length > 60 ? `${s.slice(0, 57)}...` : s);
  ps.forEach((a, i) => ps.slice(i + 1).filter(b => pairStems(a.id).some(s => pairStems(b.id).includes(s))).forEach(b => {
    const diff = (['profOpts', 'noSamplers'] as const).filter(k => f((a.run.profile as any)[k]) !== f((b.run.profile as any)[k]))
      .map(k => `${k} ${cut(f((a.run.profile as any)[k]))} vs ${cut(f((b.run.profile as any)[k]))}`);
    if (diff.length) out.push(`WARN pair ${a.id} / ${b.id} differ in profiler tools: ${diff.join(', ')} (cost known? else not comparable)`);
  }));
  return out;
}
// stale pins (Rodney 2026-10-31): a job that pins a parked asi older than the source build, or a game-copy ts3fixes.package
// older than the last ts3fixes source change, tests an old build; a warning (queue log, --dry-run), never a refusal.
// Deliberate pins (release/baseline arm, a published-release check, a job about one old build) say why in pinReason.
const verKey = (v: string) => { const m = /^(\d+)\.(\d+)\.(\d+)([a-z]?)$/.exec(v); return m ? [+m[1], +m[2], +m[3], m[4]] as const : null; };
export const verOlder = (a: string, b: string) => { const x = verKey(a), y = verKey(b); if (!x || !y) return false; for (let i = 0; i < 4; i++) if (x[i] !== y[i]) return x[i] < y[i]; return false; };
export function pinWarnings(jobs: Job[], src: string | undefined, asiVer: (path: string) => string | undefined, pkgStale: (path: string) => boolean): string[] {
  const out: string[] = [];
  for (const j of jobs.filter(j => !j.pinReason)) {
    const v = j.asi === 'tested' ? undefined : asiVer(j.asi);
    if (v && src && verOlder(v, src)) out.push(`WARN pin ${j.id}: asi ${j.asi} is ts3native ${v}, source is ${src}: "asi": "tested" runs the source build; else set pinReason`);
    if (j.fixesPackage?.startsWith('game-copy/') && pkgStale(j.fixesPackage))
      out.push(`WARN pin ${j.id}: fixesPackage ${j.fixesPackage} is older than the last ts3fixes source change: build-fixes.ts [--on kX] -> build/ts3fixes[-on-kX].package; else set pinReason`);
  }
  return out;
}
// logForbid: { "<regex>": max lines }; failed rules as text
export function logForbidFails(text: string, forb: Record<string, number>): string[] {
  return Object.entries(forb).flatMap(([re, max]) => { const n = text.split(/\r?\n/).filter(l => new RegExp(re).test(l)).length; return n <= max ? [] : [`log /${re}/ ${n} lines (want <= ${max})`]; });
}
const delta = (a: any, b: any) => (a && b ?Object.fromEntries(Object.keys(b).filter(k => typeof b[k] === 'number' && typeof a[k] === 'number' && b[k] !== a[k]).map(k => [k, b[k] - a[k]])) : null);

// verdict lines of a child's output: repro.ts "<id>: PASS (...)", "FAIL: ...", save-matrix.ts "<case>: PASS", "summary: ..."
export function profileHead(code: number | null, timedOut: boolean, stop: string | null, out: string): 'PASS' | 'ran' | 'FAIL' {
  if (code === 0) return 'PASS';
  return !timedOut && !!stop && /^stopped at /.test(stop) && verdicts(out).some(v => /^FAIL: /.test(v)) ? 'ran' : 'FAIL';
}

export function verdicts(out: string): string[] {
  return out.split(/\r?\n/).map(l => l.replace(/^\d\d:\d\d:\d\d /, '').trim())
    .filter(l => /(^|[\s:])(PASS|FAIL|PENDING)\b|^summary:/.test(l) && !/^(fix under test|step )/.test(l)).slice(-8);
}
// the last "hang capture: ..." summary a child printed (game-alive.ts captureHang), up to the dump/file part, or null
export const hangCaptureLine = (out: string): string | null => [...out.matchAll(/hang capture: [^\r\n]*/g)].at(-1)?.[0].replace(/; (dump|probe-out|[A-Z]:\\|\/).*$/, '') ?? null;
// a child that failed with no verdict line gets one naming that (2026-10-16 bp-travel-future: stopped from outside, the md said "-")
export function silentFail(code: number | null, out: string, timedOut: boolean): string[] {
  const v = verdicts(out);
  if (code === 0 || v.length) return v;
  const last = out.trim().split(/\r?\n/).at(-1)?.trim() || '(no output)';
  return [`FAIL: exit ${code} with no verdict line (${timedOut ? 'job timeout' : 'child stopped from outside or died before its summary'}); last output: ${last}`];
}

// a job's child that died from outside (2026-10-19: Claude Code's low-memory reaper killed profile-world.ts twice; the job said
// "FAIL in 2.6 min" with no reason and the game stayed open): name it, close the game now, before Bin is restored
export async function childLost(name: string, r: { code: number | null; signal?: string | null; out: string }, close: () => Promise<void>, logFn: (s: string) => void): Promise<string> {
  if (r.code === 0 && !r.signal) return '';
  if (!r.signal && verdicts(r.out).length) return ''; // the child wrote its own FAIL/PASS
  const why = `child ${name} exited (${r.signal ? `signal ${r.signal}` : `code ${r.code}`}) without a result`;
  logFn(`${why}; closing the game`);
  await close();
  return why;
}
// copy into Bin; EBUSY means the game still holds the file: close it, retry once, else log and go on
export async function copyRetry(copy: () => void, what: string, close: () => Promise<void>, logFn: (s: string) => void): Promise<boolean> {
  try { copy(); return true; } catch (e) { logFn(`${what}: ${String(e).split('\n')[0]}; closing the game, retrying`); }
  await close();
  try { copy(); return true; } catch (e) { logFn(`${what} failed again: ${String(e).split('\n')[0]}; continuing`); return false; }
}

// first line of ai-usage.ts: "5h 45% (reset 12:00 PHT) | 7d 62% (...) | read 3 min ago"
export function parseUsage(line: string): { h5: number | null; d7: number | null } {
  const n = (re: RegExp) => { const m = line.match(re); return m ? Number(m[1]) : null; };
  return { h5: n(/5h (\d+)%/), d7: n(/7d (\d+)%/) };
}

// docs/18 G11 gate on the /routelock estimate: go to async only at >= 30 ms/s movable in IP, >= 15 in BP (world from the save name)
export function g11Gate(save: string, movableMsPerSec: number | null | undefined): string {
  const w = save.match(/(?:^|[-_ ])(BP|IP)(?=$|[-_ .])/i)?.[1]?.toUpperCase(), need = w === 'IP' ? 30 : w === 'BP' ? 15 : null;
  if (typeof movableMsPerSec !== 'number') return 'G11 no estimate';
  return need === null ? `G11 no threshold for this world` : `G11 ${movableMsPerSec >= need ? 'go' : 'stop'} (${w} needs >= ${need} ms/s)`;
}
// row summary of a /routelock report: the per-second series stay in the side file
export function routelockSummary(r: any): any {
  if (!r || typeof r !== 'object') return null;
  const { series, ...g11 } = r.g11 ?? {};
  return { ...r, g11: r.g11 ? g11 : null };
}

// stopQueueOnFail / --stop-on-fail: '' = go on, else the STOP reason naming the job (a noisy job with its -rerun queued goes on)
export function failStop(j: { id: string; stopQueueOnFail?: boolean }, row: { pass?: boolean; rerun?: string; error?: string | null; verdict?: string[] }, cli: boolean): string {
  if (row.pass || row.rerun || !(j.stopQueueOnFail || cli)) return '';
  return `${j.id} failed (stopQueueOnFail): ${String(row.error || row.verdict?.find(v => /^FAIL/.test(v)) || 'see the row').slice(0, 200)}`;
}
// stop rule after a job: same job crashed twice (earlier rows count), or this and the previous job both crashed
export function crashStop(crashed: boolean, earlierCrashesOfJob: number, prevCrashed: boolean): string {
  if (!crashed) return '';
  if (earlierCrashesOfJob >= 1) return 'same job crashed twice';
  if (prevCrashed) return 'two jobs in a row crashed';
  return '';
}

if (values['self-check']) {
  const assert = (await import('node:assert')).strict;
  const ok = parseQueue([{ id: 'a', asi: 'tested', save: 'Baseline-BP', run: { soak: { minutes: 5 } } }, { id: 'b', asi: 'x', run: { repro: 'r1' } },
    { id: 'c', asi: 'tested', save: 'menu', run: { menuMemory: { waitS: 30 } } }]);
  assert.equal(ok.length, 3);
  assert.equal(parseQueue({ jobs: [{ id: 'm', asi: 'tested', save: 'S', run: { matrix: ['--cases', 'control'] } }] })[0].id, 'm');
  const pq = parseQueue({ pin: false, jobs: [{ id: 'p', asi: 'tested', save: 'S', run: { soak: { minutes: 1 } } }, { id: 'q', asi: 'tested', save: 'S', pin: { cpus: '2-3' }, run: { soak: { minutes: 1 } } }] });
  assert.equal(pq[0].pin, false); assert.deepEqual(pq[1].pin, { cpus: '2-3' }); // queue-level default, job wins
  // docs/170: reuseSession (queue-level default, job wins) and profile.repros
  const rq = parseQueue({ reuseSession: { maxJobs: 3 }, jobs: [{ id: 'r1', asi: 'tested', save: 'S', run: { profile: { minutes: 5, repros: ['a', { id: 'b', minutes: 3, nativeRequire: 'x>=1' }] } } },
    { id: 'r2', asi: 'tested', save: 'S', reuseSession: false, run: { profile: { minutes: 5 } } }] });
  assert.deepEqual(rq[0].reuseSession, { maxJobs: 3 }); assert.equal(rq[1].reuseSession, false);
  for (const [j, re] of [[{ reuseSession: { maxJobs: 0 }, run: { profile: { minutes: 5 } } }, /maxJobs/], [{ run: { profile: { minutes: 5, repros: [] } } }, /non-empty/],
    [{ run: { profile: { minutes: 5, repros: ['a', 'a'] } } }, /twice/], [{ run: { profile: { minutes: 5, repro: 'x', repros: ['a'] } } }, /takes no profile.repro/],
    [{ run: { profile: { minutes: 5, ab: 'gohome', repros: ['a'] } } }, /takes no profile.ab/], [{ run: { profile: { minutes: 5, repros: [{ id: 'a', fixesRequire: 'x=>1' }] } } }, /./]] as [any, RegExp][])
    assert.throws(() => parseQueue([{ id: 'x', asi: 'tested', save: 'S', ...j }]), re);
  const bad: [unknown, RegExp][] = [
    [[], /non-empty/], [[{ id: 'a b', asi: 'tested', run: { repro: 'x' } }], /id must/], [[{ id: 'a', asi: 'tested', run: { repro: 'x' } }, { id: 'a', asi: 'tested', run: { repro: 'x' } }], /duplicate/],
    [[{ id: 'a', run: { repro: 'x' } }], /asi/], [[{ id: 'a', asi: 'tested', run: { repro: 'x', soak: { minutes: 1 } } }], /exactly one/],
    [[{ id: 'a', asi: 'tested', run: {} }], /exactly one/], [[{ id: 'a', asi: 'tested', save: 'S', run: { soak: { minutes: 0 } } }], /minutes/],
    [[{ id: 'a', asi: 'tested', ini: { Alloc: 1 }, save: 'S', run: { soak: { minutes: 1 } } }], /Section\.Key/],
    [[{ id: 'a', asi: 'tested', run: { soak: { minutes: 1 } } }], /save is required/], [[{ id: 'a', asi: 'tested', save: 'S', run: { menuMemory: {} } }], /menu/],
    [[{ id: 'a', asi: 'tested', save: 'S', run: { soak: { minutes: 1, speed: 'turbo' } } }], /speed/],
    [[{ id: 'a', asi: 'tested', save: 'S', pin: { priority: 'RealTime' }, run: { soak: { minutes: 1 } } }], /pin/],
    [[{ id: 'a', asi: 'tested', save: 'menu', content: 'cc-full', run: { menuMemory: {} } }], /content must be/],
    [[{ id: 'a', asi: 'tested', save: 'menu', content: 'toString', run: { menuMemory: {} } }], /content must be/],
    [[{ id: 'a', asi: 'tested', save: 'S', run: { profile: { minutes: 0 } } }], /profile.minutes/],
    [[{ id: 'a', asi: 'tested', save: 'S', run: { profile: { minutes: 5, seed: 1.5 } } }], /seed/],
    [[{ id: 'a', asi: 'tested', save: 'S', run: { profile: { minutes: 5, fixesOff: 'go home' } } }], /fixesOff/],
    [[{ id: 'a', asi: 'tested', save: 'S', run: { profile: { minutes: 5, fixesOn: 'go home' } } }], /fixesOn/],
    [[{ id: 'a', asi: 'tested', save: 'S', run: { profile: { minutes: 5, fixesRequire: 'owspread_alarms=>1' } } }], /fixes-require/],
    [[{ id: 'a', asi: 'tested', save: 'S', run: { profile: { minutes: 5, untilCompares: 0 } } }], /untilCompares/],
    [[{ id: 'a', asi: 'tested', save: 'S', run: { profile: { minutes: 5, ab: 'x', abOnFirst: 'yes' } } }], /abOnFirst/],
    [[{ id: 'a', asi: 'tested', save: 'S', gameArgs: ['disablestoryprogression'], run: { profile: { minutes: 5 } } }], /gameArgs/],
    [[{ id: 'a', asi: 'tested', save: 'menu', defaultIni: { Seed: 1 }, run: { menuMemory: {} } }], /defaultIni/],
    [[{ id: 'a', asi: 'tested', save: 'menu', s3ppIni: { MonoIsinstCache: 0 }, run: { menuMemory: {} } }], /s3ppIni/],
    [[{ id: 'a', asi: 'tested', save: 'menu', s3ssToml: { enabled: false }, run: { menuMemory: {} } }], /s3ssToml/],
    [[{ id: 'a', asi: 'tested', save: 'S', run: { profile: { minutes: 5, repro: 7 } } }], /profile.repro/],
    [[{ id: 'a', asi: 'tested', save: 'S', run: { profile: { minutes: 5, mainstack: 'render' } } }], /profile.mainstack/],
    [[{ id: 'a', asi: 'tested', save: 'S', run: { profile: { minutes: 3, f4b: 'yes' } } }], /profile.f4b/],
    [[{ id: 'a', asi: 'tested', save: 'S', run: { profile: { minutes: 3, vres: 1 } } }], /profile.vres/],
    [[{ id: 'a', asi: 'tested', save: 'S', run: { profile: { minutes: 3, vresDry: 0 } } }], /profile.vresDry/],
    [[{ id: 'a', asi: 'tested', save: 'S', run: { profile: { minutes: 3, noSamplers: 1 } } }], /profile.noSamplers/],
    [[{ id: 'a', asi: 'tested', save: 'menu', run: { menuMemory: { draws: 0 } } }], /draws/],
  ];
  assert.equal(parseQueue([{ id: 'a', asi: 'tested', save: 'menu', content: 'no-cc', run: { menuMemory: {} } }])[0].content, 'no-cc');
  assert.equal(contentSets('no-cc').length, 2);
  assert.ok(contentSets('no-store')[0].include.includes('Packages/Sims 3 Store'));
  assert.equal(parseQueue([{ id: 'p', asi: 'tested', save: 'S', gameArgs: ['-disablestoryprogression'], run: { profile: { minutes: 10, seed: 7 } } },
    { id: 'i', asi: 'tested', save: 'menu', defaultIni: { 'Config.SimulatorRandomSeed': 7 }, run: { menuMemory: { draws: 3 } } }]).length, 2);
  for (const [q, re] of bad) assert.throws(() => parseQueue(q), re);
  const age = (a: object, extra: object = {}) => [{ id: 'age-bp', asi: 'tested', save: 'Baseline-BP', run: { age: { days: 14, checkpoint: 2, ...a } }, ...extra }];
  assert.equal(parseQueue(age({ keepAlive: 'off', save: 'Age-BP' }))[0].run.age!.days, 14);
  assert.equal(parseQueue(age({ keepAlive: -50 })).length, 1);
  for (const [q, re] of [[age({ days: 0 }), /days/], [age({ checkpoint: -1 }), /checkpoint/], [age({ keepAlive: 'on' }), /keepAlive/],
    [age({ save: 'Baseline-BP' }), /Baseline-/], [age({ save: 'baseline-bp-old' }), /Baseline-/], [age({}, { id: 'Baseline-age' }), /Baseline-/],
    [age({ save: 'a b' }), /run.age.save/], [age({}, { save: 'menu' }), /age needs a save/]] as [unknown, RegExp][]) assert.throws(() => parseQueue(q), re);
  const slog = 'save: result 0 travel 0/0 total 4100 ms: info 10\r\nsave flush: 1 calls\r\nsave: result 5 travel 0/0 total 9000 ms: info 1\r\n';
  assert.deepEqual(lastSave(slog), { result: 5, totalMs: 9000 });
  assert.equal(lastSave('footprint: x'), null);
  assert.equal(savedName('12:00:01 saved Age-BP-day5'), 'Age-BP-day5');
  assert.equal(savedName('12:00:01 save Age-BP-day5 not started: busy'), null);
  assert.equal(savedName('12:00:01 day 5 08:00 ultra; saved 2 sim days'), null);

  const ini = '; head\r\n[GC]\r\nEnabled=1\r\n\r\n[Alloc]\r\n; why\r\nEnabled=0\r\nMode=1\r\n\r\n[HeightCache]\r\nEnabled=0\r\n';
  const m = mergeIni(ini, { 'Alloc.Enabled': 1, 'alloc.Extra': true, 'HeightCache.Enabled': false, 'New.Key': 'x' });
  assert.ok(m.includes('\r\n') && !/[^\r]\n/.test(m), 'CRLF kept');
  assert.match(m, /\[Alloc\]\r\n; why\r\nEnabled=1\r\nMode=1\r\nExtra=1\r\n\r\n\[HeightCache\]/);
  assert.match(m, /\[HeightCache\]\r\nEnabled=0\r\n/);
  assert.match(m, /\r\n\r\n\[New\]\r\nKey=x\r\n$/);
  assert.match(m, /^; head\r\n\[GC\]\r\nEnabled=1\r\n/);
  assert.equal(mergeIni(ini, {}), ini);
  { // docs/133 §7: coverage key on correctness jobs, off on timing profile jobs, job/ini overrides
    const cj = (o: object) => parseQueue([{ id: 'c', asi: 'tested', save: 'S', run: { soak: { minutes: 1 } }, ...o }])[0];
    const pj = (o: object) => parseQueue([{ id: 'p', asi: 'tested', save: 'S', run: { profile: { minutes: 5 } }, ...o }])[0];
    assert.deepEqual(callSitesIni(cj({})), { 'ProbeSnap.CallSites': 1 });
    assert.deepEqual(callSitesIni(pj({})), {});
    // docs/67 §7: a profile job that attaches a second Frida agent never gets the key
    const np = (o: object, p: object = {}) => parseQueue([{ id: 'p', asi: 'tested', save: 'S', run: { profile: { minutes: 5, noProf: true, ...p } }, ...o }])[0];
    assert.deepEqual(callSitesIni(pj({ noiseCheck: false })), {});
    assert.equal(fridaAttach(pj({})), 'stutter profiler');
    assert.deepEqual(callSitesIni(np({ noiseCheck: false })), { 'ProbeSnap.CallSites': 1 });
    assert.deepEqual(callSitesIni(np({ callSites: true })), { 'ProbeSnap.CallSites': 1 });
    assert.equal(fridaAttach(np({})), ''); assert.equal(fridaAttach(cj({})), '');
    assert.equal(fridaAttach(np({}, { inroom: true, f4b: true })), 'inroom, f4b');
    assert.equal(fridaAttach(np({}, { noProf: false, watchstalk: true })), 'watchstalk', 'watchstalk replaces the profiler');
    assert.deepEqual(callSitesIni(np({ noiseCheck: false }, { livereads2: true })), {});
    assert.match(callSitesOffNote(pj({ noiseCheck: false })), /^ProbeSnap.CallSites off: stutter profiler attaches/);
    assert.equal(callSitesOffNote(pj({})), '', 'timing job: no key wanted, nothing to log');
    assert.equal(callSitesOffNote(np({})), '');
    assert.equal(fridaAttach(pj({ ini: { 'ProbeSnap.Enabled': 1 } })), '', 'ProbeSnap.Enabled forces --no-prof');
    assert.throws(() => pj({ callSites: true }), /second Frida agent \(stutter profiler\)/);
    assert.throws(() => pj({ noiseCheck: false, ini: { 'ProbeSnap.CallSites': 1 } }), /docs\/67/);
    assert.deepEqual(rowIni(pj({ ini: { 'ProbeSnap.CallSites': 0 } })), { 'CrashForensics.Light': 1, 'ProbeSnap.CallSites': 0 });
    assert.deepEqual(callSitesIni(cj({ callSites: false })), {});
    assert.deepEqual(callSitesIni(cj({ ini: { 'ProbeSnap.Enabled': 1 } })), {});
    assert.deepEqual(rowIni(cj({ ini: { 'ProbeSnap.CallSites': 0 } })), { 'CrashForensics.Light': 1, 'ProbeSnap.CallSites': 0 });
    assert.deepEqual(jobIni(cj({ ini: { 'Alloc.Enabled': 1 } })), { 'ProbeSnap.CallSites': 1, 'CrashForensics.Light': 1, 'Alloc.Enabled': 1 });
    assert.deepEqual(jobIni(pj({})), { 'CrashForensics.Light': 1 }, 'docs/67 §18: timing jobs get Light too');
    assert.deepEqual(rowIni(pj({ ini: { 'CrashForensics.Light': 0 } })), { 'CrashForensics.Light': 0 }, 'a job can drop it');
    assert.equal(jobIni(pj({ asi: 'game-copy/ts3native.asi.rel015' })), undefined, 'release asi: shipped ini as is');
    assert.deepEqual(jobIni(cj({ releaseDefaults: true })), { 'ProbeSnap.CallSites': 1 }, 'releaseDefaults: no Light');
    assert.throws(() => cj({ releaseDefaults: 1 }), /releaseDefaults/);
    assert.throws(() => cj({ callSites: 1 }), /callSites/);
  }
  assert.equal(mergeIni('[A]\nk=1', { 'A.k': 2 }), '[A]\nk=2'); // no override from an earlier job: every job starts from the repo text
  // dxvk.conf: no sections, replace or append
  assert.equal(mergeConf('d3d9.samplerAnisotropy = 16\r\n', { 'd3d9.textureMemory': 64 }), 'd3d9.samplerAnisotropy = 16\r\nd3d9.textureMemory = 64\r\n');
  assert.equal(mergeConf('d3d9.textureMemory=100\n# x\n', { 'd3d9.textureMemory': 64 }), 'd3d9.textureMemory = 64\n# x\n');
  assert.throws(() => parseQueue([{ id: 'a', asi: 'tested', save: 'menu', dxvkConf: { textureMemory: 64 }, run: { menuMemory: {} } }]), /dxvkConf/);
  // S3SS.toml: dotted tables, true/false values
  const toml = '[patches.Mimalloc]\nenabled = true\n\n[patches.LotStreamingOptimizations]\nenabled = true\n';
  assert.equal(mergeIni(toml, tomlSet({ 'patches.LotStreamingOptimizations.enabled': false })), '[patches.Mimalloc]\nenabled = true\n\n[patches.LotStreamingOptimizations]\nenabled=false\n');
  assert.equal(parseQueue([{ id: 'l', asi: 'x', save: 'S', s3ssToml: { 'patches.LotStreamingOptimizations.enabled': false }, run: { profile: { minutes: 6, repro: 'lotstream-pan' } } }])[0].run.profile!.repro, 'lotstream-pan');
  assert.equal(parseQueue([{ id: 'm', asi: 'x', save: 'S', run: { profile: { minutes: 6, mainstack: 'main' } } }])[0].run.profile!.mainstack, 'main');
  assert.throws(() => parseQueue([{ id: 's', asi: 'x', save: 'S', run: { profile: { minutes: 6, story: '{"seed":1}' } } }]), /profile.story/);
  assert.deepEqual(parseQueue([{ id: 's', asi: 'x', save: 'S', run: { profile: { minutes: 10, story: { seed: 1 } } } }])[0].run.profile!.story, { seed: 1 });
  assert.throws(() => parseQueue([{ id: 's', asi: 'x', save: 'S', run: { profile: { minutes: 6, envGuard: 'off' } } }]), /envGuard/);
  assert.equal(parseQueue(JSON.parse(readFileSync(join(import.meta.dirname, 'queues', 'story-days.json'), 'utf8')).jobs)[0].run.profile!.envGuard, false, 'story-days: seasons pass (docs/171 §16)');
  // story jobs load a Story-* copy, never the named save; other jobs and existing Story-* saves load as named
  const sj = (story?: object) => parseQueue([{ id: 's', asi: 'x', save: 'Baseline-BP-Spring', run: { profile: { minutes: 10, seed: 9, ...(story && { story }) } } }])[0];
  assert.equal(storyCopy(sj({ seed: 1 }), 'Baseline-BP-Spring'), 'Story-Baseline-BP-Spring-s1');
  assert.equal(storyCopy(sj({}), 'Baseline-BP-Spring.sims3'), 'Story-Baseline-BP-Spring-s9');
  assert.equal(storyCopy(sj(), 'Baseline-BP-Spring'), 'Baseline-BP-Spring');
  assert.equal(storyCopy(sj({ seed: 1 }), 'Story-Baseline-BP-Spring-s1'), 'Story-Baseline-BP-Spring-s1');
  assert.equal(parseQueue([{ id: 't', asi: 'x', save: 'S', run: { profile: { minutes: 6, travel: 'university' } } }])[0].run.profile!.travel, 'university');
  assert.throws(() => parseQueue([{ id: 't', asi: 'x', save: 'S', run: { profile: { minutes: 6, travel: 'Moon' } } }]), /travel/);

  const c = { version: 14, enabled: 0, calls: 5, allocHitsK: 10, allocMode: 1, heightHits: 3, flushJobs: 2, workerCalls: 9, lotAdds: 4, pumpMaxUs: 9, lsMode: 1, lotsX: 1 };
  assert.deepEqual(pickCounters(c, ['LotStream']), { version: 14, lotAdds: 4, pumpMaxUs: 9, lsMode: 1 });
  assert.deepEqual(pickCounters(c, ['Alloc']), { version: 14, allocHitsK: 10, allocMode: 1 });
  assert.deepEqual(pickCounters(c, ['HeightCache', 'SaveFlush', 'GC']), { version: 14, heightHits: 3, flushJobs: 2 });
  assert.equal(pickCounters(null, ['Alloc']), null);
  // Verify group (docs/51): every vf* key, nothing else
  const v = { version: 20, vfEnabled: 1, vfThreadsOffMain: 3, vfRaceCellSwap: 0, verbose: 1, lotAdds: 2 };
  assert.deepEqual(pickCounters(v, ['Verify']), { version: 20, vfEnabled: 1, vfThreadsOffMain: 3, vfRaceCellSwap: 0 });
  assert.equal(counterGate(v, 20), '');
  assert.equal(counterGate(v, undefined), '');
  assert.match(counterGate({ version: 19, lotAdds: 1 }, 20), /counters v19/);
  assert.match(counterGate(null, 20), /did not answer/);
  assert.match(counterGate({ error: 'no native' }, 20), /counters v\?/);
  assert.equal(parseQueue([{ id: 'v', asi: 'game-copy/x.asi', save: 'S', ini: { 'Verify.Enabled': 1 }, minCounters: 20, run: { profile: { minutes: 7, repro: 'verify-sweep', mainstack: 'main' } } }])[0].minCounters, 20);
  assert.throws(() => parseQueue([{ id: 'v', asi: 'x', save: 'S', minCounters: 19.5, run: { soak: { minutes: 1 } } }]), /minCounters/);
  assert.throws(() => parseQueue([{ id: 'v', asi: 'x', save: 'S', minCounters: '20', run: { soak: { minutes: 1 } } }]), /minCounters/);
  // nativeRequire / logRequire (docs/169 route repro checks)
  assert.deepEqual(flatDelta({ a: 1, p: [1, 2], s: 'x' }, { a: 4, p: [1, 7], s: 'y' }), { a: 3, 'p.0': 0, 'p.1': 5 });
  assert.deepEqual(flatDelta({ fgReady: 1, gsReady: 255 }, { fgReady: 1, gsReady: 255 }), { fgReady: 1, gsReady: 255 }); // load-time flags
  assert.deepEqual(checkRequire('sypPlans.1>=5,a==3', flatDelta({ a: 1, sypPlans: [0, 0] }, { a: 4, sypPlans: [9, 5] })), []);
  assert.deepEqual(logFails('route async: sync plan 50 ms, IC Plan, reason NoStart\nx\nroute async: sync plan 9 ms, IC Plan, reason QueueFull', { 'reason NoStart': 1, 'reason QueueFull': 2 }), ['log /reason QueueFull/ 1 lines (want >= 2)']);
  assert.throws(() => parseQueue([{ id: 'v', asi: 'x', save: 'S', logRequire: { x: 0 }, run: { soak: { minutes: 1 } } }]), /logRequire/);
  // self checks: start-up FAIL lines, lazy route worker hook, async jobs actually started
  assert.deepEqual(logForbidFails('snap replay audit MISMATCH lot 0x1: Q 1\nok', { 'snap replay audit MISMATCH': 0, 'P4 FAULT': 0 }), ['log /snap replay audit MISMATCH/ 1 lines (want <= 0)']);
  assert.throws(() => parseQueue([{ id: 'v', asi: 'x', save: 'S', logForbid: { x: -1 }, run: { soak: { minutes: 1 } } }]), /logForbid/);
  assert.throws(() => parseQueue([{ id: 'v', asi: 'x', save: 'S', routingFailFast: { ms: 300, scope: 'all' }, run: { profile: { minutes: 5, ab: 'unseenfb' } } }]), /scope "all" on an --ab/);
  assert.throws(() => parseQueue([{ id: 'v', asi: 'x', save: 'S', routingFailFast: { ms: 300, scope: 'unseen' }, run: { soak: { minutes: 1 } } }]), /needs run.profile/);
  assert.throws(() => parseQueue([{ id: 'v', asi: 'x', save: 'S', routingFailFast: { ms: 300, scope: 'unseen', allow: [{ match: 'x' }] }, run: { profile: { minutes: 5 } } }]), /reason and ref/);
  // route worker hook FAIL with [RouteAsync] on = void run; off, or no FAIL line = fine
  const rwLog = 'OK   x\r\nFAIL route worker: runtime_invoke slot 0x00F95000 holds 0x1, expected 0x2 with its prologue; plans stay on the calling thread\r\n';
  assert.match(hookFailLine(rwLog), /^self check: ts3native hook failed, run void \(FAIL route worker: runtime_invoke.*thread\)$/);
  assert.equal(hookFailLine('OK   x\nroute FAIL later\n'), '');
  const raOn = '[GC]\nEnabled=1\n[RouteAsync]\n; c\nEnabled=1\nMode=1\n';
  assert.equal(iniNum(raOn, 'RouteAsync', 'Mode'), 1); assert.equal(iniNum('[RouteAsync]\nEnabled=10\n', 'RouteAsync', 'Enabled'), 10);
  assert.ok(Number.isNaN(iniNum(raOn, 'RouteAsync', 'Sites')));
  assert.equal(routeHookState(raOn, { invokeHook: 0 }), 'wait'); assert.equal(routeHookState(raOn, { invokeHook: 1 }), '');
  assert.match(routeHookState(raOn, { invokeHook: -1 }), /invokeHook -1/);
  assert.equal(routeHookState('[RouteAsync]\nEnabled=0\nMode=1\n', { invokeHook: -1 }), ''); assert.equal(routeHookState(raOn, {}), ''); // old asi: no field
  assert.match(asyncIdle(raOn, { asyncStarted: 0 }, { asyncStarted: 0, invokeHook: -1 }), /0 async route jobs started \(invokeHook -1\)/);
  assert.equal(asyncIdle(raOn, { asyncStarted: 3 }, { asyncStarted: 90 }), ''); assert.equal(asyncIdle('[RouteAsync]\nEnabled=1\nMode=0\n', {}, { asyncStarted: 0 }), '');
  // feature activity table: enabled + counter present + no rise = void; counter missing (old asi) or feature off = skip
  const fIni = '[RouteAsync]\nEnabled=1\nMode=1\nRefcountGuard=1\n[RouteGate]\nEnabled=1\nEnforce=1\n[Discourage]\nEnabled=1\nDeferFootprint=1\n';
  assert.deepEqual(featureIdle(fIni, { rgEntries: 5, usfDeferred: 1, rcDeferred: 0, rcWould: 0 }, { rgEntries: 90, usfDeferred: 7, rcDeferred: 0, rcWould: 3 }), []);
  assert.deepEqual(featureIdle(fIni, { rgEntries: 5, usfDeferred: 1, rcDeferred: 2, rcWould: 0 }, { rgEntries: 5, usfDeferred: 7, rcDeferred: 2, rcWould: 0 }),
    ['self check: [RouteGate] Enforce on but rgEntries rose 0: feature idle, run void', 'self check: [RouteAsync] RefcountGuard on but rcDeferred+rcWould rose 0: feature idle, run void']);
  assert.deepEqual(featureIdle(fIni, {}, { rgEntries: 3 }), []); // v16 asi: no usf*/rc* fields
  // ts3fixes rows: 3 AM covered or not, after-load pass by session total, stand-down, switch off / old agent = skip
  assert.equal(eventCovered('3am', { minutes_total: 0, clock: '02:00' }, { minutes_total: 70 }), true);
  assert.equal(eventCovered('3am', { minutes_total: 0, clock: '02:00' }, { minutes_total: 65 }), false);
  assert.equal(eventCovered('3am', { minutes_total: 0, clock: '04:00' }, { minutes_total: 600 }), false);
  assert.equal(eventCovered('3am', { minutes_total: 0, clock: '23:50' }, { minutes_total: 200 }), true);
  assert.equal(eventCovered('3am', { minutes_total: 0, clock: '03:00' }, { minutes_total: 30 }), false);
  assert.equal(eventCovered('3am', { minutes_total: 0 }, { minutes_total: 3000 }), false); // no clock: unknown, never void
  assert.equal(eventCovered('load', { minutes_total: 5 }, { minutes_total: 20 }), true);
  const fx = { overwatchchain: true, stuckcheckasync: true, errortrapspread: true, owchain_alarms: 2, owspread_types_run: 40, stuckcheckasync_queued: 10, stuckcheckasync_run: 10, etspread_passes: 1 };
  const night = [{ minutes_total: 0, clock: '01:00' }, { minutes_total: 300, clock: '06:00' }];
  assert.deepEqual(fixActivity(fx, { ...fx, owchain_alarms: 3, stuckcheckasync_run: 22 }, night[0], night[1]).fails, []);
  const idleR = fixActivity(fx, fx, night[0], night[1]);
  assert.equal(idleR.fails.length, 2); assert.match(idleR.fails[0], /overwatchchain on but owchain_alarms\+owspread_types_run rose 0 .*03:00/);
  assert.deepEqual(idleR.counters.owchain_alarms, { before: 2, after: 2 });
  const dayR = fixActivity(fx, fx, { minutes_total: 0, clock: '09:00' }, { minutes_total: 120 });
  assert.deepEqual(dayR.fails, []); assert.equal(dayR.notes.length, 2); assert.match(dayR.notes[0], /not reached \(sim 09:00 \+120 min, no 03:00\)/);
  const noEt = { ...fx, etspread_passes: 0, applied: ['errortrapspread: ErrorTrap not loaded, off'] };
  const etR = fixActivity(noEt, noEt, night[0], { minutes_total: 20 });
  assert.deepEqual(etR.fails, []); assert.equal(etR.notes.at(-1), 'ts3fixes errortrapspread: stood down (errortrapspread: ErrorTrap not loaded, off)');
  assert.match(fixActivity({ ...fx, etspread_passes: 0 }, { ...fx, etspread_passes: 0 }, night[0], { minutes_total: 20 }).fails.join(), /errortrapspread on but etspread_passes is 0/);
  // docs/196 §4 step 1: the agent reads an unset default-on switch as false (shipped package): counters recorded, never judged
  const relR = fixActivity(fx, { ...fx, overwatchchain: false, stuckcheckasync: false, errortrapspread: false, owchain_alarms: 5 }, night[0], night[1]);
  assert.deepEqual([relR.fails, relR.notes, relR.counters.owchain_alarms, relR.counters.etspread_passes], [[], [], { before: 2, after: 5 }, { before: 1, after: 1 }]);
  assert.deepEqual(fixActivity(null, { overwatchchain: true }, night[0], night[1]).fails, []); // old agent: no counters
  // stuck-check outcomes (docs/163 §10.1): recorded with stuckcheckown or stuckcheckcap on, idle or not, never a fail or note
  const sc = { stuckcheckown: true, stuckcheckcap: false, stuckcheckcap_marked: 0, stuckcheck_noplan: 1, stuckcheck_notstuck: 2, stuckcheck_rearmed: 0, stuckcheck_reset: 0 };
  const scR = fixActivity(sc, { ...sc, stuckcheck_reset: 1 }, night[0], night[1]);
  assert.deepEqual([scR.fails, scR.notes, scR.counters.stuckcheck_reset, Object.keys(scR.counters).length], [[], [], { before: 0, after: 1 }, 5]);
  assert.deepEqual(fixActivity(sc, sc, { minutes_total: 0, clock: '09:00' }, { minutes_total: 60 }).notes, []);
  assert.deepEqual(fixActivity(sc, { ...sc, stuckcheckown: false }, night[0], night[1]).counters.stuckcheck_reset, { before: 0, after: 0 });
  // docs/163 §10.2: profile-world.ts restored the switches (both read off at the end): the job's fixesOn still records the rows
  const scOff = { ...sc, stuckcheckown: false };
  assert.equal(Object.keys(fixActivity(scOff, { ...scOff, stuckcheck_reset: 2 }, night[0], night[1], { fixesOn: 'stuckcheckown,stuckcheckcap' }).counters).length, 5);
  assert.deepEqual(fixActivity(fx, fx, night[0], night[1], { fixesOff: 'overwatchchain,stuckcheckasync' }).fails, []); // off for the run: no idle void
  // fixesReport (docs/202 §3.6.3): prefix-matched numbers before/after, lists as read, switches and other keys skipped
  const uf0 = { unseenfbpre: true, unseenfbpre_rj_samelot: 1 }, uf1 = { ...uf0, unseenfbpre_rj_samelot: 9, unseenfbleave_rj_goalseen: 2, unseenfbpre_callers: ['3 Foo.Run'], unseenenv_x: 1 };
  const ufR = fixActivity(uf0, uf1, night[0], night[1], jobFixesCfg({ fixesOn: 'unseenfbpre', fixesReport: 'unseenfb' }));
  assert.deepEqual([ufR.counters, ufR.lists, ufR.fails], [{ unseenfbpre_rj_samelot: { before: 1, after: 9 }, unseenfbleave_rj_goalseen: { before: null, after: 2 } }, { unseenfbpre_callers: ['3 Foo.Run'] }, []]);
  assert.equal(jobFixesCfg({ repros: [{ fixesOn: 'unseenfbarrive' }] }).report, 'unseenfbarrive');
  assert.deepEqual(jobFixesCfg({ fixesOn: 'a', fixesOff: 'c', repros: [{ id: 'r1', fixesOn: 'b' }, 'r2'] }), { fixesOn: 'a,b', fixesOff: 'c', report: 'a,b' });
  assert.deepEqual(callsiteLogs({ nativeLog: 'm', repros: [{ nativeLog: 'a' }, {}, { nativeLog: 'm' }] }), ['a', 'm']);
  assert.deepEqual(featureIdle(fIni.replace('DeferFootprint=1', 'DeferFootprint=0').replace('Mode=1', 'Mode=0'), {}, { rgEntries: 3, usfDeferred: 0, rcDeferred: 0, rcWould: 0 }), []);
  assert.deepEqual(featureIdle(fIni, null, null), []);
  assert.equal(fixesOnFail(undefined, ''), ''); assert.equal(fixesOnFail('a,b', '12:00:00 fixes on: a,b (verified)\n'), '');
  assert.match(fixesOnFail('a,b', '12:00:00 fixes on: a (verified)\n'), /fixesOn a,b without/);
  // pair check: same id stem, profiler tools differ -> one warning line (the 2026-10-24 sv-soak case); other jobs left alone
  const pj = (id: string, p: object = {}) => ({ id, asi: 'tested', save: 'S', run: { profile: { minutes: 45, ...p } } });
  assert.deepEqual(pairWarnings(parseQueue([pj('sv-soak-v014', { profOpts: { tasks: false } }), pj('sv-soak-cand'), pj('bp-soak-cand', { noSamplers: true }), pj('solo')])),
    ['WARN pair sv-soak-v014 / sv-soak-cand differ in profiler tools: profOpts {"tasks":false} vs - (cost known? else not comparable)']);
  assert.deepEqual(pairWarnings(parseQueue([pj('ip-a-on', { fixesOff: 'x' }), pj('ip-a-off', { fixesOff: 'y' })])), []);
  // stale pins: older asi / stale game-copy package warn, pinReason, tested, same version and build/ packages do not
  assert.ok(verOlder('0.36.105', '0.36.148') && verOlder('0.36.37', '0.36.37a') && !verOlder('0.36.148', '0.36.148') && !verOlder('0.37.0', '0.36.148'));
  const pw = pinWarnings(parseQueue([pj('old', { minutes: 5 }), { ...pj('rel'), asi: 'game-copy/ts3native.asi.rel020', pinReason: 'v0.2.0 release arm' }, pj('t'), { ...pj('same'), asi: 'game-copy/ts3native.asi.now' },
    { ...pj('pkg'), fixesPackage: 'game-copy/ts3fixes.package.x' }, { ...pj('bld'), fixesPackage: 'build/ts3fixes-on-kX.package' }].map((j, i) => (i === 0 ? { ...j, asi: 'game-copy/ts3native.asi.old' } : j))),
    '0.36.148', p => (p.endsWith('.now') ? '0.36.148' : '0.36.105'), () => true);
  assert.equal(pw.length, 2); assert.match(pw[0], /^WARN pin old: .*0\.36\.105, source is 0\.36\.148/); assert.match(pw[1], /^WARN pin pkg: fixesPackage/);
  assert.throws(() => parseQueue([{ ...pj('e'), pinReason: ' ' }]), /pinReason/);
  // ABBA set and two-load pair: paired across on/off and across repeats; a same-stem stranger (-x) and a longer id are not
  assert.deepEqual(pairWarnings(parseQueue([pj('bp-pc-off-1'), pj('bp-pc-on-1', { noSamplers: true }), pj('bp-pc-x'), pj('bp-pc-on-2', { noSamplers: true })])),
    ['WARN pair bp-pc-off-1 / bp-pc-on-1 differ in profiler tools: noSamplers - vs true (cost known? else not comparable)',
      'WARN pair bp-pc-off-1 / bp-pc-on-2 differ in profiler tools: noSamplers - vs true (cost known? else not comparable)']);
  assert.deepEqual(pairWarnings(parseQueue([pj('bp-d-offfirst', { profOpts: { stages: false } }), pj('bp-d-onfirst'), pj('bp-d-on-first-x')])),
    ['WARN pair bp-d-offfirst / bp-d-onfirst differ in profiler tools: profOpts {"stages":false} vs - (cost known? else not comparable)']);
  assert.deepEqual(pairWarnings(parseQueue([pj('bp-e-off-1', { fixesOff: 'a' }), pj('bp-e-on-1', { fixesOff: 'b' })])), []);
  assert.throws(() => parseQueue([{ ...pj('n'), noiseCheck: 'no' }]), /noiseCheck/);
  assert.throws(() => parseQueue([{ id: 'v', asi: 'x', save: 'S', nativeRequire: 'a=>1', run: { soak: { minutes: 1 } } }]), /not name/);
  // saveMatrix: release mode reaches save-matrix.ts; the release queue's rel-matrix jobs use it
  assert.equal(parseQueue([{ id: 'm', asi: 'x', save: 'S', run: { matrix: [] }, saveMatrix: { fixes: 'shipped' } }])[0].saveMatrix?.fixes, 'shipped'); // -> --fixes shipped (--dry-run shows it)
  assert.throws(() => parseQueue([{ id: 'm', asi: 'x', save: 'S', run: { soak: { minutes: 1 } }, saveMatrix: { fixes: 'shipped' } }]), /saveMatrix/);
  assert.throws(() => parseQueue([{ id: 'm', asi: 'x', save: 'S', run: { matrix: [] }, saveMatrix: { fixes: 'some' } }]), /saveMatrix/);
  assert.deepEqual(parseQueue(JSON.parse(readFileSync(H('queues/release-validate.json'), 'utf8'))).filter(j => j.saveMatrix?.fixes === 'shipped').map(j => j.id), ['bp-rel-matrix', 'sv-rel-matrix', 'ip-rel-matrix']);
  // the shipped verify queue parses and points at existing repros
  const vq = parseQueue(JSON.parse(readFileSync(H('queues/verify.json'), 'utf8')));
  const rids = new Set((JSON.parse(readFileSync(H('repros.json'), 'utf8')) as { id: string }[]).map(r => r.id));
  assert.ok(vq.every(j => j.minCounters === 22 && j.ini?.['Verify.Enabled'] === 1 && j.run.profile?.mainstack === 'main' && rids.has(j.run.profile!.repro!) && (j.timeoutMin ?? 60) <= 25));
  assert.deepEqual(vq.map(j => j.save), ['Baseline-BP', 'Baseline-IP']);
  // the rare-scenario queues (docs/117) parse and point at existing repros
  // the loop example (docs/168 §6): its repro's loop passes checkLoop; the profile-world stop line reaches the row
  const lq = parseQueue(JSON.parse(readFileSync(H('queues/overwatch-loop2.json'), 'utf8')))[0];
  checkLoop((JSON.parse(readFileSync(H('repros.json'), 'utf8')) as any[]).find(r => r.id === lq.run.profile!.repro).loop);
  assert.equal(STOP_LINE.exec('12:00:00 repro x step reflect_call [fire off #20]: {}\n12:15:20 stopped at 20 events after 15.3 min (off 10, on 10; 20 firings, 5 cycles; rule 10 events per arm)\n')?.[1],
    'stopped at 20 events after 15.3 min (off 10, on 10; 20 firings, 5 cycles; rule 10 events per arm)');
  assert.equal(profileHead(1, false, 'stopped at 120 events after 5.0 min (off 60, on 60)', '16:57:39 FAIL: fixes counters: x=0 (want >=10)\n'), 'ran');
  assert.equal(profileHead(1, false, 'loop cap reached: 4 events after 20.1 min, rule not met', '16:57:39 FAIL: fixes counters: x=0\n'), 'FAIL');
  assert.equal(profileHead(1, false, null, ''), 'FAIL');
  assert.equal(profileHead(0, false, null, ''), 'PASS');
  assert.ok(STOP_LINE.test('12:22:00 loop cap reached: 4 events after 20.1 min, rule not met (off 2, on 2; 8 firings, 2 cycles; rule 10 events per arm)'));
  assert.equal(STOP_LINE.exec('12:00:00 stopped at 40 compares after 6.0 min (target 20)'), null); // --until-compares is not a loop line
  for (const q of ['rare-scenarios', 'rare-scenarios-2']) assert.ok(parseQueue(JSON.parse(readFileSync(H(`queues/${q}.json`), 'utf8'))).every(j => !j.run.profile?.repro || rids.has(j.run.profile.repro)), q);
  assert.deepEqual(delta({ a: 1, b: 2, s: 'x' }, { a: 4, b: 2, s: 'y' }), { a: 3 });

  const repro = '12:00:01 bp-route-async: route\n  fix under test: async plan\n12:00:02 step patches {}: ok\n12:10:00 stuck: none\n12:10:01 bp-route-async: PASS (expected: no stuck)';
  assert.deepEqual(verdicts(repro), ['bp-route-async: PASS (expected: no stuck)']);
  assert.deepEqual(verdicts('12:00:00 FAIL: game died at 11:59:00 (TS3W.exe process gone)\nlast crash report'), ['FAIL: game died at 11:59:00 (TS3W.exe process gone)']);
  const matrix = '12:00:00 == control: x\n12:01:00 control: PASS\n12:02:00 identical: FAIL\n    type 0x1: differs\n12:03:00 summary: 2 cases, 1 passed, 1 failed (identical)\n12:03:00 results: a, b';
  assert.equal(hangCaptureLine('12:00:00 hang capture: 1 sim stacks, top frame x\n12:01:00 hang capture: 5 sim stacks, top frame ts3native.asi+0x1 (5/5 same); 3 other thread stacks; managed top A.B; dump fake; C:\\p\\hang-1-*\n'),
    'hang capture: 5 sim stacks, top frame ts3native.asi+0x1 (5/5 same); 3 other thread stacks; managed top A.B');
  assert.equal(hangCaptureLine('no hang'), null);
  assert.deepEqual(verdicts(matrix), ['control: PASS', 'identical: FAIL', 'summary: 2 cases, 1 passed, 1 failed (identical)']);
  assert.deepEqual(silentFail(1, matrix, false), verdicts(matrix));
  assert.deepEqual(silentFail(0, 'ok', false), []);
  assert.deepEqual(silentFail(1, '12:00:00 trip status: loaded true\n', false), ['FAIL: exit 1 with no verdict line (child stopped from outside or died before its summary); last output: 12:00:00 trip status: loaded true']);
  // profile-world.ts failed repro step: the reason reaches the job verdict (2026-10-19 bp-ab-wildflowertask)
  const reproFail = '12:00:00 repro wildflower-drain step wildflower_drain: {"id":29,"error":"Wildflower drain members not found","ok":false}\n12:00:00 FAIL: repro step wildflower_drain failed: Wildflower drain members not found\n12:00:01 cleanup wildflower_drain: ok\n';
  assert.deepEqual(silentFail(1, reproFail, false), ['FAIL: repro step wildflower_drain failed: Wildflower drain members not found']);

  // child killed from outside (signal, or non-zero with no verdict line): reason logged, game closed; own FAIL line: left alone
  {
    let closes = 0; const logged: string[] = [];
    const close = async () => { closes++; }, lg = (s: string) => logged.push(s);
    assert.equal(await childLost('profile-world', { code: null, signal: 'SIGTERM', out: '12:00:00 profiler attached\n' }, close, lg), 'child profile-world exited (signal SIGTERM) without a result');
    assert.equal(await childLost('profile-world', { code: 1, signal: null, out: '' }, close, lg), 'child profile-world exited (code 1) without a result');
    assert.equal(closes, 2);
    assert.match(logged[0], /^child profile-world exited \(signal SIGTERM\) without a result; closing the game$/);
    assert.equal(await childLost('profile-world', { code: 1, signal: null, out: reproFail }, close, lg), '');
    assert.equal(await childLost('profile-world', { code: 0, signal: null, out: '' }, close, lg), '');
    assert.equal(closes, 2);
    // restore EBUSY: close, retry; still busy: log and go on
    let tries = 0;
    const busyOnce = () => { if (tries++ === 0) throw Object.assign(new Error("EBUSY: resource busy or locked, copyfile 'a' -> 'ts3native.asi'"), { code: 'EBUSY' }); };
    assert.equal(await copyRetry(busyOnce, 'restore ts3native.asi', close, lg), true);
    assert.equal(closes, 3);
    assert.equal(await copyRetry(() => { throw new Error('EBUSY'); }, 'restore ts3native.asi', close, lg), false);
    assert.match(logged.at(-1)!, /failed again: Error: EBUSY; continuing$/);
  }

  assert.deepEqual(parseUsage('5h 45% (reset 12:00 PHT) | 7d 81% (reset 09:00 PHT) | read 3 min ago'), { h5: 45, d7: 81 });
  assert.deepEqual(parseUsage('5h - | 7d 12% | read 1 min ago'), { h5: null, d7: 12 });

  assert.equal(parseQueue([{ id: 'r', asi: 'tested', save: 'Baseline-IP', run: { routelock: { minutes: 10, speed: 'fast' } } }])[0].run.routelock!.minutes, 10);
  assert.throws(() => parseQueue([{ id: 'r', asi: 'tested', save: 'S', run: { routelock: { minutes: 0 } } }]), /routelock.minutes/);
  assert.throws(() => parseQueue([{ id: 'r', asi: 'tested', save: 'S', run: { routelock: { minutes: 1, speed: 'x3' } } }]), /routelock.speed/);
  { // worldimage: parsing, ini defaults, header summary
    const wj = (ini: object, run: object = { captures: 2 }) => parseQueue([{ id: 'w', asi: 'tested', save: 'Baseline-BP', ini, run: { worldimage: run } }])[0];
    const wi = jobIni(wj({ 'WorldImage.Enabled': 1, 'WorldImage.MaxCaptures': 2 }))!;
    assert.equal(wi['WorldImage.OutDir'], join(ROOT, 'game-copy', 'worldimage'));
    assert.match(String(wi['WorldImage.Helper']), /worldimage[\\/]out[\\/]worldimage\.exe$/);
    assert.equal(jobIni(wj({ 'WorldImage.Enabled': 1, 'WorldImage.OutDir': 'game-copy/wi2' }))!['WorldImage.OutDir'], join(ROOT, 'game-copy', 'wi2'));
    assert.throws(() => wj({ 'WorldImage.Enabled': 1, 'WorldImage.OutDir': 'probe-out/wi' }), /under game-copy/);
    assert.throws(() => wj({ 'WorldImage.Enabled': 1, 'WorldImage.OutDir': 'C:\\temp\\wi' }), /under game-copy/);
    assert.throws(() => wj({}), /WorldImage.Enabled=1/);
    assert.throws(() => wj({ 'WorldImage.Enabled': 1, 'WorldImage.MaxCaptures': 1 }), /MaxCaptures/);
    assert.throws(() => wj({ 'WorldImage.Enabled': 1 }, { captures: 0 }), /captures/);
    assert.deepEqual(jobIni({ id: 'x', asi: 'tested', run: { soak: { minutes: 1 } } }), { 'ProbeSnap.CallSites': 1, 'CrashForensics.Light': 1 }); // docs/133 §7, docs/67 §18
    assert.equal(wi['CrashForensics.Light'], undefined, 'worldimage: no Light');
    assert.deepEqual(callSitesIni(wj({ 'WorldImage.Enabled': 1 })), {});
    const hd = wiHeader('worldimage 1\nthread 10 7ffd0000 a00000 9f0000 1 2 3 4 5 6 7 8 9 a\ntls 114c908 2 1 0\ntls 11f4160 5 0 0\nlock 11f43a8 ffffffff 0 0\nlock 11f4130 fffffffe 1 4242\nmodule 400000 e5a000 52dec247 TS3W.exe\nstats 900 2147483648 3 2500\n');
    assert.deepEqual([hd.done, hd.regions, hd.storedMB, hd.dumpMs, hd.threads, hd.modules, hd.tlsAllocated, hd.lockOwners], [false, 900, 2048, 2500, 1, 1, 1, ['11f4130:4242']]);
    assert.deepEqual([wiHeader('stats 1 0 0 5\nloose 1\n').done, wiHeader('loose 1\n').loose], [true, 1]);
  }
  assert.deepEqual(fixesApplied(['GoHome.Run: ok', 'Localization.LocalizeString: already compiled', 'AutShadow: game assemblies = accepted set 1',
    'AutLedger: ts3native Ledger=0 this load (off)', 'TooltipManager.Simulate at patch time: not started', 'Autonomy.CalculateScore: ok']),
    ['Localization.LocalizeString: already compiled', 'AutShadow: game assemblies = accepted set 1', 'AutLedger: ts3native Ledger=0 this load (off)']);
  assert.deepEqual(fixesApplied(null), []);
  assert.deepEqual(fixesApplied(['tuned: tuning-gated patches at app startup', 'tuned: kVenueMemo on: venue score hook patched']),
    ['tuned: tuning-gated patches at app startup', 'tuned: kVenueMemo on: venue score hook patched']);
  assert.deepEqual(tunedFails(['tuned: kVenueMemo on: venue score hook patched', 'tuned: kAutLive on: go-live B14 hook MISSING', 'GoHome.Run: ok']),
    ['ts3fixes tuning-gated patch missing: kAutLive on: go-live B14 hook MISSING (docs/124 §17.8)']);
  assert.deepEqual(tunedFails(undefined), []);
  {  // shadowFails: only shadow jobs, zero cuts/entries and session-off lines fail
    const on = { 'AutShadow.Enabled': 1 }, good = { autshadow: { cuts: 40, entries: 900 } };
    assert.deepEqual(shadowFails(on, 'autshadow', { autshadow: { cuts: 0 } }, good, ['AutShadow: game assemblies = accepted set 1']), []);
    assert.deepEqual(shadowFails({}, 'autshadow', {}, {}, []), []);
    assert.deepEqual(shadowFails(on, 'autledger', {}, {}, []), []);
    assert.deepEqual(shadowFails(on, 'x,autshadow', { autshadow: { cuts: 0 } }, { autshadow: { cuts: 0, entries: 0 } }, ['AutShadow: off for the session: L3 klass self-check failed']),
      ['[AutShadow] no data: cuts 0, entries 0', 'AutShadow: off for the session: L3 klass self-check failed']);
    assert.deepEqual(shadowFails(on, 'autshadow', { autshadow: { cuts: 5 } }, { autshadow: { cuts: 5, entries: 10 } }, []), ['[AutShadow] no data: cuts 0, entries 10']);
    // Mode R: mono-cost4 ip-autshadow-r (a5 953, worker 13485 us over 953 runs, reference 2208084 us) fails twice
    const r1 = { ...on, 'AutShadow.Mode': 1 }, z = { before: 0, after: 0 };
    const ip4 = { autshadow: { cuts: 953, entries: 1024, refRuns: 953, refUs: 2_208_084, workRuns: 953, workUs: 13_485, overrun: 953, overrunIdle: 953 } };
    const f4 = shadowFails(r1, 'autshadow', {}, ip4, [], { autshadow_a0: z, autshadow_a1: z, autshadow_a2: z, autshadow_a3: z, autshadow_a5: { before: 0, after: 953 } });
    assert.equal(f4.length, 2); assert.match(f4[0], /Mode R void: 0 compared pairs .*a5 953/); assert.match(f4[1], /worker 14\.2 us per run \(953 runs\) < 50 us/);
    assert.match(shadowFails(r1, 'autshadow', {}, ip4, [])[0], /no autshadow_a0\.\.a3 counters/);
    const okR = { autshadow: { ...ip4.autshadow, workUs: 953 * 2000, overrun: 3 } };
    assert.deepEqual(shadowFails(r1, 'autshadow', {}, okR, [], { autshadow_a0: { before: 0, after: 4000 }, autshadow_a3: z }), []);
    assert.deepEqual(shadowFails(on, 'autshadow', {}, ip4, []), []);  // Mode 0: no compare rule
    assert.deepEqual(shadowFails(r1, 'autshadow', {}, okR, [], { autshadow_a0: { before: 0, after: 4000 }, autshadow_a1: { before: 2, after: 5 }, autshadow_a3: { before: 0, after: 1 } }),
      ['[AutShadow] Mode R mismatch: A1 3, A3 1 (must be 0)']);
  }
  assert.equal(g11Gate('Baseline-IP', 31.2), 'G11 go (IP needs >= 30 ms/s)');
  assert.equal(g11Gate('Baseline-IP.sims3', 29), 'G11 stop (IP needs >= 30 ms/s)');
  assert.equal(g11Gate('Baseline-BP', 15), 'G11 go (BP needs >= 15 ms/s)');
  assert.equal(g11Gate('Baseline-SV', 50), 'G11 no threshold for this world');
  assert.equal(g11Gate('Baseline-BP', null), 'G11 no estimate');
  assert.deepEqual(routelockSummary({ since: 600, g11: { movableMsPerSec: 3, series: { movableMs: [1] } } }), { since: 600, g11: { movableMsPerSec: 3 } });
  assert.deepEqual(routelockSummary({ since: 1, g11: null }), { since: 1, g11: null });
  assert.equal(routelockSummary(null), null);

  assert.equal(crashStop(false, 3, true), '');
  assert.equal(crashStop(true, 0, false), '');
  assert.equal(crashStop(true, 1, false), 'same job crashed twice');
  assert.equal(crashStop(true, 0, true), 'two jobs in a row crashed');
  // stopQueueOnFail / --stop-on-fail (2026-10-31): queue-level default, type check, STOP reason names the job
  assert.equal(parseQueue({ stopQueueOnFail: true, jobs: [{ id: 's', asi: 'x', save: 'S', run: { soak: { minutes: 1 } } }] })[0].stopQueueOnFail, true);
  assert.equal(parseQueue({ stopQueueOnFail: true, jobs: [{ id: 's', asi: 'x', save: 'S', stopQueueOnFail: false, run: { soak: { minutes: 1 } } }] })[0].stopQueueOnFail, false);
  assert.throws(() => parseQueue([{ id: 's', asi: 'x', save: 'S', stopQueueOnFail: 'yes', run: { soak: { minutes: 1 } } }]), /stopQueueOnFail must be true or false/);
  assert.equal(failStop({ id: 'a', stopQueueOnFail: true }, { pass: true }, false), '');
  assert.equal(failStop({ id: 'a' }, { pass: false, verdict: ['FAIL: x'] }, false), '');
  assert.equal(failStop({ id: 'a', stopQueueOnFail: true }, { pass: false, rerun: 'a-rerun' }, false), '');
  assert.equal(failStop({ id: 'a', stopQueueOnFail: true }, { pass: false, verdict: ['note', 'FAIL: x'] }, false), 'a failed (stopQueueOnFail): FAIL: x');
  assert.equal(failStop({ id: 'a' }, { pass: false, error: 'early gate: w (c=0)', verdict: [] }, true), 'a failed (stopQueueOnFail): early gate: w (c=0)');
  // earlyGates (2026-10-31): parse rules, absolute values, fixes: + sums, missing counters, skipped rounds
  const eg = (g: object) => [{ id: 'g', asi: 'x', save: 'S', earlyGates: [g], run: { profile: { minutes: 5 } } }];
  const g1 = { afterSec: 150, counter: 'fixes:autshadow_gm6_faults', op: '==', value: 0, why: 'G-M6 guard faults' };
  assert.equal(parseQueue(eg(g1))[0].earlyGates!.length, 1);
  assert.throws(() => parseQueue(eg({ ...g1, op: '=>' })), /earlyGates/);
  assert.throws(() => parseQueue(eg({ ...g1, why: '' })), /earlyGates/);
  assert.throws(() => parseQueue(eg({ ...g1, counter: 'a b' })), /earlyGates/);
  assert.throws(() => parseQueue([{ id: 'g', asi: 'x', save: 'S', earlyGates: [g1], run: { soak: { minutes: 1 } } }]), /needs run.profile/);
  const gates: EarlyGate[] = [g1, { afterSec: 150, counter: 'autshadow.req', op: '>', value: 0, why: 'worker never posted' },
    { afterSec: 300, counter: 'fixes:autshadow_a0+fixes:autshadow_a1+fixes:autshadow_a2+fixes:autshadow_a3', op: '>', value: 0, why: 'no compared pairs' }];
  const nat = (req: number) => ({ autshadow: { req, rainArm: 0 } }), gfx = (a0: number, f = 0) => ({ autshadow_gm6_faults: f, autshadow_a0: a0, autshadow_a1: 0, autshadow_a2: 0, autshadow_a3: 0 });
  assert.equal(gateFail(gates, 100, nat(0), gfx(0, 3)), ''); // nothing due yet
  assert.equal(gateFail(gates, 160, nat(5), gfx(0, 3)), 'early gate: G-M6 guard faults (fixes:autshadow_gm6_faults=3) [want ==0 from 150 s]');
  assert.equal(gateFail(gates, 160, nat(0), gfx(0)), 'early gate: worker never posted (autshadow.req=0) [want >0 from 150 s]');
  assert.equal(gateFail(gates, 160, nat(5), gfx(0)), ''); // pairs not due until 300 s
  assert.match(gateFail(gates, 310, nat(5), gfx(0)), /^early gate: no compared pairs \(fixes:autshadow_a0\+.*=0\)/); // the void rain-h11 shape
  assert.equal(gateFail(gates, 310, nat(5), gfx(7)), '');
  assert.equal(gateFail(gates, 310, null, null), ''); // probe/agent did not answer: skip the round (the game guard covers a dead probe)
  assert.equal(gateFail(gates, 160, nat(5), {}), 'early gate: G-M6 guard faults (fixes:autshadow_gm6_faults missing)'); // old package / wrong name
  assert.equal(gateFail([{ afterSec: 1, counter: 'p.1', op: '>=', value: 2, why: 'arr' }], 5, { p: [0, 2] }, null), '');

  // content stash round trip on a temp Mods tree
  const tmp = mkdtempSync(join(tmpdir(), 'queue-content-')), mods = join(tmp, 'Mods'), stash = join(tmp, 'stash');
  const sets = [{ include: ['Packages/CC', 'Overrides/Body'], exclude: ['Packages/CC/Scripts'] }, { include: ['Packages/Store'], exclude: [] }];
  const mk = (rel: string) => { mkdirSync(dirname(join(mods, rel)), { recursive: true }); writeFileSync(join(mods, rel), rel); };
  const all = ['Packages/CC/a.package', 'Packages/CC/sub/b.package', 'Packages/CC/readme.txt', 'Packages/CC/Scripts/s.package', 'Packages/CC/ts3agent.package',
    'Packages/CC/fix.package', 'Overrides/Body/c.package', 'Packages/Store/d.package', 'Packages/ts3fixes.package', 'Packages/root.package'];
  all.forEach(mk);
  const moved = ['Overrides/Body/c.package', 'Packages/CC/a.package', 'Packages/CC/sub/b.package', 'Packages/Store/d.package'];
  const files = contentFiles(mods, sets, new Set([normalize(join(mods, 'Packages/CC/fix.package')).toLowerCase()]));
  assert.deepEqual(files, moved);
  assert.deepEqual(contentFiles(mods, []), []);
  stashContent(mods, stash, files, { job: 't' });
  assert.ok(moved.every(r => !existsSync(join(mods, r)) && existsSync(join(stash, r))), 'moved to the stash');
  assert.ok(all.filter(r => !moved.includes(r)).every(r => existsSync(join(mods, r))), 'the rest stays');
  assert.deepEqual(JSON.parse(readFileSync(manifestOf(stash), 'utf8')).files, moved);
  assert.throws(() => stashContent(mods, stash, []), /manifest exists/);
  assert.deepEqual(restoreContent(stash), { restored: 4, problems: [] });
  assert.ok(all.every(r => readFileSync(join(mods, r), 'utf8') === r) && !existsSync(stash), 'all back, stash gone');
  // interrupted: manifest written, only one file moved; a file present in both places is never overwritten
  mkdirSync(stash, { recursive: true });
  writeFileSync(manifestOf(stash), JSON.stringify({ mods, files: moved }));
  move(join(mods, moved[0]), join(stash, moved[0]));
  mkdirSync(dirname(join(stash, moved[1])), { recursive: true });
  writeFileSync(join(stash, moved[1]), 'other');
  const r2 = restoreContent(stash);
  assert.equal(r2.restored, 1);
  assert.match(r2.problems.join(), /stash copy kept: Packages\/CC\/a\.package/);
  assert.ok(existsSync(join(mods, moved[0])) && readFileSync(join(mods, moved[1]), 'utf8') === moved[1] && existsSync(manifestOf(stash)), 'kept for a human');
  // ccSet: set dir -> Mods/Packages/cc-synth and back with the same manifest pair (moves, the set's manifest.json stays put)
  const set = join(tmp, 'cc-1k'), ccd = join(tmp, 'Mods2', 'Packages', 'cc-synth');
  mkdirSync(set, { recursive: true }); for (const f of ['ccgen-1-object-00000.package', 'ccgen-1-cas-00001.package', 'manifest.json']) writeFileSync(join(set, f), f);
  assert.deepEqual(ccSetFiles(set), ['ccgen-1-cas-00001.package', 'ccgen-1-object-00000.package']);
  stashContent(set, ccd, ccSetFiles(set), { ccSet: 'game-copy/cc-synth/cc-1k' });
  assert.ok(existsSync(join(ccd, 'ccgen-1-cas-00001.package')) && !existsSync(join(set, 'ccgen-1-cas-00001.package')) && existsSync(join(set, 'manifest.json')), 'moved, not copied');
  assert.deepEqual(restoreContent(ccd), { restored: 2, problems: [] });
  assert.ok(!existsSync(ccd) && ccSetFiles(set).length === 2, 'back in the set, Mods/Packages/cc-synth gone');
  assert.throws(() => parseQueue([{ id: 'c', asi: 'x', save: 'S', ccSet: '../x', run: { soak: { minutes: 1 } } }]), /ccSet must be/);
  assert.equal(parseQueue([{ id: 'c', asi: 'x', save: 'S', ccSet: 'game-copy/cc-synth/smoke-12', run: { soak: { minutes: 1 } } }])[0].ccSet, 'game-copy/cc-synth/smoke-12');
  // several sets: one manifest, files under <set>/ in Mods/Packages/cc-synth, all back after
  const set2 = join(tmp, 'game-copy', 'cc-synth', 'b2'), set1 = join(tmp, 'game-copy', 'cc-synth', 'a1');
  for (const [d, f] of [[set1, 'ccgen-1-object-00000.package'], [set2, 'ccgen-1-wall-00000.package']]) { mkdirSync(d, { recursive: true }); writeFileSync(join(d, f), f); }
  const pl = ccPlan(['game-copy/cc-synth/a1', 'game-copy/cc-synth/b2'], tmp);
  assert.deepEqual(pl.files, ['a1/ccgen-1-object-00000.package', 'b2/ccgen-1-wall-00000.package']);
  stashContent(pl.dir, ccd, pl.files, {});
  assert.ok(existsSync(join(ccd, 'b2', 'ccgen-1-wall-00000.package')) && !existsSync(join(set2, 'ccgen-1-wall-00000.package')), 'moved under <set>/');
  assert.deepEqual(restoreContent(ccd), { restored: 2, problems: [] });
  assert.ok(!existsSync(ccd) && existsSync(join(set1, 'ccgen-1-object-00000.package')), 'both sets back');
  assert.throws(() => parseQueue([{ id: 'c', asi: 'x', save: 'S', ccSet: ['game-copy/cc-synth/a', 'game-copy/cc-synth/../a'], run: { soak: { minutes: 1 } } }]), /ccSet must be/);
  assert.throws(() => parseQueue([{ id: 'c', asi: 'x', save: 'S', ccLots: 'x/lots', run: { soak: { minutes: 1 } } }]), /ccLots must be/);
  // ccLots: copies in, a player's own Library lot untouched, removed after; a changed copy is kept
  const lots = join(tmp, 'lots'), lib = join(tmp, 'Library'), lm = join(tmp, 'cc-lots.json'), lotA = 'ebf_0x00000000000000aa.package';
  mkdirSync(lots, { recursive: true }); mkdirSync(lib, { recursive: true });
  writeFileSync(join(lots, lotA), 'lot a'); writeFileSync(join(lots, 'manifest.json'), '{}'); writeFileSync(join(lib, 'ebf_0x4ded00f4a88ce110.package'), 'player lot');
  assert.deepEqual(installLots(lots, lib, lm), [lotA]);
  assert.throws(() => installLots(lots, lib, lm), /manifest exists/);
  assert.deepEqual(removeLots(lm), { removed: 1, problems: [] });
  assert.ok(!existsSync(join(lib, lotA)) && existsSync(join(lib, 'ebf_0x4ded00f4a88ce110.package')) && !existsSync(lm), 'only our copy removed');
  installLots(lots, lib, lm); writeFileSync(join(lib, lotA), 'changed in game');
  assert.match(removeLots(lm).problems.join(), /differs/);
  assert.ok(existsSync(lm), 'manifest kept for a human');
  rmSync(tmp, { recursive: true, force: true });
  // build verification (2026-10-24: the v0.1.5 release check ran an old ts3fixes.package): fixesBuild per job or queue-level
  const fq = parseQueue({ fixesBuild: 'ts3fixes.v0.1.5', jobs: [{ id: 'f1', asi: 'x', save: 'S', run: { soak: { minutes: 1 } } }, { id: 'f2', asi: 'x', save: 'S', fixesBuild: 'other', run: { soak: { minutes: 1 } } }] });
  assert.equal(fq[0].fixesBuild, 'ts3fixes.v0.1.5'); assert.equal(fq[1].fixesBuild, 'other');
  assert.throws(() => parseQueue([{ id: 'f3', asi: 'x', save: 'S', fixesBuild: 3, run: { soak: { minutes: 1 } } }]), /fixesBuild must be/);
  // fixesPackage (docs/196 §4 step 1: a shipped ts3fixes.package on profile/soak jobs)
  assert.equal(parseQueue([{ id: 'f4', asi: 'x', save: 'S', fixesPackage: 'game-copy/p', run: { profile: { minutes: 1 } } }])[0].fixesPackage, 'game-copy/p');
  assert.throws(() => parseQueue([{ id: 'f5', asi: 'x', save: 'S', fixesPackage: 'p', fixesBuild: 'b', run: { soak: { minutes: 1 } } }]), /fixesPackage/);
  assert.throws(() => parseQueue([{ id: 'f6', asi: 'x', save: 'S', fixesPackage: 'p', run: { matrix: [] } }]), /fixesPackage/);
  assert.equal(parseQueue([{ id: 'f7', asi: 'x', save: 'menu', fixesPackage: 'game-copy/p', run: { repro: 'r' } }])[0].fixesPackage, 'game-copy/p'); // start-path controls
  const bdb = loadDb();
  assert.ok(bdb.some(b => b.name === 'ts3fixes.v0.1.5' && b.kind === 'ts3fixes'), 'build DB has the v0.1.5 release package');
  assert.ok(bdb.some(b => b.name === 'ts3native.asi.rel015' && b.kind === 'asi' && b.version === '0.36.37'), 'build DB has asi.rel015 = 0.36.37');
  assert.match(fixesCheck(bdb, null, { name: 'ts3fixes.v0.1.5' }).fails[0], /missing/);
  assert.match(asiCheck([], Buffer.from('ts3native 1.2.3 %04d'), 'a', Buffer.from('old')).fails[0], /is not the job's asi/);
  assert.match(logVersionCheck('ts3native 0.36.40 x\n', '0.36.37'), /expected 0.36.37/);
  // baseline save integrity
  assert.deepEqual(guardedSaves('Baseline-BP-Spring'), ['Baseline-BP-Spring.sims3', 'ApexTest-Town.sims3']);
  assert.deepEqual(guardedSaves('Story-BP-s1.sims3'), ['Story-BP-s1.sims3', 'ApexTest-Town.sims3']);
  assert.deepEqual(guardedSaves('Matrix-BP-off-1'), ['ApexTest-Town.sims3']);
  assert.deepEqual(guardedSaves('ApexTest-Town'), ['ApexTest-Town.sims3']);
  const sd = mkdtempSync(join(tmpdir(), 'queue-saves-'));
  try {
    mkdirSync(join(sd, 'Baseline-X.sims3'));
    writeFileSync(join(sd, 'Baseline-X.sims3', 'a.nhd'), 'one');
    const h0 = hashSaves(sd, guardedSaves('Baseline-X'));
    assert.equal(h0['ApexTest-Town.sims3'], 'missing'); assert.match(h0['Baseline-X.sims3'], /^[0-9a-f]{16}$/);
    assert.deepEqual(saveDrift(h0, hashSaves(sd, guardedSaves('Baseline-X'))), []);
    writeFileSync(join(sd, 'Baseline-X.sims3', 'a.nhd'), 'two'); // a manual Save over the baseline
    assert.match(saveDrift(h0, hashSaves(sd, guardedSaves('Baseline-X')))[0], /^Baseline-X\.sims3 [0-9a-f]{16} -> [0-9a-f]{16}$/);
    writeFileSync(join(sd, 'Baseline-X.sims3', 'a.nhd'), 'one'); writeFileSync(join(sd, 'Baseline-X.sims3', 'b.nhd'), '');
    assert.equal(saveDrift(h0, hashSaves(sd, guardedSaves('Baseline-X'))).length, 1, 'a new file in the save counts');
  } finally { rmSync(sd, { recursive: true, force: true }); }
  // noisy rerun: once, timing jobs only, end-of-run
  const nj = parseQueue([{ id: 'n1', asi: 'x', save: 'S', run: { profile: { minutes: 10 } } }])[0];
  const nr = noisyRerun(nj, { noise: 'high' }, ['n1'])!;
  assert.equal(nr.id, 'n1-rerun'); assert.equal(nr.rerunOf, 'n1'); assert.equal(nr.run, nj.run);
  assert.equal(noisyRerun(nj, { noise: 'medium' }, ['n1']), null);
  assert.equal(noisyRerun(nj, { noise: 'low', error: 'noisy machine early: other CPU 14%' }, ['n1'])?.id, 'n1-rerun');
  assert.equal(noisyRerun(nr, { noise: 'high' }, ['n1', 'n1-rerun']), null, 'a rerun is not rerun again');
  assert.equal(noisyRerun(nj, { noise: 'high' }, ['n1', 'n1-rerun']), null, 'only once per run');
  assert.equal(noisyRerun({ ...nj, noiseCheck: false }, { noise: 'high' }, ['n1']), null, 'correctness jobs are not rerun');
  assert.equal(noisyRerun(parseQueue([{ id: 's1', asi: 'x', save: 'S', run: { soak: { minutes: 5 } } }])[0], { noise: 'high' }, []), null);
  assert.equal(countsNote({ rerun: 'n1-rerun' }), 'noisy: does not count, rerun as n1-rerun');
  assert.equal(countsNote({ rerunOf: 'n1', noise: 'low' }), 'rerun of n1: counts instead of it');
  assert.equal(countsNote({ rerunOf: 'n1', noise: 'high' }), 'rerun of n1: noisy again, neither counts');
  assert.equal(countsNote({}), '');
  { // measurement smoke: trigger minutes, derived / declared job, heavy tools, verdict
  const sq = parseQueue([{ id: 'c1', asi: 'x', save: 'S', noiseCheck: false, run: { profile: { minutes: 90 } } },
    { id: 't1', asi: 'x', save: 'S', run: { profile: { minutes: 30, repros: ['r1', { id: 'r2', minutes: 40 }] } } }, { id: 's1', asi: 'x', save: 'S', run: { soak: { minutes: 300 } } }]);
  assert.equal(timingMinutes(sq), 70, 'only timing jobs count; repros items with their own minutes');
  const smk = smokeJob(sq, sq)!;
  assert.equal(smk.id, 't1-smoke'); assert.equal(smk.run.profile!.minutes, SMOKE_MIN); assert.deepEqual((smk.run.profile as any).repros, ['r1']); assert.equal((smk as any).reuseSession, false);
  assert.equal(smokeJob({ smoke: { asi: 'y', save: 'S', run: { profile: { minutes: 5 } } }, jobs: sq }, sq)!.id, 'smoke');
  assert.equal(smokeJob(sq, [sq[0], sq[2]]), null, 'no timing job, no smoke');
  const tc = { tools: { samplers: { class: 'heavy', msPerS: null }, 'RouteSnap.audits': { class: 'heavy', msPerS: null }, 'prof-stages': { class: 'light', msPerS: 1 } }, bundles: [] };
  assert.deepEqual(heavyTools(sq[1], '[RouteSnap]\nEnabled=1\nAuditEvery=0\n', tc), []);
  assert.deepEqual(heavyTools({ ...sq[1], ini: { 'RouteSnap.AuditEvery': 50 } }, '[RouteSnap]\nEnabled=1\nAuditEvery=0\n', tc), ['RouteSnap.audits']);
  assert.deepEqual(heavyTools(parseQueue([{ id: 'w', asi: 'x', save: 'S', run: { profile: { minutes: 10 } } }])[0], '', tc), ['samplers'], 'plain window = stack samplers');
  const okRow = { pass: true, verdict: [] }, win = { tag: 't', arms: true, iters: { on: 2, off: 2 }, invalid: null, wait: { on: 240, off: 240 } };
  assert.deepEqual(smokeProblems({ row: okRow, wins: [win], log: 'native_set ok', p6: true, nativeLog: 'p6 waits arm 0 taken: x\np6 waits arm 1 taken: y' }), []);
  assert.equal(smokeProblems({ row: null, wins: [], log: '', p6: false, nativeLog: '' }).length, 1);
  const bad = smokeProblems({ row: { pass: false, verdict: ['FAIL: x'] }, wins: [{ ...win, iters: { on: 2, off: 0 }, wait: null }], log: 'repro step native_set failed: native_set: p6warm=2 did not stick (reads 1)', p6: true, nativeLog: 'p6 waits arm 1 taken' });
  assert.deepEqual(bad.map(b => b.split(':')[0]), ['smoke job failed', 't', 't', '[P6Waits] no \'p6 waits arm 0\' line in ts3native.log', 'switch read-back']);
  assert.deepEqual(smokeProblems({ row: okRow, wins: [{ ...win, arms: false, iters: null, wait: null }], log: '', p6: false, nativeLog: '' }), [], 'no arms: no split needed'); }
  console.log('self-check ok');
  process.exit(0);
}

const gameRunning = () => { try { return execFileSync('tasklist', ['/NH', '/FI', 'IMAGENAME eq TS3W.exe']).toString().includes('TS3W.exe'); } catch { return true; } };
const fixesTool = join(ROOT, 'tools', 'setup', 'install-fixes.ts');
// the Steam TS3W.exe exits on its own ("application-requested") when Steam is not running: start it silently, wait up to 180 s
// for its web helper (2026-10-17: Steam had closed, so one job's game quit mid-run and the next two quit at launch)
const steamUp = () => { try { return execFileSync('tasklist', ['/NH', '/FI', 'IMAGENAME eq steamwebhelper.exe']).toString().includes('steamwebhelper'); } catch { return false; } };
async function ensureSteam() {
  if (steamUp()) return;
  const exe = join(process.env['ProgramFiles(x86)'] ?? 'C:\\Program Files (x86)', 'Steam', 'steam.exe');
  log(`Steam not running: starting ${exe} -silent`);
  spawn(exe, ['-silent'], { detached: true, stdio: 'ignore' }).unref();
  for (let i = 0; i < 180 && !steamUp(); i++) await sleep(1000);
  if (!steamUp()) throw new Error('Steam is not running and did not start in 180 s (the Steam game quits without it)');
  await sleep(15000); // login
}

if (values['restore-content']) {
  const stashes = [CC_DIR, CONTENT_STASH].filter(d => existsSync(manifestOf(d)));
  if (!stashes.length && !existsSync(LOTS_MANIFEST)) { console.log(`no content stash manifest in ${CONTENT_STASH} or ${CC_DIR}, no ${LOTS_MANIFEST}: nothing to restore`); process.exit(0); }
  if (gameRunning()) { console.error('TS3W.exe is running: close the game first'); process.exit(2); }
  if (existsSync(LOTS_MANIFEST)) {
    const r = removeLots();
    console.log(`removed ${r.removed} ccLots copies from ${LIBRARY}`);
    for (const p of r.problems) console.error(`  ${p}`);
    if (r.problems.length) { console.error(`manifest kept: ${LOTS_MANIFEST}; fix by hand, then rerun --restore-content`); process.exit(1); }
  }
  for (const d of stashes) {
    const r = restoreContent(d);
    console.log(`restored ${r.restored} packages from ${d}`);
    for (const p of r.problems) console.error(`  ${p}`);
    if (r.problems.length) { console.error(`manifest kept: ${manifestOf(d)}; fix by hand, then rerun --restore-content`); process.exit(1); }
  }
  execFileSync(process.execPath, [fixesTool, 'install', '--only', 'cache-clear'], { stdio: 'inherit' });
  process.exit(0);
}

// ---------------------------------------------------------------- queue run
const file = positionals[0];
if (!file) { console.error('usage: node tools/harness/queue.ts <queue.json> [--from id] [--only id,id] [--dry-run] [--no-smoke] [--stop-on-fail] | --self-check | --restore-content'); process.exit(2); }
// an unfinished run's packages are still in the stash: put them back before measuring anything
const staleDir = [CONTENT_STASH, CC_DIR].find(d => existsSync(manifestOf(d)));
const staleFile = staleDir ? manifestOf(staleDir) : existsSync(LOTS_MANIFEST) ? LOTS_MANIFEST : '';
const stale = staleFile ? `content stash manifest from an unfinished run: ${staleFile}; run node tools/harness/queue.ts --restore-content` : '';
if (stale && !values['dry-run']) { console.error(`refusing to start: ${stale}`); process.exit(2); }
const queueName = basename(file).replace(/\.json$/i, '');
const queueJson = JSON.parse(readFileSync(resolve(file), 'utf8'));
const all = parseQueue(queueJson);
let jobs = all;
if (values.from) {
  const i = all.findIndex(j => j.id === values.from);
  if (i < 0) { console.error(`--from: no job ${values.from}`); process.exit(2); }
  jobs = all.slice(i);
}
if (values.only) {
  const ids = values.only.split(',').map(s => s.trim());
  const miss = ids.filter(id => !all.some(j => j.id === id));
  if (miss.length) { console.error(`--only: no job ${miss.join(', ')}`); process.exit(2); }
  jobs = jobs.filter(j => ids.includes(j.id));
}
const REPROS = JSON.parse(readFileSync(H('repros.json'), 'utf8')) as { id: string; save: string; loop?: Loop }[];
const loopOfId = (id?: string) => REPROS.find(r => r.id === id)?.loop;
const loopOf = (j: Job) => { const l = loopOfId(j.run.profile?.repro); return l && withRule(l, j.run.profile?.loopRule); };
const asiPath = (j: Job) => (j.asi === 'tested' ? TESTED_ASI : resolve(ROOT, j.asi));
// "tested" must be the source's version: a stale out/ build (0.10.1, async without the route gate) ran as "tested" for 3 days
// and crashed bp-watch-stalk-worker (docs/113 §8.6). The DLL holds the log line format "ts3native <version> %04d".
const SRC_VERSION = /#define TS3NATIVE_VERSION "([^"]+)"/.exec(readFileSync(join(ROOT, 'native', 'ts3native', 'ts3native.cpp'), 'utf8'))?.[1];
const staleAsi = (j: Job) => {
  if (j.asi !== 'tested' || !existsSync(TESTED_ASI)) return '';
  const v = /ts3native (\d+\.\d+\.\d+) %04d/.exec(readFileSync(TESTED_ASI).toString('latin1'))?.[1];
  return v === SRC_VERSION ? '' : `tested build is ts3native ${v ?? '?'}, source is ${SRC_VERSION}: rebuild with native/ts3native/build.cmd`;
};
const saveOf = (j: Job) => j.save ?? (j.run.menuMemory ? 'menu' : REPROS.find(r => r.id === j.run.repro)?.save);
const saveFile = (s: string) => (/\.sims3$/i.test(s) ? s : `${s}.sims3`);
// story jobs (docs/171 safety): the game loads Story-<save>-s<seed>, a copy queue.ts makes, never the named save itself
function storyCopy(j: Job, s: string) { // a declaration: --self-check runs before this line
  return !j.run.profile?.story || s === 'menu' || /^Story-/i.test(s) ? s
    : `Story-${s.replace(/\.sims3$/i, '').replace(/[^A-Za-z0-9_-]/g, '_')}-s${(j.run.profile.story as any).seed ?? j.run.profile.seed ?? 1}`;
}
const ageName = (j: Job) => j.run.age?.save ?? `${j.id}-${new Date().toISOString().slice(0, 10)}`;
const profileTag = (j: Job) => `${queueName}-${new Date().toISOString().slice(0, 10)}-${j.id}`;
// p: the job's profile, or for one repro of profile.repros that merged with the item (docs/170 §1); extra: session flags
const profileArgs = (j: Job, p: any = j.run.profile!, tag = profileTag(j), extra: string[] = []): string[] => [H('profile-world.ts'), '--tag', tag, '--minutes', String(p.minutes), ...(p.seed !== undefined ? ['--seed', String(p.seed)] : []),
      ...(p.repro ? ['--repro', p.repro] : []), ...(p.loopRule ? ['--loop-rule', JSON.stringify(p.loopRule)] : []),...(p.mainstack ? ['--mainstack', String(p.mainstack)] : []),
      ...(p.ab ? ['--ab', p.ab, '--slice', String(p.slice ?? 2), ...(p.abOnFirst ? ['--ab-on-first'] : [])] : []), ...(p.untilCompares ? ['--until-compares', String(p.untilCompares)] : []), ...(p.fixesOff ? ['--fixes-off', p.fixesOff] : []), ...(p.fixesOn ? ['--fixes-on', p.fixesOn] : []), ...(p.taskstall ? ['--taskstall', String(p.taskstall)] : []),
      ...(p.inroom ? ['--inroom'] : []), ...(p.watchstalk ? ['--watchstalk'] : []), ...(p.watchstalk === 'worker' ? ['--watchstalk-worker'] : []), ...(p.livereads2 ? ['--livereads2'] : []), ...(p.f4b ? ['--f4b'] : []), ...(p.vres ? ['--vres'] : []), ...(p.vresDry ? ['--vres-dry', String(p.vresDry)] : []), ...(p.noSamplers ? ['--no-samplers'] : []), ...(p.noFrames ? ['--no-frames'] : []), ...(p.cameraPan ? ['--camera-pan'] : []), ...(p.profOpts ? ['--prof-opts', JSON.stringify(p.profOpts)] : []), ...(p.noEnv ? ['--no-env'] : []), ...(p.envGuard === false ? ['--no-env-guard'] : []), ...(p.story ? ['--story', JSON.stringify(p.story)] : []), ...(Number(j.ini?.['ProbeSnap.Enabled']) || p.noProf ? ['--no-prof'] : []), ...(p.travel ? ['--travel', p.travel] : []), ...(p.fixesRequire ? ['--fixes-require', p.fixesRequire] : []), ...(p.frameLead ? ['--frame-lead', JSON.stringify(p.frameLead)] : []), ...(p.hitchTasks ? ['--hitch-tasks', JSON.stringify(p.hitchTasks)] : []), ...extra];
const runArgs = (j: Job): string[] | null =>
  j.run.repro ? [H('repro.ts'), 'run', j.run.repro, '--no-load'] : j.run.saveReload ? [H('save-reload.ts'), ...j.run.saveReload, '--port', values.port!] : j.run.matrix ? [H('save-matrix.ts'), ...j.run.matrix, ...(j.saveMatrix?.fixes ? ['--fixes', j.saveMatrix.fixes] : [])]
    : j.run.profile ? profileArgs(j)
    : j.run.age ? [H('age-save.ts'), '--days', String(j.run.age.days), '--checkpoint', String(j.run.age.checkpoint), '--save', ageName(j), '--port', values.port!,
      '--keep-alive', String(j.run.age.keepAlive ?? -50)] : null;
const SAVES = join(USER, 'Saves');
const BUILDS: Build[] = loadDb(), FIXES_INST = join(USER, 'Mods', 'Packages', 'ts3fixes.package'), FIXES_BUILT = join(ROOT, 'build', 'ts3fixes.package');
const readOr = (p: string) => (existsSync(p) ? readFileSync(p) : null);
const fixesSrc = (j: Job) => (j.fixesPackage ? resolve(ROOT, j.fixesPackage) : FIXES_BUILT);
// after cycle.ts --restart (installs build/ts3fixes.package): the job's shipped package instead; the next restart puts build/ back
const installFixesPackage = (j: Job) => {
  if (!j.fixesPackage) return;
  copyFileSync(fixesSrc(j), FIXES_INST);
  rmSync(join(USER, 'scriptCache.package'), { force: true });
};
const LOG = join(BIN, 'ts3native.log');
// saves an age job would overwrite: <name>.sims3 and <name>-day*.sims3
const ageClash = (j: Job) => { const n = ageName(j).toLowerCase(); return existsSync(SAVES) ? readdirSync(SAVES).filter(f => f.toLowerCase() === `${n}.sims3` || f.toLowerCase().startsWith(`${n}-day`)) : []; };
const DEFAULT_INI = join(BIN, 'Default.ini'), S3PP_INI = join(BIN, 'Sims3Performance.ini'), S3SS_TOML = join(USER, 'S3SS', 'S3SS.toml'), DXVK_CONF = join(BIN, 'dxvk.conf');
const sha = (p: string) => (existsSync(p) ? createHash('sha256').update(readFileSync(p)).digest('hex').slice(0, 12) : 'missing');
// docs/170: jobs with the same key need the same game start (what is written before the launch); reuseOf null = reuse off
const reuseOf = (j: Job): ReuseOpts | null => parseReuse(j.reuseSession);
const keyOf = (j: Job) => stableKey({ asi: asiPath(j), sha: sha(asiPath(j)), ini: jobIni(j) ?? {}, fixes: j.fixes ?? null, fixesPackage: j.fixesPackage ?? null, save: saveFile(saveOf(j) ?? ''), content: j.content ?? 'as-is', ccSet: j.ccSet ?? null, ccLots: j.ccLots ?? null,
  gameArgs: j.gameArgs ?? [], defaultIni: j.defaultIni ?? null, s3ppIni: j.s3ppIni ?? null, s3ssToml: j.s3ssToml ?? null, dxvkConf: j.dxvkConf ?? null, pin: pinConfig(j.pin) ?? null });
const itemsOf = (j: Job): ReproItem[] | null => (j.run.profile?.repros ? reproItems(j.run.profile.repros) : null);
// the next job may take over the running game: reuse on for it, same key, a profile run that can share a load
const canTakeOver = (key: string, next?: Job) => !!next && !!reuseOf(next) && !reuseBlock(next) && keyOf(next) === key;
for (const j of jobs) {
  if (j.run.repro && !REPROS.some(r => r.id === j.run.repro)) { console.error(`${j.id}: no repro ${j.run.repro} in tools/harness/repros.json`); process.exit(2); }
  if (j.run.profile?.repro && !REPROS.some(r => r.id === j.run.profile!.repro)) { console.error(`${j.id}: no repro ${j.run.profile.repro} in tools/harness/repros.json`); process.exit(2); }
  try { if (loopOf(j)) { checkLoop(loopOf(j)); if (j.run.profile!.ab || j.run.profile!.untilCompares) throw new Error('a loop repro takes no profile.ab / untilCompares'); } } catch (e: any) { console.error(`${j.id}: repro ${j.run.profile!.repro}: ${e.message}`); process.exit(2); }
  for (const it of itemsOf(j) ?? []) {
    if (!REPROS.some(r => r.id === it.id)) { console.error(`${j.id}: profile.repros: no repro ${it.id} in tools/harness/repros.json`); process.exit(2); }
    try { if (loopOfId(it.id)) checkLoop(loopOfId(it.id)); } catch (e: any) { console.error(`${j.id}: repro ${it.id}: ${e.message}`); process.exit(2); }
  }
  if (!saveOf(j)) { console.error(`${j.id}: no save`); process.exit(2); }
  if (j.fixesBuild && !BUILDS.some(b => b.name === j.fixesBuild && b.kind === 'ts3fixes')) { console.error(`${j.id}: fixesBuild ${j.fixesBuild} is not a ts3fixes entry in docs/data/builds.json (node tools/builds.ts list)`); process.exit(2); }
}

// Keyboard/chassis RGB off while the queue runs (night runs in a dark room): Alienware Command Center turns the lights back
// on, so alienfx-cli (AlienFX Tools, if installed) sets them to black every minute. TS3_LIGHTS=on skips it.
const AFX = join(process.env.LOCALAPPDATA ?? '', 'AlienFX Tools', 'alienfx-cli.exe');
if (!values['dry-run'] && process.env.TS3_LIGHTS !== 'on' && existsSync(AFX)) {
  const lightsOff = () => { try { execFileSync(AFX, ['setall=0,0,0'], { stdio: 'ignore', timeout: 15000, windowsHide: true }); } catch { /* next tick retries */ } };
  lightsOff();
  setInterval(lightsOff, 60000).unref();
}
// Spotify costs CPU beside timing runs: stopped at night-run starts (TS3_NIGHT=1) or with --close-spotify (Rodney 2026-10-24);
// daytime runs keep his work apps (Rodney 2026-10-26); other apps stay behind night-run.ts --close
if (!values['dry-run'] && (process.env.TS3_NIGHT === '1' || values['close-spotify'])) {
  const spotify = () => { try { return execFileSync('tasklist', ['/NH', '/FI', 'IMAGENAME eq Spotify.exe']).toString().split(/\r?\n/).filter(l => /^Spotify\.exe/i.test(l)).length; } catch { return 0; } };
  const n = spotify();
  if (n) { try { execFileSync('taskkill', ['/F', '/T', '/IM', 'Spotify.exe'], { stdio: 'ignore', timeout: 15000 }); } catch { /* checked below */ } }
  log(n ? `Spotify: ${n} processes stopped${spotify() ? ` (${spotify()} still running)` : ''}` : 'Spotify: not running');
}
let fixesSrcAt = 0; // last commit time of the ts3fixes source (ms), for the stale package pin warning
try { fixesSrcAt = 1000 * Number(execFileSync('git', ['-C', ROOT, 'log', '-1', '--format=%ct', '--', 'mods/ts3fixes', 'tools/setup/build-fixes.ts']).toString().trim()); } catch { /* not a repo */ }
const pairs = [...pairWarnings(all), ...pinWarnings(all, SRC_VERSION, p => (existsSync(resolve(ROOT, p)) ? asiVersion(readFileSync(resolve(ROOT, p))) : undefined),
  p => existsSync(resolve(ROOT, p)) && statSync(resolve(ROOT, p)).mtimeMs < fixesSrcAt)];

// measurement smoke (header): planned here so --dry-run shows it, run below before the queue touches the machine
const smoke = values['no-smoke'] || timingMinutes(jobs) <= SMOKE_OVER_MIN ? null : smokeJob(queueJson, jobs);
const smokeHeavy = smoke ? [...jobs.filter(isTimingJob), smoke].flatMap(j => heavyTools(j, readFileSync(REPO_INI, 'utf8')).map(t => `${j.id}: ${t}`)) : [];
let harnessCommit = '';
try { harnessCommit = execFileSync('git', ['-C', ROOT, 'log', '-1', '--format=%H', '--', 'tools/harness']).toString().trim(); } catch { /* not a repo */ }
// shortcut: uncommitted harness edits do not change the key, upgrade if smoke runs from a dirty tree ever matter
const smokeKey = smoke ? createHash('sha256').update(readFileSync(resolve(file))).update(existsSync(asiPath(smoke)) ? readFileSync(asiPath(smoke)) : 'no asi').update(harnessCommit).digest('hex') : '';
const SMOKE_CACHE = join(OUT, 'smoke-cache.json');
const smokeCache: Record<string, { at: string }> = (() => { try { return JSON.parse(readFileSync(SMOKE_CACHE, 'utf8')); } catch { return {}; } })();
const smokeLine = smoke ? `measurement smoke ${smoke.id} (${smoke.run.profile!.minutes} min) before ${timingMinutes(jobs)} timing minutes${smokeCache[smokeKey] ? `: passed ${smokeCache[smokeKey].at}, skipped` : ''}${smokeHeavy.length ? `; heavy tools on, the queue would stop: ${smokeHeavy.join(', ')}` : ''}`
  : `measurement smoke: none (${values['no-smoke'] ? '--no-smoke' : `${timingMinutes(jobs)} timing minutes <= ${SMOKE_OVER_MIN}`})`;

if (values['dry-run']) {
  console.log(`queue ${queueName}: ${jobs.length} of ${all.length} jobs (dry run, nothing touched)`);
  console.log(`  ${smokeLine}`);
  if (stale) console.log(`** a real run would refuse to start: ${stale}`);
  for (const w of pairs) console.log(`** ${w}`);
  let dryKey = '', dryWin = 0, dryPrev = '';
  for (const j of jobs) {
    const a = asiPath(j), s = saveOf(j)!, r = runArgs(j);
    console.log(`\n${j.id}${j.note ? ` - ${j.note}` : ''} (timeout ${j.timeoutMin ?? 60} min)`);
    // docs/170: the game of the job before is taken over when both have reuse on, the same game start and the window budget left
    const ro = reuseOf(j), key = keyOf(j), items = itemsOf(j), reused = !!ro && !reuseBlock(j) && key === dryKey && dryWin < ro.maxJobs;
    console.log(`  0 session: ${reused ? `reuses the game of ${dryPrev} (window #${dryWin} since load, if its carry-over check passes: switches back to the load values, drift within ${JSON.stringify(ro)}); steps 1-6 skipped`
      : `fresh game start + load${ro && reuseBlock(j) ? ` (no reuse: ${reuseBlock(j)})` : ro && dryKey && key !== dryKey ? ' (game start differs from the job before)' : ro && dryKey ? ` (maxJobs ${ro.maxJobs} reached)` : ''}`}`);
    dryWin = (reused ? dryWin : 0) + (items?.length ?? 1); dryKey = ro && !reuseBlock(j) ? key : ''; dryPrev = j.id;
    if (!reused) {
    if (CONTENT[j.content ?? 'as-is'].length) {
      const n = contentFiles(join(USER, 'Mods'), contentSets(j.content!), fixFiles(FIXES_STATE)).length;
      console.log(`  0 content ${j.content}: game closed, manifest, move ${n} packages now in Mods (${CONTENT[j.content!].join(' + ')} sets) to ${CONTENT_STASH}; cache-clear; back + cache-clear after the job`);
    }
    if (j.ccSet) {
      const n = ccSetReady(j.ccSet) ? ccPlan(j.ccSet).files.length : 0;
      console.log(`  0 ccSet ${[j.ccSet].flat().join(' + ')}: ${n ? `game closed, manifest, move ${n} packages to ${CC_DIR}; cache-clear; back to the set + cache-clear after the job` : '** MISSING or empty: make it with tools/cc-gen.ts; the job refuses before touching the game **'}`);
    }
    if (j.ccLots) {
      const n = existsSync(join(ROOT, j.ccLots)) ? lotFiles(join(ROOT, j.ccLots)).length : 0;
      console.log(`  0 ccLots ${j.ccLots}: ${n ? `manifest ${LOTS_MANIFEST}, copy ${n} lots into ${LIBRARY}; deleted after the job` : '** MISSING or empty: make it with tools/cc-lot-gen.ts; the job refuses before touching the game **'}`);
    }
    console.log(`  1 node tools/harness/cycle.ts --restart`);
    console.log(`  2 asi ${j.asi === 'tested' ? 'tested build' : j.asi}${existsSync(a) ? '' : '  ** MISSING: the job refuses before touching the game **'}${staleAsi(j) ? `  ** STALE: ${staleAsi(j)}; the job refuses **` : ''} -> Bin/ts3native.asi`);
    // build check (what a real run verifies before the game starts; cycle.ts installs build/ts3fixes.package)
    if (existsSync(a)) {
      const ac = asiCheck(BUILDS, readFileSync(a), j.asi, readFileSync(a));
      console.log(`  2 build check: asi sha256 ${ac.sha.slice(0, 12)} = ${ac.names.join(', ') || 'not in DB'}, ts3native ${ac.version ?? '?'} (ts3native.log first line must say it)${ac.fails.length ? `  ** ${ac.fails.join('; ')}: the job refuses **` : ''}`);
    }
    const built = readOr(fixesSrc(j)), inst = readOr(FIXES_INST);
    if (j.fixesPackage) console.log(`  1 ts3fixes.package = ${j.fixesPackage} (${built ? createHash('sha256').update(built).digest('hex').slice(0, 12) : '** MISSING: the job refuses **'}) over the build/ copy, script cache cleared`);
    const fc = built ? fixesCheck(BUILDS, built, { name: j.fixesBuild, built })
      : { fails: [`${j.fixesPackage ?? 'build/ts3fixes.package'} missing (cycle.ts installs build/; node tools/setup/build-fixes.ts)`], names: [], digest: undefined, expected: (BUILDS.find(b => b.name === j.fixesBuild) as any)?.tuningDigest as string | undefined };
    let instT = 'missing';
    try { if (inst) instT = packageTuning(inst).digest.slice(0, 12); } catch (e: any) { instT = e.message; }
    console.log(`  2 build check: ts3fixes expected ${j.fixesBuild ?? j.fixesPackage ?? 'build/ts3fixes.package'} tuning ${fc.expected?.slice(0, 12) ?? '?'}; ${j.fixesPackage ? 'package' : 'build/'} tuning ${fc.digest?.slice(0, 12) ?? 'missing'} (${fc.names.join(', ') || 'not in DB'}); installed now ${instT}${fc.fails.length ? `  ** once cycle.ts installs build/: ${fc.fails.join('; ')}: the job refuses **` : ''}`);
    console.log(`  2 save guard: ${Object.entries(hashSaves(SAVES, guardedSaves(s))).map(([k, v]) => `${k} ${v}`).join(', ')} hashed before and after; a change FAILs the job and stops the queue`);
    const ji = jobIni(j);
    if (callSitesOffNote(j)) console.log(`  3 ${callSitesOffNote(j)}`);
    console.log(`  3 Bin/ts3native.ini = repo ini${Object.keys(ji ?? {}).length ? ` + ${Object.entries(ji!).map(([k, v]) => `${k}=${v}`).join(', ')}` : ' (unchanged)'}`);
    if (j.run.worldimage && !existsSync(String(ji!['WorldImage.Helper']))) console.log(`  3 ** helper MISSING: build native\\worldimage\\build.cmd; the job refuses before touching the game **`);
    if (j.defaultIni) console.log(`  3 Bin/Default.ini + ${Object.entries(j.defaultIni).map(([k, v]) => `${k}=${v}`).join(', ')}   (put back after the job)`);
    if (j.s3ppIni) console.log(`  3 Bin/Sims3Performance.ini + ${Object.entries(j.s3ppIni).map(([k, v]) => `${k}=${v}`).join(', ')}   (put back after the job)`);
    if (j.dxvkConf) console.log(`  3 Bin/dxvk.conf + ${Object.entries(j.dxvkConf).map(([k, v]) => `${k} = ${v}`).join(', ')}${existsSync(DXVK_CONF) ? '' : '  ** MISSING **'}   (put back after the job)`);
    if (j.s3ssToml) console.log(`  3 S3SS/S3SS.toml + ${Object.entries(j.s3ssToml).map(([k, v]) => `${k}=${v}`).join(', ')}${existsSync(S3SS_TOML) ? '' : '  ** MISSING **'}   (put back after the job)`);
    if (j.routingFailFast) console.log(`  5 ${describeFailFast(j.routingFailFast)}`);
    if (j.earlyGates) console.log(`  5 ${describeGates(j.earlyGates)}`);
    if (j.stopQueueOnFail || values['stop-on-fail']) console.log('  9 stopQueueOnFail: a FAIL row stops the queue (STOP: line)');
    if (j.fixes?.install?.length) console.log(`  4 node tools/setup/install-fixes.ts install --only ${j.fixes.install.join(',')}   (uninstalled after the job)`);
    if (j.fixes?.uninstall?.length) console.log(`  4 node tools/setup/install-fixes.ts uninstall --only ${j.fixes.uninstall.join(',')}   (reinstalled after the job)`);
    console.log(`  5 python tools/probe/ts3probe.py serve --launch${j.gameArgs?.length ? ` -- ${j.gameArgs.join(' ')}` : ''}   (child, killed at job end)`);
    console.log(`  5 ${describePin(pinConfig(j.pin))}; machine load every 30 s -> ${queueName}-<date>-${j.id}-machine.jsonl (noise verdict in the note)${j.run.profile ? (j.noiseCheck === false ? '; early noise check off (noiseCheck false)' : '; early noise check ~2.5 min in: other CPU >= 10% or clock drop stops the job; noisy -> rerun once as ' + (j.rerunOf ? 'none (this is the rerun)' : `${j.id}-rerun`) + ' at the end') : ''}`);
    if (storyCopy(j, s) !== s) console.log(`  6 copy Saves/${saveFile(s)} -> Saves/${saveFile(storyCopy(j, s))}   (story scratch copy, replaced each job; the Baseline file is only read)`);
    console.log(`  6 ${s === 'menu' ? 'wait for the main menu (agent ping)' : `node tools/harness/cycle.ts --save ${saveFile(storyCopy(j, s))}`}`);
    }
    if (items) items.forEach((it, k) => {
      const p = { ...j.run.profile, ...it, repro: it.id }, l = loopOfId(it.id);
      console.log(`  7 repro ${k + 1}/${items.length} ${it.id} -> tag ${subTag(profileTag(j), it.id)}, cap ${p.minutes} min${l ? `, stop rule ${ruleText(l.until)}` : ''}${it.nativeRequire ? `, nativeRequire ${it.nativeRequire}` : ''}${p.fixesRequire ? `, fixesRequire ${p.fixesRequire}` : ''}${p.hitchTasks ? `, hitchTasks ${JSON.stringify(p.hitchTasks)}` : ''}${p.frameLead ? `, frameLead ${JSON.stringify(p.frameLead)}` : ''}`);
      if (k < items.length - 1) console.log(`    carry-over check (switches back, drift within ${JSON.stringify(ro ?? REUSE_DEFAULTS)}), reload on failure`);
    });
    if (j.run.age) { const c = ageClash(j); console.log(`  7 saves ${ageName(j)}-dayN every ${j.run.age.checkpoint} sim days, final ${ageName(j)}; per checkpoint size/types/save ms/memory -> ${queueName}-<date>-${j.id}-age.csv${c.length ? `  ** REFUSED: would overwrite ${c.join(', ')} **` : ''}`); }
    if (!items) console.log(`  7 ${r ? `node ${r.map(x => x.replace(ROOT + '\\', '').replace(/\\/g, '/')).join(' ')}` : j.run.soak ? `soak ${j.run.soak.minutes} min at ${j.run.soak.speed ?? 'ultra'} (autopilot + watchdog.ts)`
      : j.run.routelock ? `routelock ${j.run.routelock.minutes} min at ${j.run.routelock.speed ?? 'ultra'}: autopilot, /routelock?start=1, watchdog.ts, /routelock?stop=1 -> row.routelock`
      : j.run.worldimage ? `worldimage: require 'OK   world image' in ts3native.log; autopilot ${j.run.worldimage.speed ?? 'normal'}, settle ${j.run.worldimage.settleS ?? 60} s; ${j.run.worldimage.captures} x (write <OutDir>\\request, wait for the new wi-* folder's header.txt 'loose' line: progress check every 30 s, 2 misses = FAIL; gap ${j.run.worldimage.gapS ?? 30} s) -> row.worldimage (stall ms, MB, loose, lock owners, G3-1 statics read)` : `menu memory after ${j.run.menuMemory?.waitS ?? 60} s${j.run.menuMemory?.draws ? `, ${j.run.menuMemory.draws} RandomGen draws` : ''}`}`);
    if (j.saveMatrix?.fixes === 'shipped') console.log(`  7 release mode (save-matrix.ts --fixes shipped); when its reload case is pending: for ON then OFF save: close game, cycle.ts --restart, asi + ini again, probe, cycle.ts --save <it>, save-matrix.ts --reload-check [--off]`);
    if (loopOf(j)) { const l = loopOf(j)!; console.log(`  7 stop rule: ${ruleText(l.until)} (fixed before the run), arms ${l.arms.join(',')} repeated; cap ${Math.min(l.maxMinutes ?? j.run.profile!.minutes, j.run.profile!.minutes)} min (profile.minutes = cap)`); }
    if (j.minCounters !== undefined) console.log(`  7 refuse unless /native after the load reports counters >= v${j.minCounters}; full /native -> ${queueName}-<date>-${j.id}-native.json${j.ini?.['Verify.Enabled'] ? ' ([Verify] maxima zeroed after that read: live window)' : ''}`);
    console.log(`  8 /memory + /native (${['version', ...Object.keys(j.ini ?? {}).map(k => k.split('.')[0]).filter((v, i, x) => x.indexOf(v) === i)].join(', ')}) before/after; close game; ai-usage.ts G ${j.id}`);
  }
  process.exit(0);
}

// measurement smoke (header): the smoke job runs as a child queue (same game handling), then its outputs are judged
if (smoke) {
  log(smokeLine);
  if (smokeHeavy.length) { log('STOP: heavy measurement tools in timing jobs (docs/168 §9): turn them off, or --no-smoke for a correctness-only queue'); process.exit(4); }
  if (!smokeCache[smokeKey]) {
    const name = `${queueName}-smoke`, qf = join(OUT, `${name}.json`);
    const { jobs: _j, smoke: _s, ...queueLevel } = Array.isArray(queueJson) ? {} as any : queueJson;
    mkdirSync(OUT, { recursive: true });
    writeFileSync(qf, JSON.stringify({ ...queueLevel, jobs: [smoke] }, null, 1));
    await waitHold(log); // crash-triage build running (hold.ts): no smoke until it is done
    const code = await new Promise<number | null>(res => spawn(process.execPath, [import.meta.filename, qf, '--no-smoke', '--port', values.port!],
      { cwd: ROOT, stdio: 'inherit', env: { ...process.env, TS3_SMOKE_PARENT: String(process.pid) } }).on('exit', c => res(c)));
    const qj = queueFile(name, undefined, OUT), row = qj ? jsonlRows(qj).filter(r => r.job === smoke.id || r.job === `${smoke.id}-rerun`).at(-1) ?? null : null;
    const p: any = smoke.run.profile, read = (f: string) => { try { return readFileSync(f, 'latin1'); } catch { return ''; } };
    const wins: SmokeWin[] = (row?.repros?.length ? row.repros.map((r: any) => ({ tag: r.tag, repro: r.id })) : row?.profileTag ? [{ tag: row.profileTag, repro: p.repro }] : [])
      .map(({ tag, repro }: { tag: string; repro?: string }) => {
        let iters = null, invalid: string | null = null, wait = null;
        try { const r = hitchRun(tag); iters = runIters(r); invalid = r.invalid; } catch (e) { invalid = `no hitch files (${String(e).split('\n')[0]})`; }
        try { const m = /: on (\d+) s, off (\d+) s/.exec(execFileSync(process.execPath, [H('wait-ab.ts'), tag], { cwd: ROOT, stdio: ['ignore', 'pipe', 'ignore'] }).toString()); if (m) wait = { on: +m[1], off: +m[2] }; } catch { /* no <tag>-ab.json */ }
        return { tag, arms: !!p.ab || (loopOfId(repro)?.arms.length ?? 0) > 1, iters, invalid, wait };
      });
    const probs = smokeProblems({ row, wins, log: qj && row ? read(qj.replace(/\.jsonl$/, `-${row.job}.log`)) : '', p6: Number(smoke.ini?.['P6Waits.Enabled']) === 1, nativeLog: row ? callsiteLogs(row).map(read).join('\n') : '' });
    if (probs.length) { log(`STOP: measurement smoke ${smoke.id} failed (child exit ${code}): ${probs.join('; ')}; the queue did not start (fix, or --no-smoke for a correctness-only queue)`); process.exit(4); }
    smokeCache[smokeKey] = { at: new Date().toISOString() };
    writeFileSync(SMOKE_CACHE, JSON.stringify(smokeCache, null, 1));
    log(`measurement smoke ${smoke.id}: PASS (${wins.map(w => `${w.tag} on ${w.iters?.on ?? '-'} / off ${w.iters?.off ?? '-'}`).join(', ')}); starting the queue`);
  }
}

// ---- helpers that touch the machine
async function get(path: string, timeoutMs = 20000): Promise<any> {
  try { return await (await fetch(`${base}/${path}`, { signal: AbortSignal.timeout(timeoutMs) })).json(); } catch { return null; }
}
const agent = (cmd: string, args?: object) => { const q = new URLSearchParams({ cmd, timeout: '8000' }); if (args) q.set('args', JSON.stringify(args)); return get(`agent?${q}`); };
const killTree = (pid?: number) => { if (pid) try { execFileSync('taskkill', ['/T', '/F', '/PID', String(pid)], { stdio: 'ignore' }); } catch { /* gone */ } };

// set while a job's game runs: '' when the game and the probe server are up, else why not; stall = the guard's verdict
let jobGuard: (() => string) | null = null, stall = '';
// set while a timing (profile) job's child runs: '' = fine or not judged yet, else the early noise verdict (stops the child at once)
let noiseGuard: (() => string) | null = null;
// set while a routingFailFast job's profile runs: '' or the FAIL reason (stops the child at once, like noiseGuard)
let routeGuard: (() => string) | null = null;
// set while an earlyGates job's profile runs: '' or the FAIL reason. The guard is sync, so each call starts one async /native
// (+ agent patches when a gate reads fixes:) read and returns the verdict of the previous one (one GUARD_MS behind)
let gateGuard: (() => string) | null = null;
function earlyGateGuard(gs: EarlyGate[]): () => string {
  const t0 = Date.now(), needFixes = gs.some(g => g.counter.includes('fixes:'));
  let verdict = '', busy = false;
  return () => {
    if (!busy && !verdict) {
      busy = true;
      (async () => {
        const sec = (Date.now() - t0) / 1000, n = await get('native'), f = needFixes ? (await agent('patches'))?.result ?? null : null;
        verdict = gateFail(gs, sec, n, f);
      })().catch(() => { /* next round */ }).finally(() => { busy = false; });
    }
    return verdict;
  };
}
// child process with output to the console and the job log; killed (whole tree) at the deadline
function run(cmd: string, args: string[], logFile: string, deadline: number, onLine?: (l: string) => void): Promise<{ code: number | null; signal: string | null; out: string; timedOut: boolean }> {
  return new Promise(res => {
    const p = spawn(cmd, args, { cwd: ROOT }), f = createWriteStream(logFile, { flags: 'a' });
    let out = '', timedOut = false, part = '';
    f.write(`\n${new Date().toISOString()} $ ${cmd} ${args.join(' ')}\n`);
    const on = (d: Buffer) => {
      const s = d.toString(); out += s; process.stdout.write(s); f.write(s);
      if (onLine) { const ls = (part + s).split(/\r?\n/); part = ls.pop()!; ls.forEach(onLine); }
    };
    p.stdout.on('data', on); p.stderr.on('data', on);
    const t = setTimeout(() => { timedOut = true; on(Buffer.from(`${dumpHungGame('job deadline')}\n`)); killTree(p.pid); }, Math.max(1000, deadline - Date.now()));
    // fail fast (Rodney 2026-10-16): once the game is launched, every 30 s the game and the probe server must still be up;
    // two misses in a row end the child instead of waiting for the job deadline (default 60 min)
    let misses = 0;
    const g = jobGuard && setInterval(() => {
      const nz = noiseGuard?.() ?? '';
      if (nz) { noiseGuard = null; stall ||= nz; on(Buffer.from(`FAIL: ${nz}; child stopped\n`)); killTree(p.pid); return; }
      let rf = ''; try { rf = routeGuard?.() ?? ''; } catch (e) { on(Buffer.from(`routing fail-fast check failed (ignored): ${e}
`)); }
      if (rf) { routeGuard = null; stall ||= rf; on(Buffer.from(`FAIL: ${rf}; child stopped
`)); killTree(p.pid); return; }
      const eg = gateGuard?.() ?? '';
      if (eg) { gateGuard = null; stall ||= eg; on(Buffer.from(`FAIL: ${eg}; child stopped\n`)); killTree(p.pid); return; }
      const why = jobGuard!();
      misses = why ? misses + 1 : 0;
      if (misses >= 2) { stall ||= `${why} for ~${2 * GUARD_MS / 1000} s (fail-fast guard)`; on(Buffer.from(`FAIL: ${stall}; child stopped\n${dumpHungGame('fail-fast guard')}\n`)); killTree(p.pid); }
    }, GUARD_MS);
    p.on('error', e => on(Buffer.from(`spawn failed: ${e}\n`)));
    p.on('close', (code, signal) => { clearTimeout(t); if (g) clearInterval(g); f.end(); res({ code, signal, out, timedOut }); });
  });
}
const node = (args: string[], logFile: string, deadline: number, onLine?: (l: string) => void) => run(process.execPath, args, logFile, deadline, onLine);

async function closeGame(probePid?: number) {
  await get('detach', 10000); // detach first: killing a host mid-call can take the game down
  killTree(probePid);
  if (gameRunning()) try { execFileSync('taskkill', ['/F', '/IM', 'TS3W.exe'], { stdio: 'ignore' }); } catch { /* gone */ }
  for (let i = 0; i < 60 && gameRunning(); i++) await sleep(1000);
  if (gameRunning()) log('closeGame: TS3W.exe still running after 60 s');
}
const triage = (since: string) => {
  try { return execFileSync(process.execPath, [H('crash-ledger.ts'), 'triage', '--since', since]).toString().trim().split(/\n\s*\n/).at(-1) ?? ''; }
  catch (e) { return `crash-ledger triage failed: ${String(e).split('\n')[0]}`; }
};

// worldimage run (docs/123 §8.6): settle with the autopilot on, then per capture write <OutDir>\request and wait for the new
// wi-* folder to finish (header.txt gets its last record `loose`). ts3native looks for the request every 30 sim ticks; the
// helper stalls the game a few seconds and writes ~2 GB per image. Fail fast (docs/TESTING.md): every 30 s the game must be up
// and the capture must have moved (request taken, folder made, mem.bin or header.txt grown); two misses in a row end it.
async function guardedSleep(ms: number) {
  let misses = 0;
  for (const end = Date.now() + ms; Date.now() < end;) {
    await sleep(Math.min(GUARD_MS, end - Date.now()));
    const why = jobGuard?.() ?? '';
    misses = why ? misses + 1 : 0;
    if (misses >= 2) throw new Error(`${why} for ~${2 * GUARD_MS / 1000} s (fail-fast guard)`);
  }
}
async function worldImageRun(j: Job, deadline: number) {
  const w = j.run.worldimage!, dir = String(jobIni(j)!['WorldImage.OutDir']), req = join(dir, 'request');
  const size = (p: string) => (existsSync(p) ? statSync(p).size : -1);
  const wiDirs = () => (existsSync(dir) ? readdirSync(dir).filter(n => n.startsWith('wi-')) : []);
  const nlog = () => (existsSync(LOG) ? readFileSync(LOG, 'latin1') : '');
  const on = nlog().match(/^.*(OK|SKIP|FAIL) +world image.*$|^.*world image disabled.*$/m)?.[0];
  if (!on || !/OK +world image/.test(on)) return [{ ok: false, why: `[WorldImage] not active: ${on?.trim() ?? 'no world image line in ts3native.log (asi without the section?)'}` }];
  await agent('autopilot', { enabled: true, speed: w.speed ?? 'normal', dialogs: true }); // meta searches fire while Sims run
  await guardedSleep((w.settleS ?? 60) * 1000);
  const out: any[] = [];
  for (let i = 0; i < w.captures; i++) {
    if (i) await guardedSleep((w.gapS ?? 30) * 1000);
    const before = new Set(wiDirs());
    mkdirSync(dir, { recursive: true });
    writeFileSync(req, `${j.id} ${i + 1} ${new Date().toISOString()}\n`);
    let misses = 0, last = '', found = '', why = '', checked = Date.now();
    for (;;) {
      await sleep(5000);
      found ||= wiDirs().find(d => !before.has(d)) ?? '';
      const h = found ? join(dir, found) : '';
      const hdr = h && existsSync(join(h, 'header.txt')) ? wiHeader(readFileSync(join(h, 'header.txt'), 'utf8')) : null;
      if (hdr?.done) {
        const user = existsSync(join(h, 'user.txt')) ? readFileSync(join(h, 'user.txt'), 'utf8') : '';
        const g = [...user.matchAll(/^g31\.(\w+) (\S+)$/gm)].filter(m => m[1] !== 'currentticks');
        const line = nlog().split(/\r?\n/).find(l => l.includes(found)) ?? '';
        out.push({ ok: true, dir: found, stallMs: Number(line.match(/(\d+) ms stall/)?.[1] ?? NaN) || null, g31: `${g.filter(m => m[2] !== 'none').length}/${g.length}`, ...hdr });
        log(`${j.id}: capture ${i + 1}: ${found}: ${hdr.storedMB} MB, dump ${hdr.dumpMs} ms, loose ${hdr.loose}`);
        break;
      }
      if (Date.now() > deadline) why = 'job deadline during the capture';
      else if (Date.now() - checked >= GUARD_MS) {
        checked = Date.now();
        const key = [existsSync(req), found, size(join(h, 'mem.bin')), size(join(h, 'header.txt'))].join('|'), dead = jobGuard?.() ?? '';
        misses = dead || key === last ? misses + 1 : 0;
        last = key;
        if (misses >= 2) why = `${dead || (existsSync(req) ? 'request not taken (ts3native sees it only while the sim ticks)' : 'capture made no progress')} for ~${2 * GUARD_MS / 1000} s (fail-fast guard)`;
      }
      if (why) break;
    }
    if (why) { rmSync(req, { force: true }); out.push({ ok: false, dir: found || null, why: `capture ${i + 1}: ${why}` }); log(`${j.id}: capture ${i + 1}: ${why}`); break; }
  }
  return out;
}

const date = new Date().toISOString().slice(0, 10);
mkdirSync(OUT, { recursive: true });
mkdirSync(join(ROOT, 'docs', 'data', 'results'), { recursive: true });
const jsonl = join(OUT, `${queueName}-${date}.jsonl`), md = join(ROOT, 'docs', 'data', 'results', `${date}-queue-${queueName}.md`);
const earlier: any[] = existsSync(jsonl) ? readFileSync(jsonl, 'utf8').split('\n').filter(Boolean).map(l => JSON.parse(l)) : [];
const pht = (iso: string) => new Date(Date.parse(iso) + 8 * 3600e3).toISOString().slice(11, 16);
let build = 'unknown';
try { build = execFileSync('git', ['-C', ROOT, 'rev-parse', '--short', 'HEAD']).toString().trim(); } catch { /* not a repo */ }
if (!existsSync(md)) writeFileSync(md, `# Queue ${queueName} ${date}\n\nTool: \`tools/harness/queue.ts\` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: \`probe-out/queue/${queueName}-${date}.jsonl\`.\n`);
appendFileSync(md, `\n## ${new Date().toISOString().slice(0, 16)} UTC, build ${build}, jobs ${jobs.map(j => j.id).join(', ')}\n\n| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |\n|---|---|---|---|---|---|---|---|---|\n`);

// keep what Bin had, to restore the ini and fall back for the asi at the end
const BEFORE = join(OUT, 'bin-before');
// one queue at a time (2026-10-17: a queue whose parent shell was stopped kept running, and a second one fought it for the
// game: each restart killed the other's TS3W.exe)
if (!values['dry-run']) {
  const others = execFileSync('powershell', ['-NoProfile', '-NonInteractive', '-Command',
    `Get-CimInstance Win32_Process -Filter "Name='node.exe'" | Where-Object { $_.ProcessId -ne ${process.pid} -and $_.ProcessId -ne ${Number(process.env.TS3_SMOKE_PARENT) || 0} -and $_.CommandLine -match 'harness[\\/]queue\.ts' -and $_.CommandLine -notmatch '--dry-run|--self-check' } | ForEach-Object { $_.ProcessId }`]).toString().trim();
  if (others) { console.error(`another queue is running (node PID ${others.split(/\s+/).join(', ')}): stop it first (taskkill /PID <pid> /T /F)`); process.exit(3); }
}
if (!values['dry-run']) saveForeground(); // the window to give focus back to once the game takes it (focus-game.ts)
// apps a killed run left on the narrowed CPUs go back first; the same on any exit of this run (pin.ts)
if (pinStatePending()) log(`pin: leftover from an earlier run: ${restorePins()}`);
process.on('exit', () => { if (pinStatePending()) restorePins(); clearJob(); });
for (const sig of ['SIGINT', 'SIGTERM', 'SIGHUP'] as const) process.on(sig, () => process.exit(130));
mkdirSync(BEFORE, { recursive: true });
for (const f of ['ts3native.asi', 'ts3native.ini', 'Default.ini', 'Sims3Performance.ini', 'dxvk.conf']) if (existsSync(join(BIN, f))) copyFileSync(join(BIN, f), join(BEFORE, f));
if (existsSync(S3SS_TOML)) copyFileSync(S3SS_TOML, join(BEFORE, 'S3SS.toml'));
const repoIni = readFileSync(REPO_INI, 'utf8');
const memLine = (m: any) => (m ? `${m.commitMB}/${m.privateMB}/${m.largestFreeMB}` : '?');

let prevCrashed = !!earlier.at(-1)?.crashed, stopWhy = '', exitCode = 0;
const ledgerBlocks: string[] = [];  // mono ledger probe (docs/51, mono-ledger-report.ts): printed under the result table at the end
// the running game and what its start changed; kept across jobs only when reuseSession lets the next job take it over (docs/170 §2)
let stopPin = () => {}, probe: ReturnType<typeof spawn> | undefined, applied = { install: [] as string[], uninstall: [] as string[] }, launched = false, defaultIniOrig: string | null = null, s3ppIniOrig: string | null = null, s3ssTomlOrig: string | null = null, dxvkConfOrig: string | null = null, stashed = false, ccInstalled = false, lotsInstalled = false;
type Sess = { key: string; reuse: ReuseOpts | null; load: number; windows: number; logOff: number; base: WorldSnap | null; frames: string; carry?: string };
let sess: Sess | null = null, loads = 0;
const logSize = () => { try { return statSync(LOG).size; } catch { return 0; } };
// build verification (2026-10-24: the whole v0.1.5 release check ran an old ts3fixes.package; docs/TESTING.md "Build verification"):
// before every game start Bin/ts3native.asi must be the job's asi (and match the build DB entry and its version) and the installed
// ts3fixes.package tuning must be fixesBuild's (DB) or, by default, build/ts3fixes.package's; after start-up the first line of
// ts3native.log must name that version. Any miss throws: the job FAILs with the reason, nothing run.
type BuildRec = { asiSha: string; asiDb: string[]; version?: string; fixesSha?: string; fixesTuning?: string; fixesDb: string[]; fixesExpected: string; fixesExpectedTuning?: string; logVersion?: string; launchedAt: number };
let lastBuild: BuildRec | null = null;
function buildCheck(j: Job, binAsi: Buffer): { fails: string[]; rec: BuildRec } {
  const a = asiCheck(BUILDS, readFileSync(asiPath(j)), j.asi === 'tested' ? 'native/ts3native/out/ts3native.asi' : j.asi, binAsi);
  const f = fixesCheck(BUILDS, readOr(FIXES_INST), { name: j.fixesBuild, built: readOr(fixesSrc(j)) });
  return { fails: [...a.fails, ...f.fails], rec: { asiSha: a.sha, asiDb: a.names, version: a.version, fixesSha: f.sha, fixesTuning: f.digest, fixesDb: f.names,
    fixesExpected: j.fixesBuild ?? j.fixesPackage ?? 'build/ts3fixes.package', fixesExpectedTuning: f.expected, launchedAt: Date.now() } };
}
const buildLine = (b: BuildRec | null) => !b ? '' : `build: asi ${b.asiSha.slice(0, 12)} ${b.asiDb.join('/') || 'not in DB'} ts3native ${b.version ?? '?'}${b.logVersion ? ' (log ok)' : ''}; `
  + `ts3fixes tuning ${b.fixesTuning?.slice(0, 12) ?? '?'} ${b.fixesDb.join('/') || 'not in DB'} (expected ${b.fixesExpected})`;
// after start-up (agent answering): ts3native.log was rewritten by this start and names the expected version
function logVersionGate() {
  const b = lastBuild!;
  const why = !existsSync(LOG) ? 'Bin/ts3native.log missing after start-up (asi not loaded?)'
    : statSync(LOG).mtimeMs < b.launchedAt - 5000 ? 'Bin/ts3native.log not rewritten by this start (asi not loaded?)' : logVersionCheck(readFileSync(LOG, 'latin1'), b.version);
  if (why) throw new Error(`build check: ${why}; refused, nothing run`);
  b.logVersion = b.version;
  log(`build check ok: ${buildLine(b)}`);
}
const startProbe = (j: Job) => {
  const bc = buildCheck(j, readFileSync(join(BIN, 'ts3native.asi')));
  lastBuild = bc.rec;
  if (bc.fails.length) throw new Error(`build check: ${bc.fails.join('; ')}; refused before the game started`);
  const pf =createWriteStream(join(OUT, `${queueName}-${date}-${j.id}-probe.log`), { flags: 'a' });
  const pr = probe = spawn('python', [join(ROOT, 'tools', 'probe', 'ts3probe.py'), 'serve', '--launch', '--port', values.port!, ...(j.gameArgs?.length ? ['--', ...j.gameArgs] : [])], { cwd: ROOT });
  pr.stdout?.pipe(pf); pr.stderr?.pipe(pf); launched = true;
  jobGuard = () => pr.exitCode !== null || pr.signalCode !== null ? `probe server exited (${pr.exitCode ?? pr.signalCode})` : !gameRunning() ? 'TS3W.exe process gone'
    : existsSync(LOG) ? hookFailLine(readFileSync(LOG, 'latin1')) : ''; // a hook refused mid-run (lazy route worker hook) voids the run
};
// a fresh load of the job's game start (asi, ini, configs and fixes already in place): one Sess per load
const loaded = (j: Job): Sess => (sess = { key: keyOf(j), reuse: reuseOf(j), load: ++loads, windows: 0, logOff: logSize(), base: null,
  frames: join(ROOT, 'probe-out', 'profile', `${queueName}-${date}-${j.id}-load${loads}-${new Date().toISOString().slice(11, 19).replace(/:/g, '')}-frames.csv`) });
// windows that share a load (reuseSession or profile.repros) tell profile-world.ts so (first minute, lead-in, one frames session)
const sessionFlags = (j: Job) => sess && (sess.reuse || itemsOf(j)) ? ['--since-load', String(sess.windows), '--frames-shared', sess.frames] : [];
const ioAgent = { ag: agent, get: (p: string) => get(p) };
// between two windows of one load: switches back to the load values, world snapshot vs the one after the load (session-reuse.ts)
async function carryCheck(o: ReuseOpts, x: Extra): Promise<{ reasons: string[]; line: string }> {
  const rs = await restoreSwitches(ioAgent, sess!.base!, await readSwitches(ioAgent));
  const cur = await snapshot(ioAgent), reasons = drift(sess!.base!, cur, o, x);
  if (rs.failed.length) reasons.push(`switch restore failed: ${rs.failed.join(', ')}`);
  return { reasons, line: `${rs.restored.length ? `switches back: ${rs.restored.join(', ')}; ` : ''}${driftLine(sess!.base!, cur, x, reasons)}` };
}
// a reload inside a job (drift or a failed repro between profile.repros): close, restart, the job's asi and ini again, load
async function reloadGame(j: Job, logFile: string, deadline: number, why: string) {
  log(`${j.id}: reload (${why})`);
  await closeGame(probe?.pid); stopPin();
  const rr = await node([H('cycle.ts'), '--restart'], logFile, deadline);
  if (rr.code !== 0) throw new Error(`reload: cycle.ts --restart exit ${rr.code}`);
  copyFileSync(asiPath(j), join(BIN, 'ts3native.asi')); // --restart installs the source build
  writeFileSync(join(BIN, 'ts3native.ini'), mergeIni(repoIni, jobIni(j)));
  installFixesPackage(j);
  startProbe(j); stopPin = startPinner(pinConfig(j.pin));
  const ls = await node([H('cycle.ts'), '--save', saveFile(saveOf(j)!)], logFile, deadline);
  if (ls.code !== 0) throw new Error(`reload: cycle.ts --save exit ${ls.code}${ls.timedOut ? ' (timeout)' : ''}`);
  logVersionGate();
  loaded(j); sess!.base = await snapshot(ioAgent);
}
// close the running game (pins back first)
async function endGame(j: Job) {
  jobGuard = null;
  stopPin(); stopPin = () => {};
  const pin = pinConfig(j.pin);
  if (pin?.others.length || pinStatePending()) log(`${j.id}: pin: apps back: ${restorePins(pin)}`);
  await closeGame(probe?.pid);
}
// undo what the game start changed (game closed): configs, installer, content; errors for the row (stopWhy set as well)
async function restoreStart(id: string, logFile: string): Promise<string[]> {
  const errs: string[] = [];
  sess = null;
  if (defaultIniOrig !== null) try { writeFileSync(DEFAULT_INI, defaultIniOrig); } catch (e) { stopWhy ||= `could not put back Bin/Default.ini (${e}; copy in probe-out/queue/bin-before)`; }
  if (s3ppIniOrig !== null) try { writeFileSync(S3PP_INI, s3ppIniOrig); } catch (e) { stopWhy ||= `could not put back Bin/Sims3Performance.ini (${e}; copy in probe-out/queue/bin-before)`; }
  if (s3ssTomlOrig !== null) try { writeFileSync(S3SS_TOML, s3ssTomlOrig); } catch (e) { stopWhy ||= `could not put back S3SS/S3SS.toml (${e}; copy in probe-out/queue/bin-before)`; }
  if (dxvkConfOrig !== null) try { writeFileSync(DXVK_CONF, dxvkConfOrig); } catch (e) { stopWhy ||= `could not put back Bin/dxvk.conf (${e}; copy in probe-out/queue/bin-before)`; }
  defaultIniOrig = s3ppIniOrig = s3ssTomlOrig = dxvkConfOrig = null;
  // undo installer changes (game closed, so files are not locked); state unknown when this fails, so stop
  for (const [k, undo] of [['install', 'uninstall'], ['uninstall', 'install']] as const) {
    if (!applied[k].length) continue;
    const r = await node([join(ROOT, 'tools', 'setup', 'install-fixes.ts'), undo, '--only', applied[k].join(',')], logFile, Date.now() + 600000);
    if (r.code !== 0) { stopWhy ||= `could not undo install-fixes ${k} ${applied[k].join(',')} (exit ${r.code})`; errs.push(stopWhy); }
  }
  applied = { install: [], uninstall: [] };
  // content back (game closed above); the next cycle.ts --restart sees the Mods change and clears scriptCache
  if (lotsInstalled) { // generated library lots out before their CC goes
    lotsInstalled = false;
    let why = '';
    try {
      const rl = gameRunning() ? { removed: 0, problems: ['TS3W.exe still running'] } : removeLots();
      log(`${id}: ccLots: ${rl.removed} lots removed from ${LIBRARY}${rl.problems.length ? `; problems: ${rl.problems.slice(0, 3).join('; ')}` : ''}`);
      if (rl.problems.length) why = `ccLots removal incomplete, manifest kept: ${LOTS_MANIFEST}`;
    } catch (e) { why = `ccLots removal failed: ${String(e).split('\n')[0]}; run --restore-content`; }
    if (why) { stopWhy ||= why; errs.push(why); }
  }
  if (ccInstalled) { // synthetic CC back to its set dir before the content stash comes back
    ccInstalled = false;
    let why = '';
    try {
      const rs = gameRunning() ? { restored: 0, problems: ['TS3W.exe still running'] } : restoreContent(CC_DIR);
      log(`${id}: ccSet: ${rs.restored} packages moved back${rs.problems.length ? `; ${rs.problems.length} problems: ${rs.problems.slice(0, 3).join('; ')}` : ''}`);
      if (rs.problems.length) why = `ccSet restore incomplete (${rs.problems.length} problems), manifest kept: ${manifestOf(CC_DIR)}`;
      else if (!stashed && (await node([fixesTool, 'install', '--only', 'cache-clear'], logFile, Date.now() + 600000)).code !== 0) why = 'install-fixes cache-clear failed after the ccSet restore';
    } catch (e) { why = `ccSet restore failed: ${String(e).split('\n')[0]}; run --restore-content`; }
    if (why) { stopWhy ||= why; errs.push(why); }
  }
  if (stashed) {
    stashed = false;
    let why = '';
    try {
      const rs = gameRunning() ? { restored: 0, problems: ['TS3W.exe still running'] } : restoreContent(CONTENT_STASH);
      log(`${id}: content: ${rs.restored} packages moved back${rs.problems.length ? `; ${rs.problems.length} problems: ${rs.problems.slice(0, 3).join('; ')}` : ''}`);
      if (rs.problems.length) why = `content restore incomplete (${rs.problems.length} problems), manifest kept: ${manifestOf(CONTENT_STASH)}`;
      else if ((await node([fixesTool, 'install', '--only', 'cache-clear'], logFile, Date.now() + 600000)).code !== 0) why = 'install-fixes cache-clear failed after the content restore';
    } catch (e) { why = `content restore failed: ${String(e).split('\n')[0]}; run --restore-content`; }
    if (why) { stopWhy ||= why; errs.push(why); }
  }
  return errs;
}
const stuckOf = (tag: string) => { try { return JSON.parse(readFileSync(join(ROOT, 'probe-out', 'profile', `${tag}-stuck.json`), 'utf8')).length; } catch { return 0; } };
for (const w of pairs) log(w);
for (const [n, j] of jobs.entries()) {
  await waitHold(log); // crash-triage build running (hold.ts, AGENTS.md Rules): no next job until it is done
  markJob(j.id); // hold.ts wait-job-end waits for this to go
  const t0 = new Date().toISOString(), deadline = Date.now() + (j.timeoutMin ?? 60) * 60000;
  // 2026-10-25: a child that threw left the game up; closing it here made the job a CRASH with an old xcpt (sv-jobyield-verify)
  let closedByUs = false;
  const closeLost = () => { closedByUs = gameRunning(); return closeGame(probe?.pid); };
  const logFile = join(OUT, `${queueName}-${date}-${j.id}.log`), logOff = existsSync(logFile) ? statSync(logFile).size : 0;
  process.env.TS3_HANG_DIR = join(OUT, `${profileTag(j)}-hang`); // children capture a hung game here before the kill (game-alive.ts captureHang)
  const sections = [...new Set(Object.keys(j.ini ?? {}).map(k => k.split('.')[0]))];
  const s = saveOf(j)!;
  log(`== ${j.id} (${n + 1}/${jobs.length})${j.note ? `: ${j.note}` : ''}; log ${logFile}`);
  const row: any = { time: t0, queue: queueName, build, job: j.id, note: j.note ?? null, asi: j.asi, asiSha: sha(asiPath(j)), ini: rowIni(j), fixes: j.fixes ?? null,
    save: s, content: j.content ?? 'as-is', contentMoved: 0, run: j.run, gameArgs: j.gameArgs ?? [], defaultIni: j.defaultIni ?? null, s3ppIni: j.s3ppIni ?? null, s3ssToml: j.s3ssToml ?? null, dxvkConf: j.dxvkConf ?? null, profileTag: j.run.profile ? profileTag(j) : null, exitCode: null, verdict: [], pass: false, crashed: false, crash: null, timedOut: false, memory: {}, counters: {}, error: null };
  const pin = pinConfig(j.pin), machineFile = join(OUT, `${queueName}-${date}-${j.id}-${t0.slice(11, 16).replace(':', '')}-machine.jsonl`);
  if (pin) process.env.TS3_GAME_CPUS = pin.cpus; // child samplers (profile-world.ts) split by the same set
  row.pin = describePin(pin);
  if (j.rerunOf) row.rerunOf = j.rerunOf;
  const guarded = guardedSaves(s);
  row.saveHashes = { before: hashSaves(SAVES, guarded) };
  let stopLoad: (() => void) | null = null, iniUsed: string | undefined;
  // docs/170 §2: sess is still set only when the job before kept its game for this one (same key, reuse on, carry-over check ok)
  const reused = !!sess;
  row.session = reused ? { load: sess!.load, window: sess!.windows, line: `reused session (#${sess!.windows} since load)`, before: sess!.carry ?? null } : { line: 'fresh load' };
  if (reused) log(`${j.id}: ${row.session.line}; game start steps skipped`);
  else { stopPin = () => {}; probe = undefined; applied = { install: [], uninstall: [] }; launched = false; defaultIniOrig = s3ppIniOrig = s3ssTomlOrig = dxvkConfOrig = null; stashed = false; ccInstalled = false; lotsInstalled = false; lastBuild = null; }
  let jobLogOff = 0;
  try {
    if (!existsSync(asiPath(j))) throw new Error(`asi missing: ${j.asi} (refused before touching the game)`);
    if (j.ccSet && !ccSetReady(j.ccSet)) throw new Error(`ccSet missing or empty: ${[j.ccSet].flat().join(' + ')} (make it with tools/cc-gen.ts; refused before touching the game)`);
    if (j.ccLots && !(existsSync(join(ROOT, j.ccLots)) && lotFiles(join(ROOT, j.ccLots)).length)) throw new Error(`ccLots missing or empty: ${j.ccLots} (make it with tools/cc-lot-gen.ts; refused before touching the game)`);
    if (!reused) { // ---- game start and load (indent kept: the pre-docs/170 body)
    if (j.run.worldimage && !existsSync(String(jobIni(j)!['WorldImage.Helper']))) throw new Error(`worldimage helper missing: ${jobIni(j)!['WorldImage.Helper']} (build: native\\worldimage\\build.cmd; refused before touching the game)`);
    if (staleAsi(j)) console.log(`${stamp()} queue: WARN ${staleAsi(j)} (the tested build is pinned on purpose; rebuild out/ to move it)`);
    if (!values['dry-run']) await ensureSteam();
    if (CONTENT[row.content].length) {
      await closeGame(); // files must not be open when they move
      if (gameRunning()) throw new Error('content: TS3W.exe still running, nothing moved');
      const files = contentFiles(join(USER, 'Mods'), contentSets(row.content), fixFiles(FIXES_STATE));
      stashed = true; // from here on the manifest exists, even if a move below throws
      stashContent(join(USER, 'Mods'), CONTENT_STASH, files, { queue: queueName, job: j.id, content: row.content });
      row.contentMoved = files.length;
      log(`${j.id}: content ${row.content}: ${files.length} packages moved to ${CONTENT_STASH}`);
      const rc = await node([fixesTool, 'install', '--only', 'cache-clear'], logFile, deadline);
      if (rc.code !== 0) throw new Error(`install-fixes cache-clear exit ${rc.code}`);
    }
    if (j.ccSet) {
      await closeGame();
      if (gameRunning()) throw new Error('ccSet: TS3W.exe still running, nothing moved');
      const { dir, files } = ccPlan(j.ccSet);
      ccInstalled = true; // manifest from here on, even if a move below throws
      stashContent(dir, CC_DIR, files, { queue: queueName, job: j.id, ccSet: j.ccSet });
      row.ccSet = j.ccSet; row.ccMoved = files.length;
      log(`${j.id}: ccSet ${j.ccSet}: ${files.length} packages moved to ${CC_DIR}`);
      const rc = await node([fixesTool, 'install', '--only', 'cache-clear'], logFile, deadline);
      if (rc.code !== 0) throw new Error(`install-fixes cache-clear exit ${rc.code}`);
    }
    if (j.ccLots) {
      await closeGame();
      if (gameRunning()) throw new Error('ccLots: TS3W.exe still running, nothing copied');
      lotsInstalled = true; // manifest from here on
      const files = installLots(join(ROOT, j.ccLots));
      row.ccLots = j.ccLots; row.ccLotsCopied = files.length;
      log(`${j.id}: ccLots ${j.ccLots}: ${files.length} lots copied to ${LIBRARY}`);
    }
    const r0 = await node([H('cycle.ts'), '--restart'], logFile, deadline);
    if (r0.code !== 0) throw new Error(`cycle.ts --restart exit ${r0.code}`);
    installFixesPackage(j);
    copyFileSync(asiPath(j), join(BIN, 'ts3native.asi'));
    writeFileSync(join(BIN, 'ts3native.ini'), mergeIni(repoIni, jobIni(j)));
    // ts3native appends Bin/autshadow.jsonl across loads ([AutShadow] live-drop, dry-diff, dry-sim-mis): fresh per job
    try { writeFileSync(join(BIN, 'autshadow.jsonl'), ''); } catch (e) { log(`${j.id}: truncate autshadow.jsonl: ${e}`); }
    if (callSitesOffNote(j)) log(`${j.id}: ${callSitesOffNote(j)}`);
    if (j.defaultIni) { defaultIniOrig = readFileSync(DEFAULT_INI, 'utf8'); writeFileSync(DEFAULT_INI, mergeIni(defaultIniOrig, j.defaultIni)); }
    if (j.s3ppIni) {
      if (!existsSync(S3PP_INI)) throw new Error('s3ppIni: Bin/Sims3Performance.ini missing (s3pp not installed)');
      s3ppIniOrig = readFileSync(S3PP_INI, 'utf8'); writeFileSync(S3PP_INI, mergeIni(s3ppIniOrig, j.s3ppIni));
    }
    if (j.s3ssToml) {
      if (!existsSync(S3SS_TOML)) throw new Error('s3ssToml: S3SS/S3SS.toml missing (s3ss-config not installed)');
      s3ssTomlOrig = readFileSync(S3SS_TOML, 'utf8'); writeFileSync(S3SS_TOML, mergeIni(s3ssTomlOrig, tomlSet(j.s3ssToml)));
    }
    if (j.dxvkConf) {
      if (!existsSync(DXVK_CONF)) throw new Error('dxvkConf: Bin/dxvk.conf missing (dxvk-conf not installed)');
      dxvkConfOrig = readFileSync(DXVK_CONF, 'utf8'); writeFileSync(DXVK_CONF, mergeConf(dxvkConfOrig, j.dxvkConf));
    }
    for (const [k, ids] of [['install', j.fixes?.install], ['uninstall', j.fixes?.uninstall]] as const) {
      if (!ids?.length) continue;
      const r = await node([join(ROOT, 'tools', 'setup', 'install-fixes.ts'), k, '--only', ids.join(',')], logFile, deadline);
      if (r.code !== 0) throw new Error(`install-fixes ${k} exit ${r.code}`);
      applied[k] = [...ids];
    }
    startProbe(j);
    stopPin = startPinner(pin); // waits for TS3W.exe, then keeps it (and the apps) on their CPUs
    stopLoad = startMachineLoad(machineFile, 30, pin?.cpus ?? gameCpus());
    if (s === 'menu') {
      let up = false, misses = 0, nextCheck = Date.now() + GUARD_MS;
      const end = Math.min(deadline, Date.now() + 300000);
      while (!up && Date.now() < end) {
        up = (await agent('ping'))?.ok === true;
        if (!up && Date.now() >= nextCheck) { // same fail-fast rule as run(): two misses ~30 s apart
          nextCheck = Date.now() + GUARD_MS;
          const why = jobGuard();
          misses = why ? misses + 1 : 0;
          if (misses >= 2) { stall = `${why} for ~${2 * GUARD_MS / 1000} s (fail-fast guard)`; throw new Error(`waiting for the menu: ${stall}`); }
        }
        if (!up) await sleep(2000);
      }
      if (!up) throw new Error('agent not answering at the menu within 300 s');
    } else {
      const ld = storyCopy(j, s);
      if (ld !== s) { // fresh copy each job; the name starts with Story-, so the rm can never hit a Baseline save
        rmSync(join(SAVES, saveFile(ld)), { recursive: true, force: true });
        cpSync(join(SAVES, saveFile(s)), join(SAVES, saveFile(ld)), { recursive: true });
        log(`${j.id}: story scratch copy ${saveFile(s)} -> ${saveFile(ld)}`);
      }
      const r = await node([H('cycle.ts'), '--save', saveFile(ld)], logFile, deadline);
      if (r.code !== 0) throw new Error(`cycle.ts --save exit ${r.code}${r.timedOut ? ' (timeout)' : ''}`);
    }
    logVersionGate();
    loaded(j);
    } else stopLoad = startMachineLoad(machineFile, 30, pin?.cpus ?? gameCpus()); // ---- end of game start and load
    sess!.reuse = reuseOf(j); // this job's limits decide whether the next one may take the game over
    if ((sess!.reuse || itemsOf(j)) && !sess!.base) sess!.base = await snapshot(ioAgent); // switch values and world right after the load
    jobLogOff = logSize();
    if (j.run.menuMemory) await sleep((j.run.menuMemory.waitS ?? 60) * 1000);
    if (j.run.menuMemory?.draws) { // 0.0 means gRandomGen not cached yet (docs/27 §1.1)
      row.rngDraws = [];
      for (let i = 0; i < j.run.menuMemory.draws; i++) row.rngDraws.push((await agent('reflect_call', { path: 'Sims3.SimIFace.RandomGen.NextDouble' }))?.result ?? null);
    }
    const jobIniText = iniUsed = readFileSync(join(BIN, 'ts3native.ini'), 'utf8');
    // self check before any arm runs (fail fast): no ts3native hook FAIL line, route worker hook not refused. The hook installs at the
    // first worker plan, which comes only after profile-world's paused setup and profiler attach, so 'wait' is normal here: the run
    // guard (hookFailLine every GUARD_MS) and the post-run asyncIdle check catch a later refusal
    const why0 = hookFailLine(existsSync(LOG) ? readFileSync(LOG, 'latin1') : '') || routeHookState(jobIniText, await get('native'));
    if (why0 && why0 !== 'wait') throw new Error(why0);
    row.memory.before = await get('memory');
    const resetMax = !!j.ini?.['Verify.Enabled'];
    const c0 = await get(resetMax ? 'native?vfresetmax=1' : 'native'); // read first, then the [Verify] maxima start again
    const gate = counterGate(c0, j.minCounters);
    if (gate) throw new Error(`${gate}; refused, nothing run`);
    const fixRead = async () => ({ p: (await agent('patches'))?.result, t: (await agent('time'))?.result }); // ts3fixes rows of ACTIVITY
    const fx0 = j.run.profile || j.run.soak ? await fixRead() : null;
    const items = itemsOf(j), args = j.run.profile && !items ? profileArgs(j, j.run.profile, profileTag(j), sessionFlags(j)) : runArgs(j);
    let afterRun: { mem: any; native: any } | null = null;
    // early machine-load verdict (PLAN.md 2026-10-24): a timing run on a noisy machine stops ~2.5 min in instead of finishing void;
    // judged once, on the samples after the profiler's first minute. noiseCheck: false = correctness job, no timing read
    if (j.run.profile && j.noiseCheck !== false) {
      const from = Date.now();
      noiseGuard = () => { const v = earlyNoise(machineFile, from); if (v !== null) noiseGuard = null; return v ?? ''; };
    }
    if (j.routingFailFast) { routeGuard = routingGuard(j.routingFailFast, LOG, join(ROOT, 'probe-out', 'profile', profileTag(j))); log(`${j.id}: ${describeFailFast(j.routingFailFast)}`); }
    if (j.earlyGates) { gateGuard = earlyGateGuard(j.earlyGates); log(`${j.id}: ${describeGates(j.earlyGates)}`); }
    if (items) {
      // ---- profile.repros (docs/170 §1): one window per repro in this load, each its own tag, pass rules and log slice
      row.repros = [];
      const o = sess!.reuse ?? REUSE_DEFAULTS, stopOnFail = failMode(o) === 'stop';
      for (const [k, it] of items.entries()) {
        const prev = row.repros.at(-1);
        if (prev && !prev.reloadedAfter) {
          const c = await carryCheck(o, { windows: sess!.windows, stuck: prev.stuck, cleanupFailed: prev.cleanupFailed });
          prev.carry = c.line; log(`${j.id}: after ${prev.id}: ${c.line}`);
          if (c.reasons.length) await reloadGame(j, logFile, deadline, c.reasons.join('; '));
        }
        const tag = subTag(profileTag(j), it.id), p = { ...j.run.profile, ...it, repro: it.id }, sinceLoad = sess!.windows;
        const n0 = await get('native'), from = logSize();
        log(`${j.id}: repro ${k + 1}/${items.length} ${it.id} (tag ${tag}, ${sinceLoad ? `reused session (#${sinceLoad} since load)` : 'first window after load'})`);
        appendFileSync(logFile, `\n${new Date().toISOString()} repro ${it.id}: profile-world.ts output in ${tag}.log\n`);
        const r = await node(profileArgs(j, p, tag, sessionFlags(j)), join(OUT, `${tag}.log`), deadline); // <tag>.log: the queue log the hitch reports read
        sess!.windows++;
        const rr: any = { id: it.id, tag, load: sess!.load, sinceLoad, exitCode: r.code, timedOut: r.timedOut, stop: STOP_LINE.exec(r.out)?.[1] ?? null, stuck: stuckOf(tag), cleanupFailed: /cleanup \S+: ERROR/.test(r.out) };
        rr.verdict = [`${r.code === 0 ? 'PASS' : 'FAIL'}: ${it.id}: profile ${rr.stop ? `${rr.stop}, cap ${p.minutes} min` : `${p.minutes} min`}, ${sinceLoad ? `reused session (#${sinceLoad} since load)` : 'first window after load'}`, ...(r.code === 0 ? [] : silentFail(r.code, r.out, r.timedOut))];
        const fo = r.code === 0 ? fixesOnFail((p as any).fixesOn, r.out) : '';
        if (fo) { rr.verdict.push(`FAIL: ${it.id}: ${fo}`); rr.exitCode = 1; }
        if (it.nativeRequire) {
          const f = checkRequire(it.nativeRequire, flatDelta(n0, await get('native')));
          rr.verdict.push(f.length ? `FAIL: ${it.id}: events not fired: ${f.join('; ')}` : `PASS: ${it.id}: repro events fired`);
          if (f.length) rr.exitCode ||= 1;
        }
        if (existsSync(LOG)) try { writeFileSync(rr.nativeLog = join(OUT, `${tag}-${new Date().toISOString().slice(11, 16).replace(':', '')}-ts3native.log`), logCopy(readFileSync(LOG), sess!.logOff, from)); } catch (e) { log(`${j.id}: ${it.id}: ts3native.log slice: ${e}`); }
        row.repros.push(rr); row.verdict.push(...rr.verdict);
        const lost = r.timedOut || stall ? '' : await childLost('profile-world', r, closeLost, s => log(`${j.id}: ${s}`));
        if (lost) { row.error = lost; break; }
        if (rr.exitCode !== 0 || r.timedOut) {
          if (!gameRunning() || r.timedOut || k === items.length - 1) break; // crash or deadline: the job ends, the crash rules decide
          if (stopOnFail) { row.verdict.push(`STOP: ${it.id} failed: ${items.length - k - 1} repros not run (daytime rule; TS3_NIGHT=1 or reuseSession.onFail "restart" reloads and goes on)`); stopWhy ||= `repro ${it.id} of ${j.id} failed (daytime rule)`; break; }
          await reloadGame(j, logFile, deadline, `repro ${it.id} failed (night rule: fresh start, go on)`);
          rr.reloadedAfter = true;
        }
      }
      row.exitCode = row.repros.length === items.length && row.repros.every((x: any) => x.exitCode === 0) ? 0 : 1;
      row.timedOut = row.repros.some((x: any) => x.timedOut);
    } else if (j.run.age) {
      const clash = ageClash(j);
      if (clash.length) throw new Error(`age: would overwrite ${clash.join(', ')}; set run.age.save to a new name`);
      const csv = join(OUT, `${queueName}-${date}-${j.id}-age.csv`);
      if (!existsSync(csv)) writeFileSync(csv, CSV_HEADER + '\n');
      let logOff = existsSync(LOG) ? readFileSync(LOG).length : 0;
      row.ageSave = ageName(j); row.ageCsv = csv; row.checkpoints = [];
      const checkpoint = async (save: string, saved: boolean) => {
        if (saved) await sleep(3000); // let the flush worker and the folder settle
        let st: ReturnType<typeof saveStats> | null = null;
        try { st = saveStats(join(SAVES, saveFile(save))); } catch (e) { log(`${j.id}: save-stats ${save}: ${String(e).split('\n')[0]}`); }
        let sv: ReturnType<typeof lastSave> = null;
        if (saved && existsSync(LOG)) { const b = readFileSync(LOG); sv = lastSave(b.subarray(Math.min(logOff, b.length)).toString('latin1')); logOff = b.length; }
        const mem = await get('memory'), t = await agent('time');
        const day = Number(save.match(/-day(\d+)$/)?.[1] ?? t?.result?.day_number ?? NaN);
        const cp: AgeRow = { time: new Date().toISOString(), save, day: Number.isFinite(day) ? day : null, folderMB: st ? Math.round(st.total / 10485.76) / 100 : null,
          saveMs: sv?.totalMs ?? null, saveResult: sv?.result ?? null, commitMB: mem?.commitMB ?? null, privateMB: mem?.privateMB ?? null, largestFreeMB: mem?.largestFreeMB ?? null,
          types: st ? [...st.stats].sort((a, b) => b[1].bytes - a[1].bytes).map(([k, v]) => `${k}=${v.bytes}/${v.count}`).join(';') : '' };
        row.checkpoints.push(cp);
        appendFileSync(csv, csvLine(cp) + '\n');
        log(`${j.id}: checkpoint ${save} day ${cp.day ?? '?'}: ${cp.folderMB ?? '?'} MB, save ${cp.saveMs ?? '?'} ms, memory ${memLine(mem)}`);
      };
      await checkpoint(s.replace(/\.sims3$/i, ''), false); // the loaded save: the starting point of the growth fit
      let chain = Promise.resolve();
      const r = await node(args!, logFile, deadline, l => { const n = savedName(l); if (n) chain = chain.then(() => checkpoint(n, true)).catch(e => log(`${j.id}: checkpoint ${n}: ${e}`)); });
      await chain;
      const final = row.checkpoints.some((c: AgeRow) => c.save === row.ageSave);
      Object.assign(row, { exitCode: r.code, timedOut: r.timedOut });
      row.verdict = [`${r.code === 0 && final ? 'PASS' : 'FAIL'}: age ${j.run.age.days} days${final ? '' : ', no final save'}: ${summarize(row.checkpoints)}`];
    } else if (args) {
      const r = await node(args, logFile, deadline);
      if (j.run.profile) { sess!.windows++; row.stuck = stuckOf(profileTag(j)); row.cleanupFailed = /cleanup \S+: ERROR/.test(r.out); } // for the carry-over check
      Object.assign(row, { exitCode: r.code, verdict: silentFail(r.code, r.out, r.timedOut), timedOut: r.timedOut });
      const lost = r.timedOut || stall ? '' : await childLost(basename(args[0], '.ts'), r, closeLost, s => log(`${j.id}: ${s}`));
      if (lost) row.error = lost;
      // release mode: the matrix's reload case runs here (two restarts, as its printed commands), not by hand
      if (j.saveMatrix?.fixes === 'shipped' && !lost && !r.timedOut && !stall && /\breload: PENDING/.test(r.out)) {
        afterRun = { mem: await get('memory'), native: await get('native') }; // the matrix load's numbers, not the reload loads'
        if (existsSync(LOG)) copyFileSync(LOG, join(OUT, `${queueName}-${date}-${j.id}-${t0.slice(11, 16).replace(':', '')}-matrix-ts3native.log`));
        const pend = JSON.parse(readFileSync(join(ROOT, 'probe-out', 'save-matrix', 'reload-pending.json'), 'utf8'));
        for (const side of ['on', 'off'] as const) {
          const name = String(pend[side]?.save ?? '');
          if (!name) throw new Error(`reload: no ${side} save in reload-pending.json`);
          log(`${j.id}: reload ${side.toUpperCase()}: restart, load ${name}, save-matrix.ts --reload-check`);
          await closeGame(probe?.pid);
          const rr = await node([H('cycle.ts'), '--restart'], logFile, deadline);
          if (rr.code !== 0) throw new Error(`reload ${side}: cycle.ts --restart exit ${rr.code}`);
          copyFileSync(asiPath(j), join(BIN, 'ts3native.asi')); // --restart installs the source build; the job's asi and ini again
          writeFileSync(join(BIN, 'ts3native.ini'), mergeIni(repoIni, jobIni(j)));
          startProbe(j); // shortcut: the pinner watched the first TS3W.exe only; the reload loads run unpinned (no timing measured there)
          const ls = await node([H('cycle.ts'), '--save', saveFile(name)], logFile, deadline);
          if (ls.code !== 0) throw new Error(`reload ${side}: cycle.ts --save exit ${ls.code}${ls.timedOut ? ' (timeout)' : ''}`);
          logVersionGate();
          const rc = await node([H('save-matrix.ts'), '--reload-check', ...(side === 'off' ? ['--off'] : [])], logFile, deadline);
          row.verdict.push(...silentFail(rc.code, rc.out, rc.timedOut).map(v => `reload ${side}: ${v}`));
          if (rc.code !== 0 || rc.timedOut) { row.exitCode = rc.code || 1; row.timedOut ||= rc.timedOut; break; }
        }
      }
      if (j.run.profile) row.stop = STOP_LINE.exec(r.out)?.[1] ?? null; // loop repro: 'stopped at N events after M min (...)' or the cap line
      // a loop that met its stop rule is not a failure of its own: when the child named a FAIL (e.g. fixes counters), the profile
      // line says 'ran' so the row's first FAIL is the real reason (2026-11-01 sv-ur-car-loop: two FAILs, one cause)
      if (j.run.profile) row.verdict = [`${profileHead(r.code, r.timedOut, row.stop, r.out)}: profile ${row.stop ? `${row.stop}, cap ${j.run.profile.minutes} min` : `${j.run.profile.minutes} min`}, ${j.run.profile.seed !== undefined ? `seed ${j.run.profile.seed}` : 'unseeded'} (rng-compare.ts)`, ...(r.code === 0 ? [] : silentFail(r.code, r.out, r.timedOut))]; // keeps the child's FAIL reason, e.g. a failed repro step
      const fo = j.run.profile && r.code === 0 ? fixesOnFail((j.run.profile as any).fixesOn, r.out) : '';
      if (fo) { row.verdict.push(`FAIL: ${fo}`); row.selfCheckFail = fo; }
    } else if (j.run.soak) {
      await agent('autopilot', { enabled: true, speed: j.run.soak.speed ?? 'ultra', dialogs: true });
      const wd = await node([H('watchdog.ts'), '--every', '1', '--port', values.port!], logFile, Math.min(deadline, Date.now() + j.run.soak.minutes * 60000));
      const ranOut = wd.timedOut; // the watchdog only exits on its own with a diagnosis
      row.exitCode = ranOut ? 0 : 1;
      row.verdict = [ranOut ? `PASS: soak ${j.run.soak.minutes} min, watchdog quiet` : `FAIL: watchdog: ${wd.out.trim().split(/\r?\n/).at(-1) ?? `exit ${wd.code}`}`];
    } else if (j.run.routelock) {
      await agent('autopilot', { enabled: true, speed: j.run.routelock.speed ?? 'ultra', dialogs: true });
      const st = await get('routelock?start=1', 60000);
      if (!st?.hooked) throw new Error(`routelock start failed: ${JSON.stringify(st)?.slice(0, 300)}`); // byte check refused, or no probe
      const wd = await node([H('watchdog.ts'), '--every', '1', '--port', values.port!], logFile, Math.min(deadline, Date.now() + j.run.routelock.minutes * 60000));
      const rep = await get('routelock?stop=1', 60000); // detach the hooks before anything else
      writeFileSync(join(OUT, `${queueName}-${date}-${j.id}-routelock.json`), JSON.stringify(rep, null, 1));
      row.routelock = routelockSummary(rep);
      const g = rep?.g11;
      const lost = rep?.ring?.lost ?? 0; // events the probe's ring dropped: the g11 accounting is incomplete
      row.exitCode = wd.timedOut && g && !lost ? 0 : 1;
      row.verdict = [!wd.timedOut ? `FAIL: watchdog: ${wd.out.trim().split(/\r?\n/).at(-1) ?? `exit ${wd.code}`}`
        : !g ? `FAIL: /routelock report without g11: ${JSON.stringify(rep)?.slice(0, 200)}`
        : `${lost ? `FAIL: ring lost ${lost} of ${rep.ring.events + lost} events, numbers incomplete:` : 'PASS:'} routelock ${j.run.routelock.minutes} min: movable ${g.movableMsPerSec} ms/s of ${g.planMsPerSec} plan ms/s (${g.plans} plans), ` +
          `free ${g.freeShare}, unlocked gate entries ${g.unlockedGatePerSec}/s, hooks ${rep.acquireHooksPerSec}+${rep.gateHooksPerSec}/s; ${g11Gate(s, g.movableMsPerSec)}`];
    } else if (j.run.worldimage) {
      row.worldimage = await worldImageRun(j, deadline);
      const ok = row.worldimage.filter((c: any) => c.ok).length;
      row.exitCode = ok === j.run.worldimage.captures ? 0 : 1;
      row.verdict = [`${row.exitCode ? 'FAIL' : 'PASS'}: worldimage ${ok}/${j.run.worldimage.captures} captures` + row.worldimage.map((c: any) =>
        c.ok ? `; ${c.dir}: stall ${c.stallMs ?? '?'} ms (dump ${c.dumpMs} ms), ${c.storedMB} MB, loose ${c.loose}, G3-1 statics ${c.g31}, lock owners ${c.lockOwners.join(' ') || 'none'}` : `; ${c.why}`).join('')];
    } else {
      row.exitCode = row.memory.before ? 0 : 1;
      row.verdict = [row.memory.before ? `menu memory after ${j.run.menuMemory?.waitS ?? 60} s${row.rngDraws ? ` (draws ${row.rngDraws.map((d: unknown) => typeof d === 'number' ? d.toFixed(6) : '?').join(' ')})` : ''}` : 'FAIL: /memory did not answer'];
    }
    row.memory.after = afterRun?.mem ?? await get('memory');
    const c1 = afterRun?.native ?? await get('native');
    if (j.nativeRequire) row.nativeDelta = flatDelta(c0, c1);
    const idle = j.run.menuMemory ? '' : asyncIdle(jobIniText, c0, c1); // the hook can still fail after the check: plans prove it ran
    if (idle) { row.verdict.push(`FAIL: ${idle}`); row.selfCheckFail = idle; }
    // forced repro loops exercise one path: a default-on feature the job's own ini does not name may idle there (2026-10-25:
    // DeferFootprint voided the boat and Overwatch verify jobs), so it is a note; natural and bundle runs keep the void
    const pr = j.run.profile, focused = !!(pr?.repro || pr?.repros);
    for (const f of pr ? featureIdle(jobIniText, c0, c1) : []) {
      if (focused && !Object.keys(j.ini ?? {}).some(k => f.includes(k.split('.')[1]))) { row.verdict.push(`note: ${f.replace(', run void', ' (forced repro: not the job subject)')}`); continue; }
      row.verdict.push(`FAIL: ${f}`); row.selfCheckFail ||= f;
    }
    if (fx0) {
      const fx1 = await fixRead(), fa = fixActivity(fx0.p, fx1.p, fx0.t, fx1.t, jobFixesCfg(j.run.profile));
      row.fixActivity = { simStart: fx0.t?.clock, simMinutes: Number(fx1.t?.minutes_total) - Number(fx0.t?.minutes_total), counters: fa.counters, lists: fa.lists, notes: fa.notes };
      // 0->0 counters stay in the row only (a fixesReport prefix can match ~30 gates)
      const cs = Object.entries(fa.counters).filter(([, v]) => v.after || v.before).map(([k, v]) => `${k} ${v.before ?? '?'}->${v.after}`).join(', ');
      const ls = Object.entries(fa.lists).map(([k, v]) => `${k} [${v.slice(0, 12).join(' | ')}${v.length > 12 ? ` | +${v.length - 12}` : ''}]`).join('; ');
      if (cs || ls || fa.notes.length) row.verdict.push(`ts3fixes activity: ${[cs, ls, ...fa.notes].filter(Boolean).join('; ')}`);
      for (const f of fa.fails) { row.verdict.push(`FAIL: ${f}`); row.selfCheckFail ||= f; }
      for (const f of tunedFails(fx1.p?.applied)) { row.verdict.push(`FAIL: ${f}`); row.selfCheckFail ||= f; }
    }
    if (j.minCounters !== undefined) { row.nativeDump = join(OUT, `${queueName}-${date}-${j.id}-native.json`); writeFileSync(row.nativeDump, JSON.stringify({ before: c0, after: c1, maxReset: resetMax && Number(c0?.vfMaxReset) > 0 }, null, 1)); }
    if (c1?.autshadow?.ledger?.on & 1) {  // [AutShadow] Ledger=1: one line per docs/51 item (block is always exported; on bit 0 = probe ran)
      const lr = ledgerReport(c0, c1, j.run?.profile?.minutes ?? 10);
      row.ledger = lr.lines;
      row.verdict.push(`mono ledger ${lr.ok ? 'ok' : 'INCOMPLETE'}: per-item numbers below the table`);
      ledgerBlocks.push(`\n### ${j.id}: mono ledger probe (docs/51)\n\n${lr.lines.map(l => '    ' + l).join('\n')}\n`);
    }
    if (c1?.autshadow || /\baut/.test(j.run.profile?.fixesOn ?? '')) {
      row.fixesApplied = fixesApplied((await agent('patches'))?.result?.applied);
      log(`${j.id}: ts3fixes.applied: ${row.fixesApplied.join(' | ') || '(no AutShadow/AutLedger or refused-patch lines)'}`);
      // shadowCheck false: a vanilla arm that keeps the autshadow switch for its counters but never posts (golive-unseen g8-off)
      if (j.shadowCheck !== false) for (const f of shadowFails(rowIni(j), j.run.profile?.fixesOn, c0, c1, row.fixesApplied, row.fixActivity?.counters)) { row.verdict.push(`FAIL: ${f}`); row.selfCheckFail ||= f; }
    }
    row.counters ={ before: pickCounters(c0, sections), after: pickCounters(c1, sections) };
    row.counters.delta = delta(row.counters.before, row.counters.after);
  } catch (e) { row.error = String((e as Error).message ?? e); log(`${j.id}: ${row.error}`); }
  noiseGuard = null; routeGuard = null; gateGuard = null;
  if (stopLoad) { stopLoad(); const ml = loadSummary(machineFile); Object.assign(row, { machine: ml.line, noise: ml.noise, noiseNote: ml.short, machineLog: machineFile }); log(`${j.id}: ${ml.line}`); }
  if (stall && !row.error?.includes(stall)) row.error = [row.error, stall].filter(Boolean).join('; ');
  stall = '';
  row.crashed = launched && !closedByUs && !gameRunning() || row.verdict.some((v: string) => /game died/.test(v));
  if (row.crashed) { row.crash = triage(t0); log(`${j.id}: game crashed\n${row.crash}`); }
  try { row.hangCapture = hangCaptureLine(readFileSync(logFile).subarray(logOff).toString()); } catch { /* no job log */ }
  row.pass = row.exitCode === 0 && !row.crashed && !row.error && !row.timedOut && !row.selfCheckFail;
  // docs/170 §2: the next job may take this game over (reuse on for both, same game start); else it closes now, as always
  const next = jobs[n + 1];
  const mayKeep = !!sess?.reuse && !reuseBlock(j) && !row.crashed && !stopWhy && canTakeOver(sess.key, next);
  if (!mayKeep) await endGame(j);
  // the game rewrites Bin/ts3native.log at every start: keep this job's copy (travel save lines, hook OK lines); in a shared load
  // the start-up part plus this job's own lines (session-reuse.ts logCopy)
  if (launched && existsSync(LOG)) try { writeFileSync(row.nativeLog = join(OUT, `${queueName}-${date}-${j.id}-${t0.slice(11, 16).replace(':', '')}-ts3native.log`), sess && (sess.reuse || row.repros) ? logCopy(readFileSync(LOG), sess.logOff, jobLogOff) : readFileSync(LOG)); } catch (e) { log(`${j.id}: copy ts3native.log: ${e}`); }
  // docs/133 §7: call-site coverage lines of this run into the cumulative DB (a failed merge only costs coverage)
  // every game start of the row: profile.repros reloads keep their starts only in the per-repro slices (docs/133 §8)
  if (launched && Number(rowIni(j)['ProbeSnap.CallSites'])) for (const f of callsiteLogs(row)) try { log(`${j.id}: ${mergeCallsites(f, j.id)}`); } catch (e) { log(`${j.id}: callsites merge ${f}: ${e}`); }
  // repro event checks (docs/169): the job passes only when the repro fired what it is for (native counter deltas, log lines)
  if (j.nativeRequire || j.logRequire) {
    const fails = [...(j.nativeRequire ? checkRequire(j.nativeRequire, row.nativeDelta) : []),
      ...(j.logRequire ? logFails(row.nativeLog && existsSync(row.nativeLog) ? readFileSync(row.nativeLog, 'utf8') : '', j.logRequire) : [])];
    row.verdict.push(fails.length ? `FAIL: events not fired: ${fails.join('; ')}` : 'PASS: repro events fired');
    if (fails.length) row.pass = false;
  }
  if (j.logForbid) {  // correctness lines that must not appear (a replay audit MISMATCH, a clone-run fault)
    const bad = logForbidFails(row.nativeLog && existsSync(row.nativeLog) ? readFileSync(row.nativeLog, 'utf8') : '', j.logForbid);
    row.verdict.push(bad.length ? `FAIL: forbidden log lines: ${bad.join('; ')}` : 'PASS: no forbidden log lines');
    if (bad.length) row.pass = false;
  }
  if (j.scriptErrorsMax !== undefined) {
    const since = Date.parse(t0), news = readdirSync(USER).filter(f => /^ScriptError_.*\.xml$/i.test(f) && statSync(join(USER, f)).mtimeMs >= since);
    const unknown = news.filter(f => { try { return !matchKnown(readFileSync(join(USER, f), 'utf8')); } catch { return true; } });
    row.scriptErrors = unknown;
    const bad = unknown.length > j.scriptErrorsMax;
    row.verdict.push(`${bad ? 'FAIL' : 'PASS'}: ${unknown.length} new script errors (max ${j.scriptErrorsMax}${news.length > unknown.length ? `, ${news.length - unknown.length} known EA ignored` : ''})${unknown.length ? `: ${unknown.slice(0, 3).join(', ')}` : ''}`);
    if (bad) row.pass = false;
  }
  // story jobs (docs/171): stories ran, story.jsonl written, story-report joins hitches (census-only jobs run no stories)
  const st = j.run.profile?.story as any;
  if (launched && st && !st.censusOnly) {
    const tag = profileTag(j), sf = storyFile(tag), lines = existsSync(sf) ? parseStory(readFileSync(sf, 'utf8')) : [];
    let run: Run | null = null; try { run = loadRun(tag); } catch (e) { log(`${j.id}: story-report load ${tag}: ${String(e).split('\n')[0]}`); }
    // day planner jobs (docs/171 §12): also >= 2 sim days, 0 unscripted deaths, attendance, pursuits free days > work days
    const fails = [...smokeFails(run, lines), ...(st.days ? dayFails(lines) : [])];
    row.verdict.push(fails.length ? `FAIL: story: ${fails.join('; ')}` : `PASS: story: ${lines.filter(l => !l.kind).length} steps logged, join ok (story-report.ts ${tag})`);
    if (fails.length) row.pass = false;
  }
  // P4 shadow records (RouteSnap Mode=1, docs/94 §13): rewritten at every start too (row.shadowLog; shadow-compare-report.ts)
  const SHADOW = join(BIN, 'ts3native-shadow.jsonl');
  if (launched && existsSync(SHADOW)) try { copyFileSync(SHADOW, row.shadowLog = join(OUT, `${queueName}-${date}-${j.id}-${t0.slice(11, 16).replace(':', '')}-shadow.jsonl`)); } catch (e) { log(`${j.id}: copy ts3native-shadow.jsonl: ${e}`); } // shortcut: whole load's records in a shared load; slice like the log when a P4 job uses reuse
  // [AutShadow] records (go-live dry-diff / dry-sim-mis, docs/124 §17), truncated at job start (row.autshadowLog)
  const AUTSHADOW = join(BIN, 'autshadow.jsonl');
  if (launched && existsSync(AUTSHADOW) && statSync(AUTSHADOW).size > 0) try { copyFileSync(AUTSHADOW, row.autshadowLog = join(OUT, `${queueName}-${date}-${j.id}-${t0.slice(11, 16).replace(':', '')}-autshadow.jsonl`)); } catch (e) { log(`${j.id}: copy autshadow.jsonl: ${e}`); }
  let keep = mayKeep && row.pass;
  if (keep) {
    const c = await carryCheck(reuseOf(next)!, { windows: sess!.windows, stuck: row.repros?.at(-1)?.stuck ?? row.stuck ?? 0, cleanupFailed: !!(row.repros?.at(-1)?.cleanupFailed ?? row.cleanupFailed) });
    row.session.after = sess!.carry = c.line;
    log(`${j.id}: carry-over check for ${next.id}: ${c.line}`);
    if (c.reasons.length) { keep = false; row.session.next = `fresh start for ${next.id}`; }
    else row.session.next = `kept for ${next.id}`;
  }
  if (mayKeep && !keep) await endGame(j);
  if (!keep) { const errs = await restoreStart(j.id, logFile); if (errs.length) row.error = [row.error, ...errs].filter(Boolean).join('; '); }
  // docs/170: a crash or failed window of a session job ends the session; daytime that also stops the queue
  if (reuseOf(j) && !row.pass && failMode(reuseOf(j)!) === 'stop') stopWhy ||= `${j.id} ${row.crashed ? 'crashed' : 'failed'} in a reuseSession queue (daytime rule; TS3_NIGHT=1 or reuseSession.onFail "restart" goes on with a fresh start)`;
  // baseline save integrity: a changed Baseline-*/Story-*/ApexTest-Town save fails the job and stops the queue
  row.saveHashes.after = hashSaves(SAVES, guarded);
  const changedSaves = saveDrift(row.saveHashes.before, row.saveHashes.after);
  if (changedSaves.length) {
    row.verdict.push(`FAIL: baseline save changed: ${changedSaves.join('; ')}`); row.pass = false;
    stopWhy ||= `baseline save changed during ${j.id} (${changedSaves.join('; ')}): put it back from its .backup before any further run`;
  }
  row.buildCheck = lastBuild;
  // noisy timing job: once more at the end of this run as <id>-rerun (both rows kept; the note says which counts)
  const again = noisyRerun(j, row, [...all, ...jobs].map(x => x.id));
  if (again) { jobs.push(again); row.rerun = again.id; log(`${j.id}: noisy machine: ${again.id} queued at the end of this run`); }
  row.end = new Date().toISOString();
  row.minutes = Math.round((Date.parse(row.end) - Date.parse(t0)) / 6000) / 10;
  try { // timing verdict (docs/168 §10); never fails the job
    const tm = rowTiming(row, iniUsed);
    Object.assign(row, { timing: tm.timing, timingWhy: tm.why });
    for (const rr of row.repros ?? []) { const w = tm.windows?.[rr.id]; if (w) Object.assign(rr, { timing: w.timing, timingWhy: w.why }); }
  } catch (e) { row.timing = 'invalid'; row.timingWhy = `timing verdict failed: ${String(e).split('\n')[0]}`; }
  appendFileSync(jsonl, JSON.stringify(row) + '\n');
  clearJob();
  const cd = row.counters.delta ? Object.entries(row.counters.delta).map(([k, v]) => `${k} ${(v as number) > 0 ? '+' : ''}${v}`).join(', ') : '-';
  const cell = (x: string) => x.replace(/\|/g, '/').replace(/\r?\n/g, '<br>').slice(0, 400);
  appendFileSync(md, `| ${j.id} | ${t0.slice(11, 16)} (${pht(t0)}) | ${row.minutes} | ${row.pass ? 'PASS' : row.crashed ? '**CRASH**' : '**FAIL**'} | ${cell(row.verdict.join('<br>') || row.error || '-')} | ${row.exitCode ?? '-'} | ${memLine(row.memory.before)} -> ${memLine(row.memory.after)} | ${cell(cd || '-')} | ${cell([countsNote(row), `timing: ${timingText({ timing: row.timing, why: row.timingWhy })}`, buildLine(row.buildCheck), row.saveHashes.after && saveDrift(row.saveHashes.before, row.saveHashes.after).length ? 'BASELINE SAVE CHANGED' : '', row.session.line !== 'fresh load' || row.session.after ? [row.session.line, row.session.after, row.session.next].filter(Boolean).join('; ') : '', row.noiseNote, row.hangCapture, j.note, row.content !== 'as-is' ? `content ${row.content} (${row.contentMoved} moved)` : '', row.crashed ? row.crash.split('\n')[1]?.trim() : '', row.error && row.verdict.length ? row.error : ''].filter(Boolean).join('; ') || '')} |\n`);
  log(`${j.id}: ${row.pass ? 'PASS' : row.crashed ? 'CRASH' : 'FAIL'} in ${row.minutes} min`);
  if (!row.pass) exitCode = 1;

  const earlierCrashes = earlier.filter(r => r.job === j.id && r.crashed).length;
  stopWhy ||= crashStop(row.crashed, earlierCrashes, prevCrashed);
  // stopQueueOnFail / --stop-on-fail (Rodney 2026-10-31): a FAIL row ends the queue; a noisy timing job with its rerun queued does not
  stopWhy ||= failStop(j, row, !!values['stop-on-fail']);
  prevCrashed = row.crashed;
  earlier.push(row);
  if (stopWhy) { log(`STOP: ${stopWhy}${next ? `; resume: node tools/harness/queue.ts ${file} --from ${next.id}` : ''}`); exitCode = 1; break; }
  // AI budget (best effort): pause at >= 80% in either window, or at the percent in probe-out/queue/ai-pause-pct (a day's raised line)
  try {
    const pausePct = (() => { try { return Number(readFileSync(join(ROOT, 'probe-out', 'queue', 'ai-pause-pct'), 'utf8').trim()) || 80; } catch { return 80; } })();
    const first = execFileSync(process.execPath, [join(ROOT, 'tools', 'ai-usage.ts'), 'G', j.id], { cwd: ROOT }).toString().split(/\r?\n/)[0];
    const u = parseUsage(first);
    log(`ai usage: ${first}`);
    if (next && Math.max(u.h5 ?? 0, u.d7 ?? 0) >= pausePct) {
      log(`PAUSE: AI usage ${first}; resume: node tools/harness/queue.ts ${file} --from ${next.id}`);
      appendFileSync(md, `\nPaused after ${j.id} (AI usage ${first}); resume with \`--from ${next.id}\`.\n`);
      break;
    }
  } catch (e) { log(`ai-usage.ts failed (ignored): ${String(e).split('\n')[0]}`); }
}

// a game kept for a job that does not run now (STOP after the keep decision, AI pause, --only end): close it and undo its start
if (sess) { const last = jobs.find(x => keyOf(x) === sess!.key)!; await endGame(last); const errs = await restoreStart(last.id, join(OUT, `${queueName}-${date}-${last.id}.log`)); if (errs.length) { log(`session end: ${errs.join('; ')}`); exitCode = 1; } }
// put back the asi and ini found at the start as a pair (game is closed after every job)
{
  const asiOk = !existsSync(join(BEFORE, 'ts3native.asi')) || await copyRetry(() => copyFileSync(join(BEFORE, 'ts3native.asi'), join(BIN, 'ts3native.asi')), 'restore Bin/ts3native.asi', () => closeGame(), log);
  const iniOk = !existsSync(join(BEFORE, 'ts3native.ini')) || await copyRetry(() => copyFileSync(join(BEFORE, 'ts3native.ini'), join(BIN, 'ts3native.ini')), 'restore Bin/ts3native.ini', () => closeGame(), log);
  if (asiOk && iniOk) {
    const sha = existsSync(join(BIN, 'ts3native.asi')) ? createHash('sha256').update(readFileSync(join(BIN, 'ts3native.asi'))).digest('hex') : '';
    const b = loadDb().find(x => x.sha256 === sha);
    log(`restored: Bin/ts3native.asi + ini from the start (asi ${b ? b.name : `unknown build ${sha.slice(0, 12)}`})`);
    if (!b || !/^ts3native\.asi\.rel/.test(b.name)) log('WARN: Bin/ts3native.asi is not a release build: install the release asi + ini before playing or before another harness (Apex runner) uses Bin');
  }
  else { log(`restore failed (copies in ${BEFORE})`); exitCode = 1; }
}
for (const b of ledgerBlocks) appendFileSync(md, b);
if (stopWhy) appendFileSync(md, `\nStopped: ${stopWhy}.\n`);
log(`results: ${jsonl}, ${md}`);
process.exit(exitCode);
