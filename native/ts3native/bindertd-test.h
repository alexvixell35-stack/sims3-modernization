// TestBinderTypeHandle ([Binder] TypeHandleDirect, bindertd.cpp, docs/537 §20.3 E1). Included by selftest.cpp.
// Runs EA's 32-byte window (its 0xD8A8E0 call sent to a stand-in that does what RuntimeTypeHandle.get_Value does) and our
// replacement inside a copy of 0xDB0940's frame (sub esp,8Ch; push esi / edi; memset of [ebp-4Ch] with its three arguments
// still on the stack), and compares the result words, esp, esi, edi and the exception cell for many handles.
static DWORD g_tdBal, g_tdEsi, g_tdEdi, g_tdN2mCalls, g_tdMethodCell = 0x7E57;
static const void* g_tdN2mMethod;
// 0xD8A8E0(method, this, 0, &result, exc, esi): get_Value = `return value;`, the handle's one IntPtr field at offset 0
static void __cdecl TdFakeN2m(const void* method, const DWORD* self, DWORD, DWORD* result, DWORD*, DWORD) {
    g_tdN2mCalls++, g_tdN2mMethod = method;
    result[0] = self[0];
}
typedef unsigned __int64(__cdecl* TdFrameFn)(DWORD, DWORD* exc, DWORD esiv, DWORD* out, DWORD handle);
// at p: the frame, `window` (32 bytes; ea = EA's, with [0x11EF06C] and the call retargeted), then esp / esi / edi to globals and
// edx:eax = [ebp-40h]:[ebp-44h]
static TdFrameFn TdFrame(u8* p, const u8* window, bool ea) {
    static const u8 pro[] = {0x55, 0x8B, 0xEC, 0x81, 0xEC, 0x8C, 0x00, 0x00, 0x00, 0x56, 0x57,  // push ebp; mov ebp,esp; sub esp,8Ch; push esi; push edi
                             0x8D, 0x7D, 0xB4, 0x33, 0xC0, 0xB9, 0x12, 0x00, 0x00, 0x00, 0xF3, 0xAB,  // memset [ebp-4Ch], 0, 48h (rep stosd)
                             0x6A, 0x48, 0x6A, 0x00, 0x8D, 0x45, 0xB4, 0x50,  // EA's memset arguments, left on the stack as at 0xDB0958
                             0x8B, 0x75, 0x10, 0x8B, 0x7D, 0x0C};  // mov esi,[ebp+10h]; mov edi,[ebp+0Ch]
    u8* q = p;
    memcpy(q, pro, sizeof(pro)), q += sizeof(pro);
    u8* const w = q;
    memcpy(w, window, 32), q += 32;
    if (ea) {
        const DWORD cell = (DWORD)(uintptr_t)&g_tdMethodCell;
        memcpy(w + 18, &cell, 4);  // mov ecx,[0x11EF06C] -> the stand-in cell
        const LONG rel = (LONG)((uintptr_t)&TdFakeN2m - (uintptr_t)(w + 29));
        memcpy(w + 25, &rel, 4);  // call 0xD8A8E0 -> TdFakeN2m
    }
    static const u8 bal[] = {0x8D, 0x8D, 0x6C, 0xFF, 0xFF, 0xFF, 0x2B, 0xCC};  // lea ecx,[ebp-94h] (esp after push esi / edi); sub ecx,esp: 0 = balanced
    memcpy(q, bal, sizeof(bal)), q += sizeof(bal);
    const auto put = [&q](u8 modrm, const void* at) { *q++ = 0x89, *q++ = modrm; const DWORD v = (DWORD)(uintptr_t)at; memcpy(q, &v, 4), q += 4; };
    put(0x0D, &g_tdBal), put(0x35, &g_tdEsi), put(0x3D, &g_tdEdi);  // mov [abs],ecx / esi / edi
    static const u8 epi[] = {0x8B, 0x45, 0xBC, 0x8B, 0x55, 0xC0,  // mov eax,[ebp-44h]; mov edx,[ebp-40h]
                             0x8D, 0xA5, 0x6C, 0xFF, 0xFF, 0xFF, 0x5F, 0x5E, 0x8B, 0xE5, 0x5D, 0xC3};  // lea esp,[ebp-94h]; pop edi; pop esi; leave
    memcpy(q, epi, sizeof(epi));
    return (TdFrameFn)(void*)p;
}

static void TestBinderTypeHandle() {
    static_assert(offsetof(Ts3nCounters, jitFullN) == 0x6008 && offsetof(Ts3nCounters, tdOn) == 0x6014 && sizeof(Ts3nCounters) == 6323 * 4,
                  "counters v188 layout after the v185-v187 gap (docs/registry.md)");
    u8* x = (u8*)VirtualAlloc(NULL, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    assert(x);
    // the patch: refuses other bytes, writes the window and nothing else, refuses a second time (the bytes differ now)
    u8* const body = x + 0x800;
    memcpy(body, kTdBody, sizeof(kTdBody));
    body[0x34] ^= 1;  // inside the call's rel32
    assert(!TdPatch(body));
    body[0x34] ^= 1;
    assert(TdPatch(body) && !memcmp(body, kTdBody, 0x1E) && !memcmp(body + 0x1E, kTdNew, 32) && !memcmp(body + 0x3E, kTdBody + 0x3E, sizeof(kTdBody) - 0x3E));
    assert(!TdPatch(body));
    // the jmp lands on the first byte after the window (0xDB097E)
    assert(kTdNew[9] == 0xEB && 11 + kTdNew[10] == 32);
    // results: EA's window (stand-in get_Value) vs ours, for edge and random handles
    const TdFrameFn ea = TdFrame(x, kTdBody + 0x1E, true), ours = TdFrame(x + 0x400, kTdNew, false);
    DWORD hs[1024] = {0, 1, 2, 0x7FFFFFFF, 0x80000000, 0xFFFFFFFF, 0x44230000, 0x4423ABCD, 0x01220E40, 0xDEADBEEF};
    DWORD s = 12345;
    for (int i = 10; i < 1024; i++) s = s * 1103515245u + 12345u, hs[i] = s ^ ((DWORD)i << 20);
    DWORD out = 0;
    for (const DWORD h : hs) {
        DWORD exc = 0;
        const DWORD calls = g_tdN2mCalls;
        g_tdBal = g_tdEsi = g_tdEdi = 0xCCCCCCCC;
        const unsigned __int64 r0 = ea(0, &exc, 0x5E5E0000 ^ h, &out, h);
        const DWORD bal0 = g_tdBal, esi0 = g_tdEsi, edi0 = g_tdEdi;
        assert(g_tdN2mCalls == calls + 1 && g_tdN2mMethod == (const void*)(uintptr_t)g_tdMethodCell);  // the method read from the cell
        g_tdBal = g_tdEsi = g_tdEdi = 0xCCCCCCCC;
        const unsigned __int64 r1 = ours(0, &exc, 0x5E5E0000 ^ h, &out, h);
        assert(g_tdN2mCalls == calls + 1);  // no call from ours
        assert(r0 == r1 && r1 == h && bal0 == 0 && g_tdBal == 0 && esi0 == g_tdEsi && g_tdEsi == (0x5E5E0000 ^ h) &&
               edi0 == g_tdEdi && g_tdEdi == (DWORD)(uintptr_t)&exc && exc == 0);
    }
    VirtualFree(x, 0, MEM_RELEASE);
    printf("TestBinderTypeHandle: patch byte check, %d handles: EA's window (get_Value stand-in) == direct read (result, esp, esi, edi, exc)\n", (int)(sizeof(hs) / sizeof(hs[0])));
}
