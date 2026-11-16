// Per-arm sim-thread ms/s (Rodney 2026-11-02: throttles and Mono lanes are judged by summed ms/s, not hitches or it/s; hitches
// only as a no-regression guard, hitch-ab.ts). Reads only; never talks to the game.
//   node tools/harness/arm-ms.ts <tag or tag prefix> [...]   per job (its runs pooled) and over every run given: mean ms/s per arm of
//     script, route, animPose, locomotion and their sum, then on - off. Job = tag without the date and a -rerun suffix
//     (throttle-ms-2026-11-02-bp-thr-bundle -> throttle-ms-bp-thr-bundle).
//   node tools/harness/arm-ms.ts <tag> --json                  pooled sum ms/s per job group: {job:{runs,off,on,diff,pct}}
//   node tools/harness/arm-ms.ts <tag> --gate-on-slower bp,ip  one line 'GATE run: ...' if ON is slower than OFF (or unknown) in any listed world, else 'GATE skip: ...'
//   node tools/harness/arm-ms.ts --self-check
// Arm of each seconds row: the slice / arm columns profile-world.ts adds to <tag>-<secs>-seconds.csv after an --ab run (arm 1 on,
// 0 off, -1 not counted: warm-up slice, 15 s after each switch, a short last slice), else hitch-ab.ts rowArms over <tag>-ab.json
// (runs before the columns); a run without slices is one arm 'all' minus the first 60 s (load). Paused seconds (iters 0) are left out.
import assert from 'node:assert/strict';
import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { armStats, expandTags, loadRun as hitchLoad, rowArms, THRESH, type Arm, type Stats } from './hitch-ab.ts';

const ROOT = process.env.TS3_REPO ?? join(import.meta.dirname, '..', '..');
const PROFILE = join(ROOT, 'probe-out', 'profile');
export const MS_COLS = ['script', 'route', 'animPose', 'locomotion'];
// stage columns, not in the sum (taskObjects and threaded contain script and route): the Mono interpreter runs inside Threaded, so a
// saving shows as lower Threaded ms/iter (threaded ms / iterations), not as ms/s (the sim runs flat out, ~590 ms/s Threaded)
export const STAGE_COLS = ['taskObjects', 'threaded'];
const ALL_COLS = [...MS_COLS, ...STAGE_COLS];
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
type Acc = { secs: number; iters: number; ms: Record<string, number> };
const acc = (): Acc => ({ secs: 0, iters: 0, ms: Object.fromEntries(ALL_COLS.map(c => [c, 0])) });

// per arm sums of one run's rows
export function runArms(rows: Record<string, number>[], slices?: Slice[]): Record<string, Acc> {
  const arms = rows.length && 'arm' in rows[0] ? rows.map(r => r.arm === 1 ? 'on' : r.arm === 0 ? 'off' : null)
    : slices ? rowArms(rows.length, slices) : rows.map((_, i) => i >= 60 ? 'all' : null);
  const out: Record<string, Acc> = {};
  rows.forEach((r, i) => {
    const a = arms[i]; if (!a || !r.iters) return;
    const x = out[a] ??= acc(); x.secs++; x.iters += r.iters;
    for (const c of ALL_COLS) x.ms[c] += r[c] || 0;
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
// separate-load ABBA (jobs <name>-off-1, -on-1, -on-2, -off-2): no in-load slices, so a job is one 'all' arm; the jobs of a pair are also
// pooled as <name>, each with the arm from its job name, which gives the on - off row
export const abbaOf = (tag: string) => jobOf(tag).match(/^(.*)-(off|on)-\d+$/);
type RunArms = { tag: string; arms: Record<string, Acc> };
function groupRuns(runs: RunArms[]) {
  const groups = new Map<string, RunArms[]>();
  for (const r of runs) groups.set(jobOf(r.tag), [...(groups.get(jobOf(r.tag)) ?? []), r]);
  for (const r of runs) { const m = abbaOf(r.tag); if (m && r.arms.all) groups.set(m[1], [...(groups.get(m[1]) ?? []), { tag: r.tag, arms: { [m[2]]: r.arms.all } }]); }
  if (groups.size > 1) groups.set('all runs given', runs);
  return groups;
}
// --json: per job group the pooled sum ms/s of each arm and on - off (null when an arm is missing)
export function sumsJson(runs: RunArms[]) {
  const out: Record<string, { runs: number; off: number | null; on: number | null; diff: number | null; pct: number | null }> = {};
  for (const [job, rs] of groupRuns(runs)) {
    const p: Record<string, Acc> = {};
    for (const r of rs) for (const [a, x] of Object.entries(r.arms)) { const q = p[a] ??= acc(); q.secs += x.secs; for (const c of MS_COLS) q.ms[c] += x.ms[c]; }
    const sum = (a: string) => p[a]?.secs ? MS_COLS.reduce((t, c) => t + p[a].ms[c] / p[a].secs, 0) : null;
    const off = sum('off'), on = sum('on'), diff = off !== null && on !== null ? on - off : null;
    out[job] = { runs: rs.length, off, on, diff, pct: diff !== null && off ? 100 * diff / off : null };
  }
  return out;
}
// boundcall gate: true (run it) unless every listed world's pooled group (job name ends -<world>-<suffix>) reads on - off <= 0; a world
// without a row, or without both arms, is inconclusive and counts as run
export function onSlowerOrUnknown(j: ReturnType<typeof sumsJson>, worlds: string[]): { run: boolean; why: string } {
  const why: string[] = []; let run = false;
  for (const w of worlds) {
    const k = Object.keys(j).find(n => new RegExp(`-${w}-[a-z0-9]+-abba$`).test(n)), d = k ? j[k].diff : null;
    if (d === null || d === undefined) { run = true; why.push(`${w} inconclusive`); } else { if (d > 0) run = true; why.push(`${w} on-off ${d >= 0 ? '+' : ''}${d.toFixed(1)} ms/s`); }
  }
  return { run, why: why.join(', ') };
}
export function table(runs: RunArms[]): string[] {
  const groups = groupRuns(runs);
  const out = [`| Job | runs | arm | s | ${MS_COLS.join(' ms/s | ')} ms/s | sum ms/s | ${STAGE_COLS.join(' ms/s | ')} ms/s | threaded ms/iter |`, `|${'---|'.repeat(7 + MS_COLS.length + STAGE_COLS.length)}`];
  for (const [job, rs] of groups) {
    const pooled: Record<string, Acc> = {};
    for (const r of rs) for (const [a, x] of Object.entries(r.arms)) {
      const p = pooled[a] ??= acc(); p.secs += x.secs; p.iters += x.iters;
      for (const c of ALL_COLS) p.ms[c] += x.ms[c];
    }
    const mean = (x: Acc) => MS_COLS.map(c => x.secs ? x.ms[c] / x.secs : 0);
    const stage = (x: Acc) => [...STAGE_COLS.map(c => x.secs ? x.ms[c] / x.secs : 0), x.iters ? x.ms.threaded / x.iters : 0];
    const sg = (v: number) => `${v >= 0 ? '+' : ''}${f1(v)}`;
    for (const a of ['off', 'on', 'all'].filter(a => pooled[a])) {
      const m = mean(pooled[a]);
      out.push(`| ${job} | ${rs.length} | ${a} | ${pooled[a].secs} | ${m.map(f1).join(' | ')} | ${f1(m.reduce((s, v) => s + v, 0))} | ${stage(pooled[a]).map((v, i) => i < 2 ? f1(v) : v.toFixed(2)).join(' | ')} |`);
    }
    if (pooled.on && pooled.off) {
      const on = mean(pooled.on), off = mean(pooled.off), d = on.map((v, i) => v - off[i]);
      const so = off.reduce((s, v) => s + v, 0), sd = d.reduce((s, v) => s + v, 0);
      out.push(`| ${job} | | on - off | | ${d.map(v => `${v >= 0 ? '+' : ''}${f1(v)}`).join(' | ')} | ${sd >= 0 ? '+' : ''}${f1(sd)}${so ? ` (${sd >= 0 ? '+' : ''}${(100 * sd / so).toFixed(0)}%)` : ''} | ${stage(pooled.on).map((v, i) => sg(v - stage(pooled.off)[i])).join(' | ')} |`);
    }
  }
  return out;
}

// queue.ts results rows (docs/272 §4): per arm of one run the sim ms/s (sum of MS_COLS, as the table) and hitch-ab.ts's p99 / p99.9 /
// max / counts >= 300 and >= 1000 ms over the same arms (its exclusions); null when the run left no seconds.csv. Reads files only.
export type ArmHitch = { p99: string; p999: string; max: number; n300: number; n1000: number; mins: number };
export type ArmSummary = { tag: string; arms: Record<string, Acc>; hitch: Record<string, ArmHitch>; line: string };
const I300 = THRESH.indexOf(300), I1000 = THRESH.indexOf(1000);
export const armHitch = (s: Stats): ArmHitch => ({ p99: `${s.p99.bounded ? '<' : ''}${Math.round(s.p99.ms)}`, p999: `${s.p999.bounded ? '<' : ''}${Math.round(s.p999.ms)}`, max: Math.round(s.max), n300: s.counts[I300], n1000: s.counts[I1000], mins: s.mins });
const msps = (x?: Acc) => x?.secs ? MS_COLS.reduce((t, c) => t + x.ms[c] / x.secs, 0) : null;
const sgn = (v: number, d = 1) => `${v >= 0 ? '+' : ''}${v.toFixed(d)}`;
export function armLine(arms: Record<string, Acc>, hitch: Record<string, ArmHitch>): string {
  const order = ['off', 'on', 'all'].filter(a => arms[a]?.secs);
  if (!order.length) return '';
  const h = (a: string) => hitch[a] ? `p99 ${hitch[a].p99} ms, p99.9 ${hitch[a].p999}, max ${hitch[a].max}, >=300 ${hitch[a].n300}, >=1000 ${hitch[a].n1000} (${hitch[a].mins.toFixed(1)} min)` : 'no hitch files';
  const off = msps(arms.off), on = msps(arms.on);
  const head = off !== null && on !== null ? `sim ms/s off ${f1(off)} / on ${f1(on)} (on - off ${sgn(on - off)}${off ? `, ${sgn(100 * (on - off) / off)}%` : ''})`
    : order.map(a => `sim ms/s ${a === 'all' ? '' : `${a} `}${f1(msps(arms[a])!)}`).join(', ');
  return `${head}; ${order.map(a => `${order.length > 1 ? `${a}: ` : ''}${h(a)}`).join('; ')}`;
}
export function armSummary(tag: string, load: { arms: (t: string) => Record<string, Acc>; hitch: (t: string) => { arms: Record<string, Arm> } } = { arms: loadRun, hitch: hitchLoad }): ArmSummary | null {
  let arms: Record<string, Acc>;
  try { arms = load.arms(tag); } catch (e) { if (/^Error: no .*-seconds\.csv in /.test(String(e))) return null; throw e; }
  let hitch: Record<string, ArmHitch> = {};
  try { hitch = Object.fromEntries(Object.entries(load.hitch(tag).arms).map(([a, x]) => [a, armHitch(armStats(x))])); } catch { /* no hitches.jsonl: ms/s only, the line says so */ }
  return { tag, arms, hitch, line: armLine(arms, hitch) };
}
// per-queue A/B summary: job groups as table() (in-load arms per job; separate-load <name>-off-N / -on-N pooled as <name>), only
// groups with both arms; ms/s weighted by seconds, hitches per 10 min pooled over the arm's runs ('?' when a run had no hitch files)
export function abSummary(runs: Pick<ArmSummary, 'tag' | 'arms' | 'hitch'>[]): string[] {
  const g = new Map<string, { arm: string; acc: Acc; h?: ArmHitch }[]>();
  const add = (k: string, arm: string, acc: Acc, h?: ArmHitch) => g.set(k, [...(g.get(k) ?? []), { arm, acc, h }]);
  for (const r of runs) {
    for (const [a, x] of Object.entries(r.arms)) add(jobOf(r.tag), a, x, r.hitch[a]);
    const m = abbaOf(r.tag); if (m && r.arms.all) add(m[1], m[2], r.arms.all, r.hitch.all);
  }
  const out: string[] = [];
  for (const [k, xs] of g) {
    const pool = (arm: string) => {
      const ys = xs.filter(x => x.arm === arm), secs = ys.reduce((s, y) => s + y.acc.secs, 0);
      if (!secs) return null;
      const hs = ys.every(y => y.h) ? ys.map(y => y.h!) : null, mins = hs?.reduce((s, h) => s + h.mins, 0) ?? 0;
      const per10 = (n: (h: ArmHitch) => number) => hs && mins ? (hs.reduce((s, h) => s + n(h), 0) * 10 / mins).toFixed(1) : '?';
      return { runs: ys.length, ms: ys.reduce((s, y) => s + MS_COLS.reduce((t, c) => t + y.acc.ms[c], 0), 0) / secs, p300: per10(h => h.n300), p1000: per10(h => h.n1000) };
    };
    const off = pool('off'), on = pool('on');
    if (!off || !on) continue;
    out.push(`${k}: sim ms/s off ${f1(off.ms)} / on ${f1(on.ms)}, on - off ${sgn(on.ms - off.ms)} (${off.ms ? sgn(100 * (on.ms - off.ms) / off.ms) : '?'}%); `
      + `>=300 ms per 10 min off ${off.p300} / on ${on.p300}, >=1000 off ${off.p1000} / on ${on.p1000} (${off.runs} off, ${on.runs} on run${on.runs > 1 ? 's' : ''})`);
  }
  return out;
}

function selfCheck() {
  // 2-min slices: warm-up on 0-119, off 120-239, on 240-359; 15 s settle after each switch
  const slices: Slice[] = [{ from: 0, on: true }, { from: 120, on: false }, { from: 240, on: true }, { from: 360, on: true }, { from: 480, on: false }];
  const head = ['iters', ...ALL_COLS, 't'].join(',');
  const csv = [head, ...Array.from({ length: 600 }, (_, i) => [i === 300 ? 0 : 10, ...ALL_COLS.map(() => (i >= 240 && i < 480 ? 2 : 4)), i * 1000].join(','))].join('\r\n') + '\r\n';
  const withCols = addArmColumns(csv, slices), rows = csvRows(withCols);
  assert.equal(rows.length, 600); assert.equal(withCols.split('\n')[0], `${head},slice,arm`);
  assert.deepEqual([rows[0].arm, rows[130].arm, rows[140].slice, rows[140].arm, rows[260].arm, rows[490].arm], [-1, -1, 1, 0, 1, -1]);
  assert.equal(addArmColumns(withCols, slices), withCols, 'idempotent');
  const fromCols = runArms(rows), fromAb = runArms(csvRows(csv), slices);
  assert.deepEqual(fromCols, fromAb, 'columns and ab.json agree');
  assert.equal(fromCols.off.secs, 105 + 105); assert.equal(fromCols.on.secs, 105 + 105 - 1, 'paused second left out');
  assert.equal(fromCols.on.ms.script / fromCols.on.secs, 2); assert.equal(fromCols.off.ms.script / fromCols.off.secs, 4);
  assert.equal(fromCols.on.ms.threaded / fromCols.on.iters, 0.2, 'threaded ms/iter'); assert.equal(fromCols.off.ms.taskObjects / fromCols.off.secs, 4);
  assert.equal(runArms(csvRows(csv)).all.secs, 539, 'no slices: first 60 s out, paused second out');
  assert.equal(jobOf('throttle-ms-2026-11-02-bp-thr-bundle-rerun'), 'throttle-ms-bp-thr-bundle');
  const t = table([{ tag: 'q-2026-11-02-a', arms: fromCols }, { tag: 'q-2026-11-03-a', arms: fromCols }, { tag: 'q-2026-11-02-b', arms: fromCols }]);
  assert.ok(t.some(l => l.startsWith('| q-a | | on - off | | -2.0 | -2.0 | -2.0 | -2.0 | -8.0 (-50%) | -2.0 | -2.0 | -0.2 |')), t.join('\n'));
  assert.ok(t.some(l => l.startsWith('| all runs given | 3 | off | 630 |')), t.join('\n'));
  const ab = table(['x-2026-11-13-bp-a-off-1', 'x-2026-11-13-bp-a-on-1', 'x-2026-11-13-bp-a-on-2-rerun'].map(tag => ({ tag, arms: { all: fromCols.off } })));
  assert.ok(ab.some(l => l.startsWith('| x-bp-a | | on - off | | +0.0 |')), ab.join('\n'));
  assert.ok(ab.some(l => /^\| x-bp-a \| 3 \| on \|/.test(l)), 'pair pooled over its 3 jobs');
  assert.equal(abbaOf('x-2026-11-13-bp-a-office'), null);
  const js = sumsJson(['x-2026-11-13-bp-jb-abba-off-1', 'x-2026-11-13-bp-jb-abba-on-1'].map(tag => ({ tag, arms: { all: tag.includes('-on-') ? fromCols.on : fromCols.off } })));
  assert.equal(js['x-bp-jb-abba'].diff, -8); assert.equal(js['x-bp-jb-abba'].pct, -50);
  assert.deepEqual(onSlowerOrUnknown(js, ['bp']), { run: false, why: 'bp on-off -8.0 ms/s' });
  assert.equal(onSlowerOrUnknown(js, ['bp', 'ip']).run, true, 'ip has no row: inconclusive');
  assert.equal(onSlowerOrUnknown({ 'x-ip-jb-abba': { runs: 2, off: 1, on: 2, diff: 1, pct: 100 } }, ['ip']).run, true, 'slower');
  assert.equal(onSlowerOrUnknown({}, ['bp']).run, true, 'empty');
  // results-row line and A/B summary (docs/272 §4); on the real bp-iclock-eqfast-ab run (2026-11-16) the row read "sim ms/s off 212.7 /
  // on 210.8 (on - off -1.9, -0.9%); off: p99 95 ms, p99.9 153, max 218, >=300 0, >=1000 0 (2.4 min); on: ..." as the CLI tables do
  const st = (ms: number[], secs: number, iters = 1000): Stats => armStats({ iters, secs, hitches: ms.map((wallMs, t) => ({ t, wallMs })), excl: new Map() });
  const hOff = armHitch(st([1200, 400, 120, 90], 600)), hOn = armHitch(st([310, 80], 600));
  assert.deepEqual(hOff, { p99: '<50', p999: '400', max: 1200, n300: 2, n1000: 1, mins: 10 }, 'p99 below the 50 ms hitch floor is a bound');
  const line = armLine({ off: fromCols.off, on: fromCols.on }, { off: hOff, on: hOn });
  assert.equal(line, 'sim ms/s off 16.0 / on 8.0 (on - off -8.0, -50.0%); off: p99 <50 ms, p99.9 400, max 1200, >=300 2, >=1000 1 (10.0 min); on: p99 <50 ms, p99.9 80, max 310, >=300 1, >=1000 0 (10.0 min)');
  assert.equal(armLine({ all: fromCols.off }, {}), 'sim ms/s 16.0; no hitch files');
  assert.equal(armLine({}, {}), '');
  const fake = { arms: (t: string) => { if (t === 'gone') throw new Error(`no ${t}-<secs>-seconds.csv in x`); return { off: fromCols.off, on: fromCols.on }; },
    hitch: (t: string) => { if (t === 'nohitch') throw new Error('no hitches'); return { arms: {} }; } };
  assert.equal(armSummary('gone', fake), null, 'no profile files: no line');
  assert.match(armSummary('nohitch', fake)!.line, /^sim ms\/s off 16\.0 \/ on 8\.0 .*off: no hitch files; on: no hitch files$/);
  assert.throws(() => armSummary('bad', { ...fake, arms: () => { throw new Error('csv broken'); } }), /csv broken/, 'other errors are not hidden');
  const sum = abSummary([{ tag: 'q-2026-11-16-bp-x-off-1', arms: { all: fromCols.off }, hitch: { all: hOff } }, { tag: 'q-2026-11-16-bp-x-on-1', arms: { all: fromCols.on }, hitch: { all: hOn } },
    { tag: 'q-2026-11-16-bp-x-on-2', arms: { all: fromCols.on }, hitch: {} }, { tag: 'q-2026-11-16-ip-y', arms: { off: fromCols.off, on: fromCols.on }, hitch: { off: hOff, on: hOn } }]);
  assert.deepEqual(sum, ['q-bp-x: sim ms/s off 16.0 / on 8.0, on - off -8.0 (-50.0%); >=300 ms per 10 min off 2.0 / on ?, >=1000 off 1.0 / on ? (1 off, 2 on runs)',
    'q-ip-y: sim ms/s off 16.0 / on 8.0, on - off -8.0 (-50.0%); >=300 ms per 10 min off 2.0 / on 1.0, >=1000 off 1.0 / on 0.0 (1 off, 1 on run)'], sum.join('\n'));
  console.log('arm-ms self-check ok');
}

if (resolve(process.argv[1] ?? '') === fileURLToPath(import.meta.url)) {
  const args = process.argv.slice(2);
  if (args.includes('--self-check')) selfCheck();
  else {
    const json = args.includes('--json'), gi = args.indexOf('--gate-on-slower'), gate = gi >= 0 ? args[gi + 1]?.split(',') : undefined;
    if (gi >= 0 && !gate) { console.error('--gate-on-slower needs a world list, e.g. bp,ip'); process.exit(1); }
    const tags = expandTags(args.filter((a, k) => !a.startsWith('--') && !(gi >= 0 && k === gi + 1)));
    // gate with no runs yet: inconclusive, which runs the gated queue
    if (!tags.length && gate) { console.log('GATE run: no runs match, inconclusive'); process.exit(0); }
    if (!tags.length) { console.error('usage: node tools/harness/arm-ms.ts <tag or tag prefix> [...] [--json | --gate-on-slower bp,ip] | --self-check (no runs match)'); process.exit(1); }
    const runs = tags.map(tag => ({ tag, arms: loadRun(tag) }));
    if (gate) { const g = onSlowerOrUnknown(sumsJson(runs), gate); console.log(`GATE ${g.run ? 'run' : 'skip'}: ${g.why}`); }
    else if (json) console.log(JSON.stringify(sumsJson(runs), null, 1));
    else console.log(table(runs).join('\n'));
  }
}
