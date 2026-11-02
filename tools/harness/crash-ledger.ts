// Ties crashes to our changes. Every test start records the active change set; triage maps each game crash
// report (xcpt*.txt) to the change set that was active then and to what changed just before it.
//   node tools/harness/crash-ledger.ts snapshot [--note "why"]   append the current change set (only if it changed)
//   node tools/harness/crash-ledger.ts triage [--since <iso>] [--pids 1,2]   list every crash report (written at or after --since) with its change set;
//        --pids: only reports whose minidump (xcpt*.mdmp beside the .txt) names one of these game PIDs (an old report or an EA app
//        `-test` relaunch's crash is not this run's); a report without a dump is kept and marked "PID not checked"
//   node tools/harness/crash-ledger.ts --self-check              minidump PID/start-time reader and the PID filter on a fixture
// cycle.ts --save calls snapshot, so every harness run is covered.
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { appendFileSync, closeSync, existsSync, mkdtempSync, openSync, readdirSync, readFileSync, readSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { homedir, tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { parseArgs } from 'node:util';
import { BIN, GAME_EXE } from './game-exe.ts';

const ROOT = resolve(import.meta.dirname, '..', '..');
const LEDGER = join(ROOT, 'docs', 'data', 'change-ledger.jsonl');
const USER = process.env.TS3_USER ?? join(homedir(), 'Documents', 'Electronic Arts', 'The Sims 3');
const STATE = join(process.env.LOCALAPPDATA ?? join(homedir(), 'AppData', 'Local'), 'ts3-fixes', 'state.json');

type Entry = { at: string; key: string; note?: string; git: string; fixes: string[]; files: Record<string, string>; mods: string[]; options: Record<string, string> };

const sha = (b: Buffer | string) => createHash('sha256').update(b).digest('hex').slice(0, 12);
const fileHash = (p: string) => (existsSync(p) ? sha(readFileSync(p)) : 'missing');

function current(note?: string): Entry {
  let git = 'unknown';
  try {
    git = execFileSync('git', ['-C', ROOT, 'rev-parse', '--short', 'HEAD']).toString().trim();
    // docs/ (incl. this ledger) changes do not alter what runs
    if (execFileSync('git', ['-C', ROOT, 'status', '--porcelain', '--', '.', ':!docs']).toString().trim()) git += '+dirty';
  } catch { /* not a repo */ }
  const state: Record<string, { version: string | null; files: { path: string }[] }> = existsSync(STATE) ? JSON.parse(readFileSync(STATE, 'utf8')) : {};
  const files: Record<string, string> = { [GAME_EXE]: fileHash(join(BIN, GAME_EXE)), 'GraphicsRules.sgr': fileHash(join(BIN, 'GraphicsRules.sgr')) };
  for (const f of Object.values(state)) for (const x of f.files) if (!x.path.endsWith('Options.ini')) files[x.path.replace(BIN, 'Bin').replace(USER, 'User')] = fileHash(x.path);
  const pkgDir = join(USER, 'Mods', 'Packages');
  const mods = existsSync(pkgDir) ? readdirSync(pkgDir, { recursive: true }).map(String).filter(n => /\.package$/i.test(n))
    .map(n => `${n}:${n.startsWith('ts3') ? fileHash(join(pkgDir, n)) : statSync(join(pkgDir, n)).size}`).sort() : [];
  const options: Record<string, string> = {};
  const ini = join(USER, 'Options.ini');
  if (existsSync(ini)) for (const m of readFileSync(ini, 'latin1').matchAll(/^(\w*quality|enable\w+)\s*=\s*(\S+)/gim)) options[m[1].toLowerCase()] = m[2];
  const body = { git, fixes: Object.entries(state).map(([id, f]) => `${id}@${f.version ?? '-'}`).sort(), files, mods, options };
  return { at: new Date().toISOString(), key: sha(JSON.stringify(body)), ...(note ? { note } : {}), ...body };
}

const readLedger = (): Entry[] => (existsSync(LEDGER) ? readFileSync(LEDGER, 'utf8').split('\n').filter(Boolean).map(l => JSON.parse(l)) : []);

function diff(a: Entry | undefined, b: Entry): string[] {
  if (!a) return ['(first recorded change set)'];
  const out: string[] = [];
  if (a.git !== b.git) out.push(`git ${a.git} -> ${b.git}`);
  for (const x of b.fixes.filter(f => !a.fixes.includes(f))) out.push(`+fix ${x}`);
  for (const x of a.fixes.filter(f => !b.fixes.includes(f))) out.push(`-fix ${x}`);
  for (const k of new Set([...Object.keys(a.files), ...Object.keys(b.files)])) if (a.files[k] !== b.files[k]) out.push(`file ${k} ${a.files[k] ?? 'none'} -> ${b.files[k] ?? 'none'}`);
  for (const x of b.mods.filter(m => !a.mods.includes(m))) out.push(`+mod ${x}`);
  for (const x of a.mods.filter(m => !b.mods.includes(m))) out.push(`-mod ${x}`);
  for (const k of new Set([...Object.keys(a.options), ...Object.keys(b.options)])) if (a.options[k] !== b.options[k]) out.push(`option ${k} ${a.options[k]} -> ${b.options[k]}`);
  return out;
}

function snapshot(note?: string) {
  const e = current(note);
  const last = readLedger().at(-1);
  if (last?.key === e.key && !note) { console.log(`change set unchanged (${e.key})`); return; }
  appendFileSync(LEDGER, JSON.stringify(e) + '\n');
  console.log(`change set ${e.key} recorded; since last: ${diff(last, e).join('; ') || 'nothing (note only)'}`);
}

// PID and process start of a minidump (MINIDUMP_MISC_INFO, stream 15), or null (no dump, no stream, or no PID flag)
export function dumpProcess(path: string): { pid: number; started: Date | null } | null {
  if (!existsSync(path)) return null;
  const fd = openSync(path, 'r');
  try {
    const rd = (pos: number, n: number) => { const b = Buffer.alloc(n); return readSync(fd, b, 0, n, pos) === n ? b : null; };
    const h = rd(0, 16);
    if (!h || h.toString('latin1', 0, 4) !== 'MDMP') return null;
    const n = h.readUInt32LE(8), dir = rd(h.readUInt32LE(12), 12 * n);
    for (let i = 0; dir && i < n; i++) {
      if (dir.readUInt32LE(i * 12) !== 15) continue;
      const m = rd(dir.readUInt32LE(i * 12 + 8), 16);
      if (!m || !(m.readUInt32LE(4) & 1)) return null; // MINIDUMP_MISC1_PROCESS_ID
      return { pid: m.readUInt32LE(8), started: m.readUInt32LE(4) & 2 ? new Date(m.readUInt32LE(12) * 1000) : null };
    }
    return null;
  } finally { closeSync(fd); }
}
// reports of this run: PID filter when pids are given (see header); the others are listed in one note line
export function pickReports(dir: string, names: string[], pids?: number[]) {
  const keep: { n: string; proc: ReturnType<typeof dumpProcess> }[] = [], other: string[] = [];
  for (const n of names) {
    const proc = dumpProcess(join(dir, n.replace(/\.txt$/i, '.mdmp')));
    if (pids?.length && proc && !pids.includes(proc.pid)) other.push(`${n} (pid ${proc.pid})`); else keep.push({ n, proc });
  }
  return { keep, other };
}

function triage(since?: string, pids?: number[]) {
  const ledger = readLedger();
  const all = readdirSync(USER).filter(n => /^xcpt .*\.txt$/i.test(n) && (!since || statSync(join(USER, n)).mtime >= new Date(since))).sort();
  const { keep, other } = pickReports(USER, all, pids);
  if (other.length) console.log(`not this run's game (pid ${pids!.join(',')}): ${other.join('; ')}\n`); // first: callers take the last block
  if (!keep.length) { console.log(since || pids?.length ? `no crash report of this run${since ? ` written since ${since}` : ''}${pids?.length ? ` with pid ${pids.join(',')}` : ''}` : 'no crash reports'); return; }
  for (const { n, proc } of keep) {
    const t = readFileSync(join(USER, n), 'latin1');
    const [d, tm] = [t.match(/^date: (\S+)/m)?.[1], t.match(/^time: (\S+)/m)?.[1]];
    const when = d && tm ? new Date(`${d}T${tm.replace(/\./g, ':')}`) : statSync(join(USER, n)).mtime; // report time is local
    const type = t.match(/^type: (.+)$/m)?.[1]?.trim() ?? '?';
    const addr = t.match(/^address: (0x[0-9a-f]+) "([^"]*)"/im);
    const mod = addr ? `${addr[1]} in ${addr[2].split('\\').at(-1)}` : '?';
    const stackMods = [...new Set([...(t.split('[Call stack]')[1]?.split('[Stack data]')[0] ?? '').matchAll(/\\([^\\\r\n]+\.(?:exe|dll|asi))\r?\n/gi)].map(m => m[1]))];
    const i = ledger.findLastIndex(e => new Date(e.at) <= when);
    console.log(`\n${n}\n  ${when.toISOString()}  ${type}  at ${mod}  stack: ${stackMods.join(', ') || '-'}`);
    console.log(proc ? `  pid ${proc.pid}${proc.started ? `, process started ${proc.started.toISOString()}` : ''}${/^Commandline: .*\s-test\b/m.test(t) ? ', -test' : ''}` : '  no minidump: PID not checked');
    if (i < 0) { console.log('  change set: none recorded before this crash'); continue; }
    console.log(`  change set ${ledger[i].key} (recorded ${ledger[i].at}${ledger[i].note ? `, ${ledger[i].note}` : ''})`);
    console.log(`  changed just before: ${diff(ledger[i - 1], ledger[i]).join('; ')}`);
    const same = ledger.slice(0, i).filter(e => e.key === ledger[i].key).length;
    if (same) console.log(`  same change set was active ${same} earlier time(s)`);
  }
}

// minidump fixture: header, one directory entry (stream 15), MISC_INFO with flags PROCESS_ID|PROCESS_TIMES
function fakeDump(pid: number, started: number): Buffer {
  const b = Buffer.alloc(16 + 12 + 24);
  b.write('MDMP', 0, 'latin1'); b.writeUInt32LE(1, 8); b.writeUInt32LE(16, 12);
  b.writeUInt32LE(15, 16); b.writeUInt32LE(24, 20); b.writeUInt32LE(28, 24);
  b.writeUInt32LE(24, 28); b.writeUInt32LE(3, 32); b.writeUInt32LE(pid, 36); b.writeUInt32LE(started, 40);
  return b;
}

if (import.meta.main) {
  const { values, positionals } = parseArgs({ allowPositionals: true, options: { note: { type: 'string' }, since: { type: 'string' }, pids: { type: 'string' }, 'self-check': { type: 'boolean' } } });
  if (values['self-check']) {
    const assert = (await import('node:assert')).strict;
    const d = mkdtempSync(join(tmpdir(), 'ts3-xcpt-'));
    try {
      writeFileSync(join(d, 'xcpt A 1.txt'), ''); writeFileSync(join(d, 'xcpt A 1.mdmp'), fakeDump(2576, 1793955301));
      writeFileSync(join(d, 'xcpt A 2.txt'), ''); writeFileSync(join(d, 'xcpt A 2.mdmp'), fakeDump(14068, 1793958747)); // EA relaunch
      writeFileSync(join(d, 'xcpt A 3.txt'), ''); // no dump
      writeFileSync(join(d, 'xcpt A 4.mdmp'), 'MDMPshort');
      assert.deepEqual(dumpProcess(join(d, 'xcpt A 1.mdmp')), { pid: 2576, started: new Date(1793955301000) });
      assert.equal(dumpProcess(join(d, 'xcpt A 4.mdmp')), null); assert.equal(dumpProcess(join(d, 'none.mdmp')), null);
      const names = ['xcpt A 1.txt', 'xcpt A 2.txt', 'xcpt A 3.txt'];
      const r = pickReports(d, names, [2576]);
      assert.deepEqual(r.keep.map(k => k.n), ['xcpt A 1.txt', 'xcpt A 3.txt']); assert.deepEqual(r.other, ['xcpt A 2.txt (pid 14068)']);
      assert.equal(pickReports(d, names).keep.length, 3, 'no pids: no filter');
      assert.deepEqual(pickReports(d, names, [999]).keep.map(k => k.n), ['xcpt A 3.txt'], 'other PIDs only: none of this run');
    } finally { rmSync(d, { recursive: true, force: true }); }
    console.log('self-check ok');
  } else if (positionals[0] === 'snapshot') snapshot(values.note);
  else if (positionals[0] === 'triage') triage(values.since, values.pids?.split(',').filter(Boolean).map(Number));
  else { console.error('usage: node tools/harness/crash-ledger.ts snapshot [--note "why"] | triage [--since <iso>] [--pids 1,2] | --self-check'); process.exit(2); }
}
