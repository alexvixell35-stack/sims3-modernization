// [AudioStateDup] (docs/549 §5, counters v919 ad*): EA leaks one 48-byte pool node per duplicate key in the audio state table.
// Default OFF. Included by ts3native.cpp after slotguard.cpp; selftest: audiodup-test.h.
//
// EA code [verified disassembly, TS3W 1.67 repack copy; names: OpenBarrio map, docs/547]:
//   AudioState_Add 0x862CC0 (thiscall, state; key low, key high, value; ret 0xC) pops a node from the free list at
//   state+0x34 (growing the pool through 0x8625B0 when it is empty; a failed grow returns at once), writes key +0x08/+0x0C,
//   value +0x14, flags +0x10 = 0, +0x20..+0x2F = 0, then calls AudioState_Insert 0x862910 on the 53-bucket table at
//   state+0x44. Insert walks the bucket (key low % 53, chain through +0x00 to null, low word compared first) and, when a node
//   with the same key exists, returns it with "inserted" 0 and does not link the new node. Add ignores that result: the node
//   is neither linked nor pushed back on the free list, and the pool (blocks of up to 0x154 nodes) never shrinks. The existing
//   node keeps its old value.
//   Only caller: 0x9435D0 (call at 0x943620), the audio component handler for object events 0, 1 and 7; state =
//   [audio + 0x27BF4], audio = [0x11D8C40] - 0x27BD0 (docs/193), so the table lives for the process. Event 5 (before destroy)
//   marks the node (AudioState_Mark 0x862980) and the per-frame Tick 0x862C20 -> Prune 0x862AC0 frees marked nodes. Any
//   object that gets two of the events 0 / 1 / 7 before its destroy loses one node per extra event [inferred: which events
//   fire for which objects is not traced; adDups measures it].
// Threads: every code reference to [audio+0x27BF4] is in the game audio class's event-writer slots (0x8650B0..0x865D86), its
//   Update 0x865E10 (Tick), the object event handlers 0x9435D0 / 0x944250 and the ctor / reset (0x868432, 0x8681DD)
//   [verified scan]. Update runs on the sim thread [measured, docs/35 §1]; the EA audio thread's loop (0x866BA0, 0x868D00,
//   0x8B6CE0, 0x8BFAE0) reaches none of the table functions through direct calls [verified, 6 levels; its virtual calls are
//   not followed]. So the walk below runs on the thread that writes the table, as Insert's own walk right after it does, and
//   needs no lock EA does not take either [inferred for the event writers: the object events fire on the sim thread].
//
// Enabled=1: Add is detoured. Before EA runs, the hook walks the same bucket with Insert's key test. A duplicate is counted;
// with Fix=1 the hook then returns without running EA's Add. Everything else runs EA unchanged.
// Fix=1 gives vanilla results: EA's duplicate path changes nothing that is ever read again (the node is unreachable and the
// existing node keeps its value either way); only the pool stops losing nodes.
// Household rule: not applicable (audio bookkeeping, no Sim decision).
// Log: the first 16 duplicates (key, value, caller, thread), then one line at every power of two.

static const DWORD AD_ADD = 0x00862CC0;
// push ebp; mov ebp,esp; and esp,-16; sub esp,0x18  (9 bytes, no relative operand, no branch into them)
static const u8 kAdAddPro[9] = {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0, 0x83, 0xEC, 0x18};
static const DWORD AD_BUCKETS = 0x44, AD_NBUCKETS = 53, AD_NODE_KEYLO = 0x08, AD_NODE_KEYHI = 0x0C, AD_NODE_BYTES = 48;
static const int kAdChainMax = 1 << 20;  // a chain longer than this is not a table we know: leave the call to EA

typedef void(__fastcall* AdAddFn)(u8* state, void* edx, DWORD keyLo, DWORD keyHi, DWORD value);
static AdAddFn g_adAdd;  // EA's Add (trampoline); selftest points it at a fake
static bool g_adFix;

// 1 = the bucket already holds keyLo:keyHi, 0 = it does not, -1 = unreadable or endless chain (EA decides, as without us)
static int AdHasKey(const u8* state, DWORD keyLo, DWORD keyHi) {
    __try {
        const u8* n = *(const u8* const*)(state + AD_BUCKETS + 4 * (keyLo % AD_NBUCKETS));
        for (int i = 0; n && i < kAdChainMax; i++, n = *(const u8* const*)n)
            if (*(const DWORD*)(n + AD_NODE_KEYLO) == keyLo && *(const DWORD*)(n + AD_NODE_KEYHI) == keyHi) return 1;
        return n ? -1 : 0;
    } __except (EXCEPTION_EXECUTE_HANDLER) { return -1; }
}

static void __fastcall AdAddHook(u8* state, void* edx, DWORD keyLo, DWORD keyHi, DWORD value) {
    Ts3nCounters& k = ts3native_counters;
    InterlockedIncrement(&k.adCalls);
    if (AdHasKey(state, keyLo, keyHi) == 1) {
        const LONG n = InterlockedIncrement(&k.adDups);
        const LONG skipped = g_adFix ? InterlockedIncrement(&k.adSkipped) : k.adSkipped;
        if (n <= 16 || !(n & (n - 1)))
            Log("audio state dup: key %08lX%08lX value 0x%08lX from 0x%08lX tid %lu; dups %ld of %ld adds, %s (%ld skipped, ~%ld KB kept)", keyHi,
                keyLo, value, (DWORD)(uintptr_t)_ReturnAddress(), GetCurrentThreadId(), n, k.adCalls,
                g_adFix ? "node kept (Fix=1)" : "EA leaks the node (Fix=0)", skipped, skipped * (LONG)AD_NODE_BYTES / 1024);
        if (g_adFix) return;
    }
    g_adAdd(state, edx, keyLo, keyHi, value);
}

// [AudioStateDup] Enabled=1: count duplicate adds (log only); Fix=1: also skip EA's leaking duplicate path
static bool ApplyAudioStateDup(const wchar_t* ini) {
    if (GetPrivateProfileIntW(L"AudioStateDup", L"Enabled", 0, ini) <= 0) return false;
    g_adFix = GetPrivateProfileIntW(L"AudioStateDup", L"Fix", 0, ini) > 0;  // before the jmp goes live
    if (!g_adAdd) InstallDetour("AudioState_Add (audio state dup)", AD_ADD, kAdAddPro, sizeof(kAdAddPro), (void*)AdAddHook, &g_adAdd);
    ts3native_counters.adMode = !g_adAdd ? -1 : g_adFix ? 2 : 1;
    Log("audio state dup %s (docs/549 §5): duplicate keys at AudioState_Add 0x862CC0 %s", g_adAdd ? "on" : "off (no hook)",
        g_adFix ? "skip EA's add (no pool node lost; same results)" : "are counted and logged only (EA leaks a 48-byte node each)");
    return g_adAdd != NULL;
}
