// TestSavedFrames ([EaCrashFixes] SavedFrameGuard, saveframes.cpp, docs/293). Included by selftest.cpp. The classifier runs
// on a hand-made code array with its own length function (no game tables): the layout of the 2026-11-19 crash, a saved
// index one word off in either direction, an ambiguous spot and code that cannot be walked.
static int SfTestLen(const unsigned short* p) {
    switch (*p) {
        case 0x24: case 0x05: return 1;      // ldthis, pop
        case 0xEA: case 0x79: return 2;      // call, stloc
        case 0x15: case 0x66: return 3;      // ldc.i4, ldsfld
        case 0xEC: return 4;                 // callvirt
        default: return 0;                   // unknown
    }
}
static void TestSavedFrames() {
    // 0: ldthis | 1: call a | 3: ldthis | 4: ldc.i4 | 7: call DoLoop | 9: stloc | 11: ldthis | 12: call b | 14: call c | 16: pop
    static const unsigned short code[17] = {0x24, 0xEA, 0, 0x24, 0x15, 0xBFFF, 0xFFA9, 0xEA, 1, 0x79, 9, 0x24, 0xEA, 2, 0xEA, 3, 0x05};
    int fixed;
    bool after;
    assert(SfClassify(code, 17, 9, SfTestLen, &fixed, &after) == 0 && after && fixed == 9);    // the right index: untouched
    assert(SfClassify(code, 17, 3, SfTestLen, &fixed, &after) == 0 && after);
    assert(SfClassify(code, 17, 11, SfTestLen, &fixed, &after) == 0 && !after);                // a boundary after no call: left alone
    assert(SfClassify(code, 17, 17, SfTestLen, &fixed, &after) == 0);                          // the end of the code
    assert(SfClassify(code, 17, 10, SfTestLen, &fixed, &after) == 1 && fixed == 9);            // the crash: one word past the call return
    assert(SfClassify(code, 17, 8, SfTestLen, &fixed, &after) == 1 && fixed == 9);             // one word short of it
    assert(SfClassify(code, 17, 6, SfTestLen, &fixed, &after) == 2);                           // two call returns within 3 words (3 and 9)
    assert(SfClassify(code, 17, 13, SfTestLen, &fixed, &after) == 2);                          // 14 and 16 both follow a call
    assert(SfClassify(code, 17, 15, SfTestLen, &fixed, &after) == 2);
    assert(SfClassify(code, 17, 18, SfTestLen, &fixed, &after) == 2 && SfClassify(code, 17, -1, SfTestLen, &fixed, &after) == 2);
    static const unsigned short odd[6] = {0x24, 0xEA, 0, 0x1A9, 0, 0};                         // a word the walker does not know
    assert(SfClassify(odd, 6, 3, SfTestLen, &fixed, &after) == 0 && after);                    // reached before the unknown word
    assert(SfClassify(odd, 6, 5, SfTestLen, &fixed, &after) == -1);                            // behind it: not judged
    assert(SfClassify(odd, 6, 2, SfTestLen, &fixed, &after) == -1);                            // candidates past it are unknown too
    assert(SfCallOp(0xEA) && SfCallOp(0xED) && SfCallOp(0xEF) && SfCallOp(0xF4) && SfCallOp(0x1A6) && !SfCallOp(0x79) && !SfCallOp(0xE9) && !SfCallOp(0xF1));
    printf("saved frames ok\n");
}
