// [Crash2Diag] (docs/259 §9, CRASH-TRIAGE, default off; counters v193 c2*): detector for Apex 3.1.1 crash 2 (rc1111g travel job,
// eip 0x750076). Path [verified minidump + disassembly TS3W 1.67]: IC_Route_DestroyRoute 0x750400 -> route Release 0x8ABE60 ->
// Route dtor 0x75D640 releases the destination set [route+0x144] (vtable 0x1004428) -> DestList_Clear 0x74FC30 on set+0x18 unlinks
// each destination (node at +4) and calls its slot 2 (deleting dtor) -> the first element held a CAS class (vtable 0xFE46A0, two
// slots then text): a destination freed while still linked, its memory reused.
// Logs only, never changes what the game does:
//  - DestList_Clear entry (callers: ClearDestinationList 0x756EA9, set dtor 0x756FB3, route copy 0x7646F3): walks the list and logs
//    every element whose vtable is not a destination class (the Release guard: slot 2 is about to be called through it), with the
//    vtable's place (.rdata or not), its slot 2 (function start or not), owner set, caller, thread, world event phase, R2c gate,
//    and every free of that address in our ring (who freed it, when, whether it was still linked then).
//  - destination deleting dtors 0x74A830 (base, point, radial, lot) and 0x751450 (slot array): every call goes into a 1024-entry
//    ring; one that frees a destination a list neighbour still points at ("freed while linked": the culprit) is logged with the
//    code addresses on its stack.
//  - IC_Route_DestroyRoute 0x750400: a 64-entry ring of handles, printed with a bad element.
// World phase: routesnap's World_FireEvent detour (sim thread) calls C2WorldEvent. Lines: MaxLines (default 200) a session, then
// counted only (c2LinesDropped).
static const DWORD C2_CLEAR = 0x0074FC30, C2_DDTOR = 0x0074A830, C2_DDTOR_SA = 0x00751450, C2_DESTROY = 0x00750400;
static const u8 kC2Clear[6] = {0x56, 0x57, 0x8B, 0xF9, 0x8B, 0x37};    // push esi; push edi; mov edi,ecx; mov esi,[edi]
static const u8 kC2DDtor[6] = {0xF6, 0x44, 0x24, 0x04, 0x01, 0x56};    // test byte [esp+4],1; push esi
static const u8 kC2DDtorSa[6] = {0x56, 0x8B, 0xF1, 0x8B, 0x46, 0x30};  // push esi; mov esi,ecx; mov eax,[esi+0x30]
static const u8 kC2Destroy[6] = {0x8B, 0x0D, 0xC4, 0x40, 0x1D, 0x01};  // mov ecx,[0x11D40C4] (RouteManager)
// destination vtables [verified .rdata scan: slot 2 = 0x74A830 or 0x751450]: base, point, slot array, radial range, lot
static const DWORD kC2DestVt[5] = {0x01004148, 0x010041C8, 0x0100421C, 0x0100423C, 0x0100425C};
static const DWORD C2_TEXT_LO = 0x00401000, C2_TEXT_HI = 0x00F95000, C2_RDATA_LO = 0x00F95000, C2_RDATA_HI = 0x01146000;

struct C2Free {
    volatile LONG seq;  // written last
    DWORD dest, vt, tid, ret, flags;
    LONG ev;
    u8 linked, role, gate, pad;
    LONGLONG qpc;
};
static const DWORD kC2Ring = 1024;
static C2Free g_c2Ring[kC2Ring];
static volatile LONG g_c2Seq;
struct C2Destroy { volatile LONG seq; DWORD handle, tid; LONGLONG qpc; };
static C2Destroy g_c2Dr[64];
static volatile LONG g_c2DrSeq;
static int g_c2On;
static LONG g_c2MaxLines = 200;
static volatile LONG g_c2Lines;
static volatile LONG g_c2Ev = -1;  // last world event id (sim thread), -1 = none yet
static LONGLONG g_c2EvQpc, g_c2Freq = 1;
static void* g_c2ClearT;
static void* g_c2DDtorT;
static void* g_c2DDtorSaT;
static void* g_c2DestroyT;

static LONGLONG C2Now() { LARGE_INTEGER q; QueryPerformanceCounter(&q); return q.QuadPart; }
static double C2MsAgo(LONGLONG q) { return q ? (double)(C2Now() - q) * 1000.0 / (double)g_c2Freq : -1.0; }
static bool C2Line() {
    if (InterlockedIncrement(&g_c2Lines) <= g_c2MaxLines) return true;
    InterlockedIncrement(&ts3native_counters.c2LinesDropped);
    return false;
}
static const char* C2Role(int r) { return r == 0 ? "sim" : r == 1 ? "route worker" : "other"; }
static bool C2KnownVt(DWORD vt) {
    for (DWORD v : kC2DestVt) if (vt == v) return true;
    return false;
}
// plausible slot 2: in .text and a function start (16-aligned, or after int3 padding)
static bool C2FnStart(DWORD f) {
    if (f < C2_TEXT_LO || f >= C2_TEXT_HI) return false;
    if (!(f & 0xF)) return true;
    __try { return *(const u8*)(uintptr_t)(f - 1) == 0xCC; } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}
// 1: a list neighbour still points at the destination's node (+4), so freeing it leaves a dangling element
static int C2Linked(DWORD dest) {
    __try {
        const DWORD node = dest + 4, nx = *(const DWORD*)(uintptr_t)node, pv = *(const DWORD*)(uintptr_t)(node + 4);
        if (nx && nx != node && *(const DWORD*)(uintptr_t)(nx + 4) == node) return 1;
        if (pv && pv != node && *(const DWORD*)(uintptr_t)pv == node) return 1;
    } __except (EXCEPTION_EXECUTE_HANDLER) {}
    return 0;
}
// up to 8 game / ts3native code addresses among the 64 stack words from sp
static void C2Stack(const DWORD* sp, char* out, size_t cap) {
    size_t n = 0;
    out[0] = 0;
    int found = 0;
    __try {
        for (int i = 0; i < 64 && found < 8 && n + 12 < cap; i++) {
            const DWORD v = sp[i];
            if (v >= C2_TEXT_LO && v < C2_TEXT_HI) n += sprintf_s(out + n, cap - n, " %08lX", v), found++;
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {}
}

static void C2WorldEvent(DWORD id) {  // routesnap.cpp SN_WEV, sim thread
    if (!g_c2On) return;
    g_c2EvQpc = C2Now();
    InterlockedExchange(&g_c2Ev, (LONG)id);
}

static void C2LogFreesOf(DWORD e) {
    const LONG top = g_c2Seq;
    int shown = 0;
    for (LONG s = top; s > 0 && top - s < (LONG)kC2Ring && shown < 4; s--) {
        const C2Free& f = g_c2Ring[(DWORD)(s - 1) & (kC2Ring - 1)];
        if (f.seq != s || f.dest != e) continue;
        shown++;
        if (C2Line())
            Log("crash2diag:   freed before: seq %ld vt 0x%08lX flags %lu thread %lu (%s) caller 0x%08lX, still linked %s, world event %ld, "
                "R2c gate %u, %.1f ms ago", s, f.vt, f.flags, f.tid, C2Role(f.role), f.ret, f.linked ? "YES" : "no", f.ev, f.gate, C2MsAgo(f.qpc));
    }
    if (!shown && C2Line())
        Log("crash2diag:   no destination dtor freed 0x%08lX in the ring (last %lu of %ld): its memory left through another path "
            "(heap reuse of a live block, a double free elsewhere, or a free older than the ring)", e, kC2Ring, top);
}

static void C2Bad(DWORD head, int idx, DWORD e, DWORD vt, const DWORD* sp) {
    Ts3nCounters& k = ts3native_counters;
    InterlockedIncrement(&k.c2ClearBad);
    if (!C2Line()) return;
    const DWORD ret = sp[0], tid = GetCurrentThreadId(), set = head - 0x18;
    DWORD s2 = 0, setVt = 0, setRefs = 0;
    __try { s2 = *(const DWORD*)(uintptr_t)(vt + 8); } __except (EXCEPTION_EXECUTE_HANDLER) {}
    __try { setVt = *(const DWORD*)(uintptr_t)set, setRefs = *(const DWORD*)(uintptr_t)(set + 8); } __except (EXCEPTION_EXECUTE_HANDLER) {}
    const char* from = ret == 0x00756EAE ? "ClearDestinationList" : ret == 0x00756FB8 ? "destination set dtor" : ret == 0x007646F8 ? "route copy" : "?";
    char st[128];
    C2Stack(sp, st, sizeof(st));
    Log("crash2diag: BAD destination #%d 0x%08lX vt 0x%08lX (%s, slot 2 0x%08lX %s) in list 0x%08lX (set 0x%08lX vt 0x%08lX refs %ld), clear "
        "from 0x%08lX (%s), thread %lu (%s), world event %ld %.0f ms ago, R2c gate %ld; slot 2 is called through it next; stack%s",
        idx, e, vt, vt >= C2_RDATA_LO && vt < C2_RDATA_HI ? "in .rdata" : "OUTSIDE .rdata", s2, C2FnStart(s2) ? "a function start" : "NOT a function start",
        head, set, setVt, (LONG)setRefs, ret, from, tid, C2Role(PvSwClass(tid)), g_c2Ev, C2MsAgo(g_c2EvQpc), g_arGate, st);
    C2LogFreesOf(e);
    const LONG dt = g_c2DrSeq;
    for (LONG s = dt; s > 0 && dt - s < 4; s--) {
        const C2Destroy& d = g_c2Dr[(DWORD)(s - 1) & 63];
        if (d.seq == s && C2Line()) Log("crash2diag:   DestroyRoute #%ld handle 0x%08lX thread %lu, %.1f ms ago", s, d.handle, d.tid, C2MsAgo(d.qpc));
    }
}

static void __stdcall C2OnClear(DWORD head, const DWORD* sp) {
    Ts3nCounters& k = ts3native_counters;
    InterlockedIncrement(&k.c2Clears);
    __try {
        DWORD n = *(const DWORD*)(uintptr_t)head;
        for (int i = 0; n && n != head && i < 256; i++, n = *(const DWORD*)(uintptr_t)n) {
            const DWORD vt = *(const DWORD*)(uintptr_t)(n - 4);
            if (!C2KnownVt(vt)) C2Bad(head, i, n - 4, vt, sp);
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        InterlockedIncrement(&k.c2ClearBad);
        if (C2Line()) Log("crash2diag: BAD destination list 0x%08lX unreadable, clear from 0x%08lX, thread %lu", head, sp[0], GetCurrentThreadId());
    }
}

static void __stdcall C2OnDDtor(DWORD dest, const DWORD* sp) {
    Ts3nCounters& k = ts3native_counters;
    InterlockedIncrement(&k.c2Frees);
    const DWORD tid = GetCurrentThreadId();
    const int linked = C2Linked(dest);
    const LONG s = InterlockedIncrement(&g_c2Seq);
    C2Free& f = g_c2Ring[(DWORD)(s - 1) & (kC2Ring - 1)];
    f.seq = 0;
    __try { f.vt = *(const DWORD*)(uintptr_t)dest; } __except (EXCEPTION_EXECUTE_HANDLER) { f.vt = 0; }
    f.dest = dest, f.tid = tid, f.ret = sp[0], f.flags = sp[1], f.ev = g_c2Ev;
    f.linked = (u8)linked, f.role = (u8)PvSwClass(tid), f.gate = (u8)(g_arGate != 0), f.qpc = C2Now();
    _WriteBarrier();
    f.seq = s;
    if (!linked) return;
    InterlockedIncrement(&k.c2FreeLinked);
    if (!C2Line()) return;
    char st[128];
    C2Stack(sp, st, sizeof(st));
    Log("crash2diag: FREED WHILE LINKED destination 0x%08lX vt 0x%08lX flags %lu, caller 0x%08lX, thread %lu (%s), world event %ld %.0f ms ago, "
        "R2c gate %ld; stack%s", dest, f.vt, f.flags, f.ret, tid, C2Role(f.role), g_c2Ev, C2MsAgo(g_c2EvQpc), g_arGate, st);
}

static void __stdcall C2OnDestroy(const DWORD* sp) {
    InterlockedIncrement(&ts3native_counters.c2Destroys);
    const LONG s = InterlockedIncrement(&g_c2DrSeq);
    C2Destroy& d = g_c2Dr[(DWORD)(s - 1) & 63];
    d.seq = 0, d.handle = sp[1], d.tid = GetCurrentThreadId(), d.qpc = C2Now();
    _WriteBarrier();
    d.seq = s;
}

// entry stubs: registers kept, then the original through the trampoline; [esp + 0x20] after pushad = the caller's return address
static __declspec(naked) void C2ClearHook() {  // thiscall, ecx = list head (set + 0x18)
    __asm {
        pushad
        lea eax, [esp + 0x20]
        push eax
        push ecx
        call C2OnClear
        popad
        jmp dword ptr [g_c2ClearT]
    }
}
static __declspec(naked) void C2DDtorHook() {  // thiscall, ecx = destination, [esp+4] = flags (1 = free)
    __asm {
        pushad
        lea eax, [esp + 0x20]
        push eax
        push ecx
        call C2OnDDtor
        popad
        jmp dword ptr [g_c2DDtorT]
    }
}
static __declspec(naked) void C2DDtorSaHook() {
    __asm {
        pushad
        lea eax, [esp + 0x20]
        push eax
        push ecx
        call C2OnDDtor
        popad
        jmp dword ptr [g_c2DDtorSaT]
    }
}
static __declspec(naked) void C2DestroyHook() {  // cdecl, [esp+4] = route handle
    __asm {
        pushad
        lea eax, [esp + 0x20]
        push eax
        call C2OnDestroy
        popad
        jmp dword ptr [g_c2DestroyT]
    }
}

static bool ApplyCrash2Diag(const wchar_t* ini) {
    if (GetPrivateProfileIntW(L"Crash2Diag", L"Enabled", 0, ini) <= 0) return false;
    g_c2MaxLines = Clamp(GetPrivateProfileIntW(L"Crash2Diag", L"MaxLines", 200, ini), 0, 100000);
    LARGE_INTEGER f;
    QueryPerformanceFrequency(&f), g_c2Freq = f.QuadPart;
    g_c2On = 1;  // before the jmps go live: the hooks record from their first call
    int n = 0;
    n += InstallDetour("DestList_Clear (crash2diag)", C2_CLEAR, kC2Clear, sizeof(kC2Clear), (void*)&C2ClearHook, &g_c2ClearT) != NULL;
    n += InstallDetour("RouteDest deleting dtor (crash2diag)", C2_DDTOR, kC2DDtor, sizeof(kC2DDtor), (void*)&C2DDtorHook, &g_c2DDtorT) != NULL;
    n += InstallDetour("RouteDest slot-array deleting dtor (crash2diag)", C2_DDTOR_SA, kC2DDtorSa, sizeof(kC2DDtorSa), (void*)&C2DDtorSaHook,
                       &g_c2DDtorSaT) != NULL;
    n += InstallDetour("IC_Route_DestroyRoute (crash2diag)", C2_DESTROY, kC2Destroy, sizeof(kC2Destroy), (void*)&C2DestroyHook, &g_c2DestroyT) != NULL;
    ts3native_counters.c2On = n;
    Log("OK   crash2diag (docs/259 §9): %d of 4 detours; route destination frees ring %lu, bad list elements and frees while linked "
        "logged (MaxLines %ld), logging only", n, kC2Ring, g_c2MaxLines);
    return true;
}
