// [ThumbStall] (issue #40). Included by ts3native.cpp (one translation unit).
// Off by default. No counters in Ts3nCounters: results are the "thumbstall:" log lines and the agent's thumb_census
// (tm_done moves again, tm_front_age_ms stays under the timeout). Runs th15-th18 of 2026-11-12 (old save, travel, ultra):
// Action=3 TimeoutMs=3000 rendered every stalled portrait (29 of 29, 0 aborts; th17 and th18 on the bridge copy, th18
// with Apex Radiance loaded).
//
// What: UI/ThumbnailManager works one request at a time (the front of InProgressRequests, state machine 0xD59C10 stepped
// once per frame on the main thread by 0xD5C6D0). Its state 0x11 calls the "scene loaded" test 0x6F3DC0 at 0xD5A9BF with
// layer mask 0x7FFFF000 (0x7FDFF000 for one request kind) and flag mask 0x42: while ANY scene object in those layers has a
// pending texture composite (object +0x40 bit 2 or 0x40, computed by 0x6F7B40 from the material state at +0x210), the
// request returns "come back next frame". There is no timeout. Measured (th1): at game speed ultra the front request stayed
// in state 0x11 for 105 s, nothing finished, 110 requests and 14 pack-away tasks queued behind it [verified run]. The
// blocker is always the request's own model, waiting for a Sim LOD the SimService does not build (below).
//
// Site [verified disassembly, Steam TS3W.exe 1.67]: the blocked path of state 0x11,
//   0xD5A9C8  8B 54 24 60        mov edx,[esp+60h]      ; the blocking scene object (out argument of 0x6F3DC0)
//   0xD5A9CC  89 97 44 02 00 00  mov [edi+244h],edx     ; request +0x244 = blocker
//   0xD5A9D2  32 C0 5F 5E 5B 8B E5 5D C2 04 00          ; return 0 (shared epilogue)
// The 10 bytes at 0xD5A9C8 become `jmp TsStub` + 5 nops. The stub runs the two instructions, asks TsBlocked, and then
//   0 -> 0xD5A9D2  EA's "return 0", unchanged behaviour
//   1 -> request +0x108 = 0x19, then 0xD5AA25 (`mov al,1` + the same epilogue): EA's own abort state. The state machine's
//        entry sets 0x19 from any state below 0x19 except 1 when the request is cancelled, so 0x19 from 0x11 is a path the
//        game takes itself; 0x19..0x1E release the request's jobs, models and textures and wake its waiter with "failed"
//   2 -> request +0x244 = 0 (as EA leaves it when not blocked), then 0xD5A9DD, EA's "not blocked" path of state 0x11:
//        the request renders now
// eax, ecx, edx are dead at the site (the epilogue overwrites al; 0xD5A9DD reloads what it uses); ebx, esi, edi, ebp and
// the frame are untouched (TsBlocked is cdecl).
//
// [ThumbStall]
//   Enabled=0      1 = install
//   TimeoutMs=10000  how long one request may stay blocked in state 0x11 (1000..600000)
//   Action=1       0 = log only; 1 = abort the request (a missing portrait: asked for again later);
//                  2 = render anyway when the blocker is not one of the request's own models (request +0xA8..+0xAC),
//                      else abort. 2 rests on that list holding the scene objects themselves [inferred]: check the
//                      log's own= field in an Action=0 or 1 run first.
//                  3 = fallback "pick": the SimService works the blocking model's LOD build job next (below), until the
//                      request leaves state 0x11; still blocked a second TimeoutMs later = abort as 1
//                  4 = fallback "min LOD 0": set the blocking model's minimum LOD byte (handle +0x22) to 0; still blocked a
//                      second TimeoutMs later = abort as 1. Predicted by the reading below to change nothing.
//                  3 and 4 log "thumbstall: fallback #n start / rendered / aborted / other" lines with the counts.
//   PickMs=0       Action 3 only: start the pick after this many ms blocked instead of TimeoutMs (0 = TimeoutMs; at most
//                  TimeoutMs). The abort still comes TimeoutMs after the pick. A portrait of an unworn outfit always loses
//                  the camera pick, so waiting the full TimeoutMs first only delays it (2026-11-12 runs: rendered a median
//                  1.4 s after the pick, p90 2.2 s, after 3 s of waiting).
//   KeepBuilding=0 Action 3 only: at the abort point, a fallback whose LOD job is the one being built right now (SimService
//                  +0x68) gets another TimeoutMs instead, up to this many times (0..10). Both aborts of the 2026-11-12 runs
//                  (all20 / all27, Apex loaded) had their job taken and building when cut at 3 s.
//
// Where the LOD waits (2026-11-12, static, TS3W 1.67) [verified disassembly; th13 refuted the ActiveLODLimit reading: in
// use 12-54, never over]: the model handle's LOD provider (+0x3C) is the interface at +0x28 of a CAS SimService outfit
// object (interface vtables 0xFE49EC / 0xFE4AF0, object vtables 0xFE4A10 / 0xFE4B14). Its +0x10 (0x5E4EB0) calls
// SimService [0x11CE6A0] RequestSim 0x5EE910(lod, OBJECT = interface - 0x28, &out):
//   lod > [svc+0x34] (3) -> an empty LOD, yes; lod = max(lod, [svc+0x30] MinSimLOD)
//   [svc+0x4C] > [svc+0x40] (in use > ActiveLODLimit) and no better LOD in use for it -> no, nothing queued (not seen)
//   a built LOD in the cache for (lod, object) (0x5EDC90) -> yes
//   else 0x5EE770: a job for (lod, object) already in the +0x58 list, the +0x60 list or current (+0x68) gets its request
//   count (+0xC) raised (0x5EC7A0); none = a new 0x198-byte job appended to +0x58 -> no
// Jobs: +0x58 = loading, stepped in list order every SimService update (0x5F0E50) until their resources are in, then
// moved to +0x60 = ready. Only one job is built at a time (+0x68). When +0x68 is empty, 0x5EDB30 takes ONE ready job: the
// one whose object has an attached scene object (object +0x2A0 set, filled by the provider's attach +0x18) nearest the
// camera (Renderer +0x24 position vs scene object +0xB0); with no attached object anywhere, the first. A portrait of an
// unworn outfit has no world model on that object, so every job of a Sim nearer the camera goes first; at ultra the
// ready list grows (th13: 66 -> 251) and the portrait waits. A cancel (0x5EE9E0) removes a job only while its request
// count is 1, so jobs asked for every step stay until built. Action 3 hooks the one call of 0x5EDB30 (0x5F12A5) and takes
// the blocking object's ready job first (its wanted LOD if there, else any LOD of that object).
// Action 4 does not get past it: the pick in 0x6DBDE0 does take index 0 under min 0 when the distance falls in LOD 0's
// range, but RequestSim raises it to MinSimLOD (1 below SimQuality VeryHigh) and the job is still the same queue.
static const DWORD TS_SITE = 0x00D5A9C8, TS_KEEP = 0x00D5A9D2, TS_GO = 0x00D5A9DD, TS_ABORT = 0x00D5AA25;
static const u8 kTsOrig[10] = {0x8B, 0x54, 0x24, 0x60, 0x89, 0x97, 0x44, 0x02, 0x00, 0x00};
static const u8 kTsKeep[11] = {0x32, 0xC0, 0x5F, 0x5E, 0x5B, 0x8B, 0xE5, 0x5D, 0xC2, 0x04, 0x00};   // at TS_KEEP
static const u8 kTsAbort[11] = {0xB0, 0x01, 0x5F, 0x5E, 0x5B, 0x8B, 0xE5, 0x5D, 0xC2, 0x04, 0x00};  // at TS_ABORT
static const u8 kTsGo[5] = {0x80, 0x7C, 0x24, 0x17, 0x00};                                          // at TS_GO
static DWORD g_tsTimeoutMs = 10000;
static int g_tsAction = 1;
static DWORD g_tsPickMs;        // Action 3: blocked time before the pick (0 = TimeoutMs)
static int g_tsKeep;            // Action 3: extensions while the job is being built (0 = off)
static DWORD(WINAPI* g_tsNow)() = GetTickCount;  // selftest: a fake clock
static const u8* g_tsReq;       // the request being timed (only the front request is ever stepped)
static DWORD g_tsStamp;         // its creation stamp (+0x23C): tells a new request in the same deque slot apart
static DWORD g_tsSince;         // tick of its first blocked step
static LONG g_tsHits, g_tsAborts, g_tsForced;

// What the blocking model waits for: its model handle (+0x210, docs/186 10.3) word by word, and the first 16 words of
// whatever each slot points at (tag bits masked), when that memory is readable. Log only.
static void TsWords(char* out, size_t cap, const DWORD* w, int n) {
    size_t at = 0;
    out[0] = 0;
    for (int i = 0; i < n && at + 10 < cap; i++) at += (size_t)sprintf_s(out + at, cap - at, i ? " %08lX" : "%08lX", w[i]);
}

// ---- The SimService LOD builds (2026-11-12, see "Where the LOD waits" above). Plain reads under __try. ----
// prov below is the job's provider OBJECT (handle +0x3C minus 0x28), as RequestSim stores it in job +0x18.
static DWORD g_tsSvcAt = 0x011CE6A0;  // selftest: its own
static const DWORD TS_SVC_VT = 0x00FE514C;
struct TsLod {
    long inuse, limit, cached, minLod;  // [svc+0x4C], +0x40 (ActiveLODLimit), +0x50, +0x30 (MinSimLOD); -1 = no service
    int jobs, ready, current;           // build jobs in the +0x58 list (loading), the +0x60 list (ready), +0x68 set
    char job[72];                       // where the provider's job is
};
static void TsLodRead(const u8* prov, TsLod* o) {
    memset(o, 0, sizeof(*o));
    o->inuse = o->limit = o->cached = o->minLod = -1;
    strcpy_s(o->job, "none");
    __try {
        const u8* const svc = *(const u8* const*)(uintptr_t)g_tsSvcAt;
        if (!svc || !Readable(svc, 0x70) || *(const DWORD*)svc != TS_SVC_VT) { strcpy_s(o->job, "no service"); return; }
        o->minLod = *(const long*)(svc + 0x30), o->limit = *(const long*)(svc + 0x40);
        o->inuse = *(const long*)(svc + 0x4C), o->cached = *(const long*)(svc + 0x50);
        for (int l = 0; l < 2; l++) {  // job nodes (0x198 bytes): +0 next, +8 LOD, +0xC requests, +0x18 provider, +0x190 state
            const u8* const head = svc + (l ? 0x60 : 0x58);
            int pos = 0;
            for (const u8* n = *(const u8* const*)head; n && n != head && pos < 4096; n = *(const u8* const*)n, pos++)
                if (prov && *(const u8* const*)(n + 0x18) == prov && !strcmp(o->job, "none"))
                    sprintf_s(o->job, "%s #%d lod %ld requests %ld state %ld", l ? "ready" : "loading", pos,
                              *(const long*)(n + 8), *(const long*)(n + 0xC), *(const long*)(n + 0x190));
            if (l) o->ready = pos;
            else o->jobs = pos;
        }
        const u8* const cur = *(const u8* const*)(svc + 0x68);
        o->current = cur != 0;
        if (cur && prov && *(const u8* const*)(cur + 0x18) == prov)
            sprintf_s(o->job, "current lod %ld requests %ld state %ld", *(const long*)(cur + 8), *(const long*)(cur + 0xC),
                      *(const long*)(cur + 0x190));
    } __except (EXCEPTION_EXECUTE_HANDLER) { strcpy_s(o->job, "fault"); }
}
static DWORD TsVt(DWORD p) { return p && (p & 3) == 0 && Readable((const void*)(uintptr_t)p, 4) ? *(const DWORD*)(uintptr_t)p : 0; }
// the blocker's handle LOD words: current index +0x14, wanted index +0x18, min / max LOD bytes +0x22 / +0x23
static void TsLodIdx(const u8* blk, char* out, size_t cap) {
    if (!blk || !Readable(blk + 0x210, 0x40)) { strcpy_s(out, cap, "n/a"); return; }
    const u8* const h = blk + 0x210;
    sprintf_s(out, cap, "cur %ld wanted %ld min %u max %u", *(const long*)(h + 0x14), *(const long*)(h + 0x18), h[0x22], h[0x23]);
}
static void TsLodLog(const u8* blk) {
    DWORD prov = 0, h38 = 0;
    __try { if (blk && Readable(blk + 0x210, 0x40)) prov = *(const DWORD*)(blk + 0x24C), h38 = *(const DWORD*)(blk + 0x248); }
    __except (EXCEPTION_EXECUTE_HANDLER) {}
    TsLod L;
    TsLodRead(prov ? (const u8*)(uintptr_t)(prov - 0x28) : 0, &L);
    char idx[64];
    TsLodIdx(blk, idx, sizeof(idx));
    Log("thumbstall:   lod %s; provider %08lX vt=%08lX; handle+38 %08lX vt=%08lX | sim lods in use %ld limit %ld cached %ld "
        "minlod %ld | jobs loading %d ready %d current %d | provider job: %s",
        idx, prov, TsVt(prov), h38, TsVt(h38), L.inuse, L.limit, L.cached, L.minLod, L.jobs, L.ready, L.current, L.job);
}

// ---- Actions 3 and 4: the fallback ----
// Action 3: the one call of the ready-job picker, 0x5F12A5 `call 0x5EDB30` in SimService_Update (ecx = svc, result into
// svc+0x68), is re-pointed at TsPick. TsPick takes the blocking object's ready job when there is one (its wanted LOD if
// listed, else the first of that object) and unlinks it exactly as 0x5EDB30 unlinks its own choice; otherwise it calls
// 0x5EDB30. Same thread (main), same list, one job per call as before.
static const DWORD TS_PICK_AT = 0x005F12A3, TS_PICK = 0x005EDB30;
static const u8 kTsPickSite[12] = {0x8B, 0xCF, 0xE8, 0x86, 0xC8, 0xFF, 0xFF, 0x85, 0xC0, 0x89, 0x47, 0x68};  // mov ecx,edi; call; test; mov [edi+68h],eax
// The manager's Update (vtable 0x1088F2C +0x1C = 0xD61670, thiscall, 2 arguments) steps the front request through
// 0xD5C6D0 -> 0xD59C10. Its slot is swapped so the fallback sees, after each step, whether the request left state 0x11.
static const DWORD TS_TM_SLOT = 0x01088F48, TS_TM_UPDATE = 0x00D61670;
static volatile LONG g_tsPickObj;  // Action 3: the provider object whose ready job goes first (0 = none)
static volatile LONG g_tsPickLod;  // its wanted LOD, raised to MinSimLOD
static LONG g_tsPicks;             // jobs TsPick took this way (all fallbacks)
static int g_tsFbPicked;           // this fallback: the job's place in the ready list when taken, -1 = not taken
static const u8* g_tsFbReq;        // the request under a fallback (0 = none) and its stamp, start tick, model, key
static DWORD g_tsFbStamp, g_tsFbSince;
static u8* g_tsFbBlk;
static char g_tsFbKey[40];
static LONG g_tsFbN, g_tsFbRendered, g_tsFbAborted, g_tsFbOther, g_tsFbKeptAll;
static int g_tsFbKept;             // this fallback: KeepBuilding extensions used
static bool g_tsPickPatched, g_tsTmHooked;

// KeepBuilding: is the SimService's current job (+0x68, the one being built) a job of this object?
static bool TsBuilding(LONG obj) {
    __try {
        const u8* const svc = *(const u8* const*)(uintptr_t)g_tsSvcAt;
        if (!obj || !svc || *(const DWORD*)svc != TS_SVC_VT) return false;
        const u8* const cur = *(const u8* const*)(svc + 0x68);
        return cur && *(const LONG*)(cur + 0x18) == obj;
    } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}

typedef u8*(__fastcall* TsPickFn)(u8* svc);
static TsPickFn g_tsPickOrig = (TsPickFn)(uintptr_t)TS_PICK;  // selftest: its own
static u8* __fastcall TsPick(u8* svc) {
    const LONG obj = g_tsPickObj;
    if (obj) {
        __try {
            u8* const head = svc + 0x60;
            u8* best = 0;
            int pos = 0, bestPos = -1;
            for (u8* n = *(u8**)head; n && n != head && pos < 4096; n = *(u8**)n, pos++)
                if (*(const LONG*)(n + 0x18) == obj && (!best || (*(const LONG*)(best + 8) != g_tsPickLod && *(const LONG*)(n + 8) == g_tsPickLod)))
                    best = n, bestPos = pos;
            if (best) {
                u8* const prev = *(u8**)(best + 4);
                u8* const next = *(u8**)best;
                *(u8**)prev = next;
                *(u8**)(next + 4) = prev;
                InterlockedIncrement(&g_tsPicks);
                if (g_tsFbPicked < 0) g_tsFbPicked = bestPos;
                return best;
            }
        } __except (EXCEPTION_EXECUTE_HANDLER) {}
    }
    return g_tsPickOrig(svc);
}

// how: 0 rendered (the same request moved past 0x11: below 0x19, or 0x19.. with its LOD built), 1 aborted by us, 2 other
// (it left the front, or reached 0x19.. without a LOD: cancelled by its owner)
static void TsFbEnd(int how, DWORD state, bool blkLive) {
    InterlockedExchange(&g_tsPickObj, 0);
    if (!g_tsFbReq) return;
    if (how == 0) g_tsFbRendered++;
    else if (how == 1) g_tsFbAborted++;
    else g_tsFbOther++;
    char idx[64];
    if (blkLive) TsLodIdx(g_tsFbBlk, idx, sizeof(idx));
    else strcpy_s(idx, "n/a (request gone)");
    Log("thumbstall: fallback #%ld %s after %lu ms (state %02lX): key %s; lod %s; its job %s %d "
        "(fallback_rendered %ld, fallback_aborted %ld, fallback_other %ld) kept %d",
        g_tsFbN, how == 0 ? "rendered" : how == 1 ? "aborted" : "other", g_tsNow() - g_tsFbSince, state, g_tsFbKey, idx,
        g_tsFbPicked >= 0 ? "taken first from ready place" : "not taken, ready place", g_tsFbPicked,
        g_tsFbRendered, g_tsFbAborted, g_tsFbOther, g_tsFbKept);
    g_tsFbReq = 0;
}
static void TsFbStart(const u8* req, DWORD stamp, u8* blk, DWORD now) {
    TsFbEnd(2, 0, false);  // a fallback still open for another request
    g_tsFbReq = req, g_tsFbStamp = stamp, g_tsFbSince = now, g_tsFbBlk = blk, g_tsFbPicked = -1, g_tsFbKept = 0;
    g_tsFbN++;
    TsWords(g_tsFbKey, sizeof(g_tsFbKey), (const DWORD*)(req + 0x110), 4);
    char before[64], after[64];
    TsLodIdx(blk, before, sizeof(before));
    const DWORD prov = blk && Readable(blk + 0x210, 0x40) ? *(const DWORD*)(blk + 0x24C) : 0;
    if (g_tsAction == 3 && prov) {
        TsLod L;
        TsLodRead(0, &L);
        const long wanted = *(const long*)(blk + 0x210 + 0x18);
        InterlockedExchange(&g_tsPickLod, wanted < L.minLod ? L.minLod : wanted);
        InterlockedExchange(&g_tsPickObj, (LONG)(prov - 0x28));
    }
    if (g_tsAction == 4 && blk && Readable(blk + 0x232, 1)) blk[0x232] = 0;  // min LOD byte; 0xD5A1C1 set it to 1
    TsLodIdx(blk, after, sizeof(after));
    Log("thumbstall: fallback #%ld start (%s): request %p key %s; lod %s -> %s; provider %08lX", g_tsFbN,
        g_tsAction == 3 ? (prov ? "its ready LOD job goes first" : "no provider: nothing to pick") : "min LOD 0", req,
        g_tsFbKey, before, after, prov);
}
// After each manager step (main thread): has the request under a fallback left state 0x11?
static void TsFbWatch(const u8* mgr) {
    bool same = false;
    DWORD st = 0;
    __try {
        const u8* const front = *(const u8* const*)(mgr + 0x100);
        same = front && front != *(const u8* const*)(mgr + 0x110) && front == g_tsFbReq && *(const DWORD*)(front + 0x23C) == g_tsFbStamp;
        if (same) st = *(const DWORD*)(front + 0x108);
        // 0x19.. with a current LOD (handle +0x14 >= 0) is a render too (th15: 0x1B with "lod cur 1" after the pick)
        if (same && st >= 0x19 && (!g_tsFbBlk || *(const long*)(g_tsFbBlk + 0x210 + 0x14) < 0)) st |= 0x100;  // no LOD: other
    } __except (EXCEPTION_EXECUTE_HANDLER) { same = false; }
    if (same && st == 0x11) return;
    TsFbEnd(same && st > 0x11 && st < 0x100 ? 0 : 2, st & 0xFF, same);
}
typedef DWORD(__fastcall* TsTmUpdateFn)(void* self, void* edx, DWORD a, DWORD b);
static TsTmUpdateFn g_tsTmOrig;
static DWORD __fastcall TsTmUpdate(void* self, void* edx, DWORD a, DWORD b) {
    const DWORD r = g_tsTmOrig(self, edx, a, b);
    if (g_tsFbReq) TsFbWatch((const u8*)self);
    return r;
}

static void TsDump(const u8* req, const u8* blk) {
    char line[16 * 9 + 8 * 9 + 64];
    __try {
        TsWords(line, sizeof(line), (const DWORD*)(req + 0x110), 4);
        Log("thumbstall:   request key %s", line);
        TsLodLog(blk);
        if (!blk || !Readable(blk + 0x210, 0x74)) return;
        const DWORD* const h = (const DWORD*)(blk + 0x210);
        TsWords(line, sizeof(line), h, 16);
        Log("thumbstall:   handle +00 %s", line);
        TsWords(line, sizeof(line), h + 16, 13);
        Log("thumbstall:   handle +40 %s", line);
        static const int kSlots[6] = {0x00, 0x04, 0x08, 0x0C, 0x10, 0x44};
        for (int i = 0; i < 6; i++) {
            const DWORD raw = *(const DWORD*)((const u8*)h + kSlots[i]);
            const DWORD* const p = (const DWORD*)(uintptr_t)(raw & ~3u);
            if (!p || !Readable(p, 0x60)) continue;
            TsWords(line, sizeof(line), p, 24);
            Log("thumbstall:   slot +%02X raw %08lX -> %s", kSlots[i], raw, line);
        }
        // the CAS texture compositor queue [0x11CEB88] and material compositor [0x11CEB8C] (docs/42 section 3): their first
        // 32 words, so container begin/end pairs can be compared between two stall lines (growing, flat or empty)
        static const DWORD kGlobals[2] = {0x011CEB88, 0x011CEB8C};
        for (int i = 0; i < 2; i++) {
            const DWORD* const g = *(const DWORD* const*)(uintptr_t)kGlobals[i];
            if (!g || !Readable(g, 0x80)) continue;
            TsWords(line, sizeof(line), g, 16);
            Log("thumbstall:   [%08lX] %p +00 %s", kGlobals[i], g, line);
            TsWords(line, sizeof(line), g + 16, 16);
            Log("thumbstall:   [%08lX] %p +40 %s", kGlobals[i], g, line);
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {}
}

// Called from the stub on the main thread, once per blocked step of state 0x11. Returns 0 keep waiting, 1 abort, 2 render.
static int __cdecl TsBlocked(const u8* req) {
    const DWORD now = g_tsNow();
    int act = 0;
    __try {
        const DWORD stamp = *(const DWORD*)(req + 0x23C);
        if (req != g_tsReq || stamp != g_tsStamp) { g_tsReq = req, g_tsStamp = stamp, g_tsSince = now; return 0; }
        // Actions 3 and 4: the first timeout starts the fallback, a second one for the same request aborts it
        const bool fbSecond = g_tsAction >= 3 && g_tsFbReq == req && g_tsFbStamp == stamp;
        if (now - g_tsSince < (g_tsAction == 3 && !fbSecond && g_tsPickMs ? g_tsPickMs : g_tsTimeoutMs)) return 0;
        if (fbSecond && g_tsAction == 3 && g_tsFbKept < g_tsKeep && TsBuilding(g_tsPickObj)) {
            g_tsFbKept++, g_tsSince = now;
            const LONG k = InterlockedIncrement(&g_tsFbKeptAll);
            if (k <= 200 || (k & 63) == 0)
                Log("thumbstall: fallback #%ld kept %d of %d: its LOD job is the one being built, %lu ms after the pick", g_tsFbN,
                    g_tsFbKept, g_tsKeep, now - g_tsFbSince);
            return 0;
        }
        const u8* const blk = *(const u8* const*)(req + 0x244);
        const u8* const* m = *(const u8* const* const*)(req + 0xA8);
        const u8* const* const mEnd = *(const u8* const* const*)(req + 0xAC);
        bool own = false;
        const size_t models = m && mEnd >= m ? (size_t)(mEnd - m) : 0;
        for (size_t i = 0; i < models && i < 64; i++) own = own || m[i] == blk;
        act = g_tsAction >= 3 ? (fbSecond ? 1 : 0) : g_tsAction == 2 ? (own ? 1 : 2) : g_tsAction;
        const LONG n = InterlockedIncrement(&g_tsHits);
        if (act == 1) InterlockedIncrement(&g_tsAborts);
        if (act == 2) InterlockedIncrement(&g_tsForced);
        if (n <= 200 || (n & 63) == 0)
            Log("thumbstall: #%ld %s after %lu ms: request %p type=%08lX blocker %p vt=%08lX flags=%08lX layers=%08lX "
                "ms5E=%u own=%d models=%u (aborted %ld, rendered %ld)",
                n, act == 1 ? "abort" : act == 2 ? "render" : g_tsAction >= 3 ? "fallback" : "seen", now - g_tsSince, req,
                *(const DWORD*)(req + 0x118), blk,
                blk ? *(const DWORD*)blk : 0, blk ? *(const DWORD*)(blk + 0x40) : 0, blk ? *(const DWORD*)(blk + 0x44) : 0,
                blk ? (unsigned)blk[0x210 + 0x5E] : 0, (int)own, (unsigned)models, g_tsAborts, g_tsForced);
        if (n <= 200 || (n & 63) == 0) TsDump(req, blk);
        if (fbSecond) TsFbEnd(1, 0x11, true);
        else if (g_tsAction >= 3) TsFbStart(req, stamp, const_cast<u8*>(blk), now);
        g_tsSince = now;  // Action 0: one line per timeout period; 1 and 2: the request leaves state 0x11 now; 3, 4: from the fallback
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0;
    }
    return act;
}

static __declspec(naked) void TsStub() {
    __asm {
        mov edx, [esp + 0x60]
        mov [edi + 0x244], edx
        push edi
        call TsBlocked
        add esp, 4
        cmp eax, 1
        je ts_abort
        cmp eax, 2
        je ts_go
        mov eax, 0D5A9D2h
        jmp eax
    ts_abort:
        mov dword ptr [edi + 0x108], 0x19
        mov eax, 0D5AA25h
        jmp eax
    ts_go:
        mov dword ptr [edi + 0x244], 0
        mov eax, 0D5A9DDh
        jmp eax
    }
}

// Put the original bytes back on detach (only if our jump is still there).
static bool g_tsInstalled;
static void TsRevert() {
    InterlockedExchange(&g_tsPickObj, 0);
    u8* const site = (u8*)(uintptr_t)TS_SITE;
    if (g_tsInstalled && Readable(site, 1) && *site == 0xE9) Write(site, kTsOrig, sizeof(kTsOrig));
    g_tsInstalled = false;
    if (g_tsPickPatched) {
        u8* const pick = (u8*)(uintptr_t)TS_PICK_AT;
        const DWORD ours = (DWORD)(uintptr_t)&TsPick - (TS_PICK_AT + 2 + 5);
        if (Readable(pick, sizeof(kTsPickSite)) && *(const DWORD*)(pick + 3) == ours) Write(pick, kTsPickSite, sizeof(kTsPickSite));
        g_tsPickPatched = false;
    }
    if (g_tsTmHooked) {
        void** const slot = (void**)(uintptr_t)TS_TM_SLOT;
        if (Readable(slot, 4) && *slot == (void*)&TsTmUpdate) Write(slot, &g_tsTmOrig, 4);
        g_tsTmHooked = false;
    }
}

// Action 3 / 4 extras; false = not installed (the caller falls back to Action 1)
static bool TsInstallFallback() {
    void** const slot = (void**)(uintptr_t)TS_TM_SLOT;
    if (!Readable(slot, 4) || *slot != (void*)(uintptr_t)TS_TM_UPDATE) {
        Log("SKIP thumb stall fallback: manager Update slot 0x%08X does not hold 0x%08X", TS_TM_SLOT, TS_TM_UPDATE);
        return false;
    }
    if (g_tsAction == 3) {
        u8* const site = (u8*)(uintptr_t)TS_PICK_AT;
        if (!Matches((u8*)(uintptr_t)TS_PICK_AT, kTsPickSite, sizeof(kTsPickSite))) {
            Log("SKIP thumb stall fallback: the ready-job pick at 0x%08X differs", TS_PICK_AT + 2);
            return false;
        }
        u8 call[5] = {0xE8, 0, 0, 0, 0};
        const DWORD rel = (DWORD)(uintptr_t)&TsPick - (TS_PICK_AT + 2 + 5);
        memcpy(call + 1, &rel, 4);
        if (!Write(site + 2, call, 5)) { Log("FAIL thumb stall fallback: cannot write 0x%08X", TS_PICK_AT + 2); return false; }
        g_tsPickPatched = true;
    }
    g_tsTmOrig = (TsTmUpdateFn)*slot;
    const void* const hook = (const void*)&TsTmUpdate;
    if (!Write(slot, &hook, 4)) {
        Log("FAIL thumb stall fallback: cannot write 0x%08X", TS_TM_SLOT);
        TsRevert();
        return false;
    }
    g_tsTmHooked = true;
    return true;
}

static void ApplyThumbStall(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"ThumbStall", L"Enabled", 0, ini)) return;
    const int t = GetPrivateProfileIntW(L"ThumbStall", L"TimeoutMs", 10000, ini), a = GetPrivateProfileIntW(L"ThumbStall", L"Action", 1, ini);
    g_tsTimeoutMs = (DWORD)(t < 1000 ? 1000 : t > 600000 ? 600000 : t);
    g_tsAction = a < 0 ? 0 : a > 4 ? 4 : a;
    const int pm = GetPrivateProfileIntW(L"ThumbStall", L"PickMs", 0, ini), kb = GetPrivateProfileIntW(L"ThumbStall", L"KeepBuilding", 0, ini);
    g_tsPickMs = pm <= 0 ? 0 : (DWORD)pm < g_tsTimeoutMs ? (DWORD)pm : g_tsTimeoutMs;
    g_tsKeep = kb < 0 ? 0 : kb > 10 ? 10 : kb;
    u8* const site = (u8*)(uintptr_t)TS_SITE;
    if (!Matches((u8*)(uintptr_t)TS_SITE, kTsOrig, sizeof(kTsOrig)) || !Matches((u8*)(uintptr_t)TS_KEEP, kTsKeep, sizeof(kTsKeep)) ||
        !Matches((u8*)(uintptr_t)TS_ABORT, kTsAbort, sizeof(kTsAbort)) || !Matches((u8*)(uintptr_t)TS_GO, kTsGo, sizeof(kTsGo))) {
        Log("SKIP thumb stall: bytes at 0x%08X / 0x%08X / 0x%08X / 0x%08X differ: not installed", TS_SITE, TS_KEEP, TS_GO, TS_ABORT);
        return;
    }
    if (g_tsAction >= 3 && !TsInstallFallback()) {
        Log("thumb stall: action %d not available, action 1 instead", g_tsAction);
        g_tsAction = 1;
    }
    u8 patch[10] = {0xE9, 0, 0, 0, 0, 0x90, 0x90, 0x90, 0x90, 0x90};
    const DWORD rel = (DWORD)(uintptr_t)&TsStub - (TS_SITE + 5);
    memcpy(patch + 1, &rel, 4);
    if (!Write(site, patch, sizeof(patch))) { Log("FAIL thumb stall: cannot write 0x%08X", TS_SITE); TsRevert(); return; }
    g_tsInstalled = true;
    Log("OK   thumb stall (EXPERIMENT, [ThumbStall]): state 0x11 of the Sim thumbnail queue, timeout %lu ms, action %d "
        "(0 log, 1 abort the request, 2 render when the blocker is not its own model, 3 build its LOD job first "
        "then abort, 4 min LOD 0 then abort); action 3: pick after %lu ms, keep a building job up to %d more timeouts",
        g_tsTimeoutMs, g_tsAction, g_tsPickMs ? g_tsPickMs : g_tsTimeoutMs, g_tsKeep);
}

// ---- [ThumbCallers] (2026-11-12): who asks for thumbnails. Log only, off by default. ----
// Detours on the two enqueue entries a Sim portrait can come through [verified callers, TS3W 1.67]:
//   0xD614E0  household-Sim request (thiscall, manager in ecx; stack: +4 key pointer, +0x14 size mask, +0x24 notifier
//             = waited when not 0). Callers: IC ThumbnailManager_GenerateHouseholdSimThumbnail 0x778793 (no wait) and
//             0x778832 (wait).
//   0xD5C830  UI placeholder + request. Callers: IC UIManager_GetThumbnailImageImpl 0x782E5E, GetCASThumbnailImageImpl
//             0x782F78, and 0x7831E9 / 0x7832DE.
// Each call is filed under (entry, return address, script stack): the script stack is the interpreter frame chain of the
// calling thread (FbStackText, innermost first; empty off a script thread). Lines:
//   thumbcallers: new #i ...      first time a caller is seen
//   thumbcallers: req #i ...      every request: key words (household entry), waited, resource cache MB / entries at that moment
//   thumbcallers: top ...         every 30 s, the table by count
// [ThumbCallers] Enabled=0; LogEach=1 (0 = no per-request lines); Builds=1 (0 = portraits only).
static const DWORD TC_HH = 0x00D614E0, TC_UI = 0x00D5C830;
static const u8 kTcHhPro[6] = {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF8};  // push ebp; mov ebp,esp; and esp,-8
static const u8 kTcUiPro[6] = {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0};  // push ebp; mov ebp,esp; and esp,-16
static void* g_tcOrig[4];
// Also filed (2026-11-12, the same table): who builds Sim models. The registry of part compositors grows with every
// SimBuilder build (castrim.cpp), and th4 had 5,740 model-builder releases against a few hundred portraits.
//   0x5A80A0  IC CASUtils_SimBuilderBuildModelImpl (cdecl: +4 builder handle, +8 detail level). Its first 9 bytes are four
//             pushes and a call, so it gets its own stub (TcStubBuild) instead of a trampoline.
//   0x5A9E90  IC CASUtils_SetOutfitInGameObjectDelayedImpl (the live outfit swap).
static const DWORD TC_BUILD = 0x005A80A0, TC_SWAP = 0x005A9E90;
static const u8 kTcBuildPro[9] = {0x53, 0x55, 0x56, 0x57, 0xE8, 0xC7, 0x43, 0xEA, 0xFF};  // push ebx,ebp,esi,edi; call 0x44C470
static const u8 kTcSwapPro[7] = {0x83, 0xEC, 0x10, 0x8B, 0x44, 0x24, 0x1C};                // sub esp,10h; mov eax,[esp+1Ch]
static const char* const kTcName[4] = {"household 0xD614E0", "ui 0xD5C830", "buildmodel 0x5A80A0", "outfitswap 0x5A9E90"};
static bool g_tcLogEach = true;
struct TcRow { DWORD hash; LONG n, waited; char text[400]; };
static TcRow g_tcRows[64];
static volatile LONG g_tcLock, g_tcTotal, g_tcLost;
static DWORD g_tcTopAt;

// resource cache held bytes and entry count: the reader of memdiag.cpp's MdRc (manager [0x11C78D8], cache array +0xA0/+0xA4
// of {cache, priority}, cache vtable 0xFFDD94, +0x94 held, +0x28 entries)
static void TcRc(DWORD* held, DWORD* n) {
    *held = *n = 0;
    __try {
        const u8* const m = *(const u8* const*)(uintptr_t)0x011C78D8;
        if (!m || (*(const DWORD*)m != 0xFFE250 && *(const DWORD*)m != 0xFB2DA0)) return;
        const DWORD* const b = *(const DWORD* const*)(m + 0xA0);
        const DWORD* const e = *(const DWORD* const*)(m + 0xA4);
        for (int i = 0; b + 2 * i < e && i < 16; i++) {
            const u8* const c = (const u8*)(uintptr_t)b[2 * i];
            if (c && *(const DWORD*)c == 0xFFDD94 && b[2 * i + 1] == 0) { *held = *(const DWORD*)(c + 0x94), *n = *(const DWORD*)(c + 0x28); return; }
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {}
}

static void __cdecl TcSeen(int which, const DWORD* sp) {
    const DWORD ret = sp[0];
    char st[300];
    if (FbStackText(st, sizeof(st), 6) <= 0) strcpy_s(st, "(no script frames)");
    DWORD h = 2166136261u ^ (DWORD)which;
    if (which < 2) h = (h ^ ret) * 16777619u;  // an IC's own return address is the interpreter: not part of the key
    for (const char* c = st; *c; c++) h = (h ^ (u8)*c) * 16777619u;
    if (!h) h = 1;
    bool waited = false;
    DWORD key[4] = {};
    if (which == 0) __try {
        waited = sp[9] != 0;
        if (sp[1] && Readable((const void*)(uintptr_t)sp[1], 16)) memcpy(key, (const void*)(uintptr_t)sp[1], 16);
    } __except (EXCEPTION_EXECUTE_HANDLER) {}
    const LONG total = InterlockedIncrement(&g_tcTotal);
    while (InterlockedExchange(&g_tcLock, 1)) Sleep(0);
    int at = -1;
    bool isNew = false;
    for (int i = 0; i < 64 && at < 0; i++) {
        if (g_tcRows[i].hash == h) at = i;
        else if (!g_tcRows[i].hash) {
            at = i, isNew = true;
            g_tcRows[i].hash = h;
            sprintf_s(g_tcRows[i].text, "%s ret %08lX: %s", kTcName[which & 3], ret, st);
        }
    }
    LONG n = 0;
    if (at >= 0) {
        n = ++g_tcRows[at].n;
        if (waited) g_tcRows[at].waited++;
    } else g_tcLost++;
    InterlockedExchange(&g_tcLock, 0);
    if (isNew) Log("thumbcallers: new #%d %s", at, g_tcRows[at].text);
    DWORD held = 0, rcN = 0;
    TcRc(&held, &rcN);
    DWORD reg = 0;  // CAS part-compositor registry entries (castrim.cpp: service [0x11CE6A0] +0x110)
    __try { const u8* const svc = *(const u8* const*)(uintptr_t)0x011CE6A0; if (svc) reg = *(const DWORD*)(svc + 0x110); } __except (EXCEPTION_EXECUTE_HANDLER) {}
    if (g_tcLogEach && which < 2)
        Log("thumbcallers: req #%d n=%ld total=%ld key %08lX %08lX %08lX %08lX waited=%d rc=%.1f MB rcN=%lu casReg=%lu", at, n, total, key[0], key[1], key[2],
            key[3], (int)waited, held / 1048576.0, rcN, reg);
    const DWORD now = GetTickCount();
    if (now - g_tcTopAt >= 30000) {
        g_tcTopAt = now;
        for (int i = 0; i < 64 && g_tcRows[i].hash; i++)
            Log("thumbcallers: top #%d n=%ld waited=%ld %s", i, g_tcRows[i].n, g_tcRows[i].waited, g_tcRows[i].text);
        Log("thumbcallers: top total=%ld unfiled=%ld rc=%.1f MB rcN=%lu casReg=%lu", total, g_tcLost, held / 1048576.0, rcN, reg);
    }
}

static __declspec(naked) void TcStubHh() {
    __asm {
        pushad
        pushfd
        lea eax, [esp + 36]
        push eax
        push 0
        call TcSeen
        add esp, 8
        popfd
        popad
        jmp dword ptr [g_tcOrig]
    }
}
static __declspec(naked) void TcStubUi() {
    __asm {
        pushad
        pushfd
        lea eax, [esp + 36]
        push eax
        push 1
        call TcSeen
        add esp, 8
        popfd
        popad
        jmp dword ptr [g_tcOrig + 4]
    }
}

static __declspec(naked) void TcStubBuild() {
    __asm {
        pushad
        pushfd
        lea eax, [esp + 36]
        push eax
        push 2
        call TcSeen
        add esp, 8
        popfd
        popad
        push ebx
        push ebp
        push esi
        push edi
        mov eax, 0044C470h
        call eax
        push 005A80A9h
        ret
    }
}
static __declspec(naked) void TcStubSwap() {
    __asm {
        pushad
        pushfd
        lea eax, [esp + 36]
        push eax
        push 3
        call TcSeen
        add esp, 8
        popfd
        popad
        jmp dword ptr [g_tcOrig + 12]
    }
}

static void ApplyThumbCallers(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"ThumbCallers", L"Enabled", 0, ini)) return;
    g_tcLogEach = GetPrivateProfileIntW(L"ThumbCallers", L"LogEach", 1, ini) != 0;
    g_tcTopAt = GetTickCount();
    InstallDetour("ThumbHouseholdSimRequest", TC_HH, kTcHhPro, sizeof(kTcHhPro), (void*)TcStubHh, &g_tcOrig[0]);
    InstallDetour("ThumbUiRequest", TC_UI, kTcUiPro, sizeof(kTcUiPro), (void*)TcStubUi, &g_tcOrig[1]);
    if (GetPrivateProfileIntW(L"ThumbCallers", L"Builds", 1, ini)) {
        InstallDetour("OutfitSwapRequest", TC_SWAP, kTcSwapPro, sizeof(kTcSwapPro), (void*)TcStubSwap, &g_tcOrig[3]);
        u8* const b = (u8*)(uintptr_t)TC_BUILD;
        if (!Matches((u8*)(uintptr_t)TC_BUILD, kTcBuildPro, sizeof(kTcBuildPro))) Log("SKIP thumb callers: SimBuilderBuildModel 0x%08X: bytes differ", TC_BUILD);
        else {
            u8 patch[9] = {0xE9, 0, 0, 0, 0, 0x90, 0x90, 0x90, 0x90};
            const DWORD rel = (DWORD)(uintptr_t)&TcStubBuild - (TC_BUILD + 5);
            memcpy(patch + 1, &rel, 4);
            if (Write(b, patch, sizeof(patch))) Log("OK   thumb callers: SimBuilderBuildModel 0x%08X patched", TC_BUILD);
        }
    }
    Log("thumb callers (LOG ONLY, [ThumbCallers]): lines \"thumbcallers:\"; per-request lines %s", g_tcLogEach ? "on" : "off");
}
