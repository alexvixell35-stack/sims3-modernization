// [Compaction] (docs/204 §3-§7, 0.36.128): the verdict for ts3fixes' quiet save-reload. After a normal save that ended with
// result 1 while the largest free address-space block is under the threshold, ts3fixes unloads the world and loads the same
// save in the same process behind EA's loading screen (direct ReloadWorld or the main-menu route), then restores what the save
// does not carry. Native decides (threshold, hysteresis, "did it help") and logs; ts3fixes acts and asks over the [UserDiag]
// text channel (top byte 0x87, userdiag.cpp), op 7, optLo:
//   7  save task begins (snapshot of the save count: the verdict needs a save that ended after this)
//   8  on? (ts3fixes installs its save-task and menu hooks only when this answers true)
//   9  the player declined the offer (log only; the arm is used up)
//   0  verdict: compact now?  (a fresh VirtualQuery walk on the sim thread, ~0.5-30 ms, once per save)
//   1  after a "no": show the "restart the game" notice? (once, only when Compaction went off for the session and memory is low)
//   2  mode is "offered" (Enabled=2: ask the player)      3  route is the main menu (Route=1)
//   4  compaction begins (log)                             5  world back after a compaction: log, "did it help" (§3.4)
//   6  compaction skipped / failed (reason in bits 8-15): off for the session
//   0x10000 | field << 5 | bit: bit 0-15 of field 0 = largest free MB at the verdict, 1 = gain MB of the last compaction
// Answer false for everything with [Compaction] Enabled=0, [UserDiag] off, or without ts3native: ts3fixes then never compacts.
// Nothing goes into the save; the state ts3fixes restores lives in its own memory (the process survives the reload).

static int g_cpMode;                                // 0 off, 1 opt-in (compact after a low save), 2 offered (ask)
static int g_cpRoute;                               // 0 direct (EA ReloadWorld), 1 main menu (Quit to Main Menu + load)
static DWORD g_cpOnMB = 320, g_cpRearmMB = 256, g_cpMinMin = 60, g_cpAfterMin = 10;
static bool g_cpApexGuard;                          // Apex Radiance MemoryGuard on: T_on = max(ours, 320) (docs/204 §3.2)

struct CpState {  // every member initialized: the selftest builds them on the stack
    bool armed = true;       // §3.3 (2): first one this session, or back above T_on + RearmMB since the last
    bool off = false;        // §3.4 / §6: off for the session
    bool notHelped = false;  // off because a compaction did not get back to T_on + RearmMB (§3.4), not because one failed
    bool noticeDue = false, noticeGiven = false;  // off after a compaction that did not help + low again: one "restart" notice
    DWORD lastTick = 0;      // last compaction begin or world load (GetTickCount), 0 = none yet
    DWORD verdictMB = 0, beforeMB = 0, afterMB = 0, beginTick = 0, doneTick = 0;
    LONG saveSnap = 0;       // g_udSaves when the save task began
    LONG lastSaveN = 0;      // the last save that ended (UdSaveEnd): its number, result, tick
    DWORD lastSaveR = 0, lastSaveEnd = 0;
    int count = 0;           // compactions this session
    bool afterDue = false;   // the "+N min" line is still to come
};
static CpState g_cp;

static DWORD CpThreshold(DWORD onMB, bool apexGuard) { return apexGuard && onMB < 320 ? 320 : onMB; }

// Pure verdict (selftest): 1 = compact, 0 = no; st.noticeDue set when the player should be told to restart instead.
static int CpVerdict(CpState& st, int mode, DWORD onMB, DWORD rearmMB, DWORD minMin, bool apexGuard, DWORD bigMB, DWORD now, bool saveOk) {
    if (!mode || !saveOk) return 0;
    const DWORD t = CpThreshold(onMB, apexGuard);
    if (bigMB >= t + rearmMB) st.armed = true;
    if (bigMB >= t) return 0;
    if (st.off) {
        if (st.notHelped && !st.noticeGiven) st.noticeDue = true;  // a failure says nothing about memory: no notice
        return 0;
    }
    if (!st.armed || (st.lastTick && now - st.lastTick < minMin * 60000)) return 0;
    st.armed = false;  // an offer the player declines uses the arm up too (§7 option 2: at most once per arm)
    st.verdictMB = bigMB;
    return 1;
}

// Pure "did it help" (§3.4): the block after the reload must be back at T_on + RearmMB, else off for the session.
static bool CpHelped(DWORD afterMB, DWORD onMB, DWORD rearmMB, bool apexGuard) { return afterMB >= CpThreshold(onMB, apexGuard) + rearmMB; }

static DWORD CpBigMB() {  // largest free block now (sim thread; one walk)
    UdMemStat s;
    UdMemWalk(s, NULL);
    const DWORD big = (DWORD)(s.big1 >> 20);
    if (big < g_udLowBigMB) g_udLowBigMB = big;
    return big;
}

static void CpSaveEnd(DWORD r) {  // userdiag.cpp UdSaveEnd (save thread = sim thread)
    g_cp.lastSaveN = g_udSaves, g_cp.lastSaveR = r, g_cp.lastSaveEnd = GetTickCount();
}

static void CpWorldLoad() {  // userdiag.cpp UdWorldLoad: §3.3 (3) counts play time from the last world load too
    if (!g_cpMode) return;
    g_cp.lastTick = GetTickCount();
}

static void CpAfterSample(DWORD bigMB) {  // memory worker, every periodic sample
    if (!g_cpMode) return;
    if (bigMB >= CpThreshold(g_cpOnMB, g_cpApexGuard) + g_cpRearmMB) g_cp.armed = true;
    if (g_cp.afterDue && GetTickCount() - g_cp.doneTick >= g_cpAfterMin * 60000) {
        g_cp.afterDue = false;
        Log("compaction: +%lu min after #%d: largest free %lu MB (before the save %lu, right after the reload %lu)",
            (GetTickCount() - g_cp.doneTick) / 60000, g_cp.count, bigMB, g_cp.beforeMB, g_cp.afterMB);
    }
}

// op 6 reasons (ts3fixes Compaction.cs kCpWhy*)
static const char* const kCpWhy[] = {"?", "state refused after the offer (travel, build/buy, CAS, emergency, dialog)", "no save file details",
                                     "reload threw", "world load failed or another file loaded"};

static u8 CpOp(DWORD lo) {
    if (!g_cpMode) return 0;
    if (lo >= 0x10000 && lo < 0x10100) {
        const DWORD field = (lo >> 5) & 7, bit = lo & 31;
        const DWORD v = field == 0 ? g_cp.verdictMB : field == 1 ? (g_cp.afterMB > g_cp.beforeMB ? g_cp.afterMB - g_cp.beforeMB : 0) : 0;
        return bit < 16 && (v >> bit & 1);
    }
    switch (lo & 0xFF) {
        case 7: g_cp.saveSnap = g_udSaves; return 1;
        case 8: return 1;  // on (ts3fixes installs its hooks only then)
        case 9: Log("compaction: the player chose Not now (asked again only after a re-arm)"); return 1;
        case 0: {
            const DWORD now = GetTickCount();
            // the save that just ran: ended after the task began, result 1, within the last minute
            const bool saveOk = g_cp.lastSaveN > g_cp.saveSnap && g_cp.lastSaveR == 1 && now - g_cp.lastSaveEnd < 60000;
            const DWORD big = CpBigMB();
            const int v = CpVerdict(g_cp, g_cpMode, g_cpOnMB, g_cpRearmMB, g_cpMinMin, g_cpApexGuard, big, now, saveOk);
            Log("compaction: verdict %s after save #%ld (result %lu%s): largest free %lu MB, threshold %lu MB%s, %s%s", v ? "COMPACT" : "no",
                g_cp.lastSaveN, g_cp.lastSaveR, saveOk ? "" : ", not this task's save", big, CpThreshold(g_cpOnMB, g_cpApexGuard),
                g_cpApexGuard ? " (Apex MemoryGuard)" : "", g_cp.off ? "off for the session" : g_cp.armed ? "armed" : "not armed",
                g_cp.noticeDue ? ", restart notice due" : "");
            return (u8)v;
        }
        case 1: {
            const bool due = g_cp.noticeDue;
            if (due) g_cp.noticeDue = false, g_cp.noticeGiven = true;
            return due;
        }
        case 2: return g_cpMode == 2;
        case 3: return g_cpRoute == 1;
        case 4:
            g_cp.beforeMB = g_cp.verdictMB, g_cp.beginTick = g_cp.lastTick = GetTickCount();
            g_cp.count++;
            Log("compaction: begin #%d (%s route), largest free before %lu MB, uptime %lu min", g_cp.count, g_cpRoute == 1 ? "main menu" : "direct",
                g_cp.beforeMB, (GetTickCount() - g_udT0) / 60000);
            return 1;
        case 5: {
            const DWORD big = CpBigMB();
            g_cp.afterMB = big, g_cp.doneTick = g_cp.lastTick = GetTickCount(), g_cp.afterDue = true;
            const bool ok = CpHelped(big, g_cpOnMB, g_cpRearmMB, g_cpApexGuard);
            if (!ok) g_cp.off = g_cp.notHelped = true;
            Log("compaction: done #%d after %lu s: largest free %lu MB after the reload (before %lu, gain %ld MB)%s", g_cp.count,
                (g_cp.doneTick - g_cp.beginTick) / 1000, big, g_cp.beforeMB, (LONG)big - (LONG)g_cp.beforeMB,
                ok ? "" : "; still under threshold + rearm: Compaction off for this session (restart the game for the most room)");
            return ok;
        }
        case 6: {
            const DWORD why = (lo >> 8) & 0xFF;
            g_cp.off = true;
            Log("compaction: skipped or failed (%s): off for this session", kCpWhy[why < _countof(kCpWhy) ? why : 0]);
            return 1;
        }
    }
    return 0;
}

static void ApplyCompaction(const wchar_t* ini) {
    g_cpMode = GetPrivateProfileIntW(L"Compaction", L"Enabled", 0, ini);
    if (g_cpMode < 0 || g_cpMode > 2) g_cpMode = 0;
    if (!g_cpMode) return;
    if (!g_udOn || !ts3native_counters.saveReady) {  // the verdict needs the save result (savewrite.cpp -> UdSaveEnd)
        Log("compaction: needs [UserDiag] Enabled=1 and [SaveWrite] Enabled=1 (text channel, save result), off");
        g_cpMode = 0;
        return;
    }
    g_cpRoute = GetPrivateProfileIntW(L"Compaction", L"Route", 0, ini) == 1;
    g_cpOnMB = GetPrivateProfileIntW(L"Compaction", L"ThresholdMB", 320, ini);
    g_cpRearmMB = GetPrivateProfileIntW(L"Compaction", L"RearmMB", 256, ini);
    g_cpMinMin = GetPrivateProfileIntW(L"Compaction", L"MinMinutes", 60, ini);
    g_cpAfterMin = GetPrivateProfileIntW(L"Compaction", L"AfterMinutes", 10, ini);
    if (g_cpOnMB > 4000) g_cpOnMB = 4000;
    if (g_cpRearmMB > 2048) g_cpRearmMB = 2048;
    g_cpApexGuard = g_apexFound && (g_apexOn & AXF_MEMORY_GUARD);
    Log("compaction: on (%s, %s route), threshold %lu MB%s, rearm +%lu MB, at least %lu min apart, after-line at +%lu min",
        g_cpMode == 2 ? "offered" : "opt-in", g_cpRoute ? "main menu" : "direct", CpThreshold(g_cpOnMB, g_cpApexGuard),
        g_cpApexGuard ? " (Apex MemoryGuard: at least 320)" : "", g_cpRearmMB, g_cpMinMin, g_cpAfterMin);
}
