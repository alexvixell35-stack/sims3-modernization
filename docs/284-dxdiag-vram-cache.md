# 284. DxDiag VRAM cache at start-up (`[Startup] DxDiagCache`)

2026-11-17, branch `feature/dxdiag-skip-1117`, ts3native 0.36.270-ddc1. Built + selftest, NOT RUN. Ledger §504. Counters v207.
Follows docs/270 §39 (who holds the COM proxy that blocks rewind snapshots) and docs/37 C4 (skip DxDiag at start-up).

## 1. Problem

At every launch `DeviceCaps_Probe 0x613CB0` calls `VRAM_DxDiag 0x612640` (one caller, `call` at `0x613EA6`) [verified: a byte
scan of `.text` finds no other call, jmp or pointer to it]. `IDxDiagProvider::Initialize` took 60 s on the test laptop in a probe
with the game's exact calls [measured, docs/270 §39], and its WMI work keeps a combase ping-loop worker busy ~5.3 min after the
release, which makes rewind snapshots wait or refuse. The answer is a number the game stores at DeviceConfigManager +0xC4
(`mov [ebx+0xC4], eax` at `0x613EAE`); `[Verify]` reads it back as `vramMB`.

## 2. What VRAM_DxDiag does [verified, Steam 1.67 disassembly]

cdecl, one argument (the `D3DADAPTER_IDENTIFIER9*` of `DeviceCaps_Probe`, read at `[esp+0x2BC]` after the frame), returns MB:

1. `CoInitialize(NULL)` (IAT `0xF95AAC`); `CoCreateInstance(CLSID_DxDiagProvider {A65B8071-...}, NULL, INPROC, IID_IDxDiagProvider)`
   (IAT `0xF95AA8`); failure: return 0.
2. `Initialize({dwSize 0x10, dwDxDiagHeaderVersion 0x6F, bAllowWHQLChecks FALSE, pReserved NULL})`, `GetRootContainer`,
   `GetChildContainer(L"DxDiag_DisplayDevices")`, `GetNumberOfChildContainers`; any failure: 0.
3. Per child (`EnumChildContainerNames(i, name, 256)`, `GetChildContainer`; a failure skips the child): GUID buffer = GUID_NULL
   (`0xFF6D80`), `GetProp(L"szDeviceIdentifier")`, when it is a BSTR `IIDFromString` (IAT `0xF95AB0`, result not checked); compare
   16 bytes with identifier +0x438 (`0x611770`). On a match: `GetProp(L"szDisplayMemoryEnglish")`, BSTR, narrowed (`0x4F3CC0`),
   non-empty, `_atoflt` (MSVCR80) returns 0, then `0x406800` (round, minus 1 if it rounded up: truncation) and
   `((MB + 4) << 20) & 0xFF800000` (label `0x6128C5`); the loop stops at the first match whether or not the memory string was
   usable (an unusable one leaves 0).
4. Releases, then `CoUninitialize()` (IAT `0xF95AB8`; docs/270 §39 said "never uninitialized": corrected here), return value `>> 20`.

So the result is a pure function of the DxDiag display-device strings and the D3D9 adapter GUID. Under the RTX Remix bridge the
adapter is the bridge's fixed identity (`DeviceConfig.log`: "Geforce GTX 580", device 0x1080, driver 32767.65535.65535.65535,
GUID 019674C3-...), which no DxDiag device has, so the game reads 0 [measured: `vramMB` 0 in 58 `[Verify]` readouts in probe-out (DXVK and bridge runs, none from plain d3d9);
inferred for the cause] and `GraphicsRules.sgr` gives `Texture memory: 1024MB <<OVERRIDE>>`.

## 3. Switch

`[Startup] DxDiagCache` (ts3native.ini, default 0; `dxdiagcache.cpp`, shared code `dxdiagvram.h`). A 5-byte `jmp` over the
function's first instruction (12 prologue bytes checked first) to `DdcHook`, which keeps the `CoInitialize(NULL)` ...
`CoUninitialize()` pair and then:

| Value | Behaviour |
|---|---|
| 0 | not hooked: the game's own query (vanilla) |
| 1 | cache: replay step 3 on the strings in `<Bin>\ts3native-vram.ini`; missing or stale sidecar: one real query (same calls and arguments, `DdcEnum`), written to the sidecar |
| 2 | zero: return 0 without DxDiag (the game's no-match answer; what it reads under the bridge anyway) |
| 3 | refresh: as 1 but always probes and rewrites the sidecar (queue miss arm; troubleshooting) |

`[Rewind] Park=1` (test-only) forces 2 whatever the value: rewind runs never start the ping loop.

**Sidecar** `ts3native-vram.ini` in Bin, next to `ts3native.ini` (never in a save, never in Documents): `[DxDiag] Sig`,
`ProbeMs`, `By` (game | helper), `Devices`, `Dev<i>=<szDeviceIdentifier or ->|<szDisplayMemoryEnglish>`. Sig is written last,
so a cut-short write is a miss next time.

**Cache key** (`DdcSig`, FNV-1a 64): every display-class driver key under
`HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-...}` (`MatchingDeviceId` = PCI vendor/device/subsys/rev, `DriverVersion`,
`DriverDate`), installed RAM in 256 MB steps (WDDM's `szDisplayMemoryEnglish` is dedicated + shared, and shared follows RAM),
and the Windows build + UBR. No WMI, no D3D. Not `GetAdapterIdentifier` as first proposed: under the bridge the game's adapter
identity is fixed and would never see a real driver update; the GPU GUID itself is not part of the key because the replay
matches it on every start anyway.

**Log** (ts3native.log): `DxDiag cache: DxDiagCache=1 (cache), sidecar ts3native-vram.ini` at install, then one line per start:
`DxDiagCache: hit -> 0 MB (mode 1, 2 device(s), sig ..., sidecar by helper) in 3 ms, ~61234 ms saved` (paths: hit, miss probed
and written, miss failed, zero). Counters v207 at 0x6580: `ddcMode`, `ddcPath` (1 hit, 2 miss written, 3 miss failed, 4 zero),
`ddcMB`, `ddcSavedMs`.

**EA 1.69:** no decrypted EA exe on hand for the address (docs/232); `0x612640` has a 0 row in `ts3addr-ea169.h`, so the EA
build logs `SKIP DxDiag cache ... no EA 1.69 address` and the game runs its own query.

## 4. Harness (Rodney 2026-11-17: the real query once per clone/install, never per test launch)

- `native/ts3native/out/dxdiag-vram.exe <Bin dir> [--force] [--check]` (built by `build.cmd`, 32-bit): the same DxDiag query
  outside the game, writes the sidecar (`By=helper`); a current sidecar returns at once. Prefer this over a game launch: the
  asi replays the same strings, so the answer is the game's.
- `tools/harness/game-exe.ts ensureVramSidecar()` runs it; called by `install-fixes.ts` (native-patches) and by every
  `turbo-compat.ts state` (all Bin states); `node tools/harness/game-exe.ts --vram` by hand. turbo-compat keeps the sidecar on
  restore (`KEEP_CACHE`, a machine cache, not a Bin change).
- `queue.ts` `lightIni`: every harness job gets `Startup.DxDiagCache=1` next to `CrashForensics.Light=1` (release-asi and
  `releaseDefaults` jobs stay as shipped); a job sets it 0 for the vanilla query. Without a sidecar the first launch probes once.

## 5. Queue (correctness, RUN NOT YET; dry-run PASS)

`tools/harness/queues/dxdiag-cache-correct-1117.json` (rewind false except the Park pair, noiseCheck false, menu starts):
off (DxDiagCache 0, `devcfg-check.ts --record`), on miss (3), on hit (1), Park pair (Park=1, two jobs with rewind auto):
DeviceConfig.log stable lines equal to the off arm in every arm (`tools/harness/devcfg-check.ts`), the log line per path,
`row.startMs` per arm (launch to menu). Pass: same DeviceConfig lines; hit arm startMs about 60 s below off; Park arm `zero`
line; the second Park job's rewind settle shows no "settle ping loop" line in the queue log (needs bugfix/rewind-com-roid-1117
merged for the line).

## 6. Default

Per the clear-gain rule: propose `DxDiagCache=1` in the release ini once the queue shows (a) DeviceConfig.log identical off vs
hit and (b) ~60 s less to the menu. The answer is the game's own by construction (same strings, same decision), so the risk is
a stale sidecar; the key covers driver, GPU, RAM and OS changes. Mode 2 stays test-only (outside the bridge 0 means the sgr
fallback, which can change presets on plain d3d9).
