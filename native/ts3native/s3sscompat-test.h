// TestS3ssCompat ([S3SSCompat] StaleWriteGuard, s3sscompat.cpp, docs/539 §22). Included by selftest.cpp. No S3SS binary: a
// VirtualAlloc'd fake module holds the 1.6.3 call window and SafeReassert head (other absolute dwords, as after a relocation).
static volatile DWORD g_s3tEcx, g_s3tEdx, g_s3tSize, g_s3tHits;
static __declspec(naked) void S3tOrig() {  // stands in for SafeReassert: records ecx / edx / [esp+4], returns al = 1
    __asm {
        mov g_s3tEcx, ecx
        mov g_s3tEdx, edx
        mov eax, [esp + 4]
        mov g_s3tSize, eax
        inc g_s3tHits
        mov eax, 1
        ret
    }
}
static DWORD S3tCall(DWORD addr, const void* data, DWORD n) {  // OnSettingsRefired's call: ecx, edx, one pushed dword, caller pops it
    DWORD r;
    void* fn = (void*)S3cThunk;
    __asm {
        push n
        mov ecx, addr
        mov edx, data
        call fn
        add esp, 4
        movzx eax, al
        mov r, eax
    }
    return r;
}

static void TestS3ssCompat() {
    const char* kSha = kS3cBuggy[0].sha;
    const DWORD callAt = kS3cBuggy[0].call - kS3cCallE8, fnAt = kS3cBuggy[0].fn;
    u8* m = (u8*)VirtualAlloc(NULL, 0x30000, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    assert(m);
    auto reset = [&]() {
        memset(m, 0xCC, 0x30000);
        memcpy(m + callAt, kS3cCall, sizeof(kS3cCall));
        memcpy(m + fnAt, kS3cFn, sizeof(kS3cFn));
        const DWORD reloc = 0x0F000000;  // relocated absolute dwords: masked, still accepted
        for (int off : {6, 11, 26}) *(DWORD*)(m + fnAt + off) += reloc;
    };
    void* const orig0 = g_s3cOrig;

    // 1. unknown sha (a fixed S3SS) stands aside, nothing written
    reset();
    assert(!S3cInstall(m, "0000000084528748ee1f19c1f7cb2183e8e8cb7f7cae57cdb64098a7ebcdc13a"));
    assert(!memcmp(m + callAt, kS3cCall, sizeof(kS3cCall)));
    // 2. known sha, changed bytes (call window, then SafeReassert head) stand aside, nothing written
    m[callAt + 2] ^= 1;
    assert(!S3cInstall(m, kSha));
    assert(m[callAt + kS3cCallE8] == 0xE8 && !memcmp(m + callAt + kS3cCallE8, kS3cCall + kS3cCallE8, 5));
    reset();
    m[fnAt + 0x48] ^= 1;  // the `mov edi,[ebp+8]` (size argument) byte
    assert(!S3cInstall(m, kSha));
    assert(!memcmp(m + callAt, kS3cCall, sizeof(kS3cCall)));
    // 3. known sha (upper case too) + bytes: the call goes to S3cThunk, the original target is SafeReassert
    reset();
    char up[65];
    strcpy_s(up, kSha);
    _strupr_s(up);
    assert(S3cInstall(m, up));
    DWORD rel;
    memcpy(&rel, m + callAt + kS3cCallE8 + 1, 4);
    assert(m[callAt + kS3cCallE8] == 0xE8 && (DWORD)(uintptr_t)(m + callAt + kS3cCallE8 + 5) + rel == (DWORD)(uintptr_t)S3cThunk);
    assert(g_s3cOrig == m + fnAt);
    assert(!memcmp(m + callAt, kS3cCall, kS3cCallE8) && !memcmp(m + callAt + kS3cCallE8 + 5, kS3cCall + kS3cCallE8 + 5, 6));

    // 4. S3cFix / S3cThunk: exe-global, live, other-size targets untouched; stale heap target redirected; no WorldManager = skipped
    const volatile DWORD* slot0 = g_s3cWmSlot;
    const DWORD lo0 = g_s3cExeLo, hi0 = g_s3cExeHi;
    static DWORD exeGlobal, wmSlot;
    const u8* self = (const u8*)GetModuleHandleW(NULL);
    g_s3cExeLo = (DWORD)(uintptr_t)self;
    g_s3cExeHi = g_s3cExeLo + ((const IMAGE_NT_HEADERS*)(self + ((const IMAGE_DOS_HEADER*)self)->e_lfanew))->OptionalHeader.SizeOfImage;
    u8* wm = (u8*)malloc(0x1F0);
    u8* oldWm = (u8*)malloc(0x1F0);
    assert(wm && oldWm);
    wmSlot = (DWORD)(uintptr_t)wm;
    g_s3cWmSlot = &wmSlot;
    g_s3cOrig = (void*)S3tOrig;
    const float five = 5.0f;
    const DWORD live = (DWORD)(uintptr_t)(wm + 0xEC), stale = (DWORD)(uintptr_t)(oldWm + 0xEC), glob = (DWORD)(uintptr_t)&exeGlobal;
    const LONG stale0 = g_s3cStale, redir0 = g_s3cRedir, skip0 = g_s3cSkip;
    assert(S3cFix(glob, (const u8*)&five, 4) == glob);
    assert(S3cFix(live, (const u8*)&five, 4) == live);
    assert(S3cFix(stale, (const u8*)&five, 12) == stale);  // BradyBunchBegone-size writes: not ours
    assert(g_s3cStale == stale0);
    assert(S3cFix(stale, (const u8*)&five, 4) == (kS3cRedirect ? live : 0));
    assert(g_s3cStale == stale0 + 1 && (kS3cRedirect ? g_s3cRedir == redir0 + 1 : g_s3cSkip == skip0 + 1));
    // through the thunk, as S3SS calls it
    g_s3tHits = 0;
    assert(S3tCall(glob, &five, 4) == 1 && g_s3tHits == 1 && g_s3tEcx == glob && g_s3tEdx == (DWORD)(uintptr_t)&five && g_s3tSize == 4);
    assert(S3tCall(stale, &five, 4) == 1);
    if (kS3cRedirect) assert(g_s3tHits == 2 && g_s3tEcx == live && g_s3tEdx == (DWORD)(uintptr_t)&five && g_s3tSize == 4);
    else assert(g_s3tHits == 1);
    wmSlot = 0;  // between worlds: [0x11ECBC4] cleared, nothing to redirect to
    const DWORD hits = g_s3tHits;
    assert(S3tCall(stale, &five, 4) == 1 && g_s3tHits == hits && g_s3cSkip >= skip0 + 1);
    assert(S3tCall(glob, &five, 4) == 1 && g_s3tHits == hits + 1 && g_s3tEcx == glob);

    free(wm), free(oldWm);
    VirtualFree(m, 0, MEM_RELEASE);
    g_s3cWmSlot = slot0, g_s3cExeLo = lo0, g_s3cExeHi = hi0, g_s3cOrig = orig0;
    printf("s3ss compat ok\n");
}
