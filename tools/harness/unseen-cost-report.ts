// Unseen-check cost report (docs/227, ts3fixes unseencost counters 690-743 + ts3native [UnseenCost] v148 uc*): per site calls/s,
// mean us and ms/s (QPC / StopWatch floor subtracted from each 1-in-16 sample), the interop calls the script checks make,
// the repeat ratio (asks / distinct Sims or objects per tick = what one per-tick table would save), the probe's own cost,
// and the docs/227 decision (build the central table if total > 2 ms/s or repeat ratio >= 3).
//   node tools/harness/unseen-cost-report.ts <queue>-<date>[-<job>]   (rows of probe-out/queue/<queue>-<date>.jsonl whose job matches)
//   node tools/harness/unseen-cost-report.ts --self-check
// Sites nest (SimUnseen -> FarFromViewers -> RefreshViewers): per-site ms/s is inclusive; the total sums top-level calls only,
// minus the probe overhead of the nested calls inside them.
import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';

type Pair = { before?: number | null; after?: number };
export const SCRIPT = ['far', 'refresh', 'simunseen', 'townie', 'gohome', 'pos', 'lot', 'goal', 'dispose', 'gate', 'ufwhy', 'utfetch', 'utlookup'];  // utfetch, utlookup: counters 750-757 (docs/227 §9), nested in the Sim checks
export const NATIVE = ['UnObjClass', 'UtWhy(list)', 'UtViewerWhy', 'UfcAgentUnseen', 'UfcTaskSimUnseen', 'UfcListed', 'AaNearVisible', 'UnRefreshCamera'];
export const TOTAL_MS = 2, REPEAT_HIGH = 3;

type Site = { name: string; calls: number; top: number; timed: number; meanUs: number };
function part(sites: Site[], perCallUs: number, seconds: number, lines: string[], label: string) {
  const ms = (us: number) => us / 1000 / seconds;
  let calls = 0, top = 0, topMs = 0;
  for (const s of sites) {
    calls += s.calls, top += s.top, topMs += ms(s.meanUs * s.top);
    if (s.calls) lines.push(`  ${s.name.padEnd(17)} ${(s.calls / seconds).toFixed(1).padStart(8)}/s (top ${(s.top / seconds).toFixed(1)}/s), ` +
      `mean ${s.meanUs.toFixed(2)} us (${s.timed} timed), ${ms(s.meanUs * s.calls).toFixed(3)} ms/s incl. nested`);
  }
  const probeMs = ms(perCallUs * calls), nestedProbe = ms(perCallUs * (calls - top)), net = Math.max(0, topMs - nestedProbe);
  lines.push(`  ${label} total (top level, nested probe overhead ${nestedProbe.toFixed(3)} ms/s subtracted): ${net.toFixed(3)} ms/s; ` +
    `probe's own cost ${perCallUs.toFixed(3)} us/call = ${probeMs.toFixed(3)} ms/s`);
  return { net, probeMs, calls };
}

export function report(fx: Record<string, Pair>, nat: { before?: any; after?: any } | null, seconds: number) {
  const d = (k: string) => (fx[k]?.after ?? 0) - (fx[k]?.before ?? 0);
  const nd = (k: string) => Number(nat?.after?.[k] ?? 0) - Number(nat?.before?.[k] ?? 0);
  const lines: string[] = [];
  const floorUs = d('unseencost_floor_n') ? d('unseencost_floor_us') / d('unseencost_floor_n') : 0;
  const selfUs = d('unseencost_self_n') ? d('unseencost_self_us') / d('unseencost_self_n') : 0;
  const ticks = d('unseencost_ticks');
  const sOk = ticks > 0 && SCRIPT.some(s => d(`unseencost_${s}_calls`) > 0);
  lines.push(`script (unseencost): ${sOk ? `on ${ticks} ticks (${(ticks / seconds).toFixed(1)}/s)` : 'NO DATA (switch off or old package)'}; StopWatch floor ${floorUs.toFixed(2)} us/sample`);
  const ss = part(SCRIPT.map(n => {
    const t = d(`unseencost_${n}_timed`);
    return { name: n, calls: d(`unseencost_${n}_calls`), top: d(`unseencost_${n}_top`), timed: t, meanUs: t ? Math.max(0, d(`unseencost_${n}_us`) / t - floorUs) : 0 };
  }), selfUs, seconds, lines, 'script');
  const asks = d('unseencost_sim_asks'), dist = d('unseencost_sim_distinct');
  const sRep = dist ? asks / dist : 0;
  lines.push(`  interop/s: IsObjectOnScreen ${(d('unseencost_onscreen') / seconds).toFixed(1)}, UfCamAsk ${(d('unseencost_camask') / seconds).toFixed(1)}, ` +
    `RefreshViewers reads ${(d('unseencost_viewread') / seconds).toFixed(1)}; Sim asks ${(asks / seconds).toFixed(1)}/s, distinct per tick ${ticks ? (dist / ticks).toFixed(2) : '0'}, repeat ratio ${sRep.toFixed(2)}`);
  const nFloorUs = nd('ucFloorN') ? nd('ucFloorNs') / nd('ucFloorN') / 1000 : 0;
  const nSelfUs = nd('ucSelfN') ? nd('ucSelfNs') / nd('ucSelfN') / 1000 : 0;
  const nOk = Number(nat?.after?.ucReady) === 1 && NATIVE.some((_, i) => nd(`ucN_${i}`) > 0);
  lines.push(`native ([UnseenCost]): ${nOk ? `ready, ${(nd('ucPasses') / seconds).toFixed(1)} passes/s with asks` : 'NO DATA (Enabled=0 or counters < v148)'}; QPC floor ${(nFloorUs * 1000).toFixed(0)} ns/sample`);
  const ns = part(NATIVE.map((n, i) => {
    const t = nd(`ucSampled_${i}`);
    return { name: n, calls: nd(`ucN_${i}`), top: nd(`ucTop_${i}`), timed: t, meanUs: t ? Math.max(0, nd(`ucUs_${i}`) / t - nFloorUs) : 0 };
  }), nSelfUs, seconds, lines, 'native');
  const nRep = nd('ucDistinct') ? nd('ucAsks') / nd('ucDistinct') : 0;
  lines.push(`  UnObjClass asks ${(nd('ucAsks') / seconds).toFixed(1)}/s, distinct per pass ${nd('ucPasses') ? (nd('ucDistinct') / nd('ucPasses')).toFixed(2) : '0'}, repeat ratio ${nRep.toFixed(2)}`);
  const total = ss.net + ns.net, rep = Math.max(sRep, nRep);
  const build = total > TOTAL_MS || rep >= REPEAT_HIGH;
  lines.push(`decision (docs/227): total ${total.toFixed(3)} ms/s (rule > ${TOTAL_MS}), repeat ratio ${rep.toFixed(2)} (rule >= ${REPEAT_HIGH}): ` +
    `${build ? 'BUILD the central per-tick unseen table' : 'do not build (checks are cheap and rarely repeated)'}; probe cost ${(ss.probeMs + ns.probeMs).toFixed(3)} ms/s`);
  return { ok: sOk && nOk, build, total, repeat: rep, lines };
}

if (process.argv[1] && import.meta.filename === process.argv[1]) {
  const args = process.argv.slice(2);
  if (args[0] === '--self-check') {
    const fx: Record<string, Pair> = {
      unseencost_ticks: { before: 0, after: 18000 }, unseencost_floor_n: { after: 100 }, unseencost_floor_us: { after: 100 },
      unseencost_self_n: { after: 1600 }, unseencost_self_us: { after: 800 },
      unseencost_simunseen_calls: { after: 60000 }, unseencost_simunseen_top: { after: 60000 }, unseencost_simunseen_timed: { after: 3750 }, unseencost_simunseen_us: { after: 15000 },
      unseencost_far_calls: { after: 90000 }, unseencost_far_top: { after: 30000 }, unseencost_far_timed: { after: 5625 }, unseencost_far_us: { after: 16875 },
      unseencost_onscreen: { after: 60000 }, unseencost_sim_asks: { after: 60000 }, unseencost_sim_distinct: { after: 12000 },
    };
    const nat = { before: { ucN_0: 0 }, after: { ucReady: 1, ucN_0: 600000, ucTop_0: 600000, ucSampled_0: 37500, ucUs_0: 75000 + 37500 * 0.05,
      ucAsks: 600000, ucDistinct: 300000, ucPasses: 18000, ucSelfN: 64000, ucSelfNs: 64000 * 60, ucFloorN: 1000, ucFloorNs: 50000 } };
    const r = report(fx, nat, 600);
    const text = r.lines.join('\n');
    // script: simunseen mean 4-1 = 3 us x 100/s = 0.3 ms/s; far mean 3-1 = 2 us x 50/s top = 0.1; nested probe (60000 far) x 0.5 us = 0.05
    const want = ['100.0/s (top 100.0/s), mean 3.00 us (3750 timed)', 'script total (top level, nested probe overhead 0.050 ms/s subtracted): 0.350 ms/s',
      "probe's own cost 0.500 us/call = 0.125 ms/s", 'distinct per tick 0.67, repeat ratio 5.00', '1000.0/s (top 1000.0/s), mean 2.00 us',
      'native total (top level, nested probe overhead 0.000 ms/s subtracted): 2.000 ms/s', 'repeat ratio 2.00', 'BUILD the central per-tick unseen table'];
    const miss = want.filter(w => !text.includes(w));
    if (miss.length || !r.ok || !r.build) throw new Error(`unseen-cost-report self-check failed (${miss.join('; ')})\n${text}`);
    if (report({}, null, 600).ok || report({}, null, 600).build) throw new Error('unseen-cost-report self-check: no data must fail and not build');
    console.log('unseen-cost-report self-check ok');
  } else {
    const dir = 'probe-out/queue';
    let ok = true, found = 0;
    for (const tag of args) {
      const files = existsSync(tag) ? [tag] : readdirSync(dir).filter(f => f.endsWith('.jsonl') && tag.startsWith(f.slice(0, -6))).map(f => join(dir, f));
      for (const f of files)
        for (const l of readFileSync(f, 'utf8').split('\n')) {
          let row: any;
          try { row = JSON.parse(l); } catch { continue; }
          if (!row?.fixActivity?.counters || (!existsSync(tag) && !`${f.slice(dir.length + 1, -6)}-${row.job}`.startsWith(tag))) continue;
          const seconds = (Number(row.run?.profile?.minutes) || 10) * 60;
          const r = report(row.fixActivity.counters, row.counters ?? null, seconds);
          console.log(`== ${row.job} (${seconds} s)\n${r.lines.join('\n')}`);
          ok &&= r.ok; found++;
        }
    }
    if (!found) { console.error(`no queue rows with fix counters for ${args.join(' ') || '(no args)'}`); ok = false; }
    process.exitCode = ok ? 0 : 1;
  }
}
