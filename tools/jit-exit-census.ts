// Round 6 B1 (docs/207 §11): exit census of the monohost method compiler. Which opcodes still leave compiled code through
// an exit to EA's handler, how often, and how much of each method's self time sits behind them.
//   node tools/jit-exit-census.ts --prep <out.txt> [--top 60]   MONOHOST_JITPREP list: the top never-yielding self-time frames of
//                                                               docs/data/compile-candidates.csv (non-generic, resolved through
//                                                               game-copy/analysis/callgraph/methods.csv)
//   node tools/jit-exit-census.ts --census <monohost.exe.log> [--out docs/data/jit-exit-census.csv]   CSV + top-10 templates
//   node tools/jit-exit-census.ts --self-check
// Run (count mode): MONOHOST_JIT=on MONOHOST_JITCOUNT=1 MONOHOST_MINTCALL=fast MONOHOST_JITPREP=<prep> MONOHOST_JITLIST=<kJitList +
// the prep methods> monohost.exe ... selftest ...; the log then has "jitcalls m calls positions-run positions name" and
// "jitexit m op name op|slow n" for every compiled method and exit site.
// Basis: a method the bench ran (>= MIN_CALLS calls) uses its measured exits; a prep-only method (called once with null /
// zero arguments) uses its static exit sites. est_ms_s_behind = ms_s x the exits' share of the positions run (or of the
// positions, static) [inferred: every mint instruction costs the same; an exit costs more, so this understates exits].
import { readFileSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..');
const CANDIDATES = resolve(ROOT, 'docs/data/compile-candidates.csv');
const METHODS = resolve(ROOT, 'game-copy/analysis/callgraph/methods.csv');
const MIN_CALLS = 100;
const arg = (k: string) => { const i = process.argv.indexOf(k); return i > 0 ? process.argv[i + 1] : undefined; };

// minimal CSV: quoted fields with "" escapes
export function parseCsv(text: string): string[][] {
  const rows: string[][] = [];
  for (const line of text.split(/\r?\n/)) {
    if (!line) continue;
    const f: string[] = [];
    let cur = '', q = false;
    for (let i = 0; i < line.length; i++) {
      const c = line[i];
      if (q) { if (c === '"' && line[i + 1] === '"') { cur += '"'; i++; } else if (c === '"') q = false; else cur += c; }
      else if (c === '"') q = true; else if (c === ',') { f.push(cur); cur = ''; } else cur += c;
    }
    f.push(cur);
    rows.push(f);
  }
  return rows;
}
const csvField = (s: string) => /[",]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s;

// self ms/s per frame: mean over the three worlds (a world that does not list the frame counts 0), never-yielding only
export function selfTimes(text: string): Map<string, { ms: number; asm: string }> {
  const [h, ...rows] = parseCsv(text);
  const ix = (n: string) => h.indexOf(n);
  const worlds = new Set(rows.map(r => r[ix('world')]));
  const m = new Map<string, { ms: number; asm: string }>();
  for (const r of rows) {
    if (r[ix('kind')] !== 'self' || r[ix('yield_path')]) continue;
    const e = m.get(r[ix('frame')]) ?? { ms: 0, asm: r[ix('assembly')] };
    e.ms += +r[ix('ms_s')] / worlds.size;
    m.set(r[ix('frame')], e);
  }
  return m;
}

// "A.B.Class.Method" / "A.B.Class..ctor" -> [class, method]
export const splitFrame = (f: string): [string, string] => {
  const i = f.indexOf('..');
  if (i > 0) return [f.slice(0, i), f.slice(i + 1)];
  const j = f.lastIndexOf('.');
  return [f.slice(0, j), f.slice(j + 1)];
};
// parameter count of "(int,Dictionary<int,string>,bool)"
export const paramCount = (p: string) => {
  if (!p.trim()) return 0;
  let n = 1, depth = 0;
  for (const c of p) { if (c === '<' || c === '[') depth++; else if (c === '>' || c === ']') depth--; else if (c === ',' && !depth) n++; }
  return n;
};

function prep(out: string, top: number) {
  const times = [...selfTimes(readFileSync(CANDIDATES, 'utf8'))].sort((a, b) => b[1].ms - a[1].ms).slice(0, top);
  const index = new Map<string, { asm: string; n: number }[]>();
  for (const [sig, asm] of parseCsv(readFileSync(METHODS, 'utf8')).slice(1)) {
    const m = /^(.*)::([^(]+)\((.*)\)$/.exec(sig);
    if (!m) continue;
    const k = `${m[1].replace(/\//g, '.')}.${m[2]}`;
    (index.get(k) ?? index.set(k, []).get(k)!).push({ asm, n: paramCount(m[3]) });
  }
  const lines: string[] = [], skipped: string[] = [];
  for (const [frame] of times) {
    if (frame.includes('`')) { skipped.push(`${frame} (generic: the bench runs its instances)`); continue; }
    const hits = index.get(frame);
    if (!hits) { skipped.push(`${frame} (not in methods.csv)`); continue; }
    const [cls, meth] = splitFrame(frame), dot = cls.lastIndexOf('.');
    for (const h of new Map(hits.map(x => [`${x.asm}|${x.n}`, x])).values())
      lines.push(`${h.asm}.dll|${cls.slice(0, dot)}|${cls.slice(dot + 1)}|${meth}|${h.n}`);
  }
  writeFileSync(out, lines.join('\n') + '\n');
  console.log(`prep: ${lines.length} methods from the top ${times.length} frames -> ${out}`);
  for (const s of skipped) console.log(`  skipped ${s}`);
}

// opcode -> template effort class [inferred from what P0's templates do today, docs/207 §8]
export function effort(op: string): string {
  const o = op.replace(/ slow path$/, '');
  if (/^sim_/.test(o)) return 'n/a: yield (P1)';
  if (/^(call|callvirt|con\.|calli|jmp|callint|callrun|vcall)/.test(o)) return 'L: compiled-to-compiled calls (B3)';
  if (/^(ret|mono_retobj)/.test(o)) return 'L: frame teardown (B3)';
  if (/(\.r4|\.r8|\.r\.un|^conv\.r|ckfinite)/.test(o)) return 'L: x87-exact float (B2)';
  if (/^(throw|rethrow|leave|endfinally|endfilter)/.test(o)) return 'keep exit (cold)';
  if (/^(ldsfld|stsfld|ldsflda|ldstr|switch|ldtoken)/.test(o)) return 'S: constant or table at compile time';
  if (/^(newobj|newarr|mono_newobj|box)/.test(o)) return 'M: allocation helper';
  if (/^(unbox|castclass|isinst)/.test(o)) return 'M: class check with cache';
  if (/(\.vt$|^vtresult|ldobj|stobj|cpobj|initobj)/.test(o)) return 'M: value-type copy';
  if (/^(ldelem|stelem|ldelema|ldlen)/.test(o)) return 'M: bounds and type checks';
  if (/^super/.test(o)) return 'M: expand to the base templates';
  return 'M';
}

type Row = { method: string; asm: string; ms: number | undefined; op: string; perCall: number | undefined; est: number | undefined; basis: string };
export function census(log: string, times: Map<string, { ms: number; asm: string }>): Row[] {
  const meth = new Map<number, { calls: number; run: number; pos: number; name: string }>();
  const sites: { m: number; op: string; n: number }[] = [];
  for (const l of log.split(/\r?\n/)) {
    let x = /^jitcalls (\d+) (\d+) (\d+) (\d+) (.+)$/.exec(l);
    if (x) { meth.set(+x[1], { calls: +x[2], run: +x[3], pos: +x[4], name: x[5].trim() }); continue; }
    x = /^jitexit (\d+) [0-9A-F]+ (\S+) (op|slow) (\d+)$/.exec(l);
    if (x) sites.push({ m: +x[1], op: x[3] === 'slow' ? `${x[2]} slow path` : x[2], n: +x[4] });
  }
  // aggregate by method name (generic instances share one) and opcode
  const agg = new Map<string, { calls: number; run: number; pos: number; ops: Map<string, { sites: number; n: number }> }>();
  for (const [i, m] of meth) {
    if (m.name.startsWith('MonoHostTest.')) continue;  // corpus kernels and bench drivers, not game code
    const a = agg.get(m.name) ?? agg.set(m.name, { calls: 0, run: 0, pos: 0, ops: new Map() }).get(m.name)!;
    a.calls += m.calls; a.run += m.run; a.pos += m.pos;
    for (const s of sites.filter(s => s.m === i)) {
      const o = a.ops.get(s.op) ?? a.ops.set(s.op, { sites: 0, n: 0 }).get(s.op)!;
      o.sites++; o.n += s.n;
    }
  }
  const rows: Row[] = [];
  for (const [name, a] of agg) {
    const t = times.get(name), ran = a.calls >= MIN_CALLS && a.run > 0;
    for (const [op, o] of a.ops) {
      // static: a slow path (null check, overflow, ...) is not taken on a normal call, so it gets no share
      const share = ran ? o.n / a.run : a.pos && !op.endsWith(' slow path') ? o.sites / a.pos : 0;
      rows.push({ method: name, asm: t?.asm ?? '', ms: t?.ms, op, perCall: ran ? o.n / a.calls : undefined,
        est: t ? t.ms * share : undefined, basis: ran ? `run (${a.calls} calls)` : `static (${o.sites} of ${a.pos} positions)` });
    }
  }
  return rows.sort((p, q) => (q.est ?? -1) - (p.est ?? -1) || p.method.localeCompare(q.method));
}

export function templates(rows: Row[]) {
  const by = new Map<string, { est: number; methods: number }>();
  for (const r of rows) {
    if (r.est === undefined) continue;
    const t = by.get(r.op) ?? by.set(r.op, { est: 0, methods: 0 }).get(r.op)!;
    t.est += r.est; t.methods++;
  }
  return [...by].sort((a, b) => b[1].est - a[1].est).slice(0, 10).map(([op, t]) => ({ op, ...t, effort: effort(op) }));
}

// B3 call edges (docs/207 §12): "jitedge caller site op n miss callee flags native words clauses name" per compiled call /
// callvirt site. Callee traits come from its own exit sites when it is compiled: throws = any slow path or throw exit,
// allocates = newobj / newarr / box exits, calls = call-like exits (a frame is needed there), native = EA native body.
type Edge = { caller: string; callee: string; kind: string; calls: number; miss: number; compiled: boolean; words: number;
  pos: number | undefined; calleeCalls: number; outPerCall: number | undefined; throws: boolean; allocates: boolean; native: boolean;
  clauses: boolean; dispatch: string; frame: string; inline: boolean };
const CALLISH = /^(call|callvirt|con\.|calli|newobj|vcall|callint|callrun|jmp)/;
export function edges(log: string): Edge[] {
  const meth = new Map<number, { calls: number; pos: number; name: string }>();
  const sites = new Map<number, { op: string; slow: boolean; n: number }[]>();
  const raw: { caller: number; op: string; n: number; miss: number; callee: number; flags: number; native: number; words: number; clauses: number; name: string }[] = [];
  for (const l of log.split(/\r?\n/)) {
    let x = /^jitcalls (\d+) (\d+) (\d+) (\d+) (.+)$/.exec(l);
    if (x) { meth.set(+x[1], { calls: +x[2], pos: +x[4], name: x[5].trim() }); continue; }
    x = /^jitexit (\d+) [0-9A-F]+ (\S+) (op|slow) (\d+)$/.exec(l);
    if (x) { (sites.get(+x[1]) ?? sites.set(+x[1], []).get(+x[1])!).push({ op: x[2], slow: x[3] === 'slow', n: +x[4] }); continue; }
    x = /^jitedge (\d+) \d+ ([0-9A-F]+) (\d+) (\d+) (-?\d+) ([0-9A-F]+) ([0-9A-F]+) (\d+) (\d+) (.+)$/.exec(l);
    if (x) raw.push({ caller: +x[1], op: x[2] === 'EA' ? 'call' : 'callvirt', n: +x[3], miss: +x[4], callee: +x[5], flags: parseInt(x[6], 16),
      native: parseInt(x[7], 16), words: +x[8], clauses: +x[9], name: x[10].trim() });
  }
  const agg = new Map<string, Edge>();
  for (const r of raw) {
    const caller = meth.get(r.caller)?.name ?? `#${r.caller}`;
    if (caller.startsWith('MonoHostTest.')) continue;  // corpus kernels and bench drivers
    const cm = r.callee >= 0 ? meth.get(r.callee) : undefined, cs = r.callee >= 0 ? sites.get(r.callee) ?? [] : [];
    const out = cs.filter(s => !s.slow && CALLISH.test(s.op));
    const key = `${caller}|${r.name}|${r.op}`;
    const e = agg.get(key) ?? agg.set(key, { caller, callee: r.name, kind: r.op, calls: 0, miss: 0, compiled: r.callee >= 0, words: r.words,
      pos: cm?.pos, calleeCalls: 0, outPerCall: undefined, throws: false, allocates: false, native: r.native !== 0, clauses: r.clauses !== 0,
      dispatch: '', frame: '', inline: false }).get(key)!;
    e.calls += r.n; e.miss += r.miss;
    if (cm) {
      e.calleeCalls = Math.max(e.calleeCalls, cm.calls);
      if (cm.calls >= MIN_CALLS) e.outPerCall = out.reduce((a, s) => a + s.n, 0) / cm.calls;
      e.throws ||= cs.some(s => s.slow || /^(throw|rethrow)/.test(s.op));
      e.allocates ||= cs.some(s => /^(newobj|newarr|box|mono_newobj)/.test(s.op));
    }
  }
  for (const e of agg.values()) {
    // docs/207 §12: dispatch = how the call site finds its callee; frame = when a lazy frame must be written (at the callee's
    // first exit; a leaf takes none on a normal call). Monomorphic = under 1% inline-cache misses.
    const poly = e.kind === 'callvirt' && e.miss >= 0.01 * e.calls;
    e.dispatch = !e.compiled ? 'none: callee not compiled' : e.native ? 'none: EA native body' : e.clauses ? 'none: callee has clauses'
      : e.kind === 'call' ? 'direct' : poly ? 'polymorphic' : 'class guard';
    e.frame = !e.dispatch.startsWith('none') ? e.outPerCall === undefined ? 'unknown (callee not run)'
      : e.outPerCall < 0.01 ? 'never (leaf)' : `at the first inner call (${e.outPerCall.toFixed(2)} per call)` : '';
    e.inline = !e.dispatch.startsWith('none') && !poly && (e.pos ?? 99) <= 12 && !e.throws && !e.allocates && e.frame === 'never (leaf)';
  }
  return [...agg.values()].sort((a, b) => b.calls - a.calls || a.caller.localeCompare(b.caller));
}

function selfCheck() {
  let k = 0;
  const ok = (c: boolean, what: string) => { k++; if (!c) { console.log(`FAIL ${what}`); process.exitCode = 1; } };
  ok(paramCount('') === 0 && paramCount('int') === 1 && paramCount('int,Dictionary<int,string>,bool[,]') === 3, 'parameter count');
  ok(splitFrame('A.B.C.get_X').join() === 'A.B.C,get_X' && splitFrame('A.Enumerator..ctor').join() === 'A.Enumerator,.ctor', 'frame split');
  ok(parseCsv('a,"b,""c""",d')[0].join('|') === 'a|b,"c"|d', 'csv quotes');
  const cand = 'world,kind,frame,class,samples,share_of_mono,ms_s,assembly,overloads,il_bytes,il_features,yield_path\n'
    + 'bp,self,N.K.M,N,1,0,3,A,1,1,,\nip,self,N.K.M,N,1,0,6,A,1,1,,\nsv,self,N.K.Y,N,1,0,9,A,1,1,,Sleep\nsv,root,N.K.M,N,1,0,99,A,1,1,,\n';
  const t = selfTimes(cand);
  ok(t.size === 1 && t.get('N.K.M')!.ms === 3, 'self ms/s: mean over worlds, yielding and root rows skipped');
  const log = 'jitcalls 0 200 1000 10 N.K.M\njitexit 0 4F callvirt op 300\njitexit 0 4F callvirt op 100\njitexit 0 59 ret op 200\n'
    + 'jitcalls 1 1 3 8 N.K.P\njitexit 1 4F callvirt op 0\njitexit 1 46 ldfld.o slow 0\njitcalls 2 50 9 9 MonoHostTest.JitK.Add\njitexit 2 4F callvirt op 9\n';
  const r = census(log, new Map([['N.K.M', { ms: 3, asm: 'A' }], ['N.K.P', { ms: 8, asm: 'A' }]]));
  const cv = r.find(x => x.method === 'N.K.M' && x.op === 'callvirt')!, p = r.find(x => x.method === 'N.K.P' && x.op === 'callvirt')!;
  const sl = r.find(x => x.op === 'ldfld.o slow path')!;
  ok(r.length === 4 && cv.perCall === 2 && Math.abs(cv.est! - 1.2) < 1e-9 && p.perCall === undefined && p.est === 1 && p.basis.startsWith('static') && sl.est === 0,
    'census: run basis (exits / positions run), static basis (sites / positions, slow paths 0), bench drivers skipped');
  ok(templates(r)[0].op === 'callvirt' && Math.abs(templates(r)[0].est - 2.2) < 1e-9 && effort('callvirt').startsWith('L') && effort('ldsfld.i4').startsWith('S'),
    'templates ranked by summed ms/s');
  const el = 'jitcalls 0 500 9000 40 N.A.Run\njitcalls 1 400 2000 9 N.B.Leaf\njitexit 1 46 ldfld.o slow 0\njitexit 1 6 ret op 400\n'
    + 'jitcalls 2 300 900 20 N.C.Mid\njitexit 2 EA call op 300\njitedge 0 0 EA 400 0 1 300 0 12 0 N.B.Leaf\njitedge 0 1 EC 250 5 2 300 0 30 0 N.C.Mid\n'
    + 'jitedge 0 2 EC 50 0 2 300 0 30 0 N.C.Mid\njitedge 0 3 EA 7 0 -1 300 1 0 0 N.D.Ext\n';
  const ed = edges(el), leaf = ed.find(x => x.callee === 'N.B.Leaf')!, mid = ed.find(x => x.callee === 'N.C.Mid')!;
  ok(ed.length === 3 && leaf.dispatch === 'direct' && leaf.frame === 'never (leaf)' && leaf.throws && !leaf.inline && mid.calls === 300 && mid.miss === 5
    && mid.dispatch === 'polymorphic' && mid.frame.startsWith('at the first inner call (1.00') && ed.find(x => x.callee === 'N.D.Ext')!.dispatch === 'none: callee not compiled',
    'edges: sites merged per caller / callee / kind, dispatch and frame rules');
  console.log(`jit-exit-census self-check: ${k} checks${process.exitCode ? ', FAILED' : ', ok'}`);
}

if (import.meta.main) {
  if (process.argv.includes('--self-check')) selfCheck();
  else if (arg('--prep')) prep(arg('--prep')!, +(arg('--top') ?? 60));
  else if (arg('--census')) {
    const rows = census(readFileSync(arg('--census')!, 'utf8'), selfTimes(readFileSync(CANDIDATES, 'utf8')));
    const out = arg('--out') ?? resolve(ROOT, 'docs/data/jit-exit-census.csv');
    const f = (x: number | undefined, d: number) => x === undefined ? '' : x.toFixed(d);
    writeFileSync(out, 'method,assembly,ms_s,opcode,exits_per_call,est_ms_s_behind,template_effort,basis\n'
      + rows.map(r => [r.method, r.asm, f(r.ms, 2), r.op, f(r.perCall, 3), f(r.est, 3), effort(r.op), r.basis].map(csvField).join(',')).join('\n') + '\n');
    console.log(`census: ${rows.length} rows, ${new Set(rows.map(r => r.method)).size} methods -> ${out}`);
    console.log('| # | opcode | est. ms/s behind | methods | effort |\n|---|---|---|---|---|');
    templates(rows).forEach((t, i) => console.log(`| ${i + 1} | \`${t.op}\` | ${t.est.toFixed(2)} | ${t.methods} | ${t.effort} |`));
  } else if (arg('--edges')) {
    const ed = edges(readFileSync(arg('--edges')!, 'utf8'));
    const out = arg('--out') ?? resolve(ROOT, 'docs/data/jit-call-edges.csv');
    const b = (x: boolean) => x ? 'yes' : 'no';
    writeFileSync(out, 'caller,callee,kind,calls,cache_misses,callee_compiled,callee_positions,callee_calls,callee_inner_calls_per_call,callee_throws,callee_allocates,callee_native_body,dispatch,lazy_frame_written,inline_candidate\n'
      + ed.map(e => [e.caller, e.callee, e.kind, String(e.calls), String(e.miss), b(e.compiled), e.pos === undefined ? '' : String(e.pos), String(e.calleeCalls),
        e.outPerCall === undefined ? '' : e.outPerCall.toFixed(3), b(e.throws), b(e.allocates), b(e.native), e.dispatch, e.frame, b(e.inline)].map(csvField).join(',')).join('\n') + '\n');
    const tot = ed.reduce((a, e) => a + e.calls, 0), by = new Map<string, number>();
    for (const e of ed) { const k = `${e.dispatch}${e.frame ? `, frame ${e.frame.replace(/ \(.*\)$/, '')}` : ''}`; by.set(k, (by.get(k) ?? 0) + e.calls); }
    console.log(`edges: ${ed.length} (caller, callee, kind), ${tot} calls -> ${out}`);
    for (const [k, n] of [...by].sort((p, q) => q[1] - p[1])) console.log(`  ${(100 * n / tot).toFixed(1)}%  ${k}`);
  } else console.log('usage: --prep <out> [--top 60] | --census <monohost.exe.log> [--out <csv>] | --edges <monohost.exe.log> [--out <csv>] | --self-check');
}
