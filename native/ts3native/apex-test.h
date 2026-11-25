// TestApex ([Compat] ApexStandDown, apex.cpp, docs/148 §7). Included by selftest.cpp. No Apex binary is used: a loaded
// system DLL stands in for the module, the test creates the instance mutex itself.
static volatile LONG g_axNext, g_axApex;
static LONG WINAPI AxNext(EXCEPTION_POINTERS*) { g_axNext++; return EXCEPTION_EXECUTE_HANDLER; }
static LONG WINAPI AxApex(EXCEPTION_POINTERS* e) { g_axApex++; return CrFilter(e); }  // Apex's filter: report, then the previous one

static void TestApex() {
    const wchar_t* mods0 = g_apexModules[0];
    const wchar_t* mods1 = g_apexModules[1];
    // 1. detection: nothing / module / renamed module / mutex
    g_apexModules[0] = L"ts3n-no-such-apex.asi", g_apexModules[1] = L"ts3n-no-such-zzapex.asi";
    assert(ApexProbe() == 0);
    g_apexModules[0] = L"kernel32.dll";
    assert(ApexProbe() == 1);
    g_apexModules[0] = L"ts3n-no-such-apex.asi", g_apexModules[1] = L"kernel32.dll";
    assert(ApexProbe() == 2);
    g_apexModules[1] = L"ts3n-no-such-zzapex.asi";
    wchar_t m[64];
    swprintf_s(m, L"Local\\ApexRadiance.%lu", GetCurrentProcessId());
    HANDLE mx = CreateMutexW(NULL, FALSE, m);
    assert(mx && ApexProbe() == 4);
    CloseHandle(mx);
    assert(ApexProbe() == 0);
    g_apexModules[0] = mods0, g_apexModules[1] = mods1;

    // 2. config: defaults (lot group off), explicit false/true, CRLF, other tables, a longer name with the same prefix
    const int defs = AXF_OBJECT_INDEX | AXF_FAST_CACHE | AXF_MEMORY_GUARD | AXF_RES_LOOKUP | AXF_FILE_LIST | AXF_FAST_MEMORY;
    assert(ApexSwitchesOn(NULL) == defs && ApexSwitchesOn("") == defs);
    assert(ApexSwitchesOn("[patches.FastCacheCompression]\r\nenabled = false\r\n[patches.LotObjectThrottle]\r\nenabled=true\r\n") ==
           ((defs & ~AXF_FAST_CACHE) | AXF_LOT));
    assert(ApexSwitchesOn("[patches.AmbientOcclusion]\nenabled = false\n[patches.MemoryGuardX]\nenabled = false\n") == defs);
    assert(ApexSwitchesOn("[patches.MemoryGuard]\n  enabled = false # off\n[patches.ObjectLookupIndex]\nenabled = true\n") ==
           (defs & ~AXF_MEMORY_GUARD));
    assert(ApexSwitchesOn("[patches.ObjectLookupIndex]\n[other]\nenabled = false\n") == defs);  // key under another table
    assert(ApexSwitchesOn("[patches.FastMemory]\nenabled = false\n") == (defs & ~AXF_FAST_MEMORY));

    // 3. stand-down decisions
    const bool sd0 = g_apexStandDown;
    const int f0 = g_apexFound, on0 = g_apexOn;
    const LONG st0 = g_apexStood;
    g_apexFound = 0, g_apexOn = defs | AXF_LOT, g_apexStandDown = true;
    assert(!ApexStandsDown(AXF_OBJECT_INDEX, "[IdCache]", "t"));  // Apex absent
    g_apexFound = 1, g_apexStandDown = false;
    assert(!ApexStandsDown(AXF_OBJECT_INDEX, "[IdCache]", "t"));  // ApexStandDown=0
    g_apexStandDown = true;
    assert(ApexStandsDown(AXF_OBJECT_INDEX, "[IdCache]", "t") && ApexStandsDown(AXF_FAST_CACHE, "[SaveCompress]", "t") &&
           ApexStandsDown(AXF_MEMORY_GUARD, "[FootprintIndex]", "t") && ApexStandsDown(AXF_LOT, "[LotStream]", "t"));
    assert(g_apexStood == st0 + 4);
    g_apexOn = defs & ~AXF_FAST_CACHE;
    assert(!ApexStandsDown(AXF_FAST_CACHE, "[SaveCompress]", "t") && !ApexStandsDown(AXF_LOT, "[LotStream]", "t"));
    // the module entries: each returns before it writes anything, with a temp ini that turns it on
    wchar_t ini[MAX_PATH];
    GetTempPathW(MAX_PATH, ini);
    wcscat_s(ini, L"ts3n-selftest-apex.ini");
    for (const wchar_t* sec : {L"IdCache", L"SaveCompress", L"FootprintIndex", L"LotStream", L"Alloc"}) WritePrivateProfileStringW(sec, L"Enabled", L"1", ini);
    g_apexOn = defs | AXF_LOT;
    const LONG st1 = g_apexStood;
    assert(!ApplyIdCache(ini));
    assert(!ApplySaveCompress(ini));
    assert(!ApplyFootprintIndex(ini));
    assert(!ApplyLotStream(ini));
    assert(!ApplyAlloc(ini) && !ts3native_counters.allocReady);  // FastMemory owns the EA allocator (docs/148 §6 #9)
    assert(g_apexStood == st1 + 5 && ts3native_counters.apStood == g_apexStood);
    DeleteFileW(ini);
    // [Verify] probe sites by owner
    assert(ApexVerifySite(0x939100) == AXF_OBJECT_INDEX && ApexVerifySite(0x939170) == AXF_OBJECT_INDEX && ApexVerifySite(0xC60D30) == AXF_OBJECT_INDEX);
    assert(ApexVerifySite(0x4AFFC0) == AXF_RES_LOOKUP && ApexVerifySite(0x4B1AE0) == AXF_FILE_LIST && ApexVerifySite(0x4B2D00) == (AXF_RES_LOOKUP | AXF_FILE_LIST));
    assert(ApexVerifySite(0xABFAC0) == AXF_LOT && ApexVerifySite(0x7D2DB0) == AXF_LOT && ApexVerifySite(0x82C6E0) == 0);
    g_apexOn = AXF_FILE_LIST;  // ResourceLookupCache off, FileListCache on: RegisterDatabase still stands down
    assert(ApexStandsDown(ApexVerifySite(0x4B2D00), "[Verify] probe registerDb", "t") && !ApexStandsDown(ApexVerifySite(0x4AFFC0), "[Verify] probe pkgSource", "t"));

    // 4. SaveWrite coexistence: start = five sites (no 0xAB12AF) with Apex Room to Save, six without
    CallSite sites[8];
    assert(SaveSitesFor(false, sites) == 6);
    assert(SaveSitesFor(true, sites) == 5);
    for (int i = 0; i < 5; i++) assert(sites[i].site != SAVE_WRITE_SITE && sites[i].hook != (void*)SaveWriteHook);
    // late: our retarget is given back to the game's target once; a call that is not ours is left alone
    u8* code = (u8*)VirtualAlloc(NULL, 64, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    const DWORD site = (DWORD)(uintptr_t)code + 3;  // rel32 at +4: aligned, as 0xAB12B0
    code[3] = 0xE8;
    *(LONG*)(code + 4) = (LONG)((uintptr_t)SaveWriteHook - (site + 5));
    const LONG ready0 = ts3native_counters.saveReady;
    ts3native_counters.saveReady = 0;
    assert(!SaveWriteYieldToApex(site));  // [SaveWrite] not installed
    ts3native_counters.saveReady = 1;
    assert(SaveWriteYieldToApex(site) && site + 5 + *(LONG*)(code + 4) == SAVE_WRITE);
    assert(!SaveWriteYieldToApex(site) && site + 5 + *(LONG*)(code + 4) == SAVE_WRITE);
    ts3native_counters.saveReady = ready0;
    VirtualFree(code, 0, MEM_RELEASE);

    // 5. crash filter chain with Apex on top (its Refresh keeps ours as its previous): Apex -> ours -> next, each once;
    // a cycle (ours -> Apex -> ours) ends instead of recursing; ours as its own next is skipped
    const LPTOP_LEVEL_EXCEPTION_FILTER prev0 = g_crPrev;
    const LONG busy0 = g_crBusy;
    g_crBusy = 1;  // no report file here (TestCrashReport covers it)
    EXCEPTION_RECORD rec = {};
    CONTEXT ctx = {};
    EXCEPTION_POINTERS ep = {&rec, &ctx};
    g_crPrev = AxNext, g_axNext = 0, g_axApex = 0;
    assert(AxApex(&ep) == EXCEPTION_EXECUTE_HANDLER && g_axApex == 1 && g_axNext == 1);
    g_crPrev = AxApex, g_axApex = 0;
    assert(CrFilter(&ep) == EXCEPTION_CONTINUE_SEARCH && g_axApex == 1 && t_crDepth == 0);
    g_crPrev = CrFilter;
    assert(CrFilter(&ep) == EXCEPTION_CONTINUE_SEARCH);
    g_crPrev = prev0, g_crBusy = busy0;

    g_apexStandDown = sd0, g_apexFound = f0, g_apexOn = on0;
    puts("apex compat ok");
}
