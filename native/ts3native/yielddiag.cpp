// [MonoDiag] YieldDiag (log only, default 0; detector round 3 for the rc-1118n crash at McCall+0x42, 2026-11-19).
// Included by ts3native.cpp after monojit.cpp (JitMethodName, JIT_OP) and savewrite.cpp (PatchCalls).
//
// What rounds 1-2 showed: after Interaction.DoLoop returns into MetaRabbitHoleInteraction`2.Run the interpreter runs
// one word past the instruction boundary: the frame's ip is 0xF5584E44 while `call DoLoop` (2 words) sits at ..E3E, so
// stloc's operand 0x0009 runs as opcode 0x09 with the next three words as its operands (0x0009006E as a 32-bit size:
// the vt cursor [ebp-0xC] ends 0x90058 below the frame, a vt pointer lands in the eval slot) and the next op, the call
// at ..E4C, finds one stack slot instead of two. EA's yield save 0xE53620 keeps a frame's ip as a word index into the
// RuntimeMethod's code (cell +8) and the resume 0xE53F18 adds it to the code the method has THEN [verified, capstone],
// so the index is only right while the code layout stays the same.
//
// This names which of the three it is:
//   resume: the one call of the cell free 0xE535E0 (0xE5404D, end of EA's resume loop, cells still whole) goes through
//     YdResumeHook. Per frame below the top: the words before the resume ip must be a call (0xEA two words back, 0xEC
//     four words back, or a compiled word JIT_OP there); anything else is logged as SUSPECT with the state's resume
//     count (0 = the first resume of this state in the session: the cells came from the save's task context). Frames
//     of a method with "RabbitHole" in its name are logged on their first resumes either way.
//   retransform: IlpXform (owner of every transform) keeps the old code of a method that already had some and
//     YdRetransform logs a repeat transform whose word count or words differ (compiled / fused opcode words excepted).
static const DWORD YD_FREE_SITE = TS3A(0x00E5404D), YD_FREE = TS3A(0x00E535E0);
typedef void(__cdecl* YdFreeFn)(BYTE* state);
static LONG g_ydNoCode, g_ydDump, g_ydSuspect, g_ydWatch, g_ydPairs, g_ydRe, g_ydReDiff, g_ydResumes;
struct YdState { const BYTE* state; LONG n; };
static YdState g_ydStates[4096];  // direct-mapped: a collision restarts the count (the line says "resume #n")
static DWORD g_ydPair[48];        // distinct (word 4 back, word 2 back) pairs seen before a non-top resume ip

static bool YdName(const BYTE* rm, char* b, int n) {
    b[0] = 0;
    __try { JitMethodName(rm, b, n); } __except (EXCEPTION_EXECUTE_HANDLER) { strcpy_s(b, n, "?"); }
    return strstr(b, "RabbitHole") != NULL;
}

static void YdResume(const BYTE* state) {
    const LONG depth = *(const LONG*)(state + 0x140);
    if (depth < 0 || depth > 62) return;
    InterlockedIncrement(&g_ydResumes);
    YdState& s = g_ydStates[((DWORD)(uintptr_t)state >> 4) & 4095];
    if (s.state != state) s.state = state, s.n = 0;
    const LONG nth = s.n++;
    for (LONG d = 0; d <= depth; d++) {
        const BYTE* cell = *(const BYTE* const*)(state + 0x3C + 4 * d);
        if (!cell) continue;
        const BYTE* rm = *(const BYTE* const*)cell;
        const unsigned short* code = rm ? *(const unsigned short* const*)rm : NULL;
        if (!code) {  // round 4: a frame whose method has no code at the resume
            char nm[200] = "?";
            if (rm) YdName(rm, nm, sizeof(nm));
            if (InterlockedIncrement(&g_ydNoCode) <= 40) Log("yielddiag resume: state %08lX resume #%ld frame %ld of %ld %s: NO CODE (cell %08lX, rm %08lX, ip word %ld)", (DWORD)(uintptr_t)state, nth, d, depth, nm, (DWORD)(uintptr_t)cell, (DWORD)(uintptr_t)rm, *(const LONG*)(cell + 8));
            continue;
        }
        const LONG idx = *(const LONG*)(cell + 8), words = *(const unsigned short*)(rm + 0x3A);
        unsigned short w[6] = {};
        for (int i = 0; i < 6; i++) if (idx - 4 + i >= 0 && idx - 4 + i < words) w[i] = code[idx - 4 + i];
        const bool top = d == depth;
        const bool call = idx >= 2 && idx <= words && (w[2] == 0xEA || w[2] == 0xEF || w[2] == JIT_OP || (idx >= 4 && (w[0] == 0xEC || w[0] == JIT_OP)));
        bool newPair = false;
        if (!top) {
            const DWORD key = (DWORD)w[0] << 16 | w[2];
            int k = 0;
            while (k < g_ydPairs && g_ydPair[k] != key) k++;
            if (k == g_ydPairs && g_ydPairs < 48) g_ydPair[g_ydPairs++] = key, newPair = true;
        }
        const bool suspect = !top && !call;
        if (!suspect && !newPair && (nth > 2 || g_ydWatch >= 80)) continue;  // no name lookup on the common path
        char name[200];
        const bool watched = YdName(rm, name, sizeof(name));
        if (suspect ? InterlockedIncrement(&g_ydSuspect) > 60 : !newPair && (!watched || InterlockedIncrement(&g_ydWatch) > 80)) continue;
        if (watched && idx >= 0 && idx <= words && InterlockedIncrement(&g_ydDump) <= 30) {  // round 4: the code up to the resume ip
            char b[900];
            int n = sprintf_s(b, "yielddiag code: state %08lX frame %ld %s rm %08lX flags %08lX:", (DWORD)(uintptr_t)state, d, name, (DWORD)(uintptr_t)rm, *(const DWORD*)(rm + 0x44));
            for (LONG i = idx > 130 ? idx - 130 : 0; i < idx + 6 && i < words && n < 880; i++) n += sprintf_s(b + n, sizeof(b) - n, i == idx ? " |%04X" : " %04X", code[i]);
            Log("%s", b);
        }
        Log("yielddiag resume: state %08lX resume #%ld frame %ld of %ld %s: ip word %ld of %ld, stack %ld, vt off %ld, words %04X %04X %04X %04X | %04X %04X, ForceJIT byte %02X%s%s",
            (DWORD)(uintptr_t)state, nth, d, depth, name, idx, words, *(const LONG*)(cell + 0xC), *(const LONG*)(cell + 0x10), w[0], w[1], w[2], w[3], w[4], w[5],
            *g_jitForceJit, suspect ? " SUSPECT: the resume ip does not follow a call op" : "", newPair ? " (first of this word pair)" : "");
    }
}

static void __cdecl YdResumeHook(BYTE* state) {
    if (g_ydOn && state) {
        __try { YdResume(state); } __except (EXCEPTION_EXECUTE_HANDLER) {}
    }
    ((YdFreeFn)(uintptr_t)YD_FREE)(state);
}

// a repeat transform of rm: old = a copy of the code it had (at most 2048 words), oldWords its word count then
static void YdRetransform(BYTE* rm, const unsigned short* old, int oldWords, const unsigned short* oldAt) {
    InterlockedIncrement(&g_ydRe);
    __try {
        const unsigned short* code = *(const unsigned short* const*)rm;
        const int words = *(const unsigned short*)(rm + 0x3A);
        int at = -1;
        const int n = oldWords < words ? oldWords : words;
        for (int i = 0; i < n && i < 2048 && code; i++) {
            if (old[i] == code[i] || old[i] >= JIT_OP || code[i] >= JIT_OP) continue;  // compiled, fused or our own opcode words: same layout
            at = i;
            break;
        }
        if (at < 0 && words == oldWords) return;
        char name[200];
        const bool watched = YdName(rm, name, sizeof(name));
        if (InterlockedIncrement(&g_ydReDiff) > 40 && !watched) return;
        const int a = at < 0 ? n : at;
        Log("yielddiag retransform: %s: code %08lX (%d words) -> %08lX (%d words), first differing word %d: old %04X %04X %04X %04X new %04X %04X %04X %04X, ForceJIT byte %02X, thread %lu",
            name, (DWORD)(uintptr_t)oldAt, oldWords, (DWORD)(uintptr_t)code, words, at, a < oldWords && a < 2048 ? old[a] : 0, a + 1 < oldWords && a + 1 < 2048 ? old[a + 1] : 0,
            a + 2 < oldWords && a + 2 < 2048 ? old[a + 2] : 0, a + 3 < oldWords && a + 3 < 2048 ? old[a + 3] : 0, code && a < words ? code[a] : 0, code && a + 1 < words ? code[a + 1] : 0,
            code && a + 2 < words ? code[a + 2] : 0, code && a + 3 < words ? code[a + 3] : 0, *g_jitForceJit, GetCurrentThreadId());
    } __except (EXCEPTION_EXECUTE_HANDLER) {}
}

static void InstallYieldDiag(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"MonoDiag", L"YieldDiag", 0, ini)) return;
    static const CallSite kSite[1] = {{YD_FREE_SITE, YD_FREE, (void*)YdResumeHook}};
    if (!PatchCalls("yield diag (resume cell free call 0xE5404D)", kSite, 1)) return;
    g_ydOn = 1;
    Log("OK   yield diag: resume ips and repeat transforms are checked ([MonoDiag] YieldDiag=1, log only)");
}
