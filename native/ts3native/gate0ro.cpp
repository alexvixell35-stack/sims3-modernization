// [Gate0RO] (docs/152 §G1; counters v91). Included by ts3native.cpp after slotro.cpp (uses steerro.cpp's QL, lotfd.cpp's
// LtFind and the QL writer subset W4/W5/W6g).
//
// Gate 0 is the lot table find-or-insert LotTable_GetOrInsert 0x62EAB0, reached on the sim from the transform wrapper
// 0x62EF30 (return 0x62EF7A = the wait key). A hit runs LotTable_Find 0x627E00 and returns node + 0x10; it writes only its
// own stack. A miss builds and inserts an entry (0x62AA60, 0x62E680, rehash 0x625940) [verified disassembly 0x62EAB0..
// 0x62EB3C, docs/86 §3]. The worker's only writers of that table are W5 (insert, rehash) under QL exclusive (docs/83 W5).
// So a hit read under QL shared needs no +0x918.
//
// G-a callers (docs/86 §2): the wrapper's caller is RouteFollow_A 0x846B70 (ret 0x846E0E), RouteFollow_B 0x85B470
// (0x85B57E) or RouteFollow_EditPath 0x85F5A0 (0x85F76D, 0x85F7A9), each after its own +0x918 touch (0x82EDB0 at 0x846D1C /
// 0x85B507, 0x82F0B0 at 0x85F6C6) was released. With S2 ([SteerRO]) and AvoidRO on, those touches and their consumers
// (incl. this transform) are answered by the clones while a job runs at entry; the game's code, and with it this gate, runs
// only when no job ran at the RO decision and one started in the gap after the game's own release. Today the gate frame
// holds +0x918 only for the GetOrInsert call (RgLeave releases it), so the callers' unlocked footprint work after the
// transform already runs beside the next job; G1 changes only that the call itself no longer waits for the job.
//
// Rule (RgEnter, outermost non-worker entry, a job running, the sim thread not serving a job callback): second frame G-a,
// S2 and AvoidRO both on (their runtime switches), QL shared free (try only, not nested in another QL hold), LtFind finds the
// key: the game's GetOrInsert runs under QL shared with no +0x918 wait (frame: QL released at its return). Anything else
// (G-b caller, miss, odd table, busy QL, pieces off) waits as today. Every answered call and every shadow call compares the
// predicted entry with GetOrInsert's return (eax) at the leave: a difference = an insert ran (g0Mismatch, logged).
// Shadow=1: the same prediction and compare on G-a entries with no job running (behaviour unchanged).
// Switches g0Mode (+0x3998, --ab gate0ro), g0Shadow (+0x399C, --ab gate0shadow).

static const DWORD G0_WRAP_RET = TS3A(0x0062EF7A);  // the wrapper's return from GetOrInsert (gate-0 key)
static const DWORD kG0Ga[4] = {TS3A(0x00846E0E), TS3A(0x0085B57E), TS3A(0x0085F76D), TS3A(0x0085F7A9)};  // G-a second frames (docs/86 §2)
// g_g0On: declared in routegate.cpp (RgEnter reads it)
static volatile LONG g_g0Noted;
enum { G0_FB_PIECES, G0_FB_QL, G0_FB_TABLE, G0_FB_STATE };

// the wrapper's caller, from its frame (0x62EF30 keeps ebp; slot[-6] = the ebp pushad saved), as P1b reads it
static DWORD G0Second(const DWORD* slot) {
    if (VfRealRet(slot) != G0_WRAP_RET) return 0;
    const DWORD s = (DWORD)(uintptr_t)slot, ebp = slot[-6];
    return ebp > s && ebp - s < 0x10000 && !(ebp & 3) ? VfRealRet((const DWORD*)(uintptr_t)ebp + 1) : 0;
}
static bool G0IsGa(DWORD ret) {
    for (DWORD r : kG0Ga)
        if (r == ret) return true;
    return false;
}

// RgEnter, site 0, outermost non-worker entry, g_rgOffSimOpen already published. job = a job holds +0x918 now.
// Returns the frame bits (0 = not ours: go on as today) and the predicted entry in *pred.
static LONG G0Gate(const DWORD* slot, DWORD tid, bool job, const u8** pred) {
    Ts3nCounters& k = ts3native_counters;
    *pred = NULL;
    if (job ? !k.g0Mode : !k.g0Shadow) return 0;
    const DWORD ret2 = G0Second(slot);
    if (!G0IsGa(ret2)) {
        if (job) InterlockedIncrement(&k.g0Other);
        return 0;
    }
    if (job) InterlockedIncrement(&k.g0Calls);
    int why = -1;
    if (tid != g_async.owner || g_inInvoke) why = G0_FB_STATE;
    else if (job && !(k.steerReady && k.steerMode && (k.avReady & 3) == 3 && k.avMode)) why = G0_FB_PIECES;
    else if (t_qlDepth || t_qlShared || t_slHold || g_steerBroken || !TryAcquireSRWLockShared(&g_ql)) why = G0_FB_QL;
    if (why >= 0) {
        if (job) InterlockedIncrement(&k.g0Fb[why]);
        return 0;
    }
    t_qlShared = 1;
    const DWORD* key = (const DWORD*)(uintptr_t)slot[1];
    const u8* e;
    const bool hit = LtFind((const u8*)(uintptr_t)slot[-2] - 0x48, key[0], key[1], &e) == LT_OK && e;
    if (!hit || !job) {  // a miss inserts: the game's path; shadow: the prediction only, QL not kept
        t_qlShared = 0;
        ReleaseSRWLockShared(&g_ql);
        if (!hit) {
            if (job) InterlockedIncrement(&k.g0Fb[G0_FB_TABLE]);
            return 0;
        }
        *pred = e;
        return RG_REL_G0CMP;
    }
    *pred = e;
    InterlockedIncrement(&k.g0Served);
    MwSkip(MW_G0);
    return RG_REL_G0QL | RG_REL_G0CMP;
}

// RgUndo / RgLeave: QL back (also for a frame dropped by an unwind)
static void G0Undo() {
    t_qlShared = 0;
    ReleaseSRWLockShared(&g_ql);
}
// RgLeave: GetOrInsert returned eax; the predicted hit must be it (else an insert ran)
static void G0Check(const void* pred, DWORD eax) {
    Ts3nCounters& k = ts3native_counters;
    InterlockedIncrement(&k.g0Checks);
    if ((DWORD)(uintptr_t)pred == eax) return;
    InterlockedIncrement(&k.g0Mismatch);
    if (InterlockedIncrement(&g_g0Noted) <= 16)
        Log("gate0 RO: mismatch, predicted entry %08lX, GetOrInsert returned %08lX", (DWORD)(uintptr_t)pred, eax);
}

static void InstallGate0Ro(const wchar_t* ini) {
    Ts3nCounters& k = ts3native_counters;
    if (!GetPrivateProfileIntW(L"Gate0RO", L"Enabled", 0, ini)) { Log("gate0 RO off (Enabled=0)"); return; }
    if (!k.rgEnforce || !k.asyncReady || !g_rgTramp[0]) {
        Log("SKIP gate0 RO: need [RouteGate] Enforce=1 (gate stub 0) with the async path");
        return;
    }
    if (!InstallQlSubset()) { Log("SKIP gate0 RO: the QL writer stubs W4/W5/W6g are not all in place"); return; }
    k.g0Mode = Clamp(GetPrivateProfileIntW(L"Gate0RO", L"Mode", 1, ini), 0, 1);
    k.g0Shadow = Clamp(GetPrivateProfileIntW(L"Gate0RO", L"Shadow", 0, ini), 0, 1);
    k.g0Ready = 1;
    g_g0On = 1;
    Log("OK   gate0 RO: G-a gate-0 hits (RouteFollow_A/B, EditPath) under QL shared while S2 and AvoidRO are on (Mode=%ld "
        "Shadow=%ld; S2 %s, AvoidRO %s)", k.g0Mode, k.g0Shadow, k.steerReady ? "ready" : "NOT ready",
        (k.avReady & 3) == 3 ? "ready" : "NOT ready");
}
