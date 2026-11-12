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

// What the blocking model waits for: its model handle (+0x210, docs/186 10.3) word by word, and the first 16 words of
// whatever each slot points at (tag bits masked), when that memory is readable. Log only.
static void TsWords(char* out, size_t cap, const DWORD* w, int n) {
    size_t at = 0;
    out[0] = 0;
    for (int i = 0; i < n && at + 10 < cap; i++) at += (size_t)sprintf_s(out + at, cap - at, i ? " %08lX" : "%08lX", w[i]);
}
static void TsDump(const u8* req, const u8* blk) {
    char line[16 * 9 + 8 * 9 + 64];
    __try {
        TsWords(line, sizeof(line), (const DWORD*)(req + 0x110), 4);
        Log("thumbstall:   request key %s", line);
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
        if (n <= 200 || (n & 63) == 0) TsDump(req, blk);
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
// [ThumbCallers] Enabled=0; LogEach=1 (0 = no per-request lines).
static const DWORD TC_HH = 0x00D614E0, TC_UI = 0x00D5C830;
static const u8 kTcHhPro[6] = {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF8};  // push ebp; mov ebp,esp; and esp,-8
static const u8 kTcUiPro[6] = {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0};  // push ebp; mov ebp,esp; and esp,-16
static void* g_tcOrig[2];
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
    h = (h ^ ret) * 16777619u;
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
            sprintf_s(g_tcRows[i].text, "%s ret %08lX: %s", which ? "ui 0xD5C830" : "household 0xD614E0", ret, st);
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
    if (g_tcLogEach)
        Log("thumbcallers: req #%d n=%ld total=%ld key %08lX %08lX %08lX %08lX waited=%d rc=%.1f MB rcN=%lu", at, n, total, key[0], key[1], key[2],
            key[3], (int)waited, held / 1048576.0, rcN);
    const DWORD now = GetTickCount();
    if (now - g_tcTopAt >= 30000) {
        g_tcTopAt = now;
        for (int i = 0; i < 64 && g_tcRows[i].hash; i++)
            Log("thumbcallers: top #%d n=%ld waited=%ld %s", i, g_tcRows[i].n, g_tcRows[i].waited, g_tcRows[i].text);
        Log("thumbcallers: top total=%ld unfiled=%ld rc=%.1f MB rcN=%lu", total, g_tcLost, held / 1048576.0, rcN);
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

static void ApplyThumbCallers(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"ThumbCallers", L"Enabled", 0, ini)) return;
    g_tcLogEach = GetPrivateProfileIntW(L"ThumbCallers", L"LogEach", 1, ini) != 0;
    g_tcTopAt = GetTickCount();
    InstallDetour("ThumbHouseholdSimRequest", TC_HH, kTcHhPro, sizeof(kTcHhPro), (void*)TcStubHh, &g_tcOrig[0]);
    InstallDetour("ThumbUiRequest", TC_UI, kTcUiPro, sizeof(kTcUiPro), (void*)TcStubUi, &g_tcOrig[1]);
    Log("thumb callers (LOG ONLY, [ThumbCallers]): lines \"thumbcallers:\"; per-request lines %s", g_tcLogEach ? "on" : "off");
}
