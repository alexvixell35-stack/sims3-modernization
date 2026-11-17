using System;
using Ts3Fixes;

// Offline self-check of AutShadowLocalCore (docs/276 rank 5 piece 2): flag off = never posted; household only when lifted;
// unknown scope never; side entries never; B9 source order; B11 commodity order; H8 local rule; value-equality dedupe + cap.
static class AutShadowLocalTest
{
    static int sFails;
    static void Check(int got, int want, string what) { if (got != want) { sFails++; Console.WriteLine("FAIL " + what + ": " + got + ", want " + want); } }
    static void Check(bool got, bool want, string what) { Check(got ? 1 : 0, want ? 1 : 0, what); }

    // InteractionObjectPair's equality: same definition (Equals) and the same Target reference
    sealed class Pair
    {
        public readonly string Def; public readonly object Target;
        public Pair(string def, object target) { Def = def; Target = target; }
        public override bool Equals(object o) { Pair p = o as Pair; return p != null && p.Def == Def && p.Target == Target; }
        public override int GetHashCode() { return Def.GetHashCode() ^ (Target != null ? Target.GetHashCode() : 0); }
    }

    static int P(bool sw, bool hh, bool mgr, bool force, bool over, bool solo, bool local, bool busy, int why, bool lift)
    {
        return AutShadowLocalCore.Post(sw, hh, mgr, force, over, solo, local, busy, why, lift);
    }

    static void Main()
    {
        const int H = UnseenFallbackCore.kHousehold, U = UnseenFallbackCore.kSeen;
        // flag off = never posted, whatever else holds (household untouched with HouseholdToo=0)
        int[] whys = { -1, UnseenFallbackCore.kSeen, UnseenFallbackCore.kNotLive, UnseenFallbackCore.kGone, H, UnseenFallbackCore.kActiveLot, UnseenFallbackCore.kOnScreen };
        foreach (int w in whys)
            for (int b = 0; b < 4; b++)
            {
                Check(P(true, false, true, false, false, false, true, false, w, (b & 1) != 0), AutShadowLocalCore.kOff, "HouseholdToo=0 never posts, why " + w);
                Check(P(false, true, true, false, false, false, true, false, w, (b & 2) != 0), AutShadowLocalCore.kOff, "autt2local=0 never posts, why " + w);
            }
        Check(P(true, true, true, false, false, false, true, false, H, true), AutShadowLocalCore.kPost, "lifted household posts");
        Check(P(true, true, true, false, false, false, true, false, H, false), AutShadowLocalCore.kScopeHh, "household without lift = game");
        Check(P(true, true, true, false, false, false, true, false, -1, true), AutShadowLocalCore.kScopeUnknown, "unknown scope (-1) = game, even lifted");
        Check(P(true, true, true, false, false, false, true, false, UnseenFallbackCore.kNotLive, true), AutShadowLocalCore.kScopeUnknown, "not live = game");
        Check(P(true, true, true, false, false, false, true, false, UnseenFallbackCore.kGone, true), AutShadowLocalCore.kScopeUnknown, "gone = game");
        Check(P(true, true, true, false, false, false, true, false, U, false), AutShadowLocalCore.kPost, "unseen NPC posts");
        Check(P(true, true, true, false, false, false, true, false, UnseenFallbackCore.kOnScreen, false), AutShadowLocalCore.kPost, "seen NPC posts (exact path)");
        Check(P(true, true, false, false, false, false, true, false, H, true), AutShadowLocalCore.kNotManager, "side entry / non-manager = game");
        Check(P(true, true, true, true, false, false, true, false, H, true), AutShadowLocalCore.kForced, "forced local = game");
        Check(P(true, true, true, false, true, false, true, false, U, false), AutShadowLocalCore.kForced, "overridden lot = game");
        Check(P(true, true, true, false, false, true, true, false, U, false), AutShadowLocalCore.kForced, "forced solo = game");
        Check(P(true, true, true, false, false, false, false, false, H, true), AutShadowLocalCore.kNoLocal, "no local phase = nothing to post");
        Check(P(true, true, true, false, false, false, true, true, H, true), AutShadowLocalCore.kBusy, "request in flight = game");

        // B9 sources: lotNull, lotWorld, overridden, actorNpc, actorLotWorld, generic, alienBrain, forceSolo
        int all = (1 << AutShadowLocalCore.kSrcN) - 1;
        Check(AutShadowLocalCore.Sources(false, false, false, false, false, true, false, false), all, "generic on a home lot: every source");
        Check(AutShadowLocalCore.Sources(false, false, false, false, false, false, false, false), 0xF, "commodity search: no situations, no solo");
        Check(AutShadowLocalCore.Sources(false, false, false, false, false, false, true, false), 0x2F, "AlienBrainPower: solo too");
        Check(AutShadowLocalCore.Sources(false, false, false, false, false, false, false, true), 0x2F, "forced solo");
        Check(AutShadowLocalCore.Sources(false, true, false, false, true, false, false, false), 0xB, "world lot: no other Sims' maps");
        Check(AutShadowLocalCore.Sources(false, true, true, false, true, false, false, false), 0xF, "overridden world lot: other Sims too");
        Check(AutShadowLocalCore.Sources(true, false, false, true, true, false, false, false), 0, "NPC on the world lot, no closest lot: nothing");
        Check(AutShadowLocalCore.Sources(true, false, false, false, true, false, false, false), 0x8, "household on the world lot, no lot: inventory only");
        Check(AutShadowLocalCore.Sources(false, false, false, true, true, false, false, false), 0x7, "NPC on the world lot, closest home lot picked: its Sims, no inventory (actor lot rule)");

        // B11 generic commodity order: use values > 0, then desires below MaxY or Energy when sleep is considered
        int[] outK = new int[16];
        int n = AutShadowLocalCore.Commodities(new int[] { 5, 6, 7 }, new float[] { 1f, 0f, 2f }, 3,
            new int[] { 5, 9, 11 }, new float[] { 10f, 100f, 100f }, new float[] { 50f, 50f, 50f }, 3, 9, true, outK);
        Check(n, 4, "commodity count (repeat kept)");
        Check(outK[0] * 1000000 + outK[1] * 10000 + outK[2] * 100 + outK[3], 5070509, "commodity order 5,7,5,9");
        Check(AutShadowLocalCore.Commodities(new int[] { 1 }, new float[] { 1f }, 1, new int[] { 9 }, new float[] { 100f }, new float[] { 50f }, 1, 9, false, outK), 1, "energy not considered");
        Check(AutShadowLocalCore.Commodities(new int[] { 1, 2 }, new float[] { 1f, 1f }, 2, new int[0], new float[0], new float[0], 0, 9, false, new int[1]), 1, "output cap holds");

        // H8 local rule
        Check(AutShadowLocalCore.LocalCacheNoop(true, true, true), true, "own autonomy, cache on, C0 same: no-op");
        Check(AutShadowLocalCore.LocalCacheNoop(true, true, false), false, "C0 refilled: abort cache");
        Check(AutShadowLocalCore.LocalCacheNoop(false, true, true), false, "other autonomy: abort");
        Check(AutShadowLocalCore.LocalCacheNoop(true, false, true), false, "cache off at request: abort");
        LocalC0 a = new LocalC0(), c = new LocalC0();
        object grp = new object(), mem = new object();
        a.Neat = true; a.GroupSituation = grp; a.HouseholdMembers = mem;
        c.CopyFrom(a);
        Check(a.Same(c), true, "copy is same");
        c.HouseholdMembers = new object();
        Check(a.Same(c), false, "new members list = refilled");
        c.CopyFrom(a); c.FireOnLot = true;
        Check(a.Same(c), false, "bool differs");
        Check(a.Same(null), false, "no copy = not same");

        // dedupe: value equality (definition + same target), first wins; cap
        LocalPairSet s = new LocalPairSet(3);
        object t1 = new object(), t2 = new object();
        Check(s.Add(new Pair("Eat", t1)), AutShadowLocalCore.kNew, "first pair");
        Check(s.Add(new Pair("Eat", t1)), AutShadowLocalCore.kDup, "equal pair, other instance = dup");
        Check(s.Add(new Pair("Eat", t2)), AutShadowLocalCore.kNew, "other target");
        Check(s.Add(new Pair("Sit", t1)), AutShadowLocalCore.kNew, "other definition");
        Check(s.Add(new Pair("Nap", t1)), AutShadowLocalCore.kCap, "past cap");
        Check(s.Add(new Pair("Sit", t1)), AutShadowLocalCore.kDup, "dup still found at cap");
        Check(s.Count * 10 + s.Capped, 31, "count 3, capped 1");
        s.Clear();
        Check(s.Add(new Pair("Eat", t1)), AutShadowLocalCore.kNew, "after clear");
        LocalPairSet big = new LocalPairSet(AutShadowLocalCore.kMaxLocalPairs);
        object[] ts = new object[AutShadowLocalCore.kMaxLocalPairs + 5];
        int news = 0, dups = 0, caps = 0;
        for (int i = 0; i < ts.Length; i++) { ts[i] = new object(); int r = big.Add(new Pair("X", ts[i])); if (r == 0) news++; else if (r == 2) caps++; }
        for (int i = 0; i < 100; i++) if (big.Add(new Pair("X", ts[i])) == AutShadowLocalCore.kDup) dups++;
        Check(news, AutShadowLocalCore.kMaxLocalPairs, "big: cap pairs new");
        Check(caps, 5, "big: rest capped");
        Check(dups, 100, "big: repeats are dups");

        // piece 5: per-pair class by walk index, game C2 order against the worker walk
        const int Ps = AutShadowCore.kPass, Fl = AutShadowCore.kTestFailed, Sk = AutShadowCore.kSkip, Ab = AutShadowCore.kAbort;
        Check(AutShadowLocalCore.LocalClass(false, Ps, 1f, Ps, 1f), AutShadowLocalCore.kWalkMiss, "other pair at the index = walk miss");
        Check(AutShadowLocalCore.LocalClass(true, Sk, 0f, Sk, 0f), AutShadowCore.kNoClass, "off-list both sides = no class");
        Check(AutShadowLocalCore.LocalClass(true, Ps, 1.5f, Ps, 1.5f), AutShadowCore.A0, "same bits = A0");
        Check(AutShadowLocalCore.LocalClass(true, Ps, 1f, Ps, BitConverter.ToSingle(BitConverter.GetBytes(BitConverter.ToInt32(BitConverter.GetBytes(1f), 0) + 1), 0)), AutShadowCore.A1, "1 ulp = A1");
        Check(AutShadowLocalCore.LocalClass(true, Ps, 1f, Ps, 2f), AutShadowCore.A2, "far = A2");
        Check(AutShadowLocalCore.LocalClass(true, Ps, 1f, Fl, 0f), AutShadowCore.A3, "pass vs Test failed = A3");
        Check(AutShadowLocalCore.LocalClass(true, Ps, 1f, Ab, 0f), AutShadowCore.A4, "worker abort = A4");
        Check(AutShadowLocalCore.LocalClass(true, Sk, 0f, Ps, 1f), AutShadowCore.A4, "one-sided skip = A4");
        int only;
        Check(AutShadowLocalCore.GameOrderMisses(new int[] { 0, 2, 5, -1, 7 }, 5, out only), 0, "subsequence: no miss");
        Check(only, 1, "one game-only pair");
        Check(AutShadowLocalCore.GameOrderMisses(new int[] { 3, 1, 4, 2, 9 }, 5, out only), 2, "two pairs below the high mark");
        Check(AutShadowLocalCore.GameOrderMisses(new int[] { 3, 1 }, 0, out only) * 10 + only, 0, "n = 0");

        Console.WriteLine(sFails == 0 ? "autshadowlocal selftest: PASS" : "autshadowlocal selftest: " + sFails + " FAIL");
        Environment.Exit(sFails == 0 ? 0 : 1);
    }
}
