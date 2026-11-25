using System;
using System.Collections.Generic;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // docs/202 §5.7 E (both switches off by default).
    //   unseenfbloopdet   (logging only): every Sim-followed plan notes (Sim, interaction definition + target, instance, plan
    //                     failed). The same definition on the same target restarted UfLoopDetector.kLoopRestarts times after a
    //                     failed plan inside 20 sim-minutes is a loop: one "ufloop:" line in ts3native.log with the Sim's last
    //                     guard action (cut, teleport, C4 "not free") and whether it is unseen. Any Sim, any arm: compares on/off.
    //   unseenfbloopbreak (needs unseenfbloopdet): a flagged Sim that is unseen (UfWhy == 0) gets, spread over ticks: (1) motives
    //                     maxed and timed negative moodlets removed; (2) when it stands on its own home lot or a community lot
    //                     that passes UfLoopBreakCore.LotFix (not in view, not owned by the active household, no household
    //                     member there or heading there; re-checked before every step): fires out (the game's own
    //                     Fire.ExtinguishFire, as firefighters and unicorns end a fire), items the game would throw away
    //                     (IThrowAwayable.ShouldBeThrownAway: trash piles, dirty dishes, spoiled food), dirty objects cleaned,
    //                     broken objects repaired, puddles removed; at most kObjectsPerTick steps per tick. Sims on fire are
    //                     never touched. One "ufloopbreak:" line per Sim fix and per lot fix.
    // Counters: [348] loops flagged, [349] Sim fixes, [350] lot fixes finished, [351] lot fixes stopped (scope failed mid-way),
    // [352] lot steps done (objects + puddles), [353] breaks refused (Sim seen at fix time).
    public static partial class Fixes
    {
        static readonly UfLoopDetector sUfLoops = new UfLoopDetector(Sims3.Gameplay.Utilities.SimClock.ConvertToTicks(20f, Sims3.Gameplay.Utilities.TimeUnit.Minutes));
        static readonly Dictionary<ulong, string> sUfLastGuard = new Dictionary<ulong, string>();
        static int sUfLoopLogged;
        const int kUfLoopLogMax = 200;

        // guard actions on a Sim (cut, teleport, C4 answer), named on its next loop line
        static void UfGuardNote(ulong sim, string what)
        {
            if (!ShortcutOn("unseenfbloopdet")) return;
            if (sUfLastGuard.Count > 1024) sUfLastGuard.Clear();
            sUfLastGuard[sim] = what;
        }

        static void UfLoopNote(IScriptProxy follower, RoutePlanResult r)
        {
            if (!ShortcutOn("unseenfbloopdet")) return;
            Sim sim = follower != null ? follower.Target as Sim : null;
            if (sim == null || sim.SimDescription == null) return;
            InteractionInstance cur = sim.CurrentInteraction;
            if (cur == null || cur.InteractionDefinition == null) return;
            ulong id = sim.ObjectId.Value;
            string key = cur.InteractionDefinition.GetType().FullName + "@" + (cur.Target != null ? cur.Target.ObjectId.Value.ToString("X") : "0");
            if (!sUfLoops.Note(id, key, cur, r.mType != RoutePlanResultType.Succeeded, Sims3.Gameplay.Utilities.SimClock.CurrentTicks)) return;
            PatchCounters()[348]++;
            int why = UfWhy(sim);
            string guard;
            if (!sUfLastGuard.TryGetValue(id, out guard)) guard = "none";
            if (sUfLoopLogged++ < kUfLoopLogMax)
                DiagLine("ufloop: " + sim.FullName + " 0x" + id.ToString("X") + " restarts " + key + " (" + UfLoopDetector.kLoopRestarts
                    + " after failed plans in 20 sim-min), why " + why + ", last guard action " + guard);
            if (ShortcutOn("unseenfbloopbreak")) UfBreakQueue(sim, key);
        }

        // ---- breaker ----
        sealed class UfBreak { public Sim Sim; public string Key; }
        static readonly Queue<UfBreak> sUfBreaks = new Queue<UfBreak>();
        static readonly UfTickBudget sUfBreakBudget = new UfTickBudget(UfLoopBreakCore.kObjectsPerTick);
        static ObjectGuid sUfBreakTask = ObjectGuid.InvalidObjectGuid;

        static void UfBreakQueue(Sim sim, string key)
        {
            if (sUfBreaks.Count >= 32) return;  // shortcut: a storm of loops waits for the next flag
            UfBreak b = new UfBreak();
            b.Sim = sim;
            b.Key = key;
            sUfBreaks.Enqueue(b);
            if (!(sUfBreakTask.IsValid && Simulator.GetTask(sUfBreakTask) != null))
                sUfBreakTask = Simulator.AddObject(new LoopTask(UfBreakDrain));
        }

        static bool UfLive() { return GameStates.IsLiveState && !GameStates.IsTravelling && !GameStates.IsWorldShuttingDown; }

        // live mode but paused or the clock stopped (docs/205 §2.3b, SimFrozen.cs): wait, do not act and do not drop
        static bool UfFrozen() { return UfLive() && SimFrozenOrNotLive(); }

        static void UfBreakDrain()
        {
            try
            {
                while (sUfBreaks.Count > 0)
                {
                    if (UfFrozen()) { Simulator.Sleep(0); continue; }  // paused / clock stopped: keep the queue; out of live mode UfBreakOne drops it
                    UfBreak b = sUfBreaks.Dequeue();
                    try { UfBreakOne(b); } catch (Exception) { }
                    Simulator.Sleep(0);  // one Sim per tick
                }
            }
            finally { sUfBreakTask = ObjectGuid.InvalidObjectGuid; }
        }

        static void UfBreakOne(UfBreak b)
        {
            int[] c = PatchCounters();
            Sim sim = b.Sim;
            if (sim == null || sim.HasBeenDestroyed || !UfLoopBreakCore.SimFix(UfLive(), UfWhy(sim))) { c[353]++; return; }
            // (1) Sim side
            if (sim.Motives != null) sim.Motives.MaxEverything();
            int removed = 0;
            BuffManager bm = sim.BuffManager;
            if (bm != null && !bm.HasElement(BuffNames.OnFire))
            {
                List<BuffNames> neg = new List<BuffNames>();
                foreach (BuffInstance bi in bm.Buffs)
                    if (bi != null && bi.EffectValue < 0 && bi.TimeoutCount > 0f && bi.TimeoutCount < float.MaxValue) neg.Add(bi.Guid);  // timed only: permanent states stay
                foreach (BuffNames g in neg) { bm.RemoveElement(g); removed++; }
            }
            c[349]++;
            DiagLine("ufloopbreak: " + sim.FullName + " after " + b.Key + ": motives maxed, " + removed + " negative moodlets removed");
            // (2) lot side
            Lot lot = sim.LotCurrent;
            if (!UfLotFixOk(sim, lot)) return;
            List<GameObject> work = new List<GameObject>();
            foreach (GameObject o in lot.GetObjects<GameObject>()) if (o != null && !(o is Sim)) work.Add(o);
            LotLocation[] puddles = World.GetPuddles(lot.LotId, LotLocation.Invalid);
            int fires = 0, trash = 0, cleaned = 0, repaired = 0, pud = 0, i = 0, p = 0;
            bool stopped = false;
            while (i < work.Count || (puddles != null && p < puddles.Length))
            {
                if (UfFrozen() || !sUfBreakBudget.Try(Sims3.Gameplay.Utilities.SimClock.CurrentTicks)) { Simulator.Sleep(0); continue; }
                if (!UfLotFixOk(sim, lot)) { stopped = true; break; }
                c[352]++;
                if (i < work.Count)
                {
                    GameObject o = work[i++];
                    if (o.HasBeenDestroyed || !o.InWorld || o.InUse || (o.ItemComp != null && o.ItemComp.InventoryParent != null)) continue;
                    Fire f = o as Fire;
                    if (f != null) { f.ExtinguishFire(); fires++; continue; }
                    IThrowAwayable t = o as IThrowAwayable;
                    if (t != null && t.ShouldBeThrownAway()) { t.ThrowAwayImmediately(); trash++; continue; }
                    if (o.IsCleanable && o.Cleanable.NeedsToBeCleaned) { o.Cleanable.ForceClean(); cleaned++; }
                    if (o.IsRepairable && o.Repairable.Broken) { o.Repairable.ForceRepaired(null); repaired++; }
                }
                else { Sims3.Gameplay.Controllers.PuddleManager.RemovePuddle(lot.LotId, puddles[p++]); pud++; }
            }
            c[stopped ? 351 : 350]++;
            DiagLine("ufloopbreak: lot " + lot.Name + " (0x" + lot.LotId.ToString("X") + ") for " + sim.FullName + (stopped ? " stopped (scope)" : "")
                + ": fires " + fires + ", thrown away " + trash + ", cleaned " + cleaned + ", repaired " + repaired + ", puddles " + pud);
        }

        // Rodney 2026-10-27: never a lot the active household owns or has a member on / heading to; only unseen lots
        static bool UfLotFixOk(Sim sim, Lot lot)
        {
            if (lot == null) return false;
            Household hh = Household.ActiveHousehold;
            bool owned = hh != null && (lot == hh.LotHome || lot.Household == hh || (hh.RealEstateManager != null && hh.RealEstateManager.FindProperty(lot) != null));
            bool there = false;
            if (hh != null)
                foreach (Sim m in hh.AllActors)
                {
                    if (m == null) continue;
                    InteractionInstance mi = m.CurrentInteraction;
                    GameObject tgt = mi != null ? mi.Target as GameObject : null;
                    // shortcut: "heading there" = the current interaction's target is on the lot; queued trips are not seen
                    if (m.LotCurrent == lot || (tgt != null && tgt.LotCurrent == lot)) { there = true; break; }
                }
            return UfLoopBreakCore.LotFix(UfLive(), lot.IsWorldLot, lot == sim.LotHome || lot.IsCommunityLot, UfLotSeen(lot), owned, there);
        }
    }
}
