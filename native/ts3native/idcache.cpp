// [IdCache] (PLAN #22, docs/45 O2): id -> object cache in front of the object id hash. Included by ts3native.cpp.
//
// Facts this relies on [verified disassembly, Steam 1.67; tools/emu spec object_id_lookup.py checks the outputs]:
//   - ObjectId_Lookup 0x939100 (thiscall, ecx = bucket array = object service +0x10, arg1 = out iterator (2 DWORDs),
//     arg2 = pointer to the 64-bit id; ret 8, eax = out): bucket = id_lo % 0x409; walk the chain through node+0x10,
//     comparing node+0x08/+0x0C. Hit: out = {node, &bucket}. Miss: out = {[ecx+0x1024], ecx+0x1024} (the end sentinel).
//     Only 6 direct callers, all wrappers of the service (FindById 0x939560 with 442 call sites, FindByIdAddRef, ...).
//   - ObjectIdHash_EraseNode 0x938D00 (thiscall, ecx = bucket array, arg1 = out iterator, arg2 = node, arg3 = bucket
//     slot; ret 0xC) is the only code that unlinks a node: its two `add [ecx+0x1028],-1` are the only count decrements in
//     the object service code, its one caller is DestroyById 0x939420, and DestroyAll 0x939510 destroys through
//     DestroyById. The only other count write is the service constructor 0x93A510 (memset of the buckets, one call site).
// The chains are 8-15 nodes long in play (docs/62 §5 [measured]) and ids repeat (get_LotCurrent -> parent id, docs/20).
//
// Cache: direct-mapped, 4096 entries keyed on the full 64-bit id, used and filled on the sim thread only ([0x11D8A8C]);
// other threads always take the game's walk. A hit also needs the node to still carry the id and its bucket to be non-empty
// (a re-run constructor zeroes every bucket). Invalidation: EraseNode (any thread, any mode) bumps g_idcGen and clears the
// entry of the erased node before the game unlinks it; a fill that raced an erase (generation changed during the walk) is
// dropped. Insert needs nothing: only found nodes are cached, never misses. Verify=1: every hit also walks the chain and
// compares (mismatch: counted, logged, entry dropped, the walk's answer returned). Runtime switch: idcMode (+0xC3C).

static const DWORD IDC_LOOKUP = TS3A(0x00939100), IDC_ERASE = TS3A(0x00938D00);
static const u8 kIdcLookupPrologue[8] = {0x83, 0xEC, 0x08, 0x56, 0x8B, 0x74, 0x24, 0x14};  // sub esp,8; push esi; mov esi,[esp+14h]
static const u8 kIdcErasePrologue[5] = {0x8B, 0x44, 0x24, 0x04, 0x53};                     // mov eax,[esp+4]; push ebx
static const DWORD* g_idcSimTid = (const DWORD*)(uintptr_t)TS3A(0x011D8A8C);                   // sim thread id (game global, 0 until set)

typedef DWORD*(__fastcall* IdcLookupFn)(u8* tbl, void* edx, DWORD* out, const DWORD* id);
typedef void*(__fastcall* IdcEraseFn)(u8* tbl, void* edx, void* out, u8* node, void* bucket);
static IdcLookupFn g_idcLookupOrig;
static IdcEraseFn g_idcEraseOrig;
static bool g_idcVerify;
static u8* volatile g_idcTbl;       // the one bucket array seen first; any other goes to the game
static volatile LONG g_idcGen;      // bumped by every erase
static unsigned __int64 g_idcLookups, g_idcHits, g_idcFills, g_idcNotFound, g_idcSteps;  // sim thread only

struct IdcEntry { DWORD lo, hi; u8* volatile node; DWORD pad; };
static IdcEntry g_idc[4096];

static inline IdcEntry& IdcSlot(DWORD lo, DWORD hi) { return g_idc[((lo ^ hi * 0x9E3779B1u) * 0x85EBCA6Bu) >> 20]; }
static inline DWORD IdcTid() { return __readfsdword(0x24); }

// the game's walk (ObjectId_Lookup without the iterator): node or NULL, chain nodes visited added to *steps
static u8* IdcWalk(u8* const* bucket, DWORD lo, DWORD hi, unsigned __int64* steps) {
    for (u8* n = *bucket; n; n = *(u8* const*)(n + 0x10)) {
        ++*steps;
        if (*(const DWORD*)(n + 8) == lo && *(const DWORD*)(n + 0xC) == hi) return n;
    }
    return NULL;
}

static void IdcPublish(u8* tbl) {
    Ts3nCounters& k = ts3native_counters;
    k.idcLookupsK = (LONG)(g_idcLookups / 1000);
    k.idcHitsK = (LONG)(g_idcHits / 1000);
    k.idcFillsK = (LONG)(g_idcFills / 1000);
    k.idcNotFoundK = (LONG)(g_idcNotFound / 1000);
    k.idcStepsK = (LONG)(g_idcSteps / 1000);
    k.idcObjects = *(const LONG*)(tbl + 0x1028);
}

static DWORD* __fastcall IdcLookupHook(u8* tbl, void* edx, DWORD* out, const DWORD* id) {
    Ts3nCounters& k = ts3native_counters;
    if (k.idcMode != 1) return g_idcLookupOrig(tbl, edx, out, id);
    if (tbl != g_idcTbl && (g_idcTbl || InterlockedCompareExchangePointer((void* volatile*)&g_idcTbl, tbl, NULL) != NULL))
        return g_idcLookupOrig(tbl, edx, out, id);
    const DWORD sim = *g_idcSimTid;
    if (!sim || IdcTid() != sim) return g_idcLookupOrig(tbl, edx, out, id);
    if ((++g_idcLookups & 1023) == 0) IdcPublish(tbl);
    const DWORD lo = id[0], hi = id[1];
    u8** bucket = (u8**)(tbl + (lo % 0x409) * 4);
    IdcEntry& e = IdcSlot(lo, hi);
    u8* n = e.node;
    if (n && e.lo == lo && e.hi == hi && *(const DWORD*)(n + 8) == lo && *(const DWORD*)(n + 0xC) == hi && *bucket) {
        g_idcHits++;
        if (g_idcVerify) {
            unsigned __int64 ignore = 0;
            u8* real = IdcWalk(bucket, lo, hi, &ignore);
            if (real != n) {
                if (InterlockedIncrement(&k.idcMismatches) <= 10)
                    Log("idcache MISMATCH id %08X%08X cached %p real %p", hi, lo, n, real);
                e.node = NULL;
                if (!real) { out[0] = *(const DWORD*)(tbl + 0x1024); out[1] = (DWORD)(uintptr_t)(tbl + 0x1024); return out; }
                n = real;
            }
        }
        out[0] = (DWORD)(uintptr_t)n;
        out[1] = (DWORD)(uintptr_t)bucket;
        return out;
    }
    const LONG gen = g_idcGen;
    n = IdcWalk(bucket, lo, hi, &g_idcSteps);
    if (!n) {
        g_idcNotFound++;
        out[0] = *(const DWORD*)(tbl + 0x1024);
        out[1] = (DWORD)(uintptr_t)(tbl + 0x1024);
        return out;
    }
    g_idcFills++;
    e.node = NULL;
    e.lo = lo;
    e.hi = hi;
    e.node = n;
    MemoryBarrier();                 // the fill must be visible before the generation is re-read (x86 store-load order)
    if (g_idcGen != gen) e.node = NULL;  // an erase ran during the walk: it may have missed this fill
    out[0] = (DWORD)(uintptr_t)n;
    out[1] = (DWORD)(uintptr_t)bucket;
    return out;
}

// Runs in every mode: entries filled while the switch was on must not outlive their node when it is switched back on.
static void* __fastcall IdcEraseHook(u8* tbl, void* edx, void* out, u8* node, void* bucket) {
    InterlockedIncrement(&g_idcGen);  // locked: a full barrier before the entry is read
    if (node) {
        IdcEntry& e = IdcSlot(*(const DWORD*)(node + 8), *(const DWORD*)(node + 0xC));
        if (e.node == node) e.node = NULL;
    }
    InterlockedIncrement(&ts3native_counters.idcErases);
    return g_idcEraseOrig(tbl, edx, out, node, bucket);
}

// [IdCache] Enabled=1 hooks; Mode = start value of ts3native_counters.idcMode; Verify=1 walks the chain on every hit.
static bool ApplyIdCache(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"IdCache", L"Enabled", 0, ini)) { Log("object id cache disabled (Enabled=0)"); return false; }
    if (ApexStandsDown(AXF_OBJECT_INDEX, "[IdCache]", "entries 0x939100, 0x938D00")) return false;
    g_idcVerify = GetPrivateProfileIntW(L"IdCache", L"Verify", 0, ini) != 0;
    ts3native_counters.idcMode = GetPrivateProfileIntW(L"IdCache", L"Mode", 1, ini) == 0 ? 0 : 1;
    // the invalidation first: the cache never runs without it
    g_idcEraseOrig = (IdcEraseFn)InstallDetour("object id erase (id cache)", IDC_ERASE, kIdcErasePrologue, sizeof(kIdcErasePrologue), (void*)IdcEraseHook, &g_idcEraseOrig);
    if (!g_idcEraseOrig) return false;
    g_idcLookupOrig = (IdcLookupFn)InstallDetour("object id lookup (id cache)", IDC_LOOKUP, kIdcLookupPrologue, sizeof(kIdcLookupPrologue), (void*)IdcLookupHook, &g_idcLookupOrig);
    if (!g_idcLookupOrig) return false;  // the erase detour stays: it only clears entries before the game's code
    ts3native_counters.idcReady = 1;
    Log("OK   object id cache: 0x%08X, 0x%08X (Mode=%ld Verify=%d, 4096 entries, sim thread only)", IDC_LOOKUP, IDC_ERASE,
        ts3native_counters.idcMode, g_idcVerify ? 1 : 0);
    return true;
}
