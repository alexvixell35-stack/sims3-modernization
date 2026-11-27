# 351. Far features: shared API, names and module layout (ST-15) (2026-11-26)

Doc only. Nothing built, nothing run. Package ST-15 of docs/332 §2.2 / docs/340 §6.1; closes docs/346 Q1. Ledger:
docs/51 §620. Handoff: `docs/handoff/st15-simreset-1126.md`.

Purpose: the build lanes (MS, FS, FO, LZ, N) start in parallel. This note fixes every name two lanes could pick
differently: files, classes, methods, switches, log prefixes, counter prefixes, selftest and queue names. A lane that
needs a name not listed here adds it to §8 in the same commit as the code.

Sources it binds together: docs/346 (hold hook), docs/345 (far-Sim machine), docs/331 (far-object model), docs/343
(sidecar), docs/348 (own-thread objects, lot hub), docs/344 and docs/349 (lazy instantiation), docs/332 §5 and docs/340
§5 (lane ownership), docs/353 (save flush hub `FarSave`, written in parallel; read for names only). Where an older doc used another spelling, §7 maps it.

## 0. Short answer

- One shared owner table and alarm hook: `FarHold` (lane MS). FS and FO call it, never edit it, never touch its side
  lists.
- One shared lifecycle hub: `FarHub` (lane MS): world load / quit, pause gate, per-frame budget. Saves belong to
  `FarSave` (lane MS), the flush hub designed in docs/353; this note only places it.
- One shared sidecar writer: `FarSidecar` (managed, lane MS) and `farsidecar.cpp` (native, lane N).
- File prefix decides the owner: `FarHold*`, `FarHub*`, `FarSave*`, `FarSidecar*` = MS; `FarSim*` = FS; `FarObj*` = FO; `Lazy*` =
  LZ. A lane edits only files with its own prefix.
- ts3fixes pattern as in the existing code [verified in `mods/ts3fixes/src`: `LotHub.cs` / `LotHubCore.cs`,
  `AlarmKillLog.cs`]: `X.cs` is a `partial class Fixes` part with the EA-touching hooks; `XCore.cs` is a plain static
  class with no EA types, compiled into `tools/selftest/<name>`.

## 1. Files and lanes

| File (under `mods/ts3fixes/src` unless noted) | Lane | First package | Content |
|---|---|---|---|
| `FarHoldCore.cs` | MS | FO-3 | owner table (held Sim ids, parked lot ids, owner tag), side lists, take / release order, pass-through set, counters. No EA types |
| `FarHold.cs` | MS | FO-3 | Hook A on `AlarmManager.Simulate`, Hook B on `AlarmManager.Write`, `IsHeld(owner)`, lot exemption check, reflection on the private `Timer` type |
| `FarHubCore.cs`, `FarHub.cs` | MS | FO-3 | lifecycle and order (§4), pause gate, frame budget split between FS, FO, LZ |
| `FarSaveCore.cs`, `FarSave.cs` | MS | FO-5 or SD-4's build package | save flush hub of docs/353 §3: quiesce, hard flush, commit, `FarSave.Quiesce`, `FarSave.InSave` |
| `FarSidecarCore.cs`, `FarSidecar.cs` | MS | FO-5 (creates it), then FS-18 through MS | docs/343 line writer and reader, household filter, transport to ts3native |
| `FarSimModel.cs` | FS | FS-1 | pure machine of docs/345 §18 (already named there). Counts as the Core file of the far-Sim lane |
| `FarSim.cs` | FS | FS-2 | switches, hooks, the interface between the pure model and EA |
| `FarSimShadow.cs`, `FarSimTrace.cs` | FS | FS-2, DT-2 | `[Diag] FarSimShadow`, `[Diag] FarTrace` |
| `FarSim<Part>.cs` (park, drift, visit, work) | FS | FS-3 .. | free inside the prefix |
| `FarObjProbe.cs` | FO | FO-1 | `[Diag] FarObjProbe` |
| `FarObjModel.cs` | FO | FO-2 | pure per-kind models of docs/331 §4 / docs/350 |
| `FarObj.cs` | FO | FO-4 | park / promote, switches, calls into `FarHold` |
| `FarObjPause.cs` | FO | FO-4 or later | pause hooks for the own-thread classes of docs/348 (ST-8 part) |
| `Lazy*.cs` | LZ | LZ-1 | docs/344, docs/349 |
| `native/ts3native/farsidecar.cpp` | N | FO-5 | file, integrity, stamp, atomic write (docs/343 §9) |
| `native/ts3native/lotstream.cpp` | N | CP-1 | unchanged owner (docs/332 §5) |

Selftests: `tools/selftest/farhold`, `farhub`, `farsave`, `farsidecar`, `farsim`, `farobj`, `lazy`. One folder per Core file
group; each lane owns the folder with its name.

## 2. `FarHold` API (MS owns, FS / FO / LZ call)

Ids: a Sim is named by its object id value, a lot by its lot id, both as 64-bit unsigned. Sim and lot calls are
separate methods, so no caller depends on the two id spaces being disjoint.

| Method (on `Fixes`, implemented in `FarHoldCore`) | Caller | Meaning |
|---|---|---|
| `FarHoldSim(simId, owner)` -> bool | FS only | add to the held-Sim set. False (and `ownerConflict` +1) when the id is held under another owner tag. False when the switch is off |
| `FarReleaseSim(simId)` | FS | remove; the Sim's side-list timers go back into the heap on the manager's next `Simulate` (docs/346 §3.2 step 3) |
| `FarParkLot(lotId, owner)` -> bool | FO only | add to the parked-lot set; same conflict rule |
| `FarReleaseLot(lotId)` | FO | remove; the lot's manager is simulated again from its next call |
| `FarReleaseAll(reason)` | `FarHub` only | empty both sets and put every side list back. Reasons: `worldQuit`, `switchOff`, `standDown`, `selftest` |
| `FarIsSimHeld(simId)` -> bool | any lane | read only |
| `FarIsLotParked(lotId)` -> bool | any lane (docs/348 pause hooks, LZ) | read only; already false for an exempt lot (§5) |
| `FarPassThrough(declaringType, method)` | FS, FO at start-up | add a callback to the pass-through set (§3) |
| `FarHoldDiag(what)` -> string | harness | counts for the read-back |

Owner tags (`FarOwner`): `FS` = 1, `FO` = 2, `LZ` = 3. The table is the "one owner flag" of docs/331 §6.5 and
docs/345 V1: an id has at most one owner; whoever holds it releases it. LZ never holds or parks; it asks
`FarIsSimHeld` / `FarIsLotParked` before it strips anything and uses its tag only for `ownerConflict` reports.

Not part of the API: the side lists, the `Timer` reflection, the heap walk. `IsHeld(ObjectRef)` of docs/346 §3.3 is
private to `FarHold.cs`.

Order rules the callers keep (docs/331 §6.5, docs/346 §3.4): promotion = lot first, then its Sims; demotion = Sims
first, then the lot. FO applies its computed state before `FarReleaseLot`.

## 3. E1 pass-through (one rule for both models)

docs/331 §5 E1 wanted our own minimal dispatch for household-facing timers on a parked lot; docs/346 wants no code of
ours in the firing path. ST-15 decision [design, I-620-1]:

- The pass-through set is keyed by callback (declaring type name + method name). It ships empty.
- Held Sim, global manager: a due timer in the set is not taken (docs/346 §3.3).
- Parked lot: while the set is empty, the manager is not simulated at all (docs/346 §3.2 step 2). When the set is not
  empty, a parked lot whose heap head is due is handled in take mode: every due timer that is not in the set goes to
  the side list, then the original runs and fires only the pass-through timers through EA's own dispatch. Timers that
  are not due are not touched.
- So there is no dispatch code of ours, and FO-3's "E1 own dispatch" becomes "E1 take mode". docs/331 §5 E1 row and
  the FO-3 line of docs/332 read with this change.

## 4. `FarHub`: lifecycle and order

One place calls the lanes, in a fixed order, so FS and FO never subscribe to EA events for the same purpose.

| Event | Source | Order |
|---|---|---|
| World load finished | `LotHubOnWorldChange` (true) [verified it exists, `LotHub.cs:23`] | `FarSidecar` open and read, then FS load, FO load, LZ load |
| Save | `FarSave` (docs/353 §3, §4: quiesce on the existing `DisposeSaveHook`, hard flush on the two static `LoadSaveManager` save methods) | owner order and steps are docs/353 §4. `FarHold` is not called: Hook B covers the save. `FarHub` does nothing on a save |
| World quit, travel, Edit Town, quit to menu | `LotHubOnWorldChange` (false) | FS flush, FO flush, LZ flush, `FarSidecar` commit and close, `FarReleaseAll(worldQuit)`, then each lane's reset (drop every table, counters to 0) |
| Lot removed | lot hub lot event (docs/348 (ST-12 part)) | FO promote or drop, `FarReleaseLot`, then EA destroys |
| Switch off at runtime, stand-down | the lane that detects it | that lane promotes everything it owns; when both `farsim` and `farobj` are down, `FarReleaseAll` |
| Per sim frame | one `FarHub` task | budget 2 ms per frame, none in a frame over 25 ms (docs/331 §7); FS due queue first, then FO lots by priority, then LZ |

Lanes register with `FarHubRegister(owner, load, flush, reset, step)`; `FarHub` holds at most one entry per owner tag.
Long frames, as built (docs/358): after 7 skipped frames over 25 ms in a row the 8th steps with a 0.5 ms budget, so a slow game
is not starved; the count restarts on a normal frame, on pause and on world quit (docs/331 §7).
`FarSave` calls the same registered `flush` entries, so a lane registers once. "Saving" is tested with
`FarSave.InSave` (docs/353 §3).
Pause, CAS, Build / Buy, menus: `FarHubPaused()` is the one predicate; no lane step runs while it is true (docs/345
§11). It wraps the pause gate ts3fixes already uses; the FO-3 builder names the helper it wraps in §8.

## 5. Shared predicates

| Predicate | Where | Rule |
|---|---|---|
| `Fixes.HouseholdExempt(Sim)` | `PlayerSims.cs:55` [verified it exists] | every Sim decision. Unknown scope = household |
| `HhExempt` | ts3native | native side |
| `FarLotExempt(lot)` | `FarHold.cs` | true for the world lot, the active lot, the active household's home lot, a lot with a household or selected Sim on it (docs/346 §3.6). `FarIsLotParked` already applies it |

`[Experimental] HouseholdToo=1` does not lift any of them for holding or parking (docs/346 §3.6).

## 6. Switches, prefixes, counters

Switch names already used by earlier docs stay as they are; this table is the full list so no lane adds a twin.

| Area | ts3fixes switches (all default off) | Native ini | Diag | Log prefix | Counter prefix |
|---|---|---|---|---|---|
| Hold | `farhold` | - | - | `farhold:` | `farHold*`, plus the shared `ownerConflict` |
| Hub | none (runs when any consumer is on) | - | - | `farhub:` | `farHub*` |
| Save | `farsave`, implied by any far switch (docs/353 §3) | - | - | `farsave:` | `farSave*`, `farFlush*` (docs/353) |
| Sidecar | none (writes when a consumer that needs it is on) | `[UserDiag]` transport as docs/343 §9 | - | `far:` (docs/343) | `farSidecar*`, `farFlushFailed`, `farRepair*` (docs/343) |
| Far Sim | `farsim`, `farsimt3`, `farsimt4`, `farsimvenue`, `farsimwork`, `farsimvisit`, `farsimdrift` | `[FarSim] Enabled`, `[FarSim] StatesOff` | `[Diag] FarSimShadow`, `[Diag] FarTrace` | `farsim:` | `farsim*` (docs/345 §13.3 spelling) |
| Far object | `farobj`, `farobjlot`, `farobjfire`, `farobjtraffic`, `farobjworld`, and the per-kind switches of docs/350 (`farobjlight`, `farobjtarget`, `farobjwalkers`, `farobjtree`, `farobjpet`, `farobjholo`, `farobjcrowd`, `farobjcage`) | - | `[Diag] FarObjProbe` | `farobj:` | `farobj*` |
| Lazy | `lazy*` (LZ-1 names them) | - | `[Diag] LazyProbe` | `lazy:` | `lazy*` |

Counter slots and ini rows are reserved at build with `node tools/registry.ts reserve fixes` (not in this note).
Queues and repros: `farhold-*`, `farsim-*`, `farobj-*`, `lazy-*`. Handoffs: `docs/handoff/<package>-<mmdd>.md`.

Dependencies between switches: `farsimt3` needs `farhold` (docs/345 §6.5); `farobjlot` needs `farhold`; with
`farhold` off both lanes stay in shadow / count-only mode. No lane reads another lane's switch except through
`FarIsSimHeld` / `FarIsLotParked`.

## 7. Older spellings

| Earlier doc | Reads as |
|---|---|
| `Hold(simId)`, `Park(lotId)` (docs/346 Q1), `FarHold.Park(lot)` (docs/350) | `FarHoldSim`, `FarParkLot` |
| `FarHold.Release(id)`, `Release(simId)`, `Release(lotId)` (docs/345, 346, 348), `FarHold.Release(lot)` (docs/350) | `FarReleaseSim` or `FarReleaseLot` |
| `FarHold.ReleaseAll()` | `FarReleaseAll(reason)`, called by `FarHub` only |
| `HeldCount` | `FarHoldDiag` |
| "shared flush hub" (docs/346 §3.5), "save flush" (docs/331 §6.5) | `FarSave` for saves, `FarHub` for world quit |
| "periodic pass", "spread task" (docs/331 §6.5) | `FarHub` |
| "E1 own dispatch" (docs/331 §5, FO-3) | E1 take mode, §3 |
| "owner flag per lot" (docs/331 §6.5) | the parked-lot set of `FarHold` with owner tag `FO` |
| `FarCompute` (docs/340) | no class of that name; the pure parts are `FarSimModel.cs` and `FarObjModel.cs` |

## 8. Names added by builders

(empty; a builder appends: name, file, package, date)

Pending copy at merge: the names of docs/358 §7 (FO-3 part 1 and the FarLight part of FO-4, branch `feature/farlight-farhold-1126`).

## 9. Open questions

- Q1: closed by docs/353 (save hook sites). Left open: whether `FarSave` is built in FO-5 or in its own package.
- Q2: which existing pause predicate `FarHubPaused` wraps (FO-3 reads the code and writes it in §8).
- Q3: take mode on a parked lot changes "queue untouched" for lots with a due pass-through timer; FO-1's probe should
  count how many parked-candidate lots would have one before the set gets its first entry.
- Q4: docs/350 (SD-2) landed while this note was written. Only its names were checked (grep): it uses `FarHold`,
  `FarHub`, `FarObjModel.cs` and the switches now listed in §6; its `FarHold.Park(lot)` / `Release(lot)` read as §7.
  Its body was not read against §3 (E1 take mode).
