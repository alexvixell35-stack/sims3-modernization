using System;
using System.Collections.Generic;
using Sims3.Gameplay;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects.Vehicles;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Route and placement rules made less strict (docs/675). Three switches, all default off, each on its own.
    //
    // fglnonescan [verified, ilspycmd GlobalFunctions.FindGoodLocationNearby; docs/671 §11.3 logs]: after the same-position
    // probes, the same room and the adjoining rooms, EA runs up to three lot-wide native searches from the same start with
    // the same object and constraints: indoors (check IsInside; only when the start is in a room), outdoors (IsOutside) and
    // same lot (no check). They differ in the managed check alone, which the native search asks only for a spot that already
    // passed its placement test, and a search that finds nothing walks the whole lot. Measured (s154-s158): a guitar jig's
    // indoors search tested 3344 tiles, 196 spots passed and were refused as outdoors (654 / 675 ms), then the outdoors
    // search re-tested 514 of them (271 / 273 ms); a laptop jig and a toddler put-down ran two or three identical failed
    // scans in a row (50-58 ms each). Switch on: one scan with one check that takes a spot of the first stage's class at
    // once and keeps the first spot of each later stage's class; at the end the lowest stage wins. Same rule and the same
    // spot as EA's stages (up to the search's own random first facing), in one scan. The kept spot is confirmed by a
    // second search that starts on it; if that does not return it, EA's own stages run. All Sims: the rule is EA's.
    //
    // fglnnearspot (relaxed rule; includes the one scan): EA takes ANY indoor spot of the lot before the nearest outdoor
    // one. Switch on: the scan also ends at the first spot more than kFglnNearSpotMargin metres further out than the
    // nearest valid spot seen; an indoor spot wins only inside that margin, else the nearest spot does. Every spot
    // returned passed the game's placement test with the caller's constraints (Routable among them). Seen: a Sim whose
    // room and the rooms next to it have no space plays, sits down or puts the toddler down at the nearest free spot of
    // the lot instead of a far room. Household and selected Sims keep EA's rule (Fixes.IsPlayerSim, never lifted by HouseholdToo; the Sim is the
    // one whose task runs the search, unknown = household).
    //
    // followerlasttry (relaxed rule) [verified, ilspycmd GoHereWithHoldingInteraction.Run / AddRouteDest; docs/643 §7.2]:
    // a follower of a group walk gets GoHereWithSituation.NumberOfTimesToRoute (3) tries; each plans to two 60 degree
    // arcs beside the leader, 1-3.5 m from the meeting point, in the meeting point's room, and a failed try only moves
    // the point 0.45 m: nearly the same goals three times. EA itself uses the full ring for horses, boats and groups of
    // four or more. Switch on: the last try (and later ones at the same place) of an NPC follower uses the full ring, so
    // the Sim may stand in front of or behind the leader. Distance, room, footprint checks and Sim spacing stay EA's.
    // No counter slot: the checks' result lines and the log lines below.
    public static partial class Fixes
    {
        static bool sRrNearbyReady, sRrFollowReady;
        static int sFglnScans, sFglnAccepted, sFglnKept, sFglnNone, sFglnNotMerged, sFglnConfirmFail, sFglnErrors, sFglnExempt;
        static int sFglnStops;      // fglnnearspot: scans ended by the margin
        static uint sFglnUs;
        static int sFlWidened, sFlSeen, sFlExempt, sFlErrors;
        static readonly Dictionary<ulong, FlState> sFlState = new Dictionary<ulong, FlState>();

        static readonly Type[] sRrDestArgs = new Type[] { typeof(Route), typeof(Vector3).MakeByRefType(), typeof(Vector3).MakeByRefType(), typeof(float), typeof(float) };

        static void ApplyRouteRelax()
        {
            // FindGoodLocationNearby has one swap, FglInBound.cs's FibNearbyHook; it asks RrNearby first (ApplyFglInBound runs before this)
            sRrNearbyReady = sFibNearbyReady;
            sRrFollowReady = Patch(typeof(GoHereWithHoldingInteraction), "AddRouteDest", sRrDestArgs, typeof(RrFollowHook), "DestHook", "DestOrig");
        }

        // world load: Sim ids and the clock belong to the world just left
        public static void RouteRelaxWorldLoad() { sFlState.Clear(); }

        // the Sim whose task is running (an interaction's placement search runs on its Sim's task); null = unknown
        static Sim RrTaskSim()
        {
            try
            {
                IScriptProxy p = Simulator.GetProxy(Simulator.CurrentTask);
                return p != null ? p.Target as Sim : null;
            }
            catch (ResetException) { throw; }
            catch (Exception) { return null; }
        }

        // the managed check of the one scan: classes the spot as EA's IsInside / IsOutside do and asks the pick
        sealed class FglnProbe
        {
            public FglnPick Pick; public float X0, Z0;
            public bool Check(float x, float y, float z, float absAngle)
            {
                LotLocation loc = LotLocation.Invalid;
                ulong lot = World.GetLotLocation(new Vector3(x, y, z), ref loc);
                int room = World.GetRoomId(lot, loc, eRoomDefinition.LightBlocking);
                float dx = Math.Abs(x - X0), dz = Math.Abs(z - Z0);
                return Pick.Offer(RouteRelaxCore.SpotClass(room), x, y, z, dx > dz ? dx : dz);  // the search walks square rings
            }
        }

        // the confirming search's check: only the kept spot
        sealed class FglnOnly
        {
            public float X, Z;
            public bool Check(float x, float y, float z, float absAngle) { return Math.Abs(x - X) < 0.05f && Math.Abs(z - Z) < 0.05f; }
        }

        static string FglnClassName(int rank) { return rank == 0 ? "indoor" : rank == 1 ? "outdoor" : rank == 2 ? "lot" : "none"; }

        // The lot-wide stages as one scan. Returns 1 found (position / forward set), 0 nothing (EA's answer: false), -1 run EA's stages.
        static int FglnScan(IGameObject obj, ObjectGuid[] ids, Vector3 p0, int searches, int stages, FindGoodLocationBooleans bools, float margin,
            out Vector3 position, out Vector3 forward)
        {
            uint t0 = SrcNowUs();
            ObjectGuid id = obj.ObjectId;
            FglnProbe probe = new FglnProbe();
            FglnPick pick = probe.Pick = new FglnPick(stages, margin);
            Vector3 s0 = World.SnapToFloor(p0);
            probe.X0 = s0.x; probe.Z0 = s0.z;
            World.FindGoodLocationParams prm = new World.FindGoodLocationParams(p0, bools & ~FindGoodLocationBooleans.StayInRoom);
            prm.PlacementCheckFunc = probe.Check;
            bool ok = World.FindGoodLocation(id, prm, ids, out position, out forward);
            sFglnScans++;
            int res;
            string how;
            if (ok && pick.Accepted) { sFglnAccepted++; res = 1; how = "took the " + FglnClassName(pick.Rank) + " spot " + pick.Dist.ToString("0.0") + " m out"; }
            else if (pick.Rank >= 0)
            {
                if (pick.Stopped) sFglnStops++;
                FglnOnly only = new FglnOnly();
                only.X = pick.X; only.Z = pick.Z;
                World.FindGoodLocationParams again = new World.FindGoodLocationParams(new Vector3(pick.X, pick.Y, pick.Z), bools & ~FindGoodLocationBooleans.StayInRoom);
                again.PlacementCheckFunc = only.Check;
                again.MaximumSearchRadius = 2f;
                if (World.FindGoodLocation(id, again, ids, out position, out forward))
                {
                    sFglnKept++; res = 1;
                    how = "kept the " + FglnClassName(pick.Rank) + " spot " + pick.Dist.ToString("0.0") + " m out"
                        + (pick.Stopped ? ", scan ended by the " + margin.ToString("0.#") + " m margin (EA: any indoor spot of the lot first)" : ", no spot of an earlier stage on the lot");
                }
                else { sFglnConfirmFail++; res = -1; how = "the kept spot was not returned again: EA's stages run"; }
            }
            else if ((searches & RouteRelaxCore.kAnywhere) == 0) { sFglnNone++; res = 0; how = "no spot on the lot (" + pick.Calls + " passed the test)"; }
            else
            {
                // EA's last stage: no constraints; outdoors asked for = off any lot's rooms
                sFglnNone++;
                World.FindGoodLocationParams any = new World.FindGoodLocationParams(p0, FindGoodLocationBooleans.None);
                if ((searches & RouteRelaxCore.kOutdoors) != 0) any.PlacementCheckFunc = GlobalFunctions.IsOutsideEvenOnWorldLot;
                res = World.FindGoodLocation(id, any, ids, out position, out forward) ? 1 : 0;
                how = "no spot on the lot, EA's anywhere stage " + (res == 1 ? "found one" : "failed");
            }
            uint us = unchecked(SrcNowUs() - t0);
            sFglnUs += us;
            if (us >= 20000 || pick.Stopped || res < 0)
                HcLine("fglnonescan: " + (us / 1000) + " ms, " + obj.GetType().Name + ", stages " + stages + " (1 indoors, 2 outdoors, 4 lot) in one scan, "
                    + pick.Calls + " spots passed, " + how);
            return res;
        }

        // FibNearbyHook (FglInBound.cs) asks this first. 1 / 0 = the call's answer, -1 = neither switch on (fglinbound or EA's body
        // answers as before).
        static int RrNearby(IGameObject obj, ObjectGuid[] objIdsToIgnore, ref Vector3 position, ref Vector3 forward, float offset,
            GlobalFunctions.FindGoodLocationStrategies searches, FindGoodLocationBooleans fglBools)
        {
            if (obj == null) return -1;
            bool one = ShortcutOn("fglnonescan"), near = ShortcutOn("fglnnearspot");
            if (!one && !near) return -1;
            float margin = -1f;
            if (near)
            {
                // a relaxed rule (another spot than EA's): household, selected and unknown Sims keep EA's rule, HouseholdToo never lifts it
                if (IsPlayerSim(RrTaskSim())) sFglnExempt++;
                else margin = Math.Max(0f, Instantiator.kFglnNearSpotMargin);
            }
            if (!one && margin < 0f) return -1;
            Vector3 p0 = position, f0 = forward;
            try
            {
                int s = (int)searches;
                LotLocation loc = LotLocation.Invalid;
                ulong lot = World.GetLotLocation(p0, ref loc);
                int room = World.GetRoomId(lot, loc, eRoomDefinition.LightBlocking);
                int stages = RouteRelaxCore.Stages(s, lot != 0, room);
                if (RouteRelaxCore.StageCount(stages) >= 2)
                {
                    // the probes at the position, the same room and the adjoining rooms: EA's own body, those stages only
                    int early = s & RouteRelaxCore.kEarly;
                    if (early != 0 && FibNearbyHook.NearbyOrig(obj, objIdsToIgnore, ref position, ref forward, offset, (GlobalFunctions.FindGoodLocationStrategies)early, fglBools))
                        return 1;
                    int res = FglnScan(obj, objIdsToIgnore, p0, s, stages, fglBools, margin, out position, out forward);
                    if (res >= 0) return res;
                    position = p0; forward = f0;
                    return FibNearbyHook.NearbyOrig(obj, objIdsToIgnore, ref position, ref forward, offset, (GlobalFunctions.FindGoodLocationStrategies)(s & ~RouteRelaxCore.kEarly), fglBools) ? 1 : 0;
                }
                sFglnNotMerged++;
            }
            catch (ResetException) { throw; }
            catch (Exception) { sFglnErrors++; }
            position = p0; forward = f0;
            return -1;
        }

        public class RrFollowHook
        {
            public float DestHook(Route r, ref Vector3 coneDir, ref Vector3 meetingPoint, float spacing, float angle)
            {
                try
                {
                    if (angle < 359f && ShortcutOn("followerlasttry"))
                    {
                        object self = this;
                        GoHereWithHoldingInteraction ia = self as GoHereWithHoldingInteraction;
                        Sim a = ia != null ? ia.Actor : null;
                        if (a == null || IsPlayerSim(a)) sFlExempt++;  // a relaxed rule: HouseholdToo never lifts it
                        else
                        {
                            sFlSeen++;
                            if (sFlState.Count > 256) sFlState.Clear();
                            ulong id = a.ObjectId.Value;
                            FlState st;
                            if (!sFlState.TryGetValue(id, out st)) sFlState[id] = st = new FlState();
                            int tries = GoHereWithSituation.NumberOfTimesToRoute;
                            int n = RouteRelaxCore.FlNote(st, meetingPoint.x, meetingPoint.z, SimClock.CurrentTicks, SimClock.ConvertToTicks(60f, TimeUnit.Minutes));
                            if (RouteRelaxCore.FlWiden(n, tries))
                            {
                                if (st.Calls > 1) return spacing;  // the try's second arc: the ring is already in (EA returns the spacing it was given)
                                sFlWidened++;
                                HcLine("followerlasttry: " + a.FullName + " try " + n + " of " + tries + " at " + meetingPoint.x.ToString("0.0") + "," + meetingPoint.z.ToString("0.0")
                                    + ": the full ring instead of the two side arcs");
                                angle = 360f;
                            }
                        }
                    }
                }
                catch (ResetException) { throw; }
                catch (Exception) { sFlErrors++; }
                return DestOrig(r, ref coneDir, ref meetingPoint, spacing, angle);
            }
            public float DestOrig(Route r, ref Vector3 coneDir, ref Vector3 meetingPoint, float spacing, float angle) { return 0f; } // receives GoHereWithHoldingInteraction.AddRouteDest
        }

        static string FglnCounters(string name)
        {
            return name + ": one scans " + sFglnScans + " (" + (sFglnUs / 1000) + " ms; first-stage spot " + sFglnAccepted + ", kept spot " + sFglnKept + ", no spot " + sFglnNone
                + ", ended by the margin " + sFglnStops + "), calls with fewer than two lot-wide stages " + sFglnNotMerged + ", household or unknown Sim kept EA's rule " + sFglnExempt
                + ", kept spot not confirmed " + sFglnConfirmFail + ", errors " + sFglnErrors + "; " + GlMembers(CkLive(name));
        }

        // FglnOneScanCheck run: the logged shapes through EA's stage order and through the one scan (pure).
        public static object FglnOneScanCheck(string what)
        {
            string counters = FglnCounters("fglnonescan");
            if (what == "result") return counters;
            if (what != "run") return "fglnonescan <run|result>";
            bool on = ShortcutOn("fglnonescan");
            const int I = RouteRelaxCore.kInside, O = RouteRelaxCore.kOutside, X = RouteRelaxCore.kOther, all = 7;
            // the guitar jig of s154: start in a room, every passing spot outdoors
            int scans, mine, mineScans;
            int[] jig = new int[] { O, O, O, O };
            int ea = RouteRelaxCore.EaPick(all, jig, out scans);
            mine = FglnSim(on, all, jig, -1f, null, out mineScans);
            bool eaTwice = ea == 0 && scans == 2;
            bool fixedNow = on && mine == ea && mineScans == 1;
            // controls: same spot as EA in every shape; nothing passes = nothing; one stage alone is left to EA
            bool controls = eaTwice;
            int[][] shapes = new int[][] { new int[] { O, O, I, O }, new int[] { X, O, I }, new int[] { X, X }, new int[0], new int[] { I }, new int[] { X, I, O } };
            int[] stageSets = new int[] { 7, 6, 3, 5 };
            foreach (int st in stageSets)
                foreach (int[] sh in shapes)
                {
                    int a, b;
                    controls &= RouteRelaxCore.EaPick(st, sh, out a) == FglnSim(true, st, sh, -1f, null, out b) && b == 1;
                }
            controls &= RouteRelaxCore.StageCount(RouteRelaxCore.Stages(RouteRelaxCore.kOutdoors | RouteRelaxCore.kAnywhere, true, 3)) == 1
                && RouteRelaxCore.Stages(0x7F, true, 0) == 6 && RouteRelaxCore.Stages(0x7F, false, 3) == 4 && RouteRelaxCore.Stages(0x7F, true, 3) == 7;
            return B582Line("fglnonescan", on, "jig with outdoor spots only: " + (on ? mineScans : scans) + " lot-wide scan(s), spot " + (on ? mine : ea), fixedNow,
                "EA runs the indoors and the outdoors scan (" + eaTwice + "), the one scan returns EA's spot in 24 shapes, one stage alone is not merged", controls, sRrNearbyReady ? "" : "FindGoodLocationNearby swap", counters);
        }

        // Pure: the spots through the one scan (on) or EA's stages (off). dist null = spot i is i metres out.
        internal static int FglnSim(bool on, int stages, int[] cls, float margin, float[] dist, out int scans)
        {
            if (!on) return RouteRelaxCore.EaPick(stages, cls, out scans);
            scans = 1;
            FglnPick pick = new FglnPick(stages, margin);
            for (int i = 0; i < cls.Length; i++)
                if (pick.Offer(cls[i], i, 0f, 0f, dist != null ? dist[i] : i)) break;
            return pick.Rank >= 0 ? (int)pick.X : -1;  // x carries the spot's index
        }

        // FglnNearSpotCheck run: the nearest outdoor spot against a far indoor one (pure).
        public static object FglnNearSpotCheck(string what)
        {
            string counters = FglnCounters("fglnnearspot");
            if (what == "result") return counters;
            if (what != "run") return "fglnnearspot <run|result>";
            bool on = ShortcutOn("fglnnearspot");
            const int I = RouteRelaxCore.kInside, O = RouteRelaxCore.kOutside;
            float m = Math.Max(0f, Instantiator.kFglnNearSpotMargin);
            int scans;
            // outdoor spots 3 m and 3 + margin + 1 m out, an indoor spot 30 m out
            int[] cls = new int[] { O, O, I };
            float[] dist = new float[] { 3f, 4f + m, 30f };
            int ea = RouteRelaxCore.EaPick(7, cls, out scans);
            int got = on ? FglnSim(true, 7, cls, m, dist, out scans) : ea;
            bool fixedNow = on && got == 0 && ea == 2;
            // controls: an indoor spot inside the margin still wins; without the margin (household) EA's spot; nothing passes = nothing
            int a;
            bool controls = ea == 2
                && FglnSim(true, 7, new int[] { O, I, O }, m, new float[] { 3f, 3f + m, 9f + m }, out a) == 1
                && FglnSim(true, 7, cls, -1f, dist, out a) == 2
                && FglnSim(true, 7, new int[0], m, new float[0], out a) == -1
                && FglnSim(true, 7, new int[] { I, O }, m, new float[] { 2f, 2f }, out a) == 0;
            return B582Line("fglnnearspot", on, "outdoor spot 3 m out, indoor spot 30 m out: spot " + got + " taken (0 = the near one)", fixedNow,
                "EA takes the far indoor spot, an indoor spot within " + m.ToString("0.#") + " m of the nearest still wins, household Sims keep EA's rule", controls,
                sRrNearbyReady ? "" : "FindGoodLocationNearby swap", counters);
        }

        // FollowerLastTryCheck run: three tries 0.45 m apart (pure).
        public static object FollowerLastTryCheck(string what)
        {
            string counters = "followerlasttry: arc calls of NPC followers " + sFlSeen + ", tries given the full ring " + sFlWidened + ", household or unknown Sim " + sFlExempt
                + ", errors " + sFlErrors + "; " + GlMembers(CkLive("followerlasttry"));
            if (what == "result") return counters;
            if (what != "run") return "followerlasttry <run|result>";
            bool on = ShortcutOn("followerlasttry");
            FlState st = new FlState();
            int rings = 0, lastTry = 0;
            for (int t = 0; t < 3; t++)
                for (int arc = 0; arc < 2; arc++)
                {
                    lastTry = RouteRelaxCore.FlNote(st, 10f + 0.45f * t, 20f, 100 + t, 1000);
                    if (on && RouteRelaxCore.FlWiden(lastTry, 3) && st.Calls == 1) rings++;
                }
            bool fixedNow = on && rings == 1 && lastTry == 3;
            FlState far = new FlState();
            bool controls = lastTry == 3
                && RouteRelaxCore.FlNote(far, 0f, 0f, 1, 1000) == 1 && RouteRelaxCore.FlNote(far, 0f, 0f, 1, 1000) == 1 && far.Calls == 2
                && RouteRelaxCore.FlNote(far, 5f, 0f, 2, 1000) == 1                 // another meeting point starts over
                && RouteRelaxCore.FlNote(far, 5.4f, 0f, 3, 1000) == 2
                && RouteRelaxCore.FlNote(far, 5.4f, 0f, 5000, 1000) == 1            // an old try does not count
                && !RouteRelaxCore.FlWiden(2, 3) && RouteRelaxCore.FlWiden(4, 3) && !RouteRelaxCore.FlWiden(1, 1);
            return B582Line("followerlasttry", on, "three tries 0.45 m apart: " + rings + " with the full ring", fixedNow,
                "tries 1 and 2 keep EA's two arcs, a new meeting point or an old try starts over, a single-try setting never widens", controls,
                sRrFollowReady ? "" : "AddRouteDest swap", counters);
        }
    }
}
