// TestMemDiag ([MemDiag], memdiag.cpp, docs/190 phase 1, docs/191 module 1). Included by selftest.cpp after userdiag-test.h
// (uses its UdtLog). Classifier on synthetic regions, event tags, the reads of the game structures on fakes (offsets), the
// line format and marks, and the self-test itself on this process (every 'H' field must move by the injected size).
struct MdtHold { CRITICAL_SECTION* cs; HANDLE in, out; };
struct MdtHolder { virtual ~MdtHolder() {} const void* res; };  // RcCensusHolders test: an object with a vtable
static MdtHolder g_mdtHolders[2];
static LONG g_mdtRefs;  // RcCensusRefSites test: the "original" AddRef / Release behind the counters
static LONG __fastcall MdtAddRef(void*, void*) { return ++g_mdtRefs; }
static LONG __fastcall MdtRelease(void*, void*) { return --g_mdtRefs; }
static bool __fastcall MdtFind(void*, void*, const void* key, void** out) { *out = (void*)key; return key != NULL; }  // "found" = the key
static DWORD WINAPI MdtHoldLock(LPVOID p) {  // RcCensus test: own the fake cache lock until "out" is set
    MdtHold* h = (MdtHold*)p;
    EnterCriticalSection(h->cs), SetEvent(h->in);
    WaitForSingleObject(h->out, INFINITE);
    LeaveCriticalSection(h->cs);
    return 0;
}
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

    // [MemDiag] RcCensus on a 4-bucket map on the fake ResourceCache: types by count, the refs / cold split, a bad key, the
    // change since the last census; nothing at exit; lock owned by another thread (skipped); a map longer than its count
    // (stopped, lock released)
    {
        static u8 nd[9][0x1C], rs[9][0x28];
        static DWORD bk[5];
        const DWORD ty[9] = {0x00B2D882, 0x00B2D882, 0x00B2D882, 0x015A1849, 0x015A1849, 0x8EAF13DE, 0x8EAF13DE, 0x8EAF13DE, 0x8EAF13DE};
        const LONG refs[9] = {1, 1, 2, 1, 5, 1, 2, 2, 2};
        auto add = [&](int i) {
            *(DWORD*)nd[i] = 100 + i, *(DWORD*)(nd[i] + 8) = ty[i];
            *(DWORD*)(nd[i] + 0x10) = (DWORD)(uintptr_t)rs[i], memcpy(rs[i] + 8, nd[i], 16);
            *(LONG*)(rs[i] + 4) = refs[i] << 8 | 3;     // the low byte is not the count
            *(DWORD*)(rs[i] + 0x1C) = i == 0 || i == 3;  // cold
            *(DWORD*)(rs[i] + 0x20) = (DWORD)(i + 1) << 20;
            *(DWORD*)(nd[i] + 0x18) = bk[i % 4], bk[i % 4] = (DWORD)(uintptr_t)nd[i];
        };
        for (int i = 0; i < 6; i++) add(i);
        bk[4] = ~0u;  // the end sentinel past the bucket count
        CRITICAL_SECTION* cs = (CRITICAL_SECTION*)(rcC + 0x58);
        InitializeCriticalSection(cs);
        rcC[0x7C] = 1;
        *(DWORD*)(rcC + 0x20) = (DWORD)(uintptr_t)bk, *(DWORD*)(rcC + 0x24) = 4, *(DWORD*)(rcC + 0x28) = 6;
        g_rcCensusOn = true, g_rcPrevN = 0, g_mdRcMgrSlot = &mgrP;  // the self-test above unset it
        g_rcHoldersAt = 2, g_rcCensusN = 0;  // holder scan at the second census: the type that grew is 0x8EAF13DE
        RcCensus(11, "load");
        for (int i = 6; i < 9; i++) add(i);
        *(DWORD*)(rs[8] + 8) = 1;  // resource key differs from its node's
        *(DWORD*)(rcC + 0x28) = 9;
        g_mdtHolders[0].res = rs[6], g_mdtHolders[1].res = rs[7];  // two objects with a vtable hold two of its four
        RcCensus(12, "periodic");
        g_mdtHolders[0].res = g_mdtHolders[1].res = NULL;
        RcCensus(13, "exit");
        MdtHold hold = {cs, CreateEventW(NULL, TRUE, FALSE, NULL), CreateEventW(NULL, TRUE, FALSE, NULL)};
        HANDLE th = CreateThread(NULL, 0, MdtHoldLock, &hold, 0, NULL);
        const DWORD held = th ? WaitForSingleObject(hold.in, 5000) : WAIT_FAILED;
        assert(held == WAIT_OBJECT_0);
        g_rcLockWaitMs = 20;
        RcCensus(14, "periodic");
        SetEvent(hold.out), WaitForSingleObject(th, INFINITE);
        CloseHandle(th), CloseHandle(hold.in), CloseHandle(hold.out);
        *(DWORD*)(nd[2] + 0x18) = (DWORD)(uintptr_t)nd[2];  // a cycle in bucket 2
        RcCensus(15, "periodic");
        assert(!cs->OwningThread && !cs->RecursionCount);
        UdtLog(buf, sizeof(buf), h);
        const char* a = strstr(buf, "diag: rc census #11 load entries=6 (map 6) types=3 MB=21.0 cold=2 ref1held=2 keyBad=0 walk=");
        // the first entry walked per type: bucket 0 holds nodes 4, 0 (then 8, 4, 0), bucket 1 holds 5, 1
        const char* a1 = strstr(buf, "diag: rc census #11 type 0x00B2D882 n=3 (+3) MB=6.0 (+6.0) avgKB=2048.0 cold=1 ref1=2 ref2=1 ref3+=0 ref1held=1 g=0x00000000 x3 e.g. 0x0000000000000064\r");
        const char* a2 = strstr(buf, "diag: rc census #11 type 0x015A1849 n=2 (+2) MB=9.0 (+9.0) avgKB=4608.0 cold=1 ref1=1 ref2=0 ref3+=1 ref1held=0 g=0x00000000 x2 e.g. 0x0000000000000068\r");
        const char* a3 = strstr(buf, "diag: rc census #11 type 0x8EAF13DE n=1 (+1) MB=6.0 (+6.0) avgKB=6144.0 cold=0 ref1=1 ref2=0 ref3+=0 ref1held=1 g=0x00000000 x1 e.g. 0x0000000000000069\r");
        assert(a && strstr(a, "; first census, changes are since 0\r") && a < a1 && a1 < a2 && a2 < a3);
        const char* b = strstr(buf, "diag: rc census #12 periodic entries=9 (map 9) types=3 MB=45.0 cold=2 ref1held=2 keyBad=1 walk=");
        const char* b1 = strstr(buf, "diag: rc census #12 type 0x8EAF13DE n=4 (+3) MB=30.0 (+24.0) avgKB=7680.0 cold=0 ref1=1 ref2=3 ref3+=0 ref1held=1 g=0x00000000 x4 e.g. 0x000000000000006C\r");
        const char* b2 = strstr(buf, "diag: rc census #12 type 0x00B2D882 n=3 (+0) MB=6.0 (+0.0) ");
        assert(b && b < b1 && b1 < b2 && strstr(b, " ms\r") == strchr(b, '\r') - 3);
        const char* hh = strstr(buf, "diag: rc holders #12 type 0x8EAF13DE: 4 sampled (1 in 1 of 4), scanned ");
        char want[96];
        sprintf_s(want, "diag: rc holders #12 vt 0x%08lX +0x4 delta +0 n=2 covers 2 of 4\r", *(const DWORD*)&g_mdtHolders[0]);
        assert(hh && b2 < hh && strstr(hh, want) && strstr(hh, " + 4 cache nodes; no vtable ") && !strstr(buf, "rc holders #11") && !strstr(buf, "rc holders #15"));
        assert(!strstr(buf, "rc census #13") && !strstr(buf, "census #11 grow") && strstr(buf, "diag: rc census #14 periodic: skipped, cache lock busy for "));
        assert(strstr(buf, "diag: rc census #15 periodic: map not readable (stopped after 75 of 9 entries, "));
        // RcCensusBuilders: one fake SimBuilder with 2 parts holding 3 + 5 compositors, one with a broken list (not valid)
        static u8 fb[2][0x400], pn[3][0x98];
        const DWORD savedVt = g_rbVt, savedName = g_rbName;
        g_rbVt = 0x7EB0B17D, g_rbName = 0x7EB0B17E;
        for (int i = 0; i < 2; i++) *(DWORD*)fb[i] = g_rbVt, *(DWORD*)(fb[i] + 0x1E8) = g_rbName;
        u8* hd = fb[0] + 0x1E0;
        *(u8**)hd = pn[0], *(u8**)(hd + 4) = pn[1];
        *(u8**)pn[0] = pn[1], *(u8**)(pn[0] + 4) = hd, *(DWORD*)(pn[0] + 0x84) = 3;
        *(u8**)pn[1] = hd, *(u8**)(pn[1] + 4) = pn[0], *(DWORD*)(pn[1] + 0x84) = 5;
        *(u8**)(fb[1] + 0x1E0) = pn[2], *(u8**)(pn[2] + 4) = pn[0];  // prev does not point back
        RcBuilders(16);
        UdtLog(buf, sizeof(buf), h);
        const char* rb = strstr(buf, "diag: rc builders #16: 1 SimBuilders, 2 parts, 8 compositors; most: 8 in 2 parts; ");
        assert(rb && strstr(rb, " not valid; scanned ") && strtoul(strstr(rb, "parts; ") + 7, NULL, 10) >= 1);
        g_rbVt = savedVt, g_rbName = savedName;
        // RcCensusGraph: A <-> B (B keeps another interface of A), D (held by someone else too) -> E + a null + a foreign
        // pointer, C alone, F with a broken vector; listed out of order
        {
            static DWORD go[6][RG_SIZE / 4], kA[1], kB[1], kD[3], gl[6], gd[6], gq[6];
            static u8 gm[6];
            enum { A, B, C, D, E, F };
            const LONG gr[6] = {2, 2, 1, 3, 2, 1};
            for (int i = 0; i < 6; i++) go[i][1] = (DWORD)gr[i] << 8;
            const DWORD at[6] = {(DWORD)(uintptr_t)go[A], (DWORD)(uintptr_t)go[B], 0, 0, (DWORD)(uintptr_t)go[E], 0};
            kA[0] = at[B], kB[0] = at[A] + 0x10, kD[0] = at[E], kD[1] = 0, kD[2] = 0x10;
            go[A][10] = (DWORD)(uintptr_t)kA, go[A][11] = (DWORD)(uintptr_t)(kA + 1);
            go[B][10] = (DWORD)(uintptr_t)kB, go[B][11] = (DWORD)(uintptr_t)(kB + 1);
            go[D][10] = (DWORD)(uintptr_t)kD, go[D][11] = (DWORD)(uintptr_t)(kD + 3);
            go[F][10] = (DWORD)(uintptr_t)(kA + 1), go[F][11] = (DWORD)(uintptr_t)kA;
            const int order[6] = {D, A, F, E, C, B};
            for (int i = 0; i < 6; i++) gl[i] = (DWORD)(uintptr_t)go[order[i]];
            DWORD* const sv[4] = {g_rgObj, g_rgDeg, g_rgQ, (DWORD*)g_rgMore};
            const DWORD svN = g_rgN, svOver = g_rgOver;
            g_rgObj = gl, g_rgDeg = gd, g_rgQ = gq, g_rgMore = gm, g_rgN = 6, g_rgOver = 0;
            RcGraph(19);
            g_rgObj = sv[0], g_rgDeg = sv[1], g_rgQ = sv[2], g_rgMore = (u8*)sv[3], g_rgN = svN, g_rgOver = svOver;
            UdtLog(buf, sizeof(buf), h);
            assert(strstr(buf, "diag: rc graph #19: 6 part compositors, 3 with children; 5 child refs: 3 to listed compositors (0 self), "
                               "1 to others, 1 null; 1 vectors not readable; beyond the cache and compositor parents: 1 held more (+2), "
                               "0 fewer; 3 roots (refs 1: 2, 2: 0, 3+: 1); the peel from the roots leaves 2 on or below a cycle "
                               "(0 of them held more); "));
        }
        // RcRefCallers: two fake vtables; a wrong slot refuses; 3 AddRef from one site + 1 from another, 1 Release, 2 finds
        static DWORD fv[2][2] = {{(DWORD)(uintptr_t)&MdtAddRef, (DWORD)(uintptr_t)&MdtRelease}, {(DWORD)(uintptr_t)&MdtAddRef, 0}};
        const DWORD rsVt[2] = {g_rsVt[0], g_rsVt[1]}, rsOrig[2] = {g_rsOrig[0], g_rsOrig[1]};
        const RsFn rsAdd = g_rsAdd, rsRel = g_rsRel;
        const RfFn fsFind = g_rfFind;
        const DWORD fsVt = g_rfVt, fsOrig = g_rfOrig;
        static DWORD fc[8];  // a fake cache vtable: slot 6 = Find
        fc[6] = (DWORD)(uintptr_t)&MdtFind;
        g_rfVt = (DWORD)(uintptr_t)fc, g_rfOrig = fc[6], g_rfFind = &MdtFind;
        g_rsVt[0] = (DWORD)(uintptr_t)fv[0], g_rsVt[1] = (DWORD)(uintptr_t)fv[1];
        g_rsOrig[0] = fv[0][0], g_rsOrig[1] = fv[0][1];
        assert(!RsInstall() && fv[0][0] == (DWORD)(uintptr_t)&MdtAddRef);  // fv[1][1] is not the expected Release
        UdtLog(buf, sizeof(buf), h);
        assert(strstr(buf, "FAIL mem diag: rc ref callers: vtable 0x"));
        fv[1][1] = fv[0][1];
        g_rsAdd = (RsFn)&MdtAddRef, g_rsRel = (RsFn)&MdtRelease;
        g_ryOn = true, g_ryEvery = 1;  // the cost measure includes the sampling path
        assert(RsInstall() && fv[0][0] == (DWORD)(uintptr_t)&RsAddRef && fv[1][1] == (DWORD)(uintptr_t)&RsRelease &&
               fc[6] == (DWORD)(uintptr_t)&RsFind);
        g_ryOn = false;
        assert(g_rsNs > 0 && g_rsNs < 2000 && g_rsChainNs > 0 && !g_rsAt[0] && !g_rsFs[0].h && !g_rfHits);  // measured, tables cleared
        printf("rc ref callers: our work %.1f ns a hooked call, %.0f ns a find chain\n", g_rsNs, g_rsChainNs);
        RcRefCallers(17);  // baseline: nothing counted yet
        for (volatile int i = 0; i < 3; i++) ((RsFn)(uintptr_t)fv[0][0])(NULL, NULL);  // volatile: one call site, not unrolled
        ((RsFn)(uintptr_t)fv[1][0])(NULL, NULL);
        assert(((RsFn)(uintptr_t)fv[1][1])(NULL, NULL) == 3 && g_mdtRefs == 3);
        // finds: 2 return a compositor (an object whose vtable is fv[0]), 1 another object, 1 nothing
        const DWORD objA = g_rsVt[0], objB = 0x12345678;
        void* out = NULL;
        for (volatile int i = 0; i < 2; i++) assert(((RfFn)(uintptr_t)fc[6])(NULL, NULL, &objA, &out) && out == &objA);
        assert(((RfFn)(uintptr_t)fc[6])(NULL, NULL, &objB, &out) && out == &objB);
        assert(!((RfFn)(uintptr_t)fc[6])(NULL, NULL, NULL, &out));
        RcRefCallers(18);
        UdtLog(buf, sizeof(buf), h);
        assert(strstr(buf, "diag: rc ref callers #17: since the start AddRef 0, finds 0, Release 0, net +0; since the last line AddRef +0, "
                           "finds +0, Release +0, net +0; 0 callers; our hook work ~0.0 ms"));
        assert(strstr(buf, "diag: rc find sites #17: +0 finds by key returned a part compositor since the last line; 0 chains"));
        const char* rsLine = strstr(buf, "diag: rc ref callers #18: since the start AddRef 4, finds 2, Release 1, net +5; since the last line "
                                         "AddRef +4, finds +2, Release +1, net +5; 4 callers; ");
        assert(rsLine);
        const char* r1 = strstr(rsLine, " net +3 (+3): AddRef 3, finds 0, Release 0");
        const char* r2 = strstr(rsLine, " net +2 (+2): AddRef 0, finds 2, Release 0");
        const char* r3 = strstr(rsLine, " net +1 (+1): AddRef 1, finds 0, Release 0");
        const char* r4 = strstr(rsLine, " net -1 (-1): AddRef 0, finds 0, Release 1");
        assert(r1 && r2 && r3 && r4 && r1 < r2 && r2 < r3 && r3 < r4);  // by net, then the most negative
        const char* fsLine = strstr(buf, "diag: rc find sites #18: +2 finds by key returned a part compositor since the last line; ");
        assert(fsLine && strstr(fsLine, "diag: rc find sites #18 +") && strstr(fsLine, ") from 0x"));
        assert(ts3native_counters.rrcAddRefs == 4 && ts3native_counters.rrcReleases == 1 && ts3native_counters.rrcFinds == 2 &&
               ts3native_counters.rrcCallers == 4);
        // RcRefCallersSample: every new compositor sampled; o1 lives at 2 refs (3 AddRef, 1 Release, 1 Find), o2 is freed,
        // o3 is net +1 (2 AddRef, 1 Release; refs 2 with the uncounted cache reference) and in the fake registry
        {
            static DWORD o1[4], o2[4], o3[4];
            o1[0] = o2[0] = o3[0] = g_rsVt[0];
            g_ryOn = true, g_ryEvery = 1;
            const RsFn ryAdd = (RsFn)(uintptr_t)fv[0][0], ryRel = (RsFn)(uintptr_t)fv[0][1];
            ryAdd(o1, NULL), o1[1] = 1 << 8;
            ryAdd(o1, NULL), o1[1] = 2 << 8;
            ryAdd(o1, NULL), o1[1] = 3 << 8;
            ryRel(o1, NULL), o1[1] = 2 << 8;
            void* ro = NULL;
            assert(((RfFn)(uintptr_t)fc[6])(NULL, NULL, o1, &ro) && ro == o1);
            ryAdd(o2, NULL), o2[1] = 1 << 8;
            ryRel(o2, NULL);
            ryAdd(o3, NULL), o3[1] = 1 << 8;
            ryAdd(o3, NULL), o3[1] = 2 << 8;
            ryRel(o3, NULL), o3[1] = 2 << 8;  // the cache's direct AddRef in between, not seen
            // the registry: 2 buckets, one chain of o3 + a node that is not a compositor (next at +0x18)
            static u8 svc[0x114], node[2][0x1C];
            static DWORD bkt[2], svcSlot;
            *(DWORD*)(node[0] + 0x10) = (DWORD)(uintptr_t)o3, *(DWORD*)(node[0] + 0x18) = (DWORD)(uintptr_t)node[1];
            *(DWORD*)(node[1] + 0x10) = (DWORD)(uintptr_t)&objB;
            bkt[0] = (DWORD)(uintptr_t)node[0];
            *(DWORD*)(svc + 0x108) = (DWORD)(uintptr_t)bkt, *(DWORD*)(svc + 0x10C) = 2, *(DWORD*)(svc + 0x110) = 2;
            svcSlot = (DWORD)(uintptr_t)svc;
            const DWORD rrSvc = g_rrSvc;
            g_rrSvc = (DWORD)(uintptr_t)&svcSlot;
            g_rsOn = true;
            RcRefCallersLine(19, "selftest");
            g_rsOn = false, g_rrSvc = rrSvc, g_rrPrev = 0;
            UdtLog(buf, sizeof(buf), h);
            assert(strstr(buf, "diag: rc registry #19: 2 entries (+2 since the last line, 2 buckets), walked 2: 1 part compositors: refs 1: 0, "
                               "2: 1, 3+: 0; 1 sampled ones"));
            const char* ry = strstr(buf, "diag: rc ref history #19: 3 sampled (1 in 1 new, 0 not: table full), 1 freed, 0 too young, 0 not a "
                                         "compositor now; 2 alive: refs 1: 0, 2: 2, 3+: 0; 0 with more than 16 events, 0 in no listed group");
            const char* ryp = strstr(buf, "diag: rc ref history #19 x1 refs 2: A");
            assert(ry && ryp && strstr(ryp, " R") && strstr(ryp, " F") && strchr(ryp, '\r') - ryp > 40);
            const char* ryn = strstr(buf, "diag: rc ref history #19: 1 alive at net +1 (counted AddRef + finds - Release; the cache's own "
                                          "reference is not counted), 1 of them in the compositor registry; 0 in no listed group");
            assert(ryn && strstr(ryn, "diag: rc ref history #19 net+1 x1 last A") && strstr(ryn, ", in the registry 1"));
            assert(ts3native_counters.rrcRegistry == 2 && ts3native_counters.rrcSampled == 3);
            memset(g_ry, 0, sizeof(g_ry)), g_ryNew = g_ryTaken = g_ryFull = g_ryCensus = 0, g_ryOn = false, g_ryEvery = 32;
        }
        // RcRefTypes: by-type counts, births (AddRef at 0 refs), deaths (Release returning 0), the locked walk's live
        // count and histogram by vtable, a key-type entry of another class, top callers; a second line's deltas; exit is silent
        {
            static u8 t0[0x24], t1[0x24], t2[0x24], t3[0x24], tn[4][0x1C];
            static DWORD tb[3];
            *(DWORD*)t0 = *(DWORD*)t3 = g_rsVt[0], *(DWORD*)t1 = g_rsVt[1], *(DWORD*)t2 = 0x12345678;
            *(LONG*)(t0 + 4) = 0, *(LONG*)(t1 + 4) = 2 << 8 | 1, *(LONG*)(t2 + 4) = 1 << 8, *(LONG*)(t3 + 4) = 9 << 8;
            *(DWORD*)(t3 + 0x18) = 1;  // cold
            memset((void*)g_rqN, 0, sizeof(g_rqN)), g_rqBorn[0] = g_rqBorn[1] = g_rqDied[0] = g_rqDied[1] = 0;
            g_rqOn = true;
            const RsFn add0 = (RsFn)(uintptr_t)fv[0][0], rel1 = (RsFn)(uintptr_t)fv[1][1];
            add0(t0, NULL);              // type 0, a birth
            *(LONG*)(t0 + 4) = 1 << 8;
            add0(t0, NULL);              // type 0, refs 1: no birth
            add0(t1, NULL);              // type 1 (the vtable decides, not the slot)
            g_mdtRefs = 1, rel1(t1, NULL);  // returns 0: a death of type 1
            g_mdtRefs = 5, rel1(t1, NULL);  // returns 4: no death
            void* ro = NULL;
            assert(((RfFn)(uintptr_t)fc[6])(NULL, NULL, t1, &ro) && ro == t1);  // a find of type 1
            assert(g_rqBorn[0] == 1 && g_rqBorn[1] == 0 && g_rqDied[1] == 1 && g_rqDied[0] == 0);
            // the cache: t0 (refs 1), t1 (refs 2), t3 (refs 9, cold) by vtable; t2 is a 0x033A1435 key of another class
            const u8* rsx[4] = {t0, t1, t2, t3};
            const DWORD kt[4] = {0x033A1435, 0x0341ACC9, 0x033A1435, 0x033A1435};
            tb[0] = tb[1] = 0, tb[2] = ~0u;
            for (int i = 0; i < 4; i++) {
                memset(tn[i], 0, sizeof(tn[i]));
                *(DWORD*)(tn[i] + 8) = kt[i], *(DWORD*)(tn[i] + 0x10) = (DWORD)(uintptr_t)rsx[i];
                *(DWORD*)(tn[i] + 0x18) = tb[i & 1], tb[i & 1] = (DWORD)(uintptr_t)tn[i];
            }
            *(DWORD*)(rcC + 0x20) = (DWORD)(uintptr_t)tb, *(DWORD*)(rcC + 0x24) = 2, *(DWORD*)(rcC + 0x28) = 4, rcC[0x7C] = 1;
            RcRefTypesLine(20, "mark", "cas-close");
            UdtLog(buf, sizeof(buf), h);
            const char* rt = strstr(buf, "diag: rc types #1 mark ev=cas-close mem=#20: walk 4 entries in ");
            assert(rt && strstr(rt, ", wait 0 ms; caller table ok; t="));
            assert(strstr(rt, "diag: rc types #1 type 0x033A1435 vt 0x") && strstr(rt, " live 2 (+2) cold 1 same key type, other class 1; "
                              "refs 0:0 1:1 2:0 3:0 4:0 5-8:0 9-16:1 17+:0; births seen 1 (+1), deaths 0 (+0); AddRef 2, finds 0, Release 0"));
            assert(strstr(rt, " live 1 (+1) cold 0 same key type, other class 0; refs 0:0 1:0 2:1 3:0 4:0 5-8:0 9-16:0 17+:0; "
                              "births seen 0 (+0), deaths 1 (+1); AddRef 1, finds 1, Release 2"));
            const char* ta = strstr(rt, "diag: rc types #1 type 0x033A1435 AddRef callers 2, top (+since the last line/total): 0x");
            assert(ta && strstr(ta, " +1/1, 0x"));  // two call sites, one each
            assert(strstr(rt, "diag: rc types #1 type 0x033A1435 Release callers 0, top (+since the last line/total): none"));
            const char* tr = strstr(rt, "diag: rc types #1 type 0x0341ACC9 Release callers 2, top (+since the last line/total): 0x");
            assert(tr && strstr(rt, "diag: rc types #1 type 0x0341ACC9 find callers 1, top (+since the last line/total): 0x"));
            add0(t0, NULL);
            RcRefTypesLine(21, "periodic", NULL);
            RcRefTypesLine(22, "exit", NULL);
            UdtLog(buf, sizeof(buf), h);
            rt = strstr(buf, "diag: rc types #2 periodic mem=#21: walk 4 entries");
            assert(rt && strstr(rt, " live 2 (+0) cold 1") && strstr(rt, "births seen 1 (+0), deaths 0 (+0); AddRef 3, finds 0, Release 0"));
            const char* ta2 = strstr(rt, "diag: rc types #2 type 0x033A1435 AddRef callers 3, top (+since the last line/total): 0x");
            assert(ta2 && strstr(ta2, " +1/1, 0x") && strstr(ta2, " +0/1, 0x") && !strstr(buf, "diag: rc types #3"));
            // the timer: off without a period, due after one
            g_rqPeriodMs = 0;
            assert(RqWorkerPoll() == INFINITE);
            const LONG worlds = g_udWorlds;
            g_udWorlds = 1, g_rqPeriodMs = 5000, g_rqLast = (GetTickCount() - 6000) | 1;
            assert(RqWorkerPoll() == 5000);
            UdtLog(buf, sizeof(buf), h);
            assert(strstr(buf, "diag: rc types #3 timer mem=#"));
            assert(RqWorkerPoll() > 4000);
            g_udWorlds = worlds, g_rqPeriodMs = 0, g_rqLast = 0, g_rqOn = false, g_rqSeq = 0, rcC[0x7C] = 0;
            memset((void*)g_rqN, 0, sizeof(g_rqN)), memset(g_rqPrev, 0, sizeof(g_rqPrev)), g_rqBorn[0] = g_rqBorn[1] = g_rqDied[0] = g_rqDied[1] = 0;
            g_rqPrevBorn[0] = g_rqPrevBorn[1] = g_rqPrevDied[0] = g_rqPrevDied[1] = g_rqPrevLive[0] = g_rqPrevLive[1] = 0;
        }
        memset((void*)g_rsAt, 0, sizeof(g_rsAt)), memset((void*)g_rsN, 0, sizeof(g_rsN)), memset(g_rsPrev, 0, sizeof(g_rsPrev));
        memset(g_rsPrevNet, 0, sizeof(g_rsPrevNet)), g_rsPrevMs = 0;
        memset(g_rsFs, 0, sizeof(g_rsFs)), memset(g_rfPrev, 0, sizeof(g_rfPrev)), g_rfHits = g_rfPrevHits = 0;
        g_rsVt[0] = rsVt[0], g_rsVt[1] = rsVt[1], g_rsOrig[0] = rsOrig[0], g_rsOrig[1] = rsOrig[1], g_rsAdd = rsAdd, g_rsRel = rsRel;
        g_rfVt = fsVt, g_rfOrig = fsOrig, g_rfFind = fsFind;
        memset(&ts3native_counters.rrcOn, 0, 8 * sizeof(LONG));
        DeleteCriticalSection(cs);
        *(DWORD*)(rcC + 0x20) = *(DWORD*)(rcC + 0x24) = 0, *(DWORD*)(rcC + 0x28) = 41000, rcC[0x7C] = 0;
        g_rcCensusOn = false, g_rcPrevN = 0, g_rcLockWaitMs = 500, g_rcHoldersAt = g_rcCensusN = 0;
    }

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
