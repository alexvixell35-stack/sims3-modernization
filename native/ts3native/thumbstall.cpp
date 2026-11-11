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
        act = g_tsAction == 2 ? (own ? 1 : 2) : g_tsAction;
        const LONG n = InterlockedIncrement(&g_tsHits);
        if (act == 1) InterlockedIncrement(&g_tsAborts);
        if (act == 2) InterlockedIncrement(&g_tsForced);
        if (n <= 200 || (n & 63) == 0)
            Log("thumbstall: #%ld %s after %lu ms: request %p type=%08lX blocker %p vt=%08lX flags=%08lX layers=%08lX "
                "ms5E=%u own=%d models=%u (aborted %ld, rendered %ld)",
                n, act == 1 ? "abort" : act == 2 ? "render" : "seen", now - g_tsSince, req, *(const DWORD*)(req + 0x118), blk,
                blk ? *(const DWORD*)blk : 0, blk ? *(const DWORD*)(blk + 0x40) : 0, blk ? *(const DWORD*)(blk + 0x44) : 0,
                blk ? (unsigned)blk[0x210 + 0x5E] : 0, (int)own, (unsigned)models, g_tsAborts, g_tsForced);
        g_tsSince = now;  // Action 0: one line per timeout period; 1 and 2: the request leaves state 0x11 now
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
    u8* const site = (u8*)(uintptr_t)TS_SITE;
    if (g_tsInstalled && Readable(site, 1) && *site == 0xE9) Write(site, kTsOrig, sizeof(kTsOrig));
    g_tsInstalled = false;
}

static void ApplyThumbStall(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"ThumbStall", L"Enabled", 0, ini)) return;
    const int t = GetPrivateProfileIntW(L"ThumbStall", L"TimeoutMs", 10000, ini), a = GetPrivateProfileIntW(L"ThumbStall", L"Action", 1, ini);
    g_tsTimeoutMs = (DWORD)(t < 1000 ? 1000 : t > 600000 ? 600000 : t);
    g_tsAction = a < 0 ? 0 : a > 2 ? 2 : a;
    u8* const site = (u8*)(uintptr_t)TS_SITE;
    if (!Matches(site, kTsOrig, sizeof(kTsOrig)) || !Matches((u8*)(uintptr_t)TS_KEEP, kTsKeep, sizeof(kTsKeep)) ||
        !Matches((u8*)(uintptr_t)TS_ABORT, kTsAbort, sizeof(kTsAbort)) || !Matches((u8*)(uintptr_t)TS_GO, kTsGo, sizeof(kTsGo))) {
        Log("SKIP thumb stall: bytes at 0x%08X / 0x%08X / 0x%08X / 0x%08X differ: not installed", TS_SITE, TS_KEEP, TS_GO, TS_ABORT);
        return;
    }
    u8 patch[10] = {0xE9, 0, 0, 0, 0, 0x90, 0x90, 0x90, 0x90, 0x90};
    const DWORD rel = (DWORD)(uintptr_t)&TsStub - (TS_SITE + 5);
    memcpy(patch + 1, &rel, 4);
    if (!Write(site, patch, sizeof(patch))) { Log("FAIL thumb stall: cannot write 0x%08X", TS_SITE); return; }
    g_tsInstalled = true;
    Log("OK   thumb stall (EXPERIMENT, [ThumbStall]): state 0x11 of the Sim thumbnail queue, timeout %lu ms, action %d "
        "(0 log, 1 abort the request, 2 render when the blocker is not its own model)", g_tsTimeoutMs, g_tsAction);
}
