# 260. Apex 3.1.1 crash 1: a vtable in .rdata rewritten in place (static analysis)

2026-11-13, static only: minidump, Apex log, ts3native log, TS3W.exe disassembly, Apex Radiance source at tag v3.1.1 (and
v3.1.2..v3.1.6 for §6). No game run. Follows docs/259 §7.1 crash 1 (run `bp-apex311-smoke-1112`, 12:10 UTC [20:10 PHT],
crash at 12:14:42 UTC [20:14:42 PHT], `ApexRadiance.asi` + DXVK + rc1111g, plus S3SS, S3PP and MonoPatcher). Filed
upstream as loinyx/Sims3-ApexRadiance#76 (quick triage only); the comment draft with these findings is kept outside the
repo for Rodney to post.

Tags as in docs/51: [verified] = read in the dump, a log, the file bytes or the source; [inferred] = follows from verified
facts but not observed directly.

## 0. Verdict

- **Crash 1 is a delayed effect.** About 2 min 17 s before the crash, slot 0 of the TS3W vtable `0xFF39F8` was changed
  in memory from `0x0069CFE0` to `0x0069CF50`; only the low byte changed. The crash is the first deleting-destructor call
  through that slot afterwards: an ordinary final Release of a `LightProbeProxy` while the lot renderer tears down its
  lighting. [verified for the crash, inferred for the timing]
- **How the vtable got written** [inferred, strong]: at about 12:12:25 UTC the main thread called virtual slot 4 (`+0x10`)
  of the light `0x698E3630` after that light had been destroyed. A destroyed light's vtable is reset to `0xFF39F8`, and
  that table has only 4 slots, followed by the string "Lighting/LightProbe". Slot 4 therefore reads the ASCII bytes "Ligh"
  = `0x6867694C`. The CPU jumped to that heap address: TS3W.exe is not NX-compatible, so DEP does not stop it. It then ran
  heap bytes as code until it hit an invalid opcode at `0x686801E6` (the C000001D). On the way, each `00 00` byte pair
  is `add byte [eax], al` with `eax = 0x00FF39F8`, which adds `0xF8` to byte `[0xFF39F8]`. Eighteen such adds turn `E0`
  into `50`.
- **Why the write did not fault, which is Apex's part** [verified source]: Apex's `CheckClassTables`
  (`features/level_light_share.cpp:2466-2468` at v3.1.1) calls `VirtualProtect(..., PAGE_READWRITE)` on two tables at
  `0xFF36C4` and never restores the protection. Protection is per page, so the whole page `0xFF3000-0xFF3FFF` stays
  writable. That page holds the vtables of lights' base, light probes and the light blockers. In vanilla this write is an
  AV at the real call site, 2 minutes earlier, with a usable stack. Something on the stack then swallowed the C000001D
  (no VEH continues it; an SEH `__except` did) [inferred].
- **Whose bug.** The silent .rdata corruption and the delayed, misleading crash come from Apex leaving the page writable.
  The first fault, a virtual call on a destroyed light during lamp edits, is not pinned to a call site. It happened in the
  same second as Apex's `[OutdoorDelta] Outdoor lamp delta: L698E3630` line, during Apex's lamp-edit handling. We lean
  towards the Apex area, but this is unproven: the game itself may hold the stale reference.
- **Ours ruled out** (§5): no rc1111g hook, patch, switch or page protection change touches this path or that page.
- Apex 3.1.2..3.1.6 do not change `CheckClassTables` or any light lifetime handling (§6), so they would not fix this
  [inferred].

## 1. Stack of the faulting main thread (thread 10664)

Exception C0000005 at `0x0069CF50` reading `0xE8CC292F`. Registers: eax `0069CF50`, ebx `414BB360`, ecx `3E770EE0`,
edx `00FF39F8`, esi `36FD5580`, edi `63B2D9C0`, ebp `7EA95B30`, esp `001AFD34` [verified: Apex crash report, our crash log
line, minidump exception stream]. The minidump is small (1,693 regions of about 0x400 bytes around register and stack
pointers, plus the main stack `0x1ADF50-0x1B0000`). It does not hold `0xFF39F8` or the heap around `0x686801E6`.

Frames are the return addresses on the stack, each checked to follow a CALL. Our names come from
`tools/ghidra/symbols/ts3w-1.67.mainthread.csv`; the role column is from this analysis.

| ESP+ | Return / value | Module | Function (ours) | Role [tag] |
|---|---|---|---|---|
| (eip) | `0x69CF50` | TS3W | code ending at the `ret` at `0x69CF71` (nearest named symbol `Lighting_WallOccluder__h1` `0x69ACE0`) | not a function start: byte 2 of the 6-byte `jnz` at `0x69CF4F` (§2) [verified] |
| +0x00 | `0xEC17C4` | TS3W | `0xEC17A0` (`eastl_vector_ReleaseAll__h2`) | refcount Release, `call eax` with `eax = [vt+0]` after the count reaches 0 [verified] |
| +0x08 | `0x6A6AB1` | TS3W | `0x6A69E0` (`LotRenderer_UpdateLot__h114`) | walks the rooms (`[edi+0x80]` chain); `0x69EE90(room+0x20)` drops the room's `+0x74` proxy, tail-jumping to Release, so it has no frame of its own [verified] |
| +0x24 | `0x6A7FF9` | TS3W | `0x6A7FF0` (`__h56`) | teardown of a lot lighting object [verified call; role inferred] |
| +0x30 | `0xADB742` | TS3W | `0xADB6A0` (`__h28`) | calls `0x6A7FF0(edi)`, then `operator delete(edi)` at `0xADB743` [verified] |
| +0x44 | `0xAE4999` | TS3W | `0xAE4980` (`__h9`) | conditional call to `0xADB6A0` [verified] |
| +0x50 | `0xAEB34D` | TS3W | `0xAEB2E0` `LotRenderer_UpdateLot` | per-lot renderer update [verified] |
| +0x54 | `0xAEB2E0` | TS3W | (a function pointer, not a return) | the next-layer pointer that Apex's CallChain keeps on the stack [inferred] |
| +0x5C | `ApexRadiance.asi+0x34615E` | Apex | `LotBuildShare::PassHook`, `features/lot_build_share.cpp:71` (v3.1.1) | sets the thread-local `t_inLotPass`, calls `next(renderer, edx, dt)`, then clears it; a marker only [verified: the bytes `mov byte [esi+0xB4],1; call edi; ...; mov byte [esi+0xB4],0` around a TLS slot, matching lines 68-73] |
| +0x70 | `0xC7CEE4` | TS3W | `0xC7CEA0` `LotRenderer_UpdateAllLots` | the lot pass `CALL 0xC7CEDF`, which Apex points at PassHook [verified] |
| +0x84 | `0xC7E41E` | TS3W | `WorldService_Update` | [verified] |
| +0xCC | `0x59ED59` | TS3W | `ServiceList_Update` (`call eax`) | [verified] |
| +0xE0 | `0x588E1D` | TS3W | `ServiceManager_Update` | [verified] |
| +0x138 | `0xECBD71` | TS3W | `App_RunLoop` (calls `MainLoop 0xECA960`) | [verified] |
| +0x168 | `0xECBE4C` | TS3W | `App_Run` | [verified] |
| +0x1B0 | `0xF7F201` | TS3W | CRT entry | [verified] |

Stale slots that do not follow a CALL: `0x59ED3D`, the ntdll values at +0x94/+0x9C, `0xF7F371`, `0xF7F409`, and the
UTF-16 text from +0x4B0 on. ts3native has no frame on this stack. One frame comes from Apex; it is a pass-through
wrapper, not the faulting code.

The ApexRadiance.asi RVAs were mapped to source like this. For `+0x33185A` and `+0xFB878`, the 3.1.1 bytes around the
RVA (relocations masked) match exactly one place in the 3.1.0 Developer build in `game-copy/apex-rel-3.1.0/Release`, and
its `.map` names the function. `+0x34615E` changed between the builds, so it was mapped by the neighbouring `__FILE__`
and function-name strings (lot_build_share.cpp) and by the code shape above.

## 2. The vtable puzzle

- `[0xFF39F8]` in the file is `0x0069CFE0` [verified, TS3W.exe 1.67 `.rdata`]. At the crash the CPU read `0x0069CF50`
  from it: `0xEC17BC mov edx,[ecx]` gives edx `0xFF39F8`, `0xEC17BE mov eax,[edx]` gives eax `0x69CF50`, and
  `0xEC17C2 call eax` [verified registers and code]. Only byte 0 differs (`E0` -> `50`). The minidump does not hold the
  page, so the in-memory value is known through eax only.
- The code at `0x69CED0-0x69CFD0` in the dump equals the file bytes [verified]. Nothing patched it.
- `0x69CF50` is not a function start in vanilla [verified]. It is byte 2 of `0F 85 9C FD FF FF` (`jnz`) at `0x69CF4F`,
  in the code just before the `ret` at `0x69CF71`. Decoded from `0x69CF50`, the bytes `85 9C FD FF FF 8B 4C` are
  `test [ebp+edi*8+0x4C8BFFFF], ebx`. With the crash registers that address is
  `0x7EA95B30 + 8 * 0x63B2D9C0 + 0x4C8BFFFF = 0xE8CC292F`, exactly the faulting read [verified arithmetic]. The CPU really
  executed from `0x69CF50`.
- **Who uses vtable `0xFF39F8`** [verified disassembly]: 4 slots: `0x69CFE0` deleting destructor (resets the vtable,
  `operator delete 0x4E3F70`), `0x5044C0` AddRef, `0xEC17A0` Release (`lock xadd [this+4], -1`; at 0 it sets the count
  to 1 and calls slot 0 with 1), `0x9F0DE0`. The string `Lighting/LightProbe` follows at `0xFF3A08`.
  - It is the full vtable of **LightProbeProxy**: `0x6AF7C0` allocates 0x40 bytes with tag `Lighting/LightProbeProxy`
    (`0xFF3A34`), sets `0xFF39F8`, AddRefs it and pushes it into the manager vector at `mgr+0x78`.
  - It is the base vtable that constructors write first and destructors write last for:
    - LightProbe (`0x6B4050` -> `0xFF3CBC` -> `0xFF3CDC`, ctor `0x6B4240`);
    - the light blockers (`0x69CFC0` -> `0xFF30F4`; `BlockerQuadCtor 0x69D000` -> `0xFF3118`, pooled at `0x1158A70`
      with deleting destructor `0x69D020`; disc `0xFF3C24`);
    - lights (the street-lamp class `0xFF42F8` deleting destructor `0x6C12E0` writes `0xFF4250`, then `0xFF39F8`, then
      frees).

    Light vtables share slots 1-3 with it (`0xFF42F8`: `005044C0 00EC17A0 009F0DE0`).
  - A light, probe or blocker whose destructor ran therefore reads `0xFF39F8` until its memory is reused.
- **The crashing object `0x3E770EE0`** [verified dump bytes]: `[0] = 0xFF39F8`, `[4] = 1`, then plausible data (sentinels
  -1000.0, a world position about (1293.7, 92.3, 1784.4), 1.0). Refcount 1 is what Release leaves when the count reaches 0:
  `xchg [this+4], 1` before the delete. The object is the room's `+0x54` handle (room `+0x20` struct). Only `0x69EE30`
  fills it, from `0x6AF7C0`. `0x69EE90` first hands it back to the manager (`0x6AF880`) and then Releases it. So this was
  a live LightProbeProxy going through a normal last Release [inferred class, from the only writer of that slot]. **It is
  not a stale object.** Any final Release of a LightProbeProxy, and any deleting call through a base-class vtable, would
  hit the corrupted slot. The lot renderer teardown on this stack happened to be first.
- **Patched by Apex, by us, or by the other mods?** Not by a hook. No Apex source line holds `0xFF39F8`, `0x69CFE0`,
  `0x69CF50`, `0x69EE90` or `0x6AF7C0` [verified grep at v3.1.1]. The same holds for our rc1111g source, map and hook
  lists (§5). Sims3Performance.asi (576,000 bytes) and Sims3SettingsSetter.asi (1,608,192 bytes), the same sizes as the
  Bin copies in the Apex log, hold no literal of these addresses. Their only `0x00FF3xxx` dwords are incidental bytes in
  code [verified byte scan]. The write is the stray `add` described in §3, made possible by the writable page.

## 3. The C000001D 2 min 17 s earlier

- Our crash log [verified, `...-1210-ts3native.log`]: `exception C000001D at 0x686801E6, thread 10664`. Registers: eax
  `00FF39F8`, ebx `001A00A4`, ecx `698E3630`, edx `68676953`, esi `698E3630`, edi `A5294A94`, ebp `001AF5D8`, esp
  `001AF549` (odd). The last log tick before it is 145,511,687 ms. The crash comes 136.9 s later (tick 145,648,609 just
  before it), so the C000001D was at about **12:12:25 UTC [20:12:25 PHT]** [inferred, ±1 s].
- **0x698E3630 is a light** [verified log]: Apex logs `20:12:25.256 [OutdoorDelta] Outdoor lamp delta: L698E3630,
  story 1 room 0 class 2 (in its solve): 0 texels rewritten` within that second. Its neighbours `698E2E10`, `698E4970`
  and `698E6600` are lamps in the `[GpuRoomLamps] settle: lamp ...` lines. The smoke job was editing lamps (Apex logs
  "Lamp edit" every few seconds and "LiveTable 169 object moves in 1046 ms").
- **What was executing** [inferred]: a virtual call through slot 4 of a destroyed light. With vtable `0xFF39F8`, slot 4 is
  the bytes "Ligh" = `0x6867694C` (slot 5 would be "ting" = `0x676E6974`, 18 MB below the fault, so not plausible). The
  register state fits the call shape `mov esi,ecx; mov eax,[esi]; mov edx,[eax+0x10]; call edx`:
  - ecx = esi = the light;
  - eax = its vtable `0xFF39F8`;
  - edx keeps `0x686769xx` of "Ligh", with the low byte moved during the slide.

  The jump landed in heap memory. TS3W.exe has `DllCharacteristics = 0x0000` (no NX-compatible flag) [verified], so under
  the default OptIn DEP policy heap pages run [inferred]. It then ran 0x8E9A bytes of heap data as code up to the invalid
  opcode at `0x686801E6`. That memory is ordinary heap: not an Apex trampoline (Apex's detour sections are in its own
  image), not JIT code, not freed code.
- **Light slot 4 is "the colour of this light at a point"** per Apex's own notes (`features/object_light_bridge.cpp:4-5`,
  `:579-585`). The game's rig gather calls it at `0x6BB2B1`, but with ecx = edi and eax not the vtable, so the shape
  differs. In TS3W, 62 sites have the exact register shape above. The only one in the lighting and lot-render ranges is
  `0x6B4107` in `0x6B4100` (a LightProbe update: slot 4, then slot 6, called from `0xAFA6E7` on `owner+0x2F0`). Apex's
  ObjectLightBridge also replaces the slot `+0x10` of light classes with a wrapper that calls the original. **The exact
  call site cannot be recovered from this dump**: esp was misaligned after the slide, and the stack scan of our crash log
  from there (`0xA8DE09 0x69B2A2 ...`) is not a call chain.
  - `0x65B6FAC9` in that scan is ts3native `.data` (`g_asRing+0x2D61`, alloc-sites ring, off with Apex), not code
    [verified map].
- **How it rewrote the vtable** [inferred, strong]: heap data is mostly `00` bytes, and `00 00` is `add byte [eax], al`.
  With `eax = 0x00FF39F8` (unchanged at the fault, as the log shows), each one adds `0xF8` to byte `[0xFF39F8]`.
  `(0xE0 + k * 0xF8) mod 256 = 0x50` for k = 18, 50, 82, ...: the observed value. Only byte 0 can change this way, which
  is what §2 sees. Without a writable `0xFF3000` page, the first such add is a C0000005 write fault.
- **Why the page was writable** [verified source]: Apex v3.1.1 `features/level_light_share.cpp:2447-2469`
  `CheckClassTables()`, called at `:14157` (`g_classTables = amb && CheckClassTables()`), ends with
  `VirtualProtect(kClassWalls2D /*0xFF36C4*/, 0x18, PAGE_READWRITE, &old)` and no restore. Apex writes those tables later
  (`:2731-2740`, quick-pass class switches). The run's log shows LevelLightShare installed and no "One ambient ... game
  code differs" warning, so `amb` was true and `CheckClassTables` ran [inferred]. Apex's own `MemPatch::ProtectedWrite`
  (`framework/memory_patch.cpp:25-33`) restores protection after each write; this site does not use it.
- **Why the game went on**: Apex's VEH only records (`framework/crash_report.cpp:381`), and ours logs first-chance
  exceptions only. A frame up the stack must have handled the C000001D with an SEH `__except`. Apex wraps many game
  lighting calls in `__try/__except(EXCEPTION_EXECUTE_HANDLER)`, for example `object_light_bridge.cpp:110-124` around
  the game's light-rig cell gather `0x6B5AF0` (which asks lights for their colour) and `lamp_mark_filter.cpp:216-240`.
  We cannot name the frame [inferred].
- **5 s later, more stale lamp data** [verified mapping, staleness inferred]: an AV at `ApexRadiance.asi+0x33185A`,
  reading `0xB0`, main thread, stack inside the room light solve (`0x6C793E`, `0x6C5F6B`, ...). It maps to
  `lamp_mark_filter.cpp:224` `Signature()`: `info = [entry+0x20]` was `0x20`, and `[info+0x90]` faulted. A lamp entry
  holding garbage; Signature's own `__except` handled it. This is the second sign that light or lamp entries were stale
  around these lamp edits.

## 4. What Apex 3.1.1 does around this path

All paths are Apex source at tag v3.1.1.

| Feature (on in this run) | What it does here | Source |
|---|---|---|
| LevelLightShare | `CheckClassTables` leaves page `0xFF3000` (blocker, probe and light-base vtables) read-write | `features/level_light_share.cpp:2447-2469`, `:14157` |
| LotBuildShare | `PassHook` at the lot pass `CALL 0xC7CEDF`; the only Apex frame in crash 1 | `features/lot_build_share.cpp:64-73` |
| RoomLightQueue | spreads room re-solves over frames ("Lamp edit: its rooms settled after N ms over M frames") | `features/room_light_queue.cpp` |
| LampMarkFilter | per-light state keyed by raw light pointer; `RemovalHook` on light object removal; `Signature()` reads light and entry memory under SEH | `features/lamp_mark_filter.cpp:216-240`, `:437`, `:1115` |
| OutdoorDelta | keeps `LampRec.light` raw pointers across solves; two worker threads (`MoveWorker`, `PhysBasisWorker`) work on copies | `features/outdoor_delta.cpp:168-170`, `:4911`, `:5659` |
| ObjectLightBridge | replaces light vtable slot `+0x10`; calls the game's rig gathers under `__try/__except` | `features/object_light_bridge.cpp:1-19`, `:100-125`, `:579-585` |
| GpuRoomLamps / room_light_tests | knows blocker vtables `0xFF3118` / `0xFF3C24`; tests run on copies, "any thread" | `features/room_light_tests.h:1-10`, `:56` |
| InvalidateRoom `0x69EED0` entry hooks | development builds only | `features/level_light_share.cpp:7668` |

No Apex code creates, copies or releases LightProbeProxy objects or the room `+0x54` handle. `0x69EE30`, `0x69EE90` and
`0x6AF7C0` are not in its source [verified grep]. The proxy refcount uses `lock xadd`, but `0x69EE90` reads and clears
`+0x54` without a lock [verified]; no Apex thread touches it [inferred from the grep].

## 5. Our side (rc1111g, `game-copy/ts3native.ini.rc1111g`)

Checked against the rc1111g source commit `f979d8ed` (builds.json), the map and `docs/data/apex-hooks.csv` [verified
unless marked]:

- No literal in page `0xFF3000-0xFF3FFF`. None of the 22 path addresses (§1-§3) appears in our source, `docs/68` or the
  `our_overlap` column of the CSV (169 Apex rows in these ranges, none overlapping). Every `VirtualProtect` in
  ts3native restores the old protection (`ts3native.cpp:70-72`, `:115-126`).
- Sites near the ranges, all not on this path: `[Verify]` lot-lighting entry detours (OFF, ini:1261-1282);
  `[CrashForensics] DrawGuards` (OFF); `[LotPool] Trim` (stood down with Apex; lot message pool); `[ObstructedRO]`
  (ON, read-only lot query from routing); `[ProbeSnap]` (OFF).
- Heap: `[Alloc] Enabled=0` (ini:434), `[UafCheck]` 0 and stands down with Apex, `[ArenaGuard]` 0, `[MemDiag]` 0,
  `AllocSites=0`, `[Compat] ApexStandDown=1` (ini:35). Our code never handles lights, lamps, room lights, probes or
  blockers. Whether any computed pointer of ours reaches them was not fully audited [inferred no].
- `0x686801E6` is ordinary heap; nothing of ours is mapped there.

**Ours ruled out for crash 1, high confidence.** Crash 2 (route obstacles) is a separate track (docs/259 §7.1).

## 6. Apex 3.1.2 .. 3.1.6

All five tags are dated 2026-11-12. `git diff v3.1.1 v3.1.6` touches `lamp_mark_filter.cpp` (+77: diagnostic
`ReadSigDwords`, the "marks ... with no edit" log), `outdoor_delta.cpp` (+131), `room_light_queue.cpp` (+75: slow drain-step
log with page faults), `level_light_share.cpp` (+140) and `gpu_room_lamps.cpp` (+10). `CheckClassTables` is unchanged at
v3.1.6 (`level_light_share.cpp:2473-2475`, still `PAGE_READWRITE` with no restore). The changelog for 3.1.2-3.1.6 has no
lamp-lifetime or crash fix in this area ("No crash when a Sim wears a broken custom part" is unrelated). **No later
version would plausibly fix crash 1** [inferred from the diff].

## 7. Cause chain, confidence, suggestion for Apex

1. Apex install: page `0xFF3000` of TS3W `.rdata` made `PAGE_READWRITE` for good [verified source, ran inferred].
2. About 12:12:25 UTC, during lamp edits: a call through slot 4 (`+0x10`, "colour at a point") on the destroyed light
   `0x698E3630`. That is a use after free; the code that held the stale pointer is unknown [inferred, medium-high].
3. The jump to "Ligh" `0x6867694C` executed heap bytes, and `add [eax],al` with `eax = 0xFF39F8` rewrote slot 0
   `0x69CFE0` -> `0x69CF50` [inferred, high: exact byte arithmetic, only byte 0 changed].
4. The C000001D at `0x686801E6` was caught by some SEH `__except`, and the game ran on [inferred].
5. 12:14:42 UTC: lot renderer teardown, last Release of a LightProbeProxy, `call [0xFF39F8]` = `0x69CF50`, AV
   [verified].

Confidence: steps 1, 3 and 5 high; step 2 medium for the use after free and low for its owner. The root fault is in the
light lifetime: the game or Apex. With vanilla page protection it would have crashed at step 2 with the real caller on
the stack.

Suggestions for Apex:

- Restore the protection in `CheckClassTables`. Either write the class tables through `MemPatch::ProtectedWrite` each time
  they change, or copy them into Apex memory and point the three readers (`0x6A8CB0`, `0x6A8CE0`, `0x6A8D00`, whose bytes
  Apex already checks) at the copy.
- Do not catch C000001D or C0000005 from game code silently with `EXCEPTION_EXECUTE_HANDLER`. Log once with EIP, the
  registers and the first stack words, so a stale-object call is reported where it happens.
- Diagnostic for the root fault: in a Developer build, compare `[0xFF39F8..0xFF3A08)` with the file bytes once per frame
  and log the first change with a stack. Also check light-pointer users (OutdoorDelta `LampRec.light`, LampMarkFilter
  state, ObjectLightBridge) against the light removals `RemovalHook` sees: a stored light used after its removal is the
  candidate.
- For us: nothing to fix. An optional low-cost check for our crash log would be to log the protection of the TS3W
  `.rdata` pages and a compare of known vtables at crash time. Not built.

Open: the exact caller of step 2 (needs the Apex Developer build with the per-frame vtable compare, or a repro with the
page left read-only); whether any S3SS or S3PP code path (literal scan only) changes the page protection.
