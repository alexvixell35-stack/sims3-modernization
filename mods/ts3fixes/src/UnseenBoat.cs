using System;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Objects.Island;
using Sims3.Gameplay.ObjectComponents;
using Sims3.Gameplay.Objects.Vehicles;
using Sims3.Gameplay.Routing;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // docs/248 unseen boat trips (switch unseenboat, off; Rodney's design 2026-11-06). A boat trip costs a plan, a get-in, then a whole
    // replan on the sim thread with the boat as follower (docs/247 §3: 100-340 ms). An unseen lone townie that needs the water skips it:
    //   Transport: own Route.Plan that may take a boat, goal on outdoor ground in another land component (flood colours differ):
    //     the Sim waits for the travel time where it stands, is placed at the goal, plans the last metres. No get-in, no boat.
    //   Houseboat: goal on a houseboat lot: the same wait, then EA's Houseboat.PlaceSimAtEntrance, as Houseboat.cs does for a Sim
    //     with no boat beside it, and the same route options off.
    // The plan's caller must follow the route: UbCore.Follows, wider than D's rule, which holds no lot trip (docs/248 §8).
    // Everything else keeps the exact path: unknown colours, a boat route (BeginAsBoat, EndAsBoat, deep-water goal), a group,
    // followers or a passenger, an owned or chosen boat (it must end up at the goal), an interaction about the water, a goal indoors, in the water
    // or seen. Seen during the wait: the Sim has not moved, the exact plan runs from there.
    // The call sits at the top of PlanBackoff, under the existing Route.Plan hook: no new swap, UfPre / UfPlanBody / UfPlan keep their
    // IL (a save can hold them, docs/242). The wait sleeps in the Sim's own task like D's UfHold. The plan itself stays in PlanBackoff,
    // issue #92. Memory only: nothing in saves. Counters [836-849] (Fixes.cs).
    // unseenboatstart (off, needs unseenboat; Rodney 2026-11-07, docs/248 §10): an unseen Sim starting on a houseboat leaves it the same way, from deck or
    // cabin: no colour is needed, the wait runs on the boat's real distance, a lot goal is placed at the lot's street side. Counters [850-853].
    public static partial class Fixes
    {
        const float kUbBoatRadiusM = 6f, kUbHouseRadiusM = 15f;  // half the diagonal of the 17x22 m houseboat

        // A houseboat's lot is not where the boat shows: on IP the lot stands at z ~2060, the boat at its port [measured: unseenboat_log start points, houseboats.json].
        // So a Sim's or a goal's position says nothing about the view of the boat, and its distances are not the trip's.
        static Sims3.Gameplay.Objects.Island.Houseboat UbBoatOf(Lot lot) { return lot != null && lot.IsHouseboatLot() ? lot.GetHouseboat() : null; }
        static bool UbBoatUnseen(Lot lot) { Houseboat h = UbBoatOf(lot); return h == null || UfPosUnseen(h.Position, kUbHouseRadiusM); }
        static float UbTripM(Lot from, Vector3 start, Lot to, Vector3 dest)
        {
            Houseboat a = UbBoatOf(from), b = UbBoatOf(to);
            return ((b != null ? b.Position : dest) - (a != null ? a.Position : start)).Length();
        }

        // docs/247 §11.3: the Sim's last refusal owns the boat replans of that trip. Runs with unseenboat and boatreplanlog on; agent unseenboat_ms
        static readonly System.Collections.Generic.Dictionary<ulong, string> sUbLastWhy = new System.Collections.Generic.Dictionary<ulong, string>();
        static readonly UbReasonMs sUbMs = new UbReasonMs();
        // docs/247 §12.9 (boatreplanlog only, log text): which UbTrip exit left the Sim with no refusal record after the Remove, so the
        // boatreplan: lines can split the "no refusal" row: "goal invalid or < 40 m", "nested plan", "same lot", "one land area"
        static readonly System.Collections.Generic.Dictionary<ulong, string> sUbCleared = new System.Collections.Generic.Dictionary<ulong, string>();
        static void UbClearedBy(ulong sim, string exit)
        {
            if (!ShortcutOn("boatreplanlog")) return;
            if (sUbCleared.Count >= 512) sUbCleared.Clear();
            if (exit == null) sUbCleared.Remove(sim); else sUbCleared[sim] = exit;
        }
        static string UbClearedText(ulong sim)
        {
            string e;
            return sUbCleared.TryGetValue(sim, out e) ? e : "-";
        }
        static void UbBookReplan(ulong sim, uint ms)
        {
            string why;
            sUbLastWhy.TryGetValue(sim, out why);
            sUbMs.Add(why, ms);
            AppDomain.CurrentDomain.SetData("ts3fixes.unseenboat.ms", sUbMs.Lines());
        }

        public partial class RouteBackoffHook
        {
            // ReplanFromPointAsync: the replan after a get-in, the one docs/247 measured. Counted with the switch on or off, so the share
            // of unseen boat replans shows on both arms (the 89-99% of docs/247 is locomotion updates)
            void UbCountReplan()
            {
                IScriptProxy f = Follower;
                Boat boat = f != null ? f.Target as Boat : null;
                if (boat == null) return;
                try
                {
                    Sim sim = boat.Driver;
                    RoutingComponent rc = sim != null ? sim.RoutingComponent : null;
                    RouteAction prev = rc != null ? rc.GetPreviousRouteAction() : null;
                    if (prev == null || prev.GetType().Name != "GetInBoatRouteAction") return;  // internal class
                    int[] c = PatchCounters();
                    c[836]++;
                    c[UbCore.ReplanUnseen(UfWhy(sim), UfPosUnseen(boat.Position, kUbBoatRadiusM)) ? 838 : 837]++;
                }
                catch (Exception) { }
            }

            // PlanBackoff, before the plan. Returns with the Sim moved, or as it was: the plan below is right either way
            void UbTrip(Sim sim, bool marked)
            {
                if (sStuckIn || sInOwnCheck || sInRoutableCheck || sUfInLeg || Simulator.CurrentTask != sim.ObjectId) return;  // UfPlanBody's rule
                if (!GetOption2(Sims3.SimIFace.Route.RouteOption2.EnablePlanningAsBoat) || UfCarOrBoat(true)) return;
                sUbLastWhy.Remove(sim.ObjectId.Value);  // a new trip: the last refusal no longer owns the next boat replan
                UbClearedBy(sim.ObjectId.Value, null);
                int[] c = PatchCounters();
                Vector3 dest = GetApproximateOriginalDestination(), start = sim.Position;
                if (dest == Vector3.Origin || dest == Vector3.Invalid || !UbCore.FarEnough((dest - start).Length())) { UbClearedBy(sim.ObjectId.Value, "goal invalid or < 40 m"); return; }
                if (sUfPlanDepth.Depth(Simulator.CurrentTask.Value) > 1) { UbClearedBy(sim.ObjectId.Value, "nested plan"); return; }
                Lot goal = LotManager.GetLotAtPoint(dest), from = sim.LotCurrent;
                int trip, colours = 0;
                string caller = null;
                Vector3 place = dest;
                bool leave = false;
                try
                {
                    bool house = goal != null && goal.IsHouseboatLot(), same = goal == from;
                    leave = !same && UbBoatOf(from) != null && ShortcutOn("unseenboatstart");
                    colours = house || same || leave ? 1 : UfSameComponent(sim, start, dest, goal);
                    trip = UbCore.Trip(house, colours, same, leave);
                    if (trip == UbCore.kExact)
                    {
                        if (colours == 0) { c[849]++; UbSample(sim, "colours unknown, start " + (UfColourA(start, sim.LotCurrent) ? "known" : "unknown"), goal, dest, start, colours, caller); }
                        else UbClearedBy(sim.ObjectId.Value, same ? "same lot" : "one land area");  // one colour: planned by boat inside one land area
                        return;
                    }
                    c[845]++;
                    if (leave) c[850]++;
                    string why = UbGate(sim, trip, goal, dest, start, ref place, c, leave, out caller);
                    if (why != null) { UbSample(sim, UbCore.LeaveReason(leave, why), goal, dest, start, colours, caller); return; }
                }
                catch (Exception e) { c[841]++; UbSample(sim, UbCore.LeaveReason(leave, "threw " + e.GetType().Name), goal, dest, start, colours, caller); return; }  // a gate failure costs nothing but the exact plan
                if (marked) UfSend(2);  // unseenfbplan's cut mark is for the plan, not for a wait
                try { UbGo(sim, trip, goal, dest, start, place, c, colours, caller, leave); }
                finally { if (marked) UfSend(1); }
            }

            // null = go on; else the refusal, counted
            string UbGate(Sim sim, int trip, Lot goal, Vector3 dest, Vector3 start, ref Vector3 place, int[] c, bool leave, out string caller)
            {
                bool follows = UbFollows(out caller), startOut = World.GetRoomId(start) == 0;
                // indoors counts as in D's relaxed rule: its lot may not lock the Sim in (below). The start height is not used: the Sim is placed elsewhere
                if (sim.Posture != null && UbCore.WaterPosture(sim.Posture.GetType().Name)) return UbNo(c, 841, "start swimming");
                if (GetOption2(Sims3.SimIFace.Route.RouteOption2.EndAsBoat) || GetOption2(Sims3.SimIFace.Route.RouteOption2.DestinationMustBeInDeepWater)) return UbNo(c, 841, "ends on the water");
                if (!follows) return UbNo(c, 841, "caller " + (caller ?? "?"));
                if (!MayYield()) return UbNo(c, 841, "no yield");
                if (!UfPreSim(sim, false)) return UbNo(c, 842, "Sim " + UfPreSimWhy(sim, false));
                if (sim.Household == null) return UbNo(c, 842, "Sim no household");  // unknown scope counts as household (household rule)
                if (UbCore.Denied(sim.CurrentInteraction.GetType().FullName)) return UbNo(c, 842, "water interaction");
                if (!UbAlone(sim.CurrentInteraction)) return UbNo(c, 842, "followers");
                string lw = leave ? UbLeaveWhy(sim) : UfRelaxedLotWhy(sim.LotCurrent, "start", UrCore.StartLotCounts(startOut));  // leaving: a cabin is no reason, the boat's state is
                if (lw != null) return UbNo(c, 842, lw);
                if (UfWhy(sim) != 0 || !TownieUnseen(sim, null) || !UfGoalUnseen(dest) || !UbBoatUnseen(sim.LotCurrent) || !UbBoatUnseen(goal)) return UbNo(c, 846, "seen");
                bool temporary = false;
                if (sim.GetOwnedAndUsableBoat(sim.LotCurrent, false, null, ref temporary) != null || temporary) return UbNo(c, 844, "owned boat");
                if (goal != null && goal == Household.ActiveHouseholdLot) return UbNo(c, 847, "goal household lot");
                if (leave && !UbCore.FarEnough(UbTripM(sim.LotCurrent, start, goal, dest))) return UbNo(c, 847, "short trip");
                if (trip == UbCore.kHouseboat)
                {
                    Houseboat gh = goal.GetHouseboat();  // a goal boat under way stays exact (§4): a Sim placed on it would get no seasickness alarm
                    return gh == null ? UbNo(c, 847, "no houseboat") : gh.CurrentStatus == Houseboat.Status.Routing ? UbNo(c, 847, "goal boat moving") : null;
                }
                lw = UfRelaxedLotWhy(goal, "goal", true);
                if (lw != null) return UbNo(c, 847, lw);  // a resort or dive lot: exact
                if (leave && UbStreetSide(goal, ref place)) return null;
                if (!UfOutdoorGround(dest)) return UbNo(c, 847, "goal indoors, raised or in the water");
                RouteProfile prof = ProfileOf(sim);
                if (!prof.Ok || !(IsPointRoutable(dest) || UfNearGoal(dest, start, out place))) return UbNo(c, 840, "goal not routable");
                bool street = goal == null || goal.IsWorldLot;
                bool? gv = street ? true : UfVerdict(sim, goal, prof, dest);
                return gv == true || (gv == null && goal.IsCommunityLot) ? null : UbNo(c, 847, "goal verdict");
            }

            static string UbNo(int[] c, int slot, string why) { c[slot]++; return why; }

            static string UbLeaveWhy(Sim sim)
            {
                Lot lot = sim.LotCurrent;
                Houseboat h = UbBoatOf(lot);
                return UbCore.LeaveWhy(h != null, h != null && h.CurrentStatus == Houseboat.Status.Routing, h != null && h.GetDriver() == sim, lot == Household.ActiveHouseholdLot);
            }

            // a lot goal seen from a houseboat: the lot's street side, the spot UrRepairSim trusts; the plan then walks the lot as any visitor does, so a door
            // goal or a private lot needs no spot verdict. No street side on land: the goal's own rules below
            bool UbStreetSide(Lot goal, ref Vector3 place)
            {
                if (goal == null || goal.IsWorldLot) return false;
                Vector3 e = goal.EntryPoint();
                e.y = World.GetTerrainHeight(e.x, e.z);
                if ((World.GetTerrainType(e) & (TerrainType.WorldSea | TerrainType.WorldPond | TerrainType.LotPool | TerrainType.LotPond)) != 0 || !UfOutdoorGround(e) || !IsPointRoutable(e) || !UfGoalUnseen(e)) return false;
                place = e;
                return true;
            }

            // nothing else holds a Sim to a houseboat: occupants are read from the lot, a gangway callback only exists on a planned exit, and seasickness alarms
            // only while it moves, refused above [verified, decompiled]. Slot [853] must stay 0
            void UbCheckLeft(Sim sim, Lot from, Lot goal, Vector3 dest, Vector3 start, int[] c, int colours, string caller)
            {
                Houseboat h = UbBoatOf(from);
                if (LotManager.GetLotAtPoint(sim.Position) != from && (h == null || (h.GetDriver() != sim && !h.SeasicknessAlarms.ContainsKey(sim.SimDescription.SimDescriptionId)))) return;
                c[853]++;
                UbSample(sim, UbCore.LeaveReason(true, "left dirty"), goal, dest, start, colours, caller);
            }

            // UfCaller's walk, on through the Lot helpers of a trip. Read by UbCore.Follows; the first frame goes in the sample lines
            static bool UbFollows(out string caller)
            {
                caller = null;
                System.Collections.Generic.List<string> frames = new System.Collections.Generic.List<string>(8);
                for (int i = 1; i < 32 && frames.Count < 8; i++)
                {
                    System.Reflection.MethodBase m = new System.Diagnostics.StackFrame(i, false).GetMethod();
                    if (m == null) break;
                    string ft = m.DeclaringType != null ? m.DeclaringType.FullName : null;
                    if (frames.Count == 0 && (RouteSrcCore.IsPassThrough(ft, m.Name) || UfPreCore.OwnFrame(ft, m.Name))) continue;
                    frames.Add(ft + "." + m.Name);
                }
                if (frames.Count > 0) caller = frames[0];
                return UbCore.Follows(frames);
            }

            // a trip with followers: DoRouteWithFollowers walks them to the Sim, and the Sim must not leave them behind (docs/248)
            static bool UbAlone(Sims3.Gameplay.Interactions.InteractionInstance cur)
            {
                VisitCommunityLot visit = cur as VisitCommunityLot;
                if (visit != null) return visit.NumFollowers == 0;
                GoHome home = cur as GoHome;
                if (home != null) return home.SimFollowers == null || home.SimFollowers.Count == 0;
                GoToLot lot = cur as GoToLot;
                if (lot != null) return lot.Followers == null || lot.Followers.Count == 0;
                VisitLot visit2 = cur as VisitLot;
                if (visit2 != null)
                {
                    System.Collections.Generic.List<Sim> f = sUbVisitLotFollowers != null ? sUbVisitLotFollowers.GetValue(visit2) as System.Collections.Generic.List<Sim> : null;
                    return sUbVisitLotFollowers != null && (f == null || f.Count == 0);  // field gone: exact
                }
                Sims3.Gameplay.Abstracts.RabbitHole.IRabbitHoleFollowers hole = cur as Sims3.Gameplay.Abstracts.RabbitHole.IRabbitHoleFollowers;
                return hole == null || hole.SimFollowers == null || hole.SimFollowers.Count == 0;
            }

            static readonly System.Reflection.FieldInfo sUbVisitLotFollowers = typeof(VisitLot).GetField("mFollowers", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

            // first kUbSamples refusals per reason, as text (AppDomain ts3fixes.unseenboat.log, agent unseenboat_log). Only runs with the switch on
            const int kUbSamples = 20, kUbSampleLines = 240;
            static readonly System.Collections.Generic.Dictionary<string, int> sUbSampled = new System.Collections.Generic.Dictionary<string, int>();
            void UbSample(Sim sim, string why, Lot goal, Vector3 dest, Vector3 start, int colours, string caller)
            {
                try
                {
                    if (sUbLastWhy.Count >= 512) sUbLastWhy.Clear();
                    sUbLastWhy[sim.ObjectId.Value] = why;
                    System.Collections.Generic.List<object> log = AppDomain.CurrentDomain.GetData("ts3fixes.unseenboat.log") as System.Collections.Generic.List<object>;
                    if (log == null) { log = new System.Collections.Generic.List<object>(); AppDomain.CurrentDomain.SetData("ts3fixes.unseenboat.log", log); }
                    int n;
                    sUbSampled.TryGetValue(why, out n);
                    if (n >= kUbSamples || log.Count >= kUbSampleLines) return;
                    sUbSampled[why] = n + 1;
                    IScriptProxy f = Follower;
                    Lot lot = sim.LotCurrent;
                    log.Add(why + " | " + sim.FullName + " " + (int)(dest - start).Length() + " m, start " + UbPoint(start) + " room " + World.GetRoomId(start) + " on " + (lot != null ? lot.Name : "?")
                        + ", goal " + UbPoint(dest) + " on " + (goal != null ? goal.Name : "street") + ", components " + colours + ", follower " + (f != null && f.Target != null ? f.Target.GetType().Name : "?")
                        + ", " + sim.CurrentInteraction + ", caller " + caller);
                }
                catch (Exception) { }
            }
            static string UbPoint(Vector3 p) { return (int)p.x + "," + (int)p.y + "," + (int)p.z; }

            void UbGo(Sim sim, int trip, Lot goal, Vector3 dest, Vector3 start, Vector3 place, int[] c, int colours, string caller, bool leave)
            {
                Lot from = sim.LotCurrent;
                float dist = UbTripM(from, start, goal, dest), minutes = UbCore.HoldMinutes(dist);
                bool ghost = UrGhostStart(sim), held;
                try { held = UfHold(sim, dest, minutes); }
                finally { if (ghost) UrGhostEnd(sim); }
                if ((int)minutes > c[848]) c[848] = (int)minutes;
                if (!held || sim.HasBeenDestroyed || UfWhy(sim) != 0 || !TownieUnseen(sim, null) || !UfGoalUnseen(dest) || !UbBoatUnseen(from) || !UbBoatUnseen(goal)) { c[846]++; UbSample(sim, UbCore.LeaveReason(leave, "seen or ended in the wait"), goal, dest, start, colours, caller); return; }
                if (leave)
                {
                    string lw = UbLeaveWhy(sim);  // the wait is minutes long: the boat may have left
                    if (lw != null) { c[842]++; UbSample(sim, UbCore.LeaveReason(true, lw), goal, dest, start, colours, caller); return; }
                }
                if (trip == UbCore.kHouseboat)
                {
                    Houseboat h = goal.GetHouseboat();
                    if (h == null || h.CurrentStatus == Houseboat.Status.Routing || !h.PlaceSimAtEntrance(sim)) { c[840]++; if (leave) c[852]++; UbSample(sim, UbCore.LeaveReason(leave, "houseboat entrance"), goal, dest, start, colours, caller); return; }
                    PlaceSim(sim, sim.Position, sim.ForwardVector, "unseenboat houseboat");  // footprint off and the moved list; same spot
                    SetOption(RouteOption.EnableWaterPlanning, false);  // as Houseboat.cs after its own PlaceSimAtEntrance
                    SetOption2(RouteOption2.EnablePlanningAsBoat, false);
                    SetOption2(RouteOption2.DestinationMustBeInDeepWater, false);
                    SetOption2(RouteOption2.EndAsBoat, false);
                    c[843]++;
                }
                else
                {
                    if (!IsPointRoutable(place)) { c[840]++; if (leave) c[852]++; UbSample(sim, UbCore.LeaveReason(leave, "spot gone"), goal, dest, start, colours, caller); return; }
                    Vector3 fwd = dest - start;
                    fwd.y = 0f;
                    fwd = fwd.LengthSqr() > 0.01f ? fwd / fwd.Length() : sim.ForwardVector;
                    PlaceSim(sim, place, fwd, "unseenboat route goal");
                    if (sUrPlaced.Count >= 256) sUrPlaced.Clear();
                    sUrPlaced[sim.ObjectId.Value] = true;  // §3.9.7 (D): checked when it is seen and plans again
                }
                c[839]++;
                if (leave)
                {
                    c[851]++;
                    UbCheckLeft(sim, from, goal, dest, start, c, colours, caller);
                    Houseboat left = UbBoatOf(from);
                    UbSample(sim, UbCore.LeaveReason(true, "placed, " + (left != null ? left.CurrentStatus.ToString() : "?") + (left != null && left.GetPortGangway() == null ? ", no gangway" : "")), goal, dest, start, colours, caller);
                }
                ShortcutLog(sim.FullName + " => " + (trip == UbCore.kHouseboat ? "houseboat" : "route goal") + " (unseenboat, " + (int)dist + " m, held " + (int)minutes + " sim-min)");
            }
        }
    }
}
