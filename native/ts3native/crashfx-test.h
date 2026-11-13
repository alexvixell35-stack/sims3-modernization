// [CrashForensics] (crashfx.h / crashfx.cpp, docs/157 §11.7): selftest. Included by selftest.cpp. Every guarded path runs on
// dummies (probe safety rule): ring wraparound, address-in-block lookup, the heap check on a fake EA chunk pair, the map erase
// guard on a dummy node whose object has vtable 0 (and on one with a good vtable), the array clear guard on a decommitted
// array, the hand-built 0x7B0A00 trampoline's encoding, and the crash dump on a fake access violation.
static int g_cfFakeErase, g_cfFakeClr;
static DWORD* __fastcall CfFakeErase(void*, void*, DWORD* it, u8*, DWORD*) { g_cfFakeErase++; return it; }
static void __fastcall CfFakeClr(u8*, void*) { g_cfFakeClr++; }

static void TestCrashForensics() {
    static_assert(offsetof(Ts3nCounters, cfReady) == 0x4770 && offsetof(Ts3nCounters, cfCalls) == 0x4778 && offsetof(Ts3nCounters, cfBad) == 0x4780 &&
                      offsetof(Ts3nCounters, cfRingHit) == 0x4788 && offsetof(Ts3nCounters, cfHeapBad) == 0x478C &&
                      offsetof(Ts3nCounters, cfDumps) == 0x4790,
                  "counters v114 layout after v113 (docs/registry.md)");
    static_assert(offsetof(Ts3nCounters, cdReady) == 0x4794 && offsetof(Ts3nCounters, cdBad) == 0x4798 &&
                      offsetof(Ts3nCounters, ceRecs) == 0x47A8 && sizeof(Ts3nCounters) == 6379 * 4,
                  "counters v115 layout after v114 (docs/registry.md)");
    Ts3nCounters& k = ts3native_counters;
    assert(k.version == 912);
    // off: nothing recorded
    const LONG s0 = g_cfSeq;
    CfRec(CF_COPY_NEW, (void*)0x1000, 64);
    CfFree(CF_COPY_FREE, (void*)0x1000);
    CfUse(true);
    assert(g_cfSeq == s0 && !g_cfUseTid);
    g_cfOn = 1;
    LARGE_INTEGER f;
    QueryPerformanceFrequency(&f);
    g_cfQpf = f.QuadPart;

    // ring wraparound: 4096 + 100 events, the first 100 are gone, the newest 4096 are found
    const LONG base = g_cfSeq;
    for (DWORD i = 0; i < kCfRing + 100; i++) CfRec(CF_LEAF_NEW, (void*)(uintptr_t)(0x10000000 + i * 32), 32);
    assert(g_cfSeq == base + (LONG)kCfRing + 100 && k.cfRecs == g_cfSeq);
    CfEnt e;
    assert(!CfFind(0x10000000 + 50 * 32, &e));                            // overwritten
    assert(CfFind(0x10000000 + 100 * 32 + 8, &e) && e.addr == 0x10000000 + 100 * 32 && e.tag == CF_LEAF_NEW);  // oldest kept
    assert(CfFind(0x10000000 + (kCfRing + 99) * 32 + 31, &e) && e.seq == g_cfSeq && e.tid == GetCurrentThreadId());

    // address in block: newest entry wins (a free after the new), end exclusive, 16-byte minimum for unknown sizes
    static u8 blk[256];
    CfRec(CF_VEC_NEW, blk, 100);
    assert(CfFind((DWORD)(uintptr_t)(blk + 99), &e) && e.tag == CF_VEC_NEW && e.size == 100);
    assert(!CfFind((DWORD)(uintptr_t)(blk + 100), &e));
    CfRec(CF_ROOM_FREE, blk, 0);
    assert(CfFind((DWORD)(uintptr_t)(blk + 15), &e) && e.tag == CF_ROOM_FREE && CfIsFree(e.tag));
    assert(CfFind((DWORD)(uintptr_t)(blk + 50), &e) && e.tag == CF_VEC_NEW);  // beyond the free's 16 bytes: the older new
    char d[200];
    CfDescribe((DWORD)(uintptr_t)(blk + 4), d, sizeof(d));
    assert(strstr(d, "FREED by room-free") && strstr(d, "+0x4"));
    CfDescribe(0x7FFF0000, d, sizeof(d));
    assert(strstr(d, "not in the ring"));

    // g_rpUse holder
    CfUse(true);
    assert(g_cfUseTid == GetCurrentThreadId() && CfFind(0, &e) == false);
    CfUse(false);
    assert(!g_cfUseTid);

    // heap check on a fake chunk pair (EA layout, alloc.cpp): chunk at c, user pointer c + 0x10, size 0x40, next chunk's size
    // word at c + 0x44 holds "previous in use" in bit 0
    static DWORD heap[64];
    u8* c = (u8*)heap;
    heap[1] = 0x40 | 1, heap[0x44 / 4] = 0x20 | 1;
    g_cfHeap = 1;
    const LONG hb0 = k.cfHeapBad;
    CfFree(CF_COPY_FREE, c + 0x10);
    assert(k.cfHeapBad == hb0 && CfFind((DWORD)(uintptr_t)(c + 0x10 + 0x33), &e) && e.size == 0x40 - 0xC && e.tag == CF_COPY_FREE);
    heap[0x44 / 4] = 0x20;  // the chunk is free now: a second free is a double free
    CfFree(CF_COPY_FREE, c + 0x10);
    assert(k.cfHeapBad == hb0 + 1);
    heap[1] = 0x40 | 1 | 4;  // fastbin / debug-tagged: not checked, size unknown
    CfFree(CF_HASH_FREE, c + 0x10);
    assert(k.cfHeapBad == hb0 + 1 && CfFind((DWORD)(uintptr_t)(c + 0x10), &e) && e.size == 0);
    g_cfHeap = 0;

    // map erase guard on dummies: node +0x10 = object; vtable 0 = bad (logged, ring hit when the object is a freed ring block),
    // vtable in the .rdata range = fine; the game's erase (here a fake) runs either way, with the same arguments
    static DWORD obj[4], node[8], vtab[4];
    g_cfVtLo = (DWORD)(uintptr_t)vtab, g_cfVtHi = g_cfVtLo + sizeof(vtab);
    g_cfEraseOrig = CfFakeErase;
    node[4] = (DWORD)(uintptr_t)obj;
    obj[0] = (DWORD)(uintptr_t)vtab;
    DWORD it[2] = {};
    const LONG b0 = k.cfBad[0], c0 = k.cfCalls[0], rh0 = k.cfRingHit;
    assert(CfEraseHook((void*)0x1234, NULL, it, (u8*)node, NULL) == it && g_cfFakeErase == 1 && k.cfBad[0] == b0 && k.cfCalls[0] == c0 + 1);
    obj[0] = 0;
    CfRec(CF_COPY_FREE, obj, 16);
    CfEraseHook((void*)0x1234, NULL, it, (u8*)node, NULL);
    assert(g_cfFakeErase == 2 && k.cfBad[0] == b0 + 1 && k.cfRingHit == rh0 + 1);
    node[4] = 0;  // empty slot: the game skips the release, so do we
    CfEraseHook((void*)0x1234, NULL, it, (u8*)node, NULL);
    assert(g_cfFakeErase == 3 && k.cfBad[0] == b0 + 1);
    obj[0] = 0x123;  // not 4-aligned / outside .rdata
    node[4] = (DWORD)(uintptr_t)obj;
    CfEraseHook((void*)0x1234, NULL, it, (u8*)node, NULL);
    assert(k.cfBad[0] == b0 + 2);

    // array clear guard: a readable array passes; an array on a decommitted page is caught before the (fake) game code
    g_cfClrOrig = CfFakeClr;
    static u8 arr[0x60 * 3];
    static DWORD self[16];
    self[0x30 / 4] = (DWORD)(uintptr_t)arr, self[0x2C / 4] = 3;
    const LONG b1 = k.cfBad[1];
    CfClrHook((u8*)self, NULL);
    assert(g_cfFakeClr == 1 && k.cfBad[1] == b1);
    u8* pg = (u8*)VirtualAlloc(NULL, 0x10000, MEM_RESERVE, PAGE_NOACCESS);
    assert(pg);
    self[0x30 / 4] = (DWORD)(uintptr_t)pg;
    CfClrHook((u8*)self, NULL);
    assert(g_cfFakeClr == 2 && k.cfBad[1] == b1 + 1);
    self[0x30 / 4] = 0;  // no array: the game's je, no check
    CfClrHook((u8*)self, NULL);
    assert(g_cfFakeClr == 3 && k.cfBad[1] == b1 + 1);

    // 0x7B0A00 trampoline: cmp [ecx+30h],0 ; je 0x7B0A24 (rel32) ; jmp 0x7B0A06
    const u8* t = (const u8*)CfClrTramp();
    assert(t && !memcmp(t, kCfClrPro, 4) && t[4] == 0x0F && t[5] == 0x84 && t[10] == 0xE9);
    assert((DWORD)(uintptr_t)t + 10 + *(const LONG*)(t + 6) == 0x007B0A24 && (DWORD)(uintptr_t)t + 15 + *(const LONG*)(t + 11) == 0x007B0A06);  // a pool slot (TrampAlloc): never freed

    // crash dump on a fake access violation whose address and ecx lie in a freed ring block
    EXCEPTION_RECORD r = {};
    CONTEXT cx = {};
    r.ExceptionCode = EXCEPTION_ACCESS_VIOLATION, r.NumberParameters = 2, r.ExceptionInformation[1] = (ULONG_PTR)(uintptr_t)(obj + 1);
    cx.Ecx = (DWORD)(uintptr_t)obj, cx.Eip = 0x5EE23C;
    EXCEPTION_POINTERS xp = {&r, &cx};
    const LONG dm0 = k.cfDumps, rh1 = k.cfRingHit;
    cx.Eip = 0x6F5E672C;  // a start-up AV in another module: no dump
    CfOnFault(&xp);
    assert(k.cfDumps == dm0 && k.cfRingHit == rh1);
    cx.Eip = 0x5EE23C;
    CfOnFault(&xp);
    assert(k.cfDumps == dm0 + 1 && k.cfRingHit == rh1 + 2);  // ecx and the fault address
    // docs/67 §17: a wild call (illegal instruction, eip in a freed block) dumps too, with eip as the fault address
    DWORD stk[2] = {0x0050ED8E, 0};
    r.ExceptionCode = EXCEPTION_ILLEGAL_INSTRUCTION, r.NumberParameters = 0;
    cx.Eip = (DWORD)(uintptr_t)(obj + 1), cx.Esp = (DWORD)(uintptr_t)stk;
    CfOnFault(&xp);
    assert(k.cfDumps == dm0 + 2 && k.cfRingHit == rh1 + 4);
    // 2026-11-02 drain crash: a privileged instruction at a heap eip is a wild call too (dumped), and the drain-destroy dump
    // reads the message (arg - 0x10) and the manager without faulting on unreadable ones
    r.ExceptionCode = EXCEPTION_PRIV_INSTRUCTION;
    CfOnFault(&xp);
    assert(k.cfDumps == dm0 + 3 && XcptWild(EXCEPTION_PRIV_INSTRUCTION) && !XcptWild(EXCEPTION_ACCESS_VIOLATION));
    DWORD dstk[2] = {0x0059386B, 0x1010};  // message at 0x1000: unreadable
    cx.Esp = (DWORD)(uintptr_t)dstk, cx.Ecx = 0x1000;
    AaDrainFaultDump(&cx);
    cx.Esp = (DWORD)(uintptr_t)stk;

    // docs/67 §17: scene node check (0xC5FA60) on dummies: .rdata vtable passes, garbage / unreadable counted, NULL skipped
    static DWORD good[2];
    good[0] = (DWORD)(uintptr_t)vtab;
    DWORD sp[4] = {0x00C5FACD, (DWORD)(uintptr_t)good, 0x11, 0x22};
    const LONG sb = g_stBad;
    StCheck(sp, 0x1234);
    assert(g_stBad == sb);
    sp[1] = (DWORD)(uintptr_t)obj;  // obj[0] = 0x123: outside .rdata
    StCheck(sp, (DWORD)(uintptr_t)good);
    sp[1] = 0x1000;  // unreadable
    StCheck(sp, 0x1000);
    sp[1] = 0;
    StCheck(sp, 0);
    assert(g_stBad == sb + 2);

    VirtualFree(pg, 0, MEM_RELEASE);
    g_cfOn = 0, g_cfEraseOrig = NULL, g_cfClrOrig = NULL, g_cfVtLo = g_cfVtHi = 0, g_cfDumpN = 0, g_cfBadLogs[0] = g_cfBadLogs[1] = 0, g_cfHeapLogs = 0;
    printf("crash forensics: ring wraparound, block lookup, heap check, erase/clear guards on dummies, trampoline, dump ok\n");
}

// docs/186 §8: our-events ring (off = nothing, wraparound, spans per tag) and the four draw guards on dummies: the model and
// resource thunks (ecx), the effect stub (its two moved instructions, then the check) and the binding thunk (cdecl stack
// arguments); a bad pointer is counted and logged, the good path is not, and the game's code (a fake) runs either way.
static DWORD g_cdFakeEcx;
static int g_cdFakeN;
static const u8* g_cdTEntry;
static u8* g_cdTOwner;
static u8 __fastcall CdFakeGetter(void* t) { g_cdFakeEcx = (DWORD)(uintptr_t)t, g_cdFakeN++; return 7; }
static DWORD __cdecl CdFakeBind(DWORD key, DWORD table, DWORD count) { g_cdFakeN++; return key ? 0 : count ? table : 0; }
static __declspec(naked) void CdTestRet() { __asm ret }
static __declspec(naked) void CdCallFx() {  // edi = g_cdTEntry, esi = g_cdTOwner, through the stub, back via CdTestRet
    __asm {
        push esi
        push edi
        mov edi, g_cdTEntry
        mov esi, g_cdTOwner
        call CdFxStub
        pop edi
        pop esi
        ret
    }
}

static void TestCrashForensicsDraw() {
    Ts3nCounters& k = ts3native_counters;
    // off: nothing recorded
    const LONG s0 = g_ceSeq;
    CeRec(CE_DECOMP, 0x1000, 64, 64);
    assert(g_ceSeq == s0);
    g_ceOn = 1;
    // wraparound: 8192 + 10 events, the first 10 are gone
    const LONG base = g_ceSeq;
    for (DWORD i = 0; i < kCeRing + 10; i++) CeRec(CE_RC_HOLD, 0x20000000 + i * 16, i, 1);
    assert(g_ceSeq == base + (LONG)kCeRing + 10 && k.ceRecs == g_ceSeq);
    CeEnt e;
    assert(!CeFind(0x20000000 + 5 * 16, &e));
    assert(CeFind(0x20000000 + 10 * 16 + 15, &e) && e.tag == CE_RC_HOLD && e.x == 10 && e.tid == GetCurrentThreadId());
    // spans: a decode covers its output, a destroyed object 0x400, other tags 16; draw-bad entries are never a match
    CeRec(CE_DECOMP, 0x30000000, 0x5000, 0x6000);
    assert(CeFind(0x30004FFF, &e) && e.tag == CE_DECOMP && !CeFind(0x30005000, &e));
    CeRec(CE_DESTROY_OUT, 0x31000000);
    assert(CeFind(0x310003FF, &e) && e.tag == CE_DESTROY_OUT && !CeFind(0x31000400, &e));
    CeRec(CE_DRAW_BAD, 0x32000000, 1, 0);
    assert(!CeFind(0x32000000, &e));
    char d[220];
    CeDescribe(0x31000010, d, sizeof(d));
    assert(strstr(d, "destroy-out a 0x31000000 (+0x10)"));
    CeDescribe(0x7FFF0000, d, sizeof(d));
    assert(strstr(d, "not in our-events ring"));

    // model / resource thunks: ecx 0x20 and 0 are bad (counted, a draw-bad ring entry), a real pointer passes silently;
    // the getter (here a fake) gets the same ecx every time
    typedef u8(__fastcall * GetterFn)(void*);
    g_cdModelFn = g_cdResFn = (DWORD)(uintptr_t)CdFakeGetter;
    static DWORD obj[8];
    const LONG b0 = k.cdBad[0], b1 = k.cdBad[1];
    assert(((GetterFn)(void*)CdModelThunk)(obj) == 7 && g_cdFakeEcx == (DWORD)(uintptr_t)obj && g_cdFakeN == 1 && k.cdBad[0] == b0);
    assert(((GetterFn)(void*)CdModelThunk)((void*)0x20) == 7 && g_cdFakeEcx == 0x20 && g_cdFakeN == 2 && k.cdBad[0] == b0 + 1);
    const CeEnt& last = g_ceRing[(DWORD)(g_ceSeq - 1) & (kCeRing - 1)];
    assert(last.tag == CE_DRAW_BAD && last.a == 0x20 && last.x == 0);
    assert(((GetterFn)(void*)CdResThunk)(NULL) == 7 && g_cdFakeEcx == 0 && k.cdBad[1] == b1 + 1);
    assert(((GetterFn)(void*)CdResThunk)((void*)0xFFFFFFF0) == 7 && k.cdBad[1] == b1 + 2);
    assert(((GetterFn)(void*)CdResThunk)(obj) == 7 && k.cdBad[1] == b1 + 2);

    // effect stub: the moved instructions still store the instance into the list (+0x3130); instance 0 is bad
    static u8 owner[0x3200];
    static DWORD entry[20];
    g_cdFxBack = (DWORD)(uintptr_t)CdTestRet;
    g_cdTEntry = (const u8*)entry, g_cdTOwner = owner;
    entry[1] = (DWORD)(uintptr_t)obj;
    const LONG b2 = k.cdBad[2];
    CdCallFx();
    assert(*(const DWORD*)(owner + 0x3130) == (DWORD)(uintptr_t)obj && k.cdBad[2] == b2);
    entry[1] = 0;
    CdCallFx();
    assert(*(const DWORD*)(owner + 0x3130) == 0 && k.cdBad[2] == b2 + 1);

    // binding thunk (cdecl key, table, count): table 0xFFFFFFFF, a low table and a huge count are bad; a real table or an
    // empty one passes; the lookup (a fake) answers as 0x68E8F0 would
    typedef DWORD(__cdecl * BindFn)(DWORD, DWORD, DWORD);
    const BindFn bind = (BindFn)(void*)CdBindThunk;
    g_cdBindFn = (DWORD)(uintptr_t)CdFakeBind;
    const LONG b3 = k.cdBad[3];
    const int n0 = g_cdFakeN;
    assert(bind(0, (DWORD)(uintptr_t)obj, 2) == (DWORD)(uintptr_t)obj && bind(5, 0, 0) == 0 && k.cdBad[3] == b3);
    assert(bind(0, 0xFFFFFFFF, 3) == 0xFFFFFFFF && k.cdBad[3] == b3 + 1);
    assert(bind(1, 0x40, 3) == 0 && k.cdBad[3] == b3 + 2);
    assert(bind(1, (DWORD)(uintptr_t)obj, 0x200000) == 0 && k.cdBad[3] == b3 + 3 && g_cdFakeN == n0 + 5);

    // the dump: a fake access violation whose ecx lies in a destroyed object of the ring (log only; the P5 dump counts)
    CeRec(CE_DESTROY_OUT, (DWORD)(uintptr_t)obj);
    EXCEPTION_RECORD r = {};
    CONTEXT cx = {};
    r.ExceptionCode = EXCEPTION_ACCESS_VIOLATION, r.NumberParameters = 2, r.ExceptionInformation[1] = 0x44;
    cx.Ecx = (DWORD)(uintptr_t)(obj + 1), cx.Eip = 0x6C93D0;
    EXCEPTION_POINTERS xp = {&r, &cx};
    g_cfOn = 1;
    const LONG dm0 = k.cfDumps;
    CfOnFault(&xp);
    assert(k.cfDumps == dm0 + 1);

    g_cfOn = 0, g_ceOn = 0, g_cfDumpN = 0;
    g_cdModelFn = 0x0068EEF0, g_cdResFn = 0x006C93D0, g_cdFxBack = 0x0071D56B, g_cdBindFn = 0x0068E8F0;
    for (int i = 0; i < 4; i++) g_cdLogs[i] = 0;
    printf("crash forensics draw: our-events ring, spans, model/resource/effect/binding guards on dummies, dump ok\n");
}

// docs/186 §9: EffectLog on dummies: a fake SwarmRenderer (entry array +0x3110, one bucket list +0x38EC, bucket count
// +0x3980) and fake add / remove / gather / draw behind the four hooks: records, the remove-since-gather count, a stale-list
// draw (first per gather walks the buckets: one dangling node), removes off the main thread, the vtable slot swap (match and
// mismatch), the dump. Round 1 stays on and must still fire with EffectLog on (effect guard + FxExplain, model guard).
static u8 g_fxtList[0x3990];
static DWORD g_fxtEnt[4 * 20], g_fxtHead[3], g_fxtNode[3];
static u8 g_fxtNodeObj[0x220];
static int g_fxtGathers, g_fxtDraws;
static DWORD __fastcall FxtAdd(u8*, void*, DWORD owner, DWORD, LONG* index) {
    *index = 2;
    DWORD* e = g_fxtEnt + 2 * 20;
    e[0] = owner, e[1] = 0x50000000, e[8] = 0xAAA74050, e[9] = 0x4DED00F4, ((u8*)e)[0x4E] = 1;
    return 1;
}
static DWORD __fastcall FxtRem(u8*, void*, LONG index) {
    DWORD* e = g_fxtEnt + index * 20;
    e[0] = e[1] = 0, ((u8*)e)[0x4E] = 0;  // as 0x71FF10: the bucket node stays
    return 0;
}
static void __fastcall FxtGather(u8*, void*) {  // as 0x7212E0: one bucket whose list holds entry 2 (node index = count + 2)
    g_fxtGathers++;
    g_fxtHead[0] = g_fxtHead[1] = (DWORD)(uintptr_t)g_fxtNode;
    g_fxtNode[0] = g_fxtNode[1] = (DWORD)(uintptr_t)g_fxtHead, g_fxtNode[2] = 1 + 2;
}
static void __fastcall FxtDraw(u8*, void*, DWORD, DWORD) { g_fxtDraws++; }
static DWORD WINAPI FxtOffMain(LPVOID) { FxRemHook(g_fxtList, NULL, 1); return 0; }

static void TestCrashForensicsFx() {
    Ts3nCounters& k = ts3native_counters;
    const DWORD savedMain = g_mainTid;
    g_mainTid = GetCurrentThreadId();
    *(DWORD*)(g_fxtList + 0x3110) = (DWORD)(uintptr_t)g_fxtEnt, *(DWORD*)(g_fxtList + 0x3114) = (DWORD)(uintptr_t)(g_fxtEnt + 4 * 20);
    *(DWORD*)(g_fxtList + 0x38EC) = (DWORD)(uintptr_t)g_fxtHead, *(DWORD*)(g_fxtList + 0x3980) = 1;
    g_fxtHead[0] = g_fxtHead[1] = (DWORD)(uintptr_t)g_fxtHead;
    *(DWORD*)(g_fxtNodeObj + 0x210) = (DWORD)(uintptr_t)g_fxtList;
    g_fxAddFn = (DWORD)(uintptr_t)FxtAdd, g_fxRemFn = (DWORD)(uintptr_t)FxtRem, g_fxGatherFn = (DWORD)(uintptr_t)FxtGather,
    g_fxDrawFn = (DWORD)(uintptr_t)FxtDraw;
    LONG idx = -1;
    // off: pass-through, nothing recorded
    const LONG s0 = g_fxSeq;
    assert(FxAddHook(g_fxtList, NULL, 0x1234, 0, &idx) == 1 && idx == 2 && g_fxSeq == s0);
    FxGatherHook(g_fxtNodeObj, NULL);
    assert(g_fxtGathers == 1 && g_fxSeq == s0);

    g_fxOn = 1, g_ceOn = 1, g_cfOn = 1;
    const LONG r0 = k.fxRem, st0 = k.fxStaleDraw, om0 = k.fxRemOffMain;
    FxAddHook(g_fxtList, NULL, 0x1234, 0, &idx);
    const FxEnt& a = g_fxRing[(DWORD)(g_fxSeq - 1) & (kFxRing - 1)];
    assert(a.tag == FX_ADD && a.index == 2 && a.entry == (DWORD)(uintptr_t)(g_fxtEnt + 40) && a.owner == 0x1234 && a.idLo == 0xAAA74050 &&
           a.list == (DWORD)(uintptr_t)g_fxtList && k.fxRecs == g_fxSeq);
    const LONG g0 = g_fxGseq;
    FxGatherHook(g_fxtNodeObj, NULL);
    assert(g_fxGseq == g0 + 1 && g_fxRemSince == 0 && g_fxRing[(DWORD)(g_fxSeq - 1) & (kFxRing - 1)].tag == FX_GATHER);
    FxDrawHook(g_fxtNodeObj, NULL, 0, 0);
    assert(k.fxStaleDraw == st0 && g_fxtDraws == 1 && FxDangling(g_fxtList) == 0);
    // a remove after the gather: recorded with the entry as it was, then the stale draw (one dangling node), once per gather
    FxRemHook(g_fxtList, NULL, 2);
    const FxEnt& rm = g_fxRing[(DWORD)(g_fxSeq - 1) & (kFxRing - 1)];
    assert(rm.tag == FX_REM && rm.owner == 0x1234 && rm.inst == 0x50000000 && g_fxtEnt[40] == 0 && k.fxRem == r0 + 1 && g_fxRemSince == 1);
    const LONG sq = g_fxSeq;
    FxDrawHook(g_fxtNodeObj, NULL, 0, 0);
    const FxEnt& sd = g_fxRing[(DWORD)(g_fxSeq - 1) & (kFxRing - 1)];
    assert(k.fxStaleDraw == st0 + 1 && g_fxSeq == sq + 1 && sd.tag == FX_STALE && sd.index == 1 && sd.entry == 1);
    FxDrawHook(g_fxtNodeObj, NULL, 0, 0);
    assert(k.fxStaleDraw == st0 + 2 && g_fxSeq == sq + 1 && g_fxtDraws == 3);
    // a remove of a free slot does not count as a change; a remove on another thread counts as off main
    FxRemHook(g_fxtList, NULL, 3);
    assert(g_fxRemSince == 1 && k.fxRem == r0 + 2);
    HANDLE h = CreateThread(NULL, 0, FxtOffMain, NULL, 0, NULL);
    WaitForSingleObject(h, INFINITE);
    CloseHandle(h);
    assert(k.fxRemOffMain == om0 + 1 && k.fxRem == r0 + 3);

    // round 1 with EffectLog on: the effect guard still fires on the cleared entry (FxExplain reads the ring), the model
    // guard still fires and still records into the our-events ring
    g_cdFxBack = (DWORD)(uintptr_t)CdTestRet;
    g_cdTEntry = (const u8*)(g_fxtEnt + 40), g_cdTOwner = g_fxtList;
    const LONG b2 = k.cdBad[2], b0 = k.cdBad[0];
    CdCallFx();
    assert(k.cdBad[2] == b2 + 1);
    typedef u8(__fastcall * GetterFn)(void*);
    g_cdModelFn = (DWORD)(uintptr_t)CdFakeGetter;
    assert(((GetterFn)(void*)CdModelThunk)((void*)0x20) == 7 && k.cdBad[0] == b0 + 1);
    const CeEnt& last = g_ceRing[(DWORD)(g_ceSeq - 1) & (kCeRing - 1)];
    assert(last.tag == CE_DRAW_BAD && last.a == 0x20);

    // the next gather clears the stale state
    FxGatherHook(g_fxtNodeObj, NULL);
    assert(g_fxRemSince == 0);
    FxDrawHook(g_fxtNodeObj, NULL, 0, 0);
    assert(k.fxStaleDraw == st0 + 2);
    // wraparound
    const LONG w0 = g_fxSeq;
    for (DWORD i = 0; i < kFxRing + 5; i++) FxRemHook(g_fxtList, NULL, 3);
    assert(g_fxSeq == w0 + (LONG)kFxRing + 5 && g_fxRing[(DWORD)(g_fxSeq - 1) & (kFxRing - 1)].seq == g_fxSeq);
    // vtable slot swap: only over the expected function
    static DWORD slot = 0x0071FF10;
    assert(FxSlot((DWORD)(uintptr_t)&slot, 0x0071FF10, (void*)FxRemHook, "test") && slot == (DWORD)(uintptr_t)FxRemHook);
    slot = 0x12345678;
    assert(!FxSlot((DWORD)(uintptr_t)&slot, 0x0071FF10, (void*)FxRemHook, "test") && slot == 0x12345678);
    // the dump (P5, ours, all draw guard sites, effect parts)
    EXCEPTION_RECORD r = {};
    CONTEXT cx = {};
    r.ExceptionCode = EXCEPTION_ACCESS_VIOLATION, r.NumberParameters = 2, r.ExceptionInformation[0] = 1, r.ExceptionInformation[1] = 0x2AC;
    cx.Eip = 0x71D56E, cx.Edi = (DWORD)(uintptr_t)(g_fxtEnt + 40), cx.Esi = (DWORD)(uintptr_t)g_fxtList;
    EXCEPTION_POINTERS xp = {&r, &cx};
    const LONG dm0 = k.cfDumps;
    CfOnFault(&xp);
    assert(k.cfDumps == dm0 + 1);

    g_fxOn = 0, g_ceOn = 0, g_cfOn = 0, g_cfDumpN = 0, g_fxRemSince = 0, g_fxStaleLogs = 0, g_fxGatherQpc = 0;
    g_mainTid = savedMain;
    g_fxAddFn = 0x0071FD20, g_fxRemFn = 0x0071FF10, g_fxGatherFn = 0x00722190, g_fxDrawFn = 0x0071D660;
    g_cdModelFn = 0x0068EEF0, g_cdFxBack = 0x0071D56B;
    for (int i = 0; i < 4; i++) g_cdLogs[i] = 0;
    printf("crash forensics effect log: add/remove/gather/draw hooks, stale-list draw + dangling walk, off-main removes, slot swap, "
           "round-1 guards with EffectLog on, dump ok\n");
}

// docs/186 §10: ObjResLog on dummies: a fake SceneModel object (handle at +0x210) and fake unload / load request / full
// release / node update behind the hooks: records with +8 before (and after for a LOD swap), counters (unload with a key
// counts a full release), the node update thunk's caller from [ebp+4] and the last-update table, the resource DRAW BAD on
// the emptied handle (round 1 site 1 still counts; OrExplain sees the queued node), the stale Swarm draw with the drawable
// queued (round 2 still counts), the dump.
static __declspec(align(16)) u8 g_ortObj[0x300];
static DWORD g_ortLod = 0x55550000;
static void __fastcall OrtUnload(u8* h, void*) { *(DWORD*)(h + 4) = *(DWORD*)(h + 8) = *(DWORD*)(h + 0xC) = *(DWORD*)(h + 0x10) = 0, (*(DWORD*)(h + 0x1C))++; }
static void __fastcall OrtRel(u8* h, void*) { memset(h, 0, 0x14), (*(DWORD*)(h + 0x1C))++; }
static int g_ortLoads;
static void __fastcall OrtLoad(u8*, void*) { g_ortLoads++; }
static DWORD __fastcall OrtUpd(u8* h, void*) { *(DWORD*)(h + 8) = g_ortLod; return 5; }
static __declspec(naked) DWORD __fastcall OrtCallUpd(u8*) {  // ecx = handle; fake 0x6F7B40 frame: [ebp+4] = a game drain return address
    __asm {
        push ebp
        push 0x006E41A0
        push 0
        mov ebp, esp
        call OrUpdThunk
        add esp, 8
        pop ebp
        ret
    }
}
static __declspec(naked) void OrtCallRes() {  // as 0x6FA2DB: edi = the object, ecx = what 0x6DB810 returned (0)
    __asm {
        push edi
        mov edi, offset g_ortObj
        xor ecx, ecx
        call CdResThunk
        pop edi
        ret
    }
}

static void TestCrashForensicsObjRes() {
    static_assert(offsetof(Ts3nCounters, mhReady) == 0x47C4 && offsetof(Ts3nCounters, mhFxQueued) == 0x47D8 &&
                      sizeof(Ts3nCounters) == 6379 * 4, "counters v117 layout after v116 (docs/registry.md)");
    Ts3nCounters& k = ts3native_counters;
    const DWORD savedMain = g_mainTid;
    g_mainTid = GetCurrentThreadId();
    u8* const h = g_ortObj + 0x210;
    const DWORD ha = (DWORD)(uintptr_t)h;
    *(DWORD*)h = 0x386EA490, *(DWORD*)(h + 8) = 0x44440000, *(DWORD*)(h + 0x1C) = 7;
    g_orUnloadFn = (DWORD)(uintptr_t)OrtUnload, g_orLoadFn = (DWORD)(uintptr_t)OrtLoad, g_orRelFn = (DWORD)(uintptr_t)OrtRel,
    g_orUpdFn = (DWORD)(uintptr_t)OrtUpd;
    // off: pass-through, nothing recorded
    const LONG s0 = g_orSeq;
    OrUnloadHook(h, NULL);
    assert(*(DWORD*)(h + 8) == 0 && *(DWORD*)(h + 0x1C) == 8 && g_orSeq == s0);
    assert(OrtCallUpd(h) == 5 && *(DWORD*)(h + 8) == 0x55550000 && g_orSeq == s0);

    g_orOn = 1, g_cfOn = 1, g_ceOn = 1;
    const LONG u0 = k.mhUnload, r0 = k.mhRelease, l0 = k.mhLodSet;
    // unload: record with +8 as it was, model kept
    OrUnloadHook(h, NULL);
    const OrEnt& un = g_orRing[(DWORD)(g_orSeq - 1) & (kOrRing - 1)];
    assert(un.tag == OR_UNLOAD && un.h == ha && un.p8 == 0x55550000 && un.p0 == 0x386EA490 && un.ver == 8 && un.key == 0 &&
           k.mhUnload == u0 + 1 && k.mhRelease == r0 && k.mhRecs == g_orSeq && *(DWORD*)(h + 8) == 0);
    // node update through the thunk: +8 refilled -> a lod-set entry (0 -> LOD) with the drain's return address; the table
    // holds the newest update; a second update with no change adds no ring entry
    g_ortLod = 0x66660000;
    assert(OrtCallUpd(h) == 5);
    const OrEnt& st = g_orRing[(DWORD)(g_orSeq - 1) & (kOrRing - 1)];
    assert(st.tag == OR_SET && st.p8 == 0 && st.p8after == 0x66660000 && st.code[0] == 0x006E41A0 && k.mhLodSet == l0 + 1);
    const LONG s1 = g_orSeq;
    assert(OrtCallUpd(h) == 5 && g_orSeq == s1 && k.mhLodSet == l0 + 1);
    const OrLast& tu = g_orUpd[OrSlot(ha)];
    assert(tu.h == ha && tu.caller == 0x006E41A0 && tu.p8 == 0x66660000);
    // load request and full release; an unload with a key counts as a full release too
    OrLoadHook(h, NULL);
    assert(g_ortLoads == 1 && g_orRing[(DWORD)(g_orSeq - 1) & (kOrRing - 1)].tag == OR_LOAD);
    OrRelHook(h, NULL);
    assert(k.mhRelease == r0 + 1 && g_orRing[(DWORD)(g_orSeq - 1) & (kOrRing - 1)].tag == OR_RELEASE && *(DWORD*)h == 0);
    *(DWORD*)h = 0x386EA490, *(DWORD*)(h + 0x30) = 0x2;
    OrUnloadHook(h, NULL);
    assert(k.mhUnload == u0 + 2 && k.mhRelease == r0 + 2);
    *(DWORD*)(h + 0x30) = 0;

    // round 1 site 1 on the emptied handle with ObjResLog on: counted, OrExplain reads the queued node (link +0x18 elsewhere)
    *(DWORD*)(g_ortObj + 0x18) = 0x12340000, *(DWORD*)(g_ortObj + 0x30) = (DWORD)(uintptr_t)(g_ortObj + 0x100);
    *(DWORD*)(g_ortObj + 0x100 + 0x18) = 512;
    DWORD holder = 0, drained = 0;
    assert(CdNodeQueued((DWORD)(uintptr_t)g_ortObj, &holder, &drained) && drained == 512);
    g_cdResFn = (DWORD)(uintptr_t)CdFakeGetter;
    const LONG b1 = k.cdBad[1];
    OrtCallRes();
    assert(k.cdBad[1] == b1 + 1 && g_cdFakeEcx == 0);
    *(DWORD*)(g_ortObj + 0x18) = (DWORD)(uintptr_t)(g_ortObj + 0x18);  // self-loop = not queued
    assert(!CdNodeQueued((DWORD)(uintptr_t)g_ortObj, &holder, &drained));

    // round 2 with ObjResLog on: a stale Swarm draw whose drawable is still queued counts in orFxQueued
    g_fxOn = 1;
    g_fxAddFn = (DWORD)(uintptr_t)FxtAdd, g_fxRemFn = (DWORD)(uintptr_t)FxtRem, g_fxGatherFn = (DWORD)(uintptr_t)FxtGather,
    g_fxDrawFn = (DWORD)(uintptr_t)FxtDraw;
    LONG idx = -1;
    FxAddHook(g_fxtList, NULL, 0x1234, 0, &idx);
    FxGatherHook(g_fxtNodeObj, NULL);
    FxRemHook(g_fxtList, NULL, 2);
    *(DWORD*)(g_fxtNodeObj + 0x18) = 0x12340000;
    const LONG q0 = k.mhFxQueued, sd0 = k.fxStaleDraw;
    FxDrawHook(g_fxtNodeObj, NULL, 0, 0);
    assert(k.fxStaleDraw == sd0 + 1 && k.mhFxQueued == q0 + 1 && g_fxRing[(DWORD)(g_fxSeq - 1) & (kFxRing - 1)].owner == 1);
    *(DWORD*)(g_fxtNodeObj + 0x18) = 0;

    // the dump: a fake 0x6C93D0 read fault (P5, ours, effect, model handle parts)
    EXCEPTION_RECORD r = {};
    CONTEXT cx = {};
    r.ExceptionCode = EXCEPTION_ACCESS_VIOLATION, r.NumberParameters = 2, r.ExceptionInformation[1] = 0x44;
    cx.Eip = 0x6C93D0, cx.Edi = (DWORD)(uintptr_t)g_ortObj;
    EXCEPTION_POINTERS xp = {&r, &cx};
    const LONG dm0 = k.cfDumps;
    CfOnFault(&xp);
    assert(k.cfDumps == dm0 + 1);
    // wraparound
    const LONG w0 = g_orSeq;
    for (DWORD i = 0; i < kOrRing + 3; i++) OrLoadHook(h, NULL);
    assert(g_orSeq == w0 + (LONG)kOrRing + 3 && g_orRing[(DWORD)(g_orSeq - 1) & (kOrRing - 1)].seq == g_orSeq);

    g_orOn = 0, g_fxOn = 0, g_ceOn = 0, g_cfOn = 0, g_cfDumpN = 0, g_fxRemSince = 0, g_fxStaleLogs = 0, g_fxGatherQpc = 0;
    g_mainTid = savedMain;
    g_orUnloadFn = 0x006DBCE0, g_orLoadFn = 0x006DCD90, g_orRelFn = 0x006DBB50, g_orUpdFn = 0x006DC5B0;
    g_fxAddFn = 0x0071FD20, g_fxRemFn = 0x0071FF10, g_fxGatherFn = 0x00722190, g_fxDrawFn = 0x0071D660;
    g_cdResFn = 0x006C93D0;
    for (int i = 0; i < 4; i++) g_cdLogs[i] = 0;
    printf("crash forensics model handle log: unload/load/release/node-update hooks, lod-set + drain caller, update table, resource "
           "DRAW BAD with the queued node, stale Swarm draw with the drawable queued, dump ok\n");
}
