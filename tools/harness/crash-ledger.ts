// Ties crashes to our changes. Every test start records the active change set; triage maps each game crash
// report (xcpt*.txt) to the change set that was active then and to what changed just before it.
//   node tools/harness/crash-ledger.ts snapshot [--note "why"]   append the current change set (only if it changed)
//   node tools/harness/crash-ledger.ts triage [--since <iso>] [--pids 1,2]   list every crash report (written at or after --since) with its change set;
//        --pids: only reports whose minidump (xcpt*.mdmp beside the .txt) names one of these game PIDs (an old report or an EA app
//        `-test` relaunch's crash is not this run's); a report without a dump is kept and marked "PID not checked"
//   node tools/harness/crash-ledger.ts --self-check              minidump PID/start-time reader, the PID filter, the 0x5E772C, 0xD4B156 / 0x5AF67E and MSVCR80 memcpy (0x5E621C) frame decoders (fixtures)
// cycle.ts --save calls snapshot, so every harness run is covered.
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { appendFileSync, closeSync, existsSync, mkdirSync, mkdtempSync, openSync, readdirSync, readFileSync, readSync, rmSync, statSync, writeFileSync } from 'node:fs';
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
// TS3W's own exit status from ts3probe's "detached" event (probe-out/<stamp>/events.jsonl). A stack overflow or fast fail ends
// the game with no xcpt report, so the queue saw only "process gone" (16 such crash rows by 2026-11-04).
const EXIT_NAMES: Record<string, string> = { '0xC0000005': 'access violation', '0xC00000FD': 'stack overflow', '0xC0000409': 'fast fail',
  '0xC0000374': 'heap corruption', '0xC0000017': 'out of memory', '0x00000000': 'clean exit' };
export function gameExit(dir: string, since?: string, pids?: number[]): string {
  let last = '';
  if (!existsSync(dir)) return last;
  for (const s of readdirSync(dir).filter(n => /^\d{8}-\d{6}$/.test(n)).sort()) {
    const f = join(dir, s, 'events.jsonl');
    if (!existsSync(f) || (since && statSync(f).mtime < new Date(since))) continue;
    for (const l of readFileSync(f, 'utf8').split('\n')) {
      if (!l.includes('"exitCode"')) continue;
      let e: any; try { e = JSON.parse(l); } catch { continue; } // a line still being written
      if (e.kind !== 'detached' || (pids?.length && !pids.includes(e.pid)) || (since && new Date(e.time) < new Date(since))) continue; // e.time is local
      last = `; game exit code ${e.exitCode}${EXIT_NAMES[e.exitCode] ? ` (${EXIT_NAMES[e.exitCode]})` : ''}`;
    }
  }
  return last;
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
  if (!keep.length) { console.log((since || pids?.length ? `no crash report of this run${since ? ` written since ${since}` : ''}${pids?.length ? ` with pid ${pids.join(',')}` : ''}` : 'no crash reports') + gameExit(join(ROOT, 'probe-out'), since, pids)); return; }
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
    const inv = inventoryFault(t);
    if (inv) console.log(`  ${inv}`);
    const rel = modelBuilderFault(t) ?? releaseFault(t);
    if (rel) console.log(`  ${rel}`);
    const os = outfitStringFault(t);
    if (os) console.log(`  ${os}`);
    console.log(proc ? `  pid ${proc.pid}${proc.started ? `, process started ${proc.started.toISOString()}` : ''}${/^Commandline: .*\s-test\b/m.test(t) ? ', -test' : ''}` : '  no minidump: PID not checked');
    if (i < 0) { console.log('  change set: none recorded before this crash'); continue; }
    console.log(`  change set ${ledger[i].key} (recorded ${ledger[i].at}${ledger[i].note ? `, ${ledger[i].note}` : ''})`);
    console.log(`  changed just before: ${diff(ledger[i - 1], ledger[i]).join('; ')}`);
    const same = ledger.slice(0, i).filter(e => e.key === ledger[i].key).length;
    if (same) console.log(`  same change set was active ${same} earlier time(s)`);
  }
}

// docs/539 §12.5: a fault in EA's outfit inventory 0x5E7690 (0x5E76DA reads a slot of [rec+0x14], 0x5E772C of [rec+0x38];
// the 2026-11-08 travel-save crash). Its frame from the report's [Stack data] and [Registers]: record [esp+0x10], outfit +0x14,
// end +0x18, return +0x1C (0x5EFE16 = the SimService save 0x5EFD60), set +0x20, children +0x24; esi = the slot value, ebx = slot * 4.
export function inventoryFault(t: string): string | null {
  const reg = (r: string) => { const m = t.match(new RegExp(`^${r}: ([0-9a-f]{8})`, 'im')); return m ? parseInt(m[1], 16) : null; };
  const eip = reg('eip');
  if (eip !== 0x5e772c && eip !== 0x5e76da) return null;
  const bytes = new Map<number, number>();
  for (const m of (t.split('[Stack data]')[1]?.split(/\r?\n\[/)[0] ?? '').matchAll(/^([0-9a-f]{8}) \|([^|]*)\|/gim))
    m[2].replace(/[<>]/g, ' ').trim().split(/\s+/).forEach((h, i) => bytes.set(parseInt(m[1], 16) + i, parseInt(h, 16)));
  const esp = reg('esp') ?? 0, hex = (v: number | null) => (v === null ? '?' : `0x${v.toString(16).padStart(8, '0').toUpperCase()}`);
  const dw = (a: number) => { const b = [0, 1, 2, 3].map(i => bytes.get(a + i)); return b.some(x => x === undefined) ? null : (b[0]! | (b[1]! << 8) | (b[2]! << 16) | (b[3]! << 24)) >>> 0; };
  const [rec, outfit, end, ret, set, kids] = [0x10, 0x14, 0x18, 0x1c, 0x20, 0x24].map(o => dw(esp + o));
  const left = rec !== null && end !== null && end >= rec ? (end - rec) / 0x68 : null;
  return `EA outfit inventory 0x5E7690 at ${hex(eip)} (array [rec+${eip === 0x5e772c ? '0x38' : '0x14'}] slot ${(reg('ebx') ?? -4) / 4}): value esi ${hex(reg('esi'))}, ` +
    `its first dword edx ${hex(reg('edx'))}; record ${hex(rec)} (${left ?? '?'} records to the end ${hex(end)}), outfit ${hex(outfit)}, return ${hex(ret)}` +
    `${ret === 0x5efe16 ? ' (SimService save 0x5EFD60)' : ''}, set ${hex(set)}, children ${kids ?? '?'}; docs/539 §12: match the ts3native log's ` +
    '"cas trim: inventory bad slot" / "crash forensics: cas trim inventory fault" lines (castrimdiag builds)';
}

// docs/539 §13.5 item 4: EA's release of an outfit's [outfit+0xBC] result cell (the 2026-11-08 release-smoke crash): 0xD4B156 in the
// callback 0xD4B150 (frame [esp] 0x7287EC, value +4, 0x728DD4 +0x14, 0x5AF66D +0x28, cell +0x2C, outfit +0x38, return +0x3C, begin +0x40,
// end +0x44) or 0x5AF67E, the same release inlined in 0x5AF640 (esi = cell, ecx = value; outfit [esp+4], return +8, begin +0xC, end +0x10).
export function releaseFault(t: string): string | null {
  const { reg, dw, hex } = reportFrame(t);
  const eip = reg('eip');
  if ((eip === null || eip < 0xd4b150 || eip > 0xd4b159) && eip !== 0x5af67e) return null;
  const esp = reg('esp') ?? 0, d = (o: number) => dw(esp + o);
  const inl = eip === 0x5af67e;
  const frame = inl || (d(0) === 0x7287ec && d(0x14) === 0x728dd4 && d(0x28) === 0x5af66d);
  const [v, cell, outfit, ret, b, e] = inl ? [reg('ecx'), reg('esi'), d(4), d(8), d(0xc), d(0x10)]
    : [reg('ebx') ?? d(4), frame ? d(0x2c) : null, frame ? d(0x38) : null, frame ? d(0x3c) : null, frame ? d(0x40) : null, frame ? d(0x44) : null];
  const idx = cell !== null && b !== null && cell >= b ? (cell - b) / 4 : null, n = b !== null && e !== null && e >= b ? (e - b) / 4 : null;
  const who = ret === 0x5e6e65 ? ' (clear 0x5E6E20)' : ret === 0x5e7638 ? ' (SimOutfit destructor 0x5E75A0)' : '';
  return `EA outfit cell release at ${hex(eip)} (${frame ? '0x5AF640 frame' : 'value only: another caller of 0xD4B150'}): value ${hex(v)}${v === 0x40a00000 ? ' (float 5.0)' : ''}, ` +
    `cell ${hex(cell)} (entry ${idx ?? '?'} of ${n ?? '?'}, ${hex(b)}-${hex(e)}), outfit ${hex(outfit)}, return ${hex(ret)}${who}; docs/539 §13: match the ts3native log's ` +
    '"crash forensics: cas trim outfit release fault" / "cas trim: cell guard skipped" lines (castrimdiag1108b+)';
}
// docs/539 §14: the same callback 0xD4B150 (0xD4B154 / 0xD4B156) called from ModelBuilder's 0x5D82A0 (the 2026-11-09
// bp-leak40-memtravel-x3 crash). Frame: [esp] 0x7287EC, value +4, record end +0xC, record +0x10 (0x728760's saved ebp / ebx),
// 0x728DD4 +0x14, cell +0x18, loop return +0x28 (0x5D82CF / 0x5D831A lists, 0x5D8377 part records), saved esi +0x38 (the
// builder from 0x5D83B4, the holder from 0x5D96AD), return +0x44 (0x5D83B9 / 0x5D96B2).
export function modelBuilderFault(t: string): string | null {
  const { reg, dw, hex } = reportFrame(t);
  const eip = reg('eip'), esp = reg('esp') ?? 0, d = (o: number) => dw(esp + o);
  if (eip === null || eip < 0xd4b150 || eip > 0xd4b159 || d(0) !== 0x7287ec || d(0x14) !== 0x728dd4) return null;
  const lr = d(0x28), loop = lr === 0x5d8377 ? 'part records' : lr === 0x5d82cf ? 'list +0x1C' : lr === 0x5d831a ? 'list +0x28' : null;
  if (!loop) return null;
  const v = reg('ebx') ?? d(4), cell = d(0x18), ret = d(0x44), parts = lr === 0x5d8377, rec = parts ? d(0x10) : null, end = parts ? d(0xc) : null;
  const who = ret === 0x5d83b9 ? 'builder (ModelBuilder dtor 0x5D83B4)' : ret === 0x5d96b2 ? 'holder (release 0x5D96AD; builder = [holder+0xC])' : '?';
  const left = rec !== null && end !== null && end >= rec ? (end - rec) / 0x74 : null, k = rec !== null && cell !== null && cell >= rec + 4 ? (cell - rec - 4) / 4 : null;
  return `EA ModelBuilder release 0x5D82A0 ${loop} at ${hex(eip)}: value ${hex(v)}${v === 0x40a00000 ? ' (float 5.0)' : ''}, cell ${hex(cell)}` +
    `${parts ? ` (rec+0x${cell !== null && rec !== null ? (cell - rec).toString(16).toUpperCase() : '?'}, cell ${k ?? '?'}), record ${hex(rec)} (${left ?? '?'} records to the end ${hex(end)})` : ''}` +
    `, saved esi ${hex(d(0x38))} = ${who}, return ${hex(ret)}; docs/539 §14: match the ts3native log's "crash forensics: cas trim modelbuilder release fault" / ` +
    '"cas trim: modelbuilder cell bad" lines (0.36.218+)';
}
// docs/539 §15: the 2026-11-09 bp-float5-memtravel-x8 crash, an AV writing NULL in MSVCR80's memcpy (no TS3W eip). The 0x5BF4C0
// frame is found in the stack data: its return 0x5E621C at A, 0x408E20's return 0x5BF5E0 at A-0x64, 0x5E61A0's return 0x5E7BC7
// at A+0x54; A-0x58 the record (0x5BF4C0's saved esi), A-0x60 / A-0x5C the string begin / end, A-0x6C the object (0x408E20's
// saved esi), A+0xC the outfit (0x5E61A0's saved esi). 0x408C3A (0x408B90's memcpy return) below A is reported.
export function outfitStringFault(t: string): string | null {
  const { reg, dw, hex } = reportFrame(t);
  const esp = reg('esp');
  if (esp === null) return null;
  let a = -1;
  for (let p = esp; p < esp + 0x400 && a < 0; p += 4) if (dw(p) === 0x5e621c && dw(p - 0x64) === 0x5bf5e0 && dw(p + 0x54) === 0x5e7bc7) a = p;
  if (a < 0) return null;
  let mc = false;
  for (let p = esp; p < a && !mc; p += 4) mc = dw(p) === 0x408c3a;
  const b = dw(a - 0x60), e = dw(a - 0x5c), eip = reg('eip');
  return `EA outfit save string copy 0x5BF4C0 -> 0x408E20 -> 0x408B90 at ${hex(eip)}${eip !== null && (eip < 0x401000 || eip >= 0xf95000) ? ' (outside TS3W: the CRT memcpy)' : ''}: ` +
    `string begin ${hex(b)}${b === 0x40a00000 ? ' (float 5.0)' : ''}, end ${hex(e)}${e === 0x11c7e54 ? " (EA's empty string)" : ''}, object ${hex(dw(a - 0x6c))}, ` +
    `record ${hex(dw(a - 0x58))}, outfit ${hex(dw(a + 0xc))}, frame at ${hex(a)}${mc ? ', 0x408C3A memcpy return below it' : ''}; docs/539 §15: match the ts3native log's ` +
    '"crash forensics: cas trim outfit string copy fault" / "cas trim: outfit string bad" lines (0.36.225+)';
}
// an EA xcpt report's [Registers] and [Stack data] (16 bytes a line, <> around the esp byte)
function reportFrame(t: string) {
  const reg = (r: string) => { const m = t.match(new RegExp(`^${r}: ([0-9a-f]{8})`, 'im')); return m ? parseInt(m[1], 16) : null; };
  const bytes = new Map<number, number>();
  for (const m of (t.split('[Stack data]')[1]?.split(/\r?\n\[/)[0] ?? '').matchAll(/^([0-9a-f]{8}) \|([^|]*)\|/gim))
    m[2].replace(/[<>]/g, ' ').trim().split(/\s+/).forEach((h, i) => bytes.set(parseInt(m[1], 16) + i, parseInt(h, 16)));
  const dw = (a: number) => { const b = [0, 1, 2, 3].map(i => bytes.get(a + i)); return b.some(x => x === undefined) ? null : (b[0]! | (b[1]! << 8) | (b[2]! << 16) | (b[3]! << 24)) >>> 0; };
  const hex = (v: number | null) => (v === null ? '?' : `0x${v.toString(16).padStart(8, '0').toUpperCase()}`);
  return { reg, dw, hex };
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
      const ev = join(d, 'probe-out', '20261104-114729'); mkdirSync(ev, { recursive: true });
      writeFileSync(join(ev, 'events.jsonl'), '{"kind": "detached", "reason": "process-terminated", "time": "2026-11-04T11:56:31", "pid": 41004, "exitCode": "0xC00000FD"}\n{"kind": "det');
      assert.equal(gameExit(join(d, 'probe-out'), undefined, [41004]), '; game exit code 0xC00000FD (stack overflow)');
      assert.equal(gameExit(join(d, 'probe-out'), undefined, [7]), '', 'other pid');
      assert.equal(gameExit(join(d, 'probe-out'), '2099-01-01T00:00:00Z'), '', 'before since');
      assert.equal(gameExit(join(d, 'none')), '');
      // the inventory frame (values of the 11-08 crash, laid out as an EA report: 16 bytes a line, <> around the esp byte)
      const line = (a: number, w: number[]) => `${a.toString(16)} | ${w.flatMap(v => [0, 8, 16, 24].map(s => ((v >>> s) & 0xff).toString(16).padStart(2, '0'))).join(' ')} | ................ |`;
      const rep = ['[Stack data]', line(0x44b57590, [0x5e7758, 0x5e7721, 0x40a00000, 0x5de8c064]), line(0x44b575a0, [0x356ccdb0, 0, 0xffffffff, 0x334789a0]),
        line(0x44b575b0, [0x35f8d460, 0x33478ce0, 0x5efe16, 0x44b575d4]), line(0x44b575c0, [1, 0x13877260, 0, 0]), '',
        '[Registers]', 'eip: 005e772c', 'ebx: 00000004', 'edx: 00000001', 'esi: 40a00000', 'esp: 44b5759c', ''].join('\r\n').replace('a0 40 64', 'a0 40<64>');
      const inv = inventoryFault(rep)!;
      assert.match(inv, /\[rec\+0x38\] slot 1\): value esi 0x40A00000, its first dword edx 0x00000001; record 0x334789A0 \(8 records to the end 0x33478CE0\), outfit 0x35F8D460, return 0x005EFE16 \(SimService save 0x5EFD60\), set 0x44B575D4, children 1;/);
      assert.equal(inventoryFault(rep.replace('eip: 005e772c', 'eip: 0058d9b6')), null, 'another fault site');
      assert.match(inventoryFault(rep.replace(/^44b575b0 .*$/m, ''))!, /outfit \?, return \?/, 'missing stack lines: ? not a wrong value');
      // the release-smoke report (2026-11-08 06:14 UTC), its own stack data lines
      const rel = ['[Stack data]', '001afcb0 | c0 dd f1 0c c5 17 4e 00 c0 dd f1 0c<ec>87 72 00 | ......N.......r. |',
        '001afcc0 | 00 00 a0 40 c0 dd f1 0c 00 00 00 00 d0 72 e6 0c | ...@.........r.. |', '001afcd0 | d4 8d 72 00 9c 08 9e 40 50 b1 d4 00 5c 09 9e 40 | ..r....@P...\..@ |',
        '001afce0 | 9c 08 9e 40 6d f6 5a 00 9c 08 9e 40 50 b1 d4 00 | ...@m.Z....@P... |', '001afcf0 | 3c 1b 78 7b 80 1a 78 7b 65 6e 5e 00 a0 07 9e 40 | <.x{..x{en^....@ |',
        '001afd00 | 5c 09 9e 40 84 1a 78 7b d0 72 e6 0c 80 1a 78 7b | \..@..x{.r....x{ |', '',
        '[Registers]', 'eip: 00d4b156', 'eax: 00000000', 'ebx: 40a00000', 'ecx: 40a00000', 'esi: 0cf1dd80', 'esp: 001afcbc', ''].join('\r\n');
      assert.match(releaseFault(rel)!, /0x5AF640 frame\): value 0x40A00000 \(float 5\.0\), cell 0x409E089C \(entry 63 of 111, 0x409E07A0-0x409E095C\), outfit 0x7B781A80, return 0x005E6E65 \(clear 0x5E6E20\);/);
      assert.equal(releaseFault(rep), null, 'the inventory site is not a release fault');
      assert.match(releaseFault(rel.replace('001afcd0 | d4 8d', '001afcd0 | 00 00'))!, /value only/, 'another caller of the callback');
      const inl = ['[Stack data]', '0012fff0 | 00 00 00 00 00 00 00 00 00 00 00 00<3c>1b 78 7b | ................ |',
        '00130000 | 80 1a 78 7b 38 76 5e 00 a0 07 9e 40 5c 09 9e 40 | ................ |', '', '[Registers]', 'eip: 005af67e', 'ecx: 40a00000', 'esi: 409e089c', 'esp: 0012fffc', ''].join('\r\n');
      assert.match(releaseFault(inl)!, /cell 0x409E089C \(entry 63 of 111, .*outfit 0x7B781A80, return 0x005E7638 \(SimOutfit destructor 0x5E75A0\)/);
      assert.equal(modelBuilderFault(rel), null, 'the outfit release frame is not a ModelBuilder one');
      // the bp-leak40-memtravel-x3 report (2026-11-09 04:15 local): 0xD4B154 from 0x5D82A0's part-record loop
      const mb = ['[Stack data]', '001af630 | 20 50 ed 15<ec>87 72 00 00 00 a0 40 20 50 ed 15 |  P....r....@ P.. |',
        '001af640 | 30 65 cf 33 78 62 cf 33 d4 8d 72 00 7c 62 cf 33 | 0e.3xb.3..r.|b.3 |', '001af650 | 50 b1 d4 00 04 00 00 00 7c 62 cf 33 77 83 5d 00 | P.......|b.3w.]. |',
        '001af660 | 7c 62 cf 33 50 b1 d4 00 ac c2 be 77 20 c2 be 77 | |b.3P......w ..w |', '001af670 | 28 65 83 33 f0 70 de 15 b9 83 5d 00 20 c2 be 77 | (e.3.p....]. ..w |', '',
        '[Registers]', 'eip: 00d4b154', 'eax: 00000000', 'ebx: 40a00000', 'ecx: 40a00000', 'esp: 001af634', ''].join('\r\n');
      assert.match(modelBuilderFault(mb)!, /part records at 0x00D4B154: value 0x40A00000 \(float 5\.0\), cell 0x33CF627C \(rec\+0x4, cell 0\), record 0x33CF6278 \(6 records to the end 0x33CF6530\), saved esi 0x77BEC220 = builder \(ModelBuilder dtor 0x5D83B4\), return 0x005D83B9;/);
      assert.match(releaseFault(mb)!, /value only/, 'releaseFault alone: no 0x5AF640 frame');
      assert.match(modelBuilderFault(mb.replace('77 83 5d 00', 'cf 82 5d 00'))!, /list \+0x1C at .*cell 0x33CF627C, saved esi/, 'the first list loop');
      assert.equal(modelBuilderFault(mb.replace('77 83 5d 00', '6d f6 5a 00')), null, 'another loop return');
      // the bp-float5-memtravel-x8 report (2026-11-09 06:33 local): memcpy to NULL in MSVCR80 from the outfit save's string copy
      const os = ['[Stack data]', '49777210 |<00>7e 7c c0 00 00 00 00 48 72 77 49 76 74 09 70 | ................ |',
        '49777240 | 34 73 77 49 00 00 00 00 00 00 00 00 3a 8c 40 00 | ................ |', '497772a0 | 18 bd 66 7a 80 b5 39 3c e4 73 77 49 e0 f5 5b 00 | ................ |',
        '497772b0 | 00 00 a0 40 54 7e 1c 01 20 98 61 3d b3 ea 4d 00 | ................ |', '49777310 | 1c 62 5e 00 34 73 77 49 07 3a 02 00 50 d2 83 65 | ................ |',
        '49777360 | f8 47 fe 00 c7 7b 5e 00 e4 73 77 49 20 bd 66 7a | ................ |', '', '[Registers]', 'eip: 700973d0', 'ebx: 40a00000', 'esp: 49777210', ''].join('\r\n');
      assert.match(outfitStringFault(os)!, /at 0x700973D0 \(outside TS3W: the CRT memcpy\): string begin 0x40A00000 \(float 5\.0\), end 0x011C7E54 \(EA's empty string\), object 0x3C39B580, record 0x3D619820, outfit 0x6583D250, frame at 0x49777310, 0x408C3A memcpy return below it;/);
      assert.equal(outfitStringFault(os.replace('e0 f5 5b 00', '00 00 00 00')), null, 'no 0x408E20 return: not this frame');
      assert.equal(outfitStringFault(mb), null, 'the ModelBuilder frame is not this one');
    } finally { rmSync(d, { recursive: true, force: true }); }
    console.log('self-check ok');
  } else if (positionals[0] === 'snapshot') snapshot(values.note);
  else if (positionals[0] === 'triage') triage(values.since, values.pids?.split(',').filter(Boolean).map(Number));
  else { console.error('usage: node tools/harness/crash-ledger.ts snapshot [--note "why"] | triage [--since <iso>] [--pids 1,2] | --self-check'); process.exit(2); }
}
