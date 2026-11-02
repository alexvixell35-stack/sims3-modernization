// docs/208: closures of the three docs/129 offload drivers (environment score, broadcaster candidate set, motive decay).
// The roots are the read part each ts3fixes driver (mods/ts3fixes/src/AutOffload.cs) calls, not the whole game method:
// the writes (buffs, events, reactions, SetValue) stay on the sim thread. Closure as docs/127 (depth 12, the pilot's cuts
// and worker hooks, every game override of a virtual/interface call within --hops of a root), ICs classed as docs/121/126
// (autonomy-tier2.ts icClass; '*' = already a snapshot row of the pilot), YIELD/EVENT/STATIC writes listed.
// Usage (from the checkout that has game-copy/analysis/callgraph):
//   node tools/offload3-closure.ts [--index game-copy/analysis/callgraph/index.json] [--hops 5] > docs/data/offload3-closure.csv
//   node tools/offload3-closure.ts --self-check     (root resolution only: every root pattern must name >= 1 method)
// Summary per candidate to stderr. Static only.
import { ix, closure, icClass, snap, starts, CUT } from './autonomy-tier2.ts';

const arg = (n: string, d: string) => { const i = process.argv.indexOf(n); return i > 0 ? process.argv[i + 1] : d; };
const HOPS = Number(arg('--hops', '5'));
const G = 'Sims3.Gameplay.';
// [pattern, filter on the full key] per driver read; '' = every overload
type Root = [string, string];
const DRIVERS: Record<string, Root[]> = {
  env: [
    [G + 'Core.Lot::GetEnvironmentScore', ''], [G + 'Abstracts.GameObject::GetEnvironmentScore', ''],
    [G + 'Interfaces.IModifyChildrenEnvironmentScores::GetModifiedEnvironmentScore', ''],
    [G + 'Abstracts.GameObject::get_EmitsLight', ''], [G + 'ActorSystems.TraitManager::HasElement', ''],
    [G + 'Objects.Electronics.ClubLight::get_IsBlackLight', ''],
    [G + 'Core.Lot::GetObjectsInRoom', ''], [G + 'Core.Lot::GetObjects', ''], [G + 'Queries::GetObjects', 'Vector3'],
    [G + 'Core.LotManager::LotsWithinRadialRange', ''], ['Sims3.SimIFace.World::TreesWithinRadialRange', ''],
    ['Sims3.SimIFace.World::GetInformationAboutRoom', ''], [G + 'Abstracts.GameObject::get_ComeAndSee', ''],
    [G + 'Abstracts.RoomConnectionObject::ConnectsToRoom', ''], [G + 'Abstracts.GameObject::get_IsOutside', ''],
    [G + 'Actors.Sim::get_RabbitHoleEnvironmentScore', ''], [G + 'Actors.SimUpdate::CalculateContributingObjectScore', ''],
    [G + 'Core.Lot::get_IsResidentialLot', ''], [G + 'Actors.Sim::get_LotHome', ''], [G + 'Abstracts.GameObject::get_LotCurrent', ''],
    [G + 'Abstracts.GameObject::get_Parent', ''], [G + 'Actors.Sim::get_IsOutside', ''],
  ],
  bcast: [
    [G + 'Autonomy.ReactionBroadcaster::GetPulseRadius', ''], [G + 'Queries::GetObjects', 'Vector3'],
    [G + 'Abstracts.GameObject::get_InWorld', ''], [G + 'Actors.Sim::get_RabbitHoleCurrent', ''],
    [G + 'CAS.SimDescription::get_IsBonehilda', ''], [G + 'CAS.SimDescription::get_Species', ''],
    [G + 'Controllers.TombRoomManager::IsObjectInATombRoom', 'IGameObject'], [G + 'Abstracts.GameObject::get_RoomId', ''],
    [G + 'Autonomy.ReactionBroadcaster::RoomCheck', ''], [G + 'Actors.Sim::CouldSeeObject', ''], [G + 'Actors.Sim::CanReact', ''],
    [G + 'Abstracts.GameObject::GetDistanceToObject', ''], [G + 'Actors.Sim::get_Posture', ''],
  ],
  // the line-of-sight test stays on the sim thread (docs/129 §2.2); listed apart to show what it would add
  'bcast-los': [[G + 'Actors.Sim::CanSeeObject', '']],
  motive: [[G + 'Autonomy.Motive::UpdateMotiveWithDecay', '']],
};
const rootsOf = (rs: Root[]) => rs.flatMap(([p, f]) => starts(p).filter((i) => !f || ix.methods[i].k.includes(f)));

if (process.argv.includes('--self-check')) {
  let bad = 0;
  for (const [c, rs] of Object.entries(DRIVERS)) for (const r of rs) if (!rootsOf([r]).length) { bad++; console.error(`self-check: ${c}: no method for ${r[0]}`); }
  console.error(bad ? `self-check: ${bad} unresolved roots` : 'self-check: ok');
  process.exit(bad ? 1 : 0);
}

const esc = (s: string) => (/[",]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s);
const out = ['candidate,kind,name,class,depth,path'];
// One closure: rows for the CSV, one summary line; returns the blockers (YIELD/EVENT/STATIC write, class b/w/d IC outside the pilot rows).
function run(c: string, roots: number[], quiet = false): string[] {
  const r = closure(roots, undefined, HOPS);
  const byClass = new Map<string, number>(); const block: string[] = [];
  for (const [n, d] of [...r.ics].sort((a, b) => a[1] - b[1])) {
    const cl = icClass(n) + (snap.has(n.slice(n.indexOf('::'))) ? '*' : '');
    byClass.set(cl, (byClass.get(cl) ?? 0) + 1);
    if (/^(b|w|d)$/.test(cl)) block.push(`${cl} ${n.replace(/^.*::/, '')}`);
    out.push([c, 'ic', n, cl, String(d), r.path(n)].map(esc).join(','));
  }
  // instance field writes anywhere in the closure (stfld; the tags above cover static writes only). Value-type fields
  // (Vector3, LotLocation, ...) written into locals are listed too: the doc triages them by hand.
  const fw = new Map<string, number>();
  for (const i of r.ids) for (const f of ix.methods[i].fw ?? []) { const k = ix.fields[f]; if (!fw.has(k)) fw.set(k, i); }
  for (const [k, i] of fw) out.push([c, 'field_write', k, '', '', ix.methods[i].k.replace(/\(.*$/s, '')].map(esc).join(','));
  for (const [k] of r.tags) {
    if (!k.startsWith('STATIC_COLL')) block.push(k.replace(/ .*::/, ' '));
    out.push([c, k.replace(/ .*$/, '').toLowerCase(), k.replace(/^\w+ /, ''), '', '', r.path(k)].map(esc).join(','));
  }
  const tagN = (t: string) => [...r.tags.keys()].filter((k) => k.startsWith(t)).length;
  if (!quiet) console.error(`${c}: roots ${roots.length}, closure ${r.size} methods (hops ${HOPS}), ICs ${r.ics.size} [${[...byClass].map(([k, n]) => `${k} ${n}`).join(', ')}], YIELD ${tagN('YIELD')}, EVENT ${tagN('EVENT')}, STATIC writes ${tagN('STATIC_WRITE')}, field writes ${fw.size}`);
  return block;
}
for (const [c, rs] of Object.entries(DRIVERS)) run(c, rootsOf(rs));
// Motive decay by posture (docs/208 §4): the only virtual step with writes is Posture.GetPostureMotiveDecayUpdate. Each game
// override on its own (verdict per posture class), then the decay with every override cut (the worker runs it only for a Sim
// whose posture chain has read-only overrides: AutOffload.cs kDecayPostures).
const ovs = ix.methods.flatMap((m, i) => (m.k.includes('::GetPostureMotiveDecayUpdate(') ? [i] : []));
for (const i of ovs) {
  const t = ix.methods[i].k.replace(/::.*$/, '');
  const b = run(`posture:${t}`, [i], true);
  console.error(`  posture ${t}: ${b.length ? 'blocked: ' + b.slice(0, 3).join('; ') + (b.length > 3 ? ` +${b.length - 3}` : '') : 'read-only'}`);
}
CUT.push(...ovs.map((i) => ix.methods[i].k.replace(/\(.*$/, '(')));
run('motive-core', rootsOf(DRIVERS.motive));
console.log(out.join('\n'));
