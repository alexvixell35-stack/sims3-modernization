// TestPcCheck ([PcCheck], pccheck.cpp, docs/299). Included by selftest.cpp. Host checks of the verdict with fixture numbers,
// the fix hint order and the top-3 process list.
static void TestPcCheck() {
    char v[1024];
    PcFacts f = {};
    f.logical = 16, f.physical = 8, f.affinityCpus = 16, f.maxMhz = 2300, f.busySamples = 100, f.busyThreads = 2, f.ourThreads = 6;
    assert(PcVerdict(f, v, sizeof(v)) == 0 && strstr(v, "CPU: no throttle seen") && strstr(v, "PC: no problem found"));
    // clock limited 40% of busy samples: a problem with the limit in the line
    PcFacts t = f;
    t.limitSamples = 40, t.minLimitMhz = 1200;
    int fl = PcVerdict(t, v, sizeof(v));
    assert(fl == PC_THROTTLE && strstr(v, "clock limited 40% of the time (limit 1200 MHz of 2300)"));
    t.limitSamples = 20;  // 20% is not more than 20%
    assert(PcVerdict(t, v, sizeof(v)) == 0);
    t.limitSamples = 0, t.slowSamples = 30;  // slow clock alone counts too
    assert(PcVerdict(t, v, sizeof(v)) == PC_THROTTLE);
    t.busySamples = 2, t.slowSamples = 2;  // too few busy samples: no verdict
    assert(PcVerdict(t, v, sizeof(v)) == 0 && strstr(v, "too few busy samples"));
    // the four-thread i5 with a saver plan on battery, affinity 2, efficiency mode, other programs at 30%
    PcFacts m = {};
    m.logical = 4, m.physical = 4, m.affinityCpus = 2, m.maxMhz = 3000, m.busySamples = 50, m.busyThreads = 1.5, m.otherCpuPct = 30, m.ourThreads = 5;
    m.powerSaver = m.onBattery = m.efficiency = m.lowPriority = true;
    fl = PcVerdict(m, v, sizeof(v));
    assert(fl == (PC_POWERSAVER | PC_BATTERY | PC_AFFINITY | PC_EFFICIENCY | PC_PRIORITY | PC_OTHERLOAD | PC_FEWCORES));
    assert(strstr(v, "affinity limits TS3W to 2 of 4 cores") && strstr(v, "other programs used 30% CPU") && strstr(v, "2 threads for 1.5 busy game threads"));
    assert(!strstr(v, "no problem found"));
    // docs/537 §34: 4 cores with 1.5 busy threads is not "few cores" (the old rule fired on every <= 4-core PC)
    PcFacts four = {};
    four.logical = 4, four.affinityCpus = 4, four.busySamples = 10, four.busyThreads = 1.5, four.ourThreads = 6;
    fl = PcVerdict(four, v, sizeof(v));
    assert(fl == 0 && strstr(v, "PC: no problem found") && !strstr(v, "compete"));
    // no core left: advice, not a problem; "compete" only when our threads are busy
    four.busyThreads = 3.2, four.ourBusyThreads = 0.1;
    fl = PcVerdict(four, v, sizeof(v));
    assert(fl == PC_FEWCORES && strstr(v, "PC: no problem found") && strstr(v, "no core left over\n") && !strstr(v, "compete"));
    four.ourBusyThreads = 1.2;
    fl = PcVerdict(four, v, sizeof(v));
    assert(fl == PC_FEWCORES && strstr(v, "(1.2 of them ts3native's)") && strstr(v, "our busy worker threads compete with the game"));
    // hint order: battery first, throttle last; none for advice only
    assert(PcHint(PC_THROTTLE | PC_BATTERY) && strstr(PcHint(PC_THROTTLE | PC_BATTERY), "battery"));
    assert(strstr(PcHint(PC_THROTTLE), "slows down") && !PcHint(PC_OTHERLOAD | PC_FEWCORES) && !PcHint(0));
    // top 3 other processes, % of all cores
    PcAcc a = {};
    a.wallS = 10;
    PcProcAdd(a, "chrome.exe", 4000), PcProcAdd(a, "x.exe", 120), PcProcAdd(a, "CHROME.EXE", 4000), PcProcAdd(a, "av.exe", 2000), PcProcAdd(a, "y.exe", 50);
    char top[200];
    PcTop3(a, 4, top, sizeof(top));
    assert(!strcmp(top, "chrome.exe 20.0%, av.exe 5.0%, x.exe 0.3%"));
    PcAcc e = {};
    PcTop3(e, 4, top, sizeof(top));
    assert(!strcmp(top, "-"));
    PcAcc full = {};  // full table: a bigger newcomer replaces the smallest
    char nm[16];
    for (int i = 0; i < 64; i++) { sprintf_s(nm, "p%d", i); PcProcAdd(full, nm, 10 + i); }
    PcProcAdd(full, "big", 1000), PcProcAdd(full, "tiny", 1);
    assert(full.topN == 64);
    bool big = false, tiny = false, p0 = false;
    for (int i = 0; i < 64; i++) big |= !strcmp(full.top[i].name, "big"), tiny |= !strcmp(full.top[i].name, "tiny"), p0 |= !strcmp(full.top[i].name, "p0");
    assert(big && !tiny && !p0);

    // PDH performance %: Windows 11 laptop, boost 150% most of the time, two thermal samples at 95%
    {
        static LONG hist[kPcPerfBins];
        memset(hist, 0, sizeof(hist));
        assert(PcPerfMax(hist, kPcPerfBins) == 0 && PcPerfSlow(hist, kPcPerfBins, 0, 0.85) == 0);
        hist[150] = 8, hist[95] = 2;
        assert(PcPerfMax(hist, kPcPerfBins) == 150 && PcPerfMin(hist, kPcPerfBins) == 95);
        assert(PcPerfSlow(hist, kPcPerfBins, 150, 0.85) == 2);  // 95 < 127.5
        hist[130] = 5;
        assert(PcPerfSlow(hist, kPcPerfBins, 150, 0.85) == 2);  // 130 is not slow
        hist[127] = 1;
        assert(PcPerfSlow(hist, kPcPerfBins, 150, 0.85) == 3);
        memset(hist, 0, sizeof(hist));
        hist[100] = 12;  // flat (no boost, no drop): nothing slow
        assert(PcPerfSlow(hist, kPcPerfBins, PcPerfMax(hist, kPcPerfBins), 0.85) == 0);
        // the verdict on those numbers: 3 of 16 busy = 18% -> no throttle; 4 of 16 = 25% -> throttle
        PcFacts pf = {};
        pf.logical = 16, pf.physical = 8, pf.affinityCpus = 16, pf.busySamples = 16, pf.slowSamples = 3, pf.maxMhz = 3456, pf.meanCurMhz = 3300, pf.minLimitMhz = 2189;
        char pv[1024];
        assert(!(PcVerdict(pf, pv, sizeof(pv)) & PC_THROTTLE) && strstr(pv, "no throttle seen (16 busy samples, 18% limited)"));
        pf.slowSamples = 4;
        assert((PcVerdict(pf, pv, sizeof(pv)) & PC_THROTTLE) && strstr(pv, "clock limited 25% of the time (limit 2189 MHz of 3456)"));
        pf.slowSamples = 0, pf.limitSamples = 5;  // Performance Limit Flags set in 5 of 16
        assert(PcVerdict(pf, pv, sizeof(pv)) & PC_THROTTLE);
    }
}
