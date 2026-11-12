// [ThumbStall] (notes: re-thumb-1111, run th1 of 2026-11-11). Included by ts3native.cpp (one translation unit).
// EXPERIMENT, off by default. Not run in game yet. No counters in Ts3nCounters: results are the "thumbstall:" log lines and
// the agent's thumb_census (tm_done moves again, tm_front_age_ms stays under the timeout).
//
// What: UI/ThumbnailManager works one request at a time (the front of InProgressRequests, state machine 0xD59C10 stepped
// once per frame on the main thread by 0xD5C6D0). Its state 0x11 calls the "scene loaded" test 0x6F3DC0 at 0xD5A9BF with
// layer mask 0x7FFFF000 (0x7FDFF000 for one request kind) and flag mask 0x42: while ANY scene object in those layers has a
// pending texture composite (object +0x40 bit 2 or 0x40, computed by 0x6F7B40 from the material state at +0x210), the
// request returns "come back next frame". There is no timeout. Measured (th1): at game speed ultra the front request stayed
// in state 0x11 for 105 s, nothing finished, 110 requests and 14 pack-away tasks queued behind it [verified run; the cause
// of the never-clearing flag is open].
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
//                  3 = fallback "lift": exempt the blocking model's LOD provider from EA's ActiveLODLimit test (below) until
//                      the request leaves state 0x11; still blocked a second TimeoutMs later = abort as 1
//                  4 = fallback "min LOD 0": set the blocking model's minimum LOD byte (handle +0x22) to 0; still blocked a
//                      second TimeoutMs later = abort as 1. Predicted by the reading below to change nothing.
//                  3 and 4 log "thumbstall: fallback #n start / rendered / aborted / other" lines with the counts.
//
// Why the provider refuses (2026-11-12, static, TS3W 1.67) [verified disassembly; the run decides]: the model handle's LOD
// provider (+0x3C) is a CAS SimService outfit object (vtables 0xFE49EC / 0xFE4AF0; the provider interface sits at +0x28 of
// the object whose own vtable is 0xFE4A10 / 0xFE4B14). Its +0x10 (0x5E4EB0) is SimService [0x11CE6A0] RequestSim 0x5EE910
// (lod, provider, &out):
//   lod > [svc+0x34] (3)  -> an empty LOD, yes
//   lod = max(lod, [svc+0x30] MinSimLOD)
//   [svc+0x4C] > [svc+0x40] (0x5EE985) and lod < the provider's best LOD in use (100000 when it has none, 0x5EDDA0)
//                          -> NO, and nothing is queued
//   a built LOD in the cache (0x5EDC90) -> yes; else queue a build job (0x5EE770: the same limit test at 0x5EE774, a dedupe
//                          0x5EC7A0, then appended to the +0x58 list) -> no
// [svc+0x40] is ActiveLODLimit (Config, GraphicsRules.sgr: 200 at every SimQuality; 0x5EC51B / 0x5EC543). [svc+0x4C] is
// recounted on every SimService update (0x5F0820 from 0x5F0E50): cached LODs that something outside the cache holds. So
// while more than 200 Sim LODs are in use, no LOD is built for a model that has none in use; a portrait of a Sim whose
// world model is gone (the pack-away destroys it first) cannot start until the count falls. Action 4 does not get past it:
// the pick in 0x6DBDE0 does take index 0 under min 0 when the distance falls in LOD 0's range, but RequestSim raises it
// to MinSimLOD (1 below SimQuality VeryHigh) and the limit test refuses any index for a provider with no LOD in use.
static const DWORD TS_SITE = 0x00D5A9C8, TS_KEEP = 0x00D5A9D2, TS_GO = 0x00D5A9DD, TS_ABORT = 0x00D5AA25;
static const u8 kTsOrig[10] = {0x8B, 0x54, 0x24, 0x60, 0x89, 0x97, 0x44, 0x02, 0x00, 0x00};
static const u8 kTsKeep[11] = {0x32, 0xC0, 0x5F, 0x5E, 0x5B, 0x8B, 0xE5, 0x5D, 0xC2, 0x04, 0x00};   // at TS_KEEP
static const u8 kTsAbort[11] = {0xB0, 0x01, 0x5F, 0x5E, 0x5B, 0x8B, 0xE5, 0x5D, 0xC2, 0x04, 0x00};  // at TS_ABORT
static const u8 kTsGo[5] = {0x80, 0x7C, 0x24, 0x17, 0x00};                                          // at TS_GO
static DWORD g_tsTimeoutMs = 10000;
static int g_tsAction = 1;
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

// ---- The SimService LOD builds (2026-11-12, see "Why the provider refuses" above). Plain reads under __try. ----
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
    TsLodRead((const u8*)(uintptr_t)prov, &L);
    char idx[64];
    TsLodIdx(blk, idx, sizeof(idx));
    Log("thumbstall:   lod %s; provider %08lX vt=%08lX; handle+38 %08lX vt=%08lX | sim lods in use %ld limit %ld cached %ld "
        "minlod %ld | jobs loading %d ready %d current %d | provider job: %s",
        idx, prov, TsVt(prov), h38, TsVt(h38), L.inuse, L.limit, L.cached, L.minLod, L.jobs, L.ready, L.current, L.job);
}

// ---- Actions 3 and 4: the fallback ----
// Action 3: two 6-byte calls replace EA's ActiveLODLimit tests, `mov eax,[svc+4Ch]; cmp eax,[svc+40h]` (svc in esi in
// RequestSim, in ebp in the enqueue), so the following jle gets "in use <= limit" for the exempt provider only. Both sites
// have the provider at [esp+10h] (two pushes after entry), [esp+14h] inside the stub. ecx and edx are untouched (ecx is
// live at both sites); the flags come from the stub's own cmp, and ret leaves them.
static const DWORD TS_LIM_REQ = 0x005EE985, TS_LIM_ENQ = 0x005EE774;
static const u8 kTsLimReq[16] = {0x8B, 0x46, 0x4C, 0x3B, 0x46, 0x40, 0x53, 0x8B, 0x5C, 0x24, 0x14, 0x57, 0x8B, 0x39, 0x7E, 0x0C};
static const u8 kTsLimEnq[13] = {0x8B, 0x45, 0x4C, 0x3B, 0x45, 0x40, 0x57, 0x8B, 0x7C, 0x24, 0x14, 0x7E, 0x14};
// The manager's Update (vtable 0x1088F2C +0x1C = 0xD61670, thiscall, 2 arguments) steps the front request through
// 0xD5C6D0 -> 0xD59C10. Its slot is swapped so the fallback sees, after each step, whether the request left state 0x11.
static const DWORD TS_TM_SLOT = 0x01088F48, TS_TM_UPDATE = 0x00D61670;
static volatile LONG g_tsExProv;  // the provider exempt from the limit tests (Action 3), 0 = none
static volatile LONG g_tsExUses;  // limit tests it passed that way
static const u8* g_tsFbReq;       // the request under a fallback (0 = none) and its stamp, start tick, model, key
static DWORD g_tsFbStamp, g_tsFbSince;
static LONG g_tsFbUses0;
static u8* g_tsFbBlk;
static char g_tsFbKey[40];
static LONG g_tsFbN, g_tsFbRendered, g_tsFbAborted, g_tsFbOther;
static bool g_tsLimPatched, g_tsTmHooked;

static __declspec(naked) void TsLimReqStub() {
    __asm {
        mov eax, [esp + 0x14]
        test eax, eax
        jz req_test
        cmp eax, g_tsExProv
        jne req_test
        lock inc dword ptr g_tsExUses
        xor eax, eax
        cmp eax, 1
        ret
    req_test:
        mov eax, [esi + 0x4C]
        cmp eax, [esi + 0x40]
        ret
    }
}
static __declspec(naked) void TsLimEnqStub() {
    __asm {
        mov eax, [esp + 0x14]
        test eax, eax
        jz enq_test
        cmp eax, g_tsExProv
        jne enq_test
        lock inc dword ptr g_tsExUses
        xor eax, eax
        cmp eax, 1
        ret
    enq_test:
        mov eax, [ebp + 0x4C]
        cmp eax, [ebp + 0x40]
        ret
    }
}

// how: 0 rendered (the same request moved past 0x11, below the abort states 0x19..), 1 aborted by us, 2 other (it left the
// front, or reached 0x19.. by itself: cancelled by its owner, or it finished between two steps)
static void TsFbEnd(int how, DWORD state, bool blkLive) {
    InterlockedExchange(&g_tsExProv, 0);
    if (!g_tsFbReq) return;
    if (how == 0) g_tsFbRendered++;
    else if (how == 1) g_tsFbAborted++;
    else g_tsFbOther++;
    char idx[64];
    if (blkLive) TsLodIdx(g_tsFbBlk, idx, sizeof(idx));
    else strcpy_s(idx, "n/a (request gone)");
    Log("thumbstall: fallback #%ld %s after %lu ms (state %02lX): key %s; lod %s; limit tests passed %ld "
        "(fallback_rendered %ld, fallback_aborted %ld, fallback_other %ld)",
        g_tsFbN, how == 0 ? "rendered" : how == 1 ? "aborted" : "other", GetTickCount() - g_tsFbSince, state, g_tsFbKey, idx,
        g_tsExUses - g_tsFbUses0, g_tsFbRendered, g_tsFbAborted, g_tsFbOther);
    g_tsFbReq = 0;
}
static void TsFbStart(const u8* req, DWORD stamp, u8* blk, DWORD now) {
    TsFbEnd(2, 0, false);  // a fallback still open for another request
    g_tsFbReq = req, g_tsFbStamp = stamp, g_tsFbSince = now, g_tsFbBlk = blk, g_tsFbUses0 = g_tsExUses;
    g_tsFbN++;
    TsWords(g_tsFbKey, sizeof(g_tsFbKey), (const DWORD*)(req + 0x110), 4);
    char before[64], after[64];
    TsLodIdx(blk, before, sizeof(before));
    const DWORD prov = blk && Readable(blk + 0x210, 0x40) ? *(const DWORD*)(blk + 0x24C) : 0;
    if (g_tsAction == 3 && prov) InterlockedExchange(&g_tsExProv, (LONG)prov);
    if (g_tsAction == 4 && blk && Readable(blk + 0x232, 1)) blk[0x232] = 0;  // min LOD byte; 0xD5A1C1 set it to 1
    TsLodIdx(blk, after, sizeof(after));
    Log("thumbstall: fallback #%ld start (%s): request %p key %s; lod %s -> %s; provider %08lX", g_tsFbN,
        g_tsAction == 3 ? (prov ? "provider exempt from ActiveLODLimit" : "no provider: nothing to exempt") : "min LOD 0", req,
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
    } __except (EXCEPTION_EXECUTE_HANDLER) { same = false; }
    if (same && st == 0x11) return;
    TsFbEnd(same && st > 0x11 && st < 0x19 ? 0 : 2, st, same);
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
    const DWORD now = GetTickCount();
    int act = 0;
    __try {
        const DWORD stamp = *(const DWORD*)(req + 0x23C);
        if (req != g_tsReq || stamp != g_tsStamp) { g_tsReq = req, g_tsStamp = stamp, g_tsSince = now; return 0; }
        if (now - g_tsSince < g_tsTimeoutMs) return 0;
        const u8* const blk = *(const u8* const*)(req + 0x244);
        const u8* const* m = *(const u8* const* const*)(req + 0xA8);
        const u8* const* const mEnd = *(const u8* const* const*)(req + 0xAC);
        bool own = false;
        const size_t models = m && mEnd >= m ? (size_t)(mEnd - m) : 0;
        for (size_t i = 0; i < models && i < 64; i++) own = own || m[i] == blk;
        // Actions 3 and 4: the first timeout starts the fallback, a second one for the same request aborts it
        const bool fbSecond = g_tsAction >= 3 && g_tsFbReq == req && g_tsFbStamp == stamp;
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
    InterlockedExchange(&g_tsExProv, 0);
    u8* const site = (u8*)(uintptr_t)TS_SITE;
    if (g_tsInstalled && Readable(site, 1) && *site == 0xE9) Write(site, kTsOrig, sizeof(kTsOrig));
    g_tsInstalled = false;
    if (g_tsLimPatched) {
        u8* const req = (u8*)(uintptr_t)TS_LIM_REQ;
        u8* const enq = (u8*)(uintptr_t)TS_LIM_ENQ;
        if (Readable(req, 1) && *req == 0xE8) Write(req, kTsLimReq, 6);
        if (Readable(enq, 1) && *enq == 0xE8) Write(enq, kTsLimEnq, 6);
        g_tsLimPatched = false;
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
        u8* const req = (u8*)(uintptr_t)TS_LIM_REQ;
        u8* const enq = (u8*)(uintptr_t)TS_LIM_ENQ;
        if (!Matches(req, kTsLimReq, sizeof(kTsLimReq)) || !Matches(enq, kTsLimEnq, sizeof(kTsLimEnq))) {
            Log("SKIP thumb stall fallback: ActiveLODLimit tests at 0x%08X / 0x%08X differ", TS_LIM_REQ, TS_LIM_ENQ);
            return false;
        }
        u8 a[6] = {0xE8, 0, 0, 0, 0, 0x90}, b[6] = {0xE8, 0, 0, 0, 0, 0x90};
        const DWORD ra = (DWORD)(uintptr_t)&TsLimReqStub - (TS_LIM_REQ + 5), rb = (DWORD)(uintptr_t)&TsLimEnqStub - (TS_LIM_ENQ + 5);
        memcpy(a + 1, &ra, 4);
        memcpy(b + 1, &rb, 4);
        if (!Write(req, a, 6)) { Log("FAIL thumb stall fallback: cannot write 0x%08X", TS_LIM_REQ); return false; }
        if (!Write(enq, b, 6)) {
            Write(req, kTsLimReq, 6);
            Log("FAIL thumb stall fallback: cannot write 0x%08X", TS_LIM_ENQ);
            return false;
        }
        g_tsLimPatched = true;
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
    u8* const site = (u8*)(uintptr_t)TS_SITE;
    if (!Matches(site, kTsOrig, sizeof(kTsOrig)) || !Matches((u8*)(uintptr_t)TS_KEEP, kTsKeep, sizeof(kTsKeep)) ||
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
        "(0 log, 1 abort the request, 2 render when the blocker is not its own model, 3 exempt its LOD provider from "
        "ActiveLODLimit then abort, 4 min LOD 0 then abort)", g_tsTimeoutMs, g_tsAction);
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
        if (!Matches(b, kTcBuildPro, sizeof(kTcBuildPro))) Log("SKIP thumb callers: SimBuilderBuildModel 0x%08X: bytes differ", TC_BUILD);
        else {
            u8 patch[9] = {0xE9, 0, 0, 0, 0, 0x90, 0x90, 0x90, 0x90};
            const DWORD rel = (DWORD)(uintptr_t)&TcStubBuild - (TC_BUILD + 5);
            memcpy(patch + 1, &rel, 4);
            if (Write(b, patch, sizeof(patch))) Log("OK   thumb callers: SimBuilderBuildModel 0x%08X patched", TC_BUILD);
        }
    }
    Log("thumb callers (LOG ONLY, [ThumbCallers]): lines \"thumbcallers:\"; per-request lines %s", g_tcLogEach ? "on" : "off");
}
