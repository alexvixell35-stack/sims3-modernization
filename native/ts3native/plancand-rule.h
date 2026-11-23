// docs/644: the pure decisions of plancand.cpp ([RouteAsync] CandidateCut, WaterGoalCheck). No Windows types and no game
// memory here, so the rules also build and run as a plain host test (selftest's TestPlanCand runs them in the plugin build).
#pragma once
#include <stdint.h>

// router scope as ts3fixes reports it (KdScopeTag): 0 unknown, 1 household or seen, 2 unseen, 3 seen NPC. Only a router known to
// be an NPC (2, 3) may be cut; unknown counts as household (AGENTS.md). Deliberately not HhExempt: [Experimental] HouseholdToo
// lifts the exemption on realistic-only sites, never on cuts.
static inline bool PccScopeCuttable(int32_t scope) { return scope == 2 || scope == 3; }

enum { PCC_PASS = 0, PCC_CUT = 1, PCC_EXEMPT = 2 };
// mode = CandidateCut runtime switch; retOk = the helper was called from one of its listed planner sites; past = this plan's
// (or job's) deadline is past. PCC_EXEMPT = it would have been cut, but the router is household, selected or unknown.
static inline int PccRule(int32_t mode, bool retOk, bool past, int32_t scope) {
    if (mode != 1 || !retOk || !past) return PCC_PASS;
    return PccScopeCuttable(scope) ? PCC_CUT : PCC_EXEMPT;
}

static inline bool PccRetIn(uint32_t ret, const uint32_t* rets, int n) {
    for (int i = 0; i < n; i++)
        if (rets[i] && rets[i] == ret) return true;
    return false;
}

// WaterPath_Plan 0x671930 inputs, read from the water map (this) and the route by the caller (plancand.cpp WgcLoad)
struct WgcView {
    uint32_t cells;               // +0x0C: last valid cell index (the game's `ja` test: index <= cells passes)
    const uint8_t* body;          // +0x1C: 2 bytes per cell, the first = water body id (0 = none)
    uint32_t bodyW;               // +0x04: cells per row of the cell index the planner passes
    uint32_t gw, gh;              // +0x7A0 / +0x7A4: passability grid width / height
    const uint8_t* const* grid;   // +0x7B0: grid per boat tier 0..4 (16-byte descriptors, data pointer first)
    uint32_t routeFlags;          // **(route + 0x8C)
};

// the boat tier 0x671930 starts from (0x6719A6..0x6719EF: 0x624150 / 0x55DA60 are pure compares)
static inline int WgcTier(uint32_t flags) {
    const uint32_t f = flags & 0xCF00;
    if (f == 0x4100) return 2;
    if (f == 0x4200) return 3;
    if (f == 0x4300) return 4;
    return f == 0xC00 || f == 0xD00 || f == 0xE00 || f == 0xF00 || f == 0x4500 || f == 0x4600 ? 1 : 0;
}

// true = the game's search 0x620560 cannot succeed: the start cell is water in the chosen grid (so every jump is a water step,
// docs/644 §4), start != goal, and the goal cell is not water there. Every case the game rejects by itself, or where the
// answer is not certain, returns false (the game runs as before).
static inline bool WgcWouldReject(const WgcView& v, uint32_t s, uint32_t g) {
    if (s > v.cells || g > v.cells || !v.body || !v.grid || !v.bodyW) return false;
    const uint8_t bs = v.body[s * 2];
    if (!bs || bs != v.body[g * 2]) return false;  // "not the same water body": the game's own exit 0x671B98
    int tier = WgcTier(v.routeFlags);
    const uint32_t sx = s % v.bodyW, sy = s / v.bodyW, gx = g % v.bodyW, gy = g / v.bodyW;
    while (tier > 0) {  // 0x671A3E..0x671A6D: highest tier whose start cell is passable, tier 0 untested
        if (v.grid[tier] && v.grid[tier][v.gw * sy + sx]) break;
        tier--;
    }
    if (sx >= v.gw || sy >= v.gh || gx >= v.gw || gy >= v.gh) return false;  // the search's own bounds exit
    const uint32_t si = sy * v.gw + sx, gi = gy * v.gw + gx;
    const uint8_t* g1 = v.grid[tier];
    if (si == gi || !g1 || !g1[si]) return false;  // same cell: found at once; start on land: one-step land moves (flag case)
    return g1[gi] == 0;
}
