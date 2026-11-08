// TestAutShadow (autshadow.cpp, docs/122 §6.1 T1, T2, T4; gate G-A1). Included by selftest.cpp. A fake provider table
// in this image stands for the exe's: fake IC bodies with the real names and the cdecl layouts of docs/122 §13.

// the three object-event kinds the tests drive (docs/122 §14.19: one handler per event id): a plain change (transform 0x0E),
// an add (4), a destroy (5)
static char __cdecl AsObjEvDirty(void* obj, void* d) { return AsObjEv<5>(obj, d); }
static char __cdecl AsObjEvList(void* obj, void* d) { return AsObjEv<0>(obj, d); }
static char __cdecl AsObjEvGone(void* obj, void* d) { return AsObjEv<1>(obj, d); }

struct AsFakeObj { DWORD vt, pad; AsU64 id; float local[3]; AsU64 parent; DWORD hidden; DWORD lot; };  // id at +8
static AsFakeObj g_afo[65];        // id = 5:i, i = 1..64; a parent always has a lower index
static float g_afSunrise = 6.0f, g_afSea = 1.25f;
static DWORD g_afArena[1 << 16];   // fake MonoArrays made by the bodies (ring)
static DWORD g_afArenaAt;
static int g_afRefreshProbe;

static AsFakeObj* AfFind(DWORD lo, DWORD hi) { return hi == 5 && lo >= 1 && lo <= 64 ? &g_afo[lo] : NULL; }
static void AfWorld(const AsFakeObj* o, float* p) {
    p[0] = p[1] = p[2] = 0;
    for (; o; o = o->parent ? AfFind((DWORD)o->parent, (DWORD)(o->parent >> 32)) : NULL)
        for (int i = 0; i < 3; i++) p[i] += o->local[i];
}
static float* __cdecl AfPos(float* ret, DWORD lo, DWORD hi) { AfWorld(AfFind(lo, hi), ret); return ret; }
static AsU64 __cdecl AfParent(DWORD lo, DWORD hi) { const AsFakeObj* o = AfFind(lo, hi); return o ? o->parent : 0; }
static DWORD __cdecl AfHidden(DWORD lo, DWORD hi) { const AsFakeObj* o = AfFind(lo, hi); return o ? o->hidden : 0; }
static short __cdecl AfTomb(DWORD lo, DWORD) { g_afRefreshProbe++; return (short)-(int)(lo & 7); }  // negative: the mask matters
static float __cdecl AfSunrise() { return g_afSunrise; }
static float __cdecl AfSea() { return g_afSea; }
static bool g_afIsSim[65];  // T10 (docs/122 §14.18): list type 5 is Sim-free (its bodies skip these), every other type keeps them
static DWORD g_afCls[65];   // T12 (docs/122 §14.20): list types 8 and 9 hold only objects with bit (type - 8) set
static bool AfTypeHas(DWORD type, int i) { return !(type == 5 && g_afIsSim[i]) && !(type >= 8 && !(g_afCls[i] >> (type - 8) & 1)); }
static DWORD __cdecl AfCount(DWORD type, DWORD lotLo, DWORD, DWORD flags) {
    DWORD n = 0;
    for (int i = 1; i <= 64; i++) n += g_afo[i].lot == lotLo && (!flags || i % 2 == 0) && AfTypeHas(type, i);
    return n * 16 + (type & 15);
}
static DWORD* __cdecl AfOnLot(DWORD type, DWORD lotLo, DWORD, DWORD flags) {
    if (lotLo == 0) return NULL;
    if (g_afArenaAt + 80 > (1 << 16)) g_afArenaAt = 0;
    DWORD* a = g_afArena + g_afArenaAt;
    DWORD n = 0;
    for (int i = 1; i <= 64; i++) if (g_afo[i].lot == lotLo && (!flags || i % 2 == 0) && AfTypeHas(type, i)) a[4 + n++] = i;
    a[0] = 0xA0000 + type, a[1] = 0, a[2] = n, a[3] = 0;
    g_afArenaAt += 4 + n;
    return a;
}
static AsU64 __cdecl AfLotLoc(float x, float, float z, DWORD* out, DWORD room) {
    out[0] = (DWORD)(int)x, out[1] = (DWORD)(int)z, out[2] = room;
    return 0x700000000ull + (DWORD)(int)x / 10;
}
// EA's MonoString {vtable, length, UTF-16 chars at +8} (mono_string_new_size 0xEA3FB3, docs/67): no sync word
struct AsFakeStr { DWORD vt; LONG len; wchar_t c[16]; };
// like 0x741230: the out is written only when the option exists (odd length here)
static char __cdecl AfOption(const AsFakeStr* s, DWORD* out) { if (s->len & 1) *out = (DWORD)s->len * 3 + s->c[0]; return (char)(s->len & 1); }
// fake MT (stands for 0x4FA990 / 0x4FAA70: same this-relative thiscall contract, a simpler generator)
static void __fastcall AfMtInit(void* mt, void*, DWORD seed) {
    DWORD* w = (DWORD*)mt;
    w[0] = seed | 1, w[0x270] = (DWORD)(uintptr_t)mt, w[0x271] = 0x270;
}
static double __fastcall AfMtNext(void* mt, void*) {
    DWORD* w = (DWORD*)mt;
    w[0] = w[0] * 1664525u + 1013904223u;
    return (w[0] >> 8) / 16777216.0;
}
static u8 g_afDet;
static int g_afRandMt;  // T5: the fake body draws from the host MT as the game's does
static double __cdecl AfRand() { return g_afRandMt ? AfMtNext(*g_asHostPtr + 0xC30, NULL) : 0.25; }
// docs/125: slot position (handle, slot hash, age/gender/species, exit) and Query_GetObjects(Type), no change events
static float g_afSlotBias;
static float* __cdecl AfSlotPos(float* ret, DWORD lo, DWORD hi, DWORD slot, DWORD ags, DWORD exitSlot) {
    AfWorld(AfFind(lo, hi), ret);
    ret[1] += (float)(slot & 15) + (float)ags * 0.5f + g_afSlotBias + (exitSlot ? 100.0f : 0.0f);
    return ret;
}
static DWORD* __cdecl AfQuery(DWORD type) {  // type 9: a world-wide type, 300 objects (over the keep cap)
    const DWORD n = type == 9 ? 300 : 0;
    if (g_afArenaAt + 4 + 64 + n > (1 << 16)) g_afArenaAt = 0;
    DWORD* a = g_afArena + g_afArenaAt;
    DWORD m = 0;
    if (n) for (; m < n; m++) a[4 + m] = 0x900 + m;
    else for (int i = 1; i <= 64; i++) if (g_afo[i].hidden == (type & 1)) a[4 + m++] = i;
    a[0] = 0xB0000 + type, a[1] = 0, a[2] = m, a[3] = 0;
    g_afArenaAt += 4 + m;
    return a;
}
// docs/127 §7.1: Query_GetGlobalObjects(Type): NULL when nothing matches (type 2, the v0 Callbox case), else the objects
// on lot 1 (type 4: changes with the objects' lots, no list event needed: AO_CUT)
static DWORD* __cdecl AfGlobal(DWORD type) {
    if (type != 4) return NULL;
    if (g_afArenaAt + 4 + 64 > (1 << 16)) g_afArenaAt = 0;
    DWORD* a = g_afArena + g_afArenaAt;
    DWORD m = 0;
    for (int i = 1; i <= 64; i++) if (g_afo[i].lot == 1) a[4 + m++] = i;
    a[0] = 0xC0000 + type, a[1] = 0, a[2] = m, a[3] = 0;
    g_afArenaAt += 4 + m;
    return a;
}
static volatile LONG g_afRouteBody;  // H12: a route IC on the worker never runs its body
static DWORD __cdecl AfRoute(DWORD, DWORD) { InterlockedIncrement(&g_afRouteBody); return 1; }
static float __cdecl AfTerrain(float x, float) { return x + 0.5f; }  // unlisted 'f'
static float* __cdecl AfCamPos(float* ret) { ret[0] = ret[1] = ret[2] = 9; return ret; }  // unlisted 's'
static DWORD __cdecl AfUnlisted(DWORD a) { return a + 1; }  // unlisted int
static void* __cdecl AfMoney(DWORD, DWORD) { return (void*)(uintptr_t)0x1234; }  // a-stub IC

static void* g_asFakeTab[] = {
    (void*)AfPos, (void*)"ScriptCore.Objects::Objects_GetPosition",
    (void*)AfParent, (void*)"ScriptCore.Slots::Slots_GetParentIdImpl",
    (void*)AfHidden, (void*)"ScriptCore.World::World_ObjectGetHiddenFlags",
    (void*)AfTomb, (void*)"ScriptCore.World::World_GetObjectTombRoomIdImpl",
    (void*)AfSunrise, (void*)"ScriptCore.World::World_GetSunriseTime",
    (void*)AfSea, (void*)"ScriptCore.World::World_GetSeaLevelImpl",
    (void*)AfCount, (void*)"ScriptCore.Queries::Query_CountObjectsOnLot",
    (void*)AfOnLot, (void*)"ScriptCore.Queries::Query_GetObjectsOnLot",
    (void*)AfLotLoc, (void*)"ScriptCore.World::World_GetLotLocationImpl",
    (void*)AfOption, (void*)"ScriptCore.DeviceConfig::DeviceConfig_GetOption",
    (void*)AfRand, (void*)"ScriptCore.Random::Random_NextDoubleImpl",
    (void*)AfTerrain, (void*)"ScriptCore.World::World_GetTerrainHeightImpl",
    (void*)AfCamPos, (void*)"ScriptCore.CameraController::Camera_GetPosition",
    (void*)AfUnlisted, (void*)"ScriptCore.Fake::Fake_UnlistedImpl",
    (void*)AfMoney, (void*)"ScriptCore.EAText::EAText_GetMoneyString",
    (void*)0x12345678, (void*)"ScriptCore.Fake::Fake_NotCode",  // fn outside .text: not a table entry
    (void*)AfSlotPos, (void*)"ScriptCore.Slots::Slots_GetPositionOfSlotImpl",
    (void*)AfQuery, (void*)"ScriptCore.Queries::Query_GetObjects",
    (void*)AfGlobal, (void*)"ScriptCore.Queries::Query_GetGlobalObjects",
    (void*)AfRoute, (void*)"ScriptCore.Route::Route_IsPointRoutable",
    NULL, NULL};
enum { AF_POS, AF_PARENT, AF_HIDDEN, AF_TOMB, AF_SUNRISE, AF_SEA, AF_COUNT, AF_ONLOT, AF_LOTLOC, AF_OPTION, AF_RAND,
       AF_TERRAIN, AF_CAMPOS, AF_UNLISTED, AF_MONEY, AF_NOTCODE, AF_SLOTPOS, AF_QUERY, AF_GLOBAL, AF_ROUTE };
typedef float*(__cdecl* AfSlotPosFn)(float*, DWORD, DWORD, DWORD, DWORD, DWORD);
typedef DWORD*(__cdecl* AfQueryFn)(DWORD);
static void* AfSlot(int i) { return g_asFakeTab[i * 2]; }

static DWORD g_afNewArena[1 << 14];
static DWORD g_afNewAt;
static void* __cdecl AfNewArray(DWORD vtable, DWORD n, DWORD elemBytes) {  // stands for part 2's allocator
    const DWORD w = 4 + n * (elemBytes / 4);
    if (g_afNewAt + w > (1 << 14)) g_afNewAt = 0;
    DWORD* a = g_afNewArena + g_afNewAt;
    a[0] = vtable, a[2] = n;
    g_afNewAt += w;
    return a;
}

static int AsFpTop() {
    unsigned short sw;
    __asm { fnstsw sw }
    return (sw >> 11) & 7;
}

static DWORD g_afObjHeads[64];  // fake object-event lists: node {priority, fn, next}
static DWORD g_afNodes[64][3];
static int g_afNodeN;
static char __cdecl AfObjReg(DWORD id, void* fn, LONG prio) {
    DWORD* n = g_afNodes[g_afNodeN++];
    n[0] = (DWORD)prio, n[1] = (DWORD)(uintptr_t)fn, n[2] = g_afObjHeads[id];
    g_afObjHeads[id] = (DWORD)(uintptr_t)n;
    return 1;
}

static AsFakeStr AfStr(const wchar_t* s) {
    AsFakeStr r = {0x51, (LONG)wcslen(s), {0}};
    wcscpy_s(r.c, s);
    return r;
}

static u8 g_afFmt[16] = {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF8, 0x8B, 0x45, 0x0C, 0xC7, 0x00, 0x77, 0x00, 0x00, 0x00, 0x8B};  // + E5 5D C3
static u8* g_afFmtFn;

// the published version's answers equal fresh body calls (the A/B copy is complete)
static void AfCheckPublished() {
    const AsTab& t = g_as->v[g_asPub];
    LONG xn = 0;
    for (int i = 0; i < t.n; i++) {
        const AsDesc& d = *g_asDescOf[t.e[i].ic];
        if (d.owner == AO_MEMO || d.kind == AK_REF || (t.e[i].flags & AE_COLD)) continue;  // a cold row is never answered
        AsEnt x = t.e[i];
        AsRefresh(x.ic, x, g_as->poolTmp, &xn);
        assert(AsSame(d, t.e[i], t.pool, x, g_as->poolTmp));
        xn = 0;
    }
}

static AsU64 AfId(int i) { return 0x500000000ull + (DWORD)i; }

static DWORD WINAPI AfWorker(void*) {
    Ts3nAutShadow& k = ts3native_autshadow;
    g_asWorkerTid = GetCurrentThreadId();
    AsWorkerBegin();
    assert(g_asHeld == g_asPub && g_asHeld >= 0);
    // T1: snapshot answers through the guards, one per return kind
    float pos[3] = {-1, -1, -1}, want[3];
    AfWorld(&g_afo[3], want);
    float* rp = ((float*(__cdecl*)(float*, DWORD, DWORD))AfSlot(AF_POS))(pos, 3, 5);
    assert(rp == pos && !memcmp(pos, want, 12));
    assert(((AsU64(__cdecl*)(DWORD, DWORD))AfSlot(AF_PARENT))(3, 5) == g_afo[3].parent);
    assert(((DWORD(__cdecl*)(DWORD, DWORD))AfSlot(AF_HIDDEN))(3, 5) == g_afo[3].hidden);
    assert(((short(__cdecl*)(DWORD, DWORD))AfSlot(AF_TOMB))(5, 5) == -5);
    const int top = AsFpTop();
    for (int i = 0; i < 20; i++) {
        volatile float s = ((float(__cdecl*)())AfSlot(AF_SUNRISE))();
        assert(s == 6.0f);
        volatile float u = ((float(__cdecl*)(float, float))AfSlot(AF_TERRAIN))(1, 2);  // unlisted 'f': 0.0, abort ic
        assert(u == 0.0f);
        volatile double r = ((double(__cdecl*)())AfSlot(AF_RAND))();  // abort rng: 0.0
        assert(r == 0.0);
    }
    assert(AsFpTop() == top);  // x87 stack balanced on every path
    assert(AsAborted() == AS_AB_IC && k.abort[AS_AB_IC] >= 20 && k.abort[AS_AB_RNG] == 20);
    AsResetAbort();
    DWORD loc[3] = {0, 0, 0};
    assert(((AsU64(__cdecl*)(float, float, float, DWORD*, DWORD))AfSlot(AF_LOTLOC))(25.0f, 0, 7.0f, loc, 2) == 0x700000002ull);
    assert(loc[0] == 25 && loc[1] == 7 && loc[2] == 2);
    const DWORD* arr = ((DWORD*(__cdecl*)(DWORD, DWORD, DWORD, DWORD))AfSlot(AF_ONLOT))(3, 1, 0, 0);
    const DWORD* fresh = AfOnLot(3, 1, 0, 0);
    assert(arr && arr[0] == fresh[0] && arr[2] == fresh[2] && !memcmp(arr + 4, fresh + 4, fresh[2] * 4));
    assert(arr >= g_afNewArena && arr < g_afNewArena + (1 << 14));  // the worker's own array, not the sim's
    assert(((DWORD(__cdecl*)(DWORD, DWORD, DWORD, DWORD))AfSlot(AF_COUNT))(3, 1, 0, 0) == AfCount(3, 1, 0, 0));
    assert(AsAborted() == -1);
    // misses: unknown row (struct: memory untouched, eax = the result pointer), unknown list key -> learned
    float p2[3] = {7, 7, 7};
    assert(((float*(__cdecl*)(float*, DWORD, DWORD))AfSlot(AF_POS))(p2, 40, 5) == p2 && p2[0] == 7);
    assert(AsAborted() == AS_AB_ROW);
    assert(((DWORD(__cdecl*)(DWORD, DWORD, DWORD, DWORD))AfSlot(AF_COUNT))(3, 2, 0, 1) == 0);
    assert(AsAborted() == AS_AB_ROW);  // first reason wins
    AsResetAbort();
    {  // docs/216 §5 per-IC record: stop not counted, row counted twice; counters restored for the checks below
        const Ts3nAutShadow k0 = k;
        const int n0 = g_asIcN;
        if (g_asIcN < 3) g_asIcN = 3;
        const LONG a0 = g_asAbortIc[2], r0 = g_asAbortIcRow[2];
        AsAbort(AS_AB_ROW, 2), AsAbort(AS_AB_STOP, 2), AsAbort(AS_AB_MEMO, 2), AsAbort(AS_AB_ROW, (DWORD)-1);
        assert(g_asAbortIc[2] == a0 + 2 && g_asAbortIcRow[2] == r0 + 1);
        AsAbortIcLog();
        g_asAbortIc[2] = a0, g_asAbortIcRow[2] = r0, g_asIcN = n0;
        k = k0;
        AsResetAbort();
    }
    float c[3] = {1, 1, 1};
    assert(((float*(__cdecl*)(float*))AfSlot(AF_CAMPOS))(c) == c && c[0] == 1);  // unlisted 's'
    assert(((DWORD(__cdecl*)(DWORD))AfSlot(AF_UNLISTED))(5) == 0 && AsAborted() == AS_AB_IC);
    AsResetAbort();
    // memo: same content, other string object -> hit; other content -> abort memo (never learned)
    AsFakeStr s2 = AfStr(L"Shadows"), s3 = AfStr(L"Bloom");
    DWORD out = 0;
    assert(((char(__cdecl*)(const AsFakeStr*, DWORD*))AfSlot(AF_OPTION))(&s2, &out) == 1 && out == 7 * 3 + L'S');
    out = 99;
    assert(((char(__cdecl*)(const AsFakeStr*, DWORD*))AfSlot(AF_OPTION))(&s3, &out) == 0 && out == 99);
    assert(AsAborted() == AS_AB_MEMO);
    AsResetAbort();
    // memo with an unwritten out: same in-value -> the caller's own value stays; other in-value -> abort memo, untouched
    AsFakeStr s4 = AfStr(L"Glow");
    out = 42;
    assert(((char(__cdecl*)(const AsFakeStr*, DWORD*))AfSlot(AF_OPTION))(&s4, &out) == 0 && out == 42);
    assert(AsAborted() == -1);
    out = 5;
    assert(((char(__cdecl*)(const AsFakeStr*, DWORD*))AfSlot(AF_OPTION))(&s4, &out) == 0 && out == 5);
    assert(AsAborted() == AS_AB_MEMO);
    AsResetAbort();
    // a-stub: the shared empty string
    assert(((void*(__cdecl*)(DWORD, DWORD))AfSlot(AF_MONEY))(1, 0) == g_asEmptyStr);
    // docs/125: slot position and Query_GetObjects are learned keys: the first call misses (struct untouched, null array)
    float sp[3] = {3, 3, 3};
    assert(((AfSlotPosFn)AfSlot(AF_SLOTPOS))(sp, 3, 5, 2, 1, 0) == sp && sp[0] == 3 && AsAborted() == AS_AB_SLOTPOS);
    AsResetAbort();
    assert(!((AfQueryFn)AfSlot(AF_QUERY))(1) && AsAborted() == AS_AB_QUERY);
    AsResetAbort();
    assert(!((AfQueryFn)AfSlot(AF_QUERY))(9) && AsAborted() == AS_AB_QUERY);
    AsResetAbort();
    // docs/127 §7.1: Query_GetGlobalObjects keys are learned the same way (a NULL answer and a list)
    assert(!((AfQueryFn)AfSlot(AF_GLOBAL))(2) && AsAborted() == AS_AB_QUERY);
    AsResetAbort();
    assert(!((AfQueryFn)AfSlot(AF_GLOBAL))(4) && AsAborted() == AS_AB_QUERY);
    AsResetAbort();
    // H12 (docs/127 §7.2): a route IC aborts `route` with the neutral answer, never the body (handle 0 would answer false)
    const LONG routeAb = k.abortRoute, routeBody = g_afRouteBody;
    assert(((DWORD(__cdecl*)(DWORD, DWORD))AfSlot(AF_ROUTE))(0, 0) == 0 && AsAborted() == AS_AB_ROUTE);
    assert(k.abortRoute == routeAb + 1 && g_afRouteBody == routeBody);
    AsResetAbort();
    // T4: F2 at the fake ParseFormatSpecifier: abort fmt, exception out, body not run
    void* exc = NULL;
    ((void(__cdecl*)(DWORD, void**))g_afFmtFn)(0, &exc);
    assert(exc == g_asFmtExc && AsAborted() == AS_AB_FMT);
    AsResetAbort();
    // T4: stop from the sim: the next guard call abandons the run
    InterlockedExchange(&g_asStop, 1);
    assert(((AsU64(__cdecl*)(DWORD, DWORD))AfSlot(AF_PARENT))(3, 5) == 0 && AsAborted() == AS_AB_STOP);
    AsResetAbort();
    AsWorkerEnd();
    assert(g_asHeld == -1);
    return 0;
}

// docs/125: learned slot and query keys answer as the body does at the cut; a world-wide type over the cap aborts
static DWORD WINAPI AfWorker2(void*) {
    g_asWorkerTid = GetCurrentThreadId();
    AsWorkerBegin();
    float sp[3] = {0, 0, 0}, want[3];
    assert(((AfSlotPosFn)AfSlot(AF_SLOTPOS))(sp, 3, 5, 2, 1, 0) == sp && !memcmp(sp, AfSlotPos(want, 3, 5, 2, 1, 0), 12));
    const DWORD* q = ((AfQueryFn)AfSlot(AF_QUERY))(1);
    const DWORD* f = AfQuery(1);
    assert(q && q != f && q[0] == f[0] && q[2] == f[2] && f[2] > 0 && !memcmp(q + 4, f + 4, f[2] * 4));
    assert(AsAborted() == -1);
    assert(!((AfQueryFn)AfSlot(AF_QUERY))(9) && AsAborted() == AS_AB_QUERY);
    AsResetAbort();
    // docs/127 §7.1: the global query's NULL answer is kept (no abort), the list answers as the body at the cut
    assert(!((AfQueryFn)AfSlot(AF_GLOBAL))(2) && AsAborted() == -1);
    const DWORD* g = ((AfQueryFn)AfSlot(AF_GLOBAL))(4);
    const DWORD* gf = AfGlobal(4);
    assert(g && g != gf && g[0] == gf[0] && g[2] == gf[2] && gf[2] > 0 && !memcmp(g + 4, gf + 4, gf[2] * 4) && AsAborted() == -1);
    AsWorkerEnd();
    return 0;
}

// ---- T5 (part 2, docs/122 §14): request -> cut -> reference (RNG restored) -> worker -> finish on the M5 worker of
// TestMonoThreadM5 (its fakes: GC thread table, domain, tracking flag), plus overrun/stop, epoch drop, Mode 0, simstale,
// the rooted ranges, the IC registration and the JSONL log. Methods are fake ids handled by AfT5Invoke.
static float g_t5RefPos[3], g_t5WorkPos[3];
static volatile LONG g_t5Block, g_t5WorkRuns, g_t5WorkStale, g_t5Finish, g_t5Last = -9, g_t5SawStop;
static int g_t5Ics, g_t5Roots;
static u8 g_t5Host[0x1800];
static u8* g_t5HostPtr = g_t5Host;
static double g_t5R[4], g_t5W[3];  // docs/125: draws per unit in the reference and on the worker
static volatile LONG g_t5WNoUnit;
static DWORD WINAPI AfT5Release(void*) { Sleep(20); InterlockedExchange(&g_t5Block, 0); return 0; }
static double AfT5Draw() { return ((double(__cdecl*)())AfSlot(AF_RAND))(); }
static void __cdecl AfT5AddIc(const char* name, const void*) { g_t5Ics += !strncmp(name, "Ts3Fixes.AutShadowNative::", 26); }
static void __cdecl AfT5AddRoots(void* lo, void* hi) { g_t5Roots++; assert((u8*)hi - (u8*)lo >= (int)sizeof(DWORD) * AS_POOL); }
static void* __cdecl AfT5Malloc(size_t n) { return calloc(1, n); }
static void* __cdecl AfT5Invoke(void* method, void*, void**, void** exc) {
    *exc = NULL;
    float* pos = (uintptr_t)method == 2 ? g_t5RefPos : g_t5WorkPos;
    switch ((uintptr_t)method) {
        case 1: g_t5Last = AsIcLastRun(); InterlockedIncrement(&g_t5Finish); break;
        case 2: g_t5Host[0xC30 + 7] ^= 0x5A;  // the reference draws: the native side restores the MT state
        // fall through
        case 3:
            if ((uintptr_t)method == 3) {
                assert(AsIcIsWorker());
                for (int i = 0; i < 4000 && g_t5Block && !AsIcStopped(); i++) Sleep(1);
                if (AsIcStopped()) { InterlockedIncrement(&g_t5SawStop); break; }
            } else {
                assert(!AsIcIsWorker());
            }
            ((float*(__cdecl*)(float*, DWORD, DWORD))AfSlot(AF_POS))(pos, 5, 5);
            if (g_afRandMt && (uintptr_t)method == 2) {  // units 3 then 1; unit 1 draws once more (a tooltip-only draw)
                AsIcRngUnit(3);
                AsIcRngPause(1), AfT5Draw(), AsIcRngPause(0);  // addendum §4.1: a hooked call's draw before the unit's first
                g_t5R[0] = AfT5Draw(), g_t5R[1] = AfT5Draw();
                AsIcRngUnit(1);
                g_t5R[2] = AfT5Draw();
                AsIcRngPause(1), AsIcRngPause(1), AfT5Draw(), AsIcRngPause(0), AfT5Draw(), AsIcRngPause(0);  // nested, mid-unit
                g_t5R[3] = AfT5Draw();
            } else if (g_afRandMt) {  // worker: a draw outside any unit aborts; then units in the other order
                AsIcResetAbort();
                AfT5Draw();
                if (AsIcAborted() == AS_AB_RNG) InterlockedIncrement(&g_t5WNoUnit);
                AsIcResetAbort();
                AsIcRngUnit(1);
                g_t5W[2] = AfT5Draw();
                AsIcRngUnit(3);
                g_t5W[0] = AfT5Draw(), g_t5W[1] = AfT5Draw();
                assert(AsIcAborted() == -1);
            }
            if ((uintptr_t)method == 3) {
                AsIcResetAbort();
                float p7[3];
                ((float*(__cdecl*)(float*, DWORD, DWORD))AfSlot(AF_POS))(p7, 7, 5);  // listed as an unreachable Sim
                if (AsIcAborted() == AS_AB_SIMSTALE) InterlockedIncrement(&g_t5WorkStale);
                AsIcResetAbort();
                InterlockedIncrement(&g_t5WorkRuns);
            }
            break;
    }
    return NULL;
}
static void AfT5Request(int id) {
    assert(AsIcBegin());
    AsIcAddRow(5, 5, 1);
    AsIcAddRow(7, 5, 2);
    AsIcPost(id);
    assert(g_asReqId && g_asBatchN == 1);  // a second Begin would join this cut (docs/122 §14.18)
}
// docs/219 §2: the four shared rows sit last in kAsDesc, so [AutShadow] Tier2Rows=0 (the default) cuts exactly them from the
// guard lookup, and their layouts follow the managed externs (ledger §428)
static void TestAutShadowTier2Rows() {
    assert(!g_asTier2Rows);
    const AsDesc* d = kAsDesc + AS_NDESC - AS_NT2IC - AS_NT2;
    assert(!strcmp(d[0].name, "ScriptCore.World::World_IsOnRoofImpl") && d[0].owner == AO_OBJ && d[0].kind == AK_INT && d[0].nArg == 2 && d[0].sz == 1);
    assert(!strcmp(d[1].name, "ScriptCore.Objects::Objects_GetHeightAboveFloor") && d[1].owner == AO_OBJ && d[1].kind == AK_FP && d[1].nArg == 2 && d[1].sz == 4);
    assert(!strcmp(d[2].name, "ScriptCore.Objects::Objects_GetForward") && d[2].owner == AO_OBJ && d[2].kind == AK_STRUCT && d[2].nArg == 2 && d[2].sz == 12);
    assert(!strcmp(d[3].name, "ScriptCore.World::World_GetPoolObjectIdForPoolBelowPositionImpl") && d[3].owner == AO_WORLD && d[3].kind == AK_INT && d[3].nArg == 3 && d[3].sz == 8);
    // the float and struct returns need their kind entries (autshadow-kinds.h), the two integer ones must have none
    assert(AsKindOfName(d[1].name) == 'f' && AsKindOfName(d[2].name) == 's' && AsKindOfName(d[0].name) == 0 && AsKindOfName(d[3].name) == 0);
}

// docs/126 §8 IC rows (docs/data/tier2-rows-handoff.md). Shared: the gate and the per-row check. Each group's rows get one
// test function below (G1 here; G2-G4 lanes fill their own). Per row: the descriptor matches the §4.1 stack layout, the kind
// table agrees (float/struct returns need an entry, the rest none), and a dummy cdecl body with that layout, called the way
// AsRefresh calls the game's body (AsCallRaw with dummy inputs, never a game address), gives the answer the row would store.
static const AsDesc* AsT2Ic(int i) { return kAsDesc + AS_NDESC - AS_NT2IC + i; }
static void AsT2IcRow(int i, const char* name, int owner, int kind, int nArg, int sz, int reason) {
    const AsDesc& d = *AsT2Ic(i);
    assert(!strcmp(d.name, name) && d.owner == owner && d.kind == kind && d.nArg == nArg && d.sz == sz && d.reason == reason);
    assert(!d.nOut && !d.strMask && !d.lotClear && d.nArg <= AS_KEY);
    assert(AsKindOfName(d.name) == (kind == AK_FP ? 'f' : kind == AK_STRUCT ? 's' : 0));
}
// one AsRefresh-style call of a dummy body: args are the stack dwords (hidden result pointer first for struct rows)
static AsRet AsT2IcCall(int i, void* body, const DWORD* args) {
    const AsDesc& d = *AsT2Ic(i);
    AsRet r = {0xEEEEEEEE, 0xEEEEEEEE, 0};
    AsCallRaw(body, args, d.nArg + (d.kind == AK_STRUCT), &r, d.kind == AK_FP);
    AsMaskRet(d, &r.lo);
    return r;
}
static void TestAutShadowTier2IcGate() {
    assert(!g_asTier2Rows && !g_asTier2IcRows && AS_NDESC > AS_NT2 + AS_NT2IC);
    assert(AsNDescOn() == AS_NDESC - AS_NT2 - AS_NT2IC);
    g_asTier2IcRows = true;  // IcRows alone (the ini read refuses it) still leaves both sets out
    assert(AsNDescOn() == AS_NDESC - AS_NT2 - AS_NT2IC);
    g_asTier2Rows = true;
    assert(AsNDescOn() == AS_NDESC);
    g_asTier2IcRows = false;
    assert(AsNDescOn() == AS_NDESC - AS_NT2IC);
    g_asTier2Rows = false;
    for (int i = 0; i < AS_NT2IC; i++)  // no row twice, none of them a v0 / docs/208 / docs/219 row
        for (int j = 0; j < AS_NDESC; j++) assert(AsT2Ic(i) == kAsDesc + j || strcmp(AsT2Ic(i)->name, kAsDesc[j].name));
}
// G1 terrain and floor (rows 0-5): dummy bodies with the §4.1 layouts; junk in the high bytes of bool returns is masked
static float __cdecl AsT2FakeHeight(float x, float z, DWORD bridges) { return x * 2 + z + (bridges & 0xFF); }
static DWORD __cdecl AsT2FakeType(float, float, DWORD level) { return level == 1 ? 0x10 : 2; }
static DWORD __cdecl AsT2FakeFloorFeat(DWORD lotLo, DWORD lotHi, DWORD x, DWORD z, DWORD lvl, DWORD flags, DWORD mask) {
    return 0xABCD00 | ((lotLo ^ lotHi ^ x ^ z ^ lvl) & flags & mask & 1);
}
static DWORD __cdecl AsT2FakePuddle(float, float, float, DWORD flags, DWORD) { return 0x7700u | (flags == 4 ? 1u : 0u); }  // §4.1 #6: mask unread
static DWORD __cdecl AsT2FakeCamera(float x, float) { return 0x5500u | (x > 0 ? 1u : 0u); }
static DWORD __cdecl AsT2FakePlatform(float, float y, float) { return 0x3300u | (y > 1 ? 1u : 0u); }
static void TestAutShadowTier2IcG1() {
    AsT2IcRow(0, "ScriptCore.World::World_GetTerrainHeightImpl", AO_WORLD, AK_FP, 3, 4, AS_AB_POS);
    AsT2IcRow(1, "ScriptCore.World::World_GetTerrainTypeImpl", AO_WORLD, AK_INT, 3, 4, AS_AB_POS);
    AsT2IcRow(2, "ScriptCore.World::World_HasFloorFeaturesImpl", AO_CUT, AK_INT, 7, 1, AS_AB_POS);
    AsT2IcRow(3, "ScriptCore.World::World_HasFloorFeaturesWorldPosImpl", AO_CUT, AK_INT, 5, 1, AS_AB_POS);
    AsT2IcRow(4, "ScriptCore.World::World_IsCameraRoutableImpl", AO_WORLD, AK_INT, 2, 1, AS_AB_POS);
    AsT2IcRow(5, "ScriptCore.World::World_IsOnPlatformImpl", AO_WORLD, AK_INT, 3, 1, AS_AB_POS);
    auto fl = [](float v) { DWORD w; memcpy(&w, &v, 4); return w; };
    {
        const DWORD a[3] = {fl(3.0f), fl(4.0f), 0x12345601};  // bridges: only the low byte is the bool
        const AsRet r = AsT2IcCall(0, (void*)AsT2FakeHeight, a);
        assert(r.f == 11.0 && r.hi == 0);
    }
    {
        const DWORD a[3] = {fl(1.0f), fl(2.0f), 1};
        assert(AsT2IcCall(1, (void*)AsT2FakeType, a).lo == 0x10 && AsT2IcCall(1, (void*)AsT2FakeType, a).hi == 0);
    }
    {
        const DWORD a[7] = {7, 0, 2, 3, 1, 4, 0xFFFFFFFF};  // lot 7, tile (2, 3, level 1), flags 4, mask all
        assert(AsT2IcCall(2, (void*)AsT2FakeFloorFeat, a).lo == 0);  // 7^2^3^1 = 7, & 4 & 1 = 0, high bytes masked
        const DWORD b[7] = {7, 0, 2, 3, 1, 5, 0xFFFFFFFF};
        const AsRet r = AsT2IcCall(2, (void*)AsT2FakeFloorFeat, b);
        assert(r.lo == 1 && r.hi == 0);
    }
    {
        const DWORD a[5] = {fl(1.0f), fl(0.0f), fl(2.0f), 4, 0};
        assert(AsT2IcCall(3, (void*)AsT2FakePuddle, a).lo == 1);
        const DWORD b[5] = {fl(1.0f), fl(0.0f), fl(2.0f), 2, 0};
        assert(AsT2IcCall(3, (void*)AsT2FakePuddle, b).lo == 0);
    }
    {
        const DWORD a[2] = {fl(5.0f), fl(-1.0f)};
        assert(AsT2IcCall(4, (void*)AsT2FakeCamera, a).lo == 1);
        const DWORD b[3] = {fl(0.0f), fl(2.5f), fl(0.0f)};
        assert(AsT2IcCall(5, (void*)AsT2FakePlatform, b).lo == 1);
    }
}
// G2 pools (rows 6-8): bool returns with junk high bytes, a uint pond index
static DWORD __cdecl AsT2FakeInPool(float x, float, float z) { return 0x9900u | (x + z > 3 ? 1u : 0u); }
static DWORD __cdecl AsT2FakeHasPool(DWORD lotLo, DWORD, DWORD x, DWORD z, DWORD lvl) { return 0x4400u | ((lotLo + x + z + (lvl & 0xFF)) == 10 ? 1u : 0u); }
static DWORD __cdecl AsT2FakePond(DWORD lotLo, DWORD lotHi, float x, float, float) { return lotLo ? 5u + (DWORD)x : lotHi; }
static void TestAutShadowTier2IcG2() {
    AsT2IcRow(6, "ScriptCore.World::World_IsInPoolImpl", AO_WORLD, AK_INT, 3, 1, AS_AB_POS);
    AsT2IcRow(7, "ScriptCore.World::World_HasPoolImpl", AO_WORLD, AK_INT, 5, 1, AS_AB_POS);
    AsT2IcRow(8, "ScriptCore.World::World_GetPondAtPoint", AO_WORLD, AK_INT, 5, 4, AS_AB_POS);
    auto fl = [](float v) { DWORD w; memcpy(&w, &v, 4); return w; };
    const DWORD a[3] = {fl(2.0f), fl(0.0f), fl(2.0f)};
    const AsRet r = AsT2IcCall(6, (void*)AsT2FakeInPool, a);
    assert(r.lo == 1 && r.hi == 0);
    const DWORD b[3] = {fl(0.0f), fl(0.0f), fl(2.0f)};
    assert(AsT2IcCall(6, (void*)AsT2FakeInPool, b).lo == 0);
    const DWORD c[5] = {4, 0, 3, 2, 0x11223301};  // level is an sbyte: only the low byte counts
    assert(AsT2IcCall(7, (void*)AsT2FakeHasPool, c).lo == 1);  // 4+3+2+1 = 10, high bytes masked
    const DWORD c2[5] = {4, 0, 3, 2, 2};
    assert(AsT2IcCall(7, (void*)AsT2FakeHasPool, c2).lo == 0);
    const DWORD d[5] = {1, 0, fl(3.0f), fl(0.0f), fl(0.0f)};
    assert(AsT2IcCall(8, (void*)AsT2FakePond, d).lo == 8);
    const DWORD d0[5] = {0, 6, fl(3.0f), fl(0.0f), fl(0.0f)};  // lot 0: the world pond table
    assert(AsT2IcCall(8, (void*)AsT2FakePond, d0).lo == 6);
}
// G3 lot and room (rows 9-10): short return (masked to 2 bytes, 0xFFFF = no room), bool
static DWORD __cdecl AsT2FakeRoom(DWORD lotLo, DWORD lotHi, DWORD x, DWORD z, DWORD lvl, DWORD def) {
    return 0xABCD0000u | (lotLo || lotHi ? ((x + z + lvl + def) & 0xFFFF) : 0xFFFFu);
}
static DWORD __cdecl AsT2FakeEntrance(DWORD lotLo, DWORD) { return 0x6600u | (lotLo == 9 ? 1u : 0u); }
static void TestAutShadowTier2IcG3() {
    AsT2IcRow(9, "ScriptCore.World::World_GetRoomIdImpl", AO_WORLD, AK_INT, 6, 2, AS_AB_POS);
    AsT2IcRow(10, "ScriptCore.World::World_LotIsEntranceFromTargetPointImpl", AO_CUT, AK_INT, 2, 1, AS_AB_OTHER);
    const DWORD a[6] = {1, 0, 2, 3, 1, 2};
    const AsRet r = AsT2IcCall(9, (void*)AsT2FakeRoom, a);
    assert(r.lo == 8 && r.hi == 0);  // high half masked off the short
    const DWORD b[6] = {0, 0, 2, 3, 1, 2};
    assert(AsT2IcCall(9, (void*)AsT2FakeRoom, b).lo == 0xFFFF);
    const DWORD c[2] = {9, 0};
    assert(AsT2IcCall(10, (void*)AsT2FakeEntrance, c).lo == 1);
    const DWORD c2[2] = {8, 0};
    assert(AsT2IcCall(10, (void*)AsT2FakeEntrance, c2).lo == 0);
}
// G4 object fields and session (rows 11-14): struct return through the hidden result pointer (first argument, returned in eax),
// bool, float, and a string pointer (AK_REF is stored as the 4-byte value, never answered by the worker)
static DWORD* __cdecl AsT2FakeBBox(DWORD* out, DWORD idLo, DWORD idHi) {
    for (int i = 0; i < 6; i++) out[i] = idLo * 10 + idHi + i;
    return out;
}
static DWORD __cdecl AsT2FakeValid(DWORD idLo, DWORD) { return 0x1100u | (idLo != 0 ? 1u : 0u); }
static float __cdecl AsT2FakeDimmer(DWORD idLo, DWORD) { return idLo == 3 ? 0.75f : 0.0f; }
static const char* __cdecl AsT2FakeWorldName() { return "world"; }
static void TestAutShadowTier2IcG4() {
    AsT2IcRow(11, "ScriptCore.Objects::Objects_GetBoundingBox", AO_OBJ, AK_STRUCT, 2, 24, AS_AB_ROW);
    AsT2IcRow(12, "ScriptCore.Objects::Objects_IsValid", AO_CUT, AK_INT, 2, 1, AS_AB_ROW);
    AsT2IcRow(13, "ScriptCore.World::World_LightGetDimmerImpl", AO_CUT, AK_FP, 2, 4, AS_AB_ROW);
    AsT2IcRow(14, "ScriptCore.World::World_GetWorldFileName", AO_WORLD, AK_REF, 0, 4, AS_AB_OTHER);
    DWORD box[6] = {0};
    const DWORD a[3] = {(DWORD)(uintptr_t)box, 2, 1};  // nArg + 1 dwords: the result pointer first
    const AsRet r = AsT2IcCall(11, (void*)AsT2FakeBBox, a);
    assert(r.lo == 0 && r.hi == 0 && box[0] == 21 && box[5] == 26);  // the result pointer in eax is masked off
    const DWORD b[2] = {5, 0};
    assert(AsT2IcCall(12, (void*)AsT2FakeValid, b).lo == 1);
    const DWORD b0[2] = {0, 0};
    assert(AsT2IcCall(12, (void*)AsT2FakeValid, b0).lo == 0);
    const DWORD c[2] = {3, 0};
    assert(AsT2IcCall(13, (void*)AsT2FakeDimmer, c).f == 0.75);
    assert(AsT2IcCall(13, (void*)AsT2FakeDimmer, b).f == 0.0);
    const AsRet n = AsT2IcCall(14, (void*)AsT2FakeWorldName, nullptr);
    assert(!strcmp((const char*)(uintptr_t)n.lo, "world"));
}

// E rows (docs/data/tier2-enablers-handoff.md, E1 write mask + E2 ulong[]): rows 15-19 of the docs/126 §8 block. Dummy
// bodies with the verified layouts go through AsRefresh (a spare guard slot) and the hit's AsOutWrite: a false answer
// leaves the caller's out as it was, masked or unwritten bytes keep the caller's values, arrays are re-made per hit.
static DWORD __cdecl AsT2FakeBoat(DWORD lotLo, DWORD, float x, float y, float z, float* out) {
    if (lotLo != 1) return 0x7700;  // not a houseboat: out untouched, junk in the high bytes
    out[0] = x + 1, out[1] = y, out[2] = z + 2;
    return 0x7701;
}
static unsigned long long __cdecl AsT2FakeLotLocAt(float, float, float, u8* out, DWORD roomDef) {
    for (int b = 0; b < 9; b++) out[b] = (u8)(0x40 + b);  // x, z, level always (#16)
    if (roomDef == 1) out[10] = 0x5A, out[11] = 0x00;    // room short only with room data; byte 9 never
    return 0x100000002ull;
}
static DWORD g_asT2FakeArr[16];
static DWORD __cdecl AsT2FakePondPos(DWORD lotLo, DWORD, DWORD* ref) {
    if (lotLo != 3) return 0x6600;  // no pond vertices: ref untouched
    g_asT2FakeArr[0] = 0xF10A7, g_asT2FakeArr[2] = 2, g_asT2FakeArr[4] = 0x3F800000, g_asT2FakeArr[5] = 0x40000000;
    *ref = (DWORD)(uintptr_t)g_asT2FakeArr;
    return 0x6601;
}
static DWORD __cdecl AsT2FakeTiles(DWORD lotLo, DWORD, DWORD, DWORD, DWORD) {
    if (!lotLo) return 0;
    g_asT2FakeArr[0] = 0xE10A7, g_asT2FakeArr[2] = 2;  // two ulong tiles at +16: 8 bytes each
    g_asT2FakeArr[4] = 0x11111111, g_asT2FakeArr[5] = 0x22222222, g_asT2FakeArr[6] = 0x33333333, g_asT2FakeArr[7] = 0x44444444;
    return (DWORD)(uintptr_t)g_asT2FakeArr;
}
static void AsT2eRefresh(int i, void* body, const DWORD* key, int nKey, AsEnt& e, DWORD* pool, LONG* poolN) {
    const int slot = AS_MAXIC - 1;
    const AsDesc* d0 = g_asDescOf[slot];
    void* o0 = g_asOrig[slot];
    g_asDescOf[slot] = AsT2Ic(i), g_asOrig[slot] = body;
    memset(&e, 0x5C, sizeof(e));  // stale bytes everywhere: the refresh must not let them through
    memcpy(e.key, key, nKey * 4);
    AsRefresh(slot, e, pool, poolN);
    g_asDescOf[slot] = d0, g_asOrig[slot] = o0;
}
static void TestAutShadowTier2IcE() {
    assert(AS_NT2E == 8 && AS_NT2IC == 15 + AS_NT2E);  // bits 5-6 E3 (TestAutShadowTier2IcE3), bit 7 P6 (TestAutShadowTier2CountRange)
    const char* nm[5] = {"ScriptCore.World::World_GetTilesWithFloorFeaturesImpl", "ScriptCore.World::World_FindPondRepresentativePositions",
                         "ScriptCore.World::World_GetLotLocationAtFloorImpl", "ScriptCore.World::World_GetWorldToHouseboatDisplayPositionImpl",
                         "ScriptCore.World::World_GetHouseboatDisplayToWorldPositionImpl"};
    for (int i = 0; i < 5; i++) {
        const AsDesc& d = *AsT2Ic(15 + i);
        assert(!strcmp(d.name, nm[i]) && d.reason == AS_AB_POS && AsKindOfName(d.name) == 0 && d.nArg <= AS_KEY);
        assert(d.owner != AO_OBJ && d.owner != AO_LIST && d.owner != AO_MEMO);  // e.om shares the union with lp / lastRead
    }
    assert(AsT2Ic(15)->kind == AK_ARRAY && AsT2Ic(15)->sz == 8 && AsT2Ic(15)->nArg == 5 && !AsT2Ic(15)->nOut && AsT2Ic(15)->owner == AO_CUT);
    assert(AsT2Ic(16)->nArg == 3 && AsT2Ic(16)->nOut == 1 && AsT2Ic(16)->outIdx[0] == 2 && AsT2Ic(16)->of == (AS_OF_TRUE | AS_OF_ARR));
    assert(AsT2Ic(17)->nArg == 5 && AsT2Ic(17)->sz == 8 && AsT2Ic(17)->outIdx[0] == 3 && AsT2Ic(17)->outSz[0] == 12 &&
           AsT2Ic(17)->of == AS_OF_DIFF && AsT2Ic(17)->outMask == 0xDFF);
    for (int i = 18; i < 20; i++)
        assert(AsT2Ic(i)->nArg == 6 && AsT2Ic(i)->sz == 1 && AsT2Ic(i)->outIdx[0] == 5 && AsT2Ic(i)->outSz[0] == 12 &&
               AsT2Ic(i)->of == AS_OF_TRUE && AsT2Ic(i)->owner == AO_CUT);
    // gate: every E row needs Tier2Rows, Tier2IcRows and its own bit; the 15 older IC rows ignore the bits
    const int e0 = AS_NDESC - AS_NT2E;
    g_asTier2Rows = g_asTier2IcRows = true, g_asT2eBits = 0;
    for (int i = 0; i < AS_NT2E; i++) assert(!AsDescOn(e0 + i));
    assert(AsDescOn(e0 - 1));
    g_asT2eBits = 16;  // Tier2HouseboatRow alone
    assert(AsDescOn(e0 + 4) && !AsDescOn(e0) && !AsDescOn(e0 + 5) && !AsDescOn(e0 + 6) && !AsDescOn(e0 + 7));
    g_asTier2IcRows = false;
    assert(!AsDescOn(e0 + 4));
    g_asTier2Rows = false, g_asT2eBits = 0;

    static DWORD pool[256];
    LONG poolN = 0;
    AsEnt e;
    auto fl = [](float v) { DWORD w; memcpy(&w, &v, 4); return w; };
    // houseboat (row 19) and #21 (row 18): true -> 12 B out; false -> caller's out untouched (ledger §459 I-459-3)
    for (int i = 18; i < 20; i++) {
        const DWORD kT[5] = {1, 0, fl(1.0f), fl(2.0f), fl(3.0f)};
        AsT2eRefresh(i, (void*)AsT2FakeBoat, kT, 5, e, pool, &poolN);
        assert(e.r[0] == 1 && e.r[1] == 0);
        float out[4] = {9, 9, 9, 9};
        DWORD a[6] = {1, 0, kT[2], kT[3], kT[4], (DWORD)(uintptr_t)out};
        assert(AsOutWrite(*AsT2Ic(i), e, pool, a) && out[0] == 2 && out[1] == 2 && out[2] == 5 && out[3] == 9);
        const DWORD kF[5] = {2, 0, fl(1.0f), fl(2.0f), fl(3.0f)};
        AsT2eRefresh(i, (void*)AsT2FakeBoat, kF, 5, e, pool, &poolN);
        assert(e.r[0] == 0);
        float keep[3] = {7, 8, 9};
        a[5] = (DWORD)(uintptr_t)keep;
        assert(AsOutWrite(*AsT2Ic(i), e, pool, a) && keep[0] == 7 && keep[1] == 8 && keep[2] == 9);
    }
    // #16 (row 17): bytes outside the static mask (9) and bytes this input did not write (10-11 without room data) keep
    // the caller's values; the lot id comes back whole
    {
        const DWORD kR[4] = {fl(1.0f), fl(0.0f), fl(1.0f), 1};  // Vector3, roomDef 1: room short written
        AsT2eRefresh(17, (void*)AsT2FakeLotLocAt, kR, 4, e, pool, &poolN);
        assert(e.r[0] == 2 && e.r[1] == 1 && e.om == 0xDFF);
        u8 out[12];
        memset(out, 0xCC, sizeof(out));
        DWORD a[5] = {kR[0], kR[1], kR[2], (DWORD)(uintptr_t)out, 1};
        assert(AsOutWrite(*AsT2Ic(17), e, pool, a));
        assert(out[0] == 0x40 && out[8] == 0x48 && out[9] == 0xCC && out[10] == 0x5A && out[11] == 0);
        const DWORD kN[4] = {fl(1.0f), fl(0.0f), fl(1.0f), 0};  // no room data: room short untouched
        AsT2eRefresh(17, (void*)AsT2FakeLotLocAt, kN, 4, e, pool, &poolN);
        assert(e.om == 0x1FF);
        memset(out, 0xCC, sizeof(out));
        assert(AsOutWrite(*AsT2Ic(17), e, pool, a) && out[8] == 0x48 && out[9] == 0xCC && out[10] == 0xCC && out[11] == 0xCC);
    }
    // #15 (row 16): the game's array is copied to the pool at the cut (pointer not kept); a hit makes a fresh one
    {
        const AsGcMallocFn g0 = g_asGcMalloc;
        AsNewArrayFn n0 = g_asNewArray;
        g_asGcMalloc = AfT5Malloc, g_asNewArray = AsNewArrayGc;
        const DWORD kP[2] = {3, 0};
        AsT2eRefresh(16, (void*)AsT2FakePondPos, kP, 2, e, pool, &poolN);
        assert(e.r[0] == 1 && e.payN == 4 && pool[e.pay] == 0xF10A7 && pool[e.pay + 1] == 2 && pool[e.pay + 3] == 0x40000000);
        DWORD* got = NULL;
        DWORD a[3] = {3, 0, (DWORD)(uintptr_t)&got};
        assert(AsOutWrite(*AsT2Ic(16), e, pool, a) && got && got != g_asT2FakeArr && got[0] == 0xF10A7 && got[2] == 2 && got[5] == 0x40000000);
        free(got);
        const DWORD kQ[2] = {4, 0};
        AsT2eRefresh(16, (void*)AsT2FakePondPos, kQ, 2, e, pool, &poolN);
        DWORD* keep = (DWORD*)(uintptr_t)0x1234;
        a[2] = (DWORD)(uintptr_t)&keep;
        assert(e.r[0] == 0 && !e.payN && AsOutWrite(*AsT2Ic(16), e, pool, a) && keep == (DWORD*)(uintptr_t)0x1234);
        // #7 (row 15): ulong[] payload = 2 dwords per element; the hit's 8-byte allocation holds them all
        const DWORD kA[5] = {9, 0, 0, 4, 0xFFFFFFFF};
        AsT2eRefresh(15, (void*)AsT2FakeTiles, kA, 5, e, pool, &poolN);
        assert(e.payN == 2 + 2 * 2 && pool[e.pay] == 0xE10A7 && pool[e.pay + 1] == 2 && pool[e.pay + 5] == 0x44444444);
        DWORD* arr = (DWORD*)g_asNewArray(pool[e.pay], pool[e.pay + 1], AsT2Ic(15)->sz);
        memcpy(arr + 4, pool + e.pay + 2, (e.payN - 2) * 4);  // as the AK_ARRAY hit
        assert(arr[2] == 2 && arr[4] == 0x11111111 && arr[7] == 0x44444444);
        free(arr);
        const DWORD kZ[5] = {0, 0, 0, 4, 0xFFFFFFFF};
        AsT2eRefresh(15, (void*)AsT2FakeTiles, kZ, 5, e, pool, &poolN);
        assert(e.r[0] == 0 && !e.payN);
        g_asGcMalloc = g0, g_asNewArray = n0;
    }
}

// P6 (docs/data/tier2-rows35-handoff.md, ledger §462): Query_CountObjectsInRange is the last row, behind Tier2CountRange (bit 7 since E3; was bit
// 5) on top of Tier2Rows + Tier2IcRows; the same layout as Query_GetObjectsInRange; a dummy body with that cdecl layout gives
// the stored count; the worker gate refuses the row (no lookup, no learn) unless ts3fixes cleared it for this request.
static DWORD __cdecl AsT2FakeCount(DWORD type, float x, float y, float z, float r) {
    return 0xAB000000u | (type & 0xFF) | (x * x + y * y + z * z < r * r ? 0x100u : 0u);
}
static void TestAutShadowTier2CountRange() {
    const AsDesc& d = kAsDesc[AS_NDESC - 1];
    assert(&d == AsT2Ic(22));
    AsT2IcRow(22, "ScriptCore.Queries::Query_CountObjectsInRange", AO_CUT, AK_INT, 5, 4, AS_AB_QUERY);
    const AsDesc* g = NULL;
    for (int i = 0; i < AS_NDESC; i++) if (!strcmp(kAsDesc[i].name, "ScriptCore.Queries::Query_GetObjectsInRange")) g = &kAsDesc[i];
    assert(g && g->nArg == d.nArg && g->at == d.at && g->owner == d.owner && !g->strMask);  // 0x7496E0 / 0x749770 [verified]
    g_asTier2Rows = g_asTier2IcRows = true, g_asT2eBits = 0x7F;  // every other E bit (E3 included): still off
    assert(!AsDescOn(AS_NDESC - 1) && AsDescOn(AS_NDESC - 2));
    g_asT2eBits = 0x80;
    assert(AsDescOn(AS_NDESC - 1) && !AsDescOn(AS_NDESC - 2));
    g_asTier2IcRows = false;
    assert(!AsDescOn(AS_NDESC - 1));
    g_asTier2Rows = false, g_asT2eBits = 0;
    static DWORD pool[64];
    LONG poolN = 0;
    AsEnt e;
    auto fl = [](float v) { DWORD w; memcpy(&w, &v, 4); return w; };
    const DWORD kIn[5] = {0x12345677, fl(1.0f), fl(0.0f), fl(1.0f), fl(2.0f)}, kOut[5] = {0x12345677, fl(3.0f), fl(0.0f), fl(3.0f), fl(2.0f)};
    AsT2eRefresh(22, (void*)AsT2FakeCount, kIn, 5, e, pool, &poolN);
    assert(e.r[0] == 0xAB000177u && e.r[1] == 0);
    AsT2eRefresh(22, (void*)AsT2FakeCount, kOut, 5, e, pool, &poolN);
    assert(e.r[0] == 0xAB000077u);
    // actor gate: shut by default and at every run start; ActorScope works only on the worker
    const int slot = AS_MAXIC - 2;
    const AsDesc* d0 = g_asDescOf[slot];
    const char k0 = g_asKind[slot];
    const DWORD tid0 = g_asWorkerTid;
    const LONG held0 = g_asHeld;
    g_asHeld = -1;
    g_asDescOf[slot] = &d, g_asKind[slot] = 0;
    assert(g_asActorHh == 1);
    g_asWorkerTid = 0;
    AsIcActorScope(0);  // sim thread: no-op
    assert(g_asActorHh == 1);
    g_asWorkerTid = GetCurrentThreadId(), g_asStop = 0;
    DWORD args[5];
    memcpy(args, kIn, sizeof(args));
    AsRet r;
    const LONG h0 = g_asActorHhAborts, ring0 = g_asRingHead;
    AsResetAbort();
    AsWorkerAnswer(slot, args, &r);
    assert(AsAborted() == AS_AB_OTHER && g_asActorHhAborts == h0 + 1 && g_asRingHead == ring0);  // no learn
    AsResetAbort();
    AsIcActorScope(0);
    assert(g_asActorHh == 0);
    AsWorkerAnswer(slot, args, &r);  // past the gate: no published version held here, so the next check aborts
    assert(g_asActorHhAborts == h0 + 1 && AsAborted() == AS_AB_OTHER);
    AsResetAbort();
    AsIcActorScope(1);
    assert(g_asActorHh == 1);
    AsIcActorScope(0);
    AsWorkerBegin();
    assert(g_asActorHh == 1);
    AsWorkerEnd();
    g_asDescOf[slot] = d0, g_asKind[slot] = k0, g_asWorkerTid = tid0, g_asHeld = held0;
}

// E3 (ledger §463): fake WM, lot, routing world and lot-table entry in the layout AsLevelGate reads; the body counts calls
static u8 g_asLvFakeLot[0x420], g_asLvFakeRw[0x980], g_asLvFakeEnt[0x170], g_asLvFakeWmObj[4];
static u8* g_asLvFakeWm, *g_asLvFakeRwP;
static int g_asLvGipN, g_asLvBodyN;
static bool g_asLvNoLot, g_asLvNoEnt;
static u8* __fastcall AsLvFakeLotAt(u8* wm, void*, float, float) { return wm == g_asLvFakeWmObj && !g_asLvNoLot ? g_asLvFakeLot : NULL; }
static u8* __fastcall AsLvFakeGip(u8* p, void*, DWORD lo, DWORD hi) {
    g_asLvGipN++;
    assert(p == (u8*)(uintptr_t)0x95C0 && lo == 0x10 && hi == 0x20);
    return g_asLvNoEnt ? NULL : g_asLvFakeEnt;
}
static DWORD __cdecl AsLvFakeBody3(DWORD, DWORD, DWORD level) { g_asLvBodyN++; return 0x40 + level; }
static DWORD __cdecl AsLvFakeBody5(DWORD, DWORD, DWORD, DWORD, DWORD) { g_asLvBodyN++; return 0x57A0; }
static void TestAutShadowTier2IcE3() {
    const AsDesc& d2 = *AsT2Ic(20);
    const AsDesc& d4 = *AsT2Ic(21);
    assert(!strcmp(d2.name, "ScriptCore.World::World_GetTerrainSurfaceTypeImpl") && d2.kind == AK_INT && d2.nArg == 3 && d2.sz == 4 &&
           d2.of == AS_OF_LVL1 && d2.owner == AO_CUT && !d2.nOut && d2.reason == AS_AB_POS);
    assert(!strcmp(d4.name, "ScriptCore.World::World_GetFloorSurfaceTypeImpl") && d4.kind == AK_REF && d4.nArg == 5 &&
           d4.of == AS_OF_LVLA && d4.owner == AO_CUT && !d4.nOut && AsKindOfName(d4.name) == 0);
    // gate: Tier2IcNew bit 4 = both rows (e bits 5, 6), nothing else
    const int e0 = AS_NDESC - AS_NT2E;
    g_asTier2Rows = g_asTier2IcRows = true, g_asT2eBits = 0x60;
    assert(AsDescOn(e0 + 5) && AsDescOn(e0 + 6) && !AsDescOn(e0) && !AsDescOn(e0 + 4));
    g_asTier2Rows = g_asTier2IcRows = false, g_asT2eBits = 0;

    const AsLvLotAtFn l0 = g_asLvLotAt;
    const AsLvGipFn gp0 = g_asLvGip;
    u8* const* w0 = g_asLvWm;
    u8* const* r0 = g_asLvRw;
    volatile LONG* j0 = g_asJobState;
    static volatile LONG job;
    g_asLvFakeWm = g_asLvFakeWmObj, g_asLvFakeRwP = g_asLvFakeRw;
    g_asLvLotAt = AsLvFakeLotAt, g_asLvGip = AsLvFakeGip, g_asLvWm = &g_asLvFakeWm, g_asLvRw = &g_asLvFakeRwP, g_asJobState = &job;
    auto put = [](u8* b, int o, LONG v) { memcpy(b + o, &v, 4); };
    put(g_asLvFakeLot, 0x48, 0x10), put(g_asLvFakeLot, 0x4C, 0x20), put(g_asLvFakeLot, 0x17C, -1), put(g_asLvFakeLot, 0x180, 2);
    put(g_asLvFakeRw, 0x95C, 0x95C0), g_asLvFakeRw[0x974] = 1;
    put(g_asLvFakeEnt, 0x40, -1);  // bit i = level i - 1
    static DWORD pool[64];
    LONG poolN = 0;
    AsEnt e;
    auto fl = [](float v) { DWORD w; memcpy(&w, &v, 4); return w; };
    auto run2 = [&](DWORD level) {
        const DWORD k[3] = {fl(3.0f), fl(4.0f), level};
        g_asLvGipN = g_asLvBodyN = 0;
        AsT2eRefresh(20, (void*)AsLvFakeBody3, k, 3, e, pool, &poolN);
    };
    // #2: level 1 = bit 2. Set -> filled; clear -> skipped (no body call, no answer kept, no payload)
    put(g_asLvFakeEnt, 0x168, 1u << 2);
    run2(1);
    assert(g_asLvGipN == 1 && g_asLvBodyN == 1 && e.r[0] == 0x41 && !(e.flags & AE_NOPAY));
    put(g_asLvFakeEnt, 0x168, ~(1u << 2));
    run2(1);
    assert(g_asLvGipN == 1 && g_asLvBodyN == 0 && (e.flags & AE_NOPAY) && e.r[0] == 0 && e.r[1] == 0 && !e.payN);
    // level from the low byte, signed (0xAF9770 / 0xAF9B60): 0x1FF = -1 = bit 0
    put(g_asLvFakeEnt, 0x168, 1);
    run2(0x1FF);
    assert(g_asLvBodyN == 1);
    // level outside [lot+0x17C, lot+0x180]: 0x834B80 returns before the check -> filled without the lookup
    put(g_asLvFakeEnt, 0x168, 0);
    run2(3);
    assert(g_asLvGipN == 0 && g_asLvBodyN == 1);
    // a job running: no lookup (gate 12 would wait), skipped; a lot-table miss: filled
    job = 1;
    run2(1);
    assert(g_asLvGipN == 0 && g_asLvBodyN == 0 && (e.flags & AE_NOPAY));
    job = 0, g_asLvNoEnt = true;
    run2(1);
    assert(g_asLvGipN == 1 && g_asLvBodyN == 1);
    g_asLvNoEnt = false;
    // the 0x408 / 0x40C lot kind, no routing flag +0x974, no lot by position, no WM: the body never checks -> filled
    put(g_asLvFakeLot, 0x408, 1), put(g_asLvFakeLot, 0x40C, 0x50);
    run2(1);
    assert(g_asLvGipN == 0 && g_asLvBodyN == 1);
    put(g_asLvFakeLot, 0x408, 0), g_asLvFakeRw[0x974] = 0;
    run2(1);
    assert(g_asLvGipN == 0 && g_asLvBodyN == 1);
    g_asLvFakeRw[0x974] = 1, g_asLvNoLot = true;
    run2(1);
    assert(g_asLvGipN == 0 && g_asLvBodyN == 1);
    g_asLvNoLot = false, g_asLvFakeWm = NULL;
    run2(1);
    assert(g_asLvGipN == 0 && g_asLvBodyN == 1);
    g_asLvFakeWm = g_asLvFakeWmObj;
    // #4 (LVLA): every level -1..2 (bits 0-3) must be set; one clear -> skipped
    const DWORD k4[5] = {0x10, 0x20, fl(3.0f), fl(1.0f), fl(4.0f)};
    put(g_asLvFakeEnt, 0x168, 0xF);
    g_asLvGipN = g_asLvBodyN = 0;
    AsT2eRefresh(21, (void*)AsLvFakeBody5, k4, 5, e, pool, &poolN);
    assert(g_asLvGipN == 1 && g_asLvBodyN == 1 && e.r[0] == 0x57A0 && !(e.flags & AE_NOPAY));
    put(g_asLvFakeEnt, 0x168, 0xB);  // level 1 clear
    g_asLvGipN = g_asLvBodyN = 0;
    AsT2eRefresh(21, (void*)AsLvFakeBody5, k4, 5, e, pool, &poolN);
    assert(g_asLvGipN == 1 && g_asLvBodyN == 0 && (e.flags & AE_NOPAY) && e.r[0] == 0);
    g_asLvLotAt = l0, g_asLvGip = gp0, g_asLvWm = w0, g_asLvRw = r0, g_asJobState = j0;
}

static void TestAutShadowRun() {
    Ts3nAutShadow& k = ts3native_autshadow;
    Ts3nMtProbe& m = ts3native_mtprobe;
    g_asAddIc = AfT5AddIc, g_asAddRoots = AfT5AddRoots, g_asGcMalloc = AfT5Malloc, g_asHostPtr = &g_t5HostPtr;
    g_asMtInit = AfMtInit, g_asMtNext = AfMtNext, g_asDetFlag = &g_afDet;
    AsInstallRun(NULL, true);
    assert(g_asRunSkip == 0 && k.runSkip == 0);  // ini default off; the tests keep it on (with its check)
    // docs/122 §14.21: FreshEvery / KeepPartial default off; Stat adds to the consume-side counters; Fresh is inert when off
    assert(AsIcConfig(11) == 0 && AsIcConfig(12) == 0 && k.freshEvery == 0 && k.keepPartial == 0);
    {
        const LONG p0 = k.passPairs, c0 = k.classPairs, k0 = k.partialKept, f0 = k.freshReqs;
        AsIcStat(0, 3), AsIcStat(1, 10), AsIcStat(6, 1), AsIcStat(7, 99), AsIcFresh(1), AsIcFresh(0);
        assert(k.passPairs == p0 + 3 && k.classPairs == c0 + 10 && k.partialKept == k0 + 1 && k.freshReqs == f0);
        g_asFreshEvery = 8;
        AsIcFresh(1), AsIcFresh(1), AsIcFresh(0), AsIcFresh(0);  // nested opens and closes count once
        assert(k.freshReqs == f0 + 1 && !g_asInRef && !g_asFreshOpen);
        g_asFreshEvery = 0;
    }
    g_asRunSkip = 1;
    assert(g_mtOnEnter == AsSimEnter && g_mtOnLeave == AsSimLeaveLoop && g_mtJobHook == AsJobHook && g_asMode == 0 && g_sppN == 0);
    // ICs register at the first Simulate entry; Begin refuses until the methods are set
    AsSimEnter();
    assert(g_t5Ics == 35 && k.icRegistered == 35 && AsIcConfig(35) == 1 && AsIcVersion() == 11 && AsIcConfig(1) == 4 && AsIcConfig(2) == 20);
    {   // ImageSha (docs/218 §5): a fake MonoImage {.., raw_data +8, raw_data_len +0xC} over "MZ" + 510 zero bytes
        static u8 img[0x200] = { 'M', 'Z' };
        DWORD mi[16] = {}, arr[4 + 8] = {}, small[4 + 8] = {};
        mi[2] = (DWORD)(uintptr_t)img; mi[3] = sizeof(img);
        arr[2] = 32; small[2] = 16;
        static const u8 want[32] = { 0xa5, 0xb8, 0x69, 0x7c, 0x56, 0x6f, 0xe9, 0x11, 0xe9, 0xec, 0x7b, 0xcb, 0xca, 0x82, 0xb0, 0x43,
            0x27, 0xd4, 0xb5, 0x13, 0x5c, 0x47, 0x52, 0x4d, 0xb0, 0x96, 0x9f, 0xad, 0x8a, 0x18, 0x48, 0x30 };  // hashlib.sha256
        assert(AsIcImageSha((const u8*)mi, (u8*)arr) == 0x200 && memcmp((const u8*)arr + 16, want, 32) == 0);
        assert(AsIcImageSha((const u8*)mi, (u8*)small) == 0);  // short array
        img[0] = 'X';
        assert(AsIcImageSha((const u8*)mi, (u8*)arr) == 0);  // no "MZ": not an image
        img[0] = 'M';
        assert(AsIcImageSha(nullptr, (u8*)arr) == 0);
    }
    // docs/124 §17.10 cost-guard keys: marker, then the counters as they stand
    assert(AsIcConfig(24) == 1 && AsIcConfig(25) == k.cutUs && AsIcConfig(26) == k.cuts && AsIcConfig(29) == k.refUs && AsIcConfig(30) == k.refRuns);
    assert(!AsIcBegin());
    AsIcSetMethods((void*)1, (void*)2, (void*)3);
    const DWORD emptyStr[4] = {0x51, 0, 0, 0};
    AsIcSetRefs((void*)emptyStr, (void*)0xE0E0);
    assert(g_as->keep[0] == (DWORD)(uintptr_t)emptyStr && (k.rooted & 0xC) == 0xC);
    for (int i = 0; i < 0xC30 + AS_RNG_LEN; i++) g_t5Host[i] = (u8)(i * 7);

    // Mode 0: cut and finish at once, no reference, no worker
    g_mtInvoke = AfT5Invoke;
    AfT5Request(40);
    AsSimLeave();
    assert(g_t5Ics == 35 && g_t5Roots == 4 && (k.rooted & 3) == 3 && g_asRefsRooted);
    assert(k.cutReq == 1 && k.refRuns == 0 && k.noWorker == 1 && g_t5Finish == 1 && g_t5Last == AS_LAST_NOWORKER && g_asRun == AS_RUN_IDLE);
    assert(k.batchReqs == 1 && k.batchCuts == 0 && k.simTicks == 1);
    {  // shared cut (docs/122 §14.18): requests posted before the cut join it, one cut and one finish; a full batch is busy
        const LONG cut0 = k.cutReq, busy0 = k.busy;
        const int fin0 = g_t5Finish;
        for (int r = 0; r < AS_BATCH_MAX; r++) {
            assert(AsIcBegin());
            AsIcAddRow(10 + r, 5, 1);  // the request's actor
            AsIcAddRow(7, 5, 1);       // a Sim it reaches (not an actor)
            AsIcAddRow(8, 5, 2);
            AsIcPost(60 + r);
        }
        assert(!AsIcBegin() && k.busy == busy0 + 1 && g_asBatchN == AS_BATCH_MAX && g_asActorN == AS_BATCH_MAX);
        assert(AsIsActor(AfId(10)) && AsIsActor(AfId(13)) && !AsIsActor(AfId(7)) && AsSetHas(g_as->always, AfId(12)) && AsSetHas(g_as->simIds, AfId(8)));
        AsSimLeave();
        assert(k.cutReq == cut0 + 1 && k.batchCuts == 1 && k.batchReqs == 1 + AS_BATCH_MAX && k.batchMax == AS_BATCH_MAX);
        assert(g_t5Finish == fin0 + 1 && g_asReqId == 0 && g_asBatchN == 0 && g_asRun == AS_RUN_IDLE);
        // the next batch starts clean: its first reachable Sim is the only actor
        assert(AsIcBegin());
        AsIcAddRow(5, 5, 1);
        assert(g_asActor == AfId(5) && g_asActorN == 1 && !AsIsActor(AfId(10)) && !AsSetHas(g_as->always, AfId(12)));
        g_asActor = 0, g_asActorN = 0;
        AsSetClear(g_as->always);
        // B13 notes: calls and frames with a call (the go-live request and cut rates)
        const LONG calls0 = k.metaCalls, ticks0 = k.metaTicks;
        AsIcNote(), AsIcNote();
        AsSimLeave();  // nothing posted: only the frame count moves
        AsIcNote();
        assert(k.metaCalls == calls0 + 3 && k.metaTicks == ticks0 + 2);
        g_t5Finish = fin0, k.cutReq = cut0;  // the rest of T5 counts from the single-request run above
    }

    // Mode 1 on the M5 worker
    m.p3Ready = 7, m.p4Ready = 0xF;
    ts3native_counters.mtMode = 1;
    MtM5Tick();
    MtWaitFor(&m.workerState, 2);
    // Redirect (version 5): Mode 0 refuses; Mode 1 rotates the RuntimeMethod bodies target -> stub, hook -> target
    {
        DWORD mm[3][10] = {}, rm[3][18] = {};
        for (int i = 0; i < 3; i++) {
            mm[i][5] = (DWORD)(uintptr_t)rm[i], rm[i][2] = (DWORD)(uintptr_t)mm[i], rm[i][16] = 2, rm[i][17] = 0x100;
            rm[i][0] = 0xC0DE0 + i, rm[i][5] = 0xDA7A0 + i;
        }
        u8* const t = (u8*)mm[0], * const h = (u8*)mm[1], * const st = (u8*)mm[2];
        assert(AsIcRedirect(t, h, st) == -1 && rm[0][0] == 0xC0DE0);
        g_asMode = 1;
        rm[2][17] = 0;
        assert(AsIcRedirect(t, h, st) == -3 && rm[0][0] == 0xC0DE0);
        rm[2][17] = 0x100, rm[1][16] = 3;
        assert(AsIcRedirect(t, h, st) == -4 && rm[0][0] == 0xC0DE0);
        rm[1][16] = 2, mm[2][5] = 0;
        assert(AsIcRedirect(t, h, st) == -2);
        mm[2][5] = (DWORD)(uintptr_t)rm[2];
        assert(AsIcRedirect(t, h, st) == 0);
        assert(rm[0][0] == 0xC0DE1 && rm[0][5] == 0xDA7A1 && rm[0][2] == (DWORD)(uintptr_t)mm[1]);  // target runs the hook
        assert(rm[2][0] == 0xC0DE0 && rm[2][5] == 0xDA7A0 && rm[2][2] == (DWORD)(uintptr_t)mm[0]);  // stub runs the original
        assert(rm[1][0] == 0xC0DE1);
        assert(AsIcRedirect(t, h, st) == -4);  // a second call finds the target's back pointer moved: refused
    }
    assert(AsIcConfig(4) == 1);
    g_afRandMt = 1;
    AfT5Request(41);
    const u8 rng7 = g_t5Host[0xC30 + 7];
    AsSimLeave();  // cut, reference (sim thread), worker submitted
    assert(k.refRuns == 1 && k.rngDraws == 1 && g_t5Host[0xC30 + 7] == rng7 && k.workRuns == 1 && g_asRun == AS_RUN_WINDOW);
    assert(g_asOrig[AF_RAND] == (void*)AfRand && !g_asInRef && g_asRngSeedV[g_asPub]);
    MtWaitFor(&g_mtBusy, 0);
    assert(g_t5WorkRuns == 1 && g_t5WorkStale == 1 && !memcmp(g_t5RefPos, g_t5WorkPos, 12) && g_asHeld == -1);
    // docs/125: per-unit draws equal in the reference and on the worker, in any unit order, with an extra draw in one unit
    assert(g_t5WNoUnit == 1 && !memcmp(g_t5R, g_t5W, sizeof(g_t5W)) && g_t5R[0] != g_t5R[1] && g_t5R[0] != g_t5R[2]);
    assert(k.rngPause == 2 && k.rngPauseDraws == 3 && g_asRngPause == 0);
    AsIcRngPause(1);  // outside the reference: nothing
    assert(k.rngPause == 2 && g_asRngPause == 0);
    {  // v4 abort reasons from the managed hooks: H11 rain, null posture list; past the end = other
        const LONG other = k.abort[AS_AB_OTHER];
        AsManagedAbort(AS_AB_RAIN);
        assert(AsAborted() == AS_AB_RAIN && k.abort2[0] == 1);
        AsResetAbort();
        AsManagedAbort(AS_AB_POSTURE);
        AsManagedAbort(AS_AB_N);
        assert(AsAborted() == AS_AB_POSTURE && k.abort2[1] == 1 && k.abort[AS_AB_OTHER] == other + 1);
        AsResetAbort();
    }
    {
        AsRet rr = {0, 0, 0};
        g_asHeld = g_asPub, g_asRngUnitW = 0, g_asRngLiveW = false, g_afDet = 1;  // `Deterministic` on: 0.5, no MT use
        assert(AsRngWorker(AF_RAND, &rr) == 1 && rr.f == 0.5 && !g_asRngLiveW);
        g_asHeld = -1, g_afDet = 0;
    }
    AsSimEnter();  // window closes, the job had ended: no overrun
    assert(k.overrun == 0 && g_asRun == AS_RUN_WAIT && k.windowUs >= 0);
    AsSimLeave();  // finish
    assert(g_t5Finish == 2 && g_t5Last == AS_LAST_OK && g_asRun == AS_RUN_IDLE && g_asReqId == 0);
    // the stale Sim the worker read is refreshed from the next cut on: a second run reads it
    AfT5Request(42);
    AsSimLeave();
    MtWaitFor(&g_mtBusy, 0);
    assert(g_t5WorkRuns == 2 && g_t5WorkStale == 1);
    AsSimEnter();
    AsSimLeave();
    assert(g_t5Finish == 3 && g_t5Last == AS_LAST_OK);

    // overrun: the worker is still inside at the next Simulate entry -> stop, A5 status
    g_t5Block = 1;
    AfT5Request(43);
    AsSimLeave();
    AsSimEnter();
    assert(k.overrun == 1 && g_asStop == 1 && k.overrunIdle <= 1);  // idle: the worker had not entered the job yet (racy here)
    MtWaitFor(&g_mtBusy, 0);
    assert(g_t5SawStop == 1);
    AsSimLeave();
    assert(g_t5Finish == 4 && g_t5Last == AS_LAST_STOPPED);
    // epoch between cut and the next Simulate entry: dropped (A5), stop set
    AfT5Request(44);
    AsSimLeave();
    AsEpochBump();
    AsSimEnter();
    assert(k.epochDrops == 1 && g_asStop == 1);
    MtWaitFor(&g_mtBusy, 0);
    AsSimLeave();
    assert(g_t5Finish == 5 && g_t5Last == AS_LAST_EPOCH);
    // v6 WindowWaitUs (docs/122 §14.13): the sim waits at the window close for a job that ends in time: no overrun, no stop
    AfT5Request(145);
    AsSimLeave();
    HANDLE rel = CreateThread(NULL, 0, AfT5Release, NULL, 0, NULL);  // clears g_t5Block after 20 ms
    const LONG ov0 = k.overrun;
    g_asWaitUs = 2000000;
    AsSimEnter();
    g_asWaitUs = 0;
    assert(k.overrun == ov0 && k.windowWaits == 1 && k.windowWaitUs >= 5000 && !g_mtBusy && g_asRun == AS_RUN_WAIT && !g_asStop);
    WaitForSingleObject(rel, INFINITE);
    CloseHandle(rel);
    AsSimLeave();
    assert(g_t5Finish == 6 && g_t5Last == AS_LAST_OK);
    g_t5Block = 0;
    // the worker refuses the job while EA memory tracking is on
    g_mtFakeTrack = 1;
    AfT5Request(45);
    AsSimLeave();
    MtWaitFor(&g_mtBusy, 0);
    AsSimEnter();
    AsSimLeave();
    assert(g_t5Finish == 7 && g_t5Last == AS_LAST_REFUSED && k.workRefused == 1);
    g_mtFakeTrack = 0;
    ts3native_counters.mtMode = 0;
    MtM5Tick();
    assert(!g_mtWorker && m.workerStopFails == 0);
    // no idle worker in Mode 1: reference only, then finish
    AfT5Request(46);
    AsSimLeave();
    assert(g_t5Finish == 8 && g_t5Last == AS_LAST_NOWORKER && k.refRuns == 7 && k.rngDraws == 7);
    // docs/51 I-188-3: the loop-back cuts until the post-pump hook fires; from then on only the post-pump point does
    assert(k.cutSite == 1 && !g_asPumpCut);
    AfT5Request(47);
    AsSimPump();
    assert(g_t5Finish == 9 && k.cutSite == 2 && g_asPumpCut && k.cutReq == 9);
    AfT5Request(48);
    AsSimLeaveLoop();  // loop-back: nothing (request still pending)
    assert(k.cutReq == 9 && g_asReqId == 48);
    AsSimEnter();
    AsSimPump();
    assert(g_t5Finish == 10 && k.cutReq == 10 && g_asReqId == 0);
    g_asPumpCut = false;

    // AutShadowNative.Log -> autshadow.jsonl
    wchar_t tmp[MAX_PATH];
    GetTempPathW(MAX_PATH, tmp);
    swprintf_s(g_asJsonlPath, L"%sts3native-selftest-autshadow-%lu.jsonl", tmp, GetCurrentProcessId());  // per process: parallel selftest lanes
    DeleteFileW(g_asJsonlPath);
    AsFakeStr line = AfStr(L"{\"a\":1}");
    AsIcLog((const DWORD*)&line);
    AsIcLog((const DWORD*)&line);
    CloseHandle(g_asJsonl);
    g_asJsonl = INVALID_HANDLE_VALUE;
    char buf[64] = "";
    FILE* f = NULL;
    assert(!_wfopen_s(&f, g_asJsonlPath, L"rb") && f);
    const size_t got = fread(buf, 1, sizeof(buf) - 1, f);
    fclose(f);
    DeleteFileW(g_asJsonlPath);
    assert(got == 16 && !strcmp(buf, "{\"a\":1}\n{\"a\":1}\n") && k.logLines == 2);
    // the GC_malloc array allocator: MonoArray header (vtable, max_length), zeroed elements
    DWORD* arr = (DWORD*)AsNewArrayGc(0xA0001, 3, 4);
    assert(arr && arr[0] == 0xA0001 && arr[2] == 3 && arr[4] == 0 && arr[6] == 0);
    free(arr);
    arr = (DWORD*)AsNewArrayGc(0xA0002, 3, 8);  // ulong[] (docs/126 #7): 16 + 3 * 8 bytes, all zero
    assert(arr && arr[0] == 0xA0002 && arr[2] == 3 && arr[4] == 0 && arr[9] == 0 && !AsNewArrayGc(0xA0002, 3, 2));
    free(arr);
    g_mtOnEnter = NULL, g_mtOnLeave = NULL, g_mtJobHook = NULL, g_asMode = -1, g_asWorkerTid = 0, g_afRandMt = 0;
    printf("autshadow run: %ld requests, %ld cuts for them, %ld reference runs (%ld RNG restores), %ld worker runs, %ld overrun, %ld epoch drop\n",
           k.req, k.cutReq, k.refRuns, k.rngDraws, k.workRuns, k.overrun, k.epochDrops);
}

// Ledger probe (autledger.cpp, docs/51 mono ledger): the four watched ICs on their own fake table, scope by task id,
// class and frame records, the Simulate tick read, the Sleep probe marks; every answer passes through unchanged.
static double __cdecl AfLgRand() { return 0.5; }
static float* __cdecl AfLgSlot(float* ret, DWORD, DWORD, DWORD, DWORD, DWORD) { ret[0] = 1, ret[1] = 2, ret[2] = 3; return ret; }
static DWORD __cdecl AfLgObjs(DWORD type) { return type + 1; }
static DWORD __cdecl AfLgRange(DWORD type, float, float, float, float r) { return type + (DWORD)r; }
static void* g_lgFakeTab[] = {
    (void*)AfLgRand, (void*)"ScriptCore.Random::Random_NextDoubleImpl",
    (void*)AfLgSlot, (void*)"ScriptCore.Slots::Slots_GetPositionOfSlotImpl",
    (void*)AfLgObjs, (void*)"ScriptCore.Queries::Query_GetObjects",
    (void*)AfLgRange, (void*)"ScriptCore.Queries::Query_GetObjectsInRange",
    NULL, NULL};
static DWORD g_lgInfo0[3] = {0, 0, 0x111}, g_lgInfo1[3] = {0, 0, 0x222};
static DWORD g_lgFrame1[2] = {0, (DWORD)(uintptr_t)g_lgInfo1};
static DWORD g_lgFrame0[2] = {(DWORD)(uintptr_t)g_lgFrame1, (DWORD)(uintptr_t)g_lgInfo0};
static DWORD g_lgCtxOk[3] = {0, 0, (DWORD)(uintptr_t)g_lgFrame0}, g_lgCtxBad[3] = {0, 0, 0x10};
static const DWORD* g_lgCtxNow = g_lgCtxOk;
static const u8* __cdecl AfLgCtx() { return (const u8*)g_lgCtxNow; }

static void TestAutLedger() {
    Ts3nAutShadow& k = ts3native_autshadow;
    assert(offsetof(Ts3nAutShadow, lgOn) == 0x13C && offsetof(Ts3nAutShadow, lgIn) == 0x158 && offsetof(Ts3nAutShadow, lgSleep) == 0x17C &&
           offsetof(Ts3nAutShadow, lgCls) == 0x1A0 && offsetof(Ts3nAutShadow, lgCall) == 0x260 && offsetof(Ts3nAutShadow, abort2) == 0x760 && offsetof(Ts3nAutShadow, rngPauseDraws) == 0x76C && offsetof(Ts3nAutShadow, abortRoute) == 0x770 &&
           offsetof(Ts3nAutShadow, overrunIdle) == 0x778 && offsetof(Ts3nAutShadow, windowWaitUs) == 0x780 && k.version == 21 && offsetof(Ts3nAutShadow, satbMode) == 0x9C0 && offsetof(Ts3nAutShadow, satbWGiveUpOp) == 0xA5C &&
           offsetof(Ts3nAutShadow, satbIcLogged) == 0xA60 && offsetof(Ts3nAutShadow, refPassA4) == 0xA84 && offsetof(Ts3nAutShadow, pairT) == 0xA88 && offsetof(Ts3nAutShadow, pairN) == 0xA98 &&
           offsetof(Ts3nAutShadow, transOn) == 0xAA8 && offsetof(Ts3nAutShadow, runNo) == 0xAE4 && offsetof(Ts3nAutShadow, clsGoneSkip) == 0xAF8 &&
           offsetof(Ts3nAutShadow, seOn) == 0xAFC && offsetof(Ts3nAutShadow, tmOn) == 0xB10 && offsetof(Ts3nAutShadow, gdOn) == 0xB20 && offsetof(Ts3nAutShadow, gdOver) == 0xB40 &&
           offsetof(Ts3nAutShadow, idOn) == 0xB44 && offsetof(Ts3nAutShadow, seLearnOn) == 0xB64 && offsetof(Ts3nAutShadow, gdWhy) == 0xB7C);  // registry v3-v6 (v7, v8: TestAutShadowFull), v15 Satb, v16, v17 mono-cost15, v18 mono-cost16, v19 mono-cost17
    assert(AsIcConfig(5) == 0 && AsIcConfig(7) == 0 && AsLgWatchOf("ScriptCore.Queries::Query_GetObjects") < 0);  // Ledger=0: inert
    g_asLedger = true;
    const u8* base = (const u8*)GetModuleHandleW(NULL);
    const IMAGE_NT_HEADERS* nt = (const IMAGE_NT_HEADERS*)(base + ((const IMAGE_DOS_HEADER*)base)->e_lfanew);
    const IMAGE_SECTION_HEADER* sec = IMAGE_FIRST_SECTION(nt);
    DWORD textLo = 0, textHi = 0;
    for (int i = 0; i < nt->FileHeader.NumberOfSections && !textLo; i++)
        if (sec[i].Characteristics & IMAGE_SCN_MEM_EXECUTE) textLo = (DWORD)(uintptr_t)base + sec[i].VirtualAddress, textHi = textLo + sec[i].Misc.VirtualSize;
    const DWORD imgLo = (DWORD)(uintptr_t)base, imgHi = imgLo + nt->OptionalHeader.SizeOfImage;
    const int n0 = g_asIcN, nTab = (int)(sizeof(g_lgFakeTab) / sizeof(g_lgFakeTab[0]));
    AsInstallGuards((DWORD*)g_lgFakeTab, (DWORD*)g_lgFakeTab + nTab, imgLo, imgHi, textLo, textHi, false);
    assert(g_asIcN == n0 + 4);
    for (int i = 0; i < 4; i++) {
        const u8* t = g_asThunks + (n0 + i) * 10;
        assert(g_asLgWatch[n0 + i] == i + 1 && g_lgFakeTab[i * 2] == (void*)t);
        assert((DWORD)(uintptr_t)t + 10 + *(const LONG*)(t + 6) == (DWORD)(uintptr_t)AsLedgerEntry);
    }
    double(__cdecl* rnd)() = (double(__cdecl*)())g_lgFakeTab[0];
    float*(__cdecl* slot)(float*, DWORD, DWORD, DWORD, DWORD, DWORD) = (float*(__cdecl*)(float*, DWORD, DWORD, DWORD, DWORD, DWORD))g_lgFakeTab[2];
    DWORD(__cdecl* objs)(DWORD) = (DWORD(__cdecl*)(DWORD))g_lgFakeTab[4];
    DWORD(__cdecl* range)(DWORD, float, float, float, float) = (DWORD(__cdecl*)(DWORD, float, float, float, float))g_lgFakeTab[6];
    const u8*(__cdecl* ctx0)() = g_lgCtx;
    g_lgCtx = AfLgCtx;
    AsU64& task = *(AsU64*)(g_t5Host + 0xBD0);
    task = 7;
    const int top = AsFpTop();
    // outside every scope: counted, answered by the body
    assert(rnd() == 0.5 && k.lgOut[0] == 1 && k.lgIn[0] == 0 && AsFpTop() == top);
    assert(AsIcConfig(7) == 1 && AsIcConfig(5) == 1 && k.lgScopes == 1 && (k.lgOn & 4));
    assert(rnd() == 0.5 && k.lgIn[0] == 1 && k.lgCall[0][0] == 0 && k.lgCall[0][1] == 1 && k.lgCall[0][2] == 0x111 &&
           k.lgCall[0][3] == 0x222 && k.lgCall[0][4] == 0 && (k.lgOn & 8) && (k.lgOn & 16));
    float out[3] = {0, 0, 0};
    assert(slot(out, 9, 5, 1, 2, 0) == out && out[0] == 1 && out[2] == 3);
    slot(out, 10, 5, 1, 2, 0);
    slot(out, 9, 5, 1, 2, 0);
    assert(k.lgIn[1] == 3 && k.lgSlotIds == 2);
    static DWORD klass[16];
    static DWORD type[2] = {(DWORD)(uintptr_t)klass, 0x12};
    static DWORD refl[3] = {0x51, 0, (DWORD)(uintptr_t)type};
    const DWORD r = (DWORD)(uintptr_t)refl;
    assert(objs(r) == r + 1 && objs(r) == r + 1 && range(r, 1, 2, 3, 4) == r + 4);
    assert(k.lgCls[0][0] == 2 && (DWORD)k.lgCls[0][1] == (DWORD)(uintptr_t)klass && k.lgCls[0][2] == 2);
    assert(k.lgCls[1][0] == 3 && k.lgCls[1][2] == 1 && k.lgIn[2] == 2 && k.lgIn[3] == 1);
    assert(k.lgCall[1][0] == 1 && k.lgCall[1][1] == 3 && k.lgCall[2][0] == 2 && k.lgCall[3][0] == 3);
    // another task runs while B13 sleeps: outside
    task = 8;
    rnd();
    assert(k.lgOut[0] == 2 && k.lgIn[0] == 1);
    // nested open and close on the scope's task
    task = 7;
    assert(AsIcConfig(5) == 1 && k.lgScopes == 1);
    AsIcConfig(6);
    rnd();
    assert(k.lgIn[0] == 2 && k.lgCall[0][1] == 2);
    // a bad frame chain drops the record, never the call
    g_lgCtxNow = g_lgCtxBad;
    assert(rnd() == 0.5 && k.lgIn[0] == 3 && k.lgDropped == 1);
    g_lgCtxNow = g_lgCtxOk;
    AsIcConfig(6);
    rnd();
    assert(k.lgOut[0] == 3 && k.lgScopeUs >= 0 && k.lgScopeFull == 0);
    AsIcConfig(6);
    assert(k.lgScopeFull == 1);
    // Simulate ticks: the EA memory tracking word
    volatile LONG track = 0;
    volatile LONG* track0 = g_lgTrack;
    g_lgTrack = &track;
    AsSimEnter();
    track = 3;
    AsLgTick();
    track = 0;
    AsLgTick();
    assert(k.lgTicks == 3 && k.lgTrackTicks == 1 && k.lgTrackLast == 3 && (k.lgOn & 2) && AsIcConfig(8) == 3);
    // Sleep probe: ticks between two marks of a series
    assert(AsIcConfig(9) == -1);
    AsLgTick();
    assert(AsIcConfig(9) == 1 && AsIcConfig(9) == 0);
    assert(AsIcConfig(10) == -1);
    AsLgTick(), AsLgTick(), AsLgTick(), AsLgTick();
    assert(AsIcConfig(10) == 4);
    assert(k.lgSleep[0] == 1 && k.lgSleep[1] == 1 && k.lgSleep[2] == 0 && k.lgSleep[7] == 1);
    g_lgTrack = track0, g_lgCtx = ctx0, g_asLedger = false;
    task = 0;
    printf("autshadow ledger: in %ld/%ld/%ld/%ld, out %ld, dropped %ld, ticks %ld, sleep(0) %ld/%ld, sleep(1) 3+ %ld\n", k.lgIn[0],
           k.lgIn[1], k.lgIn[2], k.lgIn[3], k.lgOut[0], k.lgDropped, k.lgTicks, k.lgSleep[0], k.lgSleep[1], k.lgSleep[7]);
}

// T6 (docs/122 §14.14): a full table evicts least recently used rows instead of refusing keys. mono-cost5 BP: memo rows
// filled all 1,024 rows at load and 98.8% of the worker's aborts were `row` misses that could never be learned.
static void TestAutShadowFull() {
    Ts3nAutShadow& k = ts3native_autshadow;
    assert(offsetof(Ts3nAutShadow, evicted) == 0x784 && offsetof(Ts3nAutShadow, learnBacklog) == 0x7A4 && offsetof(Ts3nAutShadow, cutLearnUs) == 0x7A8 &&
           offsetof(Ts3nAutShadow, bodyN) == 0x7C0 && offsetof(Ts3nAutShadow, coldSkips) == 0x7E0 && offsetof(Ts3nAutShadow, workCsr) == 0x7F8 &&
           sizeof(Ts3nAutShadow) == 0xC98 && offsetof(Ts3nAutShadow, alOn) == 0xB8C && k.version == 21);
    const LONG full0 = k.full, ev0 = k.evicted;
    const int audit0 = g_asAudit;
    g_asAudit = 0;  // the timed cut below is the production cut (Audit=50 adds a bounded 1,024-row slice every 50th)
    g_asWorkerTid = 0;
    // memo flood on the sim: distinct out in-values are distinct keys; the quota holds, older memo rows go first
    const AsFakeStr s = AfStr(L"Abc");
    for (DWORD i = 0; i < (DWORD)AS_MEMO * 2; i++) {
        DWORD out = 0x10000 + i;
        assert(((char(__cdecl*)(const AsFakeStr*, DWORD*))AfSlot(AF_OPTION))(&s, &out) == 1 && out == 3 * 3 + L'A');
    }
    assert(g_asMemoN <= AS_MEMO && k.evictedMemo > 0 && k.full == full0);
    AsCut();
    // junk keys asked once each: more than the table holds, learned through the ring at AS_LEARN_CUT per cut
    auto ask = [](DWORD lo) { return ((short(__cdecl*)(DWORD, DWORD))AfSlot(AF_TOMB))(lo, 7); };
    auto run = [&](DWORD lo0, DWORD n) {  // one worker run asking n keys; returns the hits
        const LONG h0 = k.workerHits;
        g_asWorkerTid = GetCurrentThreadId();
        AsWorkerBegin();
        for (DWORD i = 0; i < n; i++) { ask(lo0 + i); AsResetAbort(); }
        AsWorkerEnd();
        g_asWorkerTid = 0;
        return k.workerHits - h0;
    };
    for (DWORD r = 0; r < 12; r++) {
        run(100000 + r * (DWORD)AS_RING, (DWORD)AS_RING);
        for (int c = 0; c < AS_RING / AS_LEARN_CUT; c++) AsCut();
    }
    assert(k.evicted > ev0 && k.entriesMax == AS_CAP && k.full == full0 && k.learnWantMax == AS_LEARN_CUT);
    // a working set the definitions keep asking for: learned within a few cuts and then answered, despite the full table
    const DWORD W = 900;
    LONG hits = 0;
    LARGE_INTEGER t0 = {}, t1 = {}, f;
    for (int c = 0; c < 8; c++) {
        hits = run(5000000, W);
        if (c == 7) QueryPerformanceCounter(&t0);
        AsCut();
        if (c == 7) QueryPerformanceCounter(&t1);
    }
    QueryPerformanceFrequency(&f);
    assert(hits == (LONG)W && k.full == full0 && g_as->m.n <= AS_CAP);
    // and the next flood keeps the working set: it was used at the last cut, the junk was not
    run(9000000, (DWORD)AS_RING);
    for (int c = 0; c < AS_RING / AS_LEARN_CUT; c++) AsCut();
    hits = run(5000000, W);
    assert(hits == (LONG)W);
    const LONG r0 = k.refreshed;
    LARGE_INTEGER t2, t3;
    QueryPerformanceCounter(&t2);
    AsCut();
    QueryPerformanceCounter(&t3);
    printf("autshadow full table: %ld rows, %ld evicted (%ld memo), %ld memo rows, cut at a full table %lld us after a run, %lld us idle "
           "(%ld body calls, tracked %d), %u bytes\n", g_as->m.n, k.evicted, k.evictedMemo, k.memoRows, (t1.QuadPart - t0.QuadPart) * 1000000 / f.QuadPart,
           (t3.QuadPart - t2.QuadPart) * 1000000 / f.QuadPart, k.refreshed - r0, g_asTracked ? 1 : 0, (unsigned)sizeof(AsMem));
    g_asAudit = audit0;
}

// docs/122 §14.15: ColdIc=1 leaves stale rows of ICs the worker does not read unrefreshed and publishes them cold; a worker
// read aborts (never a stale answer) and the IC is refreshed from the next cut. Plus the worker's FPU state around a job.
static void TestAutShadowCold() {
    Ts3nAutShadow& k = ts3native_autshadow;
    const int audit0 = g_asAudit;
    g_asAudit = 1;  // every cut audits a slice: cold rows are skipped, refreshed ones must match
    g_asColdIc = 1;
    g_asWorkerTid = 0;
    const DWORD key3[2] = {3, 5};
    auto hidden = []() {  // one worker read of object 3's hidden flags
        g_asWorkerTid = GetCurrentThreadId();
        AsWorkerBegin();
        const DWORD h = ((DWORD(__cdecl*)(DWORD, DWORD))AfSlot(AF_HIDDEN))(3, 5);
        AsWorkerEnd();
        g_asWorkerTid = 0;
        return h;
    };
    for (int i = 1; i <= 64; i++) AsAddRow(AfId(i), false);
    AsCut();
    // a Sim parented to an object with no rows (a seat, a vehicle): the object gets rows at the cut after the parent answer
    const DWORD key70[2] = {70, 5};
    const LONG pr0 = k.parentRows;
    g_asIcHit[AF_PARENT] = g_asCut;  // the worker reads parents (hot under ColdIc)
    g_afo[1].parent = AfId(70);
    AsAddRowKind(AfId(1), 2);
    AsObjEvDirty(&g_afo[1], NULL);
    AsCut();
    assert(AsFind(g_as->m, AF_POS, key70, 2) < 0);
    AsCut();
    assert(k.parentRows >= pr0 + 1 && AsFind(g_as->v[g_asPub], AF_POS, key70, 2) >= 0 && AsFind(g_as->v[g_asPub], AF_PARENT, key70, 2) >= 0);
    g_afo[1].parent = 0;
    AsObjEvDirty(&g_afo[1], NULL);
    AsCut();
    g_asIcHit[AF_HIDDEN] = 0;  // never read: cold
    const LONG skips0 = k.coldSkips, reads0 = k.coldReads, refr0 = k.refreshed, mis0 = k.auditMis[0];
    g_afo[3].hidden ^= 1;
    AsObjEvDirty(&g_afo[3], NULL);
    AsCut();
    int at = AsFind(g_as->v[g_asPub], AF_HIDDEN, key3, 2);
    assert(at >= 0 && (g_as->v[g_asPub].e[at].flags & AE_COLD) && k.coldSkips > skips0 && k.coldIcs >= 1);
    assert(k.refreshed - refr0 > 0);  // the hot ICs of the dirty object were refreshed
    assert(hidden() == 0 && AsAborted() == AS_AB_ROW && k.coldReads == reads0 + 1);
    AsResetAbort();
    AsCut();  // the heat record: the IC is hot, its cold rows are refreshed now
    at = AsFind(g_as->v[g_asPub], AF_HIDDEN, key3, 2);
    assert(at >= 0 && !(g_as->v[g_asPub].e[at].flags & AE_COLD) && g_asIcHit[AF_HIDDEN] == g_asCut);
    assert(hidden() == g_afo[3].hidden && AsAborted() == -1 && k.auditMis[0] == mis0);
    AfCheckPublished();
    // hot ICs stay refreshed on change
    g_afo[3].hidden ^= 1;
    AsObjEvDirty(&g_afo[3], NULL);
    AsCut();
    assert(hidden() == g_afo[3].hidden && AsAborted() == -1 && k.auditMis[0] == mis0);
    g_asColdIc = 0;
    AsCut();
    g_asAudit = audit0;
    // FPU: the job runs with the sim's x87 control word and MXCSR, the worker's own come back after it
    void* const work0 = g_asMWork;
    g_asMWork = (void*)(uintptr_t)0x5151;
    const unsigned short cw0 = GetX87();
    const unsigned int csr0 = _mm_getcsr();
    g_asSimCw = 0x007F, g_asSimCsr = csr0 | 0x8000;  // 24-bit precision, flush to zero
    const LONG runEpoch0 = g_asRunEpoch;
    g_asRunEpoch = g_asEpoch;
    AsJobHook(0, g_asMWork);
    assert(GetX87() == 0x007F && _mm_getcsr() == (csr0 | 0x8000) && k.workX87 == cw0 && k.workCsr == (LONG)csr0);
    AsJobHook(1, g_asMWork);
    assert(GetX87() == cw0 && _mm_getcsr() == csr0 && !g_asFpuSet);
    g_asSimCw = 0;  // no cut captured: the worker keeps its own state
    AsJobHook(0, g_asMWork);
    assert(GetX87() == cw0 && !g_asFpuSet);
    AsJobHook(1, g_asMWork);
    assert(GetX87() == cw0);
    g_asMWork = work0, g_asRunEpoch = runEpoch0;
    InterlockedExchange(&g_asStop, 0);
    printf("autshadow cold ICs: %ld rows skipped, %ld cold reads; FPU state applied and restored around the job\n", k.coldSkips, k.coldReads);
}

// T8 (docs/122 §14.16): CutLean=1 change-only lists and TierMs Sim tiers. Invariant: every published row the worker may
// answer (not memo, ref, cold or stale) equals a fresh body call, after random event sequences; a stale tier-2 row aborts.
static AsU64 AfPrintMix(AsU64 h, DWORD v) { h ^= v; return h * 0x100000001B3ull; }
static bool g_afPrintFail;
static bool AfPrint(AsU64 lot, AsU64* p) {  // the fake bodies read g_afo[i].lot (and nothing else that changes)
    p[0] = p[1] = 0xCBF29CE484222325ull;
    for (int i = 1; i <= 64; i++)
        if (g_afo[i].lot == (DWORD)lot) p[0] = AfPrintMix(p[0], i), p[1] = AfPrintMix(AfPrintMix(p[1], i), g_afo[i].hidden);
    return !g_afPrintFail;
}
static DWORD g_afNow = 1000;
static DWORD WINAPI AfNow() { return g_afNow; }
static bool g_afFar[65];
static int AfTier(AsU64 id) { return id && (DWORD)(id >> 32) == 5 && (DWORD)id <= 64 && g_afFar[(DWORD)id] ? 1 : 0; }
static void AfCheckAnswerable() {
    const AsTab& t = g_as->v[g_asPub];
    LONG xn = 0;
    for (int i = 0; i < t.n; i++) {
        const AsDesc& d = *g_asDescOf[t.e[i].ic];
        if (d.owner == AO_MEMO || d.kind == AK_REF || (t.e[i].flags & (AE_COLD | AE_STALE | AE_DISTRUST))) continue;
        AsEnt x = t.e[i];
        AsRefresh(x.ic, x, g_as->poolTmp, &xn);
        if (!AsSame(d, t.e[i], t.pool, x, g_as->poolTmp))
            printf("DBG ic %d owner %llx key %lx %lx %lx %lx flags %x cut %ld r %lx/%lx\n", t.e[i].ic, t.e[i].owner, t.e[i].key[0], t.e[i].key[1],
                   t.e[i].key[2], t.e[i].key[3], t.e[i].flags, g_asCut, t.e[i].r[0], x.r[0]);
        assert(AsSame(d, t.e[i], t.pool, x, g_as->poolTmp));
        xn = 0;
    }
}
// the game print (AsLotPrintGame) on fake EA structures: lot vector +0x14, world lot [mgr +0xB0], component lot +8, room +0x20
struct AfGComp { DWORD pad[2]; AsU64 lot; DWORD pad2[4]; DWORD room; };
struct AfGObj { DWORD vt, pad, idLo, idHi; AfGComp* comp; };
struct AfGLot { u8 pad[0x14]; AfGObj** b; AfGObj** e; };
static AfGObj g_agObj[8];
static AfGComp g_agComp[8];
static AfGObj* g_agVec[2][8];
static AfGLot g_agLot[2];  // [0] lot 0x700000001, [1] the world lot
static u8 g_agMgr[0xB4];
static void* g_agMgrP = g_agMgr;
static DWORD g_agBit = 0x40;
static const u8* __fastcall AfGCompFn(const void* o, void*, DWORD bit) { assert(bit == 0x40); return (const u8*)((const AfGObj*)o)->comp; }
static void* __fastcall AfGFind(void* mgr, void*, DWORD lo, DWORD hi, DWORD z) { assert(mgr == g_agMgr && !z); return lo == 1 && hi == 7 ? &g_agLot[0] : NULL; }
static void TestAutShadowLotPrint() {
    for (int i = 0; i < 8; i++) g_agObj[i].idLo = 100 + i, g_agObj[i].idHi = 5, g_agObj[i].comp = &g_agComp[i], g_agComp[i].room = 3;
    for (int i = 0; i < 3; i++) g_agVec[0][i] = &g_agObj[i];  // objects 0-2 on the lot
    for (int i = 0; i < 4; i++) g_agVec[1][i] = &g_agObj[4 + i], g_agComp[4 + i].lot = i < 2 ? 0x700000001ull : 0x700000002ull;  // Sims 4, 5 on it
    g_agLot[0].b = g_agVec[0], g_agLot[0].e = g_agVec[0] + 3, g_agLot[1].b = g_agVec[1], g_agLot[1].e = g_agVec[1] + 4;
    *(void**)(g_agMgr + 0xB0) = &g_agLot[1];
    void* const* wm = g_asWmPP; const AsFindLotFn fl = g_asFindLot; const AsCompFn cf = g_asComp; const DWORD* cb = g_asCompBit;
    g_asWmPP = &g_agMgrP, g_asFindLot = AfGFind, g_asComp = AfGCompFn, g_asCompBit = &g_agBit;
    AsU64 a[2], b[2], c[2];
    const AsU64 L = 0x700000001ull;
    assert(AsLotPrintGame(L, a) && AsLotPrintGame(L, b) && a[0] == b[0] && a[1] == b[1] && a[0] != a[1]);
    g_agComp[6].room = 9;  // a room change of a world object on another lot: no change
    g_agComp[1].room = 9;  // a room change on the lot: room calls only
    assert(AsLotPrintGame(L, b) && b[0] == a[0] && b[1] != a[1]);
    g_agComp[6].lot = L;   // a Sim walks onto the lot
    assert(AsLotPrintGame(L, c) && c[0] != b[0] && c[1] != b[1]);
    g_agComp[6].lot = 0x700000002ull;
    g_agLot[0].e = g_agVec[0] + 2;  // an object leaves the lot vector
    assert(AsLotPrintGame(L, c) && c[0] != b[0]);
    g_agLot[0].e = g_agVec[0] + 3;
    g_agVec[0][0] = &g_agObj[1], g_agVec[0][1] = &g_agObj[0];  // same objects, other order: other answer order
    assert(AsLotPrintGame(L, c) && c[0] != b[0]);
    g_agVec[0][0] = &g_agObj[0], g_agVec[0][1] = &g_agObj[1];
    assert(AsLotPrintGame(L, c) && c[0] == b[0] && c[1] == b[1]);
    AsU64 n1[2], n2[2];  // no such lot: one fixed print, not the empty lot's
    assert(AsLotPrintGame(0x700000003ull, n1) && AsLotPrintGame(0x700000004ull, n2) && n1[0] == n2[0] && n1[0] != c[0]);
    g_asWmPP = wm, g_asFindLot = fl, g_asComp = cf, g_asCompBit = cb;
}
static void TestAutShadowLean() {
    Ts3nAutShadow& k = ts3native_autshadow;
    TestAutShadowLotPrint();
    const int audit0 = g_asAudit;
    g_asAudit = 1;
    g_asLean = 1, g_asLotPrint = AfPrint;
    g_asTierMs = 1500, g_asTierFn = AfTier, g_asNowMs = AfNow;
    g_asColdIc = 0;
    const DWORD lk[4] = {3, 2, 0, 0}, lk1[4] = {4, 2, 0, 1};
    if (AsFind(g_as->m, AF_ONLOT, lk, 4) < 0) AsInsert(AF_ONLOT, lk, 4, 0);
    if (AsFind(g_as->m, AF_COUNT, lk1, 4) < 0) AsInsert(AF_COUNT, lk1, 4, 0);
    AsCut();
    // a list read on the worker: the body's answer, or an abort (never another answer)
    auto onLot = [](DWORD type, DWORD lot, DWORD fl, bool* ok) {
        g_asWorkerTid = GetCurrentThreadId();
        AsWorkerBegin();
        const DWORD* arr = ((DWORD*(__cdecl*)(DWORD, DWORD, DWORD, DWORD))AfSlot(AF_ONLOT))(type, lot, 0, fl);
        AsWorkerEnd();
        g_asWorkerTid = 0;
        *ok = AsAborted() == -1;
        AsResetAbort();
        const DWORD* fresh = AfOnLot(type, lot, 0, fl);
        if (*ok) assert(arr && arr[2] == fresh[2] && !memcmp(arr + 4, fresh + 4, fresh[2] * 4));
        return arr;
    };
    bool ok;
    onLot(3, 2, 0, &ok);
    assert(ok);
    // the generation moves (world event 0x11, a room change) but no object changes lot: no body call
    const LONG skip0 = k.listPrintSkips, list0 = k.bodyN[2];
    AsWorldEvent(0x11);
    AsCut();
    assert(k.listPrintSkips >= skip0 + 2 && k.bodyN[2] == list0 && k.auditMis[0] == 0);
    onLot(3, 2, 0, &ok);
    assert(ok);
    // an object moves onto lot 2: refreshed, the worker gets the new answer
    int mover = 1;
    while (g_afo[mover].lot == 2) mover++;
    g_afo[mover].lot = 2;
    AsWorldEvent(0x11);
    AsCut();
    assert(k.bodyN[2] >= list0 + 2);
    onLot(3, 2, 0, &ok);
    assert(ok);
    // a print that cannot be read refreshes
    g_afPrintFail = true;
    const LONG list1 = k.bodyN[2];
    AsWorldEvent(0x11);
    AsCut();
    assert(k.bodyN[2] >= list1 + 2);
    g_afPrintFail = false;
    // tiers: object 9 is a far unseen Sim the request reaches, object 2 the actor (far too, stays tier 1)
    g_afFar[9] = g_afFar[2] = true;
    memset(&g_as->always, 0, sizeof(AsSet));
    g_asActor = 0;
    AsAddRowKind(AfId(2), 1);
    AsAddRowKind(AfId(9), 1);
    assert(g_asActor == AfId(2));
    auto pos = [](DWORD i, float* out, bool* ok) {
        g_asWorkerTid = GetCurrentThreadId();
        AsWorkerBegin();
        out[0] = out[1] = out[2] = -1;
        ((float*(__cdecl*)(float*, DWORD, DWORD))AfSlot(AF_POS))(out, i, 5);
        AsWorkerEnd();
        g_asWorkerTid = 0;
        *ok = AsAborted() == -1;
        const int r = AsAborted();
        AsResetAbort();
        return r;
    };
    float p[3], want[3];
    g_afNow = 10000;
    const LONG t2s0 = k.tier2Stale, t2b0 = k.tierBodyN[1];
    AsCut();  // first sight: due
    assert(k.tier2Sims >= 1 && k.tierBodyN[1] > t2b0);
    pos(9, p, &ok);
    AfWorld(&g_afo[9], want);
    assert(ok && !memcmp(p, want, 12));
    for (int c = 0; c < 3; c++) {  // 400 ms later each: 9 waits (stale, abort simstale) once, then the read holds it at
        g_afNow += 400;              // tier 1 (docs/122 §14.17); the actor 2 is refreshed every cut
        g_afo[9].local[0] += 3, g_afo[2].local[0] += 3;
        AsObjEvDirty(&g_afo[9], NULL), AsObjEvDirty(&g_afo[2], NULL);
        AsCut();
        const int r9 = pos(9, p, &ok);
        AfWorld(&g_afo[9], want);
        assert(c == 0 ? r9 == AS_AB_SIMSTALE && !ok : ok && !memcmp(p, want, 12));
        pos(2, p, &ok);
        AfWorld(&g_afo[2], want);
        assert(ok && !memcmp(p, want, 12));
        AfCheckAnswerable();
    }
    assert(k.tier2Stale >= t2s0 + 1 && k.tierHold >= 1);
    memset(g_asTierTab, 0, sizeof(g_asTierTab));  // the hold ends
    g_afNow += 400;  // 1,600 ms after the last refresh: due again
    AsCut();
    pos(9, p, &ok);
    AfWorld(&g_afo[9], want);
    assert(ok && !memcmp(p, want, 12));
    // T2-style random sequences with both on: no answerable row ever differs from the body
    for (DWORD seed = 11; seed <= 13; seed++) {
        DWORD x = seed * 2654435761u;
        auto rnd = [&x](DWORD n) { x = x * 1664525u + 1013904223u; return (x >> 8) % n; };
        for (int step = 0; step < 2000; step++) {
            const int i = 1 + (int)rnd(64);
            AsFakeObj& o = g_afo[i];
            switch (rnd(8)) {
                case 0: case 1: o.local[0] += 1.5f; AsObjEvDirty(&o, NULL); break;
                case 2: o.hidden ^= 1; AsObjEvDirty(&o, NULL); break;
                case 3: g_afFar[i] = !g_afFar[i]; break;
                case 4: o.lot = 1 + rnd(4); if (rnd(2)) AsObjEvList(&o, NULL); else { AsObjEvDirty(&o, NULL); AsWorldEvent(0x11); } break;
                case 5: AsWorldEvent(0x11); break;  // a room change: generation only
                case 6: AsAddRowKind(AfId(i), rnd(2) ? 1 : 2); break;
                default: { const DWORD key[4] = {3 + rnd(2), 1 + rnd(4), 0, rnd(2)}; AsLearn(rnd(2) ? AF_ONLOT : AF_COUNT, key, 4); } break;
            }
            if (step % 7 == 6) {
                g_afNow += 300;
                AsCut();
                assert(k.auditMis[0] == 0);
                if (step % 35 == 34) AfCheckAnswerable();
            }
        }
    }
    assert(k.listPrintSkips > skip0 && k.tier2Stale > t2s0 && k.ageRuns > 0 && k.full == 0);
    printf("autshadow lean: %ld list rows kept by print (%ld prints, %ld us), %ld refreshed same; tiers %ld/%ld Sims, %ld tier-2 waits, Sim body calls %ld/%ld\n",
           k.listPrintSkips, k.listLots, k.listPrintUs, k.listSame, k.tier1Sims, k.tier2Sims, k.tier2Stale, k.tierBodyN[0], k.tierBodyN[1]);
    g_asLean = 0, g_asLotPrint = AsLotPrintGame;
    g_asTierMs = 0, g_asTierFn = AsTierOf, g_asNowMs = GetTickCount;
    memset(g_asTierTab, 0, sizeof(g_asTierTab));
    g_asAudit = audit0;
    AsCut();
}

// T9 (docs/122 §14.17): CutLean=2 dirty lots from object events, the ListAudit net (a missed event makes list reads abort,
// then the session goes back to prints), list tiers (a dirty far lot waits distrusted, a worker read holds it at tier 1),
// the tier cache (TierClassMs, camera move, hold). Invariant as T8: no answerable row ever differs from the body.
static bool g_afLotFail;
static bool AfObjLot(const void* o, AsU64* lot, DWORD* room) {
    const AsFakeObj* f = (const AsFakeObj*)o;
    *lot = f->lot, *room = f->hidden;
    return !g_afLotFail;
}
// lot-vector print on the fake world (stands for AsLotLiteGame): the objects whose lot is `lot`, Sims (simPtrs) apart
static bool g_afLiteBlind;  // the print reads g_afLiteLot (frozen lots): a change only the walker's world-vector part would show
static DWORD g_afLiteLot[65];
static bool AfLite(AsU64 lot, AsU64* p) {
    p[0] = p[1] = 0xCBF29CE484222325ull;
    for (int i = 1; i <= 64; i++)
        if ((g_afLiteBlind ? g_afLiteLot[i] : g_afo[i].lot) == (DWORD)lot) {
            AsU64& h = p[AsSetHas(g_as->simPtrs, (AsU64)(uintptr_t)&g_afo[i])];
            h = AfPrintMix(AfPrintMix(h, (DWORD)(uintptr_t)&g_afo[i]), i);
        }
    return true;
}
static bool g_afCam;
static int AfTier2(AsU64 id) { if (!id) { const bool c = g_afCam; g_afCam = false; return c ? 1 : 0; } return AfTier(id); }
static int AfListRead(DWORD ic, DWORD type, DWORD lot, DWORD fl) {  // worker read; returns the abort reason or -1 (then checked)
    g_asWorkerTid = GetCurrentThreadId();
    AsWorkerBegin();
    DWORD got;
    if (ic == AF_ONLOT) got = (DWORD)(uintptr_t)((DWORD*(__cdecl*)(DWORD, DWORD, DWORD, DWORD))AfSlot(AF_ONLOT))(type, lot, 0, fl);
    else got = ((DWORD(__cdecl*)(DWORD, DWORD, DWORD, DWORD))AfSlot(AF_COUNT))(type, lot, 0, fl);
    AsWorkerEnd();
    g_asWorkerTid = 0;
    const int r = AsAborted();
    AsResetAbort();
    if (r == -1) {
        if (ic == AF_COUNT) assert(got == AfCount(type, lot, 0, fl));
        else {
            const DWORD* arr = (const DWORD*)(uintptr_t)got;
            const DWORD* fresh = AfOnLot(type, lot, 0, fl);
            assert(arr && arr[2] == fresh[2] && !memcmp(arr + 4, fresh + 4, fresh[2] * 4));
        }
    }
    return r;
}
static int AfSimRead(int i) {  // worker position read of fake Sim i; -1 = answered (checked against the body)
    float p[3], want[3];
    g_asWorkerTid = GetCurrentThreadId();
    AsWorkerBegin();
    ((float*(__cdecl*)(float*, DWORD, DWORD))AfSlot(AF_POS))(p, (DWORD)i, 5);
    AsWorkerEnd();
    g_asWorkerTid = 0;
    const int r = AsAborted();
    AsResetAbort();
    AfWorld(&g_afo[i], want);
    if (r == -1) assert(!memcmp(p, want, 12));
    return r;
}
static LONG AfLotRows(DWORD lot, bool roomOnly) {  // list rows of a lot in the master (room rows only: room != -1)
    LONG n = 0;
    for (int i = 0; i < g_as->m.n; i++) {
        const AsEnt& e = g_as->m.e[i];
        n += g_asDescOf[e.ic]->owner == AO_LIST && e.owner == lot && (!roomOnly || e.key[3] != 0xFFFFFFFFu);
    }
    return n;
}
static int AfFirstOn(DWORD lot, int not1 = 0) { int i = 1; while (i <= 64 && (g_afo[i].lot != lot || i == not1)) i++; assert(i <= 64); return i; }
// seeding on the fake world (stands for AsSeedGame, docs/122 §14.19): every object whose lot is one of the lots
static bool AfSeed(const AsSet& lots) { for (int i = 1; i <= 64; i++) if (AsSetHas(lots, g_afo[i].lot)) AsSeedObj(&g_afo[i]); return true; }
static void TestAutShadowDirty() {
    Ts3nAutShadow& k = ts3native_autshadow;
    const int audit0 = g_asAudit;
    g_asAudit = 1;
    g_asLean = 2, g_asLotPrint = AfPrint, g_asObjLot = AfObjLot, g_asLotLite = AfLite, g_asSeed = AfSeed, g_asListAudit = 0, g_asColdIc = 0;
    for (int i = 1; i <= 64; i++) g_afo[i].lot = 1 + i % 4;
    AsEpochBump();
    AsCut();
    for (DWORD lot = 1; lot <= 4; lot++) {
        const DWORD a[4] = {3, lot, 0, 0}, b[4] = {4, lot, 0, 1};
        if (AsFind(g_as->m, AF_ONLOT, a, 4) < 0) AsInsert(AF_ONLOT, a, 4, 0);
        if (AsFind(g_as->m, AF_COUNT, b, 4) < 0) AsInsert(AF_COUNT, b, 4, 0);
    }
    const DWORD rm1[4] = {3, 3, 0, 0xFFFFFFFFu};  // a room -1 row on lot 3
    if (AsFind(g_as->m, AF_ONLOT, rm1, 4) < 0) AsInsert(AF_ONLOT, rm1, 4, 0);
    for (int i = 1; i <= 64; i++) AsObjEvDirty(&g_afo[i], NULL);
    AsCut();  // new rows, every object placed (first sight: their lots dirty)
    AsCut();  // nothing touched
    LONG body = k.bodyN[2];
    const LONG kept0 = k.dirtyKept, prints0 = k.listLots;
    AsWorldEvent(0x11);  // the generation moves, no object event: no print, no body
    AsCut();
    assert(k.bodyN[2] == body && k.dirtyKept >= kept0 + 9 && k.listLots == prints0);
    g_afo[5].local[0] += 1;  // a move inside its lot and room: placed, nothing dirty
    AsObjEvDirty(&g_afo[5], NULL);
    AsCut();
    assert(k.bodyN[2] == body && k.dirtyUnknown == 0);
    AfCheckAnswerable();
    // a lot change 1 -> 2: the old and the new lot's rows (2 + 2), not the others
    const int mv = AfFirstOn(1);
    g_afo[mv].lot = 2;
    AsObjEvDirty(&g_afo[mv], NULL);
    const LONG want0 = AfLotRows(1, false) + AfLotRows(2, false);  // before the cut: an aging pass in it may drop learned rows it refreshed
    AsCut();
    assert(k.bodyN[2] == body + want0);
    body = k.bodyN[2];
    assert(AfListRead(AF_ONLOT, 3, 2, 0) == -1 && AfListRead(AF_COUNT, 4, 1, 1) == -1);
    // a room change on lot 3: its two room rows, not its room -1 row
    const int rc = AfFirstOn(3);
    g_afo[rc].hidden ^= 1;
    AsObjEvDirty(&g_afo[rc], NULL);
    const LONG want1 = AfLotRows(3, true);  // before the cut: an aging pass in it may drop learned rows it refreshed
    AsCut();
    assert(k.bodyN[2] == body + want1 && AfLotRows(3, true) < AfLotRows(3, false));
    body = k.bodyN[2];
    AfCheckAnswerable();
    // a destroy (event 5): the lot it was placed on
    const int gone = AfFirstOn(4);
    AsObjEvGone(&g_afo[gone], NULL);
    g_afo[gone].lot = 0;
    const LONG want2 = AfLotRows(4, false);  // before the cut: an aging pass in it may drop learned rows it refreshed
    AsCut();
    assert(k.bodyN[2] == body + want2 && AfListRead(AF_ONLOT, 3, 4, 0) == -1);
    AsEpochBump();  // forget the destroyed id (the map clears at the epoch)
    AsCut();
    // destroyed before any cut placed it: its lot is read at the event
    const int early = AfFirstOn(1);
    AsObjEvGone(&g_afo[early], NULL);
    g_afo[early].lot = 0;
    const LONG unk0 = k.dirtyUnknown;
    AsCut();
    assert(k.dirtyUnknown == unk0 && AfListRead(AF_ONLOT, 3, 1, 0) == -1);
    // an object that cannot be placed: no resync (docs/122 §14.18), its lot found by the lot-vector print
    AsEpochBump();
    AsCut();
    g_afLotFail = true;
    g_afo[early].lot = 3;
    AsObjEvDirty(&g_afo[early], NULL);
    const LONG unp0 = k.unplaced, lite0 = k.liteLots;
    AsCut();
    g_afLotFail = false;
    assert(k.dirtyUnknown == unk0 && k.unplaced == unp0 + 1 && k.liteLots > lite0 && AfListRead(AF_ONLOT, 3, 3, 0) == -1);
    AfCheckAnswerable();
    AsEpochBump();
    AsCut();
    for (int i = 1; i <= 64; i++) AsObjEvDirty(&g_afo[i], NULL);
    AsCut();
    // the safety net: a lot change with no event that the lot-vector print cannot see either (as a world-vector object's
    // would be). ListAudit finds it, every list row aborts this cut, prints from the next
    g_asListAudit = 64;
    const int miss = AfFirstOn(1);
    g_afo[miss].lot = 4;
    assert(k.listMissed == 0 && !g_asDirtyTrip);
    for (int i = 1; i <= 64; i++) g_afLiteLot[i] = g_afo[i].lot;
    g_afLiteLot[miss] = 1;
    g_afLiteBlind = true;
    AsCut();
    g_afLiteBlind = false;
    assert(k.listMissed >= 1 && g_asDirtyTrip && k.dirtyTripped == 1 && k.auditMis[0] == 0);
    assert(AfListRead(AF_ONLOT, 3, 4, 0) == AS_AB_LIST && AfListRead(AF_COUNT, 4, 1, 1) == AS_AB_LIST);
    AsCut();
    assert(AfListRead(AF_ONLOT, 3, 4, 0) == -1 && AfListRead(AF_COUNT, 4, 1, 1) == -1);
    AfCheckAnswerable();
    const LONG missed = k.listMissed;
    g_asDirtyTrip = false, k.dirtyTripped = 0;
    AsEpochBump();
    AsCut();
    for (int i = 1; i <= 64; i++) AsObjEvDirty(&g_afo[i], NULL);
    AsCut();
    // list tiers: the actor stands on lot 1 (tier 1), lot 3 has no tier-1 Sim
    g_asTierMs = 1500, g_asTierFn = AfTier2, g_asNowMs = AfNow;
    g_afNow = 50000;
    memset(g_afFar, 1, sizeof(g_afFar));
    const int act = AfFirstOn(1);
    memset(&g_as->always, 0, sizeof(AsSet));
    g_asActor = 0;
    AsAddRowKind(AfId(act), 1);
    AsCut();
    assert(AsSetHas(g_as->t1Lots, 1) && !AsSetHas(g_as->t1Lots, 3) && k.t1Lots >= 1);
    auto moveOnto = [act](DWORD lot) { int i = 1; while (g_afo[i].lot == lot || g_afo[i].lot == 0 || i == act) i++; g_afo[i].lot = lot; AsObjEvDirty(&g_afo[i], NULL); };
    moveOnto(3);  // first dirty sight of lot 3: due
    g_afNow += 300;
    AsCut();
    assert(AfListRead(AF_ONLOT, 3, 3, 0) == -1);
    const LONG wait0 = k.listT2Wait, hold0 = k.tierHold;
    moveOnto(3);  // 300 ms later: waits, distrusted
    g_afNow += 300;
    AsCut();
    assert(k.listT2Wait > wait0 && AfListRead(AF_ONLOT, 3, 3, 0) == AS_AB_LIST);
    moveOnto(1);  // the actor's lot is never held back
    g_afNow += 300;
    AsCut();  // the read above holds lot 3: refreshed now
    assert(k.tierHold > hold0 && AfListRead(AF_ONLOT, 3, 3, 0) == -1 && AfListRead(AF_ONLOT, 3, 1, 0) == -1);
    AfCheckAnswerable();
    // tier cache: no reclassification inside TierClassMs; a camera move or the interval classifies again
    g_afNow += 2000;
    AsCut();
    const LONG cls0 = k.tierClassN, cam0 = k.tierCamInval;
    g_afNow += 400;
    AsCut();
    assert(k.tierClassN == cls0);
    g_afCam = true;
    g_afNow += 100;
    AsCut();
    assert(k.tierCamInval == cam0 + 1 && k.tierClassN > cls0);
    // docs/122 §14.18 triggers: after TierClassMs only a Sim placed since its last classification; after 4 x every one
    const int mover = act == 2 ? 3 : 2;
    AsAddRowKind(AfId(mover), 1);
    g_afNow += 1100;
    AsCut();  // settle (the mover may be new to the sets)
    const LONG cls2 = k.tierClassN;
    g_afNow += 1100;
    g_afo[mover].local[0] += 1;
    AsObjEvDirty(&g_afo[mover], NULL);
    AsCut();  // placed by a touch since: classified again; the others keep theirs
    assert(k.tierClassN == cls2 + 1);
    g_afNow += 4100;
    AsCut();
    assert(k.tierClassN > cls2 + 1 && k.tierKept > 0);
    // hold: a worker read of a waiting tier-2 Sim keeps it tier 1 (refreshed every cut) for AS_TIER_HOLD cuts
    const int farSim = act == 2 ? 3 : 2;
    AsAddRowKind(AfId(farSim), 1);
    g_afNow += 2000;
    AsCut();  // due: refreshed
    g_afNow += 300;
    g_afo[farSim].local[0] += 2;
    AsObjEvDirty(&g_afo[farSim], NULL);
    AsCut();  // waits
    assert(AfSimRead(farSim) == AS_AB_SIMSTALE);
    for (int c = 0; c < 3; c++) {
        g_afNow += 300;
        g_afo[farSim].local[0] += 2;
        AsObjEvDirty(&g_afo[farSim], NULL);
        AsCut();
        assert(AfSimRead(farSim) == -1);
    }
    // random sequences (as T8, every change with its event): no answerable row ever differs, no audit miss
    g_asListAudit = 4;
    memset(g_afFar, 0, sizeof(g_afFar));
    for (DWORD seed = 21; seed <= 23; seed++) {
        DWORD x = seed * 2654435761u;
        auto rnd = [&x](DWORD n) { x = x * 1664525u + 1013904223u; return (x >> 8) % n; };
        for (int step = 0; step < 2000; step++) {
            const int i = 1 + (int)rnd(64);
            AsFakeObj& o = g_afo[i];
            switch (rnd(9)) {
                case 0: case 1: o.local[0] += 1.5f; AsObjEvDirty(&o, NULL); break;
                case 2: o.hidden ^= 1; AsObjEvDirty(&o, NULL); break;
                case 3: g_afFar[i] = !g_afFar[i]; break;
                case 4: o.lot = 1 + rnd(4); if (rnd(2)) AsObjEvList(&o, NULL); else { AsObjEvDirty(&o, NULL); AsWorldEvent(0x11); } break;
                case 5: AsWorldEvent(0x11); break;
                case 6: AsAddRowKind(AfId(i), rnd(2) ? 1 : 2); break;
                case 7: if (rnd(4) == 0) g_afCam = true; break;
                default: { const DWORD key[4] = {3 + rnd(2), 1 + rnd(4), 0, rnd(2)}; AsLearn(rnd(2) ? AF_ONLOT : AF_COUNT, key, 4); } break;
            }
            if (step % 7 == 6) {
                g_afNow += 300;
                AsCut();
                assert(k.auditMis[0] == 0 && k.listMissed == missed && !g_asDirtyTrip);
                if (step % 35 == 34) AfCheckAnswerable();
            }
        }
    }
    assert(k.dirtyKept > 0 && k.dirtyRows > 0 && k.listAuditN > 0 && k.objResolved > 0 && k.full == 0);
    printf("autshadow dirty lots: %ld rows kept, %ld refreshed, %ld lots marked, %ld objects placed (%ld us), %ld resyncs, %ld list audits, %ld misses; "
           "tier cache %ld classifications, %ld camera moves, %ld holds, %ld lot waits, %ld lot refreshes\n",
           k.dirtyKept, k.dirtyRows, k.dirtyLots, k.objResolved, k.objResolveUs, k.dirtyUnknown, k.listAuditN, k.listMissed,
           k.tierClassN, k.tierCamInval, k.tierHold, k.listT2Wait, k.listT2Ref);
    g_asLean = 0, g_asLotPrint = AsLotPrintGame, g_asObjLot = AsObjLotGame, g_asLotLite = AsLotLiteGame, g_asSeed = AsSeedGame, g_asListAudit = 1, g_asDirtyTrip = false;
    g_asTierMs = 0, g_asTierFn = AsTierOf, g_asNowMs = GetTickCount;
    memset(g_asTierTab, 0, sizeof(g_asTierTab)), memset(g_asLotTier, 0, sizeof(g_asLotTier));
    memset(g_afFar, 0, sizeof(g_afFar));
    g_asAudit = audit0;
    AsEpochBump();
    AsCut();
}

// T10 (docs/122 §14.18): list kinds (a Sim move refreshes only list rows whose type can hold a Sim), unplaceable objects
// handled alone by lot-vector prints (no resync cut), the lot-vector print on fake EA structures, a shared cut serving two
// requests (both actors tier 1, each answered from its own rows). Invariant as T8/T9: no answerable row differs from the body.
static int g_afNoComp;  // this object has no world component (AsObjLot fails for it alone)
static bool AfObjLot3(const void* o, AsU64* lot, DWORD* room) { return o != &g_afo[g_afNoComp] && AfObjLot(o, lot, room); }
static LONG AfRowsT(DWORD type, DWORD lot, bool roomOnly) {  // list rows of one type and lot in the master
    LONG n = 0;
    for (int i = 0; i < g_as->m.n; i++) {
        const AsEnt& e = g_as->m.e[i];
        n += g_asDescOf[e.ic]->owner == AO_LIST && e.owner == lot && e.key[0] == type && (!roomOnly || e.key[3] != 0xFFFFFFFFu);
    }
    return n;
}
static void TestAutShadowLotLite() {  // AsLotLiteGame on the T8 fake EA structures
    void* const* wm = g_asWmPP; const AsFindLotFn fl = g_asFindLot;
    g_asWmPP = &g_agMgrP, g_asFindLot = AfGFind;
    for (int i = 0; i < 8; i++) g_agObj[i].idLo = 100 + i, g_agObj[i].idHi = 5, g_agObj[i].comp = &g_agComp[i], g_agComp[i].room = 3;
    for (int i = 0; i < 3; i++) g_agVec[0][i] = &g_agObj[i];
    g_agLot[0].b = g_agVec[0], g_agLot[0].e = g_agVec[0] + 3;
    AsSetClear(g_as->simPtrs);
    AsSetAdd(g_as->simPtrs, (AsU64)(uintptr_t)&g_agObj[2]);  // object 2 is a Sim
    const AsU64 L = 0x700000001ull;
    AsU64 a[2], b[2], c[2];
    assert(AsLotLiteGame(L, a) && AsLotLiteGame(L, b) && a[0] == b[0] && a[1] == b[1] && a[0] != a[1]);
    g_agComp[1].room = 9, g_agComp[0].lot = 0x700000002ull;  // component changes only (room, a world-vector lot): no change
    assert(AsLotLiteGame(L, c) && c[0] == a[0] && c[1] == a[1]);
    g_agLot[0].e = g_agVec[0] + 2;  // the Sim leaves the lot vector: the Sim part only
    assert(AsLotLiteGame(L, c) && c[0] == a[0] && c[1] != a[1]);
    g_agLot[0].e = g_agVec[0] + 1;  // an object leaves: the object part
    assert(AsLotLiteGame(L, c) && c[0] != a[0]);
    g_agLot[0].e = g_agVec[0] + 3;
    g_agVec[0][0] = &g_agObj[1], g_agVec[0][1] = &g_agObj[0];  // reorder: other answer order
    assert(AsLotLiteGame(L, c) && c[0] != a[0]);
    g_agVec[0][0] = &g_agObj[0], g_agVec[0][1] = &g_agObj[1];
    g_agObj[1].idLo = 900;  // same pointer, another object (reused memory): pointers only (docs/122 §14.19) ...
    assert(AsLotLiteGame(L, c) && c[0] == a[0]);
    AsSetAdd(g_as->suspect, (AsU64)(uintptr_t)&g_agObj[1]);  // ... the reuse needs a destroy or an add of it: a suspect pointer
    const LONG sus0 = ts3native_autshadow.liteSuspect;
    assert(AsLotLiteGame(L, c) && c[0] != a[0] && ts3native_autshadow.liteSuspect == sus0 + 1);
    AsSetClear(g_as->suspect);
    g_agObj[1].idLo = 101;
    AsU64 n1[2], n2[2];
    assert(AsLotLiteGame(0x700000003ull, n1) && AsLotLiteGame(0x700000004ull, n2) && n1[0] == n2[0] && n1[0] != a[0]);
    AsSetClear(g_as->simPtrs);
    g_asWmPP = wm, g_asFindLot = fl;
}
static void TestAutShadowKinds() {
    Ts3nAutShadow& k = ts3native_autshadow;
    TestAutShadowLotLite();
    const int audit0 = g_asAudit;
    g_asAudit = 1;
    g_asLean = 2, g_asLotPrint = AfPrint, g_asObjLot = AfObjLot3, g_asLotLite = AfLite, g_asSeed = AfSeed, g_asListAudit = 0, g_asColdIc = 0, g_asTierMs = 0;
    g_afNoComp = 0;
    AsSetClear(g_as->simIds);  // earlier tests listed fake objects of every kind as Sims
    for (int i = 1; i <= 64; i++) g_afo[i].lot = 1 + i % 4, g_afo[i].parent = 0, g_afIsSim[i] = i <= 16;
    for (int i = 1; i <= 16; i++) AsAddRowKind(AfId(i), 2);  // Sims (the request lists them)
    AsEpochBump();
    AsCut();
    for (DWORD lot = 1; lot <= 4; lot++)
        for (DWORD type = 5; type <= 6; type++) {
            const DWORD a[4] = {type, lot, 0, 0xFFFFFFFFu}, b[4] = {type, lot, 0, 0};
            if (AsFind(g_as->m, AF_ONLOT, a, 4) < 0) AsInsert(AF_ONLOT, a, 4, 0);
            if (AsFind(g_as->m, AF_COUNT, b, 4) < 0) AsInsert(AF_COUNT, b, 4, 0);
        }
    // ts3fixes classifies each new list type once: 5 is Sim-free, every other type can hold a Sim
    const LONG types0 = k.listTypes;
    int asked = 0;
    for (void* t; (t = AsIcListTypeNext()) != NULL; asked++) AsIcListTypeSet(t, (DWORD)(uintptr_t)t != 5);
    assert(asked >= 2 && k.listTypes == types0 + asked && k.listTypesSimFree == 1 && !AsIcListTypeNext());
    assert(AsLTypeSimFree(5) && !AsLTypeSimFree(6) && !AsLTypeSimFree(3));
    for (int i = 1; i <= 64; i++) AsObjEvDirty(&g_afo[i], NULL);
    AsCut();
    AsCut();
    AfCheckAnswerable();
    // a Sim changes lot 1 -> 2: type 6 rows of both lots, no type 5 row
    LONG body = k.bodyN[2];
    const LONG sf0 = k.simFreeKept, sm0 = k.simMarks;
    int s = 1;
    while (g_afo[s].lot != 1) s++;
    g_afo[s].lot = 2;
    AsObjEvDirty(&g_afo[s], NULL);
    const LONG want3 = AfRowsT(6, 1, false) + AfRowsT(6, 2, false) + AfRowsT(3, 1, false) + AfRowsT(3, 2, false) + AfRowsT(4, 1, false) + AfRowsT(4, 2, false);  // before the cut: an aging pass in it may drop learned rows it refreshed
    AsCut();
    assert(k.bodyN[2] == body + want3);
    assert(k.simFreeKept >= sf0 + 4 && k.simMarks > sm0);
    assert(AfListRead(AF_ONLOT, 5, 1, 0xFFFFFFFFu) == -1 && AfListRead(AF_ONLOT, 6, 2, 0xFFFFFFFFu) == -1 && AfListRead(AF_COUNT, 5, 2, 0) == -1);
    body = k.bodyN[2];
    // a Sim's room change: type 6 room rows of its lot only
    g_afo[s].hidden ^= 1;
    AsObjEvDirty(&g_afo[s], NULL);
    const LONG want4 = AfRowsT(6, 2, true) + AfRowsT(3, 2, true) + AfRowsT(4, 2, true);  // before the cut: an aging pass in it may drop learned rows it refreshed
    AsCut();
    assert(k.bodyN[2] == body + want4);
    body = k.bodyN[2];
    // an object changes lot 3 -> 4: every type on both lots
    int o = 17;
    while (g_afo[o].lot != 3) o++;
    g_afo[o].lot = 4;
    AsObjEvDirty(&g_afo[o], NULL);
    const LONG want5 = AfLotRows(3, false) + AfLotRows(4, false);  // before the cut: an aging pass in it may drop learned rows it refreshed
    AsCut();
    assert(k.bodyN[2] == body + want5);
    body = k.bodyN[2];
    AfCheckAnswerable();
    // a Sim with a child in our parent rows (object o carried): its room change marks every type
    g_afo[o].parent = AfId(s), g_afo[o].lot = 2;
    AsAddRowKind(AfId(o), 0);
    AsObjEvDirty(&g_afo[o], NULL);
    AsCut();
    AsCut();
    body = k.bodyN[2];
    g_afo[s].hidden ^= 1;
    AsObjEvDirty(&g_afo[s], NULL);
    const LONG want6 = AfLotRows(2, true);  // before the cut: an aging pass in it may drop learned rows it refreshed
    AsCut();
    assert(AsSetHas(g_as->kids, AfId(s)) && k.bodyN[2] == body + want6);
    g_afo[o].parent = 0;
    AsObjEvDirty(&g_afo[o], NULL);
    AsCut();
    AfCheckAnswerable();
    // an object with no world component (inventory-like) moves 4 -> 1, then 1 -> 3, then is destroyed: no resync cut; its
    // old lot by the map while it was placed, the rest by the lot-vector prints; lot 2's rows are left alone
    int nc = 17;
    while (g_afo[nc].lot != 4 || nc == o) nc++;
    g_afNoComp = nc;
    const LONG unk0 = k.dirtyUnknown, liteCuts0 = k.liteLots;
    AsObjEvDirty(&g_afo[nc], NULL);  // loses its component in place: lot 4 by the map; the prints run (first time: every
    AsCut();                         // list lot without a baseline is marked once), from now on with baselines
    assert(k.liteLots > liteCuts0 && k.dirtyUnknown == unk0);
    const LONG unp0 = k.unplaced, lm0 = k.liteMarks, lite0 = k.liteLots;
    AsObjEvDirty(&g_afo[1], NULL);  // a placeable touch only: no prints
    AsCut();
    assert(k.liteLots == lite0);
    body = k.bodyN[2];
    g_afo[nc].lot = 1;
    AsObjEvDirty(&g_afo[nc], NULL);
    const LONG want7 = AfLotRows(4, false) + AfLotRows(1, false);  // before the cut: an aging pass in it may drop learned rows it refreshed
    AsCut();  // unplaced: lots 4 and 1 by their prints
    assert(k.bodyN[2] == body + want7 && k.liteMarks >= lm0 + 2 && k.unplaced == unp0 + 1);
    body = k.bodyN[2];
    g_afo[nc].lot = 3;
    AsObjEvDirty(&g_afo[nc], NULL);
    const LONG want8 = AfLotRows(1, false) + AfLotRows(3, false);  // before the cut: an aging pass in it may drop learned rows it refreshed
    AsCut();  // both lots by their prints
    assert(k.bodyN[2] == body + want8 && k.unplaced == unp0 + 2);
    assert(AfListRead(AF_ONLOT, 6, 3, 0xFFFFFFFFu) == -1 && AfListRead(AF_ONLOT, 5, 1, 0xFFFFFFFFu) == -1);
    body = k.bodyN[2];
    AsObjEvGone(&g_afo[nc], NULL);  // destroyed with no component: lot 3 by its print
    g_afo[nc].lot = 0;
    const LONG want9 = AfLotRows(3, false);  // before the cut: an aging pass in it may drop learned rows it refreshed
    AsCut();
    assert(k.bodyN[2] == body + want9 && k.unplaced == unp0 + 3 && k.dirtyUnknown == unk0);
    AfCheckAnswerable();
    g_afNoComp = 0;
    g_afo[nc].lot = 4;
    AsEpochBump();  // the destroyed id is forgotten
    AsCut();
    for (int i = 1; i <= 64; i++) AsObjEvDirty(&g_afo[i], NULL);
    AsCut();
    // a shared cut: two requests posted before one cut; both actors stay tier 1 (refreshed every cut, answered), the far
    // Sim one of them reaches waits (simstale) like any tier-2 Sim
    g_asTierMs = 1500, g_asTierFn = AfTier2, g_asNowMs = AfNow;
    g_afNow = 200000;
    const int a1 = 1, a2 = 2, farS = 3;
    g_afFar[a1] = g_afFar[a2] = g_afFar[farS] = true;
    const int mode0 = g_asMode;
    void* const fin0 = g_asMFinish;
    g_asMode = 1, g_asMFinish = (void*)1;
    assert(g_asRun == AS_RUN_IDLE && !g_asReqId);
    assert(AsIcBegin());
    AsIcAddRow((DWORD)AfId(a1), 5, 1);
    AsIcAddRow((DWORD)AfId(farS), 5, 1);
    AsIcPost(80);
    assert(AsIcBegin());
    AsIcAddRow((DWORD)AfId(a2), 5, 1);
    AsIcPost(81);
    assert(g_asBatchN == 2 && AsIsActor(AfId(a1)) && AsIsActor(AfId(a2)) && !AsIsActor(AfId(farS)));
    bool waited = false;
    for (int c = 0; c < 4; c++) {
        g_afNow += 300;
        const int moved[3] = {a1, a2, farS};
        for (int i : moved) { g_afo[i].local[0] += 2; AsObjEvDirty(&g_afo[i], NULL); }
        AsCut();
        assert(AfSimRead(a1) == -1 && AfSimRead(a2) == -1);
        waited |= c == 1 && AfSimRead(farS) == AS_AB_SIMSTALE;  // due at the first cut, waits 300 ms later
    }
    assert(waited);
    InterlockedExchange(&g_asReqId, 0), g_asBatchN = 0;  // as AsFinish
    g_asMode = mode0, g_asMFinish = fin0;
    g_asTierMs = 0;
    memset(g_afFar, 0, sizeof(g_afFar));
    // random sequences: Sims and objects, types 3-6, objects losing their component, every change with its event (lot
    // vector or component): no answerable row ever differs from the body, ListAudit never misses
    g_asListAudit = 4;
    const LONG missed = k.listMissed;
    for (DWORD seed = 31; seed <= 33; seed++) {
        DWORD x = seed * 2654435761u;
        auto rnd = [&x](DWORD n) { x = x * 1664525u + 1013904223u; return (x >> 8) % n; };
        for (int step = 0; step < 2000; step++) {
            const int i = 1 + (int)rnd(64);
            AsFakeObj& f = g_afo[i];
            switch (rnd(9)) {
                case 0: case 1: f.local[0] += 1.5f; AsObjEvDirty(&f, NULL); break;
                case 2: f.hidden ^= 1; AsObjEvDirty(&f, NULL); break;
                case 3: g_afNoComp = rnd(3) ? 0 : i; break;
                case 4: f.lot = 1 + rnd(4); AsObjEvDirty(&f, NULL); break;
                case 5: AsWorldEvent(0x11); break;
                case 6: if (i <= 16) AsAddRowKind(AfId(i), 2); break;
                case 7: { const int c = 1 + (int)rnd(64); g_afo[c].parent = c > i && i <= 16 && rnd(2) ? AfId(i) : 0; AsObjEvDirty(&g_afo[c], NULL); } break;
                default: { const DWORD key[4] = {3 + rnd(4), 1 + rnd(4), 0, rnd(2) ? 0xFFFFFFFFu : 0}; AsLearn(rnd(2) ? AF_ONLOT : AF_COUNT, key, 4); } break;
            }
            if (step % 7 == 6) {
                AsCut();
                assert(k.auditMis[0] == 0 && k.listMissed == missed && !g_asDirtyTrip && k.dirtyUnknown == unk0);
                if (step % 35 == 34) AfCheckAnswerable();
            }
        }
    }
    assert(k.simFreeKept > sf0 && k.unplaced > unp0 && k.liteLots > 0 && k.full == 0);
    printf("autshadow kinds: %ld Sim-free rows kept, %ld Sim marks, %ld unplaced touches (%ld lot-vector marks, %ld prints, %ld us), %ld resyncs\n",
           k.simFreeKept, k.simMarks, k.unplaced, k.liteMarks, k.liteLots, k.liteUs, k.dirtyUnknown);
    g_afNoComp = 0;
    memset(g_afIsSim, 0, sizeof(g_afIsSim));
    for (int i = 1; i <= 64; i++) g_afo[i].parent = 0;
    g_asLean = 0, g_asLotPrint = AsLotPrintGame, g_asObjLot = AsObjLotGame, g_asLotLite = AsLotLiteGame, g_asSeed = AsSeedGame, g_asListAudit = 1;
    g_asTierFn = AsTierOf, g_asNowMs = GetTickCount;
    memset(g_asTierTab, 0, sizeof(g_asTierTab)), memset(g_asLotTier, 0, sizeof(g_asLotTier));
    g_asAudit = audit0;
    AsEpochBump();
    AsCut();
}

// T11 (docs/122 §14.19): seeding (objects that did not move cost no mark, a seeded object's first move marks its old lot
// too), dirtying causes (markBy, markEv, rowWhy), RowHot (an object row unread for N cuts waits cold, a read aborts and makes
// it hot, the actor's and parent rows never wait). Invariant as T8-T10: no answerable row differs from the body.
static void TestAutShadowSeed() {
    Ts3nAutShadow& k = ts3native_autshadow;
    const int audit0 = g_asAudit;
    g_asAudit = 1;
    g_asLean = 2, g_asLotPrint = AfPrint, g_asObjLot = AfObjLot, g_asLotLite = AfLite, g_asSeed = AfSeed, g_asListAudit = 0, g_asColdIc = 0, g_asTierMs = 0;
    AsSetClear(g_as->simIds), AsSetClear(g_as->always), AsSetClear(g_as->sims), AsSetClear(g_as->learnSims);
    for (int i = 1; i <= 64; i++) g_afo[i].lot = 1 + i % 4, g_afo[i].parent = 0, g_afo[i].hidden = 0, g_afIsSim[i] = i <= 16;
    g_afo[64].lot = 9;  // a lot with no list rows: never seeded
    for (DWORD lot = 1; lot <= 4; lot++) {
        const DWORD a[4] = {3, lot, 0, 0}, b[4] = {4, lot, 0, 1};
        if (AsFind(g_as->m, AF_ONLOT, a, 4) < 0) AsInsert(AF_ONLOT, a, 4, 0);
        if (AsFind(g_as->m, AF_COUNT, b, 4) < 0) AsInsert(AF_COUNT, b, 4, 0);
    }
    const LONG seed0 = k.seedLots, objs0 = k.seedObjs;
    AsEpochBump();
    AsCut();  // epoch: the map and the seeded lots are cleared, then every list lot is seeded (no touch needed)
    assert(k.seedLots >= seed0 + 4 && k.seedObjs >= objs0 + 63 && k.seedFull == 0);
    for (int i = 1; i <= 63; i++) { const AsOl* o = AsOlSlot(AfId(i), false); assert(o && o->seen && o->lot == g_afo[i].lot); }
    assert(!AsOlSlot(AfId(64), false));
    AsCut();
    // events on objects that did not move (0x18 visual): no mark, no list body (0.36.118 marked every lot at first sight)
    LONG body = k.bodyN[2];
    const LONG first0 = k.markBy[AS_MC_FIRST], lots0 = k.dirtyLots;
    for (int i = 1; i <= 63; i++) AsObjEv<7>(&g_afo[i], NULL);
    AsCut();
    assert(k.bodyN[2] == body && k.markBy[AS_MC_FIRST] == first0 && k.dirtyLots == lots0);
    AfCheckAnswerable();
    // a seeded object's first move 1 -> 2: the old and the new lot, cause "lot", event 0x0E (bit 5)
    const int mv = AfFirstOn(1);
    g_afo[mv].lot = 2;
    AsObjEvDirty(&g_afo[mv], NULL);
    const LONG want = AfLotRows(1, false) + AfLotRows(2, false), lot0 = k.markBy[AS_MC_LOT], rw0 = k.rowWhy[AS_MC_LOT], ev0 = k.markEv[5];
    AsCut();
    assert(k.bodyN[2] == body + want && k.markBy[AS_MC_LOT] == lot0 + 2 && k.rowWhy[AS_MC_LOT] == rw0 + want && k.markEv[5] == ev0 + 2);
    assert(AfListRead(AF_ONLOT, 3, 1, 0) == -1 && AfListRead(AF_ONLOT, 3, 2, 0) == -1);
    body = k.bodyN[2];
    // an object from a lot with no list rows walks onto lot 3: its first sight marks lot 3 only (cause "first")
    g_afo[64].lot = 3;
    AsObjEvDirty(&g_afo[64], NULL);
    const LONG want1 = AfLotRows(3, false);
    AsCut();
    assert(k.bodyN[2] == body + want1 && k.markBy[AS_MC_FIRST] == first0 + 1 && AfListRead(AF_COUNT, 4, 3, 1) == -1);
    body = k.bodyN[2];
    // a room change (cause "room": room rows only), a destroy (cause "gone", events 5)
    const int rc = AfFirstOn(4);
    g_afo[rc].hidden ^= 1;
    AsObjEvDirty(&g_afo[rc], NULL);
    const LONG want2 = AfLotRows(4, true), room0 = k.markBy[AS_MC_ROOM];
    AsCut();
    assert(k.bodyN[2] == body + want2 && k.markBy[AS_MC_ROOM] == room0 + 1);
    body = k.bodyN[2];
    const int gone = AfFirstOn(4, rc);
    AsObjEvGone(&g_afo[gone], NULL);
    g_afo[gone].lot = 0;
    const LONG want3 = AfLotRows(4, false), gone0 = k.markBy[AS_MC_GONE], evg0 = k.markEv[1];
    AsCut();
    assert(k.bodyN[2] == body + want3 && k.markBy[AS_MC_GONE] == gone0 + 1 && k.markEv[1] == evg0 + 1 && AfListRead(AF_ONLOT, 3, 4, 0) == -1);
    AfCheckAnswerable();
    // lot-vector print suspects (pointers only since 0.36.119): an unplaceable object destroyed, a new unplaceable object
    g_asObjLot = AfObjLot3, g_afNoComp = 20;
    AsObjEvDirty(&g_afo[20], NULL);
    AsCut();  // loses its component: lot by the map (cause "lost"), unplaced from now on
    const LONG lost0 = k.markBy[AS_MC_LOST];
    AsObjEvGone(&g_afo[20], NULL);
    AsCut();
    assert(AsSetHas(g_as->suspect, (AsU64)(uintptr_t)&g_afo[20]) && k.markBy[AS_MC_LOST] == lost0);
    g_afNoComp = 21;
    AsObjEvList(&g_afo[21], NULL);
    AsCut();
    assert(AsSetHas(g_as->suspect, (AsU64)(uintptr_t)&g_afo[21]) && !AsSetHas(g_as->suspect, (AsU64)(uintptr_t)&g_afo[20]) && k.markBy[AS_MC_LOST] == lost0 + 1);
    g_afNoComp = 0, g_asObjLot = AfObjLot;
    AsObjEvDirty(&g_afo[21], NULL);
    g_afo[gone].lot = 2;
    AsEpochBump();  // the destroyed id is forgotten
    AsCut();
    // RowHot: an object row the worker has not read for 16 cuts waits cold; a read aborts, the next cut answers it;
    // the actor's rows and every parent row are refreshed throughout
    g_asRowHot = 16;
    g_asActor = 0, g_asActorN = 0;
    AsAddRowKind(AfId(1), 1);  // the actor
    AsAddRowKind(AfId(3), 1);  // a reachable Sim
    assert(AsIsActor(AfId(1)) && !AsIsActor(AfId(3)));
    const LONG cs0 = k.rowColdSkips, cr0 = k.rowColdReads;
    for (int c = 0; c < 20; c++) { g_afo[3].local[0] += 1; g_afo[1].local[0] += 1; AsCut(); }
    const DWORD k3[2] = {(DWORD)AfId(3), 5}, k1[2] = {(DWORD)AfId(1), 5};
    const int p3 = AsFind(g_as->m, AF_POS, k3, 2), par3 = AsFind(g_as->m, g_asIcParent, k3, 2), p1 = AsFind(g_as->m, AF_POS, k1, 2);
    assert(p3 >= 0 && par3 >= 0 && p1 >= 0 && k.rowColdSkips > cs0);
    assert((g_as->m.e[p3].flags & AE_COLD) && !(g_as->m.e[par3].flags & AE_COLD) && !(g_as->m.e[p1].flags & AE_COLD));
    assert(AfSimRead(3) != -1 && AfSimRead(1) == -1);  // cold: aborts; the actor: answered
    AsCut();
    assert(k.rowColdReads == cr0 + 1 && !(g_as->m.e[AsFind(g_as->m, AF_POS, k3, 2)].flags & AE_COLD) && AfSimRead(3) == -1);
    AfCheckAnswerable();
    // random sequences with seeding and RowHot: moves, rooms, visual events, adds, destroys-and-returns, worker reads
    g_asListAudit = 4;
    const LONG missed = k.listMissed, unk0 = k.dirtyUnknown;
    for (DWORD seed = 41; seed <= 43; seed++) {
        DWORD x = seed * 2654435761u;
        auto rnd = [&x](DWORD n) { x = x * 1664525u + 1013904223u; return (x >> 8) % n; };
        for (int step = 0; step < 2000; step++) {
            const int i = 1 + (int)rnd(64);
            AsFakeObj& f = g_afo[i];
            switch (rnd(9)) {
                case 0: f.local[0] += 1.5f; AsObjEv<7>(&f, NULL); break;
                case 1: f.local[0] += 1.5f; AsObjEvDirty(&f, NULL); break;
                case 2: f.hidden ^= 1; AsObjEvDirty(&f, NULL); break;
                case 3: f.lot = 1 + rnd(5); AsObjEvList(&f, NULL); break;  // lot 5: no list rows
                case 4: f.lot = 1 + rnd(4); AsObjEvDirty(&f, NULL); AsWorldEvent(0x11); break;
                case 5: AsWorldEvent(0x11); break;
                case 6: if (i <= 16) AsAddRowKind(AfId(i), rnd(2) ? 1 : 2); break;  // Sims only (type 5 is Sim-free)
                case 7: { const DWORD key[4] = {3 + rnd(2), 1 + rnd(4), 0, rnd(2)}; AsLearn(rnd(2) ? AF_ONLOT : AF_COUNT, key, 4); } break;
                default: if (rnd(8) == 0) { AsEpochBump(); } break;
            }
            if (step % 7 == 6) {
                AsCut();
                assert(k.auditMis[0] == 0 && k.listMissed == missed && !g_asDirtyTrip && k.dirtyUnknown == unk0);
                AfSimRead(1 + (int)rnd(64));  // a worker read right after the cut (answered = checked; cold rows get hot)
                if (step % 35 == 34) AfCheckAnswerable();
            }
        }
    }
    assert(k.full == 0);
    printf("autshadow seed: %ld lots, %ld objects (%ld us); marks first %ld new %ld lot %ld room %ld gone %ld lost %ld lite %ld kid %ld; "
           "row heat %ld cold skips, %ld cold reads\n", k.seedLots, k.seedObjs, k.seedUs, k.markBy[0], k.markBy[1], k.markBy[2], k.markBy[3],
           k.markBy[4], k.markBy[5], k.markBy[6], k.markBy[7], k.rowColdSkips, k.rowColdReads);
    g_asRowHot = 0;
    memset(g_afIsSim, 0, sizeof(g_afIsSim));
    g_asLean = 0, g_asLotPrint = AsLotPrintGame, g_asObjLot = AsObjLotGame, g_asLotLite = AsLotLiteGame, g_asSeed = AsSeedGame, g_asListAudit = 1;
    AsSetClear(g_as->always), AsSetClear(g_as->sims), AsSetClear(g_as->learnSims);
    g_asActor = 0, g_asActorN = 0;
    g_asAudit = audit0;
    AsEpochBump();
    AsCut();
}

// T12 (docs/122 §14.20): typed marks. ts3fixes' classification stands in AfClassify (the fake bodies' own type rule, or a
// liar that reports a type-8 object as none of 8/9); a classified object's move, add, destroy or lot-vector change refreshes
// only the rows of its types; the typed ListAudit catches the liar; random steps: no answerable row differs, no miss.
static bool g_afClsLie[65];
static void AfClassify() {
    DWORD arr[4 + 2 * 16];  // MonoArray of ulong: max_length at +8, elements at +16
    arr[0] = arr[1] = arr[3] = 0, arr[2] = 16;
    for (int n; (n = AsIcObjClassTake(arr)) > 0;)
        for (int j = 0; j < n; j++) {
            const AsU64 id = *(const AsU64*)(arr + 4 + 2 * j);
            const DWORD i = (DWORD)id;
            if ((DWORD)(id >> 32) != 5 || i < 1 || i > 64) { AsIcObjClassSet((DWORD)id, (DWORD)(id >> 32), 0, 0); continue; }
            const int nt = min(AsIcListTypeCount(), 32);
            DWORD mask = 0;
            for (int t = 0; t < nt; t++) {
                const DWORD type = (DWORD)(uintptr_t)AsIcListTypeAt(t);
                if (g_afClsLie[i] ? type < 8 : AfTypeHas(type, (int)i)) mask |= 1u << t;
            }
            AsIcObjClassSet(i, 5, (int)mask, nt);
        }
}
static LONG AfLotRowsOf(DWORD lot, int obj) {  // list rows of a lot whose type can hold fake object obj
    LONG n = 0;
    for (int i = 0; i < g_as->m.n; i++) {
        const AsEnt& e = g_as->m.e[i];
        n += g_asDescOf[e.ic]->owner == AO_LIST && e.owner == lot && AfTypeHas(e.key[0], obj);
    }
    return n;
}
static int AfPropOn(DWORD lot, DWORD cls) { int i = 1; while (i <= 64 && (g_afo[i].lot != lot || g_afCls[i] != cls || i == 58 || i == 60)) i++; assert(i <= 64); return i; }
static void TestAutShadowTyped() {
    Ts3nAutShadow& k = ts3native_autshadow;
    const int audit0 = g_asAudit;
    g_asAudit = 1;
    g_asLean = 2, g_asLotPrint = AfPrint, g_asObjLot = AfObjLot3, g_asLotLite = AfLite, g_asSeed = AfSeed, g_asListAudit = 0, g_asColdIc = 0, g_asTierMs = 0, g_asRowHot = 0;
    g_afNoComp = 0;
    AsSetClear(g_as->simIds), AsSetClear(g_as->always), AsSetClear(g_as->sims), AsSetClear(g_as->learnSims);
    for (int i = 1; i <= 64; i++) {
        g_afo[i].lot = 1 + i % 4, g_afo[i].parent = 0, g_afo[i].hidden = 0, g_afIsSim[i] = false, g_afClsLie[i] = false;
        g_afCls[i] = i % 3 == 0 ? 1 : i % 3 == 1 ? 2 : 0;  // type 8, type 9, a prop of neither
    }
    g_afo[58].lot = g_afo[60].lot = 8;  // lot 8: no list rows (never seeded); 58 is even: the room -1 rows hold even objects only
    for (DWORD lot = 1; lot <= 4; lot++) {
        const DWORD a[4] = {8, lot, 0, 0xFFFFFFFFu}, b[4] = {9, lot, 0, 1}, c[4] = {3, lot, 0, 0xFFFFFFFFu};
        if (AsFind(g_as->m, AF_ONLOT, a, 4) < 0) AsInsert(AF_ONLOT, a, 4, 0);
        if (AsFind(g_as->m, AF_COUNT, b, 4) < 0) AsInsert(AF_COUNT, b, 4, 0);
        if (AsFind(g_as->m, AF_ONLOT, c, 4) < 0) AsInsert(AF_ONLOT, c, 4, 0);
    }
    const DWORD l7[4] = {8, 7, 0, 0xFFFFFFFFu};  // lot 7: one type-8 row only (the liar's target)
    if (AsFind(g_as->m, AF_ONLOT, l7, 4) < 0) AsInsert(AF_ONLOT, l7, 4, 0);
    // types 8 and 9 hold no Sim (as ts3fixes' ListTypeSet would say): the lot-vector prints' Sim part with no baseline
    // (step 4) marks only Sim-capable rows
    for (DWORD t = 8; t <= 9; t++) { AsLType* lt = AsLTypeOf(t, false); assert(lt); lt->cls = 1, lt->asked = 1; }
    AfClassify();  // ids queued by the earlier tests: their classes die with the epoch below
    AsEpochBump();
    AsCut();
    AsCut();
    AfCheckAnswerable();
    // 1. an unclassified prop moves 1 -> 2: every row of both lots (the old mark) and it is queued; classified, it moves back:
    // only the rows of types it can be (3, 4: every object), types 8/9 kept
    const int P = AfPropOn(1, 0);
    LONG body = k.bodyN[2];
    g_afo[P].lot = 2;
    AsObjEvDirty(&g_afo[P], NULL);
    const LONG all12 = AfLotRows(1, false) + AfLotRows(2, false), want1 = AfLotRowsOf(1, P) + AfLotRowsOf(2, P);  // before the cut (aging)
    AsCut();
    assert(k.bodyN[2] == body + all12);
    const LONG set0 = k.clsSet;
    AfClassify();
    assert(k.clsSet == set0 + 1 && AsOlSlot(AfId(P), false)->clsN > 0);
    body = k.bodyN[2];
    LONG kept0 = k.typedKept, tm0 = k.typedMarks;
    g_afo[P].lot = 1;
    AsObjEvDirty(&g_afo[P], NULL);
    AsCut();
    assert(k.bodyN[2] == body + want1 && k.typedKept == kept0 + all12 - want1 && k.typedMarks == tm0 + 2);
    assert(AfListRead(AF_ONLOT, 8, 1, 0xFFFFFFFFu) == -1 && AfListRead(AF_COUNT, 9, 2, 1) == -1);
    AfCheckAnswerable();
    // 2. a new type-8 object (event 4, queued at the event, classified before the cut) walks onto lot 3: type 8/3/4 rows only
    g_afCls[60] = 1;
    g_afo[60].lot = 3;
    AsObjEvList(&g_afo[60], NULL);
    AfClassify();
    body = k.bodyN[2];
    const LONG new0 = k.markBy[AS_MC_NEW], want2 = AfLotRowsOf(3, 60);
    assert(want2 < AfLotRows(3, false));  // the type-9 row is not of its types
    AsCut();
    assert(k.bodyN[2] == body + want2 && k.markBy[AS_MC_NEW] == new0 + 1);
    assert(AfListRead(AF_ONLOT, 8, 3, 0xFFFFFFFFu) == -1);
    // 3. the classified prop is destroyed: its lot's rows of types 3/4 only (cause gone)
    body = k.bodyN[2];
    const LONG gone0 = k.markBy[AS_MC_GONE], want3 = AfLotRowsOf(1, P);
    AsObjEvGone(&g_afo[P], NULL);
    g_afo[P].lot = 0;
    AsCut();
    assert(k.bodyN[2] == body + want3 && k.markBy[AS_MC_GONE] == gone0 + 1);
    AfCheckAnswerable();
    // 4. a classified prop loses its component and moves 3 -> 4: its lost mark and the lot-vector print marks are typed
    const int Q = AfPropOn(3, 0);
    g_afo[Q].lot = 2;
    AsObjEvDirty(&g_afo[Q], NULL);
    AsCut();  // unclassified: the old mark, queued
    AfClassify();
    g_afNoComp = Q;
    g_afo[Q].lot = 4;
    AsObjEvDirty(&g_afo[Q], NULL);
    body = k.bodyN[2];
    const LONG lt0 = k.liteTyped;
    kept0 = k.typedKept;
    LONG most = 0;
    for (DWORD lot = 1; lot <= 16; lot++) most += AfLotRowsOf(lot, Q);  // every list lot may lack a baseline (typed by Q)
    const LONG least = AfLotRowsOf(2, Q) + AfLotRowsOf(4, Q);
    AsCut();
    assert(k.liteTyped > lt0 && k.typedKept > kept0 && k.bodyN[2] - body >= least && k.bodyN[2] - body <= most);
    AfCheckAnswerable();
    g_afNoComp = 0;
    AsObjEvDirty(&g_afo[Q], NULL);
    AsCut();
    AfCheckAnswerable();
    // 5. negative control: a type-8 object classified as none of 8/9 moves onto lot 7 (one type-8 row): the row is kept
    // typed, the typed ListAudit (odd cuts) finds it, every list read aborts, the session goes to prints
    g_asListAudit = 1;
    g_afCls[58] = 1, g_afClsLie[58] = true;
    AsObjEvList(&g_afo[58], NULL);  // new on lot 8 (no rows): queued
    AfClassify();
    AsCut();
    if (g_asCut & 1) AsCut();  // the next cut is odd
    const LONG tam0 = k.typedAuditMis, tan0 = k.typedAuditN, lm0 = k.listMissed;
    g_afo[58].lot = 7;
    AsObjEvDirty(&g_afo[58], NULL);
    AsCut();
    assert(k.typedAuditN == tan0 + 1 && k.typedAuditMis == tam0 + 1 && k.listMissed == lm0 + 1 && g_asDirtyTrip);
    assert(AfListRead(AF_ONLOT, 8, 7, 0xFFFFFFFFu) == AS_AB_LIST);
    g_asDirtyTrip = false, k.dirtyTripped = 0, g_afClsLie[58] = false;
    g_afo[P].lot = 1;
    AsEpochBump();  // destroyed ids and the liar's class are forgotten
    AsCut();
    AfClassify();
    AfCheckAnswerable();
    // 6. random steps with classification before every cut (as ts3fixes at each request): no answerable row differs, no miss
    g_asListAudit = 4;
    const LONG missed = k.listMissed, unk0 = k.dirtyUnknown, ta0 = k.typedAuditN;
    kept0 = k.typedKept;
    for (DWORD seed = 51; seed <= 53; seed++) {
        DWORD x = seed * 2654435761u;
        auto rnd = [&x](DWORD n) { x = x * 1664525u + 1013904223u; return (x >> 8) % n; };
        for (int step = 0; step < 2000; step++) {
            const int i = 1 + (int)rnd(64);
            AsFakeObj& f = g_afo[i];
            switch (rnd(9)) {
                case 0: f.local[0] += 1.5f; AsObjEv<7>(&f, NULL); break;
                case 1: f.lot = 1 + rnd(4); AsObjEvDirty(&f, NULL); break;
                case 2: f.hidden ^= 1; AsObjEvDirty(&f, NULL); break;
                case 3: f.lot = 1 + rnd(5); AsObjEvList(&f, NULL); break;  // lot 5: no list rows
                case 4: g_afNoComp = rnd(3) ? 0 : i; break;
                case 5: f.lot = 1 + rnd(4); AsObjEvDirty(&f, NULL); AsWorldEvent(0x11); break;
                case 6: { static const DWORD ty[3] = {3, 8, 9}; const DWORD key[4] = {ty[rnd(3)], 1 + rnd(4), 0, rnd(2) ? 0xFFFFFFFFu : rnd(2)}; AsLearn(rnd(2) ? AF_ONLOT : AF_COUNT, key, 4); } break;
                case 7: f.lot = 1 + rnd(4); AsObjEvList(&f, NULL); AsObjEvDirty(&f, NULL); break;
                default: if (rnd(16) == 0) AsEpochBump(); break;
            }
            if (step % 7 == 6) {
                AfClassify();
                AsCut();
                assert(k.auditMis[0] == 0 && k.listMissed == missed && !g_asDirtyTrip && k.dirtyUnknown == unk0);
                if (step % 35 == 34) AfCheckAnswerable();
            }
        }
    }
    assert(k.full == 0 && k.typedKept > kept0 && k.typedAuditN > ta0);
    printf("autshadow typed: %ld classified (%ld asked, %ld none), %ld typed marks, %ld rows kept, %ld typed audits (%ld misses), %ld lite typed (%ld none)\n",
           k.clsSet, k.clsAsked, k.clsNone, k.typedMarks, k.typedKept, k.typedAuditN, k.typedAuditMis, k.liteTyped, k.liteTypedNone);
    g_afNoComp = 0;
    memset(g_afCls, 0, sizeof(g_afCls));
    g_asLean = 0, g_asLotPrint = AsLotPrintGame, g_asObjLot = AsObjLotGame, g_asLotLite = AsLotLiteGame, g_asSeed = AsSeedGame, g_asListAudit = 1;
    g_asAudit = audit0;
    AsEpochBump();
    AsCut();
}

// T13 (docs/122 §14.21): RunSkip for real (no check walk) and the delta publish. Every earlier test ran with the check
// (skippable runs walked anyway, runCheckMis must be 0); here clean runs are really skipped and versions get only the
// walked runs: published rows still equal fresh bodies, answerable rows too, under ColdIc, RowHot, worker reads,
// Sim rows, learned rows, epochs and aging.
static void TestAutShadowRunSkip() {
    Ts3nAutShadow& k = ts3native_autshadow;
    assert(k.runCheckMis == 0 && k.runSkips > 0);  // the check walks of T1-T12
    const int audit0 = g_asAudit;
    g_asRunSkipCheck = false;
    g_asAudit = 1;
    g_asLean = 2, g_asLotPrint = AfPrint, g_asObjLot = AfObjLot, g_asLotLite = AfLite, g_asSeed = AfSeed, g_asListAudit = 1, g_asColdIc = 1, g_asTierMs = 0, g_asRowHot = 16;
    g_afNoComp = 0;
    AsSetClear(g_as->simIds), AsSetClear(g_as->always), AsSetClear(g_as->sims), AsSetClear(g_as->learnSims);
    for (int i = 1; i <= 64; i++) g_afo[i].lot = 1 + i % 4, g_afo[i].parent = 0, g_afo[i].hidden = 0, g_afIsSim[i] = i <= 8;
    for (int i = 1; i <= 40; i++) AsAddRowKind(AfId(i), i <= 2 ? 1 : i <= 8 ? 2 : 0);
    AsEpochBump();
    AsCut();
    AsCut();
    // a clean cut: the object runs are skipped, the publish copies only what was walked
    const LONG rs0 = k.runSkips, pd0 = k.pubDelta, pr0 = k.pubRows;
    AsCut();
    AsCut();
    assert(k.runSkips > rs0 && k.pubDelta >= pd0 + 1 && k.pubRows - pr0 < 2 * g_as->m.n);
    AfCheckPublished();
    AfCheckAnswerable();
    // one object changes: its run is walked, the change is published in the version the worker reads next
    g_afo[30].local[0] += 3;
    AsObjEvDirty(&g_afo[30], NULL);
    AsCut();
    AfCheckPublished();
    AsCut();
    AfCheckPublished();  // the other version too (it had not seen the walk)
    // random steps: moves, rooms, adds, learned keys, worker reads (hot and cold rows), Sim rows, epochs
    const LONG missed = k.listMissed;
    for (DWORD seed = 61; seed <= 63; seed++) {
        DWORD x = seed * 2654435761u;
        auto rnd = [&x](DWORD n) { x = x * 1664525u + 1013904223u; return (x >> 8) % n; };
        for (int step = 0; step < 2000; step++) {
            const int i = 1 + (int)rnd(64);
            AsFakeObj& f = g_afo[i];
            switch (rnd(10)) {
                case 0: f.local[0] += 1.5f; AsObjEvDirty(&f, NULL); break;
                case 1: f.hidden ^= 1; AsObjEvDirty(&f, NULL); break;
                case 2: f.lot = 1 + rnd(4); AsObjEvDirty(&f, NULL); break;
                case 3: { const DWORD key[2] = {(DWORD)(1 + rnd(64)), 5}; AsLearn(rnd(2) ? AF_POS : AF_TOMB, key, 2); } break;
                case 4: f.lot = 1 + rnd(4); AsObjEvList(&f, NULL); break;
                case 5: if (i <= 8) AsAddRowKind(AfId(i), rnd(2) ? 1 : 2); break;
                case 6: { const DWORD key[4] = {3 + rnd(2), 1 + rnd(4), 0, rnd(2)}; AsLearn(rnd(2) ? AF_ONLOT : AF_COUNT, key, 4); } break;
                case 7: AsWorldEvent(0x11); break;
                default: if (rnd(32) == 0) AsEpochBump(); break;
            }
            if (step % 7 == 6) {
                AsCut();
                assert(k.auditMis[0] == 0 && k.listMissed == missed);
                for (int r = 0; r < 3; r++) AfSimRead(1 + (int)rnd(64));  // worker reads right after the cut (hot rows stay, cold rows get hot)
                if (step % 35 == 34) AfCheckPublished(), AfCheckAnswerable();
            }
        }
    }
    assert(k.full == 0 && k.runSkips > rs0 && k.pubDelta > pd0);
    printf("autshadow runskip: %ld runs (last cut), %ld skipped (%ld rows), %ld rebuilds, %ld IC stamp moves, %ld delta publishes (%ld rows copied), %ld check misses\n",
           k.runN, k.runSkips, k.rowSkips, k.runBuilds, k.icStampMoves, k.pubDelta, k.pubRows, k.runCheckMis);
    g_asRowHot = 0;
    memset(g_afIsSim, 0, sizeof(g_afIsSim));
    g_asLean = 0, g_asLotPrint = AsLotPrintGame, g_asObjLot = AsObjLotGame, g_asLotLite = AsLotLiteGame, g_asSeed = AsSeedGame, g_asListAudit = 1;
    AsSetClear(g_as->always), AsSetClear(g_as->sims), AsSetClear(g_as->learnSims);
    g_asActor = 0, g_asActorN = 0;
    g_asAudit = audit0;
    g_asRunSkipCheck = true;
    AsEpochBump();
    AsCut();
}

// T14 (docs/122 §14.26): mono-cost15. (a) the shared list walk's game reads on the T8 fake EA structures (lot part, world
// part, EA's room flag compare, faults); (b) a transient object marks nothing, a longer-lived one does; (c) host classes at
// a mark and at destroy type the marks without ts3fixes; (d) ListWalk answers equal the bodies (check every row), a lying
// host trips it off; (e) random steps with transients, destroys, host classes and the walk: no answerable row differs.
static bool g_afNoHandle[65];
static int AfHostCls(AsU64 id, DWORD* mask) {
    const DWORD i = (DWORD)id;
    if ((DWORD)(id >> 32) != 5 || i < 1 || i > 64 || g_afNoHandle[i]) return 0;
    const int nt = min(g_asLTypeN, 32);
    DWORD m = 0;
    for (int t = 0; t < nt; t++) if (AfTypeHas(g_asLTypes[t].type, (int)i)) m |= 1u << t;
    *mask = m;
    return nt;
}
static bool AfLwWalk(AsLwLot& w) {  // the fake bodies' world: every object of the lot, in index order (lot part)
    for (int i = 1; i <= 64; i++)
        if (g_afo[i].lot == (DWORD)w.lot) g_asLwId[g_asLwUsed] = AfId(i), g_asLwRoom[g_asLwUsed++] = 0, w.nLot++;
    return true;
}
static int g_afLwLie;  // > 0: the Nth answer from now is off by one
static DWORD AfLwAnswer(bool get, DWORD type, AsU64 lot, DWORD room, const AsU64* ids, int n) {
    // the fake bodies apply their own room rule; the ids must be the lot's objects (room -1: all of them)
    int want = 0;
    for (int i = 1; i <= 64; i++) want += g_afo[i].lot == (DWORD)lot;
    assert(room != 0xFFFFFFFFu || n == want);
    for (int j = 0; j < n; j++) assert(g_afo[(DWORD)ids[j]].lot == (DWORD)lot);
    DWORD r = get ? (DWORD)(uintptr_t)AfOnLot(type, (DWORD)lot, 0, room) : AfCount(type, (DWORD)lot, 0, room);
    if (g_afLwLie > 0 && --g_afLwLie == 0) r = get ? (DWORD)(uintptr_t)AfOnLot(type ^ 1, (DWORD)lot, 0, room) : r + 16;
    return r;
}
static void TestAutShadowLwGame() {  // (a) AsLwWalkGame + AsLwIds on fake EA structures
    for (int i = 0; i < 8; i++) g_agObj[i].idLo = 100 + i, g_agObj[i].idHi = 5, g_agObj[i].comp = &g_agComp[i], g_agComp[i].lot = 0;
    const AsU64 L = 0x700000001ull;
    for (int i = 0; i < 3; i++) g_agVec[0][i] = &g_agObj[i], g_agComp[i].lot = L;
    g_agComp[0].room = 3, g_agComp[1].room = 9, g_agComp[2].room = 3;
    for (int i = 0; i < 4; i++) g_agVec[1][i] = &g_agObj[4 + i];
    g_agComp[4].lot = L, g_agComp[4].room = 1;  // world objects on the lot: room 1 and room 0
    g_agComp[5].lot = L, g_agComp[5].room = 0;
    g_agComp[6].lot = 0x700000002ull, g_agComp[6].room = 1;  // another lot
    g_agComp[7].lot = L, g_agComp[7].room = 3;
    g_agLot[0].b = g_agVec[0], g_agLot[0].e = g_agVec[0] + 3, g_agLot[1].b = g_agVec[1], g_agLot[1].e = g_agVec[1] + 4;
    *(void**)(g_agMgr + 0xB0) = &g_agLot[1];
    void* const* wm = g_asWmPP; const AsFindLotFn fl = g_asFindLot; const AsCompFn cf = g_asComp; const DWORD* cb = g_asCompBit;
    g_asWmPP = &g_agMgrP, g_asFindLot = AfGFind, g_asComp = AfGCompFn, g_asCompBit = &g_agBit;
    auto ids = [&](AsU64 lot, DWORD room, const char* want) {  // want: the low id digits after 100, e.g. "0124"
        g_asLwN = g_asLwUsed = 0;
        const int n = AsLwIds(lot, room);
        assert(n == (int)strlen(want));
        for (int j = 0; j < n; j++) assert(g_asLwTmp[j] == (0x500000000ull | (100u + (DWORD)(want[j] - '0'))));
    };
    ids(L, 0xFFFFFFFFu, "012457");  // lot part in order, then the world objects whose component names L
    ids(L, 3, "02");       // lot part by room; world part: (room == 3) is 0 or 1, never 3
    ids(L, 1, "4");        // world part: (room == 1) == 1 only for component room 1
    ids(L, 0, "47");       // world part: (room == 0) == 0 for component rooms != 0
    ids(0x700000003ull, 0xFFFFFFFFu, "");  // no such lot: no ids (the IC answers 0)
    g_agObj[1].comp = NULL;  // a lot object with no component: room rows go to the body (EA's walk faults), room -1 does not
    g_asLwN = g_asLwUsed = 0;
    assert(AsLwIds(L, 3) == -1);
    ids(L, 0xFFFFFFFFu, "012457");
    g_agObj[1].comp = &g_agComp[1];
    g_agVec[0][1] = NULL;  // a null object in the lot vector: the body (EA's walk faults)
    g_asLwN = g_asLwUsed = 0;
    assert(AsLwIds(L, 0xFFFFFFFFu) == -1);
    g_agVec[0][1] = &g_agObj[1];
    *(void**)(g_agMgr + 0xB0) = NULL;  // no world lot: the lot part only
    ids(L, 0xFFFFFFFFu, "012");
    g_asWmPP = wm, g_asFindLot = fl, g_asComp = cf, g_asCompBit = cb;
    g_asLwN = g_asLwUsed = 0;
}
static void TestAutShadowCost15() {
    Ts3nAutShadow& k = ts3native_autshadow;
    TestAutShadowLwGame();
    const int audit0 = g_asAudit;
    g_asAudit = 1;
    g_asLean = 2, g_asLotPrint = AfPrint, g_asObjLot = AfObjLot3, g_asLotLite = AfLite, g_asSeed = AfSeed, g_asListAudit = 0, g_asColdIc = 0, g_asTierMs = 0, g_asRowHot = 0;
    g_afNoComp = 0;
    AsSetClear(g_as->simIds), AsSetClear(g_as->always), AsSetClear(g_as->sims), AsSetClear(g_as->learnSims);
    for (int i = 1; i <= 64; i++) {
        g_afo[i].lot = 1 + i % 4, g_afo[i].parent = 0, g_afo[i].hidden = 0, g_afIsSim[i] = false, g_afClsLie[i] = false, g_afNoHandle[i] = false;
        g_afCls[i] = i % 3 == 0 ? 1 : i % 3 == 1 ? 2 : 0;
    }
    for (int i = 57; i <= 64; i++) g_afo[i].lot = 8;  // off every list lot: never seeded, so a first event makes their map row
    for (DWORD lot = 1; lot <= 4; lot++) {
        const DWORD a[4] = {8, lot, 0, 0xFFFFFFFFu}, b[4] = {9, lot, 0, 1}, c[4] = {3, lot, 0, 0xFFFFFFFFu};
        if (AsFind(g_as->m, AF_ONLOT, a, 4) < 0) AsInsert(AF_ONLOT, a, 4, 0);
        if (AsFind(g_as->m, AF_COUNT, b, 4) < 0) AsInsert(AF_COUNT, b, 4, 0);
        if (AsFind(g_as->m, AF_ONLOT, c, 4) < 0) AsInsert(AF_ONLOT, c, 4, 0);
    }
    AfClassify();  // the queue of earlier tests
    AsEpochBump();
    AsCut();
    AsCut();
    AfCheckAnswerable();
    // (b) Transient=0: an object made and destroyed between two cuts marks its lot (gone); Transient=1: nothing, counted
    g_asTransient = 0;
    LONG body = k.bodyN[2], gone0 = k.markBy[AS_MC_GONE];
    g_afo[57].lot = 2;
    AsObjEvList(&g_afo[57], NULL);
    AsObjEvDirty(&g_afo[57], NULL);
    AsObjEvGone(&g_afo[57], NULL);
    g_afo[57].lot = 0;
    AsCut();
    assert(k.markBy[AS_MC_GONE] == gone0 + 1 && k.bodyN[2] > body);
    g_asTransient = 1;
    body = k.bodyN[2], gone0 = k.markBy[AS_MC_GONE];
    LONG trans0 = k.transObjs, dirty0 = k.dirtyLots, unpl0 = k.unplaced;
    g_afo[58].lot = 3;
    AsObjEvList(&g_afo[58], NULL);
    AsObjEvDirty(&g_afo[58], NULL);
    AsObjEvGone(&g_afo[58], NULL);
    g_afo[58].lot = 0;
    g_afo[59].lot = 4;  // one with no component: no unplaced touch either (no print, no suspect)
    g_afNoComp = 59;
    AsObjEvList(&g_afo[59], NULL);
    AsObjEvGone(&g_afo[59], NULL);
    g_afo[59].lot = 0;
    AsCut();
    g_afNoComp = 0;
    assert(k.transObjs == trans0 + 2 && k.markBy[AS_MC_GONE] == gone0 && k.dirtyLots == dirty0 && k.unplaced == unpl0 && k.bodyN[2] == body);
    AfCheckAnswerable();
    // a cut between its add and its destroy: no transient, the destroy marks
    g_afo[60].lot = 1;
    AsObjEvList(&g_afo[60], NULL);
    AsCut();
    gone0 = k.markBy[AS_MC_GONE], trans0 = k.transObjs;
    AsObjEvGone(&g_afo[60], NULL);
    g_afo[60].lot = 0;
    AsCut();
    assert(k.markBy[AS_MC_GONE] == gone0 + 1 && k.transObjs == trans0);
    AfCheckAnswerable();
    // (c) HostCls: an unclassified prop's move is typed by the host's filter at the cut; a destroyed one's at event 5
    g_asHostCls = 1, g_asHostClsFn = AfHostCls;
    AfClassify();
    AsEpochBump();
    AsCut();
    int P = AfPropOn(1, 0);  // a prop of neither 8 nor 9, seeded, never classified
    assert(!AsOlSlot(AfId(P), false)->clsN);
    body = k.bodyN[2];
    LONG hc0 = k.hcKnown;
    g_afo[P].lot = 2;
    AsObjEvDirty(&g_afo[P], NULL);
    LONG want = AfLotRowsOf(1, P) + AfLotRowsOf(2, P);
    assert(want < AfLotRows(1, false) + AfLotRows(2, false));
    AsCut();
    assert(k.bodyN[2] == body + want && k.hcKnown == hc0 + 1);
    AfCheckAnswerable();
    const int Q = AfPropOn(3, 1);  // a type-8 object: destroyed, its gone mark keeps type 8 (classes taken at event 5)
    assert(!AsOlSlot(AfId(Q), false)->clsN);
    body = k.bodyN[2], hc0 = k.hcKnown;
    want = AfLotRowsOf(3, Q);
    AsObjEvGone(&g_afo[Q], NULL);
    g_afo[Q].lot = 0;
    AsCut();
    assert(k.bodyN[2] == body + want && k.hcKnown == hc0 + 1);
    AfCheckAnswerable();
    const int R = AfPropOn(4, 0);  // no script handle: unknown, the old untyped mark
    g_afNoHandle[R] = true;
    body = k.bodyN[2];
    const LONG nh0 = k.hcNoHandle;
    want = AfLotRows(4, false) + AfLotRows(1, false);
    g_afo[R].lot = 1;
    AsObjEvDirty(&g_afo[R], NULL);
    AsCut();
    assert(k.bodyN[2] == body + want);
    (void)nh0;  // the fake host has no map counters
    AfCheckAnswerable();
    // (d) ListWalk with the fake world and host: every row checked against the body; a lying host turns it off
    g_asListWalk = 1, g_asListWalkCheck = 1, g_asLwWalk = AfLwWalk, g_asLwAnswer = AfLwAnswer;
    AsEpochBump();
    AsCut();
    const LONG lw0 = k.lwRows, lc0 = k.lwChecks;
    assert(k.lwRows > 0 && k.lwMis == 0 && k.lwChecks == k.lwRows && g_asListWalk == 1);
    const int S = AfPropOn(2, 1);
    g_afo[S].lot = 3;
    AsObjEvDirty(&g_afo[S], NULL);
    AsCut();
    assert(k.lwRows > lw0 && k.lwChecks > lc0 && k.lwMis == 0);
    AfCheckAnswerable();
    g_afLwLie = 1;
    AsEpochBump();
    AsCut();
    assert(k.lwMis == 1 && g_asListWalk == -1 && k.lwOn == -1);
    AfCheckAnswerable();  // the body's answer was kept
    // (e) random steps: transients, destroys, moves, adds, host classes (some objects without a handle), ListWalk checked
    k.lwMis = 0, g_asListWalk = 1, k.lwOn = 1, g_asListWalkCheck = 3, g_afLwLie = 0;
    g_asListAudit = 4;
    bool dead[65] = {}, inMap[65] = {};
    const LONG missed = k.listMissed, unk0 = k.dirtyUnknown, tr0 = k.transObjs, lwr0 = k.lwRows;
    auto revive = [&]() {  // an epoch forgets every row: the dead come back as new objects off the list lots
        for (int i = 1; i <= 64; i++) { if (dead[i]) g_afo[i].lot = 5 + i % 3, dead[i] = false; inMap[i] = g_afo[i].lot >= 1 && g_afo[i].lot <= 4; }
    };
    AsEpochBump();
    revive();
    AsCut();
    for (DWORD seed = 71; seed <= 73; seed++) {
        DWORD x = seed * 2654435761u;
        auto rnd = [&x](DWORD n) { x = x * 1664525u + 1013904223u; return (x >> 8) % n; };
        for (int step = 0; step < 2000; step++) {
            const int i = 1 + (int)rnd(64);
            AsFakeObj& f = g_afo[i];
            if (dead[i]) continue;
            switch (rnd(10)) {
                case 0: f.lot = 1 + rnd(4); AsObjEvDirty(&f, NULL); inMap[i] = true; break;
                case 1: f.hidden ^= 1; AsObjEvDirty(&f, NULL); inMap[i] = true; break;
                case 2: if (!inMap[i]) {  // a transient: made (on a list lot), maybe moved, destroyed before the next cut
                        f.lot = 1 + rnd(4); AsObjEvList(&f, NULL);
                        if (rnd(2)) f.lot = 1 + rnd(4), AsObjEvDirty(&f, NULL);
                        AsObjEvGone(&f, NULL); f.lot = 0; dead[i] = true;
                    } break;
                case 3: AsObjEvGone(&f, NULL); f.lot = 0; dead[i] = true; break;
                case 4: g_afNoHandle[i] = rnd(4) == 0; break;
                case 5: if (!inMap[i]) { f.lot = 1 + rnd(4); AsObjEvList(&f, NULL); inMap[i] = true; } break;  // a new object
                case 6: { static const DWORD ty[3] = {3, 8, 9}; const DWORD key[4] = {ty[rnd(3)], 1 + rnd(4), 0, rnd(2) ? 0xFFFFFFFFu : rnd(2)}; AsLearn(rnd(2) ? AF_ONLOT : AF_COUNT, key, 4); } break;
                case 7: f.lot = 1 + rnd(4); AsObjEvDirty(&f, NULL); AsWorldEvent(0x11); inMap[i] = true; break;
                default: if (rnd(24) == 0) { AsEpochBump(); revive(); } break;
            }
            if (step % 7 == 6) {
                if (rnd(2)) AfClassify();  // ts3fixes' requests are not at every cut
                AsCut();
                assert(k.auditMis[0] == 0 && k.listMissed == missed && !g_asDirtyTrip && k.dirtyUnknown == unk0 && k.lwMis == 0 && g_asListWalk == 1);
                if (step % 35 == 34) AfCheckAnswerable();
            }
        }
    }
    assert(k.full == 0 && k.transObjs > tr0 && k.lwRows > lwr0);
    printf("autshadow mono-cost15: %ld transients, %ld host classes (%ld no handle), %ld walk rows (%ld walks, %ld checks, %ld misses, %ld to the body), %ld class asks skipped\n",
           k.transObjs, k.hcKnown, k.hcNoHandle, k.lwRows, k.lwWalks, k.lwChecks, k.lwMis, k.lwFall, k.clsGoneSkip);
    for (int i = 1; i <= 64; i++) if (dead[i] || g_afo[i].lot == 0) g_afo[i].lot = 1 + i % 4;
    memset(g_afCls, 0, sizeof(g_afCls)), memset(g_afNoHandle, 0, sizeof(g_afNoHandle));
    g_asTransient = 0, g_asHostCls = 0, g_asHostClsFn = AsHostClsGame, g_asListWalk = 0, g_asListWalkCheck = 8, g_asLwWalk = AsLwWalkGame, g_asLwAnswer = AsLwAnswerGame;
    g_asLean = 0, g_asLotPrint = AsLotPrintGame, g_asObjLot = AsObjLotGame, g_asLotLite = AsLotLiteGame, g_asSeed = AsSeedGame, g_asListAudit = 1;
    g_asAudit = audit0;
    AsEpochBump();
    AsCut();
}

// T15 (docs/122 §14.27): mono-cost16. (a) SimEvRows: a quiet reachable Sim keeps its event-covered rows, an event refreshes
// them, a change with no event is found by SimEvCheck and takes that IC out; the actor and a parented Sim are never kept;
// (b) TierMoveLot: a move inside one lot and room does not re-classify, a lot change does; (c) ListAuditEvery; (d) GoneDelta:
// a destroy answered as a delta equals the body (every row checked), no record = body, a lying record trips it off;
// (e) random steps with destroys, transients, moves and handle losses: no answerable row differs, no delta or audit miss.
static bool g_afGdLie;
static bool AfGdRec(const void* obj, AsU64 id, AsGdRec* r) {
    memset(r, 0, sizeof(*r));
    r->id = id, r->part = 2;
    DWORD m = 0;
    const int nt = AfHostCls(id, &m);  // no handle: no record
    if (!nt) return false;
    r->mobj = (DWORD)id, r->cls = g_afGdLie ? ~m : m, r->clsN = (u8)nt;
    AsU64 lot = 0;
    DWORD room = 0;
    if (g_asObjLot(obj, &lot, &room)) r->lot = lot, r->room = room, r->part = 0;
    return true;
}
static int AfGdIn(const AsGdRec& r, DWORD room) { return room == 0 || r.mobj % 2 == 0; }  // AfOnLot / AfCount's room rule
static void TestAutShadowCost16() {
    Ts3nAutShadow& k = ts3native_autshadow;
    const int audit0 = g_asAudit;
    g_asAudit = 1;
    g_asLean = 2, g_asLotPrint = AfPrint, g_asObjLot = AfObjLot3, g_asLotLite = AfLite, g_asSeed = AfSeed, g_asListAudit = 0, g_asColdIc = 0, g_asTierMs = 0, g_asRowHot = 0;
    g_afNoComp = 0;
    AsSetClear(g_as->simIds), AsSetClear(g_as->always), AsSetClear(g_as->sims), AsSetClear(g_as->learnSims);
    for (int i = 1; i <= 64; i++) {
        g_afo[i].lot = 1 + i % 4, g_afo[i].parent = 0, g_afo[i].hidden = 0, g_afIsSim[i] = i == 1 || i == 3 || i == 6, g_afClsLie[i] = false, g_afNoHandle[i] = false;  // the Sims of (a)-(b): list type 5 is Sim-free
        g_afCls[i] = g_afIsSim[i] ? 0 : i % 3 == 0 ? 1 : i % 3 == 1 ? 2 : 0;  // Sims are of no typed list type (types 8, 9)
    }
    AfClassify();
    AsEpochBump();
    AsCut();
    AsCut();
    AfCheckAnswerable();
    // (a) SimEvRows
    g_asSimEv = 1, g_asSimEvCheck = 0, g_asSimEvIcs = 0x27;
    g_asActor = 0, g_asActorN = 0;
    AsAddRowKind(AfId(1), 1);  // the actor
    AsAddRowKind(AfId(3), 1);  // a reachable Sim, no parent
    AsAddRowKind(AfId(6), 1);  // a reachable Sim on a seat (parent 2)
    g_afo[6].parent = AfId(2);
    AsObjEvDirty(&g_afo[6], NULL);
    AsCut();
    AsCut();  // the parent rows are refreshed: Sim 3 has a known parent 0, Sim 6 a parent
    assert(AsSetHas(g_as->simFree, AfId(3)) && !AsSetHas(g_as->simFree, AfId(6)));
    LONG kept0 = k.seKept;
    AsCut();
    const DWORD k3[2] = {(DWORD)AfId(3), 5}, k1[2] = {(DWORD)AfId(1), 5}, k6[2] = {(DWORD)AfId(6), 5};
    assert(k.seKept == kept0 + 3);  // Sim 3: parent, position, hidden flags (no stays-after-evict body in the fake table)
    assert(AsFind(g_as->m, AF_POS, k1, 2) >= 0 && AsFind(g_as->m, AF_POS, k6, 2) >= 0 && AsFind(g_as->m, AF_POS, k3, 2) >= 0);
    AfCheckAnswerable();
    g_afo[1].local[0] += 1, g_afo[6].local[0] += 1;  // the actor and the parented Sim are refreshed at every cut, event or not
    AsCut();
    assert(k.seKept == kept0 + 6);
    AfCheckAnswerable();
    g_afo[3].local[0] += 1;  // an event: Sim 3's rows are refreshed
    AsObjEvDirty(&g_afo[3], NULL);
    kept0 = k.seKept;
    AsCut();
    assert(k.seKept == kept0);
    AfCheckAnswerable();
    g_asSimEvCheck = 1;  // a move with no event: every kept row checked, the position IC leaves SimEvRows
    g_afo[3].local[0] += 1;
    const LONG sc0 = k.seChecks;
    AsCut();
    assert(k.seMis == 1 && (k.seIcOff & 2) && !(g_asSimEvIcs & 2) && k.seChecks == sc0 + 3);
    AfCheckAnswerable();
    g_afo[3].local[0] += 1;  // position now refreshes every cut; parent and hidden flags are still checked (same answer)
    AsCut();
    assert(k.seMis == 1);
    AfCheckAnswerable();
    g_asSimEv = 0, g_asSimEvCheck = 8, g_asSimEvIcs = 0x27, k.seMis = 0, k.seIcOff = 0;
    g_afo[6].parent = 0;
    AsObjEvDirty(&g_afo[6], NULL);
    // (b) TierMoveLot (Sim 3 alone besides the actor)
    AsSetClear(g_as->always);
    g_asActor = 0, g_asActorN = 0;
    AsAddRowKind(AfId(1), 1);
    AsAddRowKind(AfId(3), 1);
    memset(g_asTierTab, 0, sizeof(g_asTierTab));
    g_asTierMs = 1500, g_asTierFn = AfTier, g_asNowMs = AfNow, g_asTierClassMs = 1000, g_afNow = 100000;
    AsCut();
    LONG cls0 = k.tierClassN;
    g_afNow += 1500, g_afo[3].local[0] += 1;
    AsObjEvDirty(&g_afo[3], NULL);
    AsCut();
    assert(k.tierClassN == cls0 + 1);  // TierMoveLot=0: touched and 1 s passed: classified again
    g_asTierMoveLot = 1;
    cls0 = k.tierClassN;
    const LONG tm0 = k.tmKept;
    g_afNow += 1500, g_afo[3].local[0] += 1;
    AsObjEvDirty(&g_afo[3], NULL);
    AsCut();
    assert(k.tierClassN == cls0 && k.tmKept == tm0 + 1);  // same lot and room: kept
    g_afNow += 1500, g_afo[3].lot = 1 + g_afo[3].lot % 4;
    AsObjEvDirty(&g_afo[3], NULL);
    AsCut();
    assert(k.tierClassN == cls0 + 1 && k.tmKept == tm0 + 1);  // another lot: classified
    g_afNow += 4000;
    AsCut();
    assert(k.tierClassN == cls0 + 2);  // the 4 x TierClassMs timer
    {  // 0.36.147 TierReqLots: a tier-2 Sim on a scored lot (AddRow kind 3) refreshes; off again = published stale
        const bool far0 = g_afFar[3];
        g_afFar[3] = true;
        g_afNow += 5000;
        AsCut();  // classified tier 2, refreshed (TierMs passed)
        const AsOl* ol = AsOlSlot(AfId(3), false);
        assert(ol && ol->seen && ol->lot);
        const AsU64 lot3 = ol->lot;
        g_asTierReqLots = 1;
        AsAddRowKind(lot3, 3);
        assert(AsSetHas(g_as->reqLots, lot3) && !AsSetHas(g_as->simIds, lot3) && !AsSetHas(g_as->always, lot3));
        const LONG r0 = g_asReqLotT1, st0 = k.tier2Stale;
        g_afNow += 100;
        AsCut();
        assert(g_asReqLotT1 == r0 + 1 && k.tier2Stale == st0);
        g_asTierReqLots = 0;
        g_afNow += 100;
        AsCut();
        assert(k.tier2Stale == st0 + 1);
        AsSetClear(g_as->reqLots);
        g_afFar[3] = far0;
    }
    {  // 0.36.147 PreLearn: learned parent + GetTask rows once per object
        const LONG r0 = g_asPreLearnRows, o0 = g_asPreLearnObjs;
        AsPreLearnObject(AfId(61));
        const LONG got = g_asPreLearnRows - r0;
        printf("autshadow prelearn: %ld rows for a new object (parent ic %d, task ic %d), table %d rows\n", got, g_asIcParent, g_asIcTask, g_as->m.n);
        assert(got <= (g_asIcParent >= 0) + (g_asIcTask >= 0 ? 2 : 0) && g_asPreLearnObjs == o0 + (got > 0));
        const DWORD kp[2] = {(DWORD)AfId(61), (DWORD)(AfId(61) >> 32)};
        const int pf = g_asIcParent >= 0 ? AsFind(g_as->m, g_asIcParent, kp, 2) : -1;
        assert(g_asIcParent < 0 || pf < 0 || (g_as->m.e[pf].flags & AE_LEARNED) || got < 3);  // a new parent row is a learned row
        AsPreLearnObject(AfId(61));
        assert(g_asPreLearnRows == r0 + got && g_asPreLearnObjs == o0 + (got > 0));
    }
    AfCheckAnswerable();
    g_asTierMs = 0, g_asTierFn = AsTierOf, g_asNowMs = GetTickCount, g_asTierMoveLot = 0;
    g_asActor = 0, g_asActorN = 0;
    AsSetClear(g_as->always), AsSetClear(g_as->simIds);
    // (c) ListAuditEvery: 6 dirty cuts, every 3rd audits
    g_asListAudit = 1, g_asListAuditEvery = 3;
    const LONG la0 = k.laSkips;
    for (int c = 0; c < 6; c++) AsCut();
    assert(k.laSkips == la0 + 4);
    g_asListAuditEvery = 1, g_asListAudit = 0;
    // (d) GoneDelta, every delta row checked against the body
    g_asGoneDelta = 1, g_asGoneDeltaCheck = 1, g_asGdRecFn = AfGdRec, g_asGdIn = AfGdIn, g_asGdUnit = 16;
    for (DWORD lot = 1; lot <= 4; lot++) {  // count rows of type 8 and room -1 too
        const DWORD a[4] = {8, lot, 0, 0}, b[4] = {9, lot, 0, 0xFFFFFFFFu};
        if (AsFind(g_as->m, AF_COUNT, a, 4) < 0) AsInsert(AF_COUNT, a, 4, 0);
        if (AsFind(g_as->m, AF_COUNT, b, 4) < 0) AsInsert(AF_COUNT, b, 4, 0);
    }
    AsEpochBump();
    AsCut();
    AsCut();
    auto pick = [](DWORD lot, DWORD cls, bool even) { int i = 1; while (i <= 64 && (g_afo[i].lot != lot || g_afCls[i] != cls || (i % 2 == 0) != even)) i++; assert(i <= 64); return i; };
    const int G = pick(1, 1, true);  // a type-8 object (even: in the room-filtered rows too; lots 1 and 3 hold the even ids)
    LONG rows0 = k.gdRows, chk0 = k.gdChecks, recs0 = k.gdRecs;
    AsObjEvGone(&g_afo[G], NULL);
    g_afo[G].lot = 0;
    AsCut();
    assert(k.gdRecs == recs0 + 1 && k.gdRows >= rows0 + 3 && k.gdChecks > chk0 && k.gdMis == 0 && k.gdIncons == 0 && g_asGoneDelta == 1);
    AfCheckAnswerable();
    const int G2 = pick(3, 2, true), G3 = pick(3, 0, true);  // two on one lot at one cut (a type-9 one and a plain one)
    rows0 = k.gdRows;
    AsObjEvGone(&g_afo[G2], NULL), AsObjEvGone(&g_afo[G3], NULL);
    g_afo[G2].lot = 0, g_afo[G3].lot = 0;
    AsCut();
    assert(k.gdRows >= rows0 + 3 && k.gdMis == 0 && k.gdIncons == 0);
    AfCheckAnswerable();
    const int H = pick(4, 1, false);  // no handle: no record, the lot's rows go to the body
    g_afNoHandle[H] = true;
    const LONG nr0 = k.gdNoRec;
    rows0 = k.gdRows;
    AsObjEvGone(&g_afo[H], NULL);
    g_afo[H].lot = 0;
    AsCut();
    assert(k.gdNoRec == nr0 + 1 && k.gdRows == rows0 && k.gdMis == 0);
    AfCheckAnswerable();
    g_afNoHandle[H] = false;
    const int M = pick(1, 0, true);  // destroyed after a move in the same window: the record names the new lot, the body answers
    rows0 = k.gdRows;
    g_afo[M].lot = 2;
    AsObjEvDirty(&g_afo[M], NULL);
    AsObjEvGone(&g_afo[M], NULL);
    g_afo[M].lot = 0;
    AsCut();
    assert(k.gdRows == rows0 && k.gdNoRec == nr0 + 2 && k.gdMis == 0);
    AfCheckAnswerable();
    g_afGdLie = true;  // a record whose classes lie: the array row disagrees with its element, the count row trips the check
    const int L = pick(1, 1, true);
    const LONG inc0 = k.gdIncons;
    AsObjEvGone(&g_afo[L], NULL);
    g_afo[L].lot = 0;
    AsCut();
    assert(k.gdIncons > inc0 && k.gdMis == 1 && g_asGoneDelta == -1 && k.gdOn == -1);
    AfCheckAnswerable();  // the body's answer was kept
    g_afGdLie = false;
    // (e) random steps: GoneDelta checked 1 in 3, ListAuditEvery 2, ListWalk and HostCls on
    k.gdMis = 0, g_asGoneDelta = 1, k.gdOn = 1, g_asGoneDeltaCheck = 3;
    g_asListAudit = 4, g_asListAuditEvery = 2;
    g_asTransient = 1, g_asHostCls = 1, g_asHostClsFn = AfHostCls, g_asListWalk = 1, g_asListWalkCheck = 3, g_asLwWalk = AfLwWalk, g_asLwAnswer = AfLwAnswer;
    bool dead[65] = {}, inMap[65] = {};
    const LONG missed = k.listMissed, unk0 = k.dirtyUnknown, gdr0 = k.gdRows;
    auto revive = [&]() {
        for (int i = 1; i <= 64; i++) { if (dead[i] || g_afo[i].lot == 0) g_afo[i].lot = 1 + i % 4, dead[i] = false; inMap[i] = g_afo[i].lot >= 1 && g_afo[i].lot <= 4; }
    };
    AsEpochBump();
    revive();
    AsCut();
    for (DWORD seed = 81; seed <= 83; seed++) {
        DWORD x = seed * 2654435761u;
        auto rnd = [&x](DWORD n) { x = x * 1664525u + 1013904223u; return (x >> 8) % n; };
        for (int step = 0; step < 2000; step++) {
            const int i = 1 + (int)rnd(64);
            AsFakeObj& f = g_afo[i];
            if (dead[i]) continue;
            switch (rnd(10)) {
                case 0: f.lot = 1 + rnd(4); AsObjEvDirty(&f, NULL); inMap[i] = true; break;
                case 1: f.hidden ^= 1; AsObjEvDirty(&f, NULL); inMap[i] = true; break;
                case 2: if (!inMap[i]) {
                        f.lot = 1 + rnd(4); AsObjEvList(&f, NULL);
                        AsObjEvGone(&f, NULL); f.lot = 0; dead[i] = true;
                    } break;
                case 3: case 4: if (rnd(3) == 0) f.lot = 1 + rnd(4), AsObjEvDirty(&f, NULL);  // destroys, some after a move
                    AsObjEvGone(&f, NULL); f.lot = 0; dead[i] = true; break;
                case 5: g_afNoHandle[i] = rnd(4) == 0; break;
                case 6: if (!inMap[i]) { f.lot = 1 + rnd(4); AsObjEvList(&f, NULL); inMap[i] = true; } break;
                case 7: { static const DWORD ty[3] = {3, 8, 9}; const DWORD key[4] = {ty[rnd(3)], 1 + rnd(4), 0, rnd(2) ? 0xFFFFFFFFu : rnd(2)}; AsLearn(rnd(2) ? AF_ONLOT : AF_COUNT, key, 4); } break;
                default: if (rnd(24) == 0) { AsEpochBump(); revive(); } break;
            }
            if (step % 7 == 6) {
                if (rnd(2)) AfClassify();
                AsCut();
                assert(k.auditMis[0] == 0 && k.listMissed == missed && !g_asDirtyTrip && k.dirtyUnknown == unk0 && k.gdMis == 0 && g_asGoneDelta == 1 && k.lwMis == 0);
                if (step % 35 == 34) AfCheckAnswerable();
            }
        }
    }
    assert(k.full == 0 && k.gdRows > gdr0);
    printf("autshadow mono-cost16: %ld Sim rows kept by events (%ld checks, %ld misses), %ld tier moves kept, %ld audits skipped, %ld delta rows "
           "(%ld records, %ld lots without, %ld to the body, %ld inconsistent, %ld checks, %ld misses)\n",
           k.seKept, k.seChecks, k.seMis, k.tmKept, k.laSkips, k.gdRows, k.gdRecs, k.gdNoRec, k.gdFall, k.gdIncons, k.gdChecks, k.gdMis);
    for (int i = 1; i <= 64; i++) if (dead[i] || g_afo[i].lot == 0) g_afo[i].lot = 1 + i % 4;
    memset(g_afCls, 0, sizeof(g_afCls)), memset(g_afNoHandle, 0, sizeof(g_afNoHandle));
    memset(g_afIsSim, 0, sizeof(g_afIsSim));
    g_asGoneDelta = 0, g_asGoneDeltaCheck = 8, g_asGdRecFn = AsGdRecGame, g_asGdIn = AsGdInGame, g_asGdUnit = 1, g_asListAuditEvery = 1;
    g_asTransient = 0, g_asHostCls = 0, g_asHostClsFn = AsHostClsGame, g_asListWalk = 0, g_asListWalkCheck = 8, g_asLwWalk = AsLwWalkGame, g_asLwAnswer = AsLwAnswerGame;
    g_asLean = 0, g_asLotPrint = AsLotPrintGame, g_asObjLot = AsObjLotGame, g_asLotLite = AsLotLiteGame, g_asSeed = AsSeedGame, g_asListAudit = 1;
    g_asAudit = audit0;
    AsEpochBump();
    AsCut();
}

// T16 (docs/122 §14.28): mono-cost17. A faithful fake host for partial id lists (the filter per id: the fake bodies' type
// rule, and their room rule through the row's room) and a walk whose world-part rooms give the bodies' room sets (room 0:
// all, room 1: even ids, room -1: all, the host keeps the even ones). (a) IdsDelta: same ids = no host call, new objects
// (anywhere in walk order, not only at the end) = the old answer plus them, every row checked against the full host call;
// (b) random steps with adds, moves, room changes, destroys, learned rows: no answerable row differs, no IdsDelta miss;
// (c) SimEvLearn / SimEvSeated; (d) TierClassMax defers and catches up; (e) GoneLoose: moved then destroyed, array rows
// still take the delta, count rows go to the body.
static DWORD AfIdsAnswer(bool get, DWORD type, AsU64 lot, DWORD room, const AsU64* ids, int n) {
    if (!n) return 0;
    if (g_afArenaAt + 80 > (1 << 16)) g_afArenaAt = 0;
    DWORD* a = g_afArena + g_afArenaAt;
    DWORD m = 0;
    for (int j = 0; j < n; j++) {
        const DWORD i = (DWORD)ids[j];
        assert((DWORD)(ids[j] >> 32) == 5 && i >= 1 && i <= 64 && g_afo[i].lot == (DWORD)lot);
        if (AfTypeHas(type, (int)i) && (room == 0 || i % 2 == 0)) { if (get) a[4 + m] = i; m++; }
    }
    if (!get) return m * 16 + (type & 15);
    a[0] = 0xA0000 + type, a[1] = 0, a[2] = m, a[3] = 0;
    g_afArenaAt += 4 + m;
    return (DWORD)(uintptr_t)a;
}
static DWORD AfIdsCount(DWORD type, AsU64 lot, DWORD room, const AsU64* ids, int n) {
    const DWORD* a = (const DWORD*)(uintptr_t)AfIdsAnswer(true, type, lot, room, ids, n);
    return a ? a[2] : 0;
}
static bool AfLwWalk2(AsLwLot& w) {  // every object of the lot in the world part, room 1: rows of room 0, 1 and -1 get every id, the host applies the room rule
    for (int i = 1; i <= 64; i++)
        if (g_afo[i].lot == (DWORD)w.lot) g_asLwId[g_asLwUsed] = AfId(i), g_asLwRoom[g_asLwUsed++] = 1, w.nWorld++;
    return w.nWorld > 0;  // an empty lot: the body (the fake bodies answer an empty array or a count, the IC 0)
}
static void TestAutShadowCost17() {
    Ts3nAutShadow& k = ts3native_autshadow;
    const int audit0 = g_asAudit;
    g_asAudit = 1;
    g_asLean = 2, g_asLotPrint = AfPrint, g_asObjLot = AfObjLot3, g_asLotLite = AfLite, g_asSeed = AfSeed, g_asListAudit = 2, g_asColdIc = 0, g_asTierMs = 0, g_asRowHot = 0;
    g_afNoComp = 0;
    AsSetClear(g_as->simIds), AsSetClear(g_as->always), AsSetClear(g_as->sims), AsSetClear(g_as->learnSims);
    g_asActor = 0, g_asActorN = 0;
    for (int i = 1; i <= 64; i++) {
        g_afo[i].lot = i > 56 ? 8 : 1 + i % 4, g_afo[i].parent = 0, g_afo[i].hidden = 0, g_afIsSim[i] = i == 5 || i == 9, g_afClsLie[i] = false, g_afNoHandle[i] = false;
        g_afCls[i] = g_afIsSim[i] ? 0 : i % 3 == 0 ? 1 : i % 3 == 1 ? 2 : 0;
    }
    g_asListWalk = 1, g_asListWalkCheck = 0, g_asLwWalk = AfLwWalk2, g_asLwAnswer = AfIdsAnswer, g_asIdsCount = AfIdsCount, g_asGdUnit = 16;
    g_asIdsDelta = 1, g_asIdsDeltaCheck = 1;
    for (DWORD lot = 1; lot <= 4; lot++) {  // room rows too (room 0: every object, room 1: the even ones)
        const DWORD a0[4] = {8, lot, 0, 0}, a1[4] = {8, lot, 0, 1}, a2[4] = {3, lot, 0, 0xFFFFFFFFu};
        if (AsFind(g_as->m, AF_COUNT, a0, 4) < 0) AsInsert(AF_COUNT, a0, 4, 0);
        if (AsFind(g_as->m, AF_ONLOT, a1, 4) < 0) AsInsert(AF_ONLOT, a1, 4, 0);
        if (AsFind(g_as->m, AF_ONLOT, a2, 4) < 0) AsInsert(AF_ONLOT, a2, 4, 0);
    }
    AfClassify();
    AsEpochBump();
    AsCut();
    AsCut();
    AfCheckAnswerable();
    // (a) the same ids: a room change (the fake room is the hidden flag) dirties room rows, the walk ids stay
    const LONG same0 = k.idSame, chk0 = k.idChecks;
    g_afo[12].hidden ^= 1;
    AsObjEvDirty(&g_afo[12], NULL);
    AsCut();
    assert(k.idSame > same0 && k.idChecks > chk0 && k.idMis == 0 && g_asIdsDelta == 1);
    AfCheckAnswerable();
    // new objects: one, then two on one lot (positions from the host's count of the old ids after each: no end-of-vector rule)
    const LONG app0 = k.idApp;
    g_afo[58].lot = 2;
    AsObjEvList(&g_afo[58], NULL);
    AsCut();
    assert(k.idApp > app0 && k.idMis == 0);
    AfCheckAnswerable();
    const LONG app1 = k.idApp;
    g_afo[59].lot = 3, g_afo[60].lot = 3;
    AsObjEvList(&g_afo[59], NULL), AsObjEvList(&g_afo[60], NULL);
    AsCut();
    assert(k.idApp > app1 && k.idMis == 0);
    AfCheckAnswerable();
    const LONG fall0 = k.idFall;  // a new object and a move on one lot: the host answers
    g_afo[61].lot = 4, g_afo[13].lot = 4;
    AsObjEvList(&g_afo[61], NULL), AsObjEvDirty(&g_afo[13], NULL);
    AsCut();
    assert(k.idFall > fall0 && k.idMis == 0);
    AfCheckAnswerable();
    // (b) random steps, IdsDelta checked 1 in 3
    g_asIdsDeltaCheck = 3;
    bool dead[65] = {}, inMap[65] = {};
    for (int i = 1; i <= 64; i++) inMap[i] = g_afo[i].lot >= 1 && g_afo[i].lot <= 4;
    const LONG missed = k.listMissed, unk0 = k.dirtyUnknown, sa0 = k.idSame + k.idApp;
    for (DWORD seed = 91; seed <= 93; seed++) {
        DWORD x = seed * 2654435761u;
        auto rnd = [&x](DWORD n) { x = x * 1664525u + 1013904223u; return (x >> 8) % n; };
        for (int step = 0; step < 2000; step++) {
            const int i = 1 + (int)rnd(64);
            AsFakeObj& f = g_afo[i];
            if (dead[i] || g_afIsSim[i]) continue;
            switch (rnd(9)) {
                case 0: f.lot = 1 + rnd(4); AsObjEvDirty(&f, NULL); inMap[i] = true; break;
                case 1: f.hidden ^= 1; AsObjEvDirty(&f, NULL); inMap[i] = true; break;
                case 2: case 3: if (!inMap[i]) { f.lot = 1 + rnd(4); AsObjEvList(&f, NULL); inMap[i] = true; } break;  // new objects
                case 4: if (rnd(3) == 0) { AsObjEvGone(&f, NULL); f.lot = 0; dead[i] = true; } break;
                case 5: { static const DWORD ty[3] = {3, 8, 9}; const DWORD key[4] = {ty[rnd(3)], 1 + rnd(4), 0, rnd(2) ? 0xFFFFFFFFu : rnd(2)}; AsLearn(rnd(2) ? AF_ONLOT : AF_COUNT, key, 4); } break;
                default: if (rnd(40) == 0) {  // an epoch: the dead and some objects come back off the list lots, as new objects later
                        AsEpochBump();
                        for (int q = 1; q <= 64; q++) if (dead[q] || (!g_afIsSim[q] && rnd(8) == 0)) g_afo[q].lot = 5 + q % 3, dead[q] = false, inMap[q] = false;
                    } break;
            }
            if (step % 7 == 6) {
                if (rnd(2)) AfClassify();
                AsCut();
                assert(k.auditMis[0] == 0 && k.listMissed == missed && !g_asDirtyTrip && k.dirtyUnknown == unk0 && k.idMis == 0 && g_asIdsDelta == 1);
                if (step % 35 == 34) AfCheckAnswerable();
            }
        }
    }
    assert(k.full == 0 && k.idSame + k.idApp > sa0);
    for (int i = 1; i <= 64; i++) if (dead[i] || g_afo[i].lot == 0) g_afo[i].lot = 1 + i % 4;
    AsEpochBump();
    AsCut();
    AfCheckAnswerable();
    // (c) SimEvLearn and SimEvSeated: Sim 5 promoted by a worker read, Sim 9 on object 2 (a root, no Sim)
    g_asSimEv = 1, g_asSimEvCheck = 0, g_asSimEvIcs = 0x27;
    AsAddRowKind(AfId(1), 1);  // the actor
    AsAddRowKind(AfId(5), 2), AsAddRowKind(AfId(9), 2);
    AsSetAdd(g_as->learnSims, AfId(5)), AsSetAdd(g_as->learnSims, AfId(9));
    g_afo[9].parent = AfId(2);
    AsObjEvDirty(&g_afo[9], NULL);
    AsCut();
    AsCut();
    LONG kept0 = k.seKept, lk0 = k.seLearnKept, sk0 = k.seSeatKept;
    AsCut();
    assert(k.seKept == kept0);  // SimEvLearn / SimEvSeated off: promoted Sims refresh every cut
    g_asSimEvLearn = 1;
    AsCut();
    assert(k.seLearnKept == lk0 + 3 && k.seSeatKept == sk0 && k.seKept == kept0 + 3);  // Sim 5: parent, position, hidden flags
    g_asSimEvSeated = 1;
    AsCut();
    assert(k.seSeatKept == sk0 + 2 && k.seLearnKept == lk0 + 3 + 5);  // Sim 9: parent and hidden flags (SimSeatIcs), not position
    AfCheckAnswerable();
    kept0 = k.seKept;
    g_afo[2].local[0] += 1;  // the seat moves (an event on it): Sim 9's rows are refreshed, Sim 5's kept
    AsObjEvDirty(&g_afo[2], NULL);
    AsCut();
    assert(k.seKept == kept0 + 3);
    AfCheckAnswerable();
    g_asSimEv = 0, g_asSimEvLearn = 0, g_asSimEvSeated = 0, g_afo[9].parent = 0;
    AsObjEvDirty(&g_afo[9], NULL);
    AsSetClear(g_as->learnSims), AsSetClear(g_as->always), AsSetClear(g_as->sims);
    g_asActor = 0, g_asActorN = 0;
    AsCut();
    // (d) TierClassMax: a camera move with 2 Sims due and a cap of 1: one waits a cut
    AsAddRowKind(AfId(1), 1), AsAddRowKind(AfId(5), 1), AsAddRowKind(AfId(9), 1);
    memset(g_asTierTab, 0, sizeof(g_asTierTab));
    g_asTierMs = 1500, g_asTierFn = AfTier2, g_asNowMs = AfNow, g_asTierClassMs = 1000, g_afNow = 200000;
    AsCut();
    g_asTierClassMax = 1;
    LONG cls0 = k.tierClassN;
    const LONG cap0 = k.tcCapped;
    g_afCam = true;
    AsCut();
    assert(k.tierClassN == cls0 + 1 && k.tcCapped == cap0 + 1);
    AsCut();
    assert(k.tierClassN == cls0 + 2);  // the deferred one
    g_asTierMs = 0, g_asTierFn = AsTierOf, g_asNowMs = GetTickCount, g_asTierClassMax = 0;
    AsSetClear(g_as->always), AsSetClear(g_as->simIds);
    g_asActor = 0, g_asActorN = 0;
    AfCheckAnswerable();
    // (e) GoneLoose: moved then destroyed in one window; every delta row checked
    g_asIdsDelta = 0, g_asListWalk = 0;
    g_asGoneDelta = 1, g_asGoneDeltaCheck = 1, g_asGdRecFn = AfGdRec, g_asGdIn = AfGdIn, g_asGoneLoose = 1;
    AsEpochBump();
    AsCut();
    AsCut();
    int M = 1;
    while (M <= 64 && (g_afo[M].lot != 1 || M % 2 || g_afCls[M] != 1)) M++;  // even, type 8, on lot 1
    assert(M <= 64);
    const LONG rows0 = k.gdRows, why1 = k.gdWhy[1];
    g_afo[M].lot = 2;
    AsObjEvDirty(&g_afo[M], NULL);
    AsObjEvGone(&g_afo[M], NULL);
    g_afo[M].lot = 0;
    AsCut();
    assert(k.gdWhy[1] == why1 + 1 && k.gdRows > rows0 && k.gdMis == 0 && g_asGoneDelta == 1);
    AfCheckAnswerable();
    printf("autshadow mono-cost17: %ld rows same ids, %ld rows plus new (%ld small host calls), %ld to the host, %ld checks, %ld misses; "
           "%ld promoted / %ld seated Sim rows kept; %ld classifications deferred; gone without record %ld/%ld/%ld/%ld\n",
           k.idSame, k.idApp, k.idCalls, k.idFall, k.idChecks, k.idMis, k.seLearnKept, k.seSeatKept, k.tcCapped, k.gdWhy[0], k.gdWhy[1], k.gdWhy[2], k.gdWhy[3]);
    for (int i = 1; i <= 64; i++) if (g_afo[i].lot == 0) g_afo[i].lot = 1 + i % 4;
    memset(g_afCls, 0, sizeof(g_afCls)), memset(g_afIsSim, 0, sizeof(g_afIsSim));
    g_asGoneDelta = 0, g_asGoneDeltaCheck = 8, g_asGdRecFn = AsGdRecGame, g_asGdIn = AsGdInGame, g_asGdUnit = 1, g_asGoneLoose = 0;
    g_asIdsDelta = 0, g_asIdsDeltaCheck = 8, g_asIdsCount = AsIdsCountGame;
    g_asListWalk = 0, g_asListWalkCheck = 8, g_asLwWalk = AsLwWalkGame, g_asLwAnswer = AsLwAnswerGame;
    g_asLean = 0, g_asLotPrint = AsLotPrintGame, g_asObjLot = AsObjLotGame, g_asLotLite = AsLotLiteGame, g_asSeed = AsSeedGame, g_asListAudit = 1;
    g_asAudit = audit0;
    AsEpochBump();
    AsCut();
}

// docs/124 §17 go-live (Mode 3), native half: the reference runs only at every VerifyEvery-th cut, the Config words the
// consumer compares (frame, epoch, cut frame), LiveStat, and the ObjStale watch map (stamps after the cut, unwatched =
// stale, a reset makes older cuts stale). No worker here: each run finishes NOWORKER at the cut.
static bool AfGwLot(const void*, AsU64* lot, DWORD* room) { *lot = 77, *room = 0; return true; }
static void TestAutShadowLive() {
    Ts3nAutShadow& k = ts3native_autshadow;
    g_mtInvoke = AfT5Invoke, g_asHostPtr = &g_t5HostPtr;
    g_asMode = 3, g_asLiveVerify = 4;
    AsIcSetMethods((void*)1, (void*)2, (void*)3);
    assert(AsIcConfig(13) == 10 && AsIcConfig(14) == 3 && AsIcConfig(15) == 0 && AsIcConfig(16) == 3 && AsIcConfig(17) == 4 && AsIcConfig(18) == 0);
    const LONG ref0 = k.refRuns, cut0 = k.alCuts, ver0 = k.alVerify;
    for (int r = 0; r < 8; r++) {
        AfT5Request(300 + r);
        AsSimLeave();
        assert(g_asRun == AS_RUN_IDLE && AsIcConfig(22) == AsIcConfig(19) && AsIcConfig(21) == AsIcConfig(20));
        assert(AsIcConfig(23) == (k.alCuts % 4 == 0 ? 1 : 0));
    }
    assert(k.alCuts == cut0 + 8 && k.alVerify - ver0 == 2 && k.refRuns - ref0 == 2);
    AsEpochBump();
    assert(AsIcConfig(20) != AsIcConfig(21));  // the consumer's epoch rule sees a move after the cut
    const LONG s0 = k.al[5];
    AsIcLiveStat(5, 2), AsIcLiveStat(56, 1), AsIcLiveStat(-1, 1);
    assert(k.al[5] == s0 + 2);
    // watch map
    const LONG f = g_asGwNow;
    AsIcLiveWatch(0x1234, 0, 0), AsIcLiveWatch(77, 0, 1);
    assert(!AsIcObjStale(0x1234, 0, 0, f) && !AsIcObjStale(77, 0, 1, f));
    assert(AsIcObjStale(77, 0, 0, f) && AsIcObjStale(0x9999, 0, 0, f));  // object 77 and 0x9999 not watched: stale
    AsGwTouch(0x1234);  // an event in the frame in progress (f + 1)
    assert(AsIcObjStale(0x1234, 0, 0, f) && !AsIcObjStale(0x1234, 0, 0, f + 1));
    bool (*lot0)(const void*, AsU64*, DWORD*) = g_asObjLot;
    g_asObjLot = AfGwLot;
    AsU64 fake[2] = {0, 0x5555};  // object id at +8
    AsIcLiveWatch(0x5555, 0, 0);
    g_asGwNow = f + 1;
    AsGwEvent(fake, false);  // a transform: the object only
    assert(AsIcObjStale(0x5555, 0, 0, f + 1) && !AsIcObjStale(77, 0, 1, f + 1));
    AsGwEvent(fake, true);   // an add or destroy: its lot too
    assert(AsIcObjStale(77, 0, 1, f + 1) && !AsIcObjStale(77, 0, 1, f + 2));
    g_asObjLot = lot0;
    const LONG resets0 = k.alWatchResets;
    for (DWORD i = 1; i <= (DWORD)AS_GW_MAX + 1; i++) AsIcLiveWatch(0x100000 + i, 0, 0);
    const DWORD last = 0x100000 + AS_GW_MAX + 1;
    assert(k.alWatchResets == resets0 + 1 && g_asGwN >= 1 && g_asGwN < 16 && g_asGwReset == g_asGwNow + 1);
    const LONG nAfter = g_asGwN;
    assert(AsIcObjStale(last, 0, 0, g_asGwNow) && !AsIcObjStale(last, 0, 0, g_asGwNow + 1) && AsIcObjStale(0x1234, 0, 0, g_asGwNow + 1));
    g_asMode = 1;
    AsIcLiveWatch(0x4444, 0, 0);  // only Mode 3 watches
    assert(g_asGwN == nAfter);
    memset(g_asGwId, 0, sizeof(g_asGwId)), g_asGwN = 0, g_asGwReset = 0;
    g_asMode = -1, g_asLiveVerify = 50;
    printf("autshadow live: %ld Mode 3 cuts, %ld verified, %ld stale queries (%ld stale), %ld watch resets\n", k.alCuts, k.alVerify,
           k.alStaleQ, k.alStaleHit, k.alWatchResets);
}

static void TestAutShadow() {
    Ts3nAutShadow& k = ts3native_autshadow;
    assert(offsetof(Ts3nAutShadow, abort) == 0x7C && offsetof(Ts3nAutShadow, mode) == 0xCC && offsetof(Ts3nAutShadow, lgOn) == 0x13C);
    assert(offsetof(Ts3nAutShadow, listPrintSkips) == 0x800 && offsetof(Ts3nAutShadow, ageRuns) == 0x834);
    assert(offsetof(Ts3nAutShadow, dirtyLots) == 0x838 && offsetof(Ts3nAutShadow, t1Lots) == 0x874);  // registry v10  // registry v9  // registry v1, v2 (v3: TestAutLedger)
    assert(offsetof(Ts3nAutShadow, simMarks) == 0x878 && offsetof(Ts3nAutShadow, simTicks) == 0x8C0);  // registry v11
    assert(offsetof(Ts3nAutShadow, markBy) == 0x8C4 && offsetof(Ts3nAutShadow, markEv) == 0x8E4 && offsetof(Ts3nAutShadow, rowWhy) == 0x908 &&
           offsetof(Ts3nAutShadow, seedLots) == 0x928 && offsetof(Ts3nAutShadow, rowColdReads) == 0x944);  // registry v12
    assert(offsetof(Ts3nAutShadow, clsAsked) == 0x948 && offsetof(Ts3nAutShadow, liteTypedNone) == 0x968 && offsetof(Ts3nAutShadow, clsUs) == 0x96C && sizeof(AsOl) == 40);  // registry v13
    assert(offsetof(Ts3nAutShadow, runSkip) == 0x970 && offsetof(Ts3nAutShadow, pubRows) == 0x98C && offsetof(Ts3nAutShadow, runCheckMis) == 0x990 &&
           offsetof(Ts3nAutShadow, passPairs) == 0x994 && offsetof(Ts3nAutShadow, freshEvery) == 0x9B4 &&
           offsetof(Ts3nAutShadow, keepPartial) == 0x9BC);  // registry v14
    // docs/122 §14.21: every test below runs with RunSkip on and its check: a run judged skippable is walked anyway and must
    // come out unchanged (runCheckMis 0 at the end); T13 then runs the real skip and the delta publish
    g_asRunSkip = 1, g_asRunSkipCheck = true;
    assert(AsKindOfName("ScriptCore.World::World_GetSunriseTime") == 'f' && AsKindOfName("ScriptCore.Random::Random_NextDoubleImpl") == 'd' &&
           AsKindOfName("ScriptCore.Objects::Objects_GetPosition") == 's' && AsKindOfName("ScriptCore.Fake::Fake_UnlistedImpl") == 0);
    for (int i = 1; i < (int)(sizeof(kAsKindNames) / sizeof(kAsKindNames[0])); i++) assert(strcmp(kAsKindNames[i - 1], kAsKindNames[i]) < 0);
    assert(AsAlloc());
    // install on the fake table (this image's .text and bounds)
    const u8* base = (const u8*)GetModuleHandleW(NULL);
    const IMAGE_NT_HEADERS* nt = (const IMAGE_NT_HEADERS*)(base + ((const IMAGE_DOS_HEADER*)base)->e_lfanew);
    const IMAGE_SECTION_HEADER* sec = IMAGE_FIRST_SECTION(nt);
    DWORD textLo = 0, textHi = 0;
    for (int i = 0; i < nt->FileHeader.NumberOfSections && !textLo; i++)
        if (sec[i].Characteristics & IMAGE_SCN_MEM_EXECUTE) textLo = (DWORD)(uintptr_t)base + sec[i].VirtualAddress, textHi = textLo + sec[i].Misc.VirtualSize;
    const DWORD imgLo = (DWORD)(uintptr_t)base, imgHi = imgLo + nt->OptionalHeader.SizeOfImage;
    const int nTab = (int)(sizeof(g_asFakeTab) / sizeof(g_asFakeTab[0]));
    assert(AsInstallGuards((DWORD*)g_asFakeTab, (DWORD*)g_asFakeTab + nTab, imgLo, imgHi, textLo, textHi, true) == 19);
    assert(k.descIcs == 15 && g_asNoDesc[AF_ROUTE - 1] == AS_AB_ROUTE && g_asNoDesc[AF_UNLISTED] == AS_AB_IC &&g_asIcParent == 1 && g_asIcRng == AF_RAND && (k.ready & 1) && g_asFakeTab[30] == (void*)0x12345678);
    assert(g_asOrig[AF_POS] == (void*)AfPos && g_asKind[AF_SUNRISE] == 'f' && g_asKind[AF_CAMPOS] == 's');
    g_afFmtFn = (u8*)VirtualAlloc(NULL, 64, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    memcpy(g_afFmtFn, g_afFmt, 16);
    g_afFmtFn[16] = 0xE5, g_afFmtFn[17] = 0x5D, g_afFmtFn[18] = 0xC3;
    assert(AsInstallFmt((DWORD)(uintptr_t)g_afFmtFn) && (k.ready & 2));
    void* exc = NULL;
    ((void(__cdecl*)(DWORD, void**))g_afFmtFn)(0, &exc);  // not the worker: the body runs
    assert(exc == (void*)0x77);
    // change tracking: world event 7 registers our object-event nodes once
    DWORD* heads = g_snObjHeads;
    SnObjRegFn reg = g_snObjReg;
    g_snObjHeads = g_afObjHeads, g_snObjReg = AfObjReg;
    AsWorldEvent(7);
    AsWorldEvent(7);
    assert(g_afNodeN == 9 && (k.ready & 0xC) == 0xC && g_asTracked);
    // docs/122 §14.19: one handler per event id (the touch records which events it carries)
    assert(SnObjListed(4, (void*)AsObjEv<0>) && SnObjListed(5, (void*)AsObjEv<1>) && SnObjListed(6, (void*)AsObjEv<2>) && SnObjListed(0x0E, (void*)AsObjEv<5>) &&
           SnObjListed(0x21, (void*)AsObjEv<8>) && !SnObjListed(0x21, (void*)AsObjEv<0>));
    g_snObjHeads = heads, g_snObjReg = reg;

    // fake world: 64 objects on 4 lots
    for (int i = 1; i <= 64; i++) {
        AsFakeObj& o = g_afo[i];
        o.id = AfId(i), o.local[0] = (float)i, o.local[1] = 0, o.local[2] = (float)(i % 5);
        o.parent = i > 8 && i % 3 == 0 ? AfId(i / 3) : 0, o.hidden = i % 7 == 0, o.lot = 1 + i % 4;
    }
    for (int i = 1; i <= 32; i++) AsAddRow(AfId(i), i == 1);
    const DWORD lk[4] = {3, 1, 0, 0}, lk2[4] = {3, 1, 0, 0};
    AsInsert(AF_ONLOT, lk, 4, 0);
    AsInsert(AF_COUNT, lk2, 4, 0);
    const float v[3] = {25.0f, 0, 7.0f};
    const DWORD ll[4] = {*(const DWORD*)&v[0], *(const DWORD*)&v[1], *(const DWORD*)&v[2], 2};
    AsInsert(AF_LOTLOC, ll, 4, 0);
    // the sim thread's own memo call is recorded on the pass-through
    AsFakeStr s1 = AfStr(L"Shadows");
    DWORD out = 0;
    assert(((char(__cdecl*)(const AsFakeStr*, DWORD*))AfSlot(AF_OPTION))(&s1, &out) == 1 && out == 7 * 3 + L'S' && k.memoRecords == 1);
    AsFakeStr s5 = AfStr(L"Glow");  // missing option: out left at the caller's 42, keyed on it
    out = 42;
    assert(((char(__cdecl*)(const AsFakeStr*, DWORD*))AfSlot(AF_OPTION))(&s5, &out) == 0 && out == 42 && k.memoRecords == 2);
    const LONG sim0 = g_asSimCalls;
    assert(((AsU64(__cdecl*)(DWORD, DWORD))AfSlot(AF_PARENT))(9, 5) == AfId(3) && g_asSimCalls == sim0 + 1);  // pass-through
    {  // guard thunk pass-through cost on the sim (mono-cost-report: guardSim x ns): 2M calls through the thunk vs the original
        typedef AsU64(__cdecl * ParentFn)(DWORD, DWORD);
        const ParentFn thunk = (ParentFn)AfSlot(AF_PARENT);
        ParentFn orig = NULL;
        for (int i = 0; i < g_asIcN; i++)
            if (g_asName[i] && !strcmp(g_asName[i], (const char*)g_asFakeTab[AF_PARENT * 2 + 1])) orig = (ParentFn)g_asOrig[i];
        assert(orig && orig != thunk);
        const int N = 2000000;
        LARGE_INTEGER f, t0, t1;
        QueryPerformanceFrequency(&f);
        double best[2] = {1e30, 1e30};
        volatile AsU64 sink = 0;
        for (int rep = 0; rep < 3; rep++)
            for (int via = 0; via < 2; via++) {
                const ParentFn fn = via ? thunk : orig;
                QueryPerformanceCounter(&t0);
                for (int i = 0; i < N; i++) sink = sink + fn(9, 5);
                QueryPerformanceCounter(&t1);
                const double ns = (double)(t1.QuadPart - t0.QuadPart) * 1e9 / (double)f.QuadPart / N;
                if (ns < best[via]) best[via] = ns;
            }
        printf("autshadow guard: sim pass-through %.1f ns per call via the thunk, %.1f ns direct, +%.1f ns per guarded call (best of 3 x %d)\n",
               best[1], best[0], best[1] - best[0], N);
    }
    assert(((float(__cdecl*)())AfSlot(AF_SUNRISE))() == 6.0f && ((double(__cdecl*)())AfSlot(AF_RAND))() == 0.25);
    g_asAudit = 1;
    AsCut();
    assert(k.cuts == 1 && k.epochs == 1 && k.auditMis[0] == 0 && k.entries > 100);
    AfCheckPublished();

    // T1 + T4 on a second thread marked worker
    g_asRefsRooted = true, g_asNewArray = AfNewArray;
    static DWORD emptyStr[4] = {0x51, 0, 0, 0};
    g_asEmptyStr = emptyStr, g_asFmtExc = (void*)(uintptr_t)0xE0E0;
    HANDLE th = CreateThread(NULL, 0, AfWorker, NULL, 0, NULL);
    assert(th && WaitForSingleObject(th, 10000) == WAIT_OBJECT_0);
    CloseHandle(th);
    g_asWorkerTid = 0;
    // the worker's misses come back as learned keys at the next cut, then answer
    const LONG learned0 = k.learned;
    AsCut();
    assert(k.learned == learned0 + 7);  // row 5:40 (Objects_GetPosition), list (3, lot 2, flags 1), slot, query 1 and 9, global 2 and 4
    const DWORD rk[2] = {40, 5};
    assert(AsFind(g_as->v[g_asPub], AF_POS, rk, 2) >= 0 && AsFind(g_as->v[g_asPub], AF_PARENT, rk, 2) >= 0);  // + its parent entry
    const int hid = AsFind(g_as->v[g_asPub], AF_HIDDEN, rk, 2);  // + its other object rows, learned (docs/122 §14.15)
    assert(hid >= 0 && (g_as->v[g_asPub].e[hid].flags & AE_LEARNED) && AsFind(g_as->v[g_asPub], AF_TOMB, rk, 2) >= 0);
    // docs/125: the slot and query keys answer from this cut on and follow changes that fire no event (AO_CUT)
    for (int round = 0; round < 2; round++) {
        th = CreateThread(NULL, 0, AfWorker2, NULL, 0, NULL);
        assert(th && WaitForSingleObject(th, 10000) == WAIT_OBJECT_0);
        CloseHandle(th);
        g_asWorkerTid = 0;
        g_afSlotBias += 2.0f;  // no event
        g_afo[2].hidden ^= 1;
        AsObjEvDirty(&g_afo[2], NULL);
        AsCut();
        assert(k.auditMis[0] == 0);
    }

    // T2: incremental snapshot vs full rebuild on random event sequences (invariant S-A), seeds 1-4 x 3,000 steps
    for (DWORD seed = 1; seed <= 4; seed++) {
        DWORD x = seed * 2654435761u;
        auto rnd = [&x](DWORD n) { x = x * 1664525u + 1013904223u; return (x >> 8) % n; };
        for (int step = 0; step < 3000; step++) {
            const int i = 1 + (int)rnd(64);
            AsFakeObj& o = g_afo[i];
            switch (rnd(9)) {
                case 0: case 1: o.local[0] += 1.5f; AsObjEvDirty(&o, NULL); break;                    // 0x0E transform
                case 2: o.hidden ^= 1; AsObjEvDirty(&o, NULL); break;                                  // 0x18 visibility
                case 3: o.parent = i > 1 && rnd(2) ? AfId(1 + (int)rnd(i - 1)) : 0; AsObjEvDirty(&o, NULL); break;  // 0x21 parent
                case 4: o.lot = 1 + rnd(4); if (rnd(2)) AsObjEvList(&o, NULL); else { AsObjEvDirty(&o, NULL); AsWorldEvent(0x11); } break;
                case 5: g_afSunrise += 0.25f; break;                                             // per-cut constant: no event
                case 6: if (!rnd(8)) { g_afSea += 1; AsWorldEvent(5); } break;                   // world event: epoch
                case 7: { const DWORD key[2] = {(DWORD)(1 + rnd(64)), 5}; AsLearn(rnd(2) ? AF_POS : AF_TOMB, key, 2); } break;
                default: { const DWORD key[4] = {3 + rnd(2), 1 + rnd(4), 0, rnd(2)}; AsLearn(rnd(2) ? AF_ONLOT : AF_COUNT, key, 4); } break;
            }
            if (step % 7 == 6) {
                AsCut();
                assert(k.auditMis[0] == 0);
                if (step % 70 == 69) AfCheckPublished();
            }
        }
    }
    assert(k.full == 0 && k.dirtyOverflow == 0 && k.entries > 200);
    // the incremental cut really skips clean rows: with no parents, a clean cut refreshes only the Sim row's tomb entry
    for (int i = 1; i <= 64; i++) { g_afo[i].parent = 0; AsObjEvDirty(&g_afo[i], NULL); }
    AsCut();
    const int probe = g_afRefreshProbe;
    g_asAudit = 0;
    AsCut();
    assert(g_afRefreshProbe - probe == 1);
    // negative control: a change without its event is caught by the audit (S1) and nowhere else
    g_asAudit = 1;
    g_afo[30].hidden ^= 1;
    AsCut();
    assert(k.auditMis[0] >= 1);
    k.auditMis[0] = 0;
    AsObjEvDirty(&g_afo[30], NULL);
    AsCut();
    assert(k.auditMis[0] == 0);

    // A/B: a worker run that overruns keeps its version; two cuts later it is still intact
    g_asWorkerTid = GetCurrentThreadId();
    AsWorkerBegin();
    const LONG held = g_asHeld;
    const DWORD key5[2] = {5, 5};
    const int at = AsFind(g_as->v[held], AF_POS, key5, 2);
    assert(at >= 0);
    const AsEnt before = g_as->v[held].e[at];
    for (int c = 0; c < 2; c++) {
        g_afo[5].local[0] += 100;
        AsObjEvDirty(&g_afo[5], NULL);
        AsCut();
        assert(g_asPub != held);
    }
    assert(!memcmp(&g_as->v[held].e[at], &before, sizeof(AsEnt)));
    const int now = AsFind(g_as->v[g_asPub], AF_POS, key5, 2);
    assert(now >= 0 && memcmp(g_as->v[g_asPub].e[now].st, before.st, 12));
    AsWorkerEnd();
    g_asWorkerTid = 0;
    // a learned key nobody asks for again is dropped after 100 cuts
    const LONG dropped = k.learnDropped;
    for (int c = 0; c < 101; c++) AsCut();
    assert(k.learnDropped > dropped);
    printf("autshadow: %ld IC slots, %ld cuts (max %ld us), %ld entries, %ld audits, %ld aborts\n", k.guardSlots, k.cuts,
           k.cutMaxUs, k.entries, k.auditN, k.abortTotal);
    TestAutShadowTier2Rows();
    TestAutShadowTier2IcGate();
    TestAutShadowTier2IcG1();
    TestAutShadowTier2IcG2();
    TestAutShadowTier2IcG3();
    TestAutShadowTier2IcG4();
    TestAutShadowTier2IcE();
    TestAutShadowTier2IcE3();
    TestAutShadowRun();
    TestAutLedger();
    TestAutShadowFull();
    TestAutShadowCold();
    TestAutShadowLean();
    TestAutShadowDirty();
    TestAutShadowKinds();
    TestAutShadowSeed();
    TestAutShadowTyped();
    TestAutShadowRunSkip();
    TestAutShadowCost15();
    TestAutShadowCost16();
    TestAutShadowCost17();
    TestAutShadowLive();
    TestAutShadowTier2CountRange();
}
