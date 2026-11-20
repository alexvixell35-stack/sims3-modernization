// [OutfitFirst] (docs/684 §9): the table and the choice of outfitfirst-core.h on a fake ready ring. Included by
// selftest.cpp and outfitfirst-host-test.cpp.
#pragma once

static void OfTestRing(unsigned char* head, unsigned char (*nodes)[0x20], int n) {
    unsigned char* prev = head;
    for (int i = 0; i < n; i++) {
        *(unsigned char**)prev = nodes[i];
        *(unsigned char**)(nodes[i] + 4) = prev;
        prev = nodes[i];
    }
    *(unsigned char**)prev = head;
    *(unsigned char**)(head + 4) = prev;
}
static int OfTestLen(unsigned char* head) {
    int n = 0;
    for (unsigned char* p = *(unsigned char**)head; p != head && n < 100; p = *(unsigned char**)p) n++;
    return n;
}

static void TestOutfitFirstCore() {
    static OfTable t;
    static unsigned char head[8], nodes[5][0x20];
    memset(&t, 0, sizeof(t));
    memset(nodes, 0, sizeof(nodes));
    for (int i = 0; i < 5; i++) *(unsigned*)(nodes[i] + 0x18) = 0x1000u + (unsigned)i, *(int*)(nodes[i] + 8) = i % 3;
    OfTestRing(head, nodes, 5);
    int pos = 7;
    OfDone d;
    memset(&d, 0, sizeof(d));

    // nothing noted: nothing chosen; the game's pick of any job passes nobody
    assert(!OfChoose(&t, head, 100, &pos) && pos == -1);
    assert(!OfTaken(&t, nodes[0], 100, false, &d) && t.passes == 0);

    // two swaps noted (objects of nodes 3 and 1, in that order); a third whose LOD is cached never gets a job
    OfNote(&t, 0x1003, 1000);
    OfNote(&t, 0x1001, 1200);
    OfNote(&t, 0x9999, 1300);
    OfNote(&t, 0x1003, 1400);   // again while pending: the first times stay
    OfNote(&t, 0, 1400);
    assert(t.notes == 3 && t.renotes == 1 && OfFind(&t, 0x1003) >= 0 && OfFind(&t, 0) == -1);

    // the game takes node 0 while both sit ready: both were passed once
    assert(OfChoose(&t, head, 2000, &pos) == nodes[3] && pos == 3);   // the swap noted first, wherever it stands
    OfUnlink(nodes[0]);
    assert(!OfTaken(&t, nodes[0], 2000, false, &d) && t.passes == 2);
    assert(OfTestLen(head) == 4 && *(unsigned char**)head == nodes[1]);

    // first mode: node 3 is taken out of the ring, described, its entry closed
    unsigned char* const mine = OfChoose(&t, head, 2500, &pos);
    assert(mine == nodes[3] && pos == 2);
    OfUnlink(mine);
    assert(OfTaken(&t, mine, 2500, true, &d));
    assert(d.obj == 0x1003 && d.waitMs == 1500 && d.readyMs == 500 && d.passed == 1 && d.lod == 0 && t.first == 1);
    assert(OfTestLen(head) == 3 && *(unsigned char**)nodes[2] == nodes[4] && *(unsigned char**)(nodes[4] + 4) == nodes[2]);
    assert(OfFind(&t, 0x1003) == -1);

    // measure mode: the game's own pick reaches node 1 later; the entry says how long it sat ready and behind how many
    assert(OfChoose(&t, head, 2600, &pos) == nodes[1] && pos == 0);
    OfUnlink(nodes[2]);
    assert(!OfTaken(&t, nodes[2], 2600, false, &d) && t.passes == 3);
    OfUnlink(nodes[1]);
    assert(OfTaken(&t, nodes[1], 3000, false, &d));
    assert(d.obj == 0x1001 && d.waitMs == 1800 && d.readyMs == 1000 && d.passed == 2 && d.lod == 1 && t.ea == 1);
    assert(OfTestLen(head) == 1 && !OfChoose(&t, head, 3000, &pos));

    // a swap that never got a job times out; a tick counter that wrapped still gives the right age
    OfExpire(&t, 60000, 60000);
    assert(OfFind(&t, 0x9999) >= 0 && t.expired == 0);
    OfExpire(&t, 61300, 60000);
    assert(OfFind(&t, 0x9999) == -1 && t.expired == 1);
    OfNote(&t, 0x7777, 0xFFFFFF00u);
    OfExpire(&t, 0x00000100u, 60000);
    assert(OfFind(&t, 0x7777) >= 0);
    OfExpire(&t, 0x0000EB00u, 60000);
    assert(OfFind(&t, 0x7777) == -1 && t.expired == 2);

    // a full table pushes the oldest swap out; a job taken that nobody noted is no entry
    for (unsigned i = 0; i < kOfSlots; i++) OfNote(&t, 0x2000u + i, 70000u + i);
    OfNote(&t, 0x3000, 80000);
    assert(t.evicted == 1 && OfFind(&t, 0x2000) == -1 && OfFind(&t, 0x2001) >= 0 && OfFind(&t, 0x3000) >= 0);
    assert(!OfTaken(&t, nodes[4], 80000, true, &d) && t.first == 1);
    printf("outfit first core ok\n");
}
