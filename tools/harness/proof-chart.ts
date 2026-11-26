// Visual proof for the README (PLAN.md launch checklist item 2): static SVG charts from existing timing runs, no game run.
//   node tools/harness/proof-chart.ts [--out docs/img]   writes proof-bp.svg, proof-ip.svg, proof-sv.svg, proof-summary.svg
//   node tools/harness/proof-chart.ts --self-check
// Per town: sim step time over one Baseline (our mods off) run and one v0.3.1 (on) run of the same save, the pair closest to
// the README table means (typical, not best), with the 300 ms line; summary: steps >= 300 ms per run and sim steps/s, mean of
// both runs per side (the README table cells). Numbers come from hitch-ab.ts (loadRun/armStats), so they match its table.
// Inputs: probe-out/profile/<tag>-600-{hitches.jsonl,seconds.csv} of the 2026-11-21 v0.3.0/v0.3.1 timing runs.
// Set TS3_REPO=<main checkout> when running from a git worktree (probe-out lives there).
import assert from 'node:assert/strict';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { armStats, loadRun } from './hitch-ab.ts';

const PREFIX = 'timing-v030-1120-2026-11-21';
export const TOWNS = [
  { key: 'bp', name: 'Bridgeport', off: ['off-1', 'off-2'], on: ['on-1-retry', 'on-2'] }, // bp on-1 was rerun as on-1-retry
  { key: 'ip', name: 'Isla Paradiso', off: ['off-1', 'off-2'], on: ['on-1', 'on-2'] },
  { key: 'sv', name: 'Sunset Valley', off: ['off-1', 'off-2'], on: ['on-1', 'on-2'] },
];
const tagOf = (town: string, run: string) => `${PREFIX}-${town}-v030-${run}`;

// colours: dataviz reference palette slots 1 (blue) and 2 (orange), validated there on the light surface; solid card so
// the chart reads the same on GitHub's light and dark themes
const C = { bg: '#fcfcfb', border: '#d8d8d4', ink: '#1f1f1e', ink2: '#5f5f5c', grid: '#e6e6e3', off: '#eb6834', on: '#2a78d6', line: '#d03b3b' };
const FONT = `font-family="-apple-system,'Segoe UI',Helvetica,Arial,sans-serif"`;
const esc = (s: string) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
const text = (x: number, y: number, s: string, a = '') => `<text x="${x}" y="${y}" ${a}>${esc(s)}</text>`;
export const fmt = (v: number) => (Number.isInteger(Math.round(v * 10) / 10) ? String(Math.round(v)) : v.toFixed(1));
const num = (v: number) => v.toLocaleString('en-US');

export type RunStats = { run: string; iters: number; secs: number; itps: number; p99: number; max: number; n300: number };
export type Pt = { min: number; ms: number };

// slowest step per pixel column: keeps every spike visible without one SVG line per iteration
export function bucketMax(pts: Pt[], x0: number, x1: number, cols: number): number[] {
  const out = new Array(cols).fill(0);
  for (const p of pts) {
    if (p.min < x0 || p.min > x1) continue;
    const c = Math.min(cols - 1, Math.floor((p.min - x0) / (x1 - x0) * cols));
    out[c] = Math.max(out[c], p.ms);
  }
  return out;
}

// pair i (off-i vs on-i): how far its on/off change (log ratio, +1 so zero counts work) is from the table's change (mean on /
// mean off) on it/s, p99, worst step and steps >= 300 ms, summed; lowest = the typical pair. Per-run distance to the side mean
// cannot decide with 2 runs per side (both runs are equally far). A tie goes to the smaller it/s gain (never the flattering one).
export function pickPair(off: RunStats[], on: RunStats[]): { i: number; scores: number[] } {
  const mean = (xs: RunStats[], k: keyof RunStats) => xs.reduce((s, x) => s + (x[k] as number), 0) / xs.length;
  const lr = (a: number, b: number) => Math.log((a + 1) / (b + 1));
  const scores = off.map((_, i) => (['itps', 'p99', 'max', 'n300'] as const).reduce((s, k) => s + Math.abs(lr(on[i][k], off[i][k]) - lr(mean(on, k), mean(off, k))), 0));
  const gain = (i: number) => on[i].itps / off[i].itps;
  const i = scores.map((_, i) => i).sort((a, b) => Math.abs(scores[a] - scores[b]) < 1e-6 ? gain(a) - gain(b) : scores[a] - scores[b])[0];
  return { i, scores };
}

function stats(town: string, run: string) {
  const r = loadRun(tagOf(town, run)), a = r.arms.all;
  if (!a) throw new Error(`${r.tag}: no 'all' arm`);
  if (r.invalid) throw new Error(`${r.tag}: run marked invalid`);
  const s = armStats(a), t0 = r.rows[0].t - 1000; // rows[i].t = QPC ms at the end of second i
  const st: RunStats = { run, iters: s.iters, secs: a.secs, itps: s.iters / a.secs, p99: s.p99.ms, max: s.max, n300: s.counts[3] };
  return { st, pts: a.hitches.map(h => ({ min: (h.t - t0) / 60000, ms: h.wallMs })) as Pt[] };
}

export function timeChart(name: string, sides: { label: string; color: string; st: RunStats; pts: Pt[] }[]): string {
  const W = 900, L = 64, R = 64, PH = 150, GAP = 64, TOP = 88, x0 = 1, x1 = 10, cols = W - L - R;
  const ymax = Math.max(400, Math.ceil(Math.max(...sides.flatMap(s => s.pts.map(p => p.ms))) / 100) * 100);
  const H = TOP + sides.length * (PH + GAP) + 10;
  const xs = (m: number) => L + (m - x0) / (x1 - x0) * cols;
  const ystep = ymax > 1000 ? 500 : ymax > 600 ? 200 : 100;
  const out = [`<svg xmlns="http://www.w3.org/2000/svg" width="${W}" height="${H}" viewBox="0 0 ${W} ${H}" ${FONT} role="img" aria-label="${esc(`${name}: sim step time, Baseline vs v0.3.1`)}">`,
    `<rect x="0.5" y="0.5" width="${W - 1}" height="${H - 1}" rx="8" fill="${C.bg}" stroke="${C.border}"/>`,
    text(16, 26, `${name}: sim step time over a 10-minute run, Baseline vs v0.3.1`, `font-size="16" font-weight="600" fill="${C.ink}"`),
    text(16, 46, `Each mark: the slowest sim step in a ${((x1 - x0) * 60 / cols).toFixed(1)} s slice; steps under 50 ms are not recorded. Dashed line: 300 ms, a stutter you notice.`, `font-size="11.5" fill="${C.ink2}"`),
    text(16, 62, 'Minute 0-1 (loading) and harness-caused steps are left out, as in the README table. Same save, same machine.', `font-size="11.5" fill="${C.ink2}"`)];
  sides.forEach((s, k) => {
    const top = TOP + k * (PH + GAP), base = top + PH, ys = (ms: number) => base - ms / ymax * PH;
    out.push(text(L, top - 10, `${s.label}: ${s.st.itps.toFixed(1)} sim steps/s, ${s.st.n300} steps of 300 ms or longer, worst ${Math.round(s.st.max)} ms`, `font-size="12.5" font-weight="600" fill="${C.ink}"`));
    out.push(`<rect x="${L - 14}" y="${top - 20}" width="10" height="10" rx="2" fill="${s.color}"/>`);
    for (let v = 0; v <= ymax; v += ystep) {
      out.push(`<line x1="${L}" x2="${W - R}" y1="${ys(v)}" y2="${ys(v)}" stroke="${v ? C.grid : C.ink2}" stroke-width="1"/>`);
      out.push(text(L - 6, ys(v) + 4, String(v), `font-size="11" fill="${C.ink2}" text-anchor="end"`));
    }
    for (let m = x0; m <= x1; m++) out.push(text(xs(m), base + 15, String(m), `font-size="11" fill="${C.ink2}" text-anchor="middle"`));
    const d = bucketMax(s.pts, x0, x1, cols).map((ms, c) => ms ? `M${L + c + 0.5} ${base}V${ys(ms).toFixed(1)}` : '').join('');
    out.push(`<path d="${d}" stroke="${s.color}" stroke-width="1" fill="none"/>`);
    out.push(`<line x1="${L}" x2="${W - R}" y1="${ys(300)}" y2="${ys(300)}" stroke="${C.line}" stroke-width="1.5" stroke-dasharray="6 4"/>`);
    out.push(text(W - R + 6, ys(300) + 4, '300 ms', `font-size="11" font-weight="600" fill="${C.line}"`));
    out.push(text(16, top + PH / 2, 'step time, ms', `font-size="11" fill="${C.ink2}" text-anchor="middle" transform="rotate(-90 16 ${top + PH / 2})"`));
    out.push(text(L + cols / 2, base + 31, 'minutes into the run', `font-size="11" fill="${C.ink2}" text-anchor="middle"`));
  });
  out.push('</svg>');
  return out.join('\n') + '\n';
}

export function summaryChart(rows: { name: string; off: RunStats[]; on: RunStats[] }[]): string {
  const W = 900, PW = 390, PH = 190, TOP = 92, L = 40, H = TOP + PH + 72;
  const mean = (xs: RunStats[], k: 'n300' | 'itps') => xs.reduce((s, x) => s + x[k], 0) / xs.length;
  const out = [`<svg xmlns="http://www.w3.org/2000/svg" width="${W}" height="${H}" viewBox="0 0 ${W} ${H}" ${FONT} role="img" aria-label="Steps of 300 ms or longer and sim steps per second, Baseline vs v0.3.1">`,
    `<rect x="0.5" y="0.5" width="${W - 1}" height="${H - 1}" rx="8" fill="${C.bg}" stroke="${C.border}"/>`,
    text(16, 26, 'Baseline vs v0.3.1, 10-minute runs, mean of 2 runs per side', `font-size="16" font-weight="600" fill="${C.ink}"`),
    `<rect x="16" y="38" width="10" height="10" rx="2" fill="${C.off}"/>`, text(31, 47, 'Baseline (our mods off)', `font-size="12" fill="${C.ink}"`),
    `<rect x="190" y="38" width="10" height="10" rx="2" fill="${C.on}"/>`, text(205, 47, 'v0.3.1 (our mods on)', `font-size="12" fill="${C.ink}"`)];
  const panels: { title: string; k: 'n300' | 'itps'; sub: string }[] = [
    { title: 'Steps of 300 ms or longer, per run', k: 'n300', sub: 'lower is better' },
    { title: 'Sim steps per second', k: 'itps', sub: 'higher is better' }];
  panels.forEach((p, j) => {
    const px = L + j * (PW + 50), base = TOP + PH, vmax = Math.max(...rows.flatMap(r => [mean(r.off, p.k), mean(r.on, p.k)])) * 1.15;
    out.push(text(px, TOP - 18, p.title, `font-size="13" font-weight="600" fill="${C.ink}"`), text(px + PW, TOP - 18, p.sub, `font-size="11" fill="${C.ink2}" text-anchor="end"`));
    out.push(`<line x1="${px}" x2="${px + PW}" y1="${base}" y2="${base}" stroke="${C.ink2}"/>`);
    const gw = PW / rows.length, bw = 34;
    rows.forEach((r, i) => {
      const cx = px + gw * (i + 0.5);
      ([[r.off, C.off, -1], [r.on, C.on, 1]] as const).forEach(([xs, col, side]) => {
        const v = mean(xs as RunStats[], p.k), h = Math.max(1, v / vmax * PH), x = cx + (side < 0 ? -bw - 2 : 2);
        out.push(`<path d="M${x} ${base}V${base - h + 4}q0 -4 4 -4h${bw - 8}q4 0 4 4V${base}Z" fill="${col}"/>`);
        out.push(text(x + bw / 2, base - h - 5, p.k === 'itps' ? v.toFixed(1) : fmt(v), `font-size="12" fill="${C.ink}" text-anchor="middle"`));
      });
      out.push(text(cx, base + 17, r.name, `font-size="12" fill="${C.ink}" text-anchor="middle"`));
      const runs = (xs: RunStats[]) => xs.map(x => p.k === 'itps' ? x.itps.toFixed(1) : String(x.n300)).join(' / ');
      out.push(text(cx, base + 33, `runs off: ${runs(r.off)}`, `font-size="10.5" fill="${C.ink2}" text-anchor="middle"`), text(cx, base + 47, `runs on: ${runs(r.on)}`, `font-size="10.5" fill="${C.ink2}" text-anchor="middle"`));
    });
  });
  out.push('</svg>');
  return out.join('\n') + '\n';
}

function selfCheck() {
  assert.deepEqual(bucketMax([{ min: 1, ms: 60 }, { min: 1.01, ms: 90 }, { min: 10, ms: 70 }, { min: 0.5, ms: 999 }], 1, 10, 9), [90, 0, 0, 0, 0, 0, 0, 0, 70]);
  const rs = (itps: number, p99: number, n300: number, run = ''): RunStats => ({ run, iters: 0, secs: 0, itps, p99, max: 0, n300 });
  // pair 2's change (+50% it/s) is the table's change; pairs 0 (+100%) and 1 (0%) are not
  assert.equal(pickPair([rs(40, 110, 9), rs(40, 110, 9), rs(40, 110, 9)], [rs(80, 70, 1), rs(40, 70, 1), rs(60, 70, 1)]).i, 2);
  // identical pairs tie: the first one stays
  assert.equal(pickPair([{ ...rs(40, 100, 5), max: 500 }, { ...rs(40, 100, 5), max: 500 }], [{ ...rs(50, 70, 0), max: 500 }, { ...rs(50, 70, 0), max: 500 }]).i, 0);
  assert.equal(fmt(5.5), '5.5'); assert.equal(fmt(151), '151'); assert.equal(fmt(0.5), '0.5'); assert.equal(fmt(18), '18');
  const svg = timeChart('T<1>', [{ label: 'a', color: C.off, st: rs(40, 100, 1), pts: [{ min: 2, ms: 320 }] }]) + summaryChart([{ name: 'T', off: [rs(40, 100, 5)], on: [rs(50, 70, 0)] }]);
  assert.ok(!/NaN|undefined|Infinity/.test(svg), 'no bad numbers');
  assert.ok(svg.includes('T&lt;1&gt;'), 'text escaped');
  assert.equal((svg.match(/<svg /g) ?? []).length, (svg.match(/<\/svg>/g) ?? []).length);
  console.log('proof-chart self-check: ok');
}

if (resolve(process.argv[1] ?? '') === fileURLToPath(import.meta.url)) {
  const args = process.argv.slice(2);
  if (args.includes('--self-check')) { selfCheck(); process.exit(0); }
  const oi = args.indexOf('--out'), outDir = oi >= 0 ? resolve(args[oi + 1]) : join(import.meta.dirname, '..', '..', 'docs', 'img');
  mkdirSync(outDir, { recursive: true });
  const sum: { name: string; off: RunStats[]; on: RunStats[] }[] = [];
  for (const t of TOWNS) {
    const off = t.off.map(r => stats(t.key, r)), on = t.on.map(r => stats(t.key, r));
    const { i, scores } = pickPair(off.map(x => x.st), on.map(x => x.st));
    console.log(`${t.name}: pair ${t.off[i]} + ${t.on[i]} (scores ${scores.map(s => s.toFixed(3)).join(', ')})`);
    for (const x of [...off, ...on]) console.log(`  ${x.st.run}: ${x.st.itps.toFixed(1)} it/s, p99 ${x.st.p99}, >=300 ${x.st.n300}, max ${Math.round(x.st.max)}`);
    writeFileSync(join(outDir, `proof-${t.key}.svg`), timeChart(t.name, [
      { label: `Baseline (our mods off), run ${t.off[i]}`, color: C.off, ...off[i] },
      { label: `v0.3.1 (our mods on), run ${t.on[i]}`, color: C.on, ...on[i] }]));
    sum.push({ name: t.name, off: off.map(x => x.st), on: on.map(x => x.st) });
  }
  writeFileSync(join(outDir, 'proof-summary.svg'), summaryChart(sum));
  for (const s of sum) console.log(`${s.name} mean: >=300 ${fmt((s.off[0].n300 + s.off[1].n300) / 2)} -> ${fmt((s.on[0].n300 + s.on[1].n300) / 2)}, it/s ${((s.off[0].itps + s.off[1].itps) / 2).toFixed(1)} -> ${((s.on[0].itps + s.on[1].itps) / 2).toFixed(1)}`);
}
