// Mono cost report (PLAN.md "Mono goal 2026-10-29"; queue tools/harness/queues/mono-cost.json): per run, what the Mono script
// engine costs the sim thread, and with [MonoThread] / [AutShadow] on, what the worker and the shadow scorer add. Reads only.
//   node tools/harness/mono-cost-report.ts [probe-out/queue/<queue>-<date>.jsonl | <tag> ...] [--tag <family>] [--minutes 10]
//     default: every probe-out/queue/mono-cost*-<date>.jsonl family (mono-cost, mono-cost2, ... mono-cost5, later ones), the
//     newest row per job id (a rerun counts for its job); --tag mono-cost5 keeps one family. A bare tag reads
//     probe-out/queue/<tag>-native.json (no ts3fixes counters) and assumes --minutes for the window
//   node tools/harness/mono-cost-report.ts --self-check
// Sources: sim-budget-report.ts analyze() on the profile files (Mono bucket, managed-sample owners, GC); the queue's /native
// dump {before, after}: ts3native_mtprobe (v4) and ts3native_autshadow (v5) deltas over the play window; ts3fixes autshadow_a0..a5
// (Mode R classes, docs/122 §5) from the queue row's fixActivity. Counter rates use the profile length as the window [inferred
// window]: /native has no clock. Set TS3_REPO=<main checkout> when running from a git worktree.
import assert from 'node:assert/strict';
import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { analyze, load, type Run } from './sim-budget-report.ts';

const ROOT = process.env.TS3_REPO ?? join(import.meta.dirname, '..', '..');
const QUEUE = join(ROOT, 'probe-out', 'queue');
export const STOP_MS = 5; // PLAN.md stop rule: the shadow worker alone adds >= ~5 ms/s of sim cost -> no apply step
// [measured, selftest] docs/30 §15: uncontended P3 lock entry, gate on minus gate off (runs: 68.4, 54.9, 69.8, 68.3 ns);
// per entry in game [inferred]: the same order, not measured
export const LOCK_ENTRY_NS = 68;
// [measured, selftest] TestAutShadow: sim-thread IC call through a guard thunk minus the direct call (+17.1 ns); memo ICs
// (AsCallEntry) cost more and are counted the same [inferred lower bound]
export const GUARD_NS = 17;
// docs/206 §7 Satb barrier on the sim thread: per store through the stub, extra per first-written word, per load through a
// load stub (thread test, worker path not taken). [measured, monohost AS satb cost line, best of 20: 4.9-6.0 ns per store
// at 18% first writes, 9.1 ns per load after its own stores; split 4 + 6 per word: docs/206 §7.6]
export const SATB_STORE_NS = 4, SATB_WORD_NS = 6, SATB_LOAD_NS = 9;
// docs/206 §11 (0.36.136): per-opcode load thunks (direct jumps, own cache lines); monohost 3.30 ns a load incl. the score's own stores [measured, upper bound]
export const SATB_LOAD_THUNK_NS = 3.3;
// docs/124 §15: Test share of meta pair scoring (docs/75 meta samples: base path + Test frames vs score overrides), TopN, per-search fixed ms
export const TAU = 0.7, TOPN = 10, CONSUME_FIXED_MS = 0.15;

type Budget = ReturnType<typeof analyze>;
export type Input = { tag: string; minutes: number; budget?: Budget; budgetWhy?: string; native?: { before?: any; after?: any };
  fix?: Record<string, { before?: number; after?: number }> };

const NA = 'n/a';
const f1 = (x: number) => x.toFixed(1);
// delta of one counter in a /native block; undefined when the block or the field is absent
function d(nat: Input['native'], block: string, k: string): number | undefined {
  const a = nat?.after?.[block]?.[k], b = nat?.before?.[block]?.[k];
  return typeof a === 'number' ? a - (typeof b === 'number' ? b : 0) : undefined;
}
// delta of an array counter of the autshadow block (pairT, pairN); undefined when absent
function d1(nat: Input['native'], k: string): number[] | undefined {
  const a = nat?.after?.autshadow?.[k], b = nat?.before?.autshadow?.[k];
  return Array.isArray(a) ? a.map((x: number, j: number) => x - (Array.isArray(b) ? b[j] ?? 0 : 0)) : undefined;
}
const fx = (i: Input, k: string) => { const c = i.fix?.[k]; return typeof c?.after === 'number' ? c.after - (c.before ?? 0) : undefined; };

export function costLines(i: Input): string[] {
  const secs = i.minutes * 60, perS = (us: number | undefined) => us === undefined ? NA : `${f1(us / 1000 / secs)} ms/s`;
  const L: string[] = [`### ${i.tag}`, ''];
  // 1. Mono interpreter and GC on the sim thread (sim-budget)
  const b = i.budget?.secs ? i.budget : undefined;
  if (b) {
    L.push(`- Mono interpreter (sim thread): ${f1(b.ms['Mono script'])} ms/s [inferred split of measured stage timers, ${b.samples.monoN} managed samples]; top owners: ${b.contrib['Mono script']}`,
      `- ICs under script: ${f1(b.ms['ICs (native under script)'])} ms/s [inferred split]; top: ${b.contrib['ICs (native under script)']}`,
      `- GC (mark + finalizers): ${f1(b.ms['GC + finalizers'])} ms/s [measured]; ${b.contrib['GC + finalizers']}`,
      `- run: ${b.secs} s kept, ${f1(b.its)} it/s, ${b.hitches} frames >= 300 ms`);
  } else L.push(`- Mono interpreter: ${NA}; GC: ${NA} (${i.budgetWhy ?? (i.budget ? 'no second rows kept: profile without stage columns or under 61 s' : 'no profile files')})`);
  const N = i.native;
  // 2. ledger probe (autledger.cpp): ticks and meta-scope wall
  const ticks = d(N, 'autshadow', 'lgTicks') ?? (N?.after?.autshadow?.ledger ? N.after.autshadow.ledger.ticks - (N.before?.autshadow?.ledger?.ticks ?? 0) : undefined);
  const scopeUs = N?.after?.autshadow?.ledger ? N.after.autshadow.ledger.scopeUs - (N.before?.autshadow?.ledger?.scopeUs ?? 0) : undefined;
  if (N?.after?.autshadow?.ledger?.on & 1) L.push(`- ledger: ${ticks ?? NA} Simulate ticks [measured]${ticks === 0 ? ' (0 = loop-back hook did not fire: FAIL)' : ''}, meta scopes wall ${perS(scopeUs)} [measured, includes yielded B13 sleep]`);
  // 3. [MonoThread] worker + GC lock
  const mt = N?.after?.mtprobe;
  const starts = d(N, 'mtprobe', 'workerStarts'), runUs = d(N, 'mtprobe', 'workerRunUs'), waitUs = d(N, 'mtprobe', 'lockWaitUs');
  const calls = d(N, 'mtprobe', 'lockedCalls'), callsW = d(N, 'mtprobe', 'lockedWorker');
  const worker = !!mt && (mt.workerStarts > 0 || mt.workerState > 0);
  if (!mt) L.push(`- [MonoThread]: ${NA} (no mtprobe block)`);
  else {
    L.push(`- [MonoThread] p3Sites ${mt.p3Sites}, p4Ready ${mt.p4Ready}, locking ${mt.locking}, workerState ${mt.workerState}; tripwires lockOverflow ${mt.lockOverflow}, workerCtxSet ${mt.workerCtxSet}, n2mOverflow ${mt.n2mOverflow}, lockStale ${mt.lockStale ?? NA}`);
    L.push(`- worker busy: ${worker ? perS(runUs) : NA} [measured], ${d(N, 'mtprobe', 'workerJobs') ?? NA} jobs, ${d(N, 'mtprobe', 'workerExceptions') ?? NA} exceptions`);
    L.push(`- GC lock (covers the hooked GC/alloc entries): waits ${perS(waitUs)} [measured, every thread], ${d(N, 'mtprobe', 'lockWaits') ?? NA} waits, max ${mt.lockMaxWaitUs} us; entries ${calls ?? NA} (worker ${callsW ?? NA})` +
      // v6 (docs/30 §16): the sim's own waits, and gate-offs at the first sim entry after a job (LazyEarly)
      (mt.version >= 6 ? `; sim waits ${perS(d(N, 'mtprobe', 'simLockWaitUs'))}, ${d(N, 'mtprobe', 'simLockWaits') ?? NA} waits, max ${mt.simLockMaxWaitUs} us; early gate-offs ${d(N, 'mtprobe', 'lazyEarlyOff') ?? NA}` : ''));
    if (calls !== undefined) L.push(`- GC lock uncontended entries: ${f1(calls / secs / 1000)}k/s x ${LOCK_ENTRY_NS} ns = ${perS(calls * LOCK_ENTRY_NS / 1000)} [inferred: measured entries x selftest ns, docs/30 §15]${mt.version >= 5 ? `; lazy gate ${mt.lazyGate ? 'on' : 'off'}, on ${d(N, 'mtprobe', 'lazyOn') ?? NA}, off ${d(N, 'mtprobe', 'lazyOff') ?? NA}` : ''}`);
  }
  // 4. [AutShadow]
  const as = N?.after?.autshadow;
  const req = d(N, 'autshadow', 'req'), cutUs = d(N, 'autshadow', 'cutUs'), cutReq = d(N, 'autshadow', 'cutReq');
  const guard = d(N, 'autshadow', 'guardSim');
  if (!as || !(as.mode >= 0 && (req ?? 0) + (d(N, 'autshadow', 'cuts') ?? 0) > 0)) L.push(`- [AutShadow]: ${NA} (${!as ? 'no autshadow block' : as.mode < 0 ? 'mode -1: no Simulate slot stub' : 'no requests or cuts'})`);
  else {
    const runs = d(N, 'autshadow', 'workRuns');
    L.push(`- [AutShadow] mode ${as.mode}, cut site ${as.cutSite}: cut + snapshot ${perS(cutUs)} [measured], ${cutReq ?? NA} request cuts, avg ${cutReq ? f1((cutUs ?? 0) / cutReq) : NA} us, max ${as.cutMaxUs} us, last snapshot ${as.snapBytes} B`,
      `- reference re-score on the sim (Mode R test cost, not paid by an apply step): ${perS(d(N, 'autshadow', 'refUs'))} [measured]; worker scoring ${perS(d(N, 'autshadow', 'workUs'))} [measured]${as.version >= 6 ? `; window waits ${d(N, 'autshadow', 'windowWaits') ?? NA}, ${perS(d(N, 'autshadow', 'windowWaitUs'))} (WindowWaitUs test cost) [measured]` : ''}`,
      `- guard thunks on the sim: ${guard === undefined ? NA : `${f1(guard / secs / 1000)}k calls/s x ${GUARD_NS} ns = ${perS(guard * GUARD_NS / 1000)}`} [inferred: measured calls x selftest ns]`);
    const a = [0, 1, 2, 3, 4, 5].map(k => fx(i, `autshadow_a${k}`));
    const cmp = a.slice(0, 4).every(x => x !== undefined) ? a.slice(0, 4).reduce((s, x) => s! + x!, 0)! : undefined;
    const fell = a[4] !== undefined && a[5] !== undefined ? a[4]! + a[5]! : undefined;
    L.push(`- jobs: posted ${req ?? NA}, Begin busy ${d(N, 'autshadow', 'busy') ?? NA}, worker runs ${runs ?? NA}, compared ${cmp ?? NA}, fell back ${fell ?? NA} (aborted/skipped ${a[4] ?? NA}, overrun/epoch ${a[5] ?? NA}; native abortTotal ${d(N, 'autshadow', 'abortTotal') ?? NA}, overrun ${d(N, 'autshadow', 'overrun') ?? NA}, noWorker ${d(N, 'autshadow', 'noWorker') ?? NA}) [measured]`);
    L.push(`- Mode R match rate: ${as.mode === 1 && cmp ? `${(a[0]! * 100 / cmp).toFixed(1)}% bit-equal (A0 ${a[0]} of ${cmp}; A1 ${a[1]}, A2 ${a[2]}, A3 ${a[3]}; A2/A3 must be 0)` : NA} [measured]`);
    // worker aborts by reason (abort[] + abort2 + abortRoute, read by agent.js into one map) and the answer table (v7, docs/122 §14.14)
    const ab0 = N?.before?.autshadow?.abort ?? {}, ab = Object.entries(as.abort ?? {}).map(([r, n]) => [r, (n as number) - (ab0[r] ?? 0)] as [string, number])
      .filter(([, n]) => n > 0).sort((x, y) => y[1] - x[1]);
    const abSum = ab.reduce((t, [, n]) => t + n, 0);
    L.push(`- worker aborts by reason: ${ab.length ? ab.slice(0, 5).map(([r, n]) => `${r} ${n} (${(n * 100 / abSum).toFixed(1)}%)`).join(', ') : NA} [measured]`);
    // docs/122 §5.1: A4 target < 20% of allow-listed pairs (ts3fixes from docs/122 §14.15 leaves off-allow-list skips out of A0-A5)
    const all = a.every(x => x !== undefined) ? a.reduce((s, x) => s! + x!, 0)! : undefined, skips = fx(i, 'autshadow_skips');
    L.push(`- A4 share: ${all ? `${(a[4]! * 100 / all).toFixed(1)}% of ${all} classified pairs (target < 20%)` : NA}; skipped pairs ${skips ?? NA} [measured]`);
    L.push(`- answer table: ${as.entries ?? NA} rows after the last cut, learned ${d(N, 'autshadow', 'learned') ?? NA}, learnDropped ${d(N, 'autshadow', 'learnDropped') ?? NA}, inserts refused ${d(N, 'autshadow', 'full') ?? NA}` +
      (as.version >= 7 ? `; max ${as.entriesMax} rows, memo ${as.memoRows}, evicted ${d(N, 'autshadow', 'evicted') ?? NA} (memo ${d(N, 'autshadow', 'evictedMemo') ?? NA}), ring dropped ${d(N, 'autshadow', 'ringDropped') ?? NA}, most new keys at one cut ${as.learnWantMax}, most body calls at one cut ${as.refreshMax}` : '') + ' [measured]');
    if (as.version >= 8) {  // docs/122 §14.15: where the cut time goes
      const cuts = d(N, 'autshadow', 'cuts'), per = (k: string) => { const x = d(N, 'autshadow', k); return x === undefined || !cuts ? NA : f1(x / cuts); };
      L.push(`- cut per cut (us): learn ${per('cutLearnUs')}, prep ${per('cutPrepUs')}, refresh ${per('cutRefreshUs')}, age ${per('cutAgeUs')}, audit ${per('cutAuditUs')}, publish ${per('cutPublishUs')}; ` +
        `body calls per cut (us per cut): new ${per('bodyNew')} (${per('bodyNewUs')}), object ${per('bodyObj')} (${per('bodyObjUs')}), list ${per('bodyList')} (${per('bodyListUs')}), per-cut ${per('bodyCut')} (${per('bodyCutUs')}); ` +
        `Sim parent objects learned ${d(N, 'autshadow', 'parentRows') ?? NA}; cold: ${d(N, 'autshadow', 'coldSkips') ?? NA} rows skipped, ${d(N, 'autshadow', 'coldReads') ?? NA} reads, ${as.coldIcs} ICs; x87 sim 0x${(as.simX87 >>> 0).toString(16)} worker 0x${(as.workX87 >>> 0).toString(16)}, MXCSR sim 0x${(as.simCsr >>> 0).toString(16)} worker 0x${(as.workCsr >>> 0).toString(16)} [measured]`);
      if (as.version >= 9)  // docs/122 §14.16: change-only lists (CutLean) and Sim tiers (TierMs)
        L.push(`- lists (CutLean) per cut: kept by print ${per('listPrintSkips')}, prints ${per('listLots')} (${per('listPrintUs')} us), refreshed same ${per('listSame')}; ` +
          `Sim tiers (TierMs ${as.tierMs}) per cut: tier 1 ${per('tier1Sims')} Sims, body ${per('tier1Body')} (${per('tier1BodyUs')} us); tier 2 ${per('tier2Sims')} Sims, ` +
          `${per('tier2Stale')} waiting (stale), body ${per('tier2Body')} (${per('tier2BodyUs')} us); classify ${per('tierClassUs')} us; aging runs ${d(N, 'autshadow', 'ageRuns') ?? NA} [measured]`);
      if (as.version >= 10)  // docs/122 §14.17: dirty lots (CutLean=2), ListAudit net, tier cache, list tiers
        L.push(`- dirty lots (CutLean=2) per cut: rows kept ${per('dirtyKept')}, refreshed ${per('dirtyRows')}, lots marked ${per('dirtyLots')}, objects placed ${per('objResolved')} (${per('objResolveUs')} us); ` +
          `resync cuts ${d(N, 'autshadow', 'dirtyUnknown') ?? NA}; list audit ${per('listAuditN')} rows (${per('listAuditUs')} us), misses ${d(N, 'autshadow', 'listMissed') ?? NA}, tripped ${as.dirtyTripped ?? NA}; ` +
          `tier cache: classified ${per('tierClassN')} Sims, camera moves ${d(N, 'autshadow', 'tierCamInval') ?? NA}, holds ${d(N, 'autshadow', 'tierHold') ?? NA}; ` +
          `list tiers: ${per('listT2Wait')} rows waiting, ${per('listT2Ref')} lot refreshes, tier-1 lots ${as.t1Lots ?? NA} [measured]`);
      if (as.version >= 11)  // docs/122 §14.18: list kinds, lot-vector prints, tier triggers, shared cuts
        L.push(`- list kinds per cut: Sim-free rows kept ${per('simFreeKept')}, Sim marks ${per('simMarks')} (types ${as.listTypesSimFree ?? NA} Sim-free of ${as.listTypes ?? NA}); ` +
          `unplaced touches ${per('unplaced')}, lot-vector prints ${per('liteLots')} (${per('liteUs')} us), their marks ${per('liteMarks')}; ` +
          `resyncs ${d(N, 'autshadow', 'resyncTouch') ?? NA} touch list / ${d(N, 'autshadow', 'resyncMap') ?? NA} map full, map rows ${as.olRows ?? NA}, compactions ${d(N, 'autshadow', 'olCompact') ?? NA}; ` +
          `tiers kept ${per('tierKept')} Sims; shared cuts ${d(N, 'autshadow', 'batchCuts') ?? NA}, ${per('batchReqs')} requests per cut (max ${as.batchMax ?? NA}) [measured]`);
      if (as.version >= 12) {  // docs/122 §14.19: what dirties lots, seeding, pointer lot-vector prints, RowHot
        const mc = ['First', 'New', 'Lot', 'Room', 'Gone', 'Lost', 'Lite', 'Kid'], ev = ['4', '5', '6', '0B', '0C', '0E', '12', '18', '21'];
        L.push(`- dirtying per cut, lots marked / list rows refreshed by cause: ${mc.map(n => `${n.toLowerCase()} ${per('mark' + n)}/${per('row' + n)}`).join(', ')}; ` +
          `marks by event: ${ev.map(n => `0x${n} ${per('markEv' + n)}`).join(', ')}; seeding: ${d(N, 'autshadow', 'seedLots') ?? NA} lots, ` +
          `${d(N, 'autshadow', 'seedObjs') ?? NA} objects (${per('seedUs')} us per cut), ${d(N, 'autshadow', 'seedFull') ?? NA} in part; ` +
          `lot-vector suspects ${d(N, 'autshadow', 'liteSuspect') ?? NA}; RowHot ${as.rowHot ?? NA}: ${per('rowColdSkips')} rows cold per cut, ` +
          `${d(N, 'autshadow', 'rowColdReads') ?? NA} cold reads [measured]`);
      }
      if (as.version >= 13) {  // docs/122 §14.20: typed marks (classified objects dirty only the rows of their types)
        const g = (n: string) => d(N, 'autshadow', n) ?? NA;
        L.push(`- typed marks per cut: ${per('typedMarks')} marks, ${per('typedKept')} list rows kept; classified ${g('clsSet')} of ${g('clsAsked')} asked ` +
          `(${g('clsNone')} no object, ${per('clsUs')} us per request on the sim thread); typed audits ${g('typedAuditN')}, misses ${g('typedAuditMis')} (must be 0); lot-vector prints typed ` +
          `${per('liteTyped')} (${per('liteTypedNone')} no list type) [measured]`);
      }
      if (as.version >= 14) {  // docs/122 §14.21: RunSkip (clean runs skipped whole) and the delta publish
        const g = (n: string) => d(N, 'autshadow', n) ?? NA;
        L.push(`- run skip (RunSkip ${as.runSkip ?? NA}) per cut: ${per('runSkips')} runs / ${per('rowSkips')} rows skipped of ${as.runN ?? NA} runs; ` +
          `rebuilds ${g('runBuilds')}, IC stamp moves ${g('icStampMoves')}; delta publishes ${g('pubDelta')}, ${per('pubRows')} rows copied [measured]`);
        // docs/124 §15: the pass share p, and the stale-score check (a fresh re-score >= 1 tick after the cut vs the worker's)
        const pp = d(N, 'autshadow', 'passPairs'), cp = d(N, 'autshadow', 'classPairs'), fr = d(N, 'autshadow', 'freshReqs') ?? 0;
        const fp = d(N, 'autshadow', 'freshPairs') ?? 0, pct = (x: number | undefined, y: number) => y > 0 && x !== undefined ? `${(x * 100 / y).toFixed(1)}%` : NA;
        L.push(`- consume side [measured]: pass share ${cp ? pct(pp, cp) : NA} (${pp ?? NA} of ${cp ?? NA} classified pairs pass Test); fresh re-score ` +
          `(FreshEvery ${as.freshEvery ?? NA}): ${fr} searches, ${fp} pairs, ${pct(d(N, 'autshadow', 'freshDiff'), fp)} differ, top pick changes ` +
          `${pct(d(N, 'autshadow', 'freshTop1'), fr)}, top-10 changes ${pct(d(N, 'autshadow', 'freshTop10'), fr)}, ${fr ? f1((d(N, 'autshadow', 'freshUs') ?? 0) / fr / 1000) : NA} ms each (test cost); ` +
          `KeepPartial ${as.keepPartial ?? NA}: ${d(N, 'autshadow', 'partialKept') ?? NA} overrun searches kept their finished objects`);
        if (as.version >= 16) {  // docs/122 §14.23: answered-only top compare, reference vs fresh, reference passes among A4 pairs
          const a4 = a[4], rp = d(N, 'autshadow', 'refPassA4');
          L.push(`- consume side, v16 [measured]: over pairs both answered top pick changes ${pct(d(N, 'autshadow', 'freshTop1Ans'), fr)}, top-10 set ` +
            `changes ${pct(d(N, 'autshadow', 'freshTop10Ans'), fr)}; cut-time reference vs fresh (all pairs, staleness) top pick ` +
            `${pct(d(N, 'autshadow', 'refFreshTop1'), fr)}, top-10 set ${pct(d(N, 'autshadow', 'refFreshTop10'), fr)}; reference passes among ` +
            `worker-A4 pairs ${a4 ? pct(rp, a4) : NA} (${rp ?? NA} of ${a4 ?? NA}), pass share over answered ${cp && a4 !== undefined && pp !== undefined ? pct(pp, cp - a4) : NA}`);
          const pt = d1(N, 'pairT'), pn = d1(N, 'pairN'), us = (j: number) => pn && pn[j] ? f1(pt![j] / pn[j] / 10) : NA;
          if (pn) L.push(`- game C2 per pair (sampled searches) [measured]: allow-listed pass ${us(0)} us (${pn[0]}), fail ${us(1)} us (${pn[1]}); ` +
            `off-list pass ${us(2)} us (${pn[2]}), fail ${us(3)} us (${pn[3]})`);
        }
      }
      if (as.version >= 17) {  // docs/122 §14.26: transients, host classes, the shared list walk, why runs are walked
        const g = (n: string) => d(N, 'autshadow', n) ?? NA;
        L.push(`- mono-cost15 per cut [measured]: Transient ${as.transOn ?? NA}: ${per('transObjs')} objects made and destroyed between cuts (no mark); ` +
          `HostCls ${as.hcOn ?? NA}: ${g('hcKnown')} classified by the host filter, ${g('hcNoHandle')} no handle, ${g('hcMap2')} second map only (${per('hcUs')} us); ` +
          `ListWalk ${as.lwOn ?? NA}: ${per('lwRows')} rows from ${per('lwWalks')} lot walks (walk ${per('lwWalkUs')} us, host ${per('lwHostUs')} us), ${g('lwFall')} to the body, ` +
          `checks ${g('lwChecks')}, misses ${g('lwMis')} (must be 0); runs walked by reason: not plain ${per('runNoPlain')}, stale ${per('runNoStale')}, ` +
          `new ${per('runNoWalked')}, per-cut sets ${per('runNoSets')}, cold gate ${per('runNoCold')}; class asks skipped ${g('clsGoneSkip')}`);
      }
      if (as.version >= 18) {  // docs/122 §14.27: Sim rows by events, tier moves by lot/room, audit every Nth cut, gone delta
        const g = (n: string) => d(N, 'autshadow', n) ?? NA;
        L.push(`- mono-cost16 per cut [measured]: SimEvRows ${as.seOn ?? NA}: ${per('seKept')} Sim rows kept by events, checks ${g('seChecks')}, ` +
          `misses ${g('seMis')} (must be 0; ICs off 0x${((as.seIcOff ?? 0) >>> 0).toString(16)}); TierMoveLot ${as.tmOn ?? NA}: ${per('tmKept')} re-classifications ` +
          `skipped (same lot and room); ListAuditEvery ${as.laEvery ?? NA}: ${g('laSkips')} dirty cuts without audit; GoneDelta ${as.gdOn ?? NA}: ` +
          `${per('gdRows')} rows by delta, ${per('gdFall')} to the body, records ${g('gdRecs')} (${g('gdNoRec')} lots without, ${g('gdOver')} lost), ` +
          `inconsistent ${g('gdIncons')}, checks ${g('gdChecks')}, misses ${g('gdMis')} (must be 0)`);
      }
      if (as.version >= 19) {  // docs/122 §14.28: ids delta, Sim event rows for promoted / seated Sims, tier cap, why gone marks had no record
        const g = (n: string) => d(N, 'autshadow', n) ?? NA, gw = d1(N, 'gdWhy');
        L.push(`- mono-cost17 per cut [measured]: IdsDelta ${as.idOn ?? NA}: ${per('idSame')} rows same ids, ${per('idApp')} rows plus new objects ` +
          `(${per('idCalls')} small host calls), ${per('idFall')} to the host, ${per('idUs')} us; checks ${g('idChecks')}, misses ${g('idMis')} (must be 0); ` +
          `SimEvLearn ${as.seLearnOn ?? NA} / SimEvSeated ${as.seSeatOn ?? NA}: ${per('seLearnKept')} / ${per('seSeatKept')} rows kept; ` +
          `TierClassMax ${as.tcMax ?? NA}: ${per('tcCapped')} classifications deferred; gone marks without a record: ` +
          (gw ? `none ${gw[0]}, other lot ${gw[1]}, other room ${gw[2]}, full ${gw[3]}` : NA));
      }
    }
    if (as.version >= 15 && as.satbMode) L.push(...satbLines(N, secs));
    if (as.version >= 20 && as.alOn) L.push(...liveLines(N, secs, i.fix));
    // docs/122 §14.18 go-live estimate [inferred]: the scoring a worker would take off the sim (reference us per request x
    // every B13 call x the answered share) minus what it costs the sim (one shared cut per frame with a B13 call, guard thunks)
    const calls13 = d(N, 'autshadow', 'metaCalls'), ticks13 = d(N, 'autshadow', 'metaTicks'), breq = d(N, 'autshadow', 'batchReqs'), refUs = d(N, 'autshadow', 'refUs');
    const allC = a.every(x => x !== undefined) ? a.reduce((t, x) => t! + x!, 0)! : 0, cutsN = d(N, 'autshadow', 'cuts');
    if (as.version >= 11 && calls13 && breq && refUs !== undefined && cutsN && allC) {
      // reference ms per request: refUs only covers the runs that did a reference (Mode 3 VerifyEvery cuts), so divide by refRuns
      const refRuns = d(N, 'autshadow', 'refRuns'), refMs = refUs / 1000 / (refRuns ? refRuns : breq);
      // docs/222 §6: moved = the game's own B14 time for the allow-listed ads (ts3fixes vanilla B14 rate, us per ad, x
      // allow-listed ads per request) x the consumed share (go-live: consumed objects of all B14 calls; shadow modes: the
      // answered share). Without the rate (older ts3fixes): the R model (worker reference us per request x answered).
      const b14Us = fx(i, 'autlive_b14_us'), b14Ads = fx(i, 'autlive_b14_ads'), alc = d1(N, 'al');
      const objsAll = alc ? [AL.objConsumed, AL.notInReq, AL.invalid, AL.stale, AL.ident, AL.acb, AL.late].reduce((t, k) => t + (alc[k] ?? 0), 0) : 0;
      const ansSh = 1 - a[4]! / allC, consSh = objsAll > 0 ? (alc![AL.objConsumed] ?? 0) / objsAll : ansSh;
      const vanMs = b14Us !== undefined && b14Ads ? b14Us / b14Ads / 1000 * (allC - a[5]!) / (refRuns ? refRuns : breq) : undefined;
      const moved = vanMs !== undefined ? vanMs * (calls13 / secs) * consSh : refMs * (calls13 / secs) * ansSh;
      const cutMs = (cutUs ?? 0) / 1000 / cutsN * (ticks13 ?? 0) / secs;
      const g = (guard ?? 0) * GUARD_NS / 1e6 / secs, net = moved - cutMs - g, share = moved > 0 ? cutMs * 100 / moved : Infinity;
      const how = vanMs !== undefined
        ? `vanilla B14 ${f1(b14Us! / b14Ads!)} us per ad x ${f1((allC - a[5]!) / (refRuns ? refRuns : breq))} allow-listed ads per request x ${f1(calls13 / secs)} B13 calls/s x ${(consSh * 100).toFixed(1)}% ${objsAll > 0 ? 'consumed' : 'answered'}`
        : `${f1(refMs)} ms per request x ${f1(calls13 / secs)} B13 calls/s x ${(ansSh * 100).toFixed(1)}% answered`;
      L.push(`- go-live estimate [inferred]: scoring moved ${f1(moved)} ms/s (${how}) - cut ${f1(cutMs)} ms/s ` +
        `(${f1((ticks13 ?? 0) / secs)} cuts/s x ${f1((cutUs ?? 0) / cutsN / 1000)} ms) - guard ${f1(g)} ms/s = net ${f1(net)} ms/s; cut = ${share.toFixed(0)}% of the scoring moved ` +
        `(gate <= 25%: ${share <= 25 ? 'PASS' : 'FAIL'}); GC lock entries are [MonoThread]'s, in the stop-rule line, not here`);
      // docs/122 §14.20: the worker only runs in the window (cut end to the next Simulate entry); when that is far shorter
      // than a job, Mode R makes the sim wait (WindowWaitUs). Go-live with the same window either waits too or falls back
      const winUs = d(N, 'autshadow', 'windowUs'), waits = d(N, 'autshadow', 'windowWaits'), waitUs = d(N, 'autshadow', 'windowWaitUs');
      const runs = d(N, 'autshadow', 'workRuns'), workUs = d(N, 'autshadow', 'workUs'), over = d(N, 'autshadow', 'overrun');
      if (winUs !== undefined && runs && workUs !== undefined && waits && waitUs !== undefined) {
        const waitMs = waitUs / 1000 / waits * (calls13 / secs), ticks = d(N, 'autshadow', 'simTicks');
        L.push(`- worker window [measured]: quiescent gap (cut end to the next Simulate entry, docs/124 §2/§3.4) ${f1(winUs / runs)} us vs ${f1(workUs / runs / 1000)} ms a job` +
          `${ticks ? `; post to resume ~1 tick = ${f1(secs * 1000 / ticks)} ms [inferred]` : ''}; overruns ${over ?? NA} of ${runs} ` +
          `(${f1((over ?? 0) * 100 / runs)}%) at the test wait; go-live with in-place reads [inferred]: no wait = nearly every job falls back, ` +
          `or the sim waits ${f1(waitUs / 1000 / waits)} ms a request x ${f1(calls13 / secs)}/s = ${f1(waitMs)} ms/s: net ${f1(net - waitMs)} ms/s ` +
          `(cut-time reads or a copied heap would let the job use the tick, docs/122 §14.20)`);
      }
      // docs/124 §15-16 consume cost [inferred]: go-live re-runs the game's C1 (live Test + score) for band 1 = the top TOPN passing
      // answered pairs of each search, and for further bands only when §16.1's rule fires (bounds: band 1 only / every passing
      // pair). A selected pair costs R/P x (TAU + (1 - TAU) / p): its Test (share TAU of the pair scoring, docs/75) plus the score
      // share the passing pairs carry. Off-allow-list ads of mixed objects go through C1 as in vanilla (§16.2): cost-neutral, so
      // "moved" stays the allow-listed answered scoring. Pass share p from the Mode R pass counter when present, else the range.
      // Overrun searches (native overrun / req) move nothing but pay the cut; CONSUME_FIXED_MS = post, Sleep(0), resume, selection.
      // shortcut: pass counter name assumed (native autshadow passPairs or ts3fixes autshadow_pass); align when it lands
      const P = (allC - a[5]!) / (d(N, 'autshadow', 'refRuns') || breq), ov = (d(N, 'autshadow', 'overrun') ?? 0) / (req || breq), rate = calls13 / secs;
      const passN = d(N, 'autshadow', 'passPairs') ?? fx(i, 'autshadow_pass'), ans = 1 - a[4]! / allC;
      // p = passes among answered pairs: passPairs counts worker passes, which are answered by definition, so the A4 pairs leave
      // the denominator; selected = p x P x answered (0.36.129 and older took p over all classified pairs and then x answered:
      // the aborts were discounted twice, docs/122 §14.23)
      const base = (1 - ov) * moved - cutMs - g - rate * CONSUME_FIXED_MS, ps = passN !== undefined ? [passN / (allC - a[5]! - a[4]!)] : [0.25, 0.5, 0.75, 1];
      // a selected pair's live C1 cost: the game's C2 time of an allow-listed passing pair when the v16 timer has it [measured],
      // else the R-based model R/P x (TAU + (1 - TAU)/p) [inferred]
      const pt = d1(N, 'pairT'), pn = d1(N, 'pairN'), cMeas = pn && pn[0] > 0 ? pt![0] / pn[0] / 1e4 : undefined;
      const at = ps.map(p => { const c = cMeas ?? refMs / P * (TAU + (1 - TAU) / p), cost = (n: number) => base - rate * (1 - ov) * n * c;
        return { b1: cost(Math.min(TOPN, p * P * ans)), all: cost(p * P * ans), nb: base / (rate * (1 - ov) * c) }; });
      if (P > 0) L.push(`- go-live after consume [inferred]: overrun ${(ov * 100).toFixed(1)}% of searches, before consume ${f1(base)} ms/s; live C1, top ${TOPN} + bands, ` +
        `pass share ${passN !== undefined ? `${ps[0].toFixed(2)} of answered [measured]` : `${ps.join('/')} (no pass counter)`}, pair cost ${cMeas !== undefined ? `${f1(cMeas * 1000)} us [measured C2]` : 'R model'}: ` +
        `net ${at.map(x => f1(x.b1)).join('/')} ms/s with band 1 only, ` +
        `${at.map(x => f1(x.all)).join('/')} with every band; break-even N ${at.map(x => f1(x.nb)).join('/')} (${f1(P)} allow-listed pairs per search)`);
    }
  }
  // stop rule: sim-side cost of the worker path = cut + snapshot + GC lock waits [measured] + uncontended lock entries x
  // LOCK_ENTRY_NS + sim guard calls x GUARD_NS [inferred]; the Mode R reference and the WindowWaitUs wait are test costs and
  // stay out. mtprobe v6 (docs/30 §16): only the sim's waits and the sim's entries (the worker's are on the worker's core);
  // before v6 every thread's (conservative).
  if (worker || as?.mode >= 1) {
    const v6 = (mt?.version ?? 0) >= 6, simWaitUs = v6 ? d(N, 'mtprobe', 'simLockWaitUs') : waitUs;
    const simCalls = v6 && calls !== undefined ? calls - (callsW ?? 0) : calls;
    const meas = (cutUs ?? 0) / 1000 / secs + (simWaitUs ?? 0) / 1000 / secs;
    const inf = ((simCalls ?? 0) * LOCK_ENTRY_NS + (guard ?? 0) * GUARD_NS) / 1e6 / secs, x = meas + inf;
    L.push(`- worker adds ${f1(x)} ms/s to the sim (stop rule: >= ${STOP_MS}): ${f1(meas)} [measured: cut ${cutUs === undefined ? NA : 'yes'} + GC lock waits ${waitUs === undefined ? NA : 'yes'}] + ${f1(inf)} [inferred: lock entries ${calls === undefined ? NA : `x ${LOCK_ENTRY_NS} ns`} + guard calls ${guard === undefined ? NA : `x ${GUARD_NS} ns`}]; Mode R reference and window waits left out${x >= STOP_MS ? ' STOP' : ''}`);
  }
  return L;
}

// docs/124 §17 go-live ([AutShadow] Mode=3, autshadow v20): ts3fixes AutLive.cs al[] slots (kS*). One line of searches and
// drops, one of the consume itself (objects, pairs through C1, bands, drift), one of G7 (Dry=1) or of the acting checks.
export const AL = { eligible: 0, posted: 1, consumed: 2, dry: 3, notPosted: 4, wait: 5, stopped: 6, epochRun: 7, noWorker: 8, exc: 9, epoch: 10,
  lot: 11, actor: 12, state: 13, interrupted: 14, destroyed: 15, notInReq: 16, invalid: 17, stale: 18, ident: 19, acb: 20, late: 21, objConsumed: 22,
  band1: 23, bandPairs: 24, offList: 25, band2: 26, band3: 27, bandFull: 28, whyCount: 29, whyMargin: 30, drift0: 31, dryTop10: 36, dryTop1: 37,
  drySimMis: 38, applyUs: 39, waitTicks: 40, verifyOff: 41, candidates: 42, head: 43, dryMissStale: 44, dryMissBand: 45, guardOff: 46,
  verifyRuns: 47, verifyTop1: 48, verifyTop10: 49, filterDiff: 50, other: 51, dryOffList: 52, dryMissOther: 53, c2Rehomed: 54, dryTop1Tie: 55,
  // Dry=1 only: AutLive.cs kSDryNoCmp / kSDryExtra reuse the acting-only slots consumed / band1 (al[] is 56 fixed in autshadow v20)
  dryNoCmp: 2, dryExtra: 23 } as const;
export const FEAT_BITS = ['late', 'prelearn', 'tierreqlots', 'rain', 'desirefn', 'satb', 'guard', 'cold', 'partial'] as const;
// one line: per feature bit, searches that had it, mismatches (dry unequal or replay mismatch) and give-ups (fallbacks, dry-nocmp)
// among them, with the mismatch rate; bits no search had are left out. Records: "feat"/"feats" fields in autshadow.jsonl.
export function featLine(fix?: Input['fix']): string | null {
  const dv = (k: string) => { const c = fix?.[k]; return typeof c?.after === 'number' ? c.after - (c.before ?? 0) : null; };
  if (dv('feat_late_searches') === null) return null;
  const parts = FEAT_BITS.map(b => ({ b, n: dv(`feat_${b}_searches`) ?? 0, m: dv(`feat_${b}_mis`) ?? 0, g: dv(`feat_${b}_drop`) ?? 0 }))
    .filter(x => x.n > 0 || x.m > 0 || x.g > 0)
    .map(x => `${x.b} ${x.n} searches / ${x.m} mismatches (${x.n > 0 ? `${(x.m * 100 / x.n).toFixed(1)}%` : NA}) / ${x.g} give-ups`);
  return `- go-live feature mask [measured]: ${parts.length ? parts.join('; ') : 'no search had a feature bit'} (feat/feats fields on dry-diff, dry-sim-mis, dry-nocmp, live-drop, live-use records)`;
}
// docs/222 unseen-direct (ts3fixes 611-640, switch autunseen): searches by path, direct consume, D2 live Test, snap-back,
// own cost vs vanilla scoring saved, per-path guard; Dry: top-10/top-1 by actor class. Absent counters = no lines.
export function unseenLines(fix: Input['fix'] | undefined, secs: number, dry: boolean): string[] {
  const has = (k: string) => typeof fix?.[k]?.after === 'number';
  if (!has('autunseen_posted')) return [];
  const fv = (k: string) => { const c = fix?.[k]; return typeof c?.after === 'number' ? c.after - (c.before ?? 0) : 0; };
  const pc = (x: number, y: number) => y > 0 ? `${(x * 100 / y).toFixed(1)}%` : NA;
  const L: string[] = [];
  if (dry) {
    const n = fv('autunseen_dry_runs'), ns = fv('autunseen_dry_seen_runs');
    if (n + ns > 0) L.push(`- unseen-direct Dry by actor class (docs/222) [measured]: unseen actors ${n} searches compared, top-10 set equal ` +
      `${pc(fv('autunseen_dry_top10'), n)}, top 1 equal ${pc(fv('autunseen_dry_top1'), n)}; seen actors ${ns} searches; unseen share of compared ${pc(n, n + ns)}`);
    return L;
  }
  const posted = fv('autunseen_posted'), direct = fv('autunseen_direct'), seen = fv('autunseen_seen_post');
  const tp = fv('autunseen_test_pass'), tf = fv('autunseen_test_fail'), pairs = fv('autunseen_pairs');
  L.push(`- unseen-direct (autunseen, docs/222) [measured]: eligible searches seen at the post (exact path) ${seen}, unseen posted ${posted} ` +
    `(unseen share ${pc(posted, posted + seen)}); direct ${direct}, snap-back at the resume ${fv('autunseen_snap_resume')}, at a later slice ` +
    `${fv('autunseen_snap_slice')}, dropped (exact fallback) ${fv('autunseen_dropped')}; objects to the game's B14 ${fv('autunseen_obj_game')}; ` +
    `ads inserted ${pairs} (${direct ? f1(pairs / direct) : NA} a search), off-list C1 ${fv('autunseen_offlist')}; D2 live Test pass ${tp} / fail ${tf} ` +
    `(${pc(tf, tp + tf)} fail, ${tp + tf ? f1(fv('autunseen_test_us') / (tp + tf)) : NA} us a Test), vanilla fallbacks ${fv('autunseen_vanilla')}, ` +
    `no pick ${fv('autunseen_nopick')}; queued picks tagged ${fv('autunseen_tagged')}, at the flip kept ${fv('autunseen_flip_kept')} / cancelled ` +
    `${fv('autunseen_flip_cancel')}; exceptions ${fv('autunseen_exc')}`);
  const cost = fv('autunseen_cost_us'), saved = fv('autunseen_saved_us'), ads = fv('autlive_b14_ads');
  const pm = fix?.autunseen_guard_ratio_pm?.after;
  L.push(`- unseen-direct cost [measured]: own sim cost ${f1(cost / 1000 / secs)} ms/s (consumer + cut + GC lock waits) vs vanilla scoring saved ` +
    `${f1(saved / 1000 / secs)} ms/s (game B14 ${ads ? f1(fv('autlive_b14_us') / ads) : NA} us per ad x consumed ads) = net ${f1((saved - cost) / 1000 / secs)} ms/s ` +
    `(guard thunks and the D2 Test excluded: fixed / in "cost" only via consumer); guard ${fv('autunseen_guard_windows')} windows, ` +
    `${fv('autunseen_guard_skipwindows')} skip windows, ${fv('autunseen_guard_skipped')} searches skipped, last window ` +
    `${typeof pm !== 'number' || pm < 0 ? 'n/a (no vanilla rate yet)' : `${pm} per mille`}`);
  // docs/222 §8 pacing (ts3fixes 659-660, switch autunseenpace): skipped ads' vanilla cost charged to B13's 1 ms yield timer
  if (has('autunseen_pace_us')) {
    const pus = fv('autunseen_pace_us'), pf = fv('autunseen_pace_forced');
    L.push(`- unseen-direct pacing (autunseenpace, docs/222 §8) [measured]: ${f1(pus / 1000)} ms charged to the yield timer ` +
      `(${direct ? f1(pus / direct) : NA} us a direct search), yields brought forward ${pf} (${direct ? f1(pf / direct) : NA} a direct search)` +
      `${pus === 0 ? '; 0 = switch off, no vanilla B14 rate yet, or nothing consumed' : ''}`);
  }
  return L;
}

// docs/222 §8 G8 by actor class (ts3fixes 661-669): per-search outcome rates per class, kept apart from the cadence (searches per
// Sim-hour), which go-live pacing changes. Absent counters (older build) = no lines.
export function g8ClassLines(fix: Input['fix'] | undefined): string[] {
  if (typeof fix?.g8_unseen_searches?.after !== 'number') return [];
  const fv = (k: string) => { const c = fix?.[k]; return typeof c?.after === 'number' ? c.after - (c.before ?? 0) : 0; };
  const pc = (x: number, y: number) => y > 0 ? `${(x * 100 / y).toFixed(1)}%` : NA, per = (x: number, y: number) => y > 0 ? (x / y).toFixed(3) : NA;
  const cls = (c: string) => {
    const n = fv(`g8_${c}_searches`), np = fv(`g8_${c}_null_picks`);
    return `${c} ${n} searches: no-pick share ${pc(np, n)}, GoHome per no-pick ${per(fv(`g8_${c}_gohome`), np)}, pushes per search ${per(fv(`g8_${c}_pushes`), n)}`;
  };
  const sh = fv('g8_simhours_x100') / 100, ush = fv('g8_unseen_simhours_x100') / 100, ssh = sh - ush;
  const nu = fv('g8_unseen_searches'), ns = fv('g8_seen_searches'), rate = (n: number, h: number) => h > 0 ? f1(n / h) : NA;
  return [`- G8 per search by actor class (class at the push / no-pick) [measured]: ${cls('unseen')}; ${cls('seen')}`,
    `- G8 cadence [measured]: searches per Sim-hour all ${rate(nu + ns, sh)} (${f1(sh)} Sim-hours), unseen ${rate(nu, ush)} (${f1(ush)} unseen Sim-hours), ` +
      `seen ${rate(ns, ssh)} (${f1(ssh)}; class from the hourly sample)`];
}
export function liveLines(N: Input['native'], secs: number, fix?: Input['fix']): string[] {
  const al = d1(N, 'al'), as = N?.after?.autshadow;
  if (!al) return ['- go-live: n/a (no al counters)'];
  const v = (k: keyof typeof AL) => al[AL[k]] ?? 0, pc = (x: number, y: number) => y > 0 ? `${(x * 100 / y).toFixed(1)}%` : NA;
  const dry = ((as?.alOn ?? 0) & 2) !== 0;
  const used = (dry ? 0 : v('consumed')) + v('dry'), drops = v('wait') + v('stopped') + v('epochRun') + v('noWorker') + v('exc') + v('epoch') + v('lot') + v('actor') +
    v('state') + v('head') + v('other');
  const objs = v('objConsumed') + v('notInReq') + v('invalid') + v('stale') + v('ident') + v('acb') + v('late');
  const dr = [0, 1, 2, 3, 4].map(i => al[AL.drift0 + i] ?? 0);
  const L = [`- go-live (${(as?.alOn ?? 0) & 2 ? 'Dry, G7' : 'acting'}) [measured]: ${v('eligible')} eligible searches (${f1(v('eligible') / secs)}/s), posted ${v('posted')}, ` +
      `not posted ${v('notPosted')} (sampling, busy, guard); used ${used} (${pc(used, v('posted'))}); fell back ${drops}: not ready in MaxWait ${v('wait')}, ` +
      `run stopped ${v('stopped')}, epoch in run ${v('epochRun')}, no worker/refused ${v('noWorker')}, exception ${v('exc')}, epoch since cut ${v('epoch')}, ` +
      `lot ${v('lot')}, actor fields ${v('actor')}, head interaction ${v('head')}, game state ${v('state')}, other ${v('other')}; game returns after our ` +
      `yield: interrupted ${v('interrupted')}, destroyed ${v('destroyed')}; wait ${v('posted') ? f1(v('waitTicks') / v('posted')) : NA} ticks a search; ` +
      `consumer time ${used ? f1(v('applyUs') / used) : NA} us a search (${f1(v('applyUs') / 1000 / secs)} ms/s); verify cuts ${d(N, 'autshadow', 'alVerify') ?? NA} ` +
      `(top-1 same ${v('verifyTop1')} / top-10 same ${v('verifyTop10')} of ${v('verifyRuns')}), live off: verify ${v('verifyOff')}, guard ${v('guardOff')}`,
    `- go-live objects [measured]: ${objs} B14 calls in used searches: consumed ${pc(v('objConsumed'), objs)}, game's B14 for not in request ${v('notInReq')}, ` +
      `worker abort/off-list implementor ${v('invalid')}, stale (ObjStale ${d(N, 'autshadow', 'alStaleHit') ?? NA} of ${d(N, 'autshadow', 'alStaleQ') ?? NA} queries) ` +
      `${v('stale')}, ads changed ${v('ident')}, AlwaysChooseBest ${v('acb')}, late (B13 yielded) ${v('late')}; pairs through C1: band 1 ${dry ? 'n/a (Dry)' : v('band1')}, bands 2+ ` +
      `${v('bandPairs')}, off-list ${v('offList')}, worker-filtered ${v('filterDiff')} (candidates ${v('candidates')}, ${used ? f1(v('candidates') / used) : NA} a search); ` +
      `bands fired: 2 ${v('band2')}, 3 ${v('band3')}, full ${v('bandFull')} (count rule ${v('whyCount')}, margin rule ${v('whyMargin')}); drift per search ` +
      `0: ${dr[0]}, <1%: ${dr[1]}, <5%: ${dr[2]}, <20%: ${dr[3]}, more: ${dr[4]}`];
  // docs/124 §17.10 cost guard (ts3fixes 538-541, acting only): windows, skips, the last window's cost per mille of the scoring
  // saved (a gauge: the after value, not a delta). Absent on a ts3fixes before the guard.
  const gw = fix?.autliveguard_windows;
  if (!dry && gw && typeof gw.after === 'number') {
    const dv = (k: string) => { const c = fix?.[k]; return typeof c?.after === 'number' ? c.after - (c.before ?? 0) : 0; };
    const pm = fix?.autliveguard_ratio_pm?.after, sk = dv('autliveguard_skipped');
    L.push(`- go-live cost guard [measured]: ${dv('autliveguard_windows')} windows, ${dv('autliveguard_skipwindows')} skip windows, ` +
      `${sk} eligible searches skipped (${pc(sk, v('eligible'))} of eligible); last window cost = ` +
      `${typeof pm !== 'number' || pm < 0 ? 'n/a (no reference yet)' : `${pm} per mille of the scoring saved (${pm > 1000 ? 'above' : 'at or below'} break-even)`}`);
  }
  L.push(...unseenLines(fix, secs, dry));
  const fv = (k: string) => { const c = fix?.[k]; return typeof c?.after === 'number' ? c.after - (c.before ?? 0) : 0; };
  // docs/217 L1 autlivelate (ts3fixes 550-565): absent or 0 slices = switch off / older build, no line
  if (fv('autlivelate_slices') > 0) {
    const lc = fv('autlivelate_consumed'), late = v('late'), ref = ['age', 'epoch', 'state', 'lot', 'head', 'actor', 'other'].map(r => `${r} ${fv(`autlivelate_ref_${r}`)}`);
    const per = (us: string, n: number) => n > 0 ? `${f1(fv(us) / n)} us` : NA;
    L.push(`- go-live late consume (autlivelate, docs/217 L1) [measured]: ${fv('autlivelate_slices')} later slices checked, refused: ${ref.join(', ')}; ` +
      `late objects consumed ${lc} (late-consumed share ${pc(lc, lc + late)} of late B14 calls), stale on re-query ${fv('autlivelate_stale')}; ` +
      `age 1/2/3+ ticks ${fv('autlivelate_age1')}/${fv('autlivelate_age2')}/${fv('autlivelate_age3')}; B14 per object: consumed ` +
      `${per('autlivelate_us_consumed', v('objConsumed'))}, game's B14 late ${per('autlivelate_us_late_game', late)} ` +
      `(${f1(fv('autlivelate_us_late_game') / 1000 / secs)} ms/s), game's B14 invalid/stale/ident ${per('autlivelate_us_invalid_game', v('invalid') + v('stale') + v('ident'))}`);
  }
  // docs/124 §8 G8 (ts3fixes 566-574, kAutLive packages, both autlive arms)
  if (fix?.g8_pushes && typeof fix.g8_pushes.after === 'number') {
    const sh = fv('g8_simhours_x100') / 100, lat = [0, 1, 2, 3, 4].map(i => fv(`g8_lat${i}`)), ln = lat.reduce((a, b) => a + b, 0);
    let acc = 0, med: string = NA;
    for (let i = 0; i < 5 && ln > 0; i++) { acc += lat[i]; if (acc * 2 >= ln) { med = i === 4 ? '4+' : String(i); break; } }
    L.push(`- G8 behaviour [measured]: ${fv('g8_pushes')} autonomous pushes over ${f1(sh)} Sim-hours (${sh > 0 ? f1(fv('g8_pushes') / sh) : NA} per Sim-hour), ` +
      `no-pick searches ${fv('g8_null_picks')}, MakeSimGoHome fallbacks ${fv('g8_gohome')}; request-to-push latency (Mode 3 searches, ticks 0/1/2/3/4+) ` +
      `${lat.join('/')}, median ${med} ticks; picks per definition: {"t":"picks"} lines in autshadow.jsonl`);
    L.push(...g8ClassLines(fix));
  }
  // per-search feature mask (ts3fixes 575-601, AutLiveCore.Feat): which features the searches that went wrong had; absent = no line
  const fb = featLine(fix);
  if (fb) L.push(fb);
  if (dry) {
    // compared = dry searches minus those whose compare never ran (game B13 returned interrupted/destroyed, compare exception)
    const n = v('dry') - v('dryNoCmp'), causes = v('dryMissStale') + v('dryMissBand') + v('dryMissOther') + v('dryExtra');
    const noCause = n > v('dryTop10') && causes === 0 ? `; ${n - v('dryTop10')} unequal with no counted cause (a build before dry-nocmp: searches never compared)` : '';
    L.push(`- G7 dry apply [measured]: ${n} searches compared (not compared ${v('dryNoCmp')}: game B13 returned interrupted/destroyed or compare ` +
      `exception, dry-nocmp records): top-10 set equal ${pc(v('dryTop10'), n)}, top 1 equal ${pc(v('dryTop1'), n)} ` +
      `(up to ties at the top score; ${v('dryTop1Tie')} equal only by a tie) (gate >= 99% / >= 99.5%); game queue replay mismatches ${v('drySimMis')} ` +
      `(must be 0; C2 calls kept across a B13 yield ${v('c2Rehomed')}); missed top-10 pairs by cause: worker failed Test ` +
      `(stale reject) ${v('dryMissStale')}, ranked below the bands ${v('dryMissBand')}, other ${v('dryMissOther')}; our top-10 entries the game's queue ` +
      `lacks ${v('dryExtra')}${noCause}; off-list C1 calls ${v('dryOffList')} (dry-diff / dry-sim-mis / dry-nocmp records in autshadow.jsonl)`);
  }
  return L;
}

// docs/206 §7 S0 gate [inferred model from measured counts]: per job at go-live (one shared cut per frame with a B13 call,
// Sample=1) the sim pays every store in the job span through the barrier, the first write of each word again, and the
// thread test on every load stub; unlogged events (an IC with a shared pointer argument, a runtime writer we cannot log)
// cost a wait for the worker. Gate: <= SATB_GATE_MS ms/s and <= 20k words per job; stop above SATB_STOP_MS or a 4 MB log.
export const SATB_GATE_MS = 3, SATB_STOP_MS = 5;
export function satbLines(N: Input['native'], secs: number): string[] {
  const g = (k: string) => d(N, 'autshadow', k), as = N?.after?.autshadow;
  const spans = g('satbJobSpans'), ticks13 = g('metaTicks');
  // act (Satb=2, docs/206 §7.3): no probe spans; what matters is whether jobs ran through the next tick unstopped
  if (as?.satbMode === 2) return [`- Satb act: ${g('satbSpans') ?? NA} spans, jobs still running at the next Simulate entry ${g('satbConc') ?? NA}, ` +
    `stopped/stale ${g('satbStale') ?? NA}, worker give-ups ${g('satbWGiveUp') ?? NA} (last op 0x${(as?.satbWGiveUpOp ?? 0).toString(16)}; ts3native.log 'autshadow satb give-up' by op), ` +
    `sim waits ${g('satbWaits') ?? NA} (timeouts ${g('satbWaitTimeouts') ?? NA}), worker log hits ${g('satbWHits') ?? NA} of ${g('satbWLoads') ?? NA} loads, sim loads ${g('satbLoads') ?? NA}`];
  if (!spans) return [`- Satb (mode ${as?.satbMode}): no job spans timed (satbJobSpans 0)`];
  const per = (k: string) => (g(k) ?? 0) / spans;
  const st = per('satbJobStores'), w = per('satbJobWords'), ld = per('satbJobLoads'), ev = per('satbJobIc'), us = per('satbJobUs');
  const jobsS = ticks13 !== undefined ? ticks13 / secs : undefined;
  const nsJob = st * SATB_STORE_NS + w * SATB_WORD_NS + ld * SATB_LOAD_NS, waitMs = ev * us / 2 / 1000;  // a wait ~ half a job [inferred]
  const ms = jobsS === undefined ? undefined : jobsS * (nsJob / 1e6 + waitMs);
  const maxMB = (as?.satbJobMaxWords ?? 0) * 8 / 1048576;
  const verdict = ms === undefined ? NA : ms > SATB_STOP_MS || maxMB > 4 ? 'STOP (docs/206 §5 stop rule)' : ms <= SATB_GATE_MS && w <= 20_000 ? 'PASS' : 'between gate and stop: report';
  return [`- Satb probe per job span (${f1(us / 1000)} ms, ${spans} spans) [measured]: ${f1(st)} sim stores, ${f1(w)} words first written (max ${as?.satbJobMaxWords ?? NA}, ` +
    `${f1(maxMB)} MB log), ${f1(per('satbJobPages'))} pages, ${f1(ld)} sim loads, ${f1(ev)} unlogged events (IC shared pointer / runtime writer); whole tick: ` +
    `${f1((g('satbStores') ?? 0) / (g('satbSpans') || 1))} stores, ${f1((g('satbWords') ?? 0) / (g('satbSpans') || 1))} words; IC calls ${g('satbIcCalls') ?? NA} ` +
    `(byref/array ICs ${g('satbIcCallsAll') ?? NA}, shared ${g('satbIcShared') ?? NA}), runtime writers ${g('satbRtCalls') ?? NA} (unlogged ${g('satbRtWaits') ?? NA}), big ${g('satbBig') ?? NA}, overflow ${g('satbOverflow') ?? NA}` +
    // v16 (docs/206 §9): IC byref/array arguments logged instead of waits; the unsized runtime writers left by site
    (as?.version >= 16 ? `; v16: IC arguments logged ${g('satbIcLogged') ?? NA} (${g('satbIcLogWords') ?? NA} words, ${f1(per('satbJobIcLogged'))} a job span), ` +
      `not logged ${g('satbIcShared') ?? NA}; runtime calls MonoField.SetValue ${g('satbRtFset') ?? NA}, PtrToStructure(object) ${g('satbRtPtrStruct') ?? NA} ` +
      `(0.36.136+: logged, only 'unlogged' above waits; 0.36.133-135: each one waits)` : ''),
  `- Satb barrier at go-live [inferred]: ${jobsS === undefined ? NA : f1(jobsS)} jobs/s x (${f1(nsJob / 1000)} us barrier: stores x ${SATB_STORE_NS} + words x ${SATB_WORD_NS} + loads x ${SATB_LOAD_NS} ns` +
    ` + waits ${f1(waitMs)} ms) = ${ms === undefined ? NA : `${f1(ms)} ms/s`}: ${verdict} (gate <= ${SATB_GATE_MS} ms/s and <= 20k words a job; stop > ${SATB_STOP_MS} ms/s or > 4 MB)`,
  // docs/206 §11: the same counts with 0.36.136's load thunks (the loads' share is most of the barrier in satb-s0b)
  `- Satb barrier with 0.36.136 load thunks [inferred]: loads x ${SATB_LOAD_THUNK_NS} ns -> ` +
    `${jobsS === undefined ? NA : `${f1(jobsS * ((nsJob - ld * (SATB_LOAD_NS - SATB_LOAD_THUNK_NS)) / 1e6 + waitMs))} ms/s`}`];
}

const jsonl = (f: string) => readFileSync(f, 'utf8').split('\n').filter(Boolean).flatMap(l => { try { return [JSON.parse(l)]; } catch { return []; } });
function withBudget(i: Input): Input {
  try { return { ...i, budget: analyze(load(i.tag)) }; } catch (e) { return { ...i, budgetWhy: String((e as Error).message ?? e) }; }
}
const readJson = (f?: string) => f && existsSync(f) ? JSON.parse(readFileSync(f, 'utf8')) : undefined;
const rowInput = (r: any, minutes: number) => withBudget({ tag: r.profileTag, minutes: r.run?.profile?.minutes ?? minutes,
  native: readJson(r.nativeDump) ?? readJson(join(QUEUE, `${r.profileTag}-native.json`)), fix: r.fixActivity?.counters });
export function inputs(arg: string, minutes: number): Input[] {
  if (arg.endsWith('.jsonl')) return jsonl(arg).filter(r => r.profileTag).map(r => rowInput(r, minutes));
  return [withBudget({ tag: arg, minutes, native: readJson(join(QUEUE, `${arg}-native.json`)) })];
}
// queue files of every mono-cost family (mono-cost-<date>.jsonl, mono-cost2-..., later ones); family = the part before the date
export const FAMILY_RE = /^(mono-cost\d*|satb-s\d+[a-z]?|golive-[a-z0-9]+)-(\d{4}-\d\d-\d\d)\.jsonl$/;
// newest row per job id (a rerun counts for the job it reruns), optionally one family; rows sorted by job start time
export function newestPerJob(rows: any[], family?: string): any[] {
  const best = new Map<string, any>();
  for (const r of rows) {
    if (!r?.profileTag || (family && (r.queue ?? '') !== family)) continue;
    const id = String(r.rerunOf ?? r.job), o = best.get(id);
    if (!o || String(r.time) > String(o.time)) best.set(id, r);
  }
  return [...best.values()].sort((a, b) => String(a.time) < String(b.time) ? -1 : 1);
}

function selfCheck() {
  const row = { iters: 30, script: 10, taskObjects: 80, oneShot: 0, threaded: 600, route: 100, rtPlanWait: 10, rtLock: 50, rtOtherLock: 0, rtJob: 0,
    gcMark: 4, gcForcedMs: 1, fin: 2, animSacs: 20, animPose: 30, locomotion: 40, publish: 10 };
  const run: Run = { tag: 'mono-cost-fx-bp-autshadow-r', ics: new Set(['ScriptCore.World.World_IsOnRoofImpl']),
    rows: [{ t: 0, iters: 1 }, { t: 61_000, ...row }, { t: 62_000, ...row }, { t: 63_000, ...row }], hitches: [],
    managed: [['ScriptCore.World.World_IsOnRoofImpl', 'X.Y'], ['A.B', 'Sims3.Gameplay.Autonomy.AutonomyManager.Simulate', 'ScriptCore.ScriptProxy.Simulate']],
    simstack: [], native: {} };
  const mt0 = { version: 4, workerStarts: 0, workerRunUs: 0, lockWaitUs: 0, lockWaits: 0, lockedCalls: 0, lockedWorker: 0, workerJobs: 0, workerExceptions: 0 };
  const as0 = { version: 5, mode: 1, req: 0, cuts: 0, cutUs: 0, cutReq: 0, refUs: 0, workUs: 0, workRuns: 0, busy: 0, abortTotal: 0, overrun: 0, noWorker: 0 };
  // 10 min window = 600 s: cut 600 ms -> 1.0 ms/s; lock waits 2400 ms -> 4.0 ms/s; total 5.0 = STOP
  const native = { before: { mtprobe: mt0, autshadow: as0 },
    after: { mtprobe: { ...mt0, p3Sites: 19, p4Ready: 15, locking: 1, workerState: 2, lockOverflow: 0, workerCtxSet: 0, n2mOverflow: 0, lockStale: 0, lockMaxWaitUs: 900,
      workerStarts: 1, workerRunUs: 1_200_000, lockWaitUs: 2_400_000, lockWaits: 50, lockedCalls: 1000, lockedWorker: 300, workerJobs: 400, workerExceptions: 0 },
      autshadow: { ...as0, cutSite: 2, cutMaxUs: 4000, snapBytes: 9000, req: 500, cuts: 500, cutUs: 600_000, cutReq: 500, refUs: 3_000_000, workUs: 1_200_000, workRuns: 400, busy: 3, abortTotal: 60, overrun: 40, noWorker: 0 } } };
  const fix = { autshadow_a0: { before: 0, after: 297 }, autshadow_a1: { before: 0, after: 3 }, autshadow_a2: { before: 0, after: 0 }, autshadow_a3: { before: 0, after: 0 },
    autshadow_a4: { before: 0, after: 60 }, autshadow_a5: { before: 0, after: 40 }, autshadow_skips: { before: 0, after: 900 } };
  const out = costLines({ tag: run.tag, minutes: 10, budget: analyze(run), native, fix }).join('\n');
  assert.match(out, /Mono interpreter \(sim thread\): 269\.5 ms\/s .*autonomy search/); // script 690 - route 90 - waits 60 - forced GC 1 = 539, 1 of 2 samples Mono
  assert.match(out, /GC \(mark \+ finalizers\): 6\.0 ms\/s \[measured\]/);
  assert.match(out, /worker busy: 2\.0 ms\/s/);
  assert.match(out, /cut \+ snapshot 1\.0 ms\/s .*avg 1200\.0 us/);
  assert.match(out, /reference re-score .*: 5\.0 ms\/s/);
  assert.match(out, /posted 500, .*compared 300, fell back 100/);
  assert.match(out, /Mode R match rate: 99\.0% bit-equal/);
  assert.match(out, /A4 share: 15\.0% of 400 classified pairs \(target < 20%\); skipped pairs 900/);
  // 1000 entries x 68 ns, no guard calls: inferred 0.0; measured 5.0 -> STOP
  assert.match(out, /worker adds 5\.0 ms\/s to the sim \(stop rule: >= 5\): 5\.0 \[measured.* \+ 0\.0 \[inferred.* STOP$/m);
  // 580k entries/s x 68 ns = 39.4 ms/s, 100k guard calls/s x 17 ns = 1.7 ms/s (inferred); window waits 120 ms / 600 s
  const busy = { before: native.before, after: { ...native.after, mtprobe: { ...native.after.mtprobe, version: 5, lazyGate: 0, lazyOn: 0, lazyOff: 0, lockedCalls: 580_000 * 600 },
    autshadow: { ...native.after.autshadow, version: 7, guardSim: 100_000 * 600, windowWaits: 40, windowWaitUs: 120_000,
      abort: { row: 900, other: 100 }, entries: 3000, entriesMax: 8192, memoRows: 2048, evicted: 500, evictedMemo: 100, ringDropped: 0, learnWantMax: 256, refreshMax: 700 } } };
  const v8 = { before: busy.before, after: { ...busy.after, autshadow: { ...busy.after.autshadow, version: 8, cutLearnUs: 5_000, cutPrepUs: 10_000, cutRefreshUs: 400_000,
    cutAgeUs: 5_000, cutAuditUs: 20_000, cutPublishUs: 60_000, bodyNew: 1000, bodyObj: 50_000, bodyList: 10_000, bodyCut: 5_000, bodyNewUs: 3_000, bodyObjUs: 150_000,
    bodyListUs: 200_000, bodyCutUs: 40_000, coldSkips: 7000, coldReads: 3, coldIcs: 30, simX87: 0x7f, workX87: 0x27f, simCsr: 0x1f80, workCsr: 0x1f80, parentRows: 4 } } };
  const out3 = costLines({ tag: 'v8', minutes: 10, native: v8, fix }).join('\n');
  assert.match(out3, /cut per cut \(us\): learn 10\.0, prep 20\.0, refresh 800\.0, age 10\.0, audit 40\.0, publish 120\.0; body calls per cut \(us per cut\): new 2\.0 \(6\.0\), object 100\.0 \(300\.0\), list 20\.0 \(400\.0\), per-cut 10\.0 \(80\.0\); Sim parent objects learned 4; cold: 7000 rows skipped, 3 reads, 30 ICs; x87 sim 0x7f worker 0x27f/);
  const v9 = { before: v8.before, after: { ...v8.after, autshadow: { ...v8.after.autshadow, version: 9, listPrintSkips: 4000, listPrintUs: 10_000, listLots: 2000, listSame: 100,
    tierMs: 1500, tier1Sims: 5000, tier2Sims: 3000, tier2Stale: 2000, tier1Body: 20_000, tier2Body: 4000, tier1BodyUs: 60_000, tier2BodyUs: 12_000, tierClassUs: 8000, ageRuns: 62 } } };
  const out4 = costLines({ tag: 'v9', minutes: 10, native: v9, fix }).join('\n');
  const v10 = { before: v9.before, after: { ...v9.after, autshadow: { ...v9.after.autshadow, version: 10, dirtyLots: 1000, dirtyRows: 2000, dirtyKept: 8000,
    objResolved: 40_000, objResolveUs: 20_000, dirtyUnknown: 3, listAuditN: 500, listAuditUs: 20_000, listMissed: 0, dirtyTripped: 0, tierClassN: 50_000,
    tierCamInval: 7, tierHold: 12, listT2Wait: 600, listT2Ref: 100, t1Lots: 5 } } };
  const out5 = costLines({ tag: 'v10', minutes: 10, native: v10, fix }).join('\n');
  // v11: 600 s, 500 cuts, refUs 3 s over 500 batch requests = 6 ms each; 3000 B13 calls = 5/s; A4 60 of 400 = 15%; moved 6 x 5 x 0.85 =
  // 25.5 ms/s; cut 1.2 ms x 1000 ticks / 600 s = 2.0 ms/s; guard 1.7 ms/s; net 21.8, cut 8% of moved
  const v11 = { before: v10.before, after: { ...v10.after, autshadow: { ...v10.after.autshadow, version: 11, simMarks: 5000, simFreeKept: 3000, listTypes: 12, listTypesSimFree: 9,
    unplaced: 400, liteLots: 10_000, liteUs: 5_000, liteMarks: 50, resyncTouch: 1, resyncMap: 0, olCompact: 0, olRows: 9000, tierKept: 30_000, batchCuts: 20, batchReqs: 500,
    batchMax: 3, metaCalls: 3000, metaTicks: 1000, simTicks: 20_000 } } };
  const out6 = costLines({ tag: 'v11', minutes: 10, native: v11, fix }).join('\n');
  const out6v = costLines({ tag: 'v11v', minutes: 10, native: v11, fix: { ...fix, autlive_b14_us: { before: 0, after: 50_000 }, autlive_b14_ads: { before: 0, after: 10_000 } } }).join('\n');
  assert.match(out6v, /go-live estimate \[inferred\]: scoring moved 0\.0 ms\/s \(vanilla B14 5\.0 us per ad x 0\.7 allow-listed ads per request x 5\.0 B13 calls\/s x 85\.0% answered\)/);
  assert.match(out6, /list kinds per cut: Sim-free rows kept 6\.0, Sim marks 10\.0 \(types 9 Sim-free of 12\); unplaced touches 0\.8, lot-vector prints 20\.0 \(10\.0 us\), their marks 0\.1; resyncs 1 touch list \/ 0 map full, map rows 9000, compactions 0; tiers kept 60\.0 Sims; shared cuts 20, 1\.0 requests per cut \(max 3\)/);
  const z8 = Object.fromEntries(['First', 'New', 'Lot', 'Room', 'Gone', 'Lost', 'Lite', 'Kid'].flatMap(n => [['mark' + n, 0], ['row' + n, 0]]));
  const z9 = Object.fromEntries(['4', '5', '6', '0B', '0C', '0E', '12', '18', '21'].map(n => ['markEv' + n, 0]));
  const v12 = { before: v11.before, after: { ...v11.after, autshadow: { ...v11.after.autshadow, version: 12, ...z8, ...z9, markFirst: 1000, rowFirst: 500,
    markEv0E: 1000, seedLots: 30, seedObjs: 4000, seedUs: 2500, seedFull: 0, liteSuspect: 3, rowHot: 200, rowColdSkips: 50_000, rowColdReads: 40 } } };
  const out7 = costLines({ tag: 'v12', minutes: 10, native: v12, fix }).join('\n');
  const v13 = { before: v12.before, after: { ...v12.after, autshadow: { ...v12.after.autshadow, version: 13, clsAsked: 50, clsSet: 45, clsNone: 5,
    typedMarks: 3000, typedKept: 2500, typedAuditN: 200, typedAuditMis: 0, liteTyped: 400, liteTypedNone: 300, clsUs: 10_000 } } };
  const out8 = costLines({ tag: 'v13', minutes: 10, native: v13, fix }).join('\n');
  const v14 = { before: v13.before, after: { ...v13.after, autshadow: { ...v13.after.autshadow, version: 14, runSkip: 1, runN: 1200, runSkips: 400_000,
    rowSkips: 1_500_000, runBuilds: 70, icStampMoves: 9, pubDelta: 430, pubRows: 1_000_000, runCheckMis: 0,
    passPairs: 5000, classPairs: 20_000, freshReqs: 50, freshPairs: 1000, freshDiff: 30, freshTop1: 2, freshTop10: 5, freshUs: 250_000, freshEvery: 8, partialKept: 12, keepPartial: 1 } } };
  assert.match(costLines({ tag: 'v14', minutes: 10, native: v14, fix }).join('\n'),
    /run skip \(RunSkip 1\) per cut: 800\.0 runs \/ 3000\.0 rows skipped of 1200 runs; rebuilds 70, IC stamp moves 9; delta publishes 430, 2000\.0 rows copied/);
  assert.match(costLines({ tag: 'v14', minutes: 10, native: v14, fix }).join('\n'),
    /consume side \[measured\]: pass share 25\.0% \(5000 of 20000 classified pairs pass Test\); fresh re-score \(FreshEvery 8\): 50 searches, 1000 pairs, 3\.0% differ, top pick changes 4\.0%, top-10 changes 10\.0%, 5\.0 ms each \(test cost\); KeepPartial 1: 12 overrun searches kept/);
  // docs/206 §7 Satb probe: 100 spans of 4 ms, 300 stores / 100 words / 2,000 loads / 0.1 events a span, 5 cuts/s
  const v15 = { before: v14.before, after: { ...v14.after, autshadow: { ...v14.after.autshadow, version: 15, satbMode: 1, satbJobSpans: 100,
    satbJobStores: 30_000, satbJobWords: 10_000, satbJobPages: 2_000, satbJobLoads: 200_000, satbJobIc: 10, satbJobUs: 400_000, satbJobMaxWords: 900,
    satbSpans: 100, satbStores: 90_000, satbWords: 25_000, satbIcCalls: 5000, satbIcCallsAll: 300, satbIcShared: 10, satbRtCalls: 40, satbRtWaits: 0,
    satbBig: 0, satbOverflow: 0, metaTicks: 3000 } } };
  const out15 = costLines({ tag: 'v15', minutes: 10, native: v15, fix }).join('\n');
  assert.match(out15, /Satb probe per job span \(4\.0 ms, 100 spans\) \[measured\]: 300\.0 sim stores, 100\.0 words first written \(max 900, 0\.0 MB log\), 20\.0 pages, 2000\.0 sim loads, 0\.1 unlogged events/);
  assert.match(out15, /Satb barrier at go-live \[inferred\]: 5\.0 jobs\/s x \(19\.8 us barrier: .* \+ waits 0\.2 ms\) = 1\.1 ms\/s: PASS/);
  // v16 (docs/206 §9, docs/122 §14.23): IC arguments logged, runtime waits by site, answered-only / reference tops, C2 pair timer
  const v16 = { before: v15.before, after: { ...v15.after, autshadow: { ...v15.after.autshadow, version: 16, satbIcLogged: 900, satbIcLogWords: 4000,
    satbJobIcLogged: 300, satbRtFset: 2, satbRtPtrStruct: 0, freshTop1Ans: 1, freshTop10Ans: 3, refFreshTop1: 2, refFreshTop10: 4, refPassA4: 15,
    pairT: [200_000, 30_000, 50_000, 10_000], pairN: [1000, 1000, 500, 500] } } };
  const out16 = costLines({ tag: 'v16', minutes: 10, native: v16, fix }).join('\n');
  assert.match(out16, /v16: IC arguments logged 900 \(4000 words, 3\.0 a job span\), not logged 10; runtime calls MonoField\.SetValue 2, PtrToStructure\(object\) 0 \(0\.36\.136\+/);
  assert.match(out16, /over pairs both answered top pick changes 2\.0%, top-10 set changes 6\.0%; cut-time reference vs fresh \(all pairs, staleness\) top pick 4\.0%, top-10 set 8\.0%; reference passes among worker-A4 pairs 25\.0% \(15 of 60\)/);
  assert.match(out16, /game C2 per pair \(sampled searches\) \[measured\]: allow-listed pass 20\.0 us \(1000\), fail 3\.0 us \(1000\); off-list pass 10\.0 us \(500\), fail 2\.0 us \(500\)/);
  assert.match(out16, /pair cost 20\.0 us \[measured C2\]/);
  const v17 = { before: v16.before, after: { ...v16.after, autshadow: { ...v16.after.autshadow, version: 17, transOn: 1, transObjs: 2000, hcOn: 1,
    hcKnown: 900, hcNoHandle: 100, hcMap2: 0, hcUs: 4000, lwOn: 1, lwRows: 3000, lwWalks: 1000, lwWalkUs: 30_000, lwHostUs: 20_000, lwFall: 0,
    lwChecks: 375, lwMis: 0, runNoPlain: 100, runNoStale: 200, runNoWalked: 50, runNoSets: 300, runNoCold: 10, clsGoneSkip: 77 } } };
  assert.match(costLines({ tag: 'v17', minutes: 10, native: v17, fix }).join('\n'),
    /mono-cost15 per cut \[measured\]: Transient 1: 4\.0 objects .*HostCls 1: 900 classified by the host filter, 100 no handle, 0 second map only \(8\.0 us\); ListWalk 1: 6\.0 rows from 2\.0 lot walks \(walk 60\.0 us, host 40\.0 us\), 0 to the body, checks 375, misses 0 .*not plain 0\.2, stale 0\.4, new 0\.1, per-cut sets 0\.6, cold gate 0\.0; class asks skipped 77/);
  const v18 = { before: v17.before, after: { ...v17.after, autshadow: { ...v17.after.autshadow, version: 18, seOn: 1, seKept: 4000, seChecks: 500,
    seMis: 0, seIcOff: 0, tmOn: 1, tmKept: 1000, laEvery: 2, laSkips: 250, gdOn: 1, gdRecs: 300, gdNoRec: 20, gdRows: 600, gdFall: 40,
    gdIncons: 0, gdChecks: 75, gdMis: 0, gdOver: 0 } } };
  assert.match(costLines({ tag: 'v18', minutes: 10, native: v18, fix }).join('\n'),
    /mono-cost16 per cut \[measured\]: SimEvRows 1: 8\.0 Sim rows kept by events, checks 500, misses 0 \(must be 0; ICs off 0x0\); TierMoveLot 1: 2\.0 re-classifications skipped \(same lot and room\); ListAuditEvery 2: 250 dirty cuts without audit; GoneDelta 1: 1\.2 rows by delta, 0\.1 to the body, records 300 \(20 lots without, 0 lost\), inconsistent 0, checks 75, misses 0 \(must be 0\)/);
  const v19 = { before: v18.before, after: { ...v18.after, autshadow: { ...v18.after.autshadow, version: 19, idOn: 1, idSame: 2000, idApp: 500,
    idCalls: 1500, idFall: 250, idChecks: 300, idMis: 0, idUs: 5000, seLearnOn: 1, seSeatOn: 1, seLearnKept: 10_000, seSeatKept: 2000,
    tcMax: 16, tcCapped: 100, gdWhy: [10, 20, 30, 0] } } };
  assert.match(costLines({ tag: 'v19', minutes: 10, native: v19, fix }).join('\n'),
    /mono-cost17 per cut \[measured\]: IdsDelta 1: 4\.0 rows same ids, 1\.0 rows plus new objects \(3\.0 small host calls\), 0\.5 to the host, 10\.0 us; checks 300, misses 0 \(must be 0\); SimEvLearn 1 \/ SimEvSeated 1: 20\.0 \/ 4\.0 rows kept; TierClassMax 16: 0\.2 classifications deferred; gone marks without a record: none 10, other lot 20, other room 30, full 0/);
  const v13w = { before: v13.before, after: { ...v13.after, autshadow: { ...v13.after.autshadow, windowUs: 4_000 } } };
  assert.match(costLines({ tag: 'v13w', minutes: 10, native: v13w, fix }).join('\n'),
    /worker window \[measured\]: quiescent gap .* 10\.0 us vs 3\.0 ms a job.*overruns 40 of 400 \(10\.0%\) at the test wait; .*sim waits 3\.0 ms a request x 5\.0\/s = 15\.0 ms\/s: net /);
  assert.match(out8, /typed marks per cut: 6\.0 marks, 5\.0 list rows kept; classified 45 of 50 asked \(5 no object, 20\.0 us per request on the sim thread\); typed audits 200, misses 0 \(must be 0\); lot-vector prints typed 0\.8 \(0\.6 no list type\)/);
  assert.match(out7, /dirtying per cut, lots marked \/ list rows refreshed by cause: first 2\.0\/1\.0, new 0\.0\/0\.0, lot 0\.0\/0\.0, .*kid 0\.0\/0\.0; marks by event: 0x4 0\.0, .*0x0E 2\.0, .*; seeding: 30 lots, 4000 objects \(5\.0 us per cut\), 0 in part; lot-vector suspects 3; RowHot 200: 100\.0 rows cold per cut, 40 cold reads/);
  assert.match(out6, /go-live estimate \[inferred\]: scoring moved 25\.5 ms\/s \(6\.0 ms per request x 5\.0 B13 calls\/s x 85\.0% answered\) - cut 2\.0 ms\/s \(1\.7 cuts\/s x 1\.2 ms\) - guard 1\.7 ms\/s = net 21\.8 ms\/s; cut = 8% of the scoring moved \(gate <= 25%: PASS\)/);
  // consume: P = 360 / 500 = 0.72, overrun 40 / 500 = 8%; base 0.92 x 25.5 - 2.0 - 1.7 - 5 x 0.15 = 19.0; p = 0.25: a pair 6 / 0.72 x 1.9 =
  // 15.83 ms, 0.153 selected, consume 5 x 0.92 x 0.153 x 15.83 = 11.1, net 7.9; break-even N 19.0 / (5 x 0.92 x 15.83) = 0.3
  assert.match(out6, /go-live after consume \[inferred\]: overrun 8\.0% of searches, before consume 19\.0 ms\/s; live C1, top 10 \+ bands, pass share 0\.25\/0\.5\/0\.75\/1 \(no pass counter\), pair cost R model: net 7\.9\/.* ms\/s with band 1 only, 7\.9\/.* with every band; break-even N 0\.3\/.* \(0\.7 allow-listed pairs per search\)/);
  // Mode 3 VerifyEvery: only 50 of the 500 requests ran the reference (0.3 s = 6 ms each): same per-request cost, same go-live lines
  const v11r = { before: { ...v11.before, autshadow: { ...(v11.before as any).autshadow, refRuns: 0 } },
    after: { ...v11.after, autshadow: { ...v11.after.autshadow, refUs: 300_000, refRuns: 50 } } };
  const out6r = costLines({ tag: 'v11r', minutes: 10, native: v11r, fix }).join('\n');
  // A0-A5 are counted only on reference runs (CompareR, AutShadow.cs:1019/1159), so pairs per request divide by refRuns: 360 / 50 = 7.2
  assert.equal(out6r.match(/- go-live estimate .*/)?.[0], out6.match(/- go-live estimate .*/)?.[0]);
  assert.match(out6r, /- go-live after consume .*\(7\.2 allow-listed pairs per search\)/);
  // pass counter 180 of 300 answered (400 - 40 A5 - 60 A4) = 0.6 (docs/122 §14.23: A4 leaves the denominator instead of being
  // discounted twice): a pair 6 / 0.72 x (0.7 + 0.3 / 0.6) = 10.0 ms, 0.6 x 0.72 x 0.85 = 0.367 selected, consume
  // 5 x 0.92 x 0.367 x 10.0 = 16.9, net 2.1; break-even 19.0 / 46.0 = 0.4
  const outP = costLines({ tag: 'v11p', minutes: 10, native: v11, fix: { ...fix, autshadow_pass: { before: 0, after: 180 } } }).join('\n');
  assert.match(outP, /pass share 0\.60 of answered \[measured\], pair cost R model: net 2\.1 ms\/s with band 1 only, 2\.1 with every band; break-even N 0\.4 /);
  assert.match(out5, /dirty lots \(CutLean=2\) per cut: rows kept 16\.0, refreshed 4\.0, lots marked 2\.0, objects placed 80\.0 \(40\.0 us\); resync cuts 3; list audit 1\.0 rows \(40\.0 us\), misses 0, tripped 0; tier cache: classified 100\.0 Sims, camera moves 7, holds 12; list tiers: 1\.2 rows waiting, 0\.2 lot refreshes, tier-1 lots 5/);
  assert.match(out4, /lists \(CutLean\) per cut: kept by print 8\.0, prints 4\.0 \(20\.0 us\), refreshed same 0\.2; Sim tiers \(TierMs 1500\) per cut: tier 1 10\.0 Sims, body 40\.0 \(120\.0 us\); tier 2 6\.0 Sims, 4\.0 waiting \(stale\), body 8\.0 \(24\.0 us\); classify 16\.0 us; aging runs 62/);
  const out2 = costLines({ tag: 'z', minutes: 10, native: busy, fix }).join('\n');
  assert.match(out2, /GC lock uncontended entries: 580\.0k\/s x 68 ns = 39\.4 ms\/s \[inferred.*lazy gate off/);
  assert.match(out2, /guard thunks on the sim: 100\.0k calls\/s x 17 ns = 1\.7 ms\/s \[inferred/);
  assert.match(out2, /window waits 40, 0\.2 ms\/s/);
  assert.match(out2, /worker aborts by reason: row 900 \(90\.0%\), other 100 \(10\.0%\)/);
  assert.match(out2, /answer table: 3000 rows .*max 8192 rows, memo 2048, evicted 500 \(memo 100\)/);
  assert.match(out2, /worker adds 46\.1 ms\/s .*: 5\.0 \[measured.* \+ 41\.1 \[inferred/);
  // newest row per job id across families; a rerun replaces its job; --tag keeps one family; rows without a profile skipped
  const rows = [{ queue: 'mono-cost3', job: 'ip-r', profileTag: 'a', time: '2026-10-29T01:00:00Z' }, { queue: 'mono-cost4', job: 'ip-r', profileTag: 'b', time: '2026-10-29T02:00:00Z' },
    { queue: 'mono-cost4', job: 'ip-r-rerun', rerunOf: 'ip-r', profileTag: 'c', time: '2026-10-29T03:00:00Z' }, { queue: 'mono-cost', job: 'bp-gates', profileTag: 'd', time: '2026-10-29T00:00:00Z' },
    { queue: 'mono-cost4', job: 'x', time: '2026-10-29T04:00:00Z' }];
  assert.deepEqual(newestPerJob(rows).map(r => r.profileTag), ['d', 'c']);
  assert.deepEqual(newestPerJob(rows, 'mono-cost3').map(r => r.profileTag), ['a']);
  assert.ok(FAMILY_RE.test('mono-cost-2026-10-29.jsonl') && FAMILY_RE.test('mono-cost12-2026-11-01.jsonl') && !FAMILY_RE.test('mono-cost4-2026-10-29-ip-r-native.json') && !FAMILY_RE.test('mono-ledger-2026-10-29.jsonl'));
  assert.ok(FAMILY_RE.exec('satb-s0b-2026-10-31.jsonl')?.[1] === 'satb-s0b' && FAMILY_RE.exec('satb-s0-2026-10-30.jsonl')?.[1] === 'satb-s0');
  assert.ok(FAMILY_RE.exec('golive-g7-2026-10-31.jsonl')?.[1] === 'golive-g7');
  {  // docs/124 §17 go-live lines from the v20 al[] slots (deltas after - before)
    const al0 = Array(56).fill(0), al1 = Array(56).fill(0);
    Object.assign(al1, { [AL.eligible]: 100, [AL.posted]: 90, [AL.dry]: 80, [AL.wait]: 4, [AL.epoch]: 6, [AL.objConsumed]: 30, [AL.stale]: 10,
      [AL.dryTop10]: 79, [AL.dryTop1]: 80, [AL.dryTop1Tie]: 3, [AL.c2Rehomed]: 7, [AL.dryMissBand]: 1, [AL.waitTicks]: 180 });
    const nat = { before: { autshadow: { version: 20, alOn: 3, al: al0 } }, after: { autshadow: { version: 20, alOn: 3, al: al1, alVerify: 2 } } };
    const lv = liveLines(nat, 600).join('\n');
    assert.match(lv, /go-live \(Dry, G7\) \[measured\]: 100 eligible searches .* used 80 \(88\.9%\); fell back 10: not ready in MaxWait 4/);
    assert.match(lv, /wait 2\.0 ticks a search/);
    assert.match(lv, /consumed 75\.0%/);
    assert.match(lv, /G7 dry apply \[measured\]: 80 searches compared \(not compared 0: [^)]*\): top-10 set equal 98\.8%, top 1 equal 100\.0% \(up to ties at the top score; 3 equal only by a tie\)/);
    assert.match(lv, /replay mismatches 0 \(must be 0; C2 calls kept across a B13 yield 7\)/);
    assert.match(lv, /ranked below the bands 1, other 0; our top-10 entries the game's queue lacks 0; off-list/);
    // searches never compared leave the rate denominator; slot 2 is not "consumed" in Dry; unequal with no cause is flagged
    Object.assign(al1, { [AL.dryNoCmp]: 5, [AL.dryExtra]: 2, [AL.dryTop10]: 74, [AL.dryTop1]: 75 });
    const lv2 = liveLines(nat, 600).join('\n');
    assert.match(lv2, /used 80 \(88\.9%\)/);
    assert.match(lv2, /75 searches compared \(not compared 5: .*\): top-10 set equal 98\.7%, top 1 equal 100\.0%/);
    assert.match(lv2, /lacks 2; off-list/);
    Object.assign(al1, { [AL.dryNoCmp]: 0, [AL.dryExtra]: 0, [AL.dryMissBand]: 0, [AL.dryTop10]: 70, [AL.dryTop1]: 70 });
    assert.match(liveLines(nat, 600).join('\n'), /lacks 0; 10 unequal with no counted cause/);
    assert.match(liveLines({ after: { autshadow: { version: 20 } } }, 600).join(''), /go-live: n\/a/);
    // §17.10 cost guard line: acting only, deltas for windows/skips/skipped, the ratio gauge as is
    const act = { before: { autshadow: { version: 20, alOn: 1, al: al0 } }, after: { autshadow: { version: 20, alOn: 1, al: al1 } } };
    const gfix = { autliveguard_windows: { before: 2, after: 12 }, autliveguard_skipwindows: { before: 0, after: 3 },
      autliveguard_skipped: { before: 0, after: 25 }, autliveguard_ratio_pm: { before: -1, after: 1250 } };
    assert.match(liveLines(act, 600, gfix).join('\n'), /go-live cost guard \[measured\]: 10 windows, 3 skip windows, 25 eligible searches skipped \(25\.0% of eligible\); last window cost = 1250 per mille of the scoring saved \(above break-even\)/);
    assert.match(liveLines(act, 600, { ...gfix, autliveguard_ratio_pm: { after: -1 } }).join('\n'), /last window cost = n\/a \(no reference yet\)/);
    assert.doesNotMatch(liveLines(act, 600).join('\n'), /cost guard/);
    // docs/222 unseen-direct lines
    const uz = (after: number) => ({ before: 0, after });
    const ufix = { autunseen_posted: uz(80), autunseen_seen_post: uz(20), autunseen_direct: uz(70), autunseen_snap_resume: uz(5),
      autunseen_snap_slice: uz(2), autunseen_dropped: uz(5), autunseen_obj_game: uz(30), autunseen_pairs: uz(700), autunseen_offlist: uz(140),
      autunseen_test_pass: uz(60), autunseen_test_fail: uz(20), autunseen_test_us: uz(8000), autunseen_vanilla: uz(4), autunseen_nopick: uz(6),
      autunseen_tagged: uz(50), autunseen_flip_kept: uz(3), autunseen_flip_cancel: uz(1), autunseen_exc: uz(0), autunseen_cost_us: uz(600_000),
      autunseen_saved_us: uz(1_800_000), autlive_b14_us: uz(50_000), autlive_b14_ads: uz(10_000), autunseen_guard_windows: uz(4),
      autunseen_guard_skipwindows: uz(0), autunseen_guard_skipped: uz(0), autunseen_guard_ratio_pm: { before: -1, after: 333 } };
    const uo = liveLines(act, 600, ufix).join('\n');
    assert.match(uo, /unseen-direct \(autunseen, docs\/222\) \[measured\]: eligible searches seen at the post \(exact path\) 20, unseen posted 80 \(unseen share 80\.0%\); direct 70, .*ads inserted 700 \(10\.0 a search\).*D2 live Test pass 60 \/ fail 20 \(25\.0% fail, 100\.0 us a Test\)/);
    assert.match(uo, /unseen-direct cost \[measured\]: own sim cost 1\.0 ms\/s .* saved 3\.0 ms\/s \(game B14 5\.0 us per ad x consumed ads\) = net 2\.0 ms\/s .*last window 333 per mille/);
    assert.doesNotMatch(liveLines(act, 600, gfix).join('\n'), /unseen-direct/);
    assert.doesNotMatch(uo, /pacing/);
    const po = liveLines(act, 600, { ...ufix, autunseen_pace_us: uz(35_000), autunseen_pace_forced: uz(14) }).join('\n');
    assert.match(po, /unseen-direct pacing \(autunseenpace, docs\/222 §8\) \[measured\]: 35\.0 ms charged to the yield timer \(500\.0 us a direct search\), yields brought forward 14 \(0\.2 a direct search\)$/m);
    assert.match(liveLines(act, 600, { ...ufix, autunseen_pace_us: uz(0), autunseen_pace_forced: uz(0) }).join('\n'), /0 = switch off/);
    assert.match(unseenLines({ autunseen_posted: uz(0), autunseen_dry_runs: uz(40), autunseen_dry_top10: uz(40), autunseen_dry_top1: uz(39), autunseen_dry_seen_runs: uz(10) }, 600, true).join('\n'),
      /unseen actors 40 searches compared, top-10 set equal 100\.0%, top 1 equal 97\.5%; seen actors 10 searches; unseen share of compared 80\.0%/);
    assert.doesNotMatch(liveLines(nat, 600, gfix).join('\n'), /cost guard/);
    // docs/217 L1 late line and docs/124 §8 G8 line from ts3fixes counters (deltas); absent = no line
    const z = (after: number) => ({ before: 0, after });
    const lfix = { autlivelate_slices: z(40), autlivelate_ref_age: z(5), autlivelate_ref_epoch: z(3), autlivelate_consumed: z(90),
      autlivelate_stale: z(2), autlivelate_age1: z(60), autlivelate_age2: z(25), autlivelate_age3: z(5), autlivelate_us_late_game: z(6000),
      g8_pushes: z(120), g8_simhours_x100: z(4000), g8_null_picks: z(7), g8_gohome: z(3), g8_lat0: z(10), g8_lat1: z(20), g8_lat2: z(50), g8_lat3: z(5), g8_lat4: z(1) };
    const ll = liveLines(act, 600, lfix).join('\n');
    assert.match(ll, /40 later slices checked, refused: age 5, epoch 3, state 0/);
    assert.match(ll, /late objects consumed 90 .*age 1\/2\/3\+ ticks 60\/25\/5/);
    assert.match(ll, /120 autonomous pushes over 40\.0 Sim-hours \(3\.0 per Sim-hour\), no-pick searches 7, MakeSimGoHome fallbacks 3; .* 10\/20\/50\/5\/1, median 2 ticks/);
    assert.doesNotMatch(liveLines(act, 600, gfix).join('\n'), /late consume|G8 behaviour/);
    assert.doesNotMatch(ll, /G8 cadence/);
    const cfix = { ...lfix, g8_unseen_searches: z(300), g8_unseen_pushes: z(240), g8_unseen_null_picks: z(30), g8_unseen_gohome: z(12),
      g8_seen_searches: z(100), g8_seen_pushes: z(90), g8_seen_null_picks: z(5), g8_seen_gohome: z(1), g8_unseen_simhours_x100: z(3000) };
    const cl = liveLines(act, 600, cfix).join('\n');
    assert.match(cl, /G8 per search by actor class .*: unseen 300 searches: no-pick share 10\.0%, GoHome per no-pick 0\.400, pushes per search 0\.800; seen 100 searches: no-pick share 5\.0%, GoHome per no-pick 0\.200, pushes per search 0\.900/);
    assert.match(cl, /G8 cadence \[measured\]: searches per Sim-hour all 10\.0 \(40\.0 Sim-hours\), unseen 10\.0 \(30\.0 unseen Sim-hours\), seen 10\.0 \(10\.0;/);
    // per-search feature mask line: deltas per bit, bits with nothing left out, absent counters = no line
    const ffix: any = {};
    for (const b of FEAT_BITS) for (const k of ['searches', 'mis', 'drop']) ffix[`feat_${b}_${k}`] = z(0);
    ffix.feat_rain_searches = z(200); ffix.feat_rain_mis = z(3); ffix.feat_rain_drop = z(1); ffix.feat_satb_searches = { before: 10, after: 410 };
    const fl = liveLines(act, 600, ffix).join('\n');
    assert.match(fl, /feature mask \[measured\]: rain 200 searches \/ 3 mismatches \(1\.5%\) \/ 1 give-ups; satb 400 searches \/ 0 mismatches \(0\.0%\) \/ 0 give-ups \(/);
    assert.doesNotMatch(fl, /late \d+ searches/);
    assert.equal(featLine({}), null);
    assert.doesNotMatch(liveLines(act, 600, gfix).join('\n'), /feature mask/);
  }
  // absent counters print n/a, never a number; no worker = no stop-rule line
  const bare = costLines({ tag: 'x', minutes: 10, budgetWhy: 'no x-<secs>-seconds.csv' }).join('\n');
  assert.match(bare, /Mono interpreter: n\/a; GC: n\/a/); assert.match(bare, /\[MonoThread\]: n\/a/); assert.match(bare, /\[AutShadow\]: n\/a/);
  assert.doesNotMatch(bare, /worker adds/);
  const count = costLines({ tag: 'y', minutes: 10, native: { before: native.before, after: { autshadow: { ...native.after.autshadow, mode: 0 } } } }).join('\n');
  assert.match(count, /Mode R match rate: n\/a/); assert.match(count, /compared n\/a/); assert.doesNotMatch(count, /worker adds/);
  console.log(out);
  console.log('self-check ok');
}

if (resolve(process.argv[1] ?? '') === fileURLToPath(import.meta.url)) {
  const args = process.argv.slice(2);
  if (args.includes('--self-check')) selfCheck();
  else {
    const mi = args.indexOf('--minutes'), minutes = mi >= 0 ? Number(args[mi + 1]) : 10;
    const ti = args.indexOf('--tag'), family = ti >= 0 ? args[ti + 1] : undefined;
    const files = args.filter((a, k) => !a.startsWith('--') && !(mi >= 0 && k === mi + 1) && !(ti >= 0 && k === ti + 1));
    let ins: Input[];
    if (files.length) ins = files.flatMap(a => inputs(a, minutes));
    else {
      const q = existsSync(QUEUE) ? readdirSync(QUEUE).filter(f => FAMILY_RE.test(f) && (!family || FAMILY_RE.exec(f)![1] === family)).map(f => join(QUEUE, f)) : [];
      if (!q.length) { console.error(`no ${family ?? 'mono-cost*'}-<date>.jsonl in ${QUEUE} (run a tools/harness/queues/mono-cost*.json, or pass a jsonl or tags)`); process.exit(2); }
      ins = newestPerJob(q.flatMap(jsonl), family).map(r => rowInput(r, minutes));
    }
    console.log(ins.map(i => costLines(i).join('\n')).join('\n\n'));
  }
}
