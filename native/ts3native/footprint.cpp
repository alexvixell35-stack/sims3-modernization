// Footprint owner index for the save (docs/25 option 1). Included by ts3native.cpp (one translation unit).
//
// LotLevel_SaveFootprints 0x6663C0 (thiscall, this = level graph L) writes, per floor level of a lot, the routing footprint
// entries that L owns. To find them it walks the whole footprint map of the routing world's path planner
// ([[L+0x20]+0x24], EASTL hash map: +4 buckets, +8 bucket count, +0xC element count; node +8 = entry, +0x10 = next;
// entry +4 = refcount, +0xC = owner) and, per entry, bumps the refcount, compares the owner and drops the refcount again.
// That is levels x entries per save (~1.2 s on a Bridgeport save, docs/25).
//
// We leave the game's writer untouched and only change which map it walks: at 0x666488 the two loads of the map pointer
// (`mov edx,[ebx+0x20]` / `mov ecx,[edx+0x24]`) become a call to FpScanStub, which returns a one-bucket map whose chain
// holds exactly L's entries in the real map's iteration order. The writer then runs its own code over that list, so the
// bytes written, the save indices it assigns (0x62B1E0) and the refcount traffic on L's entries are the same; the other
// entries are no longer touched (their bump and drop is net zero; an entry at refcount <= 0, where the drop would destroy
// it, makes us fall back to the real map).
//
// Staleness: the index is per lot. The call RoutingWorld_SaveLot -> LotRouting_SaveLevels (0x82C940) bumps a generation,
// so the index is rebuilt at the first level of each lot, after the lot's own routing rebuild (0x82B810 runs inside
// LotRouting_SaveLevels before its level loop) and after anything between lots (script save calls, imposters). Within one
// lot's level loop only the writer runs. Each level also checks the map header (map, buckets, bucket count, element count)
// and rebuilds on any change. Verify=1 walks the real map for every level and compares; a mismatch uses the real map.
// Saving: one map walk per lot instead of one per level (and no refcount writes on other lots' entries).
//
// Runtime switch: exported ts3native_footprintMode (0 = the game's scan, 1 = index), start value [FootprintIndex] Mode.
// One log line per save from the World_Save call 0xAAC320.

static const DWORD FP_FN_LO = TS3A(0x006663C0), FP_FN_HI = TS3A(0x00666740);  // LotLevel_SaveFootprints body
#ifdef TS3A_TABLE
static const DWORD FP_FN_FNV = 0xCA5029BC;  // EA 1.69.47: same code, moved (docs/232 "FootprintIndex on 1.69")
#else
static const DWORD FP_FN_FNV = 0xF2CDC1C1;
#endif
static const DWORD FP_SCAN_SITE = TS3A(0x00666488);
static const u8 kFpScanBytes[6] = {0x8B, 0x53, 0x20, 0x8B, 0x4A, 0x24};  // mov edx,[ebx+0x20] ; mov ecx,[edx+0x24]
static const DWORD FP_LOT_SITE = TS3A(0x0082C940), FP_LOT = TS3A(0x0063C690);      // RoutingWorld_SaveLot -> LotRouting_SaveLevels
static const DWORD FP_SAVE_SITE = TS3A(0x00AAC320), FP_SAVE = TS3A(0x00C6D460);    // Save_WritePackage -> World_Save (thiscall, ret 8)

extern "C" __declspec(dllexport) volatile LONG ts3native_footprintMode = 0;

struct FpNode { DWORD k0, k1; const u8* value; DWORD k3; FpNode* next; };  // value at +8, next at +0x10
struct FpMap { DWORD pad; FpNode** buckets; DWORD bucketCount; DWORD elems; };

static bool g_fpVerify;
static DWORD g_fpGen = 1, g_fpBuiltGen;  // bumped per lot by FpLotThunk
static DWORD g_fpLotOrig = FP_LOT, g_fpSaveOrig = FP_SAVE, g_fpSaveRet;
static const FpMap* g_fpSnapMap;
static FpMap g_fpSnap;   // header of the map the index was built from
static bool g_fpBad;     // index unusable for this generation (refcount <= 0, fault, out of memory)

// index: entries in map order, chained per owner through g_fpNext; owner table = open addressing
static const u8** g_fpVals;
static DWORD* g_fpNext;
static DWORD g_fpCap, g_fpCount;
struct FpOwner { DWORD owner, head, tail, count; };
static FpOwner* g_fpOwners;
static DWORD g_fpOwnerCap, g_fpOwnerCount;
// the fake map handed to the writer
static FpNode* g_fpNodes;
static DWORD g_fpNodeCap;
static FpNode g_fpSentinel;
static FpNode* g_fpBuckets[2];
static FpMap g_fpFake;

struct FpStats { LONG levels, builds, matched, mismatches, fallbacks, entries; LONG64 us, saveUs; };
static FpStats g_fs;
static LARGE_INTEGER g_fpQpf;

static DWORD FpFnv(DWORD lo, DWORD hi) {
    DWORD h = 0x811C9DC5;
    for (const u8* p = (const u8*)(uintptr_t)lo; p < (const u8*)(uintptr_t)hi; p++) h = (h ^ *p) * 0x01000193;
    return h;
}

static FpOwner* FpFind(DWORD owner, bool add) {
    DWORD i = (owner >> 4) * 0x9E3779B1u & (g_fpOwnerCap - 1);
    for (;; i = (i + 1) & (g_fpOwnerCap - 1)) {
        FpOwner* o = &g_fpOwners[i];
        if (o->owner == owner && o->count) return o;
        if (!o->count) {
            if (!add) return NULL;
            o->owner = owner;
            g_fpOwnerCount++;
            return o;
        }
    }
}

static bool FpOwnersInit(DWORD cap) {  // cap: power of two
    FpOwner* t = (FpOwner*)calloc(cap, sizeof(FpOwner));
    if (!t) return false;
    free(g_fpOwners);
    g_fpOwners = t;
    g_fpOwnerCap = cap;
    g_fpOwnerCount = 0;
    return true;
}

static bool FpReserve(DWORD n) {
    if (n <= g_fpCap) return true;
    DWORD cap = g_fpCap ? g_fpCap : 4096;
    while (cap < n) cap *= 2;
    const u8** v = (const u8**)realloc((void*)g_fpVals, cap * sizeof(*v));
    if (!v) return false;
    g_fpVals = v;
    DWORD* nx = (DWORD*)realloc(g_fpNext, cap * sizeof(*nx));
    if (!nx) return false;
    g_fpNext = nx;
    g_fpCap = cap;
    return true;
}

// One walk over the real map in the writer's order (bucket 0..count-1, chain by +0x10), grouped by owner.
static bool FpBuildWalk(const FpMap* m) {
    FpNode** b = m->buckets;
    const DWORD n = m->bucketCount;
    for (DWORD i = 0; i < n; i++) {
        for (const FpNode* node = b[i]; node; node = node->next) {
            const u8* v = node->value;
            if (!v) continue;  // the writer skips null entries too
            if (*(const LONG*)(v + 4) <= 0) return false;  // the writer's drop would destroy it: keep the game's scan
            if (g_fpCount == g_fpCap && !FpReserve(g_fpCount + 1)) return false;
            if (2 * (g_fpOwnerCount + 1) > g_fpOwnerCap) return false;  // caller retries with a larger table
            const DWORD owner = *(const DWORD*)(v + 0xC);
            FpOwner* o = FpFind(owner, true);
            const DWORD k = g_fpCount++;
            g_fpVals[k] = v;
            g_fpNext[k] = 0xFFFFFFFF;
            if (o->count++) g_fpNext[o->tail] = k; else o->head = k;
            o->tail = k;
        }
    }
    return true;
}

static bool FpBuild(const FpMap* m) {
    if (!Readable(m, sizeof(FpMap)) || !m->buckets || !Readable(m->buckets, (m->bucketCount + 1) * 4) || !m->buckets[m->bucketCount])
        return false;
    if (!FpReserve(m->elems + 16)) return false;
    DWORD cap = 1024;
    while (cap < 4096 && cap < 2 * (m->elems + 1)) cap *= 2;
    for (int attempt = 0; attempt < 8; attempt++, cap *= 4) {
        if (!FpOwnersInit(cap)) return false;
        g_fpCount = 0;
        bool ok = false, full = false;
        __try {
            ok = FpBuildWalk(m);
            full = !ok && 2 * (g_fpOwnerCount + 1) > g_fpOwnerCap;
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            return false;
        }
        if (ok) return true;
        if (!full) return false;
    }
    return false;
}

// Verify: the real map's entries owned by L, in order, must equal L's chain.
static bool FpVerifyLevel(const FpMap* m, DWORD L, const FpOwner* o) {
    DWORD k = o ? o->head : 0xFFFFFFFF, seen = 0;
    __try {
        for (DWORD i = 0; i < m->bucketCount; i++)
            for (const FpNode* node = m->buckets[i]; node; node = node->next) {
                const u8* v = node->value;
                if (!v || *(const DWORD*)(v + 0xC) != L) continue;
                if (k == 0xFFFFFFFF || g_fpVals[k] != v) return false;
                k = g_fpNext[k];
                seen++;
            }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
    return k == 0xFFFFFFFF && seen == (o ? o->count : 0);
}

static bool FpSameHeader(const FpMap* m) {
    return m == g_fpSnapMap && m->buckets == g_fpSnap.buckets && m->bucketCount == g_fpSnap.bucketCount && m->elems == g_fpSnap.elems;
}

static const FpMap* FpSelectInner(DWORD L, const FpMap* real) {
    g_fs.levels++;
    if (Readable(real, sizeof(FpMap)) && (LONG)real->elems > g_fs.entries) g_fs.entries = (LONG)real->elems;
    if (ts3native_footprintMode != 1) return real;
    if (g_fpBuiltGen != g_fpGen || !Readable(real, sizeof(FpMap)) || !FpSameHeader(real)) {
        g_fpBuiltGen = g_fpGen;
        g_fpSnapMap = real;
        g_fpBad = !FpBuild(real);
        if (!g_fpBad) g_fpSnap = *real;
        g_fs.builds++;
    }
    if (g_fpBad || !FpSameHeader(real)) { g_fs.fallbacks++; return real; }
    const FpOwner* o = FpFind(L, false);
    if (g_fpVerify && !FpVerifyLevel(real, L, o)) { g_fs.mismatches++; return real; }
    const DWORD n = o ? o->count : 0;
    if (n > g_fpNodeCap) {
        FpNode* nodes = (FpNode*)realloc(g_fpNodes, n * 2 * sizeof(FpNode));
        if (!nodes) { g_fs.fallbacks++; return real; }
        g_fpNodes = nodes;
        g_fpNodeCap = n * 2;
    }
    DWORD k = n ? o->head : 0xFFFFFFFF;
    for (DWORD i = 0; i < n; i++, k = g_fpNext[k]) {
        g_fpNodes[i].value = g_fpVals[k];
        g_fpNodes[i].next = i + 1 < n ? &g_fpNodes[i + 1] : NULL;
    }
    g_fs.matched += (LONG)n;
    g_fpBuckets[0] = n ? g_fpNodes : NULL;
    g_fpBuckets[1] = &g_fpSentinel;  // non-null end marker, as EASTL's bucket array has
    g_fpFake.buckets = g_fpBuckets;
    g_fpFake.bucketCount = 1;
    g_fpFake.elems = n;
    return &g_fpFake;
}

static const FpMap* __cdecl FpSelect(DWORD L, const FpMap* real) {
    LARGE_INTEGER t0, t1;
    QueryPerformanceCounter(&t0);
    const FpMap* r = FpSelectInner(L, real);
    QueryPerformanceCounter(&t1);
    g_fs.us += (t1.QuadPart - t0.QuadPart) * 1000000 / (g_fpQpf.QuadPart ? g_fpQpf.QuadPart : 1);
    return r;
}

// 0x666488: replaces `mov edx,[ebx+0x20] ; mov ecx,[edx+0x24]` (6 bytes) with `call FpScanStub ; nop`. ebx = L. The
// next instructions overwrite eax and read only ecx (the map), edx is reloaded as the original left it.
static __declspec(naked) void FpScanStub() {
    __asm {
        mov edx, [ebx + 0x20]
        mov ecx, [edx + 0x24]
        push ecx
        push ebx
        call FpSelect
        add esp, 8
        mov ecx, eax
        mov edx, [ebx + 0x20]
        ret
    }
}

// 0x82C940: call LotRouting_SaveLevels -> new generation per lot, then the game's function with the same stack and ecx.
static __declspec(naked) void FpLotThunk() {
    __asm {
        inc dword ptr [g_fpGen]
        jmp dword ptr [g_fpLotOrig]
    }
}

static void __cdecl FpSaveBegin() {
    memset(&g_fs, 0, sizeof(g_fs));
    g_fpGen++;
}

static void __cdecl FpSaveEnd(DWORD us) {
    g_fpGen++;  // drop the index: the next save rebuilds
    g_fs.saveUs = us;
    char v[48];
    if (!g_fpVerify || ts3native_footprintMode != 1) sprintf_s(v, "off");
    else if (g_fs.mismatches) sprintf_s(v, "mismatches %ld", g_fs.mismatches);
    else sprintf_s(v, "ok");
    Log("footprint: levels %ld entries %ld matched %ld calls %ld ms %lld verify %s (mode %ld fallbacks %ld worldSave ms %lld)",
        g_fs.levels, g_fs.entries, g_fs.matched, g_fs.builds, g_fs.us / 1000, v, ts3native_footprintMode, g_fs.fallbacks,
        g_fs.saveUs / 1000);
}

static LARGE_INTEGER g_fpSaveT0;
static void __cdecl FpSaveBeginT() {
    QueryPerformanceCounter(&g_fpSaveT0);
    FpSaveBegin();
}
static void __cdecl FpSaveEndT() {
    LARGE_INTEGER t1;
    QueryPerformanceCounter(&t1);
    FpSaveEnd((DWORD)((t1.QuadPart - g_fpSaveT0.QuadPart) * 1000000 / (g_fpQpf.QuadPart ? g_fpQpf.QuadPart : 1)));
}

// 0xAAC320: call World_Save (thiscall, 2 stack args, ret 8). Saves are serial on the sim thread, so one return slot.
static __declspec(naked) void FpSaveThunk() {
    __asm {
        pop eax
        mov g_fpSaveRet, eax
        push ecx
        call FpSaveBeginT
        pop ecx
        call dword ptr [g_fpSaveOrig]
        push eax
        call FpSaveEndT
        pop eax
        jmp dword ptr [g_fpSaveRet]
    }
}

static bool FpCallOk(DWORD site, DWORD target) {
    const u8* p = (const u8*)(uintptr_t)site;
    return Readable(p, 5) && p[0] == 0xE8 && (DWORD)(site + 5 + *(const LONG*)(p + 1)) == target;
}

static bool FpRedirect(DWORD site, const void* hook) {
    const LONG rel = (LONG)((uintptr_t)hook - (site + 5));
    return Write((u8*)(uintptr_t)site + 1, &rel, 4);
}

// [FootprintIndex] Enabled=1 hooks; Mode = start value of ts3native_footprintMode; Verify=1 compares every level with
// the real map. Returns true when hooked (the log stays open for the per-save lines).
static bool ApplyFootprintIndex(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"FootprintIndex", L"Enabled", 0, ini)) { Log("footprint index disabled (Enabled=0)"); return false; }
    if (ApexStandsDown(AXF_MEMORY_GUARD, "[FootprintIndex]", "call 0xAAC320")) return false;
    const int mode = GetPrivateProfileIntW(L"FootprintIndex", L"Mode", 1, ini);
    ts3native_footprintMode = mode == 0 ? 0 : 1;
    g_fpVerify = GetPrivateProfileIntW(L"FootprintIndex", L"Verify", 0, ini) != 0;
    QueryPerformanceFrequency(&g_fpQpf);
    if (!Readable((void*)(uintptr_t)FP_FN_LO, FP_FN_HI - FP_FN_LO) || FpFnv(FP_FN_LO, FP_FN_HI) != FP_FN_FNV) {
        Log("SKIP footprint index: 0x%08X..0x%08X differs from Steam 1.67, nothing written", FP_FN_LO, FP_FN_HI);
        return false;
    }
    if (!Matches((void*)(uintptr_t)FP_SCAN_SITE, kFpScanBytes, sizeof(kFpScanBytes)) || !FpCallOk(FP_LOT_SITE, FP_LOT) ||
        !FpCallOk(FP_SAVE_SITE, FP_SAVE)) {
        Log("SKIP footprint index: bytes at 0x%08X / 0x%08X / 0x%08X differ, nothing written", FP_SCAN_SITE, FP_LOT_SITE, FP_SAVE_SITE);
        return false;
    }
    // the lot generation and the save line first; the scan site last, so the index never runs without its invalidation
    if (!FpRedirect(FP_LOT_SITE, (const void*)FpLotThunk) || !FpRedirect(FP_SAVE_SITE, (const void*)FpSaveThunk)) {
        Log("FAIL footprint index: VirtualProtect (err %lu)", GetLastError());
        return false;  // shortcut: a site already redirected only bumps a counter or logs, then calls the game's function
    }
    u8 call[6] = {0xE8, 0, 0, 0, 0, 0x90};
    const LONG rel = (LONG)((uintptr_t)FpScanStub - (FP_SCAN_SITE + 5));
    memcpy(call + 1, &rel, 4);
    if (!Write((void*)(uintptr_t)FP_SCAN_SITE, call, sizeof(call))) {
        Log("FAIL footprint index: VirtualProtect at 0x%08X (err %lu)", FP_SCAN_SITE, GetLastError());
        return false;
    }
    Log("OK   footprint index: 0x%08X, 0x%08X, 0x%08X (Mode=%ld Verify=%d)", FP_SCAN_SITE, FP_LOT_SITE, FP_SAVE_SITE,
        ts3native_footprintMode, g_fpVerify ? 1 : 0);
    return true;
}
