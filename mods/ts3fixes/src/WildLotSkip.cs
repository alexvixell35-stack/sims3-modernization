using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.PetSystems;
using Sims3.Gameplay.Roles;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // docs/656 §8.3 wildlotskip (tuning kWildLotSkip, default off; kWildLotSkipHours 6, kWildLotSkipWalkHours 8).
    // [verified, decompiled WildHorsePool / WildHorses / NPCAnimal / Raccoon]
    //   - WildHorsePool.HerdBehaviorCallback (alarm) asks WildHorses.FindGoodLot for the lead horse whenever it stands on the
    //     street with no GoToLot queued; FindGoodLot weights every non-port lot with no reachability test. A GoToLot that
    //     fails at once (DestinationPortalLocked, NoPath) leaves the queue empty, so the next callback picks again, often the
    //     same unreachable lot.
    //   - NPCAnimal roles (raccoon, deer) pick with GetRandomLotToVisit (weights = ScoreLot, Raccoon.ScoreLot doubles lots with
    //     outdoor food); SimulateRole re-picks while the animal is on the street and not in a GoToLot.
    // [measured, routefail job lines, phase-20261115..19] GoToLot result 18 (DestinationPortalLocked), all uncut: Twinbrook 457
    // in 8 runs, Appaloosa Plains 121, Sunset Valley 50, Starlight Shores 39; Meeko the raccoon spent 15-31 sim-hours on one
    // "Go to Lot" walk.
    // This fix:
    //   - RouteFailLog.RflPlan: a failed GoToLot / GoToCommunityLot plan (DestinationPortalLocked or NoPath) of a wild horse
    //     or an NPCAnimal remembers (animal, lot), and (herd, lot) for a wild horse, for kWildLotSkipHours sim-hours;
    //   - FindGoodLot (BEp4WhHook, after wildhorselot's weights) and NPCAnimal.GetRandomLotToVisit: when EA's pick is a
    //     remembered lot, the pick is drawn again from the same weights without the remembered lots (exactly EA's
    //     distribution over the rest); every weighted lot remembered = EA's pick stays;
    //   - long-walk guard: NPCAnimal.SimulateRole and HerdBehaviorCallback look at each animal first; a GoToLot running
    //     kWildLotSkipWalkHours since we first saw it is cancelled (InteractionQueue.CancelInteraction, as a script cancel) and
    //     its lot remembered, so EA's next look picks another lot.
    // Household rule: wild horses and NPC animals live in the pet household, never the player's; still Fixes.HouseholdExempt
    // and IsPlayerSim (a pet the household adopted, a selected one, or unknown scope = no Sim selected) keep EA. A horse with no
    // Sim yet (SpawnWildHorse) keeps EA's pick.
    // Counters [4626] (animal, lot) pairs remembered, [4627] picks drawn again, [4628] every weighted lot remembered (EA's pick
    // kept), [4629] long walks cancelled, [4630] exempt or errors.
    public static partial class Fixes
    {
        static readonly WildLotSkipCore sWls = new WildLotSkipCore();
        static readonly MethodInfo sWlsScore = typeof(NPCAnimal).GetMethod("ScoreLot", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, new Type[] { typeof(Lot) }, null);
        static int sWlsMinute = -1, sWlsLogged, sWlsLines;

        static void WlsLog(string s)
        {
            int minute = System.Environment.TickCount / 60000;
            if (minute != sWlsMinute) { sWlsMinute = minute; sWlsLogged = 0; }
            if (sWlsLogged++ < 10 && sWlsLines++ < 300) DiagLine("wildlotskip: " + s);
        }

        static bool WlsExempt(Sim s) { return s == null || HouseholdExempt(s) || IsPlayerSim(s); }

        static long WlsSpan()
        {
            float h = Instantiator.kWildLotSkipHours;
            return SimClock.ConvertToTicks(h > 0f ? h : 6f, TimeUnit.Hours);
        }

        static bool WlsWild(SimDescription d, out bool horse)
        {
            horse = false;
            if (d == null || d.IsUnicorn) return false;  // a unicorn (and its summons) keeps EA's behaviour
            horse = WildHorses.IsWildHorse(d);
            return horse || d.AssignedRole is NPCAnimal;
        }

        // a unicorn on (or heading to) the lot: its Summon Wild Horses pushes GoToLot on the whole herd (OccultUnicorn)
        static bool WlsUnicornAt(Lot lot)
        {
            if (lot == null) return false;
            List<SimDescription> us = PetPoolManager.GetPetsByType(PetPoolType.Unicorn);
            if (us == null) return false;
            foreach (SimDescription u in us)
            {
                Sim c = u != null ? u.CreatedSim : null;
                if (c == null) continue;
                if (c.LotCurrent == lot) return true;
                InteractionInstance ui = c.CurrentInteraction;
                if (ui != null && ui.Target == lot) return true;
            }
            return false;
        }

        static string WlsLotName(Lot l) { return l == null ? "?" : "0x" + l.LotId.ToString("X") + " (" + l.Name + ")"; }

        static void ApplyWildLotSkip()
        {
            // FindGoodLot: no swap of its own, BEp4WhHook (EpBatch4bLogic.cs, wildhorselot) calls WlsHorsePick
            Patch(typeof(NPCAnimal), "GetRandomLotToVisit", Type.EmptyTypes, typeof(WlsAnimalHook), "PickHook", "PickOrig");
            Patch(typeof(NPCAnimal), "SimulateRole", new Type[] { typeof(float) }, typeof(WlsAnimalHook), "SimHook", "SimOrig");
            Patch(typeof(WildHorsePool), "HerdBehaviorCallback", Type.EmptyTypes, typeof(WlsHerdHook), "HerdHook", "HerdOrig");
            if (sWlsScore == null) AppliedLog().Add("wildlotskip: not found: NPCAnimal.ScoreLot (animal picks keep EA's lot)");
        }

        // remember (animal, lot) and, for a wild horse, (herd, lot); true when new
        static bool WlsNote(Sim sim, bool horse, Lot lot)
        {
            long now = SimClock.CurrentTicks, span = WlsSpan();
            bool fresh = sWls.Note(sim.SimDescription.SimDescriptionId, lot.LotId, now, span);
            if (horse) fresh |= sWls.Note(WildLotSkipCore.kHorsePool, lot.LotId, now, span);
            return fresh;
        }

        static bool WlsSkips(ulong owner, bool horse, Lot lot, long now, long span)
        {
            return sWls.Skipped(owner, lot.LotId, now, span) || (horse && sWls.Skipped(WildLotSkipCore.kHorsePool, lot.LotId, now, span));
        }

        // RouteFailLog.RflPlan, every failed plan
        static void WlsPlan(Sim sim, RoutePlanResult res)
        {
            if (res.mType != RoutePlanResultType.FailedBecauseDestinationPortalLocked && res.mType != RoutePlanResultType.FailedBecauseNoPathToDestination) return;
            if (!ShortcutOn("wildlotskip")) return;
            try
            {
                InteractionInstance ii = sim.CurrentInteraction;
                if (!(ii is GoToLot) && !(ii is GoToCommunityLot)) return;
                Lot lot = ii.Target as Lot;
                bool horse;
                if (lot == null || !WlsWild(sim.SimDescription, out horse)) return;
                if (WlsExempt(sim)) { PatchCounters()[4630]++; return; }
                if (!WlsNote(sim, horse, lot)) return;
                PatchCounters()[4626]++;
                WlsLog(sim.FullName + (horse ? " (wild horse)" : " (" + sim.SimDescription.AssignedRole.GetType().Name + ")") + ": GoToLot "
                    + WlsLotName(lot) + " " + res.mType + ", lot skipped for " + Instantiator.kWildLotSkipHours + " h");
            }
            catch (Exception ex) { PatchCounters()[4630]++; WlsLog("error at note " + ex.GetType().Name); }
        }

        // BEp4WhHook.FindHook (WildHorses.FindGoodLot): EA's / wildhorselot's pick, drawn again when remembered
        static Lot WlsHorsePick(SimDescription horse, Lot pick, bool whOn)
        {
            int[] c = PatchCounters();
            try
            {
                Sim s = horse.CreatedSim;
                if (s == null || sWls.Count == 0) return pick;
                long now = SimClock.CurrentTicks, span = WlsSpan();
                ulong id = horse.SimDescriptionId;
                if (!WlsSkips(id, true, pick, now, span)) return pick;
                if (WlsExempt(s)) { c[4630]++; return pick; }
                List<Lot> list = new List<Lot>(), fallback = new List<Lot>();
                List<float> weights = new List<float>();
                WhLists(horse, whOn, list, weights, fallback);
                Lot again = null;
                if (list.Count > 0)
                {
                    bool[] skip = new bool[list.Count];
                    for (int i = 0; i < skip.Length; i++) skip[i] = WlsSkips(id, true, list[i], now, span);
                    float[] w = weights.ToArray();
                    int k = WildLotSkipCore.Pick(w, skip, RandomUtil.GetFloat(WildLotSkipCore.Total(w, skip)));
                    if (k >= 0) again = list[k];
                }
                else
                {
                    bool[] skip = new bool[fallback.Count];
                    for (int i = 0; i < skip.Length; i++) skip[i] = WlsSkips(id, true, fallback[i], now, span);
                    int kept = WildLotSkipCore.Kept(skip);
                    int k = kept > 0 ? WildLotSkipCore.Nth(skip, RandomUtil.GetInt(0, kept - 1)) : -1;
                    if (k >= 0) again = fallback[k];
                }
                if (again == null) { c[4628]++; WlsLog(s.FullName + " (wild horse): every lot skipped, EA's pick " + WlsLotName(pick) + " kept"); return pick; }
                c[4627]++;
                WlsLog(s.FullName + " (wild horse): " + WlsLotName(pick) + " skipped, drew " + WlsLotName(again));
                return again;
            }
            catch (Exception ex) { c[4630]++; WlsLog("error at horse pick " + ex.GetType().Name); return pick; }
        }

        // NPCAnimal.GetRandomLotToVisit: EA's pick, drawn again from ScoreLot weights when remembered
        static Lot WlsAnimalPick(NPCAnimal role, Lot pick)
        {
            if (pick == null || sWls.Count == 0 || sWlsScore == null) return pick;
            int[] c = PatchCounters();
            try
            {
                Sim s = role.SimInRole;
                if (s == null) return pick;
                long now = SimClock.CurrentTicks, span = WlsSpan();
                ulong id = s.SimDescription.SimDescriptionId;
                if (!WlsSkips(id, false, pick, now, span)) return pick;
                if (WlsExempt(s)) { c[4630]++; return pick; }
                List<Lot> lots = new List<Lot>();
                List<float> weights = new List<float>();
                object[] arg = new object[1];
                foreach (Lot l in LotManager.AllLotsWithoutCommonExceptions)
                {
                    arg[0] = l;
                    weights.Add((float)sWlsScore.Invoke(role, arg));
                    lots.Add(l);
                }
                bool[] skip = new bool[lots.Count];
                for (int i = 0; i < skip.Length; i++) skip[i] = WlsSkips(id, false, lots[i], now, span);
                float[] w = weights.ToArray();
                int k = WildLotSkipCore.Pick(w, skip, RandomUtil.GetFloat(WildLotSkipCore.Total(w, skip)));
                if (k < 0) { c[4628]++; WlsLog(s.FullName + " (" + role.GetType().Name + "): every lot skipped, EA's pick " + WlsLotName(pick) + " kept"); return pick; }
                c[4627]++;
                WlsLog(s.FullName + " (" + role.GetType().Name + "): " + WlsLotName(pick) + " skipped, drew " + WlsLotName(lots[k]));
                return lots[k];
            }
            catch (Exception ex) { c[4630]++; WlsLog("error at animal pick " + ex.GetType().Name); return pick; }
        }

        // long-walk guard: one look at a wild animal from its role / herd tick (never inside a route)
        static void WlsWalkGuard(Sim s)
        {
            if (s == null) return;
            try
            {
                InteractionInstance ii = s.CurrentInteraction;
                bool walking = ii is GoToLot || ii is GoToCommunityLot;
                ulong id = s.SimDescription.SimDescriptionId;
                if (!walking) { if (sWls.Walks > 0) sWls.Walk(id, 0, 0, 0, 0); return; }
                bool horse;
                if (!WlsWild(s.SimDescription, out horse) || WlsExempt(s)) return;
                Lot lot = ii.Target as Lot;
                if (horse && WlsUnicornAt(lot)) return;  // a herd a unicorn summoned keeps EA's walk
                float h = Instantiator.kWildLotSkipWalkHours;
                long max = SimClock.ConvertToTicks(h > 0f ? h : 8f, TimeUnit.Hours), now = SimClock.CurrentTicks;
                long from = sWls.WalkFrom(id);
                if (sWls.Walk(id, ii.Id, lot != null ? lot.LotId : 0, now, max) != WildLotSkipCore.kWalkLong) return;
                if (lot != null) WlsNote(s, horse, lot);
                s.InteractionQueue.CancelInteraction(ii, false);
                PatchCounters()[4629]++;
                WlsLog(s.FullName + (horse ? " (wild horse)" : " (" + s.SimDescription.AssignedRole.GetType().Name + ")") + ": GoToLot " + WlsLotName(lot)
                    + " running " + Math.Round(SimClock.ConvertFromTicks(now - from, TimeUnit.Hours), 1) + " sim-hours, cancelled, lot skipped");
            }
            catch (Exception ex) { PatchCounters()[4630]++; WlsLog("error at walk guard " + ex.GetType().Name); }
        }

        public class WlsAnimalHook
        {
            public Lot PickHook()
            {
                Lot pick = PickOrig();
                if (pick == null || !ShortcutOn("wildlotskip")) return pick;
                return WlsAnimalPick((NPCAnimal)(object)this, pick);
            }
            public Lot PickOrig() { return null; } // NPCAnimal.GetRandomLotToVisit

            public void SimHook(float minPassed)
            {
                if (ShortcutOn("wildlotskip")) WlsWalkGuard(((NPCAnimal)(object)this).SimInRole);
                SimOrig(minPassed);
            }
            public void SimOrig(float minPassed) { } // NPCAnimal.SimulateRole
        }

        public class WlsHerdHook
        {
            public void HerdHook()
            {
                if (ShortcutOn("wildlotskip"))
                {
                    try
                    {
                        List<SimDescription> herd = PetPoolManager.GetPetsByType(PetPoolType.WildHorse);
                        if (herd != null) for (int i = 0; i < herd.Count; i++) if (herd[i] != null) WlsWalkGuard(herd[i].CreatedSim);
                    }
                    catch (Exception) { PatchCounters()[4630]++; }
                }
                HerdOrig();
            }
            public void HerdOrig() { } // WildHorsePool.HerdBehaviorCallback
        }
    }
}
