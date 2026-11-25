// Camera sweep report (docs/280): splits one profile window's frame times by the agent's camera_sweep segments.
//   node tools/harness/camera-sweep-report.ts --tag <profile tag> [--apex-timeline <csv>]   reads probe-out/profile/<tag>-repro.jsonl
//        (segments from the last camera_sweep {status} reply, else the start reply's plan) and <tag>-*-frames.csv (frame-times.js);
//        writes <tag>-camsweep.json + prints one table row per segment. --apex-timeline: Apex Radiance's FrameTimeline.csv
//        (frame profiler, Developer build) split by the same segments through tick_ms (GetTickCount, low 32 bits = the agent's tickMs).
//   node tools/harness/camera-sweep-report.ts --self-check
// Per segment: frames, p50 / p99 / p99.9 / max frame ms, counts > 25 / 50 / 100 ms, and Apex's hitch definition on our frames:
// frame > max(2 x median of the previous 120 frames, 8 ms) (Apex camera-pan profile §0, frame_profiler.cpp g_mult / g_floorMs).
// Apex columns: frames, hitch flags, room_solve_calls, "Lot room solve" self ms (FUN_006A8BA0 via CALL 0x00ADB9AD), and the
// hitch frames whose largest category column is the lot room solve (their "top counter" lead).
import { existsSync, readdirSync, readFileSync, writeFileSync } from 'node:fs';
import { basename, dirname, join, resolve } from 'node:path';
import { parseArgs } from 'node:util';
import assert from 'node:assert/strict';

export type Seg = { name: string; epochMs0: number; epochMs1: number; tickMs0: number | null; tickMs1: number | null; distEnd?: number; cut?: boolean };
const MOVING = new Set(['zoom_smooth', 'zoom_steps', 'rotate', 'rotate_fast', 'pan']);

// last camera_sweep reply with closed segments wins; else the start reply's plan (epochMs0 + offsets, no tickMs)
export function segmentsOf(reproJsonl: string): { segs: Seg[]; from: 'status' | 'plan' | 'none' } {
  let status: Seg[] | null = null, plan: Seg[] | null = null;
  for (const l of reproJsonl.split(/\r?\n/)) {
    if (!l.trim()) continue;
    let o: any; try { o = JSON.parse(l); } catch { continue; }
    if (o.cmd !== 'camera_sweep') continue;
    const res = o.r?.result ?? o.r;
    if (Array.isArray(res?.segments) && res.segments.some((s: any) => s.epochMs1 !== undefined))
      status = res.segments.filter((s: any) => s.epochMs1 !== undefined).map((s: any) => ({ name: s.name, epochMs0: s.epochMs0, epochMs1: s.epochMs1, tickMs0: s.tickMs0, tickMs1: s.tickMs1, distEnd: s.distEnd, cut: !!s.cut }));
    if (Array.isArray(res?.plan) && typeof res.epochMs0 === 'number')
      plan = res.plan.map((p: any) => ({ name: p.name, epochMs0: res.epochMs0 + p.offMs, epochMs1: res.epochMs0 + p.offMs + p.ms, tickMs0: null, tickMs1: null }));
  }
  return status ? { segs: status, from: 'status' } : plan ? { segs: plan, from: 'plan' } : { segs: [], from: 'none' };
}

const pct = (sorted: number[], p: number) => sorted.length ? sorted[Math.min(sorted.length - 1, Math.max(0, Math.ceil(p * sorted.length) - 1))] : NaN;
const median = (a: number[]) => { const s = [...a].sort((x, y) => x - y); return s.length ? (s.length % 2 ? s[s.length >> 1] : (s[s.length / 2 - 1] + s[s.length / 2]) / 2) : 0; };
// Apex's hitch rule over a frame series in order: frame > max(2 x median of the previous 120 frames, 8 ms)
export function apexHitchFlags(ms: number[], mult = 2, floor = 8, win = 120): boolean[] {
  return ms.map((v, i) => i > 0 && v > Math.max(mult * median(ms.slice(Math.max(0, i - win), i)), floor));
}

function csv(text: string): { head: string[]; rows: string[][] } {
  const [h, ...rows] = text.split(/\r?\n/).filter(Boolean);
  return { head: (h ?? '').split(','), rows: rows.map(r => r.split(',')) };
}
const inTick = (t: number, a: number, b: number) => ((t - a) >>> 0) < ((b - a) >>> 0); // GetTickCount wraps at 2^32

export function report(segs: Seg[], framesCsv: string, apexCsv?: string) {
  const f = csv(framesCsv), ie = f.head.indexOf('epochMs'), im = f.head.indexOf('frameMs');
  if (ie < 0 || im < 0) throw new Error('frames.csv: no epochMs / frameMs column');
  const t = f.rows.map(r => Number(r[ie])), ms = f.rows.map(r => Number(r[im])), ah = apexHitchFlags(ms);
  let apex: { head: string[]; rows: string[][] } | null = apexCsv ? csv(apexCsv) : null;
  const col = (n: string) => apex ? apex.head.indexOf(n) : -1;
  const catFrom = col('smp_other') + 1, catTo = col('Unattributed'), iRs = col('Lot room solve');
  return segs.map(s => {
    const idx = t.map((x, i) => (x >= s.epochMs0 && x < s.epochMs1 ? i : -1)).filter(i => i >= 0);
    const v = idx.map(i => ms[i]).sort((a, b) => a - b);
    const row: Record<string, unknown> = { name: s.name, moving: MOVING.has(s.name), seconds: Math.round((s.epochMs1 - s.epochMs0) / 100) / 10, frames: v.length,
      p50: pct(v, 0.5), p99: pct(v, 0.99), p999: pct(v, 0.999), max: v.length ? v[v.length - 1] : NaN,
      gt25: v.filter(x => x > 25).length, gt50: v.filter(x => x > 50).length, gt100: v.filter(x => x > 100).length,
      apexDefHitches: idx.filter(i => ah[i]).length, distEnd: s.distEnd ?? null };
    if (apex && s.tickMs0 !== null && s.tickMs1 !== null) {
      const it = col('tick_ms'), ih = col('hitch'), ic = col('room_solve_calls'), ix = col('room_solve_max_ms');
      const rows = apex.rows.filter(r => inTick(Number(r[it]), s.tickMs0!, s.tickMs1!)), hit = rows.filter(r => r[ih] === '1');
      const lead = (r: string[]) => { let best = -1, bi = -1; for (let c = catFrom; c < catTo; c++) { const x = Number(r[c] || 0); if (x > best) { best = x; bi = c; } } return bi; };
      const ledRs = iRs >= 0 ? hit.filter(r => lead(r) === iRs).length : 0;
      Object.assign(row, { apexFrames: rows.length, apexHitches: hit.length, roomSolveCalls: rows.reduce((a, r) => a + Number(r[ic] || 0), 0),
        roomSolveMs: Math.round(rows.reduce((a, r) => a + Number(r[iRs] || 0), 0) * 10) / 10, roomSolveMaxMs: rows.reduce((a, r) => Math.max(a, Number(r[ix] || 0)), 0),
        hitchesLedByRoomSolve: ledRs, roomSolveLeadPct: hit.length ? Math.round(100 * ledRs / hit.length) : null });
    }
    return row;
  });
}

export function table(rows: Record<string, unknown>[]): string {
  const apex = rows.some(r => 'apexFrames' in r);
  const cols = ['name', 'seconds', 'frames', 'p50', 'p99', 'p999', 'max', 'gt25', 'gt50', 'gt100', 'apexDefHitches', 'distEnd', ...(apex ? ['apexFrames', 'apexHitches', 'roomSolveCalls', 'roomSolveMs', 'roomSolveMaxMs', 'roomSolveLeadPct'] : [])];
  const cell = (v: unknown) => typeof v === 'number' ? (Number.isNaN(v) ? '-' : String(Math.round(v * 100) / 100)) : v === null || v === undefined ? '-' : String(v);
  return [`| ${cols.join(' | ')} |`, `|${cols.map(() => '---').join('|')}|`, ...rows.map(r => `| ${cols.map(c => cell(r[c])).join(' | ')} |`)].join('\n');
}

// pass rule for the correctness / measurement job: every planned segment closed (status reply), not cut, frames in every moving segment
export function verdict(segs: Seg[], from: string, rows: Record<string, unknown>[], planned = 8): string[] {
  const fails: string[] = [];
  if (from !== 'status') fails.push(`segments from ${from}, not from a camera_sweep status reply`);
  if (segs.length < planned) fails.push(`${segs.length} of ${planned} segments closed`);
  for (const s of segs) if (s.cut) fails.push(`${s.name} cut`);
  for (const r of rows) if (r.moving && !(Number(r.frames) > 0)) fails.push(`${r.name}: no frames`);
  return fails;
}

function selfCheck() {
  const segs: Seg[] = [{ name: 'still_start', epochMs0: 1000, epochMs1: 2000, tickMs0: 4294967000, tickMs1: 704 }, { name: 'zoom_smooth', epochMs0: 2000, epochMs1: 3000, tickMs0: 704, tickMs1: 1704 }];
  const repro = [JSON.stringify({ cmd: 'camera_sweep', r: { ok: true, result: { epochMs0: 1000, plan: [{ name: 'still_start', offMs: 0, ms: 1000 }, { name: 'zoom_smooth', offMs: 1000, ms: 1000 }] } } }),
    JSON.stringify({ cmd: 'camera', r: { ok: true } }),
    JSON.stringify({ cmd: 'camera_sweep', r: { ok: true, result: { running: false, segments: segs.map(s => ({ ...s, sets: 10, distEnd: 30 })) } } })].join('\n');
  assert.equal(segmentsOf(repro).from, 'status');
  assert.equal(segmentsOf(repro).segs[1].tickMs0, 704);
  assert.equal(segmentsOf(repro.split('\n')[0]).from, 'plan');
  assert.equal(segmentsOf(repro.split('\n')[0]).segs[1].epochMs1, 3000);
  assert.equal(segmentsOf('not json\n').from, 'none');
  // Apex rule: 10 ms steady, one 30 ms frame is a hitch (> max(20, 8)), a 15 ms frame is not; floor 8 ms at 2 ms median
  assert.deepEqual(apexHitchFlags([10, 10, 10, 30, 15]), [false, false, false, true, false]);
  assert.deepEqual(apexHitchFlags([2, 2, 7, 9]), [false, false, false, true]); // 7 ms <= the 8 ms floor
  const frames = 'epochMs,qpcMs,frameMs,workMs,fg\n' + [[1100, 16], [1500, 30], [1900, 16], [2100, 16], [2500, 120], [2900, 60], [3500, 16]].map(([t, m]) => `${t},0,${m},1,1`).join('\n');
  // Apex timeline: tick wraps inside the first segment; frame 2 of segment 2 is a hitch led by the lot room solve
  const apexCsv = 'time_s,tick_ms,frame,frame_ms,hitch,room_solve_calls,room_solve_max_ms,smp_other,Render frame (game),Lot room solve,Unattributed\n'
    + ['0.1,4294967100,1,16,0,2,1.0,0,5,1,0', '0.5,100,2,40,1,30,20.0,0,5,30,0', '1.1,800,3,16,0,1,0.5,0,5,1,0', '1.5,1200,4,120,1,27,60.0,0,5,90,0', '1.9,1600,5,60,1,0,0,0,40,1,0', '2.5,2000,6,16,0,0,0,0,5,0,0'].join('\n');
  const rows = report(segs, frames, apexCsv);
  assert.equal(rows[0].frames, 3); assert.equal(rows[1].frames, 3);
  assert.equal(rows[1].max, 120); assert.equal(rows[1].gt25, 2); assert.equal(rows[1].gt50, 2); assert.equal(rows[1].gt100, 1);
  assert.equal(rows[0].apexDefHitches, 0); assert.equal(rows[1].apexDefHitches, 2); // 30 ms <= 2 x 16; 120 and 60 > 2 x 16
  assert.equal(rows[0].apexFrames, 2); assert.equal(rows[0].apexHitches, 1); assert.equal(rows[0].roomSolveCalls, 32); assert.equal(rows[0].roomSolveLeadPct, 100);
  assert.equal(rows[1].apexFrames, 3); assert.equal(rows[1].apexHitches, 2); assert.equal(rows[1].hitchesLedByRoomSolve, 1); assert.equal(rows[1].roomSolveMaxMs, 60);
  assert.equal(pct([1, 2, 3, 4, 5, 6, 7, 8, 9, 10], 0.99), 10);
  assert.deepEqual(verdict(segs, 'status', rows, 2), []);
  assert.equal(verdict(segs, 'plan', rows, 8).length, 2);
  assert.match(table(rows), /\| zoom_smooth \| 1 \| 3 \|/);
  assert.throws(() => report(segs, 'a,b\n1,2'), /no epochMs/);
  console.log('camera-sweep-report self-check OK');
}

if (process.argv[1] && resolve(process.argv[1]) === resolve(import.meta.filename)) {
  const { values } = parseArgs({ options: { tag: { type: 'string' }, 'apex-timeline': { type: 'string' }, 'self-check': { type: 'boolean' } } });
  if (values['self-check']) selfCheck();
  else {
    if (!values.tag) throw new Error('--tag required (or --self-check)');
    const out = join(resolve(import.meta.dirname, '..', '..'), 'probe-out', 'profile'), pre = join(out, values.tag);
    const fr = readdirSync(dirname(pre)).filter(n => n.startsWith(basename(pre) + '-') && n.endsWith('-frames.csv'));
    if (!fr.length) throw new Error(`no ${values.tag}-*-frames.csv in ${dirname(pre)}`);
    const { segs, from } = segmentsOf(existsSync(`${pre}-repro.jsonl`) ? readFileSync(`${pre}-repro.jsonl`, 'utf8') : '');
    const ap = values['apex-timeline'];
    const rows = report(segs, readFileSync(join(dirname(pre), fr[0]), 'utf8'), ap && existsSync(ap) ? readFileSync(ap, 'utf8') : undefined);
    writeFileSync(`${pre}-camsweep.json`, JSON.stringify({ from, segs, rows }, null, 1));
    console.log(table(rows));
    const fails = verdict(segs, from, rows);
    console.log(fails.length ? `FAIL: camera sweep: ${fails.join('; ')}` : `PASS: camera sweep: ${segs.length} segments`);
    if (fails.length) process.exitCode = 1;
  }
}
