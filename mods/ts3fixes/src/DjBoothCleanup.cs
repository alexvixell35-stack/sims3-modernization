using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.SimIFace;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects.PerformanceObjects;

namespace Ts3Fixes
{
    // djcleanup (tuning kDjCleanup, off by default; docs/561 §7). EA bug fix, exact for every Sim.
    //
    // DJTurntable+DoPlay.Cleanup [verified, decompiled]: `if (Target.TurnedOn) { Target.DJOwnerSim.AddExitReason(Default);
    // Target.ExitDJBooth(Target.DJOwnerSim); }` then destroys Target.TipJar. Cleanup runs for every DoPlay that leaves a
    // queue (InteractionQueue.RemoveInteraction -> CleanupInteraction), also one that never ran or failed its re-Test at
    // the head of the queue. Its owner check is missing: when Sim B's queued "Play Track" is cancelled, or fails its Test
    // because Sim A got to the booth first (Test refuses while DJOwnerSim is set), B's Cleanup ends A's set (exit reason,
    // music stopped, dancers dismissed) and destroys A's tip jar. In Bridgeport clubs NPCs pick the booth autonomously, so a
    // player Sim's queued set cancelling another Sim's set, or the reverse, is common.
    // DJTurntable+RequestMusic.Cleanup sets Target.RequestedGenre = -1 the same way: a patron's request that is cancelled in
    // the queue or fails to route clears the genre another patron requested and is waiting on (WaitForRequest), so the
    // waiting patron sees RequestedGenre != CurrentGenre after the DJ complied and boos the DJ.
    // Fix: a DoPlay whose actor is not the booth's DJOwnerSim (and would touch the booth: TurnedOn or a tip jar) skips
    // EA's Cleanup body and runs only its per-Sim part: mOverrideInteractionName = null plus the base
    // InteractionInstance.Cleanup steps, re-done member by member through reflection (Interaction<,> does not override
    // Cleanup; the base cannot be called non-virtually without Reflection.Emit, which the game's mscorlib lacks). The
    // booth's TurnedOn / DJOwnerSim / TipJar are never written, so the owner's set cannot read as off at any point. If a
    // base member is missing at Apply time the DoPlay part is not patched (logged). A RequestMusic that never routed
    // (DoRouting not passed, so Run never touched RequestedGenre) keeps the booth's RequestedGenre. The owner's own
    // Cleanup (reset mid-set) and a request that ran are unchanged.
    // Counters: [2486] other Sims' sets kept, [2487] other patrons' requests kept, [2488] errors. Check: DjBoothCleanupChecks.cs.
    public static partial class Fixes
    {
        // RequestMusic instances that passed DoRouting (so Run set or read RequestedGenre); removed in their Cleanup.
        static readonly Dictionary<object, bool> sDjRouted = new Dictionary<object, bool>();

        static readonly FieldInfo sDjName = typeof(DJTurntable.DoPlay).GetField("mOverrideInteractionName", kInstAll);
        static readonly FieldInfo sDjState = typeof(InteractionInstance).GetField("mInteractionState", kInstAll);
        static readonly FieldInfo sDjExtra = typeof(InteractionInstance).GetField("mAdditionalObjcectsToClearInUse", kInstAll);
        static readonly FieldInfo sDjVfxs = typeof(InteractionInstance).GetField("mVfxs", kInstAll);
        static readonly MethodInfo sDjLine = typeof(InteractionInstance).GetMethod("RemoveFromLine", kInstAll, null, Type.EmptyTypes, null);
        static readonly MethodInfo sDjWalk = typeof(InteractionInstance).GetMethod("CleanupWalkStyleRequests", kInstAll, null, Type.EmptyTypes, null);
        static bool sDjBaseOk;

        static void ApplyDjBoothCleanup()
        {
            sDjBaseOk = sDjName != null && sDjState != null && sDjExtra != null && sDjVfxs != null && sDjLine != null && sDjWalk != null;
            if (sDjBaseOk) Patch(typeof(DJTurntable.DoPlay), "Cleanup", typeof(DjPlayHook), "CleanupHook", "CleanupOrig");
            else AppliedLog().Add("djcleanup: InteractionInstance.Cleanup members not found, DoPlay part not patched");
            Patch(typeof(DJTurntable.RequestMusic), "DoRouting", typeof(DjRequestHook), "RouteHook", "RouteOrig");
            Patch(typeof(DJTurntable.RequestMusic), "Cleanup", typeof(DjRequestHook), "CleanupHook", "CleanupOrig");
        }

        // EA's DoPlay.Cleanup minus the booth block: mOverrideInteractionName = null, then InteractionInstance.Cleanup's
        // steps in EA's order [verified, decompiled InteractionInstance.Cleanup].
        static void DjPerSimCleanup(InteractionInstance ia)
        {
            sDjName.SetValue(ia, null);
            Sim actor = ia.InstanceActor;
            sDjLine.Invoke(ia, null);
            if (actor != null) ProgressMeter.CleanupProgressMeter(actor, ia);
            ia.EndCommodityUpdates(false);
            if (actor != null && actor.LookAtManager != null && !(ia is IImmediateInteraction) && actor.InteractionQueue.GetHeadInteraction() == ia)
                actor.LookAtManager.ClearInteractionLookAt();
            if (ia.StandardEntryCalled && actor != null && actor.SimDescription.HealthManager != null)
                actor.SimDescription.HealthManager.PossibleDirtyObjectContagion(ia.Target);
            // InteractionState is a private enum: 1 = StandardEntry
            if (Convert.ToInt32(sDjState.GetValue(ia)) == 1 && ia.Target != null && ia.Target.IsActorUsingMe(actor)) ia.StandardExit();
            List<IGameObject> extra = sDjExtra.GetValue(ia) as List<IGameObject>;
            if (extra != null)
                foreach (IGameObject o in extra)
                    if (o.IsActorUsingMe(actor)) o.RemoveFromUseList(actor);
            sDjWalk.Invoke(ia, null);
            // EA's base Cleanup disposes the effect list only while shutting down (inverted test, docs/592 S5); this copy
            // follows the cleanupvfx rule so the switch covers our copy too
            if (!CvDispose(ShortcutOn("cleanupvfx"), GameStates.IsGameShuttingDown)) return;
            List<VisualEffect> vfxs = sDjVfxs.GetValue(ia) as List<VisualEffect>;
            if (vfxs == null) return;
            foreach (VisualEffect v in vfxs) v.Dispose();
            sDjVfxs.SetValue(ia, null);
        }

        // The DoPlay decision (also used by the check): the booth belongs to another Sim's set and EA would touch it.
        internal static bool DjKeepOtherSet(bool on, bool ownerSet, bool ownerIsActor, bool turnedOn, bool hasTipJar)
        {
            return on && ownerSet && !ownerIsActor && (turnedOn || hasTipJar);
        }

        // The RequestMusic decision: this request never reached Run's RequestedGenre step, and a request is pending.
        internal static bool DjKeepRequest(bool on, bool routed, int requestedGenre)
        {
            return on && !routed && requestedGenre != -1;
        }

        public class DjPlayHook
        {
            public void CleanupHook()
            {
                if (!ShortcutOn("djcleanup")) { CleanupOrig(); return; }  // fast path: switch off
                DJTurntable.DoPlay ia = (object)this as DJTurntable.DoPlay;
                bool keep = false;
                try
                {
                    DJTurntable t = ia == null ? null : ia.Target;
                    keep = t != null && DjKeepOtherSet(true, t.DJOwnerSim != null, ReferenceEquals(t.DJOwnerSim, ia.Actor), t.TurnedOn, t.TipJar != null);
                }
                catch (Exception) { PatchCounters()[2488]++; keep = false; }
                if (!keep) { CleanupOrig(); return; }
                PatchCounters()[2486]++;
                DjPerSimCleanup(ia);  // an exception here propagates as EA's Cleanup's would
            }
            public void CleanupOrig() { } // receives DJTurntable.DoPlay.Cleanup's original body
        }

        public class DjRequestHook
        {
            public bool RouteHook()
            {
                bool r = RouteOrig();
                if (r && ShortcutOn("djcleanup")) lock (sDjRouted) sDjRouted[this] = true;
                return r;
            }
            public bool RouteOrig() { return false; } // receives DJTurntable.RequestMusic.DoRouting's original body

            public void CleanupHook()
            {
                bool routed;
                lock (sDjRouted) { routed = sDjRouted.Remove(this); }
                if (!ShortcutOn("djcleanup")) { CleanupOrig(); return; }  // fast path: switch off
                DJTurntable t = null;
                int genre = -1;
                bool keep = false;
                try
                {
                    DJTurntable.RequestMusic ia = (object)this as DJTurntable.RequestMusic;
                    t = ia == null ? null : ia.Target;
                    if (t != null) { genre = t.RequestedGenre; keep = DjKeepRequest(true, routed, genre); }
                }
                catch (Exception) { PatchCounters()[2488]++; keep = false; }
                CleanupOrig();
                if (!keep) return;
                try { t.RequestedGenre = genre; PatchCounters()[2487]++; }
                catch (Exception) { PatchCounters()[2488]++; }
            }
            public void CleanupOrig() { } // receives DJTurntable.RequestMusic.Cleanup's original body
        }
    }
}
