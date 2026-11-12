// Fails a harness run when the game dies: TS3W.exe gone, or the probe agent's heartbeat (agentstatus) frozen or the probe
// unreachable (ECONNREFUSED) for more than 60 s (240 s while agentstatus says worldLoaded false: a world / travel load). Without it a crashed game read as "no stuck Sims" and a repro printed PASS
// (bp-route-async, 2026-10-13). Prints "FAIL: game died at <time> (<why>)" plus the last crash-ledger triage entry, exits 1.
//   const alive = watchGame();      start after the world is loaded (cycle.ts restarts the game on purpose)
//   await alive.check();            before printing a PASS verdict
//   node tools/harness/game-alive.ts --self-check
// A hang (heartbeat frozen, or a stall guard's "nothing moved" with the game and probe up) is captured before the FAIL line,
// so the game is still alive for it: captureHang below (docs/172 §6).
// Long blocking steps (docs/67 crash Q): while the last ts3agent mem mark in ts3native.log is an open -ph-import/-ph-place/
// -ph-bulldoze (no later mark), a frozen heartbeat is excused while the game progresses (ts3native.log size, probe /memory
// commit MB, TS3W.exe working set); 2 GUARD_MS windows without progress, or PHASE_GRACE_MS in all, fail (graceStep).
import { execFile, execFileSync } from 'node:child_process';
import { closeSync, existsSync, mkdirSync, openSync, readdirSync, readFileSync, readSync, statfsSync, statSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { analyzeDump } from '../dump-locks.ts';
import { BIN, GAME_EXE, gameRunning, seenPids } from './game-exe.ts';

const STARTED = new Date().toISOString(); // crash pickup: reports since this process started, of the game PIDs it saw

const LIMIT_MS = 60000;
// world load grace: the probe answers with agentstatus worldLoaded false (world unloaded, loading screen) while the frozen
// heartbeat is the load itself. Travel loads took 46-68 s and grew per trip; the France -> home load was killed at 68 s
// (float5-round4 all-off-1, 2026-11-09).
export const LOAD_LIMIT_MS = 240000;
const stamp = (t = Date.now()) => new Date(t).toISOString().slice(11, 19);

// why the game counts as dead, or '' while alive (pure, covered by --self-check). loading = the probe's last answer had
// worldLoaded false; ignored while the probe errors (a dead probe gets the plain 60 s).
export function deadReason(s: { running: boolean; now: number; lastBeatAt: number; error?: string; loading?: boolean }): string {
  if (!s.running) return `${GAME_EXE} process gone`;
  const load = !!s.loading && !s.error, limit = load ? LOAD_LIMIT_MS : LIMIT_MS;
  if (s.now - s.lastBeatAt > limit) return `agent not answering for ${Math.round((s.now - s.lastBeatAt) / 1000)} s${s.error ? ` (${s.error})` : load ? ' (heartbeat frozen during a world load)' : ' (heartbeat frozen)'}`;
  return '';
}


function lastTriage(): string {
  try {
    const out = execFileSync(process.execPath, [join(import.meta.dirname, 'crash-ledger.ts'), 'triage', '--since', STARTED, ...(seenPids.size ? ['--pids', [...seenPids].join(',')] : [])]).toString().trim();
    return out.split(/\n\s*\n/).at(-1) ?? out;
  } catch (e) { return `crash-ledger triage failed: ${String(e).split('\n')[0]}`; }
}

// the open long step: last mem mark tag in a ts3native.log tail when it is -ph-import/-ph-place/-ph-bulldoze, else null (pure)
export function openPhase(logTail: string): string | null {
  const last = [...logTail.matchAll(/ mark ev=([A-Za-z0-9_-]+)/g)].at(-1)?.[1];
  return last && /-ph-(import|place|bulldoze)$/.test(last) ? last : null;
}

// heartbeat-freeze grace (pure, covered by --self-check), one call per watchGame check while the heartbeat is frozen.
// excused: keep waiting; else why ('' = no open step, the plain heartbeat verdict stands). Progress = mark changed.
export const PHASE_GRACE_MS = 10 * 60000;
export type GraceState = { since: number | null; mark: string | null; movedAt: number };
export function graceStep(st: GraceState, s: { now: number; phase: string | null; mark: string }): { excused: boolean; why: string } {
  if (!s.phase) { st.since = null; return { excused: false, why: '' }; }
  if (st.since === null || s.mark !== st.mark) { st.movedAt = s.now; st.since ??= s.now; }
  st.mark = s.mark;
  if (s.now - st.since > PHASE_GRACE_MS) return { excused: false, why: `long step ${s.phase} still open after ${PHASE_GRACE_MS / 60000} min of heartbeat grace` };
  if (s.now - st.movedAt >= 2 * GUARD_MS) return { excused: false, why: `long step ${s.phase}: no progress (ts3native.log, commit MB, working set) for ${Math.round((s.now - st.movedAt) / 1000)} s` };
  return { excused: true, why: '' };
}

// unrequested state change (docs/236): live mode left, or the active household changed, with no state-changing ts3agent command
// (ts3probe.py STATE_CMD) sent in the last windowS seconds (/status state_cmd_ago_s). Pure, covered by --self-check.
// 7h/7j2: the autopilot accepted a long-distance move offer, the household left the world and the game crashed later.
export type ModeSnap = { inWorld: boolean; sub: string | null; hh: string | null };
export const modeSnap = (gs: any): ModeSnap | null => gs?.ok && gs.result ? { inWorld: !!gs.result.in_world, sub: gs.result.sub_state ?? null, hh: gs.result.active_household ?? null } : null;
export function modeExit(prev: ModeSnap | null, cur: ModeSnap | null, requestedAgoS: number | null | undefined, windowS = 180): string {
  if (!prev || !cur || (requestedAgoS != null && requestedAgoS <= windowS)) return '';
  if (prev.inWorld && prev.sub === 'LiveModeState' && (!cur.inWorld || !cur.sub || cur.sub === 'None'))
    return `unrequested mode exit: LiveModeState -> ${cur.inWorld ? cur.sub ?? 'None' : 'out of the world'}`;
  if (prev.inWorld && cur.inWorld && prev.hh && cur.hh !== prev.hh) return `unrequested mode exit: household ${prev.hh} -> ${cur.hh ?? 'none'}`;
  return '';
}
// ts3agent autopilot click log lines in ts3native.log (`ap click: <title> -> <button> (<how>)`), and its "no safe button" line
export const clickLines = (log: string, n = 10) => log.split(/\r?\n/).filter(l => l.includes('ap click: ')).slice(-n);
export const noSafeButton = (log: string) => log.split(/\r?\n/).filter(l => /ap click: .*no safe button/.test(l)).at(-1) ?? '';

export function logTail(bytes = 1 << 20, from = 0): string {
  try {
    const f = join(BIN, 'ts3native.log'), size = statSync(f).size, start = Math.max(from > size ? 0 : from, size - bytes), b = Buffer.alloc(size - start), fd = openSync(f, 'r');
    try { readSync(fd, b, 0, b.length, start); } finally { closeSync(fd); }
    return b.toString('latin1');
  } catch { return ''; }
}

// progress mark while the heartbeat is frozen: ts3native.log size, probe /memory commit MB, TS3W.exe working set (tasklist)
async function progressMark(): Promise<string> {
  let log = 0, ws = '';
  try { log = statSync(join(BIN, 'ts3native.log')).size; } catch { /* no plugin */ }
  const m = await fetch('http://127.0.0.1:47891/memory', { signal: AbortSignal.timeout(8000) }).then(r => r.json()).catch(() => null);
  try { ws = execFileSync('tasklist', ['/NH', '/FO', 'CSV', '/FI', `IMAGENAME eq ${GAME_EXE}`]).toString().split('","').at(-1)?.replace(/\D/g, '') ?? ''; } catch { /* no verdict */ }
  return [log, typeof m?.commitMB === 'number' ? Math.round(m.commitMB) : '', ws].join('|');
}

export function watchGame(onDeath?: () => void) {
  let lastBeat: unknown, lastBeatAt = Date.now(), error = '', dying: Promise<void> | null = null, busy = false, loading = false;
  const grace: GraceState = { since: null, mark: null, movedAt: 0 };
  const check = async (): Promise<void> => {
    if (dying) return dying; // never resolves (exit at its end): a caller's PASS cannot print during the hang capture
    try {
      const hb = await (await fetch('http://127.0.0.1:47891/agentstatus', { signal: AbortSignal.timeout(10000) })).json();
      if (hb?.heartbeat === undefined || hb.heartbeat !== lastBeat) { lastBeat = hb?.heartbeat; lastBeatAt = Date.now(); } // no heartbeat field: an answer counts
      error = '', loading = hb?.worldLoaded === false;
    } catch (e) { error = String((e as any)?.cause?.code ?? e); }
    const now = Date.now(), running = gameRunning();
    let why = deadReason({ running, now, lastBeatAt, error, loading });
    if (!why) { grace.since = null; return; }
    const hung = running && error !== 'ECONNREFUSED'; // process and probe up: a long step or a hang, not a crash
    if (hung) { // docs/67 crash Q: EA's Library lot import/place blocks the sim thread for minutes while still progressing
      const phase = openPhase(logTail()), first = grace.since === null, g = graceStep(grace, { now, phase, mark: await progressMark() });
      if (g.excused) { if (first) console.log(`${stamp()} heartbeat frozen in long step ${phase}: waiting while the game progresses (cap ${PHASE_GRACE_MS / 60000} min)`); return; }
      if (g.why) why = `${why}; ${g.why}`;
    }
    if (dying) return dying;
    dying = new Promise<void>(() => {});
    // capture before the kill, as the header promises (docs/172 §6)
    const cap = hung ? `\n${await captureHang(hangDir()).catch(e => `hang capture failed: ${e}`)}` : '';
    const dmp = running ? `\n${dumpHungGame(why)}` : ''; // probe gone (ECONNREFUSED) or hung: the game gets killed after this
    console.log(`${stamp()} FAIL: game died at ${stamp(running ? lastBeatAt : now)} (${why})${cap}${dmp}`);
    console.log(`last crash report (crash-ledger triage):\n${lastTriage()}`);
    onDeath?.();
    process.exit(1);
  };
  // one check at a time: grace sampling and the hang capture outlast the 10 s tick
  setInterval(() => { if (busy) return; busy = true; check().finally(() => { busy = false; }); }, 10000).unref();
  return { check };
}

// ---- Fail-fast guard for long waits (Rodney 2026-10-16; docs/TESTING.md "Fail-fast guards").
// Loads and saves legitimately take minutes with the agent silent, so wall time alone says nothing. Every GUARD_MS the
// guard samples the game; a sample without progress is a miss, and two misses in a row (~1 min) fail the wait.
// Progress = TS3W.exe running, the probe answering, and any of: agent heartbeat, world-loaded flag, committed memory
// (whole MB), ts3native.log size, or the caller's own mark (e.g. a save folder's mtime) changed since the last sample.
export const GUARD_MS = 30000;
export type GameSample = { running: boolean; answering: boolean; error?: string; mark: string };

// one guard step (pure, covered by --self-check): '' while progressing, else why this sample is a miss once misses >= 2
export function stallStep(st: { last: string | null; misses: number }, s: GameSample, liveOnly = false): string {
  const why = !s.running ? `${GAME_EXE} process gone` : liveOnly ? '' : !s.answering ? `probe not answering (${s.error ?? '?'})`
    : s.mark === st.last ? 'nothing moved (agent heartbeat, world flag, committed MB, ts3native.log)' : '';
  if (s.answering) st.last = s.mark;
  st.misses = why ? st.misses + 1 : 0;
  return st.misses >= 2 ? why : '';
}

// sim clock guard step (pure, covered by --self-check), one call per check (30 s apart): '' or why, once 2 checks in a row miss.
// The clock must move. A pause counts as a miss too (a modal dialog holds the clock), except where the game pauses by design:
// Build/Buy/CAS/Edit Town stop the clock (GateHandlers.cs; Edit Town may still report its old speed), or a repro step said "pausesClock": true. There the
// agent answering `time` is the progress check, so an agent that stops answering while excused still fails.
// The agent's enum name is "Pause" (GameSpeed.ToString); "Paused" was compared before 2026-10-26, so no pause was ever excused.
export const PAUSE_BY_DESIGN = /Build|Buy|CAS|EditTown/i;
export type ClockSample = { minutes_total: number; speed: string; day_number?: number; clock?: string } | null;
export function clockStep(st: { last: number | null; misses: number; excused?: string }, t: ClockSample, o: { subState?: string; declared?: boolean } = {}): string {
  if (!t) { // no reply: a busy frame or a save; the heartbeat guard (watchGame) judges a dead agent, except during an excused pause
    st.misses = st.excused ? st.misses + 1 : 0;
    return st.misses >= 2 ? `agent not answering while paused by design (${st.excused})` : '';
  }
  const paused = /^Pause/i.test(t.speed);
  // Edit Town stops the clock but keeps the speed it had (bp-cc-edittown 2026-10-26: "speed Triple"): by-design states count at any speed
  st.excused = PAUSE_BY_DESIGN.test(o.subState ?? '') ? o.subState : paused && o.declared ? 'repro step pausesClock' : undefined;
  const moved = t.minutes_total !== st.last;
  st.last = t.minutes_total;
  st.misses = moved || st.excused ? 0 : st.misses + 1;
  return st.misses < 2 ? '' : `sim clock stuck at day ${t.day_number ?? '?'} ${t.clock ?? t.minutes_total} for ~60 s (speed ${t.speed}${o.subState ? `, sub_state ${o.subState}` : paused ? ', sub_state ?' : ''})`;
}

export async function sampleGame(port = 47891, extra?: () => unknown): Promise<GameSample> {
  const running = gameRunning();
  const j = async (p: string, ms: number) => (await fetch(`http://127.0.0.1:${port}/${p}`, { signal: AbortSignal.timeout(ms) })).json();
  try {
    const a = await j('agentstatus', 10000), m = await j('memory', 20000).catch(() => null);
    let log = 0;
    try { log = statSync(join(BIN, 'ts3native.log')).size; } catch { /* no plugin */ }
    return { running, answering: true, mark: [a?.heartbeat, a?.worldLoaded, typeof m?.commitMB === 'number' ? Math.round(m.commitMB) : '', log, String(await extra?.() ?? '')].join('|') };
  } catch (e) { return { running, answering: false, error: String((e as any)?.cause?.code ?? e), mark: '' }; }
}

// Launcher-stage hang (docs/236 round 4, EA 1.69, three launches in a row): the game process up at ~100 MB / 17 threads, the agent never
// loaded, the DXVK log ending after "Found device". Pure step, one call per GUARD_MS sample while the agent has not answered yet: a miss =
// committed MB below LAUNCH_HANG_MB and within 2 MB of the last sample; two misses in a row = the verdict.
export const LAUNCH_HANG_MB = 400;
export function launchStep(st: { last: number | null; misses: number }, s: { running: boolean; commitMB: number | null }): string {
  const flat = s.running && s.commitMB != null && s.commitMB < LAUNCH_HANG_MB && st.last != null && Math.abs(s.commitMB - st.last) < 2;
  st.misses = flat ? st.misses + 1 : 0;
  st.last = s.commitMB;
  return st.misses >= 2 ? `hung at launcher stage: ${GAME_EXE} up at ${Math.round(s.commitMB!)} MB committed with no growth for ~${2 * GUARD_MS / 1000} s, agent never answered` : '';
}
export function launchGuard(port = 47891) {
  const st = { last: null as number | null, misses: 0 };
  let next = Date.now() + GUARD_MS, why = '';
  return async (): Promise<string> => {
    if (why || Date.now() < next) return why;
    next = Date.now() + GUARD_MS;
    const m = await fetch(`http://127.0.0.1:${port}/memory`, { signal: AbortSignal.timeout(20000) }).then(r => r.json()).catch(() => null);
    return why = launchStep(st, { running: gameRunning(), commitMB: typeof m?.commitMB === 'number' ? m.commitMB : null });
  };
}

// const guard = stallGuard('world load'); then in the wait loop: const why = await guard(); if (why) throw new Error(why);
// Cheap to call often: it samples at most every GUARD_MS. liveOnly: only "TS3W.exe gone" counts, first sample after
// GUARD_MS (game start: the probe server may still be starting and the exe handing over). Worst case to fail:
// 2 * GUARD_MS + one sample (<= 30 s of request timeouts) after the last progress.
// "[PkgLock] ABBA:" detector line in a ts3native.log tail (docs/539 §13.9.4) -> "pkg-abba: <line>", '' without one (pure)
export const PKG_ABBA = 'pkg-abba';
export function pkgAbba(tail: string): string {
  const m = tail.match(/\[PkgLock\] ABBA: [^\r\n]*/g);
  return m ? `${PKG_ABBA}: ${m.at(-1)}` : '';
}
export function stallGuard(what: string, o: { port?: number; extra?: () => unknown; liveOnly?: boolean } = {}) {
  const st = { last: null as string | null, misses: 0 };
  let next = o.liveOnly ? Date.now() + GUARD_MS : 0, why = '';
  return async (): Promise<string> => {
    if (why || Date.now() < next) return why;
    next = Date.now() + GUARD_MS;
    const r = stallStep(st, await sampleGame(o.port, o.extra), o.liveOnly);
    if (r) why = `${what}: ${r} for ${Math.round(2 * GUARD_MS / 1000)} s (fail-fast guard)`;
    if (r.startsWith('nothing moved')) why += `; ${await captureHang(hangDir(), { port: o.port }).catch(e => `hang capture failed: ${e}`)}`; // game and probe up: a hang
    if (r.startsWith('nothing moved')) { const pk = pkgAbba(logTail(1 << 18)); if (pk) why += `; ${pk}`; } // [PkgLock] Detect=1 named the EA lock cycle
    if (r && !r.startsWith(`${GAME_EXE} process gone`)) why += `; ${dumpHungGame(`${what}: ${r}`)}`; // hung or probe silent: the caller's failure kills the game
    return why;
  };
}

// ---- Hang capture (docs/172 §6). The game is hung but alive (heartbeat frozen, probe answering): before the kill, read
// (1) /simstack x5 at 1 s spacing, (2) /threadstack?which=all once (route worker, job threads: who holds what), (3) /native and
// the [HitchAttr] Holders ring ts3native_lockholders when the export exists, (4) /managedstack (the probe reads the interpreter
// frames from memory with the sim thread suspended; it needs no sim tick), (5) a procdump minidump (TS3_HANG_DUMP=off|mini|mp|ma,
// default mini = stacks + modules, a few MB; TS3_PROCDUMP = path; skipped when procdump is not found). Every call has its own
// timeout inside a ~30 s total cap, so a dead probe cannot hold up the kill. Files go to TS3_HANG_DIR (queue.ts: probe-out/queue/
// <tag>-hang, git-ignored) as hang-HHMMSS-*.json; returns the one-line summary "hang capture: N sim stacks, top frame X; ...".
export const HANG_CAP_MS = 30000;
export const hangDir = () => process.env.TS3_HANG_DIR ?? join(import.meta.dirname, '..', '..', 'probe-out', 'hang');
const HO_ROLE = ['sim', 'main', 'route worker', 'replay consumer', 'other'];
const hx = (v: number) => `0x${(v >>> 0).toString(16).toUpperCase()}`;

// ts3native_lockholders (hitchattr.cpp HoRing): version, enabled, minUs, head, then 64 x 32-byte HoRec; record i (1-based) at
// r[(i - 1) & 63], valid when its seq == i. Pure, covered by --self-check.
export function decodeHolders(hex: string) {
  const b = Buffer.from(hex, 'hex'), i32 = (o: number) => b.readInt32LE(o), u32 = (o: number) => b.readUInt32LE(o);
  if (b.length < 0x10 + 64 * 32) return { error: `ring read ${b.length} bytes, need ${0x10 + 64 * 32}` };
  const head = i32(0xC), records = [];
  for (let i = Math.max(1, head - 63); i <= head; i++) {
    const o = 0x10 + ((i - 1) & 63) * 32;
    if (i32(o) !== i) continue; // being written or overwritten
    records.push({ seq: i, waiter: i32(o + 4) ? 'main' : 'sim', lock: hx(u32(o + 8)), us: i32(o + 12), holderTid: u32(o + 16),
      holderRole: HO_ROLE[i32(o + 20)] ?? String(i32(o + 20)), holderSite: hx(u32(o + 24)), waiterSite: hx(u32(o + 28)) });
  }
  return { version: i32(0), enabled: i32(4), minUs: i32(8), head, records };
}

type HangGet = (path: string, ms: number) => Promise<any>;
const sleepMs = (ms: number) => new Promise(r => setTimeout(r, ms));
const frameName = (f: string) => f.split(' ').slice(1).join(' ') || f; // "0x1234 TS3W.exe!Name+0x12" -> "TS3W.exe!Name+0x12"
// first frame outside the OS modules: a wait's top frame (ntdll.dll+0x7837c) says only "waiting"; the module that called it says
// who (2026-11-03 load hang: every summary read ntdll, while the sim thread waited in Sims3SettingsSetter.asi and the main thread
// in ApexRadiance.asi, found only by reading the thread file)
const OS_MOD = /^(ntdll|kernelbase|kernel32|win32u|user32|combase|msvcrt|ucrtbase|msvcp\d*|vcruntime\d*)\.dll/i;
export const ownFrame = (stack?: string[] | null) => stack?.map(frameName).find(f => !OS_MOD.test(f)) ?? '-';
// "86 x A, 8 x B": threads grouped by their first non-OS frame, the n largest groups
export function frameGroups(threads: { stack?: string[] | null }[], n = 4): string {
  const c = new Map<string, number>();
  for (const t of threads) if (t.stack?.length) { const f = ownFrame(t.stack); c.set(f, (c.get(f) ?? 0) + 1); }
  return [...c].sort((a, b) => b[1] - a[1]).slice(0, n).map(([f, k]) => `${k} x ${f}`).join(', ');
}

function procdump(file: string, ms: number): Promise<string> {
  const mode = process.env.TS3_HANG_DUMP ?? 'mini';
  if (mode === 'off') return Promise.resolve('dump off (TS3_HANG_DUMP=off)');
  const args = ['-accepteula', ...(mode === 'ma' ? ['-ma'] : mode === 'mp' ? ['-mp'] : []), GAME_EXE, file];
  return new Promise(res => execFile(process.env.TS3_PROCDUMP ?? 'procdump', args, { timeout: ms, windowsHide: true }, (e: any) => {
    let size = 0; try { size = statSync(file).size; } catch { /* none written */ }
    res(size ? `dump ${mode} ${Math.round(size / 1048576)} MB` : e?.code === 'ENOENT' ? 'dump skipped: procdump not found (Sysinternals; TS3_PROCDUMP=path)'
      : `dump failed: ${e?.killed ? `timeout ${Math.round(ms / 1000)} s` : String(e?.message ?? e).split('\n')[0]}`);
  }));
}

export async function captureHang(dir: string, o: { port?: number; get?: HangGet; capMs?: number; spacingMs?: number; dump?: (file: string, ms: number) => Promise<string> } = {}): Promise<string> {
  const end = Date.now() + (o.capMs ?? HANG_CAP_MS), left = () => end - Date.now();
  const raw: HangGet = o.get ?? (async (p, ms) => (await fetch(`http://127.0.0.1:${o.port ?? 47891}/${p}`, { signal: AbortSignal.timeout(ms) })).json());
  // own timeout around every call (a fake or a stuck socket too), never past the cap
  const get = (p: string, ms: number) => {
    const t = Math.min(ms, left());
    return t <= 0 ? Promise.reject(new Error('capture cap reached')) : Promise.race([raw(p, t), sleepMs(t).then(() => { throw new Error(`timeout ${t} ms`); })]);
  };
  const err = (e: unknown) => ({ error: String((e as Error)?.message ?? e) }) as any;
  mkdirSync(dir, { recursive: true });
  const pre = join(dir, `hang-${new Date().toISOString().slice(11, 19).replace(/:/g, '')}`), put = (n: string, v: unknown) => writeFileSync(`${pre}-${n}.json`, JSON.stringify(v, null, 1));
  const sims: any[] = [];
  let fails = 0, sampleTimedOut = false;
  for (let i = 0; i < 5 && fails < 2 && left() > 0; i++) { // two failures in a row: the probe is dead, go on
    if (i) await sleepMs(Math.min(o.spacingMs ?? 1000, Math.max(0, left())));
    const r = await get('simstack?frames=32', 4000).catch(err);
    if (/timeout|cap reached/.test(r.error ?? '')) sampleTimedOut = true;
    fails = r.stack?.length ? 0 : fails + 1;
    sims.push({ time: new Date().toISOString(), ...r });
  }
  put('simstack', sims);
  const ok = sims.filter(s => s.stack?.length), top = ok[0] ? frameName(ok[0].stack[0]) : '-';
  const same = ok.filter(s => frameName(s.stack[0]) === top).length, under = ok[0] ? ownFrame(ok[0].stack) : '-';
  const th = await get('threadstack?which=all&frames=24', 8000).catch(err);
  put('threadstack', th);
  const nat = await get('native', 4000).catch(err);
  put('native', nat);
  let holders = 'holders: no export';
  const ex = await get('nativeexport?name=ts3native_lockholders', 3000).catch(err);
  if (ex?.address) {
    const rd = await get(`read?addr=${ex.address}&len=${0x10 + 64 * 32}`, 3000).catch(err);
    const ring: any = rd.hex ? decodeHolders(rd.hex) : rd;
    put('lockholders', ring);
    holders = ring.records ? `holders ${ring.enabled ? 'on' : 'off'}, ${ring.records.length} records` : `holders: ${ring.error}`;
  } else if (ex?.error) holders = `holders: ${ex.error}`;
  else if (ex) holders = 'holders: probe agent without the export address';
  // a timed-out /simstack may still be suspending the sim thread in the probe: a second sampler on it deadlocked the probe (2026-10-13)
  let managed = 'managed: skipped (a sim stack sample timed out)';
  if (!sampleTimedOut) {
    const m = await get('managedstack?frames=64', 4000).catch(err);
    put('managedstack', m);
    managed = m.methods?.length ? `managed top ${m.methods[0]}` : `managed: ${m.error ?? 'empty'}`;
  }
  const dumpFile = `${process.env.TS3_HANG_DUMP_DIR ? join(process.env.TS3_HANG_DUMP_DIR, pre.split(/[\\/]/).at(-1)!) : pre}.dmp`;
  const dump = left() > 2000 ? await (o.dump ?? procdump)(dumpFile, left()) : 'dump skipped: capture cap reached';
  const threads = th.threads?.filter((t: any) => t.stack?.length).length ?? 0;
  const main = th.threads?.find((t: any) => t.role === 'main' && t.stack?.length), groups = th.threads ? frameGroups(th.threads) : '';
  const line = `hang capture: ${ok.length} sim stacks, top frame ${top}${under !== top && under !== '-' ? ` under ${under}` : ''}${ok.length > 1 ? ` (${same}/${ok.length} same)` : ''}; ${threads} other thread stacks${th.error ? ` (${th.error})` : ''}${main ? `; main thread under ${ownFrame(main.stack)}` : ''}${groups ? `; thread frames ${groups}` : ''}; native ${nat?.error ?? `v${nat?.version ?? '?'}`}; ${holders}; ${managed}; ${dump}; ${pre}-*`;
  writeFileSync(`${pre}-summary.txt`, line + '\n');
  return line;
}

// ---- Full-memory dump before a kill, no probe needed (docs/201 §15, docs/67 §21: a load deadlocked on a heap/PEB lock left
// the probe silent and the hang without a stack). Every path that kills a still-running TS3W.exe for a stall, hang or timeout
// calls dumpHungGame first. Tool: procdump -ma (TS3_PROCDUMP or on PATH), else 32-bit Windows PowerShell calling dbghelp
// MiniDumpWriteDump (full memory + handles + threads; an x86 dump of the WOW64 game). Dumps go to TS3_DUMP_DIR (default
// probe-out/dumps, git-ignored: dumps hold personal data, never under docs or committed). One dump per game PID (a second
// caller for the same hang skips), none when the disk has < 10 GB free, TS3_KILL_DUMP=off turns it off. Synchronous on purpose:
// callers kill right after it returns. Returns a one-line note for the log.
export const DUMP_MIN_FREE = 10 * 2 ** 30;
export const dumpDir = () => process.env.TS3_DUMP_DIR ?? join(import.meta.dirname, '..', '..', 'probe-out', 'dumps');
const PS32 = 'C:\\Windows\\SysWOW64\\WindowsPowerShell\\v1.0\\powershell.exe';

// what to do (pure, covered by --self-check)
export function dumpPlan(o: { pid: number | null; freeBytes: number; existing: string[]; procdump: string | null; dir: string; off?: boolean; now?: Date }):
  { skip: string } | { tool: 'procdump' | 'powershell'; file: string } {
  if (o.off) return { skip: 'dump off (TS3_KILL_DUMP=off)' };
  if (!o.pid) return { skip: `no ${GAME_EXE} running` };
  if (o.existing.some(f => f.startsWith(`ts3w-${o.pid}-`) && f.endsWith('.dmp'))) return { skip: `already dumped (PID ${o.pid})` };
  if (o.freeBytes < DUMP_MIN_FREE) return { skip: `disk free ${Math.round(o.freeBytes / 2 ** 30)} GB < 10 GB` };
  const ts = (o.now ?? new Date()).toISOString().replace(/[-:]/g, '').slice(0, 15);
  return { tool: o.procdump ? 'procdump' : 'powershell', file: join(o.dir, `ts3w-${o.pid}-${ts}.dmp`) };
}

const findProcdump = (): string | null => {
  if (process.env.TS3_PROCDUMP) return existsSync(process.env.TS3_PROCDUMP) ? process.env.TS3_PROCDUMP : null;
  try { return execFileSync('where', ['procdump'], { timeout: 10000, stdio: ['ignore', 'pipe', 'ignore'] }).toString().split(/\r?\n/)[0].trim() || null; } catch { return null; }
};
const gamePid = (image: string): number | null => {
  try { return Number(/^"[^"]+","(\d+)"/m.exec(execFileSync('tasklist', ['/NH', '/FO', 'CSV', '/FI', `IMAGENAME eq ${image}`], { timeout: 15000 }).toString())?.[1]) || null; } catch { return null; }
};

export function dumpHungGame(reason: string, o: { pid?: number; image?: string; dir?: string; timeoutMs?: number } = {}): string {
  const dir = o.dir ?? dumpDir(), timeout = o.timeoutMs ?? 180000;
  try { mkdirSync(dir, { recursive: true }); } catch { /* statfs below says why */ }
  let freeBytes = 0, existing: string[] = [];
  try { const s = statfsSync(dir); freeBytes = s.bavail * s.bsize; existing = readdirSync(dir); } catch { /* no dir: plan skips on 0 free */ }
  const procdump = findProcdump();
  const plan = dumpPlan({ pid: o.pid ?? gamePid(o.image ?? GAME_EXE), freeBytes, existing, procdump, dir, off: process.env.TS3_KILL_DUMP === 'off' });
  const tag = `${stamp()} kill dump (${reason})`;
  if ('skip' in plan) { console.log(`${tag}: skipped, ${plan.skip}`); return `kill dump skipped: ${plan.skip}`; }
  const pid = Number(/ts3w-(\d+)-/.exec(plan.file)![1]), t0 = Date.now();
  let err = '';
  try {
    if (plan.tool === 'procdump') execFileSync(procdump!, ['-accepteula', '-ma', String(pid), plan.file], { timeout, stdio: 'ignore', windowsHide: true });
    else execFileSync(PS32, ['-NoProfile', '-NonInteractive', '-Command', `$ErrorActionPreference='Stop'
Add-Type -TypeDefinition 'using System;using System.Runtime.InteropServices;using Microsoft.Win32.SafeHandles;public static class Dh{[DllImport("dbghelp.dll",SetLastError=true)]public static extern bool MiniDumpWriteDump(IntPtr p,uint id,SafeFileHandle f,uint t,IntPtr e,IntPtr u,IntPtr c);}'
$p=Get-Process -Id ${pid}; $f=[IO.File]::Create('${plan.file.replace(/'/g, "''")}')
try { if(-not [Dh]::MiniDumpWriteDump($p.Handle,${pid},$f.SafeFileHandle,0x1826,[IntPtr]::Zero,[IntPtr]::Zero,[IntPtr]::Zero)){ throw "MiniDumpWriteDump error $([Runtime.InteropServices.Marshal]::GetLastWin32Error())" } } finally { $f.Close() }`],
      { timeout, stdio: ['ignore', 'ignore', 'pipe'], windowsHide: true }); // 0x1826 = FullMemory|HandleData|UnloadedModules|FullMemoryInfo|ThreadInfo
  } catch (e: any) { err = e?.killed || e?.signal ? `timeout ${Math.round(timeout / 1000)} s` : String(e?.stderr || e?.message || e).trim().split(/\r?\n/)[0]; }
  let size = 0; try { size = statSync(plan.file).size; } catch { /* none written */ }
  const line = size && !err ? `kill dump ${plan.tool} ${Math.round(size / 2 ** 20)} MB in ${Math.round((Date.now() - t0) / 1000)} s: ${plan.file}`
    : `kill dump failed (${plan.tool}: ${err || 'no file'})${size ? `, partial ${plan.file}` : ''}`;
  console.log(`${tag}: ${line}`);
  return size && !err ? `${line}; ${lockWaits(plan.file)}` : line;
}

// per waiting thread: the critical section and its owner, and the EA package-lock deadlock flag (tools/dump-locks.ts, docs/539 §13.8);
// one summary line back for the FAIL line (queue.ts retries the load once on the flag), the per-thread lines to the log
export function lockWaits(file: string, read: typeof analyzeDump = analyzeDump): string {
  try { const a = read(file); for (const l of a.lines) console.log(`${stamp()} ${l}`); return a.summary; } catch (e) { return `lock waits: not read (${String(e).split('\n')[0]})`; }
}

// clock-stuck trip that gets a hang capture (pure, covered by --self-check): clock not natively paused and no dialog open
// (rc1111d bookcarry 2026-11-11: native_paused false, pause_bits 0, one core busy, no stacks). No reply counts as running.
export const stuckRunning = (cs: { native_paused?: boolean } | null, pd: { open?: boolean } | null) => cs?.native_paused !== true && pd?.open !== true;

if (process.argv[1]?.endsWith('game-alive.ts') && process.argv.includes('--self-check')) {
  const assert = (await import('node:assert')).strict;
  assert.equal(stuckRunning({ native_paused: false }, { open: false }), true); // 2026-11-11 signature: capture
  assert.equal(stuckRunning({ native_paused: true }, { open: false }), false); // paused: old behaviour
  assert.equal(stuckRunning({ native_paused: false }, { open: true }), false); // dialog holds the clock
  assert.equal(stuckRunning(null, null), true); // no reply: capture
  const st = { last: null as string | null, misses: 0 }, smp = (mark: string, running = true, answering = true) => ({ running, answering, mark });
  assert.equal(stallStep(st, smp('1|true|900|10|')), ''); // first sample is progress
  assert.equal(stallStep(st, smp('1|true|900|10|')), ''); // one miss
  assert.match(stallStep(st, smp('1|true|900|10|')), /nothing moved/); // two in a row
  assert.equal(stallStep(st, smp('2|true|900|10|')), ''); assert.equal(st.misses, 0); // progress resets
  assert.equal(stallStep(st, smp('', true, false)), ''); assert.match(stallStep(st, smp('', true, false)), /probe not answering/);
  assert.equal(stallStep(st, smp('3|true|900|10|')), ''); assert.equal(st.misses, 0); // probe back and moving
  const lo = { last: null as string | null, misses: 0 };
  assert.equal(stallStep(lo, smp('a'), true), ''); assert.equal(stallStep(lo, smp('a'), true), ''); assert.equal(stallStep(lo, smp('a'), true), ''); // liveOnly ignores the mark
  assert.equal(stallStep(lo, smp('a', false), true), ''); assert.match(stallStep(lo, smp('a', false), true), /process gone/);
  // sim clock guard: Build mode pause is fine while the agent answers (bp-memsus-catalogue 2026-10-26), live-mode pause is not
  const ck = { last: null as number | null, misses: 0 } as Parameters<typeof clockStep>[0], tm = (m: number, speed = 'Normal') => ({ minutes_total: m, speed, day_number: 15, clock: '18:20' });
  assert.equal(clockStep(ck, tm(100)), ''); assert.equal(clockStep(ck, tm(101)), ''); // moving
  for (let i = 0; i < 5; i++) assert.equal(clockStep(ck, tm(101, 'Pause'), { subState: 'BuildModeState' }), '', 'build mode pause excused');
  assert.equal(clockStep(ck, null), ''); assert.match(clockStep(ck, null), /agent not answering while paused by design \(BuildModeState\)/);
  assert.equal(clockStep(ck, tm(101, 'Pause'), { subState: 'CASFullModeState' }), ''); assert.equal(ck.misses, 0);
  for (let i = 0; i < 3; i++) assert.equal(clockStep(ck, tm(101, 'Triple'), { subState: 'EditTownState' }), '', 'edit town excused at speed Triple');
  assert.equal(clockStep(ck, tm(101, 'Pause'), { subState: 'LiveModeState', declared: true }), '', 'declared by a repro step');
  assert.equal(clockStep(ck, tm(101, 'Pause'), { subState: 'LiveModeState' }), '');
  assert.match(clockStep(ck, tm(101, 'Pause'), { subState: 'LiveModeState' }), /stuck at day 15 18:20 .*speed Pause, sub_state LiveModeState/, 'live pause still fails');
  const ck2 = { last: null as number | null, misses: 0 };
  clockStep(ck2, tm(5)); assert.equal(clockStep(ck2, null), ''); assert.equal(clockStep(ck2, null), '', 'no reply outside a pause: heartbeat guard decides');
  assert.equal(clockStep(ck2, tm(5)), ''); assert.match(clockStep(ck2, tm(5)), /speed Normal\)$/, 'real stall at speed');
  // heartbeat grace during a long blocking step (docs/67 crash Q)
  const tail = (...ev: string[]) => ev.map((e, i) => `diag: mem #${i} mark ev=${e} priv=1\ndiag: memsus #${i} mark ev=${e} cc=1\n`).join('');
  assert.equal(openPhase(tail('etcc-full', 'etcc-ph-import')), 'etcc-ph-import');
  assert.equal(openPhase(tail('etcc-ph-place')), 'etcc-ph-place');
  assert.equal(openPhase(tail('etcc-ph-place', 'etcc-ph-placed')), null, 'placed closes the step');
  assert.equal(openPhase(tail('etcc-ph-bulldoze', 'etcc-empty')), null, 'any later mark closes it');
  assert.equal(openPhase(''), null);
  const gs: GraceState = { since: null, mark: null, movedAt: 0 };
  assert.deepEqual(graceStep(gs, { now: 0, phase: null, mark: 'a' }), { excused: false, why: '' }, 'no open step: heartbeat verdict stands');
  assert.equal(graceStep(gs, { now: 60000, phase: 'etcc-ph-place', mark: 'a' }).excused, true);
  assert.equal(graceStep(gs, { now: 90000, phase: 'etcc-ph-place', mark: 'a' }).excused, true, 'one window without progress');
  assert.equal(graceStep(gs, { now: 110000, phase: 'etcc-ph-place', mark: 'b' }).excused, true, 'progress resets');
  let t = 110000;
  for (let m = 0; t + 10000 <= 60000 + PHASE_GRACE_MS; m++) { t += 10000; assert.equal(graceStep(gs, { now: t, phase: 'etcc-ph-place', mark: String(m) }).excused, true); }
  assert.match(graceStep(gs, { now: t + 10000, phase: 'etcc-ph-place', mark: 'z' }).why, /still open after 10 min/, 'grace capped');
  const gs2: GraceState = { since: null, mark: null, movedAt: 0 };
  graceStep(gs2, { now: 0, phase: 'etcc-ph-import', mark: 'a' });
  assert.equal(graceStep(gs2, { now: 50000, phase: 'etcc-ph-import', mark: 'a' }).excused, true);
  assert.match(graceStep(gs2, { now: 60000, phase: 'etcc-ph-import', mark: 'a' }).why, /etcc-ph-import: no progress .* for 60 s/, 'two windows without progress fail');
  assert.equal(deadReason({ running: true, now: 70000, lastBeatAt: 20000 }), '');
  assert.equal(deadReason({ running: false, now: 1, lastBeatAt: 1 }), `${GAME_EXE} process gone`);
  assert.match(deadReason({ running: true, now: 90000, lastBeatAt: 20000 }), /not answering for 70 s \(heartbeat frozen\)/);
  assert.match(deadReason({ running: true, now: 90000, lastBeatAt: 0, error: 'ECONNREFUSED' }), /\(ECONNREFUSED\)/);
  // world load grace: 68 s frozen in a travel load (float5-round4 all-off-1) waits; past 240 s it fails; a probe error gets 60 s
  assert.equal(deadReason({ running: true, now: 88000, lastBeatAt: 20000, loading: true }), '');
  assert.equal(deadReason({ running: true, now: 240000, lastBeatAt: 0, loading: true }), '');
  assert.match(deadReason({ running: true, now: 250000, lastBeatAt: 0, loading: true }), /not answering for 250 s \(heartbeat frozen during a world load\)/);
  assert.match(deadReason({ running: true, now: 90000, lastBeatAt: 20000, loading: true, error: 'ECONNREFUSED' }), /70 s \(ECONNREFUSED\)/);
  assert.match(deadReason({ running: true, now: 90000, lastBeatAt: 20000, loading: false }), /\(heartbeat frozen\)$/);
  // kill dump plan: path, procdump vs PowerShell, one per PID, disk floor, off switch
  const dp = { pid: 4242, freeBytes: 50 * 2 ** 30, existing: [] as string[], procdump: null as string | null, dir: join('probe-out', 'dumps'), now: new Date('2026-10-26T03:04:05Z') };
  assert.deepEqual(dumpPlan(dp), { tool: 'powershell', file: join('probe-out', 'dumps', 'ts3w-4242-20261026T030405.dmp') });
  assert.equal((dumpPlan({ ...dp, procdump: 'C:\\t\\procdump.exe' }) as any).tool, 'procdump');
  assert.match((dumpPlan({ ...dp, existing: ['ts3w-4242-20261026T010000.dmp'] }) as any).skip, /already dumped \(PID 4242\)/);
  assert.equal((dumpPlan({ ...dp, existing: ['ts3w-42420-20261026T010000.dmp'] }) as any).tool, 'powershell', 'other PID with the same prefix');
  assert.match((dumpPlan({ ...dp, freeBytes: 9 * 2 ** 30 }) as any).skip, /disk free 9 GB < 10 GB/);
  assert.match((dumpPlan({ ...dp, pid: null }) as any).skip, new RegExp(`no ${GAME_EXE.replace(/\./g, '\\.')} running`));
  assert.match((dumpPlan({ ...dp, off: true }) as any).skip, /dump off/);
  assert.match(dumpHungGame('self-check', { image: 'no-such-image-ts3.exe', dir: join((await import('node:os')).tmpdir(), 'ts3-killdump-check') }), /skipped: no TS3W\.exe running/);
  // hang capture against fake probes (fixtures): healthy, dead (refused), hung (never answers)
  const { mkdtempSync, readdirSync, rmSync } = await import('node:fs'), { tmpdir } = await import('node:os');
  const ring = Buffer.alloc(0x10 + 64 * 32);
  ring.writeInt32LE(1, 0); ring.writeInt32LE(1, 4); ring.writeInt32LE(20000, 8); ring.writeInt32LE(2, 0xC);
  for (const [i, role] of [[1, 2], [2, 4]]) { const o = 0x10 + (i - 1) * 32; ring.writeInt32LE(i, o); ring.writeUInt32LE(0x11C0918, o + 8); ring.writeInt32LE(35000, o + 12); ring.writeUInt32LE(4242, o + 16); ring.writeInt32LE(role, o + 20); ring.writeUInt32LE(0x62EF7A, o + 28); }
  const hr: any = decodeHolders(ring.toString('hex'));
  assert.equal(hr.records.length, 2); assert.equal(hr.records[0].holderRole, 'route worker'); assert.equal(hr.records[1].waiterSite, '0x62EF7A');
  ring.writeInt32LE(0, 0x10 + 32); assert.equal((decodeHolders(ring.toString('hex')) as any).records.length, 1); // record 2 mid-write
  assert.match(String((decodeHolders('00') as any).error), /need/);
  const fixtures: Record<string, unknown> = {
    simstack: { thread: 7, stack: ['0x6EB41234 ts3native.asi+0x11234', '0x0062EF7A TS3W.exe!Gate0+0x4a'] },
    threadstack: { threads: [{ thread: 8, role: 'main', stack: ['0x2 ntdll.dll+0x41e02', '0x1 TS3W.exe!Main+0x1'] }, { thread: 9, stack: null, error: 'x' }] },
    native: { version: 99 }, nativeexport: { name: 'ts3native_lockholders', value: 1, address: '0x6EC00000' }, read: { hex: ring.toString('hex') },
    managedstack: { methods: ['Sims3.Gameplay.Autonomy.Run'] },
  };
  const fake = (p: string) => Promise.resolve(fixtures[p.split('?')[0]]);
  const tmp = mkdtempSync(join(tmpdir(), 'hang-'));
  try {
    const okLine = await captureHang(join(tmp, 'a'), { get: fake, spacingMs: 0, dump: async () => 'dump fake' });
    assert.match(okLine, /^hang capture: 5 sim stacks, top frame ts3native\.asi\+0x11234 \(5\/5 same\); 1 other thread stacks; main thread under TS3W\.exe!Main\+0x1; thread frames 1 x TS3W\.exe!Main\+0x1; native v99; holders on, 1 records; managed top Sims3\.Gameplay\.Autonomy\.Run; dump fake; /);
    // a wait's top frame names the module below it; groups count threads by that frame
    assert.equal(ownFrame(['0x1 ntdll.dll+0x7837c', '0x2 KERNELBASE.dll+0x160032', '0x3 Sims3SettingsSetter.asi+0x4c68c']), 'Sims3SettingsSetter.asi+0x4c68c');
    assert.equal(ownFrame(['0x1 ntdll.dll+0x7837c']), '-');
    assert.equal(frameGroups([{ stack: ['0x1 ntdll.dll+0x1', '0x2 TS3W.exe+0xdaefe'] }, { stack: ['0x3 TS3W.exe+0xdaefe'] }, { stack: ['0x4 d3d9.dll+0x5'] }, { stack: null }]), '2 x TS3W.exe+0xdaefe, 1 x d3d9.dll+0x5');
    assert.deepEqual(readdirSync(join(tmp, 'a')).map(f => f.replace(/^hang-\d+-/, '')).sort(), ['lockholders.json', 'managedstack.json', 'native.json', 'simstack.json', 'summary.txt', 'threadstack.json']);
    const dead = await captureHang(join(tmp, 'b'), { get: () => Promise.reject(new Error('ECONNREFUSED')), spacingMs: 0, dump: async () => 'dump fake' });
    assert.match(dead, /^hang capture: 0 sim stacks, top frame -; 0 other thread stacks \(ECONNREFUSED\); native ECONNREFUSED; holders: ECONNREFUSED; managed: ECONNREFUSED; dump fake/);
    assert.equal(JSON.parse(readFileSync(readdirSync(join(tmp, 'b')).map(f => join(tmp, 'b', f)).find(f => f.endsWith('simstack.json'))!, 'utf8')).length, 2); // stops after two failures
    const t0 = Date.now(), hung = await captureHang(join(tmp, 'c'), { get: () => new Promise(() => {}), capMs: 400, spacingMs: 0, dump: async () => 'dump fake' });
    assert.ok(Date.now() - t0 < 1500, 'cap holds against a probe that never answers');
    assert.match(hung, /0 sim stacks.*managed: skipped \(a sim stack sample timed out\); dump skipped: capture cap reached/);
  } finally { rmSync(tmp, { recursive: true, force: true }); }
  { // unrequested mode exit / household change, requested window, autopilot click log
    const live = { inWorld: true, sub: 'LiveModeState', hh: 'Goth' };
    assert.equal(modeExit(live, { ...live, sub: null }, null), 'unrequested mode exit: LiveModeState -> None');
    assert.equal(modeExit(live, { inWorld: false, sub: null, hh: null }, 900), 'unrequested mode exit: LiveModeState -> out of the world');
    assert.equal(modeExit(live, { ...live, hh: 'Landgraab' }, undefined), 'unrequested mode exit: household Goth -> Landgraab');
    assert.equal(modeExit(live, { ...live, sub: null }, 20), '', 'requested 20 s ago');
    assert.equal(modeExit(live, { ...live, sub: 'BuildModeState' }, null), '', 'build mode is not an exit');
    assert.equal(modeExit(null, { ...live, sub: null }, null), ''); assert.equal(modeExit(live, null, null), '', 'agent not answering: no verdict');
    assert.equal(modeExit({ ...live, sub: 'CASState' }, { ...live, sub: null }, null), '', 'only live mode exits count');
    assert.deepEqual(modeSnap({ ok: true, result: { in_world: true, sub_state: 'LiveModeState', active_household: 'Goth' } }), live);
    assert.equal(modeSnap({ ok: false, error: 'x' }), null);
    const lg = Array.from({ length: 12 }, (_, i) => `t ap click: d${i} -> #1 ok (only button)`).join('\r\n') + '\r\nother\r\nt ap click: (not clicked) -> no safe button, nothing clicked: \'Move?\' buttons: #2 \'move\' score 2\r\n';
    assert.equal(clickLines(lg).length, 10); assert.match(clickLines(lg).at(-1)!, /no safe button/);
    assert.match(noSafeButton(lg), /no safe button, nothing clicked: 'Move\?'/); assert.equal(noSafeButton('t ap click: a -> #1 cancel (safe default)'), '');
  }
  { // docs/236 #18: the cycle, save-matrix and repro waits check progress (stallGuard) unless a wait opts out with null
    const src = (f: string) => readFileSync(join(import.meta.dirname, f), 'utf8');
    for (const f of ['cycle.ts', 'save-matrix.ts']) assert.match(src(f), /async function until\([^\n]*guard: [^\n]*= stallGuard\(what/, `${f}: until() needs the stallGuard default`);
    assert.match(src('repro.ts'), /const guard = stallGuard\([^\n]*\n\s*while \(!ok\(last = await ag[^\n]*\n\s*const why = await guard\(\);/, 'repro.ts: the until loop needs a stallGuard');
    assert.deepEqual([...src('cycle.ts').matchAll(/await until\('([^']+)'[^\n]*, null\)/g)].map(m => m[1]), ['game closed'], 'cycle.ts: only the game-closed wait may opt out');
  }
  { // 2026-11-06 ip-jit-verify: slow menu under [MonoJit] Mode=2; the dialog wait must outlast it, and a load must survive an unanswered poll
    const src = readFileSync(join(import.meta.dirname, 'cycle.ts'), 'utf8');
    assert.ok(src.includes('!sawWarning ? Date.now() - t0 < 300000'), 'cycle.ts: menu wait below 300 s');
    assert.ok(src.includes("/no reply|still pending/.test"), 'cycle.ts: load_save must retry on a pending poll');
  }
  { // docs/236 round 4: launcher-stage hang = flat committed MB under LAUNCH_HANG_MB in two samples in a row after the first
    const st = { last: null as number | null, misses: 0 }, up = (mb: number | null) => launchStep(st, { running: true, commitMB: mb });
    assert.equal(up(80), ''); assert.equal(up(80.5), ''); assert.match(up(81), /^hung at launcher stage: .* 81 MB/);
    const g = { last: null as number | null, misses: 0 }, step = (mb: number | null, running = true) => launchStep(g, { running, commitMB: mb });
    assert.deepEqual([step(60), step(155), step(400), step(400), step(400)], ['', '', '', '', ''], 'growing or past the threshold is no hang');
    assert.deepEqual([step(90), step(90), step(null), step(90), step(90, false)], ['', '', '', '', ''], 'no sample or no process resets');
  }
  { // docs/236 round 3: a script that answers a dialog with accept:true first tells the in-game autopilot (dialog_expect), or it can cancel it first
    const src = (f: string) => readFileSync(join(import.meta.dirname, f), 'utf8');
    for (const f of ['cycle.ts', 'save-matrix.ts', 'travel-trip.ts', 'new-save.ts']) assert.match(src(f), /dialog_expect/, `${f}: accept:true needs dialog_expect`);
    assert.match(src('story-fuzzer.ts'), /dialog_expect/, 'story-fuzzer.ts: the step before an accept:true auto_dialog arms dialog_expect');
    // round 4: the in-game accept-on-match (dialog_expect accept:true) closes the window/poll race of H-move run 1
    for (const f of ['cycle.ts', 'save-matrix.ts', 'travel-trip.ts', 'new-save.ts', 'story-fuzzer.ts', 'vanilla-steps.ts']) assert.match(src(f), /'dialog_expect', \{[^}]*accept: true/, `${f}: dialog_expect without accept:true`);
  }
  { // lock waits line for the FAIL text (tools/dump-locks.ts): the summary comes back, a read error does not throw
    assert.equal(pkgAbba('x\r\n12:00 [PkgLock] ABBA: tid 4 holds O+0xC0, waits O+0x48 owned by tid 7 (O=0D0, in idle close for 3000 ms, Mode 0)\r\nnext'),
      'pkg-abba: [PkgLock] ABBA: tid 4 holds O+0xC0, waits O+0x48 owned by tid 7 (O=0D0, in idle close for 3000 ms, Mode 0)');
    assert.equal(pkgAbba('OK   pkglock: [PkgLock] Mode=1'), '');
    const fake = { waits: [], cycle: [1, 2], deadlock: 'EA package-lock deadlock (docs/539 §13.8)', lines: ['thread 1 waits ...'], summary: 'lock waits: 2 of 9 threads; deadlock cycle 1>2>1; EA package-lock deadlock (docs/539 §13.8)' };
    assert.equal(lockWaits('x.dmp', () => fake), fake.summary);
    assert.match(lockWaits('x.dmp', () => { throw new Error('not a minidump'); }), /^lock waits: not read \(Error: not a minidump\)/);
  }
  console.log('self-check ok');
}
