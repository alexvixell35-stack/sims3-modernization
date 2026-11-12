// docs/222 §11 default-ON gate for unseen-direct go-live: reads finished --ab and outcome runs, prints the bars. Reads only.
//   node tools/harness/gate2-report.ts <tag or tag prefix> [...]   exit 1 when a bar FAILs; a bar without data says "no data"
//   node tools/harness/gate2-report.ts --self-check
// Per --ab run: `net` = ts3fixes' saved - cost us per on-arm second (the saved side is the guard's vanilla-rate model, docs/222 §6.3),
// `stage` = script + taskObjects ms/s, ms/iter = threaded / iters, cadence = searches per Sim-hour by class, real GoHome runs.
// Outcome runs (<job>-out-off / -out-on, activity.jsonl of 30 s samples): §11.3.
// V6 (Rodney 11-06): pushes per seen Sim-hour within +-10% pooled per world (a push shows on screen, a no-pick retry does not); searches stay a log row beside it, not a bar (§11.8).
// --ab unseenrm and unseenboat runs print R1-R6 only. Jobs of one question (fastspeed, speed1, seengame, credit) are read only by naming them, never pooled into the bars of a queue.
import assert from 'node:assert/strict';
import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { rateCompare } from './hitch-ab.ts';

const ROOT = process.env.TS3_REPO ?? resolve(import.meta.dirname, '..', '..');
const PROFILE = join(ROOT, 'probe-out', 'profile');
export const BAR = { netMs: 3, iterPct: 2, seenPct: 10, venueShare: 0.75, awayExtra: 2, strandedExtra: 1, starveExtra: 0, strandSamples: 8, auditMs: 0.5,
  // docs/246 §6 routing bars R1-R6: judgement thresholds from one data set, adjust after the first pair
  r1PlansPct: 10, r2WorkPct: 10, r3LenPct: 15, r4FailMult: 1.5, r4FailExtra: 5, r6BurstMult: 1.25, r6MinSecs: 500 };

type Slice = { from: number; on: boolean; warmup?: boolean };
type Row = { i: number; s: number; c: Record<string, number> };
type NativeRow = { i: number; s: number; c: { autshadow?: Record<string, number>; asyncUs?: number; asyncStarted?: number; ufLong?: number } };
export type Arm = { secs: number; saved: number; cost: number; seen: number; seenH: number; unseen: number; unseenH: number; gohome: number; stage: number; threaded: number; iters: number; n: number;
  seenPush: number; seenNoPick: number; hh: number; hhPush: number; hhH: number; hhQN: number; hhQT: number; hhWN: number; hhWT: number; qSeenN: number; qSeenT: number; qUnseenN: number; qUnseenT: number; depth: number; depthN: number; empty: number;
  lenSeen: number; lenUnseen: number; exact: number; exactNp: number; game: number; gameNp: number; auditUs: number; nativeSecs: number;
  plans: number; fails: number; asyncUs: number; asyncN: number; ufLong: number; rps: number[] };
const zero = (): Arm => ({ secs: 0, saved: 0, cost: 0, seen: 0, seenH: 0, unseen: 0, unseenH: 0, gohome: 0, stage: 0, threaded: 0, iters: 0, n: 0,
  seenPush: 0, seenNoPick: 0, hh: 0, hhPush: 0, hhH: 0, hhQN: 0, hhQT: 0, hhWN: 0, hhWT: 0, qSeenN: 0, qSeenT: 0, qUnseenN: 0, qUnseenT: 0, depth: 0, depthN: 0, empty: 0,
  lenSeen: 0, lenUnseen: 0, exact: 0, exactNp: 0, game: 0, gameNp: 0, auditUs: 0, nativeSecs: 0,
  plans: 0, fails: 0, asyncUs: 0, asyncN: 0, ufLong: 0, rps: [] });

export function armsOf(slices: Slice[], rows: Row[], csv: string, native: NativeRow[] = []): { on: Arm; off: Arm } {
  const a = { on: zero(), off: zero() };
  for (let x = 0; x + 1 < rows.length; x++) {
    const sl = slices[rows[x].i];
    if (!sl || sl.warmup) continue;
    const t = a[sl.on ? 'on' : 'off'], d = (k: string) => (rows[x + 1].c[k] ?? 0) - (rows[x].c[k] ?? 0), hours = d('g8_simhours_x100') / 100, uh = d('g8_unseen_simhours_x100') / 100;
    t.secs += rows[x + 1].s - rows[x].s; t.saved += d('autunseen_saved_us'); t.cost += d('autunseen_cost_us');
    t.seen += d('g8_seen_searches'); t.seenH += hours - uh; t.unseen += d('g8_unseen_searches'); t.unseenH += uh; t.gohome += d('gohome_runs');
    t.seenPush += d('g8_seen_pushes'); t.seenNoPick += d('g8_seen_null_picks'); t.hh += d('g8_hh_searches'); t.hhPush += d('g8_hh_pushes'); t.hhH += d('g8_hh_simhours_x100') / 100;
    t.hhQN += d('g8_hh_q_n'); t.hhQT += d('g8_hh_q_ticks'); t.hhWN += d('g8_hh_w_n'); t.hhWT += d('g8_hh_w_ticks');
    t.qSeenN += d('g8_q_seen_n'); t.qSeenT += d('g8_q_seen_ticks'); t.qUnseenN += d('g8_q_unseen_n'); t.qUnseenT += d('g8_q_unseen_ticks');
    t.depth += d('g8_q_high_sum') + d('g8_q_low_sum'); t.depthN += d('g8_q_samples'); t.empty += d('g8_q_empty');
    t.lenSeen += d('g8_len_seen_ticks'); t.lenUnseen += d('g8_len_unseen_ticks');
    t.plans += d('backoff_plans'); t.fails += d('backoff_failures');
    t.exact += d('g8_seen_exact'); t.exactNp += d('g8_seen_exact_nopick'); t.game += d('g8_seen_game'); t.gameNp += d('g8_seen_game_nopick');
  }
  for (let x = 0; x + 1 < native.length; x++) { // Audit=N slice of the cut, on the sim thread (ts3native cutAuditUs, autshadow.cpp)
    const sl = slices[native[x].i], A = native[x].c.autshadow, B = native[x + 1].c.autshadow;
    if (!sl || sl.warmup) continue;
    const t = a[sl.on ? 'on' : 'off'], dn = (k: 'asyncUs' | 'asyncStarted' | 'ufLong') => (native[x + 1].c[k] ?? 0) - (native[x].c[k] ?? 0);
    t.asyncUs += dn('asyncUs'); t.asyncN += dn('asyncStarted'); t.ufLong += dn('ufLong');
    if (!A || !B) continue;
    t.auditUs += (B.cutAuditUs ?? 0) - (A.cutAuditUs ?? 0); t.nativeSecs += native[x + 1].s - native[x].s;
  }
  const [head, ...lines] = csv.trim().split(/\r?\n/), H = head.split(','), col = (n: string) => H.indexOf(n);
  for (const l of lines) {
    const c = l.split(','), arm = c[col('arm')];
    if ((arm !== '0' && arm !== '1') || +c[col('iters')] === 0) continue;
    const t = a[arm === '1' ? 'on' : 'off'];
    t.rps.push(+c[col('routes')] + +c[col('routeOtherThreads')]);
    t.n++; t.stage += +c[col('script')] + +c[col('taskObjects')]; t.threaded += +c[col('threaded')]; t.iters += +c[col('iters')];
  }
  return a;
}
const netMs = (a: Arm) => a.secs ? (a.saved - a.cost) / 1000 / a.secs : 0;
const pct = (on: number, off: number) => off ? 100 * (on - off) / off : 0;
export const seenCadencePct = (on: Arm, off: Arm) => pct(on.seenPush / on.seenH, off.seenPush / off.seenH); // pushes, not searches
const pushNoData = (on: Arm, off: Arm) => !on.seenPush || !off.seenPush || !on.seenH || !off.seenH; // build without the 785-817 counters
const auditMs = (a: Arm) => a.nativeSecs ? a.auditUs / 1000 / a.nativeSecs : 0;
const per = (n: number, d: number, dp = 2) => d ? (n / d).toFixed(dp) : 'n/a';
// the §11.8 splits; a build without the 785-817 counters prints n/a, without 867-870 the household waits do
function v6Log(w: string, on: Arm, off: Arm) {
  const L = [{ name: `log ${w} seen pushes per seen Sim-hour`, ok: null, text: `${per(off.seenPush, off.seenH, 3)} -> ${per(on.seenPush, on.seenH, 3)} (${pct(on.seenPush / on.seenH, off.seenPush / off.seenH).toFixed(0)}%)` },
    { name: `log ${w} seen no-pick searches per seen Sim-hour`, ok: null, text: `${per(off.seenNoPick, off.seenH, 3)} -> ${per(on.seenNoPick, on.seenH, 3)} (${pct(on.seenNoPick / on.seenH, off.seenNoPick / off.seenH).toFixed(0)}%)` }];
  if (!on.depthN && !off.depthN) return L;
  const q = (a: Arm) => `wait ticks seen ${per(a.qSeenT, a.qSeenN, 1)} / unseen ${per(a.qUnseenT, a.qUnseenN, 1)}, queue ${per(a.depth, a.depthN, 1)}, queues empty at ${per(100 * a.empty, a.depthN, 0)}% of search ends, manager busy ${per(100 * (a.lenSeen + a.lenUnseen + a.seen + a.unseen), a.iters, 0)}% of ticks`;
  const np = (a: Arm) => `exact ${a.exact} (no-pick ${per(100 * a.exactNp, a.exact, 0)}%), game ${a.game} (no-pick ${per(100 * a.gameNp, a.game, 0)}%)`;
  L.push({ name: `log ${w} manager off`, ok: null, text: q(off) }, { name: `log ${w} manager on`, ok: null, text: q(on) },
    { name: `log ${w} seen path`, ok: null, text: `off ${np(off)}; on ${np(on)}` },
    { name: `log ${w} household`, ok: null, text: `searches/h ${per(off.hh, off.hhH, 3)} -> ${per(on.hh, on.hhH, 3)}, pushes/h ${per(off.hhPush, off.hhH, 3)} -> ${per(on.hhPush, on.hhH, 3)}, queue wait ticks ${per(off.hhQT, off.hhQN, 1)} -> ${per(on.hhQT, on.hhQN, 1)}, worker wait ticks ${per(off.hhWT, off.hhWN, 1)} -> ${per(on.hhWT, on.hhWN, 1)}` });
  return L;
}

// noise view: per run G1 net, G2 ms/iter change and V6 seen-push change, as mean (min..max, SD) per order and over all runs; any number of runs per order
const stat = (v: number[], dp: number) => { const m = v.reduce((s, x) => s + x, 0) / v.length, sd = v.length > 1 ? Math.sqrt(v.reduce((s, x) => s + (x - m) ** 2, 0) / (v.length - 1)) : 0;
  return `${m.toFixed(dp)} (${Math.min(...v).toFixed(dp)}..${Math.max(...v).toFixed(dp)}, SD ${sd.toFixed(dp)}, n=${v.length})`; };
export function spreadLog(w: string, rs: { order: string; a: { on: Arm; off: Arm } }[]) {
  if (rs.length < 2) return [];
  const m: [string, (a: { on: Arm; off: Arm }) => number, number][] = [['G1 net ms/s', a => netMs(a.on), 1], ['G2 ms/iter %', a => pct(a.on.threaded / a.on.iters, a.off.threaded / a.off.iters), 1], ['V6 seen pushes %', a => seenCadencePct(a.on, a.off), 0]];
  const orders = [...new Set(rs.map(r => r.order))];
  return m.map(([n, f, dp]) => ({ name: `log ${w} spread ${n} per run`, ok: null as boolean | null, text: [...orders.map(o => `${o} ${stat(rs.filter(r => r.order === o).map(r => f(r.a)), dp)}`), `all ${stat(rs.map(r => f(r.a)), dp)}`].join('; ') }));
}

type Sample = { t: number; sims: [string, string | null, string?, boolean?, boolean?, number?, boolean?][] };
export type Outcome = { samples: number; neverHome: number; stranded: number; starving: number; venueSims: number; atHome: number; recorded: boolean };
export function outcome(jsonl: string): Outcome {
  const S = jsonl.split(/\r?\n/).filter(Boolean).map(l => JSON.parse(l) as Sample), seen = new Map<string, { home: boolean; streak: number; lot?: string; max: number; low: boolean }>();
  let venue = 0, home = 0, all = 0;
  for (const r of S) for (const [name, inter, lot, atHome, community, need] of r.sims) {
    const e = seen.get(name) ?? { home: false, streak: 0, max: 0, low: false };
    all++; if (atHome) { e.home = true; home++; } if (community) venue++;
    if (need !== undefined && need <= -95) e.low = true;
    e.streak = lot && !atHome && !inter && lot === e.lot ? e.streak + 1 : 0; // stranded: same foreign lot, nothing to do, sample after sample
    e.lot = lot; e.max = Math.max(e.max, e.streak); seen.set(name, e);
  }
  const v = [...seen.values()];
  return { samples: S.length, neverHome: v.filter(e => !e.home).length, stranded: v.filter(e => e.max >= BAR.strandSamples).length, starving: v.filter(e => e.low).length,
    venueSims: S.length ? venue / S.length : 0, atHome: all ? home / all : 0, recorded: S.some(r => r.sims[0]?.length >= 7) };
}

// docs/246 §6 R1-R6: routing must not rise with the unseen work. Every threshold is judgement (BAR.r*), not measured.
// Pooled per world, and a rise must show in both orders (the slice-order artefact, §1); one order = a log row, no verdict.
type Bar = { name: string; ok: boolean | null; text: string };
type Sum = { plans: number; fails: number; simH: number; us: number; n: number };
const p999 = (v: number[]) => v.length ? [...v].sort((a, b) => a - b)[Math.min(v.length - 1, Math.floor(v.length * 0.999))] : 0;
const sumOf = (rs: { a: { on: Arm; off: Arm } }[], k: 'on' | 'off'): Sum => rs.reduce((p, r) => { const a = r.a[k]; p.plans += a.plans; p.fails += a.fails; p.simH += a.seenH + a.unseenH; p.us += a.asyncUs; p.n += a.asyncN; return p; }, { plans: 0, fails: 0, simH: 0, us: 0, n: 0 });
export function routingBars(w: string, rs: { order: string; a: { on: Arm; off: Arm } }[]): Bar[] {
  const L: Bar[] = [], orders = [...new Set(rs.map(r => r.order))], both = orders.length === 2, tail = both ? '' : ' (one order: log only)';
  const on = sumOf(rs, 'on'), off = sumOf(rs, 'off');
  const plansH = (x: Sum) => x.plans && x.simH ? x.plans / x.simH : NaN, usH = (x: Sum) => x.us && x.simH ? x.us / x.simH : NaN, mean = (x: Sum) => x.n ? x.us / x.n : NaN;
  // FAIL when pooled is up over the limit and up in every order (`also` = a second condition, R3 needs R2 up)
  const rise = (name: string, f: (x: Sum) => number, limit: number, show: (x: Sum) => string, also = true) => {
    const pooled = pct(f(on), f(off)), ords = orders.map(o => { const q = rs.filter(r => r.order === o); return pct(f(sumOf(q, 'on')), f(sumOf(q, 'off'))); });
    if (!isFinite(pooled) || ords.some(o => !isFinite(o))) { L.push({ name, ok: null, text: 'no data' }); return NaN; }
    L.push({ name, ok: both ? !(pooled > limit && ords.every(o => o > 0) && also) : null, text: `${show(off)} -> ${show(on)} (${pooled.toFixed(1)}%, orders ${ords.map(o => o.toFixed(0)).join('/')}%)${tail}` });
    return pooled;
  };
  rise(`R1 ${w} plans per Sim-hour not up > ${BAR.r1PlansPct}% (judgement)`, plansH, BAR.r1PlansPct, x => plansH(x).toFixed(2));
  const r2 = rise(`R2 ${w} route work (asyncUs) per Sim-hour not up > ${BAR.r2WorkPct}% (judgement)`, usH, BAR.r2WorkPct, x => `${(usH(x) / 1000).toFixed(1)} ms`);
  rise(`R3 ${w} job mean not up > ${BAR.r3LenPct}% with R2 up (judgement)`, mean, BAR.r3LenPct, x => `${(mean(x) / 1000).toFixed(1)} ms`, r2 > 0);
  const exp = off.plans ? off.fails / off.plans * on.plans : NaN;
  L.push({ name: `R4 ${w} route fails <= ${BAR.r4FailMult} x off rate + ${BAR.r4FailExtra} events (judgement)`, ok: isFinite(exp) && both ? on.fails <= BAR.r4FailMult * exp + BAR.r4FailExtra : null, text: isFinite(exp) ? `${off.fails}/${off.plans} -> ${on.fails}/${on.plans}${tail}` : 'no data' });
  // docs/23 §21: pooled exact-Poisson on/off rate ratio over the arms' native seconds (hitch-ab rateCompare); FAIL only when the
  // 95% CI lies wholly above 1. The old "+2 per pair" failed on noise (IP: pooled 46 vs 42).
  const ev = (k: 'on' | 'off') => ({ k: rs.reduce((s, r) => s + r.a[k].ufLong, 0), mins: rs.reduce((s, r) => s + r.a[k].nativeSecs, 0) / 60 });
  const lOn = ev('on'), lOff = ev('off'), have = rs.some(r => r.a.on.asyncN || r.a.off.asyncN) && lOn.mins > 0 && lOff.mins > 0;
  const c = have ? rateCompare(lOn, lOff) : null, d = rs.map(r => r.a.on.ufLong - r.a.off.ufLong);
  L.push({ name: `R5 ${w} ufLong (>= 300 ms) pooled on/off rate ratio: 95% CI not wholly above 1 (exact Poisson, docs/23 §21)`, ok: c ? !(c.ratio.lo > 1) : null,
    text: c ? `${lOff.k} in ${lOff.mins.toFixed(1)} min -> ${lOn.k} in ${lOn.mins.toFixed(1)} min, ratio ${c.ratio.est.toFixed(2)} [${c.ratio.lo.toFixed(2)}, ${c.ratio.hi.toFixed(2)}] (${c.verdict}); on - off per run: ${d.join(', ')}` : 'no data' });
  const a = rs.flatMap(r => r.a.on.rps), b = rs.flatMap(r => r.a.off.rps), enough = a.length >= BAR.r6MinSecs && b.length >= BAR.r6MinSecs;
  L.push({ name: `R6 ${w} p99.9 routes per second <= ${BAR.r6BurstMult} x off (judgement)`, ok: enough ? p999(a) <= BAR.r6BurstMult * p999(b) : null, text: enough ? `${p999(b)} -> ${p999(a)}` : `no data (needs ${BAR.r6MinSecs} s per arm)` });
  return L;
}

export function bars(runs: { world: string; order: string; a: { on: Arm; off: Arm } }[], out: Record<string, { on?: Outcome; off?: Outcome }>): { name: string; ok: boolean | null; text: string }[] {
  const worlds = [...new Set(runs.map(r => r.world))], L: { name: string; ok: boolean | null; text: string }[] = [];
  const pool = (w: string, k: 'on' | 'off') => runs.filter(r => r.world === w).reduce((p, r) => { for (const f of Object.keys(p) as (keyof Arm)[]) if (Array.isArray(p[f])) (p[f] as number[]).push(...(r.a[k][f] as number[])); else (p as Record<string, number>)[f] += r.a[k][f] as number; return p; }, zero());
  const orders = (w: string) => new Set(runs.filter(r => r.world === w).map(r => r.order)).size === 2;
  const nets = runs.map(r => netMs(r.a.on));
  L.push({ name: `G1 net >= ${BAR.netMs} ms/s every run, both orders`, ok: runs.length && worlds.every(orders) ? nets.every(n => n >= BAR.netMs) : null, text: runs.map(r => `${r.world} ${r.order} ${netMs(r.a.on).toFixed(1)}`).join(', ') });
  for (const w of worlds) {
    const on = pool(w, 'on'), off = pool(w, 'off'), it = pct(on.threaded / on.iters, off.threaded / off.iters), sc = seenCadencePct(on, off);
    L.push({ name: `G2 ${w} ms/iter not up > ${BAR.iterPct}% (fixed-work stand-in)`, ok: it <= BAR.iterPct, text: `${(off.threaded / off.iters).toFixed(2)} -> ${(on.threaded / on.iters).toFixed(2)} ms/iter (${it.toFixed(1)}%), stage ${(off.stage / off.n).toFixed(1)} -> ${(on.stage / on.n).toFixed(1)} ms/s` });
    L.push({ name: `V6 ${w} seen pushes per seen Sim-hour within +-${BAR.seenPct}%`, ok: pushNoData(on, off) ? null : Math.abs(sc) <= BAR.seenPct, text: pushNoData(on, off) ? 'no data (no push counters)' : `${(off.seenPush / off.seenH).toFixed(3)} -> ${(on.seenPush / on.seenH).toFixed(3)} (${sc.toFixed(0)}%)` });
    L.push({ name: `log ${w} seen searches per seen Sim-hour (reference, not a bar)`, ok: null, text: `${per(off.seen, off.seenH, 3)} -> ${per(on.seen, on.seenH, 3)} (${pct(on.seen / on.seenH, off.seen / off.seenH).toFixed(0)}%)` });
    L.push(...v6Log(w, on, off), ...spreadLog(w, runs.filter(r => r.world === w)));
    if (on.nativeSecs) L.push({ name: `A ${w} Audit=50 cost <= ${BAR.auditMs} ms/s`, ok: auditMs(on) <= BAR.auditMs, text: `${auditMs(on).toFixed(3)} ms/s on the sim thread (on arm; off arm ${auditMs(off).toFixed(3)})` });
    L.push(...routingBars(w, runs.filter(r => r.world === w)));
    L.push({ name: `log ${w} unseen cadence, GoHome runs`, ok: null, text: `${pct(on.unseen / on.unseenH, off.unseen / off.unseenH).toFixed(0)}%, ${pct(on.gohome / on.secs, off.gohome / off.secs).toFixed(0)}% (R1: not a bar)` });
  }
  for (const [w, o] of Object.entries(out)) {
    if (!o.on || !o.off || !o.on.recorded || !o.off.recorded) { L.push({ name: `O ${w} outcomes`, ok: null, text: 'no data (needs both arms with lot columns)' }); continue; }
    const row = (name: string, ok: boolean, a: number, b: number) => L.push({ name: `O ${w} ${name}`, ok, text: `${a.toFixed(1)} -> ${b.toFixed(1)}` });
    row(`stranded Sims <= off + ${BAR.strandedExtra}`, o.on.stranded <= o.off.stranded + BAR.strandedExtra, o.off.stranded, o.on.stranded);
    row(`starving Sims <= off + ${BAR.starveExtra}`, o.on.starving <= o.off.starving + BAR.starveExtra, o.off.starving, o.on.starving);
    row(`never home <= off + ${BAR.awayExtra}`, o.on.neverHome <= o.off.neverHome + BAR.awayExtra, o.off.neverHome, o.on.neverHome);
    row(`venue Sims >= ${BAR.venueShare} x off`, o.on.venueSims >= BAR.venueShare * o.off.venueSims, o.off.venueSims, o.on.venueSims);
    L.push({ name: `log ${w} at-home share`, ok: null, text: `${(100 * o.off.atHome).toFixed(0)}% -> ${(100 * o.on.atHome).toFixed(0)}%` });
  }
  return L;
}

function selfCheck() {
  const sl: Slice[] = [{ from: 0, on: true, warmup: true }, { from: 10, on: false }, { from: 20, on: true }];
  const c = (x: number) => ({ g8_simhours_x100: x * 1000, g8_unseen_simhours_x100: x * 800, g8_seen_searches: x, g8_seen_pushes: x, g8_unseen_searches: x * 4, autunseen_saved_us: x * 2000, autunseen_cost_us: x * 500, gohome_runs: x });
  const rows: Row[] = [{ i: 0, s: 0, c: c(0) }, { i: 1, s: 10, c: c(10) }, { i: 2, s: 20, c: c(20) }, { i: 3, s: 30, c: c(40) }];
  const csv = 'iters,script,taskObjects,threaded,arm\n50,10,70,500,0\n50,10,60,500,1\n0,1,1,1,1\n50,10,70,500,-1\n';
  const a = armsOf(sl, rows, csv);
  assert.equal(a.off.secs, 10); assert.equal(a.on.seen, 20); assert.equal(a.on.n, 1, 'paused and warm-up rows out');
  assert.equal(netMs(a.on), 3); assert.equal(a.off.stage, 80);
  const runs = [['bp', 'off-first'], ['bp', 'on-first'], ['ip', 'off-first'], ['ip', 'on-first']].map(([world, order]) => ({ world, order, a }));
  const ok = bars(runs, {});
  assert.equal(ok.find(b => b.name.startsWith('G1'))!.ok, true);
  assert.equal(ok.find(b => b.name.startsWith('V6 bp'))!.ok, true);
  const nat: NativeRow[] = [0, 0, 1000, 3000].map((u, i) => ({ i, s: i * 10, c: { autshadow: { cutAuditUs: u } } }));
  const na = armsOf(sl, rows, csv, nat);
  assert.equal(na.on.auditUs, 2000); assert.equal(na.on.nativeSecs, 10); assert.equal(auditMs(na.on), 0.2);
  assert.equal(na.on.seenPush, 20);
  const heavy = bars(runs.map(r => ({ ...r, a: { ...na, on: { ...na.on, auditUs: 6000 } } })), {});
  assert.equal(heavy.find(b => b.name.startsWith('A bp'))!.ok, false, 'audit over the bar fails');
  const qr: Row[] = rows.map((r, i) => ({ ...r, c: { ...r.c, g8_q_seen_n: i * 2, g8_q_seen_ticks: i * 60, g8_q_samples: i * 2, g8_q_high_sum: i * 8, g8_q_empty: 0, g8_seen_pushes: i, g8_hh_q_n: i, g8_hh_q_ticks: i * 20, g8_hh_w_n: i, g8_hh_w_ticks: i * 3 } }));
  const qa = armsOf(sl, qr, csv);
  assert.match(v6Log('bp', qa.on, qa.off).find(l => l.name.endsWith('manager on'))!.text, /wait ticks seen 30\.0 .*queue 4\.0, queues empty at 0%/);
  assert.match(v6Log('bp', qa.on, qa.off).find(l => l.name.endsWith('household'))!.text, /queue wait ticks 20\.0 -> 20\.0, worker wait ticks 3\.0 -> 3\.0/);
  assert.equal(bars(runs.slice(0, 1), {})[0].ok, null, 'one order: no verdict');
  const many = bars([...runs, ...runs, ...runs], {}), spread = many.find(b => b.name === 'log bp spread G1 net ms/s per run')!;
  assert.match(spread.text, /off-first 3\.0 \(3\.0\.\.3\.0, SD 0\.0, n=3\); on-first 3\.0 .*n=3\); all 3\.0 .*n=6\)/, 'three runs per order pool, spread shown');
  assert.equal(stat([1, 3], 1), '2.0 (1.0..3.0, SD 1.4, n=2)');
  const low = { ...a, on: { ...a.on, cost: a.on.saved } };
  assert.equal(bars(runs.map(r => ({ ...r, a: low })), {})[0].ok, false);
  const seenDown = { ...a, on: { ...a.on, seenPush: a.on.seenPush * 0.6 } };
  assert.equal(bars(runs.map(r => ({ ...r, a: seenDown })), {}).find(b => b.name.startsWith('V6 ip'))!.ok, false, 'seen pushes -40% fails');
  const searchesDown = { ...a, on: { ...a.on, seen: a.on.seen * 0.6 } };
  assert.equal(bars(runs.map(r => ({ ...r, a: searchesDown })), {}).find(b => b.name.startsWith('V6 ip'))!.ok, true, 'searches -40% with flat pushes passes');
  const noPush = { ...a, on: { ...a.on, seenPush: 0 }, off: { ...a.off, seenPush: 0 } };
  assert.equal(bars(runs.map(r => ({ ...r, a: noPush })), {}).find(b => b.name.startsWith('V6 ip'))!.ok, null, 'no push counters: no verdict');
  const s = (sims: unknown[][]) => JSON.stringify({ t: 0, sims });
  const out = outcome([s([['A', null, 'Bar', false, true, 10, false], ['B', 'Go', 'Home', true, false, -96, true]]), ...Array(8).fill(s([['A', null, 'Bar', false, true, 10, false], ['B', 'Go', 'Home', true, false, 20, true]]))].join('\n'));
  assert.deepEqual([out.neverHome, out.stranded, out.starving, out.venueSims, out.recorded], [1, 1, 1, 1, true]);
  const none = outcome(s([['A', null]]));
  assert.equal(none.recorded, false, '11-03 style rows: no lot columns');
  assert.equal(bars([], { bp: { on: none, off: none } })[1].ok, null);
  const worse = bars([], { bp: { on: { ...out, stranded: 3 }, off: out } });
  assert.equal(worse.find(b => b.name.includes('stranded'))!.ok, false);
  // R1-R6 (docs/246 §6)
  const mk = (o: Partial<Arm>): Arm => ({ ...zero(), seenH: 5, unseenH: 5, plans: 100, fails: 2, asyncUs: 1_000_000, asyncN: 100, ufLong: 0, nativeSecs: 300, rps: Array.from({ length: 600 }, (_, i) => 10 + (i % 7)), ...o });
  const pair = (order: string, on: Partial<Arm>, off: Partial<Arm> = {}) => ({ order, a: { on: mk(on), off: mk(off) } });
  const bar = (rs: ReturnType<typeof pair>[], n: string) => routingBars('bp', rs).find(b => b.name.startsWith(n))!;
  const flat = [pair('off-first', {}), pair('on-first', {})];
  assert.deepEqual(['R1', 'R2', 'R3', 'R4', 'R5', 'R6'].map(n => bar(flat, n).ok), [true, true, true, true, true, true], 'flat passes');
  assert.equal(bar([pair('off-first', { plans: 115 }), pair('on-first', { plans: 115 })], 'R1').ok, false, 'plans +15% both orders fails R1');
  assert.equal(bar([pair('off-first', { plans: 125 }), pair('on-first', { plans: 95 })], 'R1').ok, true, 'one order only: artefact, passes');
  assert.equal(bar([pair('off-first', { plans: 115 })], 'R1').ok, null, 'one order: log only');
  assert.equal(bar([pair('off-first', { asyncUs: 1_150_000 }), pair('on-first', { asyncUs: 1_150_000 })], 'R2').ok, false, 'route work +15% fails R2');
  assert.equal(bar([pair('off-first', { asyncUs: 1_200_000 }), pair('on-first', { asyncUs: 1_200_000 })], 'R3').ok, false, 'job mean +20% with R2 up fails R3');
  assert.equal(bar([pair('off-first', { asyncUs: 1_200_000, asyncN: 120 }), pair('on-first', { asyncUs: 1_200_000, asyncN: 120 })], 'R3').ok, true, 'job mean flat: R3 passes');
  assert.equal(bar([pair('off-first', { fails: 20 }), pair('on-first', { fails: 20 })], 'R4').ok, false, 'fails 2 -> 20 fails R4');
  assert.equal(bar([pair('off-first', { fails: 5 }), pair('on-first', { fails: 5 })], 'R4').ok, true, 'fails inside 1.5 x + 5 pass');
  assert.equal(bar([pair('off-first', { ufLong: 3 }), pair('on-first', {})], 'R5').ok, true, 'ufLong +3 in one pair (0 -> 3 pooled) passes R5');
  assert.equal(bar([pair('off-first', { ufLong: 25 }, { ufLong: 20 }), pair('on-first', { ufLong: 21 }, { ufLong: 22 })], 'R5').ok, true, 'IP 11-08 case: 42 -> 46 pooled, +5 in one pair, passes');
  assert.equal(bar([pair('off-first', { ufLong: 20 }, { ufLong: 5 }), pair('on-first', { ufLong: 20 }, { ufLong: 5 })], 'R5').ok, false, '10 -> 40 pooled fails R5');
  assert.equal(bar([pair('off-first', { ufLong: 6 }), pair('on-first', {})], 'R5').ok, false, '0 -> 6 pooled: CI wholly above 1, fails');
  assert.equal(bar([pair('off-first', { ufLong: 4 }), pair('on-first', {})], 'R5').ok, true, '0 -> 4 pooled: CI reaches below 1, passes');
  assert.equal(bar([pair('off-first', { nativeSecs: 0 }, { nativeSecs: 0 })], 'R5').ok, null, 'no native seconds: no data');
  assert.equal(bar(flat.map(r => ({ ...r, a: { on: { ...r.a.on, rps: r.a.on.rps.map((x, i) => i % 100 === 0 ? 40 : x) }, off: r.a.off } })), 'R6').ok, false, 'burst p99.9 40 vs 16 fails R6');
  assert.equal(bar([pair('off-first', { rps: [] })], 'R6').ok, null, 'too few seconds: no data');
  assert.equal(bar([pair('off-first', { seenH: 0, unseenH: 0 }), pair('on-first', { seenH: 0, unseenH: 0 })], 'R1').ok, null, 'no Sim-hours (no kAutLive): no data');
  console.log('gate2-report self-check PASS');
}

if (process.argv.includes('--self-check')) { selfCheck(); process.exit(0); }
const prefixes = process.argv.slice(2);
if (!prefixes.length) { console.error('usage: gate2-report.ts <tag or tag prefix> [...] | --self-check'); process.exit(2); }
const files = readdirSync(PROFILE), runs: Parameters<typeof bars>[0] = [], out: Parameters<typeof bars>[1] = {};
let routeOnly = false; // --ab unseenrm (rootmotion-retest), unseenboat (verify-shortcut-boat): only the docs/246 route bars apply
const worldOf = (t: string) => /-(bp|ip|sv)-/.exec(t)?.[1] ?? '?';
for (const f of files) {
  const m = /^(.*)-ab\.json$/.exec(f);
  // speed 2 jobs are behaviour only: read them by naming them, never pooled into the timing bars
  const solo = m && /fastspeed|speed1|seengame|credit/.exec(m[1])?.[0];
  if (m && prefixes.some(p => m[1].startsWith(p) && (!solo || p.includes(solo))) && !/-(rh|smoke)/.test(m[1])) {
    const tag = m[1], ab = JSON.parse(readFileSync(join(PROFILE, f), 'utf8')), fx = join(PROFILE, `${tag}-ab-fixes.jsonl`), sec = files.find(x => x.startsWith(`${tag}-`) && /^-\d+-seconds\.csv$/.test(x.slice(tag.length)));
    if (!existsSync(fx) || !sec || !['autunseen', 'autseengame', 'unseenrm', 'unseenboat'].includes(ab.names?.[0])) continue;
    if (ab.names[0] === 'unseenrm' || ab.names[0] === 'unseenboat') routeOnly = true;
    const nx = join(PROFILE, `${tag}-ab-native.jsonl`), native = existsSync(nx) ? readFileSync(nx, 'utf8').trim().split('\n').map(l => JSON.parse(l)) as NativeRow[] : [];
    const rows = readFileSync(fx, 'utf8').trim().split('\n').map(l => JSON.parse(l)) as Row[], a = armsOf(ab.slices, rows, readFileSync(join(PROFILE, sec), 'utf8'), native);
    runs.push({ world: worldOf(tag), order: ab.slices.find((s: Slice) => !s.warmup)?.on ? 'on-first' : 'off-first', a });
  }
  const o = /^(.*-out-(on|off))-activity\.jsonl$/.exec(f);
  if (o && prefixes.some(p => o[1].startsWith(p))) ((out[worldOf(o[1])] ??= {})[o[2] as 'on' | 'off'] = outcome(readFileSync(join(PROFILE, f), 'utf8')));
}
const res = bars(runs, out).filter(b => !routeOnly || /^R[1-6] /.test(b.name));
for (const b of res) console.log(`${b.ok === null ? (b.name.startsWith('log') ? 'log ' : 'NONE') : b.ok ? 'PASS' : 'FAIL'}  ${b.name}: ${b.text || 'no data'}`);
process.exit(res.some(b => b.ok === false) ? 1 : 0);
