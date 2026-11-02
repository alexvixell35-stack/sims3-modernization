// Per-arm sim-thread ms/s (Rodney 2026-11-02: throttles and Mono lanes are judged by summed ms/s, not hitches or it/s; hitches
// only as a no-regression guard, hitch-ab.ts). Reads only; never talks to the game.
//   node tools/harness/arm-ms.ts <tag or tag prefix> [...]   per job (its runs pooled) and over every run given: mean ms/s per arm of
//     script, route, animPose, locomotion and their sum, then on - off. Job = tag without the date and a -rerun suffix
//     (throttle-ms-2026-11-02-bp-thr-bundle -> throttle-ms-bp-thr-bundle).
//   node tools/harness/arm-ms.ts --self-check
// Arm of each seconds row: the slice / arm columns profile-world.ts adds to <tag>-<secs>-seconds.csv after an --ab run (arm 1 on,
// 0 off, -1 not counted: warm-up slice, 15 s after each switch, a short last slice), else hitch-ab.ts rowArms over <tag>-ab.json
// (runs before the columns); a run without slices is one arm 'all' minus the first 60 s (load). Paused seconds (iters 0) are left out.
import assert from 'node:assert/strict';
import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { expandTags, rowArms } from './hitch-ab.ts';

const ROOT = process.env.TS3_REPO ?? join(import.meta.dirname, '..', '..');
const PROFILE = join(ROOT, 'probe-out', 'profile');
export const MS_COLS = ['script', 'route', 'animPose', 'locomotion'];
type Slice = { from: number; on: boolean; loop?: boolean };

// profile-world.ts after an --ab run: seconds.csv + `slice` (index of the slice the row falls in, -1 before the first) and `arm`
export function addArmColumns(csv: string, slices: Slice[]): string {
  const ls = csv.split(/\r?\n/), n = ls.findLastIndex(l => l.trim()) + 1;
  if (!n || ls[0].split(',').includes('arm')) return csv;
  const arms = rowArms(n - 1, slices);
  return ls.slice(0, n).map((l, i) => {
    if (!i) return `${l},slice,arm`;
    let cur = -1; slices.forEach((sl, k) => { if (i - 1 >= sl.from) cur = k; });
    const a = arms[i - 1];
    return `${l},${cur},${a === 'on' ? 1 : a === 'off' ? 0 : -1}`;
  }).join('\n') + '\n';
}

const csvRows = (text: string) => { const [head, ...ls] = text.trim().split(/\r?\n/); const c = head.split(','); return ls.filter(Boolean).map(l => Object.fromEntries(l.split(',').map((v, i) => [c[i], Number(v)]))); };
type Acc = { secs: number; ms: Record<string, number> };
const acc = (): Acc => ({ secs: 0, ms: Object.fromEntries(MS_COLS.map(c => [c, 0])) });

// per arm sums of one run's rows
export function runArms(rows: Record<string, number>[], slices?: Slice[]): Record<string, Acc> {
  const arms = rows.length && 'arm' in rows[0] ? rows.map(r => r.arm === 1 ? 'on' : r.arm === 0 ? 'off' : null)
    : slices ? rowArms(rows.length, slices) : rows.map((_, i) => i >= 60 ? 'all' : null);
  const out: Record<string, Acc> = {};
  rows.forEach((r, i) => {
    const a = arms[i]; if (!a || !r.iters) return;
    const x = out[a] ??= acc(); x.secs++;
    for (const c of MS_COLS) x.ms[c] += r[c] || 0;
  });
  return out;
}

function loadRun(tag: string) {
  const f = readdirSync(PROFILE).filter(n => n.startsWith(`${tag}-`) && /^\d+-seconds\.csv$/.test(n.slice(tag.length + 1)))
    .sort((a, b) => statSync(join(PROFILE, b)).mtimeMs - statSync(join(PROFILE, a)).mtimeMs)[0];
  if (!f) throw new Error(`no ${tag}-<secs>-seconds.csv in ${PROFILE}`);
  const abf = join(PROFILE, `${tag}-ab.json`), ab = existsSync(abf) ? JSON.parse(readFileSync(abf, 'utf8')) : undefined;
  const slices = ab ? (ab.slices as Slice[]).map(sl => ab.loop ? { ...sl, loop: true } : sl) : undefined;
  return runArms(csvRows(readFileSync(join(PROFILE, f), 'utf8')), slices);
}

export const jobOf = (tag: string) => tag.replace(/-\d{4}-\d{2}-\d{2}-/, '-').replace(/-rerun(-\d+)?$/, '');
const f1 = (x: number) => x.toFixed(1);
export function table(runs: { tag: string; arms: Record<string, Acc> }[]): string[] {
  const groups = new Map<string, typeof runs>();
  for (const r of runs) groups.set(jobOf(r.tag), [...(groups.get(jobOf(r.tag)) ?? []), r]);
  if (groups.size > 1) groups.set('all runs given', runs);
  const out = [`| Job | runs | arm | s | ${MS_COLS.join(' ms/s | ')} ms/s | sum ms/s |`, `|${'---|'.repeat(5 + MS_COLS.length)}`];
  for (const [job, rs] of groups) {
    const pooled: Record<string, Acc> = {};
    for (const r of rs) for (const [a, x] of Object.entries(r.arms)) {
      const p = pooled[a] ??= acc(); p.secs += x.secs;
      for (const c of MS_COLS) p.ms[c] += x.ms[c];
    }
    const mean = (x: Acc) => MS_COLS.map(c => x.secs ? x.ms[c] / x.secs : 0);
    for (const a of ['off', 'on', 'all'].filter(a => pooled[a])) {
      const m = mean(pooled[a]);
      out.push(`| ${job} | ${rs.length} | ${a} | ${pooled[a].secs} | ${m.map(f1).join(' | ')} | ${f1(m.reduce((s, v) => s + v, 0))} |`);
    }
    if (pooled.on && pooled.off) {
      const on = mean(pooled.on), off = mean(pooled.off), d = on.map((v, i) => v - off[i]);
      const so = off.reduce((s, v) => s + v, 0), sd = d.reduce((s, v) => s + v, 0);
      out.push(`| ${job} | | on - off | | ${d.map(v => `${v >= 0 ? '+' : ''}${f1(v)}`).join(' | ')} | ${sd >= 0 ? '+' : ''}${f1(sd)}${so ? ` (${sd >= 0 ? '+' : ''}${(100 * sd / so).toFixed(0)}%)` : ''} |`);
    }
  }
  return out;
}

function selfCheck() {
  // 2-min slices: warm-up on 0-119, off 120-239, on 240-359; 15 s settle after each switch
  const slices: Slice[] = [{ from: 0, on: true }, { from: 120, on: false }, { from: 240, on: true }, { from: 360, on: true }, { from: 480, on: false }];
  const head = ['iters', ...MS_COLS, 't'].join(',');
  const csv = [head, ...Array.from({ length: 600 }, (_, i) => [i === 300 ? 0 : 10, ...MS_COLS.map(() => (i >= 240 && i < 480 ? 2 : 4)), i * 1000].join(','))].join('\r\n') + '\r\n';
  const withCols = addArmColumns(csv, slices), rows = csvRows(withCols);
  assert.equal(rows.length, 600); assert.equal(withCols.split('\n')[0], `${head},slice,arm`);
  assert.deepEqual([rows[0].arm, rows[130].arm, rows[140].slice, rows[140].arm, rows[260].arm, rows[490].arm], [-1, -1, 1, 0, 1, -1]);
  assert.equal(addArmColumns(withCols, slices), withCols, 'idempotent');
  const fromCols = runArms(rows), fromAb = runArms(csvRows(csv), slices);
  assert.deepEqual(fromCols, fromAb, 'columns and ab.json agree');
  assert.equal(fromCols.off.secs, 105 + 105); assert.equal(fromCols.on.secs, 105 + 105 - 1, 'paused second left out');
  assert.equal(fromCols.on.ms.script / fromCols.on.secs, 2); assert.equal(fromCols.off.ms.script / fromCols.off.secs, 4);
  assert.equal(runArms(csvRows(csv)).all.secs, 539, 'no slices: first 60 s out, paused second out');
  assert.equal(jobOf('throttle-ms-2026-11-02-bp-thr-bundle-rerun'), 'throttle-ms-bp-thr-bundle');
  const t = table([{ tag: 'q-2026-11-02-a', arms: fromCols }, { tag: 'q-2026-11-03-a', arms: fromCols }, { tag: 'q-2026-11-02-b', arms: fromCols }]);
  assert.ok(t.some(l => l.startsWith('| q-a | | on - off | | -2.0 | -2.0 | -2.0 | -2.0 | -8.0 (-50%) |')), t.join('\n'));
  assert.ok(t.some(l => l.startsWith('| all runs given | 3 | off | 630 |')), t.join('\n'));
  console.log('arm-ms self-check ok');
}

if (resolve(process.argv[1] ?? '') === fileURLToPath(import.meta.url)) {
  const args = process.argv.slice(2);
  if (args.includes('--self-check')) selfCheck();
  else {
    const tags = expandTags(args);
    if (!tags.length) { console.error('usage: node tools/harness/arm-ms.ts <tag or tag prefix> [...] | --self-check (no runs match)'); process.exit(1); }
    console.log(table(tags.map(tag => ({ tag, arms: loadRun(tag) }))).join('\n'));
  }
}
