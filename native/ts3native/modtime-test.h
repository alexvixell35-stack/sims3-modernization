// TestModTime ([Diag] ModTime, modtime.cpp, docs/298). Included by selftest.cpp. Host checks of the parts with logic: MVID
// from a metadata root, owner kinds and the frame pick, ordering, the hitch list, the notice rule and text, the compat row
// match, the owner resolver (MdtOwnerOf) on fake Mono structs, the op 61 notice bits.
static void TestModTime() {
    // a fake S3SA blob: junk, then a metadata root (version "v2.0.50727" padded to 12) with #~, #GUID streams
    static u8 b[512] = {};
    memset(b, 0xCC, 64);
    u8* r = b + 64;
    memcpy(r, "BSJB", 4);
    *(WORD*)(r + 4) = 1, *(WORD*)(r + 6) = 1;
    *(DWORD*)(r + 12) = 12;
    memcpy(r + 16, "v2.0.50727\0\0", 12);
    u8* p = r + 28;
    *(WORD*)(p + 2) = 2;  // streams
    p += 4;
    *(DWORD*)p = 0x100, *(DWORD*)(p + 4) = 8, memcpy(p + 8, "#~\0\0", 4), p += 12;
    *(DWORD*)p = 0x120, *(DWORD*)(p + 4) = 16, memcpy(p + 8, "#GUID\0\0\0", 8), p += 16;
    for (int i = 0; i < 16; i++) r[0x120 + i] = (u8)(0xA0 + i);
    u8 mv[16] = {};
    assert(MdtMvidOf(b, sizeof(b), mv) && mv[0] == 0xA0 && mv[15] == 0xAF);
    assert(!MdtMvidOf(b, 64, mv));                       // no root in range
    u8 none[64] = {};
    assert(!MdtMvidOf(none, sizeof(none), mv));
    static u8 cut[64 + 0x110];                           // root found, #GUID past the end: no read past n
    memcpy(cut, b, sizeof(cut));
    assert(!MdtMvidOf(cut, sizeof(cut), mv));

    // kinds and the pick: innermost mod, else innermost ours, else innermost (game); none = -1
    assert(MdtKindOf("Sims3GameplaySystems") == MDT_GAME && MdtKindOf("mscorlib") == MDT_GAME && MdtKindOf("ScriptCore") == MDT_GAME);
    assert(MdtKindOf("Ts3Fixes") == MDT_OURS && MdtKindOf("NRaasStoryProgression") == MDT_MOD && MdtKindOf("SystemTweaks") == MDT_MOD);
    const int k1[4] = {MDT_GAME, MDT_GAME, MDT_MOD, MDT_GAME};
    assert(MdtPick(k1, 4) == 2);
    const int k2[3] = {MDT_GAME, MDT_OURS, MDT_GAME};
    assert(MdtPick(k2, 3) == 1);
    const int k3[3] = {MDT_GAME, MDT_MOD, MDT_OURS};  // a mod outranks ours wherever it sits
    assert(MdtPick(k3, 3) == 1);
    const int k4[2] = {MDT_GAME, MDT_GAME};
    assert(MdtPick(k4, 2) == 0 && MdtPick(k4, 0) == -1);
    // docs/298 §5.1 pass-through hook: {GAME, OURS, GAME} -> game via hook; {OURS, GAME} -> ours; a mod pick is never via hook
    assert(MdtViaHook(k2, 3, MdtPick(k2, 3)));
    const int k5[2] = {MDT_OURS, MDT_GAME};
    assert(!MdtViaHook(k5, 2, MdtPick(k5, 2)));
    assert(!MdtViaHook(k1, 4, MdtPick(k1, 4)) && !MdtViaHook(k4, 2, MdtPick(k4, 2)) && !MdtViaHook(k4, 0, -1));
    char lab[64];
    MdtHookLabel("Ts3Fixes.FestivalHiddenHook.UpdateCommodityInteractionMap", lab, sizeof(lab));
    assert(!strcmp(lab, "game via ts3fixes hook (FestivalHiddenHook)"));
    MdtHookLabel("EcHook.ProcessEvent", lab, sizeof(lab));
    assert(!strcmp(lab, "game via ts3fixes hook (EcHook)"));

    // ordering: largest first, zeros never listed
    const double w[6] = {1, 0, 5, 3, 5, 0.5};
    int idx[4], got;
    MdtOrder(w, 6, idx, 4, &got);
    assert(got == 4 && w[idx[0]] == 5 && w[idx[1]] == 5 && idx[0] != idx[1] && idx[2] == 3 && idx[3] == 0);
    const double z[2] = {0, 0};
    MdtOrder(z, 2, idx, 4, &got);
    assert(got == 0);

    // notice rule: sim hours in a row at >= 25%
    {
        int run = 0;
        const double shares[6] = {0.3, 0.25, 0.1, 0.5, 0.26, 0.27};
        int runs[6];
        for (int i = 0; i < 6; i++) runs[i] = run = MdtHotStep(run, shares[i]);
        assert(runs[0] == 1 && runs[1] == 2 && runs[2] == 0 && runs[3] == 1 && runs[5] == 3);
    }
    // package base name and the COMPATIBILITY.md row match
    {
        char base[64];
        MdtPkgBase("core mod: sub\\simler90GameplayCoreMod-UPDATE242-Steam.package", base, sizeof(base));
        assert(!strcmp(base, "simler90GameplayCoreMod-UPDATE242-Steam"));
        assert(MdtCompatRow("NRaas\\nraas_errortrap.package") && MdtCompatRow("(Die Sims 3)\\Overrides.package"));
        assert(!MdtCompatRow("NRaas_StoryProgression.package") && !MdtCompatRow("(not found in Mods)") && !MdtCompatRow("tiny-ui-fix2.package"));
    }
    // the notice text: busy line, >= 90% = cannot keep up, own / with game shares, never past cap
    {
        char t[480];
        const char* names[2] = {"NRaasStoryProgression", "SomeMod"};
        const double self[2] = {12.4, 5}, incl[2] = {31, 9.2};
        MdtNoteText(96, names, self, incl, 2, t, sizeof(t));
        assert(strstr(t, "busy 96% of the time (the simulation cannot keep up)") && strstr(t, "NRaasStoryProgression 12% / 31%, SomeMod 5% / 9%."));
        assert(strstr(t, "modtime-summary.txt"));
        MdtNoteText(40, names, self, incl, 0, t, sizeof(t));
        assert(!strstr(t, "cannot keep up") && strstr(t, "No script mod showed up"));
        char small[40];
        MdtNoteText(96, names, self, incl, 2, small, sizeof(small));
        assert(strlen(small) == sizeof(small) - 1);
    }
    // the owner a hitch credits: most frequent inclusive owner, first on ties
    {
        const int v[6] = {5, 7, 7, 5, 7, 2};
        int c = 0;
        assert(v[MdtMode(v, 6, &c)] == 7 && c == 3);
        assert(MdtMode(v, 0, &c) == -1 && c == 0);
    }
    // owner resolver for other lanes: fake MonoMethod -> MonoClass -> MonoImage in host memory, cached per method
    {
        MdtTablesInit();
        static char asmMod[] = "NRaasOverwatch", asmOurs[] = "Ts3Fixes", cls[] = "Overwatch", ns[] = "NRaas", mname[] = "OnTimer";
        static DWORD imgMod[32], imgOurs[32], klassMod[32], klassOurs[32], methA[16], methB[16], methC[16], methBad[16];
        imgMod[0x18 / 4] = (DWORD)(uintptr_t)asmMod, imgOurs[0x18 / 4] = (DWORD)(uintptr_t)asmOurs;
        klassMod[0] = (DWORD)(uintptr_t)imgMod, klassMod[0x34 / 4] = (DWORD)(uintptr_t)cls, klassMod[0x38 / 4] = (DWORD)(uintptr_t)ns;
        klassOurs[0] = (DWORD)(uintptr_t)imgOurs;
        methA[2] = methB[2] = (DWORD)(uintptr_t)klassMod, methA[0x18 / 4] = (DWORD)(uintptr_t)mname;
        methC[2] = (DWORD)(uintptr_t)klassOurs;
        MdtOwner a = {}, a2 = {}, b2 = {}, c2 = {}, bad = {};
        const int methN0 = g_mdtMethN, modN0 = g_mdtModN;
        assert(MdtOwnerOf((DWORD)(uintptr_t)methA, &a) && !strcmp(a.name, "NRaasOverwatch") && a.kind == MDT_MOD && a.idx >= 2 && !a.mvidOk);
        assert(MdtOwnerOf((DWORD)(uintptr_t)methA, &a2) && a2.idx == a.idx && g_mdtMethN == methN0 + 1);  // cached: no new row
        assert(MdtOwnerOf((DWORD)(uintptr_t)methB, &b2) && b2.idx == a.idx && g_mdtModN == modN0 + 1);     // same image, one owner row
        assert(MdtOwnerOf((DWORD)(uintptr_t)methC, &c2) && c2.kind == MDT_OURS && c2.idx != a.idx);
        assert(MdtOwnerOf((DWORD)(uintptr_t)methBad, &bad) && bad.idx == 1);  // no class: the unreadable row
        assert(!MdtOwnerOf(0, &bad));
        assert(!strcmp(g_mdtMeth[MdtMethOf((DWORD)(uintptr_t)methA)].name, "NRaas.Overwatch.OnTimer"));
    }
    // op 61 hi 4: the notice text bit by bit (ts3fixes ModTime.cs reads it this way)
    {
        g_mdtOn = 1;
        assert(MdtOp(0, 3) == 0 && MdtOp(0, 4) == 0);  // not ready
        strcpy_s(g_mdtNote, "Hi!");
        g_mdtNoteReady = 1;
        char back[8] = {};
        for (int i = 0; i < 7; i++) {
            int c = 0;
            for (int bit = 0; bit < 8; bit++) c |= MdtOp((DWORD)(i * 8 + bit), 4) << bit;
            if (!c) break;
            back[i] = (char)c;
        }
        assert(MdtOp(0, 3) == 1 && !strcmp(back, "Hi!") && MdtOp(0xFFFFFF, 4) == 0);
        g_mdtOn = 0, g_mdtNoteReady = 0, g_mdtNote[0] = 0;
    }

    // hitch list keeps the 20 longest, longest first
    g_mdtHitN = 0;
    for (LONG i = 1; i <= 50; i++) { MdtHitch t = {}; t.ms = (i * 37) % 51; MdtHitchAdd(t); }
    assert(g_mdtHitN == 20 && g_mdtHit[0].ms == 50 && g_mdtHit[19].ms == 31);
    g_mdtHitN = 0;

    // S3SA decrypt (s3pi scheme): encrypt a fake image with the inverse chain, one flagged (zero) block, then decrypt
    {
        const DWORD count = 4, tlen = count * 8;
        static u8 clear[4 * 512], body[1 + 4 + 64 + 2 + 4 * 8 + 4 * 512];
        for (DWORD i = 0; i < sizeof(clear); i++) clear[i] = (u8)(i * 7 + 3);
        memset(clear + 2 * 512, 0, 512);  // block 2 flagged: stays zero, takes no input
        DWORD bp = 0;
        body[bp++] = 1;  // version 1: no string
        bp += 4 + 64;
        *(WORD*)(body + bp) = (WORD)count, bp += 2;
        u8* table = body + bp;
        for (DWORD i = 0; i < tlen; i++) table[i] = (u8)(i * 29 + 10);
        table[0] &= ~1, table[8] &= ~1, table[16] |= 1, table[24] &= ~1;
        bp += tlen;
        unsigned long long sum = 0;
        for (DWORD i = 0; i < tlen; i += 8) sum += *(const unsigned long long*)(table + i);
        DWORD seed = (DWORD)((unsigned long long)(tlen - 1) & sum), bw = bp;
        for (DWORD blk = 0; blk < count; blk++) {
            if (table[blk * 8] & 1) continue;
            for (DWORD j = 0; j < 512; j++) { const u8 c = clear[blk * 512 + j] ^ table[seed]; body[bw++] = c; seed = (seed + c) % tlen; }
        }
        DWORD on = 0;
        u8* out = MdtS3saDecrypt(body, bw, &on);
        assert(out && on == count * 512 && !memcmp(out, clear, on));
        free(out);
        assert(!MdtS3saDecrypt(body, 40, &on));  // truncated header
        assert(!MdtS3saDecrypt(body, bw - 100, &on));  // truncated data
    }
    // real packages (not in the repo): TS3_S3SA_FIXTURES = package paths separated by ';', each must give >= 1 MVID
    // (2026-11-20: NRaas_ErrorTrap / NRaas_Overwatch from the test Mods folder; their S3SA bodies are encrypted)
    {
        wchar_t list[2048];
        if (GetEnvironmentVariableW(L"TS3_S3SA_FIXTURES", list, 2048) && (g_mdtPkg = (MdtPkg*)calloc(4096, sizeof(MdtPkg))) != NULL) {
            int files = 0;
            for (wchar_t* ctx = NULL, *t = wcstok_s(list, L";", &ctx); t; t = wcstok_s(NULL, L";", &ctx)) {
                const int before = g_mdtPkgN;
                MdtScanFile(t, t);
                printf("modtime fixture: %ls: %d MVID(s)\n", t, g_mdtPkgN - before);
                assert(g_mdtPkgN > before);
                files++;
            }
            printf("modtime: %d real S3SA fixture package(s) ok\n", files);
            g_mdtPkgN = g_mdtPkgFiles = g_mdtPkgS3sa = 0;
            free(g_mdtPkg), g_mdtPkg = NULL;
        }
    }
}
