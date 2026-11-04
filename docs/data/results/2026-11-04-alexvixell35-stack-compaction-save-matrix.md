# Compaction save matrix (2026-11-04, alexvixell35-stack)

PR #208 (`fix/compaction-save-result`): the save rule as `CpSaveOk` (result 0 = saved), checked with the save matrix and the
travel trips, as Rodney asked on 2026-11-04 (save path change).

Hardware: Lenovo Legion laptop, Ryzen 9 7945HX (16C/32T), RTX 4070 Laptop + Radeon 610M, AC power, "Legion Performance Mode",
TS3W on the dGPU
Game: Steam 1.67, all EPs/SPs, LAA on
Build: branch at `dd21e4f` rebased on main (`b78871d`), local asi build; ts3fixes from the branch. The last rerun adds the
single-check guard (this commit).
Install: release defaults; ts3native.ini non-default keys: `[UserDiag] Enabled=1`, `[SaveWrite] Enabled=1`,
`[Compaction] Enabled=1 ThresholdMB=1 MinMinutes=0` (every save gets a verdict line; with 1 MB the verdict stays "no")
Saves: Baseline-BP-Spring (Bridgeport), Baseline-SV (Sunset Valley), Baseline-IP (Isla Paradiso)
Method: `node tools/harness/queue.ts` with `run.matrix` jobs (save-matrix.ts: overwrite, back-to-back, unpaused, build mode,
counters, reload with all fixes off then on) and `save-matrix.ts --travel` jobs (vacation, save there, travel home, save at home)

| Job | Run 1 (10:26-11:34) | Run 2 (12:26) | Run 3 (12:58) |
|---|---|---|---|
| bp matrix (8 cases + reload) | cases 8/8, reload PASS; FAIL on a harness rule | cases 8/8; reload OFF FAIL (ErrorTrap report) | **PASS** |
| sv matrix | cases 8/8, reload PASS; FAIL on a harness rule | - | - |
| ip matrix | cases 8/8, reload PASS; FAIL on a harness rule | - | - |
| France | FAIL (trip dropped) | FAIL (trip dropped) | **PASS** |
| China | FAIL (trip dropped) | FAIL (trip dropped) | **PASS** |
| Egypt | FAIL (trip dropped) | FAIL (trip dropped) | **PASS** |
| University | PASS | - | - |
| Future (portal) | PASS | - | - |
| Future (direct) | PASS | - | - |

Every failure was a harness problem, fixed before the next run:
1. Run 1, matrices: the job's `logRequire` read only the final ts3native.log copy; the matrix's reload restarts the game, so the
   verdict lines were in an earlier copy. Fixed on #258 (`7d04f62`: log rules read every copy of a job).
2. Runs 1-2, World Adventures trips:
   - Run 1: the agent's dialog rule answered EA's "Name Your Saved Game" (the travel save) with cancel, and EA drops the trip. The rule now steps aside while an agent-started trip is pending (#258).
   - Run 2: the party included the household dog, which drops a vacation trip. The agent on this branch lacked the 11-02 pet skip.
3. Run 2, reload OFF: an NRaas ErrorTrap "Correction Logs" report (a car out of world) appeared on one side. It changes from load to load and compaction never ran. Fixed on #258 (`342899d`: counted as known).

## The bug the matrix found (fixed in this commit)

A save made while the game is paused leaves its `SaveGameTask` asleep in `CompactAfterSave`'s modal-window wait. In run 1 (bp matrix), 12 of them woke together after save #12, and each logged a verdict. Every verdict was "no" there. With the threshold reached, that would be 12 prompts or 12 compactions.

Fix: only one check waits at a time. A later save logs `compaction: a check from an earlier save is still waiting; this save adds none`, and the waiting check reads the newest save when it wakes.

Run 3, bp matrix: 2 verdict lines, 13 "still waiting" lines. For example, `verdict no after save #12 (result 0): largest free 1083 MB, threshold 1 MB, armed`.

Trips: the verdict ran after the save at home. Vacation saves logged `save #3 not considered: on vacation` and `save #4 not considered: on vacation`.

Crashes: none.

Not covered:
- Run 3 reran only the Bridgeport matrix and the three World Adventures trips.
- No verdict was COMPACT: the verdict is COMPACT only while the largest free block is under the threshold (1 MB here, set so
  every save gets a verdict line), and it stayed above 1 GB. So the prompt and `CompactGo` themselves did not run here.
