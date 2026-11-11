// [StreamBuf] (docs/547): the data buffers of EA's IO memory streams (EA::IO::MemoryStream, vtable 0xFB5BCC) counted by
// the code that sized the stream (Stats) and served from a small pool of our own blocks (Pool). OFF by default
// (Enabled=0). Included by ts3native.cpp after allocsites.cpp (uses its AsCallBefore / g_asTextLo / g_asTextHi);
// memdiag.cpp calls StbMemLine at every "diag: mem" mark.
//
// What the game does [verified disassembly TS3W 1.67, sha256 b78a3d53..., 2026-11-11; docs/547 §2-§4]:
// - A MemoryStream (0x28 bytes: +4 SharedPointer*, +8 allocation name, +0xC refcount, +0x10 size, +0x14 capacity, +0x18
//   position, +0x1C resize enabled, +0x20 float resize factor, +0x24 resize increment) keeps its bytes in a refcounted
//   SharedPointer (0x14 bytes, vtable 0xFB84D0: +4 allocator, +8 data, +0xC refcount, +0x10 "free the data" byte).
// - MemoryStream::Realloc 0x4DEE40 (thiscall, new capacity, ret 4) is the only code that gives a stream a buffer of its
//   own: it builds a NEW SharedPointer with the constructor 0x4DEBE0 (its only caller: 0x4DEE87, return 0x4DEE8C), which
//   asks the allocator for the bytes at 0x4DEC15 (`call edx` = allocator vtable +8, return 0x4DEC17: the caller [BigBlock]
//   names), copies min(new, old capacity) bytes from the old buffer, and releases the old SharedPointer (inline at
//   0x4DEED3-0x4DEF11). So a stream never grows in place: every size change is allocate + copy + free.
// - Realloc is called from SetSize 0x4DF190 (exact size; return 0x4DF1C3), Write 0x4DEFF0 (capacity * factor + increment,
//   at least what the write needs; return 0x4DF061) and SetPosition 0x4DEF30 (past the end; return 0x4DEF7F).
// - The data is freed in exactly two places, both `allocator vtable +0xC (data, 0)` under the "free the data" byte when
//   the SharedPointer's refcount ends: SharedPointer::Release 0x4DEA80 (call at 0x4DEAA7) and Realloc's inline copy of
//   it (call at 0x4DEF07). The whole .text was searched for other copies of that sequence: none.
// - The hot consumer [inferred from sizes and code, to be confirmed by Stats]: the async resource loader job 0x72A150 /
//   0x727540 gives each streamed record a MemoryStream sized once with SetSize (the record's raw size, then its decoded
//   size) and reads / RefPack-decodes into it; 1024x1024 DXT5 and DXT1 textures with mips are 1,398,256 and 699,192
//   bytes + header = the 1408 KB and 704 KB classes. EA's heap gives 128 KB+ requests their own VirtualAlloc.
//
// Patches (DllMain, before the exe's entry point; each window byte-checked first, all or nothing):
// - 0x4DEC0B (12 bytes `6A 00 51 8B 4C 24 10 51 8B C8 FF D2`: push 0; push name; push size; mov ecx,allocator; call
//   [vtable+8]) becomes `51 50 53 FF 74 24 14 E8 rel32`: push name; push allocator; push ebx (the stream: Realloc's
//   `this`, untouched by the constructor); push size; call StbAlloc. Same return address 0x4DEC17, so a fallback to the
//   game's allocator is still attributed to this site by [BigBlock] / [MemDiag] AllocSites.
// - 0x4DEA99 and 0x4DEEF9 (16 bytes each, `8B 4E 04 8B ?? 8B ?? 08 8B ?? 0C 6A 00 5? FF D?`: the free call) become
//   `56 E8 rel32 EB 08 CC*8`: push esi (the SharedPointer); call StbFree; jmp over the rest.
// A block is ours when its base is in the owner bitmap (one bit per 64 KB, read without a lock): every other pointer goes
// to the game's free exactly as before. A pooled block's pointer is valid from the allocation to the free of its
// SharedPointer, as the game's own block was; nothing else in the game frees stream data (above).
//
// Pool: blocks of our own (VirtualAlloc, sizes rounded up to 64 KB), for requests of MinKB to MaxBlockMB. A request takes
// the smallest idle block that fits with at most a quarter to spare (the most recently freed of that size), else a new
// block while the pool owns less than PoolMB (idle blocks are released oldest first to make room), else the game's own
// path. A freed block stays idle for IdleSec, then goes back to the OS (StbTick, on this module's thread), so the pool is
// empty when streaming stops. Zero=1 (default) hands out a reused block zero-filled over the requested bytes, as the OS
// hands out a new one: 0x727540 accepts a short read (it only fails on 0 bytes), so the tail of a buffer can be read
// without having been written [verified, docs/547 §5]. Lock: one SRW lock around the tables, never held across a system
// call, a fill or the game's allocator; streams are allocated on the loader's job thread and freed on others, so the
// pool is global. Run-time switch: Enabled / Stats / Pool / Zero / Verify are read again every 5 s (StbWatch); blocks
// handed out while the pool was on still come back to it and are released by the next tick.
// Verify (debug): 1 = a freed block is filled with 0xB5 and checked when it is reused (a write after free counts as
// uafw). 2 = also every block is handed out filled with 0xA7 instead of zeros, and at its free the bytes that still hold
// 0xA7 are counted (unwritten): a consumer that never leaves any is proven not to depend on zeros. Limits: a read after
// free and a read before write that is later overwritten cannot be seen; with 2 an unwritten byte reads 0xA7, not 0.
// Apex Radiance: no Apex site lies in 0x4DE000-0x4DFFFF (docs/data/apex-sites.csv, apex-hooks.csv), so no stand-down;
// a foreign patch is caught by the byte check at start and by StbWatch later (the pool stops for the session).
static const DWORD STB_CTOR = 0x004DEBE0, STB_REL = 0x004DEA80, STB_REALLOC = 0x004DEE40;  // the three patched functions
static const DWORD STB_SETSIZE = 0x004DF190, STB_WRITE = 0x004DEFF0, STB_SETPOS = 0x004DEF30;  // Realloc's callers (frames read, no patch)
static const DWORD STB_CTOR_AT = 0x2B, STB_REL_AT = 0x19, STB_REALLOC_AT = 0xB9;  // patch offsets: 0x4DEC0B, 0x4DEA99, 0x4DEEF9
static const DWORD STB_RET_CTOR = 0x004DEE8C, STB_RET_SETSIZE = 0x004DF1C3, STB_RET_WRITE = 0x004DF061, STB_RET_SETPOS = 0x004DEF7F;
static const u8 kStbCtor[0x4B] = {  // 0x4DEBE0-0x4DEC2A: SharedPointer(size, allocator, name)
    0x8B, 0x44, 0x24, 0x08, 0x85, 0xC0, 0x56, 0x8B, 0xF1, 0xC7, 0x06, 0xD0, 0x84, 0xFB, 0x00, 0x75, 0x05, 0xE8, 0xDA, 0xAE, 0xFF, 0xFF, 0x8B, 0x4C,
    0x24, 0x10, 0x85, 0xC9, 0x89, 0x46, 0x04, 0x75, 0x05, 0xB9, 0xD4, 0x84, 0xFB, 0x00, 0x8B, 0x10, 0x8B, 0x52, 0x08, 0x6A, 0x00, 0x51, 0x8B, 0x4C,
    0x24, 0x10, 0x51, 0x8B, 0xC8, 0xFF, 0xD2, 0x89, 0x46, 0x08, 0xC7, 0x46, 0x0C, 0x00, 0x00, 0x00, 0x00, 0xC6, 0x46, 0x10, 0x01, 0x8B, 0xC6, 0x5E,
    0xC2, 0x0C, 0x00};
static const u8 kStbRel[0x37] = {  // 0x4DEA80-0x4DEAB6: SharedPointer::Release
    0x56, 0x8B, 0xF1, 0x8B, 0x46, 0x0C, 0x83, 0xF8, 0x01, 0x7E, 0x08, 0x83, 0xC0, 0xFF, 0x89, 0x46, 0x0C, 0x5E, 0xC3, 0x80, 0x7E, 0x10, 0x00, 0x74,
    0x10, 0x8B, 0x4E, 0x04, 0x8B, 0x01, 0x8B, 0x56, 0x08, 0x8B, 0x40, 0x0C, 0x6A, 0x00, 0x52, 0xFF, 0xD0, 0x8B, 0x16, 0x8B, 0x02, 0x6A, 0x01, 0x8B,
    0xCE, 0xFF, 0xD0, 0x33, 0xC0, 0x5E, 0xC3};
static const u8 kStbRealloc[0xE2] = {  // 0x4DEE40-0x4DEF21: MemoryStream::Realloc
    0x53, 0x55, 0x8B, 0xD9, 0x8B, 0x43, 0x04, 0x85, 0xC0, 0x56, 0x57, 0x74, 0x07, 0x8B, 0x40, 0x04, 0x85, 0xC0, 0x75, 0x05, 0xE8, 0x77, 0xAC, 0xFF,
    0xFF, 0x8B, 0x6C, 0x24, 0x14, 0x85, 0xED, 0x8B, 0xF0, 0x74, 0x49, 0x8B, 0x43, 0x08, 0x85, 0xC0, 0x75, 0x05, 0xB8, 0xF8, 0x84, 0xFB, 0x00, 0x50,
    0x56, 0x6A, 0x14, 0xE8, 0x98, 0x16, 0x00, 0x00, 0x83, 0xC4, 0x0C, 0x85, 0xC0, 0x74, 0x24, 0x8B, 0x4B, 0x08, 0x51, 0x56, 0x55, 0x8B, 0xC8, 0xE8,
    0x54, 0xFD, 0xFF, 0xFF, 0x8B, 0xF8, 0x85, 0xFF, 0x74, 0x11, 0x83, 0x47, 0x0C, 0x01, 0x83, 0x7F, 0x08, 0x00, 0x75, 0x12, 0x8B, 0xCF, 0xE8, 0xDD,
    0xFB, 0xFF, 0xFF, 0x5F, 0x5E, 0x5D, 0x32, 0xC0, 0x5B, 0xC2, 0x04, 0x00, 0x33, 0xFF, 0x8B, 0x4B, 0x04, 0x85, 0xC9, 0x74, 0x5E, 0x85, 0xFF, 0x74,
    0x1A, 0x8B, 0x43, 0x14, 0x3B, 0xE8, 0x73, 0x02, 0x8B, 0xC5, 0x8B, 0x51, 0x08, 0x50, 0x8B, 0x47, 0x08, 0x52, 0x50, 0xE8, 0xD6, 0xF9, 0xA9, 0x00,
    0x83, 0xC4, 0x0C, 0x8B, 0x73, 0x04, 0x8B, 0x46, 0x0C, 0x83, 0xF8, 0x01, 0x7E, 0x15, 0x83, 0xC0, 0xFF, 0x89, 0x46, 0x0C, 0x89, 0x7B, 0x04, 0x5F,
    0x5E, 0x89, 0x6B, 0x14, 0x5D, 0xB0, 0x01, 0x5B, 0xC2, 0x04, 0x00, 0x80, 0x7E, 0x10, 0x00, 0x74, 0x10, 0x8B, 0x4E, 0x04, 0x8B, 0x11, 0x8B, 0x46,
    0x08, 0x8B, 0x52, 0x0C, 0x6A, 0x00, 0x50, 0xFF, 0xD2, 0x8B, 0x06, 0x8B, 0x10, 0x6A, 0x01, 0x8B, 0xCE, 0xFF, 0xD2, 0x89, 0x7B, 0x04, 0x5F, 0x5E,
    0x89, 0x6B, 0x14, 0x5D, 0xB0, 0x01, 0x5B, 0xC2, 0x04, 0x00};
static const u8 kStbSetSize[0x33] = {  // 0x4DF190-0x4DF1C2: push esi; push edi; ... push edi; mov ecx,esi; call Realloc
    0x56, 0x8B, 0xF1, 0x8B, 0x4E, 0x10, 0x57, 0x8B, 0x7C, 0x24, 0x0C, 0x3B, 0xF9, 0xB0, 0x01, 0x74, 0x30, 0x80, 0x7E, 0x1C, 0x00, 0x74, 0x28, 0x3B,
    0xF9, 0x73, 0x10, 0x39, 0x7E, 0x18, 0x89, 0x7E, 0x10, 0x76, 0x1E, 0x89, 0x7E, 0x18, 0x5F, 0x5E, 0xC2, 0x04, 0x00, 0x57, 0x8B, 0xCE, 0xE8, 0x7D,
    0xFC, 0xFF, 0xFF};
static const u8 kStbWrite[0x71] = {  // 0x4DEFF0-0x4DF060: sub esp,8; push ebp; push esi; push ebx; push edi; ... push eax; call Realloc
    0x83, 0xEC, 0x08, 0x55, 0x8B, 0x6C, 0x24, 0x14, 0x85, 0xED, 0x56, 0x8B, 0xF1, 0x0F, 0x86, 0xA7, 0x00, 0x00, 0x00, 0x8B, 0x4E, 0x18, 0x8B, 0x46,
    0x14, 0x53, 0x57, 0x8D, 0x3C, 0x29, 0x3B, 0xF8, 0x8B, 0xDD, 0x76, 0x62, 0x80, 0x7E, 0x1C, 0x00, 0x74, 0x55, 0x85, 0xC0, 0x89, 0x44, 0x24, 0x20,
    0xDB, 0x44, 0x24, 0x20, 0x7D, 0x06, 0xD8, 0x05, 0x40, 0x89, 0xF9, 0x00, 0xD8, 0x4E, 0x20, 0x8B, 0xCE, 0xD9, 0x7C, 0x24, 0x20, 0x0F, 0xB7, 0x44,
    0x24, 0x20, 0xDA, 0x46, 0x24, 0x0D, 0x00, 0x0C, 0x00, 0x00, 0x89, 0x44, 0x24, 0x10, 0xD9, 0x6C, 0x24, 0x10, 0xDF, 0x7C, 0x24, 0x10, 0x8B, 0x44,
    0x24, 0x10, 0x3B, 0xC7, 0x0F, 0x42, 0xC7, 0xD9, 0x6C, 0x24, 0x20, 0x50, 0xE8, 0xDF, 0xFD, 0xFF, 0xFF};
static const u8 kStbSetPos[0x4F] = {  // 0x4DEF30-0x4DEF7E: push esi; push edi; ... push eax; mov ecx,esi; call Realloc
    0x8B, 0x44, 0x24, 0x08, 0x83, 0xE8, 0x00, 0x56, 0x8B, 0xF1, 0x8B, 0x4E, 0x18, 0x57, 0x8B, 0xF9, 0x74, 0x1E, 0x83, 0xE8, 0x01, 0x74, 0x0E, 0x83,
    0xE8, 0x01, 0x75, 0x1B, 0x8B, 0x46, 0x10, 0x03, 0x44, 0x24, 0x0C, 0xEB, 0x0F, 0x8B, 0x54, 0x24, 0x0C, 0x03, 0xCA, 0x89, 0x4E, 0x18, 0xEB, 0x07,
    0x8B, 0x44, 0x24, 0x0C, 0x89, 0x46, 0x18, 0x8B, 0x46, 0x18, 0x8B, 0x4E, 0x10, 0x3B, 0xC1, 0x76, 0x24, 0x80, 0x7E, 0x1C, 0x00, 0x74, 0x14, 0x50,
    0x8B, 0xCE, 0xE8, 0xC1, 0xFE, 0xFF, 0xFF};

enum { STB_POOL = 1, STB_STATS = 2, STB_ZERO = 4, STB_VERIFY = 8, STB_VERIFY2 = 16 };  // g_stbArm bits
enum { STBS_FREE = 0, STBS_INUSE, STBS_IDLE, STBS_BUSY, STBS_EXT };  // BUSY = ours, in transit (being created, filled or checked); EXT = the game's block, tracked for Stats
static const DWORD STB_N = 512;  // shortcut: at most 512 blocks known at once (pool + tracked game blocks); more go the game's way uncounted (lost / untracked)
static const DWORD STB_WHO_N = 32, STB_TID_N = 16, STB_SZ_N = 14;
static const u8 STB_POISON = 0xB5, STB_PAT = 0xA7;
struct StbBlk { u8* p; DWORD cap, req, t, who, tid; u8 state, pat, poisoned; };  // t = handed out at (INUSE / EXT) or idle since (IDLE), ms
// Every field changes under g_stbLock.
struct StbWho { DWORD at, via, how, liveN, grows, maxReq; u64 n, bytes, big, bigB, frees, quick, xfree, lifeMs, liveB, peakB, served, fresh, fallback; };
struct StbTid { DWORD tid; u64 allocs, frees; };
struct StbStats {
    u64 reqs, bytes, big, bigB, served, servedB, fresh, fallback, tooBig, frees, xfree, grows, growB, zeroUs, zeroB, unwrittenB;
    DWORD ownedB, idleB, inUseB, peakInUseB, peakOwnedB, liveN, peakLiveN, evicts, trims, dfree, vaFail, uafw, unwrittenN, lost, untracked;
};
static StbBlk g_stbBlk[STB_N];
static DWORD g_stbHi;                 // slots [0, g_stbHi) may be in use
static StbWho g_stbWho[STB_WHO_N];      // [0] = the frame was not readable, or the rows are taken
static StbTid g_stbTid[STB_TID_N];      // 128 KB+ blocks by thread; [0] = the rows are taken
static u64 g_stbSz[STB_SZ_N];          // requests by size: < 4 KB, < 8 KB, ... < 16 MB, larger
static StbStats g_stb;
static SRWLOCK g_stbLock = SRWLOCK_INIT;
static volatile LONG g_stbOwn[2048];  // one bit per 64 KB of the address space: a pool block starts there (set before the pointer is handed out)
static volatile LONG g_stbArm;        // STB_* bits the stubs act on (0 = both pass through to the game)
static volatile LONG g_stbDetached;   // process exit: no lock is taken any more
static int g_stbInstalled;            // the three sites are patched
static bool g_stbFrames, g_stbDead;    // Realloc's callers matched (the stream and its sizer can be read from the stack); stopped for the session
static DWORD g_stbMin = 128u << 10, g_stbMaxBlock = 8u << 20, g_stbBudget = 48u << 20, g_stbIdleMs = 5000;
static wchar_t g_stbIni[MAX_PATH];
struct StbPatch { u8* at; u8 mine[16]; DWORD n; };
static StbPatch g_stbPatch[3];

static LONGLONG g_stbQpf;
static LONGLONG StbQpc() {
    LARGE_INTEGER c;
    if (!g_stbQpf) { QueryPerformanceFrequency(&c); g_stbQpf = c.QuadPart ? c.QuadPart : 1; }
    QueryPerformanceCounter(&c);
    return c.QuadPart;
}
static DWORD StbNowMs() { return (DWORD)(StbQpc() * 1000 / g_stbQpf); }
static DWORD (*g_stbNow)() = StbNowMs;  // selftest: its own clock

// --- size policy ---
static DWORD StbCap(DWORD n) { return (n + 0xFFFF) & ~0xFFFFu; }  // a new block: the request rounded up to 64 KB (what the OS reserves anyway)
// An idle block of `cap` bytes may serve a request of n: at most a quarter of the request to spare (a 1408 KB block never
// serves a 704 KB request), or the block the request itself would have made.
static bool StbFits(DWORD cap, DWORD n) { return cap >= n && (cap == StbCap(n) || cap - n <= n / 4); }
static bool StbPoolable(DWORD n) { return n >= g_stbMin && n <= g_stbMaxBlock && StbCap(n) <= g_stbBudget; }
static DWORD StbSzIx(DWORD n) {
    DWORD i = 0;
    for (DWORD t = 4096; i < STB_SZ_N - 1 && n >= t; t <<= 1) i++;
    return i;
}

static bool StbOwned(const void* p) {
    const DWORD g = (DWORD)(uintptr_t)p >> 16;
    return !((uintptr_t)p & 0xFFFF) && ((g_stbOwn[g >> 5] >> (g & 31)) & 1) != 0;
}
static void StbOwn(const void* p, bool on) {
    const DWORD g = (DWORD)(uintptr_t)p >> 16;
    if (on) _InterlockedOr(&g_stbOwn[g >> 5], (LONG)(1u << (g & 31)));
    else _InterlockedAnd(&g_stbOwn[g >> 5], (LONG)~(1u << (g & 31)));
}

// The game's allocator (EA ICoreAllocator: vtable +8 = Alloc(size, name, flags), +0xC = Free(ptr, size); thiscall).
typedef void* (__fastcall* StbGameAllocFn)(void* self, void* edx, DWORD size, const char* name, DWORD flags);
typedef void (__fastcall* StbGameFreeFn)(void* self, void* edx, void* p, DWORD size);
static void* StbGameAlloc(void* a, DWORD n, const char* name) { return ((StbGameAllocFn)(*(void***)a)[2])(a, NULL, n, name, 0); }
static void StbGameFree(void* a, void* p) { ((StbGameFreeFn)(*(void***)a)[3])(a, NULL, p, 0); }

// Who sized the stream: a = the address of StbAlloc's first argument. Above its four arguments the stack holds, by the
// byte-checked code: the constructor's saved esi and return 0x4DEE8C (a[4], a[5]), its three arguments, Realloc's four
// saved registers (a[9..12]), Realloc's return a[13] and argument a[14], then the caller's frame: SetSize and SetPosition
// two saved registers and the return (a[17]); Write four saved registers, 8 bytes of locals and the return (a[21]).
// how: 1 SetSize, 2 Write, 3 SetPosition, 0 = not read. via = the next return address into the exe above it.
static DWORD StbCreator(const DWORD* a, const DWORD* end, DWORD* via, DWORD* how) {
    *via = 0, *how = 0;
    if (!g_stbFrames || a + 24 >= end || a[5] != STB_RET_CTOR) return 0;
    int i;
    if (a[13] == STB_RET_SETSIZE) i = 17, *how = 1;
    else if (a[13] == STB_RET_WRITE) i = 21, *how = 2;
    else if (a[13] == STB_RET_SETPOS) i = 17, *how = 3;
    else return 0;
    const DWORD who = a[i];
    for (int j = i + 1; j < i + 64 && a + j < end; j++) {
        const DWORD v = a[j];
        if (v >= g_asTextLo + 8 && v < g_asTextHi && v != who && AsCallBefore((const u8*)(uintptr_t)v)) { *via = v; break; }
    }
    return who;
}
static DWORD (*g_stbWhoFn)(const DWORD*, const DWORD*, DWORD*, DWORD*) = StbCreator;  // selftest: its own

// Lock held from here to StbRemove.
static DWORD StbWhoOf(DWORD at, DWORD via, DWORD how) {
    for (DWORD i = 1; at && i < STB_WHO_N; i++) {
        if (g_stbWho[i].at == at) return i;
        if (!g_stbWho[i].at) { g_stbWho[i].at = at, g_stbWho[i].via = via, g_stbWho[i].how = how; return i; }
    }
    return 0;
}
static StbTid& StbTidOf(DWORD tid) {
    for (DWORD i = 1; i < STB_TID_N; i++) {
        if (g_stbTid[i].tid == tid) return g_stbTid[i];
        if (!g_stbTid[i].tid) { g_stbTid[i].tid = tid; return g_stbTid[i]; }
    }
    return g_stbTid[0];
}
static int StbFreeSlot() {
    for (DWORD i = 0; i < STB_N; i++)
        if (g_stbBlk[i].state == STBS_FREE) { if (i >= g_stbHi) g_stbHi = i + 1; return (int)i; }
    return -1;
}
static int StbFind(const u8* p, bool ext) {
    for (DWORD i = 0; i < g_stbHi; i++)
        if (g_stbBlk[i].p == p && g_stbBlk[i].state != STBS_FREE && (g_stbBlk[i].state == STBS_EXT) == ext) return (int)i;
    return -1;
}
static void StbSlotClear(int i) {
    g_stbBlk[i] = StbBlk{};
    while (g_stbHi && g_stbBlk[g_stbHi - 1].state == STBS_FREE) g_stbHi--;
}
// A 128 KB+ block of row wi is live (ours or the game's) / gone after lifeMs.
static void StbLive(DWORD wi, DWORD req) {
    StbWho& w = g_stbWho[wi];
    w.liveN++, w.liveB += req, g_stb.liveN++;
    if (w.liveB > w.peakB) w.peakB = w.liveB;
    if (g_stb.liveN > g_stb.peakLiveN) g_stb.peakLiveN = g_stb.liveN;
}
static void StbGone(const StbBlk& b, DWORD now, DWORD tid) {
    StbWho& w = g_stbWho[b.who < STB_WHO_N ? b.who : 0];
    const DWORD life = now - b.t;
    w.liveN--, w.liveB -= b.req, g_stb.liveN--;
    w.frees++, w.lifeMs += life, w.quick += life < 1000, g_stb.frees++;
    if (tid != b.tid) w.xfree++, g_stb.xfree++;
    StbTidOf(tid).frees++;
}
// An idle block leaves the pool: its owner bit and slot go; the caller releases the memory after the lock.
static u8* StbRemove(int i) {
    u8* const p = g_stbBlk[i].p;
    g_stb.ownedB -= g_stbBlk[i].cap, g_stb.idleB -= g_stbBlk[i].cap;
    StbOwn(p, false);
    StbSlotClear(i);
    return p;
}

static bool StbAllIs(const u8* p, DWORD n, u8 v) {
    const u64 w = 0x0101010101010101ull * v;
    DWORD i = 0;
    for (; i + 8 <= n; i += 8)
        if (*(const u64*)(p + i) != w) return false;
    for (; i < n; i++)
        if (p[i] != v) return false;
    return true;
}
// Verify 2: the bytes of [p, p+n) that still hold the hand-out pattern (whole aligned 8-byte groups); *first = the first one.
static DWORD StbUnwritten(const u8* p, DWORD n, DWORD* first) {
    const u64 w = 0x0101010101010101ull * STB_PAT;
    DWORD k = 0;
    *first = 0;
    for (DWORD i = 0; i + 8 <= n; i += 8)
        if (*(const u64*)(p + i) == w) { if (!k) *first = i; k += 8; }
    return k;
}

// In place of the allocator call at 0x4DEC15 (reached only from MemoryStream::Realloc). stream = Realloc's `this`.
static void* __stdcall StbAlloc(DWORD size, void* stream, void* allocator, const char* name) {
    const LONG arm = g_stbArm;
    if (!arm) return StbGameAlloc(allocator, size, name);
    const bool big = size >= g_stbMin;
    DWORD who = 0, via = 0, how = 0, oldCap = 0;
    bool grow = false;
    if (big || (arm & STB_STATS)) {
        who = g_stbWhoFn((const DWORD*)&size, (const DWORD*)(uintptr_t)__readfsdword(4), &via, &how);
        if (how && stream) grow = *(void* const*)((const u8*)stream + 4) != NULL, oldCap = *(const DWORD*)((const u8*)stream + 0x14);
    }
    const DWORD now = g_stbNow(), tid = GetCurrentThreadId(), cap = StbCap(size);
    u8* drop[64];
    DWORD dropN = 0, wi;
    int slot = -1;
    bool reused = false, poisoned = false, zeroed = false;
    u8* p = NULL;
    AcquireSRWLockExclusive(&g_stbLock);
    wi = StbWhoOf(who, via, how);
    StbWho& w = g_stbWho[wi];
    g_stb.reqs++, g_stb.bytes += size, g_stbSz[StbSzIx(size)]++, w.n++, w.bytes += size;
    if (size > w.maxReq) w.maxReq = size;
    if (grow) g_stb.grows++, w.grows++, g_stb.growB += oldCap < size ? oldCap : size;
    if (big) g_stb.big++, g_stb.bigB += size, w.big++, w.bigB += size, StbTidOf(tid).allocs++;
    if ((arm & STB_POOL) && big) {
        if (!StbPoolable(size)) g_stb.tooBig++;
        else {
            for (DWORD i = 0; i < g_stbHi; i++) {  // the smallest idle block that fits, the most recently freed of that size
                const StbBlk& b = g_stbBlk[i];
                if (b.state != STBS_IDLE || !StbFits(b.cap, size)) continue;
                if (slot < 0 || b.cap < g_stbBlk[slot].cap || (b.cap == g_stbBlk[slot].cap && (LONG)(b.t - g_stbBlk[slot].t) > 0)) slot = (int)i;
            }
            if (slot >= 0) reused = true, p = g_stbBlk[slot].p, poisoned = g_stbBlk[slot].poisoned != 0, g_stb.idleB -= g_stbBlk[slot].cap;
            else {
                while (g_stb.ownedB + cap > g_stbBudget && dropN < 64) {  // room for a new block: idle ones go, oldest first
                    int old = -1;
                    for (DWORD i = 0; i < g_stbHi; i++)
                        if (g_stbBlk[i].state == STBS_IDLE && (old < 0 || (LONG)(g_stbBlk[i].t - g_stbBlk[old].t) < 0)) old = (int)i;
                    if (old < 0) break;
                    drop[dropN++] = StbRemove(old), g_stb.evicts++;
                }
                if (g_stb.ownedB + cap <= g_stbBudget && (slot = StbFreeSlot()) >= 0) g_stbBlk[slot].cap = cap, g_stb.ownedB += cap;
                else slot = -1, g_stb.fallback++, w.fallback++;
            }
            if (slot >= 0) g_stbBlk[slot].state = STBS_BUSY;
        }
    }
    ReleaseSRWLockExclusive(&g_stbLock);
    for (DWORD i = 0; i < dropN; i++) VirtualFree(drop[i], 0, MEM_RELEASE);
    if (slot >= 0) {
        bool dirty = false;
        LONGLONG zt = 0;
        if (reused) {
            if (poisoned) dirty = !StbAllIs(p, g_stbBlk[slot].cap, STB_POISON);  // BUSY: only this thread touches the slot's block
            if (arm & STB_VERIFY2) memset(p, STB_PAT, size);
            else if ((arm & STB_ZERO) || poisoned) zeroed = true, zt = StbQpc(), memset(p, 0, size), zt = StbQpc() - zt;
        } else {
            p = (u8*)VirtualAlloc(NULL, cap, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);  // zero pages, as the game's own big block
            if (p && (arm & STB_VERIFY2)) memset(p, STB_PAT, size);
        }
        AcquireSRWLockExclusive(&g_stbLock);
        StbBlk& b = g_stbBlk[slot];
        if (p && !((uintptr_t)p & 0xFFFF)) {
            b.p = p, b.req = size, b.t = now, b.who = wi, b.tid = tid, b.state = STBS_INUSE, b.pat = (arm & STB_VERIFY2) != 0, b.poisoned = 0;
            StbOwn(p, true);
            g_stb.inUseB += b.cap;
            if (g_stb.inUseB > g_stb.peakInUseB) g_stb.peakInUseB = g_stb.inUseB;
            if (g_stb.ownedB > g_stb.peakOwnedB) g_stb.peakOwnedB = g_stb.ownedB;
            if (reused) g_stb.served++, g_stb.servedB += size, g_stbWho[wi].served++; else g_stb.fresh++, g_stbWho[wi].fresh++;
            if (zeroed) g_stb.zeroUs += (u64)(zt * 1000000 / g_stbQpf), g_stb.zeroB += size;
            g_stb.uafw += dirty;
            StbLive(wi, size);
        } else {  // no memory for a new block (never on reuse): the slot goes, the game's own path answers
            g_stb.ownedB -= b.cap, g_stb.vaFail++, g_stb.fallback++, g_stbWho[wi].fallback++;
            StbSlotClear(slot);
            slot = -1;
        }
        ReleaseSRWLockExclusive(&g_stbLock);
        if (dirty) Log("streambuf: VERIFY a freed block was written to before its reuse (0x%p, %lu bytes): uafw", p, size);
        if (slot >= 0) return p;
        if (p) VirtualFree(p, 0, MEM_RELEASE);  // not 64 KB aligned: cannot happen
    }
    p = (u8*)StbGameAlloc(allocator, size, name);
    if (p && big && (arm & STB_STATS)) {  // the game's block, tracked for its lifetime
        AcquireSRWLockExclusive(&g_stbLock);
        const int e = StbFind(p, true);  // a block we never saw freed (freed by a path that is not ours: none known)
        if (e >= 0) g_stb.lost++, g_stbWho[g_stbBlk[e].who].liveN--, g_stbWho[g_stbBlk[e].who].liveB -= g_stbBlk[e].req, g_stb.liveN--, StbSlotClear(e);
        const int s = StbFreeSlot();
        if (s < 0) g_stb.untracked++;
        else {
            StbBlk& b = g_stbBlk[s];
            b.p = p, b.cap = b.req = size, b.t = now, b.who = wi, b.tid = tid, b.state = STBS_EXT;
            StbLive(wi, size);
        }
        ReleaseSRWLockExclusive(&g_stbLock);
    }
    return p;
}

// A pool block comes back (its SharedPointer ended).
static void StbPut(u8* p) {
    if (g_stbDetached) return;  // process exit: the memory goes with the process
    const LONG arm = g_stbArm;
    const DWORD now = g_stbNow(), tid = GetCurrentThreadId();
    DWORD req = 0, cap = 0, wi = 0;
    bool pat = false;
    AcquireSRWLockExclusive(&g_stbLock);
    const int i = StbFind(p, false);
    if (i < 0) g_stb.lost++;  // owner bit without a block: cannot happen; the block is leaked, never handed to the game's free
    else if (g_stbBlk[i].state != STBS_INUSE) g_stb.dfree++;  // freed twice: the second free does nothing
    else {
        StbBlk& b = g_stbBlk[i];
        StbGone(b, now, tid);
        g_stb.inUseB -= b.cap;
        req = b.req, cap = b.cap, wi = b.who, pat = b.pat != 0;
        if (pat || (arm & STB_VERIFY)) b.state = STBS_BUSY;
        else b.state = STBS_IDLE, b.t = now, g_stb.idleB += cap;
    }
    ReleaseSRWLockExclusive(&g_stbLock);
    if (!cap || !(pat || (arm & STB_VERIFY))) return;
    DWORD first = 0;
    const DWORD un = pat ? StbUnwritten(p, req, &first) : 0;
    if (arm & STB_VERIFY) memset(p, STB_POISON, cap);
    AcquireSRWLockExclusive(&g_stbLock);
    StbBlk& b = g_stbBlk[i];  // BUSY: the slot stayed ours
    b.state = STBS_IDLE, b.t = now, b.poisoned = (arm & STB_VERIFY) != 0, g_stb.idleB += cap;
    const DWORD nth = un ? ++g_stb.unwrittenN : 0;
    g_stb.unwrittenB += un;
    const DWORD at = g_stbWho[wi].at;
    ReleaseSRWLockExclusive(&g_stbLock);
    if (nth && nth <= 16)
        Log("streambuf: VERIFY unwritten bytes at the free: sizer 0x%08lX, %lu of %lu bytes still hold the hand-out pattern, first at +0x%lX", at, un, req, first);
}
// The game's block is freed (Stats).
static void StbExtGone(const u8* p) {
    const DWORD now = g_stbNow(), tid = GetCurrentThreadId();
    AcquireSRWLockExclusive(&g_stbLock);
    const int i = StbFind(p, true);
    if (i >= 0) StbGone(g_stbBlk[i], now, tid), StbSlotClear(i);
    ReleaseSRWLockExclusive(&g_stbLock);
}
// In place of the two `allocator->Free(data, 0)` calls (0x4DEAA7, 0x4DEF07). sp = the SharedPointer whose last
// reference went, with its "free the data" byte set.
static void __stdcall StbFree(void* sp) {
    u8* const p = *(u8* const*)((const u8*)sp + 8);
    if (p && StbOwned(p)) { StbPut(p); return; }
    if (p && (g_stbArm & STB_STATS)) StbExtGone(p);
    StbGameFree(*(void* const*)((const u8*)sp + 4), p);
}

// Upkeep, every 500 ms on this module's thread: idle blocks older than IdleSec go back to the OS (all of them when the
// pool is switched off or stopped).
static void StbTick(DWORD now) {
    u8* drop[STB_N];
    DWORD k = 0;
    const bool all = !(g_stbArm & STB_POOL);
    AcquireSRWLockExclusive(&g_stbLock);
    for (DWORD i = g_stbHi; i-- > 0;)
        if (g_stbBlk[i].state == STBS_IDLE && (all || (g_stbIdleMs && now - g_stbBlk[i].t >= g_stbIdleMs))) drop[k++] = StbRemove((int)i), g_stb.trims++;
    ReleaseSRWLockExclusive(&g_stbLock);
    for (DWORD i = 0; i < k; i++) VirtualFree(drop[i], 0, MEM_RELEASE);
}

// The switches as the ini has them now.
static int StbIniMask(const wchar_t* ini) {
    if (GetPrivateProfileIntW(L"StreamBuf", L"Enabled", 0, ini) <= 0) return 0;
    const int v = GetPrivateProfileIntW(L"StreamBuf", L"Verify", 0, ini);
    const int m = (GetPrivateProfileIntW(L"StreamBuf", L"Pool", 0, ini) > 0 ? STB_POOL : 0) | (GetPrivateProfileIntW(L"StreamBuf", L"Stats", 0, ini) > 0 ? STB_STATS : 0);
    if (!(m & STB_POOL)) return m;
    return m | (GetPrivateProfileIntW(L"StreamBuf", L"Zero", 1, ini) > 0 ? STB_ZERO : 0) | (v >= 1 ? STB_VERIFY : 0) | (v >= 2 ? STB_VERIFY2 : 0);
}
// Every 5 s: a patched site that no longer holds our bytes stops the module for the session (the stubs may still be
// reached through the other sites, so they pass through and pool blocks still come home); else the ini's switches apply.
static void StbWatch(int mask) {
    if (g_stbDead) return;
    for (const StbPatch& p : g_stbPatch)
        if (p.at && memcmp(p.at, p.mine, p.n)) {
            g_stbDead = true;
            InterlockedExchange(&g_stbArm, 0);
            Log("streambuf: stopped for this session: the bytes at 0x%p were changed by another module (nothing armed; idle blocks released)", p.at);
            return;
        }
    const LONG was = g_stbArm;
    if (was == mask) return;
    InterlockedExchange(&g_stbArm, mask);
    Log("streambuf: switched at run time by the ini: arm %ld -> %d (1 pool, 2 stats, 4 zero, 8 verify, 16 verify 2)", was, mask);
}
static void StbDetach() { InterlockedExchange(&g_stbDetached, 1), InterlockedExchange(&g_stbArm, 0); }  // DLL_PROCESS_DETACH
static DWORD WINAPI StbThread(void*) {
    for (DWORD watch = GetTickCount();;) {
        Sleep(500);
        const DWORD tick = GetTickCount();
        if (tick - watch >= 5000) watch = tick, StbWatch(StbIniMask(g_stbIni));
        if (!g_stbDetached) StbTick(g_stbNow());
    }
}

static void StbSkip(const char* what, const u8* at, DWORD game, size_t n) {
    char hex[3 * 24 + 1];
    if (Readable(at, n)) Hex(at, n < 24 ? n : 24, hex, sizeof(hex)); else strcpy_s(hex, "<unreadable>");
    Log("SKIP stream buffer: bytes differ in %s at 0x%08lX (found %s...): not installed", what, game, hex);
}
// Checks the three windows (and the three callers' frames, which only the sizer attribution needs), then writes the two
// free sites and last the allocation site, so no pool block exists before its free is caught.
static bool StbInstallSites(u8* ctor, u8* rel, u8* realloc, const u8* setSize, const u8* write, const u8* setPos) {
    if (!Matches(ctor, kStbCtor, sizeof(kStbCtor))) { StbSkip("the SharedPointer constructor", ctor, STB_CTOR, sizeof(kStbCtor)); return false; }
    if (!Matches(rel, kStbRel, sizeof(kStbRel))) { StbSkip("SharedPointer::Release", rel, STB_REL, sizeof(kStbRel)); return false; }
    if (!Matches(realloc, kStbRealloc, sizeof(kStbRealloc))) { StbSkip("MemoryStream::Realloc", realloc, STB_REALLOC, sizeof(kStbRealloc)); return false; }
    g_stbFrames = Matches(setSize, kStbSetSize, sizeof(kStbSetSize)) && Matches(write, kStbWrite, sizeof(kStbWrite)) && Matches(setPos, kStbSetPos, sizeof(kStbSetPos));
    if (!g_stbFrames) Log("WARN stream buffer: SetSize / Write / SetPosition bytes differ: installed, but the sizer of a stream is not read (who rows empty)");
    u8* const at[3] = {rel + STB_REL_AT, realloc + STB_REALLOC_AT, ctor + STB_CTOR_AT};
    for (int i = 0; i < 3; i++) {
        StbPatch& p = g_stbPatch[i];
        if (i < 2) {  // push esi; call StbFree; jmp +8; int3 x 8
            const LONG r = (LONG)((uintptr_t)&StbFree - ((uintptr_t)at[i] + 6));
            p.n = 16, p.mine[0] = 0x56, p.mine[1] = 0xE8, memcpy(p.mine + 2, &r, 4), p.mine[6] = 0xEB, p.mine[7] = 0x08, memset(p.mine + 8, 0xCC, 8);
        } else {  // push ecx (name); push eax (allocator); push ebx (stream); push [esp+14h] (size); call StbAlloc
            static const u8 head[7] = {0x51, 0x50, 0x53, 0xFF, 0x74, 0x24, 0x14};
            const LONG r = (LONG)((uintptr_t)&StbAlloc - ((uintptr_t)at[i] + 12));
            p.n = 12, memcpy(p.mine, head, 7), p.mine[7] = 0xE8, memcpy(p.mine + 8, &r, 4);
        }
        if (!Write(at[i], p.mine, p.n)) {
            Log("FAIL stream buffer: write at 0x%p (err %lu): %s", at[i], GetLastError(), i ? "the free sites stay patched (they pass through), nothing armed" : "nothing written");
            return false;
        }
        p.at = at[i];
    }
    return true;
}

// "diag: mem" mark (memdiag.cpp MdAfterSample)
static void StbMemLine(LONG n, const char* tag) {
    if (!g_stbInstalled) return;
    static StbWho who[STB_WHO_N];  // copies: formatted after the lock (one mark at a time: the memory worker)
    static StbTid tids[STB_TID_N];
    u64 sz[STB_SZ_N];
    AcquireSRWLockShared(&g_stbLock);
    const StbStats s = g_stb;
    memcpy(who, g_stbWho, sizeof(who)), memcpy(tids, g_stbTid, sizeof(tids)), memcpy(sz, g_stbSz, sizeof(sz));
    ReleaseSRWLockShared(&g_stbLock);
    const double MB = 1.0 / (1 << 20);
    Log("streambuf: #%ld %s arm=%ld requests=%llu MB=%.1f big=%llu bigMB=%.1f served=%llu servedMB=%.1f fresh=%llu fallback=%llu tooBig=%llu "
        "frees=%llu xthreadFrees=%llu grows=%llu growCopyMB=%.1f poolMB=%.1f idleMB=%.1f inUseMB=%.1f peakInUseMB=%.1f peakPoolMB=%.1f liveN=%lu "
        "peakLiveN=%lu evicts=%lu trims=%lu zeroMs=%.1f zeroMB=%.1f errors: dfree=%lu vaFail=%lu uafw=%lu unwrittenN=%lu unwrittenKB=%llu lost=%lu "
        "untracked=%lu",
        n, tag, g_stbArm, s.reqs, s.bytes * MB, s.big, s.bigB * MB, s.served, s.servedB * MB, s.fresh, s.fallback, s.tooBig, s.frees, s.xfree, s.grows,
        s.growB * MB, s.ownedB * MB, s.idleB * MB, s.inUseB * MB, s.peakInUseB * MB, s.peakOwnedB * MB, s.liveN, s.peakLiveN, s.evicts, s.trims,
        s.zeroUs / 1000.0, s.zeroB * MB, s.dfree, s.vaFail, s.uafw, s.unwrittenN, s.unwrittenB >> 10, s.lost, s.untracked);
    char line[400];
    int at = 0;
    for (DWORD i = 0; i < STB_SZ_N; i++)
        if (sz[i]) at += _snprintf_s(line + at, sizeof(line) - at, _TRUNCATE, " %s%lu%s=%llu", i == STB_SZ_N - 1 ? ">=" : "<", i < 8 ? 4ul << i : 1ul << (i < STB_SZ_N - 1 ? i - 8 : i - 9), i < 8 ? "K" : "M", sz[i]);
    if (at) Log("streambuf: sizes #%ld (requests by size)%s", n, line);
    static const char* const kHow[4] = {"?", "SetSize", "Write", "SetPosition"};
    bool shown[STB_WHO_N] = {};
    for (int row = 0; row < 10; row++) {  // the ten sizers with the most bytes
        int best = -1;
        for (DWORD i = 0; i < STB_WHO_N; i++)
            if (who[i].n && !shown[i] && (best < 0 || who[i].bytes > who[best].bytes)) best = (int)i;
        if (best < 0) break;
        shown[best] = true;
        const StbWho& w = who[best];
        Log("streambuf: who #%ld at=0x%08lX via=0x%08lX how=%s n=%llu MB=%.1f big=%llu bigMB=%.1f maxKB=%lu grows=%lu served=%llu fresh=%llu "
            "fallback=%llu frees=%llu xthreadFrees=%llu liveN=%lu liveMB=%.1f peakMB=%.1f avgLifeMs=%.0f under1s=%.0f%%",
            n, w.at, w.via, kHow[w.how & 3], w.n, w.bytes * MB, w.big, w.bigB * MB, w.maxReq >> 10, w.grows, w.served, w.fresh, w.fallback, w.frees,
            w.xfree, w.liveN, w.liveB * MB, w.peakB * MB, w.frees ? (double)w.lifeMs / w.frees : 0.0, w.frees ? 100.0 * w.quick / w.frees : 0.0);
    }
    at = 0;
    for (DWORD i = 0; i < STB_TID_N; i++)
        if (tids[i].allocs || tids[i].frees) at += _snprintf_s(line + at, sizeof(line) - at, _TRUNCATE, " %lu:a=%llu,f=%llu", tids[i].tid, tids[i].allocs, tids[i].frees);
    if (at) Log("streambuf: threads #%ld (big blocks: thread id, allocated, freed; 0 = other threads)%s", n, line);
}

static bool ApplyStreamBuf(const wchar_t* ini) {
    if (GetPrivateProfileIntW(L"StreamBuf", L"Enabled", 0, ini) <= 0) return false;
    const int mask = StbIniMask(ini);
    wcscpy_s(g_stbIni, ini);
    const int poolMB = GetPrivateProfileIntW(L"StreamBuf", L"PoolMB", 48, ini), minKB = GetPrivateProfileIntW(L"StreamBuf", L"MinKB", 128, ini);
    const int blkMB = GetPrivateProfileIntW(L"StreamBuf", L"MaxBlockMB", 8, ini), idle = GetPrivateProfileIntW(L"StreamBuf", L"IdleSec", 5, ini);
    g_stbBudget = (DWORD)(poolMB < 1 ? 1 : poolMB > 256 ? 256 : poolMB) << 20;
    g_stbMin = (DWORD)(minKB < 64 ? 64 : minKB > 65536 ? 65536 : minKB) << 10;
    g_stbMaxBlock = (DWORD)(blkMB < 1 ? 1 : blkMB > 64 ? 64 : blkMB) << 20;
    g_stbIdleMs = (DWORD)(idle < 0 ? 0 : idle > 3600 ? 3600 : idle) * 1000;
    if (!mask) { Log("stream buffer: Enabled=1 with Pool and Stats both 0: nothing to do"); return false; }
    if (!StbInstallSites((u8*)(uintptr_t)STB_CTOR, (u8*)(uintptr_t)STB_REL, (u8*)(uintptr_t)STB_REALLOC, (const u8*)(uintptr_t)STB_SETSIZE,
                        (const u8*)(uintptr_t)STB_WRITE, (const u8*)(uintptr_t)STB_SETPOS))
        return false;
    g_stbInstalled = 1;
    HANDLE t = CreateThread(NULL, 64 * 1024, StbThread, NULL, STACK_SIZE_PARAM_IS_A_RESERVATION, NULL);
    if (t) CloseHandle(t);
    else Log("WARN stream buffer: no upkeep thread (%lu): no idle release, no run-time switch", GetLastError());
    InterlockedExchange(&g_stbArm, mask);
    Log("OK   stream buffer (docs/547): sites 0x4DEC0B / 0x4DEA99 / 0x4DEEF9 patched; Pool %s (up to %lu MB, requests %lu KB-%lu MB, idle %lu s, "
        "Zero %s, Verify %d), Stats %s%s (every \"diag: mem\" mark adds \"streambuf:\" lines)",
        (mask & STB_POOL) ? "on" : "off", g_stbBudget >> 20, g_stbMin >> 10, g_stbMaxBlock >> 20, g_stbIdleMs / 1000, (mask & STB_ZERO) ? "on" : "off",
        (mask & STB_VERIFY2) ? 2 : (mask & STB_VERIFY) ? 1 : 0, (mask & STB_STATS) ? "on" : "off", g_apexFound ? "; Apex Radiance loaded (no shared site)" : "");
    return true;
}
