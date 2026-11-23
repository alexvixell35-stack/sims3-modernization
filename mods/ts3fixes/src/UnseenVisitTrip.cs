using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects.Vehicles;
using Sims3.Gameplay.Routing;
using Sims3.Gameplay.Situations;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Unseen visit trips (run phase-20261115-2043: unseen townies crossing town on foot for hours, ~800 m in 13 sim-hours).
    //
    // Root: VisitCommunityLot.Run, for an autonomous visit whose front-door route did not run (no front door, e.g. a park,
    // or that route failed), creates the lot route and, with RandomUtil.RandomChance(Lot.ChanceOfGoingToCommunityLotByCar)
    // (Lot.kChanceOfGoingToCommunityLotByCar = 50) false, turns EnablePlanningAsCar off: half of those visits walk however
    // far the lot is. The same flip is in GoToCommunityLot, VisitLotWithDate and RabbitHole; only the visit is changed here.
    // [verified, decompiled]
    //
    // visitcar (default off): an autonomous visit by an NPC that is unseen at Run start (TownieUnseen: not household or
    // selected, standing, not riding, off screen and far from every viewer) to a lot farther than EA's own driving distance
    // (Car.GetWorldDrivingMinimumDistance(): kDistanceToDestinationSoSimWillDrive 30 m, per-world variants; Car sets it as
    // the planner's Route.SetMinimumDistanceForCarTravel) reads the chance as 100 once, in that Run's task: the flip keeps
    // driving. That is one of the two outcomes EA's flip already gives, so no new state; the planner still decides whether
    // a car is used (distance, world rules such as Egypt, hoverboards, horses). Household, selected, unknown-scope and
    // seen-at-start Sims read EA's value. Counters: [2722] long autonomous NPC visits judged, [2723] flips that kept driving
    // (the getter answered 100), [2724] left to EA's flip (household / selected / seen at start).
    //
    // visitwalkfinish (default off, backstop for walks already underway): every 10 sim-minutes, an NPC whose visit Run
    // started kVwfMinutes or more ago and is still walking to the lot (routing, standing, not on the target lot) finishes at
    // the goal when it is unseen and the goal is out of every viewer's reach: EA's own route interrupt
    // (SimRoutingComponent.RequestRouteInterruptAction, soft stop, as the toddler walk-fall uses) runs our callback in the
    // Sim's task, which checks again, places the Sim on a verified safe spot of the lot (SafeSpot, as the visit shortcut) and
    // ends the route (Terminate). Run then either plans again from the lot (front-door route branch) or returns false, and
    // the hook runs Run's own arrival step (MakeSimDoSomethingAfterArrivingAtVenue when the visit looks for actions on
    // arrival, the grouping situation) and returns true. Refused at the callback (now seen, no safe spot): the walk goes on
    // (the interrupt clones the follow action, EA's normal resume). Same exemptions as the visit shortcut (ShortcutToLot):
    //   - EP10 resort lots: Run sends resort visitors through the resort tower / check-in path, and diving lots need the boat
    //     or mermaid water route (EnablePlanningAsBoat); a placement would skip that bookkeeping;
    //   - households with pets, and any Sim in a grouping situation: EA may bring pets, a horse or household members as
    //     followers (GetFollowers, NpcShouldRideHorse); moving the leader would leave them across town;
    //   - moving houseboats, apartment shells (BuildableShells), the active household's lot, world lots.
    // Counters: [2725] interrupts asked, [2726] walks finished at the goal, [2727] refused at the callback, [2728] errors.
    public static partial class Fixes
    {
        const float kVwfMinutes = 60f, kVwfPassMinutes = 10f;

        // tasks whose visit Run may read the driving chance as 100 once (visitcar)
        static readonly Dictionary<ulong, bool> sVcTasks = new Dictionary<ulong, bool>();

        class VwfTrip { public Sim Sim; public Lot Lot; public long Start; public bool Asked, Finished; }
        static readonly Dictionary<ulong, VwfTrip> sVwfTrips = new Dictionary<ulong, VwfTrip>();  // by the Sim's ObjectId
        static AlarmHandle sVwfAlarm = AlarmHandle.kInvalidHandle;
        static AlarmManager sVwfAlarmOwner;
        static FieldInfo sVwfLookField;

        static void ApplyUnseenVisitTrip()
        {
            Patch(typeof(Lot), "get_ChanceOfGoingToCommunityLotByCar", Type.EmptyTypes, typeof(VisitCarHook), "ChanceHook", "ChanceOrig");
        }

        public class VisitCarHook
        {
            public static float ChanceHook()
            {
                float v = ChanceOrig();
                if (sVcTasks.Count == 0 || !sVcTasks.Remove(Simulator.CurrentTask.Value)) return v;
                if (v < 100f) PatchCounters()[2723]++;
                return 100f;
            }
            public static float ChanceOrig() { return 0f; } // receives Lot.get_ChanceOfGoingToCommunityLotByCar's original header
        }

        // VisitCommunityLotHook.RunHook, before RunOrig. Returns the backstop's trip record (null: not tracked).
        static VwfTrip VisitTripBegin(Sim actor, Lot target, bool autonomous)
        {
            if (actor == null || target == null || target.IsWorldLot || actor.LotCurrent == target) return null;
            bool car = autonomous && ShortcutOn("visitcar"), walk = ShortcutOn("visitwalkfinish");
            if (!car && !walk) return null;
            try
            {
                if (car && (actor.Position - target.Position).Length() > Car.GetWorldDrivingMinimumDistance())
                {
                    int[] c = PatchCounters();
                    c[2722]++;
                    if (TownieUnseen(actor, null)) sVcTasks[Simulator.CurrentTask.Value] = true;  // TownieUnseen: never household, selected or unknown scope
                    else c[2724]++;
                }
                if (!walk || IsPlayerSim(actor)) return null;  // household rule (PlayerSims.cs)
                VwfTrip t = new VwfTrip { Sim = actor, Lot = target, Start = SimClock.CurrentTicks };
                if (sVwfTrips.Count >= 1024) sVwfTrips.Clear();  // bounded; a cleared walker is simply not backstopped
                sVwfTrips[actor.ObjectId.Value] = t;
                VwfEnsureAlarm();
                return t;
            }
            catch (Exception) { PatchCounters()[2728]++; return null; }
        }

        // After RunOrig (in a finally): forget the flag and the trip
        static void VisitTripEnd(Sim actor, VwfTrip t)
        {
            if (sVcTasks.Count > 0) sVcTasks.Remove(Simulator.CurrentTask.Value);
            if (t == null || actor == null) return;
            VwfTrip cur;
            if (sVwfTrips.TryGetValue(actor.ObjectId.Value, out cur) && ReferenceEquals(cur, t)) sVwfTrips.Remove(actor.ObjectId.Value);
        }

        // Run's private mLookForAutonomousActionsUponArrival (true when it cannot be read: EA's default)
        static bool VwfLooks(object visit)
        {
            try
            {
                if (sVwfLookField == null)
                    sVwfLookField = typeof(VisitCommunityLot).GetField("mLookForAutonomousActionsUponArrival", BindingFlags.Instance | BindingFlags.NonPublic);
                return sVwfLookField == null || (bool)sVwfLookField.GetValue(visit);
            }
            catch (Exception) { return true; }
        }

        static void VwfEnsureAlarm()
        {
            AlarmManager g = AlarmManager.Global;
            if (g == null || (sVwfAlarm != AlarmHandle.kInvalidHandle && ReferenceEquals(sVwfAlarmOwner, g))) return;
            sVwfTrips.Clear();  // a new world's alarm manager: the old world's trips are gone
            sVwfAlarmOwner = g;
            sVwfAlarm = g.AddAlarmRepeating(kVwfPassMinutes, TimeUnit.Minutes, delegate { VwfPass(); }, kVwfPassMinutes, TimeUnit.Minutes,
                "Ts3Fixes.VisitWalkFinish", AlarmType.NeverPersisted, null);
        }

        // The visit shortcut's lot and Sim exemptions (ShortcutToLot), for a walk already underway
        static bool VwfLotOk(Sim s, Lot lot)
        {
            if (lot.IsWorldLot || s.LotCurrent == lot || !HouseboatOk(lot) || lot.BuildableShells.Length > 0 || lot == Household.ActiveHouseholdLot) return false;
            if (lot.CommercialLotSubType == CommercialLotSubType.kEP10_Resort || lot.CommercialLotSubType == CommercialLotSubType.kEP10_Diving) return false;
            if (s.Household != null && s.Household.Pets.Count > 0) return false;
            return s.GetSituationOfType<GroupingSituation>() == null;
        }

        // VwfLotOk's refusal as a docs/661 reason code (only asked after VwfLotOk said no)
        static int VwfLotWhy(Sim s, Lot lot)
        {
            bool other = lot.IsWorldLot || s.LotCurrent == lot || !HouseboatOk(lot) || lot.BuildableShells.Length > 0 || lot == Household.ActiveHouseholdLot;
            bool rd = lot.CommercialLotSubType == CommercialLotSubType.kEP10_Resort || lot.CommercialLotSubType == CommercialLotSubType.kEP10_Diving;
            bool pets = s.Household != null && s.Household.Pets.Count > 0;
            int r = LotGameplayCore.VwfLotReason(other, rd, pets, s.GetSituationOfType<GroupingSituation>() != null);
            return r < 0 ? LotGameplayCore.kVwfLotOther : r;
        }

        static void VwfPass()
        {
            try
            {
                if (sVwfTrips.Count == 0) return;
                if (!ShortcutOn("visitwalkfinish")) { sVwfTrips.Clear(); return; }
                if (WorldGone() || !GameStates.IsLiveState || GameStates.IsTravelling) return;
                long now = SimClock.CurrentTicks, due = SimClock.ConvertToTicks(kVwfMinutes, TimeUnit.Minutes);
                int[] c = PatchCounters();
                List<ulong> gone = null;
                foreach (KeyValuePair<ulong, VwfTrip> kv in sVwfTrips)
                {
                    VwfTrip t = kv.Value;
                    Sim s = t.Sim;
                    if (s == null || s.HasBeenDestroyed || !s.InWorld || t.Lot == null || t.Lot.HasBeenDestroyed)
                    {
                        if (gone == null) gone = new List<ulong>();
                        gone.Add(kv.Key);
                        continue;
                    }
                    if (t.Finished || now - t.Start < due || now < t.Start) continue;
                    // docs/661 §4: why a walker past the mark is not interrupted, one count per pass and walker (slots 4220-4229)
                    c[LotGameplayCore.kVwfBase + LotGameplayCore.kVwfDue]++;
                    if (!s.IsRouting) { c[LotGameplayCore.kVwfBase + LotGameplayCore.kVwfNotRouting]++; continue; }
                    if (t.Asked) { c[LotGameplayCore.kVwfBase + LotGameplayCore.kVwfAskedStill]++; continue; }
                    if (!VwfLotOk(s, t.Lot)) { c[LotGameplayCore.kVwfBase + VwfLotWhy(s, t.Lot)]++; continue; }
                    if (!TownieUnseen(s, null)) { c[LotGameplayCore.kVwfBase + LotGameplayCore.kVwfSeen]++; continue; }
                    if (!FarFromViewers(t.Lot.Position)) { c[LotGameplayCore.kVwfBase + LotGameplayCore.kVwfNearViewers]++; continue; }
                    t.Asked = true;  // one interrupt per Run
                    c[2725]++;
                    VwfTrip trip = t;
                    s.SimRoutingComponent.RequestRouteInterruptAction(delegate(Sim sim, Route path) { return VwfInterrupt(sim, trip); }, StopMode.kStop_Soft);
                }
                if (gone != null) { c[LotGameplayCore.kVwfBase + LotGameplayCore.kVwfDropped] += gone.Count; foreach (ulong id in gone) sVwfTrips.Remove(id); }
            }
            catch (Exception) { PatchCounters()[2728]++; }
        }

        // In the Sim's task (EA's route interrupt action): true = the walk goes on, false = the route ends (placed at the goal)
        static bool VwfInterrupt(Sim sim, VwfTrip t)
        {
            int[] c = PatchCounters();
            try
            {
                VwfTrip cur;
                if (sim == null || sim.HasBeenDestroyed || !ShortcutOn("visitwalkfinish") || !sVwfTrips.TryGetValue(sim.ObjectId.Value, out cur) || !ReferenceEquals(cur, t)
                    || !VwfLotOk(sim, t.Lot) || !TownieUnseen(sim, null) || !FarFromViewers(t.Lot.Position)) { c[2727]++; return true; }
                Vector3 pos, fwd;
                if (!SafeSpot(sim, t.Lot, t.Lot.EntryPoint(), out pos, out fwd, "visitwalkfinish", true)) { c[2727]++; return true; }
                string from = sim.LotCurrent != null ? sim.LotCurrent.Name : "street";
                PlaceSim(sim, pos, fwd, "visitwalkfinish " + t.Lot.Name);
                t.Finished = true;
                c[2726]++;
                ShortcutLog(sim.FullName + " => " + t.Lot.Name + " (visitwalkfinish from " + from + " after "
                    + Math.Round(SimClock.ConvertFromTicks(SimClock.CurrentTicks - t.Start, TimeUnit.Minutes)) + " sim-min)");
                return false;
            }
            catch (Exception) { c[2728]++; return true; }
        }
    }
}
