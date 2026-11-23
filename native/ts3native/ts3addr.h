// TS3A(x): a TS3W.exe address, written as its Steam 1.67 value (docs/68 §7.1, tools/addr-codemod.ts).
// Default build: (x), so the Steam binary stays byte-identical. With /DTS3A_TABLE the value comes from the committed
// ts3addr-ea169.h (EA app 1.69.47; node tools/port-run.ts --table ..., docs/228: addresses only, no exe bytes), which also
// names the host build (TS3A_HOST_PE/SIZE/NAME) for the host check in ts3native.cpp. The lookup is constexpr and forced
// to compile time, so static initializers stay constant and an address missing from the table is a compile error, never
// a wrong pointer at run time. A 0 row = unresolved on that build (listed as `// unresolved` in the header): its code
// patch fails the `Matches` byte check and logs SKIP; a data read at 0 would crash, so its ini section stays off there.
#pragma once
#ifndef TS3A_TABLE
#define TS3A(x) (x)
// the host build this asi's addresses belong to; any other exe (retail 1.67: same code, other layout, docs/230) gets nothing
#define TS3A_HOST_PE 0x52DEC247
#define TS3A_HOST_SIZE 0x00E5A000
#define TS3A_HOST_NAME "Steam 1.67"
#else
#include "ts3addr-ea169.h"  // ts3addr_pairs[][2] sorted by Steam address, ts3addr_count, TS3A_HOST_*
// EA-port-only features (ported from hotfix/v0.2.10-ea169, not in the Steam asi): [ImageGuard], [EaCrashFixes] SimNoTexture /
// SimNoTextureAtlas. Their TS3A literals sit outside any `#ifdef TS3A_TABLE` block, so the table regen maps them like the rest;
// the selftest defines it too and tests them with its own dummies.
#define TS3N_EA_PORT
// a feature whose addresses have a 0 row (`// unresolved` in ts3addr-ea169.h, docs/232 §8) returns early behind this runtime
// flag, so /W4 /WX sees no unreachable code; its SKIP line names the feature and "no EA 1.69 address"
static volatile bool ts3a_noaddr = true;
constexpr unsigned long ts3addr_find(unsigned long x, int lo, int hi) {
    return lo > hi ? throw "TS3A: address not in ts3addr-ea169.h (rerun tools/port-run.ts --table, docs/228)"
         : ts3addr_pairs[(lo + hi) / 2][0] == x ? ts3addr_pairs[(lo + hi) / 2][1]
         : ts3addr_pairs[(lo + hi) / 2][0] < x ? ts3addr_find(x, (lo + hi) / 2 + 1, hi)
         : ts3addr_find(x, lo, (lo + hi) / 2 - 1);
}
template <unsigned long V> struct ts3addr_c { static constexpr unsigned long v = V; };
// keeps the literal's own type (int, unsigned) so comparisons and /W4 /WX warnings match the default build
#define TS3A(x) ((decltype(x))ts3addr_c<ts3addr_find((x), 0, ts3addr_count - 1)>::v)
#endif
