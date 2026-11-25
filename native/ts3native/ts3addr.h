// TS3A(x): a TS3W.exe address, written as its Steam 1.67 value (docs/68 §7.1, tools/addr-codemod.ts).
// Default build: (x), so the Steam binary stays byte-identical. With /DTS3A_TABLE the value comes from the committed
// ts3addr-ea169.h (EA app 1.69.47; node tools/port-run.ts --table ..., docs/228: addresses only, no exe bytes), which also
// names the host build (TS3A_HOST_PE/SIZE/NAME) for the host check in ts3native.cpp. The lookup is constexpr and forced
// to compile time, so static initializers stay constant and an address missing from the table is a compile error, never
// a wrong pointer at run time. A 0 row = unresolved on that build (listed as `// unresolved` in the header): its code
// patch fails the `Matches` byte check and logs SKIP; a data read at 0 would crash, so its ini section stays off there.
// /DTS3A_TABLE /DTS3A_RETAIL: the retail (disc / non-Steam) TS3W.exe 1.67 instead (ts3addr-retail167.h, docs/273): Steam's
// code and features at other addresses; EA-only blocks (`#if defined(TS3A_TABLE) && !defined(TS3A_RETAIL)`) stay out.
// TS3A_PICK(steam, ea, retail): a per-build constant that is not an address (code hash, operand byte); retail values are
// checked against the retail exe by tools/retail-port-check.ts.
// No raw-on-EA escape (the old TS3A_R): every literal needs a row in both tables, 0 if unresolved (tools/ts3addr-check.ts).
#pragma once
#ifndef TS3A_TABLE
#define TS3A(x) (x)
// the host build this asi's addresses belong to; any other exe (retail 1.67: same code, other layout, docs/230) gets nothing
#define TS3A_HOST_PE 0x52DEC247
#define TS3A_HOST_SIZE 0x00E5A000
#define TS3A_HOST_NAME "Steam 1.67"
#define TS3A_PICK(steam, ea, retail) steam
#else
#ifdef TS3A_RETAIL
#include "ts3addr-retail167.h"  // ts3addr_pairs[][2] sorted by Steam address, ts3addr_count, TS3A_HOST_*
#define TS3A_PICK(steam, ea, retail) retail
// every TS3A row a feature reads is checked against the retail exe (tools/retail-port-check.ts), so the features EA 1.69
// stands down for lack of rows run here; one whose retail row is 0 has its own TS3A_RETAIL stand-down (docs/273 §8)
static volatile bool ts3a_noaddr = false;
#else
#include "ts3addr-ea169.h"  // ts3addr_pairs[][2] sorted by Steam address, ts3addr_count, TS3A_HOST_*
#define TS3A_PICK(steam, ea, retail) ea
// EA-port-only features (ported from hotfix/v0.2.10-ea169, not in the Steam asi): [ImageGuard], [EaCrashFixes] SimNoTexture /
// SimNoTextureAtlas. Their TS3A literals sit outside any `#ifdef TS3A_TABLE` block, so the table regen maps them like the rest;
// the selftest defines it too and tests them with its own dummies.
#define TS3N_EA_PORT
// a feature whose addresses have a 0 row (`// unresolved` in ts3addr-ea169.h, docs/232 §8) returns early behind this runtime
// flag, so /W4 /WX sees no unreachable code; its SKIP line names the feature and "no EA 1.69 address"
static volatile bool ts3a_noaddr = true;
#endif
constexpr unsigned long ts3addr_find(unsigned long x, int lo, int hi) {
    return lo > hi ? throw "TS3A: address not in the build's table (rerun tools/port-run.ts --table, docs/228)"
         : ts3addr_pairs[(lo + hi) / 2][0] == x ? ts3addr_pairs[(lo + hi) / 2][1]
         : ts3addr_pairs[(lo + hi) / 2][0] < x ? ts3addr_find(x, (lo + hi) / 2 + 1, hi)
         : ts3addr_find(x, lo, (lo + hi) / 2 - 1);
}
template <unsigned long V> struct ts3addr_c { static constexpr unsigned long v = V; };
// keeps the literal's own type (int, unsigned) so comparisons and /W4 /WX warnings match the default build
#define TS3A(x) ((decltype(x))ts3addr_c<ts3addr_find((x), 0, ts3addr_count - 1)>::v)
#endif

#ifdef _WINNT_
// docs/230: the host exe this asi's addresses belong to (PE timestamp + SizeOfImage). A tool that appends a section after
// EA's image (an import adder for an ASI loader writes `.addImp` there) grows SizeOfImage but moves no address, so the
// exe still passes when every section that starts below TS3A_HOST_SIZE ends inside it and one ends in its last page (EA's
// last section; EA's `.ooa` may also run past it). Retail 1.67 (PE 52D872DA, SizeOfImage 0xE5B000, data +0x1000, docs/273) fails on the timestamp already.
static inline bool Ts3HostImageOk(const IMAGE_NT_HEADERS* nt) {
    if (nt->FileHeader.TimeDateStamp != (DWORD)TS3A_HOST_PE) return false;
    const DWORD size = nt->OptionalHeader.SizeOfImage, host = (DWORD)TS3A_HOST_SIZE;
    if (size == host) return true;
    if (size < host) return false;
    const IMAGE_SECTION_HEADER* s = IMAGE_FIRST_SECTION(nt);
    bool lastPage = false;
    for (WORD i = 0; i < nt->FileHeader.NumberOfSections; i++) {
        const DWORD va = s[i].VirtualAddress, len = s[i].Misc.VirtualSize ? s[i].Misc.VirtualSize : s[i].SizeOfRawData;
        if (va >= host) continue;              // appended after EA's image
        // EA's DRM stub `.ooa` (last section, no patched address in it) may grow with a rewrap: a tester's EA app 1.69 exe
        // has 0xEB7000 with the same timestamp and the same code (docs/230); any other section running past = another build
        const bool ooa = s[i].Name[0] == '.' && s[i].Name[1] == 'o' && s[i].Name[2] == 'o' && s[i].Name[3] == 'a' && !s[i].Name[4];
        if (len > host - va && !ooa) return false;
        if (va + len > host - 0x1000) lastPage = true;
    }
    return lastPage;
}
#endif
