// Usage: node tools/selftest/autoffload/run.ts  (compiles this with mods/ts3fixes/src/AutOffloadCore.cs)
// Offline self-check of AutOffloadCore.SeqClass (docs/208 broadcaster Mode R classes), InsertTop (the env due pick,
// docs/208 §8.1), ListMemo (docs/274 R1 envlistmemo validity) and Scan / SetDiff (docs/274 R2 bcposdry). Exit 1 on failure.
using System;
using Ts3Fixes;

static class AutOffloadTest
{
    static int sFail;
    static void Check(string what, int got, int want) { if (got != want) { sFail++; Console.WriteLine("FAIL " + what + ": " + got + " want " + want); } }

    // docs/126 §8 IC compare with per-group aborts: groups 0 = slots 0-1, 1 = slot 2, 2 = slots 3-4
    static void IcDiffChecks()
    {
        byte[] g = { 0, 0, 1, 2, 2 };
        long[] w = { 1, 2, 3, 4, 5 }, r = { 1, 2, 3, 4, 5 };
        Check("ic all equal", AutOffloadCore.IcFirstDiff(w, r, g, 0), 5);
        r[2] = 9;
        Check("ic diff in slot 2", AutOffloadCore.IcFirstDiff(w, r, g, 0), 2);
        Check("ic diff in an aborted group is skipped", AutOffloadCore.IcFirstDiff(w, r, g, 1 << 1), 5);
        r[4] = 9;
        Check("ic diff after an aborted group", AutOffloadCore.IcFirstDiff(w, r, g, 1 << 1), 4);
        Check("ic other group aborted", AutOffloadCore.IcFirstDiff(w, r, g, 1 << 0), 2);
    }

    // docs/208 §9.2 closest-lot rule on a lot from (0,0) to (10,20)
    static void LotChecks()
    {
        Check("on the lot", AutOffloadCore.LotCell(5, 5, 0, 0, 10, 20, 0), 4);
        Check("on the edge counts as on", AutOffloadCore.LotCell(10, 20, 0, 0, 10, 20, 0), 4);
        Check("top left", AutOffloadCore.LotCell(-1, 21, 0, 0, 10, 20, 0), 0);
        Check("top centre", AutOffloadCore.LotCell(5, 21, 0, 0, 10, 20, 0), 1);
        Check("top right", AutOffloadCore.LotCell(11, 21, 0, 0, 10, 20, 0), 2);
        Check("left", AutOffloadCore.LotCell(-1, 5, 0, 0, 10, 20, 0), 3);
        Check("right", AutOffloadCore.LotCell(11, 5, 0, 0, 10, 20, 0), 5);
        Check("bottom left", AutOffloadCore.LotCell(-1, -1, 0, 0, 10, 20, 0), 6);
        Check("bottom centre", AutOffloadCore.LotCell(5, -1, 0, 0, 10, 20, 0), 7);
        Check("bottom right", AutOffloadCore.LotCell(11, -1, 0, 0, 10, 20, 0), 8);
        Check("houseboat pad", AutOffloadCore.LotCell(-5, 25, 0, 0, 10, 20, 10), 4);
        int[] want = { 100, 16, 200, 1, 0, 9, 300, 4, 400 };  // cells 0-8 with dxo 1, dzo 2, dxe 3, dze 4
        for (int i = 0; i < 9; i++) Check("dist cell " + i, (int)AutOffloadCore.LotCellDist(i, 1, 2, 3, 4, 100, 200, 300, 400), want[i]);
        float best = float.MaxValue;
        Check("first lot taken", AutOffloadCore.LotPickStep(5, 9f, false, ref best), 1);
        Check("farther kept out", AutOffloadCore.LotPickStep(3, 16f, false, ref best), 0);
        Check("a tie keeps the earlier", AutOffloadCore.LotPickStep(3, 9f, false, ref best), 0);
        Check("nearer taken", AutOffloadCore.LotPickStep(3, 4f, false, ref best), 1);
        Check("on a lot: pick and focus", AutOffloadCore.LotPickStep(4, 0f, false, ref best), 2);
        Check("second lot under the target keeps the first", AutOffloadCore.LotPickStep(4, 0f, false, ref best), 0);
        Check("a houseboat lot under the target wins", AutOffloadCore.LotPickStep(4, 0f, true, ref best), 2);
        Check("nothing beats distance 0", AutOffloadCore.LotPickStep(3, 0f, false, ref best), 0);
        best = 9f;
        Check("no corners counts as distance 0", AutOffloadCore.LotPickStep(-1, 0f, false, ref best), 1);
    }

    // docs/208 §10 autoffunseen: only an unseen non-household Sim (UfWhy 0) reaches EnvApply / BcRebuild
    static void ScopeChecks()
    {
        Check("unseen applies", AutOffloadCore.Scope(UnseenFallbackCore.kSeen), AutOffloadCore.kScopeApply);
        Check("household (and selected: IsSelectable) never", AutOffloadCore.Scope(UnseenFallbackCore.kHousehold), AutOffloadCore.kScopeHousehold);
        Check("goal household never", AutOffloadCore.Scope(UnseenFallbackCore.kGoalHousehold), AutOffloadCore.kScopeHousehold);
        Check("UfWhy threw = unknown", AutOffloadCore.Scope(-1), AutOffloadCore.kScopeUnknown);
        Check("not live = unknown", AutOffloadCore.Scope(UnseenFallbackCore.kNotLive), AutOffloadCore.kScopeUnknown);
        Check("gone = unknown", AutOffloadCore.Scope(UnseenFallbackCore.kGone), AutOffloadCore.kScopeUnknown);
        int[] seen = { UnseenFallbackCore.kActiveLot, UnseenFallbackCore.kOnScreen, UnseenFallbackCore.kNearViewers, UnseenFallbackCore.kHeld, UnseenFallbackCore.kSocialSeen };
        foreach (int w in seen) Check("seen code " + w, AutOffloadCore.Scope(w), AutOffloadCore.kScopeSeen);
        foreach (int w in seen) Check("seen other Sim in the scan is fine " + w, AutOffloadCore.OtherScope(w), AutOffloadCore.kScopeApply);
        Check("household other Sim in the scan blocks", AutOffloadCore.OtherScope(UnseenFallbackCore.kHousehold), AutOffloadCore.kScopeHousehold);
        Check("unknown other Sim in the scan blocks", AutOffloadCore.OtherScope(-1), AutOffloadCore.kScopeUnknown);
        for (int w = -1; w <= 12; w++)  // nothing but UfWhy 0 applies
            if (w != UnseenFallbackCore.kSeen) Check("only 0 applies, code " + w, AutOffloadCore.Scope(w) == AutOffloadCore.kScopeApply ? 1 : 0, 0);
    }

    // docs/274 R1 envlistmemo: validity rule (lot / room generations, the next-tick second bump)
    static void ListMemoChecks()
    {
        ListMemo m = new ListMemo();
        object lot = new object(), lot2 = new object(), a = new object(), b = new object();
        object[] ab = { a, b }, ba = { b, a };
        Check("memo first", m.Check(lot, 3, 0, ab, 1), ListMemo.kFirst);
        Check("memo repeat hit", m.Check(lot, 3, 0, new System.Collections.Generic.List<object>(ab), 1), ListMemo.kHit);
        Check("memo order change unseen = wrong hit", m.Check(lot, 3, 0, ba, 1), ListMemo.kHitWrong);
        Check("wrong hit keeps the memo's list", m.WrongOld != null && m.WrongOld[0] == a ? 1 : 0, 1);
        Check("memo stores the game's list", m.Check(lot, 3, 0, ba, 1), ListMemo.kHit);
        ListMemo md = new ListMemo();  // docs/274 §8.6 diag: events and age since the list was last seen equal
        md.Check(lot, 3, 0, ab, 10); md.Check(lot, 3, 0, ab, 11);
        md.Bump(lot, 4, 11); md.Bump(lot, 5, 12);
        Check("memo wrong hit after other-room events", md.Check(lot, 3, 0, ba, 14), ListMemo.kHitWrong);
        Check("memo wrong hit: lot events counted once each", md.WrongEv, 2);
        Check("memo wrong hit: age from the last equal check", (int)md.WrongAge, 3);
        Check("memo lot-wide first", m.Check(lot, -1, 1, ab, 1), ListMemo.kFirst);
        m.Bump(lot, 4, 1);
        Check("memo other room bump keeps the room", m.Check(lot, 3, 0, ba, 1), ListMemo.kHit);
        m.Bump(lot, 3, 1);
        Check("memo room bump = miss, same list", m.Check(lot, 3, 0, ba, 1), ListMemo.kMissSame);
        Check("memo room bump keeps lot-wide lists", m.Check(lot, -1, 1, ab, 1), ListMemo.kHit);
        Check("memo next tick: second bump", m.Check(lot, 3, 0, ab, 2), ListMemo.kMissDiff);
        Check("memo then valid again", m.Check(lot, 3, 0, ab, 2), ListMemo.kHit);
        Check("memo room 3's second bump did not touch lot-wide", m.Check(lot, -1, 1, ab, 2), ListMemo.kHit);
        m.Bump(lot2, -1, 2);
        Check("memo other lot bump", m.Check(lot, -1, 1, ab, 2), ListMemo.kHit);
        m.Bump(lot, -1, 2);
        Check("memo lot bump = miss on lot-wide", m.Check(lot, -1, 1, ab, 2), ListMemo.kMissSame);
        Check("memo lot bump = miss on rooms", m.Check(lot, 3, 0, ab, 2), ListMemo.kMissSame);
        Check("memo same tick: no second bump yet", m.Check(lot, 3, 0, ab, 2), ListMemo.kHit);
        Check("memo later tick: second bump", m.Check(lot, 3, 0, ab, 5), ListMemo.kMissSame);
        object lot3 = new object();
        m.Bump(lot3, -1, 5);  // before the lot has entries: the stored entry of the same tick is still bumped next tick
        Check("memo unseen lot first", m.Check(lot3, -1, 3, ab, 5), ListMemo.kFirst);
        Check("memo unseen lot same tick", m.Check(lot3, -1, 3, ab, 5), ListMemo.kHit);
        Check("memo unseen lot next tick", m.Check(lot3, -1, 3, ab, 6), ListMemo.kMissSame);
        m.Clear();
        Check("memo clear", m.Check(lot, 3, 0, ab, 7), ListMemo.kFirst);
        Check("memo empty lists equal", m.Check(lot, 9, 2, new object[0], 7) + m.Check(lot, 9, 2, new object[0], 7), ListMemo.kFirst + ListMemo.kHit);
        // docs/274 R1 apply: Get returns the valid memo list only
        ListMemo mg = new ListMemo();
        Check("get: nothing stored", mg.Get(lot, 3, 0, 1) == null ? 1 : 0, 1);
        mg.Check(lot, 3, 0, ab, 1);
        object[] got = mg.Get(lot, 3, 0, 1);
        Check("get: stored list, game order", got != null && got.Length == 2 && got[0] == a ? 1 : 0, 1);
        Check("get: other kind not stored", mg.Get(lot, 3, 2, 1) == null ? 1 : 0, 1);
        mg.Bump(lot, 4, 1);
        Check("get: other room bump keeps it", mg.Get(lot, 3, 0, 1) != null ? 1 : 0, 1);
        mg.Bump(lot, 3, 1);
        Check("get: room bump = null", mg.Get(lot, 3, 0, 1) == null ? 1 : 0, 1);
        mg.Check(lot, 3, 0, ab, 1);
        Check("get: valid after the live list is stored", mg.Get(lot, 3, 0, 1) != null ? 1 : 0, 1);
        Check("get: next-tick repeat bump = null", mg.Get(lot, 3, 0, 2) == null ? 1 : 0, 1);
        mg.Check(lot, -1, 1, ab, 2); mg.Bump(lot, -1, 2);
        Check("get: lot bump = null on lot-wide", mg.Get(lot, -1, 1, 2) == null ? 1 : 0, 1);
        Check("sameset: reorder", ListMemo.SameSet(new object[] { a, b }, ba) ? 1 : 0, 1);
        Check("sameset: other object", ListMemo.SameSet(new object[] { a, a }, ba) ? 1 : 0, 0);
        Check("sameset: other count", ListMemo.SameSet(new object[] { a }, ba) ? 1 : 0, 0);
    }

    // docs/274 R2 bcposdry: the table range scan and the set compare
    static void BcPosChecks()
    {
        float[] x = { 0f, 3f, 5f, 4f, -3f }, z = { 0f, 4f, 0f, 3.01f, -4f };
        int[] at = new int[8];
        int n = AutOffloadCore.Scan(x, z, 5, 0f, 0f, 5f, at);
        Check("scan count (edge counts)", n, 4);
        Check("scan order", at[0] == 0 && at[1] == 1 && at[2] == 2 && at[3] == 4 ? 1 : 0, 1);
        Check("scan cap", AutOffloadCore.Scan(x, z, 5, 0f, 0f, 50f, new int[2]), 2);
        object a = new object(), b = new object(), c = new object();
        int miss, extra;
        Check("set same order", AutOffloadCore.SetDiff(new object[] { a, b }, 2, new object[] { a, b }, 2, out miss, out extra), 0);
        Check("set other order", AutOffloadCore.SetDiff(new object[] { b, a }, 2, new object[] { a, b }, 2, out miss, out extra), 1);
        Check("set missing", AutOffloadCore.SetDiff(new object[] { a, null }, 1, new object[] { a, b }, 2, out miss, out extra) * 10 + miss * 3 + extra, 23);
        Check("set extra", AutOffloadCore.SetDiff(new object[] { a, c }, 2, new object[] { a }, 1, out miss, out extra) * 10 + miss * 3 + extra, 21);
        Check("set both empty", AutOffloadCore.SetDiff(new object[0], 0, new object[0], 0, out miss, out extra), 0);
    }

    static int Main()
    {
        LotChecks();
        IcDiffChecks();
        ListMemoChecks();
        BcPosChecks();
        ScopeChecks();
        object a = new object(), b = new object(), c = new object();
        object[] w = { a, b, c }, r = { a, b, c }, swapped = { b, a, c }, other = { a, b, a };
        bool[] f = { true, false, true }, fs = { false, true, true }, g = { true, true, true };
        Check("equal", AutOffloadCore.SeqClass(w, f, 3, r, f, 3), 0);
        Check("order only", AutOffloadCore.SeqClass(w, f, 3, swapped, fs, 3), 1);
        Check("order with a flag moved", AutOffloadCore.SeqClass(w, f, 3, swapped, f, 3), 2);
        Check("flag differs", AutOffloadCore.SeqClass(w, f, 3, r, g, 3), 2);
        Check("count differs", AutOffloadCore.SeqClass(w, f, 3, r, f, 2), 2);
        Check("other member", AutOffloadCore.SeqClass(w, f, 3, other, f, 3), 2);
        Check("both empty", AutOffloadCore.SeqClass(w, f, 0, r, f, 0), 0);
        // due pick (docs/208 §8.1): the 3 largest of 5 keys, descending; a key equal to the smallest kept does not replace it
        float[] k = new float[3];
        object[] it = new object[3];
        object d = new object(), e = new object();
        int n = 0;
        n = AutOffloadCore.InsertTop(k, it, n, 3, 0.6f, a);
        n = AutOffloadCore.InsertTop(k, it, n, 3, 0.9f, b);
        n = AutOffloadCore.InsertTop(k, it, n, 3, 0.7f, c);
        Check("pick fills", n, 3);
        n = AutOffloadCore.InsertTop(k, it, n, 3, 0.6f, d);
        n = AutOffloadCore.InsertTop(k, it, n, 3, 1.2f, e);
        Check("pick count", n, 3);
        Check("pick order", it[0] == e && it[1] == b && it[2] == c && k[0] == 1.2f && k[2] == 0.7f ? 1 : 0, 1);
        Console.WriteLine(sFail == 0 ? "autoffload selftest: ok (39 + 31 scope + 21 memo + 8 bcpos cases)" : "autoffload selftest: " + sFail + " failed");
        return sFail == 0 ? 0 : 1;
    }
}
