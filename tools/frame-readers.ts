// Frame-reader gate of [MonoJit] Direct (docs/537, docs/223 §8 risk 1): nothing may read a frame a lazy call did not write.
//   node tools/frame-readers.ts --census <prefix>       <prefix>-sites.tsv of tools/ghidra/FrameReaders.java -> docs/data/frame-readers.csv
//                                                       (addresses and classes only, no instruction text); exit 1 on a site no class covers
//   node tools/frame-readers.ts --log <ts3native.log>   the "jit trap:" lines of a run: hits per reader; exit 1 on an UNKNOWN hit
//       --allow-frida                                   the sampler job: a read by frida-agent counts as known (our own tool)
//   node tools/frame-readers.ts --self-check            fixtures
// A frame handle = the TLS key of the ThreadContext, the current-state and records globals, the record array, the host's two
// frame-state fields. Sites that only match [reg+0x13C/0x140/0x144] ("state") are offset coincidences of unrelated structs
// unless a handle reaches them, so they are counted, not classified [inferred, docs/537 §2].
import assert from 'node:assert/strict';
import { readFileSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..');
const OUT = join(ROOT, 'docs', 'data', 'frame-readers.csv');

type Cls = { reader: string; when: string; evidence: 'verified' | 'inferred' };
const hostTick: Cls = { reader: 'script host: frame state around a script tick', when: 'sim thread between script calls, never inside compiled code', evidence: 'verified' };
const offset: Cls = { reader: 'not a frame reader: the same offset of a stack local', when: 'never', evidence: 'verified' };
const other: Cls = { reader: 'not a frame reader: address of another object\'s field, no load', when: 'never', evidence: 'inferred' };
// by function entry, or by site address where Ghidra has no function there
const CLASSES: Record<string, Cls> = {
  '00b2e280': offset, '00be13d0': offset, '00d6cc30': other, '00f72080': other,
  '00d70e90': hostTick, '00d70f6a': hostTick, '00d761eb': hostTick, '00d76200': hostTick, '00d7621f': hostTick,
  '00d72620': hostTick, '00d747c0': hostTick, '00d74800': hostTick, '00d7d2f0': hostTick, '00d7e010': hostTick,
  '00d7f000': hostTick, '00d7f8b0': hostTick, '00d7fc60': hostTick, '00d7fe80': hostTick, '00d80bc0': hostTick,
  '00e52540': { reader: 'stack walk hook, walks ThreadContext+8', when: 'an EA or mod call: only after a frame write', evidence: 'verified' },
  '00e52b60': { reader: 'transform helper, reads the domain of ThreadContext', when: 'EA transform, not reachable from a lazy body', evidence: 'verified' },
  '00e52b90': { reader: 'temporary ThreadContext for a call into Mono', when: 'native-to-managed entry, not reachable from a lazy body', evidence: 'verified' },
  '00e53130': { reader: 'interruption raise helper', when: 'tests a flag only, no frame read', evidence: 'verified' },
  '00e53170': { reader: 'interruption raise helper', when: 'tests a flag only, no frame read', evidence: 'verified' },
  '00e531a0': { reader: 'interruption raise helper', when: 'tests a flag only, no frame read', evidence: 'verified' },
  '00e531d0': { reader: 'frame count, walks ThreadContext+8', when: 'an EA or mod call: only after a frame write', evidence: 'verified' },
  '00e53741': { reader: 'native raise', when: 'an EA handler: only after a frame write', evidence: 'verified' },
  '00e534e0': { reader: 'records by depth (vtable caller 0xD71CC0 reads rec[depth-k])', when: 'script host, sim tick; depth and records are EA\'s between calls', evidence: 'verified' },
  '00e53eb0': { reader: 'ves_exec: the owner of the records', when: 'EA handlers: only after a frame write, or the exit that writes it', evidence: 'verified' },
  '00e59fd0': { reader: 'task entry, sets up its own ThreadContext', when: 'start of a script task', evidence: 'verified' },
  '00e5a680': { reader: 'runtime init of the TLS key', when: 'startup', evidence: 'verified' },
};

const csvCell = (s: string) => /[",\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s;
export function census(tsv: string, classes = CLASSES) {
  const rows = new Map<string, { sites: number; handles: Set<string> }>(), unknown: string[] = [];
  let stateOnly = 0;
  for (const line of tsv.split(/\r?\n/).slice(1)) {
    const [addr, func, kind, what, rw] = line.split('\t');
    if (!addr) continue;
    if (kind === 'state') { stateOnly++; continue; }
    const key = func === '-' ? addr : func;
    if (!classes[key]) { if (!unknown.includes(key)) unknown.push(key); continue; }
    const r = rows.get(key) ?? { sites: 0, handles: new Set<string>() };
    r.sites++, r.handles.add(`${kind} ${what}${rw ? ' ' + rw : ''}`), rows.set(key, r);
  }
  const csv = ['address,sites,handles,reader,when,evidence', ...[...rows.keys()].sort().map(k => {
    const c = classes[k], r = rows.get(k)!;
    return [`0x${k.replace(/^0+/, '').toUpperCase()}`, r.sites, csvCell([...r.handles].sort().join('; ')), csvCell(c.reader), csvCell(c.when), c.evidence].join(',');
  })].join('\n') + '\n';
  return { csv, unknown, handleSites: [...rows.values()].reduce((a, r) => a + r.sites, 0), functions: rows.size, stateOnly };
}

const HIT = /jit trap: (UNKNOWN|known) hit at eip ([0-9A-F]{8}) \(([^,]*), ([^)]*)\), thread (\d+)( \(sim\))?, watched ([0-9A-F]{8}), window (\d)/;
export function readLog(text: string, allowFrida = false) {
  const seen = new Map<string, number>();
  let unknown = 0, windowHits = 0;
  for (const line of text.split(/\r?\n/)) {
    const m = HIT.exec(line);
    if (!m) continue;
    const own = allowFrida && /frida/i.test(m[3]) && !m[6];
    const bad = m[1] === 'UNKNOWN' && !own;
    if (bad) unknown++;
    if (m[8] === '1') windowHits++;
    const key = `${bad ? 'UNKNOWN' : 'known'} ${m[2]} ${m[3]} (${m[4]})${m[6] ?? ''}`;
    seen.set(key, (seen.get(key) ?? 0) + 1);
  }
  return { unknown, windowHits, seen };
}

function selfCheck() {
  const tsv = 'addr\tfunc\tkind\twhat\trw\tinstruction\n00403173\t00403030\tstate\t+0x140\t\tx\n00e52540\t-\tabs\t0x11F4160\t\tx\n00e53ec0\t00e53eb0\trecord\t-19572\tR\tx\n00e53ed0\t00e53eb0\tabs\t0x11F4170\t\tx\n';
  const c = census(tsv);
  assert.deepEqual(c.unknown, []);
  assert.equal(c.handleSites, 3), assert.equal(c.functions, 2), assert.equal(c.stateOnly, 1);
  assert.ok(c.csv.includes('0xE52540,1,abs 0x11F4160,'), c.csv);
  assert.ok(c.csv.includes(',abs 0x11F4170; record -19572 R,'), c.csv);
  assert.ok(!c.csv.includes('\tx'), 'no instruction text');
  assert.deepEqual(census('h\n00123456\t00123000\tabs\t0x11F4170\t\tx\n').unknown, ['00123000']);
  assert.deepEqual(census('h\n00123456\t-\tstate\t+0x13C\t\tx\n').unknown, []);
  const log = [
    '[t] jit trap: known hit at eip 00E52550 (TS3W.exe, stack walk), thread 7, watched 00001000, window 0; stack 1 2 3 4 5 6 7 8',
    '[t] jit trap: UNKNOWN hit at eip 7A000010 (frida-agent.dll, not in the census), thread 9, watched 00001000, window 0; stack 1 2 3 4 5 6 7 8',
    '[t] jit trap: UNKNOWN hit at eip 00E52550 (TS3W.exe, stack walk), thread 5 (sim), watched 00001000, window 1; stack 1 2 3 4 5 6 7 8',
    '[t] jit trap: 3 other threads watch the frame state (9 windows so far)'].join('\n');
  const a = readLog(log), b = readLog(log, true);
  assert.equal(a.unknown, 2), assert.equal(b.unknown, 1), assert.equal(a.windowHits, 1), assert.equal(a.seen.size, 3);
  // the frida allowance never covers the sim thread
  assert.equal(readLog('jit trap: UNKNOWN hit at eip 7A000010 (frida-agent.dll, not in the census), thread 5 (sim), watched 00001000, window 1; stack', true).unknown, 1);
  console.log('frame-readers: self-check ok');
}

const arg = process.argv.slice(2);
if (import.meta.filename === resolve(process.argv[1] ?? '')) {
  if (arg[0] === '--self-check') selfCheck();
  else if (arg[0] === '--census' && arg[1]) {
    const c = census(readFileSync(`${arg[1]}-sites.tsv`, 'utf8'));
    if (c.unknown.length) { console.log(`UNKNOWN frame-handle sites in: ${c.unknown.join(' ')}`); process.exit(1); }
    writeFileSync(OUT, c.csv);
    console.log(`frame readers: ${c.handleSites} handle sites in ${c.functions} places, 0 unknown; ${c.stateOnly} state-offset sites not classified -> docs/data/frame-readers.csv`);
  } else if (arg[0] === '--log' && arg[1]) {
    const r = readLog(readFileSync(arg[1], 'utf8'), arg.includes('--allow-frida'));
    for (const [k, n] of r.seen) console.log(`${String(n).padStart(6)}  ${k}`);
    console.log(`${r.unknown} unknown, ${r.windowHits} hits in a window`);
    process.exit(r.unknown ? 1 : 0);
  } else { console.log('usage: node tools/frame-readers.ts --census <prefix> | --log <ts3native.log> [--allow-frida] | --self-check'); process.exit(2); }
}
