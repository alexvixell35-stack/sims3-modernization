# 234. EA 1.69 product/entitlement gate: live diagnosis (BaseGame-only flags)

> **Verdict (2026-11-02):** BaseGame-only loads on the EA laptop came from our launcher swap skipping the real launcher's write of
> `selectedflags` to `LoadOption.ini` (section "LoadOption.ini selectedflags test"), not from timing or the registry.
> Fix: `ts3probe.py` `ensure_selected_flags` on EA launches (commit 9890067); sections between are the investigation trail.

EA laptop, EA app 1.69.47 (`Sims3Launcher.exe` = TS3.exe, base 0x400000, no ASLR), 2026-11-02 13:09-13:20 PHT. Question: do the
19 installed packs stay unmounted because the entitlement answer arrives after the gate (timing), or because the game does not
match what the EA app answers (data)? Static context: mount at 0x596A70, gate 0x596990 -> service create + wait 0xA42A30, loaded test
0xA42080, fields `[0x1244FB0]+0x28` (answered) / `+0x24`, flags dwords `[0x1227E50]`, `[0x1227E54]`. Player thread:
https://forums.ea.com/discussions/the-sims-3-technical-issues-en/all-19-owned-packs-installed-but-game-loads-base-game-only-sunset-valley/13626580

## Runs (times PHT; t = seconds after the game process appeared)
| Run | Method | Result |
|---|---|---|
| A 13:09:36 | Frida attach 0.5 s after start, `tools/probe/ep-gate-probe.js` (hooks + 100 ms reads) | .text decrypted at t=0.6; service pointer, flags stayed 0 for 60 s; no LSX traffic in the EA app log; game exited by itself at t=63 s, EA app: `Game process exit code=[-1]`, `Game session was too short: [63] seconds`. [verified] Early Frida attach is not usable on the EA build (cause inferred: DRM). |
| B 13:15:17 | OpenProcess + ReadProcessMemory, 100 ms, no injection | t=1.507 service created (`+0x28`=0); reader bug; re-read at t=49: `+0x28`=1, `+0x24`=0, `[0x1227E54]`=0x1, `[0x1227E50]`=0x1. |
| C 13:18:46 | same, from process start, 75 s | t=0: all 0. t=1.508: service created, `+0x28`=0, `+0x24`=0x45F0C48 (pointer-like, not 0). t=2.011: `+0x28`=1, `+0x24`=0, `[0x1227E54]`=0x1, `[0x1227E50]`=0x1, all in the same 100 ms tick. Unchanged until the kill at t=75. |

EA app log (`EADesktopVerbose.log`, run C, UTC): `Launching [...Sims3Launcher.exe]` 05:18:48.577, LSX `ChallengeResponse` 05:18:50.096
(t≈1.5), `QueryEntitlements` request 05:18:50.112, `QueryEntitlementsResponse` 05:18:50.513 (t≈1.9; GraphQL `sdkEntitlements` 401 ms).
Run of 13:02 (other agent, loaded a lot): same shape, request 05:02:02.331, response 05:02:02.733 (399 ms).

## Verdict
- (a) Flags after init = **0x1 (BaseGame only)** in both read runs: the player problem is reproduced on this laptop. [verified]
- (b) The answer arrives ~0.4-0.5 s after the service is created, inside the 3000 ms wait; the flags are written in the same 100 ms tick
  as `+0x28` turns 1. The gate waited for the answer and then mounted only the base game. [verified by 100 ms sampling]
- (c) Skip-wait branch (`jne` 0xA42A38): not taken. Live: `[0x1244FB0]` was 0 until the gate ran (t=1.5). Static: 0xA42A30 has three
  callers (0x596994 = gate, 0x59498A, 0x594D38) and the parent 0x597E0F calls the gate (0x597E1B) before the other two (0x597E28, 0x597E2F). [verified]
- (d) **Not a timing problem; no "always wait" patch**: it would change nothing. The mismatch is in what the game compares against the
  answer (data), so more read-only RE is needed, not a runtime patch.

## Data found for the next step
- Answer: 55 entitlements (filter `ORIGIN_DOWNLOAD`), 26 distinct `ItemId`s. 21 exist as strings in the game image (offer-id string
  table 0x1021F90-0x10227xx, e.g. `OFB-EAST:55107`, `DR:156751300`, `Origin.OFR.50.0000058`); 5 do not: `OFB-EAST:46851`,
  `Origin.OFR.50.0001139` (22 of the 55 entries), `.0002168`, `.0003439`, `.0004172`. [verified]
- The gate's product loop (0x5969C2-0x596A54) compares each pending product's id `[edi+8]` with 12-byte entries at 0x11A7B40 and with the
  list `[0x1227E48]`, then calls 0xA421F0 (entitlement lookup by name) and mounts via 0x596810 only on a hit. Which name each pack expects
  and what 0xA421F0 matches is the open point. [inferred from the disassembly, not traced]
- `0x1227E54` has no code reference in the image; `0x1227E50` has ~140 and read 0x1 live, so the "registry mask at `[0x1227E50]+0x10`"
  attribution is wrong (the value is not a pointer). Which of the two is GetProductFlags is unverified; both say BaseGame only.
- Registry `HKLM\SOFTWARE\WOW6432Node\Sims`: all 19 pack keys exist with only `Install Dir`; the base key also has `DisplayName`,
  `Locale`, `Product GUID`, `ProductID`, `SKU`. Compare with the Steam machine before assuming the game needs the extra values. [verified]

## Next
1. Read-only trace of one pack through the loop (late attach after the menu is safe for Frida, docs/230 dump-image pattern): log the
   id `[edi+8]`, the matched table entry and the 0xA421F0 argument/result; compare with the 26 answered `ItemId`s.
2. Diff the pack registry values with the Steam machine; if the EA app install lacks values the loop reads, that is an EA install bug:
   report it in the forum thread with this timeline as evidence.
Scripts: `tools/probe/ep-gate-probe.js` (Frida, late attach only); the ReadProcessMemory reader lived in the scratchpad (not kept).

## Diagnostic run 2026-11-02 (EaDiag hooks)
13:44:24 PHT, `ts3native-ea169.asi` 2ac69a35 (CI 27dea5d) with `[EaDiag] ProductGate=1`, probe launch, no save loaded, log
`probe-out/ea-eadiag-1102.ts3native.log` (gitignored). EA app LSX: `QueryEntitlements` 05:44:25.051Z, response 05:44:25.488Z
(t≈2.4 s, 55 entitlements, 26 distinct `ItemId`, as before; fields `Type ItemId EntitlementId EntitlementTag Group ResourceId ...`,
`Group=""`, `EntitlementTag="ORIGIN_DOWNLOAD"`: the only pack-identifying field is `ItemId`).
- `OK   EaDiag: ProductGate log-only hooks, 5/5 installed` but **the three gate hooks never fired** (no `after wait`, no product
  line, no mount line): the gate 0x596990 was not called. 296 lookups, all from 0x5949BC/0x5949F9 (the follow-up scan 0x594980,
  which tries every row of the offer table 0x11A7B40 and ORs the row's product bit into this+0xC) plus one `"TESTID"`.
- `eadiag: after gate: flags [0x1227E50] 0x00000000 [0x1227E54] 0x00000000; service ... +0x28 1 +0x24 0x00000000; 296 lookups, 0 products`
  (the flags get their 0x1 later than 0x597E34; runs B/C read them at 100 ms granularity).
- Lookups: 273 distinct offer ids tried, 21 found (`-> 1`), none `-> 2`: the answer was loaded in time. Found per product id of the
  12-byte table (pid, bit, folder name from 0x594780, matched `ItemId`): 1 base `OFB-EAST:55107` · 4 EP01 `DR:137311600` ·
  5 SP01 `DR:156751300` · 6 EP02 `DR:169598400` · 7 SP02 `DR:191313700` · 8 EP03 `DR:194011300`+`Origin.OFR.50.0001227` ·
  9 SP03 `DR:215625800` · 10 EP04 `DR:219720800` · 11 SP04 `DR:227821700` · 12 EP05 `OFB-EAST:60968` · 13 SP05 `DR:231553700` ·
  14 EP06 `OFB-EAST:60970` · 15 SP06 none (folder absent, not owned) · 16 SP07 `OFB-EAST:43769` · 17 EP07 `OFB-EAST:49019` ·
  18 EP08 `OFB-EAST:60964` · 19 SP08 `OFB-EAST:60962` · 20 EP09 `OFB-EAST:109545788` · 21 EP10 `Origin.OFR.50.0000058` ·
  22 SP09 `Origin.OFR.50.0000124` · 23 EP11 `OFB-EAST:64320`. Every installed pack has an answered id in the game's own table, so
  the bundle id `Origin.OFR.50.0001139` (22 entries) is irrelevant: the game compares `ItemId` and would have matched. [verified]

### Verdict: the packs never reach the gate (not pending); the entitlement data is complete
Static trace of the gate's caller (0x5979xx-0x597E34, image `probe-out/ts3-ea169.bin`) [inferred from the disassembly, not traced live]:
1. Per product row of 0x11A7A38 `{seq, pid, pid+1}` the loop calls 0x7D1820(seq): `RegOpenKeyExW(HKLM, "Software\Sims")`, enumerates
   the subkeys, reads each key's `ProductID` (1001 = base, else must equal `seq`: EP01=2, SP01=3, EP02=4 ... EP11=21) and only then its
   `Install Dir`. The EA app wrote the 19 pack keys with **`Install Dir` only** (section above, re-checked today), so no pack is found.
2. Fallback: `GetFileAttributesW(<found dir> + "/" + "EP01")` must be a directory, then `<dir>/GameData/Shared/Packages/FullBuild_pNN.package`
   (table 0x11A8940) must exist. The found dir is the empty string when 0x7D1820 failed, so the path is `/EP01` = drive root: not a
   directory, product skipped (0x597C1A -> 0x597DB5). The folders `C:\Program Files\EA Games\The Sims 3\EP01..EP11, SP01..SP09` and their
   FullBuild packages exist, the game never looks there.
3. Only the base (`ProductID` 1001 present) is found and mounted directly (0x597D84 -> 0x596810, no entitlement check). The pending list
   this+0x4BC stays empty, `0x597E0F: cmp [eax],eax; je 0x597E20` skips the gate, the follow-up scan 0x594980 still builds the full
   entitlement mask (20 bits) but nothing uses it for mounting. Flags end as 0x1 = BaseGame only.
Not verified live: the `/EP01` path string and the mask check `(bit & this+8) == bit` at 0x597D0F (both need one more log-only run with hooks
at 0x597B33, 0x597C18, 0x597CA1, 0x597D14). The registry diff with the Steam machine (expected: pack keys with `ProductID`) is still open.

### EA-app-side fix options (no code patch of the check, project policy)
- Repair the game in the EA app (Manage > Repair) and re-add the packs in Manage > Add-ons: the installer should write `ProductID` (plus the
  `DisplayName`/`Locale`/`Product GUID`/`SKU` the base key has) into every `HKLM\SOFTWARE\WOW6432Node\Sims\The Sims 3 <Pack>` key.
- Re-login / entitlement refresh is **not** the fix: the answer already carries every installed pack.
- Forum post to EA: 1.69.47 EA-app installs of the packs write only `Install Dir` to the pack registry keys; the game identifies an
  installed pack by the key's `ProductID` value (1001 base, 2..21 packs) and otherwise looks for `/EP01` on the drive root, so all 19 packs
  are treated as not installed although the entitlements are granted; ask for the installer to write `ProductID` per pack (give the
  registry dump and this timeline; log lines above as evidence).

## ProductID registry theory (superseded)
Static read of the registry probe `0x7D1820` (called from the caller loop 0x597AC6-0x597DE6 at 0x597B28): it enumerates the subkeys of
`HKLM\Software\Sims` (WOW64 view), reads the `ProductID` dword of each (1001 maps to 1, values <= 0 skipped) and accepts a key only when
`ProductID == seq`, the row's first field in the table at 0x11A7A38 (base 1, EP01 2, SP01 3 ... EP11 21); key names are not compared. [verified in the disassembly]
Measured on the EA laptop: the base key has `ProductID` 1001, all 19 pack keys carry `Install Dir` only. This theory turned out irrelevant:
the real blocker was the launcher's `selectedflags` in `LoadOption.ini` (next sections), and the EA app Repair does not write `ProductID` either.
No registry edit is needed or recommended; the per-pack script and the forum request to EA for `ProductID` writes are dropped.

## EA app Repair attempt 2026-11-02
14:14-14:17 PHT, EA laptop, screen locked, triggered remotely. [verified]
- No repair URI or command line exists: `EADesktop.exe` strings hold only `origin2://game/launch/`, `library/open/`, `store/open/`,
  `store/checkout` and link2ea `LaunchGame/`; `origin2://game/repair/?offerIds=OFB-EAST:55107` and `game/verify/` are logged by
  `EALaunchHelper` as `ProtocolParser::parseProtocolOrigin2 Unsupported origin2 protocol command=[game/repair/]` (no action). The
  `RepairGame` LSX message (game SDK), `app.repairOfferId` cfg key (`DEBUG_REPAIR_GAME_VIA_CFG`, "configurable[false]" build) and the
  `Link2EA.exe` repair strings are not reachable from outside. EA's own help: Repair is a Library tile menu item only
  (https://forums.ea.com/kb/ea-app-tutorials-and-guides-en/infoguide-how-to-repair-a-game-on-the-ea-app/11894905).
- Working remote method: the EA app is a CEF app and accepts Chromium's `--remote-debugging-port`. Restart
  `EADesktop.exe --remote-debugging-port=9222`, then over CDP (`http://127.0.0.1:9222/json`, `Runtime.evaluate` on the `https://pc.ea.com`
  page): open the Library, click the Sims 3 sidebar tile (`/en/games/the-sims-3`), the `MANAGE` button (aria-label `More options`), the
  `Repair` menu item. No confirmation dialog; log `GamesManagerProxy::initiateRepair Initiate repair for offerId=[OFB-EAST:55107]`
  at 06:14:14Z, "Repairing 2% .. 86%", done 06:16:32Z (~2.5 min: base + all 19 DLC `__Installer\...\InstallLog.txt` rerun, each logs
  `Setting up registry ...` then exit code 0). Restart the EA app without the flag afterwards (the port is open to every local process).
- Registry: `reg export HKLM\SOFTWARE\WOW6432Node\Sims` before and after are identical (151 lines). Base key: `DisplayName`, `Locale`,
  `Product GUID`, `Install Dir`, `ProductID`=1001, `SKU`, `InstallStart`; all 19 pack keys: `Install Dir` only, before and after. The DLC
  `installerdata.xml` files name only `...\DLCs\<pack>\UninstallerPath`; the base one only `Install Dir` and the Uninstall key.
- Bin: Repair rewrote `Sims3Launcher.exe` back to the original (sha 845199D3ED66, undoing the docs/230 swap); `wininet.dll`,
  `d3d9.dll`, `dxvk.conf`, `ts3native.asi`/`.ini` untouched (shas unchanged). Swap redone (`TS3.exe` over `Sims3Launcher.exe`, sha 7352DD6E599F).
- **Verdict: Repair does not add `ProductID` to the pack keys.** The player FAQ step "repair the game" cannot fix the BaseGame-only
  load; the per-key `ProductID` dword (registry probe section above) or an EA installer fix remains the only route. Re-adding the packs in
  Manage > Add-ons was not tried (needs the same CDP route; expected to rerun the same DLC installer). [inferred for Add-ons]

## LoadOption.ini selectedflags test 2026-11-02
14:54 PHT, EA laptop, screen locked, swap kept (`Sims3Launcher.exe` = TS3.exe sha 7352DD6E599F, SKIP-all asi pair c035abe5a3a6 / 8aa37522782a). [verified]
- Static (launcher + `TSLHost.dll` + image `probe-out/ts3-ea169.bin`): the real launcher is a thin .NET WinForms shell (manifest
  `asInvoker`, no elevation, no `ProductID` write anywhere); the pack logic is native in `TSLHost.dll` (`S3L_gc_SetSelectedProductFlags`,
  `S3L_gc_GetEntitledProducts`, `S3L_wp_GetInstalledProducts`, `S3L_g_launchGame` -> `origin://LaunchGameRtP/%s?CommandParams=%s` with
  `Bin\TS3.rtp`). TSLHost `0x10022510` = `SetSelectedFlags(flags, save)`: stores this+8 and writes `selectedflags` to
  `Documents\Electronic Arts\The Sims 3\LoadOption.ini` (`[productoption] playedflags / selectedflags`). The game reads it in the
  product-gate constructor `0x596A70` (this = 0x1227E50): with `-editInGameMode` (string 0x107A880, checked at 0xECB8FA) the mask
  `[this+8]` = 0xFFFFFFFF, otherwise `[this+8]` = `selectedflags` (0x596AC3-0x596AE6, string table 0x11A8930). The mount loop tests
  `and ecx,[esi+8]; cmp ecx,eax; jne skip` at `0x597D0F` (skipped only when byte `[this+0x14]` != 0), so a pack mounts only if its bit is
  in `selectedflags`. The game never writes that key (the only xref to 0x11A8934 is the read at 0x596AC5); it only ORs `playedflags`
  (0x598BC6-0x598C2B). The launcher's record of its one real run (2026-11-01 20:52): `HKCU\Software\Electronic Arts\Sims 3\Launcher\Settings`
  `LastRunIProducts = LastRunEProducts = 2093055 = 0x1FEFFF` (installed and entitled = base + 19 packs), while `LoadOption.ini` on the laptop
  had `selectedflags = 00000000` (mtime 11-02 11:21:36; who zeroed it is unverified).
- Live: `LoadOption.ini` backed up as `LoadOption.ini.bak-2026-11-02`, `selectedflags 00000000 -> 001fefff` (= LastRunIProducts &
  LastRunEProducts, written by the new harness step below at 14:54:30), harness launch `ts3probe.py serve --launch` (`TS3_LAUNCH=ea`), game
  process `Sims3Launcher.exe` pid 36032, parent EADesktop.exe, command line `-test`, no injection: ReadProcessMemory reader at 100 ms
  (`probe-out/ea/selectedflags-flags-1102.txt`): t=0.000 `[0x1227E54]` 0x0, `[0x1227E58]` (selected mask) 0x1FEFFF, `[0x1227E60]` 0x1;
  t=0.201 service answered, **`[0x1227E54]` = 0x1FEFFF**, stays so to t=60; `[0x1227E60]` stays 0x1 (so this+0x10 is not a per-pack found
  mask, or only the registry-found base lands there); window title "The Sims™ 3 Into the Future" (EP11 mounted); `LoadOption.ini` not
  rewritten by the game (mtime unchanged, `selectedflags = 001fefff`); no mod-version popup. Kill + 150 s relaunch watch, Bin pair unchanged.
- **Verdict: the swap is the cause.** Our swap (`TS3.exe` over `Sims3Launcher.exe`) skipped the real launcher, whose Play press writes the pack
  selection to `LoadOption.ini`; with `selectedflags = 0` the mask check at 0x597D0F drops every pack, so the game loads BaseGame only
  (`GetProductFlags` 0x1). With `selectedflags = 001fefff` all 19 packs mount **without any registry change**: the pack keys still have
  `Install Dir` only, so the `ProductID` registry theory (sections above) is not needed for the EA-app install; the game finds the packs
  through another path once they are selected (not traced). Fix = set `selectedflags` before an EA launch: `tools/probe/ts3probe.py`
  `ensure_selected_flags` (EA launch path) derives the value from `LastRunIProducts & LastRunEProducts` (8-digit lower-case hex as the file
  has it), writes only when different, backs the file up once as `LoadOption.ini.bak-before-ea-launch`, `TS3_SELECTED_FLAGS` overrides, and
  warns (file left alone) when the launcher keys are missing: run the real launcher once. Players going through the launcher never hit this;
  it only affects launcher-less starts (our swap, or any player tool that starts TS3.exe directly). `probe-out/ea/ea-pack-registry.ps1` is
  not needed; the forum-post draft above (installer writes no `ProductID`) stays a fact but is not the cause of BaseGame-only loads.
