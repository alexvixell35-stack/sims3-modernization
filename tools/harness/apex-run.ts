// Runs Apex Radiance's in-game runner (tools/ingame_runner/run.ps1 in an Apex checkout) with our ts3native pair in a known
// state, load by load, and always puts the Bin pair back. Replaces the hand-written apex-*.ps1 wrappers of 2026-10-24.
//   node tools/harness/apex-run.ts --apex-repo <dir> --job <apex job.json> [--apex-asi <ApexRadiance.asi>] [--variant v] [--ab]
//        [--profiler None|Frames|Sim|Both] [--release | --ts3native <asi> --ini <ini> | --no-ts3native]
//        [--loads N] [--arms with,alone,...] [--stop-on-crash] [--ok-bisect] [--tag <t>] [--dry-run]
//   node tools/harness/apex-run.ts --restore        finish the Bin restore of an interrupted run (marker probe-out/apex/restore-pending.json)
//   node tools/harness/apex-run.ts --self-check
// Our pair: --release = the newest `ts3native.asi.rel*` entry of docs/data/builds.json whose native/ts3native/out-<rel>/
// ts3native.asi has that sha256 (with its ts3native.ini); --ts3native/--ini = that pair; neither = the pair Bin has at the start.
// Arms per load cycle through --arms (default `with`; `alone` = Bin/ts3native.asi removed for that load, Apex alone).
// More than one distinct arm is a bisect (feature on/off loads): refused without --ok-bisect (AGENTS.md crash rule:
// only when Rodney says "ok bisect").
// Start: refuses while TS3W.exe runs, a restore marker exists, or Apex's outputs/ingame/restore-pending.json exists. The Bin pair
// found at the start is copied to probe-out/apex/<tag>/bin-before/ and named via builds.json; the marker is written before Bin
// is touched. End (finally, SIGINT/SIGTERM/SIGHUP, exit): waits for TS3W.exe (killed after 3 min), copies the start pair back,
// checks the hashes, deletes the marker; a hard kill leaves the marker for --restore. WARN when the restored asi is not a release.
// Per load: runner output load-<n>-<arm>.log, Bin/ts3native.log copy load-<n>-<arm>-ts3native.log (when written in that load),
// a row in runs.jsonl. Crash = new EA xcpt*.txt in Documents or a non-zero game exit code in the runner log: the
// crash-ledger.ts triage block and the `DRAW BAD|HEAP|crash forensics: DUMP` lines of the ts3native.log go into summary.txt.
// Stops on Apex restore-pending (its backup not put back: run.ps1 -Restore), on --stop-on-crash, on 15 min without runner output.
// Lights: alienfx-cli setall=0,0,0 every 30 s while TS3W.exe runs (as queue.ts; TS3_LIGHTS=on skips). Windows-only by nature.
// status.ts shows these runs (run.json + runs.jsonl). Env: TS3_BIN, TS3_USER (Documents\Electronic Arts\The Sims 3), TS3_REPO.
import assert from 'node:assert/strict';
import { type ChildProcess, execFileSync, spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { appendFileSync, copyFileSync, createWriteStream, existsSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { homedir, tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { parseArgs } from 'node:util';
import { asiVersion, loadDb, type Build } from '../builds.ts';
import { dumpHungGame } from './game-alive.ts';
import { BIN, GAME_EXE } from './game-exe.ts';

const HERE = resolve(import.meta.dirname, '..', '..');
export const mainRoot = (r: string) => /^(.*?)[\\/]\.claude[\\/]worktrees[\\/][^\\/]+[\\/]?$/.exec(r)?.[1] ?? r;
const ROOT = process.env.TS3_REPO ?? mainRoot(HERE);
const USER = process.env.TS3_USER ?? join(homedir(), 'Documents', 'Electronic Arts', 'The Sims 3');
export const APEX_OUT = join(ROOT, 'probe-out', 'apex');
const MARKER = join(APEX_OUT, 'restore-pending.json');
const PAIR = ['ts3native.asi', 'ts3native.ini'] as const;
const STALL_MS = 15 * 60000, AFX = join(process.env.LOCALAPPDATA ?? '', 'AlienFX Tools', 'alienfx-cli.exe');

export type Marker = { tag: string; pid: number; at: string; bin: string; before: string; files: Record<string, string | null> };
export type Row = { tag: string; load: number; arm: string; start: string; end: string; minutes: number; exit: number | null; gameExit: number | null;
  crashed: boolean; xcpt: string | null; pending: boolean; nativeLog: string | null; forensics: number; note?: string };

const sha = (p: string) => existsSync(p) ? createHash('sha256').update(readFileSync(p)).digest('hex') : null;
const sleep = (ms: number) => Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms);
export const armsPlan = (arms: string[], loads: number) => Array.from({ length: loads }, (_, i) => arms[i % arms.length]);
export const isBisect = (arms: string[]) => new Set(arms).size > 1;
export const BISECT_WARN = 'WARNING: arms with more than one state are a bisect (feature on/off loads). AGENTS.md: bisects cost a lot of game time and rarely name a cause; run one only after Rodney says "ok bisect" (then pass --ok-bisect).';

// what Bin's pair is, by builds.json
export function pairName(dir: string, db: Build[]): { line: string; release: boolean } {
  const a = join(dir, 'ts3native.asi'), h = sha(a), ini = sha(join(dir, 'ts3native.ini'));
  if (!h) return { line: `no ts3native.asi; ini ${ini?.slice(0, 12) ?? 'none'}`, release: false };
  const names = db.filter(b => b.kind === 'asi' && b.sha256 === h).map(b => b.name);
  return { line: `asi ${h.slice(0, 12)} ${names.join('/') || 'not in builds.json'} ts3native ${asiVersion(readFileSync(a)) ?? '?'}; ini ${ini?.slice(0, 12) ?? 'none'}`,
    release: names.some(n => /^ts3native\.asi\.rel/.test(n)) };
}

// newest release asi whose native/ts3native/out-<rel>/ pair is on disk with the DB hash
export function releasePair(db: Build[], roots: string[]): { asi: string; ini: string; name: string } {
  const rels = db.filter(b => b.kind === 'asi' && /^ts3native\.asi\.rel/.test(b.name)).sort((x, y) => y.created.localeCompare(x.created));
  for (const b of rels) for (const r of roots) {
    const d = join(r, 'native', 'ts3native', `out-${b.name.slice('ts3native.asi.'.length)}`);
    if (sha(join(d, 'ts3native.asi')) === b.sha256 && existsSync(join(d, 'ts3native.ini'))) return { asi: join(d, 'ts3native.asi'), ini: join(d, 'ts3native.ini'), name: b.name };
  }
  throw new Error(`no release pair on disk: ${rels.map(b => `native/ts3native/out-${b.name.slice(14)}`).join(', ') || 'no ts3native.asi.rel* in builds.json'} (under ${roots.join(', ')})`);
}

// copies the start pair back (absent at the start = removed), checks the hashes, deletes the marker; false = marker kept
export function restoreBin(markerFile: string, log: (s: string) => void, db: Build[], wait = sleep): boolean {
  const m: Marker = JSON.parse(readFileSync(markerFile, 'utf8'));
  for (const f of PAIR) {
    const dst = join(m.bin, f);
    let err = 'hash still differs';
    for (let i = 0; sha(dst) !== m.files[f]; i++) {
      if (i > 10) { log(`restore Bin/${f} failed: ${err} (copies in ${m.before}; close TS3W.exe, then apex-run.ts --restore)`); return false; }
      try { if (m.files[f]) copyFileSync(join(m.before, f), dst); else rmSync(dst, { force: true }); }
      catch (e) { err = String(e).split('\n')[0]; wait(3000); }
    }
  }
  rmSync(markerFile, { force: true });
  const p = pairName(m.bin, db);
  log(`restored Bin pair from the start of ${m.tag}: ${p.line}`);
  if (!p.release) log('WARN: Bin/ts3native.asi is not a release build: install the release asi + ini before playing (apex-run.ts --release keeps it for the next run)');
  return true;
}

export function crashOf(runLog: string, newXcpt: string | null) {
  const m = /game closed[^\r\n]*\(exit code (-?\d+)\)/.exec(runLog), gameExit = m ? Number(m[1]) : null;
  return { gameExit, crashed: !!newXcpt || (gameExit !== null && gameExit !== 0) };
}
export const FORENSIC = /DRAW BAD|HEAP|crash forensics: DUMP/;
export const forensicLines = (text: string) => text.split(/\r?\n/).filter(l => FORENSIC.test(l));
// one report's block of `crash-ledger.ts triage` output (blocks start with the report file name)
export const triageBlock = (out: string, name: string) => out.split(/\r?\n(?=\r?\n)/).map(b => b.trim()).find(b => b.split(/\r?\n/)[0] === name) ?? null;
export function newestXcpt(dir: string, since: number): string | null {
  if (!existsSync(dir)) return null;
  return readdirSync(dir).filter(n => /^xcpt .*\.txt$/i.test(n)).map(n => [n, statSync(join(dir, n)).mtimeMs] as const)
    .filter(([, t]) => t >= since).sort((a, b) => b[1] - a[1])[0]?.[0] ?? null;
}
export function runnerArgs(o: { bin: string; job: string; apexAsi?: string; variant?: string; ab?: boolean; profiler?: string }) {
  return ['-GameBin', o.bin, '-Job', o.job, ...(o.apexAsi ? ['-Asi', o.apexAsi] : []), ...(o.variant ? ['-Variant', o.variant] : []), ...(o.ab ? ['-AB'] : []), ...(o.profiler ? ['-Profiler', o.profiler] : [])];
}

const gameRunning = () => {
  if (process.platform !== 'win32') return false;
  try { return new RegExp(`^${GAME_EXE.replace(/\./g, '\\.')}`, 'im').test(execFileSync('tasklist', ['/NH', '/FI', `IMAGENAME eq ${GAME_EXE}`], { timeout: 15000 }).toString()); } catch { return false; }
};
function waitGameGone(log: (s: string) => void, maxMs = 180000) {
  const t0 = Date.now();
  while (gameRunning()) {
    if (Date.now() - t0 > maxMs) { log(`${GAME_EXE} still running after 3 min: killed; ${dumpHungGame('apex-run: still running after 3 min')}`); try { execFileSync('taskkill', ['/F', '/T', '/IM', GAME_EXE], { stdio: 'ignore', timeout: 15000 }); } catch { /* checked by the loop */ } sleep(3000); if (gameRunning()) return; }
    sleep(5000);
  }
}
const killTree = (c?: ChildProcess) => { if (c?.pid && c.exitCode === null) try { execFileSync('taskkill', ['/T', '/F', '/PID', String(c.pid)], { stdio: 'ignore', timeout: 15000 }); } catch { c.kill(); } };

function restoreCmd() {
  if (!existsSync(MARKER)) { console.log('nothing to restore (no probe-out/apex/restore-pending.json)'); return 0; }
  const m: Marker = JSON.parse(readFileSync(MARKER, 'utf8'));
  try { process.kill(m.pid, 0); if (m.pid !== process.pid) { console.error(`run ${m.tag} (PID ${m.pid}) is still alive: it restores Bin itself; stop it first`); return 2; } } catch { /* dead: ours to finish */ }
  waitGameGone(console.log);
  return restoreBin(MARKER, console.log, loadDb()) ? 0 : 3;
}

async function main(v: Record<string, any>) {
  const fail = (s: string) => { console.error(s); process.exit(2); };
  const arms = String(v.arms ?? (v['no-ts3native'] ? 'alone' : 'with')).split(',').map(s => s.trim()).filter(Boolean), loads = Number(v.loads ?? 1);
  if (!v['apex-repo'] || !v.job) fail('pass --apex-repo <Apex checkout> and --job <job.json> (see the header of tools/harness/apex-run.ts)');
  if (!arms.length || arms.some(a => a !== 'with' && a !== 'alone')) fail('--arms takes with / alone, comma-separated');
  if (!(Number.isInteger(loads) && loads >= 1)) fail('--loads must be a whole number >= 1');
  if ([v.release, v.ts3native || v.ini, v['no-ts3native']].filter(Boolean).length > 1) fail('pick one of --release, --ts3native + --ini, --no-ts3native');
  if (!!v.ts3native !== !!v.ini) fail('--ts3native and --ini go together (an asi and its ini are a pair)');
  if (v['no-ts3native'] && arms.includes('with')) fail('--no-ts3native runs Apex alone: arms must all be alone');
  if (isBisect(arms)) { console.error(BISECT_WARN); if (!v['ok-bisect']) process.exit(2); }
  const apex = resolve(v['apex-repo']), runPs1 = join(apex, 'tools', 'ingame_runner', 'run.ps1'), apexPending = join(apex, 'outputs', 'ingame', 'restore-pending.json');
  if (!existsSync(runPs1)) fail(`no ${runPs1}`);
  const db = loadDb(), src = v.release ? releasePair(db, [...new Set([HERE, ROOT])]) : v.ts3native ? { asi: resolve(v.ts3native), ini: resolve(v.ini), name: 'given' } : null;
  for (const f of src ? [src.asi, src.ini] : []) if (!existsSync(f)) fail(`missing ${f}`);
  const plan = armsPlan(arms, loads), tag = v.tag ?? `${new Date().toISOString().slice(0, 16).replace(/[-:]/g, '').replace('T', '-')}`;
  const out = join(APEX_OUT, tag), before = join(out, 'bin-before'), summary = join(out, 'summary.txt'), jsonl = join(out, 'runs.jsonl');
  const rargs = runnerArgs({ bin: BIN, job: v.job, apexAsi: v['apex-asi'] && resolve(v['apex-asi']), variant: v.variant, ab: v.ab, profiler: v.profiler });
  console.log(`apex ${tag}: ${loads} load(s) ${plan.join(' ')}; our pair for 'with': ${src ? `${src.name} ${src.asi}` : 'the pair Bin has now'}; runner: run.ps1 ${rargs.join(' ')}`);
  if (v['dry-run']) return 0;
  if (existsSync(MARKER)) fail(`an earlier apex run did not restore Bin (${MARKER}): node tools/harness/apex-run.ts --restore`);
  if (existsSync(apexPending)) fail(`Apex restore pending (${apexPending}): close TS3W.exe, then run.ps1 -Restore <its run dir>`);
  if (gameRunning()) fail(`${GAME_EXE} is running: close it first`);
  if (existsSync(summary)) fail(`${out} already has a run: pass another --tag`);

  mkdirSync(before, { recursive: true });
  const say = (s: string) => { const l = `${new Date().toISOString().slice(0, 19)}Z ${s}`; console.log(l); appendFileSync(summary, l + '\n'); };
  const files: Record<string, string | null> = {};
  for (const f of PAIR) { files[f] = sha(join(BIN, f)); if (files[f]) copyFileSync(join(BIN, f), join(before, f)); }
  writeFileSync(MARKER, JSON.stringify({ tag, pid: process.pid, at: new Date().toISOString(), bin: BIN, before, files } satisfies Marker, null, 2));
  const runInfo: Record<string, any> = { tag, pid: process.pid, start: new Date().toISOString(), plan, job: v.job, variant: v.variant ?? null, ab: !!v.ab, profiler: v.profiler ?? null, apexRepo: apex, ours: src?.name ?? 'start pair' };
  const saveInfo = () => writeFileSync(join(out, 'run.json'), JSON.stringify(runInfo, null, 2));
  saveInfo();
  say(`start ${tag}: Bin pair at start ${pairName(BIN, db).line} (copy in bin-before/; restored at the end)`);

  const lights = !v['dry-run'] && process.env.TS3_LIGHTS !== 'on' && existsSync(AFX)
    ? setInterval(() => { if (gameRunning()) try { execFileSync(AFX, ['setall=0,0,0'], { stdio: 'ignore', timeout: 15000, windowsHide: true }); } catch { /* next tick */ } }, 30000) : null;
  let child: ChildProcess | undefined, stopReq = '', restored = false, crashes = 0, stopWhy = '';
  const finish = () => {
    if (restored) return true;
    restored = true;
    if (lights) clearInterval(lights);
    waitGameGone(say);
    const ok = restoreBin(MARKER, say, db);
    if (lights) try { execFileSync(AFX, ['setall=0,0,0'], { stdio: 'ignore', timeout: 15000 }); } catch { /* no lights tool */ }
    say(`done${stopWhy ? ` (stopped: ${stopWhy})` : ''}; ${crashes} crash(es); summary ${summary}`);
    return ok;
  };
  process.on('exit', () => { if (!restored) finish(); });
  for (const sig of ['SIGINT', 'SIGTERM', 'SIGHUP'] as const) process.on(sig, () => {
    if (stopReq) { killTree(child); return; }
    stopReq = stopWhy = sig;
    say(`${sig}: stopping; the runner gets 90 s to put its files back, then it is killed; Bin restored after`);
    setTimeout(() => killTree(child), 90000).unref();
    if (!child) process.exit(130);
  });

  try {
    for (let i = 0; i < plan.length && !stopReq; i++) {
      const arm = plan[i], n = i + 1, base = join(out, `load-${n}-${arm}`);
      if (gameRunning()) { stopWhy = `${GAME_EXE} running before load ${n}`; break; }
      if (arm === 'alone') rmSync(join(BIN, 'ts3native.asi'), { force: true });
      else for (const f of PAIR) { const s = src ? (f === 'ts3native.asi' ? src.asi : src.ini) : files[f] ? join(before, f) : null; if (s && sha(s) !== sha(join(BIN, f))) copyFileSync(s, join(BIN, f)); }
      const t0 = Date.now();
      runInfo.current = { load: n, arm, start: new Date(t0).toISOString() }; saveInfo();
      say(`load ${n}/${plan.length} ${arm}: ${pairName(BIN, db).line}`);
      const ws = createWriteStream(base + '.log');
      let last = Date.now();
      child = spawn('pwsh', ['-NoProfile', '-NonInteractive', '-File', runPs1, ...rargs], { cwd: apex, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
      for (const s of [child.stdout!, child.stderr!]) s.on('data', d => { last = Date.now(); ws.write(d); });
      const stall = setInterval(() => { if (Date.now() - last > STALL_MS) { say(`load ${n}: no runner output for 15 min: killed`); stopWhy = `stall in load ${n}`; killTree(child); } }, 30000);
      const exit = await new Promise<number | null>(r => { child!.on('exit', c => r(c)); child!.on('error', e => { say(`runner did not start: ${e.message}`); r(null); }); });
      clearInterval(stall); await new Promise(r => ws.end(r)); child = undefined;
      waitGameGone(say, 60000);
      const nlog = join(BIN, 'ts3native.log'), ncopy = base + '-ts3native.log';
      const haveLog = existsSync(nlog) && statSync(nlog).mtimeMs >= t0;
      if (haveLog) copyFileSync(nlog, ncopy);
      const xcpt = newestXcpt(USER, t0), runLog = readFileSync(base + '.log', 'utf8'), c = crashOf(runLog, xcpt), pending = existsSync(apexPending);
      const fx = haveLog ? forensicLines(readFileSync(ncopy, 'latin1')) : [];
      const row: Row = { tag, load: n, arm, start: new Date(t0).toISOString(), end: new Date().toISOString(), minutes: +((Date.now() - t0) / 60000).toFixed(1),
        exit, gameExit: c.gameExit, crashed: c.crashed, xcpt, pending, nativeLog: haveLog ? ncopy : null, forensics: fx.length };
      appendFileSync(jsonl, JSON.stringify(row) + '\n');
      say(`load ${n} ${arm}: runner exit ${exit}, game exit ${c.gameExit ?? '-'}, ${c.crashed ? 'CRASH' : 'no crash'}${xcpt ? ` (${xcpt})` : ''}, ${row.minutes} min${pending ? ', Apex restore pending' : ''}`);
      if (c.crashed) {
        crashes++;
        let tri = '';
        try { tri = execFileSync(process.execPath, [join(HERE, 'tools', 'harness', 'crash-ledger.ts'), 'triage'], { encoding: 'utf8', timeout: 60000 }); } catch (e) { tri = `crash-ledger triage failed: ${String(e).split('\n')[0]}`; }
        appendFileSync(summary, `  triage: ${(xcpt && triageBlock(tri, xcpt)) || (xcpt ? 'report not in triage output' : 'no xcpt report written')}\n`.replace(/\n(?!$)/g, '\n    '));
        appendFileSync(summary, fx.length ? `  ts3native.log forensic lines (${fx.length}${fx.length > 40 ? ', first 40' : ''}):\n${fx.slice(0, 40).map(l => `    ${l}`).join('\n')}\n` : `  ts3native.log: no DRAW BAD / HEAP / crash forensics: DUMP lines${haveLog ? '' : ' (no log this load)'}\n`);
      }
      if (pending) { stopWhy = `Apex restore pending after load ${n} (run.ps1 -Restore)`; break; }
      if (c.crashed && v['stop-on-crash']) { stopWhy = `crash at load ${n} (--stop-on-crash)`; break; }
    }
  } finally { delete runInfo.current; saveInfo(); if (!finish()) process.exitCode = 3; }
  return process.exitCode ?? (crashes || stopWhy ? 1 : 0);
}

function selfCheck() {
  const d = mkdtempSync(join(tmpdir(), 'apex-run-'));
  try {
    assert.deepEqual(armsPlan(['with', 'alone'], 5), ['with', 'alone', 'with', 'alone', 'with']);
    assert.ok(isBisect(['with', 'alone']) && !isBisect(['with', 'with']));
    assert.deepEqual(runnerArgs({ bin: 'B', job: 'j.json', variant: 'off', ab: true, profiler: 'Frames' }), ['-GameBin', 'B', '-Job', 'j.json', '-Variant', 'off', '-AB', '-Profiler', 'Frames']);
    // crash detection from the runner log of 2026-10-24 (drawfx load 1) and from an xcpt file alone
    const crashLog = '20:40:20 file-list-cache-1-off: FAIL - game closed while waiting for step 3 (wait) (exit code -1073741819); crash report written\n';
    assert.deepEqual(crashOf(crashLog, null), { gameExit: -1073741819, crashed: true });
    assert.deepEqual(crashOf('20:40 results: 1 of 1 loads passed\n', null), { gameExit: null, crashed: false });
    assert.equal(crashOf('ok\n', 'xcpt 1.txt').crashed, true);
    assert.deepEqual(forensicLines('OK   detour crash forensics map erase\nDRAW BAD model 0x0\nfoo\ncrash forensics: DUMP 12 events\nHEAP corrupt at 0x1'), ['DRAW BAD model 0x0', 'crash forensics: DUMP 12 events', 'HEAP corrupt at 0x1']);
    const tri = '\nxcpt a.txt\n  2026  at x\n  change set k\n\nxcpt b.txt\n  2026  at y\n  changed just before: z\n';
    assert.equal(triageBlock(tri, 'xcpt b.txt'), 'xcpt b.txt\n  2026  at y\n  changed just before: z');
    // xcpt files: newest since the load start
    const docs = join(d, 'docs'); mkdirSync(docs);
    writeFileSync(join(docs, 'xcpt old.txt'), ''); const since = Date.now() + 5000;
    assert.equal(newestXcpt(docs, since), null);
    assert.equal(newestXcpt(docs, 0), 'xcpt old.txt');
    // release pair: newest rel entry whose out dir is on disk with the DB hash
    const rd = join(d, 'repo', 'native', 'ts3native', 'out-rel015'); mkdirSync(rd, { recursive: true });
    writeFileSync(join(rd, 'ts3native.asi'), 'rel015 ts3native 0.36.37 %04d'); writeFileSync(join(rd, 'ts3native.ini'), '[x]');
    const db: Build[] = [{ name: 'ts3native.asi.rel015', kind: 'asi', sha256: sha(join(rd, 'ts3native.asi'))!, version: '0.36.37', created: '2026-10-24' },
      { name: 'ts3native.asi.rel099', kind: 'asi', sha256: 'f'.repeat(64), version: '0.99.0', created: '2026-10-30' }];
    assert.equal(releasePair(db, [join(d, 'x'), join(d, 'repo')]).name, 'ts3native.asi.rel015');
    assert.throws(() => releasePair(db.slice(1), [join(d, 'repo')]), /no release pair on disk/);
    // restore: Bin changed by an 'alone' load (asi removed) and a test ini; a killed run left the marker
    const bin = join(d, 'bin'), before = join(d, 'before'); mkdirSync(bin); mkdirSync(before);
    copyFileSync(join(rd, 'ts3native.asi'), join(before, 'ts3native.asi')); copyFileSync(join(rd, 'ts3native.ini'), join(before, 'ts3native.ini'));
    writeFileSync(join(bin, 'ts3native.ini'), '[test]');
    const mk = join(d, 'restore-pending.json'), lines: string[] = [];
    writeFileSync(mk, JSON.stringify({ tag: 't', pid: 1, at: '', bin, before, files: { 'ts3native.asi': sha(join(before, 'ts3native.asi')), 'ts3native.ini': sha(join(before, 'ts3native.ini')) } } satisfies Marker));
    assert.equal(restoreBin(mk, s => lines.push(s), db, () => {}), true);
    assert.equal(sha(join(bin, 'ts3native.asi')), db[0].sha256); assert.equal(readFileSync(join(bin, 'ts3native.ini'), 'utf8'), '[x]');
    assert.ok(!existsSync(mk) && /rel015 ts3native 0\.36\.37/.test(lines[0]) && lines.length === 1, lines.join(' | '));
    // a pair absent at the start is removed again; a non-release asi warns
    writeFileSync(join(bin, 'ts3native.ini'), 'extra'); writeFileSync(join(before, 'ts3native.asi'), 'dev build'); lines.length = 0;
    writeFileSync(mk, JSON.stringify({ tag: 't', pid: 1, at: '', bin, before, files: { 'ts3native.asi': sha(join(before, 'ts3native.asi')), 'ts3native.ini': null } } satisfies Marker));
    assert.equal(restoreBin(mk, s => lines.push(s), db, () => {}), true);
    assert.ok(!existsSync(join(bin, 'ts3native.ini')) && lines.some(l => l.startsWith('WARN')), lines.join(' | '));
    console.log('apex-run self-check ok');
  } finally { rmSync(d, { recursive: true, force: true }); }
}

if (import.meta.main) {
  const { values } = parseArgs({ options: {
    'apex-repo': { type: 'string' }, job: { type: 'string' }, 'apex-asi': { type: 'string' }, variant: { type: 'string' }, ab: { type: 'boolean' }, profiler: { type: 'string' },
    release: { type: 'boolean' }, ts3native: { type: 'string' }, ini: { type: 'string' }, 'no-ts3native': { type: 'boolean' }, loads: { type: 'string' }, arms: { type: 'string' },
    'stop-on-crash': { type: 'boolean' }, 'ok-bisect': { type: 'boolean' }, tag: { type: 'string' }, 'dry-run': { type: 'boolean' }, restore: { type: 'boolean' }, 'self-check': { type: 'boolean' } } });
  if (values['self-check']) selfCheck();
  else if (values.restore) process.exit(restoreCmd());
  else process.exit(await main(values));
}
