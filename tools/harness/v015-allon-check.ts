// v0.1.5 all-on verify checker (queues/v015-allon-verify.json): PASS/FAIL per rule from counters and logs only (noise tolerant, no timing).
//   node tools/harness/v015-allon-check.ts --native <job>-native.json --log <job>-ts3native.log --stuck <job>-stuck.json
//        [--fixes <fixes counters json: agent 'patches' answer>] [--ref-stuck <release-defaults stuck.json>] [--ref-log <release-defaults ts3native.log>]
//   node tools/harness/v015-allon-check.ts --self-check
// Native dump = {before, after}; counters are after - before. Exit 1 on any FAIL.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { parseArgs } from 'node:util';

type C = Record<string, number>;
export type Row = { rule: string; ok: boolean | null; why: string };

// feature -> native counters that must move (> 0)
export const MUST_MOVE: Record<string, string[]> = {
  SlotEnabledRO: ['slTaken'], UnseenThrottle: ['utSkipped'], 'RouteAsync.FallbackAsync': ['fbForeignStarted'],
  'RouteEarlyOut Act': ['eoActed'], TailUnlock: ['tuWins'], ReplanDedupe: ['rdServed'], H2: ['h2Released'],
};
// ts3fixes counters (skipped when --fixes absent); bouncerskip_skipped may stay 0 (needs a velvet-rope venue on the route)
export const MUST_MOVE_FIXES = ['legreplanskip_skipped', 'idlememo_skipped', 'stuckcheckasync_run', 'etspread_passes'];
const ZERO = ['tuTrip_0', 'tuTrip_1', 'tuTrip_2', 'eoWrong', 'eoActDiff', 'eoActOff', 'fbActiveTrip', 'p5bWatchdog'];

export function check(d: { before: C; after: C }, log: string, stuck: number, fixes?: C, ref?: { stuck?: number; log?: string }): Row[] {
  const dl = (k: string) => (d.after[k] ?? 0) - (d.before[k] ?? 0);
  const rows: Row[] = [];
  const bad = log.split('\n').filter(l => /unhandled exception|access violation|crash|tripwire|FAIL:/i.test(l));
  rows.push({ rule: 'no crash/freeze/tripwire line', ok: !bad.length, why: bad.length ? bad.slice(0, 3).join(' | ') : 'none' });
  const z = ZERO.filter(k => !(k in d.after) || dl(k) !== 0); // docs/236: an absent tripwire counter is not 0
  rows.push({ rule: 'tripwire counters zero (tuTrip_0-2, eoWrong, eoActDiff, eoActOff, fbActiveTrip, p5bWatchdog)', ok: !z.length, why: z.length ? z.map(k => k in d.after ? `${k}=${dl(k)}` : `${k} missing`).join(', ') : 'all 0' });
  for (const [f, ks] of Object.entries(MUST_MOVE)) rows.push({ rule: `${f}: ${ks.join(',')} > 0`, ok: ks.every(k => dl(k) > 0), why: ks.map(k => `${k}=${dl(k)}`).join(', ') });
  if (fixes) { const lo = MUST_MOVE_FIXES.filter(k => !((fixes[k] ?? 0) > 0)); rows.push({ rule: `ts3fixes ${MUST_MOVE_FIXES.join(',')} > 0`, ok: !lo.length, why: lo.length ? `not moved: ${lo.join(', ')}` : 'all moved' }); }
  else rows.push({ rule: 'ts3fixes counters', ok: null, why: 'SKIP (no --fixes)' });
  rows.push(ref?.stuck === undefined ? { rule: 'stuck Sims <= reference', ok: null, why: `SKIP (no --ref-stuck), ${stuck} stuck` }
    : { rule: 'stuck Sims <= reference', ok: stuck <= ref.stuck, why: `${stuck} vs ${ref.stuck}` });
  const se = (s: string) => new Set(s.split('\n').filter(l => /ScriptError/.test(l)).map(l => l.replace(/^\S+\s+/, '')));
  const mine = se(log);
  if (ref?.log === undefined) rows.push({ rule: 'no new ScriptError', ok: null, why: `SKIP (no --ref-log), ${mine.size} distinct` });
  else { const refS = se(ref.log); const nw = [...mine].filter(l => !refS.has(l)); rows.push({ rule: 'no new ScriptError', ok: !nw.length, why: nw.length ? nw.slice(0, 2).join(' | ') : 'none new' }); }
  return rows;
}

if (process.argv[1] && import.meta.filename === process.argv[1]) {
  const { values: v } = parseArgs({ options: { 'self-check': { type: 'boolean' }, native: { type: 'string' }, log: { type: 'string' }, stuck: { type: 'string' }, fixes: { type: 'string' }, 'ref-stuck': { type: 'string' }, 'ref-log': { type: 'string' } } });
  const rd = (p: string) => readFileSync(p, 'utf8');
  const cnt = (p?: string) => (p ? JSON.parse(rd(p)).length : undefined);
  if (v['self-check']) {
    const after: C = { ...Object.fromEntries(ZERO.map(k => [k, 0])), slTaken: 5, utSkipped: 9, fbForeignStarted: 2, eoActed: 3, tuWins: 4, rdServed: 6, h2Released: 7 };
    const fx: C = { legreplanskip_skipped: 1, idlememo_skipped: 1, stuckcheckasync_run: 1, etspread_passes: 1 };
    const good = check({ before: {}, after }, 'ok\n', 2, fx, { stuck: 2, log: '' });
    assert.ok(good.every(r => r.ok === true), JSON.stringify(good));
    const bad = check({ before: {}, after: { ...after, eoWrong: 1, tuWins: 0 } }, 'x ScriptError boom\nTailUnlock: tripwire\n', 3, { ...fx, idlememo_skipped: 0 }, { stuck: 2, log: '' });
    assert.equal(bad.filter(r => r.ok === false).length, 6, JSON.stringify(bad));
    const noTrip = check({ before: {}, after: { slTaken: 5 } }, 'ok', 0)[1]; // docs/236: absent tripwire counters fail, not read as 0
    assert.ok(noTrip.ok === false && /tuTrip_0 missing/.test(noTrip.why), JSON.stringify(noTrip));
    console.log('v015-allon-check self-check ok');
    process.exit(0);
  }
  if (!v.native || !v.log) { console.error('usage: --native <native.json> --log <ts3native.log> [--stuck f] [--fixes f] [--ref-stuck f] [--ref-log f] | --self-check'); process.exit(2); }
  const rows = check(JSON.parse(rd(v.native)), rd(v.log), cnt(v.stuck) ?? 0, v.fixes ? JSON.parse(rd(v.fixes)) : undefined, { stuck: cnt(v['ref-stuck']), log: v['ref-log'] ? rd(v['ref-log']) : undefined });
  for (const r of rows) console.log(`${r.ok === null ? 'SKIP' : r.ok ? 'PASS' : 'FAIL'}  ${r.rule}: ${r.why}`);
  process.exit(rows.some(r => r.ok === false) ? 1 : 0);
}
