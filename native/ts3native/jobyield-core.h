// docs/183 §7 JobYield: the records of a route whose task steps aside. No Windows or game type here, so
// jobyield-host-test.cpp runs it as it is.
//
// ts3fixes sends a route's marks and its START one after the other and, when the START is refused, makes the plan itself at
// once. The sim thread therefore keeps what a refused START leaves for "the IC right after" in single slots:
//   why    the refusal reason (the sync plan's deadline class and its log line);
//   sc     the stuck check's last-round mark (that plan is never cut);
//   scope  ts3fixes' scope answer for the route (household / unseen / seen NPC) with its interaction id;
//   uf     the UnseenFallback mark.
// With JobYield the task sleeps between the refused START and its plan, and other tasks run. Left in the slots, the records
// are (1) overwritten by the next route's marks, so the plan after the sleep runs as a plan nobody asked a START for, and
// (2) read by whatever plans in between: a plan with no route handle takes the scope slot as its own.
// So the first busy answer takes the route's records out of the slots (JypPark), and the answer that ends the sleep puts
// them back (JypBack) right before the plan. A record leaves the table exactly once: back, dropped (the task left its
// sleep by an exception, or the route sent a new START) or expired (the task never came back).
#pragma once

enum { JYP_SLOTS = 8 };
enum { JYP_WHY = 1, JYP_SC = 2, JYP_SCOPE = 4, JYP_UF = 8 };
enum { JYP_FULL = -1 };  // JypPark: no free slot, nothing taken

struct JyRec {
    unsigned handle;  // 0 = free
    unsigned tick;    // ms clock at the last park
    int has;          // JYP_* bits held
    int why;
    long scope, ia;
};
struct JyPark {
    JyRec r[JYP_SLOTS];
    long parked, back, dropped, expired, full;  // records in / put back / dropped / timed out / not parked (no free slot)
};

static JyRec* JypFind(JyPark& p, unsigned handle) {
    if (!handle) return 0;
    for (int i = 0; i < JYP_SLOTS; i++)
        if (p.r[i].handle == handle) return &p.r[i];
    return 0;
}
static bool JypOld(const JyRec& r, unsigned now, unsigned keepMs) { return (unsigned)(now - r.tick) > keepMs; }
static void JypFree(JyRec* r) { r->handle = 0, r->has = 0; }
// a free slot; records older than keepMs go first
static JyRec* JypSlot(JyPark& p, unsigned now, unsigned keepMs) {
    JyRec* f = 0;
    for (int i = 0; i < JYP_SLOTS; i++) {
        JyRec* r = &p.r[i];
        if (r->handle && JypOld(*r, now, keepMs)) { JypFree(r); p.expired++; }
        if (!r->handle && !f) f = r;
    }
    return f;
}

// Env = the sim thread's slots: bool TakeWhy(h, int*), TakeSc(h), TakeScope(h, long* scope, long* ia), TakeUf(h) (each
// true when the slot held route h's record, which it then no longer holds) and PutWhy(h, why), PutSc(h),
// PutScope(h, scope, ia), PutUf(h).

// Route h's task sleeps: its records leave the slots. Returns the JYP_* bits the record holds now (0 = nothing parked: no
// handle, or nothing of this route in the slots), or JYP_FULL when no slot is free: the slots stay as they are and the task
// must not sleep (docs/323 M2: else the next route's marks overwrite them, or a plan with no handle takes this scope). A
// second call for a sleeping route adds what was armed since (nothing, in the flows we have) and renews its time.
template <class Env> static int JypPark(JyPark& p, Env& e, unsigned handle, unsigned now, unsigned keepMs) {
    if (!handle) return 0;
    JyRec* r = JypFind(p, handle);
    if (r && JypOld(*r, now, keepMs)) { JypFree(r); p.expired++; r = 0; }
    const bool fresh = !r;
    if (!r) r = JypSlot(p, now, keepMs);
    if (!r) { p.full++; return JYP_FULL; }
    int why = 0;
    long scope = 0, ia = 0;
    int has = 0;
    if (e.TakeWhy(handle, &why)) has |= JYP_WHY;
    if (e.TakeSc(handle)) has |= JYP_SC;
    if (e.TakeScope(handle, &scope, &ia)) has |= JYP_SCOPE;
    if (e.TakeUf(handle)) has |= JYP_UF;
    if (fresh) {
        if (!has) return 0;  // nothing of this route in the slots: no record
        r->handle = handle, r->has = 0, r->why = 0, r->scope = 0, r->ia = 0;
        p.parked++;
    }
    if (has & JYP_WHY) r->why = why;
    if (has & JYP_SCOPE) r->scope = scope, r->ia = ia;
    r->has |= has;
    r->tick = now;
    return r->has;
}

// Route h's plan follows now: its records go back into the slots. Returns the bits put back (0 = none parked, or expired).
template <class Env> static int JypBack(JyPark& p, Env& e, unsigned handle, unsigned now, unsigned keepMs) {
    JyRec* r = JypFind(p, handle);
    if (!r) return 0;
    if (JypOld(*r, now, keepMs)) { JypFree(r); p.expired++; return 0; }
    const int has = r->has;
    if (has & JYP_WHY) e.PutWhy(handle, r->why);
    if (has & JYP_SC) e.PutSc(handle);
    if (has & JYP_SCOPE) e.PutScope(handle, r->scope, r->ia);
    if (has & JYP_UF) e.PutUf(handle);
    JypFree(r);
    p.back++;
    return has;
}

// No plan follows this sleep (the task left it by an exception), or the route sends a new START: the record is forgotten.
static bool JypDrop(JyPark& p, unsigned handle) {
    JyRec* r = JypFind(p, handle);
    if (!r) return false;
    JypFree(r);
    p.dropped++;
    return true;
}
inline int JypLive(const JyPark& p) {  // tests only (inline: no unreferenced-function warning in the asi build)
    int n = 0;
    for (int i = 0; i < JYP_SLOTS; i++) n += p.r[i].handle != 0;
    return n;
}
