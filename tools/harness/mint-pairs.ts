// In-game mint pair census report ([MonoFast] PairCount, native/ts3native/mintpairs.cpp, docs/139 §3 G-I6): reads the last
// 'mintpairs:' report of a ts3native.log and prints the docs/139 §3 table row (dispatches, superop share, top fusable pairs,
// top 10 / 20 / 50 cover) plus the top fusable pairs and ops with their names. Reads only.
//   node tools/harness/mint-pairs.ts <ts3native.log | tag prefix> [...] [--top N]   default N 25
//   node tools/harness/mint-pairs.ts --self-check
// A tag prefix matches probe-out/queue/<prefix>*-ts3native.log (set TS3_REPO=<main checkout> from a worktree). Names: docs/data/mint-opcodes.csv.
// Fusable (docs/139 §3): the first op is not an end op (calls, ret*, newobj, branches, switch, throw, leave, endfinally, jmp)
// and neither op is a superop (0x1AB-0x1C0). The log holds the top 200 pairs only, so cover % is of all dispatches, from those 200.
import assert from 'node:assert/strict';
import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { basename, join } from 'node:path';

const ROOT = process.env.TS3_REPO ?? join(import.meta.dirname, '..', '..');
const QUEUE = join(ROOT, 'probe-out', 'queue');
export const HEAD_RE = /mintpairs: (\w+) #(\d+) dispatches (\d+), distinct pairs (\d+), ops (\d+)/;
export const PAIR_RE = /mintpair: 0x([0-9A-F]{3}) 0x([0-9A-F]{3}) (\d+)\s*$/;
export const OP_RE = /mintop: 0x([0-9A-F]{3}) (\d+)\s*$/;

const names = new Map<number, string>();
for (const l of readFileSync(join(import.meta.dirname, '..', '..', 'docs', 'data', 'mint-opcodes.csv'), 'utf8').split(/\r?\n/).slice(1)) {
  const [o, n, , , seq] = l.split(','); if (n) names.set(parseInt(o, 16), n === 'superop' && seq ? `superop(${seq})` : n);
}
export const nameOf = (op: number) => names.get(op) ?? `0x${op.toString(16)}`;
export const isSuper = (op: number) => op >= 0x1AB && op <= 0x1C0;
const END = /^(ret|br|b(eq|ne|ge|gt|le|lt)|leave|throw|rethrow|endfinally|switch|call|con\.call|con\.boxcall|calli|jmp|newobj)/;
export const isEnd = (op: number) => END.test(names.get(op) ?? '');
export const fusable = (a: number, b: number) => !isEnd(a) && !isSuper(a) && !isSuper(b);

export type Report = { why: string; n: number; total: number; distinct: number; pairs: [number, number, number][]; ops: [number, number][] };
export function lastReport(text: string): Report | null {
  let r: Report | null = null;
  for (const l of text.split(/\r?\n/)) {
    let m = l.match(HEAD_RE);
    if (m) { r = { why: m[1], n: +m[2], total: +m[3], distinct: +m[4], pairs: [], ops: [] }; continue; }
    if (!r) continue;
    if ((m = l.match(PAIR_RE))) r.pairs.push([parseInt(m[1], 16), parseInt(m[2], 16), +m[3]]);
    else if ((m = l.match(OP_RE))) r.ops.push([parseInt(m[1], 16), +m[2]]);
  }
  return r;
}
const pct = (n: number, t: number) => (100 * n / t).toFixed(1);
export function summary(r: Report, top: number) {
  const fp = r.pairs.filter(([a, b]) => fusable(a, b)), sup = r.ops.filter(([o]) => isSuper(o)).reduce((s, [, n]) => s + n, 0);
  const cover = (k: number) => pct(fp.slice(0, k).reduce((s, p) => s + p[2], 0), r.total);
  const row = `| in game (${r.why} #${r.n}) | ${(r.total / 1e6).toFixed(1)} M | ${pct(sup, r.total)}% | ${fp.slice(0, 5).map(([a, b, n]) => `\`${nameOf(a)}+${nameOf(b)}\` ${pct(n, r.total)}`).join(', ')} | ${cover(10)} / ${cover(20)} / ${cover(50)}% |`;
  return { row, fp: fp.slice(0, top), sup };
}

function logsFor(arg: string): string[] {
  if (existsSync(arg) && arg.endsWith('.log')) return [arg];
  return existsSync(QUEUE) ? readdirSync(QUEUE).filter(f => f.startsWith(arg) && f.endsWith('-ts3native.log')).sort().map(f => join(QUEUE, f)) : [];
}

if (process.argv.includes('--self-check')) {
  // format lines from mintpairs.cpp's Log calls (no game run of the counter yet: no real report line exists)
  const head = 'mintpairs: interval #2 dispatches 1000, distinct pairs 5, ops 4 (sim thread, head 0x00E544D5)';
  assert.match(head, HEAD_RE);
  assert.doesNotMatch('mintpair: 0x1AB 0x042 120', HEAD_RE);
  assert.match('mintpair: 0x024 0x042 300', PAIR_RE);
  assert.doesNotMatch('mintpairs: exit #3 dispatches 10, distinct pairs 1, ops 1 (sim thread, head 0x00E544D5)', PAIR_RE);
  assert.doesNotMatch('mintop: 0x042 400', PAIR_RE);
  assert.match('mintop: 0x042 400', OP_RE);
  assert.doesNotMatch('mintpair: 0x024 0x042 300', OP_RE);
  // real lines of the same log that must not match any (ts3native.log, 2026-11-15)
  for (const l of ['ts3native 0.36.261-lanes1 2026-11-15 17:24:21', 'autshadow satb give-up: op 0xC000 x4']) for (const re of [HEAD_RE, PAIR_RE, OP_RE]) assert.doesNotMatch(l, re);
  assert.equal(nameOf(0x024), 'ldthis');
  assert.ok(isEnd(0x006) && isEnd(names.has(0x0EA) ? 0x0EA : 0x006) && !isEnd(0x024)); // ret ends a run, ldthis does not
  assert.ok(isSuper(0x1AB) && !isSuper(0x1AA));
  const text = ['mintpairs: interval #1 dispatches 10, distinct pairs 1, ops 1 (sim thread, head 0x00E544D5)', 'mintpair: 0x024 0x024 10',
    head, 'mintpair: 0x024 0x042 300', 'mintpair: 0x1AB 0x042 200', 'mintpair: 0x006 0x024 100', 'mintpair: 0x042 0x024 50', 'mintop: 0x042 400', 'mintop: 0x1AB 200'].join('\n');
  const r = lastReport(text)!;
  assert.equal(r.n, 2); assert.equal(r.pairs.length, 4); assert.equal(r.ops.length, 2); // the last report only
  const s = summary(r, 5);
  assert.deepEqual(s.fp.map(p => p.slice(0, 2)), [[0x024, 0x042], [0x042, 0x024]]); // superop and ret-first pairs left out
  assert.equal(s.sup, 200);
  assert.match(s.row, /^\| in game \(interval #2\) \| 0\.0 M \| 20\.0% \| `ldthis\+ldfld\.i4` 30\.0, `ldfld\.i4\+ldthis` 5\.0 \| 35\.0 \/ 35\.0 \/ 35\.0% \|$/);
  console.log('mint-pairs self-check ok');
} else {
  const top = +(process.argv[process.argv.indexOf('--top') + 1] ?? 25) || 25;
  const args = process.argv.slice(2).filter((a, i, all) => !a.startsWith('--') && all[i - 1] !== '--top');
  if (!args.length) { console.error('usage: node tools/harness/mint-pairs.ts <ts3native.log | tag prefix> [...] [--top N] | --self-check'); process.exit(2); }
  const logs = args.flatMap(logsFor);
  if (!logs.length) { console.error(`no ts3native.log for ${args.join(' ')}`); process.exit(1); }
  console.log('| Phase | Dispatches | Superops | Top fusable pairs | Top 10 / 20 / 50 cover |\n|---|---|---|---|---|');
  const out: string[] = [];
  for (const log of logs) {
    const r = lastReport(readFileSync(log, 'utf8'));
    if (!r) { out.push(`${basename(log)}: no mintpairs report ([MonoFast] PairCount off, or the run ended before 5 min)`); continue; }
    const s = summary(r, top);
    console.log(s.row.replace('in game', `${basename(log).replace(/-ts3native\.log$/, '')}`));
    out.push(`\n${basename(log)}: top ${s.fp.length} fusable pairs (% of ${r.total} dispatches)`, ...s.fp.map(([a, b, n]) => `  ${nameOf(a)}+${nameOf(b)}  ${pct(n, r.total)}%  ${n}`),
      `  top ops: ${r.ops.slice(0, 15).map(([o, n]) => `${nameOf(o)} ${pct(n, r.total)}%`).join(', ')}`);
  }
  console.log(out.join('\n'));
}
