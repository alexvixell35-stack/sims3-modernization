using System;
using System.Collections.Generic;
using System.Text;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // venuepicktime (tuning kVenuePickTime, off by default; logging only, nothing changes). etl13 af18-bp-31361 (11-18) had
    // one 5.5 s frame: 5444 ms in one slice of a Sim's task, from the end of its VisitCommunityLot route
    // (FollowRouteAction.PerformRoute) to Lot.MakeSimDoSomethingAfterArrivingAtVenue under VisitCommunityLot.Run
    // (VisitCommunityLotHook), route time 0.95 ms, no GC in that frame. [verified, decompiled simler90 Lot and Autonomy]
    // MakeSimDoSomethingAfterArrivingAtVenue tries the venue's one-shot motives one at a time
    // (Autonomy.FindBestActionForCommodityOnLot, search type Generic) until one gives an interaction, queues it and yields
    // once at its end. Autonomy.YieldIfPossible yields only for the Autonomy search type, so a Generic search never yields
    // and every motive search of one pick runs in the same slice. Not known from the logs: which Sim and lot, how many
    // searches ran, and whether the seconds were in the searches or in the Run body after the route. This times each pick:
    // every motive search (commodity, ms, what it found) and the time from the pick's start to its last search. A pick whose
    // searches took >= 200 ms gets one line (40 a session), every Sim; compare it with the hitch record of that frame.
    // Counters [4000] picks timed, [4001] slow picks, [4002] ms in slow picks' searches, [4003] errors.
    //
    // venuepickfast (tuning kVenuePickFast, off by default; timing only, NPCs only: a household, selected or unknown-scope
    // Sim (IsPlayerSim) keeps EA's single slice, because other tasks running between its searches could change what it picks). Why one pick can take seconds [verified,
    // decompiled simler90 Autonomy]: each motive's FindBestActionForCommodityOnLot is a full FindBestAction on the venue lot
    // (mOverriddenLocalAutonomyLot; nothing off the lot is gathered, so there is nothing to filter): every lot object pair
    // for that commodity, the actor's own and inventory maps, and the commodity map of every other Sim on the lot (socials),
    // each pair Tested and scored, with no yield inside (YieldIfPossible yields only for search type Autonomy). Each motive
    // reads a different commodity list, so the searches are not repeats; a motive that finds nothing passes to the next,
    // so a crowded venue where several motives find nothing runs several full searches in one slice [inferred; the
    // venuepicktime lines show the per-search split]. Reusing Test answers across motives is not exact (some Tests roll
    // random numbers), so the fix only spreads the work: before each motive search of a pick running in the Sim's own task
    // (where MayYield allows a Sleep) the task yields one tick. The pick's searches, their order and the queued result are
    // EA's; only other tasks may run between two searches, as they already do right after EA's own Sleep(0) at the end.
    // A search and the AddNext of its result stay in one slice. Counters [4004] yields before a search, [4005] searches
    // not yielded (not the Sim's own task, no yield context, or a player Sim), [4006] errors.
    public static partial class Fixes
    {
        sealed class VpPick
        {
            public ObjectGuid Task;
            public Sim Sim;
            public Lot Lot;
            public MetaAutonomyVenueType Venue;
            public uint T0, LastEnd, SearchUs;
            public int N;
            public StringBuilder Parts = new StringBuilder();
        }
        // open picks, one per task: with venuepickfast a pick yields, so another Sim's pick can start before it ends. A task
        // killed inside a pick never reaches VpEnd: capped
        static readonly List<VpPick> sVpPicks = new List<VpPick>();
        static int sVpLines;

        static VpPick VpOpen()
        {
            for (int i = 0; i < sVpPicks.Count; i++) if (Simulator.CurrentTask == sVpPicks[i].Task) return sVpPicks[i];
            return null;
        }

        static void ApplyVenuePickTime()
        {
            Patch(typeof(Lot), "MakeSimDoSomethingAfterArrivingAtVenue", new Type[] { typeof(MetaAutonomyVenueType), typeof(Sim) }, typeof(VenuePickHook), "PickHook", "PickOrig");
            Patch(typeof(Sims3.Gameplay.Autonomy.Autonomy), "FindBestActionForCommodityOnLot",
                new Type[] { typeof(CommodityKind), typeof(Lot), typeof(bool), typeof(AutonomySearchType), typeof(InteractionFlags) }, typeof(VenuePickHook), "FindHook", "FindOrig");
        }

        public class VenuePickHook
        {
            public void PickHook(MetaAutonomyVenueType venueType, Sim sim)
            {
                VpPick p = null;
                if (ShortcutOn("venuepicktime") || ShortcutOn("venuepickfast"))
                {
                    try
                    {
                        p = new VpPick();
                        p.Task = Simulator.CurrentTask;
                        p.Sim = sim;
                        p.Lot = (Lot)(object)this;
                        p.Venue = venueType;
                        p.T0 = SrcNowUs();
                        p.LastEnd = p.T0;
                        VpPick old = VpOpen();  // a pick nested in a pick (not seen in EA code): the inner one is timed
                        if (old != null) sVpPicks.Remove(old);
                        if (sVpPicks.Count >= 8) sVpPicks.RemoveAt(0);
                        sVpPicks.Add(p);
                    }
                    catch (Exception) { PatchCounters()[4003]++; p = null; }
                }
                try { PickOrig(venueType, sim); }
                finally { if (p != null) VpEnd(p); }
            }
            public void PickOrig(MetaAutonomyVenueType venueType, Sim sim) { } // Lot.MakeSimDoSomethingAfterArrivingAtVenue

            public InteractionInstance FindHook(CommodityKind commodity, Lot lot, bool prioritizeDistance, AutonomySearchType searchType, InteractionFlags flags)
            {
                VpPick p = sVpPicks.Count == 0 ? null : VpOpen();
                if (p == null) return FindOrig(commodity, lot, prioritizeDistance, searchType, flags);
                if (ShortcutOn("venuepickfast")) VpYield(p);
                uint t0 = SrcNowUs();
                InteractionInstance r = null;
                try { r = FindOrig(commodity, lot, prioritizeDistance, searchType, flags); return r; }
                finally { VpSearch(p, commodity, t0, r); }
            }
            public InteractionInstance FindOrig(CommodityKind commodity, Lot lot, bool prioritizeDistance, AutonomySearchType searchType, InteractionFlags flags) { return null; } // Autonomy.FindBestActionForCommodityOnLot (5 arguments)
        }

        // venuepickfast: one tick before a motive search, only in the Sim's own task where a Sleep is safe (MayYield,
        // RouteBackoff.cs); a reset during the Sleep ends the task as it would at EA's own Sleep
        static void VpYield(VpPick p)
        {
            bool own = false;
            try { own = p.Sim != null && p.Task == p.Sim.ObjectId && !IsPlayerSim(p.Sim) && MayYield(); }
            catch (Exception) { PatchCounters()[4006]++; }
            if (!own) { PatchCounters()[4005]++; return; }
            PatchCounters()[4004]++;
            uint t = SrcNowUs();
            Simulator.Sleep(0);
            p.T0 = unchecked(p.T0 + (SrcNowUs() - t));  // venuepicktime: "start to last search" leaves out the ticks spent away
            p.LastEnd = SrcNowUs();
        }

        static void VpSearch(VpPick p, CommodityKind c, uint t0, InteractionInstance r)
        {
            try
            {
                uint now = SrcNowUs();
                uint us = unchecked(now - t0);
                p.SearchUs += us;
                p.LastEnd = now;
                p.N++;
                if (p.Parts.Length < 600)
                {
                    if (p.Parts.Length > 0) p.Parts.Append("; ");
                    p.Parts.Append(c).Append(' ').Append(us / 1000).Append(" ms ");
                    p.Parts.Append(r == null ? "none" : r.GetType().Name + (r.Target != null ? " on " + r.Target.GetType().Name : ""));
                }
            }
            catch (Exception) { PatchCounters()[4003]++; }
        }

        static void VpEnd(VpPick p)
        {
            try
            {
                sVpPicks.Remove(p);
                if (!ShortcutOn("venuepicktime")) return;  // venuepickfast alone: no timing lines
                int[] c = PatchCounters();
                c[4000]++;
                uint ms = p.SearchUs / 1000;
                if (ms < 200) return;
                c[4001]++;
                c[4002] += (int)ms;
                if (sVpLines >= 40) return;
                sVpLines++;
                Sim s = p.Sim;
                string scope = IsPlayerSim(s) ? "hh" : UfWhy(s) == 0 ? "unseen" : "seen";
                // "routefail" prefix: ts3native's own line cap; the shared 400-line cap is used up early (AutonomyFailCooldown.cs header)
                DiagLine("routefail venuepicktime: " + (s != null ? s.FullName : "null") + " (" + scope + ") on '" + p.Lot.Name + "' 0x" + p.Lot.LotId.ToString("X") + " " + p.Venue
                    + ": " + p.N + " searches " + ms + " ms, start to last search " + unchecked(p.LastEnd - p.T0) / 1000 + " ms: " + p.Parts);
            }
            catch (Exception) { PatchCounters()[4003]++; }
        }
    }
}
