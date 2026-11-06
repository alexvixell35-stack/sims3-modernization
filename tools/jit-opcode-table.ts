// Round 7 task 3 b: which mint opcodes the P0 compiler (native/ts3native/monojit.cpp kJitTmpl, shared by monohost) does not template yet,
// weighted by the interpreter time behind its exits (docs/data/jit-exit-census.csv est_ms_s_behind, from
// compile-candidates.csv ms/s), and which of them could be generated from EA's handler bytes (docs/213 spec).
// Writes docs/data/jit-opcode-table.csv: opcode, name, class, reason, ms_s_behind, methods, test_cases.
//   covered:     has a P0 template (its slow path may still exit; that time is in ms_s_behind)
//   generatable: no template; no call, no allocation, no managed throw (docs/213: can_throw "-" or only a native fault,
//                branches_or_calls "-" or a plain branch), so the handler body can be lifted as it is, with one corpus
//                case from the spec's test cases
//   hand:        the rest; reason lists call / alloc / throw (throw-only ones could move to generatable with a stub that
//                exits to EA's handler on the throw path)
// Usage: node tools/jit-opcode-table.ts [--check]
import { readFileSync, writeFileSync } from 'node:fs';

const OUT = 'docs/data/jit-opcode-table.csv';
const csv = (p: string): Record<string, string>[] => {
  const rows: string[][] = [];
  let row: string[] = [], f = '', q = false;
  const s = readFileSync(p, 'utf8').replace(/\r\n/g, '\n');
  for (let i = 0; i < s.length; i++) {
    const c = s[i];
    if (q) { if (c === '"' && s[i + 1] === '"') { f += '"'; i++; } else if (c === '"') q = false; else f += c; }
    else if (c === '"') q = true; else if (c === ',') { row.push(f); f = ''; }
    else if (c === '\n') { row.push(f); rows.push(row); row = []; f = ''; } else f += c;
  }
  if (f || row.length) { row.push(f); rows.push(row); }
  const [h, ...b] = rows;
  return b.filter((r) => r.length > 1).map((r) => Object.fromEntries(h.map((k, i) => [k, r[i] ?? ''])));
};
const field = (v: string) => (/[",\n]/.test(v) ? `"${v.replace(/"/g, '""')}"` : v);

const src = readFileSync('native/ts3native/monojit.cpp', 'utf8');  // the compiler core moved here (docs/527 step 1)
const t0 = src.indexOf('kJitTmpl[] = {');
if (t0 < 0) throw new Error('kJitTmpl not found in monojit.cpp');
const tmpl = new Set([...src.slice(t0, src.indexOf('};', t0)).matchAll(/\{"([^"]+)",/g)].map((m) => m[1]));
const spec = csv('docs/data/mint-opcode-spec.csv');
const byName = new Map(spec.map((r) => [r.name, r]));
for (const n of tmpl) if (!byName.has(n)) throw new Error(`template ${n} is not in mint-opcode-spec.csv`);
const behind = new Map<string, { ms: number; m: Set<string> }>();
for (const r of csv('docs/data/jit-exit-census.csv')) {
  const op = r.opcode.replace(/ slow path$/, '');
  if (!byName.has(op)) continue;  // exits that are not one opcode (frame teardown notes etc. use an opcode name; others skipped)
  const e = behind.get(op) ?? { ms: 0, m: new Set<string>() };
  e.ms += Number(r.est_ms_s_behind) || 0; e.m.add(r.method); behind.set(op, e);
}
export function classify(r: Record<string, string>, templated: boolean): { cls: string; reason: string } {
  if (templated) return { cls: 'covered', reason: '' };
  const why: string[] = [];
  const bc = r.branches_or_calls;
  if (bc !== '-' && /0x[0-9A-F]|call|mono_|invoke/i.test(bc)) why.push('call');
  if (/new |alloc/i.test(r.heap_writes)) why.push('alloc');
  if (r.can_throw !== '-' && !/^native (access violation|fault)/i.test(r.can_throw)) why.push('throw');
  return why.length ? { cls: 'hand', reason: why.join('+') } : { cls: 'generatable', reason: '' };
}
// self-check on spec-shaped rows
const selfCheck = () => {
  const row = (bc: string, hw: string, ct: string) => ({ branches_or_calls: bc, heap_writes: hw, can_throw: ct });
  const ok = (r: Record<string, string>, t: boolean, want: string) => {
    const g = classify(r, t); if (`${g.cls}${g.reason ? ':' + g.reason : ''}` !== want) throw new Error(`classify: ${JSON.stringify(r)} -> ${g.cls}:${g.reason}, want ${want}`);
  };
  ok(row('-', '-', '-'), false, 'generatable');
  ok(row('if v != 0: ip += offset else ip += 3', '-', '-'), false, 'generatable');
  ok(row('-', '-', 'native access violation if addr is bad'), false, 'generatable');
  ok(row('-', '-', 'OverflowException'), false, 'hand:throw');
  ok(row('shared call block', '-', 'anything the callee throws'), false, 'hand:call+throw');
  ok(row('mono_array_new 0xEA30F0', 'heap: new array', 'OverflowException'), false, 'hand:call+alloc+throw');
  ok(row('shared call block', '-', '-'), true, 'covered');
  console.error('jit-opcode-table self-check: PASS');
};
if (process.argv.includes('--self-check')) { selfCheck(); process.exit(0); }

const rows = spec.map((r) => {
  const c = classify(r, tmpl.has(r.name)), b = behind.get(r.name);
  return { op: r.opcode, name: r.name, ...c, ms: b ? b.ms : 0, methods: b ? b.m.size : 0, cases: r.test_cases ? r.test_cases.split(';').filter((x) => x.trim()).length : 0 };
}).sort((a, b) => b.ms - a.ms || (a.op < b.op ? -1 : 1));
const body = ['opcode,name,class,reason,ms_s_behind,methods,test_cases',
  ...rows.map((r) => [r.op, r.name, r.cls, r.reason, r.ms.toFixed(3), r.methods, r.cases].map((v) => field(String(v))).join(','))].join('\n') + '\n';
const sum = (cls: string, f: (r: typeof rows[0]) => boolean = () => true) => {
  const s = rows.filter((r) => r.cls === cls && f(r)); return `${s.length} opcodes, ${s.reduce((a, r) => a + r.ms, 0).toFixed(1)} ms/s behind`;
};
console.error(`covered ${sum('covered')} (slow paths); generatable ${sum('generatable')}; hand ${sum('hand')} (throw only: ${sum('hand', (r) => r.reason === 'throw')})`);
if (process.argv.includes('--check')) {
  if (readFileSync(OUT, 'utf8').replace(/\r\n/g, '\n') !== body) { console.error(`${OUT} differs: rerun without --check`); process.exit(1); }
  console.error(`${OUT} up to date`);
} else writeFileSync(OUT, body);
