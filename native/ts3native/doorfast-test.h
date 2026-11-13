// --- [DoorFast] (doorfast.cpp, docs/548): tables filled over the door channel, the predictor's rules (a-g) and no-prediction reasons,
// shadow compare, Mode 2 answers (household, door locked, self-check, fail latch, switch), the pass-through rules, the
// invalidations (door lock IC, invalidate-all, epoch) and the start conditions. Included by selftest.cpp after hostcalls-test.h.
static DWORD DfTAsk(DWORD lotLo, DWORD sim, DWORD ownerLo, DWORD ownerHi, DWORD initialLocked) {  // lot hi 3, sim hi 0
    return ((P4DoorFn)HcDoorStub)(lotLo, 3, sim, 0, ownerLo, ownerHi, 0xABCD0003, 0x12340004, 0xFFFFFF00 | initialLocked);
}
static bool DfTMsg(DWORD code, DWORD v) { return SpDoorMsg(v, 0x84000000 | code << 16) != 0; }
static void DfTActor(DWORD id, DWORD flags) { assert(DfTMsg(SDM_ACT_LO, id) && DfTMsg(SDM_ACT_HI, 0) && DfTMsg(SDM_ACT_FLAGS, flags)); }
// lot 0x300000123: doors A1 plain, A2 lock 6, A3 overridden, A4 barn, A5 one-way, A6 single gender (an override of its own),
// A7 male door (DoorSingleGender's IsAllowedThrough, v920 rule d), A8 the same with its gender unread; rule-b ids B1,
// 0x1000000B2; typed owners D0 (kind 0, ElevatorDoors: rule c), D4 (DivingBoard: rule f), D5 (Ladder: rule e) and D6 (kind 6,
// TubeElevator: none); hid: one hidden room id
static void DfTLot(DWORD flags, bool hid = false, DWORD hidden = 0) {
    static const DWORD door[8][3] = {{0xA1, 1, 3}, {0xA2, 1, 6}, {0xA3, 1 | 64, 3}, {0xA4, 1 | 2, 3}, {0xA5, 1, 3}, {0xA6, 1 | 32 | 64, 3},
                                     {0xA7, 1 | 32 | 64 | kDfGenderOwn | kDfDoorMale, 6}, {0xA8, 1 | 32 | 64 | kDfGenderOwn, 3}};
    assert(DfTMsg(SDM_LOT_LO, 0x123) && DfTMsg(SDM_LOT_HI, 3));
    for (const auto& d : door) {
        assert(DfTMsg(SDM_OBJ_LO, d[0]) && DfTMsg(SDM_OBJ_HI, 0) && DfTMsg(SDM_KIND, d[1]) && DfTMsg(SDM_LOCK, d[2]));
        assert(DfTMsg(SDM_ONEWAY, d[0] == 0xA5 ? 0 : 36863));  // room 0 is a valid one-way target
    }
    assert(DfTMsg(SDM_B_LO, 0xB1) && DfTMsg(SDM_B_HI, 0) && DfTMsg(SDM_B_LO, 0xB2) && DfTMsg(SDM_B_HI, 1));
    static const DWORD typed[4][2] = {{0, 0xD0}, {4, 0xD4}, {5, 0xD5}, {6, 0xD6}};
    for (const auto& t : typed) assert(DfTMsg(SDM_T_KIND, t[0]) && DfTMsg(SDM_T_LO, t[1]) && DfTMsg(SDM_T_HI, 0));
    if (hid) assert(DfTMsg(SDM_HIDDEN, hidden));
    assert(DfTMsg(SDM_LOT_FLAGS, flags) && DfTMsg(SDM_EPOCH, 1) && DfTMsg(SDM_END, 8));
}

static void TestDoorFast() {
    Ts3nCounters& k = ts3native_counters;
    const size_t v916 = offsetof(Ts3nCounters, dfMode);
    memset((u8*)&k + v916, 0, sizeof(Ts3nCounters) - v916);
    const DWORD me = GetCurrentThreadId(), savedWorker = g_workerTid;
    InvokeJob* const savedInv = g_invJob;
    void* const savedJmp = g_hcDoorJmp;
    const LONG savedEpoch = g_spEpoch, savedOn = g_spOn, savedSn = k.snReady;
    const int savedDoorN = g_spDoorN;
    g_invJob = NULL, g_workerTid = me, t_hcJob = 1, g_hcDoorJmp = (void*)HcFakeDoor, g_dfSelfN = 0, g_dfMisLog = 0;
    g_spEpoch = 9;
    enum { H = SDA_HUMAN, NM = SDA_NOT_MUMMY, NP = SDA_NO_PROPRIETOR, HH = SDA_HOUSEHOLD };
    DfTLot(SDLF_ONE_HANDLER);
    DfTActor(0x51, H | NM | NP);       // eligible human
    DfTActor(0x52, H | NM | NP | HH);  // household scope
    DfTActor(0x53, NM | NP);           // pet / horse / stray
    DfTActor(0x54, H | NP);            // tomb mummy
    DfTActor(0x55, H | NM);            // proprietor
    assert(k.dfActors == 5);

    // Mode 1 (shadow): every ask goes to the sim; predictions are compared
    k.dfMode = 1;
    LONG calls = g_hcFakeCalls;
    g_hcFakeAns = 2;
    assert(DfTAsk(0x123, 0x51, 0xA1, 0, 0) == 2 && g_hcFakeCalls == calls + 1 && k.dfPredA == 1 && k.dfMatch == 1);  // rule a
    assert(DfTAsk(0x123, 0x53, 0xB1, 0, 0) == 2 && k.dfPredB == 1 && k.dfMatch == 2);  // rule b: any actor species
    g_hcFakeAns = 1;
    assert(DfTAsk(0x123, 0x51, 0xB2, 1, 1) == 1 && k.dfPredB == 2 && k.dfMatch == 3);  // rule b: initialLocked -> locked
    assert(DfTAsk(0x123, 0x51, 0xA1, 0, 0) == 1 && k.dfMisA == 1 && !k.dfFail);  // a mismatch in Mode 1: counted only
    assert(k.dfSent == 4 && k.dfAnswered == 0 && g_hcFakeCalls == calls + 4);
    // no-prediction reasons, in the predictor's order
    struct { DWORD lot, sim, owner, init; int why; } no[] = {
        {0x124, 0x51, 0xA1, 0, DF_NO_LOT}, {0x123, 0x99, 0xA1, 0, DF_NO_ACTOR}, {0x123, 0x54, 0xB1, 0, DF_NO_ROLE},
        {0x123, 0x55, 0xA1, 0, DF_NO_ROLE}, {0x123, 0x51, 0xC1, 0, DF_NO_OWNER}, {0x123, 0x51, 0xA2, 0, DF_NO_DOOR},
        {0x123, 0x51, 0xA3, 0, DF_NO_DOOR}, {0x123, 0x51, 0xA4, 0, DF_NO_DOOR}, {0x123, 0x51, 0xA5, 0, DF_NO_DOOR},
        {0x123, 0x51, 0xA6, 0, DF_NO_DOOR}, {0x123, 0x51, 0xA1, 1, DF_NO_INITIAL}, {0x123, 0x53, 0xA1, 0, DF_NO_ANIMAL}};
    for (const auto& c : no) {
        const LONG n0 = k.dfNo[c.why];
        DfTAsk(c.lot, c.sim, c.owner, 0, c.init);
        assert(k.dfNo[c.why] == n0 + 1);
    }
    DfTLot(0);  // another handler listens: nothing predicted
    DfTAsk(0x123, 0x51, 0xA1, 0, 0);
    DfTAsk(0x123, 0x51, 0xB1, 0, 0);
    assert(k.dfNo[DF_NO_HANDLERS] == 2);
    DfTLot(SDLF_ONE_HANDLER | SDLF_HIDDEN_ROOMS);  // hidden rooms: doors not predicted, rule b still is
    const LONG pb = k.dfPredB;
    DfTAsk(0x123, 0x51, 0xA1, 0, 0);
    DfTAsk(0x123, 0x51, 0xB1, 0, 0);
    assert(k.dfNo[DF_NO_HIDDEN] == 1 && k.dfPredB == pb + 1);
    DfTLot(SDLF_ONE_HANDLER);
    // stale: a world epoch, an old actor record
    DWORD key[9] = {0x123, 3, 0x51, 0, 0xA1, 0, 3, 4, 0};
    assert(DfPredict(key, GetTickCount()).ans == 2 && DfPredict(key, GetTickCount()).rule == 1);
    assert(DfPredict(key, GetTickCount() + 20000).why == DF_NO_ACTOR);
    g_spEpoch++;
    assert(DfPredict(key, GetTickCount()).why == DF_NO_LOT);
    g_spEpoch--;
    // pass-through: another thread, no job, a nested plan, a clone run: not seen
    const LONG asks = k.dfAsks;
    g_workerTid = me + 7;
    DfTAsk(0x123, 0x51, 0xA1, 0, 0);
    g_workerTid = me, t_hcJob = 0;
    DfTAsk(0x123, 0x51, 0xA1, 0, 0);
    t_hcJob = 1;
    InvokeJob nest = {};
    g_invJob = &nest;
    DfTAsk(0x123, 0x51, 0xA1, 0, 0);
    g_invJob = NULL, t_snapRun = 1;
    DfTAsk(0x123, 0x51, 0xA1, 0, 0);
    t_snapRun = 0;
    assert(k.dfAsks == asks);

    // Mode 2: answers for actors outside household scope, every 16th asked; household, a switch at 0, a door predicted locked asked
    k.dfMode = 2, k.dfAnswer = 1, k.dfFail = 0, g_dfSelfN = 0;
    g_hcFakeAns = 2;
    calls = g_hcFakeCalls;
    const LONG sent = k.dfSent;
    for (int i = 0; i < 16; i++) assert(DfTAsk(0x123, 0x51, 0xA1, 0, 0) == 2);
    assert(k.dfAnswered == 15 && k.dfSelf == 1 && g_hcFakeCalls == calls + 1 && k.dfSent == sent + 1);
    g_hcFakeAns = 1;
    assert(DfTAsk(0x123, 0x51, 0xB1, 0, 1) == 1 && k.dfAnswered == 16 && g_hcFakeCalls == calls + 1);  // rule b locked: no door
    g_hcFakeAns = 2;
    assert(DfTAsk(0x123, 0x52, 0xA1, 0, 0) == 2 && k.dfHouse == 1 && g_hcFakeCalls == calls + 2);  // household: always asked
    k.dfAnswer = 0;
    assert(DfTAsk(0x123, 0x51, 0xA1, 0, 0) == 2 && g_hcFakeCalls == calls + 3 && k.dfAnswered == 16);  // --ab doorfast 0
    k.dfAnswer = 1;
    // a door predicted locked or discouraged is never answered natively (LockedDoorsDuringPlan); rules a / b predict none, so
    // the decision step gets one by hand; a non-door locked prediction is answered
    const DfPred doorLocked = {1, H | NM | NP, 1, 0, 3, true}, doorDisc = {3, H | NM | NP, 1, 0, 3, true}, portalLocked = {1, H | NM | NP, 0, 0, 2, false};
    g_dfSelfN = 0;
    assert(!DfDecide(&doorLocked) && !DfDecide(&doorDisc) && k.dfDoorLocked == 2 && DfDecide(&portalLocked) == 1);
    // a mismatch in Mode 2 (at a self-check ask) stops answering for the session
    g_dfSelfN = 15;
    g_hcFakeAns = 1;
    assert(DfTAsk(0x123, 0x51, 0xA1, 0, 0) == 1 && k.dfMisA == 2 && k.dfFail == 1);
    calls = g_hcFakeCalls;
    g_hcFakeAns = 2;
    assert(DfTAsk(0x123, 0x51, 0xA1, 0, 0) == 2 && g_hcFakeCalls == calls + 1 && k.dfAnswered == 17);
    k.dfFail = 0;

    // invalidations: the door lock event IC (slot 24) and build/buy exit (22) with [RouteSnap] on; the actors stay (v919: only a
    // world change drops them), each source counted
    g_spOn = 1;
    assert(DfPredict(key, GetTickCount()).ans == 2);
    SpIcNote(24);
    assert(DfPredict(key, GetTickCount()).why == DF_NO_LOT);
    DfTLot(SDLF_ONE_HANDLER);
    assert(DfPredict(key, GetTickCount()).ans == 2);
    SpIcNote(22);
    assert(DfPredict(key, GetTickCount()).why == DF_NO_LOT && k.dfInvAll[1] == 2);
    assert(DfTMsg(SDM_INV_ALL, 0) && k.dfInvAll[2] == 1 && SpActorFind(0x51, GetTickCount(), 10000));  // kept
    DfTLot(SDLF_ONE_HANDLER);
    assert(DfPredict(key, GetTickCount()).ans == 2);
    SpDoorInvalidateAll(0);  // a world event or epoch: the actors go too
    DfTLot(SDLF_ONE_HANDLER);
    assert(DfPredict(key, GetTickCount()).why == DF_NO_ACTOR && k.dfInvAll[0] == 1);
    for (DWORD id = 0x51; id <= 0x55; id++) DfTActor(id, id == 0x51 ? H | NM | NP : id == 0x52 ? H | NM | NP | HH : id == 0x53 ? NM | NP : id == 0x54 ? H | NP : H | NM);
    g_spOn = savedOn;

    // coverage (v919): lot / actor splits, the missed-lot ring and its hand-out, hidden rooms per room pair, typed owners, a
    // router that is no Sim, shadow guesses (compared, never answered), actor evictions
    memset(g_spMiss, 0, sizeof(g_spMiss));
    const size_t v919 = offsetof(Ts3nCounters, dfMissRec);
    memset((u8*)&k + v919, 0, sizeof(Ts3nCounters) - v919);
    k.dfMode = 1;
    const DWORD kz[9] = {0, 0, 0x51, 0, 0xA1, 0, 3, 4, 0};
    assert(DfPredict(kz, GetTickCount()).why == DF_NO_LOT && DfPredict(kz, GetTickCount()).sub == 0);  // lot id 0: never pushed
    DfTAsk(0x125, 0x51, 0xA1, 0, 0);
    DfTAsk(0x125, 0x51, 0xA1, 0, 0);
    assert(k.dfLotNo[1] == 2 && k.dfMissRec == 1);  // no table; one ring entry
    assert(DfTMsg(SDM_INV_LO, 0x123) && DfTMsg(SDM_INV_HI, 3));
    DfTAsk(0x123, 0x51, 0xA1, 0, 0);
    assert(k.dfLotNo[2] == 1 && k.dfMissRec == 2);  // pushed, then invalidated
    // hand-out in slot order, 64 bits each, a lot at most every kSpMissHandMs; a lot covered since or not asked for kSpMissKeepMs drops
    auto missed = []() { u64 id = 0; for (DWORD b = 0; b < 64; b++) if (DfTMsg(SDM_MISS_BIT, b)) id |= 1ull << b; return id; };
    assert(DfTMsg(SDM_MISS_NEXT, 0) && missed() == 0x300000125ull && DfTMsg(SDM_MISS_NEXT, 0) && missed() == 0x300000123ull);
    assert(!DfTMsg(SDM_MISS_NEXT, 0) && k.dfMissHand == 2);
    DfTLot(SDLF_ONE_HANDLER);
    for (SpMiss& m : g_spMiss) if (m.id == 0x300000123ull) m.handed -= kSpMissHandMs;
    assert(!DfTMsg(SDM_MISS_NEXT, 0));
    for (const SpMiss& m : g_spMiss) assert(m.id != 0x300000123ull);
    for (SpMiss& m : g_spMiss) if (m.id) m.seen -= kSpMissKeepMs + 1, m.handed -= kSpMissHandMs;
    assert(!DfTMsg(SDM_MISS_NEXT, 0) && k.dfMissHand == 2);
    for (const SpMiss& m : g_spMiss) assert(!m.id);
    g_spEpoch++;
    assert(DfPredict(key, GetTickCount()).sub == 3);  // a table of an older world epoch
    g_spEpoch--;
    // actor split: no record, a record older than ActorMs
    const DWORD k99[9] = {0x123, 3, 0x99, 0, 0xA1, 0, 3, 4, 0};
    assert(DfPredict(k99, GetTickCount()).sub == 0 && DfPredict(key, GetTickCount() + 20000).sub == 1);
    DfTAsk(0x123, 0x99, 0xA1, 0, 0);
    assert(k.dfActNo[0] == 1);
    // hidden rooms per pair (DfTAsk asks rooms 3 -> 4): room 3 hidden; another room hidden; EA widens the short room id, so 36863
    // matches the hidden id -28673 and not 36863; the list unknown (flag)
    DfTLot(SDLF_ONE_HANDLER, true, 3);
    DfTAsk(0x123, 0x51, 0xA1, 0, 0);
    assert(k.dfHidN[0] == 1 && k.dfNo[DF_NO_HIDDEN] == 2);
    DfTLot(SDLF_ONE_HANDLER, true, 5);
    assert(DfPredict(key, GetTickCount()).ans == 2);
    const DWORD kHigh[9] = {0x123, 3, 0x51, 0, 0xA1, 0, 0x8FFF, 4, 0};
    DfTLot(SDLF_ONE_HANDLER, true, 36863);
    assert(DfPredict(kHigh, GetTickCount()).ans == 2);
    DfTLot(SDLF_ONE_HANDLER, true, (DWORD)-28673);
    assert(DfPredict(kHigh, GetTickCount()).why == DF_NO_HIDDEN && DfPredict(kHigh, GetTickCount()).sub == 0);
    DfTLot(SDLF_ONE_HANDLER | SDLF_HIDDEN_ROOMS);
    DfTAsk(0x123, 0x51, 0xA1, 0, 0);
    assert(k.dfHidN[1] == 1);
    DfTLot(SDLF_ONE_HANDLER);
    // typed owners: D0 (ElevatorDoors) for a pet counted only (rule c needs a human); D6 (TubeElevator) also guessed
    // initialLocked ? locked : unlocked; Mode 2 never answers a guess, and a guess mismatch never latches
    g_hcFakeAns = 2;
    DfTAsk(0x123, 0x53, 0xD0, 0, 0);
    assert(k.dfNo2[1] == 1 && k.dfType[0] == 1 && !k.dfGuessOk[1] && !k.dfGuessBad[1]);
    DfTAsk(0x123, 0x51, 0xD6, 0, 0);
    DfTAsk(0x123, 0x51, 0xD6, 0, 1);  // live 2, guessed 1
    assert(k.dfType[6] == 2 && k.dfGuessOk[1] == 1 && k.dfGuessBad[1] == 1);
    k.dfMode = 2, k.dfAnswer = 1, k.dfFail = 0;
    calls = g_hcFakeCalls;
    LONG answered = k.dfAnswered;
    assert(DfTAsk(0x123, 0x51, 0xD6, 0, 1) == 2 && g_hcFakeCalls == calls + 1 && k.dfAnswered == answered && k.dfGuessBad[1] == 2 && !k.dfFail);
    k.dfMode = 1, k.dfAnswer = 0;

    // rules c-g (v920): the predictor's class per actor / lot fact, then counted, compared and answered like a / b
    enum { M = SDA_MALE, F = SDA_FEMALE, T = SDA_TODDLER, P = SDA_PREGNANT, L = SDA_AVOID_LADDER, NS = SDA_NOT_SIM };
    DfTActor(0x56, NS);                   // router no Sim, outside household scope (no role bits: rule g needs none)
    DfTActor(0x57, H | NM | NP | M);      // man
    DfTActor(0x58, H | NM | NP | F | P);  // pregnant woman
    DfTActor(0x59, H | NM | NP | M | T);  // toddler boy
    DfTActor(0x5A, H | NM | NP | M | L);  // man who avoids ladders (elder or trait)
    DfTActor(0x5B, NS | HH);              // router no Sim, household scope
    DfTActor(0x5C, NM | NP | L);          // elder pet
    struct { DWORD lotFlags, sim, owner, init, ans; int rule, why; } rc[] = {
        // rule c: ElevatorDoors, a human not toddler-or-below, no apartment
        {0, 0x57, 0xD0, 0, 2, DF_RULE_C, 0}, {0, 0x57, 0xD0, 1, 1, DF_RULE_C, 0}, {0, 0x58, 0xD0, 0, 2, DF_RULE_C, 0},
        {0, 0x59, 0xD0, 0, 0, 0, DF_NO_RULE}, {0, 0x5C, 0xD0, 0, 0, 0, DF_NO_RULE}, {SDLF_APARTMENT, 0x57, 0xD0, 0, 0, 0, DF_NO_RULE},
        {SDLF_RESORT, 0x57, 0xD0, 1, 1, DF_RULE_C, 0},
        // rule d: a human of the door's gender, initialLocked 0, no resort; the door's lock and hidden rooms do not count
        {0, 0x57, 0xA7, 0, 2, DF_RULE_D, 0}, {SDLF_HIDDEN_ROOMS, 0x57, 0xA7, 0, 2, DF_RULE_D, 0}, {SDLF_APARTMENT, 0x59, 0xA7, 0, 2, DF_RULE_D, 0},
        {0, 0x57, 0xA7, 1, 0, 0, DF_NO_INITIAL}, {0, 0x58, 0xA7, 0, 0, 0, DF_NO_DOOR}, {0, 0x51, 0xA7, 0, 0, 0, DF_NO_DOOR},
        {0, 0x5C, 0xA7, 0, 0, 0, DF_NO_ANIMAL}, {0, 0x57, 0xA8, 0, 0, 0, DF_NO_DOOR}, {SDLF_RESORT, 0x57, 0xA7, 0, 0, 0, DF_NO_DOOR},
        {0, 0x57, 0xA6, 0, 0, 0, DF_NO_DOOR},
        // rule e: Ladder, locked before discouraged, any species
        {0, 0x57, 0xD5, 0, 2, DF_RULE_E, 0}, {0, 0x57, 0xD5, 1, 1, DF_RULE_E, 0}, {0, 0x5A, 0xD5, 0, 3, DF_RULE_E, 0},
        {0, 0x5A, 0xD5, 1, 1, DF_RULE_E, 0}, {0, 0x5C, 0xD5, 0, 3, DF_RULE_E, 0}, {SDLF_APARTMENT | SDLF_RESORT, 0x59, 0xD5, 0, 2, DF_RULE_E, 0},
        // rule f: DivingBoard, locked for initialLocked, toddler-or-below or visibly pregnant
        {0, 0x57, 0xD4, 0, 2, DF_RULE_F, 0}, {0, 0x57, 0xD4, 1, 1, DF_RULE_F, 0}, {0, 0x58, 0xD4, 0, 1, DF_RULE_F, 0},
        {0, 0x59, 0xD4, 0, 1, DF_RULE_F, 0}, {0, 0x5C, 0xD4, 0, 2, DF_RULE_F, 0},
        // the role checks come first for c-f; rule g needs only the one handler, any owner (doors locked or not, unknown ids)
        {0, 0x54, 0xD5, 0, 0, 0, DF_NO_ROLE}, {0, 0x55, 0xD4, 0, 0, 0, DF_NO_ROLE}, {0, 0x55, 0xA7, 0, 0, 0, DF_NO_ROLE},
        {0, 0x56, 0xA1, 0, 2, DF_RULE_G, 0}, {0, 0x56, 0xA2, 1, 1, DF_RULE_G, 0}, {0, 0x56, 0xC1, 0, 2, DF_RULE_G, 0},
        {SDLF_HIDDEN_ROOMS, 0x56, 0xA7, 1, 1, DF_RULE_G, 0}, {0, 0x5B, 0xD4, 1, 1, DF_RULE_G, 0}, {0, 0, 0xB1, 0, 2, DF_RULE_G, 0},
        {0, 0, 0xA2, 1, 1, DF_RULE_G, 0}};
    for (const auto& c : rc) {
        DfTLot(SDLF_ONE_HANDLER | c.lotFlags);
        const DWORD kc[9] = {0x123, 3, c.sim, 0, c.owner, 0, 3, 4, c.init};
        const DfPred p = DfPredict(kc, GetTickCount());
        assert(p.ans == c.ans && (c.ans ? p.rule == c.rule : p.why == c.why));
    }
    DfTLot(0);
    const DWORD kg[9] = {0x123, 3, 0x56, 0, 0xA1, 0, 3, 4, 0}, k0[9] = {0x123, 3, 0, 0, 0xA1, 0, 3, 4, 0};
    assert(DfPredict(kg, GetTickCount()).why == DF_NO_HANDLERS && DfPredict(k0, GetTickCount()).why == DF_NO_HANDLERS);
    DfTLot(SDLF_ONE_HANDLER);
    assert(DfPredict(k0, GetTickCount()).actor == (NS | HH));  // router 0: a null Actor of unknown scope
    // Mode 1: counted per rule, matched or mismatched (no latch)
    g_hcFakeAns = 2;
    DfTAsk(0x123, 0x57, 0xD0, 0, 0), DfTAsk(0x123, 0x57, 0xA7, 0, 0), DfTAsk(0x123, 0x57, 0xD5, 0, 0), DfTAsk(0x123, 0x57, 0xD4, 0, 0);
    DfTAsk(0x123, 0x56, 0xA1, 0, 0);
    assert(k.dfPredC == 1 && k.dfPredD == 1 && k.dfPredE == 1 && k.dfPredF == 1 && k.dfPredG == 1);
    g_hcFakeAns = 1;
    DfTAsk(0x123, 0x57, 0xD0, 0, 0), DfTAsk(0x123, 0x57, 0xA7, 0, 0), DfTAsk(0x123, 0x57, 0xD5, 0, 0), DfTAsk(0x123, 0x57, 0xD4, 0, 0);
    DfTAsk(0x123, 0x56, 0xA1, 0, 0);
    assert(k.dfMisC == 1 && k.dfMisD == 1 && k.dfMisE == 1 && k.dfMisF == 1 && k.dfMisG == 1 && !k.dfFail);
    // Mode 2: c-f answered for a non-household Sim; rule g answers a locked door too (no Sim, nothing in LockedDoorsDuringPlan);
    // household-scope routers and router 0 are asked; a rule d / e prediction is never a locked door
    k.dfMode = 2, k.dfAnswer = 1, k.dfFail = 0, g_dfSelfN = 0;
    calls = g_hcFakeCalls, answered = k.dfAnswered;
    const LONG locked = k.dfDoorLocked;
    assert(DfTAsk(0x123, 0x57, 0xD0, 0, 1) == 1 && DfTAsk(0x123, 0x57, 0xA7, 0, 0) == 2 && DfTAsk(0x123, 0x5A, 0xD5, 0, 0) == 3);
    assert(DfTAsk(0x123, 0x58, 0xD4, 0, 0) == 1 && DfTAsk(0x123, 0x56, 0xA2, 0, 1) == 1 && DfTAsk(0x123, 0x56, 0xC1, 0, 0) == 2);
    assert(g_hcFakeCalls == calls && k.dfAnswered == answered + 6 && k.dfDoorLocked == locked);
    g_hcFakeAns = 2;
    assert(DfTAsk(0x123, 0x5B, 0xA1, 0, 0) == 2 && DfTAsk(0x123, 0, 0xA1, 0, 0) == 2 && g_hcFakeCalls == calls + 2 && k.dfAnswered == answered + 6);
    // a rule c-g mismatch at a self-check ask latches dfFail like a / b
    g_dfSelfN = 15, g_hcFakeAns = 1;
    assert(DfTAsk(0x123, 0x57, 0xD0, 0, 0) == 1 && k.dfMisC == 2 && k.dfFail == 1);
    k.dfMode = 1, k.dfAnswer = 0, k.dfFail = 0;
    // evictions: every slot fresh, a new actor overwrites one
    int used = 0;
    for (int i = 0; i < kSpActors; i++) used += g_spActors[i].id != 0;
    for (DWORD i = 0; i < (DWORD)kSpActors; i++) DfTActor(0x1000 + i, H | NM | NP);
    assert(k.dfActEvict == used);

    // start: needs the stub and [RouteSnap] Enabled=1 with its event hook and IC slots; defaults from a missing ini
    k.dfMode = 0, k.dfAnswer = 0;
    const wchar_t* none = L"C:\\no-such-dir-ts3n\\ts3native.ini";
    g_spOn = 1, k.snReady = 6;
    DfStart(none, 2, false);
    assert(!k.dfMode);
    g_spOn = 0;
    DfStart(none, 2, true);
    assert(!k.dfMode);
    g_spOn = 1, k.snReady = 2;
    DfStart(none, 1, true);
    assert(!k.dfMode);
    k.snReady = 7;
    DfStart(none, 1, true);
    assert(k.dfMode == 1 && !k.dfAnswer && g_dfActorMs == 10000 && g_dfLogMax == 20);
    DfStart(none, 2, true);
    assert(k.dfMode == 2 && k.dfAnswer == 1);
    assert(DfIniMode(none) == 0);

    g_spOn = savedOn, k.snReady = savedSn, g_spEpoch = savedEpoch, g_spDoorN = savedDoorN;
    memset(g_spActors, 0, sizeof(g_spActors));
    memset(g_spMiss, 0, sizeof(g_spMiss));
    g_workerTid = savedWorker, g_invJob = savedInv, g_hcDoorJmp = savedJmp, t_hcJob = 0;
    memset((u8*)&k + v916, 0, sizeof(Ts3nCounters) - v916);
    printf("door fast (v916 + v919 + v920): rules a / b, 12 no-prediction reasons + handlers + hidden, stale epoch / actor, pass-through, Mode 2 answers "
           "(self-check 1/16, household, switch, fail latch), lock-event / build-buy / channel / world invalidation (actors kept but at a world "
           "change), coverage: lot / actor splits, missed-lot ring hand-out, hidden rooms per pair, typed owners, guesses, evictions; rules c-g: "
           "%d predictor cases, counted / compared / answered (rule g locked doors, router 0 asked), start conditions ok\n", (int)(sizeof(rc) / sizeof(rc[0])));
}
