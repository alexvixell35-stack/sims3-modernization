using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.ActorSystems.Children;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // putdownplace (tuning kPutDownPlace, default off; docs/558 §2). NPC parent and NPC child only.
    //
    // EA path [verified, decompiled ChildUtils.cs / PutDownChild.cs / BeingCarriedPosture.cs / CarryingChildPosture.cs]:
    // ChildUtils+PutDownClass.PutDownChild tries up to seven spots (near the parent, the lot's front door, its mailbox, the
    // four lot corners), each with FindGoodLocationNearby and a route to the social jig; when every route fails it returns
    // false and nothing else happens: the child stays carried. Every interaction that needs the parent standing (posture
    // transition: CarryingChildPosture.GetStandingTransition) and every action the carried toddler picks
    // (BeingCarriedPosture+PushPutDownOnParent) queues a new PutDownChild, so a parent whose routes fail carries the child
    // for good and replays the 6-7 failed routes (with a route-failure animation) for each attempt. In the 2026-11-14
    // Bridgeport runs NPC Stella Striker carried her toddler Sebastian from day 1 on; 47 PutDownChild plans failed
    // (NoPathToDestination, 0 nodes, 0-13 ms) in 8 attempts in two runs [measured, ts3native.log]; a pregnant mom stuck
    // like this also fails every HaveBabyHospital (its posture transition is this put-down; birthroute §1).
    //
    // The fix: after EA's search failed on routes only (no cancel), the kPutDownFailsBeforePlace-th failure of the same
    // parent within 2 sim-hours puts the child down where the parent stands: EA's own in-place branch of the same method
    // (jig at the parent's position, turn to face, no route), then EA's put-down tail line for line (animation, child to the
    // jig's SimB slot, both standing). A toddler/baby whose new spot fails EA's placement test is moved by EA's
    // AttemptToPutInSafeLocation. Household, selected or unknown-scope parent or child, a social put-down (the push of a
    // social on the child), a stroller and a fire on the lot keep EA's code.
    // Counters: [2443] put-down searches failed on routes (also off), [2444] put down in place, [2445] children moved to a
    // safe spot after, [2446] declined (scope, social, stroller, fire, cancel), [2447] errors.
    public static partial class Fixes
    {
        const int kPutDownFailsBeforePlace = 2;
        static readonly long kPutDownWindowTicks = SimClock.ConvertToTicks(120f, TimeUnit.Minutes);
        sealed class PdFails { public int N; public long First; }
        static readonly Dictionary<ulong, PdFails> sPdFails = new Dictionary<ulong, PdFails>();  // parent ObjectId -> failures in the window
        static FieldInfo sPdActor, sPdTarget, sPdJig, sPdSocial;

        static void ApplyPutDownPlace()
        {
            Type t = typeof(ChildUtils.PutDownClass);
            sPdActor = t.GetField("Actor", kInstAll);
            sPdTarget = t.GetField("Target", kInstAll);
            sPdJig = t.GetField("SocialJig", kInstAll);
            sPdSocial = t.GetField("mSocialToPushToTarget", kInstAll);
            if (sPdActor == null || sPdTarget == null || sPdJig == null || sPdSocial == null) { AppliedLog().Add("putdownplace: PutDownClass fields not found, not patched"); return; }
            Patch(typeof(ChildUtils.PutDownClass), "PutDownChild", typeof(PdHook), "PutHook", "PutOrig");
        }

        // putdownplace: EA's search first; a route-only failure may become an in-place put-down
        public class PdHook
        {
            public bool PutHook()
            {
                if (PutOrig()) { PdSucceeded((object)this as ChildUtils.PutDownClass); return true; }
                try { return PdAfterFail((object)this as ChildUtils.PutDownClass); }
                catch (Exception) { PatchCounters()[2447]++; return false; }
            }
            public bool PutOrig() { return false; } // receives ChildUtils+PutDownClass.PutDownChild's original body
        }

        static void PdSucceeded(ChildUtils.PutDownClass pd)
        {
            try
            {
                Sim actor = pd != null ? sPdActor.GetValue(pd) as Sim : null;
                if (actor != null && sPdFails.Count > 0) sPdFails.Remove(actor.ObjectId.Value);
            }
            catch (Exception) { PatchCounters()[2447]++; }
        }

        // The bound (pure, also used by the check): the n-th route-only failure of one parent inside the window.
        internal static bool PutDownInPlace(bool on, bool npcPair, int failsInWindow)
        {
            return on && npcPair && failsInWindow >= kPutDownFailsBeforePlace;
        }

        static bool PdAfterFail(ChildUtils.PutDownClass pd)
        {
            int[] c = PatchCounters();
            Sim actor = sPdActor.GetValue(pd) as Sim, child = sPdTarget.GetValue(pd) as Sim;
            if (actor == null || child == null || actor.HasBeenDestroyed || child.HasBeenDestroyed) return false;
            CarryingChildPosture cc = actor.CarryingChildPosture;
            if (cc == null || cc.Child != child) return false;  // not carrying it any more: EA's false stands
            // EA clears RouteFailed before each spot and stops on any other exit reason: only a pure route failure counts
            actor.RemoveExitReason(ExitReason.RouteFailed);
            if (actor.HasExitReason(ExitReason.Default)) return false;
            c[2443]++;
            ulong id = actor.ObjectId.Value;
            long now = SimClock.CurrentTicks;
            PdFails f;
            if (!sPdFails.TryGetValue(id, out f) || now - f.First > kPutDownWindowTicks || now < f.First)
            {
                if (sPdFails.Count > 256) sPdFails.Clear();  // shortcut: a burst of distinct parents starts over
                sPdFails[id] = f = new PdFails();
                f.First = now;
            }
            f.N++;
            bool npcPair = IsNpcSim(actor) && IsNpcSim(child);
            if (!PutDownInPlace(ShortcutOn("putdownplace"), npcPair, f.N)) { if (ShortcutOn("putdownplace") && f.N >= kPutDownFailsBeforePlace) c[2446]++; return false; }
            SocialJigTwoPerson jig = sPdJig.GetValue(pd) as SocialJigTwoPerson;
            Lot lot = actor.LotCurrent;
            if (jig == null || sPdSocial.GetValue(pd) != null || cc.Stroller != null || cc.CurrentStateMachine == null || (lot != null && lot.IsFireOnLot()))
            {
                c[2446]++;
                return false;
            }
            PdPlace(actor, child, cc, jig);
            sPdFails.Remove(id);
            c[2444]++;
            DiagLine("putdownplace: " + actor.FullName + " put " + child.FullName + " down in place after " + f.N + " failed put-down searches");
            return true;
        }

        // EA's in-place branch (jig at the parent, turn to face the child's slot, the jig ignored) and EA's success tail of
        // PutDownClass.PutDownChild for a put-down without a social to push.
        static void PdPlace(Sim actor, Sim child, CarryingChildPosture cc, SocialJigTwoPerson jig)
        {
            jig.SetPosition(actor.Position);
            jig.SetForward(actor.ForwardVector);
            jig.SetOpacity(0f, 0f);
            jig.AddToWorld();
            Route turn = actor.RoutingComponent.CreateRouteTurnToFace(jig.GetSlotPosition(SocialJigTwoPerson.RoutingSlots.SimB));
            turn.AddObjectToIgnoreForRoute(jig.ObjectId);
            actor.DoRoute(turn);  // only a turn: its result does not stop the put-down
            cc.RemoveSocialBoost();
            if (child.SimDescription.Toddler)
            {
                Slots.DetachFromSlot(child.ObjectId);
                child.SetPosition(jig.GetPositionOfSlot(SocialJigTwoPerson.RoutingSlots.SimB));
                child.SetForward(jig.GetForwardOfSlot(SocialJigTwoPerson.RoutingSlots.SimB));
            }
            cc.CurrentStateMachine.SetActor("socialTemplate", jig);
            cc.CurrentStateMachine.RequestState(null, "PutDown");
            cc.CurrentStateMachine.RequestState(null, "Exit");
            cc.CurrentStateMachine.RemoveActor("socialTemplate");
            if (child.SimDescription.Baby)
            {
                child.UnParent();
                child.SetPosition(jig.GetPositionOfSlot(SocialJigTwoPerson.RoutingSlots.SimB));
                child.SetForward(jig.GetForwardOfSlot(SocialJigTwoPerson.RoutingSlots.SimB));
                if (DeepSnowEffectManager.ShouldPlayDeepSnowEffectsNow(child)) child.DeepSnowEffectManager.PlayDeepSnowEffect(false, true);
            }
            child.Posture = child.Standing;
            actor.Posture = actor.Standing;
            actor.SimRoutingComponent.EnableDynamicFootprint();
            actor.SimRoutingComponent.ForceUpdateDynamicFootprint();
            child.SimRoutingComponent.EnableDynamicFootprint();
            child.SimRoutingComponent.ForceUpdateDynamicFootprint();
            // the slot in front of the parent was not searched: a spot inside an object gets EA's own safe placement
            if (!World.TestObjectPlacement(child.ObjectId, TestObjectPlacementBooleans.None, child.Position))
            {
                child.AttemptToPutInSafeLocation(false);
                PatchCounters()[2445]++;
            }
        }
    }
}
