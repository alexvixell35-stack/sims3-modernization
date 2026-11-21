// Per-segment frame and sim-tick stats for weather-ab runs (docs/599): reads <run>/segments.tsv (name, startEpochMs,
// endEpochMs per line, written by weather-ab.sh), <run>/frames.csv (frame-times.js, epochMs + frameMs + fg) and
// <run>/tick-<name>.json (ts3agent tick_stats {"reset":true} at the segment end). The first `skip` seconds of each
// segment are dropped (EA ramps the old weather down before the forced one starts).
//   node tools/harness/weather-stats.ts <run dir> [--skip 60]
//   node tools/harness/weather-stats.ts --self-check
import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { join } from 'node:path';

export function pct(sorted: number[], q: number): number {
  if (!sorted.length) return NaN;
  return sorted[Math.min(sorted.length - 1, Math.max(0, Math.ceil(q * sorted.length) - 1))];
}

export function frameStats(ms: number[]) {
  const s = [...ms].sort((a, b) => a - b);
  const avg = s.reduce((a, b) => a + b, 0) / (s.length || 1);
  return { n: s.length, avg, p50: pct(s, 0.5), p95: pct(s, 0.95), p99: pct(s, 0.99), max: s[s.length - 1] ?? NaN, over50: s.filter(x => x >= 50).length };
}

function selfCheck() {
  const st = frameStats([10, 20, 30, 40, 100]);
  assert.equal(st.n, 5); assert.equal(st.avg, 40); assert.equal(st.p50, 30); assert.equal(st.p95, 100); assert.equal(st.over50, 1);
  assert.equal(pct([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20], 0.95), 19);
  console.log('weather-stats self-check ok');
}

if (process.argv.includes('--self-check')) { selfCheck(); process.exit(0); }
const dir = process.argv[2];
if (!dir) { console.error('usage: weather-stats.ts <run dir> [--skip 60]'); process.exit(2); }
const si = process.argv.indexOf('--skip');
const skipMs = (si > 0 ? Number(process.argv[si + 1]) : 60) * 1000;
const segs = readFileSync(join(dir, 'segments.tsv'), 'utf8').trim().split('\n').map(l => l.split('\t')).map(([name, a, b]) => ({ name, a: Number(a), b: Number(b) }));
const fpath = join(dir, 'frames.csv');
let rows: { t: number; ms: number; fg: number }[] = [];
if (existsSync(fpath)) {
  const lines = readFileSync(fpath, 'utf8').trim().split('\n');
  const h = lines[0].split(',');
  const [it, im, ifg] = ['epochMs', 'frameMs', 'fg'].map(k => h.indexOf(k));
  rows = lines.slice(1).map(l => l.split(',')).map(c => ({ t: Number(c[it]), ms: Number(c[im]), fg: Number(c[ifg]) })).filter(r => Number.isFinite(r.ms));
}
const f1 = (x: number) => Number.isFinite(x) ? x.toFixed(1) : 'na';
console.log('segment      frames  fg%   avg   p50   p95   p99    max >=50 | ticks  t/s  avg   p95   p99    max  simMin');
for (const s of segs) {
  const fr = rows.filter(r => r.t >= s.a + skipMs && r.t < s.b);
  const st = frameStats(fr.map(r => r.ms));
  const fg = fr.length ? (100 * fr.filter(r => r.fg === 1).length) / fr.length : NaN;
  const tf = join(dir, `tick-${s.name}.json`);
  let tk: any = {};
  if (existsSync(tf)) { const t = readFileSync(tf, 'utf8'); const i = t.indexOf('{'); try { const j = JSON.parse(t.slice(i)); tk = j.result ?? j; } catch { /* partial */ } }
  console.log(`${s.name.padEnd(12)} ${String(st.n).padStart(6)} ${f1(fg).padStart(4)} ${f1(st.avg).padStart(5)} ${f1(st.p50).padStart(5)} ${f1(st.p95).padStart(5)} ${f1(st.p99).padStart(5)} ${f1(st.max).padStart(6)} ${String(st.over50).padStart(4)} | ` +
    `${String(tk.ticks ?? 'na').padStart(5)} ${f1(tk.ticks_per_s).padStart(4)} ${f1(tk.avg_ms).padStart(4)} ${f1(tk.p95_ms).padStart(5)} ${f1(tk.p99_ms).padStart(5)} ${f1(tk.max_ms).padStart(6)} ${f1(tk.sim_minutes).padStart(6)}`);
}
if (!rows.length) console.log('(no frames.csv: frame columns na; tick columns from tick_stats only)');
