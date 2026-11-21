# 314. Tester kit A/B mode (kits 1121.k4 and 1121.e7b)

2026-11-21, branch `feature/kit-ab-1121` (from master 14aed570a). Two tester kits that alternate an arm A and an arm B per
game session, so a player compares our change against the game's own code in normal play.

## 1. What switches, and when

- `payload\ab\arms.json` names the two arms: `label` (the notice text after "TS3 test kit: arm X - "), and either a
  `package` (a ts3fixes.package in the payload, with its `packageSha`) or `off: true`.
- One session = one game start through menu option 6 (`TS3Kit.bat -Mode Play -GameDir ...`). Before the game starts the
  kit puts the arm's files in place and reads them back by sha256; nothing changes while the game runs. It then runs the
  normal capture (option 2: the player's own launch route, kept in the state after the first session), waits for the
  game to close and writes the arm's summary. The state file `ab\state.json` in the kit folder holds the next arm (first
  session = A, then B, A, ...), the sessions and the launch route; `-Arm A|B` (menu 7) picks the next arm by hand.
  A session without a game process does not flip the arm.
- k4 (Mihai, retail 1.67): both arms are ts3fixes packages; they differ only in `kCommodMapFast` (A True, B False) and
  the kit notice tunings. asi / inis are kit k3's (0.36.298-mkit2, k2normal / k2diag).
- e7b (Eli, EA app 1.69): arm A = our mod as kit e7 (e6 asi 0.36.297-eaooa1 + e6 inis) with a ts3fixes package that has
  `kSwapInvokeDiag` ON (the CAS detector, docs/312) + the kit tunings; arm B = off: the kit moves `Game\Bin\ts3native.asi`
  and `Mods\Packages\ts3fixes.package` into `ab\off` (copy, sha check, then delete), so the game loads neither; the next
  arm A puts the asi back byte for byte and writes the arm A package. Turbo.asi stays moved aside in both arms (the
  install's backup, kit e7). ts3native.ini stays in Bin in arm B (without the asi nothing reads it).
- Uninstall (option 4) first puts a moved asi back and clears `ab\off`, then restores the install backup as before; an
  arm package counts as the install's own (a target that did not exist before is removed when its sha is either).

## 2. The notice and the per-session numbers (ts3fixes, KitArm.cs)

- New tunings, default empty = off (nothing runs): `kKitArm` (arm letter), `kKitArmNotice` (text), `kKitArmSwitches`
  (switch names read back, space separated). Only the kit packages set them (`build-fixes.ts --on kKitArm=A,...`).
- World load: a `StyledNotification` (kSystemMessage) with the notice and one log line
  `ts3fixes: kit: arm A: <notice> | read-back commodmapfast=on commodmapshadow=off` (ShortcutOn of each name).
- A sim-thread `Task` loops `Simulator.Sleep(0)` and gives `KitStats` (KitArmCore.cs, no game types, offline self-check
  `tools/selftest/kitarm`) the real time between two wakes (Environment.TickCount): hitches >= 250 ms and >= 1 s, the
  longest, and sim updates per real second (median, lowest; seconds close on the real clock, so a 3 s hitch leaves
  seconds with 0 updates). A gap that touches a running save (`OptionsModel.SaveGameInProgress` at either wake) is counted
  apart as a save pause. One `kit: stats arm X (minute|world quit): ...` line a minute and at world quit (the shared
  400-line per-prefix cap of [UserDiag] = 6.5 h of play).
- The task wakes once per sim update also while the game is paused [see §4: measured]; a pause is no hitch.
- Arm B of e7b has no ts3fixes: its summary has minutes and script errors only, and checks that `ts3native.log` was not
  written during the session (proof the mod was off). The arm notice is the kit window line (and `ab\kit.log`); no
  always-on notice package (simplest: it would be another script mod in the "off" arm).
- Household: read-only measurement, no Sim, save, switch or setting is touched; nothing is stored in the save.

## 3. The summary (kit side, after the game closed)

`ab\results\<start>-arm<X>.txt` and the session zip's `== A/B session` section: the notice, the files read back before
the start, session minutes (kit start to game close), the world-load notice and read-back from ts3native.log (a
different arm letter is flagged), the last stats line parsed (in-world minutes, sim updates/s median / lowest, hitches
>= 250 ms / >= 1 s, longest pause, save pauses, read-back), Apex fps when the Apex log has `[Screen] ... N fps` lines
(Apex 4.0.x logs none: `[Screen]` lines carry window / limit settings only [verified: screen_control.cpp]), and the
`ScriptError*.xml` files written during the session. Every diagnose / capture zip carries all summaries, the state and
`ab\kit.log` (masked like the rest).

## 4. Checks

- Offline: `TS3Kit.ps1 -Mode SelfTest` (regex cases for the 6 new regexes), `test-kit.ps1` A/B section (Play refused
  before the install; sessions A / B / forced B / A with a stub game that writes the kit lines for the package it finds and
  no log without the asi; the summaries, state, zip contents, no user name in the zips; uninstall after arm B), both in
  PowerShell 7 and Windows PowerShell 5.1; `tools/selftest/kitarm`; `node tools/player/build-kit-ab.ts --self-check`.
- In game (Steam 1.67, Apex 4.0.13 + bridge): queue `kit-ab-smoke-1121` (k4 arm A with a 20 s pause and a save, k4 arm B,
  e7b arm A package on the Steam twin of the e6 asi, e7b arm B = ourMods false). EA 1.69 itself: queue
  `kit-ab-smoke-ea-1121`, EA laptop only, PREPARED, NOT RUN.
- Results: see §5.

## 5. Results

Kit-ab-1121 lane, 2026-11-21, Steam 1.67 on Apex latest + RTX Remix bridge (Bin state `apex`).

**Game smoke (queue `kit-ab-smoke-1121`, all 4 PASS):**

| Job | Arm | Notice + read-back line | Stats (last minute line) |
|---|---|---|---|
| k4-armA | A, commodmapfast ON | `kit: arm A: TS3 test kit: arm A - commodmapfast ON (our faster object-list update) \| read-back commodmapfast=on commodmapshadow=off` | 4.5 min, hitches >= 250 ms 3, >= 1 s 2, longest 3750 ms, save pauses 1 (18797 ms) |
| k4-armB | B, commodmapfast OFF | `... arm B - commodmapfast OFF (game's own code) \| read-back commodmapfast=off commodmapshadow=off` | 3.0 min, 3 / 1, longest 2921 ms |
| e7b-armA-steam | A, our mod ON (Steam asi m1120t stand-in) | arm A line, `read-back swapinvokediag=on` | 3.0 min, 3 / 2, longest 2937 ms |
| e7b-armB-off | B, our mod off (harness vanilla arm) | none (by design) | not measured in this arm |

- The 20 s pause in k4-armA added no hitch (I-569-1 verified) and the save landed in "save pauses" only (I-569-2 verified).
- All three hitches per arm come in the first seconds after world load; the kit summary shows them as they are (no warm-up cut).
- On-screen notice: `StyledNotification.Show` returned without an exception in every arm-A/B run (no `kit: notice failed` line), but the
  harness clears the notification panel right after world load (`quiet_ui`), so the screenshot cannot show it. Visual check open.
- No `world quit` stats line: the harness closes the game without quitting to the menu; the kit summary reads the last minute line.
- e7b on EA 1.69: not runnable here (no EA install). Queue `kit-ab-smoke-ea-1121.json` is ready for the EA laptop; the EA asi and the
  kit's arm-B disable path were checked offline only (test-kit stub).

**Kit side:** `TS3Kit.ps1 -Mode SelfTest` 0 FAIL in PowerShell 7 and Windows PowerShell 5.1; `test-kit.ps1` on both kits in both shells
(A/B sessions A, B, forced B, A; state ABBA; zips; uninstall puts the asi back from `ab\off` and resets the next arm to A).
Fixed on the way: uninstall did not reset the next arm on a kit without an off arm (k4).

**commodmap-fast-1121 correctness:** job 1 (Baseline-IP, rerun2) PASS, EXACT on every arm, 1505 = 0. Job 2 (many-mods-s):
rerun3 VOID (ts3agent did not answer within 300 s at the menu after the script-cache clear; no world load); rerun4 with
`TS3_AGENT_WAIT_S=900` (new cycle.ts knob, default 300) FAIL the same way after 900 s: the script layer never loaded (0
`ts3fixes:` lines, the game alive with JIT and task-seam lines). Not a commodmap result (the hook lives in ts3fixes, which never
ran); triage (bugfix/manymods-menu-hang-1121): the same set hangs before the main menu with ourMods false too, and without the XCAS core mod too, so the hang is in the many-mods-s set, not ours [verified]; the package is not named yet (bisect of the set needs an OK). Job 2 needs a mod set that reaches the world. By the rule "fast only when both jobs pass", Mihai's kit goes out as
**k4s** (arm A = commodmapshadow check, `ts3fixes.package.kit4s-1121`); **k4** (arm A = commodmapfast) is built and staged.
Whether Mihai's k3 log shows the shadow check exact on his save is not checked in this lane; k4s re-runs that check per session.

