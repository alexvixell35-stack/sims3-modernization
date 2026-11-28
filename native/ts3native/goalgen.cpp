// [GoalGen] SameRing (docs/177 §3, design D1, counters v102 gg*): a radius route builds its goal ring twice. In the radial
// goal generator 0x75BE60, max <= min keeps the ring count store `mov dword [esp+0x20], 1` at 0x75C05C with a radius step of
// 0.0 (xorps at 0x75C046), and the ring loop runs i = 0..count inclusive (`cmp eax,[esp+0x20]; jbe` at 0x75C252), so the ring
// builder 0x75B610 (thiscall, 16 stack arguments, `ret 0x40`) is called twice with the same inputs; max > min overwrites the
// count at 0x75C0E4 (>= 2) [verified disassembly, Steam 1.67]. Included by ts3native.cpp after locowin.cpp.
//
// Built as a thunk on that one call (0x75C246, rel32 to 0x75B610) so the A/B can flip it per firing (ggMode):
//   1 = skip the duplicate (count 1, i 1, step bits 0): `ret 0x40` without the call. The loop then ends exactly as with count
//       0; nothing after the loop reads the count, and the flags pushed before the call are the only other inputs that differ
//       (docs/177 §3: the 16th argument is never read). [GoalGen] BytePatch=1 writes the immediate (1 -> 0) at 0x75C060
//       instead, the release form (no runtime switch; the thunk then never sees a duplicate).
//   2 = shadow: both rings run; ring 0's ecx and its 15 arguments are kept and compared with the duplicate's (ggArgMismatch),
//       and on a worker job with [RouteJobPhases] GoalSplit the validation answers of the two rings are compared in order
//       (ggValCmp / ggValMismatch; GgVal from routephases.cpp's leave of 0x759B20 at its ring call 0x75BD86).
// The caller's loop locals sit at the return slot + 0x44 (16 pushed DWORDs and the return address): count +0x20, i +0x28,
// step +0x3C. The 8 bytes at 0x75C05C are checked first (refused and logged when they differ), then the call bytes.
// shortcut: the shadow compares answers (al), not the candidate records; the records are computed from the compared inputs.
static const DWORD GG_RING_CALL = TS3A(0x0075C246), GG_RING = TS3A(0x0075B610), GG_COUNT_STORE = TS3A(0x0075C05C), GG_VAL_RET = TS3A(0x0075BD8B);
static const u8 kGgCount[8] = {0xC7, 0x44, 0x24, 0x20, 0x01, 0x00, 0x00, 0x00};  // mov dword [esp+0x20], 1
static void* g_ggOrig = (void*)(uintptr_t)GG_RING;  // the selftest points it at a fake ring builder
static DWORD g_ggValRet = GG_VAL_RET;               // the selftest's own validation call site

// state: 0 none, 1 recording ring 0 of a pair, 2 ring 0 recorded, 3 comparing the duplicate
struct GgThread { int state, n0, n1; bool bad; DWORD ret, args[16]; u8 ans[64]; };
static __declspec(thread) GgThread t_gg;
static void __cdecl GgLeaveThunk();

// returns 2 = skip the call
static int __cdecl GgEnter(DWORD* slot, DWORD ecx) {
    Ts3nCounters& k = ts3native_counters;
    InterlockedIncrement(&k.ggRings);
    GgThread& t = t_gg;
    const u8* loop = (const u8*)slot + 0x44;
    const DWORD count = *(const DWORD*)(loop + 0x20), i = *(const DWORD*)(loop + 0x28), step = *(const DWORD*)(loop + 0x3C);
    const LONG mode = k.ggMode;
    if (count != 1 || step != 0 || i > 1) { t.state = 0; return 0; }
    if (i == 0) {
        t.state = 0;
        if (mode != 2) return 0;
        t.args[0] = ecx;
        memcpy(&t.args[1], slot + 1, 15 * 4);
        t.n0 = t.n1 = 0;
        t.bad = false;
        t.state = 1;
    } else {
        InterlockedIncrement(&k.ggDup);
        if (mode == 1) { t.state = 0; InterlockedIncrement(&k.ggSkipped); return 2; }
        if (mode != 2 || t.state != 2) { t.state = 0; return 0; }
        if (t.args[0] != ecx || memcmp(&t.args[1], slot + 1, 15 * 4) != 0) {
            if (InterlockedIncrement(&k.ggArgMismatch) <= 5) Log("goal gen D1 shadow: duplicate ring arguments differ from ring 0 (caller %08lX)", *slot);
        }
        t.state = 3;
    }
    t.ret = *slot;
    *slot = (DWORD)(uintptr_t)GgLeaveThunk;
    return 0;
}
static DWORD __cdecl GgLeave() {
    GgThread& t = t_gg;
    if (t.state == 1) t.state = 2;
    else if (t.state == 3) {
        Ts3nCounters& k = ts3native_counters;
        if (t.n0 || t.n1) {  // answers seen (worker job with GoalSplit)
            InterlockedIncrement(&k.ggValCmp);
            if ((t.bad || t.n0 != t.n1) && InterlockedIncrement(&k.ggValMismatch) <= 5)
                Log("goal gen D1 shadow: duplicate ring answered differently (%d vs %d validations%s)", t.n0, t.n1, t.bad ? ", an answer differs" : "");
        }
        t.state = 0;
    }
    return t.ret;
}
// routephases.cpp: a closed GoalPoint_Validate frame (ret = its return address, al = the answer)
static void GgVal(DWORD ret, u8 al) {
    GgThread& t = t_gg;
    if (ret != g_ggValRet) return;
    if (t.state == 1) {
        if (t.n0 < 64) t.ans[t.n0] = al;
        t.n0++;
    } else if (t.state == 3) {
        InterlockedIncrement(&ts3native_counters.ggValDup);
        if (t.n1 < t.n0 && t.n1 < 64 && t.ans[t.n1] != al) t.bad = true;
        t.n1++;
    }
}

static __declspec(naked) void GgThunk() {
    __asm {
        pushad
        pushfd
        lea eax, [esp + 36]  // the return slot
        push ecx
        push eax
        call GgEnter
        add esp, 8
        cmp eax, 2
        je skip
        popfd
        popad
        jmp dword ptr [g_ggOrig]
    skip:
        popfd
        popad
        ret 0x40
    }
}
static __declspec(naked) void __cdecl GgLeaveThunk() {
    __asm {
        push eax  // becomes the return address
        pushad
        pushfd
        call GgLeave
        mov [esp + 36], eax
        popfd
        popad
        ret
    }
}

// [GoalGen] SameRing=1 installs; Mode = ggMode's start (default 1); BytePatch=1 also writes the immediate
static void InstallGoalGen(const wchar_t* ini) {
    Ts3nCounters& k = ts3native_counters;
    if (!GetPrivateProfileIntW(L"GoalGen", L"SameRing", 0, ini)) { Log("goal gen D1 off ([GoalGen] SameRing=0)"); return; }
    if (!Matches((const void*)(uintptr_t)GG_COUNT_STORE, kGgCount, sizeof(kGgCount))) {
        Log("SKIP goal gen D1: bytes at 0x%08lX differ (not the ring count store of Steam 1.67), nothing written", GG_COUNT_STORE);
        return;
    }
    const CallSite site = {GG_RING_CALL, GG_RING, (void*)GgThunk};
    if (!PatchCalls("goal gen D1 (ring builder call)", &site, 1)) return;
    k.ggReady = 1;
    k.ggMode = Clamp(GetPrivateProfileIntW(L"GoalGen", L"Mode", 1, ini), 0, 2);
    if (GetPrivateProfileIntW(L"GoalGen", L"BytePatch", 0, ini)) {
        const u8 zero = 0;
        if (Write((void*)(uintptr_t)(GG_COUNT_STORE + 4), &zero, 1)) k.ggReady |= 2;
        else Log("FAIL goal gen D1: byte patch at 0x%08lX (err %lu)", GG_COUNT_STORE + 4, GetLastError());
    }
    Log("OK   goal gen D1: ring builder call 0x%08lX -> GgThunk (Mode=%ld: %s)%s", GG_RING_CALL, k.ggMode,
        k.ggMode == 1 ? "duplicate ring skipped" : k.ggMode == 2 ? "shadow, both rings compared" : "count only",
        k.ggReady & 2 ? ", ring count byte patched (1 -> 0)" : "");
}
