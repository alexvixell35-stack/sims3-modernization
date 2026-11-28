// Hitches players notice, per run and per A/B arm (docs/161). Reads only; never talks to the game.
//   node tools/harness/hitch-ab.ts <tag or tag prefix> [...]           per arm: iterations, p99, p99.9, max, counts >= 100/300/500/1000/2000 ms
//   node tools/harness/hitch-ab.ts --causes [--wh worst-hitches.json] [--arms off,all] <tag or prefix> [...]   cause ranking of >= 300 / >= 1000 ms
//   node tools/harness/hitch-ab.ts --pool <label> [--wh worst-hitches.json] [--arms off,all] <tag or prefix> [...]   one row: those arms of every run pooled
//   node tools/harness/hitch-ab.ts --scenario <tag or prefix> [...]   harness-triggered windows as the scenario's own cost, per step (docs/168 §3), plus
//     per arm (or step) the top causes and worst 10 of the iterations >= 300 ms inside them (freeze-report.ts reproCauses); setup
//     steps (repro-loop.ts isSetup) go to '<arm> setup' rows, out of the arm and its confidence rows (docs/168 §7)
//   node tools/harness/hitch-ab.ts --power [--reduction 0.3] [--arm-min 3.5]   runs needed to see a reduction, from the docs/161 baseline rates
//   node tools/harness/hitch-ab.ts --reuse <job id, repro id or tag> [--since YYYY-MM-DD]   past runs whose timing verdict (docs/168 §10)
//     is valid (in-load A/B) or valid-single (paired older -> newer per job, build differences flagged; cross-run = weaker evidence)
//   node tools/harness/hitch-ab.ts --self-check
// Pairs and pools also print 95% confidence intervals (exact Poisson per arm, exact conditional on/off ratio, docs/168 §1) for
// iterations >= 300/500/1000/2000 ms and visible freezes (<tag>-<secs>-freezes.jsonl, real publish gaps as in freeze-report.ts).
// Inputs: probe-out/profile/<tag>-<secs>-hitches.jsonl (one line per Simulate iteration >= HITCH_MS) and -seconds.csv (iters per
// second: the total count, so p99 is over every iteration, not only the slow ones), <tag>-ab.json (--ab slices), and for --causes
// probe-out/queue/<tag>-HHMM-ts3native.log ('sync plan N ms, IC X, reason R' lines, no time stamp: matched to a hitch by duration)
// plus worst-hitches.ts --json (managed task roots per hitch, aligned there).
// Arms: an --ab run gives on/off (profile-compare.mjs rules: warm-up slice 0 out, 15 s after each switch out, a last slice under
// half length out); any other run gives 'all' (first 60 s out: load). Pairs: <base>-offfirst + <base>-onfirst (both orders: pair
// means, on - off), <base>-off[-N] + <base>-on[-N] (two loads: on - off).
// Import hitchReport(tags) from queue result notes. Set TS3_REPO=<main checkout> when running from a git worktree.
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { keyName } from './hitch-attr-report.ts';
import { baseWins, cause, exclAdd, exclTable, exclusion, firingWins, queueEvents, reproSteps, rowLongest, runContext, STEP_POST_MS, type ExclRow, type RunCtx } from './worst-hitches.ts';
import { load as loadFreeze, reproCauses, reproFires } from './freeze-report.ts';
import { jsonlRows, rowTiming } from './run-verdict.ts'; // cycle with run-verdict.ts: both use the other only inside functions

const ROOT = process.env.TS3_REPO ?? join(import.meta.dirname, '..', '..');
const PROFILE = join(ROOT, 'probe-out', 'profile'), QUEUE = join(ROOT, 'probe-out', 'queue');
export const HITCH_MS = 50; // profile-world.ts starts stutter-profile.js with hitchMs 50: faster iterations are not in hitches.jsonl
export const THRESH = [100, 200, 250, 300, 500, 1000, 2000]; // 200/250: noticeable-hitch targets (Rodney 2026-10-26)
const SKIP_S = 60, SETTLE_S = 15;

type H = Record<string, any> & { t: number; wallMs: number };
type Row = Record<string, number>;
type Slice = { from: number; on: boolean; loop?: boolean }; // loop: an in-load loop firing (profile-world.ts, <tag>-ab.json loop: true)
export type Arm = { iters: number; secs: number; hitches: H[]; excl: Map<string, ExclRow>; freezes?: number[] }; // hitches, freezes = real ones only
type Fz = { t0: number; t1: number; gapMs: number; iters: number };
type Pct = { ms: number; bounded: boolean };

// arm of each seconds row (row i ~ second i of the run); without slices every row is 'all' (the first minute goes out as an
// excluded window in splitRun, so its hitches are listed)
// which --ab slices count: slice 0 (warm-up) out, a last slice under half length out; loop firings: every one after slice 0
const sliceKeep = (n: number, slices: Slice[]) => {
  if (slices[1]?.loop) return slices.map((_, i) => i > 0);
  const len = slices.length > 1 ? slices[1].from - slices[0].from : Infinity;
  return slices.map((sl, i) => i > 0 && (i < slices.length - 1 || n - sl.from >= len / 2));
};
// A/B iterations per arm of one load (timing verdict, docs/168 §10): repro firings by their '[arm|fire on|off #n]' step lines
// (repro-loop --ab), else the kept --ab slices; null = no A/B in this run
export function armIters(steps: string[] | undefined, slices: Slice[] | undefined, nRows: number): { on: number; off: number; src: 'repro' | 'slices' } | null {
  const it = { on: new Set<number>(), off: new Set<number>() };
  for (const s of steps ?? []) { const m = /\[[^\]]*\b(on|off) #(\d+)\]/.exec(s); if (m) it[m[1] as 'on' | 'off'].add(+m[2]); }
  if (it.on.size || it.off.size) return { on: it.on.size, off: it.off.size, src: 'repro' };
  if (!slices) return null;
  const keep = sliceKeep(nRows, slices);
  return { on: slices.filter((s, i) => keep[i] && s.on).length, off: slices.filter((s, i) => keep[i] && !s.on).length, src: 'slices' };
}
export const runIters = (r: { tag: string; secs: number; rows: unknown[]; slices?: Slice[] }) => armIters(reproSteps(r.tag, r.secs), r.slices, r.rows.length);

// Arms by time, not row index (docs/51 gchold-stalls triage): a row is stamped `t` (QPC ms, end of its span) and a long stall or merged
// iterations write ONE row for 5-11 s, so row i is not second i. tMs = each row's t: second of a row = (t - first t) / 1000, compared with
// the slice start (--ab slices are wall seconds). A row longer than 1.5 s counts only when its first second lies in the same kept, settled
// slice as its end; otherwise it is out (null). Without tMs (no t column) row i = second i as before.
export function rowArms(n: number, slices?: Slice[], tMs?: number[]): (string | null)[] {
  if (!slices) return Array.from({ length: n }, () => 'all');
  const end = Array.from({ length: n }, (_, i) => tMs && tMs.length === n && tMs.every(Number.isFinite) ? (tMs[i] - tMs[0]) / 1000 : i);
  const keep = sliceKeep(tMs ? Math.max(n, Math.ceil(end[n - 1] ?? 0)) : n, slices);
  const at = (sec: number) => {
    let cur = -1; slices.forEach((sl, k) => { if (sec >= sl.from) cur = k; });
    return cur >= 0 && keep[cur] && sec - slices[cur].from >= (slices[cur].loop ? 0 : SETTLE_S) ? cur : -1;
  };
  return end.map((e, i) => {
    const cur = at(e), len = i ? e - end[i - 1] : 1;
    if (cur < 0 || (len > 1.5 && at(e - len + 1) !== cur)) return null;
    return slices[cur].on ? 'on' : 'off';
  });
}

// a hitch belongs to the first row whose t (QPC ms, end of that second) is >= its own t. Real vs excluded (worst-hitches.ts
// exclusion, docs/161 §6): a row inside an excluded window leaves the arm (its seconds counted by reason); a hitch is excluded
// by its own window or its row's; an excluded hitch in a kept row also leaves the iteration count, so p99 is over real ones
export function splitRun(rows: Row[], hitches: H[], slices?: Slice[], ctx: Pick<RunCtx, 'wins' | 'foreground'> = { wins: baseWins(rows), foreground: false }, freezes: Fz[] = []): Record<string, Arm> {
  const arms = rowArms(rows.length, slices, rows.map(r => r.t)), out: Record<string, Arm> = {};
  const rl = rowLongest(rows, hitches), rowWhy = rows.map((r, i) => exclusion({ a: i ? rows[i - 1].t : r.t - 1000, b: r.t, iter: rl[i] }, ctx)?.why ?? null);
  rows.forEach((r, i) => {
    const a = arms[i]; if (!a) return;
    const x = out[a] ??= { iters: 0, secs: 0, hitches: [], excl: new Map() };
    if (rowWhy[i]) exclAdd(x.excl, rowWhy[i]!, 0, 1); else { x.iters += r.iters; x.secs++; }
  });
  let i = 0;
  for (const h of [...hitches].sort((a, b) => a.t - b.t)) {
    while (i < rows.length && rows[i].t < h.t) i++;
    const a = arms[i], x = a ? out[a] : undefined; if (!x) continue;
    const why = exclusion({ a: h.t - h.wallMs, b: h.t, wallMs: h.wallMs, steps: h.steps, iter: h }, ctx)?.why ?? (rowWhy[i] === 'probe call' ? null : rowWhy[i]); // own check judged the calls (docs/178 T2)
    if (!why) { x.hitches.push(h); continue; }
    exclAdd(x.excl, why, h.wallMs);
    if (!rowWhy[i]) x.iters--;
  }
  // publish gaps (visible freezes) as freeze-report.ts: real unless exclusion() with the longest iteration inside, or its row, says not
  if (freezes.length) for (const x of Object.values(out)) x.freezes = [];
  for (const f of freezes) {
    const j = rows.findIndex(r => r.t >= f.t1), a = j < 0 ? null : arms[j], x = a ? out[a] : undefined; if (!x || (rowWhy[j] && rowWhy[j] !== 'probe call')) continue;
    const main = hitches.filter(h => h.t - h.wallMs < f.t1 && h.t > f.t0).sort((p, q) => q.wallMs - p.wallMs)[0];
    if (!exclusion({ a: f.t0, b: f.t1, gap: { longestIterMs: main?.wallMs ?? 0, iters: f.iters }, iter: main }, ctx)) x.freezes!.push(f.gapMs);
  }
  return out;
}

// nearest rank over all iterations; ranks below the slowest HITCH_MS-filtered ones are only known to be < HITCH_MS
export function armStats(a: Arm) {
  const w = a.hitches.map(h => h.wallMs).sort((x, y) => y - x), N = a.iters;
  const pct = (p: number): Pct => { const k = N - Math.ceil(p * N); return k >= 0 && k < w.length ? { ms: w[k], bounded: false } : { ms: HITCH_MS, bounded: true }; };
  const counts = THRESH.map(t => w.filter(x => x >= t).length);
  return { iters: N, mins: a.secs / 60, p99: pct(0.99), p999: pct(0.999), max: w[0] ?? 0, counts, per10: counts.map(c => a.secs ? c * 600 / a.secs : 0) };
}
export type Stats = ReturnType<typeof armStats>;

const jsonl = (f: string) => existsSync(f) ? readFileSync(f, 'utf8').split('\n').filter(Boolean).flatMap(l => { try { return [JSON.parse(l)]; } catch { return []; } }) : [];
const csvRows = (text: string) => { const [head, ...ls] = text.trim().split(/\r?\n/); const c = head.split(','); return ls.filter(Boolean).map(l => Object.fromEntries(l.split(',').map((v, i) => [c[i], Number(v)]))); };

export function loadRun(tag: string) {
  // a tag reused for two lengths: the newest file wins
  const hf = readdirSync(PROFILE).filter(f => f.startsWith(`${tag}-`) && /^\d+-hitches\.jsonl$/.test(f.slice(tag.length + 1)))
    .sort((a, b) => statSync(join(PROFILE, b)).mtimeMs - statSync(join(PROFILE, a)).mtimeMs)[0];
  if (!hf) throw new Error(`no ${tag}-<secs>-hitches.jsonl in ${PROFILE}`);
  const rows = csvRows(readFileSync(join(PROFILE, hf.replace('-hitches.jsonl', '-seconds.csv')), 'utf8')), hitches: H[] = jsonl(join(PROFILE, hf));
  const abf = join(PROFILE, `${tag}-ab.json`), ab = existsSync(abf) ? JSON.parse(readFileSync(abf, 'utf8')) : undefined;
  const slices = ab ? (ab.slices as Slice[]).map(sl => ab.loop ? { ...sl, loop: true } : sl) : undefined;
  const secs = Number(/-(\d+)-hitches/.exec(hf)![1]), ctx = runContext(tag, rows, secs);
  const freezes: Fz[] = jsonl(join(PROFILE, hf.replace('-hitches.jsonl', '-freezes.jsonl'))); // none before docs/162: arms get no freezes
  // invalid: profiler cut short, noisy machine, crash (worst-hitches.ts runInvalid): shown, left out of pairs and pools
  return { tag, secs, rows, hitches, freezes, slices, ctx, arms: splitRun(rows, hitches, slices, loopCtx(ctx, slices), freezes), invalid: ctx.invalid };
}

// in-load loop arms (docs/189 §16): the whole firing window is the arm, so its repro-triggered windows stay in (they are what
// the arm measures); probe calls, sampler, profiler edges and the first minute still go out. --scenario keeps its own view.
export const loopCtx = <C extends Pick<RunCtx, 'wins'>>(ctx: C, slices?: Slice[]): C =>
  slices?.[1]?.loop ? { ...ctx, wins: ctx.wins.filter(w => w.why !== 'repro-triggered') } : ctx;

// tags given as full tags or prefixes of profile tags
export function expandTags(args: string[]) {
  const all = [...new Set(readdirSync(PROFILE).filter(f => /-\d+-hitches\.jsonl$/.test(f)).map(f => f.replace(/-\d+-hitches\.jsonl$/, '')))];
  return [...new Set(args.flatMap(a => all.includes(a) ? [a] : all.filter(t => t.startsWith(a)).sort()))];
}

const f0 = (x: number) => x.toFixed(0), f1 = (x: number) => x.toFixed(1);
const pc = (p: Pct) => p.bounded ? `<${HITCH_MS}` : f0(p.ms);
const HEAD = `| Run | Arm | iters | min | it/s | p99 ms | p99.9 ms | max ms | ${THRESH.map(t => `>=${t}`).join(' | ')} | per 10 min ${THRESH.join('/')} | excluded n (>= 300) |\n|${'---|'.repeat(10 + THRESH.length)}`;
const exN = (a: Arm) => { let n = 0, n3 = 0; for (const r of a.excl.values()) { n += r.n; n3 += r.n300; } return `${n} (${n3})`; };
const line = (name: string, arm: string, s: Stats, a: Arm) => `| ${name} | ${arm} | ${s.iters} | ${f1(s.mins)} | ${f1(s.mins ? s.iters / (s.mins * 60) : 0)} | ${pc(s.p99)} | ${pc(s.p999)} | ${f0(s.max)} | ${s.counts.join(' | ')} | ${s.per10.map(f1).join('/')} | ${exN(a)} |`;
export const mergeExcl = (arms: Arm[]) => {
  const t = new Map<string, ExclRow>();
  for (const a of arms) for (const [k, r] of a.excl) { const x = t.get(k) ?? { n: 0, ms: 0, n300: 0, n1000: 0, max: 0, secs: 0 }; t.set(k, { n: x.n + r.n, ms: x.ms + r.ms, n300: x.n300 + r.n300, n1000: x.n1000 + r.n1000, max: Math.max(x.max, r.max), secs: x.secs + r.secs }); }
  return t;
};

// mean of several arm stats; a bounded percentile counts as HITCH_MS and the mean is marked '<='
function meanStats(xs: Stats[]) {
  const m = (f: (s: Stats) => number) => xs.reduce((a, s) => a + f(s), 0) / xs.length;
  const mp = (k: 'p99' | 'p999') => ({ ms: m(s => s[k].ms), bounded: xs.some(s => s[k].bounded) });
  return { p99: mp('p99'), p999: mp('p999'), max: m(s => s.max), per10: THRESH.map((_, i) => m(s => s.per10[i])) };
}
const mp = (p: Pct) => `${p.bounded ? '<=' : ''}${f0(p.ms)}`;
const delta = (on: number, off: number) => `${on - off >= 0 ? '+' : ''}${f1(on - off)}${off ? ` (${on - off >= 0 ? '+' : ''}${f0(100 * (on - off) / off)}%)` : ''}`;

// ---- confidence (docs/168 §1). Iterations >= T ms and freezes >= T ms are counted as Poisson events over real minutes: exact
// (Garwood) interval per arm, exact conditional (Clopper-Pearson) interval for the on/off rate ratio, Wald interval for the
// difference. [inferred] slow iterations cluster (one stall re-plans several Sims), so the true spread is wider: the both-orders
// pair agreeing is the check. Verdict rule: fewer than MIN_EVENTS events in both arms together = 'not enough events'.
export const MIN_EVENTS = 10, CI_THRESH = [300, 500, 1000, 2000];
const Z95 = 1.959964, Z80 = 0.841621;
// root of a decreasing f on [lo, hi]
const root = (f: (x: number) => number, lo: number, hi: number) => { for (let i = 0; i < 100; i++) { const m = (lo + hi) / 2; if (f(m) > 0) lo = m; else hi = m; } return (lo + hi) / 2; };
const sumExp = (ls: number[]) => { const m = Math.max(...ls); return Math.exp(m) * ls.reduce((s, x) => s + Math.exp(x - m), 0); };
export function poisCdf(k: number, mu: number) {
  if (k < 0) return 0; if (mu <= 0) return 1;
  const ls = [-mu]; for (let i = 1; i <= k; i++) ls.push(ls[i - 1] + Math.log(mu / i));
  return Math.min(1, sumExp(ls));
}
export function binomCdf(k: number, n: number, p: number) {
  if (k < 0) return 0; if (k >= n) return 1;
  const ls = [n * Math.log1p(-p)]; for (let i = 1; i <= k; i++) ls.push(ls[i - 1] + Math.log((n - i + 1) / i) + Math.log(p / (1 - p)));
  return Math.min(1, sumExp(ls));
}
export function poisCI(k: number): [number, number] {
  const hi = k + 10 * Math.sqrt(k) + 10;
  return [k ? root(mu => poisCdf(k - 1, mu) - 0.975, 0, hi) : 0, root(mu => poisCdf(k, mu) - 0.025, 0, hi)];
}
export function cpCI(k: number, n: number): [number, number] {
  if (!n) return [0, 1];
  return [k ? root(p => binomCdf(k - 1, n, p) - 0.975, 0, 1) : 0, k < n ? root(p => binomCdf(k, n, p) - 0.025, 0, 1) : 1];
}
type Ev = { k: number; mins: number };
// rates per `per` minutes (10 = docs/161's unit, 60 = per hour)
export function rateCI(e: Ev, per = 10) { const [lo, hi] = poisCI(e.k), s = e.mins ? per / e.mins : NaN; return { est: e.k * s, lo: lo * s, hi: hi * s }; }
export function rateCompare(on: Ev, off: Ev, per = 10) {
  const n = on.k + off.k, r1 = on.k / on.mins, r0 = off.k / off.mins, se = Math.sqrt(on.k / on.mins ** 2 + off.k / off.mins ** 2);
  const odds = (p: number) => p >= 1 ? Infinity : p / (1 - p) * off.mins / on.mins, [pl, ph] = cpCI(on.k, n);
  const ratio = { est: r0 ? r1 / r0 : n ? Infinity : NaN, lo: odds(pl), hi: odds(ph) };
  const verdict = n < MIN_EVENTS ? `not enough events (${n} < ${MIN_EVENTS})` : ratio.hi < 1 ? 'fewer with on' : ratio.lo > 1 ? 'more with on' : 'no clear difference';
  return { n, diff: { est: (r1 - r0) * per, lo: (r1 - r0 - Z95 * se) * per, hi: (r1 - r0 + Z95 * se) * per }, ratio, verdict };
}
// runs to see a given reduction at two-sided 5% with 80% power (normal approximation on the log rate ratio; always well above
// MIN_EVENTS expected events); armMin = real minutes per arm per run (a 10 min --ab run, slice 2: 2 slices x (2 min - 15 s) = 3.5)
export function runsNeeded(per10: number, reduction: number, armMin: number) {
  if (!(per10 > 0) || !(reduction > 0 && reduction < 1)) return null;
  const l0 = per10 / 10, l1 = l0 * (1 - reduction);
  const mins = (Z95 + Z80) ** 2 * (1 / l1 + 1 / l0) / Math.log(1 - reduction) ** 2;
  return { minsPerArm: mins, runs: Math.ceil(mins / armMin) };
}
// docs/161 §2 real-only baseline, per 10 min at >= 100/300/500/1000/2000 ms (2026-10-22)
export const BASELINE: [string, number[]][] = [['BP play', [182.2, 5.4, 0.4, 0.4, 0]], ['BP crowd/route-bits', [99.5, 2.8, 0, 0, 0]],
  ['IP play', [529.9, 20.1, 2.8, 0.5, 0]], ['SV play', [86.6, 1.9, 0.9, 0, 0]], ['SV crowd/route-bits', [83.5, 9.0, 2.6, 2.6, 1.3]]];
export function powerReport(reduction = 0.3, armMin = 3.5) {
  const L = [`Runs needed to see a ${f0(100 * reduction)}% reduction (two-sided 5%, power 80%), ${armMin} real min per arm per run; a both-orders pair = 2 runs`,
    '', `| World / set | ${THRESH.map(t => `>= ${t} ms: per 10 min -> runs`).join(' | ')} |`, `|${'---|'.repeat(1 + THRESH.length)}`];
  for (const [name, r] of BASELINE) L.push(`| ${name} | ${r.map(x => { const n = runsNeeded(x, reduction, armMin); return n ? `${x} -> ${n.runs}` : `${x} -> no events`; }).join(' | ')} |`);
  return L.join('\n');
}
const evOf = (arms: Arm[], src: 'hitches' | 'freezes', t: number): Ev => ({
  k: arms.reduce((s, a) => s + (src === 'hitches' ? a.hitches.filter(h => h.wallMs >= t).length : (a.freezes ?? []).filter(g => g >= t).length), 0),
  mins: arms.reduce((s, a) => s + a.secs / 60, 0) });
const ci = (x: { est: number; lo: number; hi: number }) => `${f1(x.est)} [${f1(x.lo)}, ${f1(x.hi)}]`;
const cr = (x: { est: number; lo: number; hi: number }) => `${x.est.toFixed(2)} [${x.lo.toFixed(2)}, ${Number.isFinite(x.hi) ? x.hi.toFixed(2) : 'inf'}]`;
const srcs = (arms: Arm[]) => arms.every(a => a.freezes) ? ['hitches', 'freezes'] as const : ['hitches'] as const;
export const CI_HEAD = `| Pair | events | >= ms | off: n, per 10 min [95% CI] | on: n, per 10 min [95% CI] | on - off per 10 min [95% CI] | on / off [95% CI] | verdict |\n|---|---|---|---|---|---|---|---|`;
export function ciPairRows(title: string, on: Arm[], off: Arm[]) {
  return srcs([...on, ...off]).flatMap(src => CI_THRESH.map(t => {
    const a = evOf(on, src, t), b = evOf(off, src, t), c = rateCompare(a, b);
    return `| ${title} | ${src === 'hitches' ? 'iterations' : 'freezes'} | ${t} | ${b.k}, ${ci(rateCI(b))} | ${a.k}, ${ci(rateCI(a))} | ${c.n ? ci(c.diff) : '-'} | ${c.n ? cr(c.ratio) : '-'} | ${c.verdict} |`;
  }));
}
export function ciPoolRows(arms: Arm[]) {
  return [`| events | >= ms | n | per hour [95% CI] | per 10 min [95% CI] |`, '|---|---|---|---|---|', ...srcs(arms).flatMap(src => CI_THRESH.map(t => {
    const e = evOf(arms, src, t);
    return `| ${src === 'hitches' ? 'iterations' : 'visible freezes'} | ${t} | ${e.k} | ${ci(rateCI(e, 60))} | ${ci(rateCI(e))} |`;
  }))].join('\n');
}

// Measurement cost rule (iv), PLAN.md 2026-10-24 / docs/168 §9: which measurement tools a run had on, from its queue row (ini
// overrides + run.profile), and the cost docs/data/tool-costs.json knows for them.
// The ini is the one the job ran with: queue.ts writes Bin/ts3native.ini = repo ini + job ini, so toolLines() lays the row's
// overrides over the repo ini at the row's build (withBaseIni), and a tool the repo ini turns on is listed too.
// shortcut: the repo ini is read at the committed build; uncommitted ini edits at queue start are missed, upgrade by storing
// the merged ini in the queue row if that ever bites.
export function toolsOn(row: { ini?: Record<string, unknown> | null; run?: { profile?: Record<string, any> } }): string[] {
  const ini = row.ini ?? {}, p = row.run?.profile ?? {}, n = (k: string) => Number(ini[k] ?? 0), out: string[] = [];
  if (!p.noProf && !n('ProbeSnap.Enabled')) {
    out.push(p.profOpts?.stages === false ? 'prof-lean' : 'prof-stages');
    if (p.profOpts?.stages !== false && p.profOpts?.tasks !== false) out.push('prof-tasks');
    if (!p.noFrames && !p.watchstalk) out.push('frames');
  }
  // the stack samplers run only in a plain window (profile-world.ts `window`)
  if (!p.noSamplers && !(p.ab || p.taskstall || p.repro || p.inroom || p.watchstalk || p.livereads2 || p.f4b || p.vres || p.vresDry || p.story)) out.push('samplers');
  for (const k of ['mainstack', 'taskstall', 'inroom', 'watchstalk', 'livereads2', 'f4b', 'vres', 'story']) if (p[k]) out.push(k);
  for (const s of ['HitchAttr', 'RouteJobPhases', 'UnseenProbe', 'PlanRepeat', 'CellMemoProbe', 'ProbeP1b', 'ProbeP10', 'GcThreadCheck', 'IcLockCheck', 'LoadProf', 'ProbeValues', 'ProbeSnap', 'Watchpoint', 'Verify', 'MovedWaits'])
    if (n(`${s}.Enabled`)) out.push(s);
  // docs/67 §18: the light parts (Light=1, or under Enabled=1) vs the guard switches (Enabled=1 only; Guards defaults to 1)
  if (n('CrashForensics.Enabled') || n('CrashForensics.Light')) out.push('CrashForensics.Light');
  if (n('CrashForensics.Enabled')) for (const [k, d] of [['Guards', 1], ['DrawGuards', 0], ['EffectLog', 0], ['ObjResLog', 0], ['DbIndexLog', 0], ['SceneTreeGuard', 0]] as const)
    if (Number(ini[`CrashForensics.${k}`] ?? d)) out.push(`CrashForensics.${k}`);
  if (n('ProbeSnap.CallSites') && !n('ProbeSnap.Enabled')) out.push('ProbeSnap.CallSites'); // docs/133 §7 coverage (Enabled wins)
  if (n('HitchAttr.Enabled') && n('HitchAttr.Holders')) out.push('HitchAttr.Holders');
  if (n('RouteJobPhases.Enabled')) for (const k of ['SyncPlans', 'HostSplit', 'GoalSplit', 'GoalStepSplit']) if (n(`RouteJobPhases.${k}`)) out.push(`RouteJobPhases.${k}`);
  if (ini['RouteAsync.LogFallbackMs'] !== undefined && n('RouteAsync.LogFallbackMs') > 0 && n('RouteAsync.LogFallbackMs') < 200) out.push('LogFallbackMs<200');
  if (n('RouteAsync.LogFallbackStack')) out.push('LogFallbackStack');
  if (n('RouteAsync.OwnJobWait')) out.push(String(p.ab ?? '').split(',').includes('ownjobwait') ? 'OwnJobWait (on arm)' : 'OwnJobWait');
  if (n('RouteEarlyOut.Enabled') && !n('RouteEarlyOut.Act')) out.push(String(p.ab ?? '').split(',').includes('earlyout') ? 'RouteEarlyOut.observe (on arm)' : 'RouteEarlyOut.observe');
  if (n('RouteSnap.Enabled') && (ini['RouteSnap.AuditEvery'] === undefined || n('RouteSnap.AuditEvery'))) out.push('RouteSnap.audits');
  return out;
}
export type ToolCosts = { tools: Record<string, { class: string; msPerS: number | null; timing?: string }>; bundles: { name: string; tools: string[]; msPerS: number }[] };
export function toolCostLine(tools: string[], costs: ToolCosts): string {
  const base = (t: string) => t.replace(/ \(on arm\)$/, ''), left = new Set(tools.map(base)), taken: string[] = [];
  let ms = 0;
  for (const b of costs.bundles) if (b.tools.every(t => left.has(t))) { ms += b.msPerS; taken.push(b.name); b.tools.forEach(t => left.delete(t)); }
  for (const t of [...left]) { const c = costs.tools[t]?.msPerS; if (typeof c === 'number') { ms += c; taken.push(t); left.delete(t); } }
  const heavy = [...left].filter(t => costs.tools[t]?.class === 'heavy'), unknown = [...left].filter(t => !costs.tools[t]);
  const nm = [...left].filter(t => costs.tools[t] && !heavy.includes(t));
  return `tools on: ${tools.join(', ') || 'none'}; cost taken off: ${taken.length ? `${ms.toFixed(1)} ms/s (${taken.join(', ')})` : '0 ms/s'}`
    + (nm.length ? `; not measured: ${nm.join(', ')}` : '') + (heavy.length ? `; HEAVY (correctness only, no timing): ${heavy.join(', ')}` : '')
    + (unknown.length ? `; not in tool-costs.json: ${unknown.join(', ')}` : '');
}
// ts3native.ini text -> { 'Section.Key': value } (comments and trailing '; ...' dropped)
export function flatIni(text: string): Record<string, string> {
  const o: Record<string, string> = {};
  let sec = '';
  for (const l of text.split(/\r?\n/)) {
    const h = /^\s*\[([^\]]+)\]/.exec(l), kv = /^\s*([^;=\s][^=]*?)\s*=\s*(.*)$/.exec(l);
    if (h) sec = h[1]; else if (kv && sec) o[`${sec}.${kv[1]}`] = kv[2].replace(/\s*;.*$/, '').trim();
  }
  return o;
}
// the row's ini overrides on top of the repo ini text at its build; base null (build unknown or not in git) = overrides only
export const withBaseIni = <R extends { ini?: Record<string, unknown> | null }>(row: R, base: string | null): R => ({ ...row, ini: { ...(base ? flatIni(base) : {}), ...(row.ini ?? {}) } });
const iniAt = new Map<string, string | null>();
export function repoIniAt(build: unknown): string | null {
  if (typeof build !== 'string' || !/^[0-9a-f]{4,40}$/.test(build)) return null;
  if (!iniAt.has(build)) try { iniAt.set(build, execFileSync('git', ['-C', ROOT, 'show', `${build}:native/ts3native/ts3native.ini`], { stdio: ['ignore', 'pipe', 'ignore'], timeout: 30000 }).toString()); } catch { iniAt.set(build, null); }
  return iniAt.get(build)!;
}
const TOOL_COSTS = resolve(import.meta.dirname, '..', '..', 'docs', 'data', 'tool-costs.json');
export const toolCosts = (): ToolCosts => JSON.parse(readFileSync(TOOL_COSTS, 'utf8'));
// the queue row of a profile tag (probe-out/queue/<queue>-<date>.jsonl, row.profileTag), newest row wins; null = run outside a queue
function queueRow(tag: string): any {
  if (!existsSync(QUEUE)) return null;
  let found: any = null;
  for (const f of readdirSync(QUEUE).filter(f => f.endsWith('.jsonl') && tag.startsWith(f.slice(0, -6) + '-')))
    for (const l of readFileSync(join(QUEUE, f), 'utf8').split('\n')) if (l.includes(`"profileTag":"${tag}"`)) { try { found = JSON.parse(l); } catch { /* torn line */ } }
  return found;
}
function toolLines(tags: string[]): string[] {
  const costs: ToolCosts = JSON.parse(readFileSync(TOOL_COSTS, 'utf8'));
  return tags.map(t => {
    const r = queueRow(t); if (!r) return `- ${t}: tools on: unknown (no queue row)`;
    const base = repoIniAt(r.build);
    return `- ${t}: ${toolCostLine(toolsOn(withBaseIni(r, base)), costs)}${base ? '' : ` (repo ini at build ${r.build} not readable: job overrides only)`}`;
  });
}

// cross: extra pairs of two separate runs (--reuse valid-single pairs), shown as older / newer in the off / on columns
export function hitchReport(args: string[], cross: { title: string; older: string; newer: string }[] = []): string {
  const loaded = expandTags(args).map(loadRun), bad = loaded.filter(r => r.invalid).map(r => `${r.tag} (${r.invalid})`);
  const runs = new Map(loaded.filter(r => !r.invalid).map(r => [r.tag, r])), tags = [...runs.keys()];
  const out = [HEAD], shown: Arm[] = [];
  for (const r of loaded) for (const a of ['all', 'off', 'on']) if (r.arms[a]) { out.push(line(r.invalid ? `${r.tag} (INVALID)` : r.tag, a, armStats(r.arms[a]), r.arms[a])); if (!r.invalid) shown.push(r.arms[a]); }
  const pairs: string[] = [], cis: string[] = [], twoLoads: [string, Arm, Arm][] = [];
  const pair = (title: string, onA: Arm[], offA: Arm[]) => {
    const a = meanStats(onA.map(armStats)), b = meanStats(offA.map(armStats));
    pairs.push(`| ${title} | ${mp(b.p99)} / ${mp(a.p99)} | ${mp(b.p999)} / ${mp(a.p999)} | ${f0(b.max)} / ${f0(a.max)} (${delta(a.max, b.max)}) | ${THRESH.map((_, i) => `${f1(b.per10[i])} / ${f1(a.per10[i])} (${delta(a.per10[i], b.per10[i])})`).join(' | ')} |`);
    cis.push(...ciPairRows(title, onA, offA)); // counts and minutes pooled over the pair's runs
  };
  for (const t of tags) {
    const m = /^(.*)-offfirst$/.exec(t), on1 = m && runs.get(`${m[1]}-onfirst`), off1 = runs.get(t)!;
    if (m && on1 && off1.arms.on && off1.arms.off && on1.arms.on && on1.arms.off)
      pair(`${m[1]} (both orders, pair mean)`, [off1.arms.on, on1.arms.on], [off1.arms.off, on1.arms.off]);
    const n = /^(.*)-off(-\d+)?$/.exec(t), onT = n && runs.get(`${n[1]}-on${n[2] ?? ''}`);
    if (n && onT && off1.arms.all && onT.arms.all) { pair(`${n[1]}${n[2] ?? ''} (two loads)`, [onT.arms.all], [off1.arms.all]); twoLoads.push([n[1], onT.arms.all, off1.arms.all]); }
  }
  // two-load pairs of one base (-off-1/-on-1, -off-2/-on-2: both orders when the queue alternates them) also pooled
  for (const base of new Set(twoLoads.map(x => x[0]))) {
    const xs = twoLoads.filter(x => x[0] === base);
    if (xs.length > 1) pair(`${base} (${xs.length} two-load pairs, pooled)`, xs.map(x => x[1]), xs.map(x => x[2]));
  }
  for (const c of cross) { const a = runs.get(c.older)?.arms.all, b = runs.get(c.newer)?.arms.all; if (a && b) pair(c.title, [b], [a]); }
  if (pairs.length) out.push('', `| Pair | p99 off / on | p99.9 off / on | max off / on | ${THRESH.map(t => `>=${t} per 10 min off / on`).join(' | ')} |\n|${'---|'.repeat(4 + THRESH.length)}`, ...pairs,
    '', `Confidence (docs/168 §1: exact Poisson per arm, exact conditional on/off ratio, Wald difference; fewer than ${MIN_EVENTS} events in both arms = not enough events)`, '', CI_HEAD, ...cis);
  out.push('', exclTable(mergeExcl(shown)));
  if (bad.length) out.push('', `invalid runs (shown, left out of pairs and of the excluded table): ${bad.join(', ')}`);
  out.push('', 'Measurement tools per run (docs/168 §9, docs/data/tool-costs.json):', ...toolLines(loaded.map(r => r.tag)));
  out.push('', `p99 / p99.9 over every iteration (seconds.csv iters); '<${HITCH_MS}' = that rank lies below the profiler's ${HITCH_MS} ms hitch threshold, so only bounded.`);
  return out.join('\n');
}

// ---- --reuse (docs/168 §10): past runs whose timing verdict lets them count as evidence without a new timing run
export type ReuseCand = { tag: string; job: string; time: string; timing: string; why: string; asiSha?: string; tuning?: string; ini: string };
// queue rows -> runs of <id> (job id, profile.repro id, repro window id or profile tag) with their timing verdict: the stored one
// (queue.ts rows since 2026-10-24) or computed now (run-verdict.ts rowTiming); a repros job gives one run per window
export function reuseCands(rows: any[], id: string, since?: string, timingOf: (r: any) => { timing: string; why: string; windows?: Record<string, { timing: string; why: string }> } = rowTiming): ReuseCand[] {
  const out: ReuseCand[] = [];
  for (const r of rows) {
    if (since && String(r.time ?? '') < since) continue;
    const wins = (r.repros ?? []).filter((w: any) => w.id === id || r.job === id);
    const whole = !r.repros?.length && (r.job === id || bothOrdersBase(String(r.job)) === id || r.run?.profile?.repro === id || r.profileTag === id);
    if (!whole && !wins.length) continue;
    let t: ReturnType<typeof timingOf> | null = null;
    const got = () => t ??= timingOf(r);
    const base = { time: String(r.time ?? ''), asiSha: r.buildCheck?.asiSha ?? r.asiSha, tuning: r.buildCheck?.fixesTuning, ini: JSON.stringify(r.ini ?? {}) };
    if (whole) { const v = r.timing ? { timing: r.timing, why: r.timingWhy ?? '' } : got(); out.push({ ...base, tag: r.profileTag ?? `${r.queue}/${r.job} (no profile data)`, job: r.job, timing: v.timing, why: v.why }); }
    for (const w of wins) { const v = w.timing ? { timing: w.timing, why: w.timingWhy ?? '' } : got().windows?.[w.id] ?? { timing: 'invalid', why: 'window timing unknown' }; out.push({ ...base, tag: w.tag, job: `${r.job} / ${w.id}`, timing: v.timing, why: v.why }); }
  }
  return out;
}
// <base>-offfirst / <base>-onfirst -> <base> (the both-orders pair of one in-load A/B); other ids unchanged
const bothOrdersBase = (s: string) => s.replace(/-(off|on)first$/, '');
// in-load A/B runs with timing valid, plus valid-half runs pooled with the opposite-order run of the same job (or window) and the
// same asi + tuning (4 slices per arm together; in-load evidence, hitchReport's 'both orders, pair mean' row); unpaired valid-half
// runs are listed but not used. valid-single runs of one job (or window) paired older -> newer, a build or job ini
// difference flagged. Cross-run pairs are weaker evidence: two loads, and world state and machine drift between them
export function pickReuse(cands: ReuseCand[]) {
  const groups = new Map<string, ReuseCand[]>();
  for (const c of cands.filter(c => c.timing === 'valid-single').sort((a, b) => a.time.localeCompare(b.time))) groups.set(c.job, [...(groups.get(c.job) ?? []), c]);
  const h = (x?: string) => x?.slice(0, 12) ?? '?';
  const pairs = [...groups.values()].flatMap(g => g.slice(1).map((b, i) => {
    const a = g[i], flags = [a.asiSha !== b.asiSha && `asi ${h(a.asiSha)} vs ${h(b.asiSha)}`, a.tuning !== b.tuning && `ts3fixes tuning ${h(a.tuning)} vs ${h(b.tuning)}`, a.ini !== b.ini && 'job ini'].filter(Boolean);
    return { older: a.tag, newer: b.tag, title: `${b.job}: ${a.tag} -> ${b.tag} (cross-run, WEAKER evidence: two loads; ${flags.length ? `BUILD DIFFERS: ${flags.join(', ')}` : 'same build'})` };
  }));
  const halves = cands.filter(c => c.timing === 'valid-half'), pooled: string[] = [];
  for (const a of halves.filter(c => /-offfirst$/.test(c.tag))) {
    const b = halves.find(c => c.tag === a.tag.replace(/-offfirst$/, '-onfirst') && bothOrdersBase(c.job) === bothOrdersBase(a.job) && c.asiSha === a.asiSha && c.tuning === a.tuning);
    if (b) pooled.push(a.tag, b.tag);
  }
  return { ab: cands.filter(c => c.timing === 'valid').map(c => c.tag), pooled, unpaired: halves.map(c => c.tag).filter(t => !pooled.includes(t)), singles: [...groups.values()].flat().map(c => c.tag), pairs };
}
export function reuseReport(id: string, since?: string): string {
  const rows = existsSync(QUEUE) ? readdirSync(QUEUE).filter(f => /-\d{4}-\d\d-\d\d\.jsonl$/.test(f)).flatMap(f => jsonlRows(join(QUEUE, f))) : [];
  const cands = reuseCands(rows, id, since), p = pickReuse(cands), tags = [...p.ab, ...p.pooled, ...p.singles].filter(t => !t.includes(' '));
  return [`### Reusable timing runs for ${id}${since ? ` since ${since}` : ''} (docs/168 §10)`, '',
    `${p.ab.length} in-load A/B valid, ${p.pooled.length / 2} both-orders pairs of valid-half runs (pooled: in-load A/B evidence, the 'both orders, pair mean' rows)${p.unpaired.length ? `, unpaired valid-half not used: ${p.unpaired.join(', ')}` : ''}, ${p.singles.length} valid-single, ${p.pairs.length} cross-run pairs (WEAKER evidence than in-load A/B; their pair rows read older / newer in the off / on columns)`, '',
    '| Run | Start UTC | Timing | asi | ts3fixes tuning |', '|---|---|---|---|---|',
    ...cands.map(c => `| ${c.tag} | ${c.time.slice(0, 16)} | ${c.timing}${c.why ? ` (${c.why.replace(/\|/g, '/')})` : ''} | ${c.asiSha?.slice(0, 12) ?? '?'} | ${c.tuning?.slice(0, 12) ?? '?'} |`),
    '', tags.length ? hitchReport(tags, p.pairs) : 'no reusable runs (none valid, paired valid-half or valid-single)'].join('\n');
}

// worst-hitches.ts --json records by '<tag>|<hitch t>' (only iterations >= its --min)
export function loadWh(whFile?: string) {
  const wh = new Map<string, any>();
  if (whFile) for (const x of JSON.parse(readFileSync(whFile, 'utf8'))) wh.set(`${String(x.tag).replace(/@\d+$/, '')}|${x.h.t}`, x);
  return wh;
}

// one row for the chosen arms of every given run pooled (iterations, seconds and hitches summed); with whFile, iterations it
// classes as a harness step are dropped (only those >= its --min are known)
// real-only: excluded points (exclusion()) and invalid runs out; returns the row, then its excluded table and the invalid runs
export function poolReport(label: string, args: string[], armsWanted: string[]): string {
  const all = expandTags(args).map(loadRun), runs = all.filter(r => !r.invalid), pool: Arm = { iters: 0, secs: 0, hitches: [], excl: new Map() };
  const used: Arm[] = [];
  for (const r of runs) for (const a of armsWanted) { const x = r.arms[a]; if (x) { used.push(x); pool.iters += x.iters; pool.secs += x.secs; pool.hitches.push(...x.hitches); pool.excl = mergeExcl([pool, x]); } }
  const bad = all.filter(r => r.invalid).map(r => `${r.tag} (${r.invalid})`);
  return `${line(`${label} (${runs.length} runs)`, armsWanted.join(','), armStats(pool), pool)}\n\n${used.length ? ciPoolRows(used) : ''}\n\n${exclTable(pool.excl)}\n\ninvalid runs left out (${bad.length}): ${bad.join(', ') || 'none'}`;
}

// ---- scenarios (docs/168 §3): what the real-only tables leave out on purpose. Iterations and publish gaps inside the run's
// repro-triggered windows (each labelled by its step's agent command), plus 0-step iterations >= 1 s, are the scenario's own cost;
// the load minute stays out (a step's window that ends inside it is the lead-in, not the scenario)
// steps / firings: worst-hitches.ts firingWins
// an iteration led by our test agent's own command (Ts3Agent.PollTask runs the step in a script task) is harness cost, not the
// fix's: it leaves the arm (own row), the confidence rows and the moved-waits table
export const AGENT_ROW = 'agent command (harness cost)';
export const agentCost = (h: H) => { const t = (h.tasks ?? []).reduce((m: any, x: any) => (x.ms > (m?.ms ?? -1) ? x : m), null); return !!t && t.name.startsWith('Ts3Agent.') && t.ms >= h.wallMs / 2; };
type ScenRun = { tag: string; rows: Row[]; hitches: H[]; freezes: Fz[]; ctx: Pick<RunCtx, 'wins'>; arms: Record<string, Arm>; invalid: string | null; steps?: string[] };
export function scenarioRows(r: ScenRun) {
  const first = r.rows[Math.min(59, r.rows.length - 1)]?.t ?? 0, wins = r.ctx.wins.filter(w => w.why === 'repro-triggered');
  const fw = firingWins(wins, r.steps, r.rows.at(-1)?.t ?? 0).filter(f => f.b > first), arms: Record<string, Arm & { firings: number }> = {};
  // a firing may be split by its setup steps ('<arm> setup' pieces, worst-hitches.ts firingWins): count each firing once per row
  const seen = new Set<string>();
  for (const f of fw) { const x = arms[f.arm] ??= { iters: 0, secs: 0, hitches: [], excl: new Map(), freezes: [], firings: 0 }; if (!seen.has(`${f.arm}|${f.n}`)) { seen.add(`${f.arm}|${f.n}`); x.firings++; } x.secs += (f.b - Math.max(f.a, first)) / 1000; }
  const fire = (b: number) => fw.find(f => f.a <= b && b < f.b);
  const label = (a: number, b: number, wallMs = 0, steps?: number) => b <= first ? null : wins.find(w => w.a < b && w.b > a)?.what ?? (steps === 0 && wallMs >= 1000 ? 'save/load (0-step >= 1 s)' : null);
  const by = new Map<string, { it: number[]; fz: number[] }>(), at = (k: string) => by.get(k) ?? by.set(k, { it: [], fz: [] }).get(k)!;
  // an iteration (gap) goes to the piece it starts in: an agent call's iteration starts inside its step (2026-10-22: the 1698 ms
  // strand_many iteration ended 0 ms past its setup piece on the coarse clock)
  for (const h of r.hitches) {
    if (h.t > first && agentCost(h)) { at(AGENT_ROW).it.push(h.wallMs); continue; }
    const g = h.t > first && fire(h.t - h.wallMs); if (g) { arms[g.arm].hitches.push(h); continue; } const k = label(h.t - h.wallMs, h.t, h.wallMs, h.steps); if (k) at(k).it.push(h.wallMs);
  }
  for (const f of r.freezes) { const g = f.t1 > first && fire(f.t0); if (g) { arms[g.arm].freezes!.push(f.gapMs); continue; } const k = label(f.t0, f.t1); if (k) at(k).fz.push(f.gapMs); }
  const nat = r.arms.all ? armStats(r.arms.all) : null, c = (xs: number[], t: number) => xs.filter(x => x >= t).length, name = r.invalid ? `${r.tag} (INVALID)` : r.tag;
  const rows = [...by].map(([k, v]) => `| ${name} | ${k} | ${wins.filter(w => w.what === k && w.b > first).length} | ${c(v.it, 100)} | ${c(v.it, 300)} | ${c(v.it, 1000)} | ${f0(Math.max(0, ...v.it))} | ${f0(v.it.reduce((s, x) => s + x, 0))} | ${c(v.fz, 300)} | ${c(v.fz, 1000)} | ${f0(Math.max(0, ...v.fz))} | ${nat ? f1(nat.per10[1]) : '-'} |`);
  const armRows = Object.entries(arms).map(([k, x]) => { const w = x.hitches.map(h => h.wallMs); return `| ${name} | ${k} | ${x.firings} | ${f1(x.secs / 60)} | ${[100, 300, 500, 1000].map(t => c(w, t)).join(' | ')} | ${f0(Math.max(0, ...w))} | ${c(x.freezes!, 300)} |`; });
  return { rows, armRows, arms: fw.length ? arms : null };
}
// adaptive waits (repro-loop.ts runStep, docs/170 §3): steps that hit their maxS cap; their effect may run into the next firing
export const cappedSteps = (log: string) => [...log.matchAll(/^\d\d:\d\d:\d\d (?:firing #\d+|step \w+) done after ([\d.]+) s \(cap\)(?: \[([^\]]*)\])?/gm)].map(m => `${m[2] ?? '?'} (${m[1]} s)`);
export function scenarioReport(args: string[]) {
  const runs = expandTags(args).map(t => { const r = loadRun(t); return { ...r, steps: reproSteps(t, r.secs) }; }), sc = runs.map(scenarioRows);
  const capped = runs.map(r => { const f = join(QUEUE, `${r.tag}.log`); return existsSync(f) ? cappedSteps(queueEvents(readFileSync(f, 'utf8'), r.tag, r.secs).events.map(e => `00:00:00 ${e.text}`).join('\n')) : []; });
  const L = [`Scenario cost: harness-triggered windows (step + ${STEP_POST_MS / 1000} s after; steps with arm notes: per arm below), labelled by agent command; NOT natural play (docs/168 §3)`, '',
    '| Run | step (scenario) | windows | iterations >= 100 | >= 300 | >= 1000 | max ms | ms in windows >= 50 | freezes >= 300 | freezes >= 1000 | max gap ms | natural >= 300 per 10 min (same run) |',
    `|${'---|'.repeat(12)}`, ...sc.flatMap(s => s.rows)];
  const ok = sc.filter((s, i) => s.arms && !runs[i].invalid), on = ok.flatMap(s => s.arms!.on ? [s.arms!.on] : []), off = ok.flatMap(s => s.arms!.off ? [s.arms!.off] : []);
  if (sc.some(s => s.arms)) L.push('', 'Per arm: a firing = its first step\'s start to the next firing\'s start (or the run end), minus its setup steps (step start to the end of its wait: the \'<arm> setup\' rows, harness cost, not in the confidence rows; docs/168 §7)', '',
    '| Run | arm | firings | min | iterations >= 100 | >= 300 | >= 500 | >= 1000 | max ms | visible freezes >= 300 |', `|${'---|'.repeat(10)}`, ...sc.flatMap(s => s.armRows));
  capped.forEach((c, i) => { if (c.length) L.push('', `**${runs[i].tag}: ${c.length} steps hit their maxS cap** (effect may run into the next firing; check those windows): ${c.join(', ')}`); });
  if (on.length && off.length) L.push('', CI_HEAD, ...ciPairRows(ok.length > 1 ? `${ok.length} runs pooled` : runs[sc.indexOf(ok[0])].tag, on, off));
  // causes of the slow iterations inside the firings (or per-step windows), with this file's windows so the counts line up
  const mw: MwRun[] = [];
  for (const r of runs) {
    let fr; try { fr = loadFreeze(r.tag); } catch (e) { L.push('', `${r.tag}: repro causes skipped (${(e as Error).message})`); continue; }
    const fires = reproFires(r.ctx.wins, r.steps, r.rows);
    L.push('', reproCauses({ ...fr, rows: r.rows, hitches: r.hitches, ctx: r.ctx }, fires));
    if (!r.invalid) mw.push({ hitches: r.hitches, frames: fr.frames, fires, t0: r.rows[0]?.t ?? 0 });
  }
  const m = movedWaits(mw); if (m) L.push('', m);
  return L.join('\n');
}

// moved waits: a fix that only moves a +0x918 wait to the next lock taker shows that taker as GREW in the on arm
type MwRun = { hitches: H[]; frames?: { q: number; ms: number; svc?: { name: string }[] }[]; fires: { a: number; b: number; arm: string }[]; t0: number };
export function movedWaits(runs: MwRun[]): string {
  const mins = { off: 0, on: 0 }, by = new Map<string, Record<'off' | 'on', { n100: number; n300: number; ms: number }>>();
  for (const r of runs) {
    for (const f of r.fires) if (f.arm === 'off' || f.arm === 'on') mins[f.arm] += (f.b - f.a) / 60_000;
    // shortcut: of reproCauses' exclusions only the 0-step save/load one applies here; reuse exclusion() if counts drift from its table
    for (const h of r.hitches) {
      const s0 = h.t - h.wallMs, arm = r.fires.find(f => f.a <= s0 && s0 < f.b)?.arm;
      if (h.wallMs < 100 || (h.steps === 0 && h.wallMs >= 1000) || (arm !== 'off' && arm !== 'on') || agentCost(h)) continue;
      const c = cause(h as any, (h.t - r.t0) / 1000, null), svc = r.frames?.filter(f => f.q >= s0 && f.q <= h.t + 100).sort((x, y) => y.ms - x.ms)[0]?.svc?.[0]?.name;
      const k = h.rtMaxKey ? `wait ${keyName(h.rtMaxKey)}` : `${c.cls} / ${c.topTask || svc || '-'}`;
      const e = (by.get(k) ?? by.set(k, { off: { n100: 0, n300: 0, ms: 0 }, on: { n100: 0, n300: 0, ms: 0 } }).get(k)!)[arm];
      e.n100++; if (h.wallMs >= 300) e.n300++; e.ms += h.wallMs;
    }
  }
  if (!mins.off || !mins.on) return '';
  const rows = [...by].map(([k, v]) => {
    const off = v.off.ms * 10 / mins.off, on = v.on.ms * 10 / mins.on, offN = v.off.n100 * mins.on / mins.off; // off events scaled to the on arm's minutes
    const flag = !v.on.n100 ? 'GONE' : v.on.n100 - offN >= 2 || on >= 1.5 * off ? 'GREW' : '';
    return { k, flag, d: on - off, line: `| ${k} | ${v.off.n100} | ${v.off.n300} | ${f0(off)} | ${v.on.n100} | ${v.on.n300} | ${f0(on)} | ${on >= off ? '+' : ''}${f0(on - off)} | ${flag} |` };
  }).sort((a, b) => +(b.flag === 'GREW') - +(a.flag === 'GREW') || b.d - a.d);
  return [`### Moved waits: off vs on arm, iterations >= 100 ms inside the firings by cause key (wait key, else class / task or frame service), all runs pooled (${f1(mins.off)} off / ${f1(mins.on)} on firing-minutes)`, '',
    '| Cause key | off >= 100 | off >= 300 | off ms per 10 min | on >= 100 | on >= 300 | on ms per 10 min | delta | flag |', `|${'---|'.repeat(9)}`,
    ...rows.map(r => r.line), '', `next targets: ${rows.filter(r => r.flag === 'GREW').map(r => r.k).join(', ') || 'none'}`].join('\n');
}

// ---- causes: which work leads each >= 300 ms iteration
export type SyncPlan = { ms: number; ic: string; reason: string; used?: boolean };
export function syncPlans(log: string): SyncPlan[] {
  return [...log.matchAll(/sync plan (\d+) ms, (IC \w+), reason (\w+)/g)].map(m => ({ ms: Number(m[1]), ic: m[2], reason: m[3] }));
}
// closest unused logged sync plan within max(10 ms, 10%) of the iteration's longest plan entry [inferred: no shared clock]
export function matchPlan(plans: SyncPlan[], ms: number) {
  let best: SyncPlan | null = null;
  for (const p of plans) if (!p.used && Math.abs(p.ms - ms) <= Math.max(10, 0.1 * ms) && (!best || Math.abs(p.ms - ms) < Math.abs(best.ms - ms))) best = p;
  if (best) best.used = true;
  return best;
}

// the largest of the disjoint shares of one iteration (route and route-job waits are nested in the script stages)
export function causeOf(h: H, plan: SyncPlan | null, roots: string[] = [], logged = true): string {
  const g = (k: string) => h[k] ?? 0, wait = g('rtLock') + g('rtOtherLock') + g('rtJob') + g('rtPlanWait');
  const route = Math.max(0, g('route') - g('rtPlanWait'));
  const script = Math.max(0, g('taskObjects') + g('threaded') + g('oneShot') + g('script') - g('route') - g('rtLock') - g('rtOtherLock') - g('rtJob'));
  const parts: [string, number][] = [['route', route], ['wait', wait], ['gc', g('gcMark') + g('fin')], ['script', script],
    ['locomotion', g('locomotion')], ['animation', g('animSacs') + g('animPose')]];
  const timed = parts.reduce((a, [, v]) => a + v, 0);
  parts.push(['other', h.wallMs - timed]);
  const [k] = parts.sort((a, b) => b[1] - a[1])[0];
  const entry = Object.entries(h.byEntry ?? {}).sort((a: any, b: any) => b[1].ms - a[1].ms)[0]?.[0] ?? '?';
  if (k === 'route') return `sync route plan: ${plan ? `${plan.ic} ${plan.reason}` : `${entry}, ${logged ? 'no logged fallback of that length' : 'reason not logged'}`}`;
  if (k === 'wait') return `route job wait: ${keyName(h.rtMaxKey || '?')}`;
  if (k === 'gc') return 'GC';
  if (k === 'script') {
    if (h.tasks?.length) return `script: ${h.tasks[0].name}`; // timed per run by the profiler (docs/166): no sampler needed
    const c = new Map<string, number>(); roots.forEach(r => c.set(r, (c.get(r) ?? 0) + 1));
    return `script: ${[...c].sort((a, b) => b[1] - a[1])[0]?.[0] ?? `${g('taskObjects') >= g('threaded') ? 'TaskObjects' : 'ThreadedObjects'} stage, no managed sample`}`;
  }
  return k === 'other' ? 'other (not in a timed stage)' : k;
}

export function causeReport(args: string[], armsWanted: string[], whFile?: string): string {
  const wh = loadWh(whFile);
  const n300 = new Map<string, number>(), n1000 = new Map<string, number>(), ms = new Map<string, number>(), where = new Map<string, Set<string>>();
  let mins = 0;
  const used: Arm[] = [], bad: string[] = [];
  for (const tag of expandTags(args)) {
    const r = loadRun(tag);
    if (r.invalid) { bad.push(`${tag} (${r.invalid})`); continue; }
    const logs = existsSync(QUEUE) ? readdirSync(QUEUE).filter(f => f.startsWith(`${tag}-`) && /^\d{4}-ts3native\.log$/.test(f.slice(tag.length + 1))) : [];
    const plans = logs.flatMap(f => syncPlans(readFileSync(join(QUEUE, f), 'utf8'))), logged = plans.length > 0;
    for (const a of armsWanted) {
      const arm = r.arms[a]; if (!arm) continue;
      mins += arm.secs / 60; used.push(arm);
      for (const h of arm.hitches.filter(x => x.wallMs >= 300).sort((x, y) => y.wallMs - x.wallMs)) {
        const rec = wh.get(`${tag}|${h.t}`);
        const longest = Math.max(0, ...Object.values(h.byEntry ?? {}).map((v: any) => v.ms ?? 0));
        const c = causeOf(h, null, rec?.roots, logged);
        const plan = c.startsWith('sync route plan') && logged ? matchPlan(plans, longest || h.route) : null;
        const cause = plan ? causeOf(h, plan) : c;
        n300.set(cause, (n300.get(cause) ?? 0) + 1); ms.set(cause, (ms.get(cause) ?? 0) + h.wallMs);
        if (h.wallMs >= 1000) n1000.set(cause, (n1000.get(cause) ?? 0) + 1);
        (where.get(cause) ?? where.set(cause, new Set()).get(cause)!).add((/-(bp|ip|sv)-/.exec(tag) ?? [])[1] ?? '?');
      }
    }
  }
  const out = [`${f0(mins)} real profiled minutes (arms ${armsWanted.join(',')}); excluded points and invalid runs below the table\n| Cause | >= 300 ms | >= 1000 ms | per 10 min >= 300 | ms | worlds |\n|---|---|---|---|---|---|`];
  for (const [k, v] of [...n300].sort((a, b) => (n1000.get(b[0]) ?? 0) - (n1000.get(a[0]) ?? 0) || b[1] - a[1]))
    out.push(`| ${k} | ${v} | ${n1000.get(k) ?? 0} | ${f1(v * 10 / (mins || 1))} | ${f0(ms.get(k)!)} | ${[...where.get(k)!].sort().join(' ')} |`);
  out.push('', exclTable(mergeExcl(used)), '', `invalid runs left out (${bad.length}): ${bad.join(', ') || 'none'}`);
  return out.join('\n');
}

function selfCheck() {
  // 200 s run, 50 it/s; hitches: 2 in the load minute, 30 x 120 ms + 1 x 1500 ms after it, 1 x 300 ms in the profiler's stop edge
  const rows = Array.from({ length: 200 }, (_, i) => ({ iters: 50, t: 1000 * (i + 1) }));
  const hs = [500, 900].map(t => ({ t, wallMs: 400 })).concat(Array.from({ length: 30 }, (_, i) => ({ t: 70_000 + 3000 * i + 500, wallMs: 120 })), [{ t: 190_500, wallMs: 1500 }, { t: 199_500, wallMs: 300 }]);
  const a = splitRun(rows, hs);
  // first minute (rows 0-59) and the last 2 s (rows 198-199) out: 138 s
  assert.equal(a.all.iters, 138 * 50); assert.equal(a.all.secs, 138); assert.equal(a.all.hitches.length, 31);
  assert.deepEqual([...a.all.excl].map(([k, r]) => [k, r.n, r.ms, r.secs]), [['first minute', 2, 800, 60], ['profiler edge', 1, 300, 2]]);
  const s = armStats(a.all);
  // 6900 iterations: p99 rank from the top = 69 > 31 recorded -> bounded; p99.9 rank 6 -> 120 ms; max 1500
  assert.deepEqual(s.p99, { ms: HITCH_MS, bounded: true }); assert.deepEqual(s.p999, { ms: 120, bounded: false });
  assert.equal(s.max, 1500); assert.deepEqual(s.counts, [31, 1, 1, 1, 1, 1, 0]); assert.equal(s.per10[0], 31 * 600 / 138);
  // harness windows: a sampler window over 100-130 s drops 30 rows (QPC 101-130 s) and the hitches in it; a repro step hitch in
  // a kept row leaves the iteration count; a 0-step 1.2 s iteration is a save
  const w = splitRun(rows, [...hs, { t: 150_500, wallMs: 2500, steps: 3 }, { t: 160_500, wallMs: 1200, steps: 0 }], undefined,
    { wins: [...baseWins(rows), { a: 100_000, b: 130_000, why: 'sampler', what: 'native' }, { a: 149_000, b: 149_500, why: 'repro-triggered', what: 'sim_route_to' }], foreground: false });
  // the repro window also drops the row QPC 149-150 s
  assert.equal(w.all.secs, 138 - 31); assert.equal(w.all.iters, (138 - 31) * 50 - 2);
  assert.equal(w.all.excl.get('sampler')!.n, 10); assert.equal(w.all.excl.get('sampler')!.secs, 30);
  assert.equal(w.all.excl.get('repro-triggered')!.n, 2); assert.equal(w.all.excl.get('repro-triggered')!.n1000, 2);
  assert.equal(armStats(w.all).max, 1500); assert.match(exclTable(w.all.excl), /\| repro-triggered \| 2 \| 3700 \| 2 \| 2 \| 2500 \| 0\.0 \|/);
  // --ab: slices of 60 s from 0 (warm-up), on/off/on; 15 s settle; last slice 180-200 is under half length -> out
  const ab = splitRun(rows, hs, [{ from: 0, on: true }, { from: 60, on: false }, { from: 120, on: true }, { from: 180, on: false }]);
  assert.equal(ab.off.secs, 45); assert.equal(ab.on.secs, 45); assert.equal(ab.off.hitches.length, 15); assert.equal(ab.on.hitches.length, 8);
  // in-load loop slices (docs/189 §16): lead-in 65-70 out, firings 70/100/130/160 whole (no settle, short last kept), and the
  // repro-triggered window inside a firing stays in the arm
  const lsl = [{ from: 65, on: false }, { from: 70, on: false }, { from: 100, on: true }, { from: 130, on: true }, { from: 160, on: false }].map(x => ({ ...x, loop: true }));
  const lwins = { wins: [...baseWins(rows), { a: 149_000, b: 149_500, why: 'repro-triggered' as const, what: 'crowd_go' }], foreground: false };
  const lpr = splitRun(rows, [...hs, { t: 149_400, wallMs: 900, steps: 3 }], lsl, loopCtx(lwins, lsl));
  assert.equal(lpr.on.secs, 60); assert.equal(lpr.off.secs, 30 + 38); assert.equal(lpr.on.hitches.length, 21); assert.equal(lpr.on.excl.size, 0);
  assert.equal(loopCtx(lwins, undefined), lwins); // --ab and plain runs keep every window
  // causes
  assert.equal(causeOf({ t: 0, wallMs: 900, route: 800, threaded: 850, byEntry: {} }, { ms: 790, ic: 'IC Plan', reason: 'NoStart' }), 'sync route plan: IC Plan NoStart');
  assert.equal(causeOf({ t: 0, wallMs: 400, threaded: 380, rtLock: 300, rtMaxKey: '0x00760bff' }, null), 'route job wait: gate 10 goal step (sync plan)');
  assert.equal(causeOf({ t: 0, wallMs: 400, taskObjects: 390 }, null, ['A.Run', 'B.Run', 'A.Run']), 'script: A.Run');
  assert.equal(causeOf({ t: 0, wallMs: 400, taskObjects: 390, tasks: [{ stage: 'taskObjects', name: 'FunctionTask>N.X.Run', ms: 300 }] }, null, ['A.Run']), 'script: FunctionTask>N.X.Run');
  assert.equal(causeOf({ t: 0, wallMs: 600, gcMark: 500 }, null), 'GC');
  const pl = syncPlans('route async: sync plan 84 ms, IC Plan, reason Los, entry nojob\nroute async: sync plan 1313 ms, IC Plan, reason NoStart, entry nojob');
  assert.equal(matchPlan(pl, 1250)?.reason, 'NoStart'); assert.equal(matchPlan(pl, 1250), null); assert.equal(matchPlan(pl, 300), null);
  // confidence: textbook exact intervals (Garwood k=0 -> [0, 3.689], k=10 -> [4.795, 18.39]; Clopper-Pearson 5/10 -> [0.187, 0.813])
  const near = (x: number, y: number, tol = 0.002) => assert.ok(Math.abs(x - y) <= tol * Math.max(1, Math.abs(y)), `${x} vs ${y}`);
  near(poisCI(0)[1], 3.689); near(poisCI(10)[0], 4.795); near(poisCI(10)[1], 18.39); near(poisCI(2000)[0], 1913.6); // large k: no overflow
  near(cpCI(5, 10)[0], 0.187, 0.005); near(cpCI(5, 10)[1], 0.813, 0.005); assert.deepEqual(cpCI(0, 0), [0, 1]);
  const rc = rateCI({ k: 10, mins: 100 }); near(rc.est, 1); near(rc.lo, 0.4795); near(rc.hi, 1.839);
  // 2 vs 20 in 100 min each: ratio 0.1, upper well below 1; 3 vs 4: not enough events; 30 vs 33: no clear difference
  const c1 = rateCompare({ k: 2, mins: 100 }, { k: 20, mins: 100 });
  assert.equal(c1.verdict, 'fewer with on'); near(c1.ratio.est, 0.1); assert.ok(c1.ratio.hi < 0.45 && c1.ratio.lo > 0.005); near(c1.diff.est, -1.8);
  assert.match(rateCompare({ k: 3, mins: 50 }, { k: 4, mins: 50 }).verdict, /^not enough events \(7 < 10\)/);
  assert.equal(rateCompare({ k: 30, mins: 100 }, { k: 33, mins: 100 }).verdict, 'no clear difference');
  assert.equal(rateCompare({ k: 40, mins: 100 }, { k: 10, mins: 100 }).verdict, 'more with on');
  // power: BP play >= 300 ms (5.4 per 10 min) needs ~277 real min per arm for a 30% cut -> 80 runs of 3.5 min; IP (20.1) ~75 min
  near(runsNeeded(5.4, 0.3, 3.5)!.minsPerArm, 277.5, 0.01); assert.equal(runsNeeded(5.4, 0.3, 3.5)!.runs, 80);
  assert.equal(runsNeeded(20.1, 0.3, 3.5)!.runs, 22); assert.equal(runsNeeded(0, 0.3, 3.5), null);
  // freezes: a 400 ms gap with a 350 ms iteration inside is real; a 200 ms gap with no iteration >= 50 ms is render-paced; one in the
  // load minute is out
  const fa = splitRun(rows, hs, undefined, undefined, [{ t0: 120_000, t1: 120_400, gapMs: 400, iters: 1 }, { t0: 130_000, t1: 130_200, gapMs: 200, iters: 0 }, { t0: 10_000, t1: 10_500, gapMs: 500, iters: 1 }]);
  assert.deepEqual(fa.all.freezes, []); // no hitch in 120.0-120.4 s in hs: the 400 ms gap is render-paced too
  const fb = splitRun(rows, [...hs, { t: 120_380, wallMs: 350 }], undefined, undefined, [{ t0: 120_000, t1: 120_400, gapMs: 400, iters: 1 }, { t0: 130_000, t1: 130_200, gapMs: 200, iters: 0 }]);
  assert.deepEqual(fb.all.freezes, [400]); assert.equal(splitRun(rows, hs).all.freezes, undefined);
  const pr = ciPoolRows([fb.all]); assert.match(pr, /\| visible freezes \| 300 \| 1 \|/); assert.match(pr, /\| iterations \| 1000 \| 1 \| 26\.1 \[0\.7, 145\.3\] \| 4\.3 \[/); // 1 in 138 s
  assert.equal(ciPairRows('x', [fb.all], [fb.all]).length, 8);
  // scenario: a build_mode window at 100-112 s takes the 2.5 s iteration and its freeze; a 0-step save; the load minute stays out
  const sw = [...baseWins(rows), { a: 100_000, b: 112_000, why: 'repro-triggered' as const, what: 'build_mode' }, { a: 20_000, b: 30_000, why: 'repro-triggered' as const, what: 'time' }];
  const sh = [...hs, { t: 105_000, wallMs: 2500, steps: 1 }, { t: 150_500, wallMs: 1200, steps: 0 }, { t: 25_000, wallMs: 900, steps: 1 }];
  const sc = scenarioRows({ tag: 't', rows, hitches: sh, freezes: [{ t0: 102_400, t1: 105_100, gapMs: 2700, iters: 1 }], ctx: { wins: sw }, arms: splitRun(rows, sh, undefined, { wins: sw, foreground: false }), invalid: null });
  // (plus the four 120 ms fixture hitches at 100.5-109.5 s)
  assert.equal(sc.rows.length, 2); assert.match(sc.rows[0], /^\| t \| build_mode \| 1 \| 5 \| 1 \| 1 \| 2500 \| 2980 \| 1 \| 1 \| 2700 \|/); assert.match(sc.rows[1], /save\/load \(0-step >= 1 s\) \| 0 \| 1 \| 1 \| 1 \| 1200 /);
  assert.equal(sc.arms, null);
  // arm notes: off firing #1 (patches 70-81 s, fire 81-92 s), on firing #2 from 100 s to the run end; a delayed 800 ms hitch at
  // 95 s (after the off fire window, before the on firing) stays with off; the step in the load minute is out
  const lw = [{ a: 20_000, b: 30_000, what: 'time' }, { a: 70_000, b: 81_000, what: 'patches off' }, { a: 81_000, b: 92_000, what: 'fire off' },
    { a: 100_000, b: 111_000, what: 'patches on' }, { a: 111_000, b: 122_000, what: 'fire on' }].map(w => ({ ...w, why: 'repro-triggered' as const }));
  const lsteps = ['repro t step time: {}', 'repro t step patches [arm off #1]: {}', 'repro t step fire [fire off #1]: {}', 'repro t step patches [arm on #2]: {}', 'repro t step fire [fire on #2]: {}'];
  const lh = [{ t: 25_000, wallMs: 900, steps: 1 }, { t: 85_000, wallMs: 350 }, { t: 95_000, wallMs: 800 }, { t: 130_000, wallMs: 400 }];
  const lr = { tag: 't', rows, hitches: lh, freezes: [{ t0: 149_500, t1: 150_000, gapMs: 500, iters: 1 }], ctx: { wins: [...baseWins(rows), ...lw] }, arms: {}, invalid: null };
  const old = scenarioRows(lr); // no step texts: per-step windows, the delayed hitch is lost
  assert.equal(old.arms, null); assert.equal(old.rows.length, 1); assert.match(old.rows[0], /^\| t \| fire off \| 1 \| 1 \| 1 \| 0 \| 350 \|/);
  const la = scenarioRows({ ...lr, steps: lsteps });
  assert.equal(la.rows.length, 0); assert.equal(la.arms!.off.secs, 30); assert.equal(la.arms!.on.secs, 100);
  assert.deepEqual(la.armRows, ['| t | off | 1 | 0.5 | 2 | 2 | 1 | 0 | 800 | 0 |', '| t | on | 1 | 1.7 | 1 | 1 | 0 | 0 | 400 | 1 |']);
  assert.equal(ciPairRows('t', [la.arms!.on], [la.arms!.off]).length, 8);
  assert.equal(firingWins(lw, lsteps.slice(1), 200_000).length, 0); // log does not line up: old behaviour
  // setup steps (docs/168 §7): the arm switches are setup (patches off ends its wait at 74 s, patches on at 104 s); a 1700 ms
  // strand-style setup hitch at 73 s goes to 'off setup', not to the off arm; seconds split the same way
  const sw2 = lw.map(w => w.what.startsWith('patches') ? { ...w, setupEnd: w.a + 4000 } : w);
  const ls = scenarioRows({ ...lr, hitches: [...lh, { t: 73_000, wallMs: 1700 }], ctx: { wins: [...baseWins(rows), ...sw2] }, steps: lsteps });
  assert.deepEqual(Object.keys(ls.arms!).sort(), ['off', 'off setup', 'on', 'on setup']);
  assert.deepEqual([ls.arms!.off.secs, ls.arms!['off setup'].secs, ls.arms!.on.secs, ls.arms!['on setup'].secs], [26, 4, 96, 4]);
  assert.deepEqual(ls.armRows.slice(0, 2), ['| t | off setup | 1 | 0.1 | 1 | 1 | 1 | 1 | 1700 | 0 |', '| t | off | 1 | 0.4 | 2 | 2 | 1 | 0 | 800 | 0 |']);
  assert.deepEqual(firingWins(sw2, lsteps.map(s => s.replace(/^(repro t step patches \[[^\]]*\])/, '$1 (setup)')), 200_000).map(f => `${f.arm} ${f.a / 1000}-${f.b / 1000}`),
    ['off setup 70-74', 'off 74-100', 'on setup 100-104', 'on 104-200']);
  // one real result folder, when the data is here: arms add up and every recorded hitch is placed or in a dropped second
  const real = 'clean-night-0.2.0-2026-10-21-bp-fb-play-onfirst';
  if (existsSync(PROFILE) && expandTags([real]).length) {
    const r = loadRun(real), on = armStats(r.arms.on), off = armStats(r.arms.off);
    const total = r.rows.reduce((x, y) => x + y.iters, 0);
    assert.ok(on.iters > 0 && off.iters > 0 && on.iters + off.iters < total, 'real arms');
    assert.ok(r.arms.on.hitches.length + r.arms.off.hitches.length <= r.hitches.length, 'real hitches');
    for (const x of [on, off]) assert.ok(x.counts.every((c, i) => i === 0 || c <= x.counts[i - 1]) && x.max >= x.p999.ms, 'real monotonic');
    console.log(`real ${real}: on ${on.iters} it, off ${off.iters} it of ${total}`);
  } else console.log('real folder not found (set TS3_REPO): fixture only');
  // capped adaptive steps, as repro-loop.ts runStep logs them
  assert.deepEqual(cappedSteps('12:00:01 firing #3 done after 23.0 s (cap) [fire off #3]\n12:00:02 firing #4 done after 5.1 s (signal) [fire on #4]\n12:00:03 step build_mode done after 10.0 s (cap)'),
    ['fire off #3 (23.0 s)', '? (10.0 s)'], 'cappedSteps');
  // moved waits: off has steering waits, on has gate-12 waits instead; one equal script task in both arms gets no flag
  const mt = (t: number, wallMs: number, rtMaxKey?: string) => rtMaxKey ? { t, wallMs, rtLock: wallMs - 20, threaded: wallMs - 10, rtMaxKey } : { t, wallMs, taskObjects: wallMs - 10, tasks: [{ stage: 'taskObjects', name: 'A.Run', ms: wallMs - 20 }] };
  const mwOut = movedWaits([{ t0: 0, fires: [{ a: 60_000, b: 120_000, arm: 'off' }, { a: 120_000, b: 180_000, arm: 'on' }],
    hitches: [mt(70_000, 150, '0x0085fe10'), mt(80_000, 350, '0x0085fe10'), mt(90_000, 400, '0x0085fe10'), mt(100_000, 200), mt(110_000, 90, '0x0085fe10'),
      mt(130_000, 120, '0x0062f782'), mt(140_000, 320, '0x0062f782'), mt(150_000, 310, '0x0062f782'), mt(160_000, 200)] }]).split('\n');
  assert.match(mwOut[4], /^\| wait gate 12 LotTable_Contains \| 0 \| 0 \| 0 \| 3 \| 2 \| 7500 \| \+7500 \| GREW \|$/);
  assert.match(mwOut.find(l => l.includes('steering obstacle query'))!, /\| 3 \| 2 \| 9000 \| 0 \| 0 \| 0 \| -9000 \| GONE \|$/);
  assert.match(mwOut.find(l => l.includes('A.Run'))!, /\| 1 \| 0 \| 2000 \| 1 \| 0 \| 2000 \| \+0 \|  \|$/);
  assert.equal(mwOut.at(-1), 'next targets: wait gate 12 LotTable_Contains');
  // agent cost: an iteration led by Ts3Agent.PollTask leaves the arm and the moved-waits table; a game task does not
  const ag = (t: number, wallMs: number, name: string) => ({ t, wallMs, taskObjects: wallMs - 10, tasks: [{ stage: 'taskObjects', name, ms: wallMs - 20 }] });
  assert.ok(agentCost(ag(0, 1500, 'Ts3Agent.PollTask')) && !agentCost(ag(0, 1500, 'NRaas.Overwatch.Run')) && !agentCost({ t: 0, wallMs: 400 }), 'agentCost');
  const la2 = scenarioRows({ ...lr, hitches: [...lh, ag(150_000, 1600, 'Ts3Agent.PollTask')], steps: lsteps });
  assert.deepEqual(la2.armRows, la.armRows, 'agent hitch stays out of the arm rows');
  assert.ok(la2.rows.some(r => r.includes(`| ${AGENT_ROW} | `) && r.includes('| 1600 |')), 'agent row');
  assert.ok(!movedWaits([{ t0: 0, fires: [{ a: 60_000, b: 120_000, arm: 'off' }, { a: 120_000, b: 180_000, arm: 'on' }], hitches: [ag(70_000, 900, 'Ts3Agent.PollTask'), ag(130_000, 900, 'Ts3Agent.PollTask')] }]).includes('Ts3Agent'), 'agent out of moved waits');
  // tools on (docs/168 §9): the profiler-cost full job takes the bundle off; the lean job has only prof-lean + frames; an --ab
  // earlyout observe job (and --ab ownjobwait) marks the on arm; every name toolsOn() prints is in tool-costs.json
  const tc: ToolCosts = JSON.parse(readFileSync(TOOL_COSTS, 'utf8'));
  const full = { ini: { 'HitchAttr.Enabled': 1, 'RouteJobPhases.Enabled': 1, 'RouteJobPhases.SyncPlans': 1, 'RouteAsync.LogFallbackMs': 20, 'RouteAsync.LogFallbackStack': 16 }, run: { profile: { noSamplers: true } } };
  assert.deepEqual(toolsOn(full), ['prof-stages', 'prof-tasks', 'frames', 'HitchAttr', 'RouteJobPhases', 'RouteJobPhases.SyncPlans', 'LogFallbackMs<200', 'LogFallbackStack']);
  assert.equal(toolCostLine(toolsOn(full), tc), 'tools on: prof-stages, prof-tasks, frames, HitchAttr, RouteJobPhases, RouteJobPhases.SyncPlans, LogFallbackMs<200, LogFallbackStack; cost taken off: 17.9 ms/s (full measurement); not measured: frames, LogFallbackMs<200, LogFallbackStack');
  assert.deepEqual(toolsOn({ ini: { 'HitchAttr.Enabled': 0, 'RouteAsync.LogFallbackMs': 200 }, run: { profile: { noSamplers: true, profOpts: { stages: false } } } }), ['prof-lean', 'frames']);
  assert.deepEqual(toolsOn({ ini: {}, run: { profile: {} } }), ['prof-stages', 'prof-tasks', 'frames', 'samplers']);
  const eo = toolsOn({ ini: { 'RouteEarlyOut.Enabled': 1, 'RouteEarlyOut.Act': 0 }, run: { profile: { ab: 'earlyout', profOpts: { stages: false } } } });
  assert.deepEqual(eo, ['prof-lean', 'frames', 'RouteEarlyOut.observe (on arm)']);
  assert.deepEqual(toolsOn({ ini: { 'RouteAsync.OwnJobWait': 1 }, run: { profile: { ab: 'ownjobwait', profOpts: { stages: false } } } }), ['prof-lean', 'frames', 'OwnJobWait (on arm)']);
  assert.match(toolCostLine(['prof-lean', 'samplers', 'ProbeSnap'], tc), /cost taken off: 0 ms\/s; not measured: prof-lean; HEAVY \(correctness only, no timing\): samplers, ProbeSnap$/);
  const all = toolsOn({ ini: Object.fromEntries(['HitchAttr', 'RouteJobPhases', 'UnseenProbe', 'PlanRepeat', 'CellMemoProbe', 'ProbeP1b', 'ProbeP10', 'GcThreadCheck', 'IcLockCheck', 'LoadProf', 'ProbeValues', 'ProbeSnap', 'Watchpoint', 'Verify', 'CrashForensics', 'RouteSnap', 'RouteEarlyOut']
    .map(s => [`${s}.Enabled`, 1]).concat(['HitchAttr.Holders', 'RouteJobPhases.SyncPlans', 'RouteJobPhases.HostSplit', 'RouteJobPhases.GoalSplit', 'RouteJobPhases.GoalStepSplit', 'RouteAsync.OwnJobWait', 'RouteAsync.LogFallbackStack'].map(k => [k, 1]), [['RouteAsync.LogFallbackMs', 20]])),
  run: { profile: { mainstack: 'main', taskstall: 250, inroom: true, watchstalk: true, livereads2: true, f4b: true, vres: true, story: {} } } });
  assert.deepEqual(all.filter(t => !tc.tools[t]), [], 'every toolsOn name has a tool-costs.json row');
  // docs/67 §18: Light alone is not heavy; Enabled=1 brings the guards (Guards defaults on) and each opt-in guard switch
  const cf = (ini: Record<string, number>) => toolsOn({ ini, run: { profile: { noProf: true, noSamplers: true } } });
  assert.deepEqual(cf({ 'CrashForensics.Light': 1 }), ['CrashForensics.Light']);
  assert.match(toolCostLine(cf({ 'CrashForensics.Light': 1 }), tc), /^tools on: CrashForensics.Light; cost taken off: 0 ms\/s; not measured: CrashForensics.Light$/);
  assert.deepEqual(cf({ 'CrashForensics.Enabled': 1, 'CrashForensics.Light': 1 }), ['CrashForensics.Light', 'CrashForensics.Guards']);
  assert.deepEqual(cf({ 'CrashForensics.Enabled': 1, 'CrashForensics.Guards': 0, 'CrashForensics.SceneTreeGuard': 1, 'CrashForensics.DbIndexLog': 1 }), ['CrashForensics.Light', 'CrashForensics.DbIndexLog', 'CrashForensics.SceneTreeGuard']);
  const cfAll = cf(Object.fromEntries(['Enabled', 'DrawGuards', 'EffectLog', 'ObjResLog', 'DbIndexLog', 'SceneTreeGuard'].map(k => [`CrashForensics.${k}`, 1])));
  assert.deepEqual(cfAll.filter(t => tc.tools[t]?.class !== (t === 'CrashForensics.Light' ? 'light' : 'heavy')), [], 'guards heavy, Light light, all in tool-costs.json');
  const cs = toolsOn({ ini: { 'ProbeSnap.CallSites': 1 }, run: { profile: { noProf: true } } });
  assert.deepEqual(cs, ['samplers', 'ProbeSnap.CallSites']); assert.equal(tc.tools[cs[1]].timing, 'no');
  assert.ok(!toolsOn({ ini: { 'ProbeSnap.CallSites': 1, 'ProbeSnap.Enabled': 1 } }).includes('ProbeSnap.CallSites'));
  // base ini: a tool the repo ini turns on is listed; the job's override wins; trailing comments do not hide a value
  const baseTxt = '; head\n[HitchAttr]\nEnabled=1 ; probe on\n[RouteAsync]\nLogFallbackMs=200\n[RouteJobPhases]\nEnabled=0\n';
  assert.deepEqual(flatIni(baseTxt), { 'HitchAttr.Enabled': '1', 'RouteAsync.LogFallbackMs': '200', 'RouteJobPhases.Enabled': '0' });
  const nf = { noSamplers: true, profOpts: { stages: false } };
  assert.deepEqual(toolsOn(withBaseIni({ ini: {}, run: { profile: nf } }, baseTxt)), ['prof-lean', 'frames', 'HitchAttr']);
  assert.deepEqual(toolsOn(withBaseIni({ ini: { 'HitchAttr.Enabled': 0, 'RouteJobPhases.Enabled': 1 }, run: { profile: nf } }, baseTxt)), ['prof-lean', 'frames', 'RouteJobPhases']);
  assert.deepEqual(toolsOn(withBaseIni({ ini: null, run: { profile: nf } }, null)), ['prof-lean', 'frames']);
  // the repo ini as committed turns no measurement tool on (release defaults)
  assert.deepEqual(toolsOn(withBaseIni({ ini: {}, run: { profile: nf } }, readFileSync(join(ROOT, 'native', 'ts3native', 'ts3native.ini'), 'utf8'))), ['prof-lean', 'frames']);
  // timing verdict helpers (docs/168 §10): A/B iterations per arm from repro step lines, else kept --ab slices (ABBA 10 min: 2 + 2)
  assert.deepEqual(armIters(['repro x step patches [arm off #1]: {}', 'repro x step reflect_call [fire off #1]: {}', 'repro x step patches [arm on #2]: {}', 'repro x step patches [arm on #3]: {}'], undefined, 0), { on: 2, off: 1, src: 'repro' });
  const abba = [{ from: 0, on: true }, { from: 120, on: false }, { from: 240, on: true }, { from: 360, on: true }, { from: 480, on: false }];
  assert.deepEqual(armIters([], abba, 600), { on: 2, off: 2, src: 'slices' });
  assert.deepEqual(armIters(undefined, abba, 500), { on: 2, off: 1, src: 'slices' }, 'a last slice under half length is out');
  assert.equal(armIters(['save_game'], undefined, 600), null);
  // --reuse: stored verdicts used as is, windows of a repros job, valid-single pairs older -> newer with build flags
  const rr = (job: string, time: string, timing: string, asiSha = 'a1', extra: object = {}) => ({ job, time, timing, timingWhy: '', asiSha, ini: {}, profileTag: `q-${time.slice(0, 10)}-${job}`, run: { profile: {} }, ...extra });
  const qrows = [rr('j', '2026-10-20T01:00', 'valid-single'), rr('j', '2026-10-22T01:00', 'valid-single', 'b2'), rr('j', '2026-10-21T01:00', 'invalid'), rr('k', '2026-10-21T01:00', 'valid'),
    { job: 'w', time: '2026-10-21T02:00', repros: [{ id: 'r1', tag: 't-r1', timing: 'valid' }, { id: 'r2', tag: 't-r2', timing: 'invalid', timingWhy: 'noise high' }] }];
  const never = () => { throw new Error('stored verdicts must not be recomputed'); };
  const cj = reuseCands(qrows, 'j', undefined, never);
  assert.deepEqual(cj.map(c => c.timing), ['valid-single', 'valid-single', 'invalid']);
  const pj = pickReuse(cj);
  assert.deepEqual([pj.ab, pj.singles, pj.pairs.map(x => [x.older, x.newer])], [[], ['q-2026-10-20-j', 'q-2026-10-22-j'], [['q-2026-10-20-j', 'q-2026-10-22-j']]]);
  // valid-half: off-first + on-first of one job and build pool into in-load evidence; a lone or other-build half is listed only
  const hrows = [rr('h-offfirst', '2026-10-21T01:00', 'valid-half'), rr('h-onfirst', '2026-10-21T02:00', 'valid-half'), rr('g-offfirst', '2026-10-21T03:00', 'valid-half'),
    rr('g-onfirst', '2026-10-21T04:00', 'valid-half', 'zz'), rr('e-offfirst', '2026-10-21T05:00', 'valid-half')];
  const ph = pickReuse(reuseCands(hrows, 'h', undefined, never));
  assert.deepEqual([ph.pooled, ph.unpaired, ph.pairs], [['q-2026-10-21-h-offfirst', 'q-2026-10-21-h-onfirst'], [], []]);
  assert.deepEqual(pickReuse(reuseCands(hrows, 'g', undefined, never)).unpaired, ['q-2026-10-21-g-offfirst', 'q-2026-10-21-g-onfirst'], 'different asi: not pooled');
  assert.deepEqual(pickReuse(reuseCands(hrows, 'e', undefined, never)).pooled, []);
  assert.match(pj.pairs[0].title, /WEAKER evidence: two loads; BUILD DIFFERS: asi a1 vs b2\)$/);
  assert.match(pickReuse(reuseCands(qrows.slice(0, 2).map(r => ({ ...r, asiSha: 'a1' })), 'j', undefined, never)).pairs[0].title, /same build\)$/);
  assert.equal(reuseCands(qrows, 'j', '2026-10-21', never).length, 2, '--since');
  assert.deepEqual(pickReuse(reuseCands(qrows, 'k', undefined, never)).ab, ['q-2026-10-21-k']);
  assert.deepEqual(reuseCands(qrows, 'r1', undefined, never).map(c => [c.tag, c.job, c.timing]), [['t-r1', 'w / r1', 'valid']]);
  assert.equal(reuseCands(qrows, 'w', undefined, never).length, 2, 'job id = every window');
  console.log('hitch-ab self-check ok');
}

if (resolve(process.argv[1] ?? '') === fileURLToPath(import.meta.url)) {
  const args = process.argv.slice(2), opt = (k: string) => { const i = args.indexOf(k); return i < 0 ? undefined : args.splice(i, 2)[1]; };
  if (args.includes('--self-check')) selfCheck();
  else if (args.includes('--reuse')) { const id = opt('--reuse')!, since = opt('--since'); console.log(reuseReport(id, since)); }
  else if (args.includes('--power')) console.log(powerReport(Number(opt('--reduction') ?? 0.3), Number(opt('--arm-min') ?? 3.5)));
  else if (args.includes('--scenario')) { args.splice(args.indexOf('--scenario'), 1); console.log(scenarioReport(args)); }
  else if (args.includes('--causes')) {
    args.splice(args.indexOf('--causes'), 1);
    const wh = opt('--wh'), arms = opt('--arms');
    // default: the baseline (off + no-ab runs) table, plus the on arm's own table when any run has one (one table per arm, never mixed)
    if (arms) console.log(causeReport(args, arms.split(','), wh));
    else {
      console.log(causeReport(args, ['off', 'all'], wh));
      if (expandTags(args).some(t => loadRun(t).arms.on)) console.log(`\n### On arm\n${causeReport(args, ['on'], wh)}`);
    }
  } else if (args.includes('--pool')) {
    const label = opt('--pool')!, arms = (opt('--arms') ?? 'off,all').split(',');
    if (opt('--wh')) console.log('--wh is not used by --pool any more: exclusion() decides (docs/161 §6)');
    console.log(`${HEAD}\n${poolReport(label, args, arms)}`);
  } else console.log(hitchReport(args));
}
