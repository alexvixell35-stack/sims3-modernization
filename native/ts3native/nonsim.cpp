// [Diag] NonSimSplit (default 0), docs/329: measurement only. Splits the sim-thread script time that the docs/322 band sites do
// not own (docs/160 "non-Sim + unattributed") per owner and per distance band, so a later far-lot model can target the owners
// that cost. Included by ts3native.cpp right after taskseam.cpp; needs [Diag] TaskSeam=1 (the same task-run seam: one call per
// run, after EA's run returned) and, for the bands, [UnseenProbe] Enabled=1 Bands=... (unseenband.cpp UbRow / camera / viewers).
//
// Per task node (TaskSeam's side table slot), resolved once (again when the node gets a new task):
//   owner row = the ScriptProxy target's class (objects, lots, SimUpdate, managers), the delegate's method for OneShotFunctionTask /
//   FunctionTask, the invoked object's class for OneShot / ScriptTick invokes, else the task object's class (a Task subclass).
//   kind: object / lot / simdrv (a target holding a Sim in mSim or mActor: SimUpdate and the like, put in that Sim's band) /
//   sim / autonomy (already in the docs/322 rows) / delegate / task / invoke.
// Band row per node (refreshed every 10 s, simdrv every 1 s): bands 0..n from the object's own position (lots: the lot centre
// from the lot table ts3fixes pushes), NS_HH, UB_UNK (no position), NS_GLOB (no position by design: delegates, managers).
// HOUSEHOLD EXEMPTION (stated here because a far-lot model will be built on these rows): a run counts as household (row UB_HH)
// when its object is a household actor (fresh held table), lies inside a household lot (lot table flag 1), is that lot, or the
// held table is stale (unknown scope counts as household). Household lots and household-owned objects are always live: no model
// may take a UB_HH row, whatever its distance. Nothing here changes what runs: it only reads ids, positions and adds sums.
// Sim thread only (TskRunHook / TskInvokeHook and the viewer channel run there); the dump runs on TaskSeam's writer thread and
// reads the sums without a lock (a torn 64-bit read shows in one dump only).

enum { NS_GLOB = UB_ROWS, NS_ROWS = UB_ROWS + 1, NS_CN = 2048, NS_TOP = 60 };
enum { NS_K_OBJ, NS_K_LOT, NS_K_SIMDRV, NS_K_SIM, NS_K_AUT, NS_K_FUNC, NS_K_TASK, NS_K_INV, NS_KINDS };
static const char* const kNsKind[NS_KINDS] = {"object", "lot", "simdrv", "sim", "autonomy", "delegate", "task", "invoke"};

// ---- lot table (ts3fixes UnseenViewers.cs PushLots over the viewer channel, codes 29-37). Grown on the heap: no cap tied to the
// lot count (unbounded worlds). Lots never move; the table is pushed at load and every ~1000 ticks (household moves).
struct NsLot { DWORD lo, hi; float pos[3], r; DWORD flags; };  // flags: 1 household home lot, 2 the world lot
static NsLot *g_nsLot, *g_nsLotIn;
static int g_nsLotN, g_nsLotCap, g_nsLotInN, g_nsLotInCap, g_nsLotHh;
static bool g_nsLotOpen;
static NsLot g_nsLotCur;
static LONG g_nsLotPush, g_nsLotBad;

static bool NsLotGrow(NsLot** a, int* cap, int need) {
    if (need <= *cap) return true;
    const int nc = *cap ? *cap * 2 : 256;
    void* p = *a ? HeapReAlloc(GetProcessHeap(), 0, *a, nc * sizeof(NsLot)) : HeapAlloc(GetProcessHeap(), 0, nc * sizeof(NsLot));
    if (!p) return false;
    *a = (NsLot*)p, *cap = nc;
    return true;
}
// codes: 29 BEGIN, 30 id low, 31 id high, 32/33/34 centre x/y/z float bits, 35 flags, 36 radius float bits (appends), 37 END (count)
static u8 NsLotMsg(DWORD code, DWORD v) {
    if (!g_nsOn) return 0;  // ts3fixes stops for this load
    if (code == 29) { g_nsLotOpen = true, g_nsLotInN = 0; return 1; }
    if (!g_nsLotOpen) { g_nsLotBad++; return 0; }
    switch (code) {
    case 30: g_nsLotCur.lo = v; return 1;
    case 31: g_nsLotCur.hi = v; return 1;
    case 32: case 33: case 34: memcpy(&g_nsLotCur.pos[code - 32], &v, 4); return 1;
    case 35: g_nsLotCur.flags = v; return 1;
    case 36:
        memcpy(&g_nsLotCur.r, &v, 4);
        if (!NsLotGrow(&g_nsLotIn, &g_nsLotInCap, g_nsLotInN + 1)) { g_nsLotOpen = false, g_nsLotBad++; return 0; }
        g_nsLotIn[g_nsLotInN++] = g_nsLotCur;
        return 1;
    default: break;
    }
    g_nsLotOpen = false;
    if ((DWORD)g_nsLotInN != v) { g_nsLotBad++; return 0; }
    NsLot* t = g_nsLot; const int tc = g_nsLotCap;  // swap: the live table becomes the next push's buffer
    g_nsLot = g_nsLotIn, g_nsLotCap = g_nsLotInCap, g_nsLotN = g_nsLotInN;
    g_nsLotIn = t, g_nsLotInCap = tc, g_nsLotInN = 0;
    g_nsLotHh = 0;
    for (int i = 0; i < g_nsLotN; i++) g_nsLotHh += g_nsLot[i].flags & 1;
    g_nsLotPush++;
    return 1;
}
static const NsLot* NsLotById(DWORD lo, DWORD hi) {
    for (int i = 0; i < g_nsLotN; i++) if (g_nsLot[i].lo == lo && g_nsLot[i].hi == hi) return &g_nsLot[i];
    return NULL;
}
// inside a household lot: horizontal distance to the lot centre <= its half diagonal.
// shortcut: a circle, not the lot rectangle (counts a few metres around the lot as household), upgrade if a model needs the edge
static bool NsInHhLot(const float* p) {
    for (int i = 0; i < g_nsLotN && g_nsLotHh; i++) {
        const NsLot& l = g_nsLot[i];
        if (!(l.flags & 1)) continue;
        const float dx = p[0] - l.pos[0], dz = p[2] - l.pos[2];
        if (dx * dx + dz * dz <= l.r * l.r) return true;
    }
    return false;
}

// ---- owner rows ----
struct NsCls { DWORD key; u8 kind; char name[72]; LONGLONG tk[NS_ROWS]; LONG n[NS_ROWS]; };
static NsCls g_nsCls[NS_CN];  // open addressing by key; row 0 is never a key slot's "empty" (key 0 = free)
static NsCls g_nsOther = {0, NS_K_TASK, "(owner table full)", {}, {}};
static int g_nsClsN;
static LONG g_nsClsFull;
struct NsEnt { short cls; signed char row; u8 kind; DWORD at, lo, hi; };  // per TaskSeam slot; cls = row index + 1 (0 = not resolved)
static NsEnt g_nsEnt[TSK_EN];
static LONGLONG g_nsNoEntTk, g_nsBadTk;
static LONG g_nsRuns, g_nsLocates, g_nsSimDrv, g_nsBad;

static void NsReset(DWORD slot) { g_nsEnt[slot & (TSK_EN - 1)].cls = 0; }

static void NsClassName(DWORD klass, char* d, int cap) {
    char n[48], s[56];
    if (!klass || !MdtStr(*(const char* const*)(uintptr_t)(klass + 0x34), n, sizeof(n))) strcpy_s(n, "?");
    if (klass && MdtStr(*(const char* const*)(uintptr_t)(klass + 0x38), s, sizeof(s))) _snprintf_s(d, cap, _TRUNCATE, "%s.%s", s, n);
    else _snprintf_s(d, cap, _TRUNCATE, "%s", n);
}
static int NsRow(DWORD key, u8 kind, DWORD klass, DWORD method) {  // index into g_nsCls, -1 = full (g_nsOther)
    DWORD h = (key >> 3) * 2654435761u >> 21;  // 11 bits = NS_CN
    for (int p = 0; p < NS_CN; p++, h = (h + 1) & (NS_CN - 1)) {
        NsCls& c = g_nsCls[h];
        if (c.key == key && c.kind == kind) return (int)h;
        if (c.key) continue;
        if (g_nsClsN >= NS_CN * 3 / 4) break;
        NsClassName(klass, c.name, sizeof(c.name));
        char m[40];
        if (method && MdtStr(*(const char* const*)(uintptr_t)(method + 0x18), m, sizeof(m))) {  // MonoMethod +0x18 name (modtime.cpp)
            const size_t l = strlen(c.name);
            _snprintf_s(c.name + l, sizeof(c.name) - l, _TRUNCATE, "::%s", m);
        }
        c.kind = kind;
        MemoryBarrier();  // the writer thread reads key last
        c.key = key;
        g_nsClsN++;
        return (int)h;
    }
    g_nsClsFull++;
    return -1;
}

static const TskCls* NsClsOf(DWORD o) { const DWORD k = TskKlass(o); return k ? TskClass(k) : NULL; }  // NULL for null / no class
static bool NsIdOf(DWORD mobj, DWORD* lo, DWORD* hi) {  // a managed ScriptObject -> its proxy's ObjectGuid (mParent.mObjectId)
    const TskCls* c = NsClsOf(mobj);
    if (!c || c->nsParent <= 0) return false;
    const DWORD p = *(const DWORD*)(uintptr_t)(mobj + c->nsParent);
    const TskCls* pc = NsClsOf(p);
    if (!pc || pc->nsObjId <= 0) return false;
    *lo = *(const DWORD*)(uintptr_t)(p + pc->nsObjId), *hi = *(const DWORD*)(uintptr_t)(p + pc->nsObjId + 4);  // ObjectGuid.mValue
    return (*lo | *hi) != 0;
}

// sim thread, SEH by the caller. e = the TaskSeam slot; lo/hi = the node's ObjectGuid (task runs) or 0 (invokes)
static void NsResolve(NsEnt& n, const TskEnt& e, DWORD lo, DWORD hi, bool invoke) {
    const DWORD obj = e.obj, klass = TskKlass(obj);
    const TskCls* c = klass ? TskClass(klass) : NULL;
    DWORD key = klass, nameK = klass, method = 0;
    u8 kind = NS_K_TASK;
    n.lo = n.hi = 0;
    if (invoke) {
        kind = NS_K_INV;
        NsIdOf(obj, &n.lo, &n.hi);
    } else if (c && c->target > 0) {
        const DWORD t = *(const DWORD*)(uintptr_t)(obj + c->target), tk = TskKlass(t);
        const TskCls* tc = tk ? TskClass(tk) : NULL;
        key = nameK = tk ? tk : klass;
        n.lo = lo, n.hi = hi, kind = NS_K_OBJ;
        if (tc && tc->sim) kind = NS_K_SIM;
        else if (tc && tc->aut) kind = NS_K_AUT;
        else if (tc && tc->lot) kind = NS_K_LOT;
        else if (tc && tc->nsSim > 0) {
            const DWORD s = *(const DWORD*)(uintptr_t)(t + tc->nsSim);
            const TskCls* sc = NsClsOf(s);
            DWORD sl, sh;
            if (sc && sc->sim && NsIdOf(s, &sl, &sh)) kind = NS_K_SIMDRV, n.lo = sl, n.hi = sh, g_nsSimDrv++;
        }
    } else if (c && c->function > 0) {
        const DWORD d = *(const DWORD*)(uintptr_t)(obj + c->function), dk = TskKlass(d);
        const TskCls* dc = dk ? TskClass(dk) : NULL;
        const DWORD rm = dc && dc->methodInfo > 0 ? *(const DWORD*)(uintptr_t)(d + dc->methodInfo) : 0;
        method = rm ? *(const DWORD*)(uintptr_t)(rm + 4) : 0;
        if (method) key = method, nameK = *(const DWORD*)(uintptr_t)(method + 8), kind = NS_K_FUNC;  // MonoMethod +8 klass
    }
    n.kind = kind, n.row = UB_UNK, n.at = 0;
    const int r = NsRow(key ? key : 1, kind, nameK, method);
    n.cls = (short)(r + 1);  // 0 = full: g_nsOther
}

// band row of a resolved node (sim thread, SEH by the caller)
static int NsLocate(const NsEnt& n) {
    if (n.kind == NS_K_SIM || n.kind == NS_K_AUT || n.kind == NS_K_FUNC || n.kind == NS_K_TASK) return NS_GLOB;
    if (!g_ubOn || !(n.lo | n.hi)) return UB_UNK;
    if (UbHouseholdId(n.lo, n.hi)) return UB_HH;  // household actor, or no fresh table (unknown scope = household)
    if (n.kind == NS_K_LOT) {
        const NsLot* l = NsLotById(n.lo, n.hi);
        if (!l) return UB_UNK;
        if (l->flags & 1) return UB_HH;  // household lot: always live
        if (l->flags & 2) return NS_GLOB;  // the world lot spans the map: no distance
        return UbRow(l->pos);
    }
    void* mgr = *g_unObjMgr;
    void* o = mgr ? g_unFind(mgr, NULL, n.lo, n.hi) : NULL;
    if (!o) return UB_UNK;
    int k = 1;
    float pos[3];
    if (UnObjClass(o, &k, pos) < 0) return UB_UNK;
    if (n.kind != NS_K_SIMDRV && NsInHhLot(pos)) return UB_HH;  // an object on a household lot: always live
    return UbRow(pos);
}

// TskRunHook / TskInvokeHook, after the run (outside its timed span)
static LONGLONG g_nsSelfTk;
static LONG g_nsSelfN;
static void NsRunBody(int slot, LONGLONG ticks, DWORD lo, DWORD hi, bool invoke);
static void NsRun(int slot, LONGLONG ticks, DWORD lo, DWORD hi, bool invoke) {  // every 64th call timed: the split's own cost
    if (++g_nsRuns & 63) { NsRunBody(slot, ticks, lo, hi, invoke); return; }
    const LONGLONG s0 = TskNow();
    NsRunBody(slot, ticks, lo, hi, invoke);
    g_nsSelfTk += TskNow() - s0, g_nsSelfN++;
}
static void NsRunBody(int slot, LONGLONG ticks, DWORD lo, DWORD hi, bool invoke) {
    if (slot < 0) { g_nsNoEntTk += ticks; return; }
    NsEnt& n = g_nsEnt[slot];
    __try {
        if (!n.cls) NsResolve(n, g_tskEnt[slot], lo, hi, invoke);
        const DWORD now = GetTickCount();
        if (!n.at || now - n.at > (n.kind == NS_K_SIMDRV ? 1000u : 10000u)) n.row = (signed char)NsLocate(n), n.at = now ? now : 1, g_nsLocates++;
    } __except (EXCEPTION_EXECUTE_HANDLER) { n.cls = 0, g_nsBad++, g_nsBadTk += ticks; return; }
    NsCls& c = n.cls > 0 ? g_nsCls[n.cls - 1] : g_nsOther;
    const int row = n.row >= 0 && n.row < NS_ROWS ? n.row : UB_UNK;
    c.tk[row] += ticks, c.n[row]++;
}

// ---- dump: TaskSeam's writer thread, every 60 s, into ts3native.log (collected with the job; the report reads the last one) ----
static double NsUs(LONGLONG t) { return g_tskQpf.QuadPart ? t * 1e6 / g_tskQpf.QuadPart : 0; }
static void NsRowsLine(char* d, int cap, const LONGLONG* tk) {
    int o = 0;
    for (int r = 0; r < NS_ROWS && o < cap; r++) o += sprintf_s(d + o, cap - o, r ? ",%.0f" : "%.0f", NsUs(tk[r]));
}
static void NsDump() {
    if (!g_nsOn || !g_tskStart) return;
    const double wallS = NsUs(TskNow() - g_tskStart) / 1e6;
    static int idx[NS_CN];
    static double w[NS_CN];
    int used = 0;
    for (int i = 0; i < NS_CN; i++) {
        if (!g_nsCls[i].key) continue;
        double s = 0;
        for (int r = 0; r < NS_ROWS; r++) s += (double)g_nsCls[i].tk[r];
        idx[used] = i, w[used++] = s;
    }
    for (int a = 0; a < used && a < NS_TOP; a++)  // partial selection sort: top NS_TOP by time
        for (int b = a + 1; b < used; b++)
            if (w[b] > w[a]) { const double tw = w[a]; w[a] = w[b], w[b] = tw; const int ti = idx[a]; idx[a] = idx[b], idx[b] = ti; }
    LONGLONG rest[NS_ROWS] = {};
    for (int a = NS_TOP; a < used; a++)
        for (int r = 0; r < NS_ROWS; r++) rest[r] += g_nsCls[idx[a]].tk[r];
    for (int r = 0; r < NS_ROWS; r++) rest[r] += g_nsOther.tk[r];
    char line[256];
    Log("nonsim split: %.0f s, runs %ld, owners %d (full %ld), unresolved %.0f ms, no slot %.0f ms, lots %d (household %d, pushes %ld, bad %ld),"
        " locates %ld, simdrv nodes %ld, edges %ld, self %.0f ns per run; rows us = bands 0-6, hh, none, glob (docs/329)", wallS, g_nsRuns, g_nsClsN, g_nsClsFull,
        NsUs(g_nsBadTk) / 1000, NsUs(g_nsNoEntTk) / 1000, g_nsLotN, g_nsLotHh, g_nsLotPush, g_nsLotBad, g_nsLocates, g_nsSimDrv, ts3native_unseenband.nEdges,
        g_nsSelfN ? NsUs(g_nsSelfTk) * 1000 / g_nsSelfN : 0.0);
    for (int s = 0; s < 2; s++) {  // docs/322 sites footprint / plans of Sims that ran inside a non-Sim task run (part of that run's row)
        NsRowsLine(line, sizeof(line), g_nsOutTk[s]);
        Log("nonsim outside %s: us %s", s ? "plans" : "footprint", line);
    }
    for (int a = 0; a < used && a < NS_TOP; a++) {
        const NsCls& c = g_nsCls[idx[a]];
        LONG runs = 0;
        for (int r = 0; r < NS_ROWS; r++) runs += c.n[r];
        NsRowsLine(line, sizeof(line), c.tk);
        Log("nonsim class %d %s runs %ld us %s name %s", a + 1, kNsKind[c.kind], runs, line, c.name);
    }
    NsRowsLine(line, sizeof(line), rest);
    Log("nonsim class 0 rest runs 0 us %s name (owners below the top %d + table full)", line, NS_TOP);
}

static bool ApplyNonSimSplit(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"Diag", L"NonSimSplit", 0, ini)) { Log("nonsim split off ([Diag] NonSimSplit=0)"); return false; }
    if (!ts3native_counters.tskOn) { Log("SKIP nonsim split ([Diag] NonSimSplit=1): needs [Diag] TaskSeam=1 (its task-run seam), off"); return false; }
    g_nsOn = true;
    Log("OK   nonsim split ([Diag] NonSimSplit=1, docs/329): owner per task run (class / delegate / invoked object), band per object%s;"
        " household actors, household lots and their objects in the hh row; a dump in this log every 60 s",
        g_ubOn ? " ([UnseenProbe] Bands)" : " (no [UnseenProbe] Bands: rows none / glob only)");
    return true;
}
