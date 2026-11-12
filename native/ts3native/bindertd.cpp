// [Binder] TypeHandleDirect (docs/537 §20.3 E1, 0.36.245, default 0, repo ini 0): EA's native body of Type.GetTypeFromHandle
// (0xDB0940, adapter 0xDB0A00, binder init 0xDB08E1) gets the handle's value by calling RuntimeTypeHandle.get_Value through the
// native-to-managed call 0xD8A8E0 at 0xDB0976 (method [0x11EF06C], this = &handle on the C stack at [ebp+0x18], result to
// [ebp-0x44]) [verified: disassembly TS3W 1.67]. In game that is 11-15k calls/s at ~660 ns, sampled as 7.4 ms/s of get_Value
// at stack depth 1 (I-475-2, I-475-3 inferred). get_Value is `return value;` (RuntimeTypeHandle's one IntPtr field, offset 0),
// so the 32 bytes 0xDB095E-0xDB097D become a direct read with the same result for every handle:
//   mov eax,[ebp+18h]; mov [ebp-44h],eax; add esp,0Ch; jmp 0xDB097E; 21 x nop
// add esp,0Ch: the window's `add esp,24h` also pops the three memset arguments pushed before 0xDB0953 (cdecl, cleaned late), so
// esp stays where pop edi / pop esi at 0xDB09F6 need it. esi / edi are loaded before the window; [edi] (exception out) is zeroed
// by the adapter (0xDB0A0A) and nothing in the window wrote it; the result's high word [ebp-0x40] stays 0 from the memset;
// [ebp-0x4C] (the window stored &handle there) is rewritten at 0xDB0990 before any read. Lost with the call: 0xD8A8E0's own
// entry work (new interpreter state, exception frame), which get_Value (no call, no throw, `this` never null here) does not need.
// The check covers the whole body up to 0xDB0995 (frame, memset, the window, the result reads after it): another owner or
// another exe = SKIP. No other mod is known to patch this body (S3SS source, Apex 3.0.0 / 3.1.0 binaries: no 0xDB09xx address).
static const DWORD TD_BODY = 0x00DB0940, TD_SITE = 0x00DB095E;
static const u8 kTdBody[0x56] = {
    0x55, 0x8B, 0xEC, 0x81, 0xEC, 0x8C, 0x00, 0x00, 0x00, 0x56, 0x57, 0x6A, 0x48, 0x8D, 0x45, 0xB4,  // +0x00 frame, memset args
    0x6A, 0x00, 0x50, 0xE8, 0x60, 0xDF, 0x1C, 0x00, 0x8B, 0x75, 0x10, 0x8B, 0x7D, 0x0C, 0x56, 0x57,  // +0x10 call memset; esi, edi; window
    0x8D, 0x55, 0xBC, 0x52, 0x8D, 0x4D, 0x18, 0x8B, 0xC1, 0x6A, 0x00, 0x89, 0x4D, 0xB4, 0x8B, 0x0D,  // +0x20
    0x6C, 0xF0, 0x1E, 0x01, 0x50, 0x51, 0xE8, 0x65, 0x9F, 0xFD, 0xFF, 0x83, 0xC4, 0x24, 0x83, 0x3F,  // +0x30 call 0xD8A8E0; add esp,24h | cmp [edi],0
    0x00, 0x75, 0x73, 0x83, 0x3D, 0x84, 0x2D, 0x1F, 0x01, 0x00, 0x8B, 0x45, 0xBC, 0x8B, 0x55, 0xC0,  // +0x40 jne; result reads
    0x89, 0x45, 0xB4, 0x89, 0x55, 0xB8,                                                              // +0x50
};
static const u8 kTdNew[32] = {0x8B, 0x45, 0x18, 0x89, 0x45, 0xBC, 0x83, 0xC4, 0x0C, 0xEB, 0x15, 0x90, 0x90, 0x90, 0x90, 0x90,
                              0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90};
static_assert(TD_SITE - TD_BODY == 0x1E && TD_SITE - TD_BODY + sizeof(kTdNew) <= sizeof(kTdBody), "the window lies inside the checked body");

static bool TdPatch(u8* body) {
    if (!Matches(body, kTdBody, sizeof(kTdBody))) return false;
    return Write(body + (TD_SITE - TD_BODY), kTdNew, sizeof(kTdNew));  // DllMain: Mono is not started yet
}

static bool ApplyBinderTypeHandle(const wchar_t* ini) {
    if (GetPrivateProfileIntW(L"Binder", L"TypeHandleDirect", 0, ini) <= 0) return false;
    if (!TdPatch((u8*)(uintptr_t)TD_BODY)) {
        Log("SKIP binder type handle direct: bytes at 0x%08lX differ (another owner or another exe): not installed", TD_BODY);
        return false;
    }
    ts3native_counters.tdOn = 1;
    Log("OK   binder type handle direct (docs/537 §20.3): Type.GetTypeFromHandle's EA body 0xDB0940 reads the handle at 0x%08lX, "
        "no 0xD8A8E0 call to RuntimeTypeHandle.get_Value", TD_SITE);
    return true;
}
