// [ImageGuard] (docs/232 Run 8c/8d): size check on the raster buffer of EA's image decode. Default OFF.
// EA build only (TS3N_EA_PORT, ported from hotfix/v0.2.10-ea169): the Steam asi has no copy, so its counters are the
// module's own g_igK, not Ts3nCounters fields (the Steam layout stays as it is). Included by ts3native.cpp after savewrite.cpp (PatchCalls).
//
// EA's image codec layer (codecs in a registered list: JPEG 2, PNG 4, BMP 5, TGA 6, GIF 7, DDS 0x1F; format 0x32 = probe
// every codec and take the best score) decodes into an image object through the wrapper 0x446110 (7 callers), which calls
// the decode 0x445FB0 (this = image object, +0xC an eastl::vector<uint8_t>; args decoder, format, sink, flags). The decode
// asks the codec for the image info (esi: width +0x10, height +0x14, bits per pixel +0x18) and resizes the vector at
// 0x446037 to (bpp / 8) * height * width, a 32-bit imul with no check. A huge or corrupt header asks for any size: EA 1.69
// run 8c requested ~1.9 GB at the main menu, the allocator returned null and msvcr80 memcpy wrote through it.
// Fix: the call at 0x446037 goes to IgStub. Sizes below LogMB pass straight to the game's resize 0x405410. Sizes >= LogMB
// are logged (and counted). Sizes > MaxMB, or a product that wrapped in 32 bits, are rejected: the stub drops the call and
// jumps to the decode's own failure path 0x4460D7 (release the info, close the decoder, return false with bl = 0), the
// path the game takes when the codec fails to decode the rows. Every caller of the wrapper tests al and frees its objects.
// One "IMAGEGUARD" line for each of the first 64 logged calls: size, codec, stream, caller, resource key when the caller
// is one of the three resource loaders that keep the resource in ebx (key = resource->vf10(): instance, type, group).
// Where width / height come from (docs/232 Run 8d): not from the callers. The wrapper's callers pass only (stream, format
// 0x32, sink, 0xA); the info is the codec's own answer for that stream (decoder->vf10 -> codec GetInfo), built fresh per
// decode (1.69 addresses: PNG 0x4175F0 allocates and zeroes it, DDS 0x4153A0 reads the 0x70-byte header at offset 0 into a zeroed
// local), so a huge size is the stream's bytes, not a stale struct. The log line also has the raw format request and the
// first 3 code addresses on the stack above the wrapper's frame (caller chain; heuristic, may include stale values).

static const DWORD IG_SITE = TS3A(0x00446037), IG_SIZE = TS3A(0x00446019), IG_RESIZE = TS3A(0x00405410);
static const DWORD IG_FAIL = TS3A(0x004460D7), IG_WRAP_RET = TS3A(0x0044614D), IG_DECODE = TS3A(0x00445FB0);
static const DWORD IG_TEXT_LO = TS3A(0x00401000), IG_TEXT_HI = TS3A(0x00F95000);  // .text, for the caller chain
static const DWORD IG_RES_RET[3] = {TS3A(0x007E2530), TS3A(0x007E7A3D), TS3A(0x007F2F0F)};  // return addresses into the 3 resource loaders
// mov eax,[esi+18h]; mov ebx,[esp+10h]; cdq; and edx,7; add eax,edx; sar eax,3; imul eax,[esi+14h]; imul eax,[esi+10h];
// add ebx,0Ch; push eax; mov ecx,ebx (then the call at IG_SITE)
static const u8 kIgSize[30] = {0x8B, 0x46, 0x18, 0x8B, 0x5C, 0x24, 0x10, 0x99, 0x83, 0xE2, 0x07, 0x03, 0xC2, 0xC1, 0xF8,
                               0x03, 0x0F, 0xAF, 0x46, 0x14, 0x0F, 0xAF, 0x46, 0x10, 0x83, 0xC3, 0x0C, 0x50, 0x8B, 0xCB};
// mov eax,[ebp]; mov edx,[eax+18h]; push esi; mov ecx,ebp; call edx (release the info)
static const u8 kIgFail[11] = {0x8B, 0x45, 0x00, 0x8B, 0x50, 0x18, 0x56, 0x8B, 0xCD, 0xFF, 0xD2};
static DWORD g_igResize = IG_RESIZE, g_igFail = IG_FAIL;
static LONG g_igLogMB = 64, g_igMaxMB = 512;
static volatile LONG g_igLines;
// igReady: the call at 0x446037 goes to IgStub; igCalls: decodes that reached the resize; igBig: sizes >= LogMB passed and logged;
// igRejected: sizes > MaxMB; igOverflow: negative inputs or a 32-bit wrap; igMaxMB: largest size seen at or above LogMB (0x7FFFFFFF after a wrap)
static struct IgCounters { LONG igReady, igCalls, igBig, igRejected, igOverflow, igMaxMB, igSpare[2]; } g_igK;

// The game's size, (bpp / 8) * h * w, without the 32-bit wrap. 0 pass, 1 log (>= logMB), 2 reject (> maxMB),
// 3 reject (negative input, or the game's 32-bit product n is not the real size).
static int IgVerdict(LONG bpp, LONG w, LONG h, DWORD n, LONG logMB, LONG maxMB, unsigned __int64* bytes) {
    *bytes = 0;
    const LONG b = bpp / 8;  // cdq; and edx,7; add; sar 3 = signed division
    if (b < 0 || w < 0 || h < 0) return 3;
    const unsigned __int64 bw = (unsigned __int64)b * (DWORD)w;  // < 2^59
    if (bw && (DWORD)h > 0xFFFFFFFFFFFFFFFFull / bw) return 3;
    *bytes = bw * (DWORD)h;
    if (*bytes != n) return 3;
    if (*bytes > ((unsigned __int64)maxMB << 20)) return 2;
    return *bytes >= ((unsigned __int64)logMB << 20) ? 1 : 0;
}

static const char* IgCodec(DWORD id) {
    switch (id) {
    case 2: return "JPEG";
    case 4: return "PNG";
    case 5: return "BMP";
    case 6: return "TGA";
    case 7: return "GIF";
    case 0x1F: return "DDS";
    default: return "?";
    }
}

typedef const DWORD*(__thiscall* IgKeyFn)(void* res);

// The resource key through the resource's own getter (vtable slot 4, as the loaders call it), or 0 when it faults.
static bool IgKey(DWORD res, DWORD* vt, DWORD key[4]) {
    __try {
        *vt = *(const DWORD*)(uintptr_t)res;
        const DWORD* kp = ((IgKeyFn)(uintptr_t)((const DWORD*)(uintptr_t)*vt)[4])((void*)(uintptr_t)res);
        memcpy(key, kp, 4 * sizeof(DWORD));
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}

// f = the stub's frame: f[0..7] pushad (edi esi ebp esp ebx edx ecx eax), f[8] flags, f[9] return address, f[10] the size
// argument, then the decode's frame. When the decode was called by the wrapper (f[18] = IG_WRAP_RET): f[23] = the wrapper
// caller's ebx (pushed at 0x44613D), f[30] = the wrapper's return address, f[31..34] its 4 arguments (stream, format,
// sink, flags), then the caller's frame. Returns 1 to reject the call.
static LONG __stdcall IgCheck(const DWORD* f) {
    IgCounters& k = g_igK;
    InterlockedIncrement(&k.igCalls);
    const u8* info = (const u8*)(uintptr_t)f[1];
    LONG w = 0, h = 0, bpp = 0;
    __try { w = *(const LONG*)(info + 0x10), h = *(const LONG*)(info + 0x14), bpp = *(const LONG*)(info + 0x18); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return 0; }  // the game read the same fields a moment ago
    unsigned __int64 bytes;
    const int v = IgVerdict(bpp, w, h, f[10], g_igLogMB, g_igMaxMB, &bytes);
    if (!v) return 0;
    InterlockedIncrement(v == 1 ? &k.igBig : v == 2 ? &k.igRejected : &k.igOverflow);
    const LONG mb = v == 3 ? 0x7FFFFFFF : (LONG)(bytes >> 20);
    for (LONG m = k.igMaxMB; mb > m && InterlockedCompareExchange(&k.igMaxMB, mb, m) != m; m = k.igMaxMB) {}
    if (InterlockedIncrement(&g_igLines) <= 64) {
        const u8* dec = (const u8*)(uintptr_t)f[2];
        DWORD stream = 0, svt = 0, codec = 0, caller = 0, res = 0, rvt = 0, key[4] = {0, 0, 0, 0}, fmt = 0, chain[3] = {0, 0, 0};
        const char* keyNote = "n/a (caller keeps no resource in ebx)";
        __try {
            stream = *(const DWORD*)(dec + 4);
            codec = *(const DWORD*)(*(const u8* const*)(dec + 8) + 4);
            if (stream) svt = *(const DWORD*)(uintptr_t)stream;
        } __except (EXCEPTION_EXECUTE_HANDLER) {}
        if (f[18] != IG_WRAP_RET) keyNote = "n/a (decode not called by the wrapper)";
        else {
            caller = f[30], fmt = f[32];
            __try {
                for (int i = 35, n = 0; i < 35 + 64 && n < 3; i++)
                    if (f[i] >= IG_TEXT_LO && f[i] < IG_TEXT_HI) chain[n++] = f[i];
            } __except (EXCEPTION_EXECUTE_HANDLER) {}
            for (int i = 0; i < 3; i++)
                if (caller == IG_RES_RET[i]) { res = f[23]; keyNote = IgKey(res, &rvt, key) ? "ok" : "unreadable"; }
        }
        Log("%s %s: %ld x %ld, %ld bpp, codec %lu %s, %I64u bytes (game's 32-bit size %lu)%s; key %s type 0x%08lX group 0x%08lX "
            "instance 0x%08lX%08lX; stream 0x%08lX vtable 0x%08lX; resource 0x%08lX vtable 0x%08lX; format request 0x%lX; caller 0x%08lX, "
            "stack code refs 0x%08lX 0x%08lX 0x%08lX; package path n/a",
            v == 1 ? "IMAGEGUARD" : "IMAGEGUARD REJECT", v == 3 ? "size wraps or is negative" : v == 2 ? "over MaxMB" : "over LogMB",
            w, h, bpp, codec, IgCodec(codec), bytes, f[10], v >= 2 ? ", decode returns failure" : "", keyNote, key[2], key[3],
            key[1], key[0], stream, svt, res, rvt, fmt, caller, chain[0], chain[1], chain[2]);
    }
    return v >= 2;
}

// Replaces the target of the call at 0x446037 (arg: the size). Pass: every register and the flags as the game left them,
// tail jump to the resize. Reject: drop our return address and the argument (the resize's ret 4), bl = 0, failure path.
static __declspec(naked) void IgStub() {
    __asm {
        pushfd
        pushad
        push esp
        call IgCheck
        test eax, eax
        jnz reject
        popad
        popfd
        jmp dword ptr [g_igResize]
    reject:
        popad
        popfd
        add esp, 8
        xor ebx, ebx
        jmp dword ptr [g_igFail]
    }
}

static bool ApplyImageGuard(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"ImageGuard", L"Enabled", 0, ini)) { Log("image guard off (Enabled=0)"); return false; }
    g_igLogMB = Clamp(GetPrivateProfileIntW(L"ImageGuard", L"LogMB", 64, ini), 1, 2047);
    g_igMaxMB = Clamp(GetPrivateProfileIntW(L"ImageGuard", L"MaxMB", 512, ini), g_igLogMB, 2047);
    if (!Matches((const void*)(uintptr_t)IG_SIZE, kIgSize, sizeof(kIgSize)) || !Matches((const void*)(uintptr_t)IG_FAIL, kIgFail, sizeof(kIgFail))) {
        Log("SKIP image guard: decode 0x%08X differs at 0x%08X or 0x%08X (another build or mod), nothing written", IG_DECODE, IG_SIZE, IG_FAIL);
        return false;
    }
    const CallSite site = {IG_SITE, IG_RESIZE, (void*)IgStub};
    if (!PatchCalls("image guard", &site, 1)) return false;
    g_igK.igReady = 1;
    Log("OK   image guard: call 0x%08X (decode 0x%08X) -> IgStub, LogMB %ld MaxMB %ld (over MaxMB: the decode fails)", IG_SITE,
        IG_DECODE, g_igLogMB, g_igMaxMB);
    return true;  // keep the log open for the IMAGEGUARD lines
}
