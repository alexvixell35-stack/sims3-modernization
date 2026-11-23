// docs/183 §7 [RouteAsync] JobYield: JOBBUSY and the records of a plan whose task steps aside. Included by ts3native.cpp
// after plandeadline.cpp (g_kdScope*) and unseenfb.cpp. Sim thread only.
//
// A refused START leaves its route's records in single slots for the IC ts3fixes makes right after: the reason
// (g_rfbHandle / g_rfbWhy), the stuck check's last-round mark (g_scSyncH), the scope answer (g_kdScopeH) and the
// UnseenFallback mark (g_ufH). With JobYield the task sleeps before that IC. jobyield-core.h has why they cannot stay in the
// slots meanwhile and the table they wait in (host test: jobyield-host-test.cpp).
//
// A plan on a route that is being followed does not step aside (the answer is "not busy", so it waits for the job as it
// did before JobYield): the Sim would walk on along the old path while its task sleeps, where the game's plan replaces the
// path in the same tick. [RouteAsync] JobYieldFollowed=1 lets those step aside too (the behaviour up to 0.36.267).
#include "jobyield-core.h"

static JyPark g_jyPark;
static const DWORD kJyKeepMs = 20000;  // ts3fixes sleeps at most 10 s (kStepAsideMaxMs); a record this old lost its task

struct JySlots {
    bool TakeWhy(unsigned h, int* why) {
        if (g_rfbHandle != h) return false;
        *why = g_rfbWhy, g_rfbHandle = 0;
        return true;
    }
    bool TakeSc(unsigned h) {
        if (g_scSyncH != h) return false;
        g_scSyncH = 0;
        return true;
    }
    bool TakeScope(unsigned h, long* scope, long* ia) {
        if (g_kdScopeH != h) return false;
        *scope = g_kdScope, *ia = g_kdScopeIa, g_kdScopeH = 0;
        return true;
    }
    bool TakeUf(unsigned h) {
        if (g_ufH != h) return false;
        g_ufH = 0;
        return true;
    }
    void PutWhy(unsigned h, int why) { g_rfbHandle = h, g_rfbWhy = why; }
    void PutSc(unsigned h) { g_scSyncH = h; }
    void PutScope(unsigned h, long scope, long ia) { g_kdScopeH = h, g_kdScope = scope, g_kdScopeIa = ia; }  // not KdScopeSet: no new answer
    void PutUf(unsigned h) { g_ufH = h; }
};

static void JyCounts() {
    g_jyParkedN = g_jyPark.parked, g_jyBackN = g_jyPark.back, g_jyDropN = g_jyPark.dropped;
    g_jyLostN = g_jyPark.expired + g_jyPark.full;
}
static void JyDrop(DWORD handle) {
    if (handle && JypDrop(g_jyPark, handle)) JyCounts();
}
static u8 JyBusy(DWORD handle, DWORD op) {
    Ts3nCounters& k = ts3native_counters;
    JySlots slots;
    if (GetCurrentThreadId() != g_async.owner) {  // no script task here: never busy, and the table is the owner's
        if (!op && k.jyMode == 1) InterlockedIncrement(&k.jyAsks);
        return 0;
    }
    const DWORD now = GetTickCount();
    if (op == 2) {  // the sleep ended by an exception: no plan follows
        JyDrop(handle);
        return 0;
    }
    bool busy = false;
    if (!op && k.jyMode == 1) {  // op 1 (the caller's cap) or switched off while it slept: its plan follows now
        InterlockedIncrement(&k.jyAsks);
        busy = !g_inInvoke && g_async.state == A_RUNNING && CanYield();
        if (busy && handle && !g_jyFollowed && g_routeIsActive(handle)) {
            busy = false;
            g_jyActiveN++;
        }
    }
    if (!busy) {
        if (JypBack(g_jyPark, slots, handle, now, kJyKeepMs) || g_jyPark.expired + g_jyPark.full != g_jyLostN) JyCounts();
        return 0;
    }
    JypPark(g_jyPark, slots, handle, now, kJyKeepMs);  // before any managed code runs: the job's call below, then the other tasks
    JyCounts();
    if (WaitForSingleObject(g_invReady, 0) == WAIT_OBJECT_0) ServeInvoke(HC_READY);  // keep the running job moving
    InterlockedIncrement(&k.jyBusy);
    return 1;
}
