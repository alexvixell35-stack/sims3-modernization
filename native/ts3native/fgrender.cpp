// [Harness] ForegroundRender (docs/162 §7, docs/41 §2). Included by ts3native.cpp (one translation unit).
// TEST ONLY: never set it in a release ini. Harness runs are never focused (game-window rule), so the unfocused game paces
// its frames like a background window; real players run focused. ForegroundRender=1 removes the two inactive-window sleeps
// of the frame loop so an unfocused run renders (and, with S3SS Precise's once-per-frame signal, publishes) like a focused one.
//
// Sites [verified, disassembly of the Steam TS3W.exe]:
//  1. MainLoop 0xECA960: `cmp byte [esp+13h],0 / je +9 / test bl,bl / jne +5 / call App_IdleIfNotForeground 0x5887A0`.
//     The byte is "[Config] NoInactiveIdle key absent" (0x58A8D0 lookup at 0xECA976); bl is the ResourceSystem loading cap.
//     Patch the `je` at 0xECA9E2 (74 09 -> EB 09): the Sleep(10) call is always skipped, same as NoInactiveIdle present.
//  2. Frame_RenderAndPresent 0xEC9F00: after Present, `cmp byte [esi+8Dh],0 / pop esi / je +15h` (0xEC9FBA..0xEC9FC3) then
//     `push ecx / mov [esp+28h],1Eh / call 0x4E1320` (SleepEx(30) wrapper). S3SS Smooth Patch Precise rewrites the 9 bytes
//     at 0xEC9FBA (its own limiter, `je` -> `jmp`): with it installed the SleepEx is dead code. We do not touch S3SS's bytes;
//     we turn the call at 0xEC9FD1 into a 5-byte nop (the push/add esp,4 around it stay balanced). Without S3SS this removes
//     the SleepEx(30) when device +0x8D (lost/minimized, I-85-6) is set; with S3SS it changes nothing.
// The 10 FPS ResourceSystem cap (0xECAA65) needs ResourceSystem +0x1F0, set only by the thumbnail preload 0xD60800, which
// runs only with the `generateThumbnails` config key (0xD5859B): never in play, so no patch for it.

struct FgSite {
    DWORD addr;
    u8 orig[5], patched[5];
    size_t len;
    const char* what;
};
static const FgSite kFgSites[2] = {
    {TS3A(0x00ECA9E2), {0x74, 0x09}, {0xEB, 0x09}, 2, "MainLoop inactive Sleep(10) gate"},
    {TS3A(0x00EC9FD1), {0xE8, 0x4A, 0x73, 0x61, 0xFF}, {0x0F, 0x1F, 0x44, 0x00, 0x00}, 5, "SleepEx(30) after Present"},
};
static u8* g_fgAt[2];  // patched sites, for the revert on detach

// Patch each site whose bytes are the original ones (at[i] = where site i lives: the game, or a dummy in the selftest).
// Returns a bit per site now patched. A site with other bytes (another mod's) is left alone and logged.
static int FgPatch(u8* const at[2]) {
    int mask = 0;
    char hex[32];
    for (int i = 0; i < 2; i++) {
        const FgSite& s = kFgSites[i];
        if (!Matches(at[i], s.orig, s.len)) {
            if (Readable(at[i], s.len)) Hex(at[i], s.len, hex, sizeof(hex)); else strcpy_s(hex, "<unreadable>");
            Log("SKIP foreground render: %s at 0x%08X differs (found %s)", s.what, s.addr, hex);
            continue;
        }
        if (!Write(at[i], s.patched, s.len)) continue;
        g_fgAt[i] = at[i];
        mask |= 1 << i;
        Log("OK   foreground render: %s at 0x%08X patched", s.what, s.addr);
    }
    return mask;
}

// Put the original bytes back where we patched (only if our bytes are still there).
static void FgRevert() {
    for (int i = 0; i < 2; i++) {
        if (g_fgAt[i] && Matches(g_fgAt[i], kFgSites[i].patched, kFgSites[i].len)) Write(g_fgAt[i], kFgSites[i].orig, kFgSites[i].len);
        g_fgAt[i] = NULL;
    }
}

static bool ApplyForegroundRender(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"Harness", L"ForegroundRender", 0, ini)) return false;
    u8* const at[2] = {(u8*)(uintptr_t)kFgSites[0].addr, (u8*)(uintptr_t)kFgSites[1].addr};
    Log("foreground render (TEST ONLY, [Harness] ForegroundRender=1): sites patched mask %d", FgPatch(at));
    return false;  // start-up patch only: no periodic log needed
}
