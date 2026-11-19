using System;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects.Island;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // docs/857 §3 houseboatgangwayrescue (default off). An NPC rides a boat home to its DOCKED houseboat and is dropped on shore
    // ~15-22 m from the gangway at a spot with no walkable path to it. SimRoutingComponent.CheckAndUpdateForHouseboats then runs
    // EnterHouseboatThroughGangway: PlanToPointFromPoint to the port gangway's RoutingSlot_0 with water and boat planning off,
    // which fails (~650 ms). GoHome.Run plans its route again (three DoRoutes, each with EA's replans) and returns false;
    // autonomy picks GoHome again, with no give-up [verified, decompiled SimRoutingComponent / GoHome / Houseboat].
    // EA's own way onto a houseboat for a Sim with no boat is Houseboat.PlaceSimAtEntrance (in RouteOntoHouseboat.Run, the
    // gangway portal's no-boat branch), then that interaction turns water / boat planning off on the Sim's current route.
    // This fix:
    //   mark: HouseboatLegMemo's HlmPost (a recorded no-path gangway leg) or HlmPre (an answered one) notes a non-exempt NPC
    //   Sim whose home houseboat is Docked (HouseboatGangwayRescueCore: per Sim, its houseboat, ms and count of failed legs).
    //   rescue: never inside the routing callback; at the safe point after GoHome.Run returned false in the Sim's own task
    //   (GoHomeHook), when GoHome's target is that houseboat's lot and it is still Docked, the Sim is still not on that lot,
    //   still not exempt, standing, not riding / carried / carrying / parented (UfHeld, as the unseen teleport), not in a tomb or
    //   hidden room, not cancelled, its running interaction is this GoHome, and it stands within kHgrNearM of the gangway:
    //   EA's PlaceSimAtEntrance(sim) plus RouteOntoHouseboat's route-option cleanup. At most one rescue per Sim per sim-hour.
    // Household rule: Fixes.HouseholdExempt, and IsPlayerSim as well since this is a teleport (HouseholdToo never lifts one,
    // docs/271); a Sim of unknown scope counts as household (IsPlayerSim).
    // Counters [4040] marked, [4041] rescued, [4042] refused, [4043] errors; refusal reasons [4044] exempt, [4045] no / another /
    // undocked houseboat, [4046] once-per-hour rule, [4047] busy (posture, held, task, cancelled, interaction), [4048] on the lot,
    // far from the gangway or a stale mark, [4049] PlaceSimAtEntrance found no spot.
    public static partial class Fixes
    {
        const float kHgrNearM = 40f;
        static readonly HouseboatGangwayRescueCore sHgr = new HouseboatGangwayRescueCore();
        static int sHgrMinute = -1, sHgrLogged;

        static void HgrLog(string s)
        {
            int minute = System.Environment.TickCount / 60000;
            if (minute != sHgrMinute) { sHgrMinute = minute; sHgrLogged = 0; }
            if (sHgrLogged++ < 10) DiagLine("houseboatgangwayrescue: " + s);
        }

        static bool HgrExempt(Sim s) { return HouseholdExempt(s) || IsPlayerSim(s); }

        static long HgrHour() { return SimClock.ConvertToTicks(1f, TimeUnit.Hours); }

        static Houseboat HgrHome(Sim s)
        {
            Lot home = s.LotHome;
            return home != null && home.IsHouseboatLot() ? home.GetHouseboat() : null;
        }

        static string HgrLotName(Houseboat hb)
        {
            Lot l = hb != null ? hb.GetHouseboatLot() : null;
            return l != null ? l.Name : "?";
        }

        static string HgrXz(Vector3 p) { return Math.Round(p.x, 1) + "," + Math.Round(p.z, 1); }

        // ", sim x,z, gangway x,z (d m)" for log lines on a gangway leg: the exact stranding spot of natural runs
        static string HgrWhere(Sim sim)
        {
            try
            {
                Vector3 p = sim.Position;
                Houseboat hb = HgrHome(sim);
                Gangway gw = hb != null ? hb.GetPortGangway() : null;
                if (gw == null) return ", sim " + HgrXz(p) + ", no home gangway";
                Vector3 g = gw.GetPositionOfSlot(Slot.RoutingSlot_0);
                return ", sim " + HgrXz(p) + ", gangway " + HgrXz(g) + " (" + Math.Round((p - g).Length(), 1) + " m, " + hb.CurrentStatus + ")";
            }
            catch (Exception ex) { return ", where error " + ex.GetType().Name; }
        }

        static void HgrRefused(int why, string name, string detail)
        {
            int[] c = PatchCounters();
            c[4042]++;
            switch (why)
            {
                case HouseboatGangwayRescueCore.kExempt: c[4044]++; break;
                case HouseboatGangwayRescueCore.kNoBoat:
                case HouseboatGangwayRescueCore.kOtherBoat:
                case HouseboatGangwayRescueCore.kNotDocked: c[4045]++; break;
                case HouseboatGangwayRescueCore.kHour: c[4046]++; break;
                case HouseboatGangwayRescueCore.kBusy: c[4047]++; break;
                default: c[4048]++; break;
            }
            if (name != null) HgrLog("refused, " + name + ": " + HouseboatGangwayRescueCore.Why(why) + (detail != null ? " (" + detail + ")" : ""));
        }

        // a gangway leg of sim that found no path (HlmPost recorded it, ms = its plan time) or was answered (HlmPre, ms 0)
        static void HgrNote(Sim sim, int ms)
        {
            int[] c = PatchCounters();
            try
            {
                if (sim == null) return;
                bool exempt = HgrExempt(sim);
                Houseboat hb = exempt ? null : HgrHome(sim);
                bool docked = hb != null && hb.CurrentStatus == Houseboat.Status.Docked;
                int w = sHgr.Note(sim.ObjectId.Value, hb != null ? hb.ObjectId.Value : 0, exempt, docked, ms, SimClock.CurrentTicks, HgrHour());
                if (w == HouseboatGangwayRescueCore.kMarked)
                {
                    c[4040]++;
                    HgrLog("marked " + sim.FullName + ", houseboat " + HgrLotName(hb) + ", gangway leg " + ms + " ms no path" + HgrWhere(sim));
                }
                else if (w != HouseboatGangwayRescueCore.kAdded) HgrRefused(w, exempt ? null : sim.FullName, null);
            }
            catch (Exception ex) { c[4043]++; HgrLog("error at mark " + ex.GetType().Name); }
        }

        // why the Sim's position is not ours to change right now, or null
        static string HgrBusy(Sim sim, Houseboat hb, InteractionInstance gh)
        {
            if (Simulator.CurrentTask != sim.ObjectId) return "not its task";
            if (sim.HasExitReason(ExitReason.Canceled) || (sim.ExitReason & (ExitReason.HigherPriorityNext | ExitReason.UserCanceled)) != 0) return "cancelled";
            if (sim.Posture != sim.Standing) return "posture " + (sim.Posture != null ? sim.Posture.GetType().Name : "null");
            if (sim.Parent != null || UfHeld(sim)) return "held";
            if (Sims3.Gameplay.Controllers.TombRoomManager.IsObjectInATombOrHiddenRoom(sim, true)) return "tomb or hidden room";
            InteractionInstance run = sim.InteractionQueue != null ? sim.InteractionQueue.RunningInteraction : null;
            if (run != gh) return "running " + (run != null ? run.GetType().Name : "nothing");
            return null;
        }

        // after GoHome.Run returned false, in the Sim's own task (GoHomeHook.HgrRun)
        static void HgrAfterGoHome(Sim sim, Lot target, InteractionInstance gh)
        {
            if (sim == null || !sHgr.IsMarked(sim.ObjectId.Value)) return;
            int[] c = PatchCounters();
            try
            {
                ulong id = sim.ObjectId.Value;
                bool exempt = HgrExempt(sim);
                Houseboat hb = !exempt && target != null && target.IsHouseboatLot() ? target.GetHouseboat() : null;
                Lot hbLot = hb != null ? hb.GetHouseboatLot() : null;
                Gangway gw = hb != null ? hb.GetPortGangway() : null;
                bool docked = hbLot != null && gw != null && hb.CurrentStatus == Houseboat.Status.Docked && hb.GetHouseboatJig() != null;
                bool onLot = hbLot != null && sim.LotCurrent != null && sim.LotCurrent.LotId == hbLot.LotId;
                bool far = docked && (sim.Position - gw.GetPositionOfSlot(Slot.RoutingSlot_0)).Length() > kHgrNearM;
                string busy = exempt || !docked ? null : HgrBusy(sim, hb, gh);
                long now = SimClock.CurrentTicks;
                HouseboatGangwayRescueCore.Mark m;
                int w = sHgr.Check(id, hb != null ? hb.ObjectId.Value : 0, exempt, docked, onLot, far, busy != null, now, HgrHour(), out m);
                if (w != HouseboatGangwayRescueCore.kOk) { HgrRefused(w, exempt ? null : sim.FullName, busy); return; }
                sHgr.Done(id, now);
                Vector3 from = sim.Position;
                bool placed = hb.PlaceSimAtEntrance(sim);  // EA's RouteOntoHouseboat, no-boat branch
                if (placed)
                {
                    Route cur = sim.RoutingComponent != null ? sim.RoutingComponent.GetCurrentRoute() : null;
                    if (cur != null)  // the cleanup RouteOntoHouseboat does after it
                    {
                        cur.SetOption(Route.RouteOption.EnableWaterPlanning, false);
                        cur.SetOption2(Route.RouteOption2.EnablePlanningAsBoat, false);
                        cur.SetOption2(Route.RouteOption2.DestinationMustBeInDeepWater, false);
                        cur.SetOption2(Route.RouteOption2.EndAsBoat, false);
                    }
                }
                // success = EA's placement found its spot. LotCurrent may follow a bare SetPosition only later [inferred: ts3agent
                // setup_sims note, 2026-11-08], so the lot the Sim reports right now is logged, not judged.
                if (placed)
                {
                    c[4041]++;
                    Vector3 p = sim.Position;
                    MovedSims()[sim.FullName] = "houseboatgangwayrescue " + hbLot.Name + " @" + Math.Round(p.x, 1) + "," + Math.Round(p.z, 1) + " " + SimClock.CurrentTime().ToString();
                    HgrLog(sim.FullName + " placed at " + hbLot.Name + ", after " + m.Ms + " ms of failed legs (" + m.Legs + " legs, from "
                        + (int)from.x + "," + (int)from.z + ", lot now " + (sim.LotCurrent != null ? sim.LotCurrent.Name : "none") + ")");
                }
                else
                {
                    c[4042]++;
                    c[4049]++;
                    HgrLog("refused, " + sim.FullName + ": PlaceSimAtEntrance found no spot, houseboat " + hbLot.Name);
                }
            }
            catch (Exception ex) { c[4043]++; HgrLog("error at rescue " + ex.GetType().Name); }
        }

        public partial class RouteBackoffHook
        {
            // the gangway leg (EnterHouseboatThroughGangway; HlmLegRoute already true): boat planning off, a Sim follower
            Sim HgrLegSim()
            {
                if (GetOption2(Sims3.SimIFace.Route.RouteOption2.EnablePlanningAsBoat)) return null;
                IScriptProxy f = Follower;
                return f != null ? f.Target as Sim : null;
            }
        }
    }
}
