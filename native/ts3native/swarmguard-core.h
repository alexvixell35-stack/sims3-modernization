// docs/678: [SwarmGuard] decisions (swarmguard.cpp), free of Windows so swarmguard-test.h runs them on fakes.
// A cVisualEffect (0x280 bytes, built by 0x92FA00) starts with three vtable words: +0 its own, +4 and +8 of its interface
// base. The constructor is the only writer of the three and no other class shares the +0 / +8 pair, so an object whose
// +0 and +8 are exactly the constructor's values is a live cVisualEffect and its +4 has exactly one right value.
#pragma once

struct SwgVt { unsigned long vt0, vt4, vt8; };
enum { SWG_OK = 0, SWG_FOREIGN = 1, SWG_BAD = 2 };
enum { SWG_SEEN = 16, SWG_SCAN_MAX = 8192 };
struct SwgSeen { unsigned long e, word; };
struct SwgStats { long calls, bad, skipped, repaired, foreign, scans, scanBad, lines; };

// SWG_OK: all three words are the constructor's. SWG_BAD: +0 and +8 are, +4 is not (the docs/678 corruption).
// SWG_FOREIGN: +0 or +8 differ (not this class, or mid-destruction): not ours to judge, the game's code runs as before.
static int SwgCheck(const unsigned long* e, const SwgVt& v) {
    if (e[0] != v.vt0 || e[2] != v.vt8) return SWG_FOREIGN;
    return e[1] == v.vt4 ? SWG_OK : SWG_BAD;
}

// One update call of effect e. Returns 1 = skip the game's update of this effect for this frame, 0 = run it.
// repair: put the constructor's word back and run the update. *badWord = the word found at +4 when SWG_BAD.
static int SwgDecide(unsigned long* e, const SwgVt& v, bool repair, SwgStats* st, unsigned long* badWord) {
    st->calls++;
    *badWord = 0;
    const int r = SwgCheck(e, v);
    if (r == SWG_OK) return 0;
    if (r == SWG_FOREIGN) { st->foreign++; return 0; }
    st->bad++;
    *badWord = e[1];
    if (repair) { e[1] = v.vt4; st->repaired++; return 0; }
    st->skipped++;
    return 1;
}

// The group's whole effect vector (group +0x30 / +0x34: every pooled effect, null where an allocation failed), checked
// without waiting for an effect to be updated. Returns the number of bad effects; *firstBad = the first one, its word
// in *firstWord. Repairs when asked. Stops after SWG_SCAN_MAX entries (a vector that long is not a sane group).
static int SwgScan(unsigned long** begin, unsigned long** end, const SwgVt& v, bool repair, SwgStats* st, unsigned long** firstBad,
                  unsigned long* firstWord) {
    int bad = 0, n = 0;
    *firstBad = nullptr;
    *firstWord = 0;
    st->scans++;
    for (unsigned long** p = begin; p < end && n < SWG_SCAN_MAX; p++, n++) {
        unsigned long* e = *p;
        if (!e || SwgCheck(e, v) != SWG_BAD) continue;
        if (!bad) { *firstBad = e; *firstWord = e[1]; }
        bad++;
        st->scanBad++;
        if (repair) { e[1] = v.vt4; st->repaired++; }
    }
    return bad;
}

// true the first time this (effect, word) pair is seen among the last SWG_SEEN pairs: the full dump is logged once per
// pair, not once per frame while a skipped effect stays bad
static bool SwgFirstSight(SwgSeen* ring, unsigned* next, unsigned long e, unsigned long word) {
    for (int i = 0; i < SWG_SEEN; i++)
        if (ring[i].e == e && ring[i].word == word) return false;
    ring[*next % SWG_SEEN].e = e;
    ring[*next % SWG_SEEN].word = word;
    (*next)++;
    return true;
}
