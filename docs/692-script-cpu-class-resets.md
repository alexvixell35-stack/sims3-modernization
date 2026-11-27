# 692. Script-CPU frames after the routing fixes: Sims University class-change resets (termlenguard)

Written 2026-11-26 (UTC). Soak logs s570a-f, s598a-d, s603a-b (combo 35decac9, upstream defaults) and new runs s612b/d
and s612a/c (A/B), decompiled C#, NRaas source (`game-copy/nraas-src`, cite only). Tags: [measured], [verified]
(decompiled or our source), [inferred].

## 1. Classification of every frame of 300 ms or more

Method: each `hitch: frame N ms` line of 300 ms or more, the stall-watch leaves between it and the previous hitch line
(innermost frame; 16 frames deep with `[StallWatch] Managed=16` in s612), the sim clock of the matching ts3fixes
`hitchwatch:` line (within 3 s), and the ts3fixes lines in the window. A frame with no sim clock before the first one
that has a clock is world load; a frame with no clock after it is the harness (agent `MemGc` `GC.Collect`, leak census,
end of run). Scripts: session scratchpad (`cls.py`, `tab.py`), the same fields as `hcls689.py`.

| world (runs) | class | frames | ms |
|---|---|---|---|
| SU (s570a-c, s598c-d, s603b) | load / world start (XML, tuning, reflection) | 96 | 217742 |
| SV (s570d-f) | load / world start | 45 | 118547 |
| BP (s598a-b, s603a) | load / world start | 49 | 111585 |
| **SU** | **class change: academic IndexOutOfRange -> Sim reset (new wish manager, re-instantiation)** | **95** | **67777** |
| SU | harness (agent GC.Collect, leak census, end of run) | 23 | 20491 |
| SV | harness | 10 | 7118 |
| SV | gameplay: route / placement script | 10 | 4577 |
| BP | harness | 4 | 4265 |
| SU | game start: lifetime-wish pick (one-off, Sun 8 AM) | 2 | 1777 |
| SU | gameplay: other (object queries, SimDescription.Find, ...) | 7 | 5465 |
| BP | gameplay: route / placement script, object query | 4 | 1379 |
| SV | gameplay: other | 3 | 1252 |

s603 alone: s603b (SU) 37 frames = load 15 (35.4 s), class-change resets 16 (9.3 s), harness 4 (5.2 s), game start 1,
other 1. s603a (BP) 20 frames = load 14 (38.4 s), harness 3 (3.4 s), gameplay 3 (1.0 s). So Bridgeport has no
gameplay script-CPU lead left at this threshold: its "19 of 20 script/cpu" are world load and harness frames [measured].

Trigger, stack top and leaf of the SU class-change frames (s612b, 16-deep stacks):
- Sim clock 6:30 / 10:30 / 2:30 (Tue, Thu) and 10:00 / 2:00 / 6:00 (Fri): the class changes. Every one matches an
  NRaas ErrorTrap script-error file with the same Sim and sim time (11 in s612b) [measured].
- Exception: `IndexOutOfRangeException` at `ldelem.r4` in `Motive.UpdateMotiveWithDecay` (8, Tue/Thu) and in
  `AcademicCourse.AdjustAcademicPerformanceForTest` from `AttendUniversityClassInRabbitHole.InRabbitHole` (3, Fri).
  Our `BMmHook.DecayHook` (motivemultonce) only passes through to EA's body there.
- Stack tops in the frame: `NRaas.CommonSpace.Tasks.ResetSimTask.Perform` (FunctionTask), `Sim.DoReset`,
  `Instantiation.Perform`, `PlumbBob.DoSelectActor`, `HrResetHook.AttemptToPutInSafeLocation` >
  `IsPointInLotSafelyRoutable`, `Sim.OnCreation/OnStartup`, and `DreamsAndPromisesManager.CreateAndIntitForSim` >
  `SetupForNewSim` > `DreamTree.ActivateTreeForSim` + `TryAddLifetimeWish` (1000-1250 wish nodes per Sim). The leaves
  of the old leads (DreamsAndPromises, `Query_GetObjects` after `Skewer.OnHouseholdChanged`) are this rebuild: the
  re-instantiated household Sim fires `HouseholdSimsChanged` (HUD skewer, every door's `HouseholdSimChanged`) and gets a
  new wish manager.
- The wish timer (§3): 74 wish-manager rebuilds in 30 min (s612b); a burst is 100-360 ms of which the wish code is 25-110 ms:
  the rest of the frame is the reset itself.

## 2. Root cause and fix: termlenguard

EA bug [verified decompiled]. Three sites scale academic performance by
`kCourseLoadMultipliersToAcademicPerformance[NumberOfCoursesPerDay - 1]` (4 entries) and
`kTermLengthMultipliersToAcademicPerformance[(uint)(AcademicCareer.sTermLengthEx - 1)]` (2 entries, tuning comment
"One Week Term, Two Week Term") with no bounds check: `Motive.UpdateMotiveWithDecay` (AcademicPerformance while in an
`AcademicSituationBase`), `AcademicCourse.AdjustAcademicPerformanceForTest` and
`GiveAcademicPerformanceAsIfSimDidThisInteraction`. `AcademicCareer.TermLength` has kInvalid (0) and one to four weeks.
The term length is set only by an enrolment and reset by `ClearStatics`, so a new game from the Sims University world
file (students with an `AcademicCareer`, no enrolment) has kInvalid: index 0xFFFFFFFF on every class change. A 3- or
4-week term or 0 courses a day would throw too. EA's own guarded copy of the same product (the class-tone multiplier in
`AcademicCourse`) skips a factor whose index is out of range.

- Not an inefficiency: each throw ends the Sim's update and the Sim is reset (EA's handler or NRaas ErrorTrap's
  `ResetSimTask`, a full re-instantiation). Visible: the student pops, loses the current interaction and gets new wishes
  and a new lifetime wish.
- Community: NRaas Career sets kOneWeek when it assigns an academic career itself (`AcademicHelper`), nothing else; ErrorTrap
  resets the Sim. docs/184 lists no such fix.
- Fix `termlenguard` (`TermLenGuard.cs`, ON since the 2026-11-27 import, every Sim: it is an EA crash fix): when an index is out of range,
  for the duration of EA's call the table is a one-entry `{1}` array and the index source points at entry 0 (term length
  kOneWeek, `mNumberOfCoursesPerDay` 1); both are restored in a finally. Valid indices: EA's call unchanged. The decay site
  rides `BMmHook.DecayHook` (shared hook, one patch per method). Results: identical to EA wherever EA does not throw;
  where it throws, the factor is 1 (EA's guarded convention) instead of a reset.
- Counters 4904-4908 (decay updates guarded / test adjustments guarded / skipped-class grants guarded / would guard, off /
  errors). Check `TermLenGuardCheck` (repro `termlenguard-invalid-term`), selftest `tools/selftest/termlenguard`.

## 3. Probe

The A/B builds also carried a diagnostics-only wish-engine timer (`dnpcostlog`, not part of this change). It showed
that without the resets no wish-manager burst reaches 300 ms (the costliest checks take 5-20 ms per burst), so no
wish-engine fix is needed for these frames.

## 4. A/B (s612, 30 min storm, seed 156, new game from the world file, heap 512, Managed=16, both arms with the §3 timer)

| arm | world | termlenguard | frames >= 300 ms (all) | in play | hitches >= 150 ms (h150) | academic script errors | wish-manager rebuilds | NRaas ResetSimTask |
|---|---|---|---|---|---|---|---|---|
| s612b | SU | off | 36 / 51885 ms | 20 / 16228 ms | 18 / 15630 ms | 11 | 74 | 14 |
| s612d | SU | on | 21 / 40392 ms | 6 / 4507 ms | 6 / 4425 ms | 0 | 7 (all Sun 8 AM, game start) | 0 |
| s612a | BP | off | 18 / 37242 ms | 0 / 0 ms | 3 / 2319 ms | 0 | 0 | 0 |
| s612c | BP | on | 18 / 40359 ms | 4 / 4445 ms | 5 / 4749 ms | 0 | 2 | 0 |

[measured] SU: in-play frames of 300 ms or more 20 -> 6 (16.2 s -> 4.5 s), h150 18 -> 6 (15.6 s -> 4.4 s); counters
[4904] 55621 decay updates and [4905] 141 test adjustments ran with the guarded factor, [4908] 0. The 6 left: Sun 8-9 AM
game start (1.1 s `RemoveRabbitHolesThatCantHaveJobs`, 0.36 s query) and single route / harness frames; no class-time
frame. The one non-academic script error in s612d is a "Correction Logs" file (destroyed doors / clothing piles).
BP: the guard never fires (no counter, no students); the 4 in-play frames of s612c are one 747 ms route frame with a GC
(Mon 2:22 AM), two agent `MemGc` `GC.Collect` frames (harness) and one 883 ms hashtable frame: run-to-run noise, not the
switch. Load frames (18 each) are equal.

Proof status: SU A/B one run per arm, the class-change reset frames are gone and the mechanism is confirmed by counters
and error files; BP unaffected. Not yet repeated on another machine.

## 5. Leads left

- SU game start: `Occupation.RemoveRabbitHolesThatCantHaveJobs` under `AcademicCareer` course setup (`ComboJobHook`
  `InitializeBaseJobCreationSpec`), 1.1 s once at Sun 9 AM (s612d); the Sun 8 AM lifetime-wish pick (0.8-1.1 s, once).
- Remaining in-play SU frames are single 300-470 ms object-query / route frames, no repeat pattern.
- Harness frames (agent `MemGc` `GC.Collect`, leak census) are tooling, not gameplay; save/load is out of scope.

## 6. Harness note

The first s612b start raced camsafe's s605b: both waiters saw the same gap between games and one game served both
scripts (phase-20261126-0656, our package, mixed ini). s612-run.sh now waits for every other `sNNN-run.sh`, game and
`phasesoakr.sh` to end first.
