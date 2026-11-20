// docs/678: [SwarmGuard] the Swarm effect update crash (docs/67 §2 / §9 family; default off).
// The crash: the main thread's SwarmManager_UpdateEffectGroup 0x91BAB0 calls the effect update 0x92E0C0 for every active
// effect whose flag bit 2 (+0x44) is set (call site 0x91BC0A, `mov ecx, edi` / `call`; thiscall, no stack argument,
// plain `ret`). 0x92E0C0 starts with `call [[effect+4]+0x44]`. In two crashes (2026-11-11, 2026-11-20) the effect was a
// live cVisualEffect (its +0 and +8 vtables, refcount and name intact) whose +4 held a heap address instead of the
// interface vtable 0x1042528: one word written by someone else. The writer is not known (docs/678 §3).
//
// The guard: one call-site retarget of 0x91BC0A. For an object whose +0 is 0x1042570 and +8 is 0x1042520 (the two words
// only the cVisualEffect constructor 0x92FA00 writes together) the word at +4 must be 0x1042528, the constructor's third
// word; nothing else is a valid value. If it is not:
//   Repair=0: the update of this one effect is skipped this frame (its flag bit stays set, so it is tried again);
//   Repair=1: 0x1042528 is written back and the game's update runs as always.
// Any other object (other +0 or +8) goes to the game's code unjudged. Scan=1 also checks the group's whole effect vector
// (+0x30 / +0x34) at most once a millisecond, so a bad word is found within a frame of the write, not at the effect's
// next update: the log line then brackets the time of the write. Every hit is counted; the first sight of an (effect,
// word) pair logs the effect's head, its name and the memory the bad word points at (the word before it names the
// writer's class if it is an object).
#include "swarmguard-core.h"

static const DWORD SWG_SITE = TS3A(0x0091BC0A), SWG_UPDATE = TS3A(0x0092E0C0);
static const DWORD SWG_VT0 = TS3A(0x01042570), SWG_VT4 = TS3A(0x01042528), SWG_VT8 = TS3A(0x01042520);
static const LONG kSwgLines = 64;   // dump lines per session

static struct { LONG on, repair, scan; } g_swgk;
static SwgVt g_swgVt;
static DWORD g_swgTarget;
static SwgStats g_swgSt;
static SwgSeen g_swgSeen[SWG_SEEN];
static unsigned g_swgSeenNext;
static DWORD g_swgScanTick, g_swgCleanTick;

static void SwgDump(const char* how, const unsigned long* e, unsigned long word, const unsigned long* group) {
    if (!SwgFirstSight(g_swgSeen, &g_swgSeenNext, (unsigned long)(uintptr_t)e, word) || g_swgSt.lines >= kSwgLines) return;
    g_swgSt.lines++;
    char name[49];
    name[0] = 0;
    if (Readable(e, 0x48)) {
        const char* b = (const char*)(uintptr_t)e[6];
        const char* end = (const char*)(uintptr_t)e[7];
        const size_t n = end > b && (size_t)(end - b) < sizeof(name) ? (size_t)(end - b) : 0;
        if (n && Readable(b, n)) {
            for (size_t i = 0; i < n; i++) name[i] = b[i] >= 0x20 && b[i] < 0x7F ? b[i] : '?';
            name[n] = 0;
        }
        Log("swarm guard: %s effect %p in group %p: +0 %08lX +4 %08lX (want %08lX) +8 %08lX, refs %ld, flags %08lX, +0x40 %08lX, name '%s', "
            "%s; last clean scan %lu ms ago", how, e, group, e[0], word, g_swgVt.vt4, e[2], (long)e[5], e[17], e[16], name,
            g_swgk.repair ? "word put back" : "update skipped", g_swgCleanTick ? GetTickCount() - g_swgCleanTick : 0ul);
    }
    const unsigned long* w = (const unsigned long*)(uintptr_t)(word - 4);
    if (word >= 0x10000 && Readable(w, 0x48))
        Log("swarm guard:   at %08lX-4: %08lX | %08lX %08lX %08lX %08lX %08lX %08lX %08lX %08lX ... +0x44 %08lX%s", word, w[0], w[1], w[2], w[3],
            w[4], w[5], w[6], w[7], w[8], w[18], w[2] == (unsigned long)(uintptr_t)(e + 1) ? " (its 2nd word points back at effect+4)" : "");
    else
        Log("swarm guard:   %08lX is not readable memory", word);
}

// the stub's C part, on the main thread: 1 = skip the game's update of this effect
static int __cdecl SwgEnter(unsigned long* e, unsigned long* group) {
    if (g_swgk.scan && group) {
        const DWORD now = GetTickCount();
        if (now != g_swgScanTick) {
            g_swgScanTick = now;
            unsigned long** const b = (unsigned long**)(uintptr_t)group[0x30 / 4];
            unsigned long** const end = (unsigned long**)(uintptr_t)group[0x34 / 4];
            unsigned long* fb = nullptr;
            unsigned long fw = 0;
            if (b && end > b && SwgScan(b, end, g_swgVt, g_swgk.repair != 0, &g_swgSt, &fb, &fw)) SwgDump("scan found", fb, fw, group);
            else g_swgCleanTick = now;
        }
    }
    unsigned long word = 0;
    const long badBefore = g_swgSt.bad;
    const int skip = SwgDecide(e, g_swgVt, g_swgk.repair != 0, &g_swgSt, &word);
    if (g_swgSt.bad != badBefore) SwgDump("update found", e, word, group);
    return skip;
}

// 0x91BC0A's new target. Entered as 0x92E0C0 would be: ecx = effect, ebx = the group (the caller's own register), the
// return address on top. Every register and the flags are saved; the answer goes into the saved eax slot ([ebp+32]).
// eax and the flags are the callee's to change at a call, and 0x92E0C0 reads neither. Run: jump to the game's update
// with the stack as the caller left it. Skip: return to the caller (0x91BC0F reads edi and ebx only).
static __declspec(naked) void SwgStub() {
    __asm {
        pushad
        pushfd
        mov ebp, esp
        and esp, 0xFFFFFFF0
        push ebx
        push ecx
        call SwgEnter
        mov [ebp + 32], eax
        mov esp, ebp
        popfd
        popad
        test eax, eax
        jnz swg_skip
        jmp dword ptr [g_swgTarget]
    swg_skip:
        ret
    }
}

static void InstallSwarmGuard(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"SwarmGuard", L"Enabled", 0, ini)) return;
#ifdef TS3A_TABLE
    if (ts3a_noaddr) { Log("SKIP swarm guard: no EA 1.69 address, docs/678"); return; }
#endif
    g_swgk.repair = GetPrivateProfileIntW(L"SwarmGuard", L"Repair", 0, ini) != 0;
    g_swgk.scan = GetPrivateProfileIntW(L"SwarmGuard", L"Scan", 0, ini) != 0;
    g_swgVt.vt0 = SWG_VT0; g_swgVt.vt4 = SWG_VT4; g_swgVt.vt8 = SWG_VT8;
    g_swgTarget = SWG_UPDATE;
    // the constructor's three words must be what this file believes: its slot +0x44 of the +4 vtable is read and must be code
    const unsigned long* vt4 = (const unsigned long*)(uintptr_t)SWG_VT4;
    if (!Readable(vt4, 0x48) || !Readable((const void*)(uintptr_t)vt4[0x44 / 4], 4)) {
        Log("SKIP swarm guard: the interface vtable 0x%08lX is not readable, nothing written", SWG_VT4);
        return;
    }
    const CallSite site = {SWG_SITE, SWG_UPDATE, (void*)SwgStub};
    if (!PatchCalls("swarm guard (effect update call in SwarmManager_UpdateEffectGroup)", &site, 1)) return;
    g_swgk.on = 1;
    Log("OK   swarm guard: Repair=%ld Scan=%ld (a cVisualEffect whose +4 is not 0x%08lX: %s; docs/678)", g_swgk.repair, g_swgk.scan, SWG_VT4,
        g_swgk.repair ? "word put back, update runs" : "its update is skipped for the frame");
}

static void SwgLogCounters() {
    if (!g_swgk.on) return;
    Log("swarm guard: updates %ld, bad +4 %ld (skipped %ld, put back %ld), other classes %ld, scans %ld (bad found %ld), dumps %ld",
        g_swgSt.calls, g_swgSt.bad, g_swgSt.skipped, g_swgSt.repaired, g_swgSt.foreign, g_swgSt.scans, g_swgSt.scanBad, g_swgSt.lines);
}
