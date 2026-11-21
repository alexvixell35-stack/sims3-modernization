// Shadow mode of the read-only clones ([ObstructAtRO], [Blocker2dRO], [LandCheckRO]; docs/670 §26): the order of one
// compared call, and the private copy of the closest-waypoint area. No Windows or game type here, so
// roshadow-host-test.cpp runs it as it is.
//
// Rule: with Shadow=1 the game's call runs exactly as with the switch off (no frame of ours open around it, nothing
// written before it), and the clone works on copies only. Job starts are held off for the clone alone; when a job ran
// or runs between the game's call and the clone, the two answers are not from one world state and are not compared.
#pragma once

enum { RSH_COMPARED, RSH_JOB };

// Env: unsigned long JobRuns() (jobs the worker committed to run so far); bool JobLive(); void HoldStarts();
// void ReleaseStarts() (the worker starts no job between the two).
template <class Env, class Game, class Clone> static int RoShadow(Env& e, Game game, Clone clone) {
    const unsigned long runs = e.JobRuns();  // read before the job state
    const bool live = e.JobLive();
    game();
    if (live) return RSH_JOB;
    e.HoldStarts();
    const bool quiet = !e.JobLive() && e.JobRuns() == runs;
    if (quiet) clone();
    e.ReleaseStarts();
    return quiet ? RSH_COMPARED : RSH_JOB;
}

// The area of the closest-waypoint gather (docs/670 §24.2): the gather reads +0x00..+0x2F and +0x40, resets and fills the
// candidate vector {begin +0x30, end +0x34, capacity +0x38, allocator tag +0x3C} (32-byte records, grown through the
// game's allocator) and clears flag 0x400 at +0x2C. Pointers are 4 bytes (the 32-bit game and the -m32 host test).
enum { RSH_AREA_SIZE = 0x44, RSH_AREA_FLAGS = 0x2C, RSH_AREA_VEC = 0x30, RSH_AREA_TAG = 0x3C, RSH_CAND = 32 };
struct RshArea {
    unsigned char b[RSH_AREA_SIZE];
};
// the gather's inputs, taken before the game's call
static inline void RshAreaSnap(RshArea* s, const unsigned char* area) {
    for (int i = 0; i < RSH_AREA_SIZE; i++) s->b[i] = area[i];
}
// the private copy gets the inputs; its vector words stay its own (never the live area's storage)
static inline void RshAreaLoad(unsigned char* scratch, const RshArea* s) {
    for (int i = 0; i < RSH_AREA_VEC; i++) scratch[i] = s->b[i];
    for (int i = RSH_AREA_TAG; i < RSH_AREA_SIZE; i++) scratch[i] = s->b[i];
}
// candidate count and FNV-1a over the records; 0 for a vector whose words make no sense
static inline unsigned long RshAreaHash(const unsigned char* area, unsigned long* count) {
    const unsigned char* b = *(const unsigned char* const*)(area + RSH_AREA_VEC);
    const unsigned char* e = *(const unsigned char* const*)(area + RSH_AREA_VEC + 4);
    *count = 0;
    if (e < b || (unsigned long)(e - b) > 0x400000) return 0;  // an empty vector (also one with no storage yet) hashes as empty
    *count = (unsigned long)(e - b) / RSH_CAND;
    unsigned long h = 2166136261u;
    for (const unsigned char* p = b; p < e; p++) h = (h ^ *p) * 16777619u;
    return h;
}
// the private copy's storage, taken out for the caller to free with the allocator that made it (NULL = none)
static inline unsigned char* RshAreaTakeStorage(unsigned char* scratch) {
    unsigned char** v = (unsigned char**)(scratch + RSH_AREA_VEC);
    unsigned char* b = v[0];
    v[0] = v[1] = v[2] = 0;
    return b;
}
