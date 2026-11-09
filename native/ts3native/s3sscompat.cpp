// [S3SSCompat] StaleWriteGuard (docs/539 §22, 0.36.236): S3SS 1.6.3's stale "Camera speed threshold" write.
// Bug [verified: DR catch at S3SS+0xE6FFB, disassembly below, S3SS source read for behaviour only]: S3SS LotStreamingOptimizations
// (streamingSettings, default on) captures the address of the live setting "Camera speed threshold" once, WorldManager+0xEC
// (docs/28, docs/65; WorldManager = [0x11ECBC4]), and keeps it in its maintained-write list. On every later VariableRegistry pass
// (each world load) OptimizationPatch::OnSettingsRefired rewrites 5.0f there through SafeReassert. After a travel EA has freed that
// WorldManager ([0x11ECBC4] is cleared at 0xC6B507 / 0xC7E281 and set again only at 0xC67883 / 0xC6D0CC), so the 4 bytes land in
// freed or reused EA heap: the float-5 (0x40A00000) corruption of docs/539.
// Fix: the one call to SafeReassert in OnSettingsRefired (S3SS+0x28CDB, E8 rel32) goes through S3cThunk. SafeReassert's convention
// [verified disassembly S3SS 1.6.3 +0x28760]: ecx = target address, edx = data, [esp+4] = size, plain ret (the caller adds 4),
// al = result (the caller ignores it). A 4-byte write to a target outside TS3W.exe's image that is not the current
// [0x11ECBC4]+0xEC is the stale camera threshold (the only heap-target maintained write of size 4 in 1.6.3 [inferred: S3SS source,
// the other two LotStreaming targets are exe globals 0x11ECBC0 / 0x1184328, BradyBunchBegone writes 12 bytes]): Steam 1.67 sends it
// to the current WorldManager+0xEC (S3SS's own intent, same value), EA 1.69 skips it (+0xEC there not verified). Exe-global, live
// and other-size targets pass untouched. No live WorldManager = skipped.
// Step aside: only S3SS builds in kS3cBuggy (file sha256) are touched, and only when the call window and SafeReassert's head match
// byte for byte (relocated absolute dwords masked); anything else logs "stands aside" and writes nothing. A fixed S3SS release has
// another sha256, so the guard turns itself off. Default 1 in code and release ini (rare-crash rule: seen, repro, writer caught).
struct S3cBuild { const char* sha; const char* ver; DWORD call; DWORD fn; };
static const S3cBuild kS3cBuggy[] = {
    {"69ce87ee84528748ee1f19c1f7cb2183e8e8cb7f7cae57cdb64098a7ebcdc13a", "1.6.3", 0x28CDB, 0x28760},  // official 1.6.3 release asset
};
// S3SS+0x28CD0..+0x28CE5 in OnSettingsRefired's loop: mov eax,[edi+8]; mov edx,[edi+4]; sub eax,edx; mov ecx,[edi]; push eax;
// call SafeReassert (E8 at +11); add edi,0x10; add esp,4
static const u8 kS3cCall[] = {0x8B, 0x47, 0x08, 0x8B, 0x57, 0x04, 0x2B, 0xC2, 0x8B, 0x0F, 0x50, 0xE8, 0x80, 0xFA, 0xFF, 0xFF, 0x83, 0xC7, 0x10, 0x83, 0xC4, 0x04};
static const int kS3cCallE8 = 11;
// SafeReassert head through `mov ebx,ecx` / `mov eax,edx` / `mov edi,[ebp+8]`; 0 in kS3cFnMask = an absolute dword S3SS's relocation changes
static const u8 kS3cFn[] = {
    0x55, 0x8B, 0xEC, 0x6A, 0xFE, 0x68, 0x68, 0xE5, 0x16, 0x10, 0x68, 0xB0, 0x67, 0x0E, 0x10, 0x64, 0xA1, 0x00, 0x00, 0x00, 0x00, 0x50, 0x83, 0xEC, 0x14,
    0xA1, 0xC0, 0xA3, 0x17, 0x10, 0x31, 0x45, 0xF8, 0x33, 0xC5, 0x89, 0x45, 0xE4, 0x53, 0x56, 0x57, 0x50, 0x8D, 0x45, 0xF0, 0x64, 0xA3, 0x00, 0x00, 0x00,
    0x00, 0x89, 0x65, 0xE8, 0x8B, 0xC2, 0x89, 0x45, 0xDC, 0x8B, 0xD9, 0xC7, 0x45, 0xFC, 0x00, 0x00, 0x00, 0x00, 0x8B, 0xD3, 0x8B, 0xF0, 0x8B, 0x7D, 0x08};
static bool S3cFnMasked(int i) { return (i >= 6 && i <= 9) || (i >= 11 && i <= 14) || (i >= 26 && i <= 29); }

static const wchar_t* g_s3cModule = L"Sims3SettingsSetter.asi";  // selftest swaps it
static const volatile DWORD* g_s3cWmSlot = (const volatile DWORD*)(uintptr_t)TS3A(0x011ECBC4);  // WorldManager
#ifdef TS3A_TABLE
static const bool kS3cRedirect = false;  // EA 1.69: WorldManager+0xEC not verified there, a stale write is skipped
#else
static const bool kS3cRedirect = true;
#endif
static DWORD g_s3cExeLo, g_s3cExeHi;  // TS3W.exe image: S3SS's exe-global targets pass
static bool g_s3cLog = true;
static void* g_s3cOrig;               // SafeReassert (S3SS+0x28760)
static volatile LONG g_s3cCalls, g_s3cStale, g_s3cRedir, g_s3cSkip;

// The address SafeReassert gets: addr (untouched), the live WorldManager+0xEC (redirected) or 0 (skipped).
static DWORD __cdecl S3cFix(DWORD addr, const u8* data, DWORD size) {
    InterlockedIncrement(&g_s3cCalls);
    if (size != 4 || (addr >= g_s3cExeLo && addr < g_s3cExeHi)) return addr;
    DWORD wm = 0, v = 0;
    __try { wm = *g_s3cWmSlot; v = *(const volatile DWORD*)data; } __except (EXCEPTION_EXECUTE_HANDLER) {}
    const DWORD cur = wm ? wm + 0xEC : 0;
    if (addr == cur) return addr;
    const DWORD to = kS3cRedirect && cur && Readable((const void*)(uintptr_t)cur, 4) ? cur : 0;
    const LONG n = InterlockedIncrement(&g_s3cStale);
    InterlockedIncrement(to ? &g_s3cRedir : &g_s3cSkip);
    if (g_s3cLog && (n <= 20 || n % 100 == 0)) {
        char dst[16] = "skipped";
        if (to) sprintf_s(dst, "0x%08lX", to);
        Log("s3ss compat: stale maintained write 0x%08lX -> %s (value 0x%08lX, size %lu); WorldManager 0x%08lX; world load #%ld; stale %ld "
            "(redirected %ld, skipped %ld), calls %ld", addr, dst, v, size, wm, g_udWorlds, n, g_s3cRedir, g_s3cSkip, g_s3cCalls);
    }
    return to;
}

// Replaces `call SafeReassert` at S3SS+0x28CDB: same registers and stack as SafeReassert, tail-jumps to it with the fixed address.
static __declspec(naked) void S3cThunk() {
    __asm {
        push edx                // data
        push dword ptr [esp + 8]  // size
        push edx
        push ecx
        call S3cFix
        add esp, 12
        pop edx
        test eax, eax
        jz skip
        mov ecx, eax
        jmp dword ptr [g_s3cOrig]
    skip:
        mov al, 1               // as SafeReassert's "nothing to do"
        ret
    }
}

// Known-buggy sha256 and matching bytes: retarget the call. False = stands aside (one log line, nothing written).
static bool S3cInstall(u8* base, const char* sha) {
    const S3cBuild* b = NULL;
    for (const S3cBuild& k : kS3cBuggy)
        if (!_stricmp(sha, k.sha)) b = &k;
    if (!b) {
        Log("s3ss compat: stale write guard stands aside (S3SS %.8s not in the known-buggy list)", sha);
        return false;
    }
    u8* call = base + b->call - kS3cCallE8;
    const u8* fn = base + b->fn;
    bool fnOk = Readable(fn, sizeof(kS3cFn));
    for (int i = 0; fnOk && i < (int)sizeof(kS3cFn); i++) fnOk = S3cFnMasked(i) || fn[i] == kS3cFn[i];
    const bool callOk = Matches(call, kS3cCall, sizeof(kS3cCall));
    if (!callOk || !fnOk) {
        char got[sizeof(kS3cCall) * 3 + 1] = "unreadable";
        if (Readable(call, sizeof(kS3cCall))) Hex(call, sizeof(kS3cCall), got, sizeof(got));
        Log("s3ss compat: stale write guard stands aside (S3SS %s sha %.8s: %s differ; call window +0x%lX: %s)", b->ver, sha,
            !callOk ? "OnSettingsRefired call bytes" : "SafeReassert bytes", b->call - kS3cCallE8, got);
        return false;
    }
    g_s3cOrig = (void*)fn;
    u8 e8[5] = {0xE8};
    const DWORD rel = (DWORD)(uintptr_t)S3cThunk - (DWORD)(uintptr_t)(call + kS3cCallE8 + 5);
    memcpy(e8 + 1, &rel, 4);
    if (!Write(call + kS3cCallE8, e8, sizeof(e8))) {
        Log("s3ss compat: stale write guard stands aside (S3SS %s: VirtualProtect failed, err %lu)", b->ver, GetLastError());
        return false;
    }
    Log("s3ss compat: stale write guard on (S3SS %s sha %.8s, known-buggy): SafeReassert call S3SS+0x%lX retargeted; stale 4-byte heap "
        "targets (not the live [0x%08lX]+0xEC camera speed threshold) %s; exe-global targets untouched; Log=%d", b->ver, sha, b->call,
        (DWORD)(uintptr_t)g_s3cWmSlot, kS3cRedirect ? "redirected to the live WorldManager+0xEC" : "skipped (EA 1.69)", g_s3cLog ? 1 : 0);
    return true;
}

// DllMain, after InstallCrashReport. S3SS loads before us (UAL file-name order, docs/68 §5); its refire runs at world load.
static bool ApplyS3ssCompat(const wchar_t* ini) {
    const bool on = GetPrivateProfileIntW(L"S3SSCompat", L"StaleWriteGuard", 1, ini) > 0;
    g_s3cLog = GetPrivateProfileIntW(L"S3SSCompat", L"Log", 1, ini) > 0;
    HMODULE m = GetModuleHandleW(g_s3cModule);
    if (!m) { Log("s3ss compat: Sims3SettingsSetter.asi not loaded at start, stale write guard not installed"); return false; }
    if (!on) { Log("s3ss compat: stale write guard off ([S3SSCompat] StaleWriteGuard=0)"); return false; }
    const u8* exe = (const u8*)GetModuleHandleW(NULL);
    g_s3cExeLo = (DWORD)(uintptr_t)exe;
    g_s3cExeHi = g_s3cExeLo + ((const IMAGE_NT_HEADERS*)(exe + ((const IMAGE_DOS_HEADER*)exe)->e_lfanew))->OptionalHeader.SizeOfImage;
    wchar_t path[MAX_PATH];
    char sha[65];
    if (!GetModuleFileNameW(m, path, MAX_PATH) || !CrFileSha(path, sha)) {
        Log("s3ss compat: stale write guard stands aside (S3SS file sha256 unreadable)");
        return false;
    }
    return S3cInstall((u8*)m, sha);
}
