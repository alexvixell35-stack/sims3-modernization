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

// docs/643 §7 fuzzy match: FollowerMeetUpAtPoint's tries, a new RadialRange around a meeting point moved 0.45 m per try
static NpmReq NpmTFz(uint64_t foll, uint64_t shape, float sx, float sz, float gx, float gy, float gz, float ring) {
    NpmReq r = NpmTReq(foll, 0xC0FFEE + (uint64_t)(gx * 1000), sx, sz);  // the exact destination hash differs every try
    // 17 goal points on a ring of radius `ring` around (gx, gy, gz), 64-byte records as Route_PlanCommon's goals
    static uint8_t recs[17 * 64];
    memset(recs, 0, sizeof(recs));
    for (int i = 0; i < 17; i++) {
        float* p = (float*)(recs + i * 64);
        const float a = 6.2831853f * (float)i / 17.0f;
        p[0] = gx + ring * cosf(a), p[1] = gy, p[2] = gz + ring * sinf(a);
    }
    NpmGoalGeom(recs, 17, 64, &r);
    r.fkey = NpmFuzzyKey(r, 0x1234, shape);
    return r;
}

static void TestNoPathMemoFuzzy() {
    static NpmFzTable t;
    NpmFzClear(&t);
    const uint64_t ttl = NpmTtlTicks(10);  // 375 ticks: the second burst of the 2026-11-19 log came 260 ticks after the first
    int32_t ms = -1;
    float gm = -1, sm = -1;
    int d = 0;
    assert(ttl == 375);

    // goal geometry: centroid of the ring is its centre, spread its radius
    const NpmReq a = NpmTFz(1, 0x51, 10.0f, 20.0f, 30.0f, 5.0f, 40.0f, 3.5f);
    assert(a.gn == 17 && fabsf(a.gc[0] - 30.0f) < 0.01f && fabsf(a.gc[2] - 40.0f) < 0.01f && fabsf(a.gr - 3.5f) < 0.05f);
    NpmReq none = a;
    none.gn = 0;
    assert(NpmFzLook(&t, none, 100, ttl, 7, 9, &ms, &gm, &sm, &d) == NPM_MISS && NpmFzPut(&t, none, 100, 7, 9, false, 1) == 0);
    assert(NpmFzLive(&t) == 0);  // no goal points: never recorded or answered

    // the key ignores the destination records, not the follower, request shape (rooms, lot, types) or options
    assert(NpmTFz(1, 0x51, 10, 20, 31, 5, 40, 3.5f).fkey == a.fkey && NpmTFz(1, 0x51, 10, 20, 31, 5, 40, 3.5f).key != a.key);
    assert(NpmTFz(2, 0x51, 10, 20, 30, 5, 40, 3.5f).fkey != a.fkey && NpmTFz(1, 0x52, 10, 20, 30, 5, 40, 3.5f).fkey != a.fkey);
    NpmReq o = a;
    o.opts[0] = 4;
    assert(NpmFuzzyKey(o, 0x1234, 0x51) != a.fkey);

    // EA's next tries: meeting point 0.45 m on (hit), 0.85 m diagonal (hit), 1.2 m (far), another level 3 m up (far)
    assert(NpmFzLook(&t, a, 100, ttl, 7, 9, &ms, &gm, &sm, &d) == NPM_MISS);
    assert(NpmFzPut(&t, a, 100, 7, 9, false, 97) == 0 && NpmFzLive(&t) == 1);
    assert(NpmFzLook(&t, NpmTFz(1, 0x51, 10, 20, 30.45f, 5, 40, 3.5f), 101, ttl, 7, 9, &ms, &gm, &sm, &d) == NPM_HIT && ms == 97);
    assert(fabsf(gm - 0.45f) < 0.02f && sm < 0.01f);
    assert(NpmFzLook(&t, NpmTFz(1, 0x51, 10, 20, 30.6f, 5, 40.6f, 3.5f), 101, ttl, 7, 9, &ms, &gm, &sm, &d) == NPM_HIT);
    assert(NpmFzLook(&t, NpmTFz(1, 0x51, 10, 20, 31.2f, 5, 40, 3.5f), 101, ttl, 7, 9, &ms, &gm, &sm, &d) == NPM_FAR);
    assert(NpmFzLook(&t, NpmTFz(1, 0x51, 10, 20, 30.2f, 8, 40, 3.5f), 101, ttl, 7, 9, &ms, &gm, &sm, &d) == NPM_FAR);
    // a ring of another size (spread 1.2 m off), a start 0.6 m away (far) or 0.4 m (hit), another follower, another shape
    assert(NpmFzLook(&t, NpmTFz(1, 0x51, 10, 20, 30.2f, 5, 40, 2.3f), 101, ttl, 7, 9, &ms, &gm, &sm, &d) == NPM_FAR);
    assert(NpmFzLook(&t, NpmTFz(1, 0x51, 10.6f, 20, 30.2f, 5, 40, 3.5f), 101, ttl, 7, 9, &ms, &gm, &sm, &d) == NPM_FAR);
    assert(NpmFzLook(&t, NpmTFz(1, 0x51, 10.4f, 20, 30.2f, 5, 40, 3.5f), 101, ttl, 7, 9, &ms, &gm, &sm, &d) == NPM_HIT);
    assert(NpmFzLook(&t, NpmTFz(2, 0x51, 10, 20, 30.2f, 5, 40, 3.5f), 101, ttl, 7, 9, &ms, &gm, &sm, &d) == NPM_MISS);
    assert(NpmFzLook(&t, NpmTFz(1, 0x52, 10, 20, 30.2f, 5, 40, 3.5f), 101, ttl, 7, 9, &ms, &gm, &sm, &d) == NPM_MISS);

    // the nearest recorded goal answers; a second real failure 0.9 m on extends the reach only by its own record
    assert(NpmFzPut(&t, NpmTFz(1, 0x51, 10, 20, 30.9f, 5, 40, 3.5f), 110, 7, 9, false, 98) == 0 && NpmFzLive(&t) == 2);
    assert(NpmFzLook(&t, NpmTFz(1, 0x51, 10, 20, 31.2f, 5, 40, 3.5f), 111, ttl, 7, 9, &ms, &gm, &sm, &d) == NPM_HIT && ms == 98);
    assert(fabsf(gm - 0.3f) < 0.02f);
    // the same start and goal again refreshes in place
    assert(NpmFzPut(&t, NpmTFz(1, 0x51, 10, 20, 30.9f, 5, 40, 3.5f), 120, 7, 9, false, 99) == 0 && NpmFzLive(&t) == 2);

    // TTL from each record's own plan; a world version change drops entries on the way (static always, footprints if dyn)
    assert(NpmFzLook(&t, a, 100 + ttl - 1, ttl, 7, 9, &ms, &gm, &sm, &d) == NPM_HIT && ms == 97);
    assert(NpmFzLook(&t, a, 100 + ttl, ttl, 7, 9, &ms, &gm, &sm, &d) == NPM_HIT && ms == 99 && d == 1);  // first expired, second in
    assert(NpmFzLive(&t) == 1);
    assert(NpmFzLook(&t, a, 130, ttl, 8, 9, &ms, &gm, &sm, &d) == NPM_MISS && d == 1 && NpmFzLive(&t) == 0);  // a rebuild
    NpmFzPut(&t, a, 200, 7, 9, true, 97);
    assert(NpmFzLook(&t, a, 201, ttl, 7, 10, &ms, &gm, &sm, &d) == NPM_MISS && d == 1);  // footprints moved, request reads them
    NpmFzPut(&t, a, 200, 7, 9, false, 97);
    assert(NpmFzLook(&t, a, 201, ttl, 7, 10, &ms, &gm, &sm, &d) == NPM_HIT);              // footprints moved, not read
    assert(NpmFzLook(&t, a, 199, ttl, 7, 9, &ms, &gm, &sm, &d) == NPM_MISS && d == 1);   // clock went back

    // a plan of that follower that did not fail with no path drops every entry of that follower, no other
    NpmFzPut(&t, a, 300, 7, 9, false, 97);
    NpmFzPut(&t, NpmTFz(1, 0x52, 10, 20, 30, 5, 40, 3.5f), 300, 7, 9, false, 97);
    NpmFzPut(&t, NpmTFz(2, 0x51, 10, 20, 30, 5, 40, 3.5f), 300, 7, 9, false, 97);
    assert(NpmFzLive(&t) == 3 && NpmFzDropFoll(&t, 1) == 2 && NpmFzLive(&t) == 1);
    assert(NpmFzLook(&t, NpmTFz(1, 0x51, 10, 20, 30.2f, 5, 40, 3.5f), 301, ttl, 7, 9, &ms, &gm, &sm, &d) == NPM_MISS);
    assert(NpmFzLook(&t, NpmTFz(2, 0x51, 10, 20, 30.2f, 5, 40, 3.5f), 301, ttl, 7, 9, &ms, &gm, &sm, &d) == NPM_HIT);

    // bounded: a flood never holds more than the table (LRU), and clear empties it
    for (int i = 0; i < 3 * NPM_FZ_ENTRIES; i++) NpmFzPut(&t, NpmTFz(100 + (uint64_t)i, 0x51, 0, 0, 0, 0, 0, 1), 400, 7, 9, false, 1);
    assert(NpmFzLive(&t) == NPM_FZ_ENTRIES);
    assert(NpmFzClear(&t) == NPM_FZ_ENTRIES && NpmFzLive(&t) == 0);
    printf("selftest nopathmemo: fuzzy key, goal radius, start, level, spread, TTL, versions, follower drop, bound ok\n");
}
