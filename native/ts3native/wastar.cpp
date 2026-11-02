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
