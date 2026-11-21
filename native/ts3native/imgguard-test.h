// TestImageGuard ([ImageGuard], imgguard.cpp, docs/232 Run 8d). Included by selftest.cpp.
// IgVerdict on sizes around LogMB / MaxMB and on 32-bit wraps; then the stub runs for real between a fake decode frame and
// two recorders: the resize (pass, `ret 4` like the game's) and the failure path (reject, reached with the decode's esp).

static DWORD g_igOut[6];  // exit (1 resize, 2 failure), size argument (resize only), ebx, esi, ebp, edi
static __declspec(naked) void IgTestResize() {
    __asm {
        mov g_igOut[0], 1
        mov eax, [esp + 4]
        mov g_igOut[4], eax
        mov g_igOut[8], ebx
        mov g_igOut[12], esi
        mov g_igOut[16], ebp
        mov g_igOut[20], edi
        ret 4
    }
}
static __declspec(naked) void IgTestFail() {
    __asm {
        mov g_igOut[0], 2
        mov g_igOut[8], ebx
        mov g_igOut[12], esi
        mov g_igOut[16], ebp
        mov g_igOut[20], edi
        ret  // the stub left the decode's esp: this returns from IgRunFrame's call IgFrameInner
    }
}
// The decode at the call: ecx = ebx = vector, the size pushed, then the call. The resize path returns here (its ret 4).
static __declspec(naked) void IgFrameInner() {
    __asm {
        push eax
        mov ecx, ebx
        call IgStub
        ret
    }
}
// esi = info, ebp = decoder, ebx / edi = markers. The failure path returns from IgFrameInner's call directly (decode's esp).
static __declspec(naked) void __stdcall IgRunFrame(const void* /*info*/, const void* /*dec*/, DWORD /*size*/) {
    __asm {
        push ebx
        push esi
        push ebp
        push edi
        mov esi, [esp + 20]
        mov ebp, [esp + 24]
        mov eax, [esp + 28]
        mov ebx, 0x11111111
        mov edi, 0x33333333
        call IgFrameInner
        pop edi
        pop ebp
        pop esi
        pop ebx
        ret 12
    }
}

static void TestImageGuard() {
    IgCounters& k = g_igK;
    unsigned __int64 b;
    const DWORD MB = 1u << 20;
    assert(IgVerdict(32, 256, 256, 256 * 256 * 4, 64, 512, &b) == 0 && b == 256 * 256 * 4);
    assert(IgVerdict(32, 4096, 4096, 64 * MB, 64, 512, &b) == 1 && b == 64 * MB);
    assert(IgVerdict(32, 8192, 16384, 512 * MB, 64, 512, &b) == 1);
    assert(IgVerdict(32, 8192, 16385, 512 * MB + 8192 * 4, 64, 512, &b) == 2);
    assert(IgVerdict(24, 23000, 29000, 3u * 23000 * 29000, 64, 512, &b) == 2 && b == 3ull * 23000 * 29000);  // ~1.9 GB, run 8c
    assert(IgVerdict(32, 65536, 65536, 0, 64, 512, &b) == 3);                            // 16 GB wraps to 0
    assert(IgVerdict(32, 0x7FFFFFFF, 0x7FFFFFFF, 4, 64, 512, &b) == 3);
    assert(IgVerdict(0x7FFFFFF8, 0x7FFFFFFF, 0x7FFFFFFF, 0, 64, 512, &b) == 3);         // product past 2^64 is caught
    assert(IgVerdict(32, -1, 16, (DWORD)-64, 64, 512, &b) == 3 && IgVerdict(-8, 16, 16, (DWORD)-256, 64, 512, &b) == 3);
    assert(IgVerdict(4, 4096, 4096, 0, 64, 512, &b) == 0 && b == 0);                     // 4 bpp: the game's bpp / 8 = 0
    assert(IgVerdict(32, 0, 100000, 0, 64, 512, &b) == 0);

    // patch: wrong bytes before the call are refused by Matches; PatchCalls needs the E8 to the resize
    alignas(4) static u8 code[8] = {0xE8};
    *(LONG*)(code + 1) = (LONG)(0x12345678 - ((uintptr_t)code + 5));
    const CallSite bad = {(DWORD)(uintptr_t)code, 0x12345679, (void*)IgStub}, good = {(DWORD)(uintptr_t)code, 0x12345678, (void*)IgStub};
    assert(!PatchCalls("image guard test", &bad, 1) && PatchCalls("image guard test", &good, 1));
    assert((uintptr_t)code + 5 + *(LONG*)(code + 1) == (uintptr_t)&IgStub);

    // the stub: small sizes reach the resize with every register; big ones are logged and pass; huge ones take the failure path
    alignas(4) static LONG info[7];
    alignas(4) static DWORD node[2] = {0, 4}, stream[1] = {0xABCD1234};
    alignas(4) static DWORD dec[3] = {0, (DWORD)(uintptr_t)stream, (DWORD)(uintptr_t)node};
    g_igResize = (DWORD)(uintptr_t)&IgTestResize, g_igFail = (DWORD)(uintptr_t)&IgTestFail;
    memset(&k.igReady, 0, sizeof(LONG) * 8);
    info[4] = 256, info[5] = 128, info[6] = 32;
    IgRunFrame(info, dec, 256 * 128 * 4);
    assert(g_igOut[0] == 1 && g_igOut[1] == 256 * 128 * 4 && g_igOut[2] == 0x11111111 && g_igOut[3] == (DWORD)(uintptr_t)info &&
           g_igOut[4] == (DWORD)(uintptr_t)dec && g_igOut[5] == 0x33333333);
    info[4] = 8192, info[5] = 4096;
    IgRunFrame(info, dec, 128 * MB);
    assert(g_igOut[0] == 1 && g_igOut[1] == 128 * MB && k.igBig == 1 && k.igMaxMB == 128);
    info[4] = 23000, info[5] = 29000, info[6] = 24;
    IgRunFrame(info, dec, 3u * 23000 * 29000);
    assert(g_igOut[0] == 2 && g_igOut[2] == 0 && g_igOut[3] == (DWORD)(uintptr_t)info && g_igOut[4] == (DWORD)(uintptr_t)dec &&
           g_igOut[5] == 0x33333333 && k.igRejected == 1);
    info[4] = 65536, info[5] = 65536, info[6] = 32;
    IgRunFrame(info, dec, 0);
    assert(g_igOut[0] == 2 && k.igOverflow == 1 && k.igCalls == 4 && k.igMaxMB == 0x7FFFFFFF);
    g_igResize = IG_RESIZE, g_igFail = IG_FAIL;
    memset(&k.igReady, 0, sizeof(LONG) * 8);
    g_igLines = 0;
    printf("ImageGuard: verdicts (LogMB / MaxMB edges, 32-bit wrap, negative inputs), call patch, stub pass keeps registers, "
           "over MaxMB and wrap take the failure path with bl = 0: ok\n");
}
