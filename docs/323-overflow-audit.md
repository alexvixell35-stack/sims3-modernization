# 323. Overflow audit of our own code (2026-11-22)

Status: static audit on branch `feature/overflow-audit-1122`; fixes on `bugfix/overflow-fixes-1122` (§5: per finding, built + selftest, NOT RUN in game).
Scope: `native/ts3native` (all non-test `*.cpp`/`*.h`), `mods/ts3fixes/src`, `mods/ts3agent/src`, and how
`ts3native.ini` values are read. Skipped: test code (`*-test.*`, `selftest.cpp`), address tables (`ts3addr*`,
`ts3bytes*`, `vcallsites*`, `stallnames*`), build folders.

Not repeated here: world-size tables (docs/321), and the caps being fixed in another lane right now (AutShadow
1024/8192, plandeadline `kKdRows=64`, NearVenue 32, SlaveUnlink `SL_CAP=64`).

What we looked for: fixed tables with unchecked indexes or silent drops at a cap; string and buffer overruns
(`sprintf_s`, `swprintf_s`, `wcscpy_s`, `memcpy`, wchar count vs byte size); 32-bit tick and ms counter wrap,
QPC multiplies, `size*count`, signed/unsigned compares, narrowing casts; caches that grow for the whole session;
ring buffer full/empty mistakes; managed static collections that keep dead Sims or old worlds alive; trampoline
stolen-byte lengths; ini values that are not clamped.

Method: eight read-only passes (four native slices, two ts3fixes slices, ts3agent, ini reads), grep first and then
reading the code around each hit. Every candidate above "minor" got a second, adversarial read of its whole path
(callers, caps, clamps, who clears it, which thread). Anything a guard already prevents was dropped: about 220
candidates in total. The five ON items ranked highest were re-read a third time for this doc (UserDiag, SaveWrite,
JobYield, AutLive, mono fault text).

Tags: **CONFIRMED** = the path was read end to end and the overflow happens with the stated input; **PLAUSIBLE** =
the code is wrong, but the trigger was not reproduced, or it depends on an input we did not find in the game data.
Defaults: native keys from `native/ts3native/ts3native.ini` (the release ini, which is the code default unless noted);
managed switches from the `k*` tunable defaults. Code without a switch counts as ON. "Kit" = the adaptive diag
tester kit `tools/player/ts3-kit/payload/adaptdiag/ts3native-adaptdiag.ini`, which turns on `[AdaptDiag]`,
`[CrashReport]`, `[Diag] ModTime`, `[UserDiag]`, `[StallWatch]` and `[PcCheck]`.

## 0. Summary

- **One cross-cutting cause behind the worst native items: the DLL links the static CRT (`/MT`, `build.cmd`) and
  installs no `_set_invalid_parameter_handler`.** So any `sprintf_s` / `swprintf_s` / `wcscpy_s` that overflows its
  buffer does not return -1. It calls `_invoke_watson` and the game process is killed on the spot. Every
  `if (swprintf_s(...) < 0)` and `> 0` check in our code is dead code. The `_TRUNCATE` forms
  (`_snprintf_s` / `_snwprintf_s` with `_TRUNCATE`) are the only ones that fail soft; `Log` and `CrPut` already use them.
- **The realistic overflow is the Windows path length.** Folder walkers build `dir + "\\" + cFileName` into
  `wchar_t[MAX_PATH]`. A Mods folder holding one file whose full path is 258 characters or more (nested CC folders
  from archives, a long or OneDrive Documents path) makes `UserDiag` kill the game at every start (H1). The same
  pattern sits in SaveWrite (crash on save, H2) and in three diagnostics walkers that the tester kit turns on (H3).
- No hard memory corruption found in the hot ON paths (AutShadow, CasTrim, MonoJit tables, SATB, rings, hash
  tables). They are consistently guarded: masked rings, cap checks before every push, generation counters reset
  before wrap, unsigned `now - t` tick math (no `GetTickCount` 49.7-day bug found), clamped ini reads.
- Managed side: no crash found. Two bounded leaks keep dead Sims or old-world objects alive after quit to menu
  (M3, L-row ErrorTrapLoop); the rest of the static collections have a cap-clear, a world-load clear, or a
  per-request clear.
- ini: 749 reads (725 int, 24 string). About 470 are clamped, about 275 are on/off flags, mode tests or masks
  that are harmless unclamped. Four real gaps, all in OFF or test-only code. All 24 `GetPrivateProfileStringW`
  calls pass the wchar count, not the byte size.

| | High | Medium | Minor |
|---|---|---|---|
| Release default ON | 2 | 3 | 9 |
| OFF in release (ON in the tester kit) | 1 | 0 | 0 |
| OFF (built, test or diagnostics) | 0 | 0 | 10 |
| ts3agent (test machines only, never shipped) | 0 | 2 | 2 |
| **Total** | **3** | **5** | **21** |

## 1. Findings, ranked (default ON first)

### 1.1 Release default ON

| # | Sev | Tag | file:line | Kind | Trigger | Consequence | Switch = default | Fix |
|---|---|---|---|---|---|---|---|---|
| H1 | High | CONFIRMED | `native/ts3native/userdiag.cpp:386, 393, 441` | `swprintf_s(p[MAX_PATH], "%s\\%s", dir, cFileName)` in `UdScanPackages` / `UdTreeBytes`; the `< 0` check is dead (see §0) | Any file or folder up to 5 levels under `Mods\Packages` with a full path of 258+ chars. Archives with nested long folder names, long or OneDrive Documents paths | Game killed at startup, at every launch, while the file exists. Looks like an EA crash to the player | `[UserDiag] Enabled=1` (code and ini) | Shared path-join helper with `_snwprintf_s(..., _TRUNCATE)`; skip the entry (count it) on -1 |
| H2 | High | PLAUSIBLE | `native/ts3native/savewrite.cpp:41` (`Join`), `:223` (`"%s.backup"`) | Same `swprintf_s` MAX_PATH overrun on the save worker. The copy goes to `<user>\ts3native-backup.tmp\<file>`, which is longer than the game's `<user>\Saves\<name>.sims3\<file>` when the save name is under 8 chars | Save whose game path is within ~8 chars of MAX_PATH (long user or Documents path, short save name); `.backup` adds 7 chars to a `src` that was only checked against MAX_PATH | Crash on save; the session since the last save is lost (whether the old save on disk stays intact was not checked) | `[SaveWrite] Enabled=1 Mode=1` | Same helper; on overflow return false so the job is skipped and the game's own copy path runs |
| M1 | Medium | PLAUSIBLE | `native/ts3native/ts3native.cpp:367` (`MonoFaultText`, buffer `mono[1500]` at `:445`) | `sprintf_s` overrun: fixed text ~215 + caller (119) + callee (119) + stack text (1099) = up to ~1552 | An access violation inside the fast call path with 16 script frames and long class names (NRaas generic names) | Fastfail inside the vectored exception handler: the fault line is lost, and a fault Mono could have turned into a NullReferenceException becomes a hard kill | `[MonoFast] CallPath=1` (ini; code default 0), `[Crash] Log=1` | `_snprintf_s(d, room, _TRUNCATE, ...)` in both branches |
| M2 | Medium | PLAUSIBLE | `native/ts3native/jobyield-core.h:62`, `jobyield.cpp:76` | Entries dropped at a cap: `JYP_SLOTS = 8`. When the park table is full, `JypPark` returns 0 and leaves the route's records in the single slots, but `JyBusy` still returns 1 and the task sleeps | More than 8 route tasks asleep at once in one async job (busy lot, 10 s sleeps) | Silent wrong result: the next route's marks overwrite the records, or a plan with no route handle takes this route's scope (the docs/183 §7 hazard): wrong deadline class or scope. Only counted as `jyLostN` | `[RouteAsync] JobYield=1` | Let `JypPark` report "no slot"; on that, `JyBusy` returns 0 (do not sleep) |
| M3 | Medium | CONFIRMED | `mods/ts3fixes/src/AutLive.cs:1081, 1139-1140, 1162` | Static `Dictionary<object,int> sMetaStart` keyed by the `Autonomy` object; removed only when the search reaches the push; cap = clear past 256; the world-quit reset (`:250`) does not clear it | Searches that never push: Sim destroyed or reset mid-search, quit to menu or travel mid-search | Bounded leak: up to 257 `Autonomy` objects keep their actor Sim, and through it old-world objects, alive after a quit (the issue #40 pattern that `sEnq` was fixed for) | `[AutShadow] Mode=3` (go-live) | Key by `a.Actor.ObjectId.Value` like `EnqKey`; add `sMetaStart.Clear()` to the `:250` reset |
| L1 | Minor | PLAUSIBLE | `native/ts3native/monojit.cpp:1663-1673, 1712-1720, 1834` (`fix[]` at `:427`) | Fixed array: `fix[]` holds `JIT_MAX_POS+64` = 4064; only the switch case checks `nf + cnt > 4000`, later branch pushes do not | A method with ~3940+ switch cases followed by 64+ branches; none found in the game scripts | `fix[]` runs into `wordToPos[]`: wrong native branch target or AV during compile | `[MonoJit] Enabled=1` | Check `nf < JIT_MAX_POS + 64` before every push; refuse the compile otherwise |
| L2 | Minor | PLAUSIBLE | `native/ts3native/monojit.cpp:1432-1433, 1877` | JIT code budget (256 / 768 bytes per position) never compared with the emitted size | A template longer than its budget; none found in the templates read | Silent write past the reserved range into the next method's code | `[MonoJit] Enabled=1` | `if (bytes > need)` log and refuse; selftest assert of the largest template sizes |
| L3 | Minor | PLAUSIBLE | `native/ts3native/equalsfast.cpp:113-117, 152-154` | Bounded pool used up: `EqLearn` takes a `g_eqPool` slot on every call for a class whose publish failed (16-probe window full) | One hash cluster fills a probe window, then that class's Equals repeats | The 4096 pool runs out; every later class stays on EA's slow walk (results stay correct, gain lost) | `[EqualsFast] Enabled=1` | Store a reject plan on publish failure, or `EqFind` again before taking a slot |
| L4 | Minor | PLAUSIBLE | `native/ts3native/apex.cpp:43-45, 124`; `native/ts3native/s3mm.cpp:~17` | `swprintf_s` path overrun in DllMain-time probes | Documents path over ~200 chars (`...\Apex Radiance\ApexRadiance.toml`), or exe folder over ~231 chars | Process killed at DLL load | Apex probe whenever Apex is loaded (baseline); `[Compat] S3mmStandDown=1` | Same helper, skip the probe on overflow |
| L5 | Minor | PLAUSIBLE | `native/ts3native/ts3native.cpp:6730` | `swprintf_s(log, "%s.launcher.log")` adds 9 chars past the `n < MAX_PATH` check | Install path of `ts3native.asi` of 251-259 chars | Launcher process killed | launcher start, always | `_TRUNCATE`, or check `n + 9 < MAX_PATH` |
| L6 | Minor | CONFIRMED | `native/ts3native/fsro.cpp:7, 14-17` | Session cache `g_fsc[4096]` keyed by x/z/level/flags only, never cleared on world change; `(LONG)(now - tick)` age compare | New world within `MaxAgeMs` (60 s) at the same coordinates; or an entry older than 24.8 days | Wrong footstep surface (cosmetic), bounded | `[FootstepRO] Enabled=1 Mode=1` | `memset` on world load or quit; unsigned age with an explicit empty flag |
| L7 | Minor | CONFIRMED | `mods/ts3fixes/src/ErrorTrapLoop.cs:25, 51-58` | Static `Dictionary<ScriptProxy,...> sErrLoop`; removed only on the 3rd repeat; clear past 256; no world-quit clear | Tasks that throw once or twice under NRaas ErrorTrap re-runs, then quit to menu | Bounded leak: up to 257 dead proxies and their targets kept across worlds | `errortraploop` = ON (needs NRaas ErrorTrap) | Clear in the world-quit reset, or key by the target's ObjectId |
| L8 | Minor | CONFIRMED | `mods/ts3fixes/src/EpBatch4bLogic.cs:40, 124` | Static `List<Sim> sJgPending`, no world-quit clear | Look-out tower deleted or reset, or quit to menu, inside the 3-minute exit alarm | Dead Sim refs kept across worlds; linear `Contains` scans | `kJungleGymExitQueue` = ON | Clear on world quit; drop `HasBeenDestroyed` Sims in `JgPurge` |
| L9 | Minor | PLAUSIBLE (not an overflow) | `native/ts3native/apex.cpp:39-40` | `RegGetValueW` with `RRF_RT_REG_EXPAND_SZ` and without `RRF_NOEXPAND` returns `ERROR_INVALID_PARAMETER` (per the API docs, not run) | Always; it matters when Documents was moved (OneDrive) | Apex TOML not found, so Apex switches read as defaults and stand-down decisions can be wrong | with Apex | `RRF_RT_REG_SZ` alone, or `SHGetKnownFolderPath(FOLDERID_Documents)` |

### 1.2 OFF in release, ON in the tester kit

| # | Sev | Tag | file:line | Kind | Trigger | Consequence | Switch = default | Fix |
|---|---|---|---|---|---|---|---|---|
| H3 | High | CONFIRMED | `native/ts3native/crashreport.cpp:250, 258`; `adaptdiag.cpp:336, 340`; `modtime.cpp:561, 568, 594` (`MdtWalk` also overflows `subRel`) | Same `swprintf_s` MAX_PATH walker overrun as H1 | Same long-path file under `Mods`, `Mods\Packages` or other `Documents\Electronic Arts\*\Mods` folders | Game killed. In `CrPackages` this runs inside the crash handler, so the crash report itself is lost, exactly for the players we hand a kit to | `[CrashReport] Enabled=0`, `[AdaptDiag] Enabled=0`, `[Diag] ModTime=0` in release; all three **=1 in the kit ini** | Same shared helper as H1 |

### 1.3 OFF (built, test or diagnostics)

| # | Sev | Tag | file:line | Kind | Trigger | Consequence | Switch = default | Fix |
|---|---|---|---|---|---|---|---|---|
| L10 | Minor | PLAUSIBLE | `native/ts3native/unseentable.cpp:54` (with `:38, :84-91, :103-117`) | Open addressing: `g_utbGen` starts at 0 and zeroed slots also have `gen == 0`, so generation 0 looks full and `UtbInsert` never finds a free slot | `RefreshTicks >= 2` and the first ask arrives before the first build (no camera yet) | Sim thread spins forever (hang) | `[UnseenTable] Enabled=1`, but only with `RefreshTicks >= 2` (default 1 always builds first) | Start `g_utbGen = 1`, or build when `g_utbGen == 0` |
| L11 | Minor | CONFIRMED | `native/ts3native/castrim.cpp:644-646` (`CtFrames`) | Loop guard `w < cap-40` is smaller than one entry (up to 48 bytes); `sprintf_s` kills the process | Stack frames with module names of ~33+ chars | Process killed in the trap log path | `[CasTrim] WriteTrap=0` | `_snprintf_s(..., _TRUNCATE)` or `w < cap - 49` |
| L12 | Minor | PLAUSIBLE | `native/ts3native/crashfx.cpp:369` (callers `:442, :610, :635`) | `sprintf_s` "module+0xoff" into `c[96]`; loops keep a 100-byte margin, one item can be 104 | Module file name of 85+ chars on the stack during a fault | Fastfail in the forensics dump | `[CrashForensics] Enabled=0` | `_TRUNCATE` in `CdCode` |
| L13 | Minor | PLAUSIBLE | `native/ts3native/iclock.cpp:41-43` | Check-then-increment cap (`< 32`, then `[count++]`) without a lock | Two threads adding the 32nd caller at once | One DWORD written past `g_ilCallers[32]` | `[IcLockCheck] Enabled=0` | `InterlockedIncrement` to reserve, then bounds-check |
| L14 | Minor | CONFIRMED | `native/ts3native/unseenband.cpp:96` | 64-bit QPC sum cast to 32-bit LONG µs; wraps after ~2147 s per site/row | Long sessions with bands set | Diag readout negative or wrong | `[UnseenProbe] Bands` empty | Keep ms, or saturate |
| L15 | Minor | CONFIRMED | `native/ts3native/compaction.cpp:161-162` (uses `:46, :83`) | ini `MinMinutes` / `AfterMinutes` × 60000, not clamped | -1, or >= 71583 | Spacing wraps: re-offers too early or never; log line at the wrong time. No crash | `[Compaction] Enabled=0` | `Clamp(v, 1, 1440)` |
| L16 | Minor | CONFIRMED | `native/ts3native/lotstream.cpp:261` (use `:214`) | ini `ObjLogMs * 1000` as LONG, not clamped | -1, or >= 2147484 | Every object load logged: log flood | `[LotStream] Enabled=0` | `Clamp(v, 0, 60000) * 1000` |
| L17 | Minor | CONFIRMED | `native/ts3native/monojit.cpp:3231-3233` -> `monojit-capture.h:225, 513` | ini `CaptureDepth`, `CaptureMaxMb`, `CaptureFirst`, `CaptureReservoir` passed raw | `CaptureDepth=2000000000` (hang), `CaptureMaxMb >= 4096` (`<<20` wraps to 0), negatives | Sim-thread hang or no capture written | test only, needs `CaptureFile` | Clamp depth 0..16, MaxMb 1..1024, First/Reservoir 0..1024 |
| L18 | Minor | CONFIRMED | `native/ts3native/unseen.cpp:1048-1049, 1063, 1093` (use `:669-670`) | `(float)GetPrivateProfileIntW(...)` converts the UINT, so a negative becomes 4.29e9 m; the `portalM < 0` "check off" branch is unreachable | `PortalM=-1` (or any negative `NearM`/`AvoidNearM`/`RateNearM`) | Throttle never holds that Sim (safe direction); a typed -1 does not do what the code says | `[UnseenThrottle]`, OFF | `(float)(int)GPI(...)` plus `Clamp(..., -1, 200)` |
| L19 | Minor | CONFIRMED | `mods/ts3fixes/src/EnvListExact.cs:53, 266` | Static `Dictionary<LotId,bool> sExLotOff` never cleared | A verify mismatch marks a LotId off; the id is reused in the next world | Exact path stays off on that lot id for the session (perf only) | `envlistmemoexact` hard-off (`Fixes.cs:1820`) | Clear on world load or quit |

### 1.4 ts3agent (test harness, never shipped)

`README.md:75` ("Test-only, never for players") and `tools/mts-zip.ts:38, 140` (refuses to pack `ts3agent.package`
or anything with `Ts3Agent`). Severity here is about voided or wrong test runs.

| # | Sev | Tag | file:line | Kind | Trigger | Consequence | Fix |
|---|---|---|---|---|---|---|---|
| T1 | Medium | CONFIRMED | `mods/ts3agent/src/plugin/Handlers/GcStreakHandlers.cs:24-46` | Unclamped args, int shift overflow, state not restored on exception | `gc_streak_force` with negative `chars`, OOM in the guard-off arm (`gcstreakat=12`), or `spacing_kb >= 2097152` (`<<10` wraps) | Black-list bits and `GC_black_list_spacing` stay forced: heap keeps growing for the rest of the job, wrong memory numbers or an Error 12 that voids the run | Clamp `chars >= 0`, `spacing_kb` 1..1048576; restore in `finally` |
| T2 | Medium | PLAUSIBLE | `mods/ts3agent/src/plugin/Handlers/HitchWatchHandlers.cs:59-67, 224-233` | Gaps timed with the wall clock; `(int)(ms*10)` | System clock step during a run (time sync after resume, manual change) | Backward step: negative gap into bin 0, lower `mSum`. Forward step: fake max hitch and p99.9 | `Stopwatch.GetTimestamp()` deltas; drop gaps < 0 |
| T3 | Minor | PLAUSIBLE | `mods/ts3agent/src/loader/Json.cs:117-170` | Unbounded recursion in the parser | Mailbox request of deeply nested `[`/`{` (64 KB cap; only a same-user process can write it) | Uncatchable stack overflow kills TS3W.exe | Depth counter, `FormatException` past 64 |
| T4 | Minor | CONFIRMED | `mods/ts3agent/src/plugin/Handlers/FocusHandlers.cs:226` | Static List without a cap | Long focus run in a crowded world | `sFocusArrivalNames` grows until `focus_reset` | Cap ~200 like `sApCleared` |

## 2. Checked, fine (dropped after the second read)

Grouped; each was a grep hit that a guard already covers.

- **Tick math:** every `GetTickCount` / `timeGetTime` compare we found is an unsigned `now - t` or a signed delta
  (pkgprobe, pkglock, gvro, routephases, p5, fglprof, lptrim, pccheck, unseenfbc, monojit, ts3fixes `TickCount`
  uses). Long-lived timers use `GetTickCount64`. No 49.7-day bug found.
- **Rings and queues:** masked or count-checked (disc, prRing, aaEv, ctGRing, heightcache ring 64, hostcalls door
  rings, VbFreeDelay ring, AutShadow learn ring, ReplanBurst, `sOffBcRing`). Lock-free single producer where used
  (`UdMemQ`).
- **Fixed tables:** cap checks before every push in AutShadow (`g_asClsQ` 512, AsSet, AsOl, touch 4096, LwIds),
  steerro (`kSrCap`, `kSrNodes+1`), routesnap (`SnPush` stops at 16), p4/p4clone/p5 tables, connro, pccheck (prunes
  exited), lptrim, MonoJit ip table (4096, checked at 4000), lazy/guard/FrBad tables, `MtLock` depth 64 (overflow
  counter), rcguard 8 and verify 16 frame stacks, snap 512-lot door cache (falls back to the guard path).
- **Hash tables:** ConformActor 17-bit hash matches `CA_CAP`, SATB 2^17 at <=50% load with epoch-wrap memset,
  managed `DirectMemo`/`RoomMemo` wrap-wipe, `WpCache` epoch needs 2^32 bumps, `SwitchCache` refuses past half full.
- **Decompression and saves:** `RefpackFast` wild copies stay inside `wend`/`send`; savecompress jobs capped by
  `b.cap`, `CcGet` checks `outLen <= cap`; saveslim DBPF count and size validated, 32-bit products cannot wrap.
- **Patches and trampolines:** stolen-byte counts end on instruction boundaries (wastar 7/11/6/6/7, optsinit 5,
  launcherfix 8, bindertd 32-byte window, AutShadow 10-byte thunk, unseenrm E9 math); `DetourCore`/`AsDetourCore`
  trampolines fit their 32-byte slot; SATB load thunk 32 bytes fits.
- **Strings:** `Log`, `CrPut`, `hitchsum` use `_TRUNCATE`; `FbName`/`FbStackText` bounded by room; played.cpp keys
  length-checked (104/64), file capped at 256 KB; all 24 `GetPrivateProfileStringW` calls pass the wchar count.
- **ini:** clamped values that are shifted or multiplied all fit (largest: `2048<<20` as DWORD, `600000*1000`);
  every modulus divisor is clamped to >= 1; `Clamp` (`ts3native.cpp:264`) is int-only and never called with lo > hi
  except where lo is an already-clamped sibling.
- **Managed collections:** cap-clear, world-load clear or per-request clear everywhere else (GigWaitLeave,
  LoopSkip632 256, MemSuspects 2000, TripStage 256, interaction-keyed maps freed in Cleanup/finally, AFC cleared at
  world quit). `sLastShortcut` (`Fixes.cs:2811`) is a real leak but already listed in docs/321 (L15).

## 3. Fix lanes

| Lane | Items | What | Size | Default after fix |
|---|---|---|---|---|
| A. Path join | H1, H2, H3, L4, L5 | One shared `PathJoin(out, cap, dir, name)` on `_snwprintf_s(..., _TRUNCATE)` that returns false on overflow; replace every walker and SaveWrite `Join`; selftest with a 300-char name. Callers skip the entry (count it in a counter); SaveWrite skips the job so the game's own path runs | ~1 h build + selftest; one smoke run with a long-path file under `Mods\Packages` | Bug fix, no switch (rare-crash rule: repro + fix = ON) |
| B. CRT fail-soft, per call (Rodney 2026-11-22: option a) | all remaining `*_s` overruns (M1, L11, L12) | Every `*_s` format / copy call made fail-soft one by one (`SFmt` / `SCpy` / `SCat` / `WCpy` / `WCat`, §5). No module-wide `_set_invalid_parameter_handler`: it changes behaviour for the whole module and stays only a later safety-net idea | per-site | ON (no switch) |
| C. JobYield full table | M2 | `JypPark` reports "no slot"; `JyBusy` returns 0 then; counter for the case | ~30 min + correctness queue (`[RouteAsync]` A/B) | ON (it is ON today) |
| D. Managed leaks | M3, L7, L8, L19 | Key by ObjectId; clear in the world-quit resets | ~30 min, one build; quit-to-menu repro with a memory read | ON |
| E. JIT hard limits | L1, L2 | Bounds check before every `fix[]` push; emitted-size check against `need`; selftest of the largest templates | ~45 min + JIT selftest | ON |
| F. Small ON fixes | L3, L6, L9 | EqualsFast reject plan; fsro clear on world change; Apex `RegGetValueW` flags | ~45 min | ON |
| G. OFF / test cleanup | L10, L13-L18, T1-T4 | Clamps and `finally` restores; `g_utbGen = 1`; monotonic timer in hitch_watch | ~1 h, no runs needed beyond selftest | unchanged |

Order: A first (two ON crash paths and the kit's crash reporter), then C and D, then E/F, G last.

## 4. Limits of this audit

- Static only; no item was reproduced in game. H1 is CONFIRMED in code; how often players have 258+ char paths
  under `Mods` is not measured.
- Not walked line by line: `replay.cpp` (OFF), memdiag/allocsites sampler internals, `gcroots`, `arenatail`,
  `idcache`, `cellmemo`, `values`, `verify`, `memaccess` (all default 0, grep-scanned only); every MonoJit template's
  byte length against its budget (L2); `p4door.h`/`p4lotdump.h` (RouteSnap, OFF).
- Values that `native_set` changes at runtime were not checked against the clamps (only ini reads).
- Managed: EA's `RandomUtil.GetWeightedIndex` on all-zero weights (`Batch588Logic.cs:825`, ON) was not checked; if
  it returns -1 there, `list[pick]` throws. Needs a look at EA's original `TryOneAction` (decompile, local only).
</content>
</invoke>

## 5. Fix status (branch `bugfix/overflow-fixes-1122`, 2026-11-22)

Built and native selftest only; no game run yet (queue `overflow-fixes-correct-1122`, §5.1). All fixes stay ON with no new
switch: normal inputs give the same paths, texts and decisions as before, only the overflow case changes (fails soft).
Self-tests: `native/ts3native/overflow-test.h` (`TestOverflow*`), plus the JobYield host test.

| # | Lane | Status | What changed | Self-test |
|---|---|---|---|---|
| H1 | A | fixed | `PathFmt` (`ts3native.cpp`, after `Log`): `_vsnwprintf_s(..., _TRUNCATE)`, false and `out = ""` when the text does not fit (a cut path is never used), one log line for the first 4 (`path: longer than N chars, skipped`, no folder names), count `g_pathLong`. Every `swprintf_s` path build in `userdiag.cpp` (walkers + Saves / Mods / Bin lines) uses it; a long entry is skipped | 280+ char file and folder under a real temp tree (`\?\`-made): `UdScanPackages` counts the normal package only, `UdTreeBytes` no kill; PathFmt fit at 259 chars, fail at 260 and 300, equal text for normal input |
| H2 | A | fixed | `savewrite.cpp` `Join` and the `.backup` name on `PathFmt`: false = no job (`SaveJobPaths`) or a failed copy, so the game's own save path runs; never a cut path | `Join` / `.backup` of a 255-char src fail with `""`; `TreeHash` / `CopyTree` on the long tree return failure, no kill |
| H3 | A | fixed | `crashreport.cpp` (`CrPackages`, report / marker / xcpt paths, `CrFindDirs`' `wcscat_s`), `adaptdiag.cpp` (`AdHashTree`, `AdPath`, ScriptError / crash file finds), `modtime.cpp` (`MdtWalk` incl. `subRel`, `MdtScan`) on `PathFmt` | `CrPackages`, `AdHashTree`, `MdtWalk` (also with a 240-char `rel`) on the long tree |
| L4 | A | fixed | `apex.cpp` `UserSims3Dir` (`USERPROFILE` + `wcscat_s` -> `PathFmt`), new `ApexTomlPath` for both toml finds (was `wcscat_s`); `s3mm.cpp` probe on `PathFmt` | `S3mmProbe` from a 250-char Bin: 0, no kill |
| L5 | A | fixed | `ts3native.cpp` `Run`: ini and `.launcher.log` names on `PathFmt` (`""` = no log for that launcher start) | covered by the PathFmt cases |
| — | A | fixed (found here) | `played.cpp` `PlPath` / `.tmp` name: same `swprintf_s` pattern | PathFmt cases |
| M1 | B | fixed | `MonoFaultText` both branches on `SFmt` (keeps what fits); the handler's buffer 1500 -> 2048 and `Log`'s 1536 -> 2048, so the longest text (~1552) is no longer cut | fault text into a 48-byte buffer: cut, equal prefix, no kill |
| L11 | B | fixed | `castrim.cpp` `CtFrames` on `SFmt`: the `w < cap - 40` guard can now only cut text | `SFmt` "+=" chain over 30 items into 40 bytes stays inside |
| L12 | B | fixed | `crashfx.cpp` `CdCode` on `SFmt` | `CdCode` into 8 bytes: 7 chars, equal prefix |
| — | B | fixed (found here) | `adaptdiag.cpp` `AdOverlay` applied list: `swprintf_s` at `an` up to 2039 with ~100-char entries killed past 2048, and `applied[an]` could land on 2048: `_snwprintf_s(..., 2047 - an, _TRUNCATE)`, stop on -1 | by reading (ini overlay path; no host harness) |
| — | B | fixed (sweep) | every other `sprintf_s` (~250), `strcpy_s` (~107), `strcat_s` (8), `wcscpy_s` (35), `wcscat_s` (9) and the rest of `swprintf_s` in the module's non-test files (perl sweep, then compile + review): `SFmt` / `SCpy` / `SCat` truncate text, `WCpy` / `WCat` leave `""` (paths; `WCat` onto `""` stays `""`, so a failed copy never becomes a relative file in the game folder) and count in `g_pathLong`. Real overflow paths among them: the `ini` + suffix names of `[Diag] ModTime`, `[PcCheck]`, `[Diag] TaskSeam`, `[LotStream]`, `[Verify]`, P4 shadow, AutShadow jsonl, DxDiagCache sidecar, `saveslim` (install path near MAX_PATH). Not changed: `dxdiagvram.h` (also built into `dxdiag-vram.exe` without the helpers; its formats are bounded: 64 + 1 + 64 < 160), test files | `TestOverflowCalls`: equal text and return for normal input; cut / `""` past the end |
| M2 | C | fixed | `JypPark` returns `JYP_FULL` (-1) when no slot is free (nothing taken, the slots stay); `JyBusy` then answers 0: the task does not sleep and its plan runs now with its own records, so no other route takes its deadline class or scope. Counted as before (`p.full` in `jyLostN`), no new counter. Normal case (a free slot) unchanged | `TestJobYield`: 8 live records + a fresh route while the job runs = not busy, `full` +1, `jyLostN` +1, 8 records kept; control with one free slot = busy. Host test `jobyield-host-test.cpp` expects `JYP_FULL` (Linux-only build, not run here) |
| M3 | D | fixed | `AutLive.cs` `sMetaStart` keyed by the actor's ObjectId (`EnqKey`, as `sEnq`), so no `Autonomy` object is held; cleared at world quit (`WorldQuitLeaks.cs` `LeaksWorldQuit`, called from `HeldRefsWorldQuit`). The 256 cap-clear stays. Searches of an actor-less `Autonomy` are not timed (key 0; before: timed) | ts3fixes build (no offline harness for game types); in-game: quit-to-menu job in the correctness queue |
| L7 | D | fixed | `ErrorTrapLoop.cs` `sErrLoop` cleared at world quit | as M3 |
| L8 | D | fixed (clear only) | `EpBatch4bLogic.cs` `sJgPending` cleared at world quit; dropping destroyed Sims inside `JgPurge` not done (bounded by the 3-minute alarm) | as M3 |
| L19 | D | fixed | `EnvListExact.cs` `sExLotOff` cleared at world quit (hard-off switch, no effect today) | as M3 |
| L1 | E | fixed | `monojit.cpp` `JitCompile`: before each position, `nf >= JIT_MAX_POS + 64 - 1` refuses the compile (`branch fix table full`, the method stays on EA's interpreter, nothing published) | 4 switches x 999 cases + 100 `br.s` (old code: 4096 pushes into a 4064 table) refused, nothing published; control with 60 `br.s` (4056) compiles |
| L2 | E | fixed | before each position, less than `kJitPosRoom` (256) bytes left of the method's reserve refuses the compile (`code budget passed`); a switch whose 4-byte-a-case table does not fit as well exits to EA. The largest position the JIT selftests compile is 103 bytes [measured], so normal methods never hit it (the reserve is 256 a position + 2 a word + 512) | `g_jitPosMaxBytes` read after the JIT tests: 103 < 256; every existing JIT compile test unchanged |
| L3 | F | fixed | `equalsfast.cpp` `EqLearn`: a class whose plan finds no slot in its probe window is remembered in a 256-entry direct-mapped `g_eqNoRoom` (checked before a pool slot is taken) and its pool slot goes back when no other thread took one after it; results stay EA's walk for that class as before | probe window filled with foreign plans: 50 calls take no pool slot and build the plan once; room again = learned and hit |
| L6 | F | fixed | `fsro.cpp`: unsigned age (age -1 of the same tick step still young, older than `MaxAgeMs` stale, also past 24.8 days); the cache is cleared when the stub sees another world object [inferred: a new world gives a new world object; if the game reuses one object, old entries still expire after `MaxAgeMs` as before] | `FsYoung` at -1 / 60000 / 60001 / 2^31+ ms; a second world object misses and the first is cleared again |
| L9 | F | refuted, not changed | measured: `RegGetValueW(..., RRF_RT_REG_SZ \| RRF_RT_REG_EXPAND_SZ, ...)` without `RRF_NOEXPAND` succeeds on Windows 11 and returns the expanded folder, so `UserSims3Dir` already reads a moved Documents. Kept as is; a comment and a test say so | `TestOverflowSmall`: the old flags return 0, `UserSims3Dir` equals the expanded shell folder + `\Electronic Arts\The Sims 3` |

### 5.1 Builds, queue, open items

- Rule review (2026-11-22, "fix first"): `PathFmt` also fails for a `"%s\..."` format whose first directory is `""` (with `g_crDir` `""` the crash report and `running.txt` landed in the drive root); `CrOpenReport` refuses, `CrMarkerPath` gives `""` and its four callers skip it; `played.cpp` `PlPathIn` refuses an empty base; `apex.cpp` gives `""` without USERPROFILE; a failed `WCpy` in `autshadow-run.cpp` / `dxdiagcache.cpp` empties the whole path; `FlushTree` logs and counts a child that does not fit. Selftest: no `crash-*.txt`, `running.txt` or `ts3native` folder in the drive root.
- Builds (docs/data/builds.json): `ts3native.asi.overflow-fixes-1122b` (`game-copy/ts3native.asi.ovf1122b`, sha256 `8e8b3eb2a36e6436a3124672ae651533b63f5856a6570ca01731fcea230734d4`, ts3native 0.36.299-ovf1122b, the queue's build; replaces `ts3native.asi.overflow-fixes-1122`, 0.36.299-ovf1122, sha256 `8acbafa4...`), `ts3fixes.package.overflow-fixes-1122` (`game-copy/ts3fixes.package.ovf1122`, sha256 `4a7b0b02b5c2ac3b46e95c692320305d1a65c344cf9ef3152736948695b2ad90`); ini = m1122 (no ini change).
- Queue `tools/harness/queues/overflow-fixes-correct-1122.json` (correctness, Bin state apex, dry-run ok, NOT RUN, ~65 min): `bp-ovf-smoke` (no path line on a normal install), `bp-ovf-savereload` (save + main menu + load: SaveWrite and the lane D world-quit clears), `bp-ovf-longpath` (ccSet `game-copy/cc-synth/longpath-1122`: one package under a 215-char name, full path > MAX_PATH, with the kit's walkers on: must start and log `path: longer than`), `ip-ovf-jobyield` (repro `ip-jobyield-strand-storm`, JobYield=1 read back, sim-waits summary with asks and busy above 0). ts3fixes: `fixesPackage` `game-copy/ts3fixes.package.ovf1122` on the three profile jobs; `bp-ovf-savereload` (`fixesPackage` is refused on saveReload) runs the `build/ts3fixes.package` that `cycle.ts --restart` rebuilds from the worktree (same lane D source, default tuning). Run 1 (21:26) refused every job before the game started: a `fixesBuild` pin of the tagged build (tuning cb38ad21) never matches that rebuild (c7b6325019fc). Chain `probe-out/queue/chain-overflow-fixes-1122.sh` (not committed) after the caps chain. No timing queue: these are crash / correctness fixes; the only hot-path change is the per-position JIT check (two compares at compile time, not at run time).
- Open: not run in game; the Linux-only `jobyield-host-test.cpp` not run; L6 world-object clear rests on I-581-2; L8's `JgPurge` drop of destroyed Sims not done; lane B left `dxdiagvram.h` (bounded) and test files on the CRT calls; narrow paths go through `SFmt` / `SCpy`, which cut instead of emptying: only `worldimage.cpp` (OFF) builds narrow paths; the sweep moved its calls to `SFmt` / `SCpy`, and its buffers have room past MAX_PATH (+16 / +32 / +64 over MAX_PATH inputs), so none of them is ever cut. Lane G and the 4 tables of `feature/unbounded-caps-1122` untouched (lane B changed only *_s call lines in `plandeadline.cpp` 461-463 / 619 / 707 and `autshadow-run.cpp` 929 / 931).
