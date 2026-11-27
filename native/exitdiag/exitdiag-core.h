// exitdiag pure parts (no game memory, no Windows calls): site table, module lookup, path masking, trigger-hook map walk,
// collect list. Shared by exitdiag.cpp and exitdiag-test.cpp (docs/67 §47).
#pragma once
#include <stdint.h>
#include <stddef.h>
#include <stdio.h>
#include <string.h>
#include <wchar.h>

// Steam 1.67 TS3W.exe [verified: PE header of the Steam 1.67 copy]
static const uint32_t kExeStamp = 0x52DEC247, kExeImageSize = 0xE5A000;
static const uint32_t kRdataLo = 0xF95000, kRdataHi = 0x1145389;  // .rdata (vtables)
static const uint32_t kTriggerSvcVt = 0x101E5D8;                  // UI/TriggerService vtable (docs/43 §5)

// Every patched site: exact original bytes (a site whose bytes differ is skipped, not hooked) [verified: disassembly of the
// Steam 1.67 copy]. The jmp replaces whole instructions; the stub re-runs them and goes on at addr + len.
enum { S_APPSHUT, S_ECA300, S_58B310, S_MGR, S_SVC, S_HOOKS, S_WIN1, S_WIN2, S_COUNT };
struct SiteDef { const char* name; uint32_t addr; uint8_t len; uint8_t bytes[8]; };
static const SiteDef kSites[S_COUNT] = {
    {"App_Shutdown 0xEC9B40 entry", 0xEC9B40, 7, {0x56, 0x57, 0x8B, 0xF9, 0x8D, 0x4F, 0x10}},  // push esi; push edi; mov edi,ecx; lea ecx,[edi+10]
    {"0xECA300 entry", 0xECA300, 5, {0x56, 0x8B, 0x31, 0x85, 0xF6}},                          // push esi; mov esi,[ecx]; test esi,esi
    {"0x58B310 entry", 0x58B310, 7, {0x56, 0x57, 0x8B, 0xF9, 0x83, 0x3F, 0x00}},              // push esi; push edi; mov edi,ecx; cmp [edi],0
    {"service manager call 0x59F060 at 0x58941D", 0x58941D, 5, {0xE8, 0x3E, 0x5C, 0x01, 0x00}},  // call 0x59F060
    {"service vtable+0x18 call at 0x59F083", 0x59F080, 5, {0x8B, 0x50, 0x18, 0xFF, 0xD2}},    // mov edx,[eax+18]; call edx
    {"TriggerService slot +0x18 0x7F10B0 entry", 0x7F10B0, 5, {0x53, 0x56, 0x8B, 0xF1, 0x57}},  // push ebx; push esi; mov esi,ecx; push edi
    {"window +0x10C call 1 at 0x7F0F4E", 0x7F0F4B, 5, {0x50, 0x8B, 0xCB, 0xFF, 0xD2}},        // push eax; mov ecx,ebx; call edx
    {"window +0x10C call 2 at 0x7F0F61", 0x7F0F5E, 5, {0x51, 0x8B, 0xCB, 0xFF, 0xD2}},        // push ecx; mov ecx,ebx; call edx
};
static bool SiteBytesMatch(const uint8_t* at, const SiteDef& s) { return memcmp(at, s.bytes, s.len) == 0; }

struct ModRange { uint32_t base, size; char name[64]; };
static int ModFind(const ModRange* t, int n, uint32_t a) {
    for (int i = 0; i < n; i++)
        if (a - t[i].base < t[i].size) return i;
    return -1;
}
static void ModFmt(const ModRange* t, int n, uint32_t a, char* out, size_t cap) {
    const int i = ModFind(t, n, a);
    if (i < 0) snprintf(out, cap, "%08X (not in a module)", a);
    else snprintf(out, cap, "%08X (%s+0x%X)", a, t[i].name, a - t[i].base);
}

// Masks the game root, the profile folder and the bare user name, so logs never carry the Windows user name.
static bool PrefixI(const wchar_t* s, const wchar_t* p, size_t* n) {
    *n = p ? wcslen(p) : 0;
    return *n && _wcsnicmp(s, p, *n) == 0 && (s[*n] == 0 || s[*n] == L'\\' || s[*n] == L'/');
}
static void MaskPath(const wchar_t* in, const wchar_t* game, const wchar_t* profile, const wchar_t* user, wchar_t* out, size_t cap) {
    size_t n;
    wchar_t tmp[1024];
    if (PrefixI(in, game, &n)) swprintf(tmp, 1024, L"<game>%ls", in + n);  // game first: it may sit under the profile
    else if (PrefixI(in, profile, &n)) swprintf(tmp, 1024, L"%%USERPROFILE%%%ls", in + n);
    else swprintf(tmp, 1024, L"%ls", in);
    const size_t un = user ? wcslen(user) : 0;
    size_t o = 0;
    for (const wchar_t* s = tmp; *s && o + 7 < cap;) {
        if (un >= 2 && _wcsnicmp(s, user, un) == 0) { wcscpy_s(out + o, cap - o, L"<user>"); o += 6; s += un; }
        else out[o++] = *s++;
    }
    out[o] = 0;
}

// UI/TriggerService hook map [verified: 0x7F0EF0, 0x7FF6B0 find, 0x7F0DB0 erase, 0x7F10B0 drain loop]: [svc+0x14] bucket array,
// [svc+0x18] bucket count, [svc+0x1C] element count. Node: +0 handle, +0x18 window, +0x1C flags (byte, 0x10 = active,
// 0x7F0F34), +0x20 owned object (released on erase), +0x24 next in bucket. +4..+0x14 [inferred]: next/prev/priority/handler/
// callback in the dump; logged raw.
struct HookNode { uint32_t addr, handle, d4, d8, dC, d10, d14, window, flags, obj20, next; };
typedef bool (*ReadFn)(uint32_t addr, void* dst, uint32_t n, void* ctx);
// Returns nodes read (<= max), or < 0 when the map header cannot be read or looks wrong. *elems = the map's own count.
static int WalkHooks(ReadFn rd, void* ctx, uint32_t svc, HookNode* out, int max, uint32_t* elems) {
    uint32_t h[3];
    *elems = 0;
    if (!rd(svc + 0x14, h, sizeof(h), ctx)) return -1;
    *elems = h[2];
    if (!h[1] || h[1] > (1u << 20)) return -2;
    int n = 0;
    for (uint32_t b = 0; b < h[1] && n < max; b++) {
        uint32_t p;
        if (!rd(h[0] + b * 4, &p, 4, ctx)) return n ? n : -3;
        for (int guard = 0; p && n < max && guard < 4096; guard++) {
            uint32_t d[10];
            if (!rd(p, d, sizeof(d), ctx)) break;
            HookNode& o = out[n++];
            o = {p, d[0], d[1], d[2], d[3], d[4], d[5], d[6], d[7], d[8], d[9]};
            p = d[9];
        }
    }
    return n;
}

// collect-list.txt: wildcard paths collect.cmd copies into the zip (newest 5 each); local only, never zipped (full paths).
static int BuildCollectList(const wchar_t* bin, const wchar_t* user, wchar_t* out, size_t cap) {
    static const wchar_t* const kBin[] = {L"ApexRadiance_*.txt", L"ts3native*.log"};
    static const wchar_t* const kUser[] = {L"Apex Radiance\\ApexRadiance_*.txt", L"ts3native-crashes\\crash-*.txt", L"xcpt*.txt"};
    size_t o = 0;
    int n = 0;
    out[0] = 0;
    for (const wchar_t* p : kBin)
        if (bin && *bin) { o += swprintf(out + o, cap - o, L"%ls\\%ls\r\n", bin, p); n++; }
    for (const wchar_t* p : kUser)
        if (user && *user) { o += swprintf(out + o, cap - o, L"%ls\\%ls\r\n", user, p); n++; }
    return n;
}
