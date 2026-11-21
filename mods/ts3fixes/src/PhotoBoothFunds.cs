using System;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects.Entertainment;

namespace Ts3Fixes
{
    // photoboothfunds (tuning kPhotoBoothFunds, off by default; docs/561 §2). EA bug fix, exact for every Sim.
    //
    // PhotoBooth+TakePhoto.Run and PhotoBooth+TakePhotoWith.Run (master) call RouteToAndEnterPhotoBooth first: it adds the
    // Sim to the booth's use list, plays the enter animation and fades the Sim out (opacity 0) [verified, decompiled]. Only
    // then, on a community lot, do they test `Actor.FamilyFunds < kCostToUsePhotoBooth` and `return false` with no exit:
    // no RouteOutOfPhotoBooth (the only FadeIn), no StandardEntry (so Cleanup does not drop the use-list entry). The Test
    // checks the funds at queue time, so a family whose funds drop before the Sim reaches the booth (bills, a purchase,
    // a second photo queued by the family) gets a Sim that walks on invisible and stays listed as inside the booth.
    // Fix: when Run returns false with the Sim still in the booth's use list, EA's own PhotoBooth.Exit is pushed next
    // (RequiredNPCBehavior, as EA's RemoveSims does): the Sim fades in and walks out. Other failure paths already leave the
    // use list empty or queue Exit themselves; a queued Exit for this booth is not duplicated.
    // Counters: [2479] Sims taken out of the booth, [2480] errors. Harness check: PhotoBoothFundsChecks.cs.
    public static partial class Fixes
    {
        static void ApplyPhotoBoothFunds()
        {
            Patch(typeof(PhotoBooth.TakePhoto), "Run", typeof(PbHook), "RunHook", "RunOrig");
            Patch(typeof(PhotoBooth.TakePhotoWith), "Run", typeof(PbHook), "RunHook", "RunOrig");
        }

        // one hook class for both interactions (same Run signature); `this` is the TakePhoto / TakePhotoWith instance
        public class PbHook
        {
            public bool RunHook()
            {
                bool r = RunOrig();
                if (r || !ShortcutOn("photoboothfunds")) return r;  // fast path: success, or switch off
                try { PbAfterFail((object)this as Interaction<Sim, PhotoBooth>); } catch (Exception) { PatchCounters()[2480]++; }
                return r;
            }
            public bool RunOrig() { return false; } // receives TakePhoto.Run / TakePhotoWith.Run's original body
        }

        // The decision (also used by the check): failed run, Sim still listed inside, no Exit for this booth queued yet.
        internal static bool PbNeedsExit(bool on, bool runOk, bool stillInside, bool exitQueued)
        {
            return on && !runOk && stillInside && !exitQueued;
        }

        static bool PbExitQueued(Sim sim, PhotoBooth booth)
        {
            foreach (InteractionInstance ia in sim.InteractionQueue.InteractionList)
                if (ia is PhotoBooth.Exit && ReferenceEquals(ia.Target, booth)) return true;
            return false;
        }

        static void PbAfterFail(Interaction<Sim, PhotoBooth> ia)
        {
            if (ia == null) return;
            Sim sim = ia.Actor;
            PhotoBooth booth = ia.Target;
            if (sim == null || booth == null || sim.HasBeenDestroyed || sim.InteractionQueue == null) return;
            if (!PbNeedsExit(true, false, booth.IsActorUsingMe(sim), PbExitQueued(sim, booth))) return;
            sim.InteractionQueue.AddNext(PhotoBooth.Exit.Singleton.CreateInstance(booth, sim,
                new InteractionPriority(InteractionPriorityLevel.RequiredNPCBehavior), false, false));
            PatchCounters()[2479]++;
        }
    }
}
