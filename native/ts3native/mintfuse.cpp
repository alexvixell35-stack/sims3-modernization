// [MintFuse] Sub (docs/139 §4): sub-run fusion. EA's peephole 0xE5C660 hands the matcher 0xD89930 one whole run (no branch
// target inside, cut before every call/ret/newobj-class op) and fuses only when the WHOLE run equals one of the 22 descriptors
// at 0x108FCD8. This replacement also fuses descriptors found inside a run: the placement with the fewest dispatches (longer
// descriptor on a tie). Same encoding (first opcode word replaced, operands and later words in place), so branch targets and
// fault ips stay as docs/130 §9.1. Included by ts3native.cpp and by native/monohost/monohost.cpp (inside namespace mt, MONOHOST_MINTFUSE=sub). The includer
// defines MF_CTR, a struct with mfReady, mfRuns, mfWhole, mfInner (ts3native: ts3native_counters v164).

#ifndef MF_CTR
#error define MF_CTR (the struct holding the mf* counters) before including mintfuse.cpp
#endif

static const DWORD MF_SITE = 0x00E5C8CA, MF_MATCHER = 0x00D89930, MF_OPLEN = 0x01195088, MF_DESC = 0x0108FCD8;
static const int MF_BASE_OPS = 0x1A9, MF_DESCS = 22, MF_DESC_SIZE = 0x48;
static const unsigned short MF_SUPER = 0x1AA;

// win[n]: the run's opcodes without nops; code: the run's first instruction word. A superop goes to *out for a whole-run match
// (the caller writes it, EA's own case) and straight into the code for an inner one. oplen / desc are parameters for selftest.
static void MfFuse(const unsigned short* win, int n, unsigned short* out, unsigned short* code, const u8* oplen, const u8* desc) {
    if (n > 32) return;  // EA's window holds at most 32 (docs/139 §4.1); pos[] and the cost table assume it
    unsigned short* pos[32];
    bool gap[33] = {};  // gap[k]: an instruction not in the window (nop, the only "skip" op) sits right before op k
    unsigned short* p = code;
    int k = 0;
    for (int guard = 0; k < n && guard < 64; guard++) {
        const unsigned short op = *p;
        const int len = op < MF_BASE_OPS && oplen[op] ? oplen[op] : -1;
        if (len < 0) return;  // switch or out of table: leave the run alone
        if (op == win[k]) pos[k++] = p; else gap[k] = true;
        p += len;
    }
    if (k < n) return;  // more nops than the guard walks: pos[] is incomplete, leave the run alone
    MF_CTR.mfRuns++;
    // fewest dispatches over the run (right to left: cost[i] = dispatches for win[i..n)); equal cost keeps the longer descriptor.
    // Greedy left to right lost fusions: `brfalse.i4.s, ldloc.i4, add1.i4, stloc.i4` took 0x1AE + 2 ops instead of brfalse + 0x1B2.
    unsigned char cost[33], pick[32], plen[32];
    cost[n] = 0;
    for (int i = n - 1; i >= 0; i--) {
        cost[i] = (unsigned char)(1 + cost[i + 1]), pick[i] = 0, plen[i] = 1;
        for (int j = 1; j <= MF_DESCS; j++) {
            const u8* e = desc + MF_DESC_SIZE * j;
            const int m = *(const unsigned short*)(e + 4);
            if (m < 2 || m > n - i) continue;
            bool ok = !memcmp(e + 6, win + i, 2 * m);
            for (int q = i + 1; ok && q < i + m; q++) ok = !gap[q];
            const int c = 1 + cost[i + m];
            if (ok && (c < cost[i] || (c == cost[i] && m > plen[i]))) cost[i] = (unsigned char)c, pick[i] = (unsigned char)j, plen[i] = (unsigned char)m;
        }
    }
    for (int i = 0; i < n; i += plen[i]) {
        if (!pick[i]) continue;
        if (i == 0 && plen[i] == n) *out = (unsigned short)(MF_SUPER + pick[i]), MF_CTR.mfWhole++;
        else *pos[i] = (unsigned short)(MF_SUPER + pick[i]), MF_CTR.mfInner++;
    }
}
#ifdef MF_TICKS
static bool g_mfEaTimed;
#endif
static int __cdecl MfMatch(const unsigned short* win, int n, unsigned short* out, unsigned short* code) {
#ifdef MF_TICKS  // monohost only: QPC ticks spent in the matcher (transform-time cost, docs/139 §4)
    LARGE_INTEGER t0, t1; QueryPerformanceCounter(&t0);
    if (g_mfEaTimed) {  // MONOHOST_MINTFUSE=eatime: EA's own matcher, timed the same way (the base to compare against)
        const int r = ((int(__cdecl*)(const unsigned short*, int, unsigned short*))(uintptr_t)MF_MATCHER)(win, n, out);
        QueryPerformanceCounter(&t1); MF_TICKS += t1.QuadPart - t0.QuadPart; MF_CTR.mfRuns++;
        return r;
    }
#endif
    MfFuse(win, n, out, code, (const u8*)(uintptr_t)MF_OPLEN, (const u8*)(uintptr_t)MF_DESC);
#ifdef MF_TICKS
    QueryPerformanceCounter(&t1); MF_TICKS += t1.QuadPart - t0.QuadPart;
#endif
    return 0;  // the caller stores this only when the pass's stats argument is set; its one call 0xE63C2F passes 0 [verified]
}
#pragma warning(push)
#pragma warning(disable : 4414)
static __declspec(naked) void MfShim() {  // replaces `call 0xD89930` at 0xE5C8CA; edi = run start (word index)
    __asm {
        mov eax, [esp + 0x80]  // the pass's first argument (transform context; the caller reads it at [esp+0x70] before its 3 pushes)
        mov eax, [eax + 0x38]  // mint code base
        lea eax, [eax + edi * 2]
        push eax
        push dword ptr [esp + 0x10]
        push dword ptr [esp + 0x10]
        push dword ptr [esp + 0x10]
        call MfMatch
        add esp, 16
        ret
    }
}
#pragma warning(pop)
// Before MonoScriptHost_Init 0xD7E560 transforms anything (DllMain). Any other bytes at the site (another exe, another hook) refuse.
static bool MfInstall() {
    const u8* at = (const u8*)(uintptr_t)MF_SITE;
    if (!Readable(at, 5) || at[0] != 0xE8 || MF_SITE + 5 + *(const int*)(at + 1) != MF_MATCHER) {
        Log("SKIP mintfuse: 0x%08lX is not `call 0x%08lX` (Steam 1.67 only, or another hook owns it)", MF_SITE, MF_MATCHER);
        MF_CTR.mfReady = -1;
        return false;
    }
    u8 call[5] = {0xE8};
    *(DWORD*)(call + 1) = (DWORD)(uintptr_t)MfShim - (MF_SITE + 5);
    if (!Write((void*)at, call, 5)) { MF_CTR.mfReady = -1; return false; }
    MF_CTR.mfReady = 1;
    Log("OK   mintfuse: superops also fused inside runs (matcher call 0x%08lX)", MF_SITE);
    return true;
}
// ts3native.ini [MintFuse] Sub=0 (default) | 1. Transform time, no runtime switch: methods keep the form they were transformed in.
static bool ApplyMintFuse(const wchar_t* ini) {
    return GetPrivateProfileIntW(L"MintFuse", L"Sub", 0, ini) > 0 && MfInstall();
}
