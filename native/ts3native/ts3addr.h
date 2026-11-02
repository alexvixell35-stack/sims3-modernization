// TS3A(x): a TS3W.exe address, written as its Steam 1.67 value (docs/68 §7.1, tools/addr-codemod.ts).
// Default build: (x), so the Steam binary stays byte-identical. With /DTS3A_TABLE the value comes from
// out/ts3addr-table.h (node tools/addr-codemod.ts gen --map out/exe-addr-map/<exe>.csv; gitignored, derived from EA bytes).
// The lookup is constexpr and forced to compile time, so static initializers stay constant and an address missing
// from the table is a compile error, never a wrong pointer at run time.
#pragma once
#ifndef TS3A_TABLE
#define TS3A(x) (x)
#else
#include "out/ts3addr-table.h"  // ts3addr_pairs[][2] sorted by Steam address, ts3addr_count
constexpr unsigned long ts3addr_find(unsigned long x, int lo, int hi) {
    return lo > hi ? throw "TS3A: address not in out/ts3addr-table.h (run tools/addr-codemod.ts gen)"
         : ts3addr_pairs[(lo + hi) / 2][0] == x ? ts3addr_pairs[(lo + hi) / 2][1]
         : ts3addr_pairs[(lo + hi) / 2][0] < x ? ts3addr_find(x, (lo + hi) / 2 + 1, hi)
         : ts3addr_find(x, lo, (lo + hi) / 2 - 1);
}
template <unsigned long V> struct ts3addr_c { static constexpr unsigned long v = V; };
// keeps the literal's own type (int, unsigned) so comparisons and /W4 /WX warnings match the default build
#define TS3A(x) ((decltype(x))ts3addr_c<ts3addr_find((x), 0, ts3addr_count - 1)>::v)
#endif
