# 359. simdescindex: `SimDescription.Find` from a description index (2026-11-26)

Status: built, selftests pass, queues written and dry-run, **nothing run in game**. Default OFF.
Candidate "Description index" of docs/356 §6. Rodney 2026-11-26: build now, skip the call-rate measurement; the
counters below give the rate in the first run. Steam 1.67 decompiled scripts read; EA 1.69 not checked.
Ledger: docs/51 §628. Handoff: `docs/handoff/simdesc-index-1126.md`. Branch `feature/simdesc-index-1126`.
Code: `mods/ts3fixes/src/SimDescIndexCore.cs` (game-independent), `SimDescIndex.cs` (hooks), `DescWriterScan.cs`
(call targets). Selftest: `node tools/selftest/simdescindex/run.ts`.

## 1. What it does

`SimDescription.Find(id)` walks every household in list order and every description in it, and on a miss asks the
world for every `Urnstone` (docs/356 §4.3) [verified, V-625-4]. 633 call sites.

With `simdescindex` on, the hook on `Find`:

- **hit:** returns the description from a table id -> first description in walk order, after checking that it still
  sits at the same household slot and member slot and still has the id;
- **miss:** if no walked household holds the id and no urnstone the world may list holds it, returns what the game's
  tail returns (the tutorial list must be null, then `GameStates.GetEarlyDepatureSim`), with no world-wide query;
- **anything else:** runs the game's `Find`.

The answer is always the object the game's walk returns. It is a pure lookup: no Sim, household or save is changed.

## 2. Exact by construction

The table is a snapshot of the game's own walk. It is never patched in place.

| Event | How the snapshot stays exact |
|---|---|
| a writer that can add a description, add a filled household or change an id | a hook opens a window; lookups inside it are the game's; the end of the window marks the snapshot dirty |
| dirty snapshot | the first lookup after the last write is the game's, the second rebuilds (a burst of writes with lookups between costs one rebuild) |
| a removal, from any code | no hook needed: a hit is returned only if household slot, member slot and id still match; a removal cannot make an absent id present |
| a miss | answered absent only while every household is still at its slot with the member count it had at the build (checked on every miss). An add no hook saw is then the game's lookup. Not seen: an unseen add and an unseen removal in the same household between two lookups (the mod scan guards unseen adds) |
| a shadow mismatch | `untrusted` is set: every lookup is the game's for the rest of the session |
| another build of the game's code | the lookup and the four required writers are patched only if their IL hashes are the ones read (Steam 1.67); EA 1.69 or a core mod that replaces one: not patched, `ready` bit 1 clear |
| an exception in our path | the snapshot is dropped and marked dirty; an open writer window stays open |
| household list changed | the household count is compared on every lookup |
| previous-traveler household changed | a hit's household must not be the skipped one; the skipped household seen at the build must still be skipped |
| `GameStates.IsStartingVacation` | every lookup is the game's (the traveler household is walked then) |
| a null household or description in a list | the game's walk would throw there: every lookup is the game's until the next rebuild |
| world load, travel, quit | nothing is answered from load start to load finish and after quit; the snapshot and the urn set are cleared at all three |
| lookup or writer on the AutShadow worker thread | the game's lookup; a writer there only marks the snapshot dirty; a rebuild that a note interrupted is thrown away (§8 item 7) |

Duplicate ids: the build keeps the first in walk order, as the walk does.

## 3. Writers [verified decompiled, grep over all four script assemblies]

| Writer | Kind | Guard |
|---|---|---|
| `Household.Members.Add` (reached by `Household.Add`, `AddSilent`, `AddTemporary`, `Members.ImportContent`) | add | **hook, window** (required) |
| `Members.PostLoadFixup` | add | **hook, window** (required) |
| `SimDescription.MakeUniqueId` | id | **hook, window** (required; dirty only if the id changed) |
| `SimDescription.ImportContent` | id | **hook, window** (required; dirty only if the id changed) |
| `Bin.PlaceHousehold` (adds a household that already has members; clears another member list) | add | hook, window; also the count check |
| `Members.RemoveAt` (`Members.Remove` calls it), `Members.DisposeEachMember`, `Members.ImportContent` (clear) | removal | hook marks dirty; exact without it (slot check) |
| `Household.ClearSilent`, `Household.Cleanup` (clears through an alias) | removal | hook marks dirty; exact without it |
| `Household.Startup`, `Shutdown`, `DestroyPetHousehold`, `DestroyAlienHousehold`, `DestroyMermaidHousehold`, `DestroyServobotHousehold` | household list removal | hook marks dirty; exact without it (count check) |
| `Household.CreatePreviousTravelerHousehold`, `Shutdown` (the skipped household) | skip | hook marks dirty; exact without it (skip checks) |
| `Household(bool)` constructor | household list add | not hookable (constructor); the household is empty, members arrive through `Members.Add`; count check |
| `Household.Dispose(bool)` | household list removal | not hooked (runs on the finalizer thread); count check + slot check |
| id setter `SimDescription.SimDescriptionId` | id | not hookable: the game inlines it (docs/293). Its 3 EA callers (`MiniSimDescription.UnpackSim`, `Passport.DownloadSimCallback`, `WishingWell`) set it on a description created in the same method, added later through `Members.Add` [verified] |
| the save's lists (deserialised, no call) | all | load hold (§2) |

The lookup is swapped only if the four required hooks went in (`ready` bit 1). The other hooks are logged if refused.
12 EA sites keep a member list in a local; one of them writes through it (`Household.Cleanup`, hooked). No EA site
passes a member list to a method that writes it. `TownieMaker` keeps one in a field and hands it to its Sim picker, which only reads it. `TournamentManagement.FindSuitableOpponent` reads the household list.

## 4. Urnstones (the miss path)

A set of the urnstones the world may list, kept as a superset of the world's list:
seeded from the game's own `GetObjects<Urnstone>()` at the first miss after a load, added to by the `GameObject.Init`
hook (the existing `DrInitHook`; every object, also after a load; only when the lookup hook is in), dropped by the
`GameObject.Destroy` hook once the world no longer resolves the id [inferred, I-628-3]. An urn that has no id yet
at Init, or in the seed, leaves the set unseeded: the next miss seeds again. A destroyed urn seen in the set re-seeds it after 64 more misses.

A miss reads each held urn's description as the game does. If none holds the id, the game's urn loop finds none. If
one does, the game's `Find` runs (the world's order decides between two urns; counter `urn_candidates`). Without the
`Init` hook (`ready` bit 2 clear) every miss is the game's.

## 5. Mods

A mod can write the lists directly (both are public). One scan per process (`DescWriterScan`, as docs/336 §11, with
opt-in call targets) reads the IL of every loaded non-EA assembly that references the gameplay assembly.

**A writer (any one: `untrusted` > 0, every lookup stays the game's for the session, ts3native.log names up to 12):**

- a store of `sHouseholdList`, `mMembers`, `mAllSimDescriptions` or `mSimDescriptionId`, or their address;
- a member list or the household list used for an add, insert, index store, sort or reverse, or handed to code the
  scan cannot read as a reader (below);
- the id setter on anything but a local that only holds an object created in that method, with no `Household` call
  between the creation and the setter;
- the field names as strings next to reflection calls.

**A reader (no hit).** Since build b (2026-11-26) the scan follows a list instead of counting every escape:

| Shape | Rule | NRaas example (source read, not copied) |
|---|---|---|
| read or removal on the list | `Count`, enumerator, index read, `Contains`, `Find*`, copy constructor, `AddRange` source, `Remove*`, `Clear` | most uses |
| kept in a local | every load of that local is judged | `MiniSims.CreateBabyBeforePacking` |
| passed where the parameter is `IEnumerable` / `IEnumerable<T>` | no add without a cast back | `Households.NumSimsIncludingPregnancy` |
| passed to a mod method | the callee's uses of the argument are judged, 3 calls deep; a callee that returns it does not count as a reader | the StoryProgression move scenarios |
| returned as it is | the method becomes a list getter: every call of it is judged like the EA getter | `Households.All` (returns the member list as `ICollection<SimDescription>`) |
| stored in a mod field | the field becomes a list: every load of it is judged; storing to it is no write | `MoveInLotScenario.mMovers` |
| passed to an EA method read by hand | `MiniSimDescription.UpdateHouseholdMembers`, `TournamentManagement.FindSuitableOpponent` only walk the list [verified decompiled] | `MiniSims.CreateBabyBeforePacking`, the Chess / Martial Arts / Spar opponent pickers |
| delegates, arithmetic and static loads among the other arguments | counted as arguments (both compilers' cached-delegate shapes) | `SellBikeScenario.Allow` (`Find` with a lambda) |

Getters and kept fields are found during the scan, so it repeats until a pass finds no new one (NRaas full set:
13 getters, 2 fields, 3 passes); that pass is the verdict. Once a getter or a kept field exists, every
non-EA assembly is scanned, also one that names no EA type. One method is covered by its IL hash instead:
`NRaas.RegisterSpace.Options.Service.SelectService+SimSelection..ctor` hands the NPC household's list to its
selection base class, a generic class the scan cannot follow; the base keeps it in `mItems` and only walks and
counts it [read in the NRaas source]. The cover also needs the IL hashes of the 7 base-class bodies that touch
`mItems` (`DescWriterScan.SdiSelectionRead`). Another build of that mod has other hashes and is scanned normally.
Allowed by name [verified]: `Ts3Agent.Handlers::ListSims` (its local also holds a list it fills itself),
`Ts3Fixes.Fixes::.cctor` (its `"mMembers"` string is `Conversation`'s).

**NRaas methods that do write the walk's inputs** [verified, source grep, V-628-4]:

| Method | Write | Effect on the index |
|---|---|---|
| StoryProgression `ManagerHousehold.EliminateHousehold` | `sHouseholdList.Remove` | removal: exact through the count and slot checks, no stand-down |
| Traveler `InitializersEx.Cleanup` | `AllSimDescriptions.Clear`, `sHouseholdList.Remove` | removal: same |
| every NRaas mod, `MiniSims.UnpackSim` | id setter on a description it has just created | passes the fresh-object rule; the description joins through `Members.Add` |

No direct add, insert or reorder of either list exists in the NRaas source (grep), and the scan finds none through
helpers in the full set. So nothing in the NRaas full set stands the switch down. A mod that does add stays a writer: the selftest holds one writer next to
each reader shape (`WrapAdd`, `PassDeepAdd`, `KeeperWrites`, `PassEaWrites`, `PassCollection`, ...).

Offline check (`DWS_SIMDESC=1 node tools/static/desc-writer-scan.ts <dll|package>...`): no writer method in Ts3Fixes,
Ts3AgentPlugin, the NRaas full set (21 mods) or the NRaas core set. The offline tool still prints "untrusted" for 5
assemblies because .NET Framework's loader cannot read 42 methods the game can [inferred, I-628-6]; in game the
smoke and the NRaas job require `untrusted` = 0.

Not seen by the scan: a cast of an `IEnumerable` back to a list, names built at run time, emitted code. The shadow
compare is the net for those.

## 6. Counters (slots 1899-1922, agent names `simdescindex_*`)

| Slot | Name | Meaning |
|---|---|---|
| 1899 | `calls` | lookups with the switch on |
| 1900 | `hits` | answered from the table |
| 1901 | `misses` | misses answered without the walk and the urn query |
| 1902 | `game` | left to the game's `Find` (dirty, window, load, vacation, untrusted, urn candidate, null entry) |
| 1903 | `urn_candidates` | of `game`: an urn holds the id |
| 1904 | `rebuilds` | snapshot rebuilds |
| 1905-1907 | `entries`, `high_entries`, `urns` | sizes (leak check: entries <= residents, urns <= urnstones) |
| 1908 | `writer_notes` | writes seen by hooks |
| 1909 | `moved` | snapshots dropped by the slot / count / skip check (a writer no hook saw) |
| 1910 | `walk_saved_k` | descriptions the game's walk would have read, in thousands |
| 1911 | `urn_queries_saved` | world-wide urn queries not made |
| 1912 | `shadow` | shadow compares |
| 1913 | `find_mismatch` | shadow answers that differ (**must be 0**) |
| 1914, 1915 | `shadow_hit_us`, `shadow_hit_walked` | the game's walk time and length in shadow hits |
| 1916, 1917 | `shadow_miss_us`, `shadow_misses` | the game's `Find` time in shadow misses, and their count |
| 1918 | `untrusted` | mod writer methods found (0 = trusted) |
| 1919 | `errors` | exceptions in our path (the game's `Find` ran) |
| 1920 | `ready` | bits: 1 writer hooks + lookup in, 2 urn hook in, 4 scan done (7 = all) |
| 1921 | `urn_seeds` | urn set seeds |
| 1922 | - | spare |

Per second: counter delta / seconds of the run. Shadow: with the switch on every 64th answered lookup also runs the
game's `Find`, times it and compares the object; the game's answer is returned. `simdescindexverify` (default off)
compares every answered lookup.

Time saved, estimated: `walk_saved_k * 1000 * shadow_hit_us / shadow_hit_walked` (us, hits and the walk part of
misses) + `misses * (shadow_miss_us / shadow_misses - total_walk_us)` for the urn query. The timing queue measures it.

## 7. Rules

- **Household Sims:** results equal vanilla for every Sim: a proven-exact lookup (the game's own object or the
  game's own code). It applies to all Sims, household and selected included; no exemption is needed and none exists.
- **Pause / CAS / build mode:** no gate. A lookup cache does no sim work; the writers that run in CAS and Edit Town
  are the same hooked methods.
- **Leaks:** the snapshot holds descriptions and households, the urn set holds urnstones. Both are cleared at load
  start, load finish and world quit; a rebuild replaces the snapshot; an urn is dropped on destroy. Sizes: 1905-1907.
- **Switches:** `simdescindex` (OFF), `simdescindexverify` (OFF). No tuning entry yet: add one when it goes ON.
- No save data. No native change.

## 8. Open

1. NRaas stand-down: **fixed in build b** (§5). Open part: `untrusted` = 0 with NRaas must be seen in game
   (job bp-sdi-nraas-full), because the offline tool cannot read 42 methods the game can.
2. Hooks in at run time: `ready` must read 7 in the smoke job. A target already compiled at apply time is refused
   by `Fixes.Patch` [not checked, I-628-4].
3. The call rate was not measured before the build (Rodney's call). If `calls` per second is low the gain is small
   today; the cost that grows with the town is still removed (docs/333 rule).
4. The mod scan runs once per process, at the first lookup with the switch on or under the load screen. Its in-game
   time is not known (offline: 1 s for our two assemblies; the NRaas full set takes 3 passes of ~6 s each).
5. EA 1.69 not read: there the body hashes differ and the lookup is not patched (the switch does nothing).
6. `SimDescription.ImportContent` and `Bin.PlaceHousehold` can yield (docs/data/swap-stack-audit.csv). A window
   stays open across the yield (lookups are the game's meanwhile: exact). A task destroyed inside one leaves the
   window open until the next load: the switch then does nothing, seen as `game` rising with `calls`. No fix built.

7. **No Sim-thread test.** The review asked for one in place of `OnWorker()`. The game's mscorlib has no `Thread`
   class and no DllImport [verified, compile errors], so managed code cannot name its thread. Built instead: the
   native "is the AutShadow worker" test without the "run in progress" condition, and a rebuild is thrown away when a
   writer noted while it ran. A real test is a 3-line ts3native internal call (new asi build): decision for Rodney,
   needed before ON if another lane runs script code off the Sim thread [I-628-8].
   **Closed at the 1126 merge window:** `SdiSimThread()` now calls `Fixes.OnSimThread()`, the sim-thread test from
   docs/362 (ts3native `IsSimThread`); not the sim thread or not known = the game's own lookup. A rebuild is still
   thrown away when a writer was noted while it ran.
8. **ON gates** (docs/51 §628): I-628-1 (load events bracket the load), -2 and -3 (urn set is a superset), -5 (no
   lists arrive outside a load), -8, -9 were not verified statically. The switch stays OFF until the correctness
   queue passes with `find_mismatch` 0; a mismatch now ends the index's use for the session.
9. Merge: `docs/registry.md` on this branch says "1923+ next free" for the ts3fixes slots; other lanes reserved
   since. The merger runs `node tools/registry.ts fix-next docs/registry.md` (tools/agent-merge.cjs does).

## 9. Queues (written, dry-run, not run)

- `tools/harness/queues/simdesc-index-correct-1126.json`: smoke, BP / IP / SV 10 min with verify, BP on the real
  path, BP with the NRaas full set (`modSet`), six writer repros (save + reload, travel, Edit Town, moving, death,
  birth). ~3 h 10 min. The death job requires a miss and an urn seed; the save and travel jobs carry upper
  bounds on entries and urns (leak gate, not calibrated yet).
- `tools/harness/queues/simdesc-index-time-1126.json`: BP / IP / SV in-load ABBA, per-arm ms/s. Needs a quiet
  machine. ~1 h 40 min. Run after the correctness queue passes.
