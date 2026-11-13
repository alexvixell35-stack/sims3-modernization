// TestCrash2Diag ([Crash2Diag] c2diag.cpp, docs/259 §9). Included by selftest.cpp.
// Fake destination lists in static memory (node at +4: next, prev); the stubs run with landing pads as trampolines.
static DWORD g_c2tEcx, g_c2tArg;
static __declspec(naked) void C2tLandThis() { __asm { mov g_c2tEcx, ecx } __asm { ret } }        // DestList_Clear: thiscall, no args
static __declspec(naked) void C2tLandFlags() {                                                     // deleting dtor: ret 4
    __asm {
        mov g_c2tEcx, ecx
        mov eax, [esp + 4]
        mov g_c2tArg, eax
        ret 4
    }
}
static __declspec(naked) void C2tLandCdecl() { __asm { mov eax, [esp + 4] } __asm { mov g_c2tArg, eax } __asm { ret } }

static void TestCrash2Diag() {
    static_assert(offsetof(Ts3nCounters, c2On) == 0x6258 && offsetof(Ts3nCounters, c2Spare) == 0x6274 && sizeof(Ts3nCounters) == 6323 * 4,
                  "counters v193 layout (docs/registry.md)");
    Ts3nCounters& k = ts3native_counters;
    void* const t0[4] = {g_c2ClearT, g_c2DDtorT, g_c2DDtorSaT, g_c2DestroyT};
    const int on0 = g_c2On;
    g_c2On = 1, g_c2Freq = 1000, g_c2Lines = 0, g_c2MaxLines = 200;
    k.c2Clears = k.c2ClearBad = k.c2Frees = k.c2FreeLinked = k.c2Destroys = k.c2LinesDropped = 0;
    g_c2ClearT = (void*)&C2tLandThis, g_c2DDtorT = (void*)&C2tLandFlags, g_c2DDtorSaT = (void*)&C2tLandFlags, g_c2DestroyT = (void*)&C2tLandCdecl;
    // a set (head at +0x18) with a radial destination, a foreign object (the crash's CAS vtable) and a lot destination
    static DWORD set[0x14], a[16], b[16], c[16];
    DWORD* head = set + 6;
    const DWORD H = (DWORD)(uintptr_t)head, na = (DWORD)(uintptr_t)(a + 1), nb = (DWORD)(uintptr_t)(b + 1), nc = (DWORD)(uintptr_t)(c + 1);
    a[0] = 0x0100423C, b[0] = 0x00FE46A0, c[0] = 0x0100425C;
    head[0] = na, head[1] = nc;
    a[1] = nb, a[2] = H, b[1] = nc, b[2] = na, c[1] = H, c[2] = nb;
    // linked check: each node is linked; a node its neighbours no longer point at is not
    assert(C2Linked((DWORD)(uintptr_t)a) && C2Linked((DWORD)(uintptr_t)b) && C2Linked((DWORD)(uintptr_t)c));
    static DWORD lone[4] = {0x0100423C, 0, 0, 0};
    assert(!C2Linked((DWORD)(uintptr_t)lone) && !C2Linked(0x10));
    assert(C2KnownVt(0x0100423C) && !C2KnownVt(0x00FE46A0) && C2FnStart(0) == false);
    // the dtor stub: b freed while linked (logged, counted), registers and the flag reach the trampoline
    DWORD outEcx = 0;
    __asm {
        lea ecx, b
        push 1
        call C2DDtorHook
        mov outEcx, ecx
    }
    assert(g_c2tEcx == (DWORD)(uintptr_t)b && g_c2tArg == 1 && outEcx == (DWORD)(uintptr_t)b && k.c2Frees == 1 && k.c2FreeLinked == 1);
    const C2Free& f = g_c2Ring[(DWORD)(g_c2Seq - 1) & (kC2Ring - 1)];
    assert(f.dest == (DWORD)(uintptr_t)b && f.linked == 1 && f.flags == 1 && f.vt == 0x00FE46A0 && f.seq == g_c2Seq);
    // an unlinked destination: counted in the ring, no line (slot-array stub)
    __asm {
        lea ecx, lone
        push 1
        call C2DDtorSaHook
    }
    assert(k.c2Frees == 2 && k.c2FreeLinked == 1 && g_c2Ring[(DWORD)(g_c2Seq - 1) & (kC2Ring - 1)].linked == 0);
    // DestroyRoute stub: the handle reaches the ring and the trampoline
    __asm {
        push 0x259FE9D2
        call C2DestroyHook
        add esp, 4
    }
    assert(g_c2tArg == 0x259FE9D2 && k.c2Destroys == 1 && g_c2Dr[(DWORD)(g_c2DrSeq - 1) & 63].handle == 0x259FE9D2);
    // the clear stub: one bad element (b) of three, the list itself untouched, ecx kept
    const LONG lines0 = g_c2Lines;
    __asm {
        mov ecx, head
        call C2ClearHook
    }
    assert(g_c2tEcx == H && k.c2Clears == 1 && k.c2ClearBad == 1 && head[0] == na && a[1] == nb && g_c2Lines > lines0);
    // an unreadable list counts as bad
    static DWORD badHead[2] = {0x10, 0x10};
    __asm {
        lea ecx, badHead
        call C2ClearHook
    }
    assert(k.c2Clears == 2 && k.c2ClearBad == 2);
    // line budget: past MaxLines only counted
    g_c2MaxLines = 0;
    __asm {
        mov ecx, head
        call C2ClearHook
    }
    assert(k.c2ClearBad == 3 && k.c2LinesDropped >= 1);
    // world phase
    C2WorldEvent(3);
    assert(g_c2Ev == 3 && g_c2EvQpc != 0);
    g_c2ClearT = t0[0], g_c2DDtorT = t0[1], g_c2DDtorSaT = t0[2], g_c2DestroyT = t0[3], g_c2On = on0, g_c2Ev = -1, g_c2EvQpc = 0;
    g_c2MaxLines = 200, g_c2Lines = 0;
    k.c2Clears = k.c2ClearBad = k.c2Frees = k.c2FreeLinked = k.c2Destroys = k.c2LinesDropped = 0;
    printf("crash2diag: freed-while-linked found and logged, unlinked free ring-only, bad list element caught before slot 2, unreadable list, "
           "DestroyRoute handle, line budget, stubs keep ecx and the stack: ok\n");
}
