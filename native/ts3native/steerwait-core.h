// docs/670 §25 [SteerRO] RetryWait: the wait-and-rerun loop of a steering query whose read-only answer was dropped for a
// writer's hold (QL busy, tripwire). No Windows or game type here, so steerwait-host-test.cpp runs it as it is.
//
// Before: such a call went to the game's Locomotion_ObstacleCheck, whose first +0x918 acquire (key 0x85FE10) waits for the
// rest of the route job. The writer's hold is a part of the job, so the answer can come earlier: the loop waits one step at
// a time (the caller's Pump: up to 1 ms, serving the job's managed call when one is posted, as the +0x918 wait pump does)
// and runs the clone again from its protocol start. It ends at the first of:
//   SWL_ANSWERED  the clone answered (the same answer a first run gives: sequence, epoch and generation are read again);
//   SWL_JOBEND    no job runs any more: the game's call does not wait (the caller keeps job starts held for that call);
//   SWL_OTHER     a rerun met a reason that waiting does not cure (state, shape, dirty index, ...): the game's call;
//   SWL_BUDGET    the budget is used: the game's call waits for the job as before.
// So a call never waits longer for the job than the budget plus what it waits today.
#pragma once

enum { SWL_ANSWERED, SWL_JOBEND, SWL_OTHER, SWL_BUDGET, SWL_ENDS };

struct SwOutcome {
    int end;      // SWL_*
    int why;      // the last clone result (0 = answered)
    long reruns;  // clone runs made in the loop
    long us;      // time spent in the loop (Env::Us at its end)
};

// Env: bool JobRuns(); long Us() (microseconds since the loop's start); void Pump(); int Run() (the clone: 0 = answered);
// bool Curable(int why) (a writer's hold: QL busy or a tripwire).
template <class Env> static SwOutcome SwLoop(Env& e, int why, long budgetUs) {
    SwOutcome o = {SWL_OTHER, why, 0, 0};
    for (;;) {
        if (!o.why) { o.end = SWL_ANSWERED; break; }
        if (!e.Curable(o.why)) { o.end = SWL_OTHER; break; }
        if (!e.JobRuns()) { o.end = SWL_JOBEND; break; }
        if (e.Us() >= budgetUs) { o.end = SWL_BUDGET; break; }
        e.Pump();
        if (!e.JobRuns()) { o.end = SWL_JOBEND; break; }  // ended in the step: no clone run without its job (tripwires disarmed)
        o.why = e.Run();
        o.reruns++;
    }
    o.us = e.Us();
    return o;
}
