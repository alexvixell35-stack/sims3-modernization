// docs/128 §5 G-M4 / G-M5: the static lists ts3fixes' [AutShadow] guard sweep checks at run time.
// Closure of each allow-list v0 definition (its Test/CalculateScore roots + the pilot's shared base path) and of each of the
// 7 implementors (HowMuchItWantsSimToCome, get_MetaAds), depth 12, same cuts as tools/autonomy-tier2.ts (the worker hooks
// answer there). Emits mods/ts3fixes/src/AutShadowLists.cs:
//   Methods: every managed game method of the union (BCL, generic types/methods and .cctor left out: L3 cannot check them)
//            -> G-M4 checks MonoMethod klass (L3) on each.
//   Slots:   declared targets of virtual / interface calls in the union (game assemblies only) -> G-M5 looks for mod
//            overrides; Owners/OwnerSlots: which definition or implementor closure dispatches through which slot.
// Keys use the call-graph index format ("Ns.Type+Nested::Name(int,Ns.T&,System.Collections.Generic.List`1<Ns.U>)"),
// which AutShadowCore.SigKey reproduces from reflection.
// Usage: node tools/autshadow-guard-lists.ts [--asm game-copy/assemblies] [--index game-copy/analysis/callgraph/index.json] [--check]
//   --check: compare with the committed file instead of writing it (exit 1 when they differ).
import { readFileSync, writeFileSync } from 'node:fs';
import { defRoots } from './managed-callgraph.ts';

const arg = (n: string, d: string) => { const i = process.argv.indexOf(n); return i > 0 ? process.argv[i + 1] : d; };
type Rec = { k: string; a: number; c: number[] };
const ix: { asms: { name: string }[]; kinds: string[]; methods: Rec[] } = JSON.parse(readFileSync(arg('--index', 'game-copy/analysis/callgraph/index.json'), 'utf8'));
const OUT = 'mods/ts3fixes/src/AutShadowLists.cs';
const DEPTH = 12;
const BCL = new Set(['mscorlib', 'System', 'System.Xml']);
// tools/autonomy-tier2.ts CUT (pilot worker hooks, docs/121 §1 cuts); keep in step with it
const CUT = ['Sims3.Gameplay.Seasons.SeasonsManager::LazyAllocateManager(', 'Sims3.Gameplay.Socializing.Relationship::Get(',
  'Sims3.Gameplay.Actors.Sim::IsAllowedInRoom(', 'Sims3.Gameplay.Actors.Sim::get_NumSimsDoingGardening(',
  'Sims3.Gameplay.Utilities.Localization::LocalizeString(', 'Sims3.Gameplay.Utilities.OnlineDatingManager::DidSimsMeetOnline(',
  'Sims3.Gameplay.Autonomy.MetaAutonomyTuning::GetSimCountForVenueWithCustomDesireFunction(',
  'Sims3.Gameplay.ChildAndTeenUpdates.Punishment::GetPunishmentManagerFromSim('];
const IMPL = ['Sims3.Gameplay.Core.Lot', 'Sims3.Gameplay.Abstracts.RabbitHole', 'Sims3.Gameplay.Autonomy.HomeMetaObject', 'Sims3.Gameplay.Autonomy.WorkMetaObject',
  'Sims3.Gameplay.Autonomy.GardeningMetaObject', 'Sims3.Gameplay.Pools.WorldPondHelper', 'Sims3.Gameplay.Objects.Miscellaneous.BakeSaleTable'];
// tools/autonomy-tier2.ts PILOT_BASE
const BASE = ['Sims3.Gameplay.Autonomy.Autonomy::CheckAvailability', 'Sims3.Gameplay.Interactions.InteractionDefinitionUtilities::CommonTests',
  'Sims3.Gameplay.Autonomy.Autonomy::CalculateScoreAndAddToCandidates', 'Sims3.Gameplay.Interactions.MetaInteractionDefinitionUtilities::SpecialCaseTests',
  'Sims3.Gameplay.Abstracts.GameObject::get_Parent'];
const starts = (pat: string) => ix.methods.flatMap((m, i) => (m.k.startsWith(pat + '(') || m.k.startsWith(pat + '`') ? [i] : []));
const dyn = new Set(['virt', 'iface', 'cons']);

function closure(roots: number[]): { methods: Set<number>; slots: Set<number> } {
  const seen = new Set<number>(), slots = new Set<number>();
  let q = [...new Set(roots)];
  for (let d = 0; q.length && d <= DEPTH; d++) {
    const next: number[] = [];
    for (const n of q) {
      if (seen.has(n)) continue;
      seen.add(n);
      const c = ix.methods[n].c;
      for (let k = 0; k < c.length; k += 2) {
        const t = c[k], kind = ix.kinds[c[k + 1]];
        if (kind === 'dlg' || CUT.some((x) => ix.methods[t].k.startsWith(x))) continue;
        if (dyn.has(kind) && !BCL.has(ix.asms[ix.methods[t].a]?.name)) slots.add(t);
        if (d < DEPTH) next.push(t);
      }
    }
    q = next;
  }
  return { methods: seen, slots };
}

const csv = readFileSync('docs/data/autonomy-allow.csv', 'utf8').trim().split(/\r?\n/);
const head = csv[0].split(','), col = (n: string) => head.indexOf(n);
const v0 = csv.slice(1).map((l) => l.split(',')).filter((r) => r[col('verdict')] === 'in').map((r) => r[col('definition_type')]);
const baseRoots = BASE.flatMap(starts);
const owners: { name: string; c: ReturnType<typeof closure> }[] = [
  ...v0.map((d) => ({ name: d, c: closure([...defRoots(ix as never, d), ...baseRoots]) })),
  ...IMPL.map((t) => ({ name: t, c: closure([...starts(`${t}::HowMuchItWantsSimToCome`), ...starts(`${t}::get_MetaAds`)]) })),
];
const checkable = (i: number) => {
  const m = ix.methods[i], decl = m.k.slice(0, m.k.indexOf('::')), name = m.k.slice(m.k.indexOf('::') + 2, m.k.indexOf('('));
  return !BCL.has(ix.asms[m.a]?.name) && !decl.includes('`') && !name.includes('`') && name !== '.cctor' && !m.k.includes('!');
};
const methodIds = [...new Set(owners.flatMap((o) => [...o.c.methods]))].filter(checkable).sort((a, b) => (ix.methods[a].k < ix.methods[b].k ? -1 : ix.methods[a].k > ix.methods[b].k ? 1 : 0));
const methods = methodIds.map((i) => ix.methods[i].k);
// G-M6: per method, bit o = owner o's closure reaches it (a method not from a checked build skips only those owners)
const methodOwners = methodIds.map((i) => owners.reduce((b, o, j) => (o.c.methods.has(i) ? b | (1 << j) : b), 0));
const ownerRows: string[] = [];
for (let j = 0; j < methodOwners.length; j += 16) ownerRows.push('            ' + methodOwners.slice(j, j + 16).map((b) => `${b},`).join(' '));
const slotIds = [...new Set(owners.flatMap((o) => [...o.c.slots]))].filter(checkable).sort((a, b) => ix.methods[a].k < ix.methods[b].k ? -1 : 1);
const slots = slotIds.map((i) => ix.methods[i].k);
const slotIdx = new Map(slotIds.map((i, j) => [i, j]));
const q = (s: string) => JSON.stringify(s);
const body = `// Generated by tools/autshadow-guard-lists.ts from the call-graph index (docs/128 §5 G-M4, G-M5). Do not edit by hand.
// ${v0.length} allow-list v0 definitions + ${IMPL.length} implementors, depth ${DEPTH}, pilot cuts. Method and type names only (no game code).
namespace Ts3Fixes
{
    public static class AutShadowLists
    {
        // G-M4: managed game methods of the closures (L3 on each)
        public static readonly string[] Methods = {
${methods.map((m) => `            ${q(m)},`).join('\n')}
        };
        // G-M6: per Methods entry, bit o = Owners[o]'s closure reaches it
        public static readonly int[] MethodOwners = {
${ownerRows.join('\n')}
        };
        // G-M5: declared virtual / interface call targets in the closures
        public static readonly string[] Slots = {
${slots.map((m) => `            ${q(m)},`).join('\n')}
        };
        // closure owner (definition or implementor type) and the Slots indexes it dispatches through
        public static readonly string[] Owners = { ${owners.map((o) => q(o.name)).join(', ')} };
        public static readonly string[] OwnerSlots = {
${owners.map((o) => `            ${q([...o.c.slots].filter((s) => slotIdx.has(s)).map((s) => slotIdx.get(s)).sort((a, b) => a! - b!).join(','))},`).join('\n')}
        };
    }
}
`.replace(/\n/g, '\r\n');
console.error(`methods ${methods.length}, slots ${slots.length}, owners ${owners.length} (per owner closure ${owners.map((o) => o.c.methods.size).join('/')})`);
if (process.argv.includes('--check')) {
  const cur = readFileSync(OUT, 'utf8');
  if (cur !== body) { console.error(`${OUT} differs from the index: rerun without --check`); process.exit(1); }
  console.error(`${OUT} up to date`);
} else writeFileSync(OUT, body);
