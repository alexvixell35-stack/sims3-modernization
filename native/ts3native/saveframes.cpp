// [EaCrashFixes] SavedFrameGuard (docs/293, default 1): a saved task frame that would resume in the middle of an
// instruction is repaired or its stack is dropped at load. Included by ts3native.cpp after monojit.cpp (JitLen,
// JitSwitchLen, JitMethodName) and before selftest's saveframes-test.h.
//
// EA's save keeps each parked interpreter frame as ScriptCore.TaskFrame { method, IL checksum, mIPOffset, ... } and
// mIPOffset is the raw word index into the method's transformed (mint) code [verified: the yield save 0xE53620 cell +8,
// TaskControl_SetTaskContext 0xD844D4 copies element +8 into cell +8, the resume 0xE53F18 adds it to the code the method
// has then]. The only guard is the IL checksum, so the index is right only while the transform gives the same word
// layout as in the saving session. It does not when a callee the transform inlines (0xE5DE08-0xE5E3A9: a body that
// starts with ret, a static or instance field getter, a field setter) is replaced by a body it does not inline, or the
// other way round: every caller's code after that site moves by the length difference (ts3fixes' swap of
// Lot.get_ChanceOfGoingToCommunityLotByCar made MetaRabbitHoleInteraction.Run one word shorter; a save from a vanilla
// layout then resumed one word past `call DoLoop` and died at McCall+0x42, 2026-11-19). ts3fixes no longer makes such
// swaps (Fixes.Patch refuses them); this guard is for saves that already hold a moved index (written by our 11-15 to
// 11-19 test builds, or by any other mod that replaces an inlined method) and for the same change in the future.
//
// TaskControl_SetTaskContext 0xD84360 (cdecl: task id lo, hi, TaskContext*; al = restored) is detoured. Per frame of the
// context (managed TaskFrame[] at +4: length +8, elements of 0x28 bytes from +0x10; element +0 MonoMethod*, +8 ip
// index) the method is transformed if it was not yet (EA does that at the first resume) and its code is walked from
// word 0 with EA's instruction lengths:
//   - the index is an instruction boundary: untouched (this is every frame of a save with the right layout);
//   - it is not: resuming there runs an operand as an opcode. If exactly one boundary that follows a call-family
//     instruction lies within 3 words, the index is set to it (the differences seen are 1 word per moved site);
//     otherwise the IC answers false without touching the task, which is what EA does for a frame it cannot restore
//     (SavedTaskContext.Restore false: the object starts a fresh task, the Sim is reset).
// Code it cannot walk (a compiled method's entry word, an unknown opcode) is left alone. Any Sim: a dropped stack on a
// household Sim replaces a certain bad resume, never a healthy one.
// EA 1.69 / retail: 0xD84360 and 0xE52B90 are 0 rows (unresolved, docs/293 §6): the detour finds no prologue and logs SKIP.
static const DWORD SF_SET = TS3A(0x00D84360), SF_RM = TS3A(0x00E52B90), SF_XFORM = TS3A(0x00E643B0), SF_DOMAIN = TS3A(0x00E75260);
static const BYTE kSfSetPro[9] = {0x81, 0xEC, 0xC8, 0x00, 0x00, 0x00, 0x53, 0x55, 0x56};  // sub esp, 0xC8; push ebx; push ebp; push esi
typedef BYTE(__cdecl* SfSetFn)(DWORD idLo, DWORD idHi, BYTE* ctx);
static SfSetFn g_sfNext;
static LONG g_sfOn, g_sfFrames, g_sfRepaired, g_sfDropped, g_sfUnwalked, g_sfOdd;

#include "saveframes-core.h"  // SfCallOp, SfClassify (shared with native/monohost: the SF-roundtrip check)

static int SfLen(const unsigned short* p) { return *p == 0xE8 ? JitSwitchLen(p) : JitLen(p); }

// false = drop this context (the caller answers the IC with false)
static bool SfCheck(BYTE* ctx) {
    BYTE* arr = *(BYTE**)(ctx + 4);
    if (!arr) return true;
    const LONG n = *(const LONG*)(arr + 8);
    if (n <= 0 || n > 64) return true;
    void* tc = TlsGetValue(*(const DWORD*)(uintptr_t)TS3A(0x011F4160));  // this thread's interpreter context (the transform's second argument)
    if (!tc) return true;
    void* domain = ((void*(__cdecl*)())(uintptr_t)SF_DOMAIN)();
    for (LONG i = 0; i < n; i++) {
        BYTE* el = arr + 0x10 + 0x28 * i;
        void* method = *(void**)el;
        if (!method) continue;  // EA's own refusal
        BYTE* rm = ((BYTE*(__cdecl*)(void*, void*))(uintptr_t)SF_RM)(domain, method);
        if (!rm) continue;
        if (!*(void**)rm && ((void*(__cdecl*)(BYTE*, void*))(uintptr_t)SF_XFORM)(rm, tc)) continue;  // the transform threw: EA meets that at the resume
        const unsigned short* code = *(const unsigned short* const*)rm;
        if (!code) continue;
        const int words = *(const unsigned short*)(rm + 0x3A), idx = *(const LONG*)(el + 8);
        int fixed;
        bool afterCall;
        InterlockedIncrement(&g_sfFrames);
        const int v = SfClassify(code, words, idx, SfLen, &fixed, &afterCall);
        if (v < 0) { InterlockedIncrement(&g_sfUnwalked); continue; }
        if (v == 0) { if (!afterCall && i < n - 1) InterlockedIncrement(&g_sfOdd); continue; }
        char name[200];
        JitMethodName(rm, name, sizeof(name));
        unsigned short w[8] = {};
        for (int k = 0; k < 8; k++) if (idx - 4 + k >= 0 && idx - 4 + k < words) w[k] = code[idx - 4 + k];
        if (v == 1) {
            *(LONG*)(el + 8) = fixed;
            if (InterlockedIncrement(&g_sfRepaired) <= 40)
                Log("ea fix: SavedFrameGuard: frame %ld of %ld in %s: saved ip word %d of %d is inside an instruction (words %04X %04X %04X %04X | %04X %04X %04X %04X), set to the call return at word %d",
                    i, n, name, idx, words, w[0], w[1], w[2], w[3], w[4], w[5], w[6], w[7], fixed);
            continue;
        }
        if (InterlockedIncrement(&g_sfDropped) <= 40)
            Log("ea fix: SavedFrameGuard: frame %ld of %ld in %s: saved ip word %d of %d is inside an instruction (words %04X %04X %04X %04X | %04X %04X %04X %04X) and no single call return is near: this saved stack is dropped, the object starts a fresh task",
                i, n, name, idx, words, w[0], w[1], w[2], w[3], w[4], w[5], w[6], w[7]);
        return false;
    }
    return true;
}

static BYTE __cdecl SfSetHook(DWORD idLo, DWORD idHi, BYTE* ctx) {
    if (g_sfOn && ctx) {
        bool keep = true;
        __try { keep = SfCheck(ctx); } __except (EXCEPTION_EXECUTE_HANDLER) { keep = true; }
        if (!keep) return 0;
    }
    return g_sfNext(idLo, idHi, ctx);
}

static void SfLogCounters() {
    if (g_sfOn) Log("saved frame guard: %ld frames checked at load, %ld repaired, %ld stacks dropped, %ld not walked, %ld boundaries that follow no call (left alone)", g_sfFrames, g_sfRepaired, g_sfDropped, g_sfUnwalked, g_sfOdd);
}

static void InstallSavedFrameGuard(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"EaCrashFixes", L"SavedFrameGuard", 1, ini)) { Log("saved frame guard disabled (SavedFrameGuard=0)"); return; }
    if (!InstallDetour("saved frame guard (TaskControl_SetTaskContext 0xD84360)", SF_SET, kSfSetPro, sizeof(kSfSetPro), (void*)SfSetHook, &g_sfNext)) return;
    g_sfOn = 1;
    Log("OK   saved frame guard: a saved task frame inside an instruction is repaired or its stack dropped at load ([EaCrashFixes] SavedFrameGuard=1)");
}
