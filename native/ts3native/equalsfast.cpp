// [EqualsFast] (docs/258 option C, counters v186 eq*): a per-class compare plan for ValueType.InternalEquals. Included by
// ts3native.cpp and by native/monohost/monohost.cpp (inside namespace mt, MONOHOST_EQFAST=1 exactness corpus). The includer
// defines EQ_CTR, a struct with the eq* LONG fields (ts3native: ts3native_counters v186).
//
// 0xE7A590 is ves_icall_System_ValueType_InternalEquals (cdecl (this, that, MonoArray** fields), answer in al) [verified,
// docs/258 §2.1]. EA's native bodies ValueType.DefaultEquals 0xDA5970 (Enum.Equals 0xDA7FB0, Attribute.Equals 0xDC4870) call it
// through [0x11EE6CC]. Vanilla: null `that` raises ArgumentNullException; different vtables answer false (fields untouched);
// else *fields = NULL and a walk of every field with mono_class_get_fields 0xE70180, which takes and releases Mono's
// loader_mutex 0x11F43A8 per call: statics (type attrs & 0x10) and special-name "_Deleted" fields (attrs & 0x600) are skipped,
// I4/U4 compare 4 bytes, I8/U8 8 bytes, STRING length + chars, every other kind is boxed into the pair array for the managed
// Equals loop. True when no compare failed and nothing was boxed [verified disassembly 0xE7A590-0xE7A849, TS3W 1.67].
//
// So for a class whose instance fields are all I4/U4/I8/U8 (int-backed enums: value__ only) vanilla's answer is exactly "every
// field's bytes are equal", with *fields = NULL. A plan holds those (offset, size) pairs. It is built once per class, after a
// finished vanilla call, by the ORIGINAL field walk (0xE70180 under the game's own lock, the same rules as above), in a pool
// slot nobody else sees, and published with one InterlockedCompareExchangePointer (a full barrier: a reader that sees the slot
// sees the whole plan). Plans are never changed or freed. A hit compares the bytes with no lock and no walk.
// Everything else falls through to vanilla: the switch off, null `that`, different vtables, classes cached as not eligible
// (any other field kind: R4/R8 because of NaN and -0 (Equals is not a bit compare), short ints, bool, char, string, refs,
// nested structs, I/U; a byval_arg type other than VALUETYPE, i.e. generic instances; more than 8 instance fields), a thread
// that holds the loader lock (it may be inside class setup: docs/258 §4, size_inited is set before the fields exist), and a
// full pool. Option A of docs/258 (skip the lock on size_inited) is NOT used.
// shortcut: assumes MonoClass memory is never freed and reused (TS3 never unloads a domain or image [inferred, docs/258 §5 B]);
// a plan would go stale if it were. Upgrade: clear the table on an image unload hook if that ever happens.

#ifndef EQ_CTR
#error define EQ_CTR (the struct holding the eq* counters) before including equalsfast.cpp
#endif

static const DWORD EQ_ICALL = 0x00E7A590, EQ_GETFIELDS = 0x00E70180;
static const u8 kEqPrologue[6] = {0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x10};  // push ebp; mov ebp,esp; sub esp,10h
typedef bool(__cdecl* EqFn)(const u8* self, const u8* that, void** fields);
typedef const u8*(__cdecl* EqFieldsFn)(const u8* klass, void** iter);
// the game's (the selftest and the host point them at their own)
static EqFn g_eqOrig;  // the trampoline: vanilla InternalEquals
static EqFieldsFn g_eqGetFields = (EqFieldsFn)(uintptr_t)EQ_GETFIELDS;
static CRITICAL_SECTION* g_eqLoaderCs = (CRITICAL_SECTION*)(uintptr_t)0x011F43A8;

enum { EQ_MAXF = 8, EQ_TAB = 8192, EQ_POOL = 4096, EQ_PROBE = 16 };
struct EqPlan {
    const u8* klass;
    LONG n;  // instance fields compared; < 0: not eligible (-1 generic / not VALUETYPE, -2 field kind, -3 too many)
    WORD off[EQ_MAXF];
    u8 size[EQ_MAXF];
};
static EqPlan g_eqPool[EQ_POOL];
static volatile LONG g_eqPoolN, g_eqLogN;
static EqPlan* volatile g_eqTab[EQ_TAB];

static DWORD EqHash(const u8* klass) { return ((DWORD)(uintptr_t)klass >> 3) * 0x9E3779B1u >> 19; }  // 13 bits = EQ_TAB
static const EqPlan* EqFind(const u8* klass) {
    const DWORD h = EqHash(klass);
    for (DWORD i = 0; i < EQ_PROBE; i++) {
        const EqPlan* p = g_eqTab[(h + i) & (EQ_TAB - 1)];
        if (!p) return NULL;
        if (p->klass == klass) return p;
    }
    return NULL;
}
// false: no free slot in the probe window. A slot another thread filled for the same class first counts as published.
static bool EqPublish(EqPlan* p) {
    const DWORD h = EqHash(p->klass);
    for (DWORD i = 0; i < EQ_PROBE; i++) {
        EqPlan* volatile* slot = &g_eqTab[(h + i) & (EQ_TAB - 1)];
        EqPlan* cur = *slot;
        if (!cur) cur = (EqPlan*)InterlockedCompareExchangePointer((PVOID volatile*)slot, p, NULL);
        if (!cur || cur->klass == p->klass) return true;
    }
    return false;
}

// vanilla's field walk, rules copied from 0xE7A5EC-0xE7A6A0; p->n as above
static LONG EqBuild(const u8* klass, EqPlan* p) {
    p->klass = klass;
    if (klass[0xA6] != 0x11) return -1;  // byval_arg (+0xA0) .type: MONO_TYPE_VALUETYPE (enums too); 0x15 = GENERICINST
    LONG n = 0;
    void* iter = NULL;
    for (const u8* f; (f = g_eqGetFields(klass, &iter)) != NULL;) {  // MonoClassField: +0 type, +8 offset, +0xC name
        const u8* type = *(const u8* const*)f;
        const DWORD attrs = *(const DWORD*)(type + 4);
        if (attrs & 0x10) continue;  // static
        if ((attrs & 0x600) && !strcmp(*(const char* const*)(f + 0xC), "_Deleted")) continue;
        const u8 t = type[6];
        const u8 size = t == 0x08 || t == 0x09 ? 4 : t == 0x0A || t == 0x0B ? 8 : 0;  // I4 U4 : I8 U8
        if (!size) return -2;
        const DWORD off = *(const DWORD*)(f + 8);
        if (n == EQ_MAXF || off > 0xFFF0) return -3;
        p->off[n] = (WORD)off;
        p->size[n] = size;
        n++;
    }
    return n;
}

static void EqLearn(const u8* klass) {
    auto& k = EQ_CTR;
    if (g_eqLoaderCs->OwningThread == (HANDLE)(uintptr_t)GetCurrentThreadId()) { k.eqFtBusy++; return; }
    if (g_eqPoolN >= EQ_POOL) { k.eqFtFull++; return; }
    const LONG i = InterlockedIncrement(&g_eqPoolN) - 1;
    if (i >= EQ_POOL) { k.eqFtFull++; return; }
    EqPlan* p = &g_eqPool[i];
    p->n = EqBuild(klass, p);
    if (p->n >= 0) k.eqPlans++;
    else {
        k.eqRejects++;
        if (p->n == -1) k.eqRejGeneric++;
        else if (p->n == -2) k.eqRejKind++;
        else k.eqRejMany++;
    }
    if (!EqPublish(p)) { k.eqFtFull++; return; }
    if (InterlockedIncrement(&g_eqLogN) <= 48) {
        const char* nm = *(const char* const*)(klass + 0x34);
        const char* ns = *(const char* const*)(klass + 0x38);
        static const char* const why[3] = {"not VALUETYPE (generic instance?)", "a field of another kind", "more than 8 fields"};
        if (p->n >= 0) Log("equals fast: plan %s.%s, %ld int field(s) (thread %lu)", ns ? ns : "", nm ? nm : "?", p->n, GetCurrentThreadId());
        else Log("equals fast: %s.%s stays on EA's walk: %s", ns ? ns : "", nm ? nm : "?", why[-p->n - 1]);
    }
}

static bool __cdecl EqHook(const u8* self, const u8* that, void** fields) {
    auto& k = EQ_CTR;
    if (!k.eqMode) return g_eqOrig(self, that, fields);
    if (!that) { k.eqFtNull++; return g_eqOrig(self, that, fields); }
    const u8* vt = *(const u8* const*)self;
    if (vt != *(const u8* const*)that) { k.eqFtType++; return g_eqOrig(self, that, fields); }
    const u8* klass = *(const u8* const*)vt;
    const EqPlan* p = EqFind(klass);
    if (p && p->n >= 0) {
        bool eq = true;
        for (LONG i = 0; i < p->n && eq; i++) {
            const DWORD o = p->off[i];
            eq = *(const DWORD*)(self + o) == *(const DWORD*)(that + o) &&
                 (p->size[i] == 4 || *(const DWORD*)(self + o + 4) == *(const DWORD*)(that + o + 4));
        }
        *fields = NULL;
        k.eqHits++;
        return eq;
    }
    if (p) { k.eqFtKind++; return g_eqOrig(self, that, fields); }
    k.eqFtNew++;
    const bool r = g_eqOrig(self, that, fields);
    EqLearn(klass);
    return r;
}

#ifndef EQ_NO_INSTALL
// [EqualsFast] Enabled=1: the detour, switch eqMode on. Default off (code and repo ini) until measured (docs/258 §6).
static bool ApplyEqualsFast(const wchar_t* ini) {
    if (GetPrivateProfileIntW(L"EqualsFast", L"Enabled", 0, ini) <= 0) { Log("equals fast off (Enabled=0)"); return false; }
    if (!InstallDetour("ValueType.InternalEquals (equals fast)", EQ_ICALL, kEqPrologue, sizeof(kEqPrologue), (void*)EqHook, &g_eqOrig))
        return false;  // DetourCore logged the SKIP
    EQ_CTR.eqMode = 1;
    EQ_CTR.eqOn = 1;
    Log("OK   equals fast (docs/258 option C): InternalEquals 0xE7A590 answers int-only value types (enums) from a per-class plan, "
        "no loader lock (switch eqMode +0x5F94, --ab equalsfast)");
    return true;  // the plan lines
}
#endif
