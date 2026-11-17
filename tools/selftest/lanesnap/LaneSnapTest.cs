// Offline self-check of unseenlanesnap's decision (mods/ts3fixes/src/LaneSnapCore.cs). Run: node tools/selftest/lanesnap/run.ts
using System;
using Ts3Fixes;

static class LaneSnapTest
{
    static int sFail, sN;
    static void Eq(string what, int want, int got) { sN++; if (want != got) { sFail++; Console.WriteLine("FAIL " + what + ": want " + want + ", got " + got); } }
    static int D(bool shape, bool unseen, bool walk, float gap, float dx, float dy, float dz, bool spot, bool seenOk, out int why)
    { return LaneSnapCore.Decide(shape, unseen, walk, gap, dx, dy, dz, spot, seenOk, out why); }

    static int Main()
    {
        int w;
        // elevator other slot, 1.2 m away, unseen townie, everything fine: snap
        Eq("snap", LaneSnapCore.kSnap, D(true, true, true, 0f, 1.2f, 0f, 0f, true, true, out w)); Eq("snap why", LaneSnapCore.kRjNone, w);
        // shape refused first, whatever else
        Eq("shape", LaneSnapCore.kPass, D(false, true, true, 0f, 0.1f, 0f, 0f, true, true, out w)); Eq("shape why", LaneSnapCore.kRjShape, w);
        // next path a portal, or not continuing the portal's exit (an inserted approach leg), or unknown gap
        Eq("next portal", LaneSnapCore.kPass, D(true, true, false, 0f, 0.1f, 0f, 0f, true, true, out w)); Eq("next why", LaneSnapCore.kRjNext, w);
        Eq("gap", LaneSnapCore.kPass, D(true, true, true, 0.3f, 0.1f, 0f, 0f, true, true, out w)); Eq("gap why", LaneSnapCore.kRjNext, w);
        Eq("gap NaN", LaneSnapCore.kPass, D(true, true, true, float.NaN, 0.1f, 0f, 0f, true, true, out w)); Eq("gap NaN why", LaneSnapCore.kRjNext, w);
        Eq("near unseen", LaneSnapCore.kNear, D(true, true, true, 0f, 0.3f, 0.1f, 0.3f, false, false, out w));
        Eq("near other off", LaneSnapCore.kPass, D(true, false, true, 0f, 0.3f, 0f, 0f, true, true, out w)); Eq("near other why", LaneSnapCore.kRjScope, w);
        // near band edge (0.5 m) is a snap case, not near
        Eq("near edge", LaneSnapCore.kSnap, D(true, true, true, 0f, 0.5f, 0f, 0f, true, true, out w));
        // v2: no distance / floor / room limit when unseen: 3.1 m, another floor, 40 m all snap; only a NaN spot refuses
        Eq("v2 far xz snaps", LaneSnapCore.kSnap, D(true, true, true, 0f, 3.1f, 0f, 0f, true, true, out w));
        Eq("v2 other floor snaps", LaneSnapCore.kSnap, D(true, true, true, 0f, 1f, 0.5f, 0f, true, true, out w));
        Eq("v2 near but other floor snaps", LaneSnapCore.kSnap, D(true, true, true, 0f, 0.1f, 3f, 0f, true, true, out w));
        Eq("v2 40 m snaps", LaneSnapCore.kSnap, D(true, true, true, 0f, 40f, 0f, 0f, true, true, out w));
        Eq("bad point NaN", LaneSnapCore.kPass, D(true, true, true, 0f, float.NaN, 0f, 0f, true, true, out w)); Eq("bad point NaN why", LaneSnapCore.kRjFar, w);
        Eq("bad point NaN dy", LaneSnapCore.kPass, D(true, true, true, 0f, 1f, float.NaN, 0f, true, true, out w)); Eq("bad point NaN dy why", LaneSnapCore.kRjFar, w);
        Eq("bad point inf", LaneSnapCore.kPass, D(true, true, true, 0f, float.PositiveInfinity, 0f, 0f, true, true, out w)); Eq("bad point inf why", LaneSnapCore.kRjFar, w);
        // geometry order: spot, seen
        Eq("spot", LaneSnapCore.kPass, D(true, true, true, 0f, 1f, 0f, 0f, false, true, out w)); Eq("spot why", LaneSnapCore.kRjSpot, w);
        Eq("seen", LaneSnapCore.kPass, D(true, true, true, 0f, 1f, 0f, 0f, true, false, out w)); Eq("seen why", LaneSnapCore.kRjSeen, w);
        // NeedsGeo agrees with Decide: geometry for every unseen non-near real point
        Eq("geo snap", 1, LaneSnapCore.NeedsGeo(true, true, true, 0f, 1f, 0f, 0f) ? 1 : 0);
        Eq("geo near", 0, LaneSnapCore.NeedsGeo(true, true, true, 0f, 0.2f, 0f, 0f) ? 1 : 0);
        Eq("geo seen scope", 0, LaneSnapCore.NeedsGeo(true, false, true, 0f, 1f, 0f, 0f) ? 1 : 0);
        Eq("geo far", 1, LaneSnapCore.NeedsGeo(true, true, true, 0f, 4f, 0f, 0f) ? 1 : 0);
        Eq("geo floor", 1, LaneSnapCore.NeedsGeo(true, true, true, 0f, 1f, 1f, 0f) ? 1 : 0);
        Eq("geo NaN", 0, LaneSnapCore.NeedsGeo(true, true, true, 0f, float.NaN, 0f, 0f) ? 1 : 0);
        Eq("geo gap", 0, LaneSnapCore.NeedsGeo(true, true, true, 1f, 1f, 0f, 0f) ? 1 : 0);
        // every NeedsGeo == false case with unseen/shape/walk ok returns before the geometry tests in Decide
        float[] xs = { 0f, 0.2f, 0.49f, 0.5f, 1f, 2.9f, 3f, 3.01f, 10f, float.NaN };
        float[] ys = { 0f, 0.3f, 0.49f, 0.5f, 2f, float.NaN };
        foreach (float x in xs) foreach (float y in ys)
        {
            bool g = LaneSnapCore.NeedsGeo(true, true, true, 0f, x, y, 0f);
            int a = D(true, true, true, 0f, x, y, 0f, false, false, out w);
            Eq("geo order x" + x + " y" + y, g ? LaneSnapCore.kRjSpot : (a == LaneSnapCore.kNear ? LaneSnapCore.kRjNone : LaneSnapCore.kRjFar), w);
        }
        // portal kinds
        Eq("kind cab", 0, LaneSnapCore.PortalKind("ElevatorInterior"));
        Eq("kind doors", 0, LaneSnapCore.PortalKind("ElevatorDoors"));
        Eq("kind door", 1, LaneSnapCore.PortalKind("DoorSingleContemporary"));
        Eq("kind hero", 1, LaneSnapCore.PortalKind("HeroDoor"));
        Eq("kind gate", 1, LaneSnapCore.PortalKind("FenceDeckAgedWood_Gate2x1"));
        Eq("kind stairs", 2, LaneSnapCore.PortalKind("StairsModern"));
        Eq("kind null", 2, LaneSnapCore.PortalKind(null));
        Console.WriteLine(sFail == 0 ? "lanesnap selftest ok (" + sN + " checks)" : "lanesnap selftest FAILED " + sFail + " of " + sN);
        return sFail == 0 ? 0 : 1;
    }
}
