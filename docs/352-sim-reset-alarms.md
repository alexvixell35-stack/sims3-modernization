# 352. EA bug check: what a Sim reset does to the Sim's alarms (I-614-1) (2026-11-26)

Static only, Steam 1.67 decompiled script assemblies in `game-copy/decompiled` (local), census
`docs/data/alarm-callback-census.csv`, local NRaas clone (read and cited only). Nothing built, nothing run. Answers
docs/346 Q5. Ledger: docs/51 §621. Handoff: `docs/handoff/st15-simreset-1126.md`.

**Built 2026-11-26: docs/357** (probe, `simresetalarms`, repro, queue; the §6 side finding is fixed under
`serviceleave`). "Not built" below describes the state when this doc was written.

## 0. Short answer

- **Real EA bug, but it has two sides** [verified code path, effects inferred]. A reset asks only the manager of the
  lot the object stands on to drop its reset-scoped alarms. A Sim's alarms are always on the global manager. So:
  - **Sim on a normal lot: nothing is dropped.** Every alarm of the Sim survives the reset (this is I-614-1).
  - **Sim on the world lot (street, any place outside a lot): the global manager is asked**, and it drops every
    alarm of the Sim that is not `AlwaysPersisted`. That includes `NeverPersisted` alarms that EA arms only at load
    or when the system is created (vampire sunset / sunrise and the other occult day alarms), and the service
    timeouts our `serviceleave` fix already re-arms (docs/564).
- The two outcomes differ only by where the Sim stood. No code reads that difference on purpose: the add, remove and
  dispose paths all special-case Sims to the global manager, the reset path does not.
- **Affected sites:** 198 registration sites put a reset-scoped alarm on a Sim owner: 146 `DeleteOnReset` (114
  one-shot, 32 repeating) and 52 `NeverPersisted` (22 one-shot, 30 repeating). docs/346 counted only the 146; the
  flag test also covers `NeverPersisted`.
- **The harmful side is mostly the drop on the world lot**, not the survival. "Always delete on reset" (what the flag
  name asks for) would spread the vampire and service losses to every lot. That fix is rejected.
- **Fix design** (not built): `[Diag] SimResetAlarmProbe` first, then `simresetalarms` (default off): on a world-lot
  reset keep the Sim's long-lived alarms, as vanilla already does on every other lot; a reviewed drop list for
  alarms that should die on any lot comes second and starts empty. §5.
- **Side finding in our own code:** `serviceleave` assumes the timeouts are always deleted. On a normal lot they are
  not, so its re-arm replaces a live timer and restarts the delay from zero. §6.
- No NRaas or other community fix found for this. §4.

## 1. Code path [verified]

| Fact | Where |
|---|---|
| A Sim's alarms go to the global manager with the Sim as owner, whatever lot it is on | `GameObject.cs:7502-7545` (`AddAlarm`, `AddAlarmRepeating`, `AddAlarmDay`: "no lot or is a Sim" uses the global manager) |
| Dispose special-cases Sims the same way: the global manager drops everything the Sim owns | `GameObject.cs:3554-3574` |
| Reset does not: `GameObject.DoReset` calls `OnReset(this)` on the current lot's manager only; one IL caller | `GameObject.cs:3998-4003` |
| `Sim.DoReset` calls the base first and adds no alarm call of its own; later it sets one handle field to invalid without removing the alarm, so it expects that alarm to be gone | `Sim.cs:54629-54637`, `54775`; the field is removed by handle elsewhere, `BuffFirstRomance.cs:56-61` |
| Every reset entry ends in `DoReset`: `GameObject.OnReset` (sealed), `SetObjectToReset`, `Sim.SetObjectToReset` (through the proxy), `SetToResetAndSendHome` | `GameObject.cs:3956-3989`, `Sim.cs:54614-54627` |
| `AlarmManager.OnReset(obj)` clears every timer of that owner whose type is above `AlwaysPersisted`: `DeleteOnReset` **and** `NeverPersisted` | `AlarmManager.cs:108`, `627-678`; `AlarmType.cs:3-8` |
| Each lot has its own manager; the lot manager getter falls back to the global one only when the lot has none; the manager with the world lot's id is the global manager | `Lot.cs:69`, `1744-1754`, `3786`, `3813`; `AlarmManager.cs:254-281`; `LotManager.cs:210`, `806` |
| Moving between lots does not move a Sim's alarms | `AlarmManager.cs:737-763`, `GameObject.cs:5920-5924` (docs/346 §1) |

So for a Sim: current lot = a normal lot gives a call on a manager that holds none of the Sim's alarms (no effect);
current lot = the world lot gives a call on the global manager (drops all reset-scoped alarms). That a Sim outside
every lot has the world lot as its current lot is standard EA behaviour, not re-read here [inferred, I-621-2].

A destroyed Sim is not affected: dispose clears everything on the global manager [verified, row 2].

## 2. Affected sites

From the census (Sim owner, 413 sites): 215 `AlwaysPersisted` (never touched by a reset), 198 reset-scoped.

| Type | One-shot | Repeating | Total |
|---|---|---|---|
| `DeleteOnReset` | 114 | 32 | 146 |
| `NeverPersisted` | 22 | 30 | 52 |

By census group: buffs, occult and actor systems 71; services and roles 54; other objects 19; store objects 15;
situations and parties 15; careers, school, skills 7; world and lot systems 7; household, social, travel, UI 4;
fishing 3; rabbit holes and shells 2; cooking 1. Filter to list them: `hold_class` starts with "Global, Sim owner"
and `alarm_type` is not `AlwaysPersisted`.

Handle handling at the site (text scan of the registering file, [inferred, I-621-3]): 159 keep the handle and the
same file removes it somewhere; 28 keep no handle; 11 keep a handle that the file never removes.

## 3. Player-visible effects

### 3.1 Dropped on the world lot (the harmful side)

| Alarm family | Sites | Armed where | After a street reset |
|---|---|---|---|
| Vampire sunset / sunrise (`NeverPersisted`, daily) | `OccultVampire.cs:465-466` | only in `SetupSimHandlers`, called from `Sim.OnLoad` (`Sim.cs:52119`) and when the occult is added (`OccultBaseClass.cs:72`, `OccultManager.cs:889-899`) [verified] | no sunset / sunrise callback until the save is loaded again [inferred effect, I-621-4] |
| Werewolf full-moon check and sunrise, fairy daily cleanup, SimBot short-out, mermaid | `OccultWerewolf.cs:1214-1216`, `OccultFairy.cs:2255`, `OccultFrankenstein.cs:677` | same pattern [inferred from the same method name, not read per class] | the occult's periodic behaviour stops until reload |
| Celebrity daily alarms, trait "desired item" check, store buff pulses (movie, sauna, massage, cauldron, wishing well), soaked / seasick / terrified buff pulses | `CelebrityManager.cs:695-696`, `TraitManager.cs:394`, census rows with `NeverPersisted` + repeat | at start-up or when the buff is added | the pulse stops while the buff or system stays |
| Service and babysitter state timeouts (`DeleteOnReset`, owner = worker) | `ServiceSituation.cs:179`, `387` and the rows docs/564 lists | state `Init` | service never arrives, never stops, never leaves (docs/564 §1; already fixed by `serviceleave`). Only 10 situation classes override `OnReset(Sim)` |
| Other situation state checks owned by a Sim (burglar, butler cooking, Bonehilda, Grim Reaper, tutorial child) | census group "FS services and roles", repeating rows | state `Init` | the state stops polling; the NPC may stand for good [inferred, not read per state] |

### 3.2 Survive on a normal lot

For the 159 sites that remove their handle, the owner's own cleanup usually runs in the reset (interaction cleanup
through `InteractionQueue.OnReset`, buff removal) and nothing is left [inferred, I-621-3]. What is left:

| Example | Site | After a reset on a lot |
|---|---|---|
| Prop destroy one minute later | `InteractionQueue.cs:793` | the prop is still destroyed: harmless, better than the drop |
| Autograph / vaccination / emergency thought balloons | `Sim.cs:5182`, `5599`, `MedicalEmergencySituation.cs:167` | a balloon after the interaction is gone: cosmetic |
| Trait anxiety buffs (sink, fireplace, stove) | `TraitFunctions.cs:2188-2194` | the buff arrives although the Sim was reset away: minor |
| Conversation relationship lambda | `Conversation.cs:1739` | a late relationship step: minor |
| Reaction and VFX one-shots (`Reactor`, `SkipStone`) | census, `NeverPersisted` one-shot, no handle | a late reaction or effect: cosmetic |

No crash path found in the sample. Survival is the outcome EA shipped for nearly every reset (Sims are mostly on
lots), and later EA code leans on it (the occult alarms above would not last a play session otherwise).

## 4. Prior art

- NRaas (local clone, commit in docs/24): no call of `AlarmManager.OnReset` and no handling of the reset-scoped
  types for Sims [verified grep]. `NRaas/Tasks/ResetSimTask.cs` (its own reset) removes one named alarm
  (line 745) and otherwise leaves alarms alone; `NRaasOverwatch/OverwatchSpace/Loadup/CleanupAging.cs:26-38` removes
  dangling age-up alarms at load. Neither covers this.
- docs/24 and one web search (2026-11-26): no report or mod that names this cause [not found; a miss is not proof].
- Ours: `serviceleave` (docs/564, `ServiceLeave.cs`) re-arms the service and babysitter timeouts after a worker
  reset. It covers one family of §3.1 only.

## 5. Fix design (not built; default off)

Step 0, measure first: **`[Diag] SimResetAlarmProbe`** (ts3fixes, off). Read-only postfix on `Sim.DoReset` (same
`Patch` form as the other `DoReset` hooks in ts3fixes) and prefix on `AlarmManager.OnReset`: per Sim reset log the
lot kind (world / other), household or not, and per reset-scoped timer of that Sim: callback, type, repeating,
dropped or kept. Line prefix `simresetalarm:`. This gives how often resets happen on the world lot and which
callbacks are lost in real play.

Step 1, **`simresetalarms` = 1 (keep)**: prefix on `AlarmManager.OnReset(GameObject)`. When the object is a Sim and
the manager is the global one, the hook does EA's loop itself with one difference: a timer is kept when it is
`NeverPersisted` **and** repeating. Everything else is cleared as EA clears it (yielding one-shots included, through
EA's own helper). For every other object and manager the original runs.
- Why this set: all 30 such sites are armed once by a long-lived owner (occult, celebrity, trait, buff instance,
  group) and never re-armed by a reset [verified for the vampire, inferred for the rest, I-621-5].
- Exactness: the kept timers are exactly the ones vanilla keeps for the same Sim on any normal lot. No new state.
- `DeleteOnReset` service timeouts stay with `serviceleave`.

Step 2, **`simresetalarms` = 2 (keep + drop list)**: postfix on `Sim.DoReset`, for a Sim on a normal lot: clear the
Sim's `DeleteOnReset` timers whose callback (declaring type + method) is on a reviewed list. The list ships empty
and grows only from probe data plus a per-site read. Never a blanket delete.

Rules:
- **Household:** applies to every Sim, household included: it is an EA bug fix whose result is one vanilla already
  produces on the other lot kind, not a throttle or shortcut. The probe reports household and NPC counts apart.
- **Saves:** `NeverPersisted` timers are never written; the fix adds nothing to a save. No load-time cleanup needed.
  A save made after a vanilla street reset heals at load anyway (EA re-arms at load).
- **Other mods:** NRaas does not patch these methods (§4). Our own hooks on reset paths: `InteractionQueue.OnReset`
  (`InUseLogic.cs:41`), `Situation.OnReset` (`ServiceLeave.cs:49`); neither touches `AlarmManager.OnReset` or
  `Sim.DoReset` [verified grep].
- **Far features:** independent of `farhold` (docs/346 Q5). A held Sim's side-list timers are cleared through the
  same indices, so the keep rule and the hold do not interact.
- Counters to reserve at build: resets seen (world / other), timers kept, timers dropped by list, errors.
- Thread: sim thread, inside the reset; no lock.

Repro idea (`sim-reset-alarms`, no run yet): on any save, make an NPC a vampire, read the global manager's timers
owned by that Sim through the harness (count of sunset / sunrise callbacks = 2). Arm A: Sim on its home lot, reset
it, read again: 2 (vanilla and fix). Arm B: Sim on the street, reset, read: vanilla 0, fix 2. Then wait for sunset
and check that the callback ran (buff change) only where the count was 2. Control with the probe on in both arms.

## 6. Side finding: `serviceleave` on a normal lot

`ServiceLeave.cs:121-156`: after a worker reset the fix removes the old handle and adds the timeout again with EA's
full delay. When the worker stood on a normal lot the old timer was still live (§1), so the fix does not repair
anything there; it restarts the delay (arrive, leave, work limit, 3-minute check) from zero [verified in our source;
effect inferred, I-621-6]. Its repro is a pure decision check, so it could not see this. Proposed follow-up (own
bugfix branch, not in this doc-only task): re-arm only when the old handle is no longer known to the global manager.

## 7. Open questions

- Q1: how often does a reset happen on the world lot in real play? Step 0 answers it; it decides whether step 1 goes
  ON under the rare-fix rule.
- Q2: the per-class read of the 30 `NeverPersisted` repeating sites (only the vampire was read in full).
- Q3: which Sim-owned situation states stall after a street reset besides the service family (§3.1 last row).
- Q4: EA 1.69 was not re-read (assemblies not in `game-copy`); same by docs/235 only [inferred].
- Q5: objects without a lot also register on the global manager and are reset with no lot: their reset-scoped alarms
  survive every reset. Not sized here.
