# 313. `commodmapfast`: exact one-pass `CommodityInteractionMap.RemoveObject` (2026-11-21, built, NOT RUN)

Switches `commodmapfast` and `commodmapshadow` (ts3fixes, `kCommodMapFast` / `kCommodMapShadow`), both default OFF in the
release package. Code: `mods/ts3fixes/src/CommodMapFast.cs` (hook), `CommodMapFastCore.cs` (game-independent plan),
`CommodMapFastChecks.cs` (harness check). Counters 1500-1507 (registry `fixes-1500`). Queue `commodmap-fast-1121`.
Ledger §568.

## 1. Why

A core-mod tester (retail 1.67, ~1070 packages) had 57 s and 13 s hitches whose ModTime owner was
`FestivalHiddenHook.UpdateCommodityInteractionMap`; the hook passes through and the time is EA's map update below it
(docs/856 §4, I-566-1, inferred). The worst row's mod frame is a mod's `OnWorldLoadFinished` moving objects.

What EA does on every object room or lot change [verified, decompiled CommodityInteractionMap.cs, GameObject.cs
5960-6008; the simler90 242 core mod has the same bodies, cosmetic diffs only]:

- `ChangeCommodityInteractionMap(new)`: `old.RemoveObject(this)`, then `new.AddObject(this)`.
- `RemoveObject(x)`: for each pair `p` in `x.Interactions`, for each output of `p.Tradeoff` that is valid for the map,
  `list(c).Remove(p)`, and the key goes when the list is then empty.
- `List<T>.Remove(p)` = `Array.IndexOf` with `EqualityComparer<T>.Default.Equals(p, e)` = `p.Equals(e)` from index 0 to
  the first true [verified, decompiled Mono 2 mscorlib: Array.IndexOf<T>, DefaultComparer]. `InteractionObjectPair.Equals`
  is `def.Equals(e.def) && Target == e.Target`, two or three interpreted virtual calls per element.

So k pairs of one object on a list of n entries cost up to k * n Equals chains; mods that add interactions to every object
make both k and n large, and a mod that moves many objects pays that per object. `AddObject` appends (cheap).

## 2. Design (exact)

Per `RemoveObject` call: collect EA's (commodity, pair) removals in EA's order with the same getters (nothing changes), group
them per commodity list, and remove them in one pass per list:

- Valid only when every pair's `Equals` is `InteractionObjectPair`'s own and its definition's `Equals(object)` is
  `object.Equals` (both checked per runtime type, cached). Then `p.Equals(e)` is `e != null && e.def == p.def &&
  e.Target == p.Target` by reference. Any pair with its own `Equals` (EA has a few: `PostureInteractionProxyForObjects`,
  `SingingInfo`, ...; mods can add more), a null definition, or any exception while planning: EA's body runs unchanged
  (counter 1502 / 1503). Planning changes nothing, so EA then sees the map as it was, and throws where EA throws.
- Proof (CommodMapFastCore.cs header): with reference keys, the pairs of key K remove exactly the first n(K) entries with
  key K of the list as found (n(K) = number of such pairs); entries with other keys never decide which K entry is first.
  One pass removes those and keeps every other entry in its order. The pass stops once every pair found its entry, past which
  EA compares nothing either. Lists of different commodities are distinct objects. The key is dropped when a planned list
  ends empty (EA drops it after any Remove that leaves the list empty, an already empty list included).
- Same result: same keys, same `List` objects, same entries, same order; no `Equals` calls a mod could observe. The
  validity test is EA's internal `ValidForCommodityInteractionMap` rebuilt from its public parts (`ConstantChange`,
  `Commodity`, `Commodities.IsCommodityGenerallyPositive`).
- Proven exact, so it applies to every map, household Sims included (CLAUDE.md: proven-exact speedups stay); there is no
  household exemption to make because no Sim's result changes.

`commodmapshadow`: plan, run EA's body (its result is kept), compare every planned list with the plan's prediction; the first
mismatch turns both switches off for the session and writes `commodmapfast: shadow mismatch on <type> (...)` to the native
log. Shadow also times both: 1506 = EA body us, 1507 = plan us over the same calls; a call where EA took >= 50 ms writes
`commodmapfast: shadow RemoveObject <type>: EA x ms, plan y ms, ...` (first 20).

Counters: 1500 calls with a switch on, 1501 batch applied, 1502 EA ran (own Equals / null definition), 1503 EA ran
(exception while planning), 1504 shadow compares, 1505 shadow mismatches, 1506 EA us, 1507 batch us. The agent exports them
as `slot1500` .. `slot1507`.

## 3. Evidence so far

- Offline self-check `tools/selftest/commodmapfast` (in selftest-all): 4000 random maps (duplicate pairs, equal copies,
  null entries, already empty lists, missing lists, pairs not in the map, null targets): batch result equals EA's rule
  (keys, list objects, entries, order) in every case; the plan calls no `Equals`; the shadow compare finds a wrong removal,
  a replaced list and a dropped key. Work proxy on those small maps: EA 191256 Equals calls vs 84006 entries looked at
  [verified, counted]. Not a time measurement: the game's interpreter cost per entry differs.
- No game run yet: no in-game exactness or time numbers.

## 4. Queue `commodmap-fast-1121` (PREPARED, dry-run only)

1. `bp-commodmap-check`: repro `commodmap-fast-check` (throwaway maps with the real pairs of world objects + Clone()
   copies; off / shadow / fast arms through the hooked `RemoveObject`, plus a heavy run). mustMatch `verdict: EXACT`,
   hook calls > 0, 1505 = 0; EA vs batch ms per removal in the answers.
2. `ip-mm-commodmap-shadow`: ManyMods-IP-aged + many-mods-s, shadow ON 10 min, ModTime 50 Hz: 1504 >= 50, 1505 = 0.
3. `ip-mm-commodmap-ab` (NEEDS A QUIET MACHINE): ABBA `commodmapfast` 1-min slices, per-arm ms/s and hitch counts.

Default decision: OFF in the release until jobs 1-2 pass; then ON candidate (exact, so a clear gain is not required, but
the ABBA must show no loss). Tester kit k3 ships `commodmapshadow` ON (EA result used) until a game run proves it.
