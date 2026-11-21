// [OutfitFirst] (docs/684 §9): which SimService build jobs belong to a live outfit swap that a script task is waiting for,
// and the choice among the ready jobs. Plain reads and two list writes on the job ring; no game calls, no Windows calls.
// Included by thumbstall.cpp (the hooks, under __try) and outfitfirst-host-test.cpp. 32-bit only: an address is an unsigned.
//
// The ready ring (SimService +0x60) [verified, thumbstall.cpp header]: a node's +0 is the next node, +4 the one before,
// +8 the LOD, +0x18 the provider OBJECT the job builds for; the ring's head is a node-shaped pair of links.
#pragma once

enum { kOfSlots = 32, kOfMaxWalk = 4096 };

struct OfSlot {
    unsigned obj;     // the provider object of a pending live swap; 0 = free
    unsigned noted;   // tick the swap was set up
    unsigned ready;   // tick its job was first seen in the ready ring (valid with seen)
    int passed;       // jobs the game's own pick took while this one sat ready
    bool seen;
};
struct OfDone { unsigned obj, waitMs, readyMs; int passed, lod, pos; };
struct OfTable {
    OfSlot slot[kOfSlots];
    long notes, renotes, evicted, expired, first, ea, passes;   // swaps noted / again while pending / pushed out / timed out /
                                                                // jobs we took first / jobs the game took / jobs that went past
};

static int OfFind(const OfTable* t, unsigned obj) {
    if (!obj) return -1;
    for (int i = 0; i < kOfSlots; i++)
        if (t->slot[i].obj == obj) return i;
    return -1;
}

// A live swap was set up for obj at now. A second swap of an object still pending keeps the first one's times.
static void OfNote(OfTable* t, unsigned obj, unsigned now) {
    if (!obj) return;
    if (OfFind(t, obj) >= 0) { t->renotes++; return; }
    int at = -1, oldest = 0;
    for (int i = 0; i < kOfSlots && at < 0; i++) {
        if (!t->slot[i].obj) at = i;
        else if (now - t->slot[i].noted > now - t->slot[oldest].noted) oldest = i;
    }
    if (at < 0) { at = oldest; t->evicted++; }
    OfSlot* const s = &t->slot[at];
    s->obj = obj, s->noted = now, s->ready = 0, s->passed = 0, s->seen = false;
    t->notes++;
}

// Swaps noted maxAgeMs ago or longer go: the LOD was in the cache (no job was ever made), or the swap was cancelled.
static void OfExpire(OfTable* t, unsigned now, unsigned maxAgeMs) {
    for (int i = 0; i < kOfSlots; i++)
        if (t->slot[i].obj && now - t->slot[i].noted >= maxAgeMs) { t->slot[i].obj = 0; t->expired++; }
}

// Walks the ready ring at head: every noted object's job is marked as seen ready (at now), and the job of the swap noted
// first is returned with its place in *pos (0 = the front). Null: no noted object has a ready job.
static unsigned char* OfChoose(OfTable* t, unsigned char* head, unsigned now, int* pos) {
    unsigned char* best = 0;
    unsigned bestAge = 0;
    int at = 0;
    *pos = -1;
    for (unsigned char* n = *(unsigned char**)head; n && n != head && at < kOfMaxWalk; n = *(unsigned char**)n, at++) {
        const int i = OfFind(t, *(const unsigned*)(n + 0x18));
        if (i < 0) continue;
        OfSlot* const s = &t->slot[i];
        if (!s->seen) { s->seen = true; s->ready = now; }
        const unsigned age = now - s->noted;
        if (!best || age > bestAge) { best = n; bestAge = age; *pos = at; }
    }
    return best;
}

// Takes node out of its ring, as the game's own pick does with its choice.
static void OfUnlink(unsigned char* node) {
    unsigned char* const next = *(unsigned char**)node;
    unsigned char* const prev = *(unsigned char**)(node + 4);
    *(unsigned char**)prev = next;
    *(unsigned char**)(next + 4) = prev;
}

// A job was taken for building: by us (first) or by the game's own pick. A noted object's job closes its entry and is
// described in *d (true). Any other job went past every noted job that sat ready: their counts go up (false).
static bool OfTaken(OfTable* t, const unsigned char* node, unsigned now, bool first, OfDone* d) {
    const unsigned obj = *(const unsigned*)(node + 0x18);
    const int i = OfFind(t, obj);
    if (i < 0) {
        for (int k = 0; k < kOfSlots; k++)
            if (t->slot[k].obj && t->slot[k].seen) { t->slot[k].passed++; t->passes++; }
        return false;
    }
    OfSlot* const s = &t->slot[i];
    d->obj = obj;
    d->waitMs = now - s->noted;
    d->readyMs = s->seen ? now - s->ready : 0;
    d->passed = s->passed;
    d->lod = *(const int*)(node + 8);
    d->pos = -1;
    s->obj = 0;
    if (first) t->first++;
    else t->ea++;
    return true;
}
