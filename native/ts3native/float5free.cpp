// [Float5Free] (docs/539 §19, 0.36.231): float-5 pointers handed to EA Free. CRASH-TRIAGE detector + guard, code and release ini Enabled=0.
// 11-09 crash (float5-split b-1, 0.36.229): GeneralAllocator_FreeCore 0x4E51BF read [0x40A00000-0xC]: EA Free 0x4E5920 (via 0x4E5945)
// <- EA_Free 0x4E3F84 <- eastl::deque dtor 0x727E5C <- SACS Walker dtor 0x800C99 (Walker vt 0x102000C, ctor 0x803E80, deque
// "SACS/Walker/mRequestQueue" at Walker+0x2C) <- Walker deleting dtor 0x801638 <- StateMachineDriver walker clear 0x8050D2 (vector
// +0x10..+0x14 of {key, Walker*}) <- 0x805135 <- 0x8051D8 <- 0x801C47, during world unload on travel [verified disassembly TS3W 1.67].
// The deque dtor 0x727E10 (thiscall(deque), plain ret; callers 0x7282DC, 0x800C94, 0x803F0D, 0xAA4C31) frees every subarray pointer
// in the pointer array from [deque+0x14] (begin slot) to [deque+0x24] (end slot), then the array [deque+0]. The bad slot was the end
// slot 0x33CDF4DC (= user + 0xC of the 0x20-byte pointer-array chunk), and that chunk was already free at the crash (next chunk's
// prev-size 0x20, in-use bit clear) [inferred from the xcpt stack and register dumps]: the Walker kept a dangling pointer array.
// Two checks, one ini section:
//   Free side: shares [UafCheck]'s one detour on EA Free 0x4E5920 (uafcheck.cpp UafHookInstall; one detour for both). A pointer
//   misaligned / outside user space, with an unreadable chunk header, or whose chunk is not in use (a free of freed memory), and in
//   0x40A00000..0x40A0FFFF also a zero-size header (0.36.233: the range alone no longer counts, real blocks live there) logs "float5 free: ..." (caller, the SACS Walker when the caller is 0x727E5C, the uaftag.h free tag, stack scan).
//   Guard=1: that free is skipped (the block leaks, the game keeps running).
//   Deque side: a detour on 0x727E10 checks the pointer array and every subarray pointer with the same test before EA frees them,
//   logs "float5 deque: ..." and with Guard=1 skips the whole destruction (the queue leaks).
// Log: first 50 lines of each kind a session, then every 100th; counts in every line.
static const DWORD kF5fRet727 = 0x00727E5C, kF5fWalkerVt = 0x0102000C, kF5dRetWalker = 0x00800C99;
static volatile LONG g_f5fBad, g_f5fSkip, g_f5dBad, g_f5dSkip, g_f5dChecks, g_f5fRangePass;
typedef void(__fastcall* F5dDtorFn)(DWORD* dq, void* edx);
static F5dDtorFn g_f5dDtor;
static const u8 kF5dPro[] = {0x56, 0x8B, 0xF1, 0x8B, 0x46, 0x08};  // push esi; mov esi,ecx; mov eax,[esi+8]

// NULL = a plausible block for EA Free. Chunk layout as alloc.cpp / UafWanted: size word at p-0xC, the next chunk's bit 0 = in use.
// 0.36.233: the 0x40A00000..0x40A0FFFF range alone is no longer a reason. The main heap has real blocks there: float5-round4
// all-off-1 flagged and leaked 26 distinct 16-aligned pointers (0x40A001F0..0x40A07BD0 and 0x40A00000 itself, each freed once,
// callers 0x57A9EB / 0x4461AA / 0x575CC7 / 0xC3A250 ..., 22 at world load #0). The 11-09 crash's float 5.0 failed on its header
// read ([0x40A00000-0xC] unmapped), which the header checks below still catch. In the range a plain header with size < 0x10
// (e.g. zero memory) is also bad; outside it that case passes as before.
static const char* F5fBad(DWORD p) {
    if ((p & 7) || p < 0x10000 || p >= 0xFFFE0000) return "not a heap pointer";
    const bool f5 = p >= 0x40A00000 && p < 0x40A10000;
    __try {
        const DWORD hdr = *(const volatile DWORD*)(uintptr_t)(p - 0xC), ch = hdr & 0x7FFFFFF8;
        if (f5 && !(hdr & 0x80000006) && ch < 0x10) return "0x40A00000 range (float 5.0), chunk header invalid";
        if (!(hdr & 0x80000006) && ch >= 0x20 && !(*(const volatile DWORD*)(uintptr_t)(p - 0x10 + ch + 4) & 1))
            return f5 ? "0x40A00000 range (float 5.0), chunk not in use (freed)" : "chunk not in use (freed)";
    } __except (EXCEPTION_EXECUTE_HANDLER) { return f5 ? "0x40A00000 range (float 5.0), chunk header unreadable" : "chunk header unreadable"; }
    return NULL;
}
static bool F5fLogNow(LONG n) { return n <= 50 || n % 100 == 0; }
static void F5fStack(const DWORD* sp, char* out, size_t cap, int* n) {
    DWORD f[10] = {};
    *n = CrStack((DWORD)(uintptr_t)sp, f, 10);
    CtFrames(f, *n, out, cap);
}
static LONG F5fWorldSec() { return g_udWorldTick ? (LONG)(GetTickCount() - g_udWorldTick) / 1000 : -1L; }

// From UafFreeHook (ra = its return address slot: [0] EA_Free 0x4E3F84 / Object free 0x4E3EFD or the direct caller, [2] their
// caller). Returns true = skip this free (Guard=1).
static bool F5fFreeCheck(void* heap, DWORD p, const DWORD* ra) {
    const char* why = F5fBad(p);
    if (!why) {
        if (p >= 0x40A00000 && p < 0x40A10000) {  // a real block in the range: passed, logged (first 20) so a float 5.0 that passes shows up
            const LONG k = InterlockedIncrement(&g_f5fRangePass);
            if (k <= 20)
                Log("float5 free: ptr 0x%08lX in the 0x40A00000 range has an in-use chunk header (size word 0x%08lX): passed to EA; caller 0x%08lX; "
                    "thread %lu; save #%ld, %ld s after world load #%ld; range passes %ld", p, *(const volatile DWORD*)(uintptr_t)(p - 0xC),
                    ra[0] == 0x004E3F84 || ra[0] == 0x004E3EFD ? ra[2] : ra[0], GetCurrentThreadId(), g_ctInvSave, F5fWorldSec(), g_udWorlds, k);
        }
        return false;
    }
    const LONG n = InterlockedIncrement(&g_f5fBad);
    if (g_f5fGuard) InterlockedIncrement(&g_f5fSkip);
    UafNearLog(p, "bad free pointer");  // [UafCheck] (no-op when off)
    if (p >= 0x40A00000 && p < 0x40A10000) F5wBad("EA Free of a float-5 range pointer (where it was stored is unknown)", 0, 0, p);  // docs/539 §21
    if (!F5fLogNow(n)) return g_f5fGuard;
    const DWORD caller = ra[0] == 0x004E3F84 || ra[0] == 0x004E3EFD ? ra[2] : ra[0];
    char own[128] = "";
    if (caller == kF5fRet727) __try {  // 0x727E10's pushes above EA_Free's argument: ebx, edi, esi = the deque
            const DWORD dq = ra[6], w = dq - 0x2C, vt = *(const volatile DWORD*)(uintptr_t)w;
            sprintf_s(own, "; deque 0x%08lX (%s 0x%08lX vt 0x%08lX)", dq, vt == kF5fWalkerVt ? "SACS Walker mRequestQueue, Walker" : "not a SACS Walker: base", w, vt);
        } __except (EXCEPTION_EXECUTE_HANDLER) { strcpy_s(own, "; deque unreadable"); }
    char fr[10 * 44 + 8];
    int nf;
    F5fStack(ra, fr, sizeof(fr), &nf);
    Log("float5 free: ptr 0x%08lX (%s) caller 0x%08lX heap 0x%08lX%s%s; free tag %s (site ts3native+0x%lX); %s; thread %lu; save #%ld, %ld s after "
        "world load #%ld; bad %ld, skipped %ld; stack scan (%d):%s", p, why, caller, (DWORD)(uintptr_t)heap, heap == *g_uafHeapAt ? " (main)" : "", own,
        UafTagName(t_uafTag.tag), UafSiteOff(t_uafTag.site), g_f5fGuard ? "free skipped (Guard=1)" : "passed to EA (Guard 0)", GetCurrentThreadId(),
        g_ctInvSave, F5fWorldSec(), g_udWorlds, g_f5fBad, g_f5fSkip, nf, fr);
    return g_f5fGuard;
}

// Deque check before 0x727E10 frees it: returns 1 = bad (selftest), 0 = fine.
static int F5dCheck(const DWORD* dq, DWORD ret, const DWORD* sp) {
    InterlockedIncrement(&g_f5dChecks);
    DWORD arr = 0, n = 0, b = 0, e = 0, at = 0, val = 0;
    const char* why = NULL;
    __try {
        arr = dq[0], n = dq[1], b = dq[5], e = dq[9];
        if (!arr) return 0;  // EA frees nothing
        if ((why = F5fBad(arr)) != NULL) val = arr;
        else if ((b | e) & 3 || b < arr || e < b || (e - arr) / 4 >= n) why = "begin / end slot outside the pointer array", val = b;
        else
            for (DWORD s = b; s <= e; s += 4) {
                const DWORD v = *(const volatile DWORD*)(uintptr_t)s;
                if (v && (why = F5fBad(v)) != NULL) { at = s, val = v; break; }
            }
    } __except (EXCEPTION_EXECUTE_HANDLER) { why = "deque unreadable"; }
    if (!why) return 0;
    const LONG k = InterlockedIncrement(&g_f5dBad);
    if (g_f5fGuard) InterlockedIncrement(&g_f5dSkip);
    UafNearLog(arr, "deque pointer array"), UafNearLog(val, "deque bad value");
    char site[112];  // docs/539 §21
    sprintf_s(site, "float5 deque (%s): %s", ret == kF5dRetWalker ? "SACS Walker mRequestQueue" : "eastl deque", why);
    F5wBad(site, (DWORD)(uintptr_t)dq, at, val);
    if (!F5fLogNow(k)) return 1;
    char own[96] = "";
    if (ret == kF5dRetWalker) sprintf_s(own, " (SACS Walker 0x%08lX mRequestQueue)", (DWORD)(uintptr_t)dq - 0x2C);
    char fr[10 * 44 + 8];
    int nf;
    F5fStack(sp, fr, sizeof(fr), &nf);
    Log("float5 deque: deque 0x%08lX%s caller 0x%08lX: %s, value 0x%08lX at 0x%08lX (pointer array 0x%08lX size %lu, begin slot 0x%08lX, end slot "
        "0x%08lX); %s; thread %lu; save #%ld, %ld s after world load #%ld; bad %ld, skipped %ld, checks %ld; stack scan (%d):%s", (DWORD)(uintptr_t)dq, own,
        ret, why, val, at, arr, n, b, e, g_f5fGuard ? "destruction skipped, queue leaks (Guard=1)" : "passed to EA (Guard 0)", GetCurrentThreadId(),
        g_ctInvSave, F5fWorldSec(), g_udWorlds, g_f5dBad, g_f5dSkip, g_f5dChecks, nf, fr);
    return 1;
}
static void __fastcall F5dDtorHook(DWORD* dq, void* edx) {
    const DWORD* sp = (const DWORD*)_AddressOfReturnAddress();
    if (dq && F5dCheck(dq, *sp, sp) && g_f5fGuard) return;
    g_f5dDtor(dq, edx);
}

// After ApplyUafCheck (shares its Free detour).
static bool ApplyFloat5Free(const wchar_t* ini) {
    if (GetPrivateProfileIntW(L"Float5Free", L"Enabled", 0, ini) <= 0) return false;
    if (g_apexFound && g_apexStandDown) { Log("SKIP float5 free: Apex Radiance loaded (the allocator is its area; [Compat] ApexStandDown=1)"); return false; }
    g_f5fGuard = GetPrivateProfileIntW(L"Float5Free", L"Guard", 0, ini) > 0;
    g_f5fOn = UafHookInstall("float5 free");
    if (!g_f5dDtor) g_f5dDtor = (F5dDtorFn)InstallDetour("eastl deque dtor 0x727E10 (float5 free)", 0x00727E10, kF5dPro, sizeof(kF5dPro), (void*)F5dDtorHook, &g_f5dDtor);
    Log("float5 free %s (CRASH-TRIAGE, docs/539 §19): EA Free 0x4E5920 pointers not a heap pointer / unreadable / free chunk (0x40A00000 range: also "
        "zero-size header; in-use range blocks pass, first 20 logged) logged (\"float5 free:\")%s; deque dtor 0x727E10 (SACS Walker mRequestQueue) pointer array and subarrays %s%s",
        g_f5fOn ? "on" : "off (no Free hook)", g_f5fOn ? (g_uafOn ? ", one detour shared with [UafCheck]" : ", own use of the shared detour") : "",
        g_f5dDtor ? "checked (\"float5 deque:\")" : "not checked (no hook)", g_f5fGuard ? "; Guard=1: bad frees / destructions skipped (leak)" : "; log only (Guard=0)");
    return g_f5fOn || g_f5dDtor;
}
