# 357. Sim reset and alarms: probe, fix, repro (build of docs/352) (2026-11-26)

Built, self-tested, queue dry-run. **Not run in game.** Design and the EA code path: docs/352 (this doc is its
build section, kept apart because code, repro and queue name it). Ledger: docs/51 §626 (design ledger §621).
Branch `bugfix/sim-reset-alarms-1126`.

## 0. Short version

- EA bug (docs/352, I-614-1) [verified, decompiled `GameObject.DoReset`, `AlarmManager.OnReset`]: a Sim's alarms sit
  on the global alarm manager, a reset asks only the manager of the lot the Sim stands on. On a normal lot nothing is
  dropped; on the world lot every `DeleteOnReset` and `NeverPersisted` alarm of the Sim is dropped, among them the
  vampire sunset / sunrise alarms that EA arms only at load or when the occult is added.
- `simresetalarmprobe` (ts3fixes switch, tuning `kSimResetAlarmProbe`, **off**, log only): every Sim reset, lot kind,
  alarms kept or dropped. This is docs/352's `[Diag] SimResetAlarmProbe`; it is a ts3fixes tuning switch because
  ts3fixes has no ini of its own (same form as `swapinvokediag`).
- `simresetalarms` (tuning `kSimResetAlarms`, **off**; mode in `kSimResetAlarmsMode`, default 1):
  mode 1 keeps the Sim's repeating `NeverPersisted` timers on a world-lot reset; mode 2 adds a reviewed drop list for
  `DeleteOnReset` timers on a normal lot. The list ships empty, so mode 2 equals mode 1 today.
- **Decision (coordinator, standing switch-on-and-test rule, 2026-11-26; condition added by the rule review):**
  `simresetalarms` stays off now. It goes ON in the RC when queue `sim-reset-alarms-correct-1126` passes **and** the
  30 repeating `NeverPersisted` sites are read (docs/352 Q2, §3).
- `serviceleave` (already on) no longer restarts a timeout that the reset left live (§4).
- Repro `sim-reset-alarms`, agent command `sim_reset_alarms`, queue `sim-reset-alarms-correct-1126` (§6).
- `kServiceLeave`: on in code and in the package tuning, shipped on since v0.3.0; only comments and docs said off (§5).

## 1. What was built

| Part | Where |
|---|---|
| Hook on `AlarmManager.OnReset(GameObject)` (one hook for probe and both modes), decisions, check | `mods/ts3fixes/src/SimResetAlarms.cs`, `SimResetAlarmsChecks.cs` |
| Switches and tuning | `Instantiator.cs` (`kSimResetAlarms` false, `kSimResetAlarmsMode` 1, `kSimResetAlarmProbe` false), `Fixes.cs`, `tools/setup/build-fixes.ts` |
| `serviceleave` live-timer rule | `ServiceLeave.cs` (`SlRearm`, `SlSkipLive`), `SimResetAlarms.cs` (`SraLive`), `ServiceLeaveChecks.cs` |
| Agent commands `sim_reset_alarms`, `service_leave_live`, counter names `simresetalarms_*` | `mods/ts3agent/src/plugin/Handlers/SimResetAlarmsHandlers.cs`, `ServiceLeaveLiveHandlers.cs`, `PatchHandlers.cs` |
| Repro, queue | `tools/harness/repros.json` (`sim-reset-alarms`), `tools/harness/queues/sim-reset-alarms-correct-1126.json` |

Counters (ts3fixes slots, reserved `fixes-1828`): [1828] Sim resets that asked the global manager (world lot),
[1829] Sim resets that asked a lot's own manager, [1830] timers kept by mode 1, [1831] alarms removed by the drop
list, [1832] `serviceleave` re-arms skipped for a live timer, [1833] errors. [1828] and [1829] count only while the
probe or the fix is on (with both off the hook's first line calls EA's body).

One hook instead of the two docs/352 §5 sketched: `GameObject.DoReset` is the only caller of
`AlarmManager.OnReset` [verified, docs/352 §1], so "a lot's own manager is asked for a Sim" is the normal-lot reset and
no hook on `Sim.DoReset` is needed. A Sim with no current lot gets no call at all (EA's null check); its alarms
survive as on a normal lot and neither mode acts.

## 2. How mode 1 keeps the timers

When the global manager is asked for a Sim, the hook takes the handles of that Sim's repeating `NeverPersisted`
alarms out of the manager's owner index (`mGameObjectIndex`, the list EA's loop walks), runs EA's own `OnReset` body,
and puts them back in a `finally`. EA's loop then never sees those alarms and clears everything else as before
(one-shots, `DeleteOnReset`, yielding one-shots through EA's own helper). No copy of EA's loop, no new state.

- The timers themselves are never changed (type, time, callback), so nothing a save could write differs at any
  point. This matters because the call graph cannot rule out a yield under `OnReset`
  (`tools/swap-stack-audit.ts`: `OnReset > RemoveOneShotIfNeeded > ... > StateMachineClient::RequestState`, a
  virtual-call over-approximation through `Destroy` [inferred not real, I-626-2]); the swap has a stub, and a first
  design that marked the kept timers `AlwaysPersisted` for the call was dropped for this reason.
- While EA's body runs, the kept alarms are missing from the owner index only. A `RemoveAlarm` on one of them in that
  window still clears it (the index entry is optional there); such a handle is not put back.
- All timers under one handle come from one Add call with one type and one repeat flag [verified, the three
  `AddAlarm*` bodies], so the decision is per handle; a handle that still mixed decisions would go EA's way.
- The kept set is the one vanilla keeps for the same Sim on any normal lot (there vanilla keeps all of them).
- An error while reading the timers keeps nothing (EA's way) and counts [1833]. EA's body always runs.
- Leaks: no index or cache. The probe keeps one string (the last reset) and a line counter (600 lines per session,
  under the native 400-per-prefix cap anyway).

Mode 2 (after EA's body, normal lot only): a handle whose live timers are all `DeleteOnReset` and on `sSraDropList`
(`<declaring type>.<method>`) is removed with EA's `RemoveAlarm`. Entries need probe data plus a read of the site
(docs/352 §5). Empty today: [1831] must stay 0.

## 3. Household rule

Mode 1 of `simresetalarms` applies to **every Sim, household and selected Sims included**, with no exemption.
It is an **EA bug fix** (CLAUDE.md: "EA bug fixes stay"), not a deadline, cap, skip, throttle, teleport, fake or
relaxed path. It is not claimed as proven exact:

- [verified] Its result for a Sim is the one vanilla produces for the same Sim on any normal lot: the same timers,
  untouched (no new time, type or callback).
- [inferred, I-626-7] That keeping these alarms is what EA intended (keep = EA intent). The flag test
  (`DeletedOnReset` covers `NeverPersisted`) reads the other way; the argument is that EA's own systems lean on
  survival (the occult day alarms are armed only at load, docs/352 §3.1) and that Sims are reset on lots nearly always.
- So the 30 repeating `NeverPersisted` registration sites must be read one by one (docs/352 Q2; only the vampire
  pair is read so far) to confirm none of them is re-armed after a reset or must die with it. **Switching
  `simresetalarms` ON needs both: this read done and queue `sim-reset-alarms-correct-1126` passed.**

The probe prints `hh=1` for household and selected Sims so the two groups can be counted apart. The repro changes
only townies (the agent refuses household, selectable and unknown-scope Sims).

Mode 2 removes alarms vanilla keeps on a normal lot, so it is **NPC only in code**: `SraDecide` takes
`!IsPlayerSim(sim)` and never drops for a household, selected or unknown-scope Sim (control in
`SimResetAlarmsCheck`). The list is empty; each entry still needs its own review.

If the reset destroys the Sim, the kept handles are removed (EA's `RemoveAlarm`) instead of put back: dispose ran
while they were out of the owner index and could not clear them.

Saves: `NeverPersisted` timers are never written, the fix adds nothing to a save, no load-time cleanup is needed
(docs/352 §5). Large or streamed worlds: the work per reset is proportional to that Sim's own timers; nothing scans
the world or depends on its size.

## 4. `serviceleave` and a still-live timeout (I-621-6): our bug, fixed under `serviceleave` itself

`SlRearm` removed the old handle and armed the timeout again with EA's full delay after every worker reset. On a
normal lot the old timer is still live (docs/352 §1), so nothing was repaired and the delay (arrive, leave, repairman
work limit, babysitter leave / hunker-down) started again from zero [verified, our source]. A worker that is reset
again and again inside the delay would never time out. Vanilla on a normal lot keeps the original time, so this is a
change to a case EA has right: a bug of ours, small in effect [inferred: no report, I-626-4].

Fix (under `serviceleave` itself, no second switch: it repairs our own shipped-on fix; coordinator decision
2026-11-26): `SlRearm` first asks whether the global manager still knows the handle (`SraLive`); if so it leaves EA's
timer alone and counts [1832]. On the world lot the handle is gone and the re-arm runs as before. The maid's finish
alarm (handle left set after it fired) is not live, so its own rule still decides. `ServiceLeaveCheck` carries the
decision (`live timeout left alone`) and its controls. If the timer lookup is unavailable the rule answers "not
live" and `serviceleave` behaves as before.

Checks: `ServiceLeaveCheck run` proves the lookup itself on a throwaway global alarm (known while armed, unknown
after `RemoveAlarm`, unknown for the invalid handle; a miss is UNEXPECTED, `live lookup=`). Live arms of repro
`service-leave-reset` (agent command `service_leave_live`, never run): a repairman is called to the home lot by EA's
`MakeServiceRequest`. Street arm: reset while it waits on the world lot (`WaitToRoute`): the handle changes, [2517]
+1. Lot arm: reset on the lot in `Fixing` or `HangAroundBeforeLeaving`: same handle, time left not above the value
before, [1832] +1, [2517] and [2518] +0. Each arm writes one `serviceleave-live:` line. The repro's last step and
cleanup now leave `serviceleave` on (they set it off before, against the shipped default).

## 5. `kServiceLeave`: which default wins, what shipped

- `Instantiator.cs`: `kServiceLeave = true`. Package tuning (`TUNING_XML` in `tools/setup/build-fixes.ts`):
  `<kServiceLeave value="True" />`. Both agree: **on**. Where they differ the package tuning wins at start-up
  (EA's tunable loading overwrites the static) [inferred, I-626-5; not needed here since both are true].
- Both were set by the import commit `00ca05029` (2026-11-15, "14 switches on", contributor evidence in docs/564).
- Shipped: v0.2.9 does not contain the switch; **v0.3.0 ships it on** (tag sources and docs/PLAYERS.md list
  `kServiceLeave` among the v0.3.0 tunables that are on) [verified, `git show v0.3.0:...`].
- The mismatch was text only. Fixed here: the `ServiceLeave.cs` header, docs/564 intro, the docs/index.md line and
  the `service-leave-reset` repro's `fix` text said "off".

## 6. Repro and queue

Repro `sim-reset-alarms` (never run): `pick_sims` takes two townies. Per arm one Sim: `sim_reset_alarms start`
(vampire occult through EA's `AddOccultType`), `place lot` (Teleport Me Here, as `setup_sims`), `reset`
(`Sim.SetObjectToReset`), `place street` (a `WorldTerrain` / `WorldRoad` point off every lot near the Sim's home
lot), `reset`. After each reset the step polls `status`: `vampire_alarms` is the number of the occult's two handles
the global manager still knows, read by the agent with EA's `GetTimeLeft` (no ts3fixes code).

| Arm | Normal lot | World lot |
|---|---|---|
| off (`picked:1`) | 2 | 0 (the bug; staying at 2 fails as a hollow repro) |
| fix, mode 1 (`picked:2`) | 2 | 2, `kept` >= 2 |

Then `SimResetAlarmsCheck` `last` / `run` / `result`. Cleanup removes the vampire occult; nothing is saved.
Skipped from the docs/352 idea: waiting for sunset to see the callback run (a long wait for a consequence of the
count).

Queue `sim-reset-alarms-correct-1126` (correctness, about 60 min, Bin state `apex`, dry-run clean):

1. `bp-simresetalarms-probe`: BP 10 min natural play, probe on, fix off. Answers docs/352 Q1. Nothing kept, nothing
   removed, 0 script errors.
2. `bp-simresetalarms-fix`: same play, fix (mode 1) + probe on. 0 script errors, [1831] and [1833] stay 0.
3. `bp-sim-reset-alarms-repro`: the repro; the four probe head lines and both `kept (fix)` lines are required.
4. `bp-service-leave-reset-live`: repro `service-leave-reset` with its live arms (§4); both `serviceleave-live:`
   PASS lines required, a FAIL line forbidden.

No timing queue: an EA bug fix on a rare path, no gain claimed. No RUN FIRST queue: nothing built here depends on a
measurement; job 1 decides only whether the fix goes ON (rare-fix rule, docs/352 Q1).

Probe line format (ts3native.log):
`ts3fixes: simresetalarm: reset sim="<name>" lot=world|other hh=0|1 mode=0|1|2 scoped=<before>><after> npr=<b>><a> vampire=<b>><a>`
then per reset-scoped timer `ts3fixes: simresetalarm:   <type>.<method> type=DeleteOnReset|NeverPersisted rep=0|1 -> kept|dropped|kept (fix)|dropped (list)`.
`lot=world` means the global manager was asked; the after counts are read from the manager after EA's body ran.

## 7. Builds

| Build | File |
|---|---|
| `ts3fixes.package.simresetalarms-1126` | `game-copy/ts3fixes.package.simresetalarms-1126` |
| `ts3agent.package.simresetalarms-1126` | `game-copy/ts3agent.package.simresetalarms-1126` |

Hashes: `docs/data/builds.json`. Native: master set m1126b unchanged (`ts3native.asi.m1126b`, `ts3native.ini.m1126b`).

## 8. Open

- Q1 of docs/352 (how often a reset asks the global manager): queue job 1.
- docs/352 Q2 (the per-class read of the 30 repeating `NeverPersisted` sites) is still open and is a condition for
  ON (§3); mode 1 keeps all of
  them because vanilla keeps all of them on a normal lot.
- After the queue passes and Q2 is read: turn `simresetalarms` ON for the RC (code default and package tuning).
- **For the merger:** the `docs/registry.md` row for slots 1828-1833 and the "next free" row were written by
  `tools/registry.ts reserve` on this branch; regenerate them with `tools/registry.ts` at merge (do not hand-merge the
  row), then check the row says the slots are in use by `SimResetAlarms.cs`.
