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
const DEADSIM = 'opt-in, Dalexlol leak stress (#40, al342-al358)';
// Switches with no repro on purpose. Dead ends: "dead end: <evidence>"; diagnostics; parked work.
export const EXEMPT: Record<string, string> = {
  unseenfbgohere: 'docs/328 §7.5 (2026-11-23): natural-play switch, no forced GoHere visit repro; judged by compat-switchon-1122g-1123 bp-gohere-ab (--ab unseenfbgohere, eligibility log line)',
  spiralshare: 'no forced repro yet: judged by the route storms of docs/686 §8.7 (spiral stair waits of 7-12 sim-min gone); its hooks are the ones sharelanes-check reads',
  spiralsharehh: 'as spiralshare (household scope of the same hooks)',
  lanewaitlog: 'log only (one lanewait: line per wait for a portal lane, docs/686 §8): on in both arms of the route storms of docs/686 §8.7 (storm runner, not a repro)',
  standwaitlog: 'log only (one standwait: line per wait with blocked cycles, docs/686): read in the route storms of docs/686 §8.7; its forced repro standwait-landing-crowd needs agent steps that are not upstream yet',
  makebedbothlog: 'log only (one makebedboth: line per case, docs/686 §13): its forced repro makebedboth-force needs the agent step bed_setup, not upstream yet (results in docs/686 §13.5)',
  unseenoutfitdefer: 'speed change with no bug to trigger (issue #451, docs/586): counters 2570-2588 per game, natural-play compositor and frame times on 4 worlds; the next test is the in-load --ab unseenoutfitdefer on BP',
  cutonwait: 'no forced repro yet: the waits need many townies routing behind each other (DiscDrain, event-9 flush, +0x918); judged by the native selftest (11b, jobyield step 5) and the Isla Paradiso in-load --ab cutonwait pairs, both orders (docs/850 §4)',
  boundcall: 'exact speedup, no hitch to repro (docs/537 §22): the same EA String bodies run, judged by the native selftest, the monohost corpus + c3g replays (MONOHOST_JITBIND=1 MONOHOST_JITBOUNDCALL=3, 0 differ) and boundcall.json (BP/IP correctness, --ab boundcall timing)',
  equalsfast: 'exact speedup, no hitch to repro (docs/258 option C): judged by the native selftest and the monohost exactness corpus (MONOHOST_EQFAST=1, 0 mismatches) and the BP/IP --ab equalsfast timing pair (queues/equalsfast-1112.json)',
  buyiconleak: '2026-11-11 leak fixes (docs/545 §1): off/on in one session by ts3agent phase runs (switch on mid-session via patches, leak_census counters before/after); no harness queue repro yet',
  casrowleak: '2026-11-11 leak fixes (docs/545 §1): off/on in one session by ts3agent phase runs (switch on mid-session via patches, leak_census counters before/after); no harness queue repro yet',
  compositorleak: '2026-11-11 leak fixes (docs/545 §1): off/on in one session by ts3agent phase runs (switch on mid-session via patches, leak_census counters before/after); no harness queue repro yet',
  genealogycycle: '2026-11-11 leak fixes (docs/545 §1): off/on in one session by ts3agent phase runs (switch on mid-session via patches, leak_census counters before/after); no harness queue repro yet',
  spouseunlink: '2026-11-11 leak fixes (docs/545 §1): off/on in one session by ts3agent phase runs (switch on mid-session via patches, leak_census counters before/after); no harness queue repro yet',
  relremovesafe: '2026-11-11 leak fixes (docs/545 §1): off/on in one session by ts3agent phase runs (switch on mid-session via patches, leak_census counters before/after); no harness queue repro yet',
  familyinvghost: '2026-11-11 leak fixes (docs/545 §1): off/on in one session by ts3agent phase runs (switch on mid-session via patches, leak_census counters before/after); no harness queue repro yet',
  sellcursorleak: '2026-11-11 leak fixes (docs/545 §1): off/on in one session by ts3agent phase runs (switch on mid-session via patches, leak_census counters before/after); no harness queue repro yet',
  attractionalarm: '2026-11-11 leak fixes (docs/545 §1): off/on in one session by ts3agent phase runs (switch on mid-session via patches, leak_census counters before/after); no harness queue repro yet',
  uncleguard: '2026-11-11 leak fixes (docs/545 §1): off/on in one session by ts3agent phase runs (switch on mid-session via patches, leak_census counters before/after); no harness queue repro yet',
  mooddrift: '2026-11-11 leak fixes (docs/545 §1): off/on in one session by ts3agent phase runs (switch on mid-session via patches, leak_census counters before/after); no harness queue repro yet',
  dnpdisposelistener: '2026-11-11 leak fixes (docs/545 §1): off/on in one session by ts3agent phase runs (switch on mid-session via patches, leak_census counters before/after); no harness queue repro yet',
  seasondup: '2026-11-11 leak fixes (docs/545 §1): off/on in one session by ts3agent phase runs (switch on mid-session via patches, leak_census counters before/after); no harness queue repro yet',
  dreamrootorphan: '2026-11-11 leak fixes (docs/545 §1): off/on in one session by ts3agent phase runs (switch on mid-session via patches, leak_census counters before/after); no harness queue repro yet',
  blogorphan: '2026-11-11 leak fixes (docs/545 §1): off/on in one session by ts3agent phase runs (switch on mid-session via patches, leak_census counters before/after); no harness queue repro yet',
  dnpupdateguard: '2026-11-11 leak fixes (docs/545 §1): off/on in one session by ts3agent phase runs (switch on mid-session via patches, leak_census counters before/after); no harness queue repro yet',
  buycellrecycle: '2026-11-11 leak fixes (docs/545 §1): off/on in one session by ts3agent phase runs (switch on mid-session via patches, leak_census counters before/after); no harness queue repro yet',
  buyclearrecycle: '2026-11-11 leak fixes (docs/545 §1): off/on in one session by ts3agent phase runs (switch on mid-session via patches, leak_census counters before/after); no harness queue repro yet',
  buygridsweep: '2026-11-11 leak fixes (docs/545 §1): off/on in one session by ts3agent phase runs (switch on mid-session via patches, leak_census counters before/after); no harness queue repro yet',
  moodpending: '2026-11-11 leak fixes (docs/545 §1): off/on in one session by ts3agent phase runs (switch on mid-session via patches, leak_census counters before/after); no harness queue repro yet',
  filtergridleak: '2026-11-11 leak fixes (docs/545 §1): off/on in one session by ts3agent phase runs (switch on mid-session via patches, leak_census counters before/after); no harness queue repro yet',
  compositorghost: '2026-11-11 leak fixes (docs/545 §1): off/on in one session by ts3agent phase runs (switch on mid-session via patches, leak_census counters before/after); no harness queue repro yet',
  caspuckevents: '2026-11-11 leak fixes (docs/545 §1): off/on in one session by ts3agent phase runs (switch on mid-session via patches, leak_census counters before/after); no harness queue repro yet',
  famcursorleak: '2026-11-11 leak fixes (docs/545 §1): off/on in one session by ts3agent phase runs (switch on mid-session via patches, leak_census counters before/after); no harness queue repro yet',
  casexitsweep: '2026-11-11 leak fixes (docs/545 §1): off/on in one session by ts3agent phase runs (switch on mid-session via patches, leak_census counters before/after); no harness queue repro yet',
  seasonlistener: '2026-11-11 leak fixes (docs/545 §1): off/on in one session by ts3agent phase runs (switch on mid-session via patches, leak_census counters before/after); no harness queue repro yet',
  casfiltergrid: '2026-11-11 leak fixes (docs/545 §1): off/on in one session by ts3agent phase runs (switch on mid-session via patches, leak_census counters before/after); no harness queue repro yet',
  unseenanim: 'native [UnseenAnim] (UnseenAnim.Mode=1 ini or --ab unseenanim), not a fixesOn name: correctness by the unseen-anim-ab.json bp-ua-snapback job (repro unseen-snapback, ini on) and the stuck pair, gain by its in-load A/Bs (docs/144 §3.4)',
  unseenrm: 'native [UnseenRootMotion] Shortlist=1 Mode=1 (--ab unseenrm, rmMode), not a fixesOn name: correctness by unseen-root-motion.json bp-urm-snapback (repro unseen-snapback) and bp-urm-stuck-on, gain by its in-load A/Bs on rmWalkUs / aaPassUs (docs/228)',
  unseenobj: 'native [UnseenAnim] Objects=1 (--ab unseenobj, aoMode), not a fixesOn name: correctness by unseen-anim-objects.json bp-uao-snapback (repro unseen-snapback, objects in use keep animating) and bp-uao-stuck-on, gain by its in-load A/Bs on aoKindUs/aaUs_2 (docs/144 §3.6)',
  unseentable: 'native [UnseenTable] (UnseenTable.Enabled=1 ini, --ab unseentable), a shared classifier, not a hitch fix: correctness by unseen-table.json bp-utb-check / ip-utb-check (Check=1 mismatch counters, unseen-snapback repro), cost by its in-load A/Bs (docs/227 §6)',
  unseentablettl: 'native [UnseenTable] RefreshTicks arm (--ab unseentablettl, utbTtlMode), a cost knob of the shared classifier: correctness by unseen-table.json bp-utb-ttl-check (Check=1, unseen-snapback repro), cost by bp-utb-ttl-ab (docs/227 §8)',
  unseentablecopy: 'ts3fixes UnseenTable.cs copy path (docs/227 §9), the A/B baseline of the zero-copy Bits(id) reads, default off: correctness by unseen-table.json bp-utb2-check (zero-copy default, repro unseen-snapback; the copy path is the §6 path bp-utb-check tested), cost by bp-utb2-copy-ab (--ab unseentablecopy)',
  unseenghost: 'native [UnseenGhost] ini section (UnseenGhost.Enabled=1), not an ab/fixesOn name: turned on in every unseen-route-relaxed.json / unseen-route-long-ab.json job (docs/202 §3.9.5); acts only for unseenroute holds, which have repros',
  autshadowverify: DIAG, autunseencmp: DIAG, ownjobwait: DIAG, gcgrowlog: DIAG, autledger: MONO,
  hhsearchprobe: 'measurement probe (docs/276 §2.3 A, HhSearchProbe.cs): counts household autonomy searches by outcome per Sim, changes no result; run by hhsearch-sv-1116.json, not a fix',
  autt2l0: 'measurement probe (tier-2 L0, AutLocalProbe.cs): times the local phase per actor class, changes no result; run by tier2-local-l0.json, not a fix',
  autt2lcov: 'measurement probe (docs/276 rank 5 piece 1, AutLocalProbe.cs, needs autt2l0): splits local-phase C2 time by the local allow-list, changes no result; run by hhtoo-local-cov-1117.json, not a fix',
  envlistmemo: 'dry probe (docs/274 R1, EnvBcProbes.cs): compares a lot/room list memo with the game env lists, changes no result; run by env-bcast-dry-probe-1116.json, not a fix',
  envlistmemoapply: 'Mono speed, not a hitch fix (docs/274 §9 R1 apply, EnvBcProbes.cs): reuses env lists for unseen NPCs only; correctness by envlistmemo-apply-correct-1118.json (sampled verify, envlistapply_verify_diff 0), timing by envlistmemo-apply-time-1118.json',
  envlistmemoverify: DIAG,
  envlistmemoexact: 'Mono speed, not a hitch fix (docs/274 §11 R1 exact, EnvListExact.cs): the env scan room lists from the memo for every Sim inside the game method, valid by native order print + managed generations; correctness by envlistexact-correct-1118.json (sampled verify, envlistexact_verify_diff 0), timing by envlistexact-time-1118.json',
  envscancost: 'measurement probe (docs/274 §11, EnvListExact.cs): env scan calls and us by scope (household / seen / unseen), changes no result; run by envscancost-measure-1118.json, not a fix',
  bcposdry: 'dry probe (docs/274 R2, EnvBcProbes.cs): compares a per-tick position-table bcast set with the game set, changes no result; run by env-bcast-dry-probe-1116.json, not a fix',
  autofftick: OFFLOAD,
  jit: 'Mono speed, not a hitch fix: no repro makes script calls more frequent than plain play; correctness by jit-verify.json (Mode=2) and the selftest TestMonoJit, timing by jit-ab.json once jit-verify passed (docs/527 §5)',
  mintcall: 'Mono speed, not a hitch fix: no repro makes script calls more frequent than plain play; correctness by mintcall-verify.json (CallPath=2) and the monohost selftests, timing by the plain-play pairs in mintcall-ab.json (docs/139 §8)',
  steerretry: 'transient [SteerRO] fallbacks (a W1-W6 writer holding QL, a tripwire) with no deterministic trigger: measured per site (srrN / srrOk / srrFb / srrFbWaitUs) in the h4-goalvalid natural jobs (--ab watchdogasync,steerretry, docs/250 §8), correctness by the selftest TestSteerRO (retry ok, fallback after 4, State not retried, job ended)',
  ufhostlate: 'diagnostic: [UnseenFallback] HostLate=1 only splits the late-count into host vs work time (docs/202), no behaviour change; checked by the selftest TestUfHostLate (modes 0 and 1)',
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
  gcmarkhelpers: 'GC offload experiment, not a hitch fix: helpers off vs on in one load (queues/gc-markhelpers-time-1118.json, docs/287 §7); natural allocation drives the mark, the GC decides nothing about Sims, correctness in gc-markhelpers-correct-1118.json',
  gcidlegate: 'GC timing experiment, not a hitch fix: on vs off in one load (queues/gc-idle-gate-time-1117.json, docs/281 §8); natural allocation drives it, no Sim decision changes, correctness pairs in gc-idle-gate-correct-1117.json',
  doormirror: 'dead end as built: never run; door answers went through the P5 host-call proxy instead (docs/94 §19.1, docs/116); only comes back with docs/159 H4',
  errortrapsims: DEADSIM, errortraplookup: DEADSIM, errortrapctl: DEADSIM, celebritydeadsim: DEADSIM, seasonsdeadsim: DEADSIM,
  dreamnodedeadsim: DEADSIM, overwatchdeadsim: DEADSIM, casuihandlers: DEADSIM, simrefsdeadsim: DEADSIM,
  outfitwaitsaved: DEADSIM,
  tinyuieventreg: 'opt-in, Dalexlol CAS UI leak stress (#40: the CAS open/close stress runs read its sweep counters)',
  customctlsweep: 'opt-in, Dalexlol CAS UI leak stress (#40: the CAS open/close stress runs read its sweep counters)',
  deadsimcount: 'opt-in diagnostic, read-only counters (#40, PR #332)',
  hudunsub: 'opt-in test, UI memory: leftover HUD model handlers, class census A/B over live-mode entries (#40)',
  deadsimhollow: 'opt-in test, Dalexlol hold_sims A/B (#40, PR #332, al365 off vs al366 on)',
  dnpsubjectcache: 'opt-in test, four 30-minute stress runs with it on cleared 10 of 634, 7 of 658, 17 of 660 and 3 of 656 table entries, all passed (#40)',
  mapviewkeep: 'opt-in test, Dalexlol map view A/B in one load (#40, run mv4: 40 switches per arm)',
  exitcbsweep: 'opt-in test, vehicle route delegate sweep (leak-fixes-1111): check Sim.OnExitReasonsAdded list length per Sim before and after',
  dnppetkeys: 'opt-in test, DnP removal table sweep (leak-fixes-1111): mPotentialRemovalNodes pet nodes, one A/B',
  dnpobjkeys: 'opt-in test, DnP removal table sweep (leak-fixes-1111): mPotentialRemovalNodes destroyed-object keys, one A/B',
  // 2026-11-14 sim-cycle work (docs/546): measured in ts3agent phase soaks, not harness queues
  joyridecostume: 'hitch fix measured by ts3agent aut_census (docs/546 §4): longest GoForJoyride test 618/623 ms off (phase-20261113-2212/-2229), all under ~2 ms on (phase-20261113-2254, 45 tests answered, 0 errors); once per child per save, so no cheap repeatable repro (a fresh child with no ChildImagination outfit is needed each arm)',
  etderefdiag: 'log only, changes nothing (docs/552 §11): names who holds each destroyed object NRaas ErrorTrap cleans up at load; its etderef: lines are the result of any Bridgeport new-game load',
  alarmkilllog: 'log only, changes nothing (docs/546 §7): names a repeating alarm EA removed after its callback threw; nothing to repro but a throwing alarm, and its line is the result',
  routecauselog: 'log only, changes nothing (docs/580, RouteCauseLog.cs): the cause of NPC zero-plan route failures and locked-door failures; its routecause: lines in a soak are the result',
  barorderlog: 'log only, changes nothing (docs/578 §1, BarOrderLog.cs): times the bar customer side of a bartender conversation; its barorder: lines in a soak with a bartender are the result',
  socialreplanlog: 'log only, changes nothing (docs/577 §2, SocialReplanLog.cs): names the route builder, ignore list and positions when a social receiver replans against its own partner; its socialreplan: lines in a household social soak are the result',
  synctime: 'test switch, logging only, hooks only with kHitchCauseHooks (docs/546 §9, SyncWaitLog.cs): the measuring tool of socialfinish / socialreceivefail, not a fix',
  socialfinish: 'measured off/on in ts3agent social phase soaks (docs/546 §9): off 9 of 9 ShowVideos waits stood 5.3 min (phase-20261114-0401), on every logged wait ended after 0.5 min, 2310 = 16 signalled, 0 errors (phase-20261114-0430); no forced repro of one of the six masters yet',
  socialreceivefail: 'measured off/on in ts3agent social phase soaks (docs/546 §9): off 5 initiators on a parked receiver ran to the 30 min giveup (phase-20261114-1011), on 37 waits ended at once, 0 of the parked case left, 0 errors (phase-20261114-1113); the receive failure has no deterministic trigger',
  simfestkeep: 'EA bug fix from the decompiled code (docs/546 §11), not yet run: no harness check exists yet (a SimFest needs a ShowStage, an emcee and a winner pulled from the audience); add a SimFestKeepCheck and a repro before turning it on',
  unigradkeep: 'EA bug fix from the decompiled code (docs/546 §12), not yet run: needs a University world (Annex), a Sim with a completed degree whose rewards were not granted and the 12:00 ceremony; no check yet, verify in a University save before turning it on',
  bsrefund: 'EA bug fix from the decompiled code (docs/546 §12), not yet run: the pickup needs an enrolment (modal dialog), a night and an NPC car whose drive fails; no forced trigger yet, verify the refund (2351) in a play run before turning it on',
  // 2026-11-17: these were missing from the agent's names list (so no repro could set them); exempt until a repro lands
  asyncroute: 'async route plans, on since docs/18 (ts3native [RouteAsync]): judged by every timing/correctness job, which runs it on by default; no forced repro',
  autt2local: 'docs/276 rank 5 piece 2: HouseholdToo local exact path (AutShadowLocalCore.cs), nothing reads it before piece 3; needs [Experimental] HouseholdToo=1',
  eventcost: DIAG + ' (docs/545 §5, EventTracker.ProcessEvent timing; ts3agent event_cost turns it on)',
  gm6trace: DIAG + ' (AutShadow.cs IL trace of bad methods)',
  safespotdedupe: 'Dalexlol #511: ON 2026-11-20 on his 17-job soak counters (5,854 repeats stopped), a search shortcut for unseen townies, no forced repro',
  rabbitholedirect: 'Dalexlol #507: ON 2026-11-20 on his 17-job soak counters (entered 7,667), unseen townies only, no forced repro',
  rhoutside: 'Dalexlol #507: ON 2026-11-20 on his 17-job soak counters (placed 2,700), unseen townies only, no forced repro',
  unseenlanesnap: 'Dalexlol #509: ON 2026-11-20 on his 17-job soak counters (snapped 3,974), unseen townies only, no forced repro',
  unseenlegsnap: 'Dalexlol #509: ON 2026-11-20 on his 17-job soak counters (snapped 1,439), unseen townies only, no forced repro',
  stagedarrival: 'Dalexlol #505: stays OFF, his 17-job soak asked 1,137 but placed 0 (every candidate refused), unproven: no forced repro',
  icecreamdirect: 'Dalexlol #505: ON 2026-11-20 on his 17-job soak counters (skipped 1,074), unseen trucks only, no forced repro',
  nearvenue: 'Dalexlol #505: ON 2026-11-20 on his 17-job soak counters (lowered 14,417, raised 7,202), townies only, no forced repro',
  unseenoutfitnospin: 'Dalexlol #513: ON 2026-11-20 on his 17-job soak counters (taken 7,251, set ran at once 0), unseen townies only, no forced repro',
  safespotpool: 'SafeSpot never answers a pool or below-ground outdoor cell (docs/74 §13), on by default: no forced repro yet',
  savedstacks: 'on by default (docs/242): a swapped method keeps its saved Sims at load; judged by save/load correctness jobs, no forced repro',
  spotcheck: 'GoHome / lot-trip teleport with an in-view landing spot walks instead (docs/560 §7), on by default: no forced repro yet',
  tagunseen: 'all-unseen-townie Tag games skip the run routes (docs/60 §3), on by default: no forced repro yet',
  tpdoorrights: 'F1 unseen-teleport lot test by the Sim\'s own door rights (docs/584 §7), on by default: judged by the unseen-fallback jobs, no own repro',
  weddingcleanup: 'EA WeddingCeremony.CleanUp throw guard (counters 1058/1059), on by default: no forced repro yet',
// 2026-11-18 merge of Dalexlol #540 / #559: switches his PRs ship without a forced repro (his checks or natural runs are the evidence)
  fightstaycap: 'PR #559: natural runs (Bridgeport 25 released, Sunset Valley 18, 0 errors); no forced repro yet',
  autlastsearch: 'Dalexlol #540: log, check or measurement switch, changes no game state; no forced repro',
  callcensus: 'Dalexlol #540: log, check or measurement switch, changes no game state; no forced repro',
  dormantprobe: 'Dalexlol #540: log, check or measurement switch, changes no game state; no forced repro',
  buffbufcheck: 'Dalexlol #540: log, check or measurement switch, changes no game state; no forced repro',
  motivebufcheck: 'Dalexlol #540: log, check or measurement switch, changes no game state; no forced repro',
  stcsocialscheck: 'Dalexlol #540: log, check or measurement switch, changes no game state; no forced repro',
  routableasync: 'Dalexlol #469: ON 2026-11-19 with [RouteAsync] Kinds=2 (soak only); judged by the in-load --ab asyncreplan queue routableasync-ab-1119, no forced repro',
  stcsocials: 'Dalexlol #540: default off, not proven in game yet (routableasync held, #469); no forced repro',
  motivebuf: 'Dalexlol #540: default off, not proven in game yet (routableasync held, #469); no forced repro',
  visitcar: 'Dalexlol docs/584 §1 unseen visit trips: judged by his harness evidence, no forced repro',
  visitwalkfinish: 'Dalexlol docs/584 §1 unseen visit trips: judged by his harness evidence, no forced repro',
  // 2026-11-20 merge of Dalexlol #564: switches without a forced repro (his in-game results or never fired yet)
  boatroutenull: 'Dalexlol #564: default off, never fired yet (Boat.RouteBoatToLandOnWayToPosition false); no forced repro',
  unseenfbnosync: 'Dalexlol (docs/202 §3.10): ON (import 2026-11-20); acts only when ts3native refuses the async START of a marked unseen plan (queue state), which no repro can force; natural-play counters only (unseenfbnosync_asked / _recovered)',
  unseenhouseboatview: 'Dalexlol (docs/202 §3.10): default off; a scope-rule change for townies on foreign houseboats (Isla Paradiso), no bug to force; natural-play counters on 31 IP games (unseenhouseboatview_asked / _changed)',
  safespotplanyield: 'Dalexlol (docs/202 §3.10): ON (import 2026-11-20); a one-tick yield after a SafeSpot verdict plan (sim-thread slice split, not an EA bug); natural-play counters only (safespotplanyield_yields / _given_up)',
  overwatchresetgone: 'Dalexlol #564: default off, never fired yet (destroyed Sim with no proxy in SetObjectToReset); no forced repro',
  motivesnohousehold: 'Dalexlol #564: default off, not testable on his PC (Sim with no household); no forced repro',
  overwatchrelretry: 'Dalexlol #564: ON, one natural hit in his play, retry passed; load-time race, no forced repro',
  // boat trips PR (2026-11-19): townie boat routing, judged by his natural-play counters; no forced repro
  boattrip: 'Dalexlol boat trips PR: ON (import 2026-11-20), townie boat trips (BoatTrip.cs), natural Isla Paradiso play only; no forced repro',
  boatplace: 'Dalexlol boat trips PR: ON (import 2026-11-20), townie boat placement off lot water (BoatPlace.cs), natural play only; no forced repro',
  retryscope: 'Dalexlol boat trips PR: ON (import 2026-11-20), scope answer re-sent for boattrip sync plans (RetryScope.cs), natural play only; no forced repro',
  boatlandstuck: 'Dalexlol boat trips PR: ON (import 2026-11-20), EA GetOutOfBoat.Run stuck-seated fix (BoatLandStuck.cs), natural play only; no forced repro',
  jobyieldroad: 'Dalexlol #473: ON, jobyieldroad family (docs/800), judged by his natural games and soaks; no forced repro',
  jobyieldroadow: 'Dalexlol #473: ON, jobyieldroad family (docs/800), judged by his natural games and soaks; no forced repro',
  jobyieldspawn: 'Dalexlol #473: ON, jobyieldroad family (docs/800), judged by his natural games and soaks; no forced repro',
  jobyieldfp: 'Dalexlol #473: ON, jobyieldroad family (docs/800), judged by his natural games and soaks; no forced repro',
  jobyieldready: 'Dalexlol #473: ON, jobyieldroad family (docs/800), judged by his natural games and soaks; no forced repro',
  // alexvixell35-stack autonomy quality PR (2026-11-18): soak counters, no forced trigger yet
  poolnolane: 'EA bug fix (PoolNoLane.cs): needs a pool whose Inside lane slots are all disabled; no forced trigger yet, judged by soak counters 1348-1350',
  autonomyfailcooldown: 'route-failure cooldown for NPC autonomy (AutonomyFailCooldown.cs): needs an NPC whose route to one object keeps failing; judged by soak counters 1360-1369 and its routefail lines, no forced repro',
  venuepicktime: DIAG + ' (VenuePickTime.cs: venue arrival pick timing, logging only)',
  venuepickfast: 'timing only (VenuePickTime.cs): one yield before each venue arrival motive search of an NPC; judged by venuepicktime in soaks, no forced repro',
  // 2026-11-20 import of Dalexlol #503: natural soak runs are the evidence (counters moved, 0 problems); no forced repro
  visitarrive: 'Dalexlol PR #503: his 11-19 17-job soak, 14,274 arrivals without a route, 0 problems',
  visitonlot: 'Dalexlol PR #503: soak: fired 3x, 0 problems',
  gohomeshell: 'Dalexlol PR #503: soak: 6,286 asks, 2,157 placed; off until LotLevelPastEnd (PR #449) is on',
  // 2026-11-20 import of Dalexlol #499: natural soak runs are the evidence (counters moved, 0 problems); no forced repro
  unseentraffic: 'Dalexlol PR #499: his 11-19 17-job soak, 43,118 teleport legs, 0 problems',
  npccarexit: 'Dalexlol PR #499: soak: 1,691 exit asks, 0 problems',
  // 2026-11-20 import of Dalexlol #490: natural soak runs are the evidence (counters moved, 0 problems); no forced repro
  spawnfix: 'Dalexlol PR #490: his 11-19 17-job soak, 8,357 fan-outs skipped, 0 problems',
  spawnoffworld: 'Dalexlol PR #490: soak: 208 off-world spawns skipped; off (gameplay change)',
  // 2026-11-20 import of Dalexlol #501: natural soak runs are the evidence (counters moved, 0 problems); no forced repro
  festivalhidden: 'Dalexlol PR #501: two Starlight Shores runs, 44 and 40 skips, 0 errors',
  // 2026-11-20 import of Dalexlol #478: natural soak runs are the evidence (counters moved, 0 problems); no forced repro
  bouncermemo: 'Dalexlol PR #478: 7 Bridgeport games 11-18, 71-430 loops skipped, all PASS',
  // contributor PR post/ea-guards-skips-1120: natural-play evidence only, no forced repro
  frontstageowner: 'contributor: ON (import 2026-11-20), fired in natural play (3 games, unowned stage kits); no forced repro',
  careerskillnull: 'contributor: ON (import 2026-11-20), fired in natural play (11 games, skills the game could not add); no forced repro',
  stuckhbskip: 'contributor: ON (import 2026-11-20), fired in natural play (Isla Paradiso houseboats, 28 games); no forced repro',
  turnjigskip: 'Dalexlol: ON, performance skip for unseen townies, fired in natural play (91 games); no forced repro',
  unseenrelax: 'Dalexlol unseen route cost PR: ON (import 2026-11-20), route-cost switch for unseen townies, judged by natural games (no off arm); no forced repro',
  iafailmemo: 'Dalexlol unseen route cost PR: default off (his own advice: until a watchdog A/B), route-cost memo for townies, judged by natural games (no off arm); no forced repro',
  // contributor PR post/fglinbound-1121 (docs/682 §9)
  fglinboundhh: 'contributor: default off, household variant of fglinbound (same code path, the household gate lifted); its repro is fglinbound-check, whose live / live:big compares call the path directly; turned on only after fglinbound shadow runs (docs/682 §9.8 step 3)',
};

// Every ts3fixes switch (a ShortcutOn("x") call or a SwDefault case in mods/ts3fixes/src) must be in the agent's names list,
// or `patches` / profile-world --fixes-on reject it (autt2lcov, 2026-11-17).
export function modSwitches(srcs: string[]): string[] {
  const calls = srcs.flatMap(s => [...s.matchAll(/ShortcutOn\("(\w+)"\)/g)].map(m => m[1]));
  const def = srcs.map(s => s.match(/static bool SwDefault\([\s\S]*?\n {8}\}/)?.[0]).find(Boolean) ?? '';
  if (!def) throw new Error('ts3fixes: no SwDefault(name) switch in Fixes.cs');
  return [...new Set([...calls, ...[...def.matchAll(/case "(\w+)":/g)].map(m => m[1])])];
}

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
  const srcDir = 'mods/ts3fixes/src';
  for (const n of modSwitches(readdirSync(join(ROOT, srcDir)).filter(f => f.endsWith('.cs')).map(f => read(`${srcDir}/${f}`))))
    if (!fixes.includes(n)) errs.push(`${n}: ts3fixes switch missing from the agent's names list (PatchHandlers.cs)`);
  if (errs.length) { console.error(`fix-repro-check FAILED (${errs.length}):\n  ${errs.join('\n  ')}`); process.exit(1); }
  console.log(`fix-repro-check ok: ${switches.length} switches (${fixes.length} ts3fixes, ${native.length} native), ${Object.keys(EXEMPT).length} exempt`);
}

if (import.meta.filename === resolve(process.argv[1] ?? '')) {
  if (process.argv.includes('--self-check')) {
    assert.deepEqual(fixesNames('x; string[] names = { "a", "b" }; y'), ['a', 'b']);
    const swd = 'static bool SwDefault(string name)\n        {\n            switch (name) { case "d": return false; }\n        }\n        case "notsw": x';
    assert.deepEqual(modSwitches(['if (ShortcutOn("a")) y; ShortcutOn(n);', swd]), ['a', 'd'], 'ShortcutOn literals + SwDefault cases only');
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
