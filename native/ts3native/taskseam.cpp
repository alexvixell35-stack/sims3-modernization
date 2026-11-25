// [Diag] TaskSeam (default 0), docs/308 (docs/302 §3 #3). Included by ts3native.cpp after modtime.cpp (MdtTablesInit /
// MdtOwnerOf / MdtOwnerOfImage), taskserve.cpp (TsRunTaskHook, the same call sites) and unseen.cpp (g_utHeld, the household
// table ts3fixes pushes). A diagnostic: it times and attributes, it never changes what runs or when.
//
// The seam: the four call sites of ScriptHost_RunTask 0xD7F000 (thiscall(host, &node, bool, bool), ret 0Ch [verified Steam +
// retail 1.67 bytes, docs/308 §2]): ThreadedObjects resume 0xD7FA74 and first start 0xD7FB53, TaskObjects 0xD7FE01 (resume or
// first start), 0xD800FC; and the two per-object mono_runtime_invoke 0xEA1090 calls of OneShotObjects 0xD748C2 and ScriptTick
// 0xD80D14 (cdecl(method, obj, params, &exc)). Each site's rel32 is retargeted (byte-checked: E8 to its target, or to
// TaskServe's TsRunTaskHook when [RouteAsync] TaskServe owns the four RunTask sites; all or none).
// Per run: node = *entry (map node: +0/+4 the task's ObjectGuid, +8 task state), state [0] MonoMethod, [1] task object
// [verified docs/166 §1, docs/04a §1]. Owner resolved once per node into a side table (re-resolved when the node's state or
// object changes): ScriptProxy.mTarget -> its class's assembly; OneShotFunctionTask / FunctionTask mFunction -> the delegate's
// method_info -> MonoMethod -> assembly; else the task method's assembly (a mod's own Task subclass).
// Scope (reported only): a ScriptProxy target of class Sims3.Gameplay.Actors.Sim whose ObjectGuid (the node key) is not in a
// fresh household table = TS_SC_SIMCAND (an upper bound: the active lot is not known natively); everything else = household.
// Two QueryPerformanceCounter reads and one hash probe per run; every 64th run two more to measure the seam's own cost.
// Sim thread only (learned at the first Simulate iteration); other threads and nested runs call straight through.

#include "taskseam-core.h"

typedef DWORD(__cdecl* TskInvokeFn)(DWORD method, DWORD obj, DWORD params, DWORD* exc);
typedef DWORD(__cdecl* TskFieldFn)(DWORD klass, const char* name);  // mono_class_get_field_from_name 0xE6F720 [verified wimage.h]
static const DWORD TSK_RUNTASK = TS3A(0x00D7F000), TSK_INVOKE = TS3A(0x00EA1090), TSK_FIELD = TS3A(0x00E6F720);
enum { TSK_MAXO = 512 };  // = ModTime's owner rows (g_mdtMod)

static void NsReset(DWORD slot);  // nonsim.cpp (docs/329, included after this file; g_nsOn / g_tskInRun: unseen.cpp)
static void NsRun(int slot, LONGLONG ticks, DWORD lo, DWORD hi, bool invoke);
static void NsDump();
static TsRunTaskFn g_tskNext = (TsRunTaskFn)(uintptr_t)TSK_RUNTASK;  // EA's RunTask or TaskServe's hook
static TskInvokeFn g_tskInvNext = (TskInvokeFn)(uintptr_t)TSK_INVOKE;
static int g_tskOn;                // [Diag] TaskSeam in force
static DWORD g_tskSimTid;          // set by TskIter (the Simulate loop's thread)
static int g_tskDepth;             // sim thread only: nested runs (a served call, an invoke inside a run) belong to the outer one
static LARGE_INTEGER g_tskQpf;
static LONGLONG g_tskStart;        // QPC at the first iteration

// ---- side table: node (or invoke object | 1) -> owner, scope ----
struct TskEnt { DWORD key, state, obj; short owner; u8 kind, scope, role; };  // role (docs/322 bands): 1 a Sim's proxy, 2 AutonomyManager
enum { TSK_EN = 65536, TSK_CN = 4096 };  // docs/322: 8192 nodes / 512 classes filled in BP (957,961 of 1.34M runs unattributed)
static TskEnt g_tskEnt[TSK_EN];
static LONG g_tskEntFull;
// per class: instance offsets of mTarget / mFunction / method_info (-1 none), cached (512 classes)
struct TskCls { DWORD klass; int target, function, methodInfo; u8 sim, aut, lot; int nsSim, nsParent, nsObjId; };  // ns*: docs/329 (NonSimSplit only)
static TskCls g_tskCls[TSK_CN];  // open addressing by klass (12-bit hash)
static int g_tskClsN;

static double TskUs(LONGLONG ticks) { return g_tskQpf.QuadPart ? ticks * 1e6 / g_tskQpf.QuadPart : 0; }
static LONGLONG TskNow() { LARGE_INTEGER c; QueryPerformanceCounter(&c); return c.QuadPart; }

static int TskFieldOff(DWORD klass, const char* name) {  // instance field offset (counts the vtable word), -1 none or static
    const DWORD f = ((TskFieldFn)(uintptr_t)TSK_FIELD)(klass, name);
    if (!f || (*(const WORD*)(uintptr_t)(*(const DWORD*)(uintptr_t)f + 4) & 0x10)) return -1;
    const int off = *(const int*)(uintptr_t)(f + 8);
    return off > 0 ? off : -1;
}
static bool TskClsIs(DWORD klass, const char* ns, const char* name) {
    char n[32], s[48];
    return MdtStr(*(const char* const*)(uintptr_t)(klass + 0x34), n, sizeof(n)) && MdtStr(*(const char* const*)(uintptr_t)(klass + 0x38), s, sizeof(s)) &&
           !strcmp(n, name) && !strcmp(s, ns);
}
static const TskCls* TskClass(DWORD klass) {  // sim thread (the field lookup takes Mono's lock: never from another thread)
    DWORD h = (klass >> 3) * 2654435761u >> 20;
    for (int p = 0; p < TSK_CN; p++, h = (h + 1) & (TSK_CN - 1)) {
        if (g_tskCls[h].klass == klass) return &g_tskCls[h];
        if (!g_tskCls[h].klass) break;
    }
    if (g_tskClsN >= TSK_CN * 3 / 4 || g_tskCls[h].klass) return NULL;
    TskCls c = {klass, TskFieldOff(klass, "mTarget"), TskFieldOff(klass, "mFunction"), TskFieldOff(klass, "method_info"), 0, 0, 0, -1, -1, -1};
    c.sim = TskClsIs(klass, "Sims3.Gameplay.Actors", "Sim") ? 1 : 0;
    c.aut = TskClsIs(klass, "Sims3.Gameplay.Autonomy", "AutonomyManager") ? 1 : 0;
    if (g_nsOn) {  // docs/329: a held Sim (SimUpdate.mSim, mActor), ScriptObject.mParent (its proxy), ScriptProxy.mObjectId (ObjectGuid)
        c.lot = TskClsIs(klass, "Sims3.Gameplay.Core", "Lot") ? 1 : 0;
        c.nsSim = TskFieldOff(klass, "mSim");
        if (c.nsSim < 0) c.nsSim = TskFieldOff(klass, "mActor");
        c.nsParent = TskFieldOff(klass, "mParent"), c.nsObjId = TskFieldOff(klass, "mObjectId");
    }
    g_tskCls[h] = c;
    g_tskClsN++;
    return &g_tskCls[h];
}
static DWORD TskKlass(DWORD obj) { return obj ? *(const DWORD*)(uintptr_t)(*(const DWORD*)(uintptr_t)obj) : 0; }  // obj->vtable->klass

// household table (unseen.cpp g_utHeld, ts3fixes PushHeld: every active-household actor); not fresh = household
static bool TskHouseholdId(DWORD lo, DWORD hi) {
    if (!g_utHeld.n || GetTickCount() - g_utHeld.at > 2000) return true;
    for (int i = 0; i < g_utHeld.n; i++) if (g_utHeld.lo[i] == lo && g_utHeld.hi[i] == hi) return true;
    return false;
}

static void TskOwnerSet(TskEnt& e, const MdtOwner& o) { e.owner = (short)o.idx, e.kind = (u8)o.kind; }
// one run's owner and scope (sim thread, SEH by the caller). method = the run's entry method, obj = the task object.
static void TskResolve(TskEnt& e, DWORD method, DWORD obj, DWORD keyLo, DWORD keyHi, bool proxyKey) {
    MdtOwner o;
    e.owner = 1, e.kind = MDT_GAME, e.scope = TS_SC_HH, e.role = 0;  // unreadable: game, household
    if (MdtOwnerOf(method, &o)) TskOwnerSet(e, o);
    const DWORD k = TskKlass(obj);
    const TskCls* c = k ? TskClass(k) : NULL;
    if (!c) return;
    if (c->target > 0) {  // ScriptProxy: the logic object's class names the owner
        const DWORD t = *(const DWORD*)(uintptr_t)(obj + c->target), tk = TskKlass(t);
        if (tk && MdtOwnerOfImage(*(const DWORD*)(uintptr_t)tk, &o)) TskOwnerSet(e, o);
        const TskCls* tc = tk ? TskClass(tk) : NULL;
        // the proxy's ObjectGuid (the node key) is the Sim's ObjectId [inferred, docs/51 I-556-1]
        if (tc && tc->sim && proxyKey && !TskHouseholdId(keyLo, keyHi)) e.scope = TS_SC_SIMCAND;
        if (tc && proxyKey) e.role = tc->sim ? 1 : tc->aut ? 2 : 0;  // docs/322 bands (scope above stays the summary's)
    } else if (c->function > 0) {  // EA OneShotFunctionTask / NRaas FunctionTask: the delegate's method
        const DWORD d = *(const DWORD*)(uintptr_t)(obj + c->function), dk = TskKlass(d);
        const TskCls* dc = dk ? TskClass(dk) : NULL;
        if (dc && dc->methodInfo > 0) {
            const DWORD rm = *(const DWORD*)(uintptr_t)(d + dc->methodInfo);
            if (rm && MdtOwnerOf(*(const DWORD*)(uintptr_t)(rm + 4), &o)) TskOwnerSet(e, o);
        }
        // a delegate target has no ObjectGuid here: household (unknown scope)
    }
}

static TskEnt* TskLookup(DWORD key, DWORD state, DWORD obj, DWORD method, DWORD keyLo, DWORD keyHi, bool proxyKey) {
    DWORD h = (key >> 3) * 2654435761u >> 16;  // 16 bits = TSK_EN
    for (int p = 0; p < 64; p++, h = (h + 1) & (TSK_EN - 1)) {
        TskEnt& e = g_tskEnt[h];
        if (e.key == key && e.state == state && e.obj == obj) return &e;
        if (e.key == key || !e.key) {  // free, or a reused node (new task): resolve again
            e.key = key, e.state = state, e.obj = obj;
            TskResolve(e, method, obj, keyLo, keyHi, proxyKey);
            if (g_nsOn) NsReset(h);  // docs/329: a new task on this slot
            return &e;
        }
    }
    InterlockedIncrement(&g_tskEntFull);
    return NULL;
}

// ---- per-owner statistics (sim thread writes; the writer thread reads without a lock: a torn 64-bit read shows in one summary only) ----
struct TskOwn { LONGLONG ticks, npcTicks, maxTicks, invTicks; LONG runs, npcRuns, invRuns, iters, runsIter, maxRunsIter; LONG hist[TS_HB]; };
static TskOwn g_tskOwn[TSK_MAXO];
static LONGLONG g_tskSelfTicks;
static LONG g_tskSelfN, g_tskSeq;

static void TskAccount(const TskEnt* e, LONGLONG t, bool invoke) {
    Ts3nCounters& k = ts3native_counters;
    TskOwn& o = g_tskOwn[e && e->owner >= 0 && e->owner < TSK_MAXO ? e->owner : 1];
    if (invoke) { o.invTicks += t, o.invRuns++, k.tskInvRuns++; return; }
    o.ticks += t, o.runs++, o.runsIter++, k.tskRuns++;
    o.hist[TsBucket(TskUs(t))]++;
    if (t > o.maxTicks) o.maxTicks = t;
    if (e && e->kind == MDT_MOD && e->scope != TS_SC_HH) o.npcTicks += t, o.npcRuns++;
}

static void __fastcall TskRunHook(void* host, void* edx, void* entry, DWORD a, DWORD b) {
    if (!g_tskOn || g_tskDepth || GetCurrentThreadId() != g_tskSimTid) { g_tskNext(host, edx, entry, a, b); return; }
    const bool self = (++g_tskSeq & 63) == 0;
    const LONGLONG s0 = self ? TskNow() : 0;
    TskEnt* e = NULL;
    DWORD keyLo = 0, keyHi = 0;
    __try {
        const DWORD node = *(const DWORD*)entry, state = *(const DWORD*)(uintptr_t)(node + 8);
        keyLo = *(const DWORD*)(uintptr_t)node, keyHi = *(const DWORD*)(uintptr_t)(node + 4);
        e = TskLookup(node, state, *(const DWORD*)(uintptr_t)(state + 4), *(const DWORD*)(uintptr_t)state, keyLo, keyHi, true);
    } __except (EXCEPTION_EXECUTE_HANDLER) { e = NULL; }
    const LONGLONG t0 = TskNow();
    g_tskDepth++, g_tskInRun = e && e->role ? (u8)(e->role + 1) : 1;  // docs/329: 1 = a non-Sim run (unseenband.cpp UbAdd)
    __try { g_tskNext(host, edx, entry, a, b); } __finally { g_tskDepth--, g_tskInRun = 0; }  // an SEH unwind through the run must not leave the seam off
    const LONGLONG t1 = TskNow();
    TskAccount(e, t1 - t0, false);
    if (g_ubOn && e && e->role) UbTaskRun(e->role, keyLo, keyHi, t1 - t0);  // docs/322: outside the timed run
    if (g_nsOn) NsRun(e ? (int)(e - g_tskEnt) : -1, t1 - t0, keyLo, keyHi, false);  // docs/329: outside the timed run
    if (self) g_tskSelfTicks += (t0 - s0) + (TskNow() - t1), g_tskSelfN++;
}

// OneShotObjects / ScriptTick per-object invoke
static DWORD __cdecl TskInvokeHook(DWORD method, DWORD obj, DWORD params, DWORD* exc) {
    if (!g_tskOn || g_tskDepth || GetCurrentThreadId() != g_tskSimTid) return g_tskInvNext(method, obj, params, exc);
    TskEnt* e = NULL;
    __try { e = TskLookup(obj | 1, method, obj, method, 0, 0, false); } __except (EXCEPTION_EXECUTE_HANDLER) { e = NULL; }  // | 1: own key space
    const LONGLONG t0 = TskNow();
    g_tskDepth++, g_tskInRun = 1;
    DWORD r = 0;
    __try { r = g_tskInvNext(method, obj, params, exc); } __finally { g_tskDepth--, g_tskInRun = 0; }
    const LONGLONG t1 = TskNow();
    TskAccount(e, t1 - t0, true);
    if (g_nsOn) NsRun(e ? (int)(e - g_tskEnt) : -1, t1 - t0, 0, 0, true);  // docs/329
    return r;
}

// ---- end of every Simulate iteration (simloop.cpp callback): runs per iteration ----
static void __cdecl TskIter() {
    if (!g_tskSimTid) g_tskSimTid = GetCurrentThreadId(), g_tskStart = TskNow();
    ts3native_counters.tskIters++;
    for (int i = 0; i < TSK_MAXO; i++) {
        TskOwn& o = g_tskOwn[i];
        if (!o.runsIter) continue;
        o.iters++;
        if (o.runsIter > o.maxRunsIter) o.maxRunsIter = o.runsIter;
        o.runsIter = 0;
    }
}

// ---- summary (writer thread, every 60 s): taskseam-summary.txt next to the log ----
static wchar_t g_tskSum[MAX_PATH];
static void TskWriteSummary() {
    static int idx[TSK_MAXO];
    static double w[TSK_MAXO];
    Ts3nCounters& k = ts3native_counters;
    const double wallS = g_tskStart ? TskUs(TskNow() - g_tskStart) / 1e6 : 0;
    if (wallS <= 0) return;
    LONGLONG all = 0, mod = 0, npc = 0, inv = 0, maxT = 0;
    LONG owners = 0;
    const int nm = g_mdtModN < TSK_MAXO ? g_mdtModN : TSK_MAXO;
    for (int i = 0; i < TSK_MAXO; i++) {
        const TskOwn& o = g_tskOwn[i];
        w[i] = (double)(o.ticks + o.invTicks);
        all += o.ticks, inv += o.invTicks, owners += (o.runs || o.invRuns) ? 1 : 0;
        if (o.maxTicks > maxT) maxT = o.maxTicks;
        if (i < nm && g_mdtMod[i].kind == MDT_MOD) mod += o.ticks, npc += o.npcTicks;
    }
    const double selfNs = g_tskSelfN ? TskUs(g_tskSelfTicks) * 1000 / g_tskSelfN : 0;
    k.tskRunMs = (LONG)(TskUs(all) / 1000), k.tskInvMs = (LONG)(TskUs(inv) / 1000), k.tskOwners = owners, k.tskModMs = (LONG)(TskUs(mod) / 1000);
    k.tskNpcMs = (LONG)(TskUs(npc) / 1000), k.tskSelfNs = (LONG)selfNs, k.tskMaxRunMs = (LONG)(TskUs(maxT) / 1000);
    int got = 0;
    MdtOrder(w, nm, idx, 40, &got);
    HANDLE f = CreateFileW(g_tskSum, GENERIC_WRITE, FILE_SHARE_READ, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (f == INVALID_HANDLE_VALUE) return;
    const double runsPerS = (k.tskRuns + k.tskInvRuns) / wallS;
    MdtPut(f, "ts3native %s task seam report ([Diag] TaskSeam, docs/308). Read-only: nothing in the game was changed.", TS3NATIVE_VERSION);
    MdtPut(f, "%.0f real s since the first Simulate iteration, %ld iterations; task runs %ld (%.1f ms/s), invoke runs (OneShot, ScriptTick) %ld (%.1f ms/s)",
          wallS, k.tskIters, k.tskRuns, TskUs(all) / 1000 / wallS, k.tskInvRuns, TskUs(inv) / 1000 / wallS);
    MdtPut(f, "Mod-owned task runs %.1f ms/s; of them npc-candidate scope (a Sim outside the household table; active lot not checked: an upper bound)"
              " %.1f ms/s = %.1f%% of mod task time (docs/302 §4.7 gate: >= 20%%)",
          TskUs(mod) / 1000 / wallS, TskUs(npc) / 1000 / wallS, mod ? npc * 100.0 / mod : 0);
    MdtPut(f, "Seam self cost (measured on every 64th run): %.0f ns per run, %.3f%% of one core at %.0f runs/s; side table full %ld, classes %d",
          selfNs, selfNs * runsPerS / 1e7, runsPerS, g_tskEntFull, g_tskClsN);
    MdtPut(f, "");
    MdtPut(f, "%4s %-40s %-5s %8s %8s %8s %8s %8s %9s %8s %8s %8s  %s", "rank", "owner", "kind", "ms/s", "runs/s", "p50 us", "p99 us", "max ms", "runs/iter",
          "max/iter", "npc%", "inv ms/s", "package");
    for (int r = 0; r < got; r++) {
        const TskOwn& o = g_tskOwn[idx[r]];
        const MdtMod& m = g_mdtMod[idx[r]];
        MdtPut(f, "%4d %-40.40s %-5s %8.2f %8.1f %8.0f %8.0f %8.1f %9.2f %8ld %7.1f%% %8.2f  %s", r + 1, m.name, kMdtKind[m.kind], TskUs(o.ticks) / 1000 / wallS,
              o.runs / wallS, TsQuantUs(o.hist, 0.5), TsQuantUs(o.hist, 0.99), TskUs(o.maxTicks) / 1000, o.iters ? (double)o.runs / o.iters : 0, o.maxRunsIter,
              o.ticks ? o.npcTicks * 100.0 / o.ticks : 0, TskUs(o.invTicks) / 1000 / wallS, m.pkg[0] ? m.pkg : "-");
    }
    MdtPut(f, "p50 / p99 are bucket upper edges (powers of 2 us). Packages are filled when [Diag] ModTime=1 ran its package scan.");
    CloseHandle(f);
}
static DWORD WINAPI TskWriter(LPVOID) {
    SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_LOWEST);
    for (LONG n = 0;; n++) {
        Sleep(60000);
        TskWriteSummary();
        NsDump();  // docs/329 (nothing unless [Diag] NonSimSplit=1)
        if (n < 3 || !(n % 10)) Log("task seam: summary written (%ld runs, %ld invoke runs, %ld owners, self %ld ns per run)", ts3native_counters.tskRuns,
                                    ts3native_counters.tskInvRuns, ts3native_counters.tskOwners, ts3native_counters.tskSelfNs);
    }
}

// ---- install ----
static CallSite g_tskRunSites[4] = {{TS3A(0x00D7FA74), 0, (void*)TskRunHook}, {TS3A(0x00D7FB53), 0, (void*)TskRunHook},
                                    {TS3A(0x00D7FE01), 0, (void*)TskRunHook}, {TS3A(0x00D800FC), 0, (void*)TskRunHook}};
static const CallSite kTskInvSites[2] = {{TS3A(0x00D748C2), TSK_INVOKE, (void*)TskInvokeHook}, {TS3A(0x00D80D14), TSK_INVOKE, (void*)TskInvokeHook}};

static DWORD TskCallTarget(DWORD site) {
    const u8* p = (const u8*)(uintptr_t)site;
    return Readable(p, 5) && p[0] == 0xE8 ? site + 5 + *(const LONG*)(p + 1) : 0;
}

static bool ApplyTaskSeam(const wchar_t* ini) {
    Ts3nCounters& k = ts3native_counters;
    if (!GetPrivateProfileIntW(L"Diag", L"TaskSeam", 0, ini)) { Log("task seam off ([Diag] TaskSeam=0)"); return false; }
    static volatile DWORD addrs[3] = {TSK_RUNTASK, TSK_INVOKE, TSK_FIELD};  // volatile: constant per build (no C4127)
    if (!addrs[0] || !addrs[1] || !addrs[2]) {  // EA 1.69: encrypted on disk, the sites were never read (docs/308 §2)
        Log("SKIP task seam ([Diag] TaskSeam): no verified addresses for this exe (" TS3A_HOST_NAME "; docs/308 §2)");
        return false;
    }
    // the four RunTask sites go to EA's RunTask, or all four to TaskServe's hook ([RouteAsync] TaskServe owns them): chain
    const DWORD t0 = TskCallTarget(g_tskRunSites[0].site);
    if (t0 != TSK_RUNTASK && t0 != (DWORD)(uintptr_t)TsRunTaskHook) {
        Log("SKIP task seam ([Diag] TaskSeam): RunTask call at 0x%08X goes to 0x%08X, not 0xD7F000 or TaskServe (another hook owns the seam), nothing written",
            g_tskRunSites[0].site, t0);
        return false;
    }
    for (CallSite& s : g_tskRunSites) s.target = t0;
    g_tskNext = (TsRunTaskFn)(uintptr_t)t0;
    QueryPerformanceFrequency(&g_tskQpf);
    MdtTablesInit();
    if (!SimLoopAdd(TskIter, "task seam: iteration ends")) return false;
    if (!PatchCalls("task seam (ScriptHost_RunTask call sites)", g_tskRunSites, 4)) return false;
    k.tskSites = 1;
    if (PatchCalls("task seam (OneShot / ScriptTick runtime invoke call sites)", kTskInvSites, 2)) k.tskSites |= 2;
    g_tskOn = 1, k.tskOn = 1;
    WCpy(g_tskSum, ini);
    if (wchar_t* slash = wcsrchr(g_tskSum, L'\\')) slash[1] = 0, WCat(g_tskSum, L"taskseam-summary.txt");
    HANDLE t = CreateThread(NULL, 64 * 1024, TskWriter, NULL, STACK_SIZE_PARAM_IS_A_RESERVATION, NULL);
    if (t) CloseHandle(t);
    Log("OK   task seam ([Diag] TaskSeam=1, docs/308): 4 task resume sites%s%s, per-owner run time, run-length histogram, runs per iteration;"
        " taskseam-summary.txt next to this log every 60 s", k.tskSites & 2 ? " + 2 invoke sites" : " (invoke sites refused)",
        t0 == TSK_RUNTASK ? "" : ", chained to [RouteAsync] TaskServe");
    return true;
}
