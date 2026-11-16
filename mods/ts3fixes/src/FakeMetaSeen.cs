using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Services;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // fakemetaseen (contributor, default off; townies by construction): EA's FakeMetaAutonomy service spawns pool Sims
    // onto random community lots between kStartTime and kEndTime, at most kMaxNumAssignedSims at once (EA 1; a tuning mod in
    // the user's Mods sets 3). FindAssignment [ld-SGS 348902] lists every non-world community lot with a rabbit hole that offers
    // an IMetaRabbitHoleInteraction, or a park venue, picks one at random and calls Service.FindAssignmentOnLot, which takes a
    // pool Sim (or makes a new one), instantiates it hidden at the lot's NPC start point (a full Sim build) and starts
    // FakeMetaAutonomySituation: route to the rabbit hole (30 sim minutes inside, then leave) or hang out 1.5-2.5 h, then go
    // to a lot to disappear into; every route failure exits at once, and the Sim is destroyed. The next one spawns at the next
    // Simulate. Pure visuals, almost always on a lot nobody sees (jj1 outfitcensus: 186 instantiations).
    // With the switch: the same hour / max-assigned gate (the tuned fields read by reflection), the same lot list, then only
    // the lots that are seen (UfLotSeen: the camera's lot, the active household's lot, or the lot circle reaching into the
    // view or within the 80 m household viewer rule) or that hold an active household or selected Sim; EA's random pick among
    // those and EA's FindAssignmentOnLot. No seen lot: false, as EA returns when it has no lot (nothing is spawned).
    // Off, not live, travelling, world going away, or the reflection lookups failing: EA's own body (FindOrig).
    // Scope: no Sim is touched here; FindAssignmentOnLot takes Sims from this service's own pool (service NPCs); a lot with an
    // active household or selected Sim on it always gets EA's behaviour.
    // Cost: a community lot the household walks onto has no new fake-autonomy Sim until the next Simulate of this service
    // after it is seen (Services ticks one service per 30 ticks, round robin); the ones already assigned keep EA's chain.
    // Counters: [3720] assignments asked (hour and max gate passed; this switch or fakemetabackoff), [3721] allowed (a lot
    // picked), [3722] skipped (lots exist, none seen, none held), [3723] fell back to EA's body (a reflection lookup or read failed).
    // Cleanup: no loop of our own beyond EA's lot scan, no retry; EA's FindAssignmentOnLot is called directly (the hook class
    // derives from Service for that), so its exceptions reach Services.Simulate as from EA's own call. Unlike EA's scan, a
    // rabbit hole with a null Interactions list (or a null pair) is skipped instead of throwing.
    //
    // fakemetabackoff (contributor, default off; alone or with fakemetaseen): EA has no backoff for a lot whose trip fails.
    // FakeMetaAutonomySituation's RouteToLot / RouteToRabbitHole / Leave call Exit() on any route failure, the Sim is destroyed,
    // and the next Simulate spawns the next one, possibly onto the same lot (jj1 BP: 101 of 185 dead within 30 sim minutes;
    // 27 VisitCommunityLot plans on one big park failed with FailedBecauseNoDestinationSpecified). With fakemetaseen the list
    // is often one seen lot, so a failing lot would rebuild a Sim on every Simulate. A spawned Sim's object id, lot and start
    // tick are kept; at kSimDestroyed (VehicleLeaks.OnVlSimDestroyed) a Sim that lived under kFmbQuickMin sim minutes holds
    // its lot out of the list for kFmbHoldMin sim minutes. A successful visit lives at least 30 sim minutes (EnterAndExitRabbitHole's
    // alarm) or 1.5 h (HangAroundAtLot), so a shorter life is a failed or cancelled trip. A lot with an active household or
    // selected Sim on it is never held out. Static, keyed by ids, cleared per world load; nothing is persisted.
    // Counters: [3724] spawns tracked, [3725] quick deaths (lot held), [3726] lot skips by a hold, [3727] picks left with no
    // lot by holds (nothing spawned).
    public static partial class Fixes
    {
        static void ApplyFakeMetaSeen()
        {
            Type t = typeof(Service).Assembly.GetType("Sims3.Gameplay.Services.FakeMetaAutonomy");
            if (t == null) { AppliedLog().Add("FakeMetaAutonomy: not found, fakemetaseen off"); return; }
            Patch(t, "FindAssignment", new Type[] { typeof(ServiceSituation).MakeByRefType(), typeof(Lot).MakeByRefType(), typeof(Sim).MakeByRefType() },
                typeof(FakeMetaFindHook), "FindHook", "FindOrig");
        }

        // Derives from Service only so the swapped body can call the protected FindAssignmentOnLot directly (plain IL on the
        // FakeMetaAutonomy instance, no reflection byref copy-back); never instantiated, so the overrides below never run.
        public class FakeMetaFindHook : Service
        {
            public bool FindHook(ref ServiceSituation situation, ref Lot assignedLot, ref Sim createdSim)
            {
                Lot pick = null;
                bool seen = ShortcutOn("fakemetaseen"), back = ShortcutOn("fakemetabackoff");
                int r = seen || back ? FmsPick(this, seen, back, out pick) : -1;
                if (r < 0) return FindOrig(ref situation, ref assignedLot, ref createdSim);
                if (r == 0) return false;
                bool ok = FindAssignmentOnLot(pick, ref situation, ref assignedLot, ref createdSim);  // EA's own last step
                if (ok && back) FmbNote(createdSim, pick);
                return ok;
            }
            public bool FindOrig(ref ServiceSituation situation, ref Lot assignedLot, ref Sim createdSim) { return false; } // receives FakeMetaAutonomy.FindAssignment's original header

            protected override ServiceTuning Tuning { get { return null; } }
            public override ServiceType ServiceType { get { return ServiceType.None; } }
            protected override bool NeedsAssignment(Lot lot) { return false; }
            protected override ServiceSituation InternalCreateSituation(Lot assignedLot, Sim createdSim, int cost, ObjectGuid requestingSim) { return null; }
            protected override void SetServiceNPCProperties(SimDescription simDescription) { }
            protected override void SetTraits(SimDescription simDescription) { }
            public override string GetServiceTopic(Sim serviceSim) { return null; }
        }

        static FieldInfo sFmsStart, sFmsEnd, sFmsMax;
        static bool sFmsLooked;

        static bool FmsInit()
        {
            if (!sFmsLooked)
            {
                sFmsLooked = true;
                const BindingFlags st = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                Type t = typeof(Service).Assembly.GetType("Sims3.Gameplay.Services.FakeMetaAutonomy");
                if (t != null)
                {
                    sFmsStart = t.GetField("kStartTime", st);
                    sFmsEnd = t.GetField("kEndTime", st);
                    sFmsMax = t.GetField("kMaxNumAssignedSims", st);
                }
            }
            return sFmsStart != null && sFmsEnd != null && sFmsMax != null
                && sFmsStart.FieldType == typeof(int) && sFmsEnd.FieldType == typeof(int) && sFmsMax.FieldType == typeof(int);
        }

        // -1 = run EA's body; 0 = return false (nothing spawned); 1 = call FindAssignmentOnLot(pick)
        static int FmsPick(Service svc, bool seen, bool back, out Lot pick)
        {
            pick = null;
            if (svc == null || !GameStates.IsLiveState || GameStates.IsTravelling || WorldGone()) return -1;
            int[] c = PatchCounters();
            int start, end, max;
            try
            {
                if (!FmsInit()) { c[3723]++; return -1; }
                start = (int)sFmsStart.GetValue(null);
                end = (int)sFmsEnd.GetValue(null);
                max = (int)sFmsMax.GetValue(null);
            }
            catch (ResetException) { throw; }
            catch (Exception) { c[3723]++; return -1; }
            int h = SimClock.Hours24;
            if (!(h >= start && h <= end && svc.NumAssignedSims < max)) return 0;  // EA's own gate: false, not counted
            c[3720]++;
            List<Lot> list = new List<Lot>();
            int eaLots = 0, held = 0;
            long now = SimClock.CurrentTicks;
            foreach (Lot lot in LotManager.AllLots)
            {
                if (lot == null || !FmsEaLot(lot)) continue;
                eaLots++;
                bool hh = FmsHhOn(lot);
                if (!hh && back && FmbHeld(lot, now)) { held++; c[3726]++; continue; }
                if (hh || !seen || UfLotSeen(lot)) list.Add(lot);
            }
            if (list.Count == 0) { if (held > 0) c[3727]++; else if (eaLots > 0 && seen) c[3722]++; return 0; }
            c[3721]++;
            pick = RandomUtil.GetRandomObjectFromList(list);
            return 1;
        }

        // fakemetabackoff state: spawned Sim object id -> lot and start tick; lot id -> tick the hold ends
        sealed class FmbRec { public ulong Lot; public long T0; }
        static readonly Dictionary<ulong, FmbRec> sFmbLive = new Dictionary<ulong, FmbRec>();
        static readonly Dictionary<ulong, long> sFmbHold = new Dictionary<ulong, long>();
        const float kFmbQuickMin = 25f, kFmbHoldMin = 180f;

        static void FmbNote(Sim s, Lot lot)
        {
            if (s == null || lot == null) return;
            if (sFmbLive.Count >= 64) sFmbLive.Clear();  // shortcut: a Sim never destroyed would leak an entry; 64 is far past kMaxNumAssignedSims
            sFmbLive[s.ObjectId.Value] = new FmbRec { Lot = lot.LotId, T0 = SimClock.CurrentTicks };
            PatchCounters()[3724]++;
        }

        // from VehicleLeaks.OnVlSimDestroyed (kSimDestroyed, every world)
        static void FmbGone(Sim s)
        {
            FmbRec r;
            if (!sFmbLive.TryGetValue(s.ObjectId.Value, out r)) return;
            sFmbLive.Remove(s.ObjectId.Value);
            if (!GameStates.IsLiveState || WorldGone()) return;
            long now = SimClock.CurrentTicks;
            if (now - r.T0 >= SimClock.ConvertToTicks(kFmbQuickMin, TimeUnit.Minutes)) return;
            sFmbHold[r.Lot] = now + SimClock.ConvertToTicks(kFmbHoldMin, TimeUnit.Minutes);
            PatchCounters()[3725]++;
        }

        static bool FmbHeld(Lot lot, long now)
        {
            long until;
            if (!sFmbHold.TryGetValue(lot.LotId, out until)) return false;
            if (now < until) return true;
            sFmbHold.Remove(lot.LotId);
            return false;
        }

        static void FmsWorldLoad() { sFmbLive.Clear(); sFmbHold.Clear(); }

        // EA's lot test in FakeMetaAutonomy.FindAssignment, same order
        static bool FmsEaLot(Lot lot)
        {
            if (lot.IsWorldLot || !lot.IsCommunityLot) return false;
            foreach (RabbitHole rh in lot.GetObjects<RabbitHole>())
            {
                if (rh == null || rh.Interactions == null) continue;
                foreach (InteractionObjectPair p in rh.Interactions)
                    if (p != null && p.InteractionDefinition is IMetaRabbitHoleInteraction) return true;
            }
            return lot.IsParkVenueType;
        }

        static bool FmsHhOn(Lot lot)
        {
            Sim sel = PlumbBob.SelectedActor;
            if (sel != null && sel.LotCurrent == lot) return true;
            Household hh = Household.ActiveHousehold;
            if (hh == null) return false;
            foreach (Sim s in hh.AllActors) if (s != null && s.LotCurrent == lot) return true;
            return false;
        }
    }
}
