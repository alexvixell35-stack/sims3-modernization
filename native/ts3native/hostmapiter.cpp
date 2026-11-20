// [HostMapIterFix] (docs/67 §43, docs/51 §546): EA bug fix + detector, code default Enabled=0 (release ini decision after the repro).
// ProcessTaskState 0xD80BC0 finds an id in the script host's id -> handle hash map (host+0x1658, the map autshadow.cpp AsHostNode
// reads) at 0xD80EFE (`call 0xD726F0`, iterator {node, bucket slot} to [esp+14]/[esp+18]), keeps that iterator across `call
// 0xD80460` at 0xD81494 and erases with it at 0xD814B6 (`lea ecx,[esi+1660h]; call 0xD71E40`; this function's esi = host-8)
// [verified disassembly TS3W 1.67]. 0xD80460 runs managed code (0xEA1090); a managed object registration there (host vtable slot
// +0x14 0xD77920 -> insert 0xD72BF0) can grow the map past its bucket count [inferred]: rehash 0xD72170 moves every node to a new
// bucket array and frees the old one, the saved slot then points into freed memory, and erase 0xD71E40 walks a dead chain to NULL:
// AV reading 0x10 at 0xD71E7D (EA 0xD71EBD), Eli's EA 1.69 crash 2026-11-18 (15175 elements > 15173 buckets, new count 30727 =
// the next prime after 2 x 15173) [inferred from the dump arithmetic, docs/67 §43].
// Map layout (erase 0xD71E40, find 0xD726F0, insert 0xD72BF0, rehash 0xD72170): +4 bucket array (count + 1 entries, the last is
// the end marker 0xFFFFFFFF), +8 bucket count, +0xC element count, +0x10 max load, +0x14 growth, +0x18 next resize, +0x1C node
// pool; node 0x18 bytes: u64 id at +0, handle +8, next +0x10; bucket = idLo % count [verified]. Erase is thiscall(map, out iter*,
// node, slot), ret 0Ch, returns the out iterator; nothing reads that out iterator here (the loop's next find rewrites it).
// Fix: the call's rel32 goes to HmiErase, which checks that the slot lies in the live bucket array and that its chain holds the node.
// If not, the slot is re-found (idLo % count, then a scan of every chain) and EA's erase gets the valid iterator; a node that is in
// no chain (gone inside 0xD80460) is not erased again: the out iterator is end(). Every stale case is counted and logged ("host map
// iter:" line with the id; first 20 a session, then every 100th). Repair=0: detector only, EA gets its own iterator.
// The check covers the 29 bytes before the call (position-free) plus the call's target, and the erase prologue: another mod's patch
// there (none known, docs/68; S3PP only detours the entry 0xD80BC0) or another exe = SKIP.
// TestRehash=N (repro only, default 0): before each of the first N erases at this site, rehash the map (0xD72170, count + 1
// buckets): the same stale slot a rehash inside 0xD80460 leaves. Steam / retail only (no EA 1.69 address for 0xD72170).
static const DWORD HMI_SITE = TS3A(0x00D814B6), HMI_ERASE = TS3A(0x00D71E40);
static DWORD g_hmiRehashAt = TS3A(0x00D72170);  // variable: the EA 1.69 row is 0, a run-time check, not a constant one
// mov ecx,[esp+14h]; mov edx,[esp+18h]; sub esp,8; mov eax,esp; mov [eax],ecx; mov [eax+4],edx; lea eax,[esp+68h]; push eax;
// lea ecx,[esi+1660h]; then call 0xD71E40 (E8 + rel32, checked by target: EA 1.69 has another distance)
static const u8 kHmiSeq[0x1D] = {0x8B, 0x4C, 0x24, 0x14, 0x8B, 0x54, 0x24, 0x18, 0x83, 0xEC, 0x08, 0x8B, 0xC4, 0x89, 0x08,
                                 0x89, 0x50, 0x04, 0x8D, 0x44, 0x24, 0x68, 0x50, 0x8D, 0x8E, 0x60, 0x16, 0x00, 0x00};
static const u8 kHmiRehashPro[8] = {0x53, 0x55, 0x8B, 0x6C, 0x24, 0x0C, 0x56, 0x57};  // rehash 0xD72170: push ebx; push ebp; mov ebp,[esp+0Ch]; push esi; push edi
static const u8 kHmiErasePro[8] = {0x8B, 0x54, 0x24, 0x08, 0x8B, 0x42, 0x10, 0x85};  // mov edx,[esp+8]; mov eax,[edx+10h]; test eax,eax
typedef void*(__fastcall* HmiEraseFn)(u8* map, void* edx, DWORD* out, u8* node, u8** slot);
typedef void(__fastcall* HmiRehashFn)(u8* map, void* edx, DWORD n);
static HmiEraseFn g_hmiErase = (HmiEraseFn)(uintptr_t)HMI_ERASE;  // selftest swaps it
static bool g_hmiRepair = true;
static volatile LONG g_hmiCalls, g_hmiStale, g_hmiRehomed, g_hmiScanned, g_hmiGone, g_hmiFaults, g_hmiTestLeft;

static bool HmiChainHas(u8* const* slot, const u8* node) {
    int guard = 0;
    for (const u8* n = *slot; n && guard < 1 << 20; n = *(u8* const*)(n + 0x10), guard++)
        if (n == node) return true;
    return false;
}

// 0 = the slot is valid, 1 = re-found by hash, 2 = re-found by scan, 3 = the node is in no chain, 4 = fault while checking.
static int HmiCheck(u8* map, const u8* node, u8** slot, u8*** fixed) {
    __try {
        u8** const b = *(u8***)(map + 4);
        const DWORD nb = *(const DWORD*)(map + 8);
        if (!b || !nb) return 3;
        if (slot >= b && slot < b + nb && HmiChainHas(slot, node)) return 0;
        u8** s = b + *(const DWORD*)node % nb;
        if (HmiChainHas(s, node)) return *fixed = s, 1;
        for (DWORD i = 0; i < nb; i++)
            if (HmiChainHas(b + i, node)) return *fixed = b + i, 2;
        return 3;
    } __except (EXCEPTION_EXECUTE_HANDLER) { return 4; }
}

static void* __fastcall HmiErase(u8* map, void* edx, DWORD* out, u8* node, u8** slot) {
    InterlockedIncrement(&g_hmiCalls);
    if (g_hmiTestLeft > 0 && InterlockedDecrement(&g_hmiTestLeft) >= 0)  // repro: the rehash 0xD80460 can do, forced
        ((HmiRehashFn)(uintptr_t)g_hmiRehashAt)(map, NULL, *(const DWORD*)(map + 8) + 1);
    u8** fixed = slot;
    const int k = HmiCheck(map, node, slot, &fixed);
    if (!k) return g_hmiErase(map, edx, out, node, slot);
    const LONG n = InterlockedIncrement(&g_hmiStale);
    InterlockedIncrement(k == 1 ? &g_hmiRehomed : k == 2 ? &g_hmiScanned : k == 3 ? &g_hmiGone : &g_hmiFaults);
    DWORD idLo = 0, idHi = 0, nb = 0, cnt = 0, endNode = 0;
    u8** b = NULL;
    __try {
        b = *(u8***)(map + 4), nb = *(const DWORD*)(map + 8), cnt = *(const DWORD*)(map + 0xC);
        if (b) endNode = (DWORD)(uintptr_t)b[nb];  // the node past the last bucket is EA's end marker
        idLo = *(const DWORD*)node, idHi = *(const DWORD*)(node + 4);
    } __except (EXCEPTION_EXECUTE_HANDLER) {}
    static const char* const kWhy[] = {"", "re-found by id % buckets", "re-found by a scan of every chain", "in no chain (already erased): not erased again",
                                       "unreadable"};
    const bool fix = g_hmiRepair && k != 4;
    if (n <= 20 || n % 100 == 0)
        Log("host map iter: ProcessTaskState erase of id 0x%08lX%08lX (node 0x%08lX) with a stale slot 0x%08lX (buckets 0x%08lX x %lu, %lu elements): "
            "%s; %s; stale %ld of %ld erases (by hash %ld, by scan %ld, gone %ld, faults %ld)", idHi, idLo, (DWORD)(uintptr_t)node,
            (DWORD)(uintptr_t)slot, (DWORD)(uintptr_t)b, nb, cnt, kWhy[k], fix ? "fixed" : g_hmiRepair ? "passed to EA (unreadable map)" : "passed to EA (Repair=0)",
            n, g_hmiCalls, g_hmiRehomed, g_hmiScanned, g_hmiGone, g_hmiFaults);
    if (!fix) return g_hmiErase(map, edx, out, node, slot);
    if (k == 3) {  // not erased again; the out iterator is end()
        out[0] = endNode, out[1] = (DWORD)(uintptr_t)(b + nb);
        return out;
    }
    return g_hmiErase(map, edx, out, node, fixed);
}

// site = the call instruction; checks the 29 bytes before it, its target and the erase prologue, then retargets the rel32.
static bool HmiPatch(u8* site, DWORD erase) {
    if (!Matches(site - sizeof(kHmiSeq), kHmiSeq, sizeof(kHmiSeq)) || !Readable(site, 5) || site[0] != 0xE8 ||
        (DWORD)(uintptr_t)(site + 5 + *(const LONG*)(site + 1)) != erase || !Matches((const void*)(uintptr_t)erase, kHmiErasePro, sizeof(kHmiErasePro)))
        return false;
    const LONG rel = (LONG)((uintptr_t)&HmiErase - ((uintptr_t)site + 5));
    return Write(site + 1, &rel, 4);  // DllMain: the sim thread is not running yet
}

static bool ApplyHostMapIterFix(const wchar_t* ini) {
    if (GetPrivateProfileIntW(L"HostMapIterFix", L"Enabled", 0, ini) <= 0) return false;
    g_hmiRepair = GetPrivateProfileIntW(L"HostMapIterFix", L"Repair", 1, ini) > 0;
    const int test = GetPrivateProfileIntW(L"HostMapIterFix", L"TestRehash", 0, ini);
    if (!HmiPatch((u8*)(uintptr_t)HMI_SITE, HMI_ERASE)) {
        Log("SKIP host map iter fix: bytes at 0x%08lX or erase 0x%08lX differ (another owner or another exe): not installed", HMI_SITE, HMI_ERASE);
        return false;
    }
    if (test > 0 && g_hmiRehashAt && Matches((const void*)(uintptr_t)g_hmiRehashAt, kHmiRehashPro, sizeof(kHmiRehashPro))) g_hmiTestLeft = test;
    else if (test > 0) Log("SKIP host map iter fix TestRehash: no rehash 0xD72170 on this build (address or bytes)");
    Log("OK   host map iter fix (docs/67 §43): ProcessTaskState's host id map erase at 0x%08lX re-finds a slot left stale by a rehash in 0xD80460%s%s",
        HMI_SITE, g_hmiRepair ? "" : "; Repair=0: detector only, EA gets its own iterator", g_hmiTestLeft ? "; TestRehash: forced rehashes before the first erases (repro only)" : "");
    return true;
}
