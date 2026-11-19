// TestNoPathMemo ([RouteAsync] NoPathMemo, nopathmemo-core.h, docs/643). Included by selftest.cpp; uses only the pure table
// (assert, nopathmemo-core.h), so it also builds as a plain C++ program on any host.
static NpmReq NpmTReq(uint64_t foll, uint64_t goal, float x, float z) {
    NpmReq r;
    memset(&r, 0, sizeof(r));
    r.foll = foll, r.goal = goal, r.agent = 0x800;
    r.opts[1] = 0x10000000;  // EnableWaterPlanning
    r.start[0] = x, r.start[1] = 0, r.start[2] = z;
    r.key = NpmReqKey(r, 0x1234);
    return r;
}

static void TestNoPathMemo() {
    static NpmTable t;  // ~25 KB: static, not on the stack
    NpmTabClear(&t);
    const uint64_t ttl = NpmTtlTicks(120);
    const float tol = 1.0f;
    int32_t ms = -1;

    // TTL in sim ticks: 120 sim-minutes = 4500 ticks
    assert(ttl == 4500 && NpmTtlTicks(1) == 37 && NpmTtlTicks(0) == 0 && NpmTtlTicks(-5) == 0);

    // key: follower, destination set, agent, options and world, never the start
    const NpmReq a = NpmTReq(1, 0xAAAA, 10.0f, 20.0f);
    NpmReq b = a;
    b.opts[1] = 0;
    assert(NpmReqKey(b, 0x1234) != a.key && NpmReqKey(a, 0x1235) != a.key);
    b = NpmTReq(2, 0xAAAA, 10.0f, 20.0f);
    assert(b.key != a.key && NpmTReq(1, 0xBBBB, 10.0f, 20.0f).key != a.key);
    assert(NpmTReq(1, 0xAAAA, 10.4f, 20.3f).key == a.key);  // the start drifts: same key

    // start tolerance: EA's retry from 0.3-0.7 m further is answered (also across a 0.5 m cell edge), 1.5 m away is not
    assert(NpmTabLook(&t, a, 100, ttl, 7, 9, tol, &ms) == NPM_MISS);
    assert(NpmTabPut(&t, a, 100, 7, 9, false, 420) == 0);
    assert(NpmTabLook(&t, NpmTReq(1, 0xAAAA, 10.49f, 20.0f), 101, ttl, 7, 9, tol, &ms) == NPM_HIT && ms == 420);
    assert(NpmTabLook(&t, NpmTReq(1, 0xAAAA, 10.51f, 20.5f), 101, ttl, 7, 9, tol, &ms) == NPM_HIT);
    assert(NpmTabLook(&t, NpmTReq(1, 0xAAAA, 11.5f, 20.0f), 101, ttl, 7, 9, tol, &ms) == NPM_FAR);
    assert(NpmTabLook(&t, a, 101, ttl, 7, 9, tol, &ms) == NPM_HIT && NpmTabLive(&t) == 1);  // FAR kept the entry
    assert(NpmTabLook(&t, NpmTReq(1, 0xBBBB, 10.0f, 20.0f), 101, ttl, 7, 9, tol, &ms) == NPM_MISS);

    // TTL: an answer does not extend the entry; at tick + ttl it expires and is dropped
    assert(NpmTabLook(&t, a, 100 + ttl - 1, ttl, 7, 9, tol, &ms) == NPM_HIT);
    assert(NpmTabLook(&t, a, 100 + ttl, ttl, 7, 9, tol, &ms) == NPM_EXPIRED);
    assert(NpmTabLook(&t, a, 100 + ttl, ttl, 7, 9, tol, &ms) == NPM_MISS && NpmTabLive(&t) == 0);
    // the clock going back (another save loaded) drops it too
    NpmTabPut(&t, a, 5000, 7, 9, false, 300);
    assert(NpmTabLook(&t, a, 4999, ttl, 7, 9, tol, &ms) == NPM_EXPIRED && NpmTabLive(&t) == 0);

    // invalidation: the static world version (lot routing, water grid) always; the footprint version only for a dynamic request
    NpmTabPut(&t, a, 10, 7, 9, false, 300);
    assert(NpmTabLook(&t, a, 11, ttl, 7, 10, tol, &ms) == NPM_HIT);       // footprints moved: not read by this request
    assert(NpmTabLook(&t, a, 12, ttl, 8, 10, tol, &ms) == NPM_INVALID);   // a rebuild
    assert(NpmTabLook(&t, a, 13, ttl, 8, 10, tol, &ms) == NPM_MISS);
    NpmTabPut(&t, b, 10, 7, 9, true, 300);
    assert(NpmTabLook(&t, b, 11, ttl, 7, 9, tol, &ms) == NPM_HIT);
    assert(NpmTabLook(&t, b, 12, ttl, 7, 10, tol, &ms) == NPM_INVALID);
    // a success (or another failure) of the same request drops its memo; re-recording replaces in place, start included
    NpmTabPut(&t, a, 10, 7, 9, false, 300);
    assert(NpmTabPut(&t, NpmTReq(1, 0xAAAA, 13.0f, 20.0f), 20, 7, 9, false, 310) == 0 && NpmTabLive(&t) == 1);
    assert(NpmTabLook(&t, NpmTReq(1, 0xAAAA, 13.2f, 20.0f), 21, ttl, 7, 9, tol, &ms) == NPM_HIT && ms == 310);
    assert(NpmTabLook(&t, a, 21, ttl, 7, 9, tol, &ms) == NPM_FAR);
    assert(NpmTabDrop(&t, a.key) == 1 && NpmTabDrop(&t, a.key) == 0);
    assert(NpmTabLook(&t, a, 22, ttl, 7, 9, tol, &ms) == NPM_MISS);

    // miss diagnostic: the nearest live entry of the same follower
    NpmTabClear(&t);
    assert(!NpmTabNearest(&t, 1, a.start));
    NpmTabPut(&t, NpmTReq(1, 0xAAAA, 30.0f, 20.0f), 10, 7, 9, false, 1);
    NpmTabPut(&t, NpmTReq(1, 0xBBBB, 12.0f, 20.0f), 10, 7, 9, false, 2);
    NpmTabPut(&t, NpmTReq(2, 0xAAAA, 10.0f, 20.0f), 10, 7, 9, false, 3);
    const NpmEntry* n = NpmTabNearest(&t, 1, a.start);
    assert(n && n->ms == 2 && n->req.goal == 0xBBBB);

    // eviction: keys of one set (same NpmSet); the ninth evicts the least recently used, which an answer refreshes
    NpmTabClear(&t);
    NpmReq keys[NPM_WAYS + 1];
    int k = 0;
    for (uint64_t g = 1; k < NPM_WAYS + 1; g++) {
        const NpmReq r = NpmTReq(7, g, 0, 0);
        if (NpmSet(&t, r.key) == NpmSet(&t, NpmTReq(7, 1, 0, 0).key)) keys[k++] = r;
    }
    for (int i = 0; i < NPM_WAYS; i++) assert(NpmTabPut(&t, keys[i], 100 + i, 7, 9, false, i) == 0);
    assert(NpmTabLook(&t, keys[0], 200, ttl, 7, 9, tol, &ms) == NPM_HIT && ms == 0);  // keys[0] is now the most recent
    assert(NpmTabPut(&t, keys[NPM_WAYS], 201, 7, 9, false, 99) == 1);                // evicts keys[1], the oldest use
    assert(NpmTabLook(&t, keys[1], 202, ttl, 7, 9, tol, &ms) == NPM_MISS);
    assert(NpmTabLook(&t, keys[0], 202, ttl, 7, 9, tol, &ms) == NPM_HIT);
    assert(NpmTabLook(&t, keys[NPM_WAYS], 202, ttl, 7, 9, tol, &ms) == NPM_HIT && ms == 99);
    assert(NpmTabLive(&t) == NPM_WAYS);
    // memory is bounded: a flood of distinct keys never holds more than the table
    for (uint64_t g = 1000; g < 1000 + 4 * NPM_ENTRIES; g++) NpmTabPut(&t, NpmTReq(g, g, 0, 0), 300, 7, 9, false, 1);
    assert(NpmTabLive(&t) == NPM_ENTRIES);

    // a new world clears everything
    assert(NpmTabClear(&t) == NPM_ENTRIES && NpmTabLive(&t) == 0);
    printf("selftest nopathmemo: key, start tolerance, TTL, invalidation, eviction, nearest ok\n");
}
