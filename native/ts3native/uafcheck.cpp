// [UafCheck] (docs/539 §17): delayed-free quarantine on EA's main heap. TEST-RUN TOOL, code and release ini Enabled=0.
// Float-5 triage cycle 4: a stray dword 0x40A00000 lands in pointer slots of unrelated heap objects (ModelBuilder record,
// outfit string, outfit result cell, physics group shape vector). Hypothesis: a writer through a stale pointer into memory
// that was freed and handed to a new owner. Included by ts3native.cpp after allocsites.cpp (AsDetour, AsExeRet, EaFreeFn)
// and watchpt.cpp (WpArm / WpDisarm / WpRefresh / WpInstall).
//   Free hook (EA heap Free 0x4E5920, thiscall (ptr), ret 4; chains an earlier jmp like allocsites.cpp): a block of the main
//   heap [0x11CB864] (every EA_AllocTagged 0x4E3F90 / EA_Free 0x4E3F70 block) with MinSize <= user bytes <= MaxSize, a plain
//   in-use chunk (alloc.cpp layout: size word at p-0xC, next chunk's bit 0) is not freed: its user bytes are filled with
//   0xFEEEFEEE and it goes into a FIFO ring with its size, free time, save / world-load numbers and 4 exe return addresses.
//   When the ring is over MaxBlocks or MaxMB the oldest blocks are verified (every dword still the pattern) and really freed.
//   Any changed dword: "uaf: write after free" (block, size, offset, value, 0x40A00000 flagged, free stack, ms since free,
//   and the uaftag.h tag: which of our paths was freeing on that thread, with our call site, or "none").
//   Really freed blocks (evicted, DR-released) and tagged frees that pass straight to EA go to a recently-freed ring (8192);
//   castrim.cpp's float-5 detectors call UafNearLog on the hit address and its object / record base: quarantine, that ring and
//   the P5c released ring (p5c.cpp, 4096) are searched and every match logged ("float5 near our free: tag ...").
//   Every held block is also verified at each save end, each world quit and each world load (userdiag.cpp calls UafVerifyAll).
//   DrArms > 0: after the first hit (a 0x40A00000 hit replaces an earlier other one) the next DrArms freed blocks of that chunk
//   size are held aside and a helper thread puts a DR write watch (watchpt.cpp WpArm, takes a [CasTrim] WriteTrap slot when none
//   is free) on the hit offset; any write there is a write after free, logged by WpVeh with its eip ("watchpoint DRn hit").
//   After DrHoldSec the watch is dropped, the block verified and freed.
// Memory: held blocks stay committed heap memory (MaxMB, default 48) plus the ring (44 bytes a block since 0.36.230, MaxBlocks default 262144 =
// 11 MB); the total is clamped to 64 MB of the 32-bit space. Behaviour change: frees are delayed, and a reader of freed memory now
// reads 0xFEEEFEEE (a crash with that value names a stale reader). A second free of a held block is absorbed (logged).
// shortcut: Realloc 0x4E6A70 frees through the core, other heaps (Audio, MemoryMan sub-heaps) and pools with their own free
// lists (CAS node free list 0x5EE260, physics pool allocators) are not quarantined; widen if the main heap stays clean.

static const DWORD kUafPat = 0xFEEEFEEE, kUafF5 = 0x40A00000;
struct UafBlk { DWORD p, chunk, tick, save, world, from[4], tag, site, oidLo, oidHi; };  // tag, site: uaftag.h at the free; oid: Object id (Object frees)
// Object blocks (docs/539 §18, 0.36.230) [verified disassembly TS3W 1.67]: ObjectService ([0x11DC350]) keeps its allocator at
// +0x1040 (0x938A00), set by the ctor 0x93A510 from its argument, which 0x93AA20 passes as 0: the default ICoreAllocator
// 0x11DC168 (0x931020, vt 0xFB8B38, [+4] = the main heap [0x11CB864]). Object_DeletingDtor 0x935DF0 frees through vt+0xC
// = 0x4E3EF0 (thiscall(alloc; ptr, size), ret 8: push ptr, call this Free 0x4E5920 on [alloc+4]). So Object frees already
// reach this hook: at the hook the return address is 0x4E3EFD and two dwords up sits 0x4E3EF0's own return 0x935E1B. They
// are tagged by that chain (no extra detour) and may use ObjectMaxSize (Object blocks with many components pass MaxSize).
static const DWORD kUafObjRet = 0x00935E1B;
static DWORD g_uafObjMax;          // [UafCheck] ObjectMaxSize, 0 = MaxSize
static volatile LONG g_uafObjHeld;
static bool UafObjFree(const DWORD* ra) { return ra[0] == 0x004E3EFD && ra[2] == kUafObjRet; }
static void* const* g_uafHeapAt = (void* const*)(uintptr_t)0x011CB864;  // the main heap pointer (selftest: its own)
static EaFreeFn g_uafFree;                                               // the continuation of the hooked Free
static UafBlk* g_uafRing;
struct UafGone { DWORD p, chunk, tick, freedTick, tag, site; };  // chunk 0: a tagged pass-through of unknown size (0x400 window)
static const LONG kUafGoneN = 8192;
static UafGone* g_uafGone;
static volatile LONG g_uafGoneN, g_uafNearN;
static DWORD g_uafCap, g_uafHead, g_uafN, g_uafBytes, g_uafMaxBytes;
static DWORD g_uafMin = 64, g_uafMax = 4096;
// docs/539 §21 TravelHold=1: from a travel save start to the next world load end the byte cap is TravelMaxMB (everything freed in
// that window stays held up to it; the ring's MaxBlocks still applies), then normal eviction drains back to MaxMB.
static bool g_uafTravel;
static DWORD g_uafTravelBytes, g_uafHoldTick;
static volatile LONG g_uafHold, g_uafHoldEvict, g_uafHoldIn;
static SRWLOCK g_uafLock = SRWLOCK_INIT;
static bool g_uafOn;
static volatile LONG g_uafLogN, g_uafDupN;
static const LONG kUafLogMax = 100;
// DR combo
struct UafArm { volatile LONG state; UafBlk b; DWORD off, at; int slot; };  // state 0 free, 1 filling, 2 to arm, 3 held (slot -1: no DR)
static UafArm g_uafArm[4];
static LONG g_uafDrArms;                // [UafCheck] DrArms (0-4)
static DWORD g_uafDrHoldMs = 600000;
static volatile LONG g_uafDrWant;       // blocks still to hold aside for the target
static volatile DWORD g_uafDrChunk, g_uafDrOff;
static volatile LONG g_uafDrF5;         // the target came from a 0x40A00000 hit
static HANDLE g_uafArmEv;

static LONG UafSave() { return g_udSaves; }
static LONG UafWorld() { return g_udWorlds; }

static void UafFrames(const DWORD* sp, DWORD* f) {  // first 4 exe return addresses above the hook, allocator wrappers skipped
    const DWORD* top = (const DWORD*)(uintptr_t)__readfsdword(4);
    int k = 0;
    for (int i = 0; i < 96 && sp + i < top && k < 4; i++) {
        const DWORD v = sp[i];
        if (v >= 0x004E3D00 && v < 0x004E9000) continue;
        if (AsExeRet(v) && (!k || f[k - 1] != v)) f[k++] = v;
    }
}
static const char* UafTagName(DWORD t) { return t < UT_N ? kUafTagName[t] : "?"; }
static DWORD UafSiteOff(DWORD site) { return site >= g_selfLo && site < g_selfHi ? site - g_selfLo : site; }  // ts3native+off
static void UafGoneAdd(DWORD p, DWORD chunk, DWORD tick, DWORD tag, DWORD site) {
    if (!g_uafGone) return;
    UafGone& g = g_uafGone[(InterlockedIncrement(&g_uafGoneN) - 1) & (kUafGoneN - 1)];
    g.p = p, g.chunk = chunk, g.tick = tick, g.freedTick = GetTickCount(), g.tag = tag, g.site = site;
}
static void UafFill(DWORD p, DWORD n) {
    for (DWORD* d = (DWORD*)(uintptr_t)p, *e = d + n / 4; d < e; d++) *d = kUafPat;
}
static bool UafWanted(DWORD p, DWORD* chunk, bool obj = false) {  // plain in-use main-heap chunk of a quarantined size (alloc.cpp EaFreeHook's test)
    const DWORD hdr = *(const DWORD*)(uintptr_t)(p - 0xC), ch = hdr & 0x7FFFFFF8;
    if ((hdr & 0x80000006) || (ch & 0xF) || ch < 0x20 || ch - 0x10 < g_uafMin || ch - 0x10 > (obj && g_uafObjMax > g_uafMax ? g_uafObjMax : g_uafMax)) return false;
    if (!(*(const DWORD*)(uintptr_t)(p - 0x10 + ch + 4) & 1)) return false;
    return *chunk = ch, true;
}
static bool UafTryLock() {  // never blocks for long: a thread suspended by the GC while holding it costs a pass-through, not a hang
    for (int i = 0; i < 256; i++) {
        if (TryAcquireSRWLockExclusive(&g_uafLock)) return true;
        if (i < 64) YieldProcessor(); else SwitchToThread();
    }
    return false;
}

// Changed dwords of a held block: logged, counted, refilled (a second write logs again). Returns the changed count.
static int UafVerify(const UafBlk& b, const char* when) {
    const DWORD* d = (const DWORD*)(uintptr_t)b.p;
    const DWORD n = (b.chunk - 0x10) / 4;
    int changed = 0, first = -1, f5 = -1;
    DWORD offs[4] = {}, vals[4] = {};
    for (DWORD i = 0; i < n; i++) {
        if (d[i] == kUafPat) continue;
        if (changed < 4) offs[changed] = i * 4, vals[changed] = d[i];
        if (first < 0) first = (int)i;
        if (d[i] == kUafF5 && f5 < 0) f5 = (int)i;
        changed++;
    }
    if (!changed) return 0;
    InterlockedIncrement(&ts3native_counters.uafHit);
    if (f5 >= 0) InterlockedIncrement(&ts3native_counters.uafF5), F5sNote(b.p + (DWORD)f5 * 4, "uaf write after free");  // docs/539 §21 [Float5Sticky]
    const DWORD off = (DWORD)(f5 >= 0 ? f5 : first) * 4;
    if (g_uafDrArms > 0 && (!g_uafDrChunk || (f5 >= 0 && !g_uafDrF5))) {  // the DR target: first hit, a float-5 hit wins
        g_uafDrChunk = b.chunk, g_uafDrOff = off, g_uafDrF5 = f5 >= 0;
        InterlockedExchange(&g_uafDrWant, g_uafDrArms);
    }
    if (InterlockedIncrement(&g_uafLogN) <= kUafLogMax) {
        char more[96] = "";
        for (int i = 1, w = 0; i < changed && i < 4; i++) w += sprintf_s(more + w, sizeof(more) - w, " +0x%lX=0x%08lX", offs[i], vals[i]);
        char ob[96] = "";
        if (b.from[0] == kUafObjRet) sprintf_s(ob, "; Object block id %08lX%08lX (ObjectService free 0x935E1B)", b.oidHi, b.oidLo);
        Log("uaf: write after free: block 0x%08lX size 0x%lX (chunk 0x%lX), offset +0x%lX value 0x%08lX%s, %d dword(s) changed%s%s; freed %lu ms ago "
            "at save #%ld world load #%ld (now save #%ld, world load #%ld); free stack %08lX %08lX %08lX %08lX; freed under tag %s (site ts3native+0x%lX); "
            "found at %s, thread %lu%s",
            b.p, b.chunk - 0x10, b.chunk, off, d[off / 4], f5 >= 0 ? " (float 5.0)" : "", changed, changed > 1 ? ", others" : "", more,
            GetTickCount() - b.tick, (LONG)b.save, (LONG)b.world, UafSave(), UafWorld(), b.from[0], b.from[1], b.from[2], b.from[3],
            UafTagName(b.tag), UafSiteOff(b.site), when, GetCurrentThreadId(), ob);
    }
    UafFill(b.p, b.chunk - 0x10);
    return changed;
}
static void UafRealFree(const UafBlk& b) {
    InterlockedIncrement(&ts3native_counters.uafFreed);
    UafGoneAdd(b.p, b.chunk, b.tick, b.tag, b.site);
    g_uafFree(*g_uafHeapAt, NULL, (void*)(uintptr_t)b.p);
}
static bool UafHeld(DWORD p) {  // caller holds g_uafLock
    for (DWORD i = 0, j = g_uafHead; i < g_uafN; i++, j = j + 1 == g_uafCap ? 0 : j + 1)
        if (g_uafRing[j].p == p) return true;
    for (const UafArm& a : g_uafArm) if (a.state >= 2 && a.b.p == p) return true;
    return false;
}

// [Float5Free] (float5free.cpp, docs/539 §19) runs first in this hook; with [UafCheck] off the hook only does that.
static bool g_f5fOn, g_f5fGuard;
static bool F5fFreeCheck(void* heap, DWORD p, const DWORD* ra);
static void __fastcall UafFreeHook(void* heap, void* edx, void* ptr) {
    const DWORD p = (DWORD)(uintptr_t)ptr;
    if (g_f5fOn && p && F5fFreeCheck(heap, p, (const DWORD*)_AddressOfReturnAddress())) return;  // Guard=1: a float-5 free skipped
    if (!g_uafOn) return g_uafFree(heap, edx, ptr);
    DWORD ch = 0;
    const bool obj = UafObjFree((const DWORD*)_AddressOfReturnAddress());
    if (!p || heap != *g_uafHeapAt || !UafWanted(p, &ch, obj)) {
        if (p && t_uafTag.tag) UafGoneAdd(p, ch, GetTickCount(), t_uafTag.tag, t_uafTag.site);  // ch 0 unless only the size missed
        return g_uafFree(heap, edx, ptr);
    }
    if (*(const DWORD*)(uintptr_t)p == kUafPat && *(const DWORD*)(uintptr_t)(p + 4) == kUafPat) {  // a second free of a held block?
        if (UafTryLock()) {
            const bool held = UafHeld(p);
            ReleaseSRWLockExclusive(&g_uafLock);
            if (held) {
                InterlockedIncrement(&ts3native_counters.uafPass);
                DWORD f[4] = {};
                UafFrames((const DWORD*)_AddressOfReturnAddress(), f);
                if (InterlockedIncrement(&g_uafDupN) <= 20)
                    Log("uaf: second free of held block 0x%08lX size 0x%lX absorbed (EA would free it twice); stack %08lX %08lX %08lX %08lX, thread %lu", p,
                        ch - 0x10, f[0], f[1], f[2], f[3], GetCurrentThreadId());
                return;
            }
        }
    }
    UafBlk b = {p, ch, GetTickCount(), (DWORD)UafSave(), (DWORD)UafWorld(), {}, t_uafTag.tag, t_uafTag.site, 0, 0};
    if (obj) b.oidLo = ((const DWORD*)ptr)[2], b.oidHi = ((const DWORD*)ptr)[3], InterlockedIncrement(&g_uafObjHeld);  // Object +8/+0xC (0x9362B4)
    UafFrames((const DWORD*)_AddressOfReturnAddress(), b.from);  // from the hook's own return address (EA_Free 0x4E3F84 skipped)
    if (g_uafDrWant > 0 && ch == g_uafDrChunk) {  // DR combo: hold this one aside for a watch on the hit offset
        for (UafArm& a : g_uafArm) {
            if (InterlockedCompareExchange(&a.state, 1, 0) != 0) continue;
            if (InterlockedDecrement(&g_uafDrWant) < 0) { a.state = 0; break; }
            UafFill(p, ch - 0x10);
            a.b = b, a.off = g_uafDrOff, a.slot = -1;
            InterlockedIncrement(&ts3native_counters.uafHeld);
            InterlockedExchange(&a.state, 2);
            SetEvent(g_uafArmEv);
            return;
        }
    }
    UafFill(p, ch - 0x10);
    UafBlk out[8];
    int no = 0;
    bool in = false;
    const DWORD capB = g_uafHold ? g_uafTravelBytes : g_uafMaxBytes;
    if (UafTryLock()) {
        while (g_uafN && (g_uafN == g_uafCap || g_uafBytes + ch > capB) && no < 8) {
            out[no++] = g_uafRing[g_uafHead];
            g_uafBytes -= g_uafRing[g_uafHead].chunk, g_uafN--, g_uafHead = g_uafHead + 1 == g_uafCap ? 0 : g_uafHead + 1;
        }
        if (g_uafHold) g_uafHoldEvict += no, g_uafHoldIn++;
        if (g_uafN < g_uafCap && g_uafBytes + ch <= capB) {
            DWORD t = g_uafHead + g_uafN;
            g_uafRing[t >= g_uafCap ? t - g_uafCap : t] = b, g_uafN++, g_uafBytes += ch, in = true;
        }
        ReleaseSRWLockExclusive(&g_uafLock);
    }
    for (int i = 0; i < no; i++) UafVerify(out[i], "eviction"), UafRealFree(out[i]);
    if (in) InterlockedIncrement(&ts3native_counters.uafHeld);
    else {
        InterlockedIncrement(&ts3native_counters.uafPass);
        if (b.tag) UafGoneAdd(p, ch, b.tick, b.tag, b.site);
        g_uafFree(heap, edx, ptr);
    }
}

// castrim.cpp float-5 detectors: is `a` (a hit address or its object / record base) inside memory our paths freed? Every match in
// the quarantine (held or DR-held), the recently-freed ring and the P5c released ring: "float5 near our free: tag ..."; none: one
// "float5 free lookup: no match" line. [UafCheck] on only; 300 lookups a session.
static int UafNearLog(DWORD a, const char* what) {  // returns the matches (selftest)
    if (!g_uafOn || !a || InterlockedIncrement(&g_uafNearN) > 300) return 0;
    const DWORD now = GetTickCount();
    int hits = 0;
    auto in = [a](DWORD p, DWORD chunk) { return a >= p && a - p < (chunk ? chunk - 0x10 : 0x400); };
    if (UafTryLock()) {
        for (DWORD i = 0, j = g_uafHead; i < g_uafN && hits < 4; i++, j = j + 1 == g_uafCap ? 0 : j + 1) {
            const UafBlk& b = g_uafRing[j];
            if (!in(b.p, b.chunk)) continue;
            hits++;
            char ob[80] = "";
            if (b.from[0] == kUafObjRet) sprintf_s(ob, "; Object block id %08lX%08lX (ObjectService free)", b.oidHi, b.oidLo);
            Log("float5 near our free: tag %s (site ts3native+0x%lX): %s 0x%08lX is +0x%lX into quarantined block 0x%08lX size 0x%lX, freed %lu ms ago "
                "at save #%ld world load #%ld; free stack %08lX %08lX %08lX %08lX%s", UafTagName(b.tag), UafSiteOff(b.site), what, a, a - b.p, b.p,
                b.chunk - 0x10, now - b.tick, (LONG)b.save, (LONG)b.world, b.from[0], b.from[1], b.from[2], b.from[3], ob);
        }
        ReleaseSRWLockExclusive(&g_uafLock);
    }
    for (const UafArm& m : g_uafArm)
        if (m.state >= 2 && in(m.b.p, m.b.chunk) && hits < 8)
            hits++, Log("float5 near our free: tag %s (site ts3native+0x%lX): %s 0x%08lX is +0x%lX into DR-held block 0x%08lX size 0x%lX, freed %lu ms ago",
                        UafTagName(m.b.tag), UafSiteOff(m.b.site), what, a, a - m.b.p, m.b.p, m.b.chunk - 0x10, now - m.b.tick);
    const LONG gn = g_uafGone ? g_uafGoneN : 0;
    for (LONG j = gn - 1; j >= 0 && j >= gn - kUafGoneN && hits < 8; j--) {
        const UafGone& g = g_uafGone[j & (kUafGoneN - 1)];
        if (!in(g.p, g.chunk)) continue;
        hits++;
        Log("float5 near our free: tag %s (site ts3native+0x%lX): %s 0x%08lX is +0x%lX into block 0x%08lX size 0x%lX%s, freed %lu ms ago, "
            "really freed %lu ms ago (recently-freed ring, %ld frees back)", UafTagName(g.tag), UafSiteOff(g.site), what, a, a - g.p, g.p,
            g.chunk ? g.chunk - 0x10 : 0, g.chunk ? "" : " (unknown, 0x400 window)", now - g.tick, now - g.freedTick, gn - 1 - j);
    }
    char rel[192];
    if (P5cRelFind(a, rel, sizeof(rel))) hits++, Log("float5 near our free: tag %s (P5c released ring): %s 0x%08lX %s", UafTagName(UT_P5C_FLUSH), what, a, rel);
    if (!hits)
        Log("float5 free lookup: no match for %s 0x%08lX (quarantine %lu blocks, recently-freed ring %ld, P5c released ring %ld)", what, a, g_uafN,
            gn < kUafGoneN ? gn : kUafGoneN, g_p5cRelN < kP5cRelN ? (LONG)g_p5cRelN : kP5cRelN);
    return hits;
}

// S3SS Mimalloc replaces MSVCR80 malloc/free (docs/68), never EA's GeneralAllocator: say what this process has (S3SS may load
// after us, so again at the first verify). A detour shows as a jmp at the CRT entry, an IAT redirect as no exe slot holding it.
static void UafCrtNote(const char* when) {
    const HMODULE m = GetModuleHandleW(L"MSVCR80.dll");
    const u8* crt = m ? (const u8*)(void*)GetProcAddress(m, "malloc") : NULL;
    const bool jmp = crt && Readable(crt, 1) && crt[0] == 0xE9, iat = crt && CrFindIat(GetModuleHandleW(NULL), crt) != NULL;
    Log("uaf check (%s): MSVCR80 malloc %s%s%s; S3SS Mimalloc %s; EA heap Free 0x4E5920 starts %02X (E9 = a detour: ours or another tool's)", when,
        crt ? "found" : "not loaded", jmp ? ", jmp at its entry" : "", crt ? (iat ? ", exe IAT slot points at it" : ", no exe IAT slot points at it") : "",
        !crt ? "unknown" : jmp || !iat ? "MAY BE ACTIVE (CRT malloc redirected)" : "not active (CRT malloc untouched)", Readable((const void*)(uintptr_t)0x004E5920, 1) ? *(const u8*)(uintptr_t)0x004E5920 : 0);
}

// docs/539 §21: f5watch.cpp F5wEvent calls this at a travel save start (on) and at each world load end (off). A hold that never
// sees a world load ends at the first verify-all 15 min after its start.
static void UafTravelHold(bool on, const char* why, LONG n) {
    if (!g_uafOn || !g_uafTravel || on == (g_uafHold != 0)) return;
    const DWORD now = GetTickCount();
    if (on) {
        g_uafHoldEvict = g_uafHoldIn = 0, g_uafHoldTick = now;
        InterlockedExchange(&g_uafHold, 1);
        Log("uaf: travel hold start at %s #%ld: blocks %lu-%lu bytes freed from now to the next world load end held up to %lu MB (normal cap %lu MB); "
            "held now %lu block(s) %lu MB", why, n, g_uafMin, g_uafMax, g_uafTravelBytes >> 20, g_uafMaxBytes >> 20, g_uafN, g_uafBytes >> 20);
        return;
    }
    InterlockedExchange(&g_uafHold, 0);
    Log("uaf: travel hold end at %s #%ld after %lu s: held %lu block(s) %lu MB (cap %lu MB), %ld freed into the hold, %ld evicted during it "
        "(0 = everything freed in the window stayed in quarantine)", why, n, (now - g_uafHoldTick) / 1000, g_uafN, g_uafBytes >> 20,
        g_uafTravelBytes >> 20, g_uafHoldIn, g_uafHoldEvict);
}

// Each save end, world quit and world load (userdiag.cpp): verify every held block, then one status line.
static void UafVerifyAll(const char* why) {
    if (!g_uafOn) return;
    if (g_uafHold && GetTickCount() - g_uafHoldTick > 15 * 60000) UafTravelHold(false, "hold timeout (no world load in 15 min), verify-all", 0);
    static LONG s_crt;
    if (!s_crt++) UafCrtNote(why);
    const DWORD t0 = GetTickCount();
    AcquireSRWLockExclusive(&g_uafLock);  // shortcut: frees on other threads pass through to EA while this runs (~ms per 10 MB)
    int hits = 0;
    for (DWORD i = 0, j = g_uafHead; i < g_uafN; i++, j = j + 1 == g_uafCap ? 0 : j + 1) hits += UafVerify(g_uafRing[j], why) ? 1 : 0;
    const DWORD n = g_uafN, mb = g_uafBytes >> 20, oldest = n ? t0 - g_uafRing[g_uafHead].tick : 0;
    ReleaseSRWLockExclusive(&g_uafLock);  // held-aside DR blocks: verified by the helper at their release (their watch logs any write)
    const Ts3nCounters& z = ts3native_counters;
    Log("uaf: %s (save #%ld, world load #%ld): held %lu block(s) %lu MB, oldest freed %lu s ago, %d written now, %lu ms; session held %ld, freed %ld, "
        "written %ld (0x40A00000 %ld), passed %ld, DR arms %ld%s (DR hits by ts3native %ld, on a dropped address %ld); Object blocks held %ld (ObjectMaxSize %lu)", why, UafSave(), UafWorld(), n, mb, oldest / 1000, hits, GetTickCount() - t0,
        z.uafHeld, z.uafFreed, z.uafHit, z.uafF5, z.uafPass, z.uafArms, g_uafDrChunk ? ", DR target set" : "", g_wpOwn, g_wpStale, g_uafObjHeld, g_uafObjMax ? g_uafObjMax : g_uafMax);
}

static void UafArmStep(DWORD now) {  // helper thread: arm new held-aside blocks, release old ones
    for (int k = 0; k < 4; k++) {
        UafArm& a = g_uafArm[k];
        if (a.state == 2) {
            a.slot = WpArm((const void*)(uintptr_t)(a.b.p + a.off), 4, true);
            if (a.slot >= 0) InterlockedIncrement(&ts3native_counters.uafArms);
            a.at = now;
            Log("uaf: DR%d %s on block 0x%08lX size 0x%lX offset +0x%lX (target from a %s hit; free stack %08lX %08lX %08lX %08lX); held %lu s", a.slot,
                a.slot >= 0 ? "write watch" : "NOT SET (no debug register free), block held without a watch", a.b.p, a.b.chunk - 0x10, a.off,
                g_uafDrF5 ? "0x40A00000" : "write-after-free", a.b.from[0], a.b.from[1], a.b.from[2], a.b.from[3], g_uafDrHoldMs / 1000);
            InterlockedExchange(&a.state, 3);
        } else if (a.state == 3 && now - a.at >= g_uafDrHoldMs) {
            if (a.slot >= 0) WpDisarm(a.slot);
            UafVerify(a.b, "DR release");
            UafRealFree(a.b);
            InterlockedExchange(&a.state, 0);
        }
    }
}
static DWORD WINAPI UafArmMain(LPVOID) {
    for (DWORD refresh = GetTickCount();;) {
        WaitForSingleObject(g_uafArmEv, 1000);
        const DWORD now = GetTickCount();
        UafArmStep(now);
        bool armed = false;
        for (const UafArm& a : g_uafArm) armed |= a.state == 3 && a.slot >= 0;
        if (armed && now - refresh >= 10000) WpRefresh(), refresh = now;  // threads started since the arm
    }
}

// 0.36.230: a jmp at 0x4E5920 into another module (S3SS, Apex, a debugger tool) is refused; ours ([Alloc], AllocSites) chain.
// Returns that jmp's target, 0 = none or ours.
static DWORD UafForeignJmp(const u8* t) {
    if (t[0] != 0xE9) return 0;
    const DWORD dst = (DWORD)(uintptr_t)(t + 5 + *(const LONG*)(t + 1));
    return dst >= g_selfLo && dst < g_selfHi ? 0 : dst;
}
static bool UafHookCheck(const char* who) {  // 0x4E5920 as expected: EA's prologue or our own jmp ([Alloc], AllocSites)
    const u8* t = (const u8*)(uintptr_t)0x004E5920;
    if (!Readable(t, 10) || (t[0] != 0xE9 && memcmp(t, kEaLockedPrologue, sizeof(kEaLockedPrologue)))) { Log("SKIP %s: EA heap Free not as expected", who); return false; }
    if (const DWORD dst = UafForeignJmp(t)) {
        Log("SKIP %s: EA heap Free 0x4E5920 is already patched by another module (jmp to 0x%08lX, outside ts3native); not chaining into it", who, dst);
        return false;
    }
    return true;
}
// One detour on EA Free for [UafCheck] and [Float5Free] (0.36.231): the first caller installs it, the second reuses it.
static bool UafHookInstall(const char* who) {
    if (g_uafFree) return true;
    if (!UafHookCheck(who)) return false;
    g_uafFree = (EaFreeFn)AsDetour("EA heap Free (uaf check / float5 free)", 0x004E5920, kEaLockedPrologue, (int)sizeof(kEaLockedPrologue), (void*)UafFreeHook, &g_uafFree);
    return g_uafFree != NULL;
}
static bool UafInit(DWORD maxMB, DWORD maxBlocks, DWORD travelMB = 0) {
    const DWORD metaMB = (maxBlocks * (DWORD)sizeof(UafBlk) + kUafGoneN * (DWORD)sizeof(UafGone) + (1 << 20) - 1) >> 20;
    if (maxMB + metaMB > 64) maxMB = metaMB < 64 ? 64 - metaMB : 1;  // 32-bit address space: 64 MB in all
    if (travelMB + metaMB > 64) travelMB = metaMB < 64 ? 64 - metaMB : 1;  // the travel hold keeps the same clamp
    g_uafTravelBytes = (travelMB > maxMB ? travelMB : maxMB) << 20;
    g_uafRing = (UafBlk*)VirtualAlloc(NULL, maxBlocks * sizeof(UafBlk), MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
    if (!g_uafRing) return false;
    if (!g_uafGone) g_uafGone = (UafGone*)VirtualAlloc(NULL, kUafGoneN * sizeof(UafGone), MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
    g_uafCap = maxBlocks, g_uafMaxBytes = maxMB << 20, g_uafHead = g_uafN = g_uafBytes = 0;
    return true;
}

// [UafCheck] Enabled=1: TEST-RUN TOOL (docs/539 §17). After [CrashForensics] and [MemDiag] AllocSites (chains their jmps).
static bool ApplyUafCheck(const wchar_t* ini) {
    if (GetPrivateProfileIntW(L"UafCheck", L"Enabled", 0, ini) <= 0) return false;
    if (g_apexFound && g_apexStandDown) { Log("SKIP uaf check: Apex Radiance loaded (the allocator is its area; [Compat] ApexStandDown=1)"); return false; }
    g_uafMin = (DWORD)Clamp((int)GetPrivateProfileIntW(L"UafCheck", L"MinSize", 64, ini), 0, 65536);
    g_uafMax = (DWORD)Clamp((int)GetPrivateProfileIntW(L"UafCheck", L"MaxSize", 4096, ini), 16, 65536);
    const DWORD maxMB = (DWORD)Clamp((int)GetPrivateProfileIntW(L"UafCheck", L"MaxMB", 48, ini), 1, 64);
    const DWORD maxBlocks = (DWORD)Clamp((int)GetPrivateProfileIntW(L"UafCheck", L"MaxBlocks", 262144, ini), 16, 1 << 20);
    g_uafDrArms = Clamp((int)GetPrivateProfileIntW(L"UafCheck", L"DrArms", 2, ini), 0, 4);
    g_uafDrHoldMs = (DWORD)Clamp((int)GetPrivateProfileIntW(L"UafCheck", L"DrHoldSec", 600, ini), 5, 86400) * 1000;
    g_uafObjMax = (DWORD)Clamp((int)GetPrivateProfileIntW(L"UafCheck", L"ObjectMaxSize", 0, ini), 0, 65536);
    g_uafTravel = GetPrivateProfileIntW(L"UafCheck", L"TravelHold", 0, ini) > 0;  // docs/539 §21
    const DWORD travelMB = (DWORD)Clamp((int)GetPrivateProfileIntW(L"UafCheck", L"TravelMaxMB", 0, ini), 0, 64);
    UafCrtNote("install");
    if (!UafHookCheck("uaf check")) return false;
    if (!UafInit(maxMB, maxBlocks, travelMB)) { Log("FAIL uaf check: ring (err %lu)", GetLastError()); return false; }
    if (g_uafDrArms) {
        WpInstall();
        g_uafArmEv = CreateEventW(NULL, FALSE, FALSE, NULL);
        HANDLE h = g_uafArmEv ? CreateThread(NULL, 64 << 10, UafArmMain, NULL, STACK_SIZE_PARAM_IS_A_RESERVATION, NULL) : NULL;
        if (h) CloseHandle(h); else g_uafDrArms = 0;
    }
    g_uafOn = UafHookInstall("uaf check");
    Log("uaf check %s (TEST-RUN TOOL, docs/539 §17): main heap [0x11CB864] blocks %lu-%lu bytes held filled with 0xFEEEFEEE, up to %lu blocks / %lu MB "
        "(ring %lu KB), verified at eviction, save end, world quit and world load; DR arms %ld (hold %lu s); Object blocks (ObjectService free 0x935E1B, "
        "id kept) up to %lu bytes", g_uafOn ? "on" : "off (no hook)", g_uafMin, g_uafMax, g_uafCap, g_uafMaxBytes >> 20,
        g_uafCap * (DWORD)sizeof(UafBlk) >> 10, g_uafDrArms, g_uafDrHoldMs / 1000, g_uafObjMax > g_uafMax ? g_uafObjMax : g_uafMax);
    if (g_uafOn && g_uafTravel)
        Log("uaf check travel hold on (docs/539 §21): from each travel save start to the next world load end the cap is %lu MB (TravelMaxMB, "
            "clamped with the ring to 64 MB; normal %lu MB)", g_uafTravelBytes >> 20, g_uafMaxBytes >> 20);
    return g_uafOn;
}
