// [EaCrashFixes] WorldAStarRefs (0.36.151, docs/67 §30, counters v146 wa*, issue #132): range check for the world A*'s
// neighbour references. Default OFF. Included by ts3native.cpp after eafix.cpp.
//
// The world A* 0x676260 decodes each neighbour reference of the current waypoint (docs/99 §1.3: bits 0-9 waypoint, bits
// 10-12 dx + 3, bits 13-15 dz + 3) into a target chunk `current + dz * w + dx` in 16 bits and stores the pair at its
// [esp+1Ch] (chunk) / [esp+1Eh] (waypoint), 0x67661D / 0x676622. Nothing checks the pair: it indexes the closed-bitset row
// table (chunk + 1, 0x67663F / 0x67664F) and then the cost function's chunk offset table and the chunk's record offsets
// (0x6742B5; the parent's pair at 0x6743FE). A pair outside R reads past those tables: Baseline-IP crashed 5 of 5 at the
// four sites, run 168 with chunk 0xFFD5 (-43) and waypoint 0x207 on a 64 x 64 world whose shipped graph has every reference
// in range (docs/67 §30). Fix: `mov edx,[esp+1Ch]; movzx ecx,ax` at 0x676627 becomes a jump to WaStub, which sends a pair
// outside R (chunk >= w * h, or waypoint >= the chunk's count) to the game's own "already closed" path 0x67673F (next
// neighbour; it reloads its state from the stack) and runs the two moved instructions for every other pair. In-range pairs
// cost one call; game state is never written. One "ea fix: WorldAStarRefs" line for each of the first 8 skips.
//
// [EaCrashFixes] PortalFlagRefs (0.36.152, docs/67 §30.5, counters v146 pf*): the cause behind those pairs. Two writers of a
// waypoint's blocked flag (record + 1, bit 0x80) index R with a pair they never check: 0x82A190 with a lot portal's stored
// id (chunk = id & 0x3FFF, waypoint = id >> 14 & 0x3FF), 0x82B590 with a neighbour decoded from a lot waypoint's references.
// For a waypoint past its chunk's count the record offset is read from the next bytes, and the flag write lands inside
// another record: on Baseline-IP it cleared bit 15 of two references (dz +1 became dz -3), which then sent the A* off the
// graph. Fix: both index computations become jumps to stubs that skip the write (writer 1: the next portal; writer 2: the
// next reference) when the pair is not a waypoint of R. One "ea fix: PortalFlagRefs" line for each of the first 8 skips.
//
// [EaCrashFixes] LotGridMiss (next version, docs/67 §31.8, PR #212): Isla Paradiso Error 12. The lot routing finalize
// 0x82B590 clears the lot's "build in progress" byte (lot routing record + 0x8E, 0x631A30 at 0x82B802) only on the way out
// of its connection pass. A lot with lot+0x4BC == 0 whose centre has no world grid point within 1 m (0x651DA0 false: a save started
// on another version of the world, or a lot made in play) leaves at 0x82B608 without the clear. The lot then stays "in progress": the
// finalize runs again about once a second, and the save's per-lot routing writer 0x63C690 fails for it after its forced
// rebuild (still in progress at 0x63C781) and clears the save context's ok flag. Every later write of that lot entry is
// then skipped, and World_Save returns false (Error 12). Fix: the je at 0x82B608 keeps its condition but goes to LgStub,
// which goes on at the game's own exit for a lot with nothing to connect (0x82B7F4, the target of 0x82B659): it clears the
// byte and returns. The connection pass is skipped as before. One "ea fix: LotGridMiss" line for each of the first 8 lots.
//
// [EaCrashFixes] HeightsInfLoop (0.36.253, docs/67 §36, no counters; on in the ini): an Isla Paradiso freeze. Post pass 16 (0x64C7C0,
// the node heights pass, called by 0x64E380 at 0x64E4ED) splits each path segment into steps: from the squared length L2
// ([esp+340h], all four lanes) it takes s = sqrt(L2) and halves s until s <= 2 m (0x64CF10..0x64CF5E: `ja 0x64CF55` on
// s > 2, else a lane test, then s *= 0.5 and jmp back). Segments with L2 below the constant at 0x11CF0C0, or near it, jump
// to 0x64D275 first (no extra steps). L2 is the 3D squared distance between two of the pass's height samples (x, y, z at
// list entry + 0x10, 0x64CCF0); in the 2026-11-12 dump 11 samples in a row had y = 2.26e34 (bits 0x788B85D4) next to a
// normal 35.7, so L2 overflowed to +inf, s = inf, and inf * 0.5 stays inf: the sim thread (or the route worker) never
// leaves the loop. Seen three
// times on Baseline-IP, each a hang capture with the sim thread at 0x64CF22..0x64CF55 under Route_Replan /
// Route_PlanFromPoint (2026-11-06, 2026-11-10 houseboat move, 2026-11-12 world load); the 2026-11-12 kill dump shows
// xmm2 = (inf, inf, inf, inf), xmm7 = 0.5, xmm4 = 2.0. Fix: the 6-byte `ja 0x64D275` at 0x64CEBA becomes a jump to
// HiStub, which takes the game's ja as before and also sends L2 = +inf (lane 0, the lane the loop tests) to 0x64D275, the
// game's own no-split path for short segments. Only that one value changes path: NaN keeps the game's way (the loop's
// compares are false for NaN and it leaves at once). xmm0-7 are kept around the log call (the 0x64D275 path reads them).
// One "ea fix: HeightsInfLoop" line for each of the first 8 segments. Not fixed: where the infinite endpoint comes from.
//
// [EaCrashFixes] BridgeHeightNaN (0.36.253, docs/67 §37, counters v918 bhnHits at 0x62C8; on in the ini): the source of that endpoint. Three height
// queries ask the bridge under a point when their "consider bridges" byte is set: 0xC6A250 (World_GetHeightAt, the script's
// World.GetTerrainHeight(x, z, true) through 0x786200, and post pass 16), 0xC6A540 (Terrain_GetHeight) and 0xC61200. Each
// finds the bridge with 0xB793F0, takes its height with 0xB745A0 (st0), stores it as the answer and keeps the terrain height
// (0xC2A570) only when the terrain is higher (`fcomi`; false for NaN, +inf and huge values). On Isla Paradiso (2026-11-13,
// x 835..885, z 840..849) one bridge answers NaN or +-inf at 7 of 21 points, and the 2026-11-12 dump had 2.26e34 there, while
// the same points without bridges read 20-36 m. Fix: the 7 bytes after each `call 0xB745A0` (`mov ecx,[reg+54h]; fstp
// [esp+N]`) become a jump to a stub. A finite bridge height of magnitude 1e6 or less runs the two moved instructions (the
// game's answer, unchanged). Any other value is popped and the query goes on at its own "no bridge here" path (the target of
// its `je` after 0xB793F0, same esp), which ends in the terrain height. One "ea fix: BridgeHeightNaN" line for each of the
// first 8 hits. Not fixed: why the bridge's curve (0xB73F60 inside 0xB745A0) gives such values.

static const DWORD WA_SITE = 0x00676627, WA_BACK = 0x0067662E, WA_SKIP = 0x0067673F;
static const u8 kWaSite[7] = {0x8B, 0x54, 0x24, 0x1C, 0x0F, 0xB7, 0xC8};  // mov edx,[esp+1Ch]; movzx ecx,ax
static DWORD g_waBack = WA_BACK, g_waSkip = WA_SKIP;
static volatile LONG g_waLogged;

// 0 when (chunk, wp) is a waypoint of R (+0 w, +4 h, +8 chunk offsets, +0xC record area, docs/99 §1.4), else the reason:
// 1 chunk outside the map, 2 waypoint past its chunk's count, 3 chunk record unreadable. *count = the chunk's count.
static int WaPairWhy(const u8* r, DWORD chunk, DWORD wp, DWORD* count) {
    const DWORD w = *(const DWORD*)r, h = *(const DWORD*)(r + 4);
    *count = 0;
    if (chunk >= w * h) return 1;
    __try { *count = *(const WORD*)(*(const u8* const*)(r + 0xC) + (*(const DWORD* const*)(r + 8))[chunk]); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return 3; }
    return wp >= *count ? 2 : 0;
}

// sp = the A*'s esp at the patch site; cur = esi there (the current search node's chunk / waypoint pair).
// Returns 1 for a pair inside R, else 0 (counted in waSkip[reason - 1]).
static LONG __stdcall WaRefOk(const u8* sp, const WORD* cur) {
    const u8* r = *(const u8* const*)(sp + 0x50);  // R
    const DWORD chunk = *(const WORD*)(sp + 0x1C), wp = *(const WORD*)(sp + 0x1E);
    const DWORD w = *(const DWORD*)r, h = *(const DWORD*)(r + 4);
    DWORD count;
    const int why = WaPairWhy(r, chunk, wp, &count);
    if (!why) return 1;
    InterlockedIncrement(&ts3native_counters.waSkip[why - 1]);
    if (InterlockedIncrement(&g_waLogged) <= 8) {
        static const char* const kWhy[3] = {"chunk outside the map", "waypoint past its chunk's count", "chunk record unreadable"};
        const u8* rec = *(const u8* const*)(sp + 0x2C);  // the current waypoint's record: +0 n, +1 flags, +5 WORD refs[n]
        const DWORD k = sp[0x58];
        DWORD ref = 0, n = 0, fl = 0, cc = 0xFFFF, cw = 0xFFFF;
        __try { n = rec[0], fl = rec[1], ref = *(const WORD*)(rec + 5 + 2 * k), cc = cur[0], cw = cur[1]; } __except (EXCEPTION_EXECUTE_HANDLER) {}
        Log("ea fix: WorldAStarRefs: neighbour %lu of %lu of waypoint (chunk %lu, %lu), flags 0x%02lX, ref 0x%04lX -> chunk %lu "
            "waypoint %lu (map %lu x %lu chunks, chunk count %lu) skipped: %s", k, n, cc, cw, fl, ref, chunk, wp, w, h, count, kWhy[why - 1]);
    }
    return 0;
}

// Replaces the 7 bytes at 0x676627. Registers and flags at the patch site are the game's (flags are dead: `add edx,1` follows).
static __declspec(naked) void WaStub() {
    __asm {
        push eax
        push ecx
        push edx
        push esi
        lea ecx, [esp + 16]  // the A*'s esp
        push ecx
        call WaRefOk
        test eax, eax
        pop edx
        pop ecx
        pop eax
        jz skip
        mov edx, [esp + 0x1C]
        movzx ecx, ax
        jmp dword ptr [g_waBack]
    skip:
        jmp dword ptr [g_waSkip]
    }
}

static bool WaPatch(u8* at) {
    if (!Matches(at, kWaSite, sizeof(kWaSite))) return false;
    u8 b[7] = {0xE9, 0, 0, 0, 0, 0x66, 0x90};
    *(LONG*)(b + 1) = (LONG)((uintptr_t)&WaStub - ((uintptr_t)at + 5));
    return Write(at, b, sizeof(b));
}

static bool ApplyWorldAStarRefs(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"EaCrashFixes", L"WorldAStarRefs", 0, ini)) return false;
    if (!WaPatch((u8*)(uintptr_t)WA_SITE)) {
        Log("SKIP ea fix: WorldAStarRefs: bytes at 0x%08X differ (another mod or a probe build owns it): not installed", WA_SITE);
        return false;
    }
    ts3native_counters.waOn = 1;
    Log("OK   ea fix: WorldAStarRefs (docs/67 §30): world A* neighbours outside the routing graph are skipped (0x%08X)", WA_SITE);
    return true;  // keep the log open for the skip lines
}

// --- [EaCrashFixes] PortalFlagRefs ---
static const DWORD PF1_SITE = 0x0082A1CF, PF1_BACK = 0x0082A1DA, PF1_SKIP = 0x0082A204;  // writer 1, a lot portal's stored id
static const DWORD PF2_SITE = 0x0082B6B1, PF2_BACK = 0x0082B6B7, PF2_SKIP = 0x0082B7D5;  // writer 2, a lot waypoint's neighbour
static const u8 kPf1Site[11] = {0x8B, 0x46, 0x5C, 0x8B, 0xD1, 0x81, 0xE2, 0xFF, 0x3F, 0x00, 0x00};  // mov eax,[esi+5Ch]; mov edx,ecx; and edx,3FFFh
static const u8 kPf2Site[6] = {0x8B, 0x0C, 0x8A, 0x03, 0x4E, 0x60};  // mov ecx,[edx+ecx*4]; add ecx,[esi+60h]
static DWORD g_pf1Back = PF1_BACK, g_pf1Skip = PF1_SKIP, g_pf2Back = PF2_BACK, g_pf2Skip = PF2_SKIP;
static volatile LONG g_pfLogged;

// world = esi at both sites: the routing world, R at +0x54. Returns 1 when the pair is a waypoint of R, else 0 (counted in
// pfSkip[which - 1]).
static LONG PfOk(const u8* world, DWORD chunk, DWORD wp, DWORD id, int which) {
    const u8* r = world + 0x54;
    DWORD count;
    const int why = WaPairWhy(r, chunk, wp, &count);
    if (!why) return 1;
    InterlockedIncrement(&ts3native_counters.pfSkip[which - 1]);
    if (InterlockedIncrement(&g_pfLogged) <= 8) {
        static const char* const kWhy[3] = {"chunk outside the map", "waypoint past its chunk's count", "chunk record unreadable"};
        Log("ea fix: PortalFlagRefs: %s 0x%08lX -> chunk %ld waypoint %lu (map %lu x %lu chunks, chunk count %lu) skipped: %s",
            which == 1 ? "lot portal id" : "lot waypoint neighbour, chunk", which == 1 ? id : chunk, (long)chunk, wp,
            *(const DWORD*)r, *(const DWORD*)(r + 4), count, kWhy[why - 1]);
    }
    return 0;
}
static LONG __stdcall PfIdOk(const u8* world, DWORD id) { return PfOk(world, id & 0x3FFF, (id >> 14) & 0x3FF, id, 1); }
static LONG __stdcall PfPairOk(const u8* world, DWORD chunk, DWORD wp) { return PfOk(world, chunk, wp, 0, 2); }

// Writer 1 (0x82A1CF): ecx = the portal's id, esi = the world; eax and edx are written next. Skip = the next portal.
static __declspec(naked) void PfStub1() {
    __asm {
        push eax
        push ecx
        push edx
        push ecx
        push esi
        call PfIdOk
        test eax, eax
        pop edx
        pop ecx
        pop eax
        jz skip
        mov eax, [esi + 0x5C]
        mov edx, ecx
        and edx, 0x3FFF
        jmp dword ptr [g_pf1Back]
    skip:
        jmp dword ptr [g_pf1Skip]
    }
}

// Writer 2 (0x82B6B1): ecx = the neighbour's chunk (32 bit, may be negative), eax = its waypoint, edx = R's chunk offsets,
// esi = the world. Skip = the next reference (0x82B7D5 reloads its state from the stack).
static __declspec(naked) void PfStub2() {
    __asm {
        push eax
        push ecx
        push edx
        push eax
        push ecx
        push esi
        call PfPairOk
        test eax, eax
        pop edx
        pop ecx
        pop eax
        jz skip
        mov ecx, [edx + ecx * 4]
        add ecx, [esi + 0x60]
        jmp dword ptr [g_pf2Back]
    skip:
        jmp dword ptr [g_pf2Skip]
    }
}

// The game's bytes at `at` become jmp stub + int3 fill (never run: the stub jumps back past them).
static bool PfPatch(u8* at, const u8* want, size_t n, void (*stub)()) {
    if (!Matches(at, want, n)) return false;
    u8 b[11];
    memset(b, 0xCC, sizeof(b));
    b[0] = 0xE9;
    *(LONG*)(b + 1) = (LONG)((uintptr_t)stub - ((uintptr_t)at + 5));
    return Write(at, b, n);
}

// --- [EaCrashFixes] LotGridMiss ---
static const DWORD LG_SITE = 0x0082B608, LG_CLEAR = 0x0082B7F4;
static const u8 kLgSite[6] = {0x0F, 0x84, 0xF9, 0x01, 0x00, 0x00};   // je 0x82B807 (the exit without the clear)
static const u8 kLgClear[6] = {0x8B, 0x4B, 0x4C, 0x8B, 0x53, 0x48};  // mov ecx,[ebx+4Ch]; mov edx,[ebx+48h] (the lot id)
static DWORD g_lgClear = LG_CLEAR;
static volatile LONG g_lgHits;

static void __stdcall LgHit(const u8* lot) {
    const LONG n = InterlockedIncrement(&g_lgHits);
    if (n <= 8 && Readable(lot + 0x48, 8))
        Log("ea fix: LotGridMiss: lot 0x%08lX%08lX has no world grid point at its centre: its routing build is marked done "
            "(otherwise the save fails, docs/67 §31.8)", *(const DWORD*)(lot + 0x4C), *(const DWORD*)(lot + 0x48));
}

// ebx = the lot (checked non-null at 0x82B5C1); the clear path reads only ebx and esi.
static __declspec(naked) void LgStub() {
    __asm {
        pushad
        push ebx
        call LgHit
        popad
        jmp dword ptr [g_lgClear]
    }
}

// The je's rel32 at `at` + 2 is pointed at LgStub; the condition and the fall-through stay the game's.
static bool LgPatch(u8* at) {
    if (!Matches(at, kLgSite, sizeof(kLgSite))) return false;
    const LONG rel = (LONG)((uintptr_t)&LgStub - ((uintptr_t)at + 6));
    return Write(at + 2, &rel, 4);
}

static bool ApplyLotGridMiss(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"EaCrashFixes", L"LotGridMiss", 0, ini)) return false;
    if (!Matches((u8*)(uintptr_t)LG_CLEAR, kLgClear, sizeof(kLgClear)) || !LgPatch((u8*)(uintptr_t)LG_SITE)) {
        Log("SKIP ea fix: LotGridMiss: bytes at 0x%08X / 0x%08X differ (another mod or a probe build owns them): not installed", LG_SITE, LG_CLEAR);
        return false;
    }
    Log("OK   ea fix: LotGridMiss (docs/67 §31.8): a lot routing finalize with no world grid point clears its build flag (0x%08X)", LG_SITE);
    return true;  // keep the log open for the hit lines
}

static bool ApplyPortalFlagRefs(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"EaCrashFixes", L"PortalFlagRefs", 0, ini)) return false;
    if (!Matches((u8*)(uintptr_t)PF1_SITE, kPf1Site, sizeof(kPf1Site)) || !Matches((u8*)(uintptr_t)PF2_SITE, kPf2Site, sizeof(kPf2Site))) {
        Log("SKIP ea fix: PortalFlagRefs: bytes at 0x%08X / 0x%08X differ (another mod or a probe build owns them): not installed", PF1_SITE, PF2_SITE);
        return false;
    }
    if (!PfPatch((u8*)(uintptr_t)PF1_SITE, kPf1Site, sizeof(kPf1Site), PfStub1) || !PfPatch((u8*)(uintptr_t)PF2_SITE, kPf2Site, sizeof(kPf2Site), PfStub2)) {
        Log("FAIL ea fix: PortalFlagRefs: patch write failed");
        return false;
    }
    ts3native_counters.pfOn = 1;
    Log("OK   ea fix: PortalFlagRefs (docs/67 §30.5): lot portal blocked-flag writes through pairs outside R are skipped (0x%08X, 0x%08X)", PF1_SITE, PF2_SITE);
    return true;  // keep the log open for the skip lines
}

// --- [EaCrashFixes] HeightsInfLoop ---
static const DWORD HI_SITE = 0x0064CEBA, HI_BACK = 0x0064CEC0, HI_SKIP = 0x0064D275, HI_LOOP = 0x0064CF55;
static const u8 kHiSite[6] = {0x0F, 0x87, 0xB5, 0x03, 0x00, 0x00};  // ja 0x64D275
static const u8 kHiLoop[11] = {0x0F, 0x28, 0xC2, 0x0F, 0x59, 0xC7, 0x0F, 0x28, 0xD0, 0xEB, 0xB0};  // s *= 0.5; jmp 0x64CF10
static DWORD g_hiBack = HI_BACK, g_hiSkip = HI_SKIP;
static volatile LONG g_hiHits;

// prev = pass 16's copy of the previous point ([esp+360h], stored at 0x64CE2D), cur = the current list entry (esi; x, y, z
// at +0x10). Logged only: either may be stale or unreadable.
static void __stdcall HiHit(const float* prev, const u8* cur) {
    const LONG n = InterlockedIncrement(&g_hiHits);
    if (n > 8) return;
    float p[3] = {0, 0, 0}, c[3] = {0, 0, 0};
    __try { memcpy(p, prev, sizeof(p)), memcpy(c, cur + 0x10, sizeof(c)); } __except (EXCEPTION_EXECUTE_HANDLER) {}
    Log("ea fix: HeightsInfLoop: a path segment of infinite length was not split by the heights pass (segment %ld, from "
        "(%g, %g, %g) to (%g, %g, %g); the game would halve it forever, docs/67 §36)", n, p[0], p[1], p[2], c[0], c[1], c[2]);
}

// At 0x64CEBA: flags from `comiss xmm1, [esp+2C0h]` (xmm1 = the short-segment constant, [esp+2C0h] = lane 0 of xmm2 = L2).
static __declspec(naked) void HiStub() {
    __asm {
        ja skip  // the game's ja: a short segment
        push eax
        movd eax, xmm2
        cmp eax, 0x7F800000  // L2 = +inf
        pop eax
        je inf
        jmp dword ptr [g_hiBack]
    inf:
        sub esp, 128
        movups [esp], xmm0
        movups [esp + 16], xmm1
        movups [esp + 32], xmm2
        movups [esp + 48], xmm3
        movups [esp + 64], xmm4
        movups [esp + 80], xmm5
        movups [esp + 96], xmm6
        movups [esp + 112], xmm7
        pushad
        lea eax, [esp + 160 + 0x360]  // pass 16's esp + 360h
        push esi
        push eax
        call HiHit
        popad
        movups xmm0, [esp]
        movups xmm1, [esp + 16]
        movups xmm2, [esp + 32]
        movups xmm3, [esp + 48]
        movups xmm4, [esp + 64]
        movups xmm5, [esp + 80]
        movups xmm6, [esp + 96]
        movups xmm7, [esp + 112]
        add esp, 128
    skip:
        jmp dword ptr [g_hiSkip]
    }
}

// The 6-byte ja at `at` becomes jmp HiStub + one int3 (never run: the stub jumps on past it).
static bool HiPatch(u8* at) {
    if (!Matches(at, kHiSite, sizeof(kHiSite))) return false;
    u8 b[6] = {0xE9, 0, 0, 0, 0, 0xCC};
    *(LONG*)(b + 1) = (LONG)((uintptr_t)&HiStub - ((uintptr_t)at + 5));
    return Write(at, b, sizeof(b));
}

static bool ApplyHeightsInfLoop(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"EaCrashFixes", L"HeightsInfLoop", 0, ini)) return false;
    if (!Matches((u8*)(uintptr_t)HI_LOOP, kHiLoop, sizeof(kHiLoop)) || !HiPatch((u8*)(uintptr_t)HI_SITE)) {
        Log("SKIP ea fix: HeightsInfLoop: bytes at 0x%08X / 0x%08X differ (another mod or a probe build owns them): not installed", HI_SITE, HI_LOOP);
        return false;
    }
    Log("OK   ea fix: HeightsInfLoop (docs/67 §36): path segments of infinite length skip the heights pass's halving loop (0x%08X)", HI_SITE);
    return true;  // keep the log open for the hit lines
}

// --- [EaCrashFixes] BridgeHeightNaN ---
// Site i = the 7 bytes after `call 0xB745A0` (the bridge height, st0) in 0xC61200, World_GetHeightAt 0xC6A250 and
// Terrain_GetHeight 0xC6A540. back = the next instruction; skip = the query's own path for "no bridge here", the target of its
// je after 0xB793F0 (0xC61318, 0xC6A448, 0xC6A6E2), where esp is the same (0xB793F0 is ret 1Ch, 0xB745A0 ret 0Ch).
static const DWORD BH_CALL = 0x00B745A0;
static const DWORD BH_SITE[3] = {0x00C6133B, 0x00C6A46B, 0x00C6A709};
static const DWORD BH_BACK[3] = {0x00C61342, 0x00C6A472, 0x00C6A710};
static const DWORD BH_SKIP[3] = {0x00C6137B, 0x00C6A4B3, 0x00C6A772};
static const u8 kBhSite[3][7] = {
    {0x8B, 0x4E, 0x54, 0xD9, 0x5C, 0x24, 0x14},  // mov ecx,[esi+54h]; fstp dword ptr [esp+14h]
    {0x8B, 0x4B, 0x54, 0xD9, 0x5C, 0x24, 0x14},  // mov ecx,[ebx+54h]; fstp dword ptr [esp+14h]
    {0x8B, 0x4E, 0x54, 0xD9, 0x5C, 0x24, 0x20},  // mov ecx,[esi+54h]; fstp dword ptr [esp+20h]
};
static DWORD g_bhBack[3], g_bhSkip[3];
static volatile LONG g_bhLogged;

// site = 0..2; bits = the bridge height as a float; xz = the query's x, z arguments.
static void __stdcall BhHit(DWORD site, DWORD bits, const float* xz) {
    InterlockedIncrement(&ts3native_counters.bhnHits);
    const LONG n = InterlockedIncrement(&g_bhLogged);
    if (n > 8) return;
    static const char* const kFn[3] = {"0xC61200", "World_GetHeightAt 0xC6A250", "Terrain_GetHeight 0xC6A540"};
    float h, x = 0, z = 0;
    memcpy(&h, &bits, 4);
    __try { x = xz[0], z = xz[1]; } __except (EXCEPTION_EXECUTE_HANDLER) {}  // the query's [ebp+8]: logged only
    Log("ea fix: BridgeHeightNaN: the bridge height at (%g, %g) is 0x%08lX (%g); %s used the terrain height instead (hit %ld, "
        "docs/67 §37)", x, z, bits, h, kFn[site], n);
}

// Flags for `ja`: above when st0 (the bridge height) is not finite or its magnitude is above 1e6 (bits 0x49742400; with the
// sign cleared, float bits order like magnitudes, and NaN and +-inf sit above every finite value). st0 is kept.
static __declspec(naked) void BhTest() {
    __asm {
        push eax
        sub esp, 4
        fst dword ptr [esp]
        mov eax, [esp]
        and eax, 0x7FFFFFFF
        cmp eax, 0x49742400
        lea esp, [esp + 4]
        pop eax
        ret
    }
}

// The stubs' bad path: [esp+4] = site. Pops st0 (the x87 stack is then as on the game's no-bridge path), counts and logs.
// General registers are kept; xmm0-7 are not needed (the site follows a call, and every skip target writes them before use).
// x, z come from the query's frame: all three keep it in ebp (x [ebp+8], z [ebp+0Ch]).
static __declspec(naked) void BhDrop() {
    __asm {
        sub esp, 4
        fstp dword ptr [esp]
        pushad
        lea eax, [ebp + 8]
        push eax                   // xz
        push dword ptr [esp + 36]  // bits (the fstp slot)
        push dword ptr [esp + 48]  // site
        call BhHit
        popad
        lea esp, [esp + 4]
        ret 4
    }
}

static __declspec(naked) void BhStub0() {  // 0xC61200
    __asm {
        call BhTest
        ja bad
        mov ecx, [esi + 0x54]
        fstp dword ptr [esp + 0x14]
        jmp dword ptr [g_bhBack]
    bad:
        push 0
        call BhDrop
        jmp dword ptr [g_bhSkip]
    }
}
static __declspec(naked) void BhStub1() {  // World_GetHeightAt 0xC6A250
    __asm {
        call BhTest
        ja bad
        mov ecx, [ebx + 0x54]
        fstp dword ptr [esp + 0x14]
        jmp dword ptr [g_bhBack + 4]
    bad:
        push 1
        call BhDrop
        jmp dword ptr [g_bhSkip + 4]
    }
}
static __declspec(naked) void BhStub2() {  // Terrain_GetHeight 0xC6A540
    __asm {
        call BhTest
        ja bad
        mov ecx, [esi + 0x54]
        fstp dword ptr [esp + 0x20]
        jmp dword ptr [g_bhBack + 8]
    bad:
        push 2
        call BhDrop
        jmp dword ptr [g_bhSkip + 8]
    }
}
static void (*const kBhStub[3])() = {BhStub0, BhStub1, BhStub2};

// The 7 bytes at `at` (right after a call to 0xB745A0) become jmp kBhStub[i] + 2 int3 (never run: the stub jumps past them).
static bool BhPatch(u8* at, int i) {
    if (at[-5] != 0xE8 || (DWORD)((uintptr_t)at + *(const LONG*)(at - 4)) != BH_CALL || !Matches(at, kBhSite[i], 7)) return false;
    u8 b[7] = {0xE9, 0, 0, 0, 0, 0xCC, 0xCC};
    *(LONG*)(b + 1) = (LONG)((uintptr_t)kBhStub[i] - ((uintptr_t)at + 5));
    return Write(at, b, sizeof(b));
}

static bool ApplyBridgeHeightNaN(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"EaCrashFixes", L"BridgeHeightNaN", 0, ini)) return false;
    memcpy(g_bhBack, BH_BACK, sizeof(g_bhBack)), memcpy(g_bhSkip, BH_SKIP, sizeof(g_bhSkip));
    int n = 0;
    for (int i = 0; i < 3; i++) {  // each query is patched on its own
        if (BhPatch((u8*)(uintptr_t)BH_SITE[i], i)) n++;
        else Log("SKIP ea fix: BridgeHeightNaN: bytes at 0x%08X differ (another mod or a probe build owns them): that query not patched", BH_SITE[i]);
    }
    if (!n) return false;
    Log("OK   ea fix: BridgeHeightNaN (docs/67 §37): bridge heights that are not finite or above 1e6 in magnitude are ignored "
        "(%d of 3 height queries: 0x%08X, 0x%08X, 0x%08X)", n, BH_SITE[0], BH_SITE[1], BH_SITE[2]);
    return true;  // keep the log open for the hit lines
}
