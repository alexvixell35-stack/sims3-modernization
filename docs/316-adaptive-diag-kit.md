# 316. Adaptive diag kit: level 0 logging, rules engine, known issues, make report

2026-11-21, built + offline tests, smoke queue PREPARED, NOT RUN (ledger §571; branch `feature/adaptive-diag-kit-1121`,
ts3native 0.36.298-adk1, ts3fixes adk1-1121, kit 1121.a1). Interface for the benchmark lane: docs/317.

Everything here is off unless `[AdaptDiag] Enabled=1`. That section exists only in the kit's fragment
(`tools/player/ts3-kit/payload/adaptdiag/ts3native-adaptdiag.ini`), which the kit merges into the **diagnostic** build's
ini at install. The release ini and `native/ts3native/ts3native.ini` are unchanged. The kit reads the game and changes no
Sim, save or switch the player sees: no household, selected-Sim or unknown-scope exemption is needed because no code path
touches a Sim (activity is read from `GameStates` / `Gameflow` only).

## 1. Prerequisite: ModTime pass-through hook label (docs/298 §5.1, built)

`modtime.cpp`: when the picked frame of a sample is a ts3fixes frame (`MDT_OURS`) and frame 0 (own time) is game code
(`MDT_GAME`), the sample goes to a pseudo row `game via ts3fixes hook (<Class>)` (image 0, kind game) instead of
Ts3Fixes. Ts3Fixes keeps samples whose frame 0 is ours. This covers FestivalHiddenHook, EcHook, TrafficCreateHook,
SpatialCacheHook and every other swap that only calls through [verified: `MdtViaHook` / `MdtHookLabel` selftests on
{GAME, OURS, GAME} and {OURS, GAME}]. The sampler sleep is now `1000 / g_mdtHz` per loop, so the rate can be raised at run
time (hitchstack drill-down).

## 2. Level 0 (always on in the kit)

| Input | Source | Cost |
|---|---|---|
| sim iteration times per activity (count, >= 250 ms, >= 1000 ms, longest) | `SimLoopAdd(AdIter)` | 2 QPC reads per iteration |
| activity tag: live / cas / buildbuy / travel / paused (ts3fixes, once a second on change), load, save (`g_udSaveTick`) | UserDiag op 63 sub 0 | one DiagSend per change |
| hitch, stall, modtime, route wait, memdiag, forensics, loadprof line counts; last hitch / stall line | log tap `g_logTap` (TryEnter only) | prefix compares per log line |
| largest free block, commit | `UdMemWalk` once a minute | VirtualQuery walk |
| script errors since the session start, newest file head | `ScriptError*.xml` in the user folder | one FindFirstFile per minute |
| other-process CPU, clock vs max, flagged share | PcCheck accumulator deltas | none extra |
| temperature | PDH `\Thermal Zone Information(*)\Temperature`, Kelvin to C, no admin [inferred I-571-2] | one PDH query per minute |
| AV / indexer presence | Toolhelp snapshot against a name list (names only) | one snapshot per minute |
| mod set + mod config hash | names + sizes + mtimes of `Mods\Packages`, contents of `.cfg .ini .toml .xml .json .conf` in Mods and Bin (own files excluded) | at start |

The thread `AdMain` runs below normal priority with 1 s ticks: a CSV row each minute (`adaptdiag.csv`, docs/317 §2),
the install check 2 minutes after the world is live, the rules, and the report every 5 minutes, at world quit and at
exit. Own CPU time is printed in `## session` (target <= 1 ms/s; not measured yet, needs the smoke).

**Noise grade** per minute (`AdNoiseGrade`): C when other-process CPU >= 10 %, clock below 85 % of max or more than half
the samples flagged; B when other CPU >= 5 %, clock below 95 %, any flagged sample or an AV scanner busy; A otherwise;
`?` without PcCheck data. Summaries (`## activity summary`, `## benchmark`) use A and B minutes only.

## 3. Drill-downs (reused detectors, no new hooks)

| id | Detector | When | Runtime change / next session keys |
|---|---|---|---|
| hitchstack | HitchAttr + StallWatch + ModTime | >= 2 iterations >= 1000 ms in 5 min (sev 3) or >= 10 >= 250 ms (sev 2) | run time: hitch 250 ms, 120 lines/min, StallWatch 1000/250/333 ms, 8 lines, ModTime 50 Hz (~3.5 ms/s) |
| routewait | HitchAttr job names + route lock | >= 3 minutes in a row with route-job wait lines (sev 2) | run time: JobNameMs 100 ms, lock log 50 ms |
| scripterr | swapinvokediag (docs/312) | >= 2 script errors in 10 min (sev 2, 3 at >= 5) | next session: `AdaptDiag.SwapInvokeDiag=1` (ts3fixes asks op 63 sub 3) |
| memowner | MemDiag alloc sites | largest free block < 256 MB (sev 4) or a >= 300 MB drop (sev 3) | next session: `MemDiag.Enabled/AllocSites` |
| loadsplit | LoadProf | load >= 5 min (sev 2) | next session: `LoadProf.Enabled=1` |
| crashforensics | CrashForensics + VbGuard | last session ended without a clean exit | same session (overlay before CrPreLog) |

Rules (`AdRules`, once a minute): rank = severity x frequency; at most `MaxActive` (3) active; `AdPick` takes the highest
ranks that fit `BudgetUsPerS` (5000) and turns the lowest rank off first; each drill-down has a TTL (`TtlMin` 15) and a
line cap (`LineCapKB` 4096 of log growth while on); the log cap (`LogCapMB` 200) turns everything off. One run per
session per drill-down. Every turn-on / turn-off / next-session decision is one `adaptdiag:` log line with its evidence.
Next-session keys go to `[Escalate]` of the state file; at the next start `AdOverlay` copies the ini to
`ts3native.adapt.ini`, applies only allow-listed keys (`AdAllowed`), records them under `[Applied]` and deletes
`[Escalate]`. **A/B and benchmark sessions** (`Arm` set or `Benchmark=1`) never change detectors at run time: the same
decision goes to the next session, so both arms run with identical diagnostics.

## 4. Known-issue library

`payload/adaptdiag/ts3native.adaptdiag-known.tsv` (installed as `Game\Bin\ts3native.adaptdiag-known.tsv`), tab separated
`id kind edition match label doc fixedin switch`. Kinds: crash (the `fault:` frame of the newest `crash-*.txt` after an
unclean exit), script (ScriptError head), hitch (last hitch / stall line), log (any line). Match = case-insensitive
substring; edition steam / retail / ea / any. A match labels the issue `known: <label> (<doc>), fixed in <ver> / switch
<name>` in the report and takes no drill-down slot. Rows: the docs/67 Steam 1.67 crash sites (GeomBoneIndex, ThumbPoseBones,
GeomNoMaterial, WorldAStarRefs x3, LotLevelPastEnd x3, FglStaleObject, SlaveUnlink from docs/143, VbFreeDelay by
`SetStreamSource`), the CAS TargetException (docs/312) and the docs/247 §11 hitch causes (wildflower, boat replans,
CAS/SimService triangle sort). Crash frames are `TS3W+0x<rva>` as `CrFrame` writes them [verified: crashreport.cpp]; EA 1.69
and retail rows need their own addresses (none yet).

## 5. Install self-check

At start the tap marks each expected part seen (an `OK   ` line, or a ts3fixes `patched` line, contains it) or bad (a
`SKIP` / `FAIL` / `REFUSED` / `not patched` line names it). ts3fixes logs `adaptinstall: patched N, failed M: ...` once per
world load from its applied log; failed names minus the edition's `allow:` rows count as missing. Two minutes after the
world is live the check logs `adaptdiag: install check: OK` or `MISSING n`, and ts3fixes shows one notice (after 3 min)
asking for option 8. Lists: `payload/adaptdiag/expect-{steam167,retail167,ea169}.txt`, generated with
`node tools/adaptdiag-expect.ts <clean kit start log>` (one stable name per OK line, failed patches as `allow:`). **All
three lists are empty for now**: no clean reference start of the kit build exists per edition. With an empty list the
check reports only ts3fixes' failed patches.

## 6. Mod-set change, A/B validity

Each start appends the previous session to `adaptdiag-sessions.csv` and writes `[Live] ModSet / Config`. The report's
`## a/b validity` compares the current arm with the newest session of another arm: a changed mod set or mod config marks
the comparison `INVALID`.

## 7. Make report (kit option 8, `-Mode Report`)

Lists the log / diag files in each Game\Bin and their size (capped at 5 MB per file), says whether an adaptive report
exists, asks, then runs the normal masked diagnosis (user name, home path, computer name, other profiles masked) which
now also copies `adaptdiag-report.txt`, `adaptdiag*.csv`, `adaptdiag-bench*` and `ts3native-crashes\crash-*.txt`; prints
the zip size and resets `[Session] Benchmark=0`.

## 8. Switches (all default off; kit diag ini only)

`[AdaptDiag] Enabled` (0), `MaxActive` (3), `TtlMin` (15), `BudgetUsPerS` (5000), `LineCapKB` (4096), `LogCapMB` (200),
`SwapInvokeDiag` (0). Kit test only, never in a player kit: `InduceHitchMs` (0; 3 sim-thread sleeps 20 s apart),
`InduceAfterS` (120), `InduceScriptErrors` (0; ts3fixes adds that many throwing tasks from 150 s).

## 9. Tests

- Native selftest `TestAdaptDiag` (adaptdiag-test.h): FNV, allow list, entry split, noise grade, signature parse / match /
  label, dominant activity, `AdPick` budget order, failed-patch minus allow list; `modtime-test.h`: hook label cases.
- `node tools/adaptdiag-expect.ts --self-check` (real OK / SKIP / stat lines, adaptinstall lines).
- Kit: `TS3Kit.ps1 -Mode SelfTest` (regex cases for every new regex, state-file helper, ini merge, library and list files)
  and `test-kit.ps1` (diag install puts the files and `[AdaptDiag]` in, uninstall removes them, `-Mode Report` zip holds
  the report / csv / bench files masked, benchmark flag reset), both on Windows PowerShell 5.1 and 7.
- Game: queue `tools/harness/queues/adaptdiag-kit-correct-1121.json` (job 1 level 0 read-back, 0 script errors; job 2
  induced hitches and script errors must log `turn on hitchstack` and `next session turns on scripterr`). NOT RUN.

Builds (docs/data/builds.json): `asi.adaptive-diag-kit-1121` (sha256 1f9c54e19ce0..., Steam 1.67), `ts3native-ea169.asi.adaptive-diag-kit-1121` (60753ada1907..., EA 1.69, /DTS3A_TABLE), `ts3fixes.package.adaptive-diag-kit-1121` (bc168eac84bd...) and `-diag` (851ce42eeba9..., kSwapInvokeDiag True). Kit zip TS3Kit-ea169-adaptdiag-1121.zip (kit e7 normal payload unchanged; payload/diag = the EA adk1 asi + the diag package + payload/adaptdiag), sha256 418969eabba357101d958491f3eaed3002edad82bf9ad4f6fe7fe2f90fa5d309, local only (probe-out/outbox), not for testers before the smoke passes.

## 10. Unverified

- Own cost (target <= 1 ms/s) and the noise grade on a real session: needs the smoke.
- I-571-1: an exception thrown from a `Task.Simulate` is written as a ScriptError file (job 2 checks it).
- I-571-2: the PDH thermal zone counter is readable without admin on the test laptops (else `tempc` stays empty).
- I-571-3: `Simulator.Sleep(0)` in the activity task runs every frame while the game is paused (else paused minutes read as
  the last activity).
- The expected-install lists, and EA / retail crash rows of the library.
