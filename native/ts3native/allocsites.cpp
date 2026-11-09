// [MemDiag] AllocSites (docs/201): attributes native address-space growth to call sites. TEST-RUN TOOL, off by default,
// never a release default. The allocator is Apex Radiance's area (docs/191 Ownership): this only observes, and it stands
// down when Apex is loaded ([Compat] ApexStandDown=1). Included by ts3native.cpp after memdiag.cpp (g_mdOn, MdTag; its
// output rides on every "diag: mem" sample: each mem_mark and every 5 min of play).
//
// Hooks (docs/201 §2, each verified against the bytes before it is written; a jmp already there is chained, never lost):
//   EA GeneralAllocator locked entries (alloc.cpp, docs/15 §8): Malloc 0x4E69D0 (size, flags) ret 8, Memalign 0x4E6A20
//     (size, align, offset, flags) ret 0x10, Realloc 0x4E6A70 (ptr, size, flags) ret 0xC, Free 0x4E5920 (ptr) ret 4;
//     prologue 56 57 8B F9 8B B7 E4 04 00 00 at all four [verified disassembly 2026-10-26]. Only cores 0x4E5E40 / 0x4E51B0 /
//     0x4E6710 / 0x4E63F0 are called past them, from the realloc core and the EA debug layer 0x4E7B9E-0x4E846A (reached
//     only when ICoreAllocator 0x4E3E40 finds debug allocator id 0x8843221A [inferred off in retail]).
//   ntdll NtAllocateVirtualMemory(+Ex) / NtFreeVirtualMemory / NtMapViewOfSection(+Ex) / NtUnmapViewOfSection(+Ex): every
//     module's VirtualAlloc(2) / VirtualFree / MapViewOfFile(Ex, 3) / UnmapViewOfFile ends there (EA cores and direct blocks
//     0x4E48F0 / 0x4E50F0, Boehm GC_win32_get_mem 0xD70900, S3SS mimalloc arenas, DXVK MemoryFilePool, NT heap segments).
//     WoW64 stubs start with mov eax,imm32 (B8, 5 bytes, no relative operand) [verified at install per stub, host selftest
//     round trips]. A MEM_COMMIT without MEM_RESERVE at a NULL base reserves too and is recorded (docs/201 §14).
// Untracked: each output walks the address space and lists allocations >= 1 MB no record owns (docs/201 §14).
// Sampling: heap blocks < 64 KB 1 in N by a hash of the block address (so Free knows without a lookup whether to look),
// weight N; blocks >= 64 KB (chunk size word at p-0xC, alloc.cpp) and every reservation / view, weight 1.
// Attribution: at = first frame outside the allocator (EA wrappers 0x4E3D00-0x4E9000 skipped) or, for VirtualAlloc /
// views, the first frame outside ntdll / kernelbase / kernel32; from = the next one (other module, or outside the EA
// allocator range). Frames come from a stack scan for return addresses preceded by a call [inferred: frameless EA code
// leaves no ebp chain; a stale return address in an uninitialised local can be picked up].
// No allocation inside a hook: fixed tables reserved at install (sites 16384, sampled blocks 256 stripes x 1024, regions
// 16384), per-stripe try-locks with a bounded spin (a suspended holder - Boehm stop-the-world - costs a dropped record,
// never a hang), a per-thread reentrancy flag in a thread-id table (no TLS: docs/201 §15). Nothing a hook calls takes a
// lock or allocates (only VirtualQuery, GetProcessId, QueryPerformanceCounter: system calls without user-mode locks).

static bool g_asOn;
static DWORD g_asShift = 26;  // sampled when (hash >> g_asShift) == 0: 1 in 2^(32 - shift); 26 = 1 in 64
static DWORD g_asN = 64;
static int g_asTop = 12;
static const DWORD kAsBig = 64 * 1024;
// code ranges (the host selftest points them at its own image)
static DWORD g_asTextLo = 0x00401000, g_asTextHi = 0x00F95000, g_asExeBase = 0x00400000;
static DWORD g_asWrapLo = 0x004E3D00, g_asWrapHi = 0x004E9000;  // EA allocator wrappers, cores and debug layer
static DWORD g_asSelfLo, g_asSelfHi;  // our own image: our hooks' frames are never a site (docs/201 §12.1)
static DWORD g_asHeapAt[4] = {0x004E69D0, 0x004E6A20, 0x004E6A70, 0x004E5920};  // Malloc, Memalign, Realloc, Free
static DWORD g_asSys[3][2];  // ntdll, kernelbase, kernel32: [base, end)

enum { AS_HEAP, AS_VIRT, AS_MAP, AS_KINDS };
static const char* const kAsKind[AS_KINDS] = {"heap", "virtual", "map"};

struct AsSite {
    volatile LONG used;  // 0 free, 1 being filled, 2 ready
    DWORD f0, f1;
    LONG kind;
    volatile LONG64 live, allocB, freeB;
    volatile LONG liveN, allocN, freeN;
    LONG64 prevLive;  // output thread only
    LONG prevN;
};
static const int kAsSites = 16384;  // [0] = overflow (table full: siteFull counts the records it took; 4096 filled up, docs/201 §13.1)
struct AsBlk { DWORD p, w; WORD site, n; };  // n = count weight (1 or N)
static const int kAsStripes = 256, kAsPerStripe = 1024;
struct AsRgn { DWORD base, size, site, kind; };
static const int kAsRgns = 16384;

static AsSite* g_asSite;
static AsBlk* g_asBlk;
static volatile LONG* g_asStripeLock;
static AsRgn* g_asRgn;
// per event tag (mem_mark ev, e.g. live-empty): every site's live bytes / count at that tag's previous sample, so "cyc"
// rows give the growth from one cycle's mark to the next one with the same tag (growth that survives removal)
static const int kAsTags = 16;
static char g_asTag[kAsTags][32];
static LONG g_asTagN[kAsTags];  // sample number of the tag's previous output, 0 = none yet
static LONG64* g_asTagLive;     // [kAsTags][kAsSites]
static LONG* g_asTagCnt;        // [kAsTags][kAsSites]
static volatile LONG g_asRgnLock;
static volatile LONG g_asSiteLock;
static volatile LONG64 g_asSampled, g_asVCalls, g_asVUs;
static volatile LONG g_asBlocks, g_asRgnN, g_asDrops, g_asBusy, g_asNoFree, g_asRgnMiss, g_asVMaxUs, g_asFull;

static DWORD AsHash(DWORD p) { return (p >> 4) * 2654435761u; }
static bool AsSampled(DWORD p) { return g_asShift >= 32 || (AsHash(p) >> g_asShift) == 0; }

// bounded try-lock: false after ~1 ms of trying (the holder may be suspended by a stop-the-world)
static bool AsLock(volatile LONG* l) {
    for (int i = 0; i < 4096; i++) {
        if (!*l && !InterlockedExchange(l, 1)) return true;
        if (i < 64) YieldProcessor(); else SwitchToThread();
    }
    InterlockedIncrement(&g_asBusy);
    return false;
}
static void AsUnlock(volatile LONG* l) { InterlockedExchange(l, 0); }

// reentrancy: our own code below never allocates, but a hooked call made while recording must not record again.
// Not a TLS slot (0.36.82-0.36.87): the game's TlsAlloc indexes are >= 64, and TlsSetValue on such an index allocates the
// thread's expansion array from the process heap under the PEB lock on the thread's first use. The hooks run inside
// NtAllocateVirtualMemory, which the NT heap calls with its heap lock held: heap lock -> PEB lock here against PEB lock ->
// heap lock in any other thread's first TlsSetValue = deadlock at load (docs/201 §15, verified by a probe). Instead the
// threads now inside our code are thread ids in a small lock-free table: no allocation, no lock, no system call.
static volatile LONG g_asIn[256];
static int AsInHome() { return (int)(((DWORD)__readfsdword(0x24) * 2654435761u) >> 24); }  // TEB ClientId.UniqueThread
static bool AsEnter() {
    const LONG tid = (LONG)__readfsdword(0x24);
    const int h = AsInHome();
    for (int i = 0; i < 32; i++) if (g_asIn[(h + i) & 255] == tid) return false;  // a thread holds at most one slot, within 32 of home
    for (int i = 0; i < 32; i++)
        if (!g_asIn[(h + i) & 255] && !InterlockedCompareExchange(&g_asIn[(h + i) & 255], tid, 0)) return true;
    InterlockedIncrement(&g_asBusy);  // 32 threads inside at once on one run of slots: the record is dropped
    return false;
}
static void AsLeave() {
    const LONG tid = (LONG)__readfsdword(0x24);
    const int h = AsInHome();
    for (int i = 0; i < 32; i++) if (g_asIn[(h + i) & 255] == tid) { InterlockedExchange(&g_asIn[(h + i) & 255], 0); return; }
}

static int AsSiteOf(DWORD f0, DWORD f1, LONG kind) {
    DWORD h = (f0 * 2654435761u) ^ (f1 * 0x85EBCA6Bu) ^ (DWORD)kind * 0x9E3779B9u;
    for (int i = 0; i < 64; i++, h++) {
        const int at = 1 + (int)(h % (kAsSites - 1));
        AsSite& s = g_asSite[at];
        if (s.used == 2) {
            if (s.f0 == f0 && s.f1 == f1 && s.kind == kind) return at;
            continue;
        }
        if (s.used == 1) continue;  // another thread fills it: a duplicate row at worst (the report merges by key)
        if (InterlockedCompareExchange(&s.used, 1, 0) == 0) {
            s.f0 = f0, s.f1 = f1, s.kind = kind;
            MemoryBarrier();
            s.used = 2;
            return at;
        }
        if (s.used == 2 && s.f0 == f0 && s.f1 == f1 && s.kind == kind) return at;
    }
    InterlockedIncrement(&g_asFull);
    return 0;
}
static void AsSiteAdd(int site, LONG64 w, LONG n) {
    AsSite& s = g_asSite[site];
    InterlockedAdd64(&s.live, w), InterlockedAdd64(&s.allocB, w);
    InterlockedExchangeAdd(&s.liveN, n), InterlockedExchangeAdd(&s.allocN, n);
}
static void AsSiteSub(int site, LONG64 w, LONG n) {
    AsSite& s = g_asSite[site];
    InterlockedAdd64(&s.live, -w), InterlockedAdd64(&s.freeB, w);
    InterlockedExchangeAdd(&s.liveN, -n), InterlockedExchangeAdd(&s.freeN, n);
}

// --- frames -----------------------------------------------------------------------------------------------------------
static bool AsCallBefore(const u8* p) {  // the bytes before a return address end in a call
    if (p[-5] == 0xE8) return true;
    if (p[-6] == 0xFF && ((p[-5] & 0x38) == 0x10) && ((p[-5] & 0xC0) == 0x80 || (p[-5] & 0xC7) == 0x05)) return true;  // FF /2 disp32
    if (p[-3] == 0xFF && (p[-2] & 0xF8) == 0x50 && (p[-2] & 7) != 4) return true;                                  // FF /2 disp8
    if (p[-4] == 0xFF && p[-3] == 0x54) return true;                                                                // FF /2 sib disp8
    if (p[-2] == 0xFF && ((p[-1] & 0xF8) == 0xD0 || ((p[-1] & 0xF8) == 0x10 && (p[-1] & 7) != 4 && (p[-1] & 7) != 5))) return true;
    return false;
}
static bool AsExeRet(DWORD a) { return a >= g_asTextLo + 8 && a < g_asTextHi && AsCallBefore((const u8*)(uintptr_t)a); }
static bool AsInWrap(DWORD a) { return a >= g_asWrapLo && a < g_asWrapHi; }
static bool AsSysMod(DWORD base) {
    for (auto& s : g_asSys) if (base && base == s[0]) return true;
    return false;
}

// heap: the caller of Malloc (or of an EA wrapper around it), then the next exe frame outside the allocator
static void AsHeapFrames(DWORD ret, const DWORD* sp, DWORD* f0, DWORD* f1) {
    const DWORD* top = (const DWORD*)(uintptr_t)__readfsdword(4);
    DWORD a = ret, b = 0;
    bool wrap = AsInWrap(a) || (a >= g_asSelfLo && a < g_asSelfHi);  // a call from our own code: its exe caller instead
    for (int i = 0; i < 48 && sp + i < top && !b; i++) {
        const DWORD v = sp[i];
        if (v == a || AsInWrap(v) || !AsExeRet(v)) continue;
        if (wrap) a = v, wrap = false;
        else b = v;
    }
    *f0 = a, *f1 = b;
}

// virtual / map: any module. Image ranges and non-image 64 KB granules are cached (no VirtualQuery per stack word).
// A hook must never fault (docs/201 §7.2): these hooks run on every thread, also inside the loader, the heap and Frida's
// injected agent thread, where a first-chance AV (even one our __except catches) is dispatched through Frida's exceptor
// and hung the alloc-sites queue 2026-10-26. So only words inside a module's executable sections are read: a stack word
// equal to an image base (an HMODULE, e.g. the agent's own) used to read base-5, an unmapped page.
static struct { DWORD lo, hi, cLo, cHi; } g_asMods[64];  // image [lo, hi), executable sections [cLo, cHi)
static volatile LONG g_asModN;
// GPU driver modules (Vulkan ICDs under DXVK), by the DLL name in their export directory: regions they reserve carry
// kAsDrv in the record and stay out of the diag: mem miRes / miCom fields (docs/201 §14; the nvoglv32 128 MB reserves are
// accepted DXVK cost, not mimalloc). shortcut: names of the 32-bit NVIDIA / AMD / Intel ICDs only; add others when seen.
static const char* g_asDrvName[4] = {"nvoglv32.dll", "amdvlk32.dll", "igvk32.dll", NULL};  // [3]: host selftest
static struct { DWORD lo, hi; } g_asDrv[8];
static volatile LONG g_asDrvN;
static const DWORD kAsDrv = 0x100;  // AsRgn.kind flag
static bool AsInDrv(DWORD a) {
    for (LONG i = 0, n = g_asDrvN; i < n && i < 8; i++)
        if (a >= g_asDrv[i].lo && a < g_asDrv[i].hi) return true;
    return false;
}
static DWORD g_asNotImg[256];  // granule + 1 by (granule & 255)
static DWORD AsModOf(DWORD v, DWORD* cLo, DWORD* cHi) {  // module base, 0 = not in an image
    if (v >= g_asTextLo && v < g_asTextHi) return *cLo = g_asTextLo, *cHi = g_asTextHi, g_asExeBase;
    for (LONG i = 0, n = g_asModN; i < n && i < 64; i++)
        if (v >= g_asMods[i].lo && v < g_asMods[i].hi) return *cLo = g_asMods[i].cLo, *cHi = g_asMods[i].cHi, g_asMods[i].lo;
    const DWORD g = v >> 16;
    if (g_asNotImg[g & 255] == g + 1) return 0;
    MEMORY_BASIC_INFORMATION m;
    if (v < 0x10000 || !VirtualQuery((void*)(uintptr_t)v, &m, sizeof(m)) || m.Type != MEM_IMAGE) { g_asNotImg[g & 255] = g + 1; return 0; }
    const DWORD base = (DWORD)(uintptr_t)m.AllocationBase;
    DWORD size = 0, lo = 0xFFFFFFFF, hi = 0;
    bool drv = false;
    // headers: the image's first page, mapped with the view (no fault; the __try stays as a backstop)
    __try {
        const IMAGE_NT_HEADERS* nt = (const IMAGE_NT_HEADERS*)(uintptr_t)(base + ((const IMAGE_DOS_HEADER*)(uintptr_t)base)->e_lfanew);
        size = nt->OptionalHeader.SizeOfImage;
        const IMAGE_SECTION_HEADER* s = IMAGE_FIRST_SECTION(nt);
        for (int i = 0; i < nt->FileHeader.NumberOfSections && i < 96; i++, s++)
            if ((s->Characteristics & IMAGE_SCN_MEM_EXECUTE) && s->Misc.VirtualSize) {
                if (base + s->VirtualAddress < lo) lo = base + s->VirtualAddress;
                if (base + s->VirtualAddress + s->Misc.VirtualSize > hi) hi = base + s->VirtualAddress + s->Misc.VirtualSize;
            }
        const IMAGE_DATA_DIRECTORY& ex = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_EXPORT];  // inside the image: mapped
        if (ex.VirtualAddress && ex.VirtualAddress + sizeof(IMAGE_EXPORT_DIRECTORY) <= size) {
            const DWORD nm = ((const IMAGE_EXPORT_DIRECTORY*)(uintptr_t)(base + ex.VirtualAddress))->Name;
            for (int i = 0; i < 4 && nm && nm + 16 <= size; i++)
                if (g_asDrvName[i] && !_stricmp((const char*)(uintptr_t)(base + nm), g_asDrvName[i])) drv = true;
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) { return 0; }
    if (hi <= lo) lo = hi = 0;  // no code: no frame in this module
    if (drv) {
        const LONG d = InterlockedIncrement(&g_asDrvN) - 1;
        if (d < 8) g_asDrv[d].hi = base + size, MemoryBarrier(), g_asDrv[d].lo = base;
    }
    const LONG at = InterlockedIncrement(&g_asModN) - 1;
    if (at < 64) {  // lo last: a reader that sees lo sees the rest; shortcut: a torn row is only a missed cache hit
        g_asMods[at].cLo = lo, g_asMods[at].cHi = hi, g_asMods[at].hi = base + size;
        MemoryBarrier();
        g_asMods[at].lo = base;
    }
    *cLo = lo, *cHi = hi;
    return base;
}
// a is a committed page of the image at mod right now (a cached row outlives FreeLibrary: docs/67 §21)
static bool AsLiveImage(DWORD a, DWORD mod) {
    MEMORY_BASIC_INFORMATION m;
    return VirtualQuery((void*)(uintptr_t)a, &m, sizeof(m)) && m.State == MEM_COMMIT && m.Type == MEM_IMAGE &&
           (DWORD)(uintptr_t)m.AllocationBase == mod && !(m.Protect & (PAGE_NOACCESS | PAGE_GUARD));
}
// a stale row (module unloaded, the range free or another image there) never matches again; the next lookup adds a fresh row
static void AsDropMod(DWORD mod) {
    for (LONG i = 0, n = g_asModN; i < n && i < 64; i++)
        if (g_asMods[i].lo == mod) g_asMods[i].hi = 0;
}
// the 6 bytes before v lie in the module's executable sections and that module is still mapped there. 0.36.85 trusted the
// cache: 16 first-chance AVs per load reading base-5 of a DLL loaded where an unloaded one had been (docs/67 §21)
static bool AsCodeRetSafe(DWORD v, DWORD mod, DWORD cLo, DWORD cHi) {
    if (v < cLo + 8 || v >= cHi) return false;
    if (!AsLiveImage(v - 6, mod) || ((v - 6) >> 12 != (v - 1) >> 12 && !AsLiveImage(v - 1, mod))) return AsDropMod(mod), false;
    __try { return AsCallBefore((const u8*)(uintptr_t)v); } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}
static void AsAnyFrames(const DWORD* sp, DWORD* f0, DWORD* f1) {
    const DWORD* top = (const DWORD*)(uintptr_t)__readfsdword(4);
    DWORD a = 0, am = 0, b = 0;
    for (int i = 0; i < 160 && sp + i < top && !b; i++) {
        const DWORD v = sp[i];
        if (v < 0x10000 || v == a || (v >= g_asSelfLo && v < g_asSelfHi)) continue;  // our heap hook between game frames
        DWORD cLo = 0, cHi = 0;
        const DWORD mod = AsModOf(v, &cLo, &cHi);
        if (!mod || AsSysMod(mod) || !(mod == g_asExeBase ? AsExeRet(v) : AsCodeRetSafe(v, mod, cLo, cHi))) continue;
        if (!a) { a = v, am = mod; continue; }
        if (am == g_asExeBase ? (mod != am || !AsInWrap(a) || !AsInWrap(v)) : mod != am) b = v;
    }
    *f0 = a, *f1 = b;
}

// --- sampled heap blocks ----------------------------------------------------------------------------------------------
static DWORD AsChunk(DWORD p) { return *(const DWORD*)(uintptr_t)(p - 0xC) & 0x7FFFFFF8; }  // EA chunk size word (alloc.cpp)
static bool AsWant(DWORD p) { return AsSampled(p) || AsChunk(p) >= kAsBig; }
static int AsBlkHome(DWORD p) { return (int)((p * 0x85EBCA6Bu) & (kAsPerStripe - 1)); }

static void AsBlockAdd(DWORD p, DWORD ret, const DWORD* sp) {
    if (!AsEnter()) return;
    InterlockedIncrement64(&g_asSampled);
    DWORD f0, f1;
    AsHeapFrames(ret, sp, &f0, &f1);
    const int site = AsSiteOf(f0, f1, AS_HEAP);
    const DWORD cs = AsChunk(p);
    const bool big = cs >= kAsBig;
    const DWORD w = big ? cs : cs * g_asN;  // N <= 4096 and cs < 64 KB: no wrap
    const WORD cn = (WORD)(big ? 1 : g_asN);
    const int st = (int)((p * 0x85EBCA6Bu) >> 24);
    AsBlk* t = g_asBlk + st * kAsPerStripe;
    AsBlk old = {};
    bool ok = false;
    if (AsLock(&g_asStripeLock[st])) {
        for (int i = 0, j = AsBlkHome(p); i < kAsPerStripe; i++, j = (j + 1) & (kAsPerStripe - 1))
            if (!t[j].p || t[j].p == p) {  // p already there = a free we never saw (debug layer, a busy drop): replaced
                old = t[j];
                t[j].p = p, t[j].w = w, t[j].site = (WORD)site, t[j].n = cn;
                ok = true;
                break;
            }
        AsUnlock(&g_asStripeLock[st]);
    }
    if (old.p) AsSiteSub(old.site, old.w, old.n);
    else if (ok) InterlockedIncrement(&g_asBlocks);
    if (ok) AsSiteAdd(site, w, cn);
    else InterlockedIncrement(&g_asDrops);
    AsLeave();
}
static void AsBlockRemove(DWORD p) {
    const int st = (int)((p * 0x85EBCA6Bu) >> 24);
    AsBlk* t = g_asBlk + st * kAsPerStripe;
    if (!AsLock(&g_asStripeLock[st])) return;
    int j = AsBlkHome(p);
    for (int i = 0; i < kAsPerStripe && t[j].p && t[j].p != p; i++) j = (j + 1) & (kAsPerStripe - 1);
    if (t[j].p != p) { AsUnlock(&g_asStripeLock[st]); InterlockedIncrement(&g_asNoFree); return; }
    const AsBlk b = t[j];
    for (int k = (j + 1) & (kAsPerStripe - 1); t[k].p; k = (k + 1) & (kAsPerStripe - 1)) {  // backward-shift delete
        const int home = AsBlkHome(t[k].p);
        if (((k - home) & (kAsPerStripe - 1)) >= ((k - j) & (kAsPerStripe - 1))) t[j] = t[k], j = k;
    }
    t[j].p = 0;
    AsUnlock(&g_asStripeLock[st]);
    InterlockedDecrement(&g_asBlocks);
    AsSiteSub(b.site, b.w, b.n);
}

// --- reserved regions and views ---------------------------------------------------------------------------------------
static int AsRgnHome(DWORD base) { return (int)(((base >> 12) * 2654435761u) & (kAsRgns - 1)); }
static void AsRgnAdd(LONG kind, DWORD base, DWORD size, const DWORD* sp) {
    if (!base || !size || !AsEnter()) return;
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    DWORD f0, f1;
    AsAnyFrames(sp, &f0, &f1);
    const int site = AsSiteOf(f0, f1, kind);
    AsRgn old = {};
    bool ok = false;
    if (AsLock(&g_asRgnLock)) {
        for (int i = 0, j = AsRgnHome(base); i < kAsRgns; i++, j = (j + 1) & (kAsRgns - 1))
            if (!g_asRgn[j].base || g_asRgn[j].base == base) {
                old = g_asRgn[j];
                g_asRgn[j] = {base, size, (DWORD)site, (DWORD)kind | (AsInDrv(f0) ? kAsDrv : 0)};
                ok = true;
                break;
            }
        AsUnlock(&g_asRgnLock);
    }
    if (old.base) AsSiteSub((int)old.site, old.size, 1);
    else if (ok) InterlockedIncrement(&g_asRgnN);
    if (ok) AsSiteAdd(site, size, 1);
    else InterlockedIncrement(&g_asDrops);
    const LONG us = UsSince(t0);
    InterlockedIncrement64(&g_asVCalls);
    InterlockedAdd64(&g_asVUs, us);
    for (LONG m = g_asVMaxUs; us > m && InterlockedCompareExchange(&g_asVMaxUs, us, m) != m; m = g_asVMaxUs) {}
    AsLeave();
}
static void AsRgnRemove(DWORD base) {
    if (!base || !AsLock(&g_asRgnLock)) return;
    int j = AsRgnHome(base);
    for (int i = 0; i < kAsRgns && g_asRgn[j].base && g_asRgn[j].base != base; i++) j = (j + 1) & (kAsRgns - 1);
    if (g_asRgn[j].base != base) { AsUnlock(&g_asRgnLock); InterlockedIncrement(&g_asRgnMiss); return; }  // made before install
    const AsRgn r = g_asRgn[j];
    for (int k = (j + 1) & (kAsRgns - 1); g_asRgn[k].base; k = (k + 1) & (kAsRgns - 1)) {
        const int home = AsRgnHome(g_asRgn[k].base);
        if (((k - home) & (kAsRgns - 1)) >= ((k - j) & (kAsRgns - 1))) g_asRgn[j] = g_asRgn[k], j = k;
    }
    g_asRgn[j].base = 0;
    AsUnlock(&g_asRgnLock);
    InterlockedDecrement(&g_asRgnN);
    AsSiteSub((int)r.site, r.size, 1);
}

// --- hooks ------------------------------------------------------------------------------------------------------------
typedef void*(__fastcall* AsReallocFn)(void* heap, void* edx, void* p, DWORD n, DWORD flags);
static EaMallocFn g_asMalloc;
static EaMemalignFn g_asMemalign;
static AsReallocFn g_asRealloc;
static EaFreeFn g_asFree;
// the frame scan starts above the hook's own arguments: return address + 2 / 4 / 3 stack arguments
static void* __fastcall AsMallocHook(void* heap, void* edx, DWORD n, DWORD flags) {
    void* r = g_asMalloc(heap, edx, n, flags);
    if (r && AsWant((DWORD)(uintptr_t)r)) AsBlockAdd((DWORD)(uintptr_t)r, (DWORD)(uintptr_t)_ReturnAddress(), (const DWORD*)_AddressOfReturnAddress() + 3);
    return r;
}
static void* __fastcall AsMemalignHook(void* heap, void* edx, DWORD n, DWORD align, DWORD offset, DWORD flags) {
    void* r = g_asMemalign(heap, edx, n, align, offset, flags);
    if (r && AsWant((DWORD)(uintptr_t)r)) AsBlockAdd((DWORD)(uintptr_t)r, (DWORD)(uintptr_t)_ReturnAddress(), (const DWORD*)_AddressOfReturnAddress() + 5);
    return r;
}
// a block leaves the table BEFORE the game frees it: after the free another thread may get the same address
static void* __fastcall AsReallocHook(void* heap, void* edx, void* p, DWORD n, DWORD flags) {
    if (p && AsWant((DWORD)(uintptr_t)p)) AsBlockRemove((DWORD)(uintptr_t)p);  // shortcut: a failed realloc loses the old block's record
    void* r = g_asRealloc(heap, edx, p, n, flags);
    if (r && AsWant((DWORD)(uintptr_t)r)) AsBlockAdd((DWORD)(uintptr_t)r, (DWORD)(uintptr_t)_ReturnAddress(), (const DWORD*)_AddressOfReturnAddress() + 4);
    return r;
}
static void __fastcall AsFreeHook(void* heap, void* edx, void* p) {
    if (p && AsWant((DWORD)(uintptr_t)p)) AsBlockRemove((DWORD)(uintptr_t)p);
    g_asFree(heap, edx, p);
}

typedef LONG(NTAPI* AsNtAllocFn)(HANDLE, PVOID*, ULONG_PTR, PSIZE_T, ULONG, ULONG);
typedef LONG(NTAPI* AsNtFreeFn)(HANDLE, PVOID*, PSIZE_T, ULONG);
typedef LONG(NTAPI* AsNtMapFn)(HANDLE, HANDLE, PVOID*, ULONG_PTR, SIZE_T, PLARGE_INTEGER, PSIZE_T, DWORD, ULONG, ULONG);
typedef LONG(NTAPI* AsNtUnmapFn)(HANDLE, PVOID);
typedef LONG(NTAPI* AsNtUnmapExFn)(HANDLE, PVOID, ULONG);
// Windows 10 1803+: VirtualAlloc2 / MapViewOfFile3 (process, *base, *size, type, protect, extended parameters, count)
typedef LONG(NTAPI* AsNtAllocExFn)(HANDLE, PVOID*, PSIZE_T, ULONG, ULONG, PVOID, ULONG);
typedef LONG(NTAPI* AsNtMapExFn)(HANDLE, HANDLE, PVOID*, PLARGE_INTEGER, PSIZE_T, ULONG, ULONG, PVOID, ULONG);
static AsNtAllocExFn g_asNtAllocEx;
static AsNtMapExFn g_asNtMapEx;
static AsNtAllocFn g_asNtAlloc;
static AsNtFreeFn g_asNtFree;
static AsNtMapFn g_asNtMap;
static AsNtUnmapFn g_asNtUnmap;
static AsNtUnmapExFn g_asNtUnmapEx;
static bool AsSelf(HANDLE h) { return h == (HANDLE)(LONG_PTR)-1 || GetProcessId(h) == GetCurrentProcessId(); }

// a new region: MEM_RESERVE, or MEM_COMMIT alone at a NULL base (the kernel reserves it implicitly; the old hook missed
// these, a candidate for the untracked 16 MB blocks of docs/201 §13). *base is read before the call only from this
// thread's stack or a committed readable page: a hook never faults (docs/201 §11).
static bool AsNewRegion(PVOID* base, ULONG type) {
    if (type & MEM_RESERVE) return true;
    if (!(type & MEM_COMMIT) || !base) return false;
    const DWORD p = (DWORD)(uintptr_t)base;
    if (!(p >= __readfsdword(8) && p + 4 <= __readfsdword(4))) {
        MEMORY_BASIC_INFORMATION m;
        if (!VirtualQuery(base, &m, sizeof(m)) || m.State != MEM_COMMIT || (m.Protect & (PAGE_GUARD | PAGE_NOACCESS)) ||
            !(m.Protect & (PAGE_READONLY | PAGE_READWRITE | PAGE_WRITECOPY | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY)) ||
            (DWORD)(uintptr_t)m.BaseAddress + m.RegionSize < p + 4)
            return false;
    }
    return *base == NULL;
}
static LONG NTAPI AsNtAllocHook(HANDLE proc, PVOID* base, ULONG_PTR zb, PSIZE_T size, ULONG type, ULONG prot) {
    const bool rsv = AsNewRegion(base, type);
    const LONG st = g_asNtAlloc(proc, base, zb, size, type, prot);
    if (st >= 0 && rsv && AsSelf(proc))
        AsRgnAdd(AS_VIRT, (DWORD)(uintptr_t)*base, (DWORD)*size, (const DWORD*)_AddressOfReturnAddress());
    return st;
}
// shortcut: placeholders (MEM_RESERVE_PLACEHOLDER, split / replaced by later calls) are recorded as one reserve at their
// base; a view replacing one at the same base takes its record over; split pieces stay untracked (no 32-bit user known)
static LONG NTAPI AsNtAllocExHook(HANDLE proc, PVOID* base, PSIZE_T size, ULONG type, ULONG prot, PVOID ext, ULONG extN) {
    const bool rsv = AsNewRegion(base, type);
    const LONG st = g_asNtAllocEx(proc, base, size, type, prot, ext, extN);
    if (st >= 0 && rsv && AsSelf(proc))
        AsRgnAdd(AS_VIRT, (DWORD)(uintptr_t)*base, (DWORD)*size, (const DWORD*)_AddressOfReturnAddress());
    return st;
}
static LONG NTAPI AsNtFreeHook(HANDLE proc, PVOID* base, PSIZE_T size, ULONG type) {
    if ((type & MEM_RELEASE) && base && AsSelf(proc)) AsRgnRemove((DWORD)(uintptr_t)*base);
    return g_asNtFree(proc, base, size, type);
}
static LONG NTAPI AsNtMapHook(HANDLE sec, HANDLE proc, PVOID* base, ULONG_PTR zb, SIZE_T commit, PLARGE_INTEGER off, PSIZE_T view,
                              DWORD inherit, ULONG type, ULONG prot) {
    const LONG st = g_asNtMap(sec, proc, base, zb, commit, off, view, inherit, type, prot);
    if (st >= 0 && AsSelf(proc)) {
        MEMORY_BASIC_INFORMATION m;  // images (the loader maps DLLs here) are not the map field
        if (VirtualQuery(*base, &m, sizeof(m)) && m.Type == MEM_MAPPED)
            AsRgnAdd(AS_MAP, (DWORD)(uintptr_t)*base, (DWORD)*view, (const DWORD*)_AddressOfReturnAddress());
    }
    return st;
}
static LONG NTAPI AsNtMapExHook(HANDLE sec, HANDLE proc, PVOID* base, PLARGE_INTEGER off, PSIZE_T view, ULONG type, ULONG prot,
                                PVOID ext, ULONG extN) {
    const LONG st = g_asNtMapEx(sec, proc, base, off, view, type, prot, ext, extN);
    if (st >= 0 && AsSelf(proc)) {
        MEMORY_BASIC_INFORMATION m;
        if (VirtualQuery(*base, &m, sizeof(m)) && m.Type == MEM_MAPPED)
            AsRgnAdd(AS_MAP, (DWORD)(uintptr_t)*base, (DWORD)*view, (const DWORD*)_AddressOfReturnAddress());
    }
    return st;
}
static LONG NTAPI AsNtUnmapHook(HANDLE proc, PVOID base) {
    if (AsSelf(proc)) AsRgnRemove((DWORD)(uintptr_t)base);
    return g_asNtUnmap(proc, base);
}
static LONG NTAPI AsNtUnmapExHook(HANDLE proc, PVOID base, ULONG flags) {
    if (AsSelf(proc)) AsRgnRemove((DWORD)(uintptr_t)base);
    return g_asNtUnmapEx(proc, base, flags);
}

// --- install ----------------------------------------------------------------------------------------------------------
static u8* g_asTramp;
static int g_asTrampUsed;
struct AsPatch { DWORD at; u8 old[5]; };
static AsPatch g_asPatch[16];
static int g_asPatchN;

// jmp at `at` to `hook`; returns code that runs what was there: the verified prologue (pro, len), a mov eax,imm32 stub
// (pro NULL: an ntdll system call stub), or an earlier jmp (chained: [Alloc], Frida, another tool). `slot` (what the hook
// calls through) is stored before the jmp goes live: a DXVK thread entering NtAllocateVirtualMemory's hook in that window
// called address 0 on EA 1.69 (docs/232 Run 8e).
static void* AsDetourCore(const char* name, DWORD at, const u8* pro, int len, void* hook, void* volatile* slot) {
    u8* t = (u8*)(uintptr_t)at;
    // 0.36.230: AsInit made the trampoline page only for AllocSites; [UafCheck] alone (AllocSites=0) copied the prologue to
    // NULL (float5uaf-1109b, AV writing 0 in memcpy at ts3native+0xC5D39 from ApplyUafCheck, float5-split a-1 / b-1)
    if (!g_asTramp && !(g_asTramp = (u8*)VirtualAlloc(NULL, 4096, MEM_RESERVE | MEM_COMMIT, PAGE_EXECUTE_READWRITE))) {
        Log("SKIP alloc sites %s: no trampoline page (err %lu)", name, GetLastError());
        return NULL;
    }
    if (!Readable(t, pro ? len : 5) || g_asTrampUsed + 32 > 4096 || g_asPatchN >= 16) { Log("SKIP alloc sites %s: unreadable", name); return NULL; }
    u8* tr = g_asTramp + g_asTrampUsed;
    const char* how;
    if (t[0] == 0xE9) {
        const u8* dst = t + 5 + *(const LONG*)(t + 1);
        tr[0] = 0xE9, *(LONG*)(tr + 1) = (LONG)(dst - (tr + 5));
        how = "chained";
    } else if (pro ? !memcmp(t, pro, len) : t[0] == 0xB8) {
        if (!pro) len = 5;
        memcpy(tr, t, len);
        tr[len] = 0xE9, *(LONG*)(tr + len + 1) = (LONG)((t + len) - (tr + len + 5));
        how = "detour";
    } else {
        char hex[40];
        Hex(t, 10, hex, sizeof(hex));
        Log("SKIP alloc sites %s at 0x%08lX: bytes differ (%s)", name, at, hex);
        return NULL;
    }
    AsPatch& p = g_asPatch[g_asPatchN];
    p.at = at;
    memcpy(p.old, t, 5);
    u8 jmp[5] = {0xE9};
    *(LONG*)(jmp + 1) = (LONG)((u8*)hook - (t + 5));
    FlushInstructionCache(GetCurrentProcess(), tr, 32);
    void* const prev = *slot;
    *slot = tr;
    MemoryBarrier();
    if (!Write5(t, jmp)) { *slot = prev; Log("FAIL alloc sites %s: VirtualProtect (err %lu)", name, GetLastError()); return NULL; }
    g_asPatchN++;
    g_asTrampUsed += 32;
    Log("OK   alloc sites %s %s at 0x%08lX", name, how, at);
    return tr;
}
template <class F> static void* AsDetour(const char* name, DWORD at, const u8* pro, int len, void* hook, F* slot) {
    static_assert(sizeof(F) == sizeof(void*), "slot holds a code pointer");
    return AsDetourCore(name, at, pro, len, hook, (void* volatile*)slot);
}
static void AsUnhookAll() {  // original bytes back, newest first (host selftest; a partial install with no complete group)
    while (g_asPatchN > 0) {
        const AsPatch& p = g_asPatch[--g_asPatchN];
        Write5((u8*)(uintptr_t)p.at, p.old);
    }
    g_asTrampUsed = 0;
}

static DWORD g_asMemBase;  // our own tables (made before the hooks: shown as an untracked row, owner ts3native-allocsites)
static bool AsInit() {
    if (g_asSite) return true;
    const SIZE_T bytes = sizeof(AsSite) * kAsSites + sizeof(AsBlk) * kAsStripes * kAsPerStripe + sizeof(LONG) * kAsStripes +
                         sizeof(AsRgn) * kAsRgns * 2 + (sizeof(LONG64) + sizeof(LONG)) * kAsTags * kAsSites;
    u8* m = (u8*)VirtualAlloc(NULL, bytes, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
    g_asMemBase = (DWORD)(uintptr_t)m;
    if (!g_asTramp) g_asTramp = (u8*)VirtualAlloc(NULL, 4096, MEM_RESERVE | MEM_COMMIT, PAGE_EXECUTE_READWRITE);
    if (!m || !g_asTramp) return false;
    g_asSite = (AsSite*)m, m += sizeof(AsSite) * kAsSites;
    g_asBlk = (AsBlk*)m, m += sizeof(AsBlk) * kAsStripes * kAsPerStripe;
    g_asStripeLock = (volatile LONG*)m, m += sizeof(LONG) * kAsStripes;
    g_asRgn = (AsRgn*)m, m += sizeof(AsRgn) * kAsRgns * 2;
    g_asTagLive = (LONG64*)m, m += sizeof(LONG64) * kAsTags * kAsSites;
    g_asTagCnt = (LONG*)m;
    g_asSite[0].used = 2;  // overflow row
    const wchar_t* sys[3] = {L"ntdll.dll", L"kernelbase.dll", L"kernel32.dll"};
    for (int i = 0; i < 3; i++) {
        const u8* b = (const u8*)GetModuleHandleW(sys[i]);
        if (!b) continue;
        const IMAGE_NT_HEADERS* nt = (const IMAGE_NT_HEADERS*)(b + ((const IMAGE_DOS_HEADER*)b)->e_lfanew);
        g_asSys[i][0] = (DWORD)(uintptr_t)b, g_asSys[i][1] = (DWORD)(uintptr_t)b + nt->OptionalHeader.SizeOfImage;
    }
    return true;
}
static AsRgn* AsRgnCopy() { return g_asRgn + kAsRgns; }
// userdiag.cpp's walk: a 64 / 128 MB private allocation reserved by a GPU driver is not a mimalloc arena (false while off)
static bool AsIsDriverRegion(DWORD base) {
    if (!g_asOn || !g_asDrvN || !AsLock(&g_asRgnLock)) return false;
    int j = AsRgnHome(base);
    for (int i = 0; i < kAsRgns && g_asRgn[j].base && g_asRgn[j].base != base; i++) j = (j + 1) & (kAsRgns - 1);
    const bool d = g_asRgn[j].base == base && (g_asRgn[j].kind & kAsDrv);
    AsUnlock(&g_asRgnLock);
    return d;
}

// all four EA entries or none (a Free we do not see would leave live bytes behind)
static int AsInstallHeap() {
    for (DWORD a : g_asHeapAt) {
        const u8* t = (const u8*)(uintptr_t)a;
        if (!Readable(t, 10) || (t[0] != 0xE9 && memcmp(t, kEaLockedPrologue, sizeof(kEaLockedPrologue)))) {
            Log("SKIP alloc sites EA heap: entry 0x%08lX not as expected, no heap hooks", a);
            return 0;
        }
    }
    const int L = (int)sizeof(kEaLockedPrologue);
    g_asFree = (EaFreeFn)AsDetour("EA heap Free", g_asHeapAt[3], kEaLockedPrologue, L, (void*)AsFreeHook, &g_asFree);
    g_asRealloc = g_asFree ? (AsReallocFn)AsDetour("EA heap Realloc", g_asHeapAt[2], kEaLockedPrologue, L, (void*)AsReallocHook, &g_asRealloc) : NULL;
    g_asMalloc = g_asRealloc ? (EaMallocFn)AsDetour("EA heap Malloc", g_asHeapAt[0], kEaLockedPrologue, L, (void*)AsMallocHook, &g_asMalloc) : NULL;
    g_asMemalign = g_asMalloc ? (EaMemalignFn)AsDetour("EA heap Memalign", g_asHeapAt[1], kEaLockedPrologue, L, (void*)AsMemalignHook, &g_asMemalign) : NULL;
    return g_asMemalign ? 4 : 0;  // shortcut: a later failure leaves the earlier hooks in (frees only: harmless lookups)
}
// release before reserve, unmap before map: every reserve / view we record has its release hooked already
static int AsInstallVirtual() {
    HMODULE nt = GetModuleHandleW(L"ntdll.dll");
    void* fa = (void*)GetProcAddress(nt, "NtAllocateVirtualMemory");
    void* ff = (void*)GetProcAddress(nt, "NtFreeVirtualMemory");
    void* fm = (void*)GetProcAddress(nt, "NtMapViewOfSection");
    void* fu = (void*)GetProcAddress(nt, "NtUnmapViewOfSection");
    void* fx = (void*)GetProcAddress(nt, "NtUnmapViewOfSectionEx");  // Windows 8+: UnmapViewOfFileEx
    void* fax = (void*)GetProcAddress(nt, "NtAllocateVirtualMemoryEx");  // Windows 10 1803+: VirtualAlloc2
    void* fmx = (void*)GetProcAddress(nt, "NtMapViewOfSectionEx");       // Windows 10 1803+: MapViewOfFile3
    int n = 0;
    if (fa && ff && (g_asNtFree = (AsNtFreeFn)AsDetour("NtFreeVirtualMemory", (DWORD)(uintptr_t)ff, NULL, 5, (void*)AsNtFreeHook, &g_asNtFree)) &&
        (g_asNtAlloc = (AsNtAllocFn)AsDetour("NtAllocateVirtualMemory", (DWORD)(uintptr_t)fa, NULL, 5, (void*)AsNtAllocHook, &g_asNtAlloc))) {
        n += 2;
        if (fax && (g_asNtAllocEx = (AsNtAllocExFn)AsDetour("NtAllocateVirtualMemoryEx", (DWORD)(uintptr_t)fax, NULL, 5, (void*)AsNtAllocExHook, &g_asNtAllocEx))) n++;
    }
    if (fm && fu && (g_asNtUnmap = (AsNtUnmapFn)AsDetour("NtUnmapViewOfSection", (DWORD)(uintptr_t)fu, NULL, 5, (void*)AsNtUnmapHook, &g_asNtUnmap)) &&
        (!fx || (g_asNtUnmapEx = (AsNtUnmapExFn)AsDetour("NtUnmapViewOfSectionEx", (DWORD)(uintptr_t)fx, NULL, 5, (void*)AsNtUnmapExHook, &g_asNtUnmapEx))) &&
        (g_asNtMap = (AsNtMapFn)AsDetour("NtMapViewOfSection", (DWORD)(uintptr_t)fm, NULL, 5, (void*)AsNtMapHook, &g_asNtMap))) {
        n += fx ? 3 : 2;
        if (fmx && (g_asNtMapEx = (AsNtMapExFn)AsDetour("NtMapViewOfSectionEx", (DWORD)(uintptr_t)fmx, NULL, 5, (void*)AsNtMapExHook, &g_asNtMapEx))) n++;
    }
    return n;
}

// --- output (every diag: mem sample: each mem_mark and every 5 min of play) --------------------------------------------
static SRWLOCK g_asOutLock = SRWLOCK_INIT;
static DWORD g_asWinLo, g_asWinHi;  // the largest free block at the previous output

static void AsWhere(DWORD a, char* out, size_t cap) {
    if (!a) { strcpy_s(out, cap, "-"); return; }
    MEMORY_BASIC_INFORMATION m;
    wchar_t w[MAX_PATH];
    if (!(a >= g_asTextLo && a < g_asTextHi) && VirtualQuery((void*)(uintptr_t)a, &m, sizeof(m)) && m.Type == MEM_IMAGE &&
        GetModuleFileNameW((HMODULE)m.AllocationBase, w, MAX_PATH)) {
        const wchar_t* s = wcsrchr(w, L'\\');  // file name only (players' logs: docs/188 privacy)
        sprintf_s(out, cap, "%ls+0x%lX", s ? s + 1 : w, a - (DWORD)(uintptr_t)m.AllocationBase);
    } else sprintf_s(out, cap, "0x%08lX", a);  // the exe: a VA (tools/memory-report.ts names it from the symbol table)
}
static void AsSiteText(int i, char* out, size_t cap) {
    char a[96], b[96];
    AsWhere(g_asSite[i].f0, a, sizeof(a)), AsWhere(g_asSite[i].f1, b, sizeof(b));
    sprintf_s(out, cap, "k=%s at=%s from=%s", i ? kAsKind[g_asSite[i].kind] : "overflow", a, b);
}
static int AsRgnFind(const AsRgn* rc, DWORD base) {  // index of the record at this base in the copy, -1 = untracked
    for (int j = AsRgnHome(base), i = 0; i < kAsRgns && rc[j].base; i++, j = (j + 1) & (kAsRgns - 1))
        if (rc[j].base == base) return j;
    return -1;
}
// the allocation next to a free block: a tracked region's site, else the region type (and module for an image)
static void AsNeighbour(DWORD addr, const AsRgn* rc, char* out, size_t cap) {
    MEMORY_BASIC_INFORMATION m;
    if (!VirtualQuery((void*)(uintptr_t)addr, &m, sizeof(m)) || m.State == MEM_FREE) { strcpy_s(out, cap, "-"); return; }
    const DWORD ab = (DWORD)(uintptr_t)m.AllocationBase;
    const int j = AsRgnFind(rc, ab);
    if (j >= 0) {
        char s[220];
        AsSiteText((int)rc[j].site, s, sizeof(s));
        sprintf_s(out, cap, "{%s}", s);
        return;
    }
    if (m.Type == MEM_IMAGE) { char s[120]; AsWhere(ab + 0x1000, s, sizeof(s)); sprintf_s(out, cap, "{image %s}", s); return; }
    sprintf_s(out, cap, "{untracked %s 0x%08lX}", m.Type == MEM_MAPPED ? "map" : "private", ab);
}

// untracked allocations (docs/201 §14): whole allocations (every region with one AllocationBase) >= 1 MB that no record
// owns, this output's and the previous one's in address order (first = the sample that first showed base + size)
struct AsUnt { DWORD base, size, com, prot, aprot, type; LONG first; bool guard; };
static const int kAsUnts = 1024;
static AsUnt g_asUnt[2][kAsUnts];
static int g_asUntN[2], g_asUntCur;
static bool g_asUntHad;  // an earlier output exists (on the first one nothing is "new")
static void AsOwner(const AsUnt& u, const HANDLE* heaps, DWORD nh, char* out, size_t cap) {
    if (u.type != MEM_PRIVATE) {  // image or view: the file (name only, docs/188 privacy); a pagefile-backed view has none
        wchar_t w[MAX_PATH];
        if (K32GetMappedFileNameW(GetCurrentProcess(), (void*)(uintptr_t)u.base, w, MAX_PATH)) {
            const wchar_t* f = wcsrchr(w, L'\\');
            sprintf_s(out, cap, "file:%.60ls", f ? f + 1 : w);
        } else strcpy_s(out, cap, u.type == MEM_MAPPED ? "pagefile-section" : "?");
        return;
    }
    if (u.base == g_asMemBase) { strcpy_s(out, cap, "ts3native-allocsites"); return; }
    for (DWORD i = 0; i < nh; i++)
        if ((DWORD)(uintptr_t)heaps[i] == u.base) { strcpy_s(out, cap, "ntheap"); return; }  // first segment only
    strcpy_s(out, cap, u.guard ? "stack?" : "?");  // a guard page: a thread stack [inferred]
}

static void AsAfterSample(LONG n, const char* tag, const char* ev) {
    if (!g_asOn || !AsEnter()) return;  // the tool's own allocations here are not recorded
    AcquireSRWLockExclusive(&g_asOutLock);
    char head[96], s[240];
    sprintf_s(head, "allocsites: #%ld %s%s%s", n, tag, ev && *ev ? " ev=" : "", ev && *ev ? ev : "");
    const double MB = 1.0 / (1 << 20);
    static int idx[kAsSites];
    static bool taken[kAsSites];
    int ni = 0;
    double kMB[AS_KINDS] = {};
    LONG kN[AS_KINDS] = {};
    for (int i = 0; i < kAsSites; i++)
        if (g_asSite[i].used == 2 && (g_asSite[i].allocN || g_asSite[i].live)) {
            idx[ni++] = i;
            if (i) kMB[g_asSite[i].kind] += g_asSite[i].live * MB, kN[g_asSite[i].kind] += g_asSite[i].liveN;
        }
    Log("%s sum heapMB=%.1f heapN=%ld virtMB=%.1f virtN=%ld mapMB=%.1f mapN=%ld sites=%d blocks=%ld regions=%ld sampled=%lld "
        "drops=%ld busy=%ld nofree=%ld rgnMiss=%ld siteFull=%ld vCalls=%lld vUs=%lld vMaxUs=%ld sample=%lu",
        head, kMB[AS_HEAP], kN[AS_HEAP], kMB[AS_VIRT], kN[AS_VIRT], kMB[AS_MAP], kN[AS_MAP], ni, g_asBlocks, g_asRgnN, g_asSampled,
        g_asDrops, g_asBusy, g_asNoFree, g_asRgnMiss, g_asFull, g_asVCalls, g_asVUs, g_asVMaxUs, g_asN);
    // top by live bytes, by growth since the previous output, and (with an event tag) by growth since that tag's previous
    // sample (selection: top <= 32 of <= 16384 sites)
    int tg = -1;
    if (ev && *ev) {
        for (int i = 0; i < kAsTags && tg < 0; i++) if (!strcmp(g_asTag[i], ev)) tg = i;
        for (int i = 0; i < kAsTags && tg < 0; i++) if (!g_asTag[i][0]) tg = i, strncpy_s(g_asTag[i], ev, _TRUNCATE);  // shortcut: tags after the 16th get no cyc rows
    }
    const LONG64* tl = tg >= 0 ? g_asTagLive + tg * kAsSites : NULL;
    const LONG* tc = tg >= 0 ? g_asTagCnt + tg * kAsSites : NULL;
    for (int pass = 0; pass < 3; pass++) {
        if (pass == 2 && (tg < 0 || !g_asTagN[tg])) break;
        memset(taken, 0, sizeof(taken));
        for (int r = 1; r <= g_asTop; r++) {
            int best = -1;
            LONG64 bv = pass ? 64 * 1024 : 0;  // growth rows from +64 KB
            for (int k = 0; k < ni; k++) {
                const int i = idx[k];
                const LONG64 v = pass == 0 ? g_asSite[i].live : g_asSite[i].live - (pass == 1 ? g_asSite[i].prevLive : tl[i]);
                if (!taken[i] && v > bv) bv = v, best = i;
            }
            if (best < 0) break;
            taken[best] = true;
            const AsSite& x = g_asSite[best];
            const LONG64 b0 = pass == 2 ? tl[best] : x.prevLive;
            const LONG n0 = pass == 2 ? tc[best] : x.prevN;
            char since[24] = "";
            if (pass == 2) sprintf_s(since, " since=#%ld", g_asTagN[tg]);
            AsSiteText(best, s, sizeof(s));
            Log("%s %s r=%d %s MB=%.2f n=%ld dMB=%+.2f dN=%+ld aMB=%.1f fMB=%.1f%s", head, pass == 0 ? "live" : pass == 1 ? "grow" : "cyc", r, s,
                x.live * MB, x.liveN, (x.live - b0) * MB, x.liveN - n0, x.allocB * MB, x.freeB * MB, since);
        }
    }
    for (int k = 0; k < ni; k++) {
        AsSite& x = g_asSite[idx[k]];
        x.prevLive = x.live, x.prevN = x.liveN;
        if (tg >= 0) g_asTagLive[tg * kAsSites + idx[k]] = x.live, g_asTagCnt[tg * kAsSites + idx[k]] = x.liveN;
    }
    if (tg >= 0) g_asTagN[tg] = n;

    // regions: a copy, then which sites hold blocks inside the previous output's largest free block (they split it), and
    // the allocations bounding the three largest free blocks now
    AsRgn* rc = AsRgnCopy();
    if (AsLock(&g_asRgnLock)) { memcpy(rc, g_asRgn, sizeof(AsRgn) * kAsRgns); AsUnlock(&g_asRgnLock); }
    if (g_asWinHi > g_asWinLo) {
        static LONG64 inB[kAsSites];
        static LONG inN[kAsSites];
        memset(inB, 0, sizeof(inB)), memset(inN, 0, sizeof(inN));
        for (int j = 0; j < kAsRgns; j++)
            if (rc[j].base && rc[j].base < g_asWinHi && rc[j].base + rc[j].size > g_asWinLo) inB[rc[j].site] += rc[j].size, inN[rc[j].site]++;  // overlap: a base below the window counts too (§13.1)
        for (int r = 1; r <= 5; r++) {
            int best = -1;
            for (int i = 0; i < kAsSites; i++) if (inB[i] && (best < 0 || inB[i] > inB[best])) best = i;
            if (best < 0) break;
            AsSiteText(best, s, sizeof(s));
            Log("%s inbig r=%d %s MB=%.2f n=%ld win=0x%08lX+%luMB", head, r, s, inB[best] * MB, inN[best], g_asWinLo, (g_asWinHi - g_asWinLo) >> 20);
            inB[best] = 0;
        }
    }
    // one walk: the three largest free blocks and the untracked allocations (regions grouped by AllocationBase)
    DWORD fb[3] = {}, fs[3] = {};
    AsUnt* cu = g_asUnt[g_asUntCur];
    int nu = 0, more = 0;
    DWORD ab = 0, aEnd = 0, aCom = 0, aProt = 0, aAProt = 0, aType = 0;
    bool aGuard = false;
    auto flush = [&]() {
        if (ab && aEnd - ab >= (1u << 20) && AsRgnFind(rc, ab) < 0) {
            if (nu < kAsUnts) cu[nu++] = {ab, aEnd - ab, aCom, aProt, aAProt, aType, n, aGuard};
            else more++;
        }
        ab = 0;
    };
    SYSTEM_INFO si;
    GetSystemInfo(&si);
    MEMORY_BASIC_INFORMATION m;
    for (uintptr_t a = (uintptr_t)si.lpMinimumApplicationAddress; a < (uintptr_t)si.lpMaximumApplicationAddress;) {
        if (!VirtualQuery((void*)a, &m, sizeof(m))) break;
        const DWORD rb = (DWORD)(uintptr_t)m.BaseAddress, rs = (DWORD)m.RegionSize;
        if (m.State == MEM_FREE) {
            flush();
            for (int r = 0; r < 3; r++)
                if (rs > fs[r]) {
                    for (int q = 2; q > r; q--) fb[q] = fb[q - 1], fs[q] = fs[q - 1];
                    fb[r] = rb, fs[r] = rs;
                    break;
                }
        } else {
            if ((DWORD)(uintptr_t)m.AllocationBase != ab) {
                flush();
                ab = (DWORD)(uintptr_t)m.AllocationBase, aCom = 0, aProt = 0, aAProt = m.AllocationProtect, aType = m.Type, aGuard = false;
            }
            aEnd = rb + rs;
            if (m.State == MEM_COMMIT) {
                aCom += rs;
                if (!aProt) aProt = m.Protect;
                if (m.Protect & PAGE_GUARD) aGuard = true;
            }
        }
        const uintptr_t next = (uintptr_t)m.BaseAddress + m.RegionSize;
        if (next <= a) break;
        a = next;
    }
    flush();
    for (int r = 0; r < 3 && fs[r]; r++) {
        char lo[240], hi[240];
        AsNeighbour(fb[r] - 1, rc, lo, sizeof(lo)), AsNeighbour(fb[r] + fs[r], rc, hi, sizeof(hi));
        Log("%s free r=%d base=0x%08lX MB=%.1f below=%s above=%s", head, r + 1, fb[r], fs[r] * MB, lo, hi);
    }
    // untracked: first-seen sample from the previous list (both in address order), then the summary and the rows that
    // matter: inside the previous largest free block or touching the current one, new since the previous output, and
    // every private / map allocation that is not a thread stack (images and stacks only when new or at the window)
    {
        const AsUnt* pv = g_asUnt[g_asUntCur ^ 1];
        const int np = g_asUntN[g_asUntCur ^ 1];
        static const char* const kTy[3] = {"private", "map", "image"};
        int cN[3] = {}, newN = 0, shown = 0;
        double cMB[3] = {};
        HANDLE heaps[64];
        DWORD nh = GetProcessHeaps(64, heaps);
        if (nh > 64) nh = 0;  // more heaps than fit: no ntheap names
        for (int i = 0; i < nu; i++) {
            AsUnt& u = cu[i];
            int lo = 0, hi = np - 1;
            while (lo <= hi) {
                const int mid = (lo + hi) >> 1;
                if (pv[mid].base < u.base) lo = mid + 1;
                else if (pv[mid].base > u.base) hi = mid - 1;
                else { if (pv[mid].size == u.size) u.first = pv[mid].first; break; }
            }
            const int t = u.type == MEM_MAPPED ? 1 : u.type == MEM_IMAGE ? 2 : 0;
            cN[t]++, cMB[t] += u.size * MB;
            if (g_asUntHad && u.first == n) newN++;
        }
        Log("%s usum private=%d/%.1fMB map=%d/%.1fMB image=%d/%.1fMB new=%d more=%d", head, cN[0], cMB[0], cN[1], cMB[1], cN[2], cMB[2], newN, more);
        for (int i = 0; i < nu; i++) {
            const AsUnt& u = cu[i];
            const DWORD end = u.base + u.size;
            const char* win = (g_asWinHi > g_asWinLo && u.base < g_asWinHi && end > g_asWinLo) ? "in"
                            : (fs[0] && (end == fb[0] || u.base == fb[0] + fs[0])) ? "edge" : "-";
            const bool isNew = g_asUntHad && u.first == n;
            if (!isNew && *win == '-' && (u.type == MEM_IMAGE || u.guard)) continue;
            if (++shown > 64) continue;  // shortcut: at most 64 rows per output; usum counts them all
            char own[96];
            AsOwner(u, heaps, nh, own, sizeof(own));
            Log("%s untr r=%d type=%s base=0x%08lX MB=%.2f comMB=%.2f prot=0x%lX aprot=0x%lX owner=%s first=#%ld new=%d win=%s", head, shown,
                kTy[u.type == MEM_MAPPED ? 1 : u.type == MEM_IMAGE ? 2 : 0], u.base, u.size * MB, u.com * MB, u.prot, u.aprot, own, u.first, isNew ? 1 : 0, win);
        }
        g_asUntN[g_asUntCur] = nu, g_asUntCur ^= 1, g_asUntHad = true;
    }
    g_asWinLo = fb[0], g_asWinHi = fb[0] + fs[0];
    ReleaseSRWLockExclusive(&g_asOutLock);
    AsLeave();
}

// [MemDiag] AllocSites=1: TEST-RUN TOOL (docs/201). Needs [MemDiag] Light or Enabled (its samples carry the lines).
static bool ApplyAllocSites(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"MemDiag", L"AllocSites", 0, ini)) return false;
    if (g_apexFound && g_apexStandDown) { Log("SKIP alloc sites: Apex Radiance loaded (the allocator is its area; [Compat] ApexStandDown=1)"); return false; }
    if (!g_mdOn) { Log("SKIP alloc sites: needs [MemDiag] Light=1 or Enabled=1 (its samples carry the output)"); return false; }
    const DWORD n = (DWORD)Clamp((int)GetPrivateProfileIntW(L"MemDiag", L"AllocSitesSample", 64, ini), 1, 4096);
    DWORD bits = 0;
    while ((2u << bits) <= n) bits++;  // round down to a power of two
    g_asN = 1u << bits, g_asShift = 32 - bits;  // bits 0 = every block (shift 32)
    g_asTop = Clamp((int)GetPrivateProfileIntW(L"MemDiag", L"AllocSitesTop", 12, ini), 4, 32);
    if (!AsInit()) { Log("FAIL alloc sites: tables (err %lu)", GetLastError()); return false; }
    HMODULE self = NULL;
    if (GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCWSTR)(void*)AsMallocHook, &self)) {
        const IMAGE_NT_HEADERS* nt = (const IMAGE_NT_HEADERS*)((const u8*)self + ((const IMAGE_DOS_HEADER*)self)->e_lfanew);
        g_asSelfLo = (DWORD)(uintptr_t)self, g_asSelfHi = g_asSelfLo + nt->OptionalHeader.SizeOfImage;
    }
    const int v = GetPrivateProfileIntW(L"MemDiag", L"AllocSitesVirtual", 1, ini) ? AsInstallVirtual() : 0;
    const int h = GetPrivateProfileIntW(L"MemDiag", L"AllocSitesHeap", 1, ini) ? AsInstallHeap() : 0;
    g_asOn = v || h;
    if (!g_asOn) AsUnhookAll();  // a half-installed group (e.g. the free hook without its alloc hook) only costs time
    Log("alloc sites %s (TEST-RUN TOOL, docs/201): EA heap hooks %d/4, ntdll hooks %d, heap blocks < 64 KB 1 in %lu by address, "
        "blocks >= 64 KB / reserves / views all, top %d; allocsites: lines at every diag: mem sample", g_asOn ? "on" : "off (no hook)",
        h, v, g_asN, g_asTop);
    return g_asOn;
}
