// [MonoFast] PairCount (docs/139 §3 G-I6 in game, TEST-RUN TOOL, default 0): mint opcode and opcode-pair counts of the sim
// thread, the hot-pair census a superinstruction pick needs (docs/139 rank 3: pick pairs from play, not from the host phases).
// Included by ts3native.cpp after simloop.cpp.
//
// Dispatch head 0xE544D5 [verified bytes, Steam 1.67 exe 2026-11-15]: movzx eax,word [edi] (0F B7 07); movzx edx,ax (0F B7 D0);
// cmp edx,0x1C0 (81 FA C0 01 00 00); ja 0xE596C2; jmp [edx*4+0xE59880]. Every EA handler and our mint stubs (Satb, MintDivGuard,
// MonoJit exits) end with a jmp to the head, and nothing else writes it (docs/68). The first 6 bytes become jmp MpHead + nop:
// MpHead counts the pair (previous op, this op) when it runs on the sim thread, then does the two movzx and jumps to the cmp at
// 0xE544DB. Flags are dead at the head (the cmp sets them); eax and edx are the head's own outputs; ecx is saved.
// Not counted: dispatches through the head copies inside [MonoFast] CallPath's handlers (mintcall.cpp p_next: the op after a
// fast call / ret), so the census job runs with CallPath=0; code [MonoJit] compiled runs no dispatch at all (that is the point:
// the census with JIT on shows what is still interpreted). Worker-thread dispatches (AutShadow, MonoThread) are not counted.
// Cost: ~10 instructions per dispatch on the sim thread, a 1.6 MB table (VirtualAlloc only when on): diagnostics, never timing.
// Report (sim thread, end of a Simulate iteration): every 5 min, after each save and at exit, 'mintpairs:' header, then the top
// MP_TOP pairs as 'mintpair: 0xAAA 0xBBB n' and the top MP_TOPOP ops as 'mintop: 0xAAA n' (counts since the load, 64-bit).
// Read with node tools/harness/mint-pairs.ts <ts3native.log> (names from docs/data/mint-opcodes.csv, fusable filter of docs/139 §3).
static const DWORD MP_SITE = TS3A(0x00E544D5);
static const u8 kMpHead[12] = {0x0F, 0xB7, 0x07, 0x0F, 0xB7, 0xD0, 0x81, 0xFA, 0xC0, 0x01, 0x00, 0x00};
enum { MP_OPS = 0x1C1, MP_TOP = 200, MP_TOPOP = 120, MP_REPORTS = 40 };
static DWORD g_mpBack = MP_SITE + 6;          // the cmp after the two movzx (selftest: its own return stub)
static volatile DWORD g_mpSimTid;             // 0 until the first sim iteration: nothing counted before
static DWORD g_mpPrev;                        // previous op on the sim thread
static unsigned __int64* g_mpPairs;           // [prev * MP_OPS + op]
static DWORD g_mpLastTick, g_mpReports;
static LONG g_mpLastSave = -1;

static __declspec(naked) void MpHead() {
    __asm {
        movzx eax, word ptr [edi]
        mov edx, dword ptr fs:[0x24]
        cmp edx, g_mpSimTid
        jne mp_done
        cmp eax, 0x1C0
        ja mp_done
        push ecx
        mov ecx, g_mpPrev
        mov g_mpPrev, eax
        imul ecx, ecx, 0x1C1            // MP_OPS
        add ecx, eax
        mov edx, g_mpPairs
        add dword ptr [edx + ecx * 8], 1
        adc dword ptr [edx + ecx * 8 + 4], 0
        pop ecx
    mp_done:
        movzx edx, ax
        jmp dword ptr [g_mpBack]
    }
}

// top n of v (indices into it, largest first); n small, so insertion into a sorted list
static int MpTop(const unsigned __int64* v, int len, int* idx, int n) {  // n >= 1
    int k = 0;
    for (int i = 0; i < len; i++) {
        if (!v[i] || (k == n && v[i] <= v[idx[k - 1]])) continue;
        int j = k < n ? k++ : n - 1;
        while (j > 0 && v[idx[j - 1]] < v[i]) idx[j] = idx[j - 1], j--;
        idx[j] = i;
    }
    return k;
}

static void MpReport(const char* why) {
    if (!g_mpPairs || g_mpReports >= MP_REPORTS) return;
    g_mpReports++;
    static unsigned __int64 ops[MP_OPS];
    unsigned __int64 total = 0;
    int distinct = 0, nops = 0;
    memset(ops, 0, sizeof(ops));
    for (int p = 0; p < MP_OPS; p++)
        for (int o = 0; o < MP_OPS; o++) { const unsigned __int64 n = g_mpPairs[p * MP_OPS + o]; if (n) ops[o] += n, total += n, distinct++; }
    for (int o = 0; o < MP_OPS; o++) nops += ops[o] != 0;
    Log("mintpairs: %s #%lu dispatches %I64u, distinct pairs %d, ops %d (sim thread, head 0x%08X)", why, g_mpReports, total, distinct, nops, MP_SITE);
    static int idx[MP_TOP];
    const int k = MpTop(g_mpPairs, MP_OPS * MP_OPS, idx, MP_TOP);
    for (int i = 0; i < k; i++) Log("mintpair: 0x%03X 0x%03X %I64u", idx[i] / MP_OPS, idx[i] % MP_OPS, g_mpPairs[idx[i]]);
    const int ko = MpTop(ops, MP_OPS, idx, MP_TOPOP);
    for (int i = 0; i < ko; i++) Log("mintop: 0x%03X %I64u", idx[i], ops[idx[i]]);
}

static void __cdecl MpTick() {  // sim thread, end of every Simulate iteration
    if (!g_mpSimTid) { g_mpSimTid = GetCurrentThreadId(); g_mpLastTick = GetTickCount(); g_mpLastSave = ts3native_counters.saveCount; return; }
    const DWORD now = GetTickCount();
    if (g_mpLastSave != ts3native_counters.saveCount) g_mpLastSave = ts3native_counters.saveCount, g_mpLastTick = now, MpReport("save");
    else if (now - g_mpLastTick >= 300000) g_mpLastTick = now, MpReport("interval");
}
static void MpExit() {  // DLL_PROCESS_DETACH: the table may be read while nothing runs any more
    if (g_mpPairs) __try { MpReport("exit"); } __except (EXCEPTION_EXECUTE_HANDLER) {}
}

// site = the 12 checked head bytes (game: MP_SITE; selftest: a copy)
static bool MpInstall(u8* site) {
    if (!Matches(site, kMpHead, sizeof(kMpHead))) return false;
    if (!g_mpPairs) g_mpPairs = (unsigned __int64*)VirtualAlloc(NULL, MP_OPS * MP_OPS * 8, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!g_mpPairs) return false;
    u8 j[6] = {0xE9, 0, 0, 0, 0, 0x90};
    const LONG rel = (LONG)((DWORD)(uintptr_t)MpHead - (DWORD)(uintptr_t)(site + 5));
    memcpy(j + 1, &rel, 4);
    return Write(site, j, sizeof(j));
}

static bool ApplyMintPairs(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"MonoFast", L"PairCount", 0, ini)) return false;
    if (!MpInstall((u8*)(uintptr_t)MP_SITE)) { Log("SKIP mint pair count: dispatch head bytes at 0x%08X differ or no table memory", MP_SITE); return false; }
    if (!SimLoopAdd(MpTick, "mint pair count: sim thread id + reports")) { Log("SKIP mint pair count reports: no Simulate iteration hook (head stays counted, no lines)"); }
    Log("OK   mint pair count ([MonoFast] PairCount, docs/139 §3, TEST-RUN TOOL): head 0x%08X -> counter, sim thread only, report every 5 min / save / exit%s",
        MP_SITE, GetPrivateProfileIntW(L"MonoFast", L"CallPath", 0, ini) > 0 ? "; CallPath on: ops after its fast call / ret are not counted" : "");
    return true;  // keep the log open for the report lines
}
