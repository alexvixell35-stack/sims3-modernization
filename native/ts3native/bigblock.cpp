// [BigBlock] (docs/543): EA direct big blocks top-down, Mono sub-heap cores bottom-up, freed blocks kept as reservations
// for reuse. OFF by default (Enabled=0; every switch 0). NOT RUN IN GAME: built and host-selftested only. Included by
// ts3native.cpp after lptrim.cpp (uses apex.cpp's g_apexFound / g_apexStandDown / ApexProbe / g_apexStood; memdiag.cpp
// calls BbMemLine at every "diag: mem" mark). Stands down when Apex Radiance is loaded ([Compat] ApexStandDown=1): its
// FastMemory claims the same byte +0x498 and the same VirtualFree call 0x4E5306 (docs/148, docs/187 #12).
//
// EA GeneralAllocator direct blocks [verified disassembly TS3W 1.67, 2026-11-11; docs/201 §12.3, docs/15 phase 8]:
// - 0x4E50F0 (thiscall heap, request n, ret 4) is called from the Malloc core 0x4E5E40 (flag bit 3) and from 0x4E61BA
//   (request >= [heap+0x494] = 128 KB from Init, with byte [heap+0x46D] set and [heap+0x488] < [heap+0x490]). It asks
//   VirtualAlloc(NULL, size, flags, PAGE_READWRITE) at 0x4E512A (`ff 15 60 52 f9 00`, call [import VirtualAlloc]) with
//   size = (n + 0x27) rounded up to the page size [heap+0x4D4] (0x1000) and flags = MEM_RESERVE|MEM_COMMIT 0x3000, or
//   0x103000 (+ MEM_TOP_DOWN) when byte [heap+0x498] is set. Header at the base: [base] = 0, [base+4] = (size-0x10) | 2
//   (IS_MMAPPED); [heap+0x488] / [heap+0x48C] count live blocks / bytes.
// - The free core 0x4E51B0 takes the IS_MMAPPED path (test dl,2 / jne 0x4E52CF at 0x4E51F5): base = chunk - [chunk],
//   EA's size = [chunk] + (head & 0x7FFFFFF8) + 0x10, then VirtualFree(base, 0, MEM_RELEASE) at 0x4E5306
//   (`ff 15 5c 52 f9 00`). The size is in the header, but only blocks this module saw allocated are cached: its own map
//   (below) says which bases are ours; the header is only cross-checked.
// - These are the only VirtualAlloc / VirtualFree calls on direct blocks: all 7 VirtualAlloc and 5 VirtualFree import
//   references of the exe were listed (cores 0x4E4E81 / 0x4E4EB4 / 0x4E4EDB / 0x4E5536, core release 0x4E48AB at heap
//   shutdown, trim decommit 0x4E504C, the rest outside the allocator: Boehm, Mono, CRT). Realloc of a direct block is
//   malloc + copy + free through the same two calls.
// - +0x498 is written only by the ctor 0x4E5AA0 (store 0x4E5B8A), Init 0x4E5710 (0x4E5855, the ctor's last call) and
//   SetOption 11 (0x4E4AC7; no direct caller passes 11, docs/201 §12.2). [0x11CB864] is published at 0x4E40A6 after the
//   ctor + Init ran, so one write after the pointer appears sticks.
// - Locking: Malloc 0x4E69D0 / Free 0x4E5920 take the heap's lock [heap+0x4E4] around both paths, but a heap whose +0x4E4
//   is NULL runs them unlocked (the test at 0x4E69DA), and the call sites are shared by every GeneralAllocator instance
//   (Audio, SpeedTree, Scripting/Simulator). So this module keeps its own SRW lock, held only for table updates and never
//   across a call; on the main heap it nests inside EA's lock, so it adds no wait that EA's lock does not already have.
// - Mono sub-heap supplier 0xD71510 (the Scripting/Simulator heap's core callback, docs/201 §13.2): rounds the request up
//   to a power of two, at least 1 MB, then `mov ecx,[0x11CB864]; push 0; push esi; call 0x4E69D0` (0xD7154A-0xD71557):
//   a main-heap Malloc that reaches 0x4E50F0 (the `at=0x4E5130 from=0xD71558` direct blocks, 74 -> 98 MB per run).
//
// Switches (all need Enabled=1):
// - TopDown: [heap+0x498] = 1 on the MemoryMan heap once [0x11CB864] is set, after checking +0x4D4 = 0x1000 and the
//   reserve / commit pair +0x4D8 / +0x4DC = 16 / 4 MB (EA) or 4 / 1 MB (S3PP AllocatorArenaTuning). No code patch: EA's
//   own branch at 0x4E5111 then passes MEM_TOP_DOWN. Side effect: the core fallback 0x4E4EDB (only when a core reserve
//   fails) also goes top-down.
// - MonoBottom (needs TopDown): the call at 0xD71553 goes to BbMonoMalloc, which marks the calling thread (one slot,
//   compare-exchange, no TLS: docs/201 §15) around EA's Malloc; the VirtualAlloc stub strips MEM_TOP_DOWN for that thread
//   only. A second thread in the supplier at the same time is not marked (monoBusy): its core goes top-down like any block.
// - Reuse: the VirtualFree at 0x4E5306 goes to BbFree: a block this module recorded (64 KB to ReuseMaxBlockMB) is
//   decommitted (RAM back, reservation kept) and cached; while the cache holds more than ReuseMaxMB of reservations (or
//   BB_CACHE_N entries) the oldest are released with VirtualFree(base, 0, MEM_RELEASE). BbAlloc serves a request from a
//   cached reservation of the same 64 KB class (the OS rounds every reservation's start to 64 KB, so a block of that class
//   costs the same address space wherever it sits) with VirtualAlloc(base, size, MEM_COMMIT, same protection): fresh zero
//   pages, as from a new block. Any failure falls back to EA's original call. Mono-marked requests never use the cache.
//
// Patches (DllMain, before the exe's entry point; docs/68 §6 rule 5): the disp32 of the two `ff 15` calls is pointed at
// our own pointer slots (same 6 bytes, same return addresses 0x4E5130 / 0x4E530C, so [MemDiag] AllocSites attribution is
// unchanged) and the rel32 of the call at 0xD71553 at BbMonoMalloc. Each is one aligned 4-byte write after the whole
// window around it matched. Until the heap exists the stubs only pass through (g_bbArm = 0). If Apex Radiance turns up
// by then (zzApexRadiance.asi loads after us), the original dwords are written back (each only while it still holds
// ours) and nothing is armed. Apex installs its game code after the first Present + 1 s (apex.cpp ApexLateThread note),
// so the give-back comes before its 0x4E5306 patch looks at the bytes [inferred: the heap pointer is set before the first
// Present, and every ASI is loaded before the game creates the heap; not seen in a run].
static const DWORD BB_HEAP_SLOT = 0x011CB864;     // MemoryMan heap pointer (docs/re/structs.md §9)
static const DWORD BB_ALLOC_SITE = 0x004E50F0;    // 0x4E50F0-0x4E5133: whole head of the direct-block alloc, disp32 at +0x3C
static const DWORD BB_FREE_BRANCH = 0x004E51F5;   // free core: test dl,2 (IS_MMAPPED); push ebx; push ebp; jne 0x4E52CF
static const DWORD BB_FREE_SITE = 0x004E52CF;     // 0x4E52CF-0x4E530B: mmapped free path, disp32 at +0x39 (0x4E5308)
static const DWORD BB_MONO_SITE = 0x00D71510;     // 0xD71510-0xD71559: Mono core supplier head, rel32 at +0x44 (0xD71554)
static const DWORD BB_EA_MALLOC = 0x004E69D0;     // the target of that call (EA heap Malloc, thiscall (size, flags), ret 8)
// 0x4E50F0: push esi; push edi; mov edi,ecx; mov eax,[edi+4D4h]; mov ecx,[esp+0Ch]; sub eax,1; lea esi,[eax+ecx+27h];
// not eax; and esi,eax; cmp esi,ecx; jb 0x4E51A1 | cmp byte [edi+498h],0; mov eax,3000h; je +5; mov eax,103000h |
// push 4; push eax; push esi; push 0; call [0xF95260] (VirtualAlloc) | test eax,eax; je 0x4E51A1
static const u8 kBbAllocBytes[0x44] = {
    0x56, 0x57, 0x8B, 0xF9, 0x8B, 0x87, 0xD4, 0x04, 0x00, 0x00, 0x8B, 0x4C, 0x24, 0x0C, 0x83, 0xE8, 0x01, 0x8D, 0x74, 0x08, 0x27,
    0xF7, 0xD0, 0x23, 0xF0, 0x3B, 0xF1, 0x0F, 0x82, 0x90, 0x00, 0x00, 0x00, 0x80, 0xBF, 0x98, 0x04, 0x00, 0x00, 0x00, 0xB8, 0x00,
    0x30, 0x00, 0x00, 0x74, 0x05, 0xB8, 0x00, 0x30, 0x10, 0x00, 0x6A, 0x04, 0x50, 0x56, 0x6A, 0x00, 0xFF, 0x15, 0x60, 0x52, 0xF9,
    0x00, 0x85, 0xC0, 0x74, 0x6D};
static const u8 kBbFreeBranch[11] = {0xF6, 0xC2, 0x02, 0x53, 0x55, 0x0F, 0x85, 0xCF, 0x00, 0x00, 0x00};
// 0x4E52CF: mov edx,[eax] (prev_foot); mov ebp,[esi+eax+0Ch]; mov ecx,eax; lea ebx,[edx+esi+10h] (EA's size); sub ecx,edx
// (base); unlink from the direct list at heap+0x49C (lea/mov x7); push 8000h (MEM_RELEASE); push 0; mov [edx+8],eax;
// sub [edi+48Ch],ebx; add dword [edi+488h],-1; push ecx; call [0xF9525C] (VirtualFree)
static const u8 kBbFreeBytes[0x3D] = {
    0x8B, 0x10, 0x8B, 0x6C, 0x06, 0x0C, 0x8B, 0xC8, 0x8D, 0x5C, 0x32, 0x10, 0x2B, 0xCA, 0x8D, 0x54, 0x06, 0x0C, 0x8D, 0x44, 0x06,
    0x08, 0x8B, 0x30, 0x89, 0x6E, 0x0C, 0x8B, 0x12, 0x8B, 0x00, 0x68, 0x00, 0x80, 0x00, 0x00, 0x6A, 0x00, 0x89, 0x42, 0x08, 0x29,
    0x9F, 0x8C, 0x04, 0x00, 0x00, 0x83, 0x87, 0x88, 0x04, 0x00, 0x00, 0xFF, 0x51, 0xFF, 0x15, 0x5C, 0x52, 0xF9, 0x00};
// 0xD71510: mov eax,[esp+0Ch]; add eax,-1; or-shift by 16/8/4/2/1; add esi,1 (next power of two); cmp esi,100000h; jae +5;
// mov esi,100000h | mov ecx,[0x11CB864]; push 0; push esi; call 0x4E69D0 (EA Malloc) | test eax,eax
static const u8 kBbMonoBytes[0x4A] = {
    0x8B, 0x44, 0x24, 0x0C, 0x83, 0xC0, 0xFF, 0x8B, 0xC8, 0xC1, 0xE9, 0x10, 0x0B, 0xC1, 0x8B, 0xD0, 0xC1, 0xEA, 0x08, 0x0B, 0xC2,
    0x8B, 0xC8, 0xC1, 0xE9, 0x04, 0x0B, 0xC1, 0x8B, 0xD0, 0xC1, 0xEA, 0x02, 0x56, 0x0B, 0xC2, 0x8B, 0xF0, 0xD1, 0xEE, 0x0B, 0xF0,
    0x83, 0xC6, 0x01, 0x81, 0xFE, 0x00, 0x00, 0x10, 0x00, 0x73, 0x05, 0xBE, 0x00, 0x00, 0x10, 0x00, 0x8B, 0x0D, 0x64, 0xB8, 0x1C,
    0x01, 0x6A, 0x00, 0x56, 0xE8, 0x78, 0x54, 0x77, 0xFF, 0x85, 0xC0};
static const DWORD BB_ALLOC_DISP = 0x3C, BB_FREE_DISP = 0x39, BB_MONO_REL = 0x44;

enum { BB_TOPDOWN = 1, BB_MONO = 2, BB_REUSE = 4 };          // switches / installed parts / armed parts
enum { BB_M_BUSY = 1, BB_M_CACHED = 2, BB_M_FLAGS = 3 };      // map entry = region bytes (page multiple) | flags
static const DWORD BB_CACHE_N = 256;  // shortcut: at most 256 cached reservations (oldest released first); raise with ReuseMaxMB / small blocks
typedef LPVOID(WINAPI* BbVaFn)(LPVOID, SIZE_T, DWORD, DWORD);
typedef BOOL(WINAPI* BbVfFn)(LPVOID, SIZE_T, DWORD);
static void* const* g_bbVaIat = (void* const*)(uintptr_t)0x00F95260;  // TS3W import slots the two calls read (selftest: its own)
static void* const* g_bbVfIat = (void* const*)(uintptr_t)0x00F9525C;
static u8* const* g_bbHeapSlot = (u8* const*)(uintptr_t)BB_HEAP_SLOT;
static DWORD g_bbMonoMalloc = BB_EA_MALLOC;
static int g_bbWant;                 // switches asked for (after the start checks)
static volatile LONG g_bbArm;        // parts the stubs act on (0 = pass through)
static DWORD g_bbBudget = 128u << 20, g_bbMaxBlock = 16u << 20;
static volatile LONG g_bbMonoTid;    // the thread inside BbMonoMalloc (0 = none)
static DWORD* g_bbMap;               // one DWORD per 64 KB of the 32-bit space (256 KB, Reuse only)
struct BbEnt { DWORD base, size; };
static BbEnt g_bbCache[BB_CACHE_N];  // oldest first
static DWORD g_bbCacheN, g_bbCacheBytes;
static SRWLOCK g_bbLock = SRWLOCK_INIT;
struct BbPatch { u8* at; DWORD old, mine; };
static BbPatch g_bbPatch[3];
static int g_bbPatchN;
struct BbStats { volatile LONG allocs, allocKB, frees, hits, misses, evicts, strips, monoBusy, pass, dfree, bad, stale; };
static BbStats g_bb;

static DWORD BbClass(DWORD n) { return (n + 0xFFFF) & ~0xFFFFu; }
static bool BbCacheable(DWORD r) { return r >= 0x10000 && r <= g_bbMaxBlock; }

// Lock held. Oldest entry out of the cache (map entry cleared: the caller releases it).
static BbEnt BbPopOldest() {
    const BbEnt e = g_bbCache[0];
    memmove(g_bbCache, g_bbCache + 1, (g_bbCacheN - 1) * sizeof(BbEnt));
    g_bbCacheN--, g_bbCacheBytes -= e.size, g_bbMap[e.base >> 16] = 0;
    return e;
}
// Lock held. The newest cached reservation of n's 64 KB class with room for n, taken out (map: busy); 0 = none.
static DWORD BbTake(DWORD n) {
    for (DWORD i = g_bbCacheN; i-- > 0;) {
        const BbEnt e = g_bbCache[i];
        if (BbClass(e.size) != BbClass(n) || e.size < n) continue;
        memmove(g_bbCache + i, g_bbCache + i + 1, (g_bbCacheN - i - 1) * sizeof(BbEnt));
        g_bbCacheN--, g_bbCacheBytes -= e.size, g_bbMap[e.base >> 16] = e.size | BB_M_BUSY;
        return e.base;
    }
    return 0;
}
// Lock held. (base, size) cached as the newest; the oldest go to out while the cache is full or over budget. Returns
// how many went (size <= g_bbMaxBlock <= g_bbBudget, so the new one always stays).
static DWORD BbPut(DWORD base, DWORD size, BbEnt* out) {
    DWORD k = 0;
    if (g_bbCacheN == BB_CACHE_N) out[k++] = BbPopOldest();
    g_bbCache[g_bbCacheN].base = base, g_bbCache[g_bbCacheN].size = size, g_bbCacheN++;
    g_bbCacheBytes += size, g_bbMap[base >> 16] = size | BB_M_CACHED;
    while (g_bbCacheBytes > g_bbBudget && g_bbCacheN > 1) out[k++] = BbPopOldest();
    return k;
}
static void BbSetMap(DWORD base, DWORD v) {
    AcquireSRWLockExclusive(&g_bbLock);
    g_bbMap[base >> 16] = v;
    ReleaseSRWLockExclusive(&g_bbLock);
}
// EA's header at the base (0x4E516C / 0x4E5172, read back by 0x4E52CF-0x4E52D7): prev_foot 0, IS_MMAPPED, and EA's size
// in the 64 KB class of the region we hold (a reused region can be up to 60 KB longer than the request).
static bool BbHeaderOk(const u8* base, DWORD region) {
    const DWORD foot = *(const DWORD*)base, head = *(const DWORD*)(base + 4), n = foot + (head & 0x7FFFFFF8) + 0x10;
    return !foot && (head & 2) && n <= region && BbClass(n) == BbClass(region);
}

// In place of `call [VirtualAlloc]` at 0x4E512A (stdcall, 4 arguments).
static LPVOID WINAPI BbAlloc(LPVOID at, SIZE_T n, DWORD type, DWORD prot) {
    const BbVaFn va = (BbVaFn)*g_bbVaIat;
    const LONG arm = g_bbArm;
    if (!arm || at || prot != PAGE_READWRITE || (type & ~MEM_TOP_DOWN) != (MEM_RESERVE | MEM_COMMIT) || n > 0x7FFF0000)
        return va(at, n, type, prot);  // not armed, or not the call 0x4E512A makes
    InterlockedIncrement(&g_bb.allocs);
    InterlockedExchangeAdd(&g_bb.allocKB, (LONG)(n >> 10));
    const LONG mt = g_bbMonoTid;
    if ((arm & BB_MONO) && mt && mt == (LONG)GetCurrentThreadId()) {  // a Mono sub-heap core: bottom-up, never cached
        if (type & MEM_TOP_DOWN) type &= ~MEM_TOP_DOWN, InterlockedIncrement(&g_bb.strips);
        return va(NULL, n, type, prot);
    }
    const DWORD r = (DWORD)((n + 0xFFF) & ~(SIZE_T)0xFFF);
    if (!(arm & BB_REUSE) || !BbCacheable(r)) return va(NULL, n, type, prot);
    AcquireSRWLockExclusive(&g_bbLock);
    const DWORD c = BbTake(r);
    ReleaseSRWLockExclusive(&g_bbLock);
    if (c) {
        if (va((void*)(uintptr_t)c, n, MEM_COMMIT, prot) == (void*)(uintptr_t)c) {
            AcquireSRWLockExclusive(&g_bbLock);
            g_bbMap[c >> 16] &= ~(DWORD)BB_M_FLAGS;  // live again, region size kept
            ReleaseSRWLockExclusive(&g_bbLock);
            InterlockedIncrement(&g_bb.hits);
            return (void*)(uintptr_t)c;
        }
        BbSetMap(c, 0);  // commit failed (commit limit): the reservation goes back, EA's own call below
        ((BbVfFn)*g_bbVfIat)((void*)(uintptr_t)c, 0, MEM_RELEASE);
        InterlockedIncrement(&g_bb.bad);
    } else InterlockedIncrement(&g_bb.misses);
    void* p = va(NULL, n, type, prot);
    if (p && !((uintptr_t)p & 0xFFFF)) {
        AcquireSRWLockExclusive(&g_bbLock);
        if (g_bbMap[(DWORD)(uintptr_t)p >> 16]) InterlockedIncrement(&g_bb.stale);  // released by a path we do not see
        g_bbMap[(DWORD)(uintptr_t)p >> 16] = r;
        ReleaseSRWLockExclusive(&g_bbLock);
    }
    return p;
}

// In place of `call [VirtualFree]` at 0x4E5306 (stdcall, 3 arguments: base, 0, MEM_RELEASE).
static BOOL WINAPI BbFree(LPVOID at, SIZE_T n, DWORD type) {
    const BbVfFn vf = (BbVfFn)*g_bbVfIat;
    const DWORD base = (DWORD)(uintptr_t)at;
    if (!(g_bbArm & BB_REUSE) || n || type != MEM_RELEASE || !base || (base & 0xFFFF)) return vf(at, n, type);
    InterlockedIncrement(&g_bb.frees);
    AcquireSRWLockExclusive(&g_bbLock);
    const DWORD e = g_bbMap[base >> 16];
    if (e && !(e & BB_M_FLAGS)) g_bbMap[base >> 16] = e | BB_M_BUSY;
    ReleaseSRWLockExclusive(&g_bbLock);
    if (!e) {  // not recorded here: allocated before arming, outside 64 KB-ReuseMaxBlockMB, or a Mono core
        InterlockedIncrement(&g_bb.pass);
        return vf(at, 0, MEM_RELEASE);
    }
    if (e & BB_M_FLAGS) {  // ours and already freed (cached or in transit): a second free must not release our reservation
        InterlockedIncrement(&g_bb.dfree);
        SetLastError(ERROR_INVALID_ADDRESS);  // what VirtualFree says for a region already released
        return FALSE;
    }
    const DWORD r = e & ~0xFFFu;
    if (!BbHeaderOk((const u8*)at, r) || !vf(at, r, MEM_DECOMMIT)) {
        BbSetMap(base, 0);
        InterlockedIncrement(&g_bb.bad);
        return vf(at, 0, MEM_RELEASE);
    }
    BbEnt out[BB_CACHE_N + 1];
    AcquireSRWLockExclusive(&g_bbLock);
    const DWORD k = BbPut(base, r, out);
    ReleaseSRWLockExclusive(&g_bbLock);
    for (DWORD i = 0; i < k; i++) vf((void*)(uintptr_t)out[i].base, 0, MEM_RELEASE);
    if (k) InterlockedExchangeAdd(&g_bb.evicts, (LONG)k);
    return TRUE;
}
static void* g_bbAllocSlot = (void*)&BbAlloc;  // the `ff 15` disp32 of 0x4E512A / 0x4E5306 points here
static void* g_bbFreeSlot = (void*)&BbFree;

static LONG __stdcall BbMonoEnter() {
    if (!InterlockedCompareExchange(&g_bbMonoTid, (LONG)GetCurrentThreadId(), 0)) return 1;
    InterlockedIncrement(&g_bb.monoBusy);
    return 0;
}
// In place of the call target at 0xD71553: thiscall Malloc(size, flags), ecx = the main heap, ret 8; eax = EA's result.
static __declspec(naked) void BbMonoMalloc() {
    __asm {
        push ecx
        call BbMonoEnter              // eax = 1 when this thread now owns the mark (keeps ebx / esi / edi / ebp)
        pop ecx
        push eax                      // [esp] owned, [esp+4] return 0xD71558, [esp+8] size, [esp+0Ch] flags
        push dword ptr [esp + 0Ch]    // flags
        push dword ptr [esp + 0Ch]    // size
        call dword ptr [g_bbMonoMalloc]
        pop ecx
        test ecx, ecx
        jz done
        mov dword ptr [g_bbMonoTid], 0
    done:
        ret 8
    }
}

// One aligned 4-byte write of code (a thread runs the old or the new operand, never half of one), only while the operand
// still holds `expect`. Returns false when it was not written.
static bool BbWrite32(u8* at, DWORD expect, DWORD v) {
    DWORD prot;
    if (((uintptr_t)at & 3) || !VirtualProtect(at, 4, PAGE_EXECUTE_READWRITE, &prot)) return false;
    const bool ok = (DWORD)InterlockedCompareExchange((volatile LONG*)at, (LONG)v, (LONG)expect) == expect;
    VirtualProtect(at, 4, prot, &prot);
    FlushInstructionCache(GetCurrentProcess(), at, 4);
    return ok;
}
static void BbSkip(const char* what, const u8* at, size_t n) {
    char hex[3 * 0x4A + 1];
    if (Readable(at, n)) Hex(at, n, hex, sizeof(hex)); else strcpy_s(hex, "<unreadable>");
    Log("SKIP big block %s: bytes at 0x%08lX differ (found %s): not installed", what, (DWORD)(uintptr_t)at, hex);
}
// Checks every window `want` needs, then patches (free first, so no block recorded by the alloc stub is freed unseen).
// Returns the parts installed. The alloc window is the +0x498 reader (TopDown) and the call site (MonoBottom, Reuse).
static int BbInstallSites(u8* alloc, u8* freeBranch, u8* freeSite, u8* mono, int want) {
    if (!Matches(alloc, kBbAllocBytes, sizeof(kBbAllocBytes))) { BbSkip("(every switch)", alloc, sizeof(kBbAllocBytes)); return 0; }
    if ((want & BB_REUSE) && !Matches(freeBranch, kBbFreeBranch, sizeof(kBbFreeBranch))) BbSkip("Reuse", freeBranch, sizeof(kBbFreeBranch)), want &= ~BB_REUSE;
    else if ((want & BB_REUSE) && !Matches(freeSite, kBbFreeBytes, sizeof(kBbFreeBytes))) BbSkip("Reuse", freeSite, sizeof(kBbFreeBytes)), want &= ~BB_REUSE;
    if ((want & BB_MONO) && !Matches(mono, kBbMonoBytes, sizeof(kBbMonoBytes))) BbSkip("MonoBottom", mono, sizeof(kBbMonoBytes)), want &= ~BB_MONO;
    struct { int part; u8* at; DWORD v; } w[3] = {
        {BB_REUSE, freeSite + BB_FREE_DISP, (DWORD)(uintptr_t)&g_bbFreeSlot},
        {BB_REUSE | BB_MONO, alloc + BB_ALLOC_DISP, (DWORD)(uintptr_t)&g_bbAllocSlot},
        {BB_MONO, mono + BB_MONO_REL, (DWORD)((uintptr_t)&BbMonoMalloc - ((uintptr_t)mono + BB_MONO_REL + 4))}};
    for (const auto& x : w) {
        if (!(want & x.part)) continue;
        BbPatch& p = g_bbPatch[g_bbPatchN];
        p.at = x.at, p.old = *(const DWORD*)x.at, p.mine = x.v;  // the window matched: old is the game's operand
        if (!BbWrite32(x.at, p.old, x.v)) {
            Log("FAIL big block: write at 0x%08lX (err %lu): parts not installed", (DWORD)(uintptr_t)x.at, GetLastError());
            want &= ~x.part;  // an earlier write stays: its stub passes through for every part that is not armed
            continue;
        }
        g_bbPatchN++;
    }
    return want;
}
// Original operands back (late Apex), each only while it still holds ours, so another module's later write is never
// undone. Calls in flight finish through the stubs, which pass through while nothing is armed.
static void BbGiveBack() {
    while (g_bbPatchN > 0) {
        const BbPatch& p = g_bbPatch[--g_bbPatchN];
        if (!BbWrite32(p.at, p.mine, p.old)) Log("WARN big block: operand at 0x%08lX changed by another module: left as it is", (DWORD)(uintptr_t)p.at);
    }
}

// Heap checks before the one byte write; NULL = written, else why not.
static const char* BbSetTopDown(u8* h, u8* oldv) {
    *oldv = 0xFF;
    if (!Readable(h, 0x4E8)) return "heap not readable";
    *oldv = h[0x498];
    const DWORD pg = *(const DWORD*)(h + 0x4D4), res = *(const DWORD*)(h + 0x4D8), com = *(const DWORD*)(h + 0x4DC);
    if (h[0] != 1 || pg != 0x1000 || !((res == 16u << 20 && com == 4u << 20) || (res == 4u << 20 && com == 1u << 20)))
        return "init byte +0 / page size +0x4D4 / core reserve +0x4D8 and commit +0x4DC not 1 / 0x1000 / 16+4 MB or 4+1 MB";
    if (*oldv) return "+0x498 already set (another mod)";
    *(volatile u8*)(h + 0x498) = 1;
    return NULL;
}
static bool BbMapInit() {
    if (!g_bbMap) g_bbMap = (DWORD*)VirtualAlloc(NULL, 0x10000 * sizeof(DWORD), MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
    return g_bbMap != NULL;
}
// The heap exists (or never appeared: h NULL). Arms the stubs and sets the byte.
static void BbGoLive(u8* h, bool apexNow) {
    if (apexNow && g_apexStandDown) {
        BbGiveBack();
        InterlockedIncrement(&g_apexStood);
        Log("compat: stood down for Apex Radiance (loaded after ts3native): [BigBlock] call sites given back, nothing armed (docs/148 §6)");
        return;
    }
    if (!h) { Log("FAIL big block: MemoryMan heap [0x%08lX] not set after 120 s: nothing armed (stubs pass through)", BB_HEAP_SLOT); return; }
    int arm = g_bbWant & (BB_MONO | BB_REUSE);
    if ((arm & BB_REUSE) && !BbMapInit()) Log("FAIL big block Reuse: no 256 KB map (err %lu)", GetLastError()), arm &= ~BB_REUSE;
    InterlockedExchange(&g_bbArm, arm);  // before the byte: Mono cores are stripped from the first top-down block on
    u8 old = 0;
    const char* why = (g_bbWant & BB_TOPDOWN) ? BbSetTopDown(h, &old) : "off";
    Log("OK   big block (docs/543, NOT RUN IN GAME before this build): heap 0x%p; TopDown %s (+0x498 %u -> %u); MonoBottom %s; "
        "Reuse %s (up to %lu MB of reservations, blocks 64 KB-%lu MB)",
        h, why ? why : "on", old, h && Readable(h + 0x498, 1) ? h[0x498] : 0xFF, (arm & BB_MONO) ? "on" : "off", (arm & BB_REUSE) ? "on" : "off",
        g_bbBudget >> 20, g_bbMaxBlock >> 20);
}
static DWORD WINAPI BbThread(void*) {
    u8* h = NULL;
    for (int i = 0; i < 12000 && !(h = *(u8* const volatile*)g_bbHeapSlot); i++) Sleep(10);
    BbGoLive(h, ApexProbe() != 0);
    return 0;
}

// "diag: mem" mark (memdiag.cpp MdAfterSample)
static void BbMemLine(LONG n, const char* tag) {
    if (!g_bbWant) return;
    DWORD liveN = 0, liveB = 0, cN, cB;
    int td = -1;
    __try {
        const u8* h = *(const u8* const volatile*)g_bbHeapSlot;
        if (h) liveN = *(const volatile DWORD*)(h + 0x488), liveB = *(const volatile DWORD*)(h + 0x48C), td = h[0x498];
    } __except (EXCEPTION_EXECUTE_HANDLER) { td = -2; }
    AcquireSRWLockShared(&g_bbLock);
    cN = g_bbCacheN, cB = g_bbCacheBytes;
    ReleaseSRWLockShared(&g_bbLock);
    const double MB = 1.0 / (1 << 20);
    Log("bigblock: #%ld %s arm=%ld topDown=%d allocs=%ld allocMB=%.1f frees=%ld eaLiveN=%lu eaLiveMB=%.1f hits=%ld misses=%ld "
        "cacheN=%lu cacheMB=%.1f evicts=%ld strips=%ld monoBusy=%ld pass=%ld dfree=%ld bad=%ld stale=%ld",
        n, tag, g_bbArm, td, g_bb.allocs, g_bb.allocKB / 1024.0, g_bb.frees, liveN, liveB * MB, g_bb.hits, g_bb.misses, cN, cB * MB,
        g_bb.evicts, g_bb.strips, g_bb.monoBusy, g_bb.pass, g_bb.dfree, g_bb.bad, g_bb.stale);
}

static bool ApplyBigBlock(const wchar_t* ini) {
    if (GetPrivateProfileIntW(L"BigBlock", L"Enabled", 0, ini) <= 0) return false;
    int want = (GetPrivateProfileIntW(L"BigBlock", L"TopDown", 0, ini) > 0 ? BB_TOPDOWN : 0) |
               (GetPrivateProfileIntW(L"BigBlock", L"MonoBottom", 0, ini) > 0 ? BB_MONO : 0) |
               (GetPrivateProfileIntW(L"BigBlock", L"Reuse", 0, ini) > 0 ? BB_REUSE : 0);
    int maxMB = GetPrivateProfileIntW(L"BigBlock", L"ReuseMaxMB", 128, ini), blkMB = GetPrivateProfileIntW(L"BigBlock", L"ReuseMaxBlockMB", 16, ini);
    maxMB = maxMB < 1 ? 1 : maxMB > 1024 ? 1024 : maxMB;
    blkMB = blkMB < 1 ? 1 : blkMB > 256 ? 256 : blkMB > maxMB ? maxMB : blkMB;
    g_bbBudget = (DWORD)maxMB << 20, g_bbMaxBlock = (DWORD)blkMB << 20;
    if (g_apexFound && g_apexStandDown) {
        InterlockedIncrement(&g_apexStood);
        Log("compat: stood down for Apex Radiance: [BigBlock] not installed (FastMemory owns +0x498 and the VirtualFree call 0x4E5306, docs/148 §6)");
        return false;
    }
    if (g_apexFound) Log("WARN big block: Apex Radiance loaded and [Compat] ApexStandDown=0: installed beside FastMemory (same byte +0x498, same call 0x4E5306)");
    if ((want & BB_MONO) && !(want & BB_TOPDOWN)) Log("SKIP big block MonoBottom: needs TopDown=1 (EA passes MEM_TOP_DOWN only with +0x498 set)"), want &= ~BB_MONO;
    if (!want) { Log("big block: Enabled=1 with TopDown, MonoBottom and Reuse all 0: nothing to do"); return false; }
    const int got = BbInstallSites((u8*)(uintptr_t)BB_ALLOC_SITE, (u8*)(uintptr_t)BB_FREE_BRANCH, (u8*)(uintptr_t)BB_FREE_SITE,
                                   (u8*)(uintptr_t)BB_MONO_SITE, want);
    if (!got) return false;
    g_bbWant = got;
    HANDLE t = CreateThread(NULL, 64 * 1024, BbThread, NULL, STACK_SIZE_PARAM_IS_A_RESERVATION, NULL);
    if (!t) { BbGiveBack(); g_bbWant = 0; Log("FAIL big block: no thread (%lu): sites given back, not installed", GetLastError()); return false; }
    CloseHandle(t);
    Log("big block: TopDown %s, MonoBottom %s (call 0xD71553), Reuse %s (calls 0x4E512A / 0x4E5306): patched, armed when the MemoryMan heap "
        "exists (every \"diag: mem\" mark adds a \"bigblock:\" line)", (got & BB_TOPDOWN) ? "on" : "off", (got & BB_MONO) ? "on" : "off",
        (got & BB_REUSE) ? "on" : "off");
    return true;
}
