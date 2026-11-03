// [CellMemoProbe] (docs/150 §12, counters v88), observe only: would design M (docs/150 §8.3, a memo of in-room legs within one
// hierarchical plan) hit, and how much cell A* time would those hits save? Nothing is cached; every leg runs as before.
// Included by ts3native.cpp right after routephases.cpp; needs [RouteJobPhases] Enabled=1 (its worker-only job window
// g_rjpTid and its cell A* frame 0x61E500, whose inclusive time RjpClose hands to CmCell).
//
// Per worker plan (from one Q reset 0x61E320 to the next, or the job's end): each in-room leg request at the entry of
// InRoomPlan 0x632E40 / InRoomPlanToPoint 0x632630 is hashed with design M's key (wrapper, X+0x50, lot key, start point
// bits incl. level, then the goal point bits + threshold bits + goal vector records + any-room and no-nudge flags, or the
// target point bits + no-nudge flag; docs/98 §2) and looked up in the plan's key list. A key seen before in the same plan is
// a repeat (would-hit); the cell A* calls of that leg (0x61E500 has no other direct caller, docs/109) count as would-hit time.
// shortcut: the portal lock refresh 0x632460 rewriting a footprint inside the plan (M drops entries then) is not seen, so
// would-hit is an upper bound; it rewrites only on the first call in a room (docs/150 §8.3). Upgrade if M gets built.

enum { CMS_QRESET, CMS_INROOM, CMS_TOPOINT, CMS_N };
static const RjpSite kCmSites[CMS_N] = {
    {0x0061E320, 5, {0x53, 0x8B, 0x5C, 0x24, 0x08}, 0, "Q reset"},  // routegate.cpp site 6 owns it when [RouteGate] is on
    {0x00632E40, 6, {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0}, 0, "InRoomPlan"},
    {0x00632630, 6, {0x81, 0xEC, 0x80, 0x00, 0x00, 0x00}, 0, "InRoomPlanToPoint"},
};
static void* g_cmJmp[CMS_N];

static const int CM_KEYS = 256;
struct CmThread { int n, legs; LONG repeats; bool hit; unsigned long long key[CM_KEYS]; };
static __declspec(thread) CmThread t_cm;

static unsigned long long CmHash(unsigned long long h, const void* p, size_t n) {  // FNV-1a 64
    for (const u8* b = (const u8*)p; n--; b++) h = (h ^ *b) * 0x100000001B3ull;
    return h;
}

// closes the plan: counted only when it planned a leg; histogram by repeats 0, 1, 2-3, 4-7, 8-15, 16-31, 32-63, 64+
static void CmPlanEnd(CmThread& t) {
    Ts3nCounters& k = ts3native_counters;
    if (t.legs) {
        int b = 0;
        while ((t.repeats >> b) && b < 7) b++;
        InterlockedIncrement(&k.cmPlans);
        InterlockedIncrement(&k.cmHist[b]);
        Max(&k.cmMaxRepeats, t.repeats);
    }
    t.n = t.legs = 0;
    t.repeats = 0;
    t.hit = false;
}

// slot = the return-address slot; stack args follow (both wrappers are thiscall on P, docs/98 §2)
static void __cdecl CmEnter(int site, DWORD* slot) {
    Ts3nCounters& k = ts3native_counters;
    if (!k.cmReady) return;
    CmThread& t = t_cm;
    if (site == CMS_QRESET) { CmPlanEnd(t); return; }
    const DWORD* a = slot + 1;  // X, lot lo, lot hi, start*, goal* / target*, ...
    unsigned long long h = CmHash(0xCBF29CE484222325ull, &site, sizeof(site));
    h = CmHash(h, (const u8*)(uintptr_t)a[0] + 0x50, 4);  // X+0x50 context flags
    h = CmHash(h, &a[1], 8);                               // lot key
    h = CmHash(h, (const void*)(uintptr_t)a[3], 12);       // start {x, z, level}
    h = CmHash(h, (const void*)(uintptr_t)a[4], 12);       // goal or target {x, z, level}
    if (site == CMS_INROOM) {
        h = CmHash(h, &a[5], 4);                              // threshold score bits
        const DWORD* v = (const DWORD*)(uintptr_t)a[6];        // goal vector {begin, end}, 28-byte records
        const DWORD len = v[1] - v[0];
        h = CmHash(h, (const void*)(uintptr_t)v[0], len <= 28 * 4096 ? len : 0);  // shortcut: a bogus length hashes nothing
        const u8 f[2] = {(u8)a[9], (u8)a[10]};               // any-room, no-nudge (pushed as dwords, low byte read)
        h = CmHash(h, f, 2);
    } else {
        const u8 f = (u8)a[6];  // no-nudge
        h = CmHash(h, &f, 1);
    }
    const int w = site - CMS_INROOM;
    InterlockedIncrement(&k.cmLegs[w]);
    t.legs++;
    t.hit = false;
    for (int i = 0; i < t.n; i++)
        if (t.key[i] == h) { t.hit = true; break; }
    if (t.hit) {
        InterlockedIncrement(&k.cmRepeats[w]);
        t.repeats++;
    } else if (t.n < CM_KEYS) t.key[t.n++] = h;
    else InterlockedIncrement(&k.cmFull);  // shortcut: a full list stops learning keys (counted); grow it if cmFull shows
}

// RjpClose of a cell A* frame (0x61E500, inclusive time)
static void CmCell(LONGLONG incl) {
    Ts3nCounters& k = ts3native_counters;
    if (!k.cmReady) return;
    const LONG us = RjpUs(incl);
    InterlockedExchangeAdd(&k.cmCellUs, us);
    if (t_cm.hit) {
        InterlockedExchangeAdd(&k.cmHitCellUs, us);
        InterlockedIncrement(&k.cmHitCellN);
    }
}

// RjpJobBegin / RjpJobEnd: a new job starts with an empty plan; the job's end closes the open one
static void CmJob(bool begin) {
    if (!ts3native_counters.cmReady) return;
    if (!begin) { CmPlanEnd(t_cm); return; }
    t_cm.n = t_cm.legs = 0;
    t_cm.repeats = 0;
    t_cm.hit = false;
}

// entry only (no return swap): the worker inside a job reaches CmEnter, every other thread one compare
#define CM_STUB(i) static __declspec(naked) void CmStub##i() { __asm push eax __asm mov eax, dword ptr fs:[0x24] \
    __asm cmp eax, g_rjpTid __asm pop eax __asm jne pass##i __asm pushad __asm pushfd __asm lea eax, [esp + 36] __asm push eax \
    __asm push i __asm call CmEnter __asm add esp, 8 __asm popfd __asm popad __asm pass##i: __asm jmp dword ptr [g_cmJmp + i * 4] }
CM_STUB(0) CM_STUB(1) CM_STUB(2)
static void (*const kCmStubs[CMS_N])() = {CmStub0, CmStub1, CmStub2};

// as RjpInstallAt: our detour when the prologue matches, else the owner's trampoline pointer, else skipped
static bool CmInstallAt(int i, DWORD at, void** owner) {
    const RjpSite& s = kCmSites[i];
    if (Matches((const void*)(uintptr_t)at, s.pro, s.len)) {
        char name[80];
        sprintf_s(name, "%s (cell memo probe)", s.name);
        return (g_cmJmp[i] = InstallDetour(name, at, s.pro, s.len, (void*)kCmStubs[i], &g_cmJmp[i])) != NULL;
    }
    if (owner && *owner) {
        g_cmJmp[i] = *owner;
        InterlockedExchangePointer(owner, (void*)kCmStubs[i]);
        Log("OK   cell memo probe %s: inside its owner's stub", s.name);
        return true;
    }
    Log("SKIP cell memo probe %s: bytes at 0x%08lX differ and no known owner", s.name, at);
    return false;
}

static bool ApplyCellMemoProbe(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"CellMemoProbe", L"Enabled", 0, ini)) { Log("cell memo probe off (Enabled=0)"); return false; }
    Ts3nCounters& k = ts3native_counters;
    if (!k.rjpReady || !g_rjpJmp[RJS_CELL]) { Log("SKIP cell memo probe: needs [RouteJobPhases] Enabled=1 with its cell A* site"); return true; }
    for (int i = 0; i < CMS_N; i++)
        // Q reset: [RouteGate]'s stub, else P-GS's own detour (docs/178 T3, installed just before) when GoalStepSplit is on
        if (!CmInstallAt(i, kCmSites[i].at, i != CMS_QRESET ? NULL : g_rgTramp[6] ? &g_rgTramp[6] : g_rjpJmp[RJS_GS0 + 3] ? &g_rjpJmp[RJS_GS0 + 3] : NULL))
            k.cmSkipped++;
    k.cmReady = k.cmSkipped ? 0 : 1;  // without every site the plan boundaries or the keys are wrong: count nothing
    Log("%s cell memo probe: %d of %d sites (worker plans only)", k.cmReady ? "OK  " : "FAIL", CMS_N - (int)k.cmSkipped, CMS_N);
    return true;
}
