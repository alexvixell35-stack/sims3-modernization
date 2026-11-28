// docs/679 [WaypointSnap]: the parts of wpsnap.cpp without the game or Windows (wpsnap-test.h, wpsnap-host-test.cpp).
//  - WpsTab: a per-thread memo of World_ArePointsConnectedByLand answers. A row is (the four coordinate words as passed,
//    the context word, the epoch word, a stamp in ms) -> answer. A row answers only when all of them match bit for bit and
//    the row is younger than the cap; nothing is rounded, so two points that differ in the last bit are two rows.
//  - WpsBurst: calls of one thread that follow each other within a gap count as one burst (one managed loop).
#pragma once

enum { WPS_SLOTS = 512 };  // rows per thread, two per bucket; a power of two

struct WpsKey {
    unsigned int w[4];  // ax, az, bx, bz as the 32-bit words on the stack
};
struct WpsRow {
    WpsKey k;
    unsigned int ctx;    // hash of the routing objects the answer was read from
    unsigned int epoch;  // routing write sequence at the call's start (unchanged at its end)
    unsigned int ms;     // when it was stored
    unsigned char used, ans;
};
struct WpsTab {
    WpsRow r[WPS_SLOTS];
};

static unsigned int WpsHash(const WpsKey& k) {
    unsigned int h = 0x9E3779B9u;
    for (int i = 0; i < 4; i++) {
        h ^= k.w[i] + 0x7F4A7C15u + (h << 6) + (h >> 2);
        h *= 0x85EBCA6Bu;
        h ^= h >> 15;
    }
    return h;
}
static bool WpsKeyEq(const WpsKey& a, const WpsKey& b) { return a.w[0] == b.w[0] && a.w[1] == b.w[1] && a.w[2] == b.w[2] && a.w[3] == b.w[3]; }
static WpsRow* WpsBucket(WpsTab* t, const WpsKey& k) { return &t->r[(WpsHash(k) & (WPS_SLOTS / 2 - 1)) * 2]; }

// true = *ans is the stored answer of exactly this question in exactly this state, at most capMs old
static bool WpsGet(WpsTab* t, const WpsKey& k, unsigned int ctx, unsigned int epoch, unsigned int nowMs, unsigned int capMs, bool* ans) {
    WpsRow* b = WpsBucket(t, k);
    for (int i = 0; i < 2; i++) {
        const WpsRow& r = b[i];
        if (!r.used || !WpsKeyEq(r.k, k) || r.ctx != ctx || r.epoch != epoch) continue;
        if (nowMs - r.ms > capMs) continue;  // unsigned: a wrapped tick count ages the row out, never keeps it
        *ans = r.ans != 0;
        return true;
    }
    return false;
}
// the row of this key is replaced, else a free one is taken, else the older of the two
static void WpsPut(WpsTab* t, const WpsKey& k, unsigned int ctx, unsigned int epoch, unsigned int nowMs, bool ans) {
    WpsRow* b = WpsBucket(t, k);
    WpsRow* pick = NULL;
    for (int i = 0; i < 2 && !pick; i++)
        if (b[i].used && WpsKeyEq(b[i].k, k)) pick = &b[i];
    for (int i = 0; i < 2 && !pick; i++)
        if (!b[i].used) pick = &b[i];
    if (!pick) pick = nowMs - b[0].ms >= nowMs - b[1].ms ? &b[0] : &b[1];
    pick->k = k;
    pick->ctx = ctx;
    pick->epoch = epoch;
    pick->ms = nowMs;
    pick->ans = ans ? 1 : 0;
    pick->used = 1;
}

struct WpsBurst {
    long long endUs;  // end of the last call
    long long us;     // time inside the calls
    unsigned int calls, hits, gathers, cands, most;  // most = most candidates of one gather
};
// Called at the start of a call. true = the burst before this call is over (gap exceeded) and *done holds it; b is reset.
static bool WpsBurstStart(WpsBurst* b, long long startUs, long long gapUs, WpsBurst* done) {
    if (!b->calls || startUs - b->endUs <= gapUs) return false;
    *done = *b;
    b->us = 0;
    b->calls = b->hits = b->gathers = b->cands = b->most = 0;
    return true;
}
static void WpsBurstEnd(WpsBurst* b, long long endUs, long long us, bool hit) {
    b->calls++;
    b->hits += hit ? 1 : 0;
    b->us += us;
    b->endUs = endUs;
}
