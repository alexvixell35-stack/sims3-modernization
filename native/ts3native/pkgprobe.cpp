// [PkgProbe] (docs/549 §4.1-§4.3, counters v919 pp*): resource manager probe (log only) and the package remove re-find fix.
// Default OFF. Included by ts3native.cpp after pkglock.cpp; selftest: pkgprobe-test.h.
//
// EA code [verified disassembly, TS3W 1.67 repack copy; names: OpenBarrio map, docs/547]. The resource manager M is the
// global [0x11C78D8] (vtables 0xFB2DA0 / 0xFFE250), its lock is the EA Lock at M+0x48, its database list a vector of
// {database, priority} pairs at M+0x30 (begin) / M+0x34 (end), highest priority first.
//   Resolve 0x7D80E0 (cdecl: out key*, key*; returns out; 16-byte key, word 3 = group): a key with type-tag bits
//   (word 3 & 0xF0000000) is copied as is. Otherwise M slot 17 (+0x44, 0x4AFDA0 -> FindDatabase 0x4AFFC0, which walks every
//   database and unlocks around each one's find) is asked; on a miss the fallback key 0x7D7580 (group bit 0x08000000 flipped,
//   and the same bit of the instance for five types) is built, compared with 0x54DD80 (1 = differs; it always does) and
//   asked too; out = the fallback when that finds a database, else the key. 190 bytes, no other effect.
//   RegisterDatabase 0x4B2D00 (thiscall M; bool add, database*, priority; ret 0xC; M slot 13, also called directly from the
//   subclass slot 0x736A70 at 0x736C69): add inserts by priority under the lock; remove (add = 0) finds the pair under the
//   lock (esi), unlocks (0x4B2E3A), calls the database's detach slots, locks again (0x4B2E73) and erases at esi.
//   SetDatabasePriority 0x4B2EC0 (thiscall M; database*, priority; ret 8; M slot 15 in both vtables): erases the pair, then
//   inserts from the same, now shifted, slot (0x4B2F18..0x4B2FFD): the moved database drops out of the list, its successor
//   appears twice and the new priority is never stored (docs/549 §4 finding 3). No caller found: no direct call, and no
//   `[reg+0x3C]` call within 60 instructions of any of the 489 fetches of [0x11C78D8] [verified scan]; Apex Radiance chains
//   this slot (docs/data/apex-hooks.csv, Night Lights) [claim]. So it is counted and logged here, not fixed.
//
// Enabled=1 (log only, results unchanged):
//   - Resolve is replaced by our own copy of the steps above (whole body byte-checked by hash at install, so it only goes in
//     over the code it copies). It calls the same slot through M's vtable at call time (Apex's slot chain included), EA's
//     0x7D7580 and 0x54DD80, so every answer is EA's; it counts calls, tagged keys, first-lookup misses, fallback lookups and
//     fallback hits, and how many Resolves are in flight.
//   - RegisterDatabase and SetDatabasePriority entries are detoured to count adds / removes / repositions, with thread and
//     whether another thread was inside a Resolve at the time (the finding 2 race window: FindDatabase holds begin / end
//     across its unlocks [verified 0x4AFFD6 / 0x4AFFD9]). Each remove and reposition, and the first 16 off-main-thread adds,
//     get a "pkgprobe:" line (200 a session).
//   - One "pkgprobe:" summary line a minute (from the first Resolve after the minute is up; none when nothing resolves).
// RemoveFix=1 (finding 2, remove half): a 6-byte stub at 0x4B2E78, right after the remove path locks again, finds the
//   database's pair again in the list as it is now (first match, as EA's own search). Same pair = EA's path unchanged; moved
//   (the list changed while unlocked) = erase at its new place; gone = no erase (EA would erase whatever sits at the stale
//   slot, or freed memory). Without a concurrent add / remove the list is the one EA searched, so results are vanilla.
//   FindDatabase's own stale begin / end is not fixed: Apex owns that slot (ResourceLookupCache), and a walk that lost its
//   place has no exact vanilla answer to keep; the probe counts the window instead.
// Apex Radiance: chains the manager's vtable slots (it checks the slot values, which these detours leave alone) [docs/148:
//   slot_chain refuses only a slot that holds another value]. Our jmps sit inside EA's bodies, which Apex calls through.
// Household rule: not applicable (resource system, no Sim decision).

static const DWORD PP_RESOLVE = 0x007D80E0, PP_REGISTER = 0x004B2D00, PP_REPOS = 0x004B2EC0;
static const DWORD PP_RF_AT = 0x004B2E78, PP_RF_BACK = 0x004B2E7E, PP_RF_SKIP = 0x004B2EA4;
static const DWORD PP_MGR_GLOBAL = 0x011C78D8, PP_VT_A = 0x00FB2DA0, PP_VT_B = 0x00FFE250;
static const DWORD PP_RESOLVE_LEN = 0xBE, PP_RESOLVE_FNV = 0x98A9D373;  // FNV-1a 32 of 0x7D80E0..0x7D819D (ends in ret)
// sub esp,10h; push esi; mov esi,[esp+1Ch]
static const u8 kPpResolve[8] = {0x83, 0xEC, 0x10, 0x56, 0x8B, 0x74, 0x24, 0x1C};
// sub esp,10h; push ebx; push ebp; push esi; push edi; mov edi,ecx
static const u8 kPpRegister[9] = {0x83, 0xEC, 0x10, 0x53, 0x55, 0x56, 0x57, 0x8B, 0xF9};
// sub esp,0Ch; push ebp; push esi; mov ebp,ecx; push edi
static const u8 kPpRepos[8] = {0x83, 0xEC, 0x0C, 0x55, 0x56, 0x8B, 0xE9, 0x57};
// mov ecx,[edi+34h]; lea eax,[esi+8]  (edi = M, esi = the pair found before the unlock, ebp = the database)
static const u8 kPpRf[6] = {0x8B, 0x4F, 0x34, 0x8D, 0x46, 0x08};

typedef void*(__fastcall* PpLookupFn)(void* mgr, void* edx, const DWORD* key);  // M slot 17: database or NULL
typedef void(__cdecl* PpFallbackFn)(DWORD* out, const DWORD* key);              // 0x7D7580
typedef u8(__cdecl* PpDifferFn)(const DWORD* a, const DWORD* b);                // 0x54DD80: 1 = keys differ
typedef DWORD(__fastcall* PpRegisterFn)(u8* mgr, void* edx, DWORD add, u8* db, LONG prio);
typedef DWORD(__fastcall* PpReposFn)(u8* mgr, void* edx, u8* db, LONG prio);

// game entries; selftest swaps the first three
static u8* const* g_ppMgrAt = (u8* const*)(uintptr_t)PP_MGR_GLOBAL;
static PpFallbackFn g_ppFallback = (PpFallbackFn)(uintptr_t)0x007D7580;
static PpDifferFn g_ppDiffer = (PpDifferFn)(uintptr_t)0x0054DD80;
static PpRegisterFn g_ppRegister;  // trampolines
static PpReposFn g_ppRepos;
static DWORD g_ppRfBack = PP_RF_BACK, g_ppRfSkip = PP_RF_SKIP;  // the RemoveFix stub's exits (selftest swaps them)

static volatile LONG g_ppInResolve;  // Resolves in flight, all threads
static volatile LONG g_ppNextLine;   // GetTickCount of the next summary line
static volatile LONG g_ppLines;      // event lines written
static const LONG kPpMaxLines = 200;
static volatile LONG g_ppOffMainAdds;

static u8* PpMgr() { return *(u8* volatile const*)g_ppMgrAt; }
static bool PpKnownMgr(const u8* m) {
    __try {
        const DWORD vt = *(const DWORD*)m;
        return vt == PP_VT_A || vt == PP_VT_B;
    } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}
// databases in M's list (unlocked read: a count for the log only), -1 = no manager we know
static LONG PpMounted(const u8* m) {
    if (!m || !PpKnownMgr(m)) return -1;
    __try {
        const uintptr_t b = *(const uintptr_t*)(m + 0x30), e = *(const uintptr_t*)(m + 0x34);
        return e >= b ? (LONG)((e - b) / 8) : -1;
    } __except (EXCEPTION_EXECUTE_HANDLER) { return -1; }
}
static void PpCopyKey(DWORD* out, const DWORD* key) {
    const DWORD a = key[0], b = key[1], c = key[2], d = key[3];  // read first: out may be key
    out[0] = a, out[1] = b, out[2] = c, out[3] = d;
}

// --- summary line, once a minute ---
static void PpSummary(DWORD now) {
    static LONG last[6];  // resolve, tagged, miss, retry, retry hit, no manager at the last line (one writer: the CAS winner)
    Ts3nCounters& k = ts3native_counters;
    const LONG cur[6] = {k.ppResolve, k.ppTagged, k.ppMiss, k.ppRetry, k.ppRetryHit, k.ppNoMgr};
    LONG d[6];
    for (int i = 0; i < 6; i++) d[i] = cur[i] - last[i], last[i] = cur[i];
    k.ppMounted = PpMounted(PpMgr());
    const LONG looked = d[0] - d[1] - d[5];
    Log("pkgprobe: last 60 s resolve %ld (tagged %ld, no manager %ld, looked up %ld: miss %ld = %.1f%%, fallback tried %ld, hit %ld); "
        "%ld databases; session: adds %ld, removes %ld, repositions %ld (off main %ld, during a resolve %ld), remove re-find moved %ld "
        "gone %ld [t=%lu]",
        d[0], d[1], d[5], looked, d[2], looked > 0 ? 100.0 * d[2] / looked : 0.0, d[3], d[4], k.ppMounted, k.ppAdds, k.ppRemoves, k.ppRepos,
        k.ppOffMain, k.ppOverlap, k.ppRfMoved, k.ppRfGone, now);
}
static void PpMaybeSummary() {
    const DWORD now = GetTickCount();
    const LONG due = g_ppNextLine;
    if ((LONG)(now - (DWORD)due) < 0) return;
    if (InterlockedCompareExchange(&g_ppNextLine, (LONG)(now + 60000), due) == due) PpSummary(now);
}

// --- Resolve 0x7D80E0: EA's steps, counted ---
static DWORD* __cdecl PpResolveHook(DWORD* out, const DWORD* key) {
    Ts3nCounters& k = ts3native_counters;
    InterlockedIncrement(&k.ppResolve);
    if (key[3] & 0xF0000000) {  // type-tag bits: no lookup
        InterlockedIncrement(&k.ppTagged);
        PpCopyKey(out, key);
        return out;
    }
    InterlockedIncrement(&g_ppInResolve);
    const DWORD* use = key;
    DWORD fb[4];
    u8* const m = PpMgr();
    if (!m) InterlockedIncrement(&k.ppNoMgr);
    else {
        const PpLookupFn find = *(const PpLookupFn*)(*(const u8* const*)m + 0x44);
        if (!find(m, NULL, key)) {
            InterlockedIncrement(&k.ppMiss);
            g_ppFallback(fb, key);
            if (g_ppDiffer(fb, key)) {
                InterlockedIncrement(&k.ppRetry);
                if (find(m, NULL, fb)) InterlockedIncrement(&k.ppRetryHit), use = fb;
            }
        }
    }
    PpCopyKey(out, use);
    InterlockedDecrement(&g_ppInResolve);
    PpMaybeSummary();
    return out;
}

// --- RegisterDatabase / SetDatabasePriority entries: counted, rare ones logged ---
static bool PpNote(bool rare) {  // counts thread and overlap; true = write a line for this call
    Ts3nCounters& k = ts3native_counters;
    const bool offMain = GetCurrentThreadId() != g_mainTid;
    if (offMain) InterlockedIncrement(&k.ppOffMain);
    if (g_ppInResolve > 0) InterlockedIncrement(&k.ppOverlap);
    if (!rare && !(offMain && InterlockedIncrement(&g_ppOffMainAdds) <= 16)) return false;
    return InterlockedIncrement(&g_ppLines) <= kPpMaxLines;
}
static DWORD __fastcall PpRegisterHook(u8* m, void* edx, DWORD add, u8* db, LONG prio) {
    const bool isAdd = (u8)add != 0;
    InterlockedIncrement(isAdd ? &ts3native_counters.ppAdds : &ts3native_counters.ppRemoves);
    if (PpNote(!isAdd))
        Log("pkgprobe: %s database %p prio %ld tid %lu (%s) from 0x%08lX; %ld databases, %ld resolves in flight", isAdd ? "add" : "remove", db,
            prio, GetCurrentThreadId(), GetCurrentThreadId() == g_mainTid ? "main" : "other", (DWORD)(uintptr_t)_ReturnAddress(), PpMounted(m),
            g_ppInResolve);
    return g_ppRegister(m, edx, add, db, prio);  // full eax kept (callers read al)
}
static DWORD __fastcall PpReposHook(u8* m, void* edx, u8* db, LONG prio) {
    InterlockedIncrement(&ts3native_counters.ppRepos);
    if (PpNote(true))
        Log("pkgprobe: reposition database %p to prio %ld tid %lu (%s) from 0x%08lX; %ld databases (EA bug, docs/549 §4.3: the database "
            "drops out of the list)",
            db, prio, GetCurrentThreadId(), GetCurrentThreadId() == g_mainTid ? "main" : "other", (DWORD)(uintptr_t)_ReturnAddress(), PpMounted(m));
    return g_ppRepos(m, edx, db, prio);
}

// --- RemoveFix: the pair again, after the remove path's relock (M's lock held) ---
// The first pair of `db` in M's list as it is now; NULL = gone. Counts a pair that moved or went.
static u8* __cdecl PpRefind(const u8* m, const u8* db, u8* stale) {
    u8* it = *(u8* const*)(m + 0x30);
    u8* const end = *(u8* const*)(m + 0x34);
    for (; it < end; it += 8)
        if (*(const u8* const*)it == db) break;
    if (it >= end) { InterlockedIncrement(&ts3native_counters.ppRfGone); return NULL; }
    if (it != stale) InterlockedIncrement(&ts3native_counters.ppRfMoved);
    return it;
}
static __declspec(naked) void PpRemoveFixStub() {  // at 0x4B2E78: edi = M, ebp = database, esi = stale pair
    __asm {
        push esi
        push ebp
        push edi
        call PpRefind  // eax, ecx, edx are dead here: EA's next instructions write eax / ecx, edx is written before any read
        add esp, 12
        test eax, eax
        jz gone
        mov esi, eax
        mov ecx, dword ptr [edi + 0x34]  // EA's two replaced instructions
        lea eax, [esi + 8]
        jmp dword ptr [g_ppRfBack]  // 0x4B2E7E: EA's erase at esi
    gone:
        jmp dword ptr [g_ppRfSkip]  // 0x4B2EA4: EA's unlock and return (al = the detach result, as EA)
    }
}

static DWORD PpFnv(const u8* p, DWORD n) {
    DWORD h = 0x811C9DC5;
    for (DWORD i = 0; i < n; i++) h = (h ^ p[i]) * 0x01000193;
    return h;
}

// [PkgProbe] Enabled=1: Resolve / register / reposition counters and lines (log only); RemoveFix=1: the remove re-find
static bool ApplyPkgProbe(const wchar_t* ini) {
    const bool probe = GetPrivateProfileIntW(L"PkgProbe", L"Enabled", 0, ini) > 0;
    const bool fix = GetPrivateProfileIntW(L"PkgProbe", L"RemoveFix", 0, ini) > 0;
    if (!probe && !fix) return false;
    LONG mode = 0;
    if (probe) {
        g_ppNextLine = (LONG)(GetTickCount() + 60000);
        const u8* r = (const u8*)(uintptr_t)PP_RESOLVE;
        if (Readable(r, PP_RESOLVE_LEN) && PpFnv(r, PP_RESOLVE_LEN) == PP_RESOLVE_FNV) {
            if (InstallDetour("Resolve (pkgprobe)", PP_RESOLVE, kPpResolve, sizeof(kPpResolve), (void*)PpResolveHook)) mode |= 1;
        } else Log("SKIP pkgprobe Resolve 0x7D80E0: body differs from the one our copy follows");
        if (InstallDetour("RegisterDatabase (pkgprobe)", PP_REGISTER, kPpRegister, sizeof(kPpRegister), (void*)PpRegisterHook, &g_ppRegister)) mode |= 2;
        if (InstallDetour("SetDatabasePriority (pkgprobe)", PP_REPOS, kPpRepos, sizeof(kPpRepos), (void*)PpReposHook, &g_ppRepos)) mode |= 4;
    }
    if (fix && InstallDetour("RegisterDatabase remove re-find (pkgprobe RemoveFix)", PP_RF_AT, kPpRf, sizeof(kPpRf), (void*)PpRemoveFixStub)) mode |= 8;
    ts3native_counters.ppMode = mode;
    ts3native_counters.ppMounted = -1;
    Log("pkgprobe (docs/549 §4): mode %ld (1 Resolve counted, 2 register / remove, 4 reposition, 8 RemoveFix); probe %s, RemoveFix %s", mode,
        probe ? "on (log only)" : "off", fix ? ((mode & 8) ? "on" : "requested, not installed") : "off");
    return mode != 0;
}
