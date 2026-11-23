using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects.CookingObjects;

namespace Ts3Fixes
{
    // picnicexit (tuning kPicnicExit, default off; docs/645 §1). EA bug fix, no Sim is moved, skipped or changed.
    // [verified, decompiled] PicnicBasketSituation.EndPicnic sets the CleanUpPicnic state; CleanUpPicnic.Init pushes the
    // basket cleanup (or EA's AsyncCleanup) and sets Basket.Situation = null, and nothing ever calls Exit on the parent
    // PicnicSituation. PicnicBasket.Dispose only disposes through mSituation, which is null by then. So every finished picnic
    // stays in Situation.sAllSituations for the rest of the session and is saved with it (OFF runs: Isla Paradiso 19 -> 54
    // picnic situations a session, Sunset Valley 9 -> 41).
    // Fix: after EA's EndPicnic, every listed finished PicnicBasketSituation (CleanUpPicnic state, no longer the basket's) with
    // no Sim holding a role in it and no situation interaction still queued is exited with EA's own Situation.Exit (CleanUp +
    // Dispose up the chain, the same call CleanUpPicnic.Init makes itself when the basket is gone). A picnic whose cleanup
    // interaction is still queued waits for a later EndPicnic: Exit would clear that interaction's OnFailure callback.
    // Older finished picnics loaded from a save are swept the same way. Rule: PicnicExitCore.Decide.
    // Counters [4600] exited, [4601] skipped (Sims present or cleanup pending), [4609] errors (shared, docs/645).
    // Log "picnicexit: exited N finished picnic(s) ..., M skipped, K picnic situations left listed", capped.
    public static partial class Fixes
    {
        static readonly FieldInfo sPxInteractions = typeof(Situation).GetField("mInteractions", BindingFlags.NonPublic | BindingFlags.Instance);
        static int sPxMinute = -1, sPxLogged;

        static void ApplyPicnicExit()
        {
            if (sPxInteractions == null) { AppliedLog().Add("picnicexit: Situation.mInteractions not found, not patched"); return; }
            Patch(typeof(PicnicBasketSituation), "EndPicnic", typeof(PicnicExitHook), "EndPicnicHook", "EndPicnicOrig");
        }

        static void PxLog(string s)
        {
            int minute = Environment.TickCount / 60000;
            if (minute != sPxMinute) { sPxMinute = minute; sPxLogged = 0; }
            if (sPxLogged++ < 10) DiagLine("picnicexit: " + s);
        }

        public class PicnicExitHook
        {
            public void EndPicnicHook()
            {
                EndPicnicOrig();
                if (!ShortcutOn("picnicexit")) return;
                PxSweep();
            }
            public void EndPicnicOrig() { } // receives PicnicBasketSituation.EndPicnic's original body
        }

        // queued situation interactions of the cleanup state (ForceCleanUp's PicnicBasket.CleanUp)
        static int PxPending(Situation child)
        {
            List<InteractionInstance> list = sPxInteractions.GetValue(child) as List<InteractionInstance>;
            if (list == null) return 0;
            int n = 0;
            foreach (InteractionInstance ii in list)
            {
                if (ii == null) continue;
                Sim a = ii.InstanceActor as Sim;
                if (a == null || a.HasBeenDestroyed || a.InteractionQueue == null) continue;
                if (a.InteractionQueue.HasInteraction(ii)) n++;
            }
            return n;
        }

        static void PxSweep()
        {
            int[] c = PatchCounters();
            try
            {
                List<PicnicBasketSituation> finished = new List<PicnicBasketSituation>();
                int listed = 0;
                foreach (Situation s in Situation.sAllSituations)
                {
                    PicnicBasketSituation p = s as PicnicBasketSituation;
                    if (p == null) continue;
                    listed++;
                    if (p.Child is PicnicBasketSituation.CleanUpPicnic) finished.Add(p);
                }
                if (finished.Count == 0) return;
                // Sims holding a role in a finished picnic: one pass over the actors for all of them
                Dictionary<Situation, int> roles = new Dictionary<Situation, int>();
                foreach (PicnicBasketSituation p in finished) roles[p] = 0;
                foreach (Sim sim in LotManager.Actors)
                {
                    if (sim == null || sim.Autonomy == null || sim.Autonomy.SituationComponent == null) continue;
                    List<Situation> mine = sim.Autonomy.SituationComponent.Situations;
                    if (mine == null) continue;
                    for (int i = 0; i < mine.Count; i++)
                    {
                        Situation r = mine[i];
                        if (r != null && roles.ContainsKey(r)) roles[r]++;
                    }
                }
                int exited = 0, skipped = 0;
                string why = null;
                foreach (PicnicBasketSituation p in finished)
                {
                    PicnicBasketSituation.CleanUpPicnic child = p.Child as PicnicBasketSituation.CleanUpPicnic;
                    PicnicBasket basket = child != null ? child.Basket : null;
                    bool owns = basket != null && !basket.HasBeenDestroyed && basket.Situation == p;
                    int d = PicnicExitCore.Decide(p.SituationExited, child != null, owns, roles[p], child != null ? PxPending(child) : 0);
                    if (d == PicnicExitCore.kExit)
                    {
                        p.Exit();
                        exited++;
                    }
                    else if (d == PicnicExitCore.kSimsPresent || d == PicnicExitCore.kPending) { skipped++; why = PicnicExitCore.Name(d); }
                }
                c[4600] += exited;
                c[4601] += skipped;
                if (exited > 0 || skipped > 0)
                    PxLog("exited " + exited + " finished picnic(s), " + skipped + " skipped" + (why != null ? " (" + why + ")" : "")
                        + ", " + (listed - exited) + " picnic situations left listed, " + Situation.sAllSituations.Count + " situations in all");
            }
            catch (Exception ex) { c[4609]++; PxLog("error " + ex.GetType().Name); }
        }
    }
}
