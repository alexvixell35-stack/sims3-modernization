// Every fix switch has a forced repro (owner rule: every fix has a repro, docs/169/170). Checks:
// (1) each ts3fixes switch (the `string[] names` list in mods/ts3agent/src/plugin/Handlers/PatchHandlers.cs) and each
//     ts3native switch (tools/harness/native-switches.ts nativeNames + counterSwitches) is exercised by at least one repro:
//     in tools/harness/repros.json a `patches` / `native_set` step arg (plain or loop steps; cleanup does not count) with
//     value "$on", true or a non-zero number, or the repro's `switches` list (fixes the repro runs without a step: on by
//     default or by the job ini, e.g. gohome); or `fixes` / `native` of a tools/harness/feature-repros.json feature that
//     has a repro; or ab / fixesOn of a queue job (tools/harness/queues/*.json) that runs a repro (repro / repros field).
//     Else it must be in EXEMPT below with a reason. A switch only ever set off (a precondition) does not count.
// (2) no repro referenced by a queue (tools/harness/queues/*.json "repro" / "repros") still holds a `<...>` placeholder.
// (3) EXEMPT names exist (no stale entries) and are not also covered (drop the exemption once a repro lands).
//   node tools/harness/fix-repro-check.ts               check the repo; exit 1 on a gap
//   node tools/harness/fix-repro-check.ts --self-check  fixtures, then the repo check
import assert from 'node:assert/strict';
import { readdirSync, readFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { counterSwitches, nativeNames } from './native-switches.ts';

const ROOT = resolve(import.meta.dirname, '..', '..');
const read = (p: string) => readFileSync(join(ROOT, p), 'utf8');

const DIAG = 'diagnostic switch (verify / probe / fake upstream): a test aid, not a fix';
const SHADOW = 'diagnostic: shadow mode of a read-only fix (runs both answers and compares); the fix itself has repros';
const OFFLOAD = 'sim-thread offload on the [AutShadow] worker (docs/208): checked by Mode R A0-A5 and the dry checks in offload3-moder.json / offload3-act.json / offload-ranks.json, not a hitch repro';
const MONO ='Mono offload, parked: no Mono game runs allowed now (memory mono-parked, docs/118)';
// Switches with no repro on purpose. Dead ends: "dead end: <evidence>"; diagnostics; parked work.
export const EXEMPT: Record<string, string> = {
  unseenfbteleport: 'trip-shortcut type: verified by verify-shortcut.ts --name unseenfbteleport and the unseen-fallback.json sv-ufb-teleport-verify job (fixesRequire), not by an A/B repro (shortcut-verification rule, docs/202 §6)',
  unseenanim: 'native [UnseenAnim] (UnseenAnim.Mode=1 ini or --ab unseenanim), not a fixesOn name: correctness by the unseen-anim-ab.json bp-ua-snapback job (repro unseen-snapback, ini on) and the stuck pair, gain by its in-load A/Bs (docs/144 §3.4)',
  unseenrm: 'native [UnseenRootMotion] Shortlist=1 Mode=1 (--ab unseenrm, rmMode), not a fixesOn name: correctness by unseen-root-motion.json bp-urm-snapback (repro unseen-snapback) and bp-urm-stuck-on, gain by its in-load A/Bs on rmWalkUs / aaPassUs (docs/228)',
  unseenobj: 'native [UnseenAnim] Objects=1 (--ab unseenobj, aoMode), not a fixesOn name: correctness by unseen-anim-objects.json bp-uao-snapback (repro unseen-snapback, objects in use keep animating) and bp-uao-stuck-on, gain by its in-load A/Bs on aoKindUs/aaUs_2 (docs/144 §3.6)',
  unseentable: 'native [UnseenTable] (UnseenTable.Enabled=1 ini, --ab unseentable), a shared classifier, not a hitch fix: correctness by unseen-table.json bp-utb-check / ip-utb-check (Check=1 mismatch counters, unseen-snapback repro), cost by its in-load A/Bs (docs/227 §6)',
  unseentablettl: 'native [UnseenTable] RefreshTicks arm (--ab unseentablettl, utbTtlMode), a cost knob of the shared classifier: correctness by unseen-table.json bp-utb-ttl-check (Check=1, unseen-snapback repro), cost by bp-utb-ttl-ab (docs/227 §8)',
  unseentablecopy: 'ts3fixes UnseenTable.cs copy path (docs/227 §9), the A/B baseline of the zero-copy Bits(id) reads, default off: correctness by unseen-table.json bp-utb2-check (zero-copy default, repro unseen-snapback; the copy path is the §6 path bp-utb-check tested), cost by bp-utb2-copy-ab (--ab unseentablecopy)',
  unseenghost: 'native [UnseenGhost] ini section (UnseenGhost.Enabled=1), not an ab/fixesOn name: turned on in every unseen-route-relaxed.json / unseen-route-long-ab.json job (docs/202 §3.9.5); acts only for unseenroute holds, which have repros',
  autshadowverify: DIAG, autunseencmp: DIAG, autseengame: DIAG, autunseenpacecredit: DIAG, ownjobwait: DIAG, gcgrowlog: DIAG, autledger: MONO,
  autofftick: OFFLOAD,
  jit: 'Mono speed, not a hitch fix: no repro makes script calls more frequent than plain play; correctness by jit-verify.json (Mode=2) and the selftest TestMonoJit, timing by jit-ab.json once jit-verify passed (docs/527 §5)',
  mintcall: 'Mono speed, not a hitch fix: no repro makes script calls more frequent than plain play; correctness by mintcall-verify.json (CallPath=2) and the monohost selftests, timing by the plain-play pairs in mintcall-ab.json (docs/139 §8)',
  workerwait: 'worker CPU, not a hitch fix: no repro makes the job-start wait more likely than plain play; measured by the off/on plain-play pairs in route-worker-wait.json, correctness by the selftest TestRouteWorkerWait (docs/203 §6)',
  steershadow: SHADOW, lotfdshadow: SHADOW, obstructedshadow: SHADOW, slotenabledshadow: SHADOW, avoidshadow: SHADOW,
  gate0shadow: SHADOW, routetoobjroshadow: SHADOW, placementroshadow: SHADOW, goalvalidroshadow: SHADOW, idlecheckroshadow: SHADOW, idlecheckcshadow: SHADOW,
  routesnapgate: 'dead end for now: P5b ReleaseGates (lends +0x918 to gate reads), with routesnaprel in the P5 path dropped from v0.1.5 and parked (docs/136 §3.8, docs/94 §22.10)',
  spatialcache: 'dead end: kSpatialCache A/B 25.7 vs 27.4 it/s, hitches 5.55 vs 4.75 (2026-10-13 +5% worse), PLAN.md results table',
  stuckcheckbusy: 'dead end: kRoutableCache + kStuckCheckBusy A/B 38.0 vs 44.3 it/s, 0 busy skipped, FAIL (PLAN.md results table); superseded by stuckcheckasync',
  routebudget: 'dead end: [RouteBudget] built 0.4.0, never verified in game ("OFF until verified", ts3native.ini); per-plan caps live on as [RouteAsync] SyncCapMs / PlanDeadlineMs (docs/134, docs/164 §7)',
  fprebuildlock: 'dead end: [FpRebuildLock] "No gain: the same wait, earlier" (ts3native.ini, docs/84 §4.4)',
  asyncreplan: 'dead end: [RouteAsync] Kinds=3 (A10) keyed waits +7.4 ms/s, "Decision: drop" (docs/136 §3.6)',
  locowin: 'dead end: [LocoWindow] no-op, "Decision: drop" (docs/136 §3.7); same field +0x13E8 as h2, which has repros',
  routesnaprel: 'dead end for now: P5b lock lending, part of the P5 snapshot path dropped from v0.1.5 and parked (docs/136 §3.8, docs/94 §22.9)',
  routesnapfree: 'dead end for now: P5c, keyed waits +5.2 BP / +13.8 IP ms/s, "drop from v0.1.5; P5c stays parked" (docs/136 §3.8)',
  cachedreflection: 'dead end: never installs in game ("PersistedClass.Init: already compiled", save matrix 2026-10-20/22, PLAN.md v0.1.4 triage); off as shipped',
  typegrowth: DIAG,
  sqrtheap: 'experiment, not a fix yet: heap policy compared by separate loads (observe vs act, queues/mem-exp.json, docs/199); allocation in natural play drives it, a repro gets added if it becomes a fix',
  doormirror: 'dead end as built: never run; door answers went through the P5 host-call proxy instead (docs/94 §19.1, docs/116); only comes back with docs/159 H4',
};

export const fixesNames = (cs: string) => {
  const m = cs.match(/string\[\]\s+names\s*=\s*\{([^}]*)\}/);
  if (!m) throw new Error('PatchHandlers.cs: no `string[] names = { ... }` list');
  return [...m[1].matchAll(/"(\w+)"/g)].map(x => x[1]);
};

const onValue = (v: unknown) => v === '$on' || v === true || (typeof v === 'number' && v !== 0);
// switch names turned on by a patches / native_set step of a repro, or in its switches list
export function coveredByRepros(repros: any[]): Map<string, string[]> {
  const out = new Map<string, string[]>();
  for (const r of repros) {
    const steps = [...(r.steps ?? []), ...(r.loop?.steps ?? [])].filter((s: any) => (s.cmd === 'patches' || s.cmd === 'native_set') && s.args && typeof s.args === 'object');
    const on = steps.flatMap((s: any) => Object.entries(s.args).filter(([, v]) => onValue(v)).map(([k]) => k));
    for (const k of [...(r.switches ?? []), ...on]) out.set(k, [...new Set([...(out.get(k) ?? []), r.id])]);
  }
  return out;
}
export function coveredByFeatures(fr: any): Map<string, string[]> {
  const out = new Map<string, string[]>();
  for (const [id, f] of Object.entries<any>(fr.features ?? {})) if (f.repro?.id) for (const k of [...(f.fixes ?? []), ...(f.native ?? [])]) out.set(k, [...(out.get(k) ?? []), `feature ${f.id ?? id}`]);
  return out;
}
export const placeholders = (r: any) => JSON.stringify([r.steps, r.loop, r.cleanup]).match(/"<[^"<>]+>"/g) ?? [];

export function check(switches: string[], repros: any[], fr: any, queues: unknown[], exempt = EXEMPT): string[] {
  const cov = coveredByRepros(repros), q = fromQueues(queues), queued = q.ids;
  for (const m of [coveredByFeatures(fr), q.cov]) for (const [k, v] of m) cov.set(k, [...(cov.get(k) ?? []), ...v]);
  const errs: string[] = [];
  for (const n of switches) {
    if (!cov.has(n) && !exempt[n]) errs.push(`${n}: no repro turns it on and no exemption`);
    if (cov.has(n) && exempt[n]) errs.push(`${n}: exempt but covered by ${cov.get(n)!.join(', ')}: drop the exemption`);
  }
  for (const n of Object.keys(exempt)) if (!switches.includes(n)) errs.push(`${n}: exemption for an unknown switch`);
  for (const r of repros) if (queued.has(r.id) && placeholders(r).length) errs.push(`${r.id}: queued repro holds placeholders ${placeholders(r).join(' ')}`);
  return errs;
}

// queue jobs: every object with repro / repros names those repro ids; its ab / fixesOn (comma lists) turn switches on for them
export function fromQueues(queues: unknown[]): { ids: Set<string>; cov: Map<string, string[]> } {
  const ids = new Set<string>(), cov = new Map<string, string[]>();
  const walk = (o: any): void => {
    if (Array.isArray(o)) return o.forEach(walk);
    if (!o || typeof o !== 'object') return;
    const rs = [o.repro, ...(Array.isArray(o.repros) ? o.repros : [])].filter((x): x is string => typeof x === 'string');
    rs.forEach(r => ids.add(r));
    if (rs.length) for (const k of ['ab', 'fixesOn']) if (typeof o[k] === 'string')
      for (const n of o[k].split(',').map((s: string) => s.trim())) cov.set(n, [...new Set([...(cov.get(n) ?? []), ...rs.map(r => `queue ${r}`)])]);
    Object.values(o).forEach(walk);
  };
  queues.forEach(walk);
  return { ids, cov };
}
const readQueues = () => { const dir = join(ROOT, 'tools', 'harness', 'queues'); return readdirSync(dir).filter(f => f.endsWith('.json')).map(f => JSON.parse(readFileSync(join(dir, f), 'utf8'))); };

function repoCheck() {
  const fixes = fixesNames(read('mods/ts3agent/src/plugin/Handlers/PatchHandlers.cs'));
  const native = [...new Set([...nativeNames, ...Object.keys(counterSwitches)])];
  const switches = [...new Set([...fixes, ...native])];
  const errs = check(switches, JSON.parse(read('tools/harness/repros.json')), JSON.parse(read('tools/harness/feature-repros.json')), readQueues());
  if (errs.length) { console.error(`fix-repro-check FAILED (${errs.length}):\n  ${errs.join('\n  ')}`); process.exit(1); }
  console.log(`fix-repro-check ok: ${switches.length} switches (${fixes.length} ts3fixes, ${native.length} native), ${Object.keys(EXEMPT).length} exempt`);
}

if (import.meta.filename === resolve(process.argv[1] ?? '')) {
  if (process.argv.includes('--self-check')) {
    assert.deepEqual(fixesNames('x; string[] names = { "a", "b" }; y'), ['a', 'b']);
    const repros = [
      { id: 'r1', steps: [{ cmd: 'patches', args: { a: true, off: false } }], loop: { steps: [{ cmd: 'native_set', args: { n: '$on' } }] } },
      { id: 'r2', loop: { steps: [{ cmd: 'go_to_work', args: { sim: '<child 1>' } }] } },
    ];
    const fr = { features: { f: { repro: { id: 'r1' }, fixes: ['b'] }, g: { needsRepro: 'x', fixes: ['c'] } } };
    assert.deepEqual(check(['a', 'b', 'n'], repros, fr, [], {}), []);
    assert.deepEqual(check(['a', 'off', 'c'], repros, fr, [], {}).map(e => e.split(':')[0]), ['off', 'c'], 'off-only and needsRepro do not count');
    assert.deepEqual(check(['a', 'c'], repros, fr, [], { a: 'x', c: 'y', z: 'w' }).map(e => e.split(':')[0]), ['a', 'z'], 'covered-but-exempt and stale exemption');
    assert.deepEqual(check([], repros, fr, [{ jobs: [{ profile: { repro: 'r2' } }] }], {}), ['r2: queued repro holds placeholders "<child 1>"']);
    assert.deepEqual(check([], repros, fr, [], {}), [], 'unqueued placeholders pass');
    assert.deepEqual(check(['q'], repros, fr, [{ jobs: [{ run: { profile: { repro: 'r1', ab: 'x,q' } } }, { profile: { fixesOn: 'z' } }] }], {}), [], 'queue --ab on a repro job');
    assert.equal(check(['z'], repros, fr, [{ jobs: [{ profile: { fixesOn: 'z' } }] }], {}).length, 1, 'fixesOn without a repro does not count');
    console.log('fix-repro-check self-check ok');
  }
  repoCheck();
}
