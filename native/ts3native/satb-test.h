// TestSatbLog: [AutShadow] Satb log (satb.cpp, docs/206 §7): first-write values, partial-word merge, overflow, epoch wraps,
// and one writer (log, then store) racing one reader (load, then merge): the reader must always get the cut value.
static DWORD g_sbArr[4096];
static DWORD g_sbCut[4096];
static volatile LONG g_sbPause = 1, g_sbIdle = 1, g_sbQuit, g_sbBad, g_sbReads;
static DWORD WINAPI SatbTestReader(void*) {
    unsigned x = 12345;
    while (!g_sbQuit) {
        InterlockedExchange(&g_sbIdle, 0);  // full fence before reading g_sbPause
        if (g_sbPause) { g_sbIdle = 1; YieldProcessor(); continue; }
        x = x * 1103515245u + 12345u;
        const DWORD i = (x >> 8) & 4095;
        u8 b[8];
        const DWORD at = (DWORD)(uintptr_t)&g_sbArr[i];
        const DWORD n = i < 4095 && (x & 1) ? 8 : 4;  // 8-byte loads too (two words)
        memcpy(b, (const void*)(uintptr_t)at, n);
        _ReadWriteBarrier();
        SatbMerge(at, n, b);
        if (memcmp(b, &g_sbCut[i], n)) InterlockedIncrement(&g_sbBad);
        g_sbReads++;
    }
    return 0;
}

// docs/206 §9: EA IC byref/array arguments into shared memory are logged per argument (only the parameter's own dword is
// read: an object id or float next to it is never taken for a pointer); private locals, nulls skipped; too big = wait.
// Marshal.copy_from_unmanaged logs its destination elements.
static DWORD g_sbVec[3] = {11, 12, 13}, g_sbF = 14, g_sbVt[1], g_sbArr8[4 + 2 * 5], g_sbArr4[4 + 3], g_sbSlot, g_sbBigArr[4];
static DWORD __cdecl SatbTestEs(const void*) { return 4; }
static DWORD __cdecl SatbTestMcopy(DWORD, DWORD, DWORD, DWORD) { return 0; }
static int SatbTestRow(const char* name) {
    for (int j = 0; j < SATB_NICS; j++) if (!strcmp(kSatbIcs[j].name, name)) return j;
    assert(!"satb-ics.h row missing");
    return -1;
}
static void TestSatbIcArgs() {
    Ts3nAutShadow& k = ts3native_autshadow;
    const int mode0 = g_satbMode;
    const DWORD wtid0 = g_satbWTid;
    g_satbMode = SATB_PROBE, g_satbWTid = 0;  // probe: a wait only counts
    static DWORD noState = 0;  // no interpreter state: only the stack is private
    DWORD* const cur0 = g_curState;
    g_curState = &noState;
    const int IDX = 3;
    const short row0 = g_satbIcRow[IDX];
    SatbReset();
    const LONG lg0 = k.satbIcLogged, sh0 = k.satbIcShared, w0 = k.satbIcLogWords;
    DWORD v;
    // byref Vector3 (heap) + byref Vector3 (the caller's stack: private) + byref float; the ulong route id before them looks
    // like a pointer and must not be read (0.36.125 scanned it)
    g_satbIcRow[IDX] = (short)(1 + SatbTestRow("ScriptCore.Route::Route_GetPointAtDistanceOnRoute"));
    DWORD local[3] = {1, 2, 3};
    const DWORD a1[5] = {0x00012345, 0x00ABCDEF, (DWORD)(uintptr_t)g_sbVec, (DWORD)(uintptr_t)local, (DWORD)(uintptr_t)&g_sbF};
    SatbIcSim(IDX, a1);
    assert(k.satbIcLogged == lg0 + 2 && k.satbIcShared == sh0 && k.satbIcLogWords == w0 + 4 && g_satbN == 4);
    assert(SatbFind((DWORD)(uintptr_t)&g_sbVec[2], &v) && v == 13 && SatbFind((DWORD)(uintptr_t)&g_sbF, &v) && v == 14);
    assert(!SatbFind((DWORD)(uintptr_t)local, &v) && !SatbFind(0x00012344, &v));
    // ulong[] (element size from the table): 5 elements = 10 words; a null array is skipped
    g_satbIcRow[IDX] = (short)(1 + SatbTestRow("ScriptCore.Route::Route_SetLockKeys"));
    g_sbArr8[2] = 5;
    const DWORD a2[2] = {0, (DWORD)(uintptr_t)g_sbArr8}, a2n[2] = {0, 0};
    SatbIcSim(IDX, a2), SatbIcSim(IDX, a2n);
    assert(k.satbIcLogged == lg0 + 3 && k.satbIcLogWords == w0 + 14 && SatbFind((DWORD)(uintptr_t)&g_sbArr8[4 + 9], &v) &&
           !SatbFind((DWORD)(uintptr_t)&g_sbArr8[2], &v));
    // byref array: the slot, then the elements of the array it holds (3 x 4 bytes)
    g_satbIcRow[IDX] = (short)(1 + SatbTestRow("ScriptCore.Route::Route_InsertCustomPathAtIndex"));
    g_sbArr4[2] = 3, g_sbSlot = (DWORD)(uintptr_t)g_sbArr4;
    const DWORD a3[3] = {0x00F00000, 0x00F00001, (DWORD)(uintptr_t)&g_sbSlot};
    SatbIcSim(IDX, a3);
    assert(k.satbIcLogged == lg0 + 4 && k.satbIcLogWords == w0 + 18 && SatbFind((DWORD)(uintptr_t)&g_sbSlot, &v) &&
           SatbFind((DWORD)(uintptr_t)&g_sbArr4[6], &v));
    // an array over SATB_IC_MAXW words: not logged, a wait (counted in probe mode); the header is all that is read
    g_satbIcRow[IDX] = (short)(1 + SatbTestRow("ScriptCore.Route::Route_SetLockKeys"));
    g_sbBigArr[2] = SATB_IC_MAXW;  // x 8 bytes = 2 x SATB_IC_MAXW words
    const DWORD a4[2] = {0, (DWORD)(uintptr_t)g_sbBigArr};
    SatbIcSim(IDX, a4);
    assert(k.satbIcShared == sh0 + 1 && k.satbIcLogged == lg0 + 4 && (g_satbStale & SATB_ST_IC));
    // Marshal.copy_from_unmanaged(src, 2, dest, 3): dest elements 2..4 (element size from the class)
    const SatbEsFn es0 = g_satbElemSize;
    void* const mc0 = g_satbRt[SRT_MCOPY];
    g_satbElemSize = SatbTestEs, g_satbRt[SRT_MCOPY] = (void*)SatbTestMcopy, g_satbOn = 1;
    static DWORD dest[4 + 6];  // heap-like (static): a stack array would be the sim's private memory
    dest[0] = (DWORD)(uintptr_t)g_sbVt, dest[2] = 6;
    const LONG rt0 = k.satbRtWaits, n0 = g_satbN;
    SatbRtMcopy(0x1000, 2, (DWORD)(uintptr_t)dest, 3);
    assert(k.satbRtWaits == rt0 && g_satbN == n0 + 3 && SatbFind((DWORD)(uintptr_t)&dest[4 + 2], &v) && SatbFind((DWORD)(uintptr_t)&dest[4 + 4], &v) &&
           !SatbFind((DWORD)(uintptr_t)&dest[4 + 5], &v));
    g_satbOn = 0, g_satbElemSize = es0, g_satbRt[SRT_MCOPY] = mc0;
    g_satbIcRow[IDX] = row0, g_satbMode = mode0, g_satbWTid = wtid0, g_curState = cur0;
    SatbReset();
    printf("satb IC arguments: byref/array/byref-array ranges logged (%ld words), private and null skipped, ids next to them never read, "
           "oversized array waits, copy_from_unmanaged destination logged\n", k.satbIcLogWords - w0);
}

// docs/206 §10 (0.36.136): the two runtime writers that still waited are sized from Mono's own structures. MonoField
// .SetValueInternal: the field's range by MonoType kind (instance at obj + offset, static in the class vtable's data),
// enum = base kind, valuetype / valuetype generic instance = instance size - 4; unknown kind waits, a null object neither
// logs nor waits. PtrToStructure(intptr, object): the object's fields; the (intptr, Type) entry makes a new object: nothing.
static DWORD g_sfVtStatic[4], g_sfStatic[8], g_sfObj[16], g_sfPod[8], g_sfVtPod[1];
static u8 g_sfVClass[0xA8], g_sfEClass[0xA8], g_sfGCont[0xA8], g_sfPodClass[0x58];
static DWORD g_sfGenClass[2], g_sfGenType[2];
static DWORD __cdecl SatbTestClassOfType(DWORD type) {
    return type == (DWORD)(uintptr_t)g_sfGenType ? (DWORD)(uintptr_t)g_sfVClass : *(const DWORD*)(uintptr_t)type;
}
static DWORD __cdecl SatbTestVtable(DWORD, DWORD) { return (DWORD)(uintptr_t)g_sfVtStatic; }
static DWORD __cdecl SatbTestRt3(DWORD, DWORD, DWORD) { return 0; }
static DWORD __cdecl SatbTestRt2(DWORD, DWORD) { return 0; }
static DWORD g_sfGetRet;
static DWORD __cdecl SatbTestFget(DWORD, DWORD) { return g_sfGetRet; }
static DWORD __cdecl SatbTestNotNullable(DWORD) { return 0; }
static void TestSatbRtWriters() {
    Ts3nAutShadow& k = ts3native_autshadow;
    const int mode0 = g_satbMode;
    const DWORD wtid0 = g_satbWTid;
    DWORD* const cur0 = g_curState;
    static DWORD noState = 0;
    g_satbMode = SATB_PROBE, g_satbWTid = 0, g_curState = &noState;
    const SatbPtrFn1 cot0 = g_satbClassOfType;
    const SatbPtrFn2 vt0 = g_satbClassVtable;
    void* const rt0[3] = {g_satbRt[SRT_FSET], g_satbRt[SRT_PTRTOSTRUCT], g_satbRt[SRT_PTRTOSTRUCT2]};
    g_satbClassOfType = SatbTestClassOfType, g_satbClassVtable = SatbTestVtable;
    g_satbRt[SRT_FSET] = (void*)SatbTestRt3, g_satbRt[SRT_PTRTOSTRUCT] = (void*)SatbTestRt2, g_satbRt[SRT_PTRTOSTRUCT2] = (void*)SatbTestRt2;
    g_satbOn = 1;
    g_sfVtStatic[3] = (DWORD)(uintptr_t)g_sfStatic;                      // vtable +0xC = static data
    g_sfVClass[0x14] = 4, *(DWORD*)(g_sfVClass + 0x54) = 4 + 12;        // a 12-byte struct, layout set
    static DWORD baseU1[2] = {0, 0x05u << 16};                           // u1 MonoType
    g_sfEClass[0x14] = 4 | 0x10, *(DWORD*)(g_sfEClass + 4) = (DWORD)(uintptr_t)baseU1;  // enum : byte
    g_sfGCont[0xA6] = 0x11, g_sfGenClass[1] = (DWORD)(uintptr_t)g_sfGCont;
    g_sfGenType[0] = (DWORD)(uintptr_t)g_sfGenClass, g_sfGenType[1] = 0x15u << 16;
    static DWORD vtRf[3] = {0, 0, 0x1234};                               // MonoReflectionField's vtable: +8 domain
    struct Case { DWORD data, attrs, kind, off; int words; bool wait; };
    const Case cs[] = {
        {0, 0, 0x08, 4, 1, false},                                       // int at obj+4
        {0, 0, 0x0D, 8, 2, false},                                       // double: 2 words
        {(DWORD)(uintptr_t)g_sfVClass, 0, 0x11, 16, 3, false},           // 12-byte struct
        {(DWORD)(uintptr_t)g_sfEClass, 0, 0x11, 30, 1, false},           // enum : byte at +30: 1 byte, 1 word
        {0, 0x40000000, 0x0D, 40, 1, false},                             // byref (a pointer), whatever the kind
        {0, 0x10, 0x0A, 4, 2, false},                                    // static long: the vtable's data
        {0, 0, 0x13, 4, 0, true},                                        // VAR: the game's set_value refuses it: wait
    };
    for (int i = 0; i < (int)(sizeof(cs) / sizeof(cs[0])); i++) {
        SatbReset();
        static DWORD ty[2], cf[6], rf[3];
        ty[0] = cs[i].data, ty[1] = cs[i].attrs | (cs[i].kind << 16);
        cf[0] = (DWORD)(uintptr_t)ty, cf[2] = cs[i].off, cf[5] = 0x5000;
        rf[0] = (DWORD)(uintptr_t)vtRf, rf[2] = (DWORD)(uintptr_t)cf;
        const LONG f0 = k.satbRtFset, w0 = k.satbRtWaits;
        SatbRtFieldSet((DWORD)(uintptr_t)rf, (DWORD)(uintptr_t)g_sfObj, 0);
        const u8* base = (cs[i].attrs & 0x10) ? (const u8*)g_sfStatic : (const u8*)g_sfObj;
        DWORD v;
        assert(k.satbRtFset == f0 + 1 && (k.satbRtWaits - w0 == (cs[i].wait ? 1 : 0)) && g_satbN == (LONG)cs[i].words);
        if (cs[i].words) assert(SatbFind((DWORD)(uintptr_t)(base + (cs[i].off & ~3u)), &v));
    }
    {   // valuetype generic instance: the value size of its class; null instance object: nothing, no wait
        SatbReset();
        static DWORD cf[6], rf[3];
        cf[0] = (DWORD)(uintptr_t)g_sfGenType, cf[2] = 4;
        rf[0] = (DWORD)(uintptr_t)vtRf, rf[2] = (DWORD)(uintptr_t)cf;
        const LONG w0 = k.satbRtWaits;
        SatbRtFieldSet((DWORD)(uintptr_t)rf, (DWORD)(uintptr_t)g_sfObj, 0);
        assert(g_satbN == 3);
        SatbReset();
        SatbRtFieldSet((DWORD)(uintptr_t)rf, 0, 0);
        assert(g_satbN == 0 && k.satbRtWaits == w0);
    }
    {   // PtrToStructure(ptr, existing object of a 4 + 20-byte class): 5 words from obj + 4; the Type entry: nothing
        SatbReset();
        *(DWORD*)(g_sfPodClass + 0x54) = 4 + 20;
        g_sfVtPod[0] = (DWORD)(uintptr_t)g_sfPodClass, g_sfPod[0] = (DWORD)(uintptr_t)g_sfVtPod;
        const LONG p0 = k.satbRtPtrStruct, w0 = k.satbRtWaits;
        SatbRtPtrToStruct(0x1000, (DWORD)(uintptr_t)g_sfPod);
        DWORD v;
        assert(k.satbRtPtrStruct == p0 + 1 && k.satbRtWaits == w0 && g_satbN == 5 && SatbFind((DWORD)(uintptr_t)&g_sfPod[1], &v) &&
               SatbFind((DWORD)(uintptr_t)&g_sfPod[5], &v) && !SatbFind((DWORD)(uintptr_t)&g_sfPod[0], &v) && !SatbFind((DWORD)(uintptr_t)&g_sfPod[6], &v));
        SatbRtPtrToStruct2(0x1000, 0x2000);
        assert(k.satbRtPtrStruct == p0 + 1 && g_satbN == 5 && k.satbRtWaits == w0);
    }
    {   // docs/206 §12: MonoField.GetValue on the worker (act) answers the cut: a reference field from the log, a value field's
        // box patched from the log; a field not written since the cut is the call's own result, no give-up
        void* const fget0 = g_satbRt[SRT_FGET];
        const SatbPtrFn1 nul0 = g_satbIsNullable;
        g_satbRt[SRT_FGET] = (void*)SatbTestFget, g_satbIsNullable = SatbTestNotNullable;
        static DWORD ty[2], cf[6], rf[3], box[4];
        rf[0] = (DWORD)(uintptr_t)vtRf, rf[2] = (DWORD)(uintptr_t)cf, cf[0] = (DWORD)(uintptr_t)ty;
        g_satbMode = SATB_ACT, g_satbWTid = GetCurrentThreadId();
        const LONG give0 = k.satbWGiveUp;
        // reference (class) field at +8: the sim logged it, then wrote a new pointer
        SatbReset();
        ty[0] = 0, ty[1] = 0x12u << 16, cf[2] = 8;
        g_sfObj[2] = 0x11110000;
        assert(SatbLogWord((DWORD)(uintptr_t)&g_sfObj[2]));
        g_sfObj[2] = 0x22220000, g_sfGetRet = 0x22220000;
        assert(SatbRtFieldGet((DWORD)(uintptr_t)rf, (DWORD)(uintptr_t)g_sfObj) == 0x11110000);
        // double field at +16: the box (header + 8 bytes) gets the cut bytes
        ty[1] = 0x0Du << 16, cf[2] = 16;
        g_sfObj[4] = 1, g_sfObj[5] = 2;
        assert(SatbLogWord((DWORD)(uintptr_t)&g_sfObj[4]) && SatbLogWord((DWORD)(uintptr_t)&g_sfObj[5]));
        g_sfObj[4] = 7, g_sfObj[5] = 8, box[1] = 7, box[2] = 8, g_sfGetRet = (DWORD)(uintptr_t)box;
        assert(SatbRtFieldGet((DWORD)(uintptr_t)rf, (DWORD)(uintptr_t)g_sfObj) == (DWORD)(uintptr_t)box && box[1] == 1 && box[2] == 2);
        // a field nobody wrote: the live answer as is
        ty[1] = 0x08u << 16, cf[2] = 24, g_sfGetRet = 0x33330000;
        assert(SatbRtFieldGet((DWORD)(uintptr_t)rf, (DWORD)(uintptr_t)g_sfObj) == 0x33330000 && k.satbWGiveUp == give0);
        g_satbRt[SRT_FGET] = fget0, g_satbIsNullable = nul0;
    }
    g_satbOn = 0, g_satbClassOfType = cot0, g_satbClassVtable = vt0;
    g_satbRt[SRT_FSET] = rt0[0], g_satbRt[SRT_PTRTOSTRUCT] = rt0[1], g_satbRt[SRT_PTRTOSTRUCT2] = rt0[2];
    g_satbMode = mode0, g_satbWTid = wtid0, g_curState = cur0;
    SatbReset();
    printf("satb runtime writers: MonoField.SetValue sized by kind (int, double, struct, enum, byref, static, generic struct), "
           "unknown kind waits, null object skipped; PtrToStructure(object) fields logged, new-object entry not\n");
}

// docs/206 §11: the per-opcode load thunk runs the handler on any thread but the worker's (counting it), the worker path on
// the worker's; edx (the opcode) reaches both untouched. Timed against the shared stub's old sequence.
static __declspec(naked) int SatbTestHandler() { __asm { lea eax, [edx + 1000] } __asm { ret } }
static __declspec(naked) int SatbTestWorkerPath() { __asm { lea eax, [edx + 2000] } __asm { ret } }
static SatbLine g_stTid, g_stCount;
static volatile DWORD g_stOldTid, g_stOldCount;
static void* g_stOldTarget = (void*)SatbTestHandler;
static __declspec(naked) int SatbTestOldStub() {  // 0.36.135's sim path: push/fs/cmp/pop, counter, shared indirect jump
    __asm {
        push eax
        mov eax, dword ptr fs:[0x24]
        cmp eax, dword ptr [g_stOldTid]
        pop eax
        je w
        add dword ptr [g_stOldCount], 1
        jmp dword ptr [g_stOldTarget]
    w:
        jmp SatbTestWorkerPath
    }
}
static void TestSatbLoadThunk() {
    typedef int(__cdecl * Fn)();
    const DWORD op = 0x41;
    Fn f = (Fn)SatbMakeLoadThunk(op, (void*)SatbTestHandler, (void*)SatbTestWorkerPath, &g_stTid.v, &g_stCount.v);
    assert(f);
    int r = 0;
    g_stTid.v = GetCurrentThreadId() + 1, g_stCount.v = 0;
    __asm { mov edx, 7 }
    __asm { call f }
    __asm { mov r, eax }
    assert(r == 1007 && g_stCount.v == 1);  // sim path: handler, counted
    g_stTid.v = GetCurrentThreadId();
    __asm { mov edx, 9 }
    __asm { call f }
    __asm { mov r, eax }
    assert(r == 2009 && g_stCount.v == 1);  // worker path, not counted
    // cost of the sim path, thunk vs the old shared stub (best of 20 x 1M calls); both include the call/ret of the test
    g_stTid.v = g_stOldTid = GetCurrentThreadId() + 1;
    Fn old = (Fn)SatbTestOldStub;
    double best[2] = {1e30, 1e30};
    LARGE_INTEGER fq, a, b;
    QueryPerformanceFrequency(&fq);
    for (int rep = 0; rep < 20; rep++)
        for (int which = 0; which < 2; which++) {
            Fn g = which ? old : f;
            QueryPerformanceCounter(&a);
            for (int i = 0; i < 1000000; i++) g();
            QueryPerformanceCounter(&b);
            const double ns = (double)(b.QuadPart - a.QuadPart) * 1e9 / (double)fq.QuadPart / 1e6;
            if (ns < best[which]) best[which] = ns;
        }
    printf("satb load thunk: sim path -> handler (counted), worker path -> worker; %.2f ns a call (old shared stub %.2f ns, incl. call/ret)\n",
           best[0], best[1]);
}

// docs/206 §13: the worker's arena chunk list counts distinct chunks: the same pool chunk taken again by a later grow
// (deep call paths) is one entry, its widest end kept; a word in any listed chunk is the worker's own frame.
static void TestSatbArenaChunks() {
    const LONG n0 = g_satbWArenaN;
    g_satbWArenaN = 0;
    static DWORD c[4][64];
    for (int round = 0; round < 500; round++) {
        const int i = round % 3;
        SatbWArenaAdd((DWORD)(uintptr_t)c[i], (DWORD)(uintptr_t)&c[i][round % 2 ? 64 : 32]);
    }
    assert(g_satbWArenaN == 3);  // 0.36.139 counted 500 entries and gave up past 64
    assert(SatbWInArena((DWORD)(uintptr_t)&c[0][60]) && SatbWInArena((DWORD)(uintptr_t)&c[2][0]) && !SatbWInArena((DWORD)(uintptr_t)&c[3][0]));
    for (int i = 0; i < 200; i++) SatbWArenaAdd(0x40000000u + (DWORD)i * 0x1000, 0x40000800u + (DWORD)i * 0x1000);
    assert(g_satbWArenaN == 203 && SatbWInArena(0x400C7100) && !SatbWInArena(0x400C7900));
    g_satbWArenaN = n0;
    printf("satb arena chunks: 500 grows over 3 chunks = 3 entries, 200 more distinct = 203 (cap %ld), ranges kept\n", SATB_WARENA);
}

// docs/127 H11 shelter answer: an armed worker callvirt (receiver + site MonoMethod) is not made: receiver and 5 arguments
// popped, null pushed in the receiver's slot, ip past the 4-word callvirt (mirror too), armed once. Not armed, another site
// or another receiver: the normal path (a null receiver here, so it stops at the handler's null path). RainArm: worker
// only, act span only, disarm answers the skips and counts an armed call that never came.
static void TestSatbRainSkip() {
    const int mode0 = g_satbMode;
    const LONG on0 = g_satbOn;
    const DWORD wtid0 = g_asWorkerTid;
    Ts3nAutShadow& k = ts3native_autshadow;
    const LONG arm0 = k.rainArm, skip0 = k.rainSkip, miss0 = k.rainMiss;
    static DWORD obj[2], cls[1], mm[1], other[1];
    static u8 rm[0x48], rmFrame[0x48];
    static DWORD items[4], frame[8];
    static WORD ip[8];
    static DWORD stack[2 * 8];
    obj[0] = (DWORD)(uintptr_t)cls;
    *(DWORD*)(rm + 8) = (DWORD)(uintptr_t)mm, rm[0x40] = 5;
    items[3] = (DWORD)(uintptr_t)rm;
    *(DWORD*)(rmFrame + 0x14) = (DWORD)(uintptr_t)items;
    const DWORD ebp = (DWORD)(uintptr_t)&frame[6];
    frame[6 - 4] = (DWORD)(uintptr_t)rmFrame;  // [ebp-0x10]
    ip[0] = 0xEC, ip[1] = 0, ip[3] = 3, ip[4] = 0x1C1;
    g_satbMode = SATB_ACT, g_satbOn = 1;
    DWORD r[8];
    // round 0: armed, this site: skipped; 1: same again (stays armed, counted twice); 2: another receiver, same site: skipped
    // too (by site); 3: another site with a null receiver: not skipped (the handler's null path); 4: disarmed: not skipped
    for (int round = 0; round < 5; round++) {
        for (int i = 0; i < 16; i++) stack[i] = 0xA0 + i;
        stack[2] = round >= 3 ? 0 : round == 2 ? (DWORD)(uintptr_t)other : (DWORD)(uintptr_t)obj;  // receiver slot (slot 1)
        memset(r, 0, sizeof(r));
        r[SR_EDI] = (DWORD)(uintptr_t)ip, r[SR_ESI] = (DWORD)(uintptr_t)&stack[2 * 7], r[SR_EBP] = ebp, r[SR_EDX] = 0xEC;
        if (round == 0) g_asRainDef = (DWORD)(uintptr_t)obj, g_asRainSkips = 0;
        g_asRainMm = (DWORD)(uintptr_t)(round == 3 ? other : mm);
        if (round == 4) g_asRainDef = 0;
        const LONG s0 = g_asRainSkips;
        if (round <= 2) {
            assert(SatbWorkerLoad(r) == 1);
            assert(r[SR_ESI] == (DWORD)(uintptr_t)&stack[4] && stack[2] == 0 && stack[3] == 0 && stack[0] == 0xA0);
            assert(r[SR_EDI] == (DWORD)(uintptr_t)&ip[4] && frame[6 - 1] == r[SR_EDI]);
            assert(g_asRainDef == (DWORD)(uintptr_t)obj && g_asRainSkips == s0 + 1);
        } else {
            assert(SatbWorkerLoad(r) == 0 && g_asRainSkips == s0);
        }
    }
    g_asRainDef = 0, g_asRainSkips = 0;
    // RainArm
    assert(AsIcRainArm(obj, mm) == -1);  // not the worker
    g_asWorkerTid = GetCurrentThreadId();
    assert(AsIcRainArm(obj, mm) == 1 && g_asRainDef == (DWORD)(uintptr_t)obj && k.rainArm == arm0 + 1);
    g_asRainSkips = 1;  // as SatbRainSkip leaves it (still armed)
    assert(AsIcRainArm(NULL, NULL) == 1 && g_asRainSkips == 0 && k.rainMiss == miss0);
    assert(AsIcRainArm(obj, mm) == 1 && AsIcRainArm(NULL, NULL) == 0 && k.rainMiss == miss0 + 1 && g_asRainDef == 0);
    g_satbOn = 0;
    assert(AsIcRainArm(obj, mm) == 0 && g_asRainDef == 0);  // no act span: not armed
    g_satbOn = 1, g_satbMode = SATB_PROBE;
    assert(AsIcRainArm(obj, mm) == 0 && AsIcConfig(32) == 0);
    g_satbMode = SATB_ACT;
    assert(AsIcConfig(32) == 1 && k.rainSkip == skip0 + 3);
    g_asWorkerTid = wtid0, g_satbMode = mode0, g_satbOn = on0;
    {  // per-search feature mask: names, the crash-log mark (MonoString copied, ASCII), Config(33)
        char t[128];
        assert(AsFeatText(0x109, t, sizeof(t)) > 0 && !strcmp(t, "late|rain|partial"));
        assert(AsFeatText(0, t, sizeof(t)) == 0 && !t[0]);
        static DWORD str[2 + 2];
        str[1] = 3;
        WORD* w = (WORD*)&str[2];
        w[0] = 'A', w[1] = 'b', w[2] = 0x263A;
        const LONG r0 = g_asMarkReq, m0 = g_asMarkMask, lb0 = g_asLastJobBits;
        AsIcSearchMark(7, 0x109, str);
        assert(g_asMarkReq == 7 && g_asMarkMask == 0x109 && !strcmp(g_asMarkDef, "Ab?"));
        g_asLastJobBits = AS_FEAT_SATB | AS_FEAT_COLD;
        assert(AsIcConfig(33) == 12);
        g_asMarkReq = r0, g_asMarkMask = m0, g_asLastJobBits = lb0, g_asMarkDef[0] = 0;
    }
    printf("satb rain skip: armed site popped (receiver + 5 args), null pushed, ip +8, any receiver, counted per call; other site or disarmed not; RainArm worker+act only, miss counted\n");
}

// docs/206 §15: native writers. Sim: PriorityQueue bodies log the object's fields and its items array, Rehash the fields,
// ElapsedTime*(ref float) the float; the tick statics are logged at the cut (fake host). Worker: Curve.Fx's m_x store is
// taken back, a body on a private ref runs, a body on an unchanged `this` runs without a give-up.
static DWORD g_snKlass[0x18], g_snArrKlass[0x18], g_snObj[8], g_snArr[4 + 8], g_snRm[12], g_snFrame[10], g_snArgs[2];
static DWORD g_snFloat, g_snTick[2][2], g_snHost[0xC30 / 4];
static LONG g_snCalls;
static void __cdecl SatbTestBody(DWORD frame, DWORD) {
    g_snCalls++;
    const DWORD self = *(const DWORD*)(uintptr_t)(frame + 0xC);
    if (self) *(DWORD*)(uintptr_t)(self + 0x14) = 0x7777;  // Fx-like: caches its argument in m_x
}
static void TestSatbNativeWriters() {
    const int mode0 = g_satbMode;
    const DWORD wtid0 = g_satbWTid;
    DWORD* const cur0 = g_curState;
    static DWORD noState = 0;
    void* const next0 = g_satbBindNext;
    g_satbMode = SATB_ACT, g_satbWTid = 0, g_curState = &noState, g_satbOn = 1;
    g_satbBindNext = (void*)SatbTestBody;
    *(DWORD*)((u8*)g_snKlass + 0x54) = 4 + 12;                              // object: 3 fields
    ((u8*)g_snArrKlass)[0x10] = 1, *(DWORD*)((u8*)g_snArrKlass + 0x5C) = 16;  // QueueItem[]: rank 1, 16-byte elements
    static DWORD vtObj[1], vtArr[1];
    vtObj[0] = (DWORD)(uintptr_t)g_snKlass, vtArr[0] = (DWORD)(uintptr_t)g_snArrKlass;
    g_snObj[0] = (DWORD)(uintptr_t)vtObj, g_snObj[1] = (DWORD)(uintptr_t)g_snArr, g_snObj[2] = 2, g_snObj[3] = 1;
    g_snArr[0] = (DWORD)(uintptr_t)vtArr, g_snArr[2] = 2;                   // max_length 2: 32 bytes of elements
    g_snFrame[1] = (DWORD)(uintptr_t)g_snRm, g_snFrame[3] = (DWORD)(uintptr_t)g_snObj, g_snFrame[5] = (DWORD)(uintptr_t)g_snArgs;
    DWORD v;
    // sim: PriorityQueue.Add (matched on +0x28): 3 field words + 8 element words
    SatbReset();
    g_snRm[10] = kSatbBodies[SB_PQ_ADD];
    SatbBindCall((DWORD)(uintptr_t)g_snFrame, 0);
    assert(g_snCalls == 1 && g_satbN == 3 + 8 && SatbFind((DWORD)(uintptr_t)&g_snObj[1], &v) && SatbFind((DWORD)(uintptr_t)&g_snArr[4 + 7], &v));
    // sim: Hashtable.Rehash (matched on +0x24): fields only
    SatbReset();
    g_snRm[10] = 0, g_snRm[9] = kSatbBodies[SB_HT_REHASH];
    SatbBindCall((DWORD)(uintptr_t)g_snFrame, 0);
    assert(g_snCalls == 2 && g_satbN == 3);
    // sim: ElapsedTimeInMinutes(ref float): the float
    SatbReset();
    g_snRm[9] = kSatbBodies[SB_ELAPSED_MIN], g_snArgs[0] = (DWORD)(uintptr_t)&g_snFloat, g_snFrame[3] = 0;
    SatbBindCall((DWORD)(uintptr_t)g_snFrame, 0);
    assert(g_snCalls == 3 && g_satbN == 1 && SatbFind((DWORD)(uintptr_t)&g_snFloat, &v));
    // the tick statics at the cut (fake host +0xC20 / +0xC24)
    SatbReset();
    const DWORD host0 = g_mtHostThis;
    g_snHost[0xC20 / 4] = (DWORD)(uintptr_t)g_snTick[0], g_snHost[0xC24 / 4] = (DWORD)(uintptr_t)g_snTick[1];
    g_mtHostThis = (DWORD)(uintptr_t)g_snHost;
    g_snTick[0][0] = 100, g_snTick[1][0] = 100;
    SatbLogTicks();
    g_snTick[0][0] = 101, g_snTick[1][0] = 101;  // the sim's raw store at the next Simulate
    assert(g_satbN == 4 && SatbFind((DWORD)(uintptr_t)&g_snTick[1][0], &v) && v == 100);
    g_mtHostThis = host0;
    // worker: Curve.Fx's m_x store taken back; a private ref runs the body; an unchanged `this` runs, no give-up
    SatbReset();
    g_satbWTid = GetCurrentThreadId();
    const LONG give0 = ts3native_autshadow.satbWGiveUp;
    g_snRm[9] = kSatbBodies[SB_CURVE_FX], g_snFrame[3] = (DWORD)(uintptr_t)g_snObj, g_snObj[5] = 0x1234;
    SatbBindCall((DWORD)(uintptr_t)g_snFrame, 0);
    assert(g_snCalls == 4 && g_snObj[5] == 0x1234);
    DWORD local = 0;
    g_snRm[9] = kSatbBodies[SB_ELAPSED_HRS], g_snArgs[0] = (DWORD)(uintptr_t)&local, g_snFrame[3] = 0;
    SatbBindCall((DWORD)(uintptr_t)g_snFrame, 0);
    assert(g_snCalls == 5);
    g_snRm[9] = 0x1000, g_snFrame[3] = (DWORD)(uintptr_t)g_snObj;  // any other body on an object nobody logged
    SatbBindCall((DWORD)(uintptr_t)g_snFrame, 0);
    assert(g_snCalls == 6 && ts3native_autshadow.satbWGiveUp == give0 && g_snObj[5] == 0x7777);
    // docs/206 §16: address-of a static struct the sim changed since the cut: the worker gets a private copy with the cut
    // bytes (no give-up); an unchanged static keeps its own address
    {
        static DWORD stat[4] = {1, 2, 3, 4}, quiet[2] = {5, 6};
        g_satbWStaticAt = 0;
        assert(SatbLogWord((DWORD)(uintptr_t)&stat[1]) && SatbLogWord((DWORD)(uintptr_t)&stat[2]));
        stat[1] = 20, stat[2] = 30;  // the sim's stores after the cut
        const DWORD* sv = (const DWORD*)(uintptr_t)SatbWStaticView((DWORD)(uintptr_t)stat, 16);
        assert(sv && sv != stat && sv[0] == 1 && sv[1] == 2 && sv[2] == 3 && sv[3] == 4 && stat[1] == 20);
        assert(SatbWStaticView((DWORD)(uintptr_t)quiet, 8) == (DWORD)(uintptr_t)quiet && ts3native_autshadow.satbWGiveUp == give0);
        g_satbWStaticAt = 0;
    }
    g_satbBindNext = next0, g_satbOn = 0;
    g_satbMode = mode0, g_satbWTid = wtid0, g_curState = cur0;
    SatbReset();
    printf("satb native writers: PriorityQueue fields + items, Rehash fields, ElapsedTime ref float, tick statics at the cut "
           "logged (sim); Curve.Fx m_x taken back, private ref and unchanged this run (worker)\n");
}

static void TestSatbLog() {
    assert(SatbAlloc());
    // single thread: the first write's value is kept, bytes of a partly covered word are merged
    for (int i = 0; i < 4096; i++) g_sbArr[i] = 0x1000 + i;
    SatbReset();
    const DWORD a0 = (DWORD)(uintptr_t)&g_sbArr[10];
    assert(SatbLogWord(a0) && g_satbN == 1);
    g_sbArr[10] = 7;
    assert(SatbLogWord(a0) && g_satbN == 1);  // second write: already logged
    g_sbArr[10] = 8;
    DWORD v = 0;
    assert(SatbFind(a0, &v) && v == 0x1000 + 10);
    u8 b[6];
    memcpy(b, (const u8*)&g_sbArr[10] + 2, 6);  // bytes 2..7: half of word 10 (logged), half of word 11 (not)
    assert(SatbMerge(a0 + 2, 6, b) == 1);
    const DWORD w10 = 0x1000 + 10, w11 = 0x1000 + 11;
    assert(!memcmp(b, (const u8*)&w10 + 2, 2) && !memcmp(b + 2, &w11, 4));
    // new job: the old entry is gone without a clear
    SatbReset();
    assert(!SatbFind(a0, &v) && g_satbN == 0);
    // overflow: SATB_CAP entries, then refused
    static DWORD big[SATB_CAP + 8];
    for (DWORD i = 0; i < SATB_CAP; i++) assert(SatbLogWord((DWORD)(uintptr_t)&big[i]));
    assert(!SatbLogWord((DWORD)(uintptr_t)&big[SATB_CAP]));
    DWORD vv;
    assert(SatbFind((DWORD)(uintptr_t)&big[SATB_CAP - 1], &vv) && !SatbFind((DWORD)(uintptr_t)&big[SATB_CAP], &vv));
    // epoch wraps: 8-bit after 255 jobs, 16-bit after 65535; an entry of an old job is never found
    for (int j = 0; j < 70000; j++) {
        SatbReset();
        if (j % 997 == 0 || g_satbE8 == 1 || g_satbE16 == 1) {
            assert(!SatbFind(a0, &v));
            assert(SatbLogWord(a0) && SatbFind(a0, &v));
        }
    }
    // writer + reader: 200 jobs, the writer logs then stores random values; the reader always sees the cut value
    g_sbBad = 0, g_sbReads = 0, g_sbQuit = 0;
    HANDLE th = CreateThread(NULL, 0, SatbTestReader, NULL, 0, NULL);
    unsigned x = 777;
    LONG stores = 0;
    for (int job = 0; job < 200; job++) {
        InterlockedExchange(&g_sbPause, 1);  // no reader during a reset, as in the game (the worker is idle at a cut)
        while (!g_sbIdle) YieldProcessor();
        SatbReset();
        memcpy(g_sbCut, g_sbArr, sizeof(g_sbArr));
        InterlockedExchange(&g_sbPause, 0);
        for (int k = 0; k < 20000; k++) {
            x = x * 1664525u + 1013904223u;
            const DWORD i = (x >> 10) & 4095;
            if (!SatbLogWord((DWORD)(uintptr_t)&g_sbArr[i])) break;
            _ReadWriteBarrier();
            g_sbArr[i] = x;
            stores++;
        }
    }
    g_sbQuit = 1;
    WaitForSingleObject(th, INFINITE);
    CloseHandle(th);
    printf("satb log: first values kept, partial words merged, overflow at %lu, epoch wraps clean; writer/reader %ld stores, %ld reads, %ld wrong\n",
           SATB_CAP, stores, g_sbReads, g_sbBad);
    assert(g_sbBad == 0 && g_sbReads > 1000);
    TestSatbIcArgs();
    TestSatbRtWriters();
    TestSatbLoadThunk();
    TestSatbArenaChunks();
    TestSatbNativeWriters();
    TestSatbRainSkip();
}
