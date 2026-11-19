// TestNoPathMemo ([RouteAsync] NoPathMemo, nopathmemo-core.h, docs/643). Included by selftest.cpp; uses only the pure table
// (assert, nopathmemo-core.h), so it also builds as a plain C++ program on any host.
static void TestNoPathMemo() {
    static NpmTable t;  // ~10 KB: static, not on the stack
    NpmTabClear(&t);
    const uint64_t ttl = NpmTtlTicks(120);
    int32_t ms = -1;

    // TTL in sim ticks: 120 sim-minutes = 4500 ticks
    assert(ttl == 4500 && NpmTtlTicks(1) == 37 && NpmTtlTicks(0) == 0 && NpmTtlTicks(-5) == 0);

    // key: a miss before any record; a record is answered for its key only
    assert(NpmTabLook(&t, 0x1111, 100, ttl, 7, 9, &ms) == NPM_MISS);
    assert(NpmTabPut(&t, 0x1111, 100, 7, 9, false, 420) == 0);
    assert(NpmTabLook(&t, 0x1111, 101, ttl, 7, 9, &ms) == NPM_HIT && ms == 420);
    assert(NpmTabLook(&t, 0x1112, 101, ttl, 7, 9, &ms) == NPM_MISS);
    assert(NpmTabLive(&t) == 1);

    // TTL: an answer does not extend the entry; at tick + ttl it expires and is dropped
    assert(NpmTabLook(&t, 0x1111, 100 + ttl - 1, ttl, 7, 9, &ms) == NPM_HIT);
    assert(NpmTabLook(&t, 0x1111, 100 + ttl, ttl, 7, 9, &ms) == NPM_EXPIRED);
    assert(NpmTabLook(&t, 0x1111, 100 + ttl, ttl, 7, 9, &ms) == NPM_MISS && NpmTabLive(&t) == 0);
    // the clock going back (another save loaded) drops it too
    NpmTabPut(&t, 0x2222, 5000, 7, 9, false, 300);
    assert(NpmTabLook(&t, 0x2222, 4999, ttl, 7, 9, &ms) == NPM_EXPIRED && NpmTabLive(&t) == 0);

    // invalidation: the static world version (lot routing, water grid) always; the footprint version only for a dynamic request
    NpmTabPut(&t, 0x3333, 10, 7, 9, false, 300);
    assert(NpmTabLook(&t, 0x3333, 11, ttl, 7, 10, &ms) == NPM_HIT);       // footprints moved: not read by this request
    assert(NpmTabLook(&t, 0x3333, 12, ttl, 8, 10, &ms) == NPM_INVALID);   // a rebuild
    assert(NpmTabLook(&t, 0x3333, 13, ttl, 8, 10, &ms) == NPM_MISS);
    NpmTabPut(&t, 0x4444, 10, 7, 9, true, 300);
    assert(NpmTabLook(&t, 0x4444, 11, ttl, 7, 9, &ms) == NPM_HIT);
    assert(NpmTabLook(&t, 0x4444, 12, ttl, 7, 10, &ms) == NPM_INVALID);
    // a success (or another failure) of the same request drops its memo; re-recording replaces in place
    NpmTabPut(&t, 0x5555, 10, 7, 9, false, 300);
    assert(NpmTabPut(&t, 0x5555, 20, 7, 9, false, 310) == 0 && NpmTabLive(&t) == 1);
    assert(NpmTabLook(&t, 0x5555, 21, ttl, 7, 9, &ms) == NPM_HIT && ms == 310);
    assert(NpmTabDrop(&t, 0x5555) == 1 && NpmTabDrop(&t, 0x5555) == 0);
    assert(NpmTabLook(&t, 0x5555, 22, ttl, 7, 9, &ms) == NPM_MISS);

    // eviction: keys of one set (same NpmSet); the ninth evicts the least recently used, which an answer refreshes
    NpmTabClear(&t);
    uint64_t keys[NPM_WAYS + 1];
    int n = 0;
    for (uint64_t k = 1; n < NPM_WAYS + 1; k++)
        if (NpmSet(&t, k) == NpmSet(&t, 1)) keys[n++] = k;
    for (int i = 0; i < NPM_WAYS; i++) assert(NpmTabPut(&t, keys[i], 100 + i, 7, 9, false, i) == 0);
    assert(NpmTabLook(&t, keys[0], 200, ttl, 7, 9, &ms) == NPM_HIT && ms == 0);  // keys[0] is now the most recent
    assert(NpmTabPut(&t, keys[NPM_WAYS], 201, 7, 9, false, 99) == 1);           // evicts keys[1], the oldest use
    assert(NpmTabLook(&t, keys[1], 202, ttl, 7, 9, &ms) == NPM_MISS);
    assert(NpmTabLook(&t, keys[0], 202, ttl, 7, 9, &ms) == NPM_HIT);
    assert(NpmTabLook(&t, keys[NPM_WAYS], 202, ttl, 7, 9, &ms) == NPM_HIT && ms == 99);
    assert(NpmTabLive(&t) == NPM_WAYS);
    // memory is bounded: a flood of distinct keys never holds more than the table
    for (uint64_t k = 1000; k < 1000 + 4 * NPM_ENTRIES; k++) NpmTabPut(&t, k * 0x9E3779B97F4A7C15ull, 300, 7, 9, false, 1);
    assert(NpmTabLive(&t) == NPM_ENTRIES);

    // a new world clears everything
    assert(NpmTabClear(&t) == NPM_ENTRIES && NpmTabLive(&t) == 0);
    printf("selftest nopathmemo: key, TTL, invalidation, eviction ok\n");
}
