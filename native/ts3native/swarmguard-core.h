// docs/678: [SwarmGuard] decisions (swarmguard.cpp), free of Windows so swarmguard-test.h runs them on fakes.
// A cVisualEffect (0x280 bytes, built by 0x92FA00) starts with three vtable words: +0 its own, +4 and +8 of its interface
// base. The constructor is the only writer of the three and no other class shares the +0 / +8 pair, so an object whose
// +0 and +8 are exactly the constructor's values is a live cVisualEffect and its +4 has exactly one right value.
#pragma once

struct SwgVt { unsigned long vt0, vt4, vt8; };
enum { SWG_OK = 0, SWG_FOREIGN = 1, SWG_BAD = 2 };
enum { SWG_SEEN = 16, SWG_SCAN_MAX = 8192 };
struct SwgSeen { unsigned long e, word; };
struct SwgStats { long calls, bad, skipped, repaired, foreign, scans, scanBad, lines, births; };

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

// docs/678 §8 (Trace=1): the birth ring. One entry per cVisualEffect allocation (0x91A8CC): the block's address, when,
// which thread, and the block's first words as the allocator handed it out, BEFORE the constructor ran: what the last
// owner of that address left there. If the +4 writer is a list neighbour of an object that used to live at the effect's
// address, these words are that object's remains (its +8 would be its other link).
enum { SWG_BIRTHS = 1024, SWG_BIRTH_WORDS = 8 };
struct SwgBirth { unsigned long e, tick, tid, seq, w[SWG_BIRTH_WORDS]; };

static void SwgBirthPut(SwgBirth* ring, unsigned* next, unsigned long e, unsigned long tick, unsigned long tid, const unsigned long* raw) {
    SwgBirth& b = ring[*next % SWG_BIRTHS];
    b.e = e; b.tick = tick; b.tid = tid; b.seq = *next + 1;
    for (int i = 0; i < SWG_BIRTH_WORDS; i++) b.w[i] = raw ? raw[i] : 0;
    (*next)++;
}

// the newest entry for address e among the last SWG_BIRTHS allocations, or null
static const SwgBirth* SwgBirthFind(const SwgBirth* ring, unsigned next, unsigned long e) {
    const unsigned n = next < SWG_BIRTHS ? next : SWG_BIRTHS;
    for (unsigned i = 1; i <= n; i++) {
        const SwgBirth& b = ring[(next - i) % SWG_BIRTHS];
        if (b.e == e) return &b;
    }
    return nullptr;
}

// The nearest word below `at` (at most maxWords words down) whose value is 4-aligned and lies in [lo, hi), the image's
// read-only data: a vtable pointer, so the start of the object that holds `at`. Returns the distance in bytes (4 = the
// word just before), 0 = none; *val = the word.
static unsigned SwgVtBefore(const unsigned long* at, unsigned maxWords, unsigned long lo, unsigned long hi, unsigned long* val) {
    *val = 0;
    for (unsigned i = 1; i <= maxWords; i++) {
        const unsigned long v = *(at - i);
        if (v >= lo && v < hi && !(v & 3)) { *val = v; return i * 4; }
    }
    return 0;
}
