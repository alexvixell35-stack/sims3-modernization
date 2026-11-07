# 242. Sims reset at load: saved stacks through swapped methods, and ErrorTrap's route check

A Sim saved mid-route could lose her Sim object seconds after a load: a new object at home, her trip gone (the "Sim lost mid-visit" entry in PLAN, queues sim-gone to sim-gone9). Two of our changes caused it, each alone enough: a method swap refused her saved stack, and our ErrorTrap spread let ErrorTrap's route check run late. This doc is the cause, the fix, the audit of every swap and the test results. Ledger section: §429. Audit table: `docs/data/swap-stack-audit.csv`.

Claim marks: **[verified]** = read in the decompiled game code or disassembly, or seen live; **[inferred]** = our reading, not yet checked in game.

## 1. What the load does with a saved stack

- A saved task is a list of frames. Each frame names its method through a method table: the `MethodInfo` (written as declaring type, name, parameter types) plus a checksum of the method's IL (`TaskControl.GetMethodChecksum`, native `0xD83680`) [verified].
- `TaskStateCollection.Read` looks each method up with `GetMethod` and compares the checksum. A miss clears the entry's handle, and `ReadTask` throws the whole task away when a frame hits a cleared entry [verified]. The Sim then fails `PreSimulate` and is reset.
- A frame resumes by offsets into the method's compiled code, so the check is right to refuse a body that is not the saved one [verified].

Our `SwapMethod` breaks both halves of that lookup for a hooked method M with stub S [verified, sim-gone7 and sim-gone9]:

| Frame in the save | Why it was refused |
|---|---|
| M, written by the game without our mods | M now hashes as the hook |
| S, written by our own build | S's `MethodInfo` writes as (game class, `"XOrig"`), and the game class has no such method. The load finds nothing |

So a Sim inside any hooked method that yields was reset at load, with a save from any build, ours included. sim-gone9: a pair save loaded by the same pair refused 30 tasks, 9 methods.

## 2. The stack fix: `savedstacks`

The original IL still exists: it is in the stub, with the saved checksum. `Fixes.SavedStackHook` hooks `TaskStateCollection.ReadTask`. Its first call of a load walks the method table and points each cleared entry at the stub with that checksum (`SsRemap`). The frame resumes in EA's code and returns to its caller, so a frame from a save without our mods finishes as EA's and skips the hook's after-work for that one call. This one hook covers every swap with a stub, which is all of them (section 5). It adds nothing to a save.

- Switch `savedstacks` (on). Counters: [760] entries remapped, [761] entries left dropped, [762] stubs with no checksum.
- The table also holds every hooked target and every method of ts3fixes. A save by our own build names a frame in a hook's body by the hook's class (`SwapMethod` moves the class word too), and a hook that does not derive from the game class cannot find the method again (`UfFinishActionHook`, seen as 1B2FF7C7 in `ss-bp-c-after`). A sum already in the table stays: a hook shares its target's sum. Two stubs or targets with one sum are refused, as EA would.
- Limit: a frame in a hook's own body is matched by the hook's checksum. A build that changes that hook's IL, or removes the hook, refuses it once, on the first load after the update: this is why `ss-*-c-upgrade` (master's save on the fix) stays high: a Sim in the removed `DoRoute` hook cannot come back.
- Limit: removing ts3fixes from a save made with it refuses the frames that sit in stubs and hooks, which resets those Sims once. The save still loads (`ss-*-c-removed`).

## 3. The second cause: the ErrorTrap spread moved ErrorTrap's route check

With section 2 alone the stack came back (Lala's events matched the run without our mods) but she was still reset in 4 of 4 runs. Source of that reset [verified, detector `sim_gone_deref`, NRaas source read, not copied]:

1. ErrorTrap's `DereferenceFollowRouteAction.PreProcess` takes the route actions it listed at load. An action no longer in the Sim's `mRouteActions` makes that Sim a candidate, with her current interaction.
2. ~12 s later `Perform` queues `DelayedResetTask` for every candidate whose route has distance left.
3. 30 sim-minutes later, if that interaction is still current, `ResetSimTask` destroys the Sim and makes a new one at home.

So any Sim whose saved route action finished before PreProcess and who is on one long interaction afterwards is reset, although nothing is wrong with her. Without our mods PreProcess runs at the first poll after the visit starts (<= ~0.5 s, sim clock 11:54, Lala's load action still in her chain: not a candidate, 2 of 2). With `errortrapspread` (on in the shipped tuning) it ran at ~2.4 s, 12:09 PM: our inventory slices ran in ErrorTrap's own task, ahead of PreProcess, and held it back ~15 sim-minutes, past the visit's cancel of her old route (4 of 4).

Fix: the spread's inventory pass runs in a task of its own (`ErrorTrapSpreadHook.CheckHook`) and ErrorTrap's task waits one tick, so PreProcess keeps its usual moment and still starts in a fresh tick. Same work, same slices. The inventory check does not feed the dereference scan: it uses neither `AddToBeDeleted` nor `ObjectLookup` [verified, NRaas source]. Result: pair 4 of 4 PASS.

ErrorTrap's own false positive stays, as without our mods: a Sim whose saved action ends in the first half second still counts (`sg3-noours-2` lost Roxy to an ErrorTrap reset with none of our mods installed). Not patched.

## 4. Hooks removed

Both were logging only, on a path that yields:

- `SimRoutingComponent.DoRoute` (`RflRouteHook`). `routefail:` lines for failed plans stay (Route hooks). A DoRoute that fails after a good plan has no line now: no non-swap source covers every household Sim cheaply. The showoff repro uses EA's own event `RoutingComponent.OnRouteActionsFinished` on its two Sims.
- `SocialJigTwoPerson.PlaceJigAlongRoute` (`RflJigHook`). The `showoff:` jig line is gone.

## 5. Audit of every swap

`docs/data/swap-stack-audit.csv` lists all swaps: hook, target, file, stub, can it yield, proof path, purpose, action. `tools/swap-stack-audit.ts --recompute` writes it from the managed call graph: a target "can yield" when it reaches a yield primitive (Sleep, Yield, `StateMachineClient.RequestState`, SACS) through direct calls and, for virtual calls, every game override. Delegates are not followed. That reach is wide on purpose, so "yes" means possible, not common. Runtime proof for the common ones: the refused frames of sim-gone7 and 9 (section 1).

179 swaps after the removals:

| Can yield | Swaps | Action |
|---|---|---|
| yes (path in the table) | 107 | keep, stub remap |
| no direct path | 64 | keep |
| mod target (NRaas) | 8 | keep, stub remap |
| not in the call graph index | 1 | keep, stub remap |
| removed | 2 | `DoRoute`, `PlaceJigAlongRoute`: logging only |
| kept with no stub | 0 | the gate refuses one |

Every row has a stub, so `savedstacks` covers every row. No fix-carrying method had to go. Logging-only rows left on a yielding path: `PlayTrace.cs` hooks run in the UI flow task, not on a saved Sim task [inferred], so they stay.

**CI:** `node tools/swap-stack-audit.ts` (in `--self-check`, so `selftest-all`) fails on a `Patch` not in the table, a swap with no stub, a changed stub or a stale row. A new swap can't come back unreviewed.

## 6. `GetMethods` on hook classes

A stub takes the target's whole header, so a stub of a virtual target carried a vtable slot into a hook class. `hook.GetMethods` then died in EA's `int3` stub in sim-gone6 and 7 (`SwAbuseHook`), Mono's `method->slot < nslots` [inferred from the source of that Mono era, the abort itself verified below]. EA's `PersistReader` calls `GetMethod(s)` on a type named in a save, so a persisted reference into a hook class could do the same.

- #283 `KeepVirtual` does not change this: it acts on a virtual *original*, and a stub is not one when the swap happens (`SwapMethod(stub, target)` keeps the target's flags and slot in the stub).
- `StubDirect` (every stub, no switch): clears the stub's `Virtual` flag and sets its slot to -1. A stub is only ever called directly. Dummy check: repro `stub-slot-check`. Negative control: repro `stub-slot-raw`, queue `saved-stacks-raw.json` (expected to abort). Result: the raw dummy aborts at the same int3 (`0x58D9B6`, `ssr-raw`), the dummy after `StubDirect` and all 119 hook classes do not [verified].

## 7. Results

Refused stacks at load, tasks marked invalid / household Sims refused. "Floor" = none of our mods at all. Jobs: `saved-stacks-matrix.json`, `-matrix2.json`, `-verify.json`, tables in `docs/data/results/2026-11-06-queue-saved-stacks-*.md`.

| Save type | World | Floor | Master | Fix |
|---|---|---|---|---|
| a: baseline save, no ts3fixes frames of this build | BP | 6 / 0 | 20 / 2 | 4 / 0 |
| a | IP | 0 / 0 | 3 / 0 | 0 / 0 |
| b: saved by that build after 20 s, no visit | BP | not run | 40 / 3 | 13 / 0 |
| b | IP | not run | 6 / 0 | 2 / 0 |
| c: saved by that build mid-route | BP | 0 / 0 | 30 / 2 | 5 / 0 (final package; 10 / 0 and 16 / 2 on earlier packages of the same day, run to run spread is wide) |
| c | IP | 0 / 0 | 5 / 0 | 1 / 0 |
| c, upgrade: master's save on the fix | BP / IP | - | - | 28 / 1, 4 / 0, once |
| c, ts3fixes removed: the fix's save with no ts3fixes | BP / IP | - | - | 35 / 1, 12 / 1, once; the save loads |

BP type a is below the floor because the baseline save holds frames of an older build of ours, and the remap finds them. BP counts are all townies but the household columns.

What is left: on a save made by the fix 1-2 method entries stay refused, 55B40D0E in every save and CC5013C0 in one, costing 2-13 townie stacks a load (reset, not lost: they restart). They are not in the stub table and not in ts3fixes; the name is unknown [verified that they stay, inferred that they are not ours]. Two detectors to name them crashed the game (a full `GetMethods` walk, `Module.ResolveMethod` over our own tokens, the int3 at `0x58D9B6` in both, neither is a path a player runs), so both were removed. No household Sim was refused on the final package.

Sim reset and regressions on the final package (`saved-stacks-verify.json`, ErrorTrap on):

| Check | Result |
|---|---|
| `sim-gone-load` pair x4 | PASS 4/4; with the same ErrorTrap change on the package before, 4/4 too. Before that change 0/4 |
| `compat-visit-control`, `compat-moar-community` | PASS, PASS |
| `stub-slot-check`, `stub-slot-raw` | PASS; raw aborts as expected |
| Save matrix, shipped mode, BP | 9 cases, 9 PASS |
| Save matrix, shipped mode, IP | 9 cases, 9 PASS. Job FAIL only on one ErrorTrap correction file (`PlateServing`, Margarita Manzano), the same file `ss-ip-a-noours` writes with none of our mods |
| `showoff-badges-route` | two shipped arms route ok on the new event; the repro then stops at `native_set`, a command that no longer exists (old, not this change) |

## 8. Hot paths and cost

| Hook | Change | Calls | Cost of the new form |
|---|---|---|---|
| `SimRoutingComponent.DoRoute` | dropped | at most the plan rate: counter [24], ~32 plans/s in sim-gone9 at ultra | none, a saving of about two clock reads a route [inferred] |
| `SocialJigTwoPerson.PlaceJigAlongRoute` | dropped | a social with a jig, not per tick | none |
| `TaskStateCollection.ReadTask` | new | once per saved task, load only; 10,640 tasks on BP | one reference compare per task, one table walk per load; 0 in play |
| `StubDirect` | new | once per stub at install | 0 in play |
| ErrorTrap `CheckHook` | moved to own task | once per load | same slices; ErrorTrap's scan and the slices now share ticks after the load [inferred: no longer hitch] |

Timing is not measured: `final-bundle.json` has the fix in both arms, because nothing here has a switch that a profile can flip (docs/222 §11.9, 2026-11-06). It needs a quiet ABBA only if the bundle regresses: the scan and inventory slices sharing ticks after a load, and the DoRoute saving. Queue `tools/harness/queues/saved-stacks-abba.json` (written, not run: no quiet window was left, ~95 min): BP and IP, ABBA master package vs fix package, same asi and ini pinned in `game-copy/swapfix/`, noise check on; `node tools/harness/arm-ms.ts saved-stacks-abba` gives ms/s per arm.
