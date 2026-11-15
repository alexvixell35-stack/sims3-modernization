// Satb give-up census per run (handoff #9 watch item, inventory E2): counts the worker's give-up reasons from a run's ts3native.log.
// Reads only; never talks to the game.
//   node tools/harness/satb-giveups.ts <tag prefix | log path> [...]   one table per log: op, reason, count; then the reasons summed
//   node tools/harness/satb-giveups.ts <...> --row                     one markdown row per log for the release-run summary
//   node tools/harness/satb-giveups.ts --self-check
// A tag prefix matches probe-out/queue/<prefix>*-ts3native.log (queue job copies). Set TS3_REPO=<main checkout> from a worktree.
// Source (native/ts3native/satb.cpp SatbGiveOpLog): 'autshadow satb give-up: op 0x<op> x<n>' is written when an op's count first
// reaches 1, 10, 100, ... with the count at that moment, so the largest n per op is a LOWER BOUND (the true count is < 10x it).
// The exact total is satbWGiveUp after - before in the job's <stem>-native.json when it sits next to the log; 16 ops at most are
// tracked per session. 'worker store op ... skipped, job stopped' lines (conflicts, first 8 only) are counted beside.
// Labels are for Steam 1.67 addresses (an EA 1.69 log names other addresses: label '?').
import assert from 'node:assert/strict';
import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { basename, dirname, join } from 'node:path';

const ROOT = process.env.TS3_REPO ?? join(import.meta.dirname, '..', '..');
const QUEUE = join(ROOT, 'probe-out', 'queue');

export const GIVE_RE = /autshadow satb give-up: op 0x([0-9A-F]+) x(\d+)\s*$/;
export const CONFLICT_RE = /autshadow satb: worker store op 0x([0-9A-F]+) to [0-9A-F]+ \(object [0-9A-F]+\) the sim changed in this job/;

// op -> reason (satb.cpp SatbWGiveUp call sites)
const LABELS: Record<number, string> = {
  0xC000: 'cold: IL -> mint transform', 0xC001: 'cold: tramp_create_thunk', 0xC002: 'cold: vtable creation',
  0xC003: 'cold: class init (.cctor)', 0xC004: 'cold: image load',
  0xD71900: 'pool grow chunk (list full)', 0xE79210: 'array store into a logged element', 0xEBA000: 'Interlocked write to a logged word',
  0xE7B150: 'FieldInfo.GetValue on a logged field', 0xE7A400: 'object clone with logged words', 0xE78FD0: 'GCHandle.GetTarget after a sim GC',
  0xE525C0: 'binder call (shared object / logged words)', 0xD89BF0: 'worker store into a shared static', 0xD89C60: 'static get: unknown size / buffer full',
  0xD89CD0: 'static address-of: unknown size', 0xD8A8E0: 'native-to-managed wrapper refused',
  0xE0D450: 'binder body PriorityQueue.Add', 0xE0C640: 'binder body PriorityQueue.RemoveAt', 0xE0D290: 'binder body PriorityQueue.SetCapacity',
  0xDCA870: 'binder body Hashtable.Rehash', 0xE0A6D0: 'binder body SimClock.ElapsedTimeInMinutes', 0xE0A790: 'binder body SimClock.ElapsedTimeHours',
  0xE0A950: 'binder body Curve.Fx',
};
let opNames: Map<number, string> | null = null;
function mintName(op: number): string | undefined {
  if (!opNames) {
    opNames = new Map();
    const f = join(import.meta.dirname, '..', '..', 'docs', 'data', 'mint-opcodes.csv');
    if (existsSync(f)) for (const l of readFileSync(f, 'utf8').split(/\r?\n/).slice(1)) { const [o, n] = l.split(','); if (n) opNames.set(parseInt(o, 16), n); }
  }
  return opNames.get(op);
}
export const label = (op: number) => LABELS[op] ?? (op <= 0x1C0 ? `mint op ${mintName(op) ?? '?'} on a logged word` : '?');

export type Census = { ops: Map<number, number>; conflicts: number };
export function parse(text: string): Census {
  const ops = new Map<number, number>();
  let conflicts = 0;
  for (const l of text.split(/\r?\n/)) {
    const m = l.match(GIVE_RE);
    if (m) { const op = parseInt(m[1], 16); ops.set(op, Math.max(ops.get(op) ?? 0, +m[2])); continue; }
    if (CONFLICT_RE.test(l)) conflicts++;
  }
  return { ops, conflicts };
}
// exact total from the job's native dump next to the log, else null
export function exactTotal(log: string): number | null {
  const f = log.replace(/-ts3native\.log$/, '-native.json');
  if (f === log || !existsSync(f)) return null;
  try { const j = JSON.parse(readFileSync(f, 'utf8')); const a = j.after?.autshadow?.satbWGiveUp, b = j.before?.autshadow?.satbWGiveUp ?? 0;
    return typeof a === 'number' ? a - b : null; } catch { return null; }
}
const lb = (c: Census) => [...c.ops.values()].reduce((a, b) => a + b, 0);
export function row(name: string, c: Census, exact: number | null): string {
  const top = [...c.ops].sort((a, b) => b[1] - a[1]).slice(0, 3).map(([op, n]) => `${label(op)} (0x${op.toString(16).toUpperCase()}) >=${n}`).join('; ');
  return `| satb give-ups ${name} | ${exact ?? 'n/a'} | >=${lb(c)} | ${c.ops.size} | ${c.conflicts} | ${top || '-'} |`;
}
export const ROW_HEAD = '| run | give-ups (native exact) | logged lower bound | ops | conflicts | top reasons |\n|---|---|---|---|---|---|';

function logsFor(arg: string): string[] {
  if (existsSync(arg) && arg.endsWith('.log')) return [arg];
  if (!existsSync(QUEUE)) return [];
  return readdirSync(QUEUE).filter(f => f.startsWith(arg) && f.endsWith('-ts3native.log')).sort().map(f => join(QUEUE, f));
}

if (process.argv.includes('--self-check')) {
  // give-up lines: real (probe-out/queue lane-l3/l4 1115 logs); conflict line: built from the satb.cpp:936 format, no real one in probe-out yet
  const give = ['autshadow satb give-up: op 0xC000 x10', 'autshadow satb give-up: op 0xC002 x1', 'autshadow satb give-up: op 0xD71900 x100'];
  const notGive = ['autshadow satb binder body on the sim: Curve.Fx x10', 'autshadow satb binder body on the sim: Hashtable.Rehash x2',
    'autshadow satb: load thunks per opcode'];
  for (const l of give) assert.match(l, GIVE_RE, l);
  for (const l of notGive) assert.doesNotMatch(l, GIVE_RE, l);
  const conf = 'autshadow satb: worker store op 0x0A3 to 1F3C2A10 (object 1F3C2A00) the sim changed in this job: skipped, job stopped';
  assert.match(conf, CONFLICT_RE);
  assert.doesNotMatch('autshadow satb: load thunks per opcode', CONFLICT_RE);
  const c = parse([...give, give[0].replace('x10', 'x3'), 'autshadow satb give-up: op 0xC000 x21', conf, ...notGive].join('\n'));
  assert.equal(c.ops.get(0xC000), 21); // largest n per op, not the sum of its lines
  assert.equal(c.ops.get(0xC002), 1);
  assert.equal(c.conflicts, 1);
  assert.equal(lb(c), 122);
  assert.equal(label(0xC003), 'cold: class init (.cctor)');
  assert.equal(label(0x1234567), '?');
  assert.match(label(0x002), /^mint op ldnull/);
  assert.match(row('x', c, 140), /^\| satb give-ups x \| 140 \| >=122 \| 3 \| 1 \| pool grow chunk \(list full\) \(0xD71900\) >=100; cold: IL -> mint transform \(0xC000\) >=21; cold: vtable creation \(0xC002\) >=1 \|$/);
  assert.equal(exactTotal('nowhere.txt'), null);
  console.log('satb-giveups self-check ok');
} else {
  const args = process.argv.slice(2).filter(a => !a.startsWith('--')), asRow = process.argv.includes('--row');
  if (!args.length) { console.error('usage: node tools/harness/satb-giveups.ts <tag prefix | log path> [...] [--row] | --self-check'); process.exit(2); }
  const logs = args.flatMap(logsFor);
  if (!logs.length) { console.error(`no ts3native.log for ${args.join(' ')} in ${QUEUE}`); process.exit(1); }
  const sum = new Map<number, number>();
  if (asRow) console.log(ROW_HEAD);
  for (const log of logs) {
    const c = parse(readFileSync(log, 'utf8')), name = basename(log).replace(/-ts3native\.log$/, ''), exact = exactTotal(log);
    for (const [op, n] of c.ops) sum.set(op, (sum.get(op) ?? 0) + n);
    if (asRow) { console.log(row(name, c, exact)); continue; }
    console.log(`\n${name}: give-ups ${exact ?? 'n/a'} (native exact), >=${lb(c)} logged, conflicts ${c.conflicts} (${dirname(log)})`);
    for (const [op, n] of [...c.ops].sort((a, b) => b[1] - a[1])) console.log(`  0x${op.toString(16).toUpperCase().padStart(3, '0')}  >=${n}  ${label(op)}`);
  }
  if (!asRow && logs.length > 1) {
    console.log(`\nall ${logs.length} logs, lower bounds summed:`);
    for (const [op, n] of [...sum].sort((a, b) => b[1] - a[1])) console.log(`  0x${op.toString(16).toUpperCase().padStart(3, '0')}  >=${n}  ${label(op)}`);
  }
}
