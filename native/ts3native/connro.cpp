// [ConnRoomsRO] (docs/670 §15): IC_World_GetConnectedRoomsImpl answered read-only while an async route job holds +0x918.
// Included by ts3native.cpp after idlero.cpp (uses steerro.cpp's QL, writer set and SrEntry; idlero.cpp's IkEnter / IkMoved /
// IkLeave; avoidro.cpp's AvMayRun; savewrite.cpp's PatchCalls). routegate.cpp calls CnGate from RgEnter (site 11).
//
// The wait (s160-s166 storms: key 0xD882D7, 55 lines / 3.7 s in seven 4 min arms): the key is the Mono icall trampoline's
// return, the taker is our own coarse gate on the IC's entry 0x7929F0 (kRgSites 11, docs/18, docs/67 §13; docs/670 §12.1).
// The IC (cdecl: lot id lo, hi, room, level) asks the script host, the routing world and the lot planner, then calls
// RoomGraph_ConnectedRooms 0x63DCB0 at 0x792A55 (thiscall P: lo, hi, room, level, vector*; ret 0x14) into an empty local
// vector (begin = end = cap = 0, tag "Scripting/WorldHost") and copies the vector into a managed array.
// 0x63DCB0 [verified disassembly 0x63DCB0..0x63DF57]: LotTable_Contains (gate 12), GetOrInsert (gate 0, a hit after
// Contains); the room index (room > 0: room + 0x10; room == 0: level + 4; room < 0: 0xFFFF, all as 16-bit words; the room
// compared as a signed word) against the entry's room vector +0x90 / +0x94 (0x44 bytes each); RoomGraph_FirstUse 0x63D2F0
// (gate 2: builds the lot when entry+0x8C is 0, fills the room record when its first byte is 0, else nothing); then per
// portal pointer of the room (+4 .. +8), unless bit 1 of portal+0x58 is set, the words +0x48 and +0x4A that are neither
// 0xFFFF nor the room index, as (w <= 0x10 ? 0 : w - 0x10), into a set ordered as signed words; the set appended to the
// caller's vector as sign-extended ints (one allocation of 4 * count bytes through 0x4E3F90 with the vector's tag when the
// vector is too small); al = the vector is not empty.
//
// The clone: at the entry gate, on the sim (owner) thread while a job runs, before the gate's wait: the lot entry by the
// Contains rules (SrEntry, no insert), the room index, the built flag and the room's fill byte, the portal words into a
// sorted list, all under QL shared (try only) with S2's protocol (sequence, tripwire epoch, generation), as [IdleCheckRO]
// reads the same vectors and portal fields. An answer is parked for this IC call (keyed by the IC's return slot and its
// arguments) and the gate passes with no frame and no lock; the stub on the call at 0x792A55 hands the parked answer out
// through the game's allocator, in the game's order, and the IC goes on to its managed array as after the game's call.
// Given back to the gate (today's wait, the game's body under +0x918): QL busy, a tripwire, the job's end, a lot that is not
// built, a room that is not filled (both are 0x63D2F0's writes), a null portal pointer, more than kCnCap rooms, no planner.
// Shadow (no job; the gate's frame holds job starts off): the game's 0x63DCB0, then the clone, the vector compared.
// Exported block ts3native_connroomsro: +0x00 mode is the runtime switch (native_set connroomsro); Shadow is ini only.

struct ConnRoomsRoCounters {
    volatile LONG cnMode;            // +0x00 runtime switch: 1 = answer while a job runs
    volatile LONG cnShadow;          // +0x04 1 = with no job running, compare the clone with the game's call
    volatile LONG cnReady;           // +0x08 the call site is patched and the gate asks CnGate
    volatile LONG cnJob;             // +0x0C outermost sim-thread IC entries while a job ran, mode on
    volatile LONG cnTaken;           // +0x10 answered without the gate's wait
    volatile LONG cnEmpty;           // +0x14 of those, the game's "false" (lot not in the table, room index past the vector, no rooms)
    volatile LONG cnRooms;           // +0x18 room numbers handed out
    volatile LONG cnUs;              // +0x1C time in answered clones, us
    LONG cnMaxUs;                    // +0x20
    volatile LONG cnFb[8];           // +0x24 entries given back to the gate: CN_FB_*
    volatile LONG cnUsed;            // +0x44 parked answers the call-site stub handed out
    volatile LONG cnStale;           // +0x48 a parked answer the stub did not use (another call, other arguments, a non-empty vector): the game's call
    volatile LONG cnShadowRuns;      // +0x4C
    volatile LONG cnShadowMismatch;  // +0x50 must stay 0
};
extern "C" __declspec(dllexport) ConnRoomsRoCounters ts3native_connroomsro = {};

static const DWORD CN_SITE = TS3A(0x00792A55), CN_TARGET = TS3A(0x0063DCB0);
typedef DWORD(__fastcall* CnFn)(u8* p, void* edx, DWORD lo, DWORD hi, DWORD room, DWORD level, DWORD* vec);  // ret 0x14, al
typedef void*(__cdecl* CnNewFn)(DWORD size, DWORD tag, DWORD, DWORD, const char* file, DWORD line);          // EA_AllocTagged 0x4E3F90
static CnFn g_cnOrig = (CnFn)(uintptr_t)CN_TARGET;
static CnNewFn g_cnNew = (CnNewFn)(uintptr_t)TS3A(0x004E3F90);

enum { CN_FB_STATE, CN_FB_TRY, CN_FB_TRIP, CN_FB_JOBEND, CN_FB_UNBUILT, CN_FB_UNFILLED, CN_FB_SHAPE, CN_FB_MAYNOT };
enum { CN_WHY_JOBEND = 120, CN_WHY_UNBUILT, CN_WHY_UNFILLED, CN_WHY_SHAPE };  // above the SR_* codes
enum { kCnCap = 128 };
// 0x792A55's return slot is the IC's return slot - 0x34: sub esp, 0x14; push ebx; push edi; five arguments; the call
enum { kCnSlotGap = 0x34 / 4 };

struct CnAnswer { int n; short v[kCnCap]; };
// the parked answer (owner thread only: CnGate and the stub both check the owner first)
static struct { bool set; const DWORD* slot; const u8* p; DWORD lo, hi, room, level; CnAnswer a; } g_cnA;

// 0x63DCB0's room index: 0xFFFF never passes the count test (a vector of 0xFFFF rooms does not exist: 0x44 bytes each)
static DWORD CnRoomIndex(DWORD room, DWORD level) {
    const short r = (short)room;
    return r > 0 ? (room + 0x10) & 0xFFFF : r == 0 ? (level + 4) & 0xFFFF : 0xFFFF;
}

// the set insert 0x6286F0: unique, ordered as signed 16-bit words
static bool CnInsert(CnAnswer* a, short v) {
    int i = 0;
    while (i < a->n && a->v[i] < v) i++;
    if (i < a->n && a->v[i] == v) return true;
    if (a->n == kCnCap) return false;
    memmove(a->v + i + 1, a->v + i, (size_t)(a->n - i) * sizeof(a->v[0]));
    a->v[i] = v;
    a->n++;
    return true;
}

// The read, caller holds QL shared. SR_OK with a->n = the rooms (0 = the game returns false)
static int CnRead(const u8* p, DWORD lo, DWORD hi, DWORD room, DWORD level, CnAnswer* a) {
    a->n = 0;
    const u8* e = NULL;
    const int why = SrEntry(p, lo, hi, true, &e);
    if (why || !e) return why;  // Contains false: al 0 before any other read
    const DWORD idx = CnRoomIndex(room, level);
    const u8* rooms = *(const u8* const*)(e + 0x90);
    const LONG count = (LONG)(*(const u8* const*)(e + 0x94) - rooms) / 0x44;  // signed, as the imul / sar pair
    if (idx >= (DWORD)count) return SR_OK;  // unsigned compare (jb): al 0, 0x63D2F0 is not reached
    if (!e[0x8C]) return CN_WHY_UNBUILT;    // 0x63D2F0 would build the lot (W4)
    const u8* r = rooms + idx * 0x44;
    if (!r[0]) return CN_WHY_UNFILLED;      // 0x63D2F0 would fill the room record (a room-to-room search)
    const u8* const* it = *(const u8* const* const*)(r + 4);
    const u8* const* end = *(const u8* const* const*)(r + 8);
    if (it != end && (!it || !end || end < it || ((const u8*)end - (const u8*)it) & 3)) return CN_WHY_SHAPE;
    for (; it != end; it++) {
        const u8* pt = *it;
        if (!pt) return CN_WHY_SHAPE;  // the game reads [0 + 0x48] here
        if ((pt[0x58] >> 1) & 1) continue;
        for (int k = 0; k < 2; k++) {
            const WORD w = *(const WORD*)(pt + 0x48 + k * 2);
            if (w == 0xFFFF || w == (WORD)idx) continue;
            if (!CnInsert(a, (short)(w <= 0x10 ? 0 : (WORD)(w - 0x10)))) return CN_WHY_SHAPE;
        }
    }
    return SR_OK;
}

// One clone run under S2's protocol; job = the answer will be used
static int CnRun(const u8* p, DWORD lo, DWORD hi, DWORD room, DWORD level, bool job, CnAnswer* a) {
    IkSnap s = {};
    int why = IkEnter(&s);
    if (why) return why;
    why = job && !g_twArmed ? (int)CN_WHY_JOBEND : g_twActive ? (int)SR_FB_TRIP : (int)SR_OK;
    if (!why) why = CnRead(p, lo, hi, room, level, a);
    if (!why) {
        why = IkMoved(s, job);
        if (why == IK_WHY_JOBEND) why = CN_WHY_JOBEND;
    }
    IkLeave();
    return why;
}

static void CnFallback(int why) {
    int i = CN_FB_STATE;
    switch (why) {
    case SR_FB_TRY: i = CN_FB_TRY; break;
    case SR_FB_TRIP: i = CN_FB_TRIP; break;
    case CN_WHY_JOBEND: i = CN_FB_JOBEND; break;
    case CN_WHY_UNBUILT: i = CN_FB_UNBUILT; break;
    case CN_WHY_UNFILLED: i = CN_FB_UNFILLED; break;
    case CN_WHY_SHAPE: case SR_FB_SHAPE: case SR_FB_LOTMISS: i = CN_FB_SHAPE; break;
    default: break;
    }
    InterlockedIncrement(&ts3native_connroomsro.cnFb[i]);
}

// the planner the IC will pass (0x8292D0, 0x829BC0); NULL = the IC returns before its call
static const u8* CnPlanner() {
    const u8* world = g_srWorldGet();
    return world && world[0x974] ? *(const u8* const*)(world + 0x95C) : NULL;
}

// RgEnter (routegate.cpp), site 11, an outermost non-worker entry with enforcement on, before the gate publishes its frame.
// slot = the IC's return slot, slot[1..4] = lot id lo, hi, room, level. true = an answer is parked: no frame, no wait
static bool CnGate(const DWORD* slot, DWORD tid) {
    ConnRoomsRoCounters& k = ts3native_connroomsro;
    g_cnA.set = false;
    if (!k.cnMode || tid != g_async.owner || g_async.state != A_RUNNING) return false;
    InterlockedIncrement(&k.cnJob);
    if (!AvMayRun(tid)) { InterlockedIncrement(&k.cnFb[CN_FB_MAYNOT]); return false; }  // a job callback: the gate borrows as today
    const u8* p = CnPlanner();
    if (!p) { CnFallback(CN_WHY_SHAPE); return false; }
    LARGE_INTEGER t0 = {};
    QueryPerformanceCounter(&t0);
    const int why = CnRun(p, slot[1], slot[2], slot[3], slot[4], true, &g_cnA.a);
    if (why) { CnFallback(why); return false; }
    g_cnA.set = true, g_cnA.slot = slot, g_cnA.p = p;
    g_cnA.lo = slot[1], g_cnA.hi = slot[2], g_cnA.room = slot[3], g_cnA.level = slot[4];
    const LONG us = UsSince(t0);
    InterlockedIncrement(&k.cnTaken);
    if (!g_cnA.a.n) InterlockedIncrement(&k.cnEmpty);
    InterlockedExchangeAdd(&k.cnRooms, g_cnA.a.n);
    InterlockedExchangeAdd(&k.cnUs, us);
    Max(&k.cnMaxUs, us);
    return true;
}

static bool CnVecEmpty(const DWORD* vec) { return vec && !vec[0] && !vec[1] && !vec[2]; }

// 0x63DE54..0x63DF27 on an empty vector: one block of 4 * n bytes with the vector's tag, the set appended in order
static DWORD CnFill(DWORD* vec, const CnAnswer& a) {
    if (!a.n) return 0;
    LONG* b = (LONG*)g_cnNew((DWORD)a.n * 4, vec[3], 0, 0, "ts3native/connro.cpp", 0xE7);
    for (int i = 0; i < a.n; i++) b[i] = a.v[i];
    vec[0] = (DWORD)(uintptr_t)b;
    vec[1] = vec[2] = (DWORD)(uintptr_t)(b + a.n);
    return 1;
}

static volatile LONG g_cnNoted;
static DWORD CnShadow(u8* p, DWORD lo, DWORD hi, DWORD room, DWORD level, DWORD* vec) {
    ConnRoomsRoCounters& k = ts3native_connroomsro;
    const DWORD r = g_cnOrig(p, NULL, lo, hi, room, level, vec);
    static CnAnswer a;  // owner thread only
    const int why = CnRun(p, lo, hi, room, level, false, &a);
    InterlockedIncrement(&k.cnShadowRuns);
    if (why) { CnFallback(why); return r; }
    const LONG* b = (const LONG*)(uintptr_t)vec[0];
    const int n = (int)((vec[1] - vec[0]) / 4);
    bool same = n == a.n && ((u8)r != 0) == (n != 0);
    for (int i = 0; same && i < n; i++) same = b[i] == a.v[i];
    if (!same) {
        InterlockedIncrement(&k.cnShadowMismatch);
        if (InterlockedIncrement(&g_cnNoted) <= 16)
            Log("connected rooms RO: SHADOW MISMATCH lot %08lX:%08lX room %ld level %ld: game %d rooms (al %u, first %ld), clone %d (first %d)",
                hi, lo, (LONG)(short)room, (LONG)level, n, (u8)r, n ? b[0] : 0, a.n, a.n ? a.v[0] : 0);
    }
    return r;
}

static DWORD __fastcall CnStub(u8* p, void* edx, DWORD lo, DWORD hi, DWORD room, DWORD level, DWORD* vec) {
    ConnRoomsRoCounters& k = ts3native_connroomsro;
    if (GetCurrentThreadId() != g_async.owner) return g_cnOrig(p, edx, lo, hi, room, level, vec);
    if (g_cnA.set) {
        g_cnA.set = false;
        const DWORD* slot = (const DWORD*)_AddressOfReturnAddress() + kCnSlotGap;
        if (g_cnA.slot == slot && g_cnA.p == p && g_cnA.lo == lo && g_cnA.hi == hi && g_cnA.room == room && g_cnA.level == level &&
            CnVecEmpty(vec)) {
            InterlockedIncrement(&k.cnUsed);
            return CnFill(vec, g_cnA.a);
        }
        InterlockedIncrement(&k.cnStale);  // not the call the answer was read for: the game's body, each gate on its own
        return g_cnOrig(p, edx, lo, hi, room, level, vec);
    }
    if (!k.cnShadow || g_async.state == A_RUNNING || !CnVecEmpty(vec) || t_qlDepth || t_qlShared) return g_cnOrig(p, edx, lo, hi, room, level, vec);
    InterlockedIncrement(&g_rgOffSimOpen);  // no job starts while the two answers are compared (worker Dekker order)
    const DWORD r = g_async.state == A_RUNNING ? g_cnOrig(p, edx, lo, hi, room, level, vec) : CnShadow(p, lo, hi, room, level, vec);
    RgOffSimDrop();
    return r;
}

static const CallSite kCnSite = {CN_SITE, CN_TARGET, (void*)CnStub};
// 0x7929F0's frame up to the call (the slot gap above), 0x792A26 on: the five argument pushes and the empty vector
static const u8 kCnFrame[] = {0x57, 0x89, 0x5C, 0x24, 0x08};  // +6, after the gate's six bytes (sub esp,0x14; push ebx; xor ebx,ebx): push edi; mov [esp+8],ebx
static const u8 kCnArgs[] = {0x8B, 0x54, 0x24, 0x2C, 0x8D, 0x4C, 0x24, 0x0C, 0x51, 0x8B, 0x4C, 0x24, 0x2C, 0x52, 0x8B, 0x54, 0x24, 0x2C, 0x51,
                             0x8B, 0x4C, 0x24, 0x2C, 0x52, 0x51, 0x8B, 0xC8, 0x89, 0x5C, 0x24, 0x20, 0x89, 0x5C, 0x24, 0x24, 0x89, 0x5C, 0x24, 0x28};

// [ConnRoomsRO] Enabled=1: needs [RouteGate] Enforce=1 (gate stub 11) with the async path and the S2 writer set
static void InstallConnRoomsRO(const wchar_t* ini) {
    ConnRoomsRoCounters& k = ts3native_connroomsro;
    if (!GetPrivateProfileIntW(L"ConnRoomsRO", L"Enabled", 0, ini)) { Log("connected rooms RO off (Enabled=0)"); return; }
#ifdef TS3A_TABLE
    if (ts3a_noaddr) { Log("SKIP connected rooms RO: no EA 1.69 address, docs/670"); return; }
#endif
    if (!ts3native_counters.rgEnforce || !ts3native_counters.asyncReady || !g_rgTramp[11]) {
        Log("SKIP connected rooms RO: needs [RouteGate] Enforce=1 (gate stub 11) with the async path");
        return;
    }
    // the gate's own six bytes were checked when its stub went in; the rest of the frame in place
    if (!Matches((const void*)(uintptr_t)(kRgSites[11].addr + 6), kCnFrame, sizeof(kCnFrame)) ||
        !Matches((const void*)(uintptr_t)(CN_SITE - sizeof(kCnArgs) - 8), kCnArgs, sizeof(kCnArgs))) {
        Log("SKIP connected rooms RO: the IC's frame up to 0x%08lX differs", CN_SITE);
        return;
    }
    if (!InstallSrWriters()) { Log("SKIP connected rooms RO: the S2 writer set (W1-W6, W4, tripwires) is not all in place"); return; }
    if (!PatchCalls("connected rooms RO (0x7929F0 -> RoomGraph_ConnectedRooms)", &kCnSite, 1)) return;
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    k.cnMode = Clamp(GetPrivateProfileIntW(L"ConnRoomsRO", L"Mode", 1, ini), 0, 1);
    k.cnShadow = Clamp(GetPrivateProfileIntW(L"ConnRoomsRO", L"Shadow", 0, ini), 0, 1);
    k.cnReady = 1;
    InterlockedExchange(&g_cnOn, 1);
    Log("OK   connected rooms RO: gate 11 + 0x%08lX + the S2 writer set (Mode=%ld Shadow=%ld)", CN_SITE, k.cnMode, k.cnShadow);
}

// the 60 s counter line (ts3native.cpp MaybeLogCounters)
static void CnLogCounters() {
    const ConnRoomsRoCounters& k = ts3native_connroomsro;
    if (!k.cnReady) return;
    Log("connected rooms RO: mode %ld shadow %ld, IC entries with a job %ld: answered %ld (no rooms %ld, rooms %ld; %ld ms, max %ld us), "
        "handed out %ld, not used %ld; given back: state %ld, QL busy %ld, tripwire %ld, job end %ld, lot not built %ld, room not filled "
        "%ld, shape %ld, may not %ld; shadow checks %ld mismatch %ld",
        k.cnMode, k.cnShadow, k.cnJob, k.cnTaken, k.cnEmpty, k.cnRooms, k.cnUs / 1000, k.cnMaxUs, k.cnUsed, k.cnStale, k.cnFb[CN_FB_STATE],
        k.cnFb[CN_FB_TRY], k.cnFb[CN_FB_TRIP], k.cnFb[CN_FB_JOBEND], k.cnFb[CN_FB_UNBUILT], k.cnFb[CN_FB_UNFILLED], k.cnFb[CN_FB_SHAPE],
        k.cnFb[CN_FB_MAYNOT], k.cnShadowRuns, k.cnShadowMismatch);
}
