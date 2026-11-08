// docs/126: tier-2 autonomy allow-list. Per candidate (meta definitions not in allow-list v0, sampled local definitions,
// the candidates of the biggest unresolved bucket, local base paths, sampled scoring functions): managed closure at depth 12
// with the pilot's cuts and worker hooks (docs/122 §3.5, §12.5), new native ICs against the pilot snapshot set
// (docs/data/autonomy-proxy-ics.csv), new managed writes against the pilot union, verdict ready / new-rows / blocked.
// Usage: node tools/autonomy-tier2.ts --asm <repo>/game-copy/assemblies [--index game-copy/analysis/callgraph/index.json] > docs/data/autonomy-tier2.csv
//        (summary to stderr). Static only: reads the call-graph index, the assemblies' metadata and docs/data CSVs.
import { existsSync, readFileSync } from 'node:fs';
import { defRoots, ownTags, icVerdicts, subtypes, virtualTargets, familyTargets, onChain, receiverTypes, type Recv } from './managed-callgraph.ts';

const arg = (n: string, d: string) => { const i = process.argv.indexOf(n); return i > 0 ? process.argv[i + 1] : d; };
type Rec = { k: string; a: number; ic?: string; c: number[]; fw: number[] };
if (process.argv.includes('--self-check') && !existsSync(arg('--index', 'game-copy/analysis/callgraph/index.json'))) { console.log('autonomy-tier2 self-check: no call-graph index (game-copy), skipped'); process.exit(0); }
export const ix: { asms: { name: string }[]; kinds: string[]; methods: Rec[]; fields: string[] } =
  JSON.parse(readFileSync(arg('--index', 'game-copy/analysis/callgraph/index.json'), 'utf8'));
const DEPTH = 12;
const parseCsv = (s: string) => s.trim().split(/\r?\n/).map((l) => {
  const out: string[] = []; let cur = '', q = false;
  for (let i = 0; i < l.length; i++) {
    const ch = l[i];
    if (q) { if (ch === '"' && l[i + 1] === '"') { cur += '"'; i++; } else if (ch === '"') q = false; else cur += ch; }
    else if (ch === '"') q = true; else if (ch === ',') { out.push(cur); cur = ''; } else cur += ch;
  }
  out.push(cur); return out;
});
const csv = (f: string) => { const r = parseCsv(readFileSync(f, 'utf8')); return r.slice(1).map((x) => Object.fromEntries(r[0].map((h, i) => [h, x[i] ?? '']))); };

// docs/121 §1 cuts plus the pilot's managed worker hooks: the worker answers there, so their subtrees are not the definition's.
export const CUT = ['Sims3.Gameplay.Seasons.SeasonsManager::LazyAllocateManager(', 'Sims3.Gameplay.Socializing.Relationship::Get(',
  'Sims3.Gameplay.Actors.Sim::IsAllowedInRoom(', 'Sims3.Gameplay.Actors.Sim::get_NumSimsDoingGardening(',
  'Sims3.Gameplay.Utilities.Localization::LocalizeString(', 'Sims3.Gameplay.Utilities.OnlineDatingManager::DidSimsMeetOnline(',
  'Sims3.Gameplay.Autonomy.MetaAutonomyTuning::GetSimCountForVenueWithCustomDesireFunction(',
  'Sims3.Gameplay.ChildAndTeenUpdates.Punishment::GetPunishmentManagerFromSim('];
// docs/126 §3 tier-2 worker hooks (design, not built): H7 top dragged object from the cut snapshot (both getters),
// H8 the actor's search cache must already be active, H9 tuning caches read-only (miss = abort), H10 autonomy LOD flag
// read without the 60-tick recompute. --no-tier2-hooks gives the counts before them.
if (!process.argv.includes('--no-tier2-hooks')) CUT.push('Sims3.Gameplay.UI.LiveDragHelperModel::get_CachedTopDraggedObject(',
  'Sims3.Gameplay.UI.LiveDragHelperModel::get_TopDraggedObject(', 'Sims3.Gameplay.Autonomy.Autonomy::UpdateCacheIfNeeded(',
  'Sims3.Gameplay.Autonomy.InteractionObjectPair::AddTuning(', 'Sims3.Gameplay.Autonomy.Autonomy::get_IsSufficientlyOnScreenForHighLODSimulation(');
// docs/219 §3.1 tier-2 P1 (built 2026-11-08, ts3fixes AutInUseHook / AutFireHook, switches autt2inuse / autt2fire, not run):
// the six get_InUse overrides that can destroy their prop and the leaf-pile fire check abort on the worker.
// --no-tier2-p1 gives the counts before them.
if (!process.argv.includes('--no-tier2-hooks') && !process.argv.includes('--no-tier2-p1')) CUT.push(
  ...['Sims3.Gameplay.Objects.Gardening.Soil', 'Sims3.Store.Objects.BBallHoop+BBallHoopBall', 'Sims3.Store.Objects.BBallArcade+BBallArcadeBall',
    'Sims3.Store.Objects.ChocolateFountain+ChocolateFountainSkewer', 'Sims3.Store.Objects.IceCreamMaker+IceCreamCone',
    'Sims3.Store.Objects.GardenStation+GardenStationProp'].map((t) => `${t}::get_InUse(`),
  'Sims3.Gameplay.Controllers.FireManager::HasOnlyLeafPileFires(');
// Lazy fills of a fixed value (docs/126 §3 pre-warm list, read in the decompiled source): one sim-thread call per load, then reads.
// LiveDragHelperModel::gSingleton: tier-2 P1 drag pre-warm (switch autt2dragwarm).
export const PREWARM = /::(sRoommateManager|sMaxHunger|mCachedCuriousDewRecipe|cjk\w+|utf8EncodingWithoutMarkers|cache)$|LiveDragHelperModel::gSingleton$/;
const ics = icVerdicts();
const icRow = new Map(csv('docs/data/ic-safety.csv').map((r) => [r.ic, r]));
export const snap = new Map(csv('docs/data/autonomy-proxy-ics.csv').map((r) => [r.ic.slice(r.ic.indexOf('::')), r.class]));
export const starts = (pat: string) => ix.methods.flatMap((m, i) => (m.k === pat || m.k.startsWith(pat + '(') || m.k.startsWith(pat + '`') ? [i] : []));

export type Clo = { size: number; ids: number[]; ics: Map<string, number>; tags: Map<string, string>; own: Set<string>; path: (key: string) => string; mod: Set<string> };
// docs/133: static targets per callvirt edge (`caller|declared` method ids): the targets the closure enters (declared + overrides
// within ovHops) and the smallest visit depth of the caller.
export type Edges = Map<string, { d: number; kind: string; ts: Set<number> }>;
// ovHops (docs/127): virt/iface calls in methods fewer than ovHops calls from a root also enter every game override (CHA);
// mod overrides (managed-callgraph --mods) are collected in mod, not followed. Path steps into an override are marked "=>".
// ovHops (docs/127): virt/iface calls in visits fewer than ovHops calls from a root also enter overrides. Without a receiver
// callback: every game override (CHA). With one (typed run), each visit carries the class family of its `this`: roots get
// self (defaults to ownType); per call the callee's family is the hand-reviewed receiver (callback: caller, declared, caller's
// family, IL receiver -> family), else the receiver's static type from the IL (managed-callgraph receiverTypes; the caller's own family for
// ldarg.0), else the caller's family for a call between two methods on its base chain [inferred]; a virtual call with a
// family enters only that family's overrides (familyTargets), one without every override.
export function closure(roots: number[], ownType?: string, ovHops = 0, self = ownType, receiver?: (caller: number, decl: number, fam: string | undefined, rt: Recv | undefined) => string | undefined, edges?: Edges): Clo {
  const dist = new Map<number, number>(); const icsHit = new Map<string, number>(); const tags = new Map<string, string>(); const own = new Set<string>();
  const parent = new Map<number, number>(); const at = new Map<string, number>(); const ovEdge = new Set<number>(); const mod = new Set<string>();
  const seen = new Set<string>(); const fam0 = ovHops && receiver ? self : undefined;
  let q: [number, string | undefined][] = [...new Set(roots)].map((r) => [r, fam0]); q.forEach(([r]) => { dist.set(r, 0); seen.add(`${r}|${fam0}`); });
  for (let d = 0; q.length && d <= DEPTH; d++) {
    const next: [number, string | undefined][] = [];
    for (const [n, fam] of q) {
      const m = ix.methods[n];
      if (m.ic && ix.asms[m.a]?.name !== 'mscorlib' && !icsHit.has(m.ic)) { icsHit.set(m.ic, d); at.set(m.ic, n); }
      for (const [tag, det] of ownTags(ix as never, n, ics)) if (/^(YIELD|EVENT|STATIC_WRITE|STATIC_COLL)$/.test(tag)) { const key = `${tag} ${det}`; if (!tags.has(key)) { tags.set(key, m.k.replace(/\(.*$/s, '')); at.set(key, n); } }
      if (ownType) for (const f of m.fw ?? []) if (ix.fields[f].startsWith(ownType + '::') && !own.has(ix.fields[f])) { own.add(ix.fields[f]); at.set(ix.fields[f], n); }
      for (let k = 0; k < m.c.length; k += 2) {
        const t = m.c[k], kind = ix.kinds[m.c[k + 1]];
        if (kind === 'dlg') continue;
        // Family of the callee's `this`: hand-reviewed site, else the receiver's static type from the IL (the caller's own
        // family when the receiver is ldarg.0), else the caller's family for a call between methods on its chain.
        let tf: string | undefined;
        if (ovHops && receiver) {
          const rt = receiverTypes(n).get(t);
          const onThis = fam !== undefined && onChain(fam, n) && onChain(fam, t);
          tf = receiver(n, t, fam, rt) ?? (rt?.isThis && onThis ? fam : rt?.type ?? (onThis ? fam : undefined));
        }
        const ts: number[] = [t];
        if (d < ovHops && (kind === 'virt' || kind === 'iface')) {
          const ft = tf ? familyTargets(t, tf) : undefined;
          const v = ft ? { ea: ft, mod: [] } : virtualTargets(t); ts.push(...v.ea); v.mod.forEach((x) => mod.add(x));
        }
        if (edges && (kind === 'virt' || kind === 'iface' || kind === 'cv')) {
          const e = edges.get(`${n}|${t}`) ?? { d, kind, ts: new Set<number>() };
          e.d = Math.min(e.d, d); ts.forEach((u) => e.ts.add(u)); edges.set(`${n}|${t}`, e);
        }
        for (const u of ts) {
          if (CUT.some((c) => ix.methods[u].k.startsWith(c)) || d >= DEPTH) continue;
          const key = `${u}|${tf}`;
          if (seen.has(key)) continue;
          seen.add(key); next.push([u, tf]);
          if (!dist.has(u)) { dist.set(u, d + 1); parent.set(u, n); if (u !== t) ovEdge.add(u); }
        }
      }
    }
    q = next;
  }
  const short = (i: number) => ix.methods[i].k.replace(/\(.*$/s, '').replace(/^.*[.+]([^.+]+::)/, '$1');
  const path = (key: string) => { let n = at.get(key); let p = ''; while (n !== undefined) { const pn = parent.get(n); p = (pn === undefined ? '' : ovEdge.has(n) ? ' => ' : ' > ') + short(n) + p; n = pn; } return p; };
  return { size: dist.size, ids: [...dist.keys()], ics: icsHit, tags, own, path, mod };
}

// docs/121 classes for an IC outside the snapshot set, from its docs/62 row [inferred: scan verdicts, not hand-read].
const WRITE_VERB = /::[A-Za-z]+_(Set|Add|Remove|Start|Stop|Save|Create|Destroy|Load|Play|Attach|Detach|Fire|Post|Push|Apply|Show|Hide|Enable|Disable|Reset|Clear|Update|Request|Trigger|Spawn|Release|Begin|End|Register|DeRegister)/;
// docs/126 §4.1 [verified: native bodies read to depth 4, I-126-1]: answered from a snapshot row filled at the cut.
const TIER2_ROW = new Set(['World_GetTerrainHeightImpl', 'World_GetTerrainSurfaceTypeImpl', 'World_GetTerrainTypeImpl', 'World_GetFloorSurfaceTypeImpl',
  'World_HasFloorFeaturesImpl', 'World_HasFloorFeaturesWorldPosImpl', 'World_GetTilesWithFloorFeaturesImpl', 'World_IsCameraRoutableImpl', 'World_IsOnPlatformImpl',
  'World_IsInPoolImpl', 'World_HasPoolImpl', 'World_GetPoolObjectIdForPoolBelowPositionImpl', 'World_GetPondAtPoint', 'World_GetPondInfo',
  'World_FindPondRepresentativePositions', 'World_GetLotLocationAtFloorImpl', 'World_GetRoomIdImpl', 'World_GetInformationAboutRoomImpl',
  'World_LotIsEntranceFromTargetPointImpl', 'World_LotGetDisplayLevelInfoImpl', 'World_GetWorldToHouseboatDisplayPositionImpl', 'Objects_GetForward',
  'Objects_GetBoundingBox', 'Objects_IsValid', 'World_LightGetDimmerImpl', 'World_GetWorldFileName']);
// docs/126 §4.1 + docs/131 §9 [verified: direct calls to 0x831710 at hop 5]: rows whose fill runs the lazy terrain
// footprint update under the steering lock +0x918 when the lot level bit +0x168 is clear: fill at the cut only when the bit is set.
const TIER2_CUT = new Set(['World_GetTerrainSurfaceTypeImpl', 'World_GetFloorSurfaceTypeImpl', 'World_GetInformationAboutRoomImpl']);
// docs/126 §8 / docs/219 §2 [verified: host selftest per row; Mode R dry compare pending]: rows built behind [AutShadow]
// Tier2Rows (+ Tier2IcRows): class `row`, no longer a new row. Each lane adds its group here once its selftest passes.
export const TIER2_BUILT = new Set(['World_GetPoolObjectIdForPoolBelowPositionImpl', 'Objects_GetForward',
  // G1 terrain and floor (docs/data/tier2-rows-handoff.md)
  'World_GetTerrainHeightImpl', 'World_GetTerrainTypeImpl', 'World_HasFloorFeaturesImpl', 'World_HasFloorFeaturesWorldPosImpl',
  'World_IsCameraRoutableImpl', 'World_IsOnPlatformImpl',
  // G2 pools, G3 lot and room, G4 object fields and session
  'World_IsInPoolImpl', 'World_HasPoolImpl', 'World_GetPondAtPoint', 'World_GetRoomIdImpl', 'World_LotIsEntranceFromTargetPointImpl',
  'Objects_GetBoundingBox', 'Objects_IsValid', 'World_LightGetDimmerImpl', 'World_GetWorldFileName',
  // E rows: write mask (E1) and ulong[] copy (E2), docs/data/tier2-enablers-handoff.md
  'World_GetTilesWithFloorFeaturesImpl', 'World_FindPondRepresentativePositions', 'World_GetLotLocationAtFloorImpl',
  'World_GetWorldToHouseboatDisplayPositionImpl', 'World_GetHouseboatDisplayToWorldPositionImpl']);
export function icClass(name: string): string {
  const meth = name.slice(name.indexOf('::'));
  if (snap.has(meth)) return snap.get(meth)!;
  if (TIER2_BUILT.has(meth.slice(2))) return 'row';
  if (TIER2_CUT.has(meth.slice(2))) return 'a-cut';
  if (TIER2_ROW.has(meth.slice(2))) return 'a-row';
  if (/::Random_/.test(name)) return 'b';
  // Hand overrides [inferred from docs/62 reads + docs/121 §2.7]: placement tests and road search walk footprints, the
  // quadtree and the routing planners (unbounded domain); the slot matrix uses the slot helper of GetPositionOfSlot;
  // World_GetObjects is a world-wide physics query like Query_GetObjects.
  if (/::(World_TestObjectPlacement\w*|UserToolUtils_CheckObjectPlacementImpl|World_FindPlacesOnRoad|Slots_GetMatrixOfSlotImpl)$/.test(name)) return 'b';
  if (/::World_GetObjectsImpl$/.test(name)) return 'd';
  if (/::MonoScriptHost_GetEnumSimple/.test(name)) return 'c'; // registered by MonoScriptHost_Init itself: enum name from metadata (docs/119)
  if (/::(EAText_|LocalizedStringService_|OnlineFeaturesHost_GetPersonaName)/.test(name)) return 'a-stub';
  const r = icRow.get(name) ?? [...icRow.values()].find((x) => x.ic.endsWith(meth));
  if (!r) return 'd';
  if (r.verdict === 'SAFE') return 'c';
  if (r.verdict === 'UNSAFE' || r.verdict === 'MAIN') return 'b';
  if (WRITE_VERB.test(name)) return 'w';
  // docs/62 calls every void IC "queued"; a Get/Is/Has/Find/Test name with a void return answers through an out-parameter.
  if (r.proxy === 'queued' && !/::[A-Za-z]+_(Get|Is|Has|Find|Test)/.test(name)) return 'w';
  return 'a';
}

// Pilot union: allow-list v0, the 7 implementors, the meta base path. Its managed writes were triaged in docs/122 §12.2.
export const allow = csv('docs/data/autonomy-allow.csv');
export const inV0 = new Set(allow.filter((r) => r.verdict === 'in').map((r) => r.definition_type));
export const IMPL = ['Sims3.Gameplay.Core.Lot', 'Sims3.Gameplay.Abstracts.RabbitHole', 'Sims3.Gameplay.Autonomy.HomeMetaObject', 'Sims3.Gameplay.Autonomy.WorkMetaObject',
  'Sims3.Gameplay.Autonomy.GardeningMetaObject', 'Sims3.Gameplay.Pools.WorldPondHelper', 'Sims3.Gameplay.Objects.Miscellaneous.BakeSaleTable'];
const BASE: Record<string, string> = {
  'Autonomy.CheckAvailability': 'Sims3.Gameplay.Autonomy.Autonomy::CheckAvailability',
  'InteractionDefinitionUtilities.CommonTests': 'Sims3.Gameplay.Interactions.InteractionDefinitionUtilities::CommonTests',
  'C2 self': 'Sims3.Gameplay.Autonomy.Autonomy::CalculateScoreAndAddToCandidates',
  'MetaInteractionDefinitionUtilities.SpecialCaseTests': 'Sims3.Gameplay.Interactions.MetaInteractionDefinitionUtilities::SpecialCaseTests',
  'GameObject.get_Parent': 'Sims3.Gameplay.Abstracts.GameObject::get_Parent',
  'InteractionDefinitionUtilities.SpecialCaseTests': 'Sims3.Gameplay.Interactions.InteractionDefinitionUtilities::SpecialCaseTests',
  'Autonomy.CalculateScoreForObjectInteraction': 'Sims3.Gameplay.Autonomy.Autonomy::CalculateScoreForObjectInteraction',
  'Autonomy.UpdateCacheIfNeeded': 'Sims3.Gameplay.Autonomy.Autonomy::UpdateCacheIfNeeded',
  'LiveDragHelperModel.get_CachedTopDraggedObject': 'Sims3.Gameplay.UI.LiveDragHelperModel::get_CachedTopDraggedObject',
  'Autonomy.CheckAvailabilityTooltip': 'Sims3.Gameplay.Autonomy.Autonomy::CheckAvailabilityTooltip',
  'Motives.FunInteractionTest': 'Sims3.Gameplay.Autonomy.Motives::FunInteractionTest',
  'InteractionTuning.HasFlags': 'Sims3.Gameplay.Autonomy.InteractionTuning::HasFlags',
  'Sim.get_Motives': 'Sims3.Gameplay.Actors.Sim::get_Motives', 'Sim.get_Autonomy': 'Sims3.Gameplay.Actors.Sim::get_Autonomy',
  'Sim.get_IsSelectable': 'Sims3.Gameplay.Actors.Sim::get_IsSelectable', 'Tradeoff.get_FunExit': 'Sims3.Gameplay.Autonomy.Tradeoff::get_FunExit',
};
const PILOT_BASE = ['Autonomy.CheckAvailability', 'InteractionDefinitionUtilities.CommonTests', 'C2 self', 'MetaInteractionDefinitionUtilities.SpecialCaseTests', 'GameObject.get_Parent'];
const pilotRoots = [...[...inV0].flatMap((t) => defRoots(ix as never, t)), ...IMPL.flatMap((t) => starts(`${t}::HowMuchItWantsSimToCome`)), ...PILOT_BASE.flatMap((b) => starts(BASE[b]))];
export const pilot = closure(pilotRoots);
const pilotIcs = [...pilot.ics.keys()].filter((n) => !snap.has(n.slice(n.indexOf('::'))));
if (pilotIcs.length) console.error(`pilot union ICs outside the snapshot set (expected none): ${pilotIcs.join(' ')}`);

// Candidates.
const entries = csv('docs/data/autonomy-entry-list.csv');
export type Cand = { group: string; rank: string; samples: string; share: string; ctx: string; name: string; asm: string; roots: number[]; own?: string };
export const cands: Cand[] = [];
const defCand = (group: string, e: Record<string, string> | undefined, t: string, asm = '') => {
  let roots: number[] = []; try { roots = defRoots(ix as never, t); } catch { /* not a type in the index */ }
  cands.push({ group, rank: e?.rank ?? '', samples: e?.samples ?? '', share: e?.share_of_scoring ?? '', ctx: e?.context ?? '', name: t, asm, roots, own: t });
};
const metaAll = (subtypes('Sims3.Gameplay.Interactions.MetaInteractionDefinition`3') ?? []).filter((x) => !x.abstract);
const metaSet = new Set(metaAll.map((x) => x.full));
const rowOf = (t: string) => entries.find((e) => e.definition_type.split(' | ').includes(t));
for (const m of metaAll) if (!inV0.has(m.full)) defCand('meta-rest', rowOf(m.full), m.full, m.asm);
for (const e of entries) {
  const d = e.definition_type;
  if (e.kind === 'base') { const b = d.replace(/^base path: /, ''); if (!PILOT_BASE.includes(b) && BASE[b]) cands.push({ group: 'base-local', rank: e.rank, samples: e.samples, share: e.share_of_scoring, ctx: e.context, name: d, asm: '', roots: starts(BASE[b]) }); continue; }
  if (e.kind === 'scoring-function') { const m = d.match(/^scoring function (\w+)\.(\w+)$/)!; cands.push({ group: 'scoring-function', rank: e.rank, samples: e.samples, share: e.share_of_scoring, ctx: e.context, name: d, asm: '', roots: ix.methods.flatMap((x, i) => (x.k.includes(`.${m[1]}::${m[2]}(`) ? [i] : [])) }); continue; }
  if (/unresolved|MetaInteractionScoredByVenue/.test(d)) continue;
  for (const t of d.split(' | ')) if (!inV0.has(t) && !metaSet.has(t) && !cands.some((c) => c.name === t)) {
    // docs/219 P4: a sampled abstract base (Phone+Call+CallDefinition`1, 63 subclasses) stands for its concrete subclasses; one candidate per
    // subclass, so its roots get the subclass family and only that subclass's Test is reached (the base row's samples split evenly [inferred]).
    const all = subtypes(t) ?? [], conc = all.filter((x) => !x.abstract);
    if (all.some((x) => x.full === t && x.abstract) && conc.length) { for (const x of conc) if (!inV0.has(x.full) && !metaSet.has(x.full) && !cands.some((c) => c.name === x.full)) defCand('local-sampled', e, x.full, x.asm); }
    else defCand('local-sampled', e, t, e.assembly);
  }
}
// Row 5: definitions named "Definition" outside meta whose own Test calls a generic base Test (docs/75 §2 resolution rule).
const row5 = entries.find((e) => /unresolved: \d+ candidates; callee base Test; commodity/.test(e.definition_type))!;
const GEN = /^Sims3\.Gameplay\.Interactions\.(InteractionDefinition`3|SoloSimInteractionDefinition`1|ImmediateInteractionDefinition`3|ActorlessInteractionDefinition`3|InteractionDefinition`4)::Test\(/;
const testsOf = new Map<string, Rec[]>();
for (const m of ix.methods) { const i = m.k.indexOf('::Test('); if (i > 0) { const t = m.k.slice(0, i); testsOf.set(t, [...(testsOf.get(t) ?? []), m]); } }
for (const t of (subtypes('Sims3.Gameplay.Interactions.InteractionDefinition`3') ?? []).filter((x) => !x.abstract && /[.+]Definition$/.test(x.full) && !metaSet.has(x.full))) {
  const own = testsOf.get(t.full) ?? [];
  if (!own.some((m) => { for (let k = 0; k < m.c.length; k += 2) if (GEN.test(ix.methods[m.c[k]].k)) return true; return false; })) continue;
  if (!cands.some((c) => c.name === t.full)) defCand('row5-candidate', { ...row5, samples: '', share_of_scoring: '' }, t.full, t.asm);
}

// Classify one closure by the docs/126 §1 rules.
export function classify(c: Cand, r: Clo) {
  const newIcs = [...r.ics].filter(([n]) => !snap.has(n.slice(n.indexOf('::')))).map(([n, d]) => [n, icClass(n), d] as const);
  const abortB = [...r.ics.keys()].filter((n) => snap.get(n.slice(n.indexOf('::'))) === 'b').map((n) => n.replace(/^.*::/, ''));
  const blockers: string[] = []; const keys: string[] = []; const warm: string[] = [];
  for (const [n, cl, d] of newIcs) if (/^(b|w|d)$/.test(cl)) { blockers.push(`ic-${cl} ${n.replace(/^.*::/, '')}@${d}`); keys.push(n); }
  for (const [key, where] of r.tags) if (!pilot.tags.has(key) && PREWARM.test(key) && /^STATIC/.test(key)) warm.push(key.replace(/^\w+ .*\.([^.]+::)/, '$1'));
    else if (!pilot.tags.has(key)) { keys.push(key); blockers.push(`${key.replace(/^(\w+) .*::/, '$1 ')} (${where.replace(/^.*\./, '')})`); }
  for (const f of r.own) { keys.push(f); blockers.push(`OWN_FIELD_WRITE ${f.replace(/^.*::/, '')}`); }
  const hooked = c.roots.every((i) => CUT.some((x) => ix.methods[i].k.startsWith(x)));
  const verdict = hooked ? 'hooked' : blockers.length ? 'blocked' : newIcs.some(([, cl]) => /^a/.test(cl)) ? 'new-rows' : 'ready';
  const reason = hooked ? 'root is a worker hook site (docs/126 §3): the worker answers there' : blockers[0] ?? (verdict === 'new-rows' ? `new IC ${newIcs.find(([, cl]) => /^a/.test(cl))![0].replace(/^.*::/, '')}` : newIcs.some(([, cl]) => cl === 'row') ? `built rows only (docs/126 §8, Mode R dry compare pending): ${newIcs.filter(([, cl]) => cl === 'row').map(([n]) => n.replace(/^.*::/, '')).join(' ')}` : 'no IC or managed write outside the pilot');
  return { newIcs, abortB, blockers, keys, warm, verdict, reason };
}

if (/autonomy-tier2\.ts$/.test(process.argv[1] ?? '') && process.argv.includes('--self-check')) {
  // docs/219 P4: the sampled abstract Phone base is split into its concrete subclasses, each with its own roots; no candidate is an abstract base.
  const fail = (m: string) => { throw new Error('autonomy-tier2 self-check: ' + m); };
  const base = 'Sims3.Gameplay.Objects.Electronics.Phone+Call+CallDefinition`1';
  const phone = cands.filter((c) => c.group === 'local-sampled' && c.name.startsWith('Sims3.Gameplay.Objects.Electronics.Phone+'));
  if (cands.some((c) => c.name === base)) fail('the abstract CallDefinition base is still a candidate');
  if (phone.length < 50 || phone.some((c) => !c.roots.length)) fail(`Phone subclasses: ${phone.length} candidates, ${phone.filter((c) => !c.roots.length).length} without roots`);
  if (!phone.some((c) => c.name.endsWith('Phone+CallBandToLocation+Definition'))) fail('CallBandToLocation is not a candidate');
  if (new Set(cands.map((c) => c.name)).size !== cands.length) fail('duplicate candidate names');
  console.log(`autonomy-tier2 self-check ok: ${cands.length} candidates, ${phone.length} Phone subclasses split from the base row`); process.exit(0);
}
if (/autonomy-tier2\.ts$/.test(process.argv[1] ?? '')) {
const out = ['group,entry_rank,definition_type,assembly,row_samples,row_share,context,closure_methods,ea_ics,new_ics,abort_branch_ics,blockers,prewarm,verdict,first_reason,first_blocker_path'];
const esc = (s: string) => (/[",]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s);
const tally: Record<string, Record<string, number>> = {}; const newIcCount = new Map<string, number>(); const swCount = new Map<string, number>();
for (const c of cands) {
  if (!c.roots.length) { out.push([c.group, c.rank, c.name, c.asm, c.samples, c.share, c.ctx, '0', '0', '', '', '', '', 'unknown', 'no roots in the index', ''].map(esc).join(',')); continue; }
  const r = closure(c.roots, c.own);
  const { newIcs, abortB, blockers, keys, warm, verdict, reason } = classify(c, r);
  for (const k of r.tags.keys()) if (/^STATIC/.test(k) && !pilot.tags.has(k) && !PREWARM.test(k)) swCount.set(k, (swCount.get(k) ?? 0) + 1);
  for (const [n, cl] of newIcs) newIcCount.set(`${n}\t${cl}`, (newIcCount.get(`${n}\t${cl}`) ?? 0) + 1);
  (tally[c.group] ??= {})[verdict] = (tally[c.group][verdict] ?? 0) + 1;
  out.push([c.group, c.rank, c.name, c.asm, c.samples, c.share, c.ctx, String(r.size), String(r.ics.size), newIcs.map(([n, cl]) => `${n.replace(/^.*::/, '')}:${cl}`).join(';'), [...new Set(abortB)].join(';'), blockers.slice(0, 6).join(';') + (blockers.length > 6 ? `;+${blockers.length - 6}` : ''), [...new Set(warm)].join(';'), verdict, reason, keys.length ? r.path(keys[0]) : ''].map(esc).join(','));
}
console.error(`pilot union: ${pilot.size} methods, ${pilot.ics.size} EA ICs, ${pilot.tags.size} managed write/yield/event sites`);
console.error(`candidates ${cands.length}: ${JSON.stringify(tally)}`);
console.error('new ICs (defs reaching each, docs/121 class):'); for (const [k, v] of [...newIcCount].sort((a, b) => b[1] - a[1])) console.error(`  ${v}\t${k}`);
console.error('new managed sites (top 40):'); for (const [k, v] of [...swCount].sort((a, b) => b[1] - a[1]).slice(0, 40)) console.error(`  ${v}\t${k}`);
console.log(out.join('\n'));
}
