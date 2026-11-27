// TestHeapCall: [Crash] HeapCall detector (heapcall.cpp, docs/51 §948) on fabricated exception records, as XcptLog passes them.
static char g_hctLines[8][2048];
static int g_hctN;
static bool HctPre(const char* s, const char* p) { return !strncmp(s, p, strlen(p)); }
// on a miss: the captured lines first, then the assert
#define HCT(c) do { if (!(c)) { for (int i_ = 0; i_ < g_hctN; i_++) printf("hct %d: %s\n", i_, g_hctLines[i_]); assert(c); } } while (0)
static void HctTap(const char* s) { if (g_hctN < 8) SCpy(g_hctLines[g_hctN++], s); }
static __declspec(noinline) DWORD HctRet() { return (DWORD)(uintptr_t)_ReturnAddress(); }  // a return address right after a call

static void TestHeapCall() {
    // a heap page with "path" bytes (the m1124c vtable slot), an object whose vtable is that word, a one-page stack with one
    // return address into this image (CrStack stops at the page end)
    u8* const heap = (u8*)VirtualAlloc(NULL, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    DWORD* const obj = (DWORD*)VirtualAlloc(NULL, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    DWORD* const stk = (DWORD*)VirtualAlloc(NULL, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    assert(heap && obj && stk);
    memcpy(heap + 0x170, "path", 4);
    obj[0] = 0x68746170;
    const DWORD ret = HctRet();
    stk[5] = ret;
    CONTEXT c = {};
    c.Eip = (DWORD)(uintptr_t)(heap + 0x182), c.Ecx = c.Ebx = (DWORD)(uintptr_t)obj, c.Esi = 0x0114C900, c.Edi = 0x40000000;
    c.Eax = 0x68746170, c.Esp = (DWORD)(uintptr_t)stk;
    EXCEPTION_RECORD r = {};
    r.ExceptionCode = EXCEPTION_ACCESS_VIOLATION, r.NumberParameters = 2, r.ExceptionInformation[0] = 1;
    EXCEPTION_POINTERS x = {&r, &c};
    void (*const tap)(const char*) = g_logTap;
    const bool on = g_hcdOn;
    const LONG n0 = g_hcdN, w0 = g_hcdWildN, d0 = g_hcdDumpN, av0 = ts3native_counters.hcdAv656277;
    const LONG* const sn0 = g_hcdSectN;
    const DWORD* const ss0 = g_hcdSects;
    const DWORD ae0 = g_hcdAvEip, am0 = g_hcdAliveMs;
    // one fake Boehm section over the heap page (GC_n_heap_sects = 1, GC_heap_sects[0] = {heap, 4096}); no alive timer here
    static LONG sectN = 1;
    static DWORD sects[2];
    sects[0] = (DWORD)(uintptr_t)heap, sects[1] = 4096;
    g_hcdSectN = &sectN, g_hcdSects = sects, g_hcdAliveMs = 0, g_hcdAvEip = 0;
    g_logTap = HctTap, g_hctN = 0, g_hcdN = 0, g_hcdWildN = 0, g_hcdDumpN = 0, g_hcdDumpBusy = 0;

    g_hcdOn = false;  // [Crash] HeapCall=0: nothing
    HcdOnXcpt(&x);
    HCT(g_hctN == 0);
    g_hcdOn = true;
    ts3native_reprostep = 7;
    HcdOnXcpt(&x);  // heap eip: context line + stack line
    HCT(g_hctN == 2 && g_hcdN == 1);
    const char* l = g_hctLines[0];
    HCT(HctPre(l, "heapcall: #1 code C0000005 (info 00000001 00000000) eip ") && strstr(l, "[heap ") && strstr(l, " step 7;"));
    HCT(strstr(l, "bytes at eip: 00 00") && strstr(l, "; ecx ") && strstr(l, "-> 68746170 [") && strstr(l, "\"path\"]"));
    HCT(strstr(l, "; esi 0114C900 [") && strstr(l, "; eax 68746170 [") && strstr(l, "ebx ") && !strstr(l, " wild"));
    HCT(HctPre(g_hctLines[1], "  heapcall stack (1 return addresses from esp): selftest+0x"));
    HCT(strstr(l, "] in GC heap sect 0 thread "));
    HCT(g_hcdTid[0][0] == GetCurrentThreadId());
    HcdAlive(1);  // what the timer runs 3 s later: this thread still runs, no crash report
    HCT(g_hctN == 3 && HctPre(g_hctLines[2], "  heapcall #1: handled, continued (process alive 0 ms later, no crash report; thread ") &&
        strstr(g_hctLines[2], " still runs)"));
    g_hctN = 2;
    sects[1] = 0x100;  // eip heap+0x182 past the section: no tag
    g_hcdN = 0;
    HcdOnXcpt(&x);
    HCT(g_hctN == 4 && !strstr(g_hctLines[2], "in GC heap"));
    g_hctN = 2, g_hcdN = 1;

    c.Eip = ret;  // eip inside an image, an AV: not ours to log; counted when it is the 0x656277 site
    HcdOnXcpt(&x);
    HCT(g_hctN == 2 && g_hcdN == 1 && ts3native_counters.hcdAv656277 == av0);
    g_hcdAvEip = ret;
    HcdOnXcpt(&x);
    HCT(g_hctN == 2 && g_hcdN == 1 && ts3native_counters.hcdAv656277 == av0 + 1);
    g_hcdAvEip = 0;
    g_hcdN = g_hcdMax;  // rate limit
    c.Eip = (DWORD)(uintptr_t)(heap + 0x10);
    HcdOnXcpt(&x);
    HCT(g_hctN == 2);

    // C000001D inside an image: logged as wild (own cap), with a minidump into the log folder
    wchar_t tmp[MAX_PATH];
    const DWORD tl = GetTempPathW(MAX_PATH, tmp);
    HCT(tl > 1 && tl < MAX_PATH);
    tmp[tl - 1] = 0;  // no trailing backslash, as g_hcdDir
    WCpy(g_hcdDir, tmp);
    r.ExceptionCode = EXCEPTION_ILLEGAL_INSTRUCTION, r.NumberParameters = 0, c.Eip = ret;
    HcdOnXcpt(&x);
    HCT(g_hctN == 5 && g_hcdWildN == 1 && g_hcdDumpN == 1 && !g_hcdDumpBusy);
    HCT(HctPre(g_hctLines[2], "heapcall: #1 wild code C000001D (info 00000000 00000000) eip ") && strstr(g_hctLines[2], "[selftest+0x"));
    HCT(HctPre(g_hctLines[4], "heapcall dump: written ts3native-heapcall-"));
    wchar_t dmp[MAX_PATH];
    HCT(PathFmt(dmp, L"%s\\ts3native-heapcall-%lu-1.dmp", g_hcdDir, GetCurrentProcessId()));
    WIN32_FILE_ATTRIBUTE_DATA fa;
    HCT(GetFileAttributesExW(dmp, GetFileExInfoStandard, &fa) && fa.nFileSizeLow > 1024);
    DeleteFileW(dmp);
    // the dump loaded dbghelp.dll and with it oleaut32.dll, new to this process: a later selftest (autshadow run) then faulted
    // in oleaut32. The game has oleaut32 loaded from the start; here both are unloaded again.
    const HMODULE dbg = GetModuleHandleW(L"dbghelp.dll");
    HCT(dbg && FreeLibrary(dbg) && !GetModuleHandleW(L"dbghelp.dll"));
    printf("selftest heapcall: dbghelp unloaded, oleaut32 %s\n", GetModuleHandleW(L"oleaut32.dll") ? "still loaded" : "unloaded");
    g_hcdDumpN = g_hcdDumpMax;  // dump cap: the line only
    HcdOnXcpt(&x);
    HCT(g_hctN == 7 && g_hcdWildN == 2);

    g_logTap = tap, g_hcdOn = on, g_hcdN = n0, g_hcdWildN = w0, g_hcdDumpN = d0, g_hcdDumpBusy = 0, g_hcdDir[0] = 0, ts3native_reprostep = 0;
    g_hcdSectN = sn0, g_hcdSects = ss0, g_hcdAvEip = ae0, g_hcdAliveMs = am0, ts3native_counters.hcdAv656277 = av0;
    VirtualFree(heap, 0, MEM_RELEASE), VirtualFree(obj, 0, MEM_RELEASE), VirtualFree(stk, 0, MEM_RELEASE);
    printf("selftest heapcall: ok\n");
}
