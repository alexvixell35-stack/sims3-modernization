// [Diag] TaskSeam (docs/308), the parts without game addresses: the run-length histogram and the scope tags. Included by
// taskseam.cpp; the selftest (taskseam-test.h) checks them on plain numbers.
#pragma once

// ---- run-length histogram: bucket 0 = < 1 us, bucket b = [2^(b-1), 2^b) us, the last one open (>= 2^22 us ~ 4.2 s) ----
enum { TS_HB = 24 };
static int TsBucket(double us) {
    int b = 0;
    while (b < TS_HB - 1 && us >= (double)(1u << b)) b++;
    return b;
}
// the upper edge (us) of the bucket that holds quantile q (0..1) of the runs; 0 = no runs
static double TsQuantUs(const LONG* h, double q) {
    double n = 0;
    for (int b = 0; b < TS_HB; b++) n += h[b];
    if (n <= 0) return 0;
    double acc = 0;
    for (int b = 0; b < TS_HB; b++)
        if ((acc += h[b]) >= q * n) return (double)(1u << b);
    return (double)(1u << (TS_HB - 1));
}

// ---- scope of a task run (docs/302 §4.2), reported only. Unknown counts as household.
// SIMCAND: a ScriptProxy target Sim outside a fresh household table; the active lot is not known natively, so this is an upper
// bound of the NPC-scoped share (the docs/302 §4.7 gate), never a proof that a run is NPC-only.
enum { TS_SC_HH = 0, TS_SC_SIMCAND = 2 };
