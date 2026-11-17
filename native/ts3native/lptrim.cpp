// [LotPool] Trim (docs/541, counters v913 lp*): OFF by default. Included by ts3native.cpp after castrim.cpp (uses
// userdiag.cpp's g_udOn / g_udWorlds / g_udQuits, memdiag.cpp's g_mdWorld and apex.cpp's g_apexFound / g_apexStandDown).
//
// The pool: EA fixed pool 0x11EB590, 272-byte lot message slots (init 0xF8C2C0 -> 0x4E43D0; exit free-all 0xF94160 ->
// 0x4E4250). Header +0 item size, +4 align, +8 items per core, +0xC core list (core +0 next, +4 bytes), +0x10 free list
// (slot +0 next), +0x14 alloc callback 0x5C32D0 (main EA heap Malloc), +0x18 free callback 0x4E8590 (main EA heap Free),
// +0x1C callback argument [verified disassembly, TS3W 1.67]. Only the pop 0xAE00A0 and the push 0xAE00F0 touch the free
// list (all 4 references to 0x11EB5A0), both under the pool's lock 0x11EB5B0 (EA mutex 0x4E16F0 / unlock 0x4E17B0; byte
// +0x24 = 1, locking on, read in game). A core is added only when the free list is empty (add-core 0x4E42F0: (items + 2)
// * item + 0xC bytes, then the carve 0x4E4290) and freed only at exit, so the pool keeps its world-load peak for the whole
// session. al320 (Bridgeport, read-only census): 599 cores (20.2 MiB) after the load, 99.3-99.7% of the slots free,
// 494-553 cores (16.7-18.7 MiB) with every slot free; no growth on the trip back.
//
// What this does: SettleSec after each world load (the ts3fixes "world:" line, so it needs [UserDiag]), on its own thread,
// under the pool's lock: counts the free slots of every core against the carve layout (first slot (core + align + 7) &
// ~(align - 1), then one every item bytes while the slot <= core + bytes - 2 * item, plus one more), takes the cores whose
// every slot is free, all but KeepCores of them, out of the free list and the core list, unlocks, and hands them to the
// pool's own free callback (the EA heap gets them back for other allocations). Anything unexpected skips the trim and
// changes nothing (lpBad, one log line): a header that is not the one above, more than LP_MAX cores, a free slot outside
// every core or off the carve positions, a free list longer than the slots.
// Exit: EA's atexit destroys the pool lock (0xF94150) and frees every core without it (0xF94160 -> 0x4E4250). A trim
// overlapping that could free a core twice, so a world quit (ts3fixes "world quit" line, g_udQuits) cancels the pending
// trim and none runs until the next world load. Quit to desktop from a world goes InWorldToQuit -> TransitionToNotInWorld
// (raises World.OnWorldQuit) -> Quit state -> exit, so the line arrives before EA's atexit [verified decompiled scripts].
// Not covered: an exit that skips the world quit (process killed: no atexit runs either).
// Apex Radiance loaded ([Compat] ApexStandDown=1): no trim (memory is its area, like rctrim.cpp), counted in lpBad.
static const DWORD LP_POOL = TS3A(0x011EB590), LP_LOCK = TS3A(0x011EB5B0), LP_LOCK_FN = TS3A(0x004E16F0), LP_UNLOCK_FN = TS3A(0x004E17B0);
static const DWORD LP_ITEM = 0x110, LP_ALIGN = 0x10, LP_PER = 0x80, LP_ALLOC_CB = TS3A(0x005C32D0), LP_FREE_CB = TS3A(0x004E8590);
static const DWORD LP_MAX = 8192;  // cores; al320 Bridgeport had 599
typedef int(__thiscall* LpLockFn)(void* lock, const LONG* deadline);
typedef void(__thiscall* LpUnlockFn)(void* lock);
typedef void(__cdecl* LpFreeFn)(void* core, void* arg);
static u8* g_lpPool = (u8*)(uintptr_t)LP_POOL;  // selftest: a fake
static void* g_lpLock = (void*)(uintptr_t)LP_LOCK;
static LpLockFn g_lpLockFn = (LpLockFn)(uintptr_t)LP_LOCK_FN;
static LpUnlockFn g_lpUnlockFn = (LpUnlockFn)(uintptr_t)LP_UNLOCK_FN;
static DWORD g_lpAllocCb = LP_ALLOC_CB, g_lpFreeCb = LP_FREE_CB;  // selftest: its own
static DWORD g_lpSettleMs = 60000, g_lpKeep = 16;
struct LpCore { DWORD at, bytes, cap, free, gone; };
static LpCore g_lpCores[LP_MAX];
static DWORD g_lpGone[LP_MAX];
struct LpResult { DWORD cores, slots, freeSlots, fullFree, freed, kept, freedBytes; LONG us; const char* bad; };

static int __cdecl LpCmp(const void* a, const void* b) {
    const DWORD x = ((const LpCore*)a)->at, y = ((const LpCore*)b)->at;
    return x < y ? -1 : x > y;
}
static int LpFind(DWORD n, DWORD p) {  // the core holding p, or -1 (g_lpCores sorted by address)
    DWORD lo = 0, hi = n;
    while (lo < hi) {
        const DWORD mid = (lo + hi) / 2;
        if (g_lpCores[mid].at <= p) lo = mid + 1;
        else hi = mid;
    }
    return lo && p < g_lpCores[lo - 1].at + g_lpCores[lo - 1].bytes ? (int)lo - 1 : -1;
}
static DWORD LpFirst(DWORD core, DWORD align) { return (core + align + 7) & ~(align - 1); }

// Pass 1 (lock held, reads only): cores and free slots counted and checked. NULL = consistent.
static const char* LpCount(const u8* p, DWORD& n, LpResult& r) {
    const DWORD item = *(const DWORD*)p, align = *(const DWORD*)(p + 4);
    __try {
        for (DWORD c = *(const DWORD*)(p + 0xC); c; c = *(const DWORD*)(uintptr_t)c) {
            if (n == LP_MAX) return "more cores than LP_MAX";
            const DWORD bytes = *(const DWORD*)(uintptr_t)(c + 4), s = LpFirst(c, align), e = c + bytes - 2 * item;
            LpCore& k = g_lpCores[n++];
            k.at = c, k.bytes = bytes, k.cap = bytes > 2 * item && s <= e ? (e - s) / item + 2 : 0, k.free = k.gone = 0;
            r.slots += k.cap;
        }
        qsort(g_lpCores, n, sizeof(LpCore), LpCmp);
        DWORD steps = 0;
        for (DWORD s = *(const DWORD*)(p + 0x10); s; s = *(const DWORD*)(uintptr_t)s) {
            if (++steps > r.slots) return "free list longer than the slots";
            const int i = LpFind(n, s);
            if (i < 0) return "free slot outside every core";
            LpCore& k = g_lpCores[i];
            const DWORD first = LpFirst(k.at, align);
            if (s < first || (s - first) % item || (s - first) / item >= k.cap) return "free slot off the carve";
            k.free++;
        }
        r.freeSlots = steps;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return "fault while reading the pool";
    }
    return NULL;
}

// One trim (any thread; the game's pool lock is taken). Changes nothing unless the whole pool reads as expected.
static LpResult LpTrim() {
    LpResult r = {};
    u8* const p = g_lpPool;
    if (g_apexFound && g_apexStandDown) {
        r.bad = "stood down: Apex Radiance loaded";
        return r;
    }
    if (*(DWORD*)p != LP_ITEM || *(DWORD*)(p + 4) != LP_ALIGN || *(DWORD*)(p + 8) != LP_PER || *(DWORD*)(p + 0x14) != g_lpAllocCb ||
        *(DWORD*)(p + 0x18) != g_lpFreeCb) {
        r.bad = "pool header not as expected";
        return r;
    }
    static const LONG kForever = -1;  // the pop's own deadline: [0x1068820] = -1, wait without a time limit
    LARGE_INTEGER t0;
    g_lpLockFn(g_lpLock, &kForever);
    QueryPerformanceCounter(&t0);
    DWORD n = 0, gone = 0;
    r.bad = LpCount(p, n, r);
    r.cores = n;
    if (!r.bad) {
        for (DWORD i = 0; i < n; i++) {
            LpCore& k = g_lpCores[i];
            if (!k.cap || k.free != k.cap) continue;
            r.fullFree++;
            if (r.kept < g_lpKeep) r.kept++;
            else k.gone = 1, gone++;
        }
    }
    if (!r.bad && gone) {
        // pass 2: the free list without the slots of the cores that go (order kept; next read before the link is written)
        DWORD* link = (DWORD*)(p + 0x10);
        for (DWORD s = *link; s;) {
            const DWORD next = *(const DWORD*)(uintptr_t)s;
            if (!g_lpCores[LpFind(n, s)].gone) *link = s, link = (DWORD*)(uintptr_t)s;
            s = next;
        }
        *link = 0;
        // pass 3: the core list without them
        gone = 0;
        DWORD* cl = (DWORD*)(p + 0xC);
        for (DWORD c = *cl; c;) {
            const DWORD next = *(const DWORD*)(uintptr_t)c;
            const LpCore& k = g_lpCores[LpFind(n, c)];
            if (!k.gone) *cl = c, cl = (DWORD*)(uintptr_t)c;
            else g_lpGone[gone++] = c, r.freedBytes += k.bytes;
            c = next;
        }
        *cl = 0;
    }
    r.us = UsSince(t0);
    const LpFreeFn freeCb = (LpFreeFn)(uintptr_t) * (DWORD*)(p + 0x18);
    void* const arg = *(void**)(p + 0x1C);
    g_lpUnlockFn(g_lpLock);
    for (DWORD i = 0; i < gone; i++) freeCb((void*)(uintptr_t)g_lpGone[i], arg);  // unlinked: nothing else reaches them
    r.freed = gone;
    return r;
}

static void LpRun(const char* why) {
    const LpResult r = LpTrim();
    Ts3nCounters& k = ts3native_counters;
    const double MB = 1.0 / (1 << 20);
    if (r.bad) {
        const LONG b = InterlockedIncrement(&k.lpBad);
        if (b <= 5) Log("lptrim: skipped (%s), %s world=%s: %s; nothing changed", why, r.cores ? "pool read" : "pool not read", g_mdWorld[0] ? g_mdWorld : "-", r.bad);
        return;
    }
    const LONG t = InterlockedIncrement(&k.lpTrims);
    InterlockedExchangeAdd(&k.lpCoresFreed, (LONG)r.freed);
    InterlockedExchangeAdd(&k.lpFreedKB, (LONG)(r.freedBytes >> 10));
    k.lpCoresLeft = (LONG)(r.cores - r.freed), k.lpKept = (LONG)r.kept, k.lpLastUs = r.us;
    Log("lptrim: #%ld %s world=%s cores %lu -> %lu, slots %lu free %lu (%.1f%%), completely free %lu: freed %lu (%.2f MiB), kept %lu; "
        "locked %.2f ms",
        t, why, g_mdWorld[0] ? g_mdWorld : "-", r.cores, r.cores - r.freed, r.slots, r.freeSlots, r.slots ? 100.0 * r.freeSlots / r.slots : 0.0,
        r.fullFree, r.freed, r.freedBytes * MB, r.kept, r.us / 1000.0);
}

static DWORD WINAPI LpThread(void*) {
    LONG seen = 0, quits = 0;
    DWORD at = 0;
    bool due = false;
    for (;;) {
        Sleep(1000);
        const LONG w = g_udWorlds, q = g_udQuits;
        const DWORD now = GetTickCount();
        if (q != quits) quits = q, due = false;  // world quit (menu, travel, exit): no trim until the next world load
        if (w != seen) seen = w, at = now, due = true;
        if (due && now - at >= g_lpSettleMs && g_udQuits == quits) {
            due = false;
            char why[32];
            sprintf_s(why, "world load #%ld", w);
            LpRun(why);
        }
    }
}

static void ApplyLotPoolTrim(const wchar_t* ini) {
    if (GetPrivateProfileIntW(L"LotPool", L"Trim", 0, ini) <= 0) return;
    const int settle = GetPrivateProfileIntW(L"LotPool", L"SettleSec", 60, ini);
    const int keep = GetPrivateProfileIntW(L"LotPool", L"KeepCores", 16, ini);
    g_lpSettleMs = (DWORD)(settle < 5 ? 5 : settle > 3600 ? 3600 : settle) * 1000;
    g_lpKeep = (DWORD)(keep < 0 ? 0 : keep > 4096 ? 4096 : keep);
    if (!g_udOn) { Log("FAIL lptrim: needs [UserDiag] (world loads come from its ts3fixes line): not installed"); return; }
    HANDLE t = CreateThread(NULL, 64 * 1024, LpThread, NULL, STACK_SIZE_PARAM_IS_A_RESERVATION, NULL);
    if (!t) { Log("FAIL lptrim: no thread (%lu): not installed", GetLastError()); return; }
    CloseHandle(t);
    ts3native_counters.lpOn = 1;
    Log("lptrim: on (docs/541): lot message pool 0x%08X, %lu s after each world load, keeps %lu completely free cores", LP_POOL,
        g_lpSettleMs / 1000, g_lpKeep);
}
