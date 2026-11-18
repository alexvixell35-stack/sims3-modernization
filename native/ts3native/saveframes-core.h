// The saved-frame classifier of [EaCrashFixes] SavedFrameGuard (saveframes.cpp, docs/293), without game addresses: shared
// with native/monohost (SF-roundtrip runs it on code EA's transform made) and the selftest (saveframes-test.h).
#pragma once
static bool SfCallOp(unsigned op) { return (op >= 0xEA && op <= 0xF0) || op == 0xF2 || op == 0xF3 || op == 0xF4 || (op >= 0x1A6 && op <= 0x1A8); }

// The verdict for a saved index into `code` (`words` long): 0 = an instruction boundary, 1 = repaired (*fixed = the one
// call-return boundary within 3 words), 2 = not a boundary and no single candidate, -1 = the code cannot be walked that
// far. `len` gives an instruction's length in words (<= 0: unknown); *afterCall = the boundary follows a call.
template <class LenFn> static int SfClassify(const unsigned short* code, int words, int idx, LenFn len, int* fixed, bool* afterCall) {
    *fixed = idx, *afterCall = false;
    if (idx < 0 || idx > words) return 2;
    int cand = -1, nCand = 0;
    bool boundary = false, prevCall = false;
    for (int i = 0; i <= words;) {
        if (i == idx) boundary = true, *afterCall = prevCall;
        if (prevCall && i >= idx - 3 && i <= idx + 3) cand = i, nCand++;
        if (i > idx + 3 || i == words) break;
        const int n = len(code + i);
        if (n <= 0 || i + n > words) return boundary ? 0 : -1;
        prevCall = SfCallOp(code[i]);
        i += n;
    }
    if (boundary) return 0;
    if (nCand != 1) return 2;
    *fixed = cand;
    return 1;
}

