// docs/673: the pure part of [RouteJobPhases] HoldProbe (workerhold.cpp): buckets, the per-job boundary record and the
// duplicate ring. No Windows, no game: workerhold-host-test.cpp and the selftest run it as is.
#ifndef WORKERHOLD_CORE_H
#define WORKERHOLD_CORE_H

#include <stdint.h>

// a point of a worker job where no search of the plan is running: the start of a plan, of a candidate (LotEdgePortals_Choose),
// of a lot leg (LotPlan), of the world A*, of the water plan and of the post pass
enum { WH_PLAN, WH_CAND, WH_LOTLEG, WH_WORLD, WH_WATER, WH_POST, WH_KINDS };
enum { WH_JOBB = 6, WH_CANDB = 4, WH_DUPS = 32 };

static inline int WhJobBucket(long us) {  // < 5, < 20, < 50, < 100, < 250, >= 250 ms
    static const long kEdge[WH_JOBB - 1] = {5000, 20000, 50000, 100000, 250000};
    int b = 0;
    while (b < WH_JOBB - 1 && us >= kEdge[b]) b++;
    return b;
}
static inline int WhCandBucket(long cands) { return cands <= 1 ? 0 : cands == 2 ? 1 : cands < 10 ? 2 : 3; }  // 0-1, 2, 3-9, >= 10

struct WhJob {
    bool open;
    int firstKind;       // the first boundary this job reached while the sim thread waited on +0x918 (-1 = none)
    long long firstQ;    // its clock
    long plans, fails;   // PlanHierarchical calls that ended in this job, and those with a code other than 0
    long long phQ;       // their inclusive clock ticks (outermost calls)
    long seen[WH_KINDS], waiting[WH_KINDS];
};

static inline void WhJobReset(WhJob* j, bool open) {
    j->open = open, j->firstKind = -1, j->firstQ = 0, j->plans = j->fails = 0, j->phQ = 0;
    for (int i = 0; i < WH_KINDS; i++) j->seen[i] = j->waiting[i] = 0;
}
// 1 = this is the job's first boundary with the sim waiting
static inline int WhBoundary(WhJob* j, int kind, bool simWaits, long long nowQ) {
    if (!j->open || kind < 0 || kind >= WH_KINDS) return 0;
    j->seen[kind]++;
    if (!simWaits) return 0;
    j->waiting[kind]++;
    if (j->firstKind >= 0) return 0;
    j->firstKind = kind, j->firstQ = nowQ;
    return 1;
}

// the last jobs that ended, by route handle and IC kind: a job that starts within `windowMs` of the end of a job with the
// same pair is a repeat. One slot per pair (the newest end), the oldest slot is replaced
struct WhDup { uint32_t handle, endTick; int kind; long us; };
struct WhDupRing { WhDup e[WH_DUPS]; uint32_t n, at; };

static inline int WhDupFind(const WhDupRing* r, uint32_t handle, int kind) {
    for (uint32_t i = 0; i < r->n; i++)
        if (r->e[i].handle == handle && r->e[i].kind == kind) return (int)i;
    return -1;
}
// 1 = a repeat (the earlier job's time in *prevUs)
static inline int WhDupNote(WhDupRing* r, uint32_t handle, int kind, uint32_t startTick, uint32_t endTick, long us, uint32_t windowMs,
                            long* prevUs) {
    int i = WhDupFind(r, handle, kind);
    int rep = 0;
    if (i >= 0) {
        if ((uint32_t)(startTick - r->e[i].endTick) <= windowMs) rep = 1, *prevUs = r->e[i].us;
    } else if (r->n < WH_DUPS) {
        i = (int)r->n++;
    } else {
        i = (int)r->at;
        r->at = (r->at + 1) % WH_DUPS;
    }
    r->e[i].handle = handle, r->e[i].kind = kind, r->e[i].endTick = endTick, r->e[i].us = us;
    return rep;
}

#endif
