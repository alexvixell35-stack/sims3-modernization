// docs/674: TestDoorShare ([RouteSnap] DoorShare, doorshare.cpp + doorshare-core.h). Included by selftest.cpp and, as is, by
// doorshare-host-test.cpp (Linux, no Windows). No clock is read: every time is a number handed in.

static void TestDsCore() {
    static DsTable t;
    memset(&t, 0, sizeof(t));
    const uint32_t k9[9] = {0x11, 0x22, 0xA0A0, 0xB0B0, 0x33, 0x44, 5, 6, 0};
    uint32_t k7[7] = {};
    DsKey(k9, k7);
    assert(k7[0] == 0x11 && k7[1] == 0x22 && k7[2] == 0x33 && k7[3] == 0x44 && k7[4] == 5 && k7[5] == 6 && k7[6] == 0);  // no Sim
    const DsRow* row = NULL;
    assert(DsFind(&t, k7, 7, 0x100, 1000, 500, &row) == DS_NONE && !row);
    DsPut(&t, k7, 7, 0x100, 1000, DS_KIND_DOOR);
    assert(DsFind(&t, k7, 7, 0x100, 1500, 500, &row) == DS_VALID && row && row->kind == DS_KIND_DOOR);  // at the age cap: still good
    assert(DsFind(&t, k7, 7, 0x100, 1501, 500, &row) == DS_OLD && !row);
    assert(DsFind(&t, k7, 8, 0x100, 1000, 500, &row) == DS_OTHERGEN && !row);   // generation moved
    assert(DsFind(&t, k7, 7, 0x104, 1000, 500, &row) == DS_OTHERGEN && !row);   // event 59 delegate re-registered
    DsPut(&t, k7, 8, 0x100, 2000, DS_KIND_INERT);  // a live answer refreshes the key's one row
    assert(DsFind(&t, k7, 8, 0x100, 2400, 500, &row) == DS_VALID && row->kind == DS_KIND_INERT);
    int rows = 0;
    for (int i = 0; i < DS_ROWS; i++) rows += t.r[i].kind != 0;
    assert(rows == 1);
    uint32_t other[7] = {};
    memcpy(other, k7, sizeof(other));
    other[4] = 6, other[5] = 5;  // the other direction is another key
    assert(DsFind(&t, other, 8, 0x100, 2400, 500, &row) == DS_NONE);
    other[4] = 5, other[5] = 6, other[6] = 1;  // so is initialLocked
    assert(DsFind(&t, other, 8, 0x100, 2400, 500, &row) == DS_NONE);
    assert(DsDrop(&t, k7) == 1 && DsDrop(&t, k7) == 0 && DsFind(&t, k7, 8, 0x100, 2400, 500, &row) == DS_NONE);
    // a full set loses its oldest row: fill one set with DS_WAYS + 1 keys
    memset(&t, 0, sizeof(t));
    uint32_t in[DS_WAYS + 1][7] = {};
    int n = 0;
    const DsRow* const set0 = DsSet(&t, k7);
    for (uint32_t v = 1; n < DS_WAYS + 1 && v < 1000000; v++) {
        uint32_t c[7] = {v, 0, 0x33, 0x44, 5, 6, 0};
        if (DsSet(&t, c) != set0) continue;
        memcpy(in[n], c, sizeof(c));
        DsPut(&t, c, 1, 0, 100 + n, DS_KIND_DOOR);
        n++;
    }
    assert(n == DS_WAYS + 1);
    assert(DsFind(&t, in[0], 1, 0, 200, 1000, &row) == DS_NONE);  // the oldest went
    for (int i = 1; i <= DS_WAYS; i++) assert(DsFind(&t, in[i], 1, 0, 200, 1000, &row) == DS_VALID);
    // the note hash names the ask (portal, Sim, rooms as 16 bits), never 0
    const uint32_t h = DsNoteHashKey(k9);
    assert(h && h == DsNoteHash(0x33, 0x44, 0xA0A0, 0xB0B0, 5, 6) && h == DsNoteHash(0x33, 0x44, 0xA0A0, 0xB0B0, 0x10005, 0xFFFF0006));
    assert(h != DsNoteHash(0x33, 0x44, 0xA0A1, 0xB0B0, 5, 6) && h != DsNoteHash(0x33, 0x44, 0xA0A0, 0xB0B0, 6, 5));
    assert(DsNoteHash(0x34, 0x44, 0xA0A0, 0xB0B0, 5, 6) != h);
    // the values tools/selftest/doorshare checks for ts3fixes' DoorShareCore.NoteHash / SimFold
    assert(h == 0x7845B309u && DsNoteHash(0x12345678, 0x9ABCDEF0, 0x0BADF00D, 0x374A0055, 0xFFFF, 0x7FFF) == 0x14397595u);
    assert(DsSimFold(0xA0A0, 0xB0B0) == 0x1010 && DsSimFold(0x12345678, 0x9ABCDEF0) == 0xCEE);
    // who may take: class, the Sim the class was sent for, no player command, NPC scope or the household switch
    const uint32_t bits = DS_C_ORDINARY | DsSimFold(0xA0A0, 0xB0B0) << DS_C_SIMSHIFT;
    assert(bits <= 0xFFFF && DsSimFold(0xFFFFFFFF, 0) <= 0x1FFF);
    assert(DsMayTake(bits, 0xA0A0, 0xB0B0, 2, 0) == DS_TAKE_OK && DsMayTake(bits, 0xA0A0, 0xB0B0, 3, 0) == DS_TAKE_OK);
    assert(DsMayTake(0, 0xA0A0, 0xB0B0, 2, 0) == DS_NO_CLASS && DsMayTake(bits & ~(uint32_t)DS_C_ORDINARY, 0xA0A0, 0xB0B0, 2, 1) == DS_NO_CLASS);
    assert(DsMayTake(bits, 0xA0A1, 0xB0B0, 2, 0) == DS_NO_SIM);
    assert(DsMayTake(bits, 0xA0A0, 0xB0B0, 1, 0) == DS_NO_SCOPE && DsMayTake(bits, 0xA0A0, 0xB0B0, 0, 0) == DS_NO_SCOPE);
    assert(DsMayTake(bits, 0xA0A0, 0xB0B0, 1, 1) == DS_TAKE_OK && DsMayTake(bits, 0xA0A0, 0xB0B0, 0, 1) == DS_TAKE_OK);
    assert(DsMayTake(bits | DS_C_COMMAND, 0xA0A0, 0xB0B0, 1, 1) == DS_NO_COMMAND && DsMayTake(bits | DS_C_COMMAND, 0xA0A0, 0xB0B0, 2, 0) == DS_NO_COMMAND);
    long seq = 0;
    int due = 0;
    for (int i = 0; i < 16; i++) due += DsCheckDue(&seq, 8);
    assert(due == 2 && DsCheckDue(&seq, 1) && DsCheckDue(&seq, 0));
    static DsSeen seen;
    memset(&seen, 0, sizeof(seen));
    assert(DsSeenAdd(&seen, 77) == 1 && DsSeenAdd(&seen, 77) == 0 && DsSeenAdd(&seen, 78) == 1 && seen.n == 2);
}

// one door ask of the worker through DsBefore / DsAfter; live = the answer a host call would give, noted = ts3fixes' note kind
// for it (0 = none). Returns the answer the stub would return and *asked = a host call was made.
static DWORD DsTestAsk(const DWORD* key, LONG gen, LONGLONG now, DWORD live, DWORD noted, bool* asked) {
    DsAsk a = {};
    *asked = false;
    if (ts3native_doorshare.mode && DsBefore(key, gen, 0x100, now, &a)) return 2;
    *asked = true;
    DsLive(&a);
    if (noted) DsNoteOp(DsNoteHashKey((const uint32_t*)key), noted);  // the sim thread, inside the served call
    DsAfter(&a, key, live, 600);
    return live;
}

static void TestDoorShare() {
    TestDsCore();
    DoorShareCounters& c = ts3native_doorshare;
    static DoorShareCounters saved;
    memcpy((void*)&saved, (const void*)&c, sizeof(c));
    const LONG sArmed = ts3native_doorgen.armed, sScope = t_hcScope, sBits = t_dsBits, sDepth = t_dsPlDepth, sPlans = t_dsPlans;
    const LONGLONG sTicks = g_dsTicks;
    const DWORD sH = g_dsH;
    const LONG sHb = g_dsBits;
    memset((void*)&c, 0, sizeof(c));
    memset(&g_dsTab, 0, sizeof(g_dsTab));
    memset(&g_dsSeenSim, 0, sizeof(g_dsSeenSim));
    memset(&g_dsSeenDoor, 0, sizeof(g_dsSeenDoor));
    g_dsTicks = 5000, g_dsCheckSeq = 0;
    c.checkN = 1000;  // no sampled re-ask until the check cases
    // key: lot, Sim A, portal, rooms 5 -> 6, not locked by the planner
    DWORD ka[9] = {0x11, 0x22, 0xA0A0, 0xB0B0, 0x33, 0x44, 5, 6, 0};
    DWORD kb[9] = {0x11, 0x22, 0xC0C0, 0xD1D7, 0x33, 0x44, 5, 6, 0};  // Sim B, same door and direction
    const LONG bitsA = (LONG)(DS_C_ORDINARY | DsSimFold(0xA0A0, 0xB0B0) << DS_C_SIMSHIFT);
    const LONG bitsB = (LONG)(DS_C_ORDINARY | DsSimFold(0xC0C0, 0xD1D7) << DS_C_SIMSHIFT);
    bool asked = false;
    t_hcScope = 2, t_dsBits = bitsA, t_dsPlDepth = 0, t_dsPlans = 0;
    // off: nothing counted
    assert(DsTestAsk(ka, 1, 100, 2, DS_KIND_DOOR, &asked) == 2 && asked && !c.asks && !c.live && !c.puts[DS_KIND_DOOR]);
    // the switch flipped at run time without the install: still nothing
    c.mode = 1;
    assert(DsTestAsk(ka, 1, 100, 2, DS_KIND_DOOR, &asked) == 2 && asked && !c.asks && !c.live);
    c.ready = 1;
    // on, ts3fixes doorgen not armed: counted, nothing kept
    ts3native_doorgen.armed = 0;
    assert(DsTestAsk(ka, 1, 100, 2, DS_KIND_DOOR, &asked) == 2 && asked && c.asks == 1 && c.unarmed == 1 && !c.live && !c.puts[DS_KIND_DOOR]);
    ts3native_doorgen.armed = 1;
    // Sim A asks: no row, the live answer with a note is kept
    assert(DsTestAsk(ka, 1, 100, 2, DS_KIND_DOOR, &asked) == 2 && asked && c.miss[DS_NONE] == 1 && c.live == 1 && c.puts[DS_KIND_DOOR] == 1);
    assert(c.part[0] == 2 && c.keysSim == 1 && c.keysDoor == 1 && c.classAsks == 1);
    // Sim B, another job, inside a plan: answered from A's row, no host call
    t_dsBits = bitsB, t_dsPlDepth = 1;
    assert(DsTestAsk(kb, 1, 200, 1, 0, &asked) == 2 && !asked && c.hits == 1 && c.live == 1 && c.part[1] == 1 && c.keysSim == 2 && c.keysDoor == 1);
    t_dsPlDepth = 0, t_dsPlans = 1;
    // a Sim outside the class (no op 40 for its route): asks live; its "locked" answer leaves the row
    t_dsBits = 0;
    assert(DsTestAsk(kb, 1, 300, 1, 0, &asked) == 1 && asked && c.noTake[DS_NO_CLASS] == 1 && c.liveLocked == 1 && !c.drops && !c.mismatch);
    assert(c.part[2] == 1);
    // a class sent for another Sim than the one asked about: not used
    t_dsBits = bitsA;
    assert(DsTestAsk(kb, 1, 300, 2, 0, &asked) == 2 && asked && c.noTake[DS_NO_SIM] == 1 && c.liveOther == 1 && !c.drops);
    // household / unknown scope: live unless DoorShareHousehold; the selected Sim's own command always live
    t_dsBits = bitsB;
    for (LONG s = 0; s <= 1; s++) {
        t_hcScope = s;
        assert(DsTestAsk(kb, 1, 400, 2, DS_KIND_DOOR, &asked) == 2 && asked && c.noTake[DS_NO_SCOPE] == s + 1 && c.checks == s + 1 && !c.mismatch);
    }
    c.household = 1;
    assert(DsTestAsk(kb, 1, 500, 1, 0, &asked) == 2 && !asked && c.hits == 2);
    t_dsBits = bitsB | DS_C_COMMAND;
    assert(DsTestAsk(kb, 1, 500, 2, DS_KIND_DOOR, &asked) == 2 && asked && c.noTake[DS_NO_COMMAND] == 1);
    c.household = 0, t_hcScope = 3, t_dsBits = bitsB;
    assert(DsTestAsk(kb, 1, 600, 1, 0, &asked) == 2 && !asked && c.hits == 3);  // a seen NPC takes it
    // the generation moved (a lock setter, a portal IC, a world load): asked again, kept under the new one
    assert(DsTestAsk(kb, 2, 700, 2, DS_KIND_DOOR, &asked) == 2 && asked && c.miss[DS_OTHERGEN] == 1);
    // the row answers the key for every class Sim, but this job's class was sent for Sim B and the ask is about Sim A: live
    assert(DsTestAsk(ka, 2, 800, 1, 0, &asked) == 1 && asked && c.noTake[DS_NO_SIM] == 2 && c.liveLocked == 2 && !c.drops && !c.mismatch);
    t_dsBits = bitsA;
    assert(DsTestAsk(ka, 2, 800, 1, 0, &asked) == 2 && !asked && c.hits == 4);
    // no live ask confirmed the row for DoorShareMs: asked again
    assert(DsTestAsk(ka, 2, 700 + 5001, 2, DS_KIND_DOOR, &asked) == 2 && asked && c.miss[DS_OLD] == 1);
    // an "unlocked" answer ts3fixes did not mark (the lock type changed, a handler was added): the row goes
    const LONG drops = c.drops;
    DsAsk a = {};
    assert(!DsBefore(ka, 3, 0x100, 6000, &a) && a.on && !a.row);
    DsLive(&a);
    DsNoteOp(DsNoteHashKey((const uint32_t*)kb), DS_KIND_DOOR);  // a note for another ask does not count
    DsAfter(&a, ka, 2, 10);
    assert(c.liveNoNote == 1 && c.drops == drops + 1);
    assert(DsTestAsk(ka, 3, 6100, 2, DS_KIND_INERT, &asked) == 2 && asked && c.puts[DS_KIND_INERT] == 1);
    // the sampled re-ask: equal = nothing; different = off for the session
    c.checkN = 1;
    const LONG checks = c.checks;
    assert(DsTestAsk(ka, 3, 6200, 2, DS_KIND_INERT, &asked) == 2 && asked && c.checks == checks + 1 && !c.mismatch && c.mode == 1);
    assert(DsTestAsk(ka, 3, 6300, 1, 0, &asked) == 1 && asked && c.mismatch == 1 && c.mode == 0);
    // shadow: never answers, compares every valid row
    c.mode = 2, c.checkN = 1000;
    assert(DsTestAsk(ka, 4, 7000, 2, DS_KIND_DOOR, &asked) == 2 && asked);
    const LONG hits = c.hits;
    assert(DsTestAsk(ka, 4, 7100, 2, DS_KIND_DOOR, &asked) == 2 && asked && c.hits == hits && c.checks == checks + 3 && c.mismatch == 1);
    assert(DsTestAsk(ka, 4, 7200, 3, 0, &asked) == 3 && asked && c.mismatch == 2 && c.mode == 2);
    // op 40: the class is good for the route's next START only
    assert(DsClassOp(0x25000001, (DWORD)bitsA | 0x00280000) == 1 && DsClassTake(0x25000002) == 0 && DsClassTake(0x25000001) == 0);
    DsClassOp(0x25000001, (DWORD)bitsA);
    assert(DsClassTake(0x25000001) == bitsA && DsClassTake(0x25000001) == 0 && DsClassTake(0) == 0);
    c.mode = 0;
    assert(DsClassOp(0x25000001, 1) == 0);

    memcpy((void*)&c, (const void*)&saved, sizeof(c));
    memset(&g_dsTab, 0, sizeof(g_dsTab));
    memset(&g_dsSeenSim, 0, sizeof(g_dsSeenSim));
    memset(&g_dsSeenDoor, 0, sizeof(g_dsSeenDoor));
    ts3native_doorgen.armed = sArmed, t_hcScope = sScope, t_dsBits = sBits, t_dsPlDepth = sDepth, t_dsPlans = sPlans;
    g_dsTicks = sTicks, g_dsCheckSeq = 0, g_dsH = sH, g_dsBits = sHb;
    printf("DoorShare (docs/674): class-shared door rows (put by note, hit for another Sim, generation / delegate / age, drop), "
           "scope and command rules, sampled check and shadow, op 40 class take\n");
}
