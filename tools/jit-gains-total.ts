// Totals of docs/data/jit-gains.csv (docs/data/jit-bench-howto.md): one row per MonoJit / compiler piece, modelled ms/s saved per
// world. Sums per world (bp / ip / sv) for the default-ON pieces, for every built piece (verdict not parked) and for the parked
// estimates, and checks every row (columns, default on|off, verdict, numbers).
//   node tools/jit-gains-total.ts [--csv <file>]
//   node tools/jit-gains-total.ts --self-check
// Modelled, not measured: a row's ms/s is (interp or compiled ns - guarded ns) x calls/s from a game count run (calls_source).
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { csvFields } from './jit-hot.ts';

export const CSV = resolve(import.meta.dirname, '../docs/data/jit-gains.csv');
export const HEADER = 'piece,switch,default,interp_ns,compiled_ns,guarded_ns,calls_s_bp,calls_s_ip,calls_s_sv,calls_source,ms_s_bp,ms_s_ip,ms_s_sv,guard_cost_ms_s,verdict,bench_commit,note';
export const VERDICTS = ['ON', 'off', 'parked', 'pending'];
const NUM = ['interp_ns', 'compiled_ns', 'guarded_ns', 'calls_s_bp', 'calls_s_ip', 'calls_s_sv', 'ms_s_bp', 'ms_s_ip', 'ms_s_sv', 'guard_cost_ms_s'];
export const WORLDS = ['bp', 'ip', 'sv'] as const;
export type Gain = Record<string, string>;
export function parse(text: string): { rows: Gain[]; errors: string[] } {
  const lines = text.split(/\r?\n/).filter(l => l.trim()), errors: string[] = [], rows: Gain[] = [];
  if (lines[0] !== HEADER) errors.push(`header differs from: ${HEADER}`);
  const cols = HEADER.split(',');
  for (const [i, l] of lines.slice(1).entries()) {
    const f = csvFields(l), at = `row ${i + 2}`;
    if (f.length !== cols.length) { errors.push(`${at}: ${f.length} fields, want ${cols.length}`); continue; }
    const r = Object.fromEntries(cols.map((c, k) => [c, f[k]]));
    if (!r.piece) errors.push(`${at}: no piece`);
    if (r.default !== 'on' && r.default !== 'off') errors.push(`${at} ${r.piece}: default "${r.default}" (want on|off)`);
    if (!VERDICTS.includes(r.verdict)) errors.push(`${at} ${r.piece}: verdict "${r.verdict}" (want ${VERDICTS.join('|')})`);
    for (const c of NUM) if (r[c] !== '' && !Number.isFinite(Number(r[c]))) errors.push(`${at} ${r.piece}: ${c} "${r[c]}" is not a number`);
    if (r.verdict !== 'parked' && r.verdict !== 'pending' && !r.bench_commit) errors.push(`${at} ${r.piece}: a judged piece needs bench_commit`);
    if (r.verdict === 'ON' && r.default !== 'on') errors.push(`${at} ${r.piece}: verdict ON but default off`);
    rows.push(r);
  }
  const dup = rows.map(r => r.piece).filter((p, i, a) => a.indexOf(p) !== i);
  for (const d of new Set(dup)) errors.push(`piece "${d}" twice`);
  return { rows, errors };
}
export function totals(rows: Gain[]) {
  const sum = (pick: (r: Gain) => boolean) => Object.fromEntries(WORLDS.map(w => [w, rows.filter(pick).reduce((s, r) => s + (Number(r[`ms_s_${w}`]) || 0), 0)])) as Record<typeof WORLDS[number], number>;
  return { on: sum(r => r.default === 'on'), built: sum(r => r.verdict !== 'parked'), parked: sum(r => r.verdict === 'parked') };
}
function selfCheck() {
  const ok = (c: boolean, m: string) => { if (!c) { console.error(`FAIL ${m}`); process.exit(1); } };
  const row = (p: string, d: string, v: string, ms: string[], commit = 'abc1234') => [p, '[X] Y', d, '10', '5', '6', '1000', '2000', '3000', 'run', ...ms, '0.01', v, commit, 'n'].map(x => `"${x}"`).join(',');
  const good = [HEADER, row('A', 'on', 'ON', ['1', '2', '3']), row('B', 'off', 'off', ['0.5', '0.5', '0.5']), row('C', 'off', 'parked', ['4', '', ''], '')].join('\n');
  const p = parse(good);
  ok(p.errors.length === 0 && p.rows.length === 3, `good csv parses: ${p.errors.join('; ')}`);
  const t = totals(p.rows);
  ok(t.on.bp === 1 && t.on.sv === 3 && t.built.ip === 2.5 && t.parked.bp === 4 && t.parked.ip === 0, 'totals: on, built, parked');
  ok(parse(good.replace('"on","10"', '"yes","10"')).errors.some(e => /default "yes"/.test(e)), 'bad default refused');
  ok(parse(good.replace('"ON","abc1234"', '"maybe","abc1234"')).errors.some(e => /verdict "maybe"/.test(e)), 'bad verdict refused');
  ok(parse(good.replace('"10","5"', '"ten","5"')).errors.some(e => /interp_ns "ten"/.test(e)), 'non-number refused');
  ok(parse(good.replace('"off","off","abc1234"', '"off","off",""').replace(row('B', 'off', 'off', ['0.5', '0.5', '0.5']), row('B', 'off', 'off', ['0.5', '0.5', '0.5'], ''))).errors.some(e => /bench_commit/.test(e)), 'judged row without a commit refused');
  ok(parse(good + '\n' + row('A', 'on', 'ON', ['1', '1', '1'])).errors.some(e => /twice/.test(e)), 'duplicate piece refused');
  ok(parse(good.replace(HEADER, HEADER.replace('note', 'notes'))).errors.length > 0, 'header checked');
  ok(parse(`${HEADER}\n"A","x"`).errors.some(e => /2 fields/.test(e)), 'short row refused');
  const real = parse(readFileSync(CSV, 'utf8'));
  ok(real.errors.length === 0, `docs/data/jit-gains.csv: ${real.errors.join('; ')}`);
  console.log('jit-gains-total self-check OK');
}
if (import.meta.main) {
  if (process.argv.includes('--self-check')) selfCheck();
  else {
    const i = process.argv.indexOf('--csv'), { rows, errors } = parse(readFileSync(i > 0 ? process.argv[i + 1] : CSV, 'utf8'));
    for (const e of errors) console.log(`ERROR ${e}`);
    const t = totals(rows), f = (x: Record<string, number>) => WORLDS.map(w => `${w} ${x[w].toFixed(2)}`).join(', ');
    console.log(`${rows.length} pieces (modelled ms/s saved, not measured)`);
    console.log(`default ON:          ${f(t.on)}`);
    console.log(`all built pieces:    ${f(t.built)}`);
    console.log(`parked (estimates):  ${f(t.parked)}`);
    if (errors.length) process.exit(1);
  }
}
