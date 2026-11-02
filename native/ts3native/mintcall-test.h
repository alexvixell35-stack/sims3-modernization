// TestMintCall: [MonoFast] CallPath (mintcall.cpp, docs/139 §8) pieces that run without the game's interpreter: the code
// hash, entry ownership (Satb's chain test) and the verify snapshot's range packing. The handlers themselves are proven
// in monohost against the real interpreter (MONOHOST_MINTCALL=verify, docs/139 §8.3).
static void TestMintCall() {
    assert(McFnv((const u8*)"", 0) == 0x811C9DC5u);
    assert(McFnv((const u8*)"a", 1) == 0xE40C292Cu);
    assert(McFnv((const u8*)"foobar", 6) == 0xBF9CF968u);
    for (int i = 0; i < 4; i++) {
        assert(McOwns(MC_OPS[i], (DWORD)(uintptr_t)kMcMine[i]));
        assert(!McOwns(MC_OPS[i], MC_EA[i]));
        assert(!McOwns(MC_OPS[(i + 1) % 4], (DWORD)(uintptr_t)kMcMine[i]));
    }
    // modeled code ranges are contiguous handler blocks of the interpreter and do not overlap
    for (int i = 0; i < 5; i++) {
        assert(kMcCode[i].at >= 0x00E53EB0 && kMcCode[i].at + kMcCode[i].n <= 0x00E59880);
        for (int j = 0; j < i; j++) assert(kMcCode[i].at >= kMcCode[j].at + kMcCode[j].n || kMcCode[j].at >= kMcCode[i].at + kMcCode[i].n);
    }
    // verify ranges pack back to back, refuse null pages and overflow the buffer cleanly
    static McV v;
    v.nr = 0;
    assert(McVAdd(v, 0x10000, 0x10) && McVAdd(v, 0x20000, 0x28) && v.rg[1].off == 0x10);
    assert(!McVAdd(v, 0x8000, 4) && v.nr == 2);
    assert(!McVAdd(v, 0x30000, MC_VBYTES) && v.nr == 2);
    for (int i = v.nr; i < MC_VR; i++) assert(McVAdd(v, 0x40000 + i * 16, 4));
    assert(!McVAdd(v, 0x50000, 4));
    printf("TestMintCall ok\n");
}
