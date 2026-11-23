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

// docs/673 §9: a job's time by part. The job is one route IC: start points, goal generation and the goal step, then
// Route_PlanLocked (our detour's outermost call: a plan nested in a managed call is inside it), then the result mapping
// and the EditPath tail. A host-call wait belongs to the part it was posted in.
enum { WH_PRE, WH_IN, WH_TAIL, WH_PARTS };  // before the first or between Route_PlanLocked calls, inside one, after the last
struct WhSplit {
    int depth;                  // open Route_PlanLocked calls
    long plans;                 // outermost calls that returned
    long long markQ;            // the job's start, or the last outermost entry / return
    long long partQ[WH_PARTS];  // clock ticks by part (WH_TAIL is set at the IC's end)
    long long hostQ[WH_PARTS];  // host-call waits by part
    long hostN[WH_PARTS];
    bool endWait;               // the sim thread waited on +0x918 at the last outermost return
};
static inline void WhSplitReset(WhSplit* s, long long nowQ) {
    s->depth = 0, s->plans = 0, s->markQ = nowQ, s->endWait = false;
    for (int i = 0; i < WH_PARTS; i++) s->partQ[i] = s->hostQ[i] = 0, s->hostN[i] = 0;
}
static inline int WhSplitPart(const WhSplit* s) { return s->depth > 0 ? WH_IN : s->plans ? WH_TAIL : WH_PRE; }
static inline void WhSplitEnter(WhSplit* s, long long nowQ) {
    if (s->depth++) return;
    s->partQ[WH_PRE] += nowQ - s->markQ, s->markQ = nowQ;
    // what looked like the tail was a gap between two plans
    s->hostQ[WH_PRE] += s->hostQ[WH_TAIL], s->hostN[WH_PRE] += s->hostN[WH_TAIL], s->hostQ[WH_TAIL] = 0, s->hostN[WH_TAIL] = 0;
}
// 1 = an outermost call returned
static inline int WhSplitLeave(WhSplit* s, long long nowQ, bool simWaits) {
    if (s->depth <= 0 || --s->depth) return 0;
    s->partQ[WH_IN] += nowQ - s->markQ, s->markQ = nowQ, s->plans++, s->endWait = simWaits;
    return 1;
}
static inline void WhSplitHost(WhSplit* s, long long q) {
    const int part = WhSplitPart(s);
    s->hostQ[part] += q, s->hostN[part]++;
}
// the IC returned: the time since the last plan is the tail (with no plan the whole job is WH_PRE)
static inline void WhSplitIcDone(WhSplit* s, long long nowQ) {
    s->partQ[s->plans ? WH_TAIL : WH_PRE] += nowQ - s->markQ, s->markQ = nowQ;
}

struct WhJob {
    bool open;
    WhSplit sp;
    int firstKind;       // the first boundary this job reached while the sim thread waited on +0x918 (-1 = none)
    long long firstQ;    // its clock
    long plans, fails;   // PlanHierarchical calls that ended in this job, and those with a code other than 0
    long long phQ;       // their inclusive clock ticks (outermost calls)
    long seen[WH_KINDS], waiting[WH_KINDS];
};

static inline void WhJobReset(WhJob* j, bool open) {
    j->open = open, j->firstKind = -1, j->firstQ = 0, j->plans = j->fails = 0, j->phQ = 0;
    for (int i = 0; i < WH_KINDS; i++) j->seen[i] = j->waiting[i] = 0;
    WhSplitReset(&j->sp, 0);
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
