// docs/336 §9: guard that deadrefslimuse stays exact. Scans the managed call graph of the game assemblies
// (game-copy/analysis/callgraph/index.json, built from game-copy/assemblies) for every method that both touches a use /
// reference / routing-reference list (GameObject.get_ActorsUsingMe / get_ReferenceList / get_RoutingReferenceList, or the
// fields mActorsUsingMe / mReferenceList / mRoutingReferenceList) and calls a list Add / Insert / AddRange / InsertRange.
// That over-approximates the writers; every hit must be in KNOWN below: covered by a ts3fixes hook (the hook class must
// exist in mods/ts3fixes/src and be Patch()ed), covered through its only callers ("via"), or a reviewed reader (it adds
// to a list of its own; decompiled check, docs/336 §9). A new hit (a game update, a new EP DLL) or a stale row fails.
//   node tools/uselist-writers.ts               the gate (needs the index; local only)
//   node tools/uselist-writers.ts --self-check  the gate plus unit checks on a fixture
import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..');
const INDEX = join(ROOT, 'game-copy', 'analysis', 'callgraph', 'index.json');
const SRC = join(ROOT, 'mods', 'ts3fixes', 'src');

type Cover = { hook: string } | { via: string[] } | { reader: true };
// method key up to '(' -> how its write is covered
export const KNOWN: Record<string, Cover> = {
  // the hooked helpers (LC-5, DeadRefsDelta.cs)
  'Sims3.Gameplay.Abstracts.GameObject::AddToUseList': { hook: 'DrUseAddHook' },
  'Sims3.Gameplay.Abstracts.GameObject::AddToReferenceList': { hook: 'DrRefAddHook' },
  'Sims3.Gameplay.Abstracts.GameObject::AddToRoutingReferenceList': { hook: 'DrRouteRefAddHook' },
  'Sims3.Gameplay.Objects.CookingObjects.ServingContainer::AddToUseList': { hook: 'DrServeUseAddHook' },
  // the 17 direct ActorsUsingMe.Add sites (DeadRefsPin.cs)
  'Sims3.Gameplay.Objects.Alchemy.PhilosopherStone+TransmuteGold::Run': { hook: 'DrPinStoneHook' },
  'Sims3.Gameplay.Objects.CookingObjects.PicnicPosture::.ctor': { via: ['Sims3.Gameplay.Objects.CookingObjects.PicnicBasket+SitDown::Run'] },
  'Sims3.Gameplay.Objects.CookingObjects.PicnicBasket+SitDown::Run': { hook: 'DrPinPicnicHook' },
  'Sims3.Gameplay.Objects.Electronics.PortableMp3Player::StartMusic': { hook: 'DrPinMp3Hook' },
  'Sims3.Gameplay.Objects.Environment.BonehildaCoffin::SpawnBonehilda': { hook: 'DrPinBonehildaHook' },
  'Sims3.Gameplay.Objects.HobbiesSkills.GnubbField::GetTeam': { hook: 'DrPinGnubbHook' },
  'Sims3.Gameplay.Objects.HobbiesSkills.Shuffleboard::GetTeam': { hook: 'DrPinShuffleHook' },
  'Sims3.Gameplay.Objects.Insect.Terrarium+RadiationExperiment::OnEpicSuccess': { hook: 'DrPinTerrariumHook' },
  'Sims3.Gameplay.Objects.RabbitHoles.Subway+PlayForTips::StartPlayingForTips': { hook: 'DrPinSubwayHook' },
  'Sims3.Store.Objects.ChickenCoopClassic+HarvestEggs::Run': { hook: 'DrPinCoopHook' },
  'Sims3.Store.Objects.FVShoppingRegister+TendRegister::Run': { hook: 'DrPinFvHook' },
  'Sims3.Store.Objects.IceCreamMaker+MakeDeepFriedIceCreamFritter::Run': { hook: 'DrPinIceCreamHook' },
  'Sims3.Store.Objects.LemonadeStand+GetPsychiatricHelp::Run': { hook: 'DrPinLemonHelpHook' },
  'Sims3.Store.Objects.LemonadeStand+Purchase::Run': { hook: 'DrPinLemonBuyHook' },
  'Sims3.Store.Objects.LemonadeStand+Sell::Run': { hook: 'DrPinLemonSellHook' },
  'Sims3.Store.Objects.PlaypenClassic+InPenIdle::Run': { hook: 'DrPinPlaypenHook' },
  'Sims3.Store.Objects.TeppanyakiGrill+TGCook::Run': { hook: 'DrPinTeppanyakiHook' },
  // reviewed readers: they read a list and Add to a list of their own (removals only where they touch the object's list)
  'Sims3.Gameplay.Objects.Pets.HitchingPost+TieUpHorseA::Run': { reader: true },
  'Sims3.Gameplay.Objects.RabbitHoles.GetDrinks::PlaySocialGame': { reader: true },
  'Sims3.Gameplay.Objects.RabbitHoles.GreetingCardRabbitHole::SimsForPhoto': { reader: true },
  'Sims3.Gameplay.Objects.Pets.BoxStall::GetHorseOccupants': { reader: true },
  'Sims3.Gameplay.Objects.Plumbing.Urinal+UseUrinal::LoopFunc': { reader: true },
  'Sims3.Gameplay.Objects.Plumbing.HotTubBase::GetSkinnyDippers': { reader: true },
  'Sims3.Gameplay.Objects.RabbitHoles.Stadium::PlayerWinPercentage': { reader: true },
  'Sims3.Gameplay.Objects.Electronics.TV+PlayVideoGameSystemOnTV::Run': { reader: true },
  'Sims3.Gameplay.Objects.RabbitHoles.Subway::MuggingAlarmCallback': { reader: true },
  'Sims3.Gameplay.Objects.HobbiesSkills.Shuffleboard+Play::Run': { reader: true },
  'Sims3.Gameplay.Objects.Environment.Treehouse+PlayIn::GetRandomPart': { reader: true },
  'Sims3.Gameplay.Abstracts.GameObject::ValidateReferenceListAndUseList': { reader: true },
  'Sims3.Gameplay.Abstracts.GameObject::GetUniqueReferenceList': { reader: true },
  'Sims3.Gameplay.Abstracts.GameObject::get_ActorsUsingMeAsSimDescriptions': { reader: true },
  'Sims3.Gameplay.Services.ServiceSituation+ServiceNPCSteal::Run': { reader: true },
  'Sims3.Gameplay.Pools.Ocean+AutonomousSplash::Run': { reader: true },
  'Sims3.Gameplay.Objects.Vehicles.CarpoolManager+CarpoolData+DoCarpoolPickupAndDropoff::Run': { reader: true },
  'Sims3.Gameplay.EditTownModel::ChangeLotTypeHelper': { reader: true },
  'Sims3.Gameplay.Objects.Environment.Snowman+MakeSnowman::FinalizeSnowmanType': { reader: true },
  'Sims3.Gameplay.Abstracts.RabbitHole::GetRomanticSims': { reader: true },
  'Sims3.Gameplay.Skills.DetonateObjectHelper::GetActorsUsingObject': { reader: true },
  'Sims3.Gameplay.Services.BurglarSituation+Stealing+Steal::Run': { reader: true },
  'Sims3.Gameplay.MapTags.WorkMapTag::get_SimsInside': { reader: true },
  'Sims3.Gameplay.MapTags.RabbitHoleMapTag::get_SimsInside': { reader: true },
  'Sims3.Gameplay.MapTags.ShellMapTag::get_SimsInside': { reader: true },
  'Sims3.Gameplay.Pools.WorldPond::OnStartUp': { reader: true },
  'Sims3.Gameplay.Objects.SnowWaterFight::ControllerCallback': { reader: true },
  'Sims3.Gameplay.Core.RentScheduler+RentalInformation::OnRentalStarted': { reader: true },
  'Sims3.Gameplay.Objects.Misc.BoxOfMystery+PerformTrick::Run': { reader: true },
  'Sims3.Gameplay.Objects.Misc.BoxOfMystery+WooHooInBoxOfMystery::Run': { reader: true },
  'Sims3.Gameplay.Objects.DigSite+ExcavateBase::DoRoute': { reader: true },
  'Sims3.Gameplay.Moving.GameplayMovingModel::PackFurniture': { reader: true },
  'Sims3.Gameplay.ActorSystems.OccultImaginaryFriend::InteractionUpdateLoop': { reader: true },
  'Sims3.Store.Objects.PlaypenClassic+TakeOut+SetupDefinition::AddInteractions': { reader: true },
  'Sims3.Store.Objects.PlaypenClassic+PlayWithOutsideSim+SetupDefinition::AddInteractions': { reader: true },
};

type Ix = { kinds: string[]; methods: { k: string; c: number[]; fr?: number[]; fw?: number[] }[]; fields: string[] };
const LIST_ADD = /List`1.*::(Add|Insert|AddRange|InsertRange)\(/;
const GETTER = /^Sims3\.Gameplay\.Abstracts\.GameObject::get_(ActorsUsingMe|ReferenceList|RoutingReferenceList)\(/;
const FIELD = /^Sims3\.Gameplay\.Abstracts\.GameObject::(mActorsUsingMe|mReferenceList|mRoutingReferenceList)$/;
const keyOf = (k: string) => k.replace(/\(.*$/s, '');

// method keys (up to '(') that may write a use / reference / routing list, plus each one's callers
export function candidates(ix: Ix): { hits: string[]; callers: Map<string, Set<string>> } {
  const add = new Set<number>(), get = new Set<number>(), fld = new Set<number>();
  ix.methods.forEach((m, i) => { if (LIST_ADD.test(m.k)) add.add(i); if (GETTER.test(m.k)) get.add(i); });
  ix.fields.forEach((f, i) => { if (FIELD.test(f)) fld.add(i); });
  const hits = new Set<string>(), callers = new Map<string, Set<string>>();
  ix.methods.forEach((m) => {
    let a = false, l = false;
    for (let k = 0; k < m.c.length; k += 2) {
      const t = m.c[k];
      if (add.has(t)) a = true;
      if (get.has(t)) l = true;
      const tk = keyOf(ix.methods[t].k);
      let s = callers.get(tk); if (!s) callers.set(tk, s = new Set()); s.add(keyOf(m.k));
    }
    for (const f of [...(m.fr ?? []), ...(m.fw ?? [])]) if (fld.has(f)) l = true;
    if (a && l) hits.add(keyOf(m.k));
  });
  return { hits: [...hits].sort(), callers };
}

// problems: unknown hits, stale rows, hooks missing from the source, via rows with other callers
export function gate(hits: string[], callers: Map<string, Set<string>>, known: Record<string, Cover>, src: string): string[] {
  const out: string[] = [];
  const hs = new Set(hits);
  for (const h of hits) if (!known[h]) out.push(`NEW use/reference list writer candidate: ${h} (hook it in DeadRefsPin.cs or review it as a reader, docs/336 §9)`);
  for (const [k, c] of Object.entries(known)) {
    const viaTarget = Object.values(known).some((v) => 'via' in v && v.via.includes(k)); // a hooked caller of a "via" row
    if (!hs.has(k) && !viaTarget) out.push(`stale row (no longer a candidate): ${k}`);
    if ('hook' in c && !new RegExp(`typeof\\(${c.hook}\\)`).test(src)) out.push(`${k}: hook ${c.hook} is not patched in mods/ts3fixes/src`);
    if ('via' in c) for (const caller of callers.get(k) ?? []) if (!c.via.includes(caller)) out.push(`${k}: called from ${caller}, not only from ${c.via.join(', ')}`);
  }
  return out;
}

function selfCheck(): void {
  const ix: Ix = {
    kinds: ['call', 'newobj'],
    fields: ['Sims3.Gameplay.Abstracts.GameObject::mActorsUsingMe', 'X::other'],
    methods: [
      { k: 'System.Collections.Generic.List`1::Add(!0)', c: [] },
      { k: 'Sims3.Gameplay.Abstracts.GameObject::get_ActorsUsingMe()', c: [] },
      { k: 'A::Writer()', c: [1, 0, 0, 0] },
      { k: 'A::FieldWriter()', c: [0, 0], fw: [0] },
      { k: 'A::Reader()', c: [1, 0] },
      { k: 'A::OtherField()', c: [0, 0], fr: [1] },
      { k: 'P::.ctor(int)', c: [1, 0, 0, 0] },
      { k: 'Q::Run()', c: [6, 1] },
    ],
  };
  const { hits, callers } = candidates(ix);
  const want = ['A::FieldWriter', 'A::Writer', 'P::.ctor'];
  if (JSON.stringify(hits) !== JSON.stringify(want)) throw new Error(`candidates ${JSON.stringify(hits)}`);
  const src = 'Patch(typeof(X), "W", typeof(WHook), "H", "O");';
  const ok = gate(hits, callers, { 'A::Writer': { hook: 'WHook' }, 'A::FieldWriter': { reader: true }, 'P::.ctor': { via: ['Q::Run'] } }, src);
  if (ok.length) throw new Error(`clean fixture flagged: ${ok.join('; ')}`);
  const bad = gate(hits, callers, { 'A::Writer': { hook: 'Missing' }, 'P::.ctor': { via: ['Z::Run'] }, 'A::Gone': { reader: true } }, src);
  for (const want of ['NEW use/reference list writer candidate: A::FieldWriter', 'hook Missing is not patched', 'called from Q::Run', 'stale row (no longer a candidate): A::Gone'])
    if (!bad.some((b) => b.includes(want))) throw new Error(`fixture: expected "${want}" in ${JSON.stringify(bad)}`);
  console.log('uselist-writers self-check ok (5 cases)');
}

if (import.meta.main ?? process.argv[1]?.endsWith('uselist-writers.ts')) {
  if (process.argv.includes('--self-check')) selfCheck();
  if (!existsSync(INDEX)) { console.log(`uselist-writers: SKIP, no ${INDEX} (local only)`); process.exit(0); }
  const ix = JSON.parse(readFileSync(INDEX, 'utf8')) as Ix;
  const src = readdirSync(SRC).filter((f) => f.endsWith('.cs')).map((f) => readFileSync(join(SRC, f), 'utf8')).join('\n');
  const { hits, callers } = candidates(ix);
  const bad = gate(hits, callers, KNOWN, src);
  for (const b of bad) console.log(`FAIL ${b}`);
  const n = (t: string) => Object.values(KNOWN).filter((c) => t in c).length;
  console.log(`uselist-writers: ${hits.length} candidates, ${n('hook')} hooked, ${n('via')} via, ${n('reader')} readers, ${bad.length} problems`);
  process.exit(bad.length ? 1 : 0);
}
