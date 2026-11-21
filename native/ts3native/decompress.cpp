// Phase 6: faster RefPack decompression (docs/15 phase 6). Included by ts3native.cpp (one translation unit).
//
// The game's only RefPack decode core is 0x4EB3B0 (cdecl: dst, dstCap, src, srcLen; returns the size in the stream
// header after the stop code, 0 on any error). Its one caller is the codec's Decompress slot 0x4EC010 (vtable 0xFB9018
// slot 2), which checks the 0x10FB signature and header size <= dstCap first. Every compressed package resource goes
// through it (PackedFile 0x4A7360/0x4A7030, MemoryDB 0x72B0A0, PackedStream 0x72C980), on whichever thread asks.
// S3SS's RefPackDecompressor replaces 0x4EB3B0 with a jmp to its SSE2/AVX2 version. We do not touch those bytes: we
// retarget the call at 0x4EC065 instead, so mode 0 still runs whatever sits at 0x4EB3B0 (S3SS or the game's own).

// Exact C port of the game's 0x4EB3B0 (decompile verified): same checks in the same order, byte loops.
// The selftest and the offline test (decompress-test.cpp) use it as the reference (not static: unused in the .asi).
int __cdecl RefpackRef(u8* dst, DWORD cap, const u8* src, DWORD len) {
    if (!src || len < 2) return 0;
    const u8 f = src[0];
    const DWORD sizeBytes = (f & 0x80) ? 4 : 3;
    DWORD rem = len - 2;
    const u8* p = src + 2;
    if (f & 1) {  // compressed-size field, skipped
        if (rem < sizeBytes) return 0;
        rem -= sizeBytes;
        p += sizeBytes;
    }
    if (rem < sizeBytes) return 0;
    DWORD hdr = 0;
    for (DWORD i = 0; i < sizeBytes; i++) hdr = (hdr << 8) | p[i];
    p += sizeBytes;
    rem -= sizeBytes;
    u8* d = dst;
    DWORD dleft = cap;
    while (rem) {
        const u8 b0 = *p;
        DWORD lit, n, off;
        rem--;
        if (b0 < 0x80) {
            if (rem < 1) return 0;
            lit = b0 & 3;
            off = ((b0 & 0x60u) << 3) + p[1] + 1;
            n = ((b0 >> 2) & 7) + 3;
            p += 2;
            rem -= 1;
        } else if (!(b0 & 0x40)) {
            if (rem < 2) return 0;
            lit = p[1] >> 6;
            off = ((p[1] & 0x3fu) << 8) + p[2] + 1;
            n = (b0 & 0x3f) + 4;
            p += 3;
            rem -= 2;
        } else if (!(b0 & 0x20)) {
            if (rem < 3) return 0;
            lit = b0 & 3;
            off = ((b0 & 0x10u) << 12) + ((DWORD)p[1] << 8) + p[2] + 1;
            n = p[3] + 5 + ((b0 & 0x0cu) << 6);
            p += 4;
            rem -= 3;
        } else {
            n = ((b0 & 0x1fu) << 2) + 4;
            p++;
            if (n > 0x70) {  // stop code 0xFC-0xFF with 0-3 trailing literals
                lit = b0 & 3;
                if (dleft < lit || rem < lit) return 0;
                while (lit--) *d++ = *p++;
                return (int)hdr;
            }
            if (dleft < n || rem < n) return 0;
            dleft -= n;
            rem -= n;
            while (n--) *d++ = *p++;
            continue;
        }
        if (dleft < lit || rem < lit) return 0;
        dleft -= lit;
        rem -= lit;
        while (lit--) *d++ = *p++;
        if (off > (DWORD)(d - dst)) return 0;
        if (dleft < n) return 0;
        dleft -= n;
        for (const u8* m = d - off; n--;) *d++ = *m++;
    }
    return 0;  // input ended without a stop code
}

// Same result and the same checks as RefpackRef. Faster: away from the ends of the buffers it copies in fixed 4/8/16
// byte blocks that may run past the bytes a command needs ("wild" copies, as LZ4 does); the overrun stays inside
// [dst, dst + min(cap, header size)) and inside src, and the next command overwrites it. Near the ends it falls back to
// exact byte copies. So on success bytes [0, result) are identical to the game's; the game leaves bytes past the end of
// the output untouched, we may leave scratch there only when the stream produces less than its header says (never seen
// in the shipped packages, decompress-test.cpp checks that).
static __forceinline void Copy16(u8* d, const u8* s) { _mm_storeu_si128((__m128i*)d, _mm_loadu_si128((const __m128i*)s)); }
static __forceinline void Copy8(u8* d, const u8* s) { _mm_storel_epi64((__m128i*)d, _mm_loadl_epi64((const __m128i*)s)); }

static int __cdecl RefpackFast(u8* dst, DWORD cap, const u8* src, DWORD len) {
    if (!src || len < 2) return 0;
    const u8 f = src[0];
    const DWORD sizeBytes = (f & 0x80) ? 4 : 3;
    DWORD rem = len - 2;
    const u8* p = src + 2;
    if (f & 1) {
        if (rem < sizeBytes) return 0;
        rem -= sizeBytes;
        p += sizeBytes;
    }
    if (rem < sizeBytes) return 0;
    DWORD hdr = 0;
    for (DWORD i = 0; i < sizeBytes; i++) hdr = (hdr << 8) | p[i];
    p += sizeBytes;
    rem -= sizeBytes;
    u8* d = dst;
    u8* const dend = dst + cap;                       // what the checks use (as the game)
    u8* const wend = dst + (hdr < cap ? hdr : cap);   // where wild writes may reach
    const u8* const send = p + rem;
    while (p < send) {
        const u8 b0 = *p;
        DWORD lit, n, off;
        const size_t left = (size_t)(send - p) - 1;  // bytes after the command byte
        if (b0 < 0x80) {
            if (left < 1) return 0;
            lit = b0 & 3;
            off = ((b0 & 0x60u) << 3) + p[1] + 1;
            n = ((b0 >> 2) & 7) + 3;
            p += 2;
        } else if (!(b0 & 0x40)) {
            if (left < 2) return 0;
            lit = p[1] >> 6;
            off = ((p[1] & 0x3fu) << 8) + p[2] + 1;
            n = (b0 & 0x3f) + 4;
            p += 3;
        } else if (!(b0 & 0x20)) {
            if (left < 3) return 0;
            lit = b0 & 3;
            off = ((b0 & 0x10u) << 12) + ((DWORD)p[1] << 8) + p[2] + 1;
            n = p[3] + 5 + ((b0 & 0x0cu) << 6);
            p += 4;
        } else {
            n = ((b0 & 0x1fu) << 2) + 4;
            p++;
            if (n > 0x70) {
                lit = b0 & 3;
                if ((DWORD)(dend - d) < lit || (DWORD)(send - p) < lit) return 0;
                while (lit--) *d++ = *p++;
                return (int)hdr;
            }
            if ((DWORD)(dend - d) < n || (DWORD)(send - p) < n) return 0;
            if (d < wend && n + 15 <= (DWORD)(wend - d) && n + 15 <= (DWORD)(send - p)) {
                for (DWORD i = 0; i < n; i += 16) Copy16(d + i, p + i);
            } else {
                memcpy(d, p, n);
            }
            d += n;
            p += n;
            continue;
        }
        if ((DWORD)(dend - d) < lit || (DWORD)(send - p) < lit) return 0;
        if (d < wend && (DWORD)(wend - d) >= 4 && (DWORD)(send - p) >= 4) {
            *(DWORD*)d = *(const DWORD*)p;  // x86: unaligned is fine
        } else {
            for (DWORD i = 0; i < lit; i++) d[i] = p[i];
        }
        d += lit;
        p += lit;
        if (off > (DWORD)(d - dst)) return 0;
        if ((DWORD)(dend - d) < n) return 0;
        const u8* m = d - off;
        const DWORD room = d < wend ? (DWORD)(wend - d) : 0;  // 0 past the header size (cap > header)
        if (off >= 16 && n + 15 <= room) {
            for (DWORD i = 0; i < n; i += 16) Copy16(d + i, m + i);
        } else if (off >= 8 && n + 7 <= room) {
            for (DWORD i = 0; i < n; i += 8) Copy8(d + i, m + i);
        } else if (off == 1) {
            memset(d, m[0], n);
        } else {
            for (DWORD i = 0; i < n; i++) d[i] = m[i];
        }
        d += n;
    }
    return 0;
}

// --- hook and counters ---
// Switches and int32 counters: ts3native_counters.decompress* (v7). 64-bit sums and per-thread detail for the log here.
struct DecompressStats {
    LONG64 inBytes;        // compressed bytes in
    LONG64 outBytes;       // decompressed bytes out (sum of results)
    LONG64 ticks;          // QPC ticks inside the decoder (excludes verify)
    // per thread, first 8 threads that decode: id, calls, bytes out, QPC ticks; mainTid = the thread that loaded us
    LONG mainTid;
    LONG64 mainTicks;
    LONG tid[8];
    LONG tcalls[8];
    LONG64 tbytes[8];
    LONG64 tticks[8];
};
static DecompressStats g_dstat;

typedef int(__cdecl* RefpackFn)(u8*, DWORD, const u8*, DWORD);
static RefpackFn g_refpackOrig = (RefpackFn)(uintptr_t)TS3A(0x004EB3B0);
static LARGE_INTEGER g_dqpf;
static volatile LONG64 g_dNextLog;

static int ThreadSlot(LONG tid) {
    DecompressStats& c = g_dstat;
    for (int i = 0; i < 8; i++) {
        LONG t = c.tid[i];
        if (t == tid) return i;
        if (t == 0) {
            t = InterlockedCompareExchange(&c.tid[i], tid, 0);
            if (t == 0 || t == tid) return i;
        }
    }
    return -1;
}

static void LogDecompress() {
    const DecompressStats& c = g_dstat;
    const Ts3nCounters& k = ts3native_counters;
    const LONG64 f = g_dqpf.QuadPart ? g_dqpf.QuadPart : 1;
    Log("decompress: mode %ld verify %ld calls %ld fails %ld mismatches %ld in %lld MB out %lld MB time %lld ms max %ld us",
        k.decompressMode, k.decompressVerify, k.decompressCalls, k.decompressFails, k.decompressMismatches, c.inBytes >> 20,
        c.outBytes >> 20, c.ticks * 1000 / f, k.decompressMaxUs);
    for (int i = 0; i < 8 && c.tid[i]; i++)
        Log("  thread %ld%s: calls %ld out %lld MB time %lld ms", c.tid[i], c.tid[i] == c.mainTid ? " (main)" : "", c.tcalls[i],
            c.tbytes[i] >> 20, c.tticks[i] * 1000 / f);
}

static int __cdecl RefpackHook(u8* dst, DWORD cap, const u8* src, DWORD len) {
    DecompressStats& c = g_dstat;
    Ts3nCounters& k = ts3native_counters;
    const bool fast = k.decompressMode != 0;
    LARGE_INTEGER t0, t1;
    QueryPerformanceCounter(&t0);
    int r = fast ? RefpackFast(dst, cap, src, len) : g_refpackOrig(dst, cap, src, len);
    QueryPerformanceCounter(&t1);
    const LONG64 dt = t1.QuadPart - t0.QuadPart;
    if (fast && k.decompressVerify) {
        u8* tmp = cap ? (u8*)HeapAlloc(GetProcessHeap(), 0, cap) : NULL;
        if (tmp || !cap) {
            const int r2 = g_refpackOrig(tmp, cap, src, len);
            const DWORD n = r2 > 0 && (DWORD)r2 < cap ? (DWORD)r2 : cap;
            if (r2 != r || (r2 && memcmp(dst, tmp, n) != 0)) {
                if (InterlockedIncrement(&k.decompressMismatches) <= 5) Log("decompress MISMATCH: src len %lu cap %lu fast %d orig %d", len, cap, r, r2);
                if (r2) memcpy(dst, tmp, n);
                r = r2;
            }
            if (tmp) HeapFree(GetProcessHeap(), 0, tmp);
        }
    }
    const LONG64 f = g_dqpf.QuadPart ? g_dqpf.QuadPart : 1;
    InterlockedIncrement(&k.decompressCalls);
    if (!r) InterlockedIncrement(&k.decompressFails);
    CeRec(CE_DECOMP, (DWORD)(uintptr_t)dst, r > 0 ? (DWORD)r : 0, cap);  // docs/186 §8
    k.decompressInMB = (LONG)(InterlockedAdd64(&c.inBytes, len) >> 20);
    k.decompressOutMB = (LONG)(InterlockedAdd64(&c.outBytes, r > 0 ? r : 0) >> 20);
    k.decompressMs = (LONG)(InterlockedAdd64(&c.ticks, dt) * 1000 / f);
    Max(&k.decompressMaxUs, (LONG)(dt * 1000000 / f));
    const LONG tid = (LONG)GetCurrentThreadId();
    if (tid == c.mainTid) {
        InterlockedIncrement(&k.decompressMainCalls);
        k.decompressMainMs = (LONG)(InterlockedAdd64(&c.mainTicks, dt) * 1000 / f);
    }
    const int s = ThreadSlot(tid);
    if (s >= 0) {
        InterlockedIncrement(&c.tcalls[s]);
        InterlockedAdd64(&c.tbytes[s], r > 0 ? r : 0);
        InterlockedAdd64(&c.tticks[s], dt);
    }
    const ULONGLONG now = GetTickCount64();
    const LONG64 next = g_dNextLog;
    if ((LONG64)now >= next && InterlockedCompareExchange64(&g_dNextLog, (LONG64)now + 60000, next) == next) LogDecompress();
    return r;
}

// Retargets the rel32 of `call 0x4EB3B0` at `site` (verified bytes first). Parameters let the selftest use local memory.
static bool PatchRefpackCall(DWORD site, DWORD callee, void* hook) {
    u8 expect[5] = {0xE8};
    *(LONG*)(expect + 1) = (LONG)(callee - (site + 5));
    if (!Matches((void*)(uintptr_t)site, expect, 5)) {
        u8 got[5] = {0};
        char hex[32];
        if (Readable((void*)(uintptr_t)site, 5)) memcpy(got, (void*)(uintptr_t)site, 5);
        Hex(got, 5, hex, sizeof(hex));
        Log("SKIP decompress: call at 0x%08X differs (%s)", site, hex);
        return false;
    }
    LONG rel = (LONG)((u8*)hook - (u8*)(uintptr_t)(site + 5));
    if (!Write((void*)(uintptr_t)(site + 1), &rel, 4)) {
        Log("FAIL decompress: VirtualProtect at 0x%08X failed (err %lu)", site, GetLastError());
        return false;
    }
    return true;
}

static const DWORD REFPACK_CALL = TS3A(0x004EC065), REFPACK_CORE = TS3A(0x004EB3B0);

// [Decompress]: returns true when the hook is installed (the log must then stay open for the periodic counters).
static bool ApplyDecompress(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"Decompress", L"Enabled", 0, ini)) { Log("decompress off (Enabled=0)"); return false; }
    QueryPerformanceFrequency(&g_dqpf);
    Ts3nCounters& c = ts3native_counters;
    c.decompressMode = GetPrivateProfileIntW(L"Decompress", L"Mode", 1, ini) ? 1 : 0;
    c.decompressVerify = GetPrivateProfileIntW(L"Decompress", L"Verify", 0, ini) ? 1 : 0;
    g_dstat.mainTid = (LONG)GetCurrentThreadId();
    if (!PatchRefpackCall(REFPACK_CALL, REFPACK_CORE, (void*)RefpackHook)) return false;
    c.decompressReady = 1;
    u8 head[5];
    char hex[32];
    memcpy(head, (void*)(uintptr_t)REFPACK_CORE, 5);
    Hex(head, 5, hex, sizeof(hex));
    Log("OK   decompress: call 0x%08X -> RefpackHook, mode %ld verify %ld (0x%08X starts %s%s at load time)", REFPACK_CALL, c.decompressMode, c.decompressVerify,
        REFPACK_CORE, hex, head[0] == 0xE9 ? "= jmp, likely S3SS RefPackDecompressor" : "= game code");
    return true;
}
