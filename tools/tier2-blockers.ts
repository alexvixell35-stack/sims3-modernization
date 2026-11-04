// docs/219: what still blocks each tier-2 autonomy definition (docs/126 candidates) that is not ready after the built worker
// hooks (H7-H12) and the Query_GetGlobalObjects row (docs/127 §7): the exact missing piece (snapshot rows with the key their
// answer depends on; the managed write or sim-thread IC that blocks, with its path), an effort class and the scoring share
// the definition would move (docs/75 samples). Input: the --full JSON of tools/autonomy-virtual-closure.ts, run with those
// hooks as what-if cuts (command in docs/219 Reproduce). Also classifies every game override of GameObject.get_InUse with
// the same rules: a base path scores any target, so all of them sit in its closure.
// Usage: node tools/tier2-blockers.ts --asm <repo>/game-copy/assemblies --index <...>/index.json --full <json>
//        [--csv docs/data/tier2-blockers.csv] [--what-if <the same cuts as the --full run>]   (summary to stderr). Static only.
import { readFileSync, writeFileSync } from 'node:fs';
import { ix, closure, classify, CUT } from './autonomy-tier2.ts';
import { virtualTargets } from './managed-callgraph.ts';

const arg = (n: string, d: string) => { const i = process.argv.indexOf(n); return i > 0 ? process.argv[i + 1] : d; };
CUT.push(...arg('--what-if', '').split(',').filter(Boolean));
type Full = { group: string; name: string; rank: string; samples: string; share: string; verdict: string; ics: { ic: string; cls: string; d: number; path: string }[]; blockers: { what: string; path: string }[] };
const full: { whatIf: string; cands: Full[] } = JSON.parse(readFileSync(arg('--full', ''), 'utf8'));
const entries = readFileSync('docs/data/autonomy-entry-list.csv', 'utf8').trim().split('\n').slice(1);
const TOTAL = entries.reduce((s, l) => s + Number(l.split(',')[4]), 0); // 1,033 scoring samples (docs/75 §2)
const ROW5 = Number(entries.find((l) => l.startsWith('5,'))!.split(',')[4]); // 61, not splittable per type (docs/75 §6)
const META_REST = 29; // tier-2 meta samples (docs/126 Summary: rows 14, 37, 40, 52, 66 and the meta parts of 17, 19, 20, 31)

// Answer key per snapshot row. docs/126 §4.1 numbers for the 26 designed rows; the others read from the managed signature.
const KEY: Record<string, string> = {
  World_GetTerrainHeightImpl: '§4.1 #1: float per (row x/z, bridges flag)', World_GetTerrainSurfaceTypeImpl: '§4.1 #2: int per (row, level); a-cut: fill only with the lot level bit set',
  World_GetTerrainTypeImpl: '§4.1 #3: 1 B per (row, level)', World_GetFloorSurfaceTypeImpl: '§4.1 #4: string ref per (row, lot); a-cut',
  World_HasFloorFeaturesImpl: '§4.1 #5: bit per (row tile, flags, mask), learned keys', World_HasFloorFeaturesWorldPosImpl: '§4.1 #6: puddle bit per row, refreshed every cut',
  World_GetTilesWithFloorFeaturesImpl: '§4.1 #7: per lot and (roomDef, flags, mask)', World_IsCameraRoutableImpl: '§4.1 #8: bit per row', World_IsOnPlatformImpl: '§4.1 #9: bit per row',
  World_IsInPoolImpl: '§4.1 #10: bit per row', World_HasPoolImpl: '§4.1 #11: bit per (lot, row tile)', World_GetPoolObjectIdForPoolBelowPositionImpl: '§4.1 #12: pool id per row',
  World_GetPondAtPoint: '§4.1 #13: per (row, lot)', World_GetPondInfo: '§4.1 #14: per pond, partial out', World_FindPondRepresentativePositions: '§4.1 #15: per lot, partial out',
  World_GetLotLocationAtFloorImpl: '§4.1 #16: per (row, roomDef), room short only with room data', World_GetRoomIdImpl: '§4.1 #17: per (row tile, roomDef)',
  World_GetInformationAboutRoomImpl: '§4.1 #18: per (lot, room, roomDef); a-cut', World_LotIsEntranceFromTargetPointImpl: '§4.1 #19: per lot, gate sites',
  World_LotGetDisplayLevelInfoImpl: '§4.1 #20: per lot', World_GetWorldToHouseboatDisplayPositionImpl: '§4.1 #21: per (row, houseboat lot)', Objects_GetForward: '§4.1 #22: vector per row',
  Objects_GetBoundingBox: '§4.1 #23: box per row', Objects_IsValid: '§4.1 #24: row present', World_LightGetDimmerImpl: '§4.1 #25: float per light row', World_GetWorldFileName: '§4.1 #26: once per load',
  Objects_GetHeightAboveFloor: 'new: float per row (object id), next to the row position',
  World_IsOnRoofImpl: 'new: bit per row (position); docs/127 ~0.25 h',
  Slots_IsSlotEnabledImpl: 'new: bit per (object, slot name); the body takes +0x918 (docs/90), so fill at the cut like a-cut rows',
  Slots_GetRoutingSlotNamesImpl: 'new: slot-name array per object model (fixed per model), memo',
};
// The four rows a base-path override puts in most local closures (docs/219 §2).
const SHARED = ['World_IsOnRoofImpl', 'Objects_GetHeightAboveFloor', 'World_GetPoolObjectIdForPoolBelowPositionImpl', 'Objects_GetForward'];

// Blocked definitions, read from their blocker paths (docs/219 §3). [kind, missing piece and fix, effort, notes]
const INUSE = 'GameObject.get_InUse overrides (no target type here): 10 of 48 block (§3.1: 6 destroy an orphaned prop, 2 reach a fire placement check, 2 build the LiveDragHelperModel singleton); fix = worker hook on the 8 (abort `inuse`) + pre-warm LiveDragHelperModel.Singleton';
const HAND: [RegExp, string, string, 'small' | 'medium' | 'hard', string][] = [
  [/^base path: Autonomy\.CalculateScoreForObjectInteraction$/, 'managed-write', INUSE, 'small', 'every blocker path enters a get_InUse override through GetInUseAutonomyMultiplier'],
  [/^scoring function GameObject\.SitInteractionModifier$/, 'managed-write', INUSE, 'small', 'same get_InUse family as the base path'],
  [/NotAutonomousForSelectableSims$/, 'managed-write', INUSE, 'small', 'through CalculateScoreForObjectInteraction'],
  [/TraitFunctions\+NeatTraitCleanHouse\+Definition$/, 'managed-write', INUSE, 'small', 'Lot.DoesObjectNeedCleaning calls get_InUse itself, so the hook must sit on the overrides, not in GetInUseAutonomyMultiplier'],
  [/(WerewolfScratchObject|InteractionsShared\.ScratchObject)\+Definition$/, 'sim-thread-ic', 'UserToolUtils_CheckObjectPlacementImpl in the own Test (Object.CheckObjectPlacement), plus the get_InUse family', 'hard', 'placement test on every call: stays on the sim thread'],
  [/Sim\+FreakOutAtObject\+Definition$/, 'sim-thread-ic', 'UserToolUtils_CheckObjectPlacementImpl in the own Test', 'hard', 'stays on the sim thread'],
  [/Sim\+CallPet\+Definition$/, 'own-field-write', 'Test fills mSimPickerList on the shared definition object', 'medium', 'docs/75 §3 stage A per-tick cache, or a worker copy with its own list [inferred]'],
  [/SitHereForEating\.EatHereScoringFunction$/, 'sim-thread-ic', 'chair CanScootBack (ChairBarStool, ChairDining via EatingSurfaceImpl.GetClosestFreeSlotPair): World_TestObjectPlacementPQ, and DisableFootprint (World_ObjectEnableFootprint) around it', 'hard', 'a real footprint toggle + placement test per chair: a per-chair bit at the cut would cost one placement test per chair per cut'],
  [/Phone\+Call\+CallDefinition`1$/, 'family-root', 'the generic phone-call base: its Test reaches every subclass Test (PerformanceCareer.GetSteadyGigProprietors 45 blockers, LogicSkill chess challenger 16, MovingWorldDialog world search 3)', 'medium', 'split per concrete subclass and allow-list the clean ones; the entry-list row is per base'],
  [/Socializing\.SocialInteractionA\+Definition$/, 'static-coll', 'Relationship.GetMiniRelationships adds to Relationship.sAllRelationships; CauseEffectService persistable data lazy fill', 'medium', 'the OWN_FIELD_WRITE hits are the .ctor of a new Definition in GetYouShouldTargets (a fresh object, not the shared one) [inferred: rule false positive]; pre-warm CauseEffectService'],
  [/Sim_ReadToSleep_Autonomous\+Definition$/, 'static-coll', 'Relationship.GetMiniRelationships adds to Relationship.sAllRelationships; CauseEffectService persistable data lazy fill', 'medium', 'pre-warm CauseEffectService; abort when the Sim has no relationship entry yet'],
  [/Door\+ChangeDoorbellSound\+Definition$/, 'writing-ic', 'Lot.GetFrontDoor lazy lookup: SetFrontDoor (World_SetLotFrontDoorImpl), UpdateStreetParking (World_FindPlacesOnRoad)', 'medium', 'pre-warm the front door per lot (load and lot change)'],
  [/(ViewObjects|WalkerClassic\+HoldChild)\+Definition$/, 'static-write', 'LiveDragHelperModel.get_Singleton lazy singleton', 'small', 'pre-warm once per load; then new rows only (docs/219 §3.2 what-if)'],
  [/(Phone\+CallIntelligenceAgency|GoToOccupationJobLocation)\+Definition$/, 'sim-thread-ic', 'Lot.IsFireOnLot -> FireManager.HasOnlyLeafPileFires -> Fire.GetObjectsOnFloor -> CheckObjectPlacement', 'small', 'abort `fire` while the lot has a fire (FireManager.NoFire false); same guard covers the IceCreamMaker / DeepFryerVegas get_InUse'],
  [/InteriorDesigner\+TakeMeasurements\+Definition$/, 'static-write', 'Cabinetry.get_BuyModeSuite lazy BuyModeSuite: DesignModeSwap.DesignModeSwapComplete event add', 'medium', 'pre-warm per cabinetry object, or abort while the suite is null'],
  [/Terrain\+SplashInPuddle\+Definition$/, 'unclear-ic', 'World_GetObjectsImpl (class d, as Query_GetObjects I-121-7) and a CheckObjectPlacement', 'hard', 'two pieces; the placement check keeps it on the sim thread'],
  [/SnowWaterFight\+PlayWith\+Definition$/, 'own-field-write', 'Test writes mHitPosition on the shared definition; CauseEffectService lazy fill', 'hard', ''],
  [/Careers\.SpawnJob\+Definition$/, 'own-field-write', 'Test writes Lot, Job, JobId, JobCreationSpec, JobData on the shared definition', 'hard', ''],
  [/MartialArts\+TeleportDuringMeditation\+Definition$/, 'sim-thread-ic', 'World_TestObjectPlacementP in the own Test', 'hard', ''],
  [/PondManager\+IceSkateHere\+Definition$/, 'sim-thread-ic', 'WorldPond.HasRoomForMoreSkaters: World_FindGoodLocation, object create', 'hard', ''],
  [/InteriorDesigner\+CompleteRenovation\+Definition$/, 'writing-ic', 'Test calls GetInteractionName: chess-rank lookup (skill add, level-up sound), cost, cause-effect service', 'hard', '16 blockers, all behind the name'],
  [/Terrain\+DEBUG_LightningStrike\+Definition$/, 'writing-ic', 'SeasonsManager.CanObjectBeStruckByLightning over any object (IsOnHandTool UI wrappers, get_InUse family); own field mHitType', 'hard', 'debug interaction [inferred: not scored in normal play]'],
  [/PushableStatue\+Move\+Definition$/, 'writing-ic', 'Test creates a statue jig (GlobalFunctions.CreateObjectOutOfWorld) and toggles footprints', 'hard', ''],
  [/MagicCauldronRegency\+TeleportPotion\+Definition$/, 'writing-ic', 'Test creates an object out of world and plans a route (Route.PlanToPoint)', 'hard', ''],
  [/OccultUnicorn\+UnicornTeleport\+Definition$/, 'writing-ic', 'Test calls Destroy on an object (TimePortal, EventLotMarker, Newspaper ... through a wide Destroy dispatch)', 'hard', ''],
  [/Jetpack\+FlyHere\+Definition$/, 'yield', 'Sim.GetActiveJetpack pops the posture: OnExitPosture -> Simulator.Sleep', 'hard', 'docs/127 §7.2'],
  [/(RockBand\+GetBandMemberToLot|BakeSaleTable\+GotoBakeSaleTable)\+Definition$/, 'wide-dispatch', 'a Test call on a definition the closure cannot type (InteractionUtil.Test(InteractionDefinition, ...) parameter; generic base Test): 200+ blockers from every Test override', 'medium', 'verdict unknown until the definition argument is typed per call site; first real blocker is World_TestObjectPlacementP'],
];

const notReady = full.cands.filter((c) => !/^v0/.test(c.group) && (c.verdict === 'new-rows' || c.verdict === 'blocked'));
const perRank = new Map<string, number>(); for (const c of full.cands) if (c.samples) perRank.set(c.rank, (perRank.get(c.rank) ?? 0) + 1);
const nRow5 = full.cands.filter((c) => c.group === 'row5-candidate').length, nMeta = full.cands.filter((c) => c.group === 'meta-rest').length;
const samplesOf = (c: Full): [number, string] =>
  c.samples ? [Number(c.samples) / perRank.get(c.rank)!, perRank.get(c.rank)! > 1 ? `docs/75 row ${c.rank} split over ${perRank.get(c.rank)} candidates` : `docs/75 row ${c.rank}`]
  : c.group === 'row5-candidate' ? [ROW5 / nRow5, `row 5 count split (${ROW5}/${nRow5})`] : c.group === 'meta-rest' ? [META_REST / nMeta, `tier-2 meta count split (${META_REST}/${nMeta})`] : [0, 'no samples'];
const esc = (s: string) => (/[",\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s);
const out = ['definition,blocker_kind,blocker_detail,effort,est_share,notes'];
const tally = new Map<string, [number, number]>(); const rowUse = new Map<string, [number, number]>();
for (const c of notReady.sort((a, b) => samplesOf(b)[0] - samplesOf(a)[0] || a.name.localeCompare(b.name))) {
  const [smp, basis] = samplesOf(c); const share = smp / TOTAL;
  let kind: string, detail: string, effort: string, notes: string;
  if (c.verdict === 'new-rows') {
    const miss = [...new Set(c.ics.filter((i) => /^a/.test(i.cls) && !i.cls.endsWith('*')).map((i) => i.ic))];
    const extra = miss.filter((m) => !SHARED.includes(m));
    for (const m of miss) { const u = rowUse.get(m) ?? [0, 0]; rowUse.set(m, [u[0] + 1, u[1] + share]); }
    kind = 'snapshot-rows';
    detail = miss.map((m) => (SHARED.includes(m) ? `${m} (§2)` : `${m} (${KEY[m] ?? 'no row design: ' + c.ics.find((i) => i.ic === m)!.cls})`)).join('; ');
    effort = extra.some((m) => !KEY[m]) || extra.length > 3 ? 'medium' : extra.some((m) => /a-cut|\+0x918/.test(KEY[m])) ? 'medium' : 'small';
    notes = `${basis}; ${miss.length} rows in all`;
  } else {
    const h = HAND.find(([re]) => re.test(c.name));
    const first = c.blockers[0]?.what ?? '';
    [kind, detail, effort, notes] = h ? [h[1], h[2], h[3], h[4]]
      : [first.startsWith('ic-b') ? 'sim-thread-ic' : first.startsWith('ic-w') ? 'writing-ic' : first.startsWith('ic-d') ? 'unclear-ic' : /^STATIC/.test(first) ? 'static-write' : first.startsWith('OWN') ? 'own-field-write' : 'yield', first, c.blockers.length > 5 ? 'hard' : 'medium', 'not read by hand'];
    notes = `${basis}; ${c.blockers.length} blockers${notes ? '; ' + notes : ''}`;
  }
  const t = tally.get(`${c.verdict} ${effort}`) ?? [0, 0]; tally.set(`${c.verdict} ${effort}`, [t[0] + 1, t[1] + share]);
  out.push([c.name, kind, detail, effort, share.toFixed(5), notes].map(esc).join(','));
}
const csv = arg('--csv', 'docs/data/tier2-blockers.csv'); writeFileSync(csv, out.join('\n') + '\n');
console.error(`${notReady.length} tier-2 definitions not ready (of ${full.cands.filter((c) => !/^v0/.test(c.group)).length}; what-if: ${full.whatIf.split(',').length} cuts) -> ${csv}; total samples ${TOTAL}`);
for (const [k, [n, s]] of [...tally].sort()) console.error(`  ${k}: ${n} definitions, ${(100 * s).toFixed(2)}% of scoring`);
console.error('snapshot rows by definitions needing them (share of scoring they would open together with the rest of each definition):');
for (const [m, [n, s]] of [...rowUse].sort((a, b) => b[1][0] - a[1][0])) console.error(`  ${n}\t${(100 * s).toFixed(2)}%\t${m}\t${KEY[m] ?? '-'}`);

// §3.1: every game override of GameObject.get_InUse, closed with the this-family rule (no other receiver context).
const decl = ix.methods.findIndex((m) => m.k === 'Sims3.Gameplay.Abstracts.GameObject::get_InUse()');
const ovs = virtualTargets(decl).ea; let ok = 0;
for (const o of ovs) {
  const own = ix.methods[o].k.replace(/::.*$/, '');
  const v = classify({ group: 'inuse', rank: '', samples: '', share: '', ctx: '', name: own, asm: '', roots: [o] }, closure([o], undefined, 5, own, () => undefined));
  if (v.verdict === 'ready') { ok++; continue; }
  console.error(`  get_InUse ${v.verdict}: ${own} | ${v.blockers.slice(0, 2).join('; ')} | rows ${v.newIcs.filter(([, cl]) => /^a/.test(cl)).map(([n]) => n.replace(/^.*::/, '')).join(' ')}`);
}
console.error(`get_InUse overrides: ${ovs.length}, ready ${ok}`);
