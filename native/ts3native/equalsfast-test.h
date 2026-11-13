// TestEqualsFast ([EqualsFast] equalsfast.cpp, docs/258 option C). Included by selftest.cpp.
// Fake Mono metadata with the layout equalsfast.cpp reads: MonoClass (+0x34 name, +0x38 namespace, +0x68 field count, +0x88
// field array, byval_arg type byte +0xA6), MonoClassField 0x1C bytes (+0 type, +8 offset, +0xC name), MonoType (+4 attrs,
// +6 type), objects (+0 vtable, vtable +0 klass). EqtOrig is a C model of vanilla 0xE7A590 (docs/258 §2.1): R4 compares like
// Single.Equals (NaN equals NaN, 0 equals -0), the other kinds by bytes; a boxed (non-int) field sets *fields to a marker.
struct EqtClass {
    alignas(8) u8 k[0xC0];
    u8 f[12][0x1C];
    u8 t[12][8];
    const u8* vt[2];  // the vtable: [0] = klass
};
static volatile LONG g_eqtOrig;
static const u8* __cdecl EqtGetFields(const u8* klass, void** iter) {  // 0xE70180's stepping (stride 0x1C, count +0x68, array +0x88)
    const DWORD n = *(const DWORD*)(klass + 0x68);
    const u8* arr = *(const u8* const*)(klass + 0x88);
    const u8* next = *iter ? (const u8*)*iter + 0x1C : arr;
    if (!n || next >= arr + n * 0x1C) return NULL;
    *iter = (void*)next;
    return next;
}
static void* const kEqtBoxed = (void*)(uintptr_t)0xB0B0B0B0;
static bool __cdecl EqtOrig(const u8* a, const u8* b, void** fields) {
    InterlockedIncrement(&g_eqtOrig);
    if (!b) return false;  // vanilla raises ArgumentNullException here
    if (*(const u8* const*)a != *(const u8* const*)b) return false;
    const u8* klass = **(const u8* const* const*)a;
    *fields = NULL;
    bool eq = true;
    void* iter = NULL;
    for (const u8* f; (f = EqtGetFields(klass, &iter)) != NULL;) {
        const u8* type = *(const u8* const*)f;
        const DWORD attrs = *(const DWORD*)(type + 4), o = *(const DWORD*)(f + 8);
        if ((attrs & 0x10) || ((attrs & 0x600) && !strcmp(*(const char* const*)(f + 0xC), "_Deleted"))) continue;
        switch (type[6]) {
        case 0x08: case 0x09: eq = eq && !memcmp(a + o, b + o, 4); break;
        case 0x0A: case 0x0B: eq = eq && !memcmp(a + o, b + o, 8); break;
        case 0x0C: {  // R4: boxed, managed Single.Equals
            *fields = kEqtBoxed;
            const float x = *(const float*)(a + o), y = *(const float*)(b + o);
            eq = eq && (x == y || (x != x && y != y));
            break;
        }
        default: *fields = kEqtBoxed; eq = eq && !memcmp(a + o, b + o, 1); break;  // U1 etc.: boxed, byte compare
        }
    }
    return eq;
}
struct EqtField { u8 kind; DWORD attrs; DWORD off; const char* name; };
static void EqtMake(EqtClass& c, u8 byval, const char* name, int n, const EqtField* fs) {
    memset(&c, 0, sizeof(c));
    c.k[0xA6] = byval;
    *(const char**)(c.k + 0x34) = name;
    *(const char**)(c.k + 0x38) = "EqTest";
    *(DWORD*)(c.k + 0x68) = (DWORD)n;
    *(u8**)(c.k + 0x88) = &c.f[0][0];
    for (int i = 0; i < n; i++) {
        *(WORD*)(c.t[i] + 4) = (WORD)fs[i].attrs;  // MonoType: attrs:16, then the type byte (vanilla reads the dword)
        c.t[i][6] = fs[i].kind;
        *(const u8**)(c.f[i] + 0) = c.t[i];
        *(DWORD*)(c.f[i] + 8) = fs[i].off;
        *(const char**)(c.f[i] + 0xC) = fs[i].name;
    }
    c.vt[0] = c.k;
}
struct EqtObj { alignas(8) u8 b[48]; };
static void EqtNew(EqtObj& o, const EqtClass& c) { memset(&o, 0, sizeof(o)); *(const void**)o.b = c.vt; }
static void EqtReset() {
    memset((void*)g_eqTab, 0, sizeof(g_eqTab));
    g_eqPoolN = 0;
    Ts3nCounters& k = ts3native_counters;
    memset((void*)&k.eqOn, 0, offsetof(Ts3nCounters, eqSpare) + sizeof(k.eqSpare) - offsetof(Ts3nCounters, eqOn));
    k.eqMode = 1;
}
// one call through the hook and one through the model: same answer; hit = answered from a plan (fields NULL, model not run)
static bool EqtSame(const EqtObj& a, const EqtObj* b, bool* hit) {
    Ts3nCounters& k = ts3native_counters;
    void* f1 = (void*)1;
    void* f0 = (void*)1;
    const LONG h0 = k.eqHits;
    const bool r = EqHook(a.b, b ? b->b : NULL, &f1);
    const LONG o0 = g_eqtOrig;
    const bool r0 = EqtOrig(a.b, b ? b->b : NULL, &f0);
    g_eqtOrig = o0;
    *hit = k.eqHits != h0;
    return r == r0 && (!*hit || (f1 == NULL && f0 == NULL));
}

struct EqtThreadArg { EqtClass* cls; int nCls; DWORD seed; LONG bad; LONG hits; };
static DWORD WINAPI EqtWorker(void* p) {
    EqtThreadArg& a = *(EqtThreadArg*)p;
    DWORD s = a.seed;
    for (int i = 0; i < 20000; i++) {
        s = s * 1103515245u + 12345u;
        EqtClass& c = a.cls[(s >> 8) % a.nCls];
        EqtObj x, y;
        EqtNew(x, c), EqtNew(y, c);
        for (int j = 4; j < 28; j += 4) *(DWORD*)(x.b + j) = *(DWORD*)(y.b + j) = (s >> 16) & 3;
        if (s & 0x100000) *(DWORD*)(y.b + 4 + ((s >> 21) % 6) * 4) ^= 1u << ((s >> 24) & 31);
        void* f = (void*)1;
        const bool r = EqHook(x.b, y.b, &f);
        void* f0 = (void*)1;
        const bool r0 = EqtOrig(x.b, y.b, &f0);
        if (r != r0) a.bad++;
        if (f == NULL && f0 == NULL) a.hits++;
    }
    return 0;
}

static void TestEqualsFast() {
    static_assert(offsetof(Ts3nCounters, sbVtGiveUp) == 0x5F80 && offsetof(Ts3nCounters, eqOn) == 0x5F90 && offsetof(Ts3nCounters, eqMode) == 0x5F94 &&
                      offsetof(Ts3nCounters, eqRejMany) == 0x5FC4 && offsetof(Ts3nCounters, eqSpare) == 0x5FC8 && sizeof(Ts3nCounters) == 6323 * 4,
                  "counters v186 layout after v185 (docs/registry.md)");
    static_assert(sizeof(kEqPrologue) == 6, "push ebp; mov ebp,esp; sub esp,10h");
    Ts3nCounters& k = ts3native_counters;
    const EqFn orig0 = g_eqOrig;
    const EqFieldsFn gf0 = g_eqGetFields;
    CRITICAL_SECTION* cs0 = g_eqLoaderCs;
    static CRITICAL_SECTION cs;
    InitializeCriticalSection(&cs);
    g_eqOrig = EqtOrig, g_eqGetFields = EqtGetFields, g_eqLoaderCs = &cs;
    EqtReset();
    bool hit = false;
    void* f = (void*)1;

    // int-backed enum: value__ (special name, not "_Deleted") + static literals of the enum's own type: one I4 field
    static EqtClass en;
    const EqtField fen[3] = {{0x08, 0x606, 4, "value__"}, {0x11, 0x8056, 0, "A"}, {0x11, 0x8056, 0, "B"}};
    EqtMake(en, 0x11, "Mood", 3, fen);
    EqtObj a, b, c;
    EqtNew(a, en), EqtNew(b, en), EqtNew(c, en);
    *(int*)(a.b + 4) = 5, *(int*)(b.b + 4) = 5, *(int*)(c.b + 4) = 7;
    g_eqtOrig = 0;
    f = (void*)1;
    assert(EqHook(a.b, b.b, &f) && f == NULL && g_eqtOrig == 1 && k.eqFtNew == 1 && k.eqPlans == 1 && k.eqHits == 0);  // vanilla, then the plan
    f = (void*)1;
    assert(EqHook(a.b, b.b, &f) && f == NULL && g_eqtOrig == 1 && k.eqHits == 1);  // from the plan: no vanilla call
    f = (void*)1;
    assert(!EqHook(a.b, c.b, &f) && f == NULL && g_eqtOrig == 1 && k.eqHits == 2);
    const EqPlan* p = EqFind(en.k);
    assert(p && p->n == 1 && p->off[0] == 4 && p->size[0] == 4);

    // I8 + U4 struct: the high dword of the I8 counts
    static EqtClass s8;
    const EqtField fs8[2] = {{0x0A, 0x1, 8, "Id"}, {0x09, 0x1, 16, "Flags"}};
    EqtMake(s8, 0x11, "Key", 2, fs8);
    EqtNew(a, s8), EqtNew(b, s8);
    *(unsigned __int64*)(a.b + 8) = *(unsigned __int64*)(b.b + 8) = 0x123456789ULL, *(DWORD*)(a.b + 16) = *(DWORD*)(b.b + 16) = 9;
    assert(EqtSame(a, &b, &hit) && !hit && k.eqPlans == 2);
    assert(EqtSame(a, &b, &hit) && hit);
    *(unsigned __int64*)(b.b + 8) = 0x223456789ULL;
    assert(EqtSame(a, &b, &hit) && hit && !EqHook(a.b, b.b, &f));
    *(unsigned __int64*)(b.b + 8) = 0x123456789ULL, *(DWORD*)(b.b + 16) = 8;
    assert(EqtSame(a, &b, &hit) && hit && !EqHook(a.b, b.b, &f));

    // mixed I4 + R4: stays on vanilla (NaN equals NaN, 0 equals -0 there: no bit compare)
    static EqtClass mx;
    const EqtField fmx[2] = {{0x08, 0x1, 4, "N"}, {0x0C, 0x1, 8, "W"}};
    EqtMake(mx, 0x11, "Mixed", 2, fmx);
    EqtNew(a, mx), EqtNew(b, mx);
    *(DWORD*)(a.b + 8) = 0x80000000, *(DWORD*)(b.b + 8) = 0;  // -0 vs 0: equal for vanilla, not for a byte compare
    g_eqtOrig = 0;
    assert(EqtSame(a, &b, &hit) && !hit && k.eqRejKind == 1 && k.eqRejects == 1 && g_eqtOrig == 1);
    assert(EqtSame(a, &b, &hit) && !hit && k.eqFtKind == 1 && g_eqtOrig == 2 && EqHook(a.b, b.b, &f) && f == kEqtBoxed);
    *(DWORD*)(a.b + 8) = *(DWORD*)(b.b + 8) = 0x7FC00001;  // NaN == NaN for Single.Equals
    assert(EqtSame(a, &b, &hit) && !hit && EqHook(a.b, b.b, &f));

    // a byte field (U1): boxed by vanilla, not cached
    static EqtClass u1;
    const EqtField fu1[1] = {{0x05, 0x1, 4, "B"}};
    EqtMake(u1, 0x11, "Small", 1, fu1);
    EqtNew(a, u1), EqtNew(b, u1);
    assert(EqtSame(a, &b, &hit) && !hit && k.eqRejKind == 2);

    // generic instance (byval_arg GENERICINST): not cached even with int fields
    static EqtClass gi;
    const EqtField fgi[2] = {{0x08, 0x1, 4, "key"}, {0x08, 0x1, 8, "value"}};
    EqtMake(gi, 0x15, "Pair`2", 2, fgi);
    EqtNew(a, gi), EqtNew(b, gi);
    assert(EqtSame(a, &b, &hit) && !hit && k.eqRejGeneric == 1 && EqtSame(a, &b, &hit) && !hit);

    // nine int fields: over EQ_MAXF
    static EqtClass many;
    EqtField fm[9];
    for (int i = 0; i < 9; i++) fm[i] = {0x08, 0x1, (DWORD)(4 + 4 * i), "x"};
    EqtMake(many, 0x11, "Wide", 9, fm);
    EqtNew(a, many), EqtNew(b, many);
    assert(EqtSame(a, &b, &hit) && !hit && k.eqRejMany == 1);

    // "_Deleted" special-name fields are skipped like vanilla; another special-name R4 field is not
    static EqtClass del, sp;
    const EqtField fdel[2] = {{0x0C, 0x600, 4, "_Deleted"}, {0x08, 0x1, 8, "V"}};
    EqtMake(del, 0x11, "Del", 2, fdel);
    EqtNew(a, del), EqtNew(b, del);
    *(float*)(a.b + 4) = 1.0f, *(float*)(b.b + 4) = 2.0f;
    assert(EqtSame(a, &b, &hit) && !hit && EqtSame(a, &b, &hit) && hit && EqHook(a.b, b.b, &f));
    const EqtField fsp[2] = {{0x0C, 0x600, 4, "_Deletex"}, {0x08, 0x1, 8, "V"}};
    EqtMake(sp, 0x11, "Spec", 2, fsp);
    EqtNew(a, sp), EqtNew(b, sp);
    assert(EqtSame(a, &b, &hit) && !hit && k.eqRejKind == 3);

    // null that, other vtable, switch off: vanilla (counted), nothing cached
    EqtNew(a, en), EqtNew(b, s8);
    const LONG plans = k.eqPlans, rej = k.eqRejects;
    g_eqtOrig = 0;
    assert(!EqHook(a.b, NULL, &f) && k.eqFtNull == 1 && g_eqtOrig == 1);
    assert(!EqHook(a.b, b.b, &f) && k.eqFtType == 1 && g_eqtOrig == 2);
    k.eqMode = 0;
    EqtNew(b, en);
    const LONG hits0 = k.eqHits;
    assert(EqHook(a.b, b.b, &f) && g_eqtOrig == 3 && k.eqHits == hits0);
    k.eqMode = 1;
    assert(k.eqPlans == plans && k.eqRejects == rej);

    // a thread that holds the loader lock builds nothing (class setup may be in flight); the next call does
    static EqtClass busy;
    const EqtField fb[1] = {{0x09, 0x1, 4, "u"}};
    EqtMake(busy, 0x11, "Busy", 1, fb);
    EqtNew(a, busy), EqtNew(b, busy);
    EnterCriticalSection(&cs);
    assert(EqtSame(a, &b, &hit) && !hit && k.eqFtBusy == 1 && !EqFind(busy.k));
    LeaveCriticalSection(&cs);
    assert(EqtSame(a, &b, &hit) && !hit && EqFind(busy.k) && EqtSame(a, &b, &hit) && hit);

    // pool full: vanilla, no entry
    static EqtClass late;
    EqtMake(late, 0x11, "Late", 1, fb);
    EqtNew(a, late), EqtNew(b, late);
    const LONG pool = g_eqPoolN;
    g_eqPoolN = EQ_POOL;
    assert(EqtSame(a, &b, &hit) && !hit && k.eqFtFull == 1 && !EqFind(late.k));
    g_eqPoolN = pool;

    // random values over every class above: the hook and the model never differ
    EqtClass* all[] = {&en, &s8, &mx, &u1, &gi, &many, &del, &busy, &late};
    DWORD s = 7;
    int hits = 0;
    for (int i = 0; i < 20000; i++) {
        s = s * 1103515245u + 12345u;
        EqtClass& cl = *all[(s >> 8) % 9];
        EqtNew(a, cl), EqtNew(b, cl);
        for (int j = 4; j < 48; j += 4) *(DWORD*)(a.b + j) = *(DWORD*)(b.b + j) = (s >> 12) & 1 ? 0x80000000u : (s >> 13) & 3;
        if (s & 0x40000) b.b[4 + (s >> 19) % 40] ^= (u8)(1 << ((s >> 26) & 7));
        assert(EqtSame(a, &b, &hit));
        hits += hit;
    }
    assert(hits > 5000);

    // four threads, 64 fresh int-only classes: plans race in, every answer equals the model's
    static EqtClass mt[64];
    static char names[64][8];
    static EqtField fmt[64][3];
    for (int i = 0; i < 64; i++) {
        sprintf_s(names[i], "T%d", i);
        const int n = 1 + i % 3;
        for (int j = 0; j < n; j++) fmt[i][j] = {(u8)(i & 1 ? 0x08 : (j ? 0x0A : 0x09)), 0x1, (DWORD)(4 + 8 * j), names[i]};
        EqtMake(mt[i], 0x11, names[i], n, fmt[i]);
    }
    EqtThreadArg ta[4];
    HANDLE th[4];
    for (int i = 0; i < 4; i++) ta[i] = {mt, 64, 0x1234u * (i + 1), 0, 0}, th[i] = CreateThread(NULL, 0, EqtWorker, &ta[i], 0, NULL);
    WaitForMultipleObjects(4, th, TRUE, INFINITE);
    for (int i = 0; i < 4; i++) { CloseHandle(th[i]); assert(ta[i].bad == 0 && ta[i].hits > 10000); }
    for (int i = 0; i < 64; i++) assert(EqFind(mt[i].k) && EqFind(mt[i].k)->n == 1 + i % 3);

    // install in this process: Enabled=0 does nothing; Enabled=1 finds no 0xE7A590 prologue in our own image
    wchar_t ini[MAX_PATH];
    GetTempPathW(MAX_PATH, ini);
    swprintf_s(ini + wcslen(ini), MAX_PATH - wcslen(ini), L"ts3n-eq-test-%lu.ini", GetCurrentProcessId());
    WritePrivateProfileStringW(L"EqualsFast", L"Enabled", L"0", ini);
    k.eqMode = 0;
    assert(!ApplyEqualsFast(ini) && !k.eqMode);
    WritePrivateProfileStringW(L"EqualsFast", L"Enabled", L"1", ini);
    assert(!ApplyEqualsFast(ini) && !k.eqOn && !k.eqMode);
    DeleteFileW(ini);

    g_eqOrig = orig0, g_eqGetFields = gf0, g_eqLoaderCs = cs0;
    EqtReset();
    k.eqMode = 0;
    DeleteCriticalSection(&cs);
    printf("equalsfast: enum / int-struct plans (vanilla first, then no walk), R4 / U1 / generic / 9-field / special-name rejects, "
           "_Deleted skip, null / vtable / off / busy / full fall-through, 20000 random pairs, 4 threads x 64 classes: ok\n");
}
