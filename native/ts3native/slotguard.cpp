// [SlotGuard] (docs/539 §18, 0.36.230): float-5 site 0x951A00. CRASH-TRIAGE detector + guard, code and release ini Enabled=0.
// 11-09 crash: ObjMgr_RemoveObject 0xC70150 -> ObjectService_DestroyById 0x939420 -> Object_DeletingDtor 0x935DF0 ->
// Object_Destruct 0x935860 -> Slot component scalar dtor 0x952F60 -> dtor 0x952D20 -> SlotComponent clear 0x9519C0, whose
// ContainmentSlots array pointer [comp+0x14] held 0x40A00000: `mov edx,[eax]` at 0x951A00 read the element vtable 0.
// SlotComponent (vt 0x1044320, 0x68 bytes, placed inside the Object block by Object_Allocate 0x9361D0) [verified disassembly
// TS3W 1.67]: five {count, T* new[] with a count cookie at p-4} pairs +0x08/+0x0C RoutingSlots, +0x10/+0x14 ContainmentSlots,
// +0x18/+0x1C FxSlots, +0x20/+0x24, +0x28/+0x2C. The clear frees each array: cookie != 0 -> element vt[0](3) (vector deleting
// dtor), else EA_Free(p-4); then zeroes the pair (EA zeroes +0x20 twice and leaves +0x28; harmless).
// Hooks (InstallDetour: prologue verified, refused with a SKIP line when it differs, e.g. another tool's jmp):
//   0x9519C0 clear, thiscall(comp), plain ret; callers 0x952D20 dtor (ret 0x952D2F), load 0x951F44 (ret 0x951F49), clone
//            0x951B59 (ret 0x951B5E). Before EA: every pair checked.
//   0x951F20 load, thiscall(comp; data, reset byte), ret 8 (writes +0x14 at 0x9520B7). After EA: every pair checked.
//   0x951AE0 clone, thiscall(comp; src), ret 4, returns al = 1 (writes +0x14 at 0x951C73). After EA: every pair checked.
// A bad array pointer: 0x40A00000, misaligned / outside user space, [p-4..p+4) unreadable, or a cookie != 0 whose first
// element's vtable is not in the exe image. Each one: "slot guard: float5 slot bad: comp X obj Y off +N val V count C" with
// the owning Object (found by walking back to vt 0x1042CA8 whose component list +0x34.. holds comp: id +8/+0xC, type key
// +0x18..+0x24, refcount +4), when (teardown / clear before load / clear before clone / load end / clone end), how long ago
// this component last passed a check (load end / clone end; a lossy 4096-entry table), [UafCheck] lookups (UafNearLog) on the
// component, its Object and the bad value, and a stack scan. Guard=1: the pair's count and pointer are set to 0 (EA then frees
// nothing and the old array leaks) so the teardown cannot fault. First 100 lines a session; counts in every line.
typedef void(__fastcall* SgClearFn)(u8* comp, void* edx);
typedef DWORD(__fastcall* SgLoadFn)(u8* comp, void* edx, void* data, DWORD reset);
typedef DWORD(__fastcall* SgCloneFn)(u8* comp, void* edx, const u8* src);
static SgClearFn g_sgClear;
static SgLoadFn g_sgLoad;
static SgCloneFn g_sgClone;
static const u8 kSgClearPro[] = {0x53, 0x56, 0x8B, 0xF1, 0x8B, 0x4E, 0x0C};  // push ebx; push esi; mov esi,ecx; mov ecx,[esi+0xC]
static const u8 kSgLoadPro[] = {0x53, 0x55, 0x56, 0x33, 0xDB};              // push ebx; push ebp; push esi; xor ebx,ebx
static const u8 kSgClonePro[] = {0x53, 0x55, 0x56, 0x8B, 0xF1};             // push ebx; push ebp; push esi; mov esi,ecx
static const DWORD kSgObjVt = TS3A(0x01042CA8), kSgF5 = 0x40A00000;
static const char* const kSgPair[5] = {"RoutingSlots", "ContainmentSlots", "FxSlots", "slots +0x24", "slots +0x2C"};
static bool g_sgGuard;
static DWORD g_sgExeLo = 0x00401000, g_sgExeHi = 0x01400000;  // element vtables live in the image (selftest: its own range)
static volatile LONG g_sgLogs, g_sgBad, g_sgFixed, g_sgChecks;
struct SgSeen { DWORD comp, tick; };
static SgSeen g_sgSeen[4096];  // last passed check per component (load / clone end), lossy

static const char* SgBadPtr(DWORD p) {  // NULL = fine (0 is fine: EA skips it)
    if (!p) return NULL;
    if (p == kSgF5) return "0x40A00000 (float 5.0)";
    if ((p & 3) || p < 0x10000 || p >= 0xFFFE0000) return "not a heap pointer";
    __try {
        if (*(const volatile DWORD*)(uintptr_t)(p - 4)) {
            const DWORD vt = *(const volatile DWORD*)(uintptr_t)p;
            if (vt < g_sgExeLo || vt >= g_sgExeHi) return "first element's vtable not in the exe";
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) { return "unreadable"; }
    return NULL;
}
static DWORD SgOwner(DWORD comp) {  // the Object whose component list holds comp, 0 = not found
    __try {
        for (DWORD o = (comp - 4) & ~3u, lo = comp > 0x2000 ? comp - 0x2000 : 0; o > lo; o -= 4) {
            if (*(const volatile DWORD*)(uintptr_t)o != kSgObjVt) continue;
            for (int k = 0; k < 32; k++)
                if (*(const volatile DWORD*)(uintptr_t)(o + 0x34 + 4 * k) == comp) return o;
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {}
    return 0;
}
static SgSeen& SgSlot(DWORD comp) { return g_sgSeen[(comp >> 3) & 4095]; }
static const char* SgWhen(DWORD ret, bool after) {
    if (after) return ret ? "load end (0x951F20)" : "clone end (0x951AE0)";
    switch (ret) {
        case TS3A(0x00952D2F): return "teardown (SlotComponent dtor 0x952D20)";
        case TS3A(0x00951F49): return "clear before load (0x951F44)";
        case TS3A(0x00951B5E): return "clear before clone (0x951B59)";
        default: return "clear (other caller)";
    }
}

// returns the bad pairs found (selftest)
static int SgCheck(u8* comp, const char* when, DWORD ret, const DWORD* sp) {
    InterlockedIncrement(&g_sgChecks);
    const DWORD c = (DWORD)(uintptr_t)comp;
    int bad = 0;
    for (int i = 0; i < 5; i++) {
        DWORD* pair = (DWORD*)(comp + 8 + 8 * i);
        const DWORD v = pair[1], n = pair[0];
        const char* why = SgBadPtr(v);
        if (!why) continue;
        bad++;
        InterlockedIncrement(&g_sgBad);
        if (v == kSgF5) {  // docs/539 §21 [Float5Watch] / [Float5Sticky]
            char site[112];
            sprintf_s(site, "slot guard %s: +0x%lX %s", when, (DWORD)(0xC + 8 * i), kSgPair[i]);
            F5wBad(site, c, c + 0xC + 8 * i, v);
        }
        if (g_sgGuard) pair[0] = pair[1] = 0, InterlockedIncrement(&g_sgFixed);
        const DWORD obj = SgOwner(c);
        UafNearLog(c, "slot component"), UafNearLog(v, "slot array value");  // [UafCheck] (docs/539 §17)
        if (obj) UafNearLog(obj, "slot component's Object");
        if (InterlockedIncrement(&g_sgLogs) > 100) continue;
        char ob[160] = "not found (no vt 0x1042CA8 within 0x2000 below listing it)";
        if (obj) __try {
                const DWORD* o = (const DWORD*)(uintptr_t)obj;
                sprintf_s(ob, "id %08lX%08lX, type key %08lX %08lX %08lX %08lX, refs %ld, comp at Object+0x%lX", o[3], o[2], o[6], o[7], o[8], o[9],
                          (LONG)o[1], c - obj);
            } __except (EXCEPTION_EXECUTE_HANDLER) { strcpy_s(ob, "unreadable"); }
        const SgSeen& s = SgSlot(c);
        char seen[64] = "never seen at a load / clone end";
        if (s.comp == c) sprintf_s(seen, "last checked OK %lu ms ago", GetTickCount() - s.tick);
        DWORD f[10] = {};
        const int nf = sp ? CrStack((DWORD)(uintptr_t)sp, f, 10) : 0;
        char fr[10 * 44 + 8];
        CtFrames(f, nf, fr, sizeof(fr));
        Log("slot guard: float5 slot bad: comp 0x%08lX obj 0x%08lX off +0x%lX val 0x%08lX count %lu (%s, %s); Object %s; at %s from 0x%08lX; %s; %s; "
            "thread %lu; save #%ld, %ld s after world load #%ld; bad %ld, zeroed %ld, checks %ld; stack scan (%d):%s", c, obj, (DWORD)(0xC + 8 * i), v, n, kSgPair[i], why,
            ob, when, ret, seen, g_sgGuard ? "pair zeroed (Guard=1)" : "log only (Guard 0)", GetCurrentThreadId(), g_ctInvSave,
            g_udWorldTick ? (LONG)(GetTickCount() - g_udWorldTick) / 1000 : -1L, g_udWorlds, g_sgBad, g_sgFixed, g_sgChecks, nf, fr);
    }
    const bool end = !strncmp(when, "load", 4) || !strncmp(when, "clone", 5);
    if (!bad && end) SgSlot(c) = SgSeen{c, GetTickCount()}, F5wOk(c, F5K_SLOT, when, true);  // docs/539 §21: registered at creation
    if (!strncmp(when, "teardown", 8) && SgSlot(c).comp == c) SgSlot(c).comp = 0;  // the component goes with its Object
    if (!strncmp(when, "teardown", 8)) F5wGone(c);
    return bad;
}

static void __fastcall SgClearHook(u8* comp, void* edx) {
    const DWORD* sp = (const DWORD*)_AddressOfReturnAddress();
    if (comp) SgCheck(comp, SgWhen(*sp, false), *sp, sp);
    g_sgClear(comp, edx);
}
static DWORD __fastcall SgLoadHook(u8* comp, void* edx, void* data, DWORD reset) {
    const DWORD r = g_sgLoad(comp, edx, data, reset);
    const DWORD* sp = (const DWORD*)_AddressOfReturnAddress();
    if (comp) SgCheck(comp, SgWhen(1, true), *sp, sp);
    return r;
}
static DWORD __fastcall SgCloneHook(u8* comp, void* edx, const u8* src) {
    const DWORD r = g_sgClone(comp, edx, src);
    const DWORD* sp = (const DWORD*)_AddressOfReturnAddress();
    if (comp) SgCheck(comp, SgWhen(0, true), *sp, sp);
    return r;
}

static bool ApplySlotGuard(const wchar_t* ini) {
    if (GetPrivateProfileIntW(L"SlotGuard", L"Enabled", 0, ini) <= 0) return false;
    g_sgGuard = GetPrivateProfileIntW(L"SlotGuard", L"Guard", 0, ini) > 0;
    if (!g_sgClear) g_sgClear = (SgClearFn)InstallDetour("SlotComponent_Clear (slot guard)", TS3A(0x009519C0), kSgClearPro, sizeof(kSgClearPro), (void*)SgClearHook, &g_sgClear);
    if (!g_sgLoad) g_sgLoad = (SgLoadFn)InstallDetour("SlotComponent_Load (slot guard)", TS3A(0x00951F20), kSgLoadPro, sizeof(kSgLoadPro), (void*)SgLoadHook, &g_sgLoad);
    if (!g_sgClone) g_sgClone = (SgCloneFn)InstallDetour("SlotComponent_Clone (slot guard)", TS3A(0x00951AE0), kSgClonePro, sizeof(kSgClonePro), (void*)SgCloneHook, &g_sgClone);
    Log("slot guard %s (CRASH-TRIAGE, docs/539 §18): SlotComponent slot arrays +0x0C..+0x2C checked before every clear (teardown 0x9519C0)%s%s; "
        "a 0x40A00000 / non-heap / unreadable array pointer logged (\"slot guard: float5 slot bad\")%s", g_sgClear ? "on" : "off (no hook)",
        g_sgLoad ? ", after every load (0x951F20)" : "", g_sgClone ? ", after every clone (0x951AE0)" : "",
        g_sgGuard ? " and its pair zeroed (Guard=1)" : ", log only (Guard=0)");
    return g_sgClear != NULL;
}
