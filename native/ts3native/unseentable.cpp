// [UnseenTable] (docs/227 §6, §8 lazy rows, counters v149 utb* + v152, Rodney 2026-11-02): one "can the player see it?"
// answer per object per tick for every unseen consumer, instead of each check asking again (repeat ratio 1.36 script / 1.61
// native on BP). Included by ts3native.cpp after unseenanim.cpp (uses unseen.cpp's classifier, camera and household table,
// simloop.cpp's SimPumpAdd and autshadow-run.cpp's g_asAddIc).
//
// Tick: a sim-pump callback (SimPumpAdd, sim thread) bumps g_utbTick. The first ask after a bump starts a new generation
// (g_utbGen, the hash stamp: old rows vanish without clearing) and reads the camera once (UnRefreshCamera). RefreshTicks N
// (ini, 1-8, switch unseentablettl): a generation lasts N pumps; the camera is still read every pump, and a move > 1 m,
// camera lost/found or a world load starts the next one at once (utbForced). Shortcut: rows may be up to N-1 pumps old
// (position, class; an address reused by a new object within them keeps the old row); N = 1 until the A/B says otherwise.
// Rows are lazy (§8): native UnObjClass computes an object's row at its first ask in the generation (class only: UnObjClassBody,
// the old native rule) and answers later asks from it. The script fetch walks the locomotion agent list (sentinel
// [0x11D9140]+0x40, node at agent+0x40, owner at agent+4; next pointer at node+0 [inferred, ledger I-418-4]) once per
// generation and keeps the listed owners sorted by object id (§9: no rows computed in the walk); a listed object's row and
// bits 1-4 are computed at its first script ask (Bits) or by the copy fetch (Fetch, every listed row):
//   bit 0  class by the old native rule (UnObjClassBody: 1 m sphere on screen or within the camera reach) - 1 = unseen
//   bit 1  on screen with a sphere of RadiusM + 1 m 1 m above the position (default 3: UfWhy's kUfSimRadiusM; wider than the
//          1 m rule and than World.IsObjectOnScreen's model bounds for a Sim [inferred]); EA's sphere-vs-frustum test
//          (g_unInFrustum), already the cheapest equal test
//   bit 2  within the camera reach max(80 m, 2 x camera height) + RadiusM of camera position or target, or a wide camera
//          (unseenfb.cpp kUfWideReachM) or no camera: UfSphereUnseen's rule, at least FarFromViewers' reach
//   bit 3  within 80 m of an active-household Sim (ts3fixes' viewer push, UtViewerWhyBody), bit 4 that table is fresh (2 s)
//   bit 5  parented (vehicle, horse, carried), bit 6 a Sim, bit 7 a valid row; bits 8-9 internal (never fetched)
// Script reads (§9, ts3fixes switch unseentablecopy): Bits(id) answers one Sim's row by a binary search of the listed owners
// (no copy; per ask one IC + ~10 compares, inferred < 0.5 us); Fetch copies every listed row (the §6 path, the A/B baseline).
// Answers (utbMode 1 = the runtime switch, --ab unseentable): UnObjClass returns bit 0 with the row's kind, position and
// parent flag (the position at the first ask, earlier in the same tick); a row without a transform answers -1 as the old
// rule would. Another thread, mode 0 or a full table (1024 rows) runs the old rule. ts3fixes' Sim checks read bits 1-4
// (UnseenTable.cs, Bits per ask or the Fetch copy per tick) and keep their own extra rules.
// Check=1: every table answer also runs the old rule; utbMisUnsafe = table unseen but old visible, utbMisSafe = the reverse.
enum { UTB_ROWS = 1024, UTB_HASH = 2048, UTB_WALK = 4096 };
enum { UTB_UNSEEN = 1, UTB_ONR = 2, UTB_REACH = 4, UTB_VIEWER = 8, UTB_FRESH = 16, UTB_PARENT = 32, UTB_SIM = 64, UTB_VALID = 128,
       UTB_FULL = 256, UTB_BAD = 512 };  // FULL: bits 1-4 computed; BAD: bits 1-4 faulted (never answered to the script)
struct UtbRow { void* obj; DWORD lo, hi; float pos[3]; LONG bits; };
static UtbRow g_utbRow[UTB_ROWS];
static int g_utbN;
static struct { void* obj; DWORD gen; int row; } g_utbHash[UTB_HASH];
static DWORD g_utbTick = 1, g_utbBuilt, g_utbGen, g_utbSortedGen;
static DWORD g_utbTid;
static float g_utbRadius = 3.0f;
static bool g_utbIcOk;
static struct UtbListed { void* obj; DWORD lo, hi; } g_utbList[UTB_WALK];  // the agent list's owners by object id, no duplicates
static int g_utbListN;  // (walked at the first script read of a generation)

static int UtbFind(const void* obj) {
    for (DWORD h = ((uintptr_t)obj >> 4) & (UTB_HASH - 1), n = 0; n < UTB_HASH; h = (h + 1) & (UTB_HASH - 1), n++) {
        if (g_utbHash[h].gen != g_utbGen) return -1;
        if (g_utbHash[h].obj == obj) return g_utbHash[h].row;
    }
    return -1;
}
static void UtbInsert(void* obj, int row) {
    DWORD h = ((uintptr_t)obj >> 4) & (UTB_HASH - 1);
    while (g_utbHash[h].gen == g_utbGen) h = (h + 1) & (UTB_HASH - 1);  // never full: rows <= UTB_HASH / 2
    g_utbHash[h].obj = obj, g_utbHash[h].gen = g_utbGen, g_utbHash[h].row = row;
}

// [UnseenTable] RefreshTicks (1 = a generation per pump; 2-8 = rows stay valid for that many pumps unless forced)
static DWORD g_utbRefreshTicks = 1, g_utbGenTick;
static float g_utbGenCam[7];  // the generation's camera: position, target, reach
static bool g_utbGenCamOk;
static volatile LONG g_utbWorlds, g_utbWorldsSeen;  // userdiag.cpp UdWorldLoad bumps g_utbWorlds (ts3fixes "world:" line)

// a move that can change answers at once (map view, lot or portrait jump, zoom): camera position or target > 1 m, reach > 1 m,
// camera lost or found, or a world load (object addresses are reused)
static bool UtbViewChanged() {
    if (g_utbWorlds != g_utbWorldsSeen || g_unCam.ok != g_utbGenCamOk) return true;
    if (!g_unCam.ok) return false;
    float d2p = 0, d2t = 0;
    for (int i = 0; i < 3; i++) {
        d2p += (g_unCam.pos[i] - g_utbGenCam[i]) * (g_unCam.pos[i] - g_utbGenCam[i]);
        d2t += (g_unCam.tgt[i] - g_utbGenCam[3 + i]) * (g_unCam.tgt[i] - g_utbGenCam[3 + i]);
    }
    const float dr = g_unCam.reach - g_utbGenCam[6];
    return d2p > 1.0f || d2t > 1.0f || dr > 1.0f || dr < -1.0f;
}

// first ask after a pump: one camera read; a new generation (every row stale at once) every RefreshTicks pumps or on a view
// change
static void UtbBegin() {
    Ts3nCounters& k = ts3native_counters;
    g_utbBuilt = g_utbTick;
    UnRefreshCamera();
    const bool due = g_utbTick - g_utbGenTick >= (k.utbTtlMode ? g_utbRefreshTicks : 1);
    if (!due && !UtbViewChanged()) return;
    if (!due) k.utbForced++;
    g_utbGenTick = g_utbTick;
    g_utbWorldsSeen = g_utbWorlds;
    g_utbGenCamOk = g_unCam.ok;
    memcpy(g_utbGenCam, g_unCam.pos, 12), memcpy(g_utbGenCam + 3, g_unCam.tgt, 12), g_utbGenCam[6] = g_unCam.reach;
    g_utbGen++;
    g_utbN = 0;
    k.utbBuilds++;
}

// the object's row in this generation, class only, computed at its first ask; -1 = table full (the old rule answers).
// *lazy = answered from a row computed earlier in the generation. A row without a transform (or a fault) stays invalid.
static int UtbRowGet(void* obj, bool* lazy) {
    Ts3nCounters& k = ts3native_counters;
    const int i = UtbFind(obj);
    if (lazy) *lazy = i >= 0;
    if (i >= 0) return i;
    if (g_utbN == UTB_ROWS) return -1;
    UtbRow& r = g_utbRow[g_utbN];
    const bool samp = (k.utbRowCalc++ & 15) == 0;  // time 1 row in 16 (a QPC pair costs ~40 ns)
    LARGE_INTEGER t0 = {}, t1;
    if (samp) QueryPerformanceCounter(&t0);
    int kind = 1;
    bool par = false;
    const int c = UnObjClassBody(obj, &kind, r.pos, &par);
    r.obj = obj, r.lo = r.hi = 0;
    r.bits = c < 0 ? 0 : UTB_VALID | (c ? UTB_UNSEEN : 0) | (par ? UTB_PARENT : 0) | (kind == 0 ? UTB_SIM : 0);
    if (samp) {
        QueryPerformanceCounter(&t1);
        k.utbRowSampUs10 += (LONG)((t1.QuadPart - t0.QuadPart) * 10000000 / g_qpf.QuadPart);
    }
    UtbInsert(obj, g_utbN);
    k.utbRows = ++g_utbN;
    Max(&k.utbRowsMax, (LONG)g_utbN);
    return g_utbN - 1;
}

// bits 1-4 (script fetch only), from the row's position; false = a fault (the row stays out of the fetch)
static bool UtbFull(UtbRow& r) {
    const float* pos = r.pos;
    // centre 1 m up (EA's model middle), radius RadiusM + 1: covers UfPosUnseen's sphere (Sim position, RadiusM)
    __declspec(align(16)) float sph[4] = {pos[0], pos[1] + 1.0f, pos[2], g_utbRadius + 1.0f};
    bool onR = true;  // a fault counts as seen
    __try {
        const void* fr = g_unFrustum();
        onR = fr && g_unInFrustum(fr, NULL, sph) != 0;
        r.lo = *(const DWORD*)((const u8*)r.obj + 8), r.hi = *(const DWORD*)((const u8*)r.obj + 0xC);
    } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    // reach as UfSphereUnseen (reach + RadiusM; a wide camera or no camera = seen): at least as wide as FarFromViewers' reach
    bool reach = !g_unCam.ok || g_unCam.reach >= kUfWideReachM;
    for (int j = 0; j < 2 && !reach; j++) {
        const float* q = j ? g_unCam.tgt : g_unCam.pos;
        float d2 = 0;
        for (int i = 0; i < 3; i++) d2 += (pos[i] - q[i]) * (pos[i] - q[i]);
        const float rr = g_unCam.reach + g_utbRadius;
        reach = d2 < rr * rr;
    }
    const bool fresh = g_utView.at && GetTickCount() - g_utView.at <= UT_VIEWER_AGE_MS;
    const bool viewer = fresh && UtViewerWhyBody(pos) == UTW_VIEWER;
    r.bits |= UTB_FULL | (onR ? UTB_ONR : 0) | (reach ? UTB_REACH : 0) | (viewer ? UTB_VIEWER : 0) | (fresh ? UTB_FRESH : 0);
    return true;
}

// the owners' object ids (+8/+0xC); an owner whose id read faults is left out
static void UtbWalk(bool* cap) {
    const u8* sys = *g_unLocoSys;
    u8* const sent = sys ? (u8*)sys + 0x40 : NULL;
    int walked = 0;
    for (u8* node = sent ? *(u8* const*)sent : NULL; node && node != sent && walked < UTB_WALK; node = *(u8* const*)node, walked++) {
        void* owner = *(void* const*)(node - 0x40 + 4);
        if (!owner) continue;
        UtbListed& e = g_utbList[g_utbListN];
        __try {
            e.lo = *(const DWORD*)((const u8*)owner + 8), e.hi = *(const DWORD*)((const u8*)owner + 0xC);
        } __except (EXCEPTION_EXECUTE_HANDLER) { continue; }
        e.obj = owner, g_utbListN++;
    }
    if (walked == UTB_WALK) *cap = true;
}

static unsigned __int64 UtbId(const UtbListed& e) { return e.lo | (unsigned __int64)e.hi << 32; }

// the walk, once per generation: the listed owners sorted by object id, duplicates (an owner listed twice) dropped
static void UtbList() {
    Ts3nCounters& k = ts3native_counters;
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    g_utbSortedGen = g_utbGen;
    g_utbListN = 0;
    bool fault = false, cap = false;
    __try { UtbWalk(&cap); } __except (EXCEPTION_EXECUTE_HANDLER) { fault = true; }
    if (fault) g_utbListN = 0, k.utbFaults++;  // nothing listed this generation (the script's own rules answer); native rows stay
    if (cap) k.utbListCap++;
    qsort(g_utbList, (size_t)g_utbListN, sizeof(UtbListed), [](const void* a, const void* b) {
        const unsigned __int64 u = UtbId(*(const UtbListed*)a), v = UtbId(*(const UtbListed*)b);
        return u < v ? -1 : u > v ? 1 : 0;
    });
    int m = 0;
    for (int i = 0; i < g_utbListN; i++)
        if (!m || UtbId(g_utbList[i]) != UtbId(g_utbList[m - 1])) g_utbList[m++] = g_utbList[i];
    g_utbListN = m;
    InterlockedExchangeAdd(&k.utbBuildUs, UsSince(t0));
}

// listed owner e's row with bits 1-4 (computed at its first use in the generation); -1 = table full, -2 = no transform or a
// fault (the script's own rules answer)
static int UtbListedRow(int e) {
    const int i = UtbRowGet(g_utbList[e].obj, NULL);
    if (i < 0) return -1;
    UtbRow& r = g_utbRow[i];
    if (!(r.bits & UTB_VALID) || r.bits & UTB_BAD) return -2;
    if (!(r.bits & UTB_FULL) && !UtbFull(r)) { r.bits |= UTB_BAD; return -2; }
    return i;
}

// a script read: sim thread and mode 1, the generation begun and its owners listed
static bool UtbScriptReady() {
    if (ts3native_counters.utbMode != 1 || GetCurrentThreadId() != g_utbTid) return false;
    if (g_utbBuilt != g_utbTick) UtbBegin();
    if (g_utbSortedGen != g_utbGen) UtbList();
    return true;
}

// g_utbClass (unseen.cpp UnObjClass): -2 = no answer
static int UtbClass(void* obj, int* kind, float* posOut, bool* parented) {
    Ts3nCounters& k = ts3native_counters;
    if (k.utbMode != 1 || !obj || GetCurrentThreadId() != g_utbTid) return -2;
    if (g_utbBuilt != g_utbTick) UtbBegin();
    bool lazy;
    const int i = UtbRowGet(obj, &lazy);
    if (i < 0) { k.utbMiss++; return -2; }
    if (lazy) k.utbLazyHits++;
    const UtbRow& r = g_utbRow[i];
    if (!(r.bits & UTB_VALID)) return -1;  // no transform: the old rule's answer
    if (kind) *kind = r.bits & UTB_SIM ? 0 : 1;
    if (posOut) memcpy(posOut, r.pos, 12);
    if (parented) *parented = (r.bits & UTB_PARENT) != 0;
    const int c = r.bits & UTB_UNSEEN;
    k.utbHits++;
    if (k.utbCheck) {
        int k2 = 1;
        bool p2 = false;
        float q[3];
        const int o = UnObjClassOld(obj, &k2, q, &p2);
        if (o >= 0 && o != c) InterlockedIncrement(c ? &k.utbMisUnsafe : &k.utbMisSafe);
    }
    return c;
}

// UnseenTableNative::Fetch(ulong[] ids, int[] bits) (MonoArray: max_length +8, elements +16, as AsIcObjClassTake): every
// listed row by object id (ts3fixes unseentablecopy, the §6 path); -1 = no table (mode 0, not the sim thread)
static int __cdecl UtbIcFetch(void* ids, void* bits) {
    Ts3nCounters& k = ts3native_counters;
    if (!ids || !bits || !UtbScriptReady()) { k.utbFetchOff++; return -1; }
    const LONG capI = *(const LONG*)((const u8*)ids + 8), capB = *(const LONG*)((const u8*)bits + 8);
    unsigned __int64* outI = (unsigned __int64*)((u8*)ids + 16);
    LONG* outB = (LONG*)((u8*)bits + 16);
    int n = 0;
    for (int e = 0; e < g_utbListN && n < capI && n < capB; e++) {
        const int i = UtbListedRow(e);
        if (i == -1) { k.utbListCap++; break; }
        if (i < 0) continue;
        const UtbRow& r = g_utbRow[i];
        outI[n] = r.lo | (unsigned __int64)r.hi << 32, outB[n] = r.bits & 0xFF;
        n++;
    }
    k.utbFetches++;
    k.utbFetchRows += n;
    return n;
}

// UnseenTableNative::Bits(ulong id) (a ulong IC argument arrives as DWORD lo, hi, as AsIcAddRow): the listed object's row
// bits 0-7, no copy (§9); -1 = no table (mode 0, not the sim thread), -2 = not listed, no transform or a fault
static int __cdecl UtbIcBits(DWORD lo, DWORD hi) {
    if (!UtbScriptReady()) { ts3native_counters.utbFetchOff++; return -1; }
    const unsigned __int64 id = lo | (unsigned __int64)hi << 32;
    int a = 0, b = g_utbListN;
    while (a < b) {
        const int m = (a + b) >> 1;
        if (UtbId(g_utbList[m]) < id) a = m + 1; else b = m;
    }
    if (a == g_utbListN || UtbId(g_utbList[a]) != id) return -2;
    const int i = UtbListedRow(a);
    return i < 0 ? -2 : g_utbRow[i].bits & 0xFF;
}

// UnseenTableNative::PosUnseen(float[] q), q = x, y, z, radius (MonoArray: max_length +8, elements +16): unseenfb.cpp's camera
// query in one call instead of the four viewer-channel codes 3-6 (ts3fixes UfPosUnseen, docs/227 probe run 2: 1379 calls/s);
// the same answer as code 6 after 3-5 (1 = unseen for sure, 0 = seen or unknown). Codes 3-6 stay for older ts3fixes.
static int __cdecl UtbIcPosUnseen(void* q) {
    if (!q || *(const LONG*)((const u8*)q + 8) < 4) return 0;
    const float* f = (const float*)((const u8*)q + 16);
    if (!(f[3] >= 0.0f && f[3] < 1000.0f)) return 0;  // as code 6, also NaN
    return UfSphereUnseen(f);
}

// viewer channel code 25: 1 = the ICs are registered (ts3fixes may call Fetch, Bits and PosUnseen); 26: 1 = Check is on (ts3fixes compares too)
static u8 UtbReadyMsg(DWORD code) { return code == 26 ? (ts3native_counters.utbCheck ? 1 : 0) : g_utbIcOk ? 1 : 0; }

static void __cdecl UtbPump() {
    g_utbTid = GetCurrentThreadId();
    if (!g_utbIcOk && g_asAddIc) {  // first pump: Mono is up (as AsSimEnter's registration at the first Simulate)
        g_asAddIc("Ts3Fixes.UnseenTableNative::Fetch", (const void*)UtbIcFetch);
        g_asAddIc("Ts3Fixes.UnseenTableNative::PosUnseen", (const void*)UtbIcPosUnseen);
        g_asAddIc("Ts3Fixes.UnseenTableNative::Bits", (const void*)UtbIcBits);
        g_utbIcOk = true;
        ts3native_counters.utbReady |= 2;
    }
    g_utbTick++;
}

// [UnseenTable] Enabled=1: the pump callback and the UnObjClass hook (no call sites)
static bool ApplyUnseenTable(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"UnseenTable", L"Enabled", 0, ini)) return false;
    Ts3nCounters& k = ts3native_counters;
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    const int r = (int)GetPrivateProfileIntW(L"UnseenTable", L"RadiusM", 3, ini);
    g_utbRadius = (float)(r < 1 ? 1 : r > 10 ? 10 : r);
    k.utbCheck = GetPrivateProfileIntW(L"UnseenTable", L"Check", 0, ini) ? 1 : 0;
    const UINT rt = GetPrivateProfileIntW(L"UnseenTable", L"RefreshTicks", 1, ini);
    g_utbRefreshTicks = rt < 1 ? 1 : rt > 8 ? 8 : rt;
    k.utbTtlMode = 1;  // --ab unseentablettl: 0 = one pump per generation (RefreshTicks=1 makes both arms equal)
    if (SimPumpAdd(UtbPump, "unseen table: tick + UnseenTableNative ICs")) {
        k.utbReady |= 1;
        g_utbOn = true;  // unseen.cpp UtViewerMsg: accept the household table without [UnseenThrottle]
        g_utbClass = UtbClass;
        k.utbMode = GetPrivateProfileIntW(L"UnseenTable", L"Mode", 1, ini) == 1 ? 1 : 0;
    }
    Log("%s unseen table: ready 0x%lX, mode %ld, Check=%ld RadiusM=%d RefreshTicks=%lu; one unseen answer per object per "
        "generation for UnObjClass and the ts3fixes checks (--ab unseentable)", g_utbOn ? "OK  " : "FAIL", k.utbReady, k.utbMode,
        k.utbCheck, (int)g_utbRadius, g_utbRefreshTicks);
    return true;
}
