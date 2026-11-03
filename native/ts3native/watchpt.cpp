// [Watchpoint] (docs/157 §8): hardware data watchpoints for P5 correctness jobs (who writes a field the replay never saw).
// Build-only helper, default off: with Enabled=0 nothing is installed and WpArm returns -1. Included by ts3native.cpp after
// crashreport.cpp (CrStack).
//   WpArm(addr, len 1/2/4, writeOnly): one of DR0-DR3 is set on every thread of the process (a helper thread suspends each
//   other thread, sets the debug registers through Get/SetThreadContext(CONTEXT_DEBUG_REGISTERS), resumes it; the caller
//   waits, so it is set too). The vectored handler takes EXCEPTION_SINGLE_STEP with a watched DR6 bit, logs the writer
//   (eip = the instruction after the write), the address, the value now, the thread and a stack scan, clears DR6 and
//   continues; the slot stays armed. Call it from the hook where the object is created; WpDisarm(slot) frees the slot.
// shortcut: threads created after WpArm are not watched (Windows starts them with DR7 = 0); arm again after a thread
// start if a new thread is the suspect.
#include <tlhelp32.h>

struct WpSlot { DWORD addr; DWORD len; bool write; DWORD obj; };  // obj: the watched child footprint (Lot= arms; 0 = none)
static WpSlot g_wp[4];
static volatile LONG g_wpHits[4];
static volatile LONG g_wpLogN[4];  // hits that were not constructor pairs (the LogHits budget)
static volatile DWORD g_wpLastEip[4];  // selftest
static LONG g_wpLogMax = 16;           // [Watchpoint] LogHits: logged hits per slot
static PVOID g_wpVeh;
static volatile LONG g_wpLock;
static LONG g_wpSetFail;  // threads whose registers could not be set (logged)

static DWORD WpDr7() {
    DWORD d7 = 0;
    for (int i = 0; i < 4; i++) {
        if (!g_wp[i].addr) continue;
        const DWORD rw = g_wp[i].write ? 1 : 3, ln = g_wp[i].len == 1 ? 0 : g_wp[i].len == 2 ? 1 : 3;
        d7 |= 1u << (2 * i);                     // local enable
        d7 |= (rw | ln << 2) << (16 + 4 * i);   // R/W and LEN
    }
    return d7;
}

static DWORD WINAPI WpApplyAll(LPVOID) {
    const DWORD self = GetCurrentThreadId(), pid = GetCurrentProcessId();
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
    if (snap == INVALID_HANDLE_VALUE) return 0;
    const DWORD d7 = WpDr7();
    LONG done = 0;
    THREADENTRY32 te = {sizeof(te)};
    for (BOOL ok = Thread32First(snap, &te); ok; te.dwSize = sizeof(te), ok = Thread32Next(snap, &te)) {
        if (te.th32OwnerProcessID != pid || te.th32ThreadID == self) continue;
        HANDLE h = OpenThread(THREAD_SUSPEND_RESUME | THREAD_GET_CONTEXT | THREAD_SET_CONTEXT, FALSE, te.th32ThreadID);
        if (!h) { InterlockedIncrement(&g_wpSetFail); continue; }
        bool set = false;
        if (SuspendThread(h) != (DWORD)-1) {  // nothing between suspend and resume takes a lock (no heap, no log)
            CONTEXT c = {};
            c.ContextFlags = CONTEXT_DEBUG_REGISTERS;
            if (GetThreadContext(h, &c)) {
                c.Dr0 = g_wp[0].addr, c.Dr1 = g_wp[1].addr, c.Dr2 = g_wp[2].addr, c.Dr3 = g_wp[3].addr;
                c.Dr7 = (c.Dr7 & 0x0000FF00) | d7;  // keep the other bits (GE/LE, GD)
                set = SetThreadContext(h, &c) != 0;
            }
            ResumeThread(h);
        }
        CloseHandle(h);
        if (set) done++;
        else InterlockedIncrement(&g_wpSetFail);
    }
    CloseHandle(snap);
    return (DWORD)done;
}

static LONG WpApply() {  // threads set
    HANDLE t = CreateThread(NULL, 64 << 10, WpApplyAll, NULL, STACK_SIZE_PARAM_IS_A_RESERVATION, NULL);
    if (!t) return 0;
    DWORD n = 0;
    if (WaitForSingleObject(t, 5000) == WAIT_OBJECT_0) GetExitCodeThread(t, &n);
    CloseHandle(t);
    return (LONG)n;
}

// docs/157 §9.2: a pool block reused for a new child footprint takes two stores of the constructor's z inside 0x807400:
// the inline default at 0x807508 (trap eip 0x80750D), then slot +0x48 0x626270 called at 0x807555 (trap eip 0x62627B, the
// `ret 4`, so [esp] is its return 0x807557). Construction, not a writer: counted (wpCtor), not logged
static bool WpCtorHit(DWORD eip, DWORD esp) {
    if (eip == 0x80750D) return true;
    return eip == 0x62627B && Readable((const void*)(uintptr_t)esp, 4) && *(const DWORD*)(uintptr_t)esp == 0x807557;
}

static LONG CALLBACK WpVeh(EXCEPTION_POINTERS* e) {
    if (e->ExceptionRecord->ExceptionCode != EXCEPTION_SINGLE_STEP) return EXCEPTION_CONTINUE_SEARCH;
    CONTEXT* c = e->ContextRecord;
    DWORD hit = c->Dr6 & 0xF;
    for (int i = 0; i < 4; i++) if (!g_wp[i].addr) hit &= ~(1u << i);
    if (!hit) return EXCEPTION_CONTINUE_SEARCH;  // a trace flag or someone else's register
    for (int i = 0; i < 4; i++) {
        if (!(hit & (1u << i))) continue;
        const LONG n = InterlockedIncrement(&g_wpHits[i]);
        InterlockedIncrement(&ts3native_counters.wpHits);
        g_wpLastEip[i] = c->Eip;
        if (WpCtorHit(c->Eip, c->Esp)) { InterlockedIncrement(&ts3native_counters.wpCtor); continue; }
        if (InterlockedIncrement(&g_wpLogN[i]) > g_wpLogMax) continue;
        const WpSlot s = g_wp[i];
        DWORD v = 0;
        if (Readable((const void*)(uintptr_t)s.addr, s.len)) memcpy(&v, (const void*)(uintptr_t)s.addr, s.len);
        DWORD f[6] = {};
        const int nf = CrStack(c->Esp, f, 6);
        const DWORD tid = GetCurrentThreadId();
        char who[80] = "";  // the child now in the block (+0x14 / +0x18 id, +0xC parent): a reused block shows another child
        if (s.obj && Readable((const void*)(uintptr_t)s.obj, 0x1C))
            sprintf_s(who, ", child id 0x%08lX%08lX parent 0x%08lX", *(const DWORD*)(uintptr_t)(s.obj + 0x18), *(const DWORD*)(uintptr_t)(s.obj + 0x14),
                      *(const DWORD*)(uintptr_t)(s.obj + 0xC));
        Log("watchpoint DR%d hit %ld: 0x%08lX len %lu %s by eip 0x%08lX (value now 0x%08lX)%s, thread %lu%s, job %d, stack (%d) %08lX %08lX %08lX %08lX %08lX %08lX",
            i, n, s.addr, s.len, s.write ? "written" : "accessed", c->Eip, v, who, tid, g_workerTid && tid == g_workerTid ? " (route worker)" : "",
            RgJobLive() ? 1 : 0, nf, f[0], f[1], f[2], f[3], f[4], f[5]);
    }
    c->Dr6 = 0;  // the registers stay armed (data breakpoints are traps, no RF needed)
    return EXCEPTION_CONTINUE_EXECUTION;
}

static void WpInstall() {
    if (!g_wpVeh) g_wpVeh = AddVectoredExceptionHandler(1, WpVeh);
}
void WpUninstall() {  // selftest; external linkage: unreferenced in the asi (no C4505)
    if (g_wpVeh) RemoveVectoredExceptionHandler(g_wpVeh), g_wpVeh = NULL;
}

// -1 = off, no free slot, bad length or unaligned address. External linkage: no game caller yet (no C4505)
int WpArm(const void* addr, DWORD len, bool writeOnly) {
    const DWORD a = (DWORD)(uintptr_t)addr;
    if (!g_wpVeh || !a || (len != 1 && len != 2 && len != 4) || a % len) return -1;
    while (InterlockedExchange(&g_wpLock, 1)) Sleep(0);
    int slot = -1;
    for (int i = 0; i < 4 && slot < 0; i++) if (!g_wp[i].addr) slot = i;
    if (slot >= 0) {
        g_wp[slot].addr = a, g_wp[slot].len = len, g_wp[slot].write = writeOnly, g_wp[slot].obj = 0, g_wpLogN[slot] = 0;
        const LONG n = WpApply();
        Log("watchpoint DR%d armed: 0x%08lX len %lu %s, %ld thread(s) set (%ld failed so far)", slot, a, len, writeOnly ? "write" : "read/write", n,
            g_wpSetFail);
    }
    InterlockedExchange(&g_wpLock, 0);
    return slot;
}
void WpDisarm(int slot) {
    if (slot < 0 || slot > 3) return;
    while (InterlockedExchange(&g_wpLock, 1)) Sleep(0);
    g_wp[slot].addr = 0, g_wp[slot].obj = 0;
    WpApply();
    InterlockedExchange(&g_wpLock, 0);
}

// --- docs/157 §8.1: the arm site ([Watchpoint] Lot=, Arms=) and the count-only writer probes (Writers=1) ---
// Lot=<id>: the first Arms child footprints (class 0xFEBF90) inserted live on that lot (W1 0x665C50 entry, replay.cpp
// RpPreBody: the INS record's source) get a write watch on +0x10 (z), so the first write by any writer, hooked or not, logs
// its eip and stack. Arming waits for the route worker thread (threads started after an arm are not watched, see above).
static const DWORD kWpChildVt = 0xFEBF90;  // child footprint class (docs/157 §7.3)
static LONG g_wpArmMax = 1;                // [Watchpoint] Arms (1-4)
static LONG g_wpArmed;
static DWORD g_wpLotFp[64];                // footprints inserted on the lot (writer probe class 2); sim thread appends
static volatile LONG g_wpNLotFp;

static LONG g_wpFollow;                    // [Watchpoint] Follow (docs/157 §10)
struct WpFol { u64 par, id; };
static WpFol g_wpFol[4];                   // removed watched children waiting for their re-insert (0 = free)
static u64 WpKey(const u8* fp) { return (u64)RpD(fp + 0x18) << 32 | RpD(fp + 0x14); }
static u64 WpParKey(const u8* fp) { const u8* p = RpP(fp + 0xC); return p ? WpKey(p) : 0; }  // +0xC = the parent (replay.cpp)

static void WpLotNoteQ(DWORD q) {  // sim thread (replay mirror pass)
    for (int i = 0; i < 16; i++) {
        if (g_wpLotQ[i] == q) return;
        if (!g_wpLotQ[i]) { g_wpLotQ[i] = q; return; }
    }
}
static bool WpLotHas(DWORD fp) {
    const LONG n = g_wpNLotFp < 64 ? g_wpNLotFp : 64;
    for (LONG i = 0; i < n; i++) if (g_wpLotFp[i] == fp) return true;
    return false;
}
static void WpLotInsert(DWORD q, u8* fp) {  // sim thread, W1 entry (inside RpPre's __try)
    int i = 0;
    while (i < 16 && g_wpLotQ[i] && g_wpLotQ[i] != q) i++;
    if (i == 16 || g_wpLotQ[i] != q || !fp) return;
    const DWORD a = (DWORD)(uintptr_t)fp;
    if (!WpLotHas(a) && g_wpNLotFp < 64) g_wpLotFp[g_wpNLotFp] = a, InterlockedIncrement(&g_wpNLotFp);
    if (!g_workerTid || RpD(fp) != kWpChildVt) return;
    for (int j = 0; j < 4; j++) if (g_wp[j].addr == a + 0x10) return;  // re-inserted: already watched
    const u64 par = WpParKey(fp), id = WpKey(fp);
    int fo = -1;  // Follow: a slot the same parent's child left at its remove
    for (int j = 0; j < 4 && g_wpFollow && fo < 0; j++) if (g_wpFol[j].par == par && g_wpFol[j].id == id && (par || id)) fo = j;
    if (fo < 0 && g_wpArmed >= g_wpArmMax) return;
    const int s = WpArm(fp + 0x10, 4, true);
    if (s < 0) return;
    g_wp[s].obj = a;
    if (fo >= 0) {
        g_wpFol[fo].par = g_wpFol[fo].id = 0, InterlockedIncrement(&ts3native_counters.wpFollow[1]);
        Log("watchpoint: lot 0x%08lX%08lX child footprint 0x%08lX (id 0x%08lX%08lX, parent id 0x%08lX%08lX) re-armed DR%d at its insert (follows the removed child)",
            (DWORD)(g_wpLot >> 32), (DWORD)g_wpLot, a, (DWORD)(id >> 32), (DWORD)id, (DWORD)(par >> 32), (DWORD)par, s);
        return;
    }
    g_wpArmed++, InterlockedIncrement(&ts3native_counters.wpArms);
    Log("watchpoint: lot 0x%08lX%08lX child footprint 0x%08lX (id 0x%08lX%08lX, +0x10 0x%08lX) armed DR%d at its insert (%ld of %ld)",
        (DWORD)(g_wpLot >> 32), (DWORD)g_wpLot, a, RpD(fp + 0x18), RpD(fp + 0x14), RpD(fp + 0x10), s, g_wpArmed, g_wpArmMax);
}
// Follow=1 (docs/157 §10): W2 0x666750 entry (sim thread, inside RpPre's __try). ApplyRemove takes the parent's id and erases
// its children (~id) with it, so a remove of the parent's id, the child's id or ~id frees the watched block: disarm it (the
// freelist hands the block to the next constructed child, docs/157 §9.2) and keep the slot for the next insert of a child
// with the same parent id and child id (the rebuilt footprint), so the watch follows the child, not the address
static void WpLotRemove(DWORD lo, DWORD hi) {
    if (!g_wpFollow) return;
    const u64 key = (u64)hi << 32 | lo;
    for (int j = 0; j < 4; j++) {
        const DWORD o = g_wp[j].obj;
        if (!o) continue;
        const u8* fp = (const u8*)(uintptr_t)o;
        const u64 par = WpParKey(fp), id = WpKey(fp);
        if (key != par && key != id && key != ~id) continue;
        int f = 0;
        while (f < 3 && (g_wpFol[f].par || g_wpFol[f].id)) f++;
        g_wpFol[f].par = par, g_wpFol[f].id = id;
        WpDisarm(j);
        InterlockedIncrement(&ts3native_counters.wpFollow[0]);
        Log("watchpoint: lot 0x%08lX%08lX child footprint 0x%08lX (id 0x%08lX%08lX, parent id 0x%08lX%08lX) removed (W2 key 0x%08lX%08lX): DR%d disarmed, "
            "re-armed at the next insert of that child",
            (DWORD)(g_wpLot >> 32), (DWORD)g_wpLot, o, (DWORD)(id >> 32), (DWORD)id, (DWORD)(par >> 32), (DWORD)par, hi, lo, j);
    }
}

// Writers=1: count-only entry probes, 6-byte movss prologue moved to the trampoline (both bodies: movss xmm0,[esp+4];
// movss [ecx+off],xmm0; ret 4). No argument or result changes.
static const u8 kWrPro[] = {0xF3, 0x0F, 0x10, 0x44, 0x24, 0x04};
static const DWORD kWrZAt = 0x626270, kWrP30At = 0x620D80;
static void* g_wrZTramp;
static void* g_wrPTramp;
static void WrCount(volatile LONG* c, DWORD fp) {
    InterlockedIncrement(&c[0]);
    if (RgJobLive()) InterlockedIncrement(&c[1]);
    if (g_wpNLotFp && WpLotHas(fp)) InterlockedIncrement(&c[2]);
}
static void __stdcall WrZCount(DWORD fp) { WrCount(ts3native_counters.wrZ, fp); }
static void __stdcall WrPCount(DWORD fp) { WrCount(ts3native_counters.wrP30, fp); }
static __declspec(naked) void WrZStub() {
    __asm pushad __asm pushfd __asm push ecx __asm call WrZCount
    __asm popfd __asm popad __asm jmp dword ptr [g_wrZTramp]
}
static __declspec(naked) void WrPStub() {
    __asm pushad __asm pushfd __asm push ecx __asm call WrPCount
    __asm popfd __asm popad __asm jmp dword ptr [g_wrPTramp]
}

static bool ApplyWatchpoint(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"Watchpoint", L"Enabled", 0, ini)) return false;
    g_wpLogMax = Clamp(GetPrivateProfileIntW(L"Watchpoint", L"LogHits", 16, ini), 0, 4096);
    WpInstall();
    Log("watchpoint: handler %s (docs/157 §8; armed only by code through WpArm), LogHits=%ld", g_wpVeh ? "installed" : "NOT installed", g_wpLogMax);
    wchar_t lot[32] = {};
    GetPrivateProfileStringW(L"Watchpoint", L"Lot", L"0", lot, 32, ini);
    g_wpLot = _wcstoui64(lot, NULL, 0);
    g_wpArmMax = Clamp(GetPrivateProfileIntW(L"Watchpoint", L"Arms", 1, ini), 1, 4);
    g_wpFollow = GetPrivateProfileIntW(L"Watchpoint", L"Follow", 0, ini) != 0;
    if (g_wpLot)
        Log("watchpoint: Lot=0x%08lX%08lX Arms=%ld Follow=%ld: child footprints' +0x10 armed at their live insert on that lot (needs [RouteSnap] Lots=replay); "
            "constructor pairs (eip 0x80750D, eip 0x62627B from 0x807557) counted, not logged",
            (DWORD)(g_wpLot >> 32), (DWORD)g_wpLot, g_wpArmMax, g_wpFollow);
    if (GetPrivateProfileIntW(L"Watchpoint", L"Writers", 0, ini)) {
        g_wrZTramp = InstallDetour("child z setter 0x626270 (count only)", kWrZAt, kWrPro, sizeof(kWrPro), (void*)WrZStub, &g_wrZTramp);
        g_wrPTramp = InstallDetour("parent +0x30 setter 0x620D80 (count only)", kWrP30At, kWrPro, sizeof(kWrPro), (void*)WrPStub, &g_wrPTramp);
    }
    return g_wpVeh != NULL;
}
