// docs/537 §17: IL branch coverage of the [MonoJit] compile list (native/ts3native/monojit-list.h) by the host corpus. The bar: every
// edge of every listed body is hit by the corpus running compiled, or has a line with a reason in docs/data/jit-coverage-reasons.csv.
// An edge is the entry of a body, both sides of each conditional branch, each switch arm and its default, and each clause handler.
// monohost counts mint branch edges (MONOHOST_JITCOVER=1, jit-cover.txt); this tool maps them to IL by branch order and adds the
// bodies the corpus never reached, from the assemblies. Hits add up over the runtime methods of one IL body (generic instances).
//   node tools/jit-coverage.ts --run [--game-copy <dir>] [--no-write]  run monohost twice (lazy off, on), write docs/data/jit-coverage.csv
//   node tools/jit-coverage.ts --report <jit-cover.txt>... [--game-copy <dir>] [--no-write]  the same from existing files
//   node tools/jit-coverage.ts --self-check   unit checks, then the bar on the checked-in csv (FAIL = a miss with no reason or a stale reason)
// monohost.exe comes from native/monohost/out (build.cmd); run it with the machine free of timing jobs.
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync, readdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { jitList } from './jit-corpus.ts';
import { decodeAll, loadStandalone, rva, type Asm } from './managed-callgraph.ts';
import { csvFields, ilCode } from './jit-hot.ts';

const ROOT = resolve(import.meta.dirname, '..');
const LIST = resolve(ROOT, 'native/ts3native/monojit-list.h');
export const CSV = resolve(ROOT, 'docs/data/jit-coverage.csv');
export const REASONS = resolve(ROOT, 'docs/data/jit-coverage-reasons.csv');
export const OUT = resolve(ROOT, 'native/monohost/out');
const arg = (n: string) => { const i = process.argv.indexOf(n); return i > 0 ? process.argv[i + 1] : undefined; };

export type Row = { key: string; size: number; sha: string };
export const listRows = (h: string): Row[] => [...h.matchAll(/\{"([^"]+)", (\d+), \{([^}]+)\}\}/g)].map(m => ({ key: m[1], size: +m[2], sha: m[3].replace(/0x|,| /g, '') }));

// ---- IL side ----
export type Edge = { offset: number; side: string };  // offset -1: the body's entry
export type Branch = { at: number; target: number; arms?: number[]; boxed?: boolean };  // arms: switch targets; boxed: the op before is box
export function ilBranches(il: Buffer): Branch[] {
  const out: Branch[] = [];
  const ins = decodeAll(il, 0, il.length).ins;
  for (const [k, i] of ins.entries()) {
    if ((i.op >= 0x2c && i.op <= 0x37) || (i.op >= 0x39 && i.op <= 0x44)) out.push({ at: i.at, target: i.next + i.v, boxed: ins[k - 1]?.op === 0x8c });
    else if (i.op === 0x45) {
      const n = il.readUInt32LE(i.at + 1), base = i.at + 5 + 4 * n;
      out.push({ at: i.at, target: -1, arms: Array.from({ length: n }, (_, k) => base + il.readInt32LE(i.at + 5 + 4 * k)) });
    }
  }
  return out;
}
export function bodyEdges(br: Branch[], handlers: number[]): Edge[] {
  const e: Edge[] = [{ offset: -1, side: 'entry' }];
  for (const b of br) {
    if (b.arms) { e.push({ offset: b.at, side: 'default' }); b.arms.forEach((_, k) => e.push({ offset: b.at, side: `arm${k}` })); }
    else e.push({ offset: b.at, side: 'fall' }, { offset: b.at, side: 'taken' });
  }
  // shortcut: a handler counts as an edge but monohost does not report handler hits (rm's clause table is not mapped), so a listed body with clauses shows them as misses; no row has clauses today and [MonoJit] refuses such a body without Wide
  for (const h of handlers) e.push({ offset: h, side: 'clause' });
  return e;
}
function clauseHandlers(b: Buffer, p: number): number[] {  // II.25.4.5 of the fat header at p
  if ((b[p] & 3) !== 3 || !(b[p] & 8)) return [];
  let q = (p + (b.readUInt16LE(p) >> 12) * 4 + b.readUInt32LE(p + 4) + 3) & ~3;
  const kind = b[q], fat = (kind & 0x40) !== 0, out: number[] = [];
  const size = fat ? b.readUInt32LE(q) >>> 8 : b[q + 1], step = fat ? 24 : 12;
  for (let i = 0, o = q + 4; i < Math.floor((size - 4) / step); i++, o += step) out.push(fat ? b.readUInt32LE(o + 12) : b.readUInt16LE(o + 5));
  return out;
}
export type Body = { row: Row; il: Buffer; handlers: number[] };
export function bodies(rows: Row[], dir: string): Map<string, Body> {  // by sha; a row found in no assembly stays out and the caller reports it
  const out = new Map<string, Body>(), want = new Map(rows.map(r => [r.sha, r]));
  for (const f of readdirSync(dir).filter(n => n.endsWith('.dll'))) {
    let a: Asm;
    try { a = loadStandalone(join(dir, f), f.slice(0, -4)); } catch { continue; }
    for (const t of a.types) for (const m of t.methods) {
      if (!m.rva) continue;
      const p = rva(a, m.rva), il = ilCode(a.buf, p), sha = createHash('sha256').update(il).digest('hex');
      const r = want.get(sha);
      if (r && !out.has(sha)) out.set(sha, { row: r, il: Buffer.from(il), handlers: clauseHandlers(a.buf, p) });
    }
  }
  return out;
}

// ---- host side ----
export type HostEdge = { from: number; to: number; op: number; arm: number; n: number };
export type HostBody = { name: string; calls: number; size: number; il: string; edges: HostEdge[] };
export function parseCover(text: string): HostBody[] {
  const out: HostBody[] = [];
  for (const l of text.split(/\r?\n/)) {
    if (l.startsWith('B ')) { const f = l.split(' '); out.push({ name: f.slice(6).join(' '), calls: +f[2], size: +f[5], il: '', edges: [] }); }
    else if (l.startsWith('I ')) out[out.length - 1].il = l.slice(2);
    else if (l.startsWith('E ')) { const f = l.split(' '); out[out.length - 1].edges.push({ from: +f[1], to: +f[2], op: parseInt(f[3], 16), arm: +f[4], n: +f[5] }); }
  }
  return out;
}
// The transform keeps the order of the code, so the k-th mint branch is the k-th IL branch [verified per body: branch count and
// target order are compared, a body that fails is returned as null]. Returns hits per "offset|side".
export function mapHits(br: Branch[], h: HostBody): Map<string, number> | null {
  const mint = [...Map.groupBy(h.edges, e => e.from).entries()].sort((a, b) => a[0] - b[0]);
  // a value-type instance loses a null test: `box !T; brtrue` folds in the transform [verified: instance branch counts]. Try each way to drop that many box-fed branches.
  if (mint.length < br.length) {
    const can = br.filter(b => b.boxed), d = br.length - mint.length;
    for (let mask = 0; mask < 1 << can.length; mask++) {
      const drop = can.filter((_, i) => mask >> i & 1);
      if (drop.length !== d) continue;
      const m = mapHits(br.filter(b => !drop.includes(b)), h);
      if (m) return m;
    }
    return null;
  }
  if (mint.length !== br.length) return null;
  const hits = new Map<string, number>();
  const add = (k: string, n: number) => hits.set(k, (hits.get(k) ?? 0) + n);
  for (let i = 0; i < br.length; i++) {
    const [, es] = mint[i], b = br[i];
    if (b.arms) {
      if (es.length !== b.arms.length + 1) return null;
      for (const e of es) add(`${b.at}|${e.arm === 0 ? 'default' : `arm${e.arm - 2}`}`, e.n);
    } else for (const e of es) add(`${b.at}|${e.arm === 0 ? 'fall' : 'taken'}`, e.n);
  }
  const key = (x: number[]) => x.map((v, i) => x.map(w => Math.sign(v - w)).join()).join(';');  // relative order of the targets
  const conds = br.map((b, i) => [b, mint[i][1]] as const).filter(([b]) => !b.arms);
  if (key(conds.map(([b]) => b.target)) !== key(conds.map(([, es]) => es.find(e => e.arm === 1)?.to ?? -1))) return null;
  return hits;
}

// ---- report ----
export type Reason = { method: string; sha: string; offset: number; side: string; reason: string };
export const edgeKey = (sha: string, offset: number, side: string) => `${sha}|${offset}|${side}`;
export function parseReasons(text: string): Reason[] {
  return text.split(/\r?\n/).slice(1).filter(l => l.trim()).map(l => { const f = csvFields(l); return { method: f[0], sha: f[1], offset: f[2] === 'entry' ? -1 : parseInt(f[2].slice(3), 16), side: f[3], reason: f[4] ?? '' }; });
}
export type Result = { key: string; sha: string; size: number; edges: number; hit: number; missing: Edge[]; note: string };
export function judge(results: { sha: string; missing: Edge[] }[], reasons: Reason[]): string[] {
  const bad: string[] = [], missing = new Set(results.flatMap(r => r.missing.map(e => edgeKey(r.sha, e.offset, e.side))));
  const listed = new Set(reasons.map(r => edgeKey(r.sha, r.offset, r.side)));
  for (const r of reasons) {
    if (!r.reason.trim()) bad.push(`no reason text: ${r.method} ${r.sha} IL_${r.offset} ${r.side}`);
    if (!missing.has(edgeKey(r.sha, r.offset, r.side))) bad.push(`stale reason, the edge is hit or gone: ${r.method} ${r.sha} IL_${r.offset} ${r.side}`);
  }
  for (const r of results) for (const e of r.missing) if (!listed.has(edgeKey(r.sha, e.offset, e.side))) bad.push(`unlisted miss: ${r.sha} IL_${e.offset} ${e.side}`);
  return bad;
}
const q = (s: string | number) => `"${s}"`;
export function csvText(res: Result[], reasons: Reason[]): string {
  const why = new Map(reasons.map(r => [edgeKey(r.sha, r.offset, r.side), r.reason]));
  const lines = ['method,sha8,il_bytes,edges,hit,missing_il_offset,missing_side,reason,note'];
  for (const r of res) {
    const head = [r.key, r.sha, r.size, r.edges, r.hit].map(q).join(',');
    if (!r.missing.length) lines.push(`${head},"","","",${q(r.note)}`);
    for (const e of r.missing) lines.push(`${head},${q(e.offset < 0 ? 'entry' : `IL_${e.offset.toString(16).padStart(4, '0')}`)},${q(e.side)},${q((why.get(edgeKey(r.sha, e.offset, e.side)) ?? '').replaceAll('"', "'"))},${q(r.note)}`);
  }
  return lines.join('\n') + '\n';
}
export function resultsOfCsv(text: string): { sha: string; missing: Edge[] }[] {
  const by = new Map<string, Edge[]>();
  for (const l of text.split(/\r?\n/).slice(1).filter(x => x.trim())) {
    const f = csvFields(l);
    if (!by.has(f[1])) by.set(f[1], []);
    if (f[5]) by.get(f[1])!.push({ offset: f[5] === 'entry' ? -1 : parseInt(f[5].slice(3), 16), side: f[6] });
  }
  return [...by].map(([sha, missing]) => ({ sha, missing }));
}
export function analyse(rows: Row[], dir: string, hosts: HostBody[]): { res: Result[]; hostOnly: number } {
  const bs = bodies(rows, dir), res: Result[] = [];
  const bySha = Map.groupBy(hosts, h => createHash('sha256').update(Buffer.from(h.il, 'hex')).digest('hex'));
  for (const r of rows) {
    const b = bs.get(r.sha.slice(0, 64));
    if (!b) { res.push({ key: r.key, sha: r.sha.slice(0, 8), size: r.size, edges: 1, hit: 0, missing: [{ offset: -1, side: 'entry' }], note: 'body in no assembly of the folder' }); continue; }
    const br = ilBranches(b.il), all = bodyEdges(br, b.handlers), hits = new Map<string, number>();
    let unmapped = 0, calls = 0;
    const hs = bySha.get(r.sha) ?? [];
    for (const h of hs) {
      calls += h.calls;
      const m = mapHits(br, h);
      if (!m) { if (h.calls) unmapped++; continue; }
      for (const [k, n] of m) hits.set(k, (hits.get(k) ?? 0) + n);
    }
    if (calls) hits.set('-1|entry', calls);
    const missing = all.filter(e => !hits.get(`${e.offset}|${e.side}`));
    const note = !hs.length ? 'not compiled in the corpus' : !calls ? 'compiled, never entered by the corpus' : unmapped ? `${unmapped} of ${hs.length} instances not mapped to IL` : '';
    res.push({ key: r.key, sha: r.sha.slice(0, 8), size: r.size, edges: all.length, hit: all.length - missing.length, missing, note });
  }
  return { res, hostOnly: hosts.filter(h => !bySha.has(h.il) && !rows.some(r => r.size === h.size)).length };
}
// hits add up over the instances of a body; this says how far one instance alone gets (first run only)
function instanceFull(rows: Row[], file: string): string {
  const shas = new Set(rows.map(r => r.sha));
  const hs = parseCover(readFileSync(file, 'latin1')).filter(h => h.edges.length && shas.has(createHash('sha256').update(Buffer.from(h.il, 'hex')).digest('hex')));
  return `${hs.filter(h => h.edges.every(e => e.n)).length} of ${hs.length} branching runtime methods`;
}
export function gameCopy(): string {
  const d = arg('--game-copy') ?? join(ROOT, 'game-copy');
  if (!existsSync(join(d, 'assemblies'))) throw new Error(`no assemblies in ${d}: pass --game-copy <dir>, the main checkout's game-copy from a worktree`);
  return d;
}
// one monohost selftest run with the corpus on; the log and output files land in native/monohost/out
export function runHost(env: Record<string, string>, gc: string, timeoutS: number, corpus = true) {
  const exe = join(OUT, 'monohost.exe');
  if (!existsSync(exe)) throw new Error('no native/monohost/out/monohost.exe: run native/monohost/build.cmd first');
  const r = spawnSync(exe, [join(gc, 'monohost', 'TS3W.exe'), join(gc, 'assemblies'), 'selftest', join(gc, 'monohost', 'hostdata.txt')],
    { env: { ...process.env, MONOHOST_JITCORPUS: '1', MONOHOST_JITCORPUS_WORKER: '0', MONOHOST_DIVGUARD: '1', MONOHOST_MINTCALL: 'fast', MONOHOST_JITLIST: jitList(readFileSync(resolve(ROOT, 'native/monohost/monohost-jit.cpp'), 'utf8')), ...env }, timeout: timeoutS * 1000, stdio: 'ignore' });
  if (r.error) throw r.error;
  const log = readFileSync(join(OUT, 'monohost.exe.log'), 'latin1');
  const c = log.match(/jit corpus: (\d+) cases, (\d+) differ, (\d+) unstable.*/);
  if (!c && corpus) throw new Error('no corpus line in monohost.exe.log');
  return { line: c?.[0] ?? '', differ: +(c?.[2] ?? 0), log };
}

function selfCheck() {
  const ok = (c: boolean, m: string) => { if (!c) { console.error(`FAIL ${m}`); process.exit(1); } };
  // ldarg.0; brtrue.s +1; ret(nop); ldc.i4 ...; switch(2) {+0, +1}; ret
  const il = Buffer.from([0x02, 0x2d, 0x01, 0x00, 0x02, 0x45, 2, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0x2a]);
  const br = ilBranches(il);
  ok(br.length === 2 && br[0].at === 1 && br[0].target === 4 && br[1].arms?.join() === '18,19', 'IL branches');
  const e = bodyEdges(br, [9]);
  ok(e.length === 1 + 2 + 3 + 1 && e[3].side === 'default' && e[6].side === 'clause', 'edges');
  const h: HostBody = { name: 'x', calls: 3, size: il.length, il: il.toString('hex'), edges: [
    { from: 2, to: 4, op: 0xa9, arm: 0, n: 1 }, { from: 2, to: 6, op: 0xa9, arm: 1, n: 2 },
    { from: 9, to: 20, op: 0xe8, arm: 0, n: 0 }, { from: 9, to: 21, op: 0xe8, arm: 2, n: 5 }, { from: 9, to: 22, op: 0xe8, arm: 3, n: 0 }] };
  const m = mapHits(br, h)!;
  ok(m.get('1|fall') === 1 && m.get('1|taken') === 2 && m.get('5|arm0') === 5 && m.get('5|arm1') === 0 && m.get('5|default') === 0, 'mapHits');
  ok(mapHits(br.slice(0, 1), h) === null, 'branch count differs = not mapped');
  const parsed = parseCover(`B 0 7 11 0 4 A.B C\nI 00ff\nE 1 2 A9 1 3\n`);
  ok(parsed[0].name === 'A.B C' && parsed[0].calls === 7 && parsed[0].il === '00ff' && parsed[0].edges[0].n === 3, 'parseCover');
  ok(listRows('{"A.B", 3, {0x0a, 0xFF}},')[0].sha === '0aFF', 'listRows');
  const res = [{ sha: 'aa', missing: [{ offset: 4, side: 'fall' }] }];
  const rs = (o: number, why: string) => [{ method: 'M', sha: 'aa', offset: o, side: 'fall', reason: why }];
  ok(judge(res, rs(4, 'needs a world')).length === 0, 'listed miss passes');
  ok(judge(res, []).length === 1, 'unlisted miss fails');
  ok(judge(res, rs(4, ' ')).length === 1, 'blank reason fails');
  ok(judge(res, rs(8, 'x')).length === 2, 'stale reason fails');
  const csv = csvText([{ key: 'M', sha: 'aa', size: 9, edges: 3, hit: 2, missing: [{ offset: 4, side: 'fall' }], note: '' }], rs(4, 'why'));
  ok(resultsOfCsv(csv)[0].missing[0].offset === 4 && parseReasons(`h\n"M","aa","IL_0004","fall","a, b"\n`)[0].offset === 4, 'csv round trip');
  // the bar on the checked-in files
  const bad = judge(resultsOfCsv(readFileSync(CSV, 'utf8')), parseReasons(readFileSync(REASONS, 'utf8')));
  for (const b of bad.slice(0, 20)) console.error(`FAIL ${b}`);
  if (bad.length) { console.error(`FAIL jit-coverage: ${bad.length} edges against the bar`); process.exit(1); }
  console.log('jit-coverage self-check OK');
}

if (import.meta.main) {
  if (process.argv.includes('--self-check')) selfCheck();
  else {
    const gc = gameCopy(), files: string[] = [];
    if (process.argv.includes('--run')) {
      for (const [tag, lazy] of [['off', '0'], ['on', '1']]) {
        const r = runHost({ MONOHOST_JIT: 'verify', MONOHOST_JITCOVER: '1', MONOHOST_JITLAZY: lazy }, gc, 280);
        console.log(`lazy ${tag}: ${r.line}`);
        if (r.differ) throw new Error('the corpus differs: fix that before reading coverage');
        const t = readFileSync(join(OUT, 'jit-cover.txt'), 'latin1'), p = join(OUT, `jit-cover-${tag}.txt`);
        writeFileSync(p, t); files.push(p);
      }
    } else {
      for (let i = process.argv.indexOf('--report') + 1; i > 0 && i < process.argv.length && !process.argv[i].startsWith('--'); i++) files.push(process.argv[i]);
      if (!files.length) throw new Error('--run, --report <files> or --self-check');
    }
    const rows = listRows(readFileSync(LIST, 'utf8'));
    const { res } = analyse(rows, join(gc, 'assemblies'), files.flatMap(f => parseCover(readFileSync(f, 'latin1'))));
    const reasons = existsSync(REASONS) ? parseReasons(readFileSync(REASONS, 'utf8')) : [];
    if (!process.argv.includes('--no-write')) writeFileSync(CSV, csvText(res, reasons));
    console.log(`generic instances with every mapped edge hit by that instance: ${instanceFull(rows, files[0])}`);
    const edges = res.reduce((s, r) => s + r.edges, 0), hit = res.reduce((s, r) => s + r.hit, 0);
    console.log(`${res.length} bodies, ${edges} edges, ${hit} hit (${(100 * hit / edges).toFixed(1)}%), ${res.filter(r => r.note.startsWith('not') || r.note.startsWith('compiled')).length} bodies not entered`);
    for (const r of res) if (r.missing.length) console.log(`  ${r.key} ${r.sha} il ${r.size}: ${r.hit}/${r.edges} ${r.note}  ${r.missing.slice(0, 12).map(e => `${e.offset < 0 ? '' : e.offset.toString(16)}:${e.side}`).join(' ')}`);
    const bad = judge(res, reasons);
    console.log(bad.length ? `FAIL: ${bad.length} edges against the bar` : 'bar: PASS');
    if (bad.length) process.exitCode = 1;
  }
}
