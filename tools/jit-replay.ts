// docs/537 §17: real game inputs for the monohost compiler corpus. [MonoJit] Capture=1 (native/ts3native/monojit-capture.h) samples the
// arguments, this and the reachable objects of compiled methods; this tool turns such files into the replay text that
// native/monohost/monohost-jitreplay.cpp rebuilds against the game DLLs in game-copy (same exe, same DLLs, so object layouts are the same).
// Each input then runs interpreted twice and compiled once, as JitCorpus does: result + exception + heap writes.
//   node tools/jit-replay.ts --capture <a.jitcap> [<b.jitcap> ...] --out <file>     merge, keep the last sample of each slot, write the replay text
//   node tools/jit-replay.ts --fixture --out <file>    the same from native/monohost/jit-replay-fixture.json (hand-made: cycle, string, array, null, ...)
//   node tools/jit-replay.ts --summary <a.jitcap>      per method: samples, objects, cuts, truncated; no field values, no string text
//   node tools/jit-replay.ts --run <replay file> [--list <names>] [--mode on|verify] [--lazy]   run monohost, report
//   node tools/jit-replay.ts --capture-corpus <out.jitcap> [--lazy] [--inline 1|2]   the game's capture code on the host: the corpus under capture,
//       for a round trip without the game; --lazy / --inline also sample lazy bodies and inlined sites (docs/537 §19)
//   --run also takes --switches VtOps=1,CallExit=0,NewVt=1 (Wide, Lazy, Inline=<0|1|2> too): sets those host env switches for the run
//       (MONOHOST_JITVTOPS ...), the rest comes from the environment; the set is printed in the summary line
//   node tools/jit-replay.ts --run <replay> --switches <set> --bisect   rerun halves of the ON switches until the failing one is named
//       (fail = differ, faulted, STOPPED or per-call MISMATCH); two halves that pass alone but fail together are reported as a joint set
//   node tools/jit-replay.ts --run <replay> --coverage [--switches ...]   host with MONOHOST_JITCOVER=1: per compiled method the calls and the
//       IL branch edges reached by at least one input; writes docs/data/results/<date>-jit-replay-coverage.csv, 0 calls = the rare-path list
//   node tools/jit-replay.ts --report <monohost.exe.log>
//   node tools/jit-replay.ts --self-check
// Captures hold game state (Sim names, save data): they stay in probe-out/ or game-copy/, never in git. Only the fixture is committed.
import { readFileSync, writeFileSync, mkdirSync, copyFileSync, existsSync, mkdtempSync } from 'node:fs';
import { resolve, join, dirname } from 'node:path';
import { spawn } from 'node:child_process';
import { tmpdir } from 'node:os';
import { createHash } from 'node:crypto';
import { jitList } from './jit-corpus.ts';
import { analyse, listRows, parseCover, type HostBody, type Row, type Result } from './jit-coverage.ts';

const ROOT = resolve(import.meta.dirname, '..');
const FIXTURE = resolve(ROOT, 'native/monohost/jit-replay-fixture.json');
const SCAN = 1, STRING = 2, ARRAY = 4, BLOB_MAX = 64;
const hex32 = (n: number) => (n >>> 0).toString(16);

export type Cls = { id: number; name: string; flags: number; is: number; es: number };
export type Meth = { id: number; token: number; np: number; cls: string; name: string };
export type Sfield = { id: number; cls: string; field: string };
export type Obj = { addr: number; cls: number; n: number; full: number; bytes: Buffer };
export type Sample = { meth: number; slot: number; via?: number; seen: number; self: number; args: [number, number][]; statics: [number, number][]; objs: Obj[]; cuts: { obj: number; off: number; cls: number }[]; cutLost: number; expect?: number };
export type Capture = { classes: Map<number, Cls>; methods: Map<number, Meth>; statics: Map<number, Sfield>; samples: Sample[]; tornTail: boolean };

export function parseCapture(b: Buffer): Capture {
  if (b.length < 8 || b.toString('latin1', 0, 4) !== 'JCAP') throw new Error('not a capture file (no JCAP)');
  const version = b.readUInt32LE(4);  // 2 (docs/537 §19) adds the path word after the slot: 0 JitEnter, 1 lazy body, 2 inlined site
  if (version !== 1 && version !== 2) throw new Error(`capture version ${version}, this tool reads 1 and 2`);
  const cap: Capture = { classes: new Map(), methods: new Map(), statics: new Map(), samples: [], tornTail: false };
  let at = 8;
  while (at + 8 <= b.length) {
    const tag = b.readUInt32LE(at), len = b.readUInt32LE(at + 4);
    if (at + 8 + len > b.length) { cap.tornTail = true; break; }
    let p = at + 8;
    const u32 = () => { const v = b.readUInt32LE(p); p += 4; return v; };
    const str = () => { const l = u32(), s = b.toString('latin1', p, p + l); p += (l + 3) & ~3; return s; };
    if (tag === 1) { const id = u32(); u32(); const is = u32(), es = u32(), flags = u32(); cap.classes.set(id, { id, name: str(), flags, is, es }); }
    else if (tag === 2) { const id = u32(), token = u32(), np = u32(), cls = str(); cap.methods.set(id, { id, token, np, cls, name: str() }); }
    else if (tag === 3) { const id = u32(), cls = str(); cap.statics.set(id, { id, cls, field: str() }); }
    else if (tag === 4) {
      const meth = u32(), slot = u32(), via = version >= 2 ? u32() : 0, seen = u32(), np = u32(), self = u32(), nObj = u32(), nCut = u32(), cutLost = u32(), nSt = u32();
      const args: [number, number][] = [], statics: [number, number][] = [], objs: Obj[] = [], cuts: Sample['cuts'] = [];
      for (let i = 0; i < np; i++) { const lo = u32(), hi = u32(); args.push([lo, hi]); }
      for (let i = 0; i < nSt; i++) { const s = u32(), o = u32(); statics.push([s, o]); }
      for (let i = 0; i < nObj; i++) { const addr = u32(), cls = u32(), n = u32(), full = u32(); objs.push({ addr, cls, n, full, bytes: Buffer.alloc(0) }); }
      for (let i = 0; i < nCut; i++) { const obj = u32(), off = u32(), cls = u32(); cuts.push({ obj, off, cls }); }
      for (const o of objs) { o.bytes = b.subarray(p, p + o.n); p += (o.n + 3) & ~3; }
      cap.samples.push({ meth, slot, via, seen, self, args, statics, objs, cuts, cutLost });
    }
    at += 8 + len;
  }
  return cap;
}
// the writer's format, for the self-check: a parse of what the reader must meet, without a game
export function encodeCapture(c: Capture): Buffer {
  const parts: Buffer[] = [Buffer.from('JCAP', 'latin1'), u32s(2)];
  const rec = (tag: number, body: Buffer) => parts.push(u32s(tag, body.length), body);
  const s = (t: string) => { const b = Buffer.from(t, 'latin1'); return Buffer.concat([u32s(b.length), b, Buffer.alloc((4 - (b.length & 3)) & 3)]); };
  for (const k of c.classes.values()) rec(1, Buffer.concat([u32s(k.id, 0, k.is, k.es, k.flags), s(k.name)]));
  for (const m of c.methods.values()) rec(2, Buffer.concat([u32s(m.id, m.token, m.np), s(m.cls), s(m.name)]));
  for (const x of c.statics.values()) rec(3, Buffer.concat([u32s(x.id), s(x.cls), s(x.field)]));
  for (const x of c.samples) rec(4, Buffer.concat([u32s(x.meth, x.slot, x.via ?? 0, x.seen, x.args.length, x.self, x.objs.length, x.cuts.length, x.cutLost, x.statics.length),
    ...x.args.map(a => u32s(...a)), ...x.statics.map(a => u32s(...a)), ...x.objs.map(o => u32s(o.addr, o.cls, o.n, o.full)), ...x.cuts.map(a => u32s(a.obj, a.off, a.cls)),
    ...x.objs.map(o => Buffer.concat([o.bytes, Buffer.alloc((4 - (o.n & 3)) & 3)]))]));
  return Buffer.concat(parts);
}
function u32s(...v: number[]) { const b = Buffer.alloc(4 * v.length); v.forEach((x, i) => b.writeUInt32LE(x >>> 0, 4 * i)); return b; }

// ---- replay text ----
type Totals = { inputs: number; superseded: number; objects: number; blobs: number; refs: number; cuts: number; cutLost: number; truncated: number; selfMissing: number };
export function toReplay(caps: Capture[]): { text: string; totals: Totals; methods: string[] } {
  const typeIds = new Map<string, number>(), meths = new Map<string, number>(), sfields = new Map<string, number>();
  const types: string[] = [], methLines: string[] = [], sfLines: string[] = [], methods: string[] = [];
  const typeId = (name: string) => { let i = typeIds.get(name); if (i === undefined) { typeIds.set(name, i = types.length); types.push(name); } return i; };
  const t: Totals = { inputs: 0, superseded: 0, objects: 0, blobs: 0, refs: 0, cuts: 0, cutLost: 0, truncated: 0, selfMissing: 0 };
  const kept = new Map<string, { c: Capture; s: Sample }>();
  for (const c of caps) for (const s of c.samples) {
    const k = `${methKey(c.methods.get(s.meth)!)}\0${s.slot}`;
    if (kept.has(k)) t.superseded++;
    kept.set(k, { c, s });
  }
  const body: string[] = [];
  for (const { c, s } of kept.values()) {
    const m = c.methods.get(s.meth)!, mk = methKey(m);
    let mid = meths.get(mk);
    if (mid === undefined) {
      meths.set(mk, mid = methLines.length);
      methLines.push(`meth ${mid} ${typeId(m.cls)} ${m.token} ${m.name}`);
      methods.push(m.name);
    }
    const objs = s.objs.map(o => ({ ...o }));
    const typed = new Map<number, number>(), blobAt = new Map<number, number>();
    objs.forEach((o, i) => { (o.cls ? typed : blobAt).set(o.addr, i); });
    // a word into the middle of a kept object: a copy of those bytes, as a value slot (an aliasing write to the object itself is not modelled)
    const slot = (w: number): [string, number] => {
      if (w < 0x10000) return ['n', -1];
      const oi = typed.get(w);
      if (oi !== undefined) return ['o', oi];
      const bi = blobAt.get(w);
      if (bi !== undefined) return ['b', bi];
      const inner = objs.findIndex(o => w > o.addr && w < o.addr + o.n);
      if (inner < 0) return ['n', -1];
      const from = objs[inner], off = w - from.addr, n = Math.min(from.n - off, BLOB_MAX);
      objs.push({ addr: w, cls: 0, n, full: n, bytes: from.bytes.subarray(off, off + n) });
      blobAt.set(w, objs.length - 1);
      return ['b', objs.length - 1];
    };
    const selfS = slot(s.self), args = s.args.map(a => slot(a[0]));
    if (s.self && selfS[0] === 'n') t.selfMissing++;
    const lines = [`input ${t.inputs} ${mid} ${s.expect === undefined ? 0 : 1} ${s.expect ?? 0}`];
    const cutAt = new Set(s.cuts.map(x => `${x.obj}:${x.off}`));
    const refs: string[] = [];
    objs.forEach((o, i) => {
      const k = o.cls ? c.classes.get(o.cls) : undefined;
      if (o.cls && !k) throw new Error(`sample names class ${o.cls}, no such record`);
      let len = 0;
      if (k && (k.flags & STRING)) len = o.bytes.readUInt32LE(4);
      else if (k && (k.flags & ARRAY)) len = o.bytes.readUInt32LE(8);
      if (o.full > o.n) t.truncated++;
      lines.push(`obj ${i} ${k ? typeId(k.name) : -1} ${len} ${o.full} ${o.n ? o.bytes.toString('hex') : '-'}`);
      if (k) t.objects++; else t.blobs++;
      if (k && !(k.flags & SCAN)) return;
      for (let off = k ? 4 : 0; off + 4 <= o.n; off += 4) {
        if (cutAt.has(`${i}:${off}`)) { refs.push(`cut ${i} ${off}`); t.cuts++; continue; }
        const to = typed.get(o.bytes.readUInt32LE(off));
        if (to !== undefined) { refs.push(`ref ${i} ${off} ${to}`); t.refs++; }
      }
    });
    t.cutLost += s.cutLost;
    lines.push(...refs, `self ${selfS[0]} ${selfS[1]}`);
    s.args.forEach((a, k) => lines.push(`arg ${k} ${hex32(a[0])} ${hex32(a[1])} ${args[k][0]} ${args[k][1]}`));
    for (const [sid, oi] of s.statics) {
      const f = c.statics.get(sid)!, fk = `${f.cls}\0${f.field}`;
      let id = sfields.get(fk);
      if (id === undefined) { sfields.set(fk, id = sfLines.length); sfLines.push(`sfield ${id} ${typeId(f.cls)} ${f.field}`); }
      const v = objs[oi].bytes, lo = v.length >= 4 ? v.readUInt32LE(0) : v.length >= 2 ? v.readUInt16LE(0) : v.readUInt8(0), hi = v.length >= 8 ? v.readUInt32LE(4) : 0;
      lines.push(`static ${id} ${hex32(lo)} ${hex32(hi)} b ${oi}`);
    }
    lines.push('end');
    body.push(lines.join('\n'));
    t.inputs++;
  }
  const head = ['jitreplay 1', ...types.map((n, i) => `type ${i} ${n}`), ...sfLines, ...methLines];
  return { text: [...head, ...body, ''].join('\n'), totals: t, methods };
}
const methKey = (m: Meth) => `${m.cls}\0${m.token}\0${m.name}`;

export function summary(c: Capture) {
  const rows = new Map<string, { samples: number; maxSeen: number; objs: number; cuts: number; lost: number; trunc: number; via: number[] }>();
  for (const s of c.samples) {
    const m = c.methods.get(s.meth)!, k = `${m.cls}.${m.name.split('.').pop()}/${m.np}`;
    const r = rows.get(k) ?? { samples: 0, maxSeen: 0, objs: 0, cuts: 0, lost: 0, trunc: 0, via: [0, 0, 0] };
    r.samples++; r.maxSeen = Math.max(r.maxSeen, s.seen); r.objs += s.objs.length; r.cuts += s.cuts.length; r.lost += s.cutLost; r.trunc += s.objs.filter(o => o.full > o.n).length;
    r.via[s.via ?? 0]++;
    rows.set(k, r);
  }
  return [...rows.entries()].sort((a, b) => b[1].maxSeen - a[1].maxSeen).map(([k, r]) => `${String(r.maxSeen).padStart(10)} calls  ${String(r.samples).padStart(4)} samples (enter ${r.via[0]}, lazy ${r.via[1]}, inline ${r.via[2]})  ${String(r.objs).padStart(6)} objects  ${r.cuts + r.lost} cuts  ${r.trunc} truncated  ${k}`);
}

// ---- fixture: hand-made, in the capture's own model. Words are numbers or "@label" (an object's address); strings and arrays are spelled out ----
type FixObj = { label: string; cls?: string; str?: string; ints?: number[]; words?: (number | string)[] };
type FixInput = { method: string; cls: string; self?: string; args: (number | string)[]; expect?: number; statics?: { cls: string; field: string; value: number }[]; cuts?: { obj: string; off: number }[]; objs: FixObj[] };
export function fixtureCapture(inputs: FixInput[]): Capture {
  const c: Capture = { classes: new Map(), methods: new Map(), statics: new Map(), samples: [], tornTail: false };
  const clsId = (name: string, flags: number) => {
    for (const k of c.classes.values()) if (k.name === name) return k.id;
    const id = c.classes.size + 1;
    c.classes.set(id, { id, name, flags, is: 0, es: 4 });
    return id;
  };
  inputs.forEach((f, inputNo) => {
    const addrs = new Map(f.objs.map((o, i) => [o.label, 0x10000000 + 0x10000 * inputNo + 0x100 * i]));
    const word = (w: number | string) => typeof w === 'number' ? w : addrs.get(w) ?? (() => { throw new Error(`no label ${w}`); })();
    const mid = c.methods.size;
    c.methods.set(mid, { id: mid, token: 0, np: f.args.length, cls: f.cls, name: f.method });
    const objs: Obj[] = f.objs.map(o => {
      let bytes: Buffer, cls: number;
      if (o.str !== undefined) { const u = Buffer.from(o.str, 'utf16le'); bytes = Buffer.concat([u32s(0, o.str.length), u]); cls = clsId('System.String', STRING); }
      else if (o.ints) { bytes = Buffer.concat([u32s(0, 0, o.ints.length, 0), u32s(...o.ints)]); cls = clsId('System.Int32[]', ARRAY); }
      else if (o.cls) { bytes = u32s(0, ...o.words!.map(word)); cls = clsId(o.cls, SCAN); }
      else { bytes = u32s(...o.words!.map(word)); cls = 0; }  // a blob: a value type in a frame
      return { addr: addrs.get(o.label)!, cls, n: bytes.length, full: bytes.length, bytes };
    });
    const statics: [number, number][] = (f.statics ?? []).map(st => {
      const sid = c.statics.size + 1;
      c.statics.set(sid, { id: sid, cls: st.cls, field: st.field });
      objs.push({ addr: 0x20000000 + sid * 16, cls: 0, n: 4, full: 4, bytes: u32s(st.value) });
      return [sid, objs.length - 1];
    });
    const cuts = (f.cuts ?? []).map(x => ({ obj: f.objs.findIndex(o => o.label === x.obj), off: x.off, cls: 0 }));
    c.samples.push({ meth: mid, slot: 0, seen: 1, self: f.self ? word(f.self) : 0, args: f.args.map(a => [word(a), 0]), statics, objs, cuts, cutLost: 0, expect: f.expect });
  });
  return c;
}

// ---- run and report ----
export function report(log: string) {
  const r = { inputs: 0, ran: 0, differ: 0, unstable: 0, skipped: 0, faulted: 0, stopped: false, verify: '', lines: [] as string[], methods: [] as string[], reasons: [] as string[] };
  for (const l of log.split(/\r?\n/)) {
    let m = /^jit replay: (\d+) inputs, (\d+) ran, (\d+) differ, (\d+) unstable, (\d+) skipped(?:, (\d+) faulted)?(, STOPPED on a fault)?; per-call verify: (.*)$/.exec(l);
    if (m) { r.inputs = +m[1]; r.ran = +m[2]; r.differ = +m[3]; r.unstable = +m[4]; r.skipped = +m[5]; r.faulted = +(m[6] ?? 0); r.stopped = !!m[7]; r.verify = m[8]; continue; }
    if (/^jit replay (MISMATCH|UNSTABLE|FAULT) /.test(l)) r.lines.push(l);
    else if (l.startsWith('jit replay method ')) r.methods.push(l.slice(18));
    else if (l.startsWith('jit replay reason ')) r.reasons.push(l.slice(18));
  }
  return r;
}
const fail = (s: string): never => { console.error(`jit-replay: ${s}`); process.exit(1); };
function gameCopy() {
  for (let d = ROOT; ; d = dirname(d)) {
    if (existsSync(join(d, 'game-copy', 'assemblies'))) return join(d, 'game-copy');
    if (dirname(d) === d) fail('no game-copy/assemblies above this checkout');
  }
}
const argv = process.argv.slice(2);
const opt = (n: string) => { const i = argv.indexOf(n); return i >= 0 ? argv[i + 1] : undefined; };
// private exe folder (build.cmd's note: no game DLLs or .asi next to it). The launcher exits while its child runs, so wait for the summary line.
async function runHost(replay: string | undefined, env: Record<string, string>, marker: RegExp, timeoutMs: number) {
  const gc = gameCopy(), dir = mkdtempSync(join(tmpdir(), 'jit-replay-')), bin = resolve(ROOT, 'native/monohost/out');
  for (const f of ['monohost.exe', 'MonoHostTest.dll']) copyFileSync(join(bin, f), join(dir, f));
  const log = join(dir, 'monohost.exe.log');
  const childEnv = { ...process.env, ...env, ...(replay ? { MONOHOST_JITREPLAY: replay } : {}) };
  spawn(join(dir, 'monohost.exe'), [join(gc, 'monohost', 'TS3W.exe'), join(gc, 'assemblies'), 'selftest', join(gc, 'monohost', 'hostdata.txt')], { stdio: 'ignore', env: childEnv, cwd: dir, detached: true }).unref();
  const end = Date.now() + timeoutMs;
  for (;;) {
    await new Promise(r => setTimeout(r, 1000));
    let t = '';
    try { t = readFileSync(log, 'utf8'); } catch { /* not there yet, or the child has it open this moment */ }
    if (marker.test(t)) return { log: t, path: log };
    if (/SELFTEST|fatal|unhandled/i.test(t.split('\n').slice(-3).join('\n'))) fail(`host ended without a summary, log ${log}`);
    if (Date.now() > end) fail(`no summary in ${timeoutMs / 1000} s, log ${log}`);
  }
}
// one replay over fresh host processes (an interpreter fault leaves old Mono's frame state unusable, so a fresh process continues at the next
// input via MONOHOST_JITREPLAY_FROM; rows add up by index); covers = each round's jit-cover.txt when cover is set
async function replayRun(replay: string, env: Record<string, string>, cover: boolean) {
  let from = 0, total: ReturnType<typeof report> | null = null;
  const covers: string[] = [];
  const rowRe = /^(.*): inputs (\d+), ran (\d+), differ (\d+), unstable (\d+), skipped (\d+), faulted (\d+), objects not rebuilt (\d+)$/;
  for (let round = 0; round < 600; round++) {
    const { log, path } = await runHost(replay, { ...env, MONOHOST_JITREPLAY_FROM: String(from) }, /^jit replay: \d+ inputs/m, 1800000);
    console.log(`log: ${path}`);
    if (cover) { try { covers.push(readFileSync(join(dirname(path), 'jit-cover.txt'), 'latin1')); } catch { fail(`no jit-cover.txt beside ${path}: native/monohost/out is older than the --coverage change, run native/monohost/build.cmd`); } }
    const r = report(log);
    if (!total) total = r;
    else {
      total.inputs += r.inputs; total.ran += r.ran; total.differ += r.differ; total.unstable += r.unstable; total.skipped += r.skipped; total.faulted += r.faulted;
      total.lines.push(...r.lines); total.stopped = r.stopped; total.verify = r.verify;
      total.methods = total.methods.map((a, i) => {
        const x = rowRe.exec(a), y = rowRe.exec(r.methods[i] ?? '');
        if (!x || !y) return a;
        return `${x[1]}: inputs ${+x[2] + +y[2]}, ran ${+x[3] + +y[3]}, differ ${+x[4] + +y[4]}, unstable ${+x[5] + +y[5]}, skipped ${+x[6] + +y[6]}, faulted ${+x[7] + +y[7]}, objects not rebuilt ${+x[8] + +y[8]}`;
      });
    }
    const f = [...r.lines].reverse().map(l => /^jit replay FAULT input (\d+) /.exec(l)).find(Boolean);
    if (!r.stopped || !f) break;
    from = +f[1] + 1;
    console.log(`jit-replay: fault at input ${f[1]}, continuing in a fresh process from ${from}`);
  }
  return { total: total!, covers };
}
function printReport(r: ReturnType<typeof report>) {
  console.log(`jit-replay: ${r.inputs} inputs, ${r.ran} ran, ${r.differ} differ, ${r.unstable} unstable, ${r.skipped} skipped, ${r.faulted} faulted${r.stopped ? ', STOPPED on a fault' : ''}; per-call verify: ${r.verify}`);
  for (const l of [...r.lines.slice(0, 40), ...r.methods, ...r.reasons.map(x => `  skip reason: ${x}`)]) console.log(l);
  if (r.differ || r.stopped) process.exitCode = 1;
}

// ---- --switches / --bisect / --coverage ----
const SWITCH_ENV: Record<string, string> = { VtOps: 'MONOHOST_JITVTOPS', CallExit: 'MONOHOST_JITCALLEXIT', NewVt: 'MONOHOST_JITNEWVT', Wide: 'MONOHOST_JITWIDE', Lazy: 'MONOHOST_JITLAZY', Inline: 'MONOHOST_JITINLINE' };
export type Switches = [string, string][];  // name, value
export function parseSwitches(t: string): Switches {
  return t.split(',').filter(x => x.trim()).map(x => {
    const m = /^(\w+)=(\d)$/.exec(x.trim());
    if (!m || !SWITCH_ENV[m[1]]) throw new Error(`bad switch "${x}": want Name=0|1, Name one of ${Object.keys(SWITCH_ENV).join(' ')}`);
    return [m[1], m[2]];
  });
}
export const switchEnv = (sw: Switches) => Object.fromEntries(sw.map(([n, v]) => [SWITCH_ENV[n], v]));
export const switchText = (sw: Switches) => sw.map(([n, v]) => `${n}=${v}`).join(',') || '(environment)';
// fails(on) runs with the candidates in `on` at their value and every other candidate 0. Returns what to blame.
export type Blame = { kind: 'passes' | 'base' | 'single' | 'joint'; set: string[]; runs: number };
export async function bisect(cands: string[], fails: (on: string[]) => Promise<boolean>): Promise<Blame> {
  let runs = 0;
  const f = async (on: string[]) => { runs++; return fails(on); };
  if (!await f(cands)) return { kind: 'passes', set: [], runs };
  if (await f([])) return { kind: 'base', set: [], runs };
  let cur = cands;
  while (cur.length > 1) {
    const h = cur.length >> 1, a = cur.slice(0, h), b = cur.slice(h);
    if (await f(a)) cur = a;
    else if (await f(b)) cur = b;
    else return { kind: 'joint', set: cur, runs };  // each half passes alone: they fail together
  }
  return { kind: 'single', set: cur, runs };
}
export const failed = (r: ReturnType<typeof report>) => r.differ > 0 || r.faulted > 0 || r.stopped || /MISMATCH [1-9]/.test(r.verify);
export function coverageCsv(rows: { res: Result; calls: number }[]): string {
  const q = (x: string | number) => `"${x}"`;
  const out = ['method,sha8,il_bytes,edges,hit,calls,class,missing_il_offset_side'];
  for (const { res: r, calls } of rows)
    out.push([q(r.key), q(r.sha), r.size, r.edges, r.hit, calls, q(!calls ? 'rare' : r.hit < r.edges ? 'partial' : 'full'),
      q(r.missing.map(e => `${e.offset < 0 ? 'entry' : 'IL_' + e.offset.toString(16).padStart(4, '0')}:${e.side}`).join(' '))].join(','));
  return out.join('\n') + '\n';
}
const sha256 = (hex: string) => createHash('sha256').update(Buffer.from(hex, 'hex')).digest('hex');
function coverRows(covers: string[], gc: string) {
  const hosts: HostBody[] = covers.flatMap(t => parseCover(t));
  const rows = new Map<string, Row>(listRows(readFileSync(resolve(ROOT, 'native/ts3native/monojit-list.h'), 'utf8')).map(r => [r.sha, r]));
  for (const h of hosts) { const sha = sha256(h.il); if (h.il && !rows.has(sha)) rows.set(sha, { key: h.name, size: h.size, sha }); }
  const { res } = analyse([...rows.values()], join(gc, 'assemblies'), hosts);
  const calls = new Map<string, number>();
  for (const h of hosts) { const k = sha256(h.il).slice(0, 8); calls.set(k, (calls.get(k) ?? 0) + h.calls); }
  return res.map(r => ({ res: r, calls: calls.get(r.sha) ?? 0 }));
}

async function selfCheck() {
  let k = 0;
  const ok = (c: boolean, what: string) => { k++; if (!c) { console.log(`FAIL ${what}`); process.exitCode = 1; } };
  const fx = fixtureCapture(JSON.parse(readFileSync(FIXTURE, 'utf8')));
  ok(fx.samples.length >= 6, 'fixture has its cases');
  const back = parseCapture(encodeCapture(fx));
  ok(back.samples.length === fx.samples.length && back.classes.size === fx.classes.size && back.methods.size === fx.methods.size, 'encode + parse keeps the records');
  ok(back.samples[0].objs[0].bytes.equals(fx.samples[0].objs[0].bytes), 'object bytes survive');
  const r = toReplay([fx]), lines = r.text.split('\n');
  ok(lines[0] === 'jitreplay 1' && !lines.some(l => l === 'undefined'), 'header');
  ok(lines.includes('ref 0 4 1') && lines.includes('ref 2 4 0'), 'a cycle becomes two refs by index');
  ok(/^self n -1$/m.test(r.text), 'a static method has no this');
  ok(r.totals.inputs === fx.samples.length, 'one input per sample');
  ok(/^cut \d+ \d+$/m.test(r.text), 'a cut word stays a cut line');
  // two samples in one slot: the later one stands; a torn tail is not a record
  const dup: Capture = { ...fx, samples: [fx.samples[0], { ...fx.samples[0], seen: 9 }] };
  const d = toReplay([dup]);
  ok(d.totals.inputs === 1 && d.totals.superseded === 1, 'a reservoir slot keeps its last sample');
  const enc = encodeCapture(fx), torn = parseCapture(enc.subarray(0, enc.length - 3));
  ok(torn.tornTail && torn.samples.length === fx.samples.length - 1, 'a torn last record is dropped');
  let bad = false; try { parseCapture(Buffer.from('nope0000')); } catch { bad = true; }
  ok(bad, 'a foreign file is refused');
  // a pointer into the middle of a kept object becomes a blob of those bytes
  const words = Buffer.alloc(16); words.writeUInt32LE(0x11111111, 8); words.writeUInt32LE(0x22222222, 12);
  const inner: Capture = { classes: new Map([[1, { id: 1, name: 'X', flags: SCAN, is: 16, es: 0 }]]), methods: new Map([[0, { id: 0, token: 7, np: 1, cls: 'X', name: 'X.M' }]]), statics: new Map(), tornTail: false,
    samples: [{ meth: 0, slot: 0, seen: 1, self: 0, args: [[0x10008, 0]], statics: [], objs: [{ addr: 0x10000, cls: 1, n: 16, full: 16, bytes: words }], cuts: [], cutLost: 0 }] };
  const it = toReplay([inner]).text;
  ok(/^obj 1 -1 0 8 1111111122222222$/m.test(it) && /^arg 0 10008 0 b 1$/m.test(it), 'an interior pointer becomes a value blob');
  // a sample with a string in it: the summary shows no text
  ok(!summary(fx).join('\n').includes('hi'), 'the summary holds no string text');
  // version 2 (docs/537 §19): the path word survives, the summary splits by it, and a version 1 file still reads (path 0)
  const vias: Capture = { ...fx, samples: fx.samples.slice(0, 3).map((x, i) => ({ ...x, via: i })) };
  ok(parseCapture(encodeCapture(vias)).samples.map(x => x.via).join() === '0,1,2', 'the path of each sample survives');
  ok(summary({ ...vias, samples: vias.samples.map(x => ({ ...x, meth: vias.samples[0].meth })) }).join('\n').includes('(enter 1, lazy 1, inline 1)'), 'the summary splits samples by path');
  const mrec = Buffer.concat([u32s(0, 7, 0, 1), Buffer.from('A\0\0\0', 'latin1'), u32s(3), Buffer.from('A.M\0', 'latin1')]);
  const p1 = parseCapture(Buffer.concat([Buffer.from('JCAP', 'latin1'), u32s(1, 2, mrec.length), mrec, u32s(4, 36, 0, 3, 5, 0, 0, 0, 0, 0, 0)]));
  ok(p1.methods.get(0)?.name === 'A.M' && p1.samples.length === 1 && p1.samples[0].via === 0 && p1.samples[0].slot === 3 && p1.samples[0].seen === 5, 'a version 1 sample reads as JitEnter');
  const one = (via: number) => toReplay([{ ...fx, samples: [{ ...fx.samples[0], via }] }]).text;
  ok(one(0) === one(1) && one(1) === one(2), 'the path does not change the replay input');
  const rep = report('jit replay method A.B: inputs 2, ran 1, differ 0, unstable 0, skipped 1, objects not rebuilt 0\njit replay reason A.B: delegate x1\njit replay: 2 inputs, 1 ran, 0 differ, 0 unstable, 1 skipped; per-call verify: ok 1, MISMATCH 0, abandoned (exception) 0\n');
  ok(rep.inputs === 2 && rep.ran === 1 && rep.reasons.length === 1 && rep.methods.length === 1 && !rep.stopped, 'report parse');
  ok(switchText(parseSwitches('VtOps=1, CallExit=0,NewVt=1')) === 'VtOps=1,CallExit=0,NewVt=1' && switchEnv(parseSwitches('CallExit=0')).MONOHOST_JITCALLEXIT === '0', 'switch parse and env');
  let badSw = false; try { parseSwitches('Nope=1'); } catch { badSw = true; }
  ok(badSw, 'an unknown switch is refused');
  // bisect demo on a fake failing switch: only NewVt breaks; then a pair that fails only together; a base failure; a pass
  const demo = (broken: string[][]) => bisect(['VtOps', 'CallExit', 'NewVt', 'Wide'], async on => broken.some(set => set.every(x => on.includes(x))));
  const bis1 = await demo([['NewVt']]), bisP = await demo([['VtOps', 'Wide']]), bisB = await demo([[]]), bisN = await demo([['Inline']]);
  ok(bis1.kind === 'single' && bis1.set[0] === 'NewVt' && bis1.runs <= 6, 'bisect names the one failing switch');
  ok(bisP.kind === 'joint' && bisP.set.includes('VtOps') && bisP.set.includes('Wide'), 'bisect reports a pair that fails only together');
  ok(bisB.kind === 'base', 'bisect reports a failure with every switch off');
  ok(bisN.kind === 'passes' && bisN.runs === 1, 'bisect stops when the full set passes');
  console.log(`jit-replay bisect demo: fake failing switch NewVt found in ${bis1.runs} runs (${bis1.kind})`);
  const base0 = report('jit replay: 3 inputs, 3 ran, 0 differ, 0 unstable, 0 skipped, 0 faulted; per-call verify: ok 9, MISMATCH 0, abandoned (exception) 0\n');
  ok(!failed(base0) && failed({ ...base0, differ: 1 }) && failed({ ...base0, faulted: 1 }) && failed({ ...base0, verify: 'ok 9, MISMATCH 2, abandoned (exception) 0' }), 'failure predicate');
  const csv = coverageCsv([{ res: { key: 'A.B', sha: 'aabbccdd', size: 9, edges: 3, hit: 1, missing: [{ offset: 4, side: 'taken' }], note: '' }, calls: 0 }, { res: { key: 'C.D', sha: '11223344', size: 5, edges: 1, hit: 1, missing: [], note: '' }, calls: 7 }]);
  ok(/"A\.B","aabbccdd",9,3,1,0,"rare","IL_0004:taken"/.test(csv) && /"C\.D","11223344",5,1,1,7,"full",""/.test(csv), 'coverage csv');
  console.log(`jit-replay self-check: ${k} checks${process.exitCode ? ', FAILED' : ', ok'}`);
}

if (import.meta.main) {
  const out = opt('--out');
  if (argv.includes("--self-check")) await selfCheck();
  else if (argv.includes('--fixture')) {
    if (!out) fail('--fixture needs --out');
    writeFileSync(out!, toReplay([fixtureCapture(JSON.parse(readFileSync(FIXTURE, 'utf8')))]).text);
    console.log(`wrote ${out}`);
  } else if (argv.includes('--capture')) {
    const files = argv.slice(argv.indexOf('--capture') + 1).filter((a, i, all) => !a.startsWith('--') && all[i - 1] !== '--out');
    if (!out || !files.length) fail('--capture needs files and --out');
    const caps = files.map(f => parseCapture(readFileSync(f)));
    const r = toReplay(caps);
    mkdirSync(dirname(resolve(out!)), { recursive: true });
    writeFileSync(out!, r.text);
    console.log(`jit-replay: ${r.totals.inputs} inputs of ${new Set(r.methods).size} methods (${r.totals.superseded} reservoir samples replaced), ${r.totals.objects} objects, ${r.totals.blobs} blobs, ${r.totals.refs} refs, ${r.totals.cuts} cuts + ${r.totals.cutLost} unlisted, ${r.totals.truncated} truncated${caps.some(c => c.tornTail) ? ', a torn tail dropped' : ''}; wrote ${out}`);
  } else if (argv.includes('--summary')) {
    for (const l of summary(parseCapture(readFileSync(opt('--summary')!)))) console.log(l);
  } else if (argv.includes('--report')) printReport(report(readFileSync(opt('--report')!, 'utf8')));
  else if (argv.includes('--capture-corpus')) {
    const file = resolve(opt('--capture-corpus')!);
    mkdirSync(dirname(file), { recursive: true });
    const env: Record<string, string> = { MONOHOST_JIT: 'on', MONOHOST_MINTCALL: 'fast', MONOHOST_DIVGUARD: '1', MONOHOST_JITCORPUS: '1', MONOHOST_JITCAPTURE: file,
      MONOHOST_JITLIST: jitList(readFileSync(resolve(ROOT, 'native/monohost/monohost-jit.cpp'), 'utf8')) };
    if (argv.includes('--lazy')) env.MONOHOST_JITLAZY = '1';
    if (opt('--inline')) env.MONOHOST_JITINLINE = opt('--inline')!;
    const { log, path } = await runHost(undefined, env, /^jit capture: \d+ samples/m, 1800000);
    console.log(`log: ${path}`);
    for (const l of log.split(/\r?\n/)) if (/^jit (capture|corpus):/.test(l)) console.log(l);
  } else if (argv.includes('--run')) {
    const replay = resolve(opt('--run')!), mode = opt('--mode') ?? 'verify', sw = parseSwitches(opt('--switches') ?? ''), cover = argv.includes('--coverage');
    const list = opt('--list') ?? [...new Set(readFileSync(replay, 'utf8').split('\n').filter(l => l.startsWith('meth ')).map(l => l.split(' ').slice(4).join(' ')))].join(',');
    const baseEnv = (s: Switches): Record<string, string> => ({ MONOHOST_JIT: mode, MONOHOST_MINTCALL: 'fast', MONOHOST_DIVGUARD: '1', ...(argv.includes('--lazy') ? { MONOHOST_JITLAZY: '1' } : {}),
      MONOHOST_JITLIST: list === 'corpus' ? jitList(readFileSync(resolve(ROOT, 'native/monohost/monohost-jit.cpp'), 'utf8')) : list, ...(cover ? { MONOHOST_JITCOVER: '1' } : {}), ...switchEnv(s) });
    if (argv.includes('--bisect')) {
      const cands = sw.filter(([, v]) => v !== '0').map(([n]) => n);
      if (!cands.length) fail('--bisect needs --switches with at least one switch on, e.g. VtOps=1,CallExit=1,NewVt=1');
      const blame = await bisect(cands, async on => {
        const set: Switches = sw.map(([n, v]) => [n, cands.includes(n) && !on.includes(n) ? '0' : v]);
        const bad = failed((await replayRun(replay, baseEnv(set), false)).total);
        console.log(`jit-replay bisect: ${switchText(set)} -> ${bad ? 'FAIL' : 'pass'}`);
        return bad;
      });
      const msg = { passes: 'passes with the whole set: nothing to bisect', base: 'fails with every candidate switch off: not a switch, a replay or host problem',
        single: `failing switch: ${blame.set[0]}`, joint: `each half passes alone, these fail together: ${blame.set.join(',')}` }[blame.kind];
      console.log(`jit-replay bisect: ${msg} (${blame.runs} runs)`);
      if (blame.kind !== 'passes') process.exitCode = 1;
    } else {
      const { total, covers } = await replayRun(replay, baseEnv(sw), cover);
      console.log(`jit-replay: switches ${switchText(sw)}`);
      printReport(total);
      if (cover) {
        const rows = coverRows(covers, gameCopy()), date = new Date().toISOString().slice(0, 10), file = resolve(ROOT, `docs/data/results/${date}-jit-replay-coverage.csv`);
        mkdirSync(dirname(file), { recursive: true });
        writeFileSync(file, coverageCsv(rows));
        const rare = rows.filter(r => !r.calls), edges = rows.reduce((x, r) => x + r.res.edges, 0), hit = rows.reduce((x, r) => x + r.res.hit, 0);
        console.log(`jit-replay coverage: ${rows.length} methods, ${edges} edges, ${hit} reached (${(100 * hit / Math.max(edges, 1)).toFixed(1)}%), ${rare.length} with 0 calls (rare path); wrote ${file}`);
        for (const r of rare.slice(0, 60)) console.log(`  rare: ${r.res.key}`);
      }
    }
  } else {
    console.log('see the header of tools/jit-replay.ts');
    process.exitCode = 1;
  }
}
