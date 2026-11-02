// TestMemDiag ([MemDiag], memdiag.cpp, docs/190 phase 1, docs/191 module 1). Included by selftest.cpp after userdiag-test.h
// (uses its UdtLog). Classifier on synthetic regions, event tags, the reads of the game structures on fakes (offsets), the
// line format and marks, and the self-test itself on this process (every 'H' field must move by the injected size).
static void TestMemDiag() {
    // allocation grouping: 64 MiB private reserve (16 committed) = arena; a private allocation with a guard page = stack;
    // a 64 MiB mapped view = neither; a free region ends a group
    {
        UdMemStat s = {};
        MEMORY_BASIC_INFORMATION m = {};
        auto add = [&](uintptr_t base, DWORD state, DWORD type, DWORD prot, SIZE_T kb) {
            m.AllocationBase = (void*)base, m.State = state, m.Type = type, m.Protect = prot, m.RegionSize = kb << 10;
            UdMemAdd(s, m);
        };
        add(0x10000000, MEM_COMMIT, MEM_PRIVATE, PAGE_READWRITE, 16 << 10), add(0x10000000, MEM_RESERVE, MEM_PRIVATE, 0, 48 << 10);
        add(0x14000000, MEM_RESERVE, MEM_PRIVATE, 0, 1012), add(0x14000000, MEM_COMMIT, MEM_PRIVATE, PAGE_READWRITE | PAGE_GUARD, 4),
            add(0x14000000, MEM_COMMIT, MEM_PRIVATE, PAGE_READWRITE, 8);
        add(0, MEM_FREE, 0, PAGE_NOACCESS, 64);
        add(0x20000000, MEM_COMMIT, MEM_MAPPED, PAGE_READWRITE, 64 << 10);
        add(0x30000000, MEM_COMMIT, MEM_PRIVATE, PAGE_READWRITE, 128 << 10);  // 128 MiB: the 9th mimalloc arena size
        UdMemGroupEnd(s);
        assert(s.miRes == 192ull << 20 && s.miCom == 144ull << 20);
        assert(s.stkRes == 1024ull << 10 && s.stkCom == 12ull << 10);
        assert(s.privCommit == (16ull << 20) + (12ull << 10) + (128ull << 20) && s.mapped == 64ull << 20 && s.regions == 8);
    }

    // [MemDiag] self-test view: only MEM_MAPPED regions starting inside [ownLo, ownLo + ownLen) count; the map total has all
    {
        UdMemStat o = {};
        o.ownLo = 0x20000000, o.ownLen = 1 << 20;
        MEMORY_BASIC_INFORMATION m = {};
        m.State = MEM_COMMIT, m.Type = MEM_MAPPED, m.RegionSize = 1 << 20;
        for (uintptr_t base : {0x20000000u, 0x20100000u, 0x1FF00000u}) m.AllocationBase = m.BaseAddress = (void*)base, UdMemAdd(o, m);
        m.Type = MEM_PRIVATE, m.AllocationBase = m.BaseAddress = (void*)0x20000000;
        UdMemAdd(o, m);
        assert(o.ownMapped == 1u << 20 && o.mapped == 3u << 20);
    }
    // file-mapping verdict (0 ok, 1 retry, 2 fail): the walk's count of our own view decides; the whole-process total only
    // triggers retries (run 2026-10-25: streaming moved it +36.12 / back -30.62 with the view exact)
    {
        const u64 w = 32ull << 20;
        assert(MdMapVerdict(w, 0, w, 32.0, 32.0, 0.5, false) == 0 && MdMapVerdict(w, 0, w, 32.3, 31.8, 0.5, true) == 0);
        assert(MdMapVerdict(w, 0, w, 36.12, 30.62, 0.5, false) == 1 && MdMapVerdict(w, 0, w, 36.12, 30.62, 0.5, true) == 0);
        assert(MdMapVerdict(w - 4096, 0, w, 32.0, 32.0, 0.5, false) == 2 && MdMapVerdict(0, 0, w, 32.0, 32.0, 0.5, false) == 2);
        assert(MdMapVerdict(w, 4096, w, 32.0, 32.0, 0.5, false) == 1 && MdMapVerdict(w, 4096, w, 32.0, 32.0, 0.5, true) == 2);
    }

    // sample names -> tags; world events (travel save, world switch)
    assert(!strcmp(MdTag("diag: memory at world load #2"), "load") && !strcmp(MdTag("save: memory at start #1"), "save-start"));
    assert(!strcmp(MdTag("save: memory at end #1"), "save-end") && !strcmp(MdTag("diag: memory at exit"), "exit"));
    assert(!strcmp(MdTag("diag: memory at 20 min"), "periodic") && !strcmp(MdTag("diag: memory at mark #3"), "mark"));
    assert(!strcmp(MdTag("diag: memory at selftest leak after"), "selftest"));
    g_mdWorld[0] = 0, g_mdTravel = false;
    assert(MdWorldEvent("world: Bridgeport.world, lots 90") == NULL && MdWorldEvent("world: Bridgeport.world, lots 90") == NULL);
    MdSaveStart(true);
    assert(!strcmp(MdWorldEvent("world: France.world, lots 12"), "travel,world-switch"));
    MdSaveStart(false);
    assert(!strcmp(MdWorldEvent("world: Bridgeport.world, lots 90"), "world-switch"));
    MdSaveStart(true);
    assert(!strcmp(MdWorldEvent("world: Bridgeport.world, lots 90"), "travel"));
    assert(MdWorldEvent("not a world line") == NULL);

    // game structures on fakes: EA heap direct blocks, GC counters, ResourceCache (priority 0) + AnimationCache (1000)
    static u8 heap[0x518], core0[0x20], core1[0x20], rcC[0xA0], animC[0xA0], mgr[0xA8];
    static DWORD gc[3] = {360u << 20, 151u << 20, 812}, entries[4];
    static const u8* heapP = heap;
    static const u8* mgrP = mgr;
    static const u8* nullP = NULL;
    static const DWORD zero[3] = {};
    *(DWORD*)(heap + 0x488) = 3, *(DWORD*)(heap + 0x48C) = 22u << 20;
    // two cores in the ring headed at heap +0x448 (first at +0x460, next at core +0x18): 100+12 MB committed, 128+16 reserved
    *(DWORD*)(heap + 0x460) = (DWORD)(uintptr_t)core0, *(DWORD*)(core0 + 0x18) = (DWORD)(uintptr_t)core1;
    *(DWORD*)(core1 + 0x18) = (DWORD)(uintptr_t)(heap + 0x448);
    *(DWORD*)(core0 + 4) = 100u << 20, *(DWORD*)(core0 + 8) = 128u << 20, *(DWORD*)(core1 + 4) = 12u << 20, *(DWORD*)(core1 + 8) = 16u << 20;
    *(DWORD*)rcC = *(DWORD*)animC = 0xFFDD94;
    *(DWORD*)(rcC + 0x94) = 388u << 20, *(DWORD*)(rcC + 0x98) = 14u << 20, *(DWORD*)(rcC + 0x8C) = 200u << 20, *(DWORD*)(rcC + 0x28) = 41000;
    *(DWORD*)(animC + 0x94) = 4u << 20;
    entries[0] = (DWORD)(uintptr_t)animC, entries[1] = 1000, entries[2] = (DWORD)(uintptr_t)rcC, entries[3] = 0;
    *(DWORD*)mgr = 0xFFE250, *(DWORD*)(mgr + 0xA0) = (DWORD)(uintptr_t)entries, *(DWORD*)(mgr + 0xA4) = (DWORD)(uintptr_t)(entries + 4);
    const u8* const* savedEa = g_mdEaHeapSlot;
    const u8* const* savedCore[2] = {g_mdEaCoreSlot[0], g_mdEaCoreSlot[1]};
    const u8* const* savedRc = g_mdRcMgrSlot;
    const DWORD* savedGc[3] = {g_mdGc[0], g_mdGc[1], g_mdGc[2]};
    g_mdEaHeapSlot = &heapP, g_mdRcMgrSlot = &mgrP;
    g_mdEaCoreSlot[0] = g_mdEaCoreSlot[1] = &heapP;  // same object twice: counted once
    for (int i = 0; i < 3; i++) g_mdGc[i] = &gc[i];
    Ts3nCounters& k = ts3native_counters;
    const LONG savedPl = k.plKB, savedSp = k.spKB;
    k.plKB = 96 << 10, k.spKB = 7 << 10;
    UdMemStat s = {};
    s.privCommit = 1830ull << 20, s.commit = 2167ull << 20, s.freeB = 1646ull << 20, s.big1 = 1525ull << 20;
    s.miCom = 210ull << 20, s.stkCom = 9ull << 20;
    MdVals x;
    MdValues(s, 1851ull << 20, x);
    assert(x.have[MD_EADIR] && x.v[MD_EADIR] == 22 && x.v[MD_EADIRN] == 3 && x.v[MD_GCHEAP] == 360 && x.v[MD_GCN] == 812);
    assert(x.v[MD_RC] == 388 && x.v[MD_RCCOLD] == 14 && x.v[MD_RCBUD] == 200 && x.v[MD_RCN] == 41000 && x.v[MD_ANIM] == 4);
    assert(x.v[MD_EACOM] == 112 && x.v[MD_EARES] == 144 && x.v[MD_EACOREN] == 2);
    assert(fabs(x.v[MD_REST] - (1851 - 360 - 22 - 112 - 210 - x.v[MD_RP] - 96 - 7 - 9)) < 1e-6);
    *(DWORD*)mgr = 0x12345678;  // not the manager's vtable: no rc fields
    g_mdEaHeapSlot = &nullP;    // heap not created yet
    g_mdEaCoreSlot[0] = g_mdEaCoreSlot[1] = &nullP;
    g_mdGc[0] = NULL;           // unreadable: SEH, no gc fields
    MdValues(s, 1851ull << 20, x);
    assert(!x.have[MD_RC] && !x.have[MD_ANIM] && !x.have[MD_EADIR] && !x.have[MD_EACOM] && !x.have[MD_GCHEAP] && x.have[MD_PRIV]);
    *(DWORD*)mgr = 0xFFE250, g_mdEaHeapSlot = &heapP, g_mdGc[0] = &gc[0];
    g_mdEaCoreSlot[0] = g_mdEaCoreSlot[1] = &heapP;

    // log capture: the line, marks, legend once
    wchar_t tmp[MAX_PATH], lp[MAX_PATH];
    GetTempPathW(MAX_PATH, tmp);
    swprintf_s(lp, L"%sts3n-mdtest-%lu.log", tmp, GetCurrentProcessId());
    HANDLE h = CreateFileW(lp, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ, NULL, CREATE_ALWAYS, FILE_FLAG_DELETE_ON_CLOSE, NULL);
    assert(h != INVALID_HANDLE_VALUE);
    const HANDLE saved = g_log;
    g_log = h;
    static char buf[1 << 17];
    const bool wasOn = g_mdOn;
    g_mdOn = true, g_mdLegend = 0, g_mdN = 0, g_mdPass = g_mdFail = 0;
    MdAfterSample("diag: memory at world load #1", "travel,world-switch", s, 1851ull << 20);
    MdAfterSample("diag: memory at 10 min", NULL, s, 1851ull << 20);
    UdtLog(buf, sizeof(buf), h);
    const char* L = strstr(buf, "diag: mem #1 load ev=travel,world-switch priv=1851 wPriv=1830 wCom=2167 free=1646 big=1525? img=0.0? map=0.0 ");
    assert(L && strstr(L, " eaDir=22.0? eaDirN=3? eaCom=112? eaRes=144? eaCoreN=2? rp=") && strstr(L, " pl=96.0? sp=7.0? gcHeap=360? gcFree=151? gcN=812? rc=388? rcCold=14.0? "));
    assert(strstr(L, " rcBudget=200? rcN=41000? anim=4.0? rest=") && strstr(buf, "diag: mem #2 periodic priv=1851 "));
    const char* T = strstr(L, " t=");
    assert(T && T < strchr(L, '\r') && strchr(L, '\r') - T <= 12);  // seconds since start, last field
    for (const char* e = L; *e != '\n'; e++) assert(e - L < 640);
    const char* G = strstr(buf, "diag: mem fields (docs/190 phase 1;");
    assert(G && G < L && !strstr(G + 1, "diag: mem fields (") && strstr(G, "validated priv wPriv wCom free map stkRes miRes miCom rp; unvalidated big img"));
    g_mdFail = 1 << MD_MAP;  // a failed field is marked '!'
    MdAfterSample("diag: memory at 20 min", NULL, s, 1851ull << 20);
    UdtLog(buf, sizeof(buf), h);
    assert(strstr(buf, "diag: mem #3 periodic ") && strstr(buf, " map=0.0! "));
    g_mdFail = 0;
    MdMark("build enter!");  // no memory worker in the selftest: sampled inline
    UdtLog(buf, sizeof(buf), h);
    assert(strstr(buf, "diag: memory at mark #") && strstr(buf, " mark ev=build_enter_ priv="));
    const bool on = g_mdOn;
    g_mdOn = false;
    MdMark("ignored");
    g_mdOn = on;
    UdtLog(buf, sizeof(buf), h);
    assert(!strstr(buf, "ignored"));

    // module 2 suspect counters: each sample leaves a wanted slot; ts3fixes' answer is logged with that sample's number,
    // tag and event, once; ops 3-5 answer 0 with [MemDiag] off; the self-test is wanted until its PASS/FAIL line
    {
        const LONG mark = g_mdN;  // the mark just above (sampled inline)
        g_msTail = g_msHead - 1;  // the earlier samples above stay unanswered: only the mark is queued
        const auto& q = g_msQ[g_msTail & 7];
        assert(MsOp(3) == 1 && MsOp(4) == 1 && q.n == mark && !strcmp(q.tag, "mark") && !strcmp(q.ev, "build_enter_"));
        MsLine("memsus: cc=1234 ccIcon=1000? ppc=2 rdMs=0.4");
        assert(MsOp(4) == 0);
        UdtLog(buf, sizeof(buf), h);
        char want[96];
        sprintf_s(want, "diag: memsus #%ld mark ev=build_enter_ cc=1234 ccIcon=1000? ppc=2 rdMs=0.4 lag=", mark);
        assert(strstr(buf, want) && strstr(strstr(buf, want), " t="));
        MdAfterSample("diag: memory at 30 min", NULL, s, 1851ull << 20);
        assert(MsOp(4) == 1 && !strcmp(g_msQ[g_msTail & 7].tag, "periodic") && g_msQ[g_msTail & 7].ev[0] == 0);
        MdMark("after");  // a second sample before the answer: both are answered, oldest first (0.36.74 lost the load one)
        MsLine("memsus: cc=1240");
        assert(MsOp(4) == 1);
        MsLine("memsus: cc=1241");
        assert(MsOp(4) == 0);
        UdtLog(buf, sizeof(buf), h);
        sprintf_s(want, "diag: memsus #%ld periodic cc=1240 lag=", g_mdN - 1);
        assert(strstr(buf, want));
        sprintf_s(want, "diag: memsus #%ld mark ev=after cc=1241 lag=", g_mdN);
        assert(strstr(buf, want));
        MsLine("memsus: cc=1");  // nothing wanted: logged as unasked
        UdtLog(buf, sizeof(buf), h);
        assert(strstr(buf, "diag: memsus #0 unasked cc=1 lag="));
        g_msSelfTest = 1;
        assert(MsOp(5) == 1);
        MsLine("memsus selftest cc +1 back 0 ok");
        assert(MsOp(5) == 1);
        MsLine("memsus selftest PASS (cc ppc)");
        assert(MsOp(5) == 0 && g_msSelfTest == 2);
        UdtLog(buf, sizeof(buf), h);
        assert(strstr(buf, "diag: memsus selftest cc +1 back 0 ok") && strstr(buf, "diag: memsus selftest PASS (cc ppc)"));
        const bool udOn = g_udOn;
        g_udOn = true;
        assert(UdTextMsg(0, kUdTextHi | 3u << 16) == 1 && UdTextMsg(0, kUdTextHi | 4u << 16) == 0);  // channel routes ops 3-5
        g_mdOn = false;
        assert(MsOp(3) == 0 && MsOp(4) == 0 && UdTextMsg(0, kUdTextHi | 3u << 16) == 0);
        g_udOn = udOn, g_mdOn = on, g_msSelfTest = 0;
    }

    // the self-test on this process: real allocations, every checked field within tolerance (no EA heap here)
    g_mdEaHeapSlot = &nullP, g_mdRcMgrSlot = &nullP;
    g_mdEaCoreSlot[0] = g_mdEaCoreSlot[1] = &nullP;
    for (int i = 0; i < 3; i++) g_mdGc[i] = &zero[i];
    k.plKB = k.spKB = 0;
    const bool pass = MdSelfTest(false);
    UdtLog(buf, sizeof(buf), h);
    if (!pass) printf("%s\n", strstr(buf, "diag: mem selftest") ? strstr(buf, "diag: mem selftest") : buf);
    assert(pass && strstr(buf, "diag: mem selftest PASS") && !strstr(buf, " FAIL"));
    assert(strstr(buf, "diag: mem selftest thread stack 4 MB reserve stkRes +") && strstr(buf, "diag: mem selftest file mapping 32 MB map view +32.00, after 0.00 (want +32.00 exact); total +32.0"));
    assert(strstr(buf, " selftest ev=selftest-leak-before ") && strstr(buf, " selftest ev=selftest-leak-after "));
    assert(strstr(buf, "diag: mem selftest leak 8 x 1 MB wPriv +8.") && strstr(buf, "diag: mem selftest cross-check: OS private "));
    assert(g_mdPass & (1 << MD_STKRES) && !g_mdFail);

    g_mdOn = wasOn, g_mdLegend = 0, g_mdPass = g_mdFail = 0;
    g_mdEaHeapSlot = savedEa, g_mdRcMgrSlot = savedRc;
    g_mdEaCoreSlot[0] = savedCore[0], g_mdEaCoreSlot[1] = savedCore[1];
    for (int i = 0; i < 3; i++) g_mdGc[i] = savedGc[i];
    k.plKB = savedPl, k.spKB = savedSp;
    g_log = saved;
    CloseHandle(h);
    printf("mem diag: allocation grouping (stacks, arenas), own-view mapped count + verdict, tags + world events, fake EA heap / GC / ResourceCache reads, line "
           "format + marks, self-test on this process (commit, arena, stack, mapping, routesnap heap, leak, OS cross-check) ok\n");
}
