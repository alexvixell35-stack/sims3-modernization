// docs/674: TestDoorShare ([RouteSnap] DoorShare, doorshare.cpp + doorshare-core.h). Included by selftest.cpp and, as is, by
// doorshare-host-test.cpp (Linux, no Windows). No clock is read: every time is a number handed in.

static void TestDsCore() {
    static DsTable t;
    static DsStamps st;
    memset(&t, 0, sizeof(t));
    memset(&st, 0, sizeof(st));
    const uint32_t k9[9] = {0x11, 0x22, 0xA0A0, 0xB0B0, 0x33, 0x44, 5, 6, 0};
    uint32_t k7[7] = {};
    DsKey(k9, k7);
    assert(k7[0] == 0x11 && k7[1] == 0x22 && k7[2] == 0x33 && k7[3] == 0x44 && k7[4] == 5 && k7[5] == 6 && k7[6] == 0);  // no Sim
    DsGen g = {};
    DsGenOf(&st, k7, &g);
    const DsRow* row = NULL;
    assert(DsFind(&t, k7, &g, 0x100, 1000, 500, &row) == DS_NONE && !row);
    DsPut(&t, k7, &g, 0x100, 1000, DS_KIND_DOOR);
    assert(DsFind(&t, k7, &g, 0x100, 1500, 500, &row) == DS_VALID && row && row->kind == DS_KIND_DOOR);  // at the age cap: still good
    assert(DsFind(&t, k7, &g, 0x100, 1501, 500, &row) == DS_OLD && !row);
    assert(DsFind(&t, k7, &g, 0x104, 1000, 500, &row) == DS_OTHERGEN && !row);  // event 59 delegate re-registered
    // stamps: the row's owner, the row's lot and the global one each end it; another owner's or lot's stamp does not
    const uint32_t ownerH = DsIdHash(0x33, 0x44), lotH = DsIdHash(0x11, 0x22);
    uint32_t otherOwner = 1, otherLot = 1;
    while ((DsIdHash(otherOwner, 0) & (DS_OWNERS - 1)) == (ownerH & (DS_OWNERS - 1))) otherOwner++;
    while ((DsIdHash(otherLot, 0) & (DS_LOTS - 1)) == (lotH & (DS_LOTS - 1))) otherLot++;
    ++*DsStamp(&st, DS_SC_OWNER, DsIdHash(otherOwner, 0));
    ++*DsStamp(&st, DS_SC_LOT, DsIdHash(otherLot, 0));
    assert(!DsStamp(&st, DS_SC_NONE, ownerH));
    DsGen g2 = {};
    DsGenOf(&st, k7, &g2);
    assert(DsGenSame(&g, &g2) && DsFind(&t, k7, &g2, 0x100, 1000, 500, &row) == DS_VALID);
    for (int scope = DS_SC_OWNER; scope <= DS_SC_GLOBAL; scope++) {
        DsGenOf(&st, k7, &g);
        DsPut(&t, k7, &g, 0x100, 1000, DS_KIND_DOOR);
        assert(DsFind(&t, k7, &g, 0x100, 1000, 500, &row) == DS_VALID);
        ++*DsStamp(&st, scope, scope == DS_SC_OWNER ? ownerH : lotH);
        DsGenOf(&st, k7, &g2);
        assert(!DsGenSame(&g, &g2) && DsFind(&t, k7, &g2, 0x100, 1000, 500, &row) == DS_OTHERGEN);
    }
    DsGenOf(&st, k7, &g);
    DsPut(&t, k7, &g, 0x100, 2000, DS_KIND_INERT);  // a live answer refreshes the key's one row
    assert(DsFind(&t, k7, &g, 0x100, 2400, 500, &row) == DS_VALID && row->kind == DS_KIND_INERT);
    DsPut(&t, k7, &g, 0x100, 2300, DS_KIND_NOACTOR);  // a no-actor note only confirms a row that serves both classes
    assert(DsFind(&t, k7, &g, 0x100, 2800, 500, &row) == DS_VALID && row->kind == DS_KIND_INERT);
    int rows = 0;
    for (int i = 0; i < DS_ROWS; i++) rows += t.r[i].kind != 0;
    assert(rows == 1);
    ++*DsStamp(&st, DS_SC_OWNER, ownerH);
    DsGenOf(&st, k7, &g);
    DsPut(&t, k7, &g, 0x100, 2300, DS_KIND_NOACTOR);  // ... but replaces one that no longer holds
    assert(DsFind(&t, k7, &g, 0x100, 2300, 500, &row) == DS_VALID && row->kind == DS_KIND_NOACTOR);
    DsPut(&t, k7, &g, 0x100, 2300, DS_KIND_DOOR);     // and an ordinary Sim's noted answer upgrades it
    assert(DsFind(&t, k7, &g, 0x100, 2300, 500, &row) == DS_VALID && row->kind == DS_KIND_DOOR);
    uint32_t other[7] = {};
    memcpy(other, k7, sizeof(other));
    other[4] = 6, other[5] = 5;  // the other direction is another key
    assert(DsFind(&t, other, &g, 0x100, 2400, 500, &row) == DS_NONE);
    other[4] = 5, other[5] = 6, other[6] = 1;  // so is initialLocked
    assert(DsFind(&t, other, &g, 0x100, 2400, 500, &row) == DS_NONE && !DsPeek(&t, other) && DsPeek(&t, k7));
    // a full set loses its oldest row: fill one set with DS_WAYS + 1 keys
    memset(&t, 0, sizeof(t));
    memset(&st, 0, sizeof(st));
    DsGenOf(&st, k7, &g);
    uint32_t in[DS_WAYS + 1][7] = {};
    int n = 0;
    const DsRow* const set0 = DsSet(&t, k7);
    for (uint32_t v = 1; n < DS_WAYS + 1 && v < 1000000; v++) {
        uint32_t c[7] = {v, 0, 0x33, 0x44, 5, 6, 0};
        if (DsSet(&t, c) != set0) continue;
        memcpy(in[n], c, sizeof(c));
        DsPut(&t, c, &g, 0, 100 + n, DS_KIND_DOOR);
        n++;
    }
    assert(n == DS_WAYS + 1);
    assert(DsFind(&t, in[0], &g, 0, 200, 1000, &row) == DS_NONE);  // the oldest went
    for (int i = 1; i <= DS_WAYS; i++) assert(DsFind(&t, in[i], &g, 0, 200, 1000, &row) == DS_VALID);
    // the note hash names the ask (portal, router, rooms as 16 bits), never 0
    const uint32_t h = DsNoteHashKey(k9);
    assert(h && h == DsNoteHash(0x33, 0x44, 0xA0A0, 0xB0B0, 5, 6) && h == DsNoteHash(0x33, 0x44, 0xA0A0, 0xB0B0, 0x10005, 0xFFFF0006));
    assert(h != DsNoteHash(0x33, 0x44, 0xA0A1, 0xB0B0, 5, 6) && h != DsNoteHash(0x33, 0x44, 0xA0A0, 0xB0B0, 6, 5));
    assert(DsNoteHash(0x34, 0x44, 0xA0A0, 0xB0B0, 5, 6) != h);
    // the values tools/selftest/doorshare checks for ts3fixes' DoorShareCore.NoteHash / SimFold / IdHash
    assert(h == 0x7845B309u && DsNoteHash(0x12345678, 0x9ABCDEF0, 0x0BADF00D, 0x374A0055, 0xFFFF, 0x7FFF) == 0x14397595u);
    assert(DsSimFold(0xA0A0, 0xB0B0) == 0x1010 && DsSimFold(0x12345678, 0x9ABCDEF0) == 0xCEE);
    assert(DsIdHash(0x33, 0x44) == 0x1BEF90ADu && DsIdHash(0x12345678, 0x9ABCDEF0) == 0xFB6BD181u && DsIdHash(0, 0) >= 2);
    // what each change clears
    assert(DsSrcScope(DS_SRC_LOCK, 5) == DS_SC_OWNER && DsSrcScope(DS_SRC_OWNERLIST, 5) == DS_SC_OWNER && DsSrcScope(DS_SRC_LOCK, 0) == DS_SC_GLOBAL);
    assert(DsSrcScope(DS_SRC_LOTHANDLER, 5) == DS_SC_LOT && DsSrcScope(DS_SRC_HIDDEN, 5) == DS_SC_LOT && DsSrcScope(DS_SRC_HIDDEN, 0) == DS_SC_GLOBAL);
    for (int s = DS_SRC_GREET; s < DS_SRCS; s++) assert(DsSrcScope(s, 5) == DS_SC_NONE);  // Sim-side inputs: read at every plan, or of no shared rule
    assert(DsSrcScope(DS_SRC_UNKNOWN, 0) == DS_SC_GLOBAL && DsSrcScope(DS_SRC_ARM, 0) == DS_SC_GLOBAL && DsSrcScope(DS_SRCS, 5) == DS_SC_GLOBAL &&
           DsSrcScope(99, 5) == DS_SC_GLOBAL);
    for (int i = 0; i < DS_IC_SLOTS; i++)
        assert(DsIcScope(i) == (i >= 4 && i <= 11 ? DS_SC_OWNER : i == 14 ? DS_SC_LOT : i == 22 ? DS_SC_GLOBAL : DS_SC_NONE));
    for (int i = 0; i < DS_WEV_SLOTS; i++)
        assert(DsWevScope(i) == (i == 5 || i == 7 || i == 9 || i == 0x0A || i == 0x0D || i == 0x0E ? DS_SC_GLOBAL : DS_SC_NONE));
    // classes: the class, the router the class was sent for, the row's kind, no player command, NPC scope or the household switch
    const uint32_t fold = DsSimFold(0xA0A0, 0xB0B0) << DS_C_SIMSHIFT, bits = DS_C_ORDINARY | fold, boat = DS_C_NOACTOR | fold;
    assert(bits <= 0xFFFF && boat <= 0xFFFF && DsSimFold(0xFFFFFFFF, 0) <= 0x1FFF);
    assert(DsClassOf(bits, 0xA0A0, 0xB0B0) == DS_CLS_ORDINARY && DsClassOf(boat, 0xA0A0, 0xB0B0) == DS_CLS_NOACTOR);
    assert(DsClassOf(bits, 0xA0A1, 0xB0B0) == DS_CLS_NONE && DsClassOf(fold, 0xA0A0, 0xB0B0) == DS_CLS_NONE && DsClassOf(0, 0, 0) == DS_CLS_NONE);
    assert(DsKindFor(DS_CLS_ORDINARY, DS_KIND_DOOR) && DsKindFor(DS_CLS_ORDINARY, DS_KIND_INERT) && !DsKindFor(DS_CLS_ORDINARY, DS_KIND_NOACTOR));
    assert(DsKindFor(DS_CLS_NOACTOR, DS_KIND_DOOR) && DsKindFor(DS_CLS_NOACTOR, DS_KIND_INERT) && DsKindFor(DS_CLS_NOACTOR, DS_KIND_NOACTOR));
    assert(!DsKindFor(DS_CLS_NONE, DS_KIND_DOOR) && !DsKindFor(DS_CLS_ORDINARY, DS_KIND_NONE) && !DsKindFor(DS_CLS_NOACTOR, DS_KINDS));
    assert(DsNoteFor(DS_CLS_ORDINARY, DS_KIND_DOOR) && DsNoteFor(DS_CLS_ORDINARY, DS_KIND_INERT) && !DsNoteFor(DS_CLS_ORDINARY, DS_KIND_NOACTOR));
    assert(DsNoteFor(DS_CLS_NOACTOR, DS_KIND_NOACTOR) && !DsNoteFor(DS_CLS_NOACTOR, DS_KIND_DOOR) && !DsNoteFor(DS_CLS_NONE, DS_KIND_NOACTOR));
    assert(DsMayTake(bits, 0xA0A0, 0xB0B0, 2, 0, DS_KIND_DOOR) == DS_TAKE_OK && DsMayTake(bits, 0xA0A0, 0xB0B0, 3, 0, DS_KIND_INERT) == DS_TAKE_OK);
    assert(DsMayTake(bits, 0xA0A0, 0xB0B0, 2, 0, DS_KIND_NOACTOR) == DS_NO_KIND && DsMayTake(boat, 0xA0A0, 0xB0B0, 2, 0, DS_KIND_NOACTOR) == DS_TAKE_OK);
    assert(DsMayTake(boat, 0xA0A0, 0xB0B0, 2, 0, DS_KIND_DOOR) == DS_TAKE_OK);
    assert(DsMayTake(0, 0xA0A0, 0xB0B0, 2, 0, DS_KIND_DOOR) == DS_NO_CLASS && DsMayTake(fold, 0xA0A0, 0xB0B0, 2, 1, DS_KIND_DOOR) == DS_NO_CLASS);
    assert(DsMayTake(bits, 0xA0A1, 0xB0B0, 2, 0, DS_KIND_DOOR) == DS_NO_SIM);
    assert(DsMayTake(bits, 0xA0A0, 0xB0B0, 1, 0, DS_KIND_DOOR) == DS_NO_SCOPE && DsMayTake(bits, 0xA0A0, 0xB0B0, 0, 0, DS_KIND_DOOR) == DS_NO_SCOPE);
    assert(DsMayTake(bits, 0xA0A0, 0xB0B0, 1, 1, DS_KIND_DOOR) == DS_TAKE_OK && DsMayTake(bits, 0xA0A0, 0xB0B0, 0, 1, DS_KIND_DOOR) == DS_TAKE_OK);
    assert(DsMayTake(bits | DS_C_COMMAND, 0xA0A0, 0xB0B0, 1, 1, DS_KIND_DOOR) == DS_NO_COMMAND);
    assert(DsMayTake(boat | DS_C_COMMAND, 0xA0A0, 0xB0B0, 2, 0, DS_KIND_NOACTOR) == DS_NO_COMMAND);
    assert(DsWhyOf(bits) == -1 && DsWhyOf(boat) == -1 && DsWhyOf(0) == DS_WHY_NOOP && DsWhyOf(DS_WHY_HORSE << DS_C_SIMSHIFT) == DS_WHY_HORSE &&
           DsWhyOf(DS_WHY_IGNORELOCKS << DS_C_SIMSHIFT) == DS_WHY_IGNORELOCKS && DsWhyOf(DS_WHYS << DS_C_SIMSHIFT) == DS_WHY_NOOP);
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
static DWORD DsTestAsk(const DWORD* key, LONGLONG now, DWORD live, DWORD noted, bool* asked) {
    DsAsk a = {};
    *asked = false;
    if (ts3native_doorshare.mode && DsBefore(key, 0x100, now, &a)) return 2;
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
    static DsStamps savedStamps;
    memcpy((void*)&saved, (const void*)&c, sizeof(c));
    memcpy(&savedStamps, &g_dsStamps, sizeof(savedStamps));
    const LONG sArmed = ts3native_doorgen.armed, sScope = t_hcScope, sBits = t_dsBits, sDepth = t_dsPlDepth, sPlans = t_dsPlans;
    const LONGLONG sTicks = g_dsTicks;
    const DWORD sH = g_dsH;
    const LONG sHb = g_dsBits;
    memset((void*)&c, 0, sizeof(c));
    memset(&g_dsTab, 0, sizeof(g_dsTab));
    memset(&g_dsStamps, 0, sizeof(g_dsStamps));
    memset(&g_dsSeenSim, 0, sizeof(g_dsSeenSim));
    memset(&g_dsSeenDoor, 0, sizeof(g_dsSeenDoor));
    g_dsTicks = 5000, g_dsCheckSeq = 0;
    c.checkN = 1000;  // no sampled re-ask until the check cases
    // key: lot, Sim A, portal, rooms 5 -> 6, not locked by the planner
    DWORD ka[9] = {0x11, 0x22, 0xA0A0, 0xB0B0, 0x33, 0x44, 5, 6, 0};
    DWORD kb[9] = {0x11, 0x22, 0xC0C0, 0xD1D7, 0x33, 0x44, 5, 6, 0};  // Sim B, same door and direction
    DWORD kv[9] = {0x11, 0x22, 0xE0E0, 0xF2F9, 0x33, 0x44, 5, 6, 0};  // a boat, same door and direction
    DWORD kw[9] = {0x11, 0x22, 0xE0E0, 0xF2F9, 0x55, 0x66, 5, 6, 0};  // the boat at a door no Sim's answer is shared for
    const LONG bitsA = (LONG)(DS_C_ORDINARY | DsSimFold(0xA0A0, 0xB0B0) << DS_C_SIMSHIFT);
    const LONG bitsB = (LONG)(DS_C_ORDINARY | DsSimFold(0xC0C0, 0xD1D7) << DS_C_SIMSHIFT);
    const LONG bitsV = (LONG)(DS_C_NOACTOR | DsSimFold(0xE0E0, 0xF2F9) << DS_C_SIMSHIFT);
    DWORD doorId[3] = {0, 0x33, 0x44}, lotId[3] = {0, 0x11, 0x22}, door2[3] = {0, 0x55, 0x66};  // an IC's slot: [1], [2] = its first argument
    bool asked = false;
    t_hcScope = 2, t_dsBits = bitsA, t_dsPlDepth = 0, t_dsPlans = 0;
    // off: nothing counted
    assert(DsTestAsk(ka, 100, 2, DS_KIND_DOOR, &asked) == 2 && asked && !c.asks && !c.live && !c.puts[DS_KIND_DOOR]);
    // the switch flipped at run time without the install: still nothing, and no change event is counted
    c.mode = 1;
    assert(DsTestAsk(ka, 100, 2, DS_KIND_DOOR, &asked) == 2 && asked && !c.asks && !c.live);
    DsIcBump(4, doorId), DsWevBump(7), DsSrcBump(DS_SRC_LOCK, DsIdHash(0x33, 0x44));
    assert(!c.bumpIc[4] && !c.bumpWev[7] && !c.bumpSrc[DS_SRC_LOCK] && !g_dsStamps.global);
    c.ready = 1;
    // on, ts3fixes doorgen not armed: counted, nothing kept
    ts3native_doorgen.armed = 0;
    assert(DsTestAsk(ka, 100, 2, DS_KIND_DOOR, &asked) == 2 && asked && c.asks == 1 && c.unarmed == 1 && !c.live && !c.puts[DS_KIND_DOOR]);
    ts3native_doorgen.armed = 1;
    // Sim A asks: no row, the live answer with a note is kept
    assert(DsTestAsk(ka, 100, 2, DS_KIND_DOOR, &asked) == 2 && asked && c.miss[DS_NONE] == 1 && c.live == 1 && c.puts[DS_KIND_DOOR] == 1);
    assert(c.part[0] == 2 && c.keysSim == 1 && c.keysDoor == 1 && c.classAsks[DS_CLS_ORDINARY] == 2);
    // Sim B, another job, inside a plan: answered from A's row, no host call
    t_dsBits = bitsB, t_dsPlDepth = 1;
    assert(DsTestAsk(kb, 200, 1, 0, &asked) == 2 && !asked && c.hits == 1 && c.live == 1 && c.part[1] == 1 && c.keysSim == 2 && c.keysDoor == 1);
    t_dsPlDepth = 0, t_dsPlans = 1;
    // a Sim outside the class (ts3fixes sent the reason): asks live; its "locked" answer leaves the row
    t_dsBits = DS_WHY_SERVICE << DS_C_SIMSHIFT;
    assert(DsTestAsk(kb, 300, 1, 0, &asked) == 1 && asked && c.noTake[DS_NO_CLASS] == 1 && c.liveLocked == 1 && !c.drops && !c.mismatch);
    assert(c.part[2] == 1 && c.whyAsks[DS_WHY_SERVICE] == 1 && c.classAsks[DS_CLS_NONE] == 1);
    // a class sent for another Sim than the one asked about: not used
    t_dsBits = bitsA;
    assert(DsTestAsk(kb, 300, 2, 0, &asked) == 2 && asked && c.noTake[DS_NO_SIM] == 1 && c.liveOther == 1 && !c.drops);
    // household / unknown scope: live unless DoorShareHousehold; the selected Sim's own command always live
    t_dsBits = bitsB;
    for (LONG s = 0; s <= 1; s++) {
        t_hcScope = s;
        assert(DsTestAsk(kb, 400, 2, DS_KIND_DOOR, &asked) == 2 && asked && c.noTake[DS_NO_SCOPE] == s + 1 && c.checks == s + 1 && !c.mismatch);
    }
    c.household = 1;
    assert(DsTestAsk(kb, 500, 1, 0, &asked) == 2 && !asked && c.hits == 2);
    t_dsBits = bitsB | DS_C_COMMAND;
    assert(DsTestAsk(kb, 500, 2, DS_KIND_DOOR, &asked) == 2 && asked && c.noTake[DS_NO_COMMAND] == 1);
    c.household = 0, t_hcScope = 3, t_dsBits = bitsB;
    assert(DsTestAsk(kb, 600, 1, 0, &asked) == 2 && !asked && c.hits == 3);  // a seen NPC takes it

    // --- change events ---
    // events that cannot change a shared answer: the row stays (portal cost and flags, lot bounds / type, Sim-side doorgen sources)
    DsIcBump(12, doorId), DsIcBump(13, doorId), DsWevBump(0x0F), DsWevBump(0x10);
    for (int s = DS_SRC_GREET; s < DS_SRCS; s++) DsSrcBump(s, 0);
    assert(c.bumpScope[DS_SC_NONE] == 4 + DS_SRCS - DS_SRC_GREET && c.bumpIc[12] == 1 && c.bumpWev[0x10] == 1 && c.bumpSrc[DS_SRC_ROLE] == 1);
    assert(DsTestAsk(kb, 610, 1, 0, &asked) == 2 && !asked && c.hits == 4);
    // another object's portals, another lot's handlers, an IC that is no portal IC, a world event that is no lot event: the row stays
    DsIcBump(4, door2), DsIcBump(11, door2), DsSrcBump(DS_SRC_LOCK, DsIdHash(0x55, 0x66)), DsSrcBump(DS_SRC_HIDDEN, DsIdHash(0x77, 0x88));
    DsIcBump(0, doorId), DsIcBump(18, doorId), DsIcBump(35, doorId), DsWevBump(0x11), DsWevBump(59);
    assert(c.bumpScope[DS_SC_OWNER] == 3 && c.bumpScope[DS_SC_LOT] == 1 && !c.bumpIc[0] && !c.bumpIc[18] && !c.bumpIc[35]);
    assert(DsTestAsk(kb, 620, 1, 0, &asked) == 2 && !asked && c.hits == 5);
    // each event that names the row's door or lot, and each global one, ends the row once: asked again, kept again
    LONG stale = 0, byOwner = 0, byLot = 0, byGlobal = 0;
    for (int ev = 0; ev < 12; ev++) {
        switch (ev) {
            case 0: DsIcBump(4, doorId); byOwner++; break;                               // AddPortal of this object
            case 1: DsIcBump(11, doorId); byOwner++; break;                              // RemovePortals
            case 2: DsSrcBump(DS_SRC_LOCK, DsIdHash(0x33, 0x44)); byOwner++; break;      // its lock type / owner / one-way room
            case 3: DsSrcBump(DS_SRC_OWNERLIST, DsIdHash(0x33, 0x44)); byOwner++; break;
            case 4: DsIcBump(14, lotId); byLot++; break;                                 // SetPortalLockStatus of this lot
            case 5: DsSrcBump(DS_SRC_LOTHANDLER, DsIdHash(0x11, 0x22)); byLot++; break;  // a privacy situation, a mod's handler
            case 6: DsSrcBump(DS_SRC_HIDDEN, DsIdHash(0x11, 0x22)); byLot++; break;
            case 7: DsWevBump(7); byGlobal++; break;                                     // world load finished
            case 8: DsWevBump(0x0D); byGlobal++; break;                                  // lot added
            case 9: DsIcBump(22, doorId); byGlobal++; break;                             // build / buy exit
            case 10: DsSrcBump(DS_SRC_UNKNOWN, 0); byGlobal++; break;                    // an older ts3fixes' plain bump
            default: DsSrcBump(DS_SRC_LOCK, 0); byGlobal++; break;                       // a door hook that could not name its door
        }
        stale++;
        assert(DsTestAsk(kb, 700 + ev, 2, DS_KIND_DOOR, &asked) == 2 && asked && c.miss[DS_OTHERGEN] == stale);
        assert(c.staleBy[DS_SC_OWNER] == byOwner && c.staleBy[DS_SC_LOT] == byLot && c.staleBy[DS_SC_GLOBAL] == byGlobal);
        assert(DsTestAsk(kb, 700 + ev, 1, 0, &asked) == 2 && !asked);
    }
    assert(c.hits == 5 + 12 && c.bumpSrc[DS_SRC_UNKNOWN] == 1 && c.bumpSrc[DS_SRC_LOCK] == 3 && c.bumpIc[22] == 1);
    // the row answers the key for every class Sim, but this job's class was sent for Sim B and the ask is about Sim A: live
    assert(DsTestAsk(ka, 800, 1, 0, &asked) == 1 && asked && c.noTake[DS_NO_SIM] == 2 && c.liveLocked == 2 && !c.drops && !c.mismatch);
    t_dsBits = bitsA;
    assert(DsTestAsk(ka, 800, 1, 0, &asked) == 2 && !asked && c.hits == 18);
    // no live ask confirmed the row for DoorShareMs: asked again
    assert(DsTestAsk(ka, 711 + 5001, 2, DS_KIND_DOOR, &asked) == 2 && asked && c.miss[DS_OLD] == 1);

    // --- a router that is no Sim ---
    t_dsBits = bitsV;
    assert(DsTestAsk(kv, 5800, 1, 0, &asked) == 2 && !asked && c.hits == 19 && c.classAsks[DS_CLS_NOACTOR] == 1);  // a door row serves it too
    assert(DsTestAsk(kw, 5800, 2, DS_KIND_DOOR, &asked) == 2 && asked && c.liveNoNote == 1 && !c.puts[DS_KIND_NOACTOR]);  // a Sim's note is not its note
    assert(DsTestAsk(kw, 5800, 2, DS_KIND_NOACTOR, &asked) == 2 && asked && c.puts[DS_KIND_NOACTOR] == 1);
    assert(DsTestAsk(kw, 5810, 1, 0, &asked) == 2 && !asked && c.hits == 20);
    // an ordinary Sim at that door: the row is not for it; its live answer (here: locked for it) neither checks nor drops the row
    DWORD kc[9] = {0x11, 0x22, 0xA0A0, 0xB0B0, 0x55, 0x66, 5, 6, 0};
    t_dsBits = bitsA;
    const LONG checks0 = c.checks;
    assert(DsTestAsk(kc, 5820, 1, 0, &asked) == 1 && asked && c.noTake[DS_NO_KIND] == 1 && c.checks == checks0 && !c.mismatch && !c.drops);
    t_dsBits = bitsV;
    assert(DsTestAsk(kw, 5830, 1, 0, &asked) == 2 && !asked && c.hits == 21);
    // an ordinary Sim's own noted answer there upgrades the row; the boat keeps using it, and its own note does not downgrade it
    t_dsBits = bitsA;
    assert(DsTestAsk(kc, 5840, 2, DS_KIND_INERT, &asked) == 2 && asked && c.puts[DS_KIND_INERT] == 1);
    assert(DsTestAsk(kc, 5850, 1, 0, &asked) == 2 && !asked && c.hits == 22);
    t_dsBits = bitsV;
    assert(DsTestAsk(kw, 5860, 1, 0, &asked) == 2 && !asked && c.hits == 23);

    // --- drops, checks, shadow ---
    // an "unlocked" answer ts3fixes did not mark (a handler was added and reported late, say): the row goes
    t_dsBits = bitsA;
    DsAsk a = {};
    c.checkN = 1;
    assert(!DsBefore(ka, 0x100, 5900, &a) && a.on && a.row);  // valid, asked anyway
    DsLive(&a);
    DsNoteOp(DsNoteHashKey((const uint32_t*)kb), DS_KIND_DOOR);  // a note for another ask does not count
    DsAfter(&a, ka, 2, 10);
    assert(c.liveNoNote == 2 && c.drops == 1 && !c.mismatch);
    c.checkN = 1000;
    assert(DsTestAsk(ka, 6100, 2, DS_KIND_INERT, &asked) == 2 && asked && c.miss[DS_NONE] == 4 && c.puts[DS_KIND_INERT] == 2);
    // the sampled re-ask: equal = nothing; different = off for the session
    c.checkN = 1;
    const LONG checks = c.checks;
    assert(DsTestAsk(ka, 6200, 2, DS_KIND_INERT, &asked) == 2 && asked && c.checks == checks + 1 && !c.mismatch && c.mode == 1);
    // a change event of the row's door while the sampled re-ask is out (the sim thread locked it, then served the ask): no
    // mismatch, the session stays on; the "locked" answer drops the row and the next noted answer writes it again
    {
        DsAsk b = {};
        assert(!DsBefore(ka, 0x100, 6250, &b) && b.on && b.row);
        DsLive(&b);
        DsSrcBump(DS_SRC_LOCK, DsIdHash(0x33, 0x44));
        DsAfter(&b, ka, 1, 10);
        assert(c.checks == checks + 1 && !c.mismatch && c.mode == 1);
        assert(DsTestAsk(ka, 6260, 2, DS_KIND_INERT, &asked) == 2 && asked && c.checks == checks + 1 && !c.mismatch);
    }
    assert(DsTestAsk(ka, 6300, 1, 0, &asked) == 1 && asked && c.mismatch == 1 && c.mode == 0);
    // shadow: never answers, compares every valid row
    c.mode = 2, c.checkN = 1000;
    DsWevBump(5);
    assert(DsTestAsk(ka, 7000, 2, DS_KIND_DOOR, &asked) == 2 && asked);
    const LONG hits = c.hits;
    assert(DsTestAsk(ka, 7100, 2, DS_KIND_DOOR, &asked) == 2 && asked && c.hits == hits && c.checks == checks + 3 && c.mismatch == 1);
    assert(DsTestAsk(ka, 7200, 3, 0, &asked) == 3 && asked && c.mismatch == 2 && c.mode == 2);
    // op 40: the class is good for the route's next START only
    assert(DsClassOp(0x25000001, (DWORD)bitsA | 0x00280000) == 1 && DsClassTake(0x25000002) == 0 && DsClassTake(0x25000001) == 0);
    DsClassOp(0x25000001, (DWORD)bitsA);
    assert(DsClassTake(0x25000001) == bitsA && DsClassTake(0x25000001) == 0 && DsClassTake(0) == 0);
    c.mode = 0;
    assert(DsClassOp(0x25000001, 1) == 0);

    memcpy((void*)&c, (const void*)&saved, sizeof(c));
    memcpy(&g_dsStamps, &savedStamps, sizeof(savedStamps));
    memset(&g_dsTab, 0, sizeof(g_dsTab));
    memset(&g_dsSeenSim, 0, sizeof(g_dsSeenSim));
    memset(&g_dsSeenDoor, 0, sizeof(g_dsSeenDoor));
    ts3native_doorgen.armed = sArmed, t_hcScope = sScope, t_dsBits = sBits, t_dsPlDepth = sDepth, t_dsPlans = sPlans;
    g_dsTicks = sTicks, g_dsCheckSeq = 0, g_dsH = sH, g_dsBits = sHb;
    printf("DoorShare (docs/674): class-shared door rows (put by note, hit for another Sim, stamps per owner / lot / global, delegate, age, "
           "drop), change events by scope, the no-actor class, scope and command rules, sampled check and shadow, op 40 class take\n");
}
