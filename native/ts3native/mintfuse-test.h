// TestMintFuse: [MintFuse] Sub matcher (mintfuse.cpp, docs/139 §4) over a fake length table and descriptors. Behaviour on real
// script code is proven in monohost (MONOHOST_MINTFUSE=sub, docs/139 §4.4).
static void TestMintFuse() {
    enum : unsigned short { NOP = 0, A = 1, B = 2, C = 3, D = 4, SW = 5, BIG = 0x1A9 };
    static u8 len[MF_BASE_OPS], desc[MF_DESC_SIZE * (MF_DESCS + 1)];
    memset(len, 1, sizeof(len));
    len[B] = 2;   // one operand word
    len[SW] = 0;  // switch: variable length
    static const unsigned short kSeq[3][3] = {{A, B, 0}, {B, C, 0}, {A, B, C}};
    static const unsigned short kN[3] = {2, 2, 3};
    for (int j = 0; j < 3; j++) {
        u8* e = desc + MF_DESC_SIZE * (j + 1);
        *(unsigned short*)(e + 4) = kN[j];
        memcpy(e + 6, kSeq[j], 2 * kN[j]);
    }
    const unsigned short S1 = MF_SUPER + 1, S2 = MF_SUPER + 2, S3 = MF_SUPER + 3;
    unsigned short out;
    const auto run = [&](const unsigned short* w, int n, unsigned short* c) { out = 0; MfFuse(w, n, &out, c, len, desc); };
    MF_CTR.mfRuns = MF_CTR.mfWhole = MF_CTR.mfInner = 0;
    {   // a whole-run match goes to *out, the code stays EA's to write
        const unsigned short w[] = {A, B};
        unsigned short c[] = {A, B, 9};
        run(w, 2, c);
        assert(out == S1 && c[0] == A && MF_CTR.mfWhole == 1 && MF_CTR.mfInner == 0);
    }
    {   // the longest descriptor wins, inside a run, at the instruction's own word; operands stay
        const unsigned short w[] = {D, A, B, C, D};
        unsigned short c[] = {D, A, B, 7, C, D};
        run(w, 5, c);
        assert(out == 0 && c[0] == D && c[1] == S3 && c[2] == B && c[3] == 7 && c[4] == C && c[5] == D && MF_CTR.mfInner == 1);
    }
    {   // left to right: no overlap, B + C after A + B is gone
        const unsigned short w[] = {A, B, B, C, D};
        unsigned short c[] = {A, B, 1, B, 2, C, D};
        run(w, 5, c);
        assert(out == 0 && c[0] == S1 && c[3] == S2);
    }
    {   // a nop inside a descriptor is a barrier; between two is not
        const unsigned short w[] = {A, B, D};
        unsigned short c[] = {A, NOP, B, 1, D};
        run(w, 3, c);
        assert(out == 0 && c[0] == A && c[2] == B);
        const unsigned short w2[] = {A, B, D, D};
        unsigned short c2[] = {A, B, 1, NOP, D, D};
        run(w2, 4, c2);
        assert(out == 0 && c2[0] == S1);
    }
    {   // a switch or an opcode past the table leaves the whole run alone
        const unsigned short w[] = {A, B, SW};
        unsigned short c[] = {A, B, 1, SW, 0};
        run(w, 3, c);
        assert(out == 0 && c[0] == A);
        const unsigned short w2[] = {A, B, BIG};
        unsigned short c2[] = {A, B, 1, BIG};
        run(w2, 3, c2);
        assert(out == 0 && c2[0] == A);
    }
    {   // more nops than the walk's guard: no write through an unset pos[] (the run is left alone)
        const unsigned short w[] = {D, A, B};
        unsigned short c[80] = {D};
        c[70] = A, c[71] = B, c[72] = 1;
        const LONG runs = MF_CTR.mfRuns;
        run(w, 3, c);
        assert(out == 0 && c[70] == A && MF_CTR.mfRuns == runs);
    }
}
