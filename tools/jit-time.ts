// [MonoJit] Time=1 reader (docs/handoff/jit-paired-timing.md): the `jittime:` block of a verify session (game ts3native.log with
// Mode=2 + Time=1, or the host's monohost.exe.log with MONOHOST_JIT=verify + MONOHOST_JITTIME=1) as per-method and per-piece ms/s.
// Each `jittime: m` row sums, over the verified-equal calls of one compiled method, the TSC cycles of pass 1 (compiled) and pass 2
// (EA's interpreter, same call, same state). Both windows still hold one stub + JitEnter probe per verifier entry, and pass 2 enters
// the verifier once per interpreted instruction of a compiled method, so the reader subtracts --stub-cycles per entry (ent1, ent2).
// The per-entry cost moves with machine load (host 2026-11-14: 49 cycles quiet, 62 with a game test running; JitSum 64 against
// the bench's 64 x 16.2 ns interp at 2304 MHz TSC), so the default `auto` takes it from the block itself: the lowest interp cycles
// per entry over rows with >= 100 entries per pair (a loop: the handler work there is a few cycles, so this over-corrects slightly
// = a LOWER bound on the gain); the no-correction total is printed too (an upper bound). saved ns/call = (interp - jit) per pair;
// ms/s = calls/s x saved. calls = `calls` column (game: Count=1 call count, else the fresh calls the verifier saw; host: verified
// calls) over the block's ms. Rows with nested compiled entries in pass 1 (nest > 0) include their callees' gain: summed apart
// ("overlap"). Pieces: one row per exact tag set (combo), and per tag an even split (a method's ms/s / its tag count). Untagged = Base.
// guards = lazy-site guard sequences the method's compiled code ran (every call, not sampled); --guard-ns turns them into ms/s.
//   node tools/jit-time.ts <log> [--stub-cycles auto|<n>] [--top 20] [--block <n>] [--csv <prefix>] [--guard-ns <ns>]   (prefix-methods.csv, prefix-pieces.csv)
//   node tools/jit-time.ts --self-check
import { readFileSync, writeFileSync } from 'node:fs';

export const TAGS = ['Direct', 'Guard', 'Inline', 'Wide', 'RetTd', 'Clause', 'LazyBody', 'FastCopy'];
export type Head = { why: string; n: number; ms: number; khz: number; samples: number; dropped: number; wrapped: number; entries: number };
export type Row = { idx: number; tags: number; calls: number; n: number; jit: number; interp: number; nest: number; ent1: number; ent2: number; drop: number; guards: number; name: string };
export type Block = { head: Head; rows: Row[] };
// monojit-time.h JtReportTo format strings (game: no line prefix, host: same text)
const BEGIN = /^jittime: begin (\S+) #(\d+), (\d+) ms, tsc (\d+) kHz, (\d+) samples, (\d+) dropped \((\d+) wrapped\), (\d+) entries, \d+ rows, \d+ methods\s*$/;
const ROW = /^jittime: m (\d+) ([0-9A-Fa-f]+) (-?\d+) (\d+) (\d+) (\d+) (\d+) (\d+) (\d+) (\d+) (\d+) (.+?)\s*$/;
const END = /^jittime: end (\S+) #(\d+), (\d+) rows/;

export function parseBlocks(text: string): Block[] {
  const out: Block[] = [];
  let cur: Block | null = null;
  for (const line of text.split(/\r?\n/)) {
    let m = BEGIN.exec(line);
    if (m) { cur = { head: { why: m[1], n: +m[2], ms: +m[3], khz: +m[4], samples: +m[5], dropped: +m[6], wrapped: +m[7], entries: +m[8] }, rows: [] }; continue; }
    if (!cur) continue;
    if ((m = ROW.exec(line))) {
      cur.rows.push({ idx: +m[1], tags: parseInt(m[2], 16), calls: +m[3], n: +m[4], jit: +m[5], interp: +m[6], nest: +m[7], ent1: +m[8], ent2: +m[9], drop: +m[10], guards: +m[11], name: m[12] });
      continue;
    }
    if ((m = END.exec(line)) && +m[2] === cur.head.n) { out.push(cur); cur = null; }  // a block cut by a crash has no end line: not used
  }
  return out;
}
export const tagNames = (t: number) => TAGS.filter((_, i) => t >> i & 1);
export type Method = Row & { guardsS: number; callsS: number; jitNs: number; interpNs: number; savedNs: number; msS: number; stubShare: number; overlap: boolean };
// the stub cost per verifier entry from the block: min interp cycles per entry over loop-like rows (fallback: the quiet host value)
export function autoStub(b: Block): number {
  const r = b.rows.filter(x => x.n > 0 && x.ent2 >= 100 * x.n).map(x => x.interp / x.ent2);
  return r.length ? Math.floor(Math.min(...r)) : 49;
}
export function analyze(b: Block, stub: number): Method[] {
  const ns = (cyc: number) => cyc * 1e6 / b.head.khz, s = b.head.ms / 1000;
  return b.rows.filter(r => r.n > 0).map(r => {
    const jit = Math.max(0, r.jit - stub * r.ent1), interp = Math.max(0, r.interp - stub * r.ent2);
    const jitNs = ns(jit) / r.n, interpNs = ns(interp) / r.n, savedNs = interpNs - jitNs, callsS = Math.max(r.calls, r.n) / s;
    return { ...r, guardsS: r.guards / s, callsS, jitNs, interpNs, savedNs, msS: callsS * savedNs / 1e6, stubShare: r.interp ? stub * r.ent2 / r.interp : 0, overlap: r.nest > 0 };
  });
}
export type Piece = { piece: string; methods: number; msS: number; msSLeaf: number; msSOverlap: number };
export function pieces(ms: Method[]) {
  const combo = new Map<string, Piece>(), split = new Map<string, Piece>();
  const add = (m: Map<string, Piece>, k: string, x: Method, w: number) => {
    const p = m.get(k) ?? { piece: k, methods: 0, msS: 0, msSLeaf: 0, msSOverlap: 0 };
    p.methods++, p.msS += x.msS * w, x.overlap ? (p.msSOverlap += x.msS * w) : (p.msSLeaf += x.msS * w);
    m.set(k, p);
  };
  for (const x of ms) {
    const t = tagNames(x.tags);
    add(combo, t.length ? t.join('+') : 'Base', x, 1);
    for (const k of t.length ? t : ['Base']) add(split, k, x, 1 / Math.max(1, t.length));
  }
  const by = (m: Map<string, Piece>) => [...m.values()].sort((a, b) => b.msS - a.msS);
  return { combo: by(combo), split: by(split) };
}
const f = (x: number, d = 2) => x.toFixed(d);

function report(text: string, stubArg: string, top: number, which?: number, csv?: string, guardNs = 0) {
  const bs = parseBlocks(text);
  if (!bs.length) throw new Error('no complete `jittime: begin` .. `end` block (Time=1 with Mode=2, or MONOHOST_JITTIME=1 with MONOHOST_JIT=verify)');
  const b = which ? bs.find(x => x.head.n === which) : bs[bs.length - 1];
  if (!b) throw new Error(`no block #${which}`);
  const stub = stubArg === 'auto' ? autoStub(b) : +stubArg;
  const ms = analyze(b, stub), h = b.head, secs = h.ms / 1000;
  const gS = b.rows.reduce((a, r) => a + r.guards, 0) / secs;
  const leaf = ms.filter(m => !m.overlap).reduce((a, m) => a + m.msS, 0), over = ms.filter(m => m.overlap).reduce((a, m) => a + m.msS, 0);
  const raw = analyze(b, 0).reduce((a, m) => a + m.msS, 0);
  console.log(`block #${h.n} (${h.why}): ${(h.ms / 60000).toFixed(1)} min, TSC ${f(h.khz / 1000, 0)} MHz, ${h.samples} timed pairs, ${h.dropped} dropped (${h.wrapped} wrapped), ${ms.length} methods`);
  console.log(`saved ms/s (stub ${stub} cycles/entry${stubArg === 'auto' ? ', auto' : ''}): leaf rows ${f(leaf)}, overlap rows ${f(over)} (include nested compiled callees), no-stub-correction total ${f(raw)}`);
  console.log(`lazy-site guard sequences: ${f(gS, 0)}/s (all compiled methods${guardNs ? `; at ${guardNs} ns each = ${f(gS * guardNs / 1e6)} ms/s` : '; --guard-ns <host NoGuard delta per sequence> for ms/s'})`);
  if (h.wrapped) console.log('WARN: wrapped windows (a pass shorter than the verifier time inside it): timing bug, numbers not trusted');
  const shaky = ms.filter(m => m.stubShare > 0.8);
  if (shaky.length) console.log(`note: ${shaky.length} methods have > 80% of their interp pass in the stub correction (their saved ns is mostly the correction: check --stub-cycles)`);
  const { combo, split } = pieces(ms);
  console.log('\npiece (exact tag set)                      methods   ms/s   leaf  overlap');
  for (const p of combo) console.log(`${p.piece.padEnd(42)} ${String(p.methods).padStart(7)} ${f(p.msS).padStart(6)} ${f(p.msSLeaf).padStart(6)} ${f(p.msSOverlap).padStart(8)}`);
  console.log('\npiece (even split of each method over its tags)  ms/s   leaf  overlap');
  for (const p of split) console.log(`${p.piece.padEnd(48)} ${f(p.msS).padStart(6)} ${f(p.msSLeaf).padStart(6)} ${f(p.msSOverlap).padStart(8)}`);
  console.log(`\ntop ${top} methods by saved ms/s           calls/s   pairs   jit ns  interp ns  saved ns   ms/s  nest/pair  guards/s  tags`);
  for (const m of [...ms].sort((a, b) => b.msS - a.msS).slice(0, top))
    console.log(`${m.name.slice(0, 40).padEnd(40)} ${f(m.callsS, 0).padStart(8)} ${String(m.n).padStart(7)} ${f(m.jitNs, 1).padStart(8)} ${f(m.interpNs, 1).padStart(10)} ${f(m.savedNs, 1).padStart(9)} ${f(m.msS, 3).padStart(6)} ${f(m.nest / m.n, 1).padStart(10)} ${f(m.guardsS, 0).padStart(9)}  ${tagNames(m.tags).join('+') || 'Base'}`);
  if (csv) {
    const q = (s: string) => `"${s.replace(/"/g, '""')}"`;
    writeFileSync(`${csv}-methods.csv`, 'method,tags,calls_s,pairs,jit_ns,interp_ns,saved_ns,saved_ms_s,nest_per_pair,ent1_per_pair,ent2_per_pair,stub_share,overlap,guards_s,stub_cycles,block\n' +
      ms.map(m => [q(m.name), q(tagNames(m.tags).join('+') || 'Base'), f(m.callsS, 1), m.n, f(m.jitNs, 1), f(m.interpNs, 1), f(m.savedNs, 1), f(m.msS, 4), f(m.nest / m.n, 2), f(m.ent1 / m.n, 1), f(m.ent2 / m.n, 1), f(m.stubShare), m.overlap ? 1 : 0, f(m.guardsS, 1), stub, h.n].join(',')).join('\n') + '\n');
    writeFileSync(`${csv}-pieces.csv`, 'kind,piece,methods,ms_s,ms_s_leaf,ms_s_overlap,stub_cycles,block\n' +
      [...combo.map(p => ['combo', p] as const), ...split.map(p => ['split', p] as const)].map(([k, p]) => [k, q(p.piece), p.methods, f(p.msS, 4), f(p.msSLeaf, 4), f(p.msSOverlap, 4), stub, h.n].join(',')).join('\n') + '\n');
    console.log(`\nwrote ${csv}-methods.csv, ${csv}-pieces.csv`);
  }
}

function selfCheck() {
  const ok = (c: boolean, m: string) => { if (!c) { console.error(`FAIL ${m}`); process.exit(1); } };
  // real host lines (monohost.exe.log, MONOHOST_JITTIME=1, 2026-11-14), the block cut to three rows + a guards-only row; then a
  // block with no end line
  const log = [
    'jit: Time=1 (MONOHOST_JITTIME, verify mode only)',
    'jittime: begin end #1, 66106 ms, tsc 2304001 kHz, 2350998 samples, 0 dropped (0 wrapped), 64051657 entries, 917 rows, 1274 methods',
    'jittime: m 1236 40 32 32 33076 1661060 0 32 26976 0 0 MonoHostTest.HostTest.JitSum',
    'jittime: m 1246 41 32 32 914112 2733284 2048 8224 41280 0 2048 MonoHostTest.HostTest.JitLzAlloc',
    'jittime: m 1243 43 32 32 164242 2359274 2 38 41312 0 2048 MonoHostTest.HostTest.JitLzVirt',
    'jittime: m 7 1 0 0 0 0 0 0 0 0 500 A.B',
    'jit verify   MonoHostTest.HostTest.JitSum                                           ok 32 mismatch 0 unverified 0, inside a verified caller 0',
    'jittime: end end #1, 4 rows',
    'jittime: begin interval #2, 600000 ms, tsc 2304001 kHz, 9 samples, 0 dropped (0 wrapped), 9 entries, 1 rows, 9 methods\r',
    'jittime: m 3 0 9 9 900 900 0 9 9 0 0 X.Y',
  ].join('\n');
  const bs = parseBlocks(log);
  ok(bs.length === 1 && bs[0].rows.length === 4 && bs[0].head.khz === 2304001 && bs[0].head.ms === 66106, 'one complete block, the cut one dropped');
  ok(!ROW.test('jit verify   MonoHostTest.HostTest.JitSum                                           ok 32 mismatch 0 unverified 0, inside a verified caller 0') && !ROW.test('jitlazy leaf MonoHostTest.HostTest.JitSum') && !BEGIN.test('jitcount: begin exit #1'), 'regex non-matches');
  ok(!ROW.test('jittime: m 1236 40 32 32 27324 1395358 0 32 26976 0 MonoHostTest.HostTest.JitSum'), 'the old 10-number row (no guards column) does not match');
  ok(END.test('jittime: end save #3, 0 rows (cut at JT_LINES)'), 'end line with the cut note');
  const r = bs[0].rows[0];
  ok(r.name === 'MonoHostTest.HostTest.JitSum' && r.tags === 0x40 && r.ent2 === 26976 && r.guards === 0 && tagNames(bs[0].rows[1].tags).join() === 'Direct,LazyBody' && bs[0].rows[1].nest === 2048 && bs[0].rows[1].guards === 2048, 'row fields + tags');
  ok(autoStub(bs[0]) === 57, 'auto stub = floor(min interp per entry over loop rows) = JitLzVirt 57.1');
  const m = analyze(bs[0], 49);
  // JitSum: interp (1661060 - 49 * 26976) / 32 cycles = 4601.18 ns; jit (33076 - 49 * 32) / 32 cycles = 427.35 ns
  ok(m.length === 3 && Math.abs(m[0].interpNs - 4601.18) < 0.05 && Math.abs(m[0].jitNs - 427.35) < 0.05 && !m[0].overlap && m[1].overlap, 'stub correction, ns, overlap, guards-only row left out');
  ok(Math.abs(m[0].callsS - 32 / 66.106) < 1e-9 && Math.abs(m[0].msS - m[0].callsS * m[0].savedNs / 1e6) < 1e-12 && Math.abs(m[1].guardsS - 2048 / 66.106) < 1e-9, 'ms/s, guards/s');
  const p = pieces(m);
  ok(p.combo.length === 3 && p.split.find(x => x.piece === 'Direct')!.msS === m[1].msS / 2 + m[2].msS / 3 && p.split.find(x => x.piece === 'Guard')!.msS === m[2].msS / 3, 'pieces: combo + even split');
  console.log('jit-time self-check OK');
}

if (import.meta.main) {
  const args = process.argv.slice(2), arg = (k: string) => { const i = args.indexOf(k); return i >= 0 ? args[i + 1] : undefined; };
  if (args.includes('--self-check')) selfCheck();
  else {
    const file = args.find(a => !a.startsWith('--') && !['--stub-cycles', '--top', '--block', '--csv', '--guard-ns'].includes(args[args.indexOf(a) - 1]));
    if (!file) throw new Error('usage: node tools/jit-time.ts <ts3native.log | monohost.exe.log> [--stub-cycles auto|<n>] [--top 20] [--block <n>] [--csv <prefix>] [--guard-ns <ns>] | --self-check');
    report(readFileSync(file, 'latin1'), arg('--stub-cycles') ?? 'auto', +(arg('--top') ?? '20'), arg('--block') ? +arg('--block')! : undefined, arg('--csv'), +(arg('--guard-ns') ?? '0'));
  }
}
