using System;
using System.Collections.Generic;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects.Plumbing;
using Sims3.Gameplay.Pools;
using Sims3.Gameplay.Socializing;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using Sims3.SimIFace.CAS;

namespace Ts3Fixes
{
    // Partners shower together, and a Sim with nowhere to pee takes a shower (docs/686 §15). A player's wish; clipping
    // is fine. Both parts default off; rules in ShowerShareCore.cs.
    //
    // [verified, decompiled] Shower.TakeShower.Run (showers and shower tubs, IShowerable): the shower's line
    // (SimQueue.WaitForTurn), the walk to RoutingSlot_0 with "in use -> give up" (RouteToSlotAndCheckInUse, which asks
    // the line once more), a ShowerPrivacySituation, StandardEntry (use list), its own "Shower" state machine (actors x
    // and Shower), the loop, then WaitToLeaveShower: EnableFootprintAndPushSims(824351308, actor) and a sleep loop while
    // IsRoutingSlotObstructed(actor, RoutingSlot_0), then the exit animation and StandardExit. The privacy situation
    // already lets a Sim in a romantic relationship stay (Sim.CanIgnoreNormalPrivacySituationsWith), and the game's
    // own WooHoo in a shower puts a second Sim on the same slot. AllInOneBathroom.UseAllInOneBathroom: line, the same
    // in-use walk, no privacy situation, the Sim fades out inside; it fills hygiene and the bladder.
    //
    // showertogether / showertogetherhh
    //   A Sim that starts a shower (or an all-in-one bathroom use) where exactly one other Sim showers, both young
    //   adults or older, in a romantic relationship (the privacy test's own rule) and not disliking each other:
    //     - the line is skipped (sharelanes' LdsQueueHook.WaitHook) and the two become a pair for that object;
    //     - the in-use refusal after the walk passes while every Sim using the object is of the pair
    //       (SimRoutingComponent.RouteToSlotAndCheckInUse, the game's three steps with that one exemption);
    //     - each runs the game's own interaction with its own state machine on the same slot (they overlap);
    //     - at the exit the partner is not pushed off the slot (GameObject.PushSimsFromFootprint), and the wait for a
    //       free slot is ours for a pair (GameObject.IsRoutingSlotObstructed): a Sim waits while its partner plays its
    //       exit, so they come out one at a time, and does not wait for a partner that is still showering.
    //   The all-in-one bathroom has no exit wait: both exits may overlap there.
    //
    // showerpee / showerpeehh
    //   [verified, decompiled] Motive.MotiveDistress(Bladder) queues Sim.BladderFailure when the bladder reaches its
    //   minimum. BuffReallyHasToPee is the last warning before it.
    //   A human Sim, child or older, on a lot with no toilet, urinal or all-in-one bathroom it could use now (none,
    //   broken, used or queued for by others) and a shower it could use (free, or a partner's per showertogether):
    //     - at the last warning (unless the player has given it orders) and at the failure itself, the game's own Take
    //       Shower is queued instead; the failure is held back while that shower is queued or runs;
    //     - any shower such a Sim starts while it has to pee refills its bladder motive: a motive delta on the shower from its first
    //       animation (the all-in-one bathroom's five sim-minutes), and the bladder is full when the Sim steps out.
    //   No new interaction and no menu entry: the player sees a shower. A shower that does not begin within 30
    //   sim-minutes gives the failure back to the game, and that Sim is not sent again for an hour.
    //
    // Scope: the plain switch = Sims the player does not control, hh = household and selected Sims too (both Sims of a
    // pair must be in scope). Off = compare mode: counted, nothing done. Counters 4494-4509. Check: ShowerShareCheck.
    public static partial class Fixes
    {
        const int kShsJoins = 4494, kShsWould = 4495, kShsExempt = 4496, kShsKeptRomance = 4497, kShsKeptAge = 4498, kShsKeptOther = 4499,
            kShsExitWaits = 4500, kShsErrors = 4501,
            kShpPeed = 4502, kShpPushed = 4503, kShpAvoided = 4504, kShpNoShower = 4505, kShpWould = 4506, kShpExempt = 4507, kShpFellBack = 4508,
            kShsAioJoins = 4509;
        const uint kShsExitFootprint = 824351308u;  // the shower's exit footprint (Shower.WaitToLeaveShower)
        const int kShsShower = 1, kShsAio = 2;
        const float kShsStepAwayMinutes = 2f;  // after a partner left: time for it to step off the slot before the next walk
        const int kShpPending = 0, kShpFilling = 1, kShpFailed = 2;
        const float kShpCoolMinutes = 60f;

        sealed class ShsPair { public GameObject Obj; public int Kind; public Sim A, B; public int StateA, StateB; public long ExitStart; public bool Waited; }
        sealed class ShpRun { public Sim Sim; public InteractionInstance Inst; public GameObject Target; public long Start; public int State; public bool Pushed; }

        static readonly Dictionary<ulong, ShsPair> sShs = new Dictionary<ulong, ShsPair>();  // pairs, by shower
        static readonly Dictionary<ulong, ShpRun> sShp = new Dictionary<ulong, ShpRun>();    // showers that fill a bladder, by Sim
        static int sShsArmed, sShpArmed;  // the dictionaries' counts, read by the hot hooks
        static readonly LineCapCore sShsCap = new LineCapCore(30, 60000, 600);
        static readonly string[] kShsSites = { "line", "walk", "leave", "exit wait", "set actor", "distress", "last warning", "push" };
        static readonly int[] sShsSiteErrors = new int[8];
        static string sShsMissing = "", sShpMissing = "", sShsLast = "", sShsLastError = "";

        static void ApplyShowerShare()
        {
            string join = "", pee = "";
            if (!Patch(typeof(SimRoutingComponent), "RouteToSlotAndCheckInUse", new Type[] { typeof(IGameObject), typeof(Slot), typeof(bool) }, typeof(ShsRouteHook), "InUseHook", "InUseOrig"))
                join += " SimRoutingComponent.RouteToSlotAndCheckInUse";
            if (!Patch(typeof(GameObject), "PushSimsFromFootprint", new Type[] { typeof(uint), typeof(Sim), typeof(List<Sim>), typeof(bool), typeof(float), typeof(bool) }, typeof(ShsPushHook), "PushHook", "PushOrig"))
                join += " GameObject.PushSimsFromFootprint";
            if (!Patch(typeof(GameObject), "IsRoutingSlotObstructed",
                new Type[] { typeof(Sim), typeof(Slot), typeof(GameObject), typeof(bool), typeof(Route.RouteOption), typeof(CASAgeGenderFlags), typeof(bool), typeof(BlockedStatus).MakeByRefType() },
                typeof(ShsPushHook), "ObstructedHook", "ObstructedOrig"))
                join += " GameObject.IsRoutingSlotObstructed";
            if (!Patch(typeof(Motive), "MotiveDistress", new Type[] { typeof(Sim), typeof(CommodityKind) }, typeof(ShpDistressHook), "DistressHook", "DistressOrig")) pee += " Motive.MotiveDistress";
            if (!Patch(typeof(BuffReallyHasToPee), "OnAddition", new Type[] { typeof(BuffManager), typeof(BuffInstance), typeof(bool) }, typeof(ShpBuffHook), "AddHook", "AddOrig")) pee += " BuffReallyHasToPee.OnAddition";
            sShsMissing = join.Trim();
            sShpMissing = pee.Trim();
            if (sShsMissing.Length + sShpMissing.Length > 0) AppliedLog().Add("showershare: not found: " + (sShsMissing + " " + sShpMissing).Trim());
        }

        static void ShsError(int site, Exception e)
        {
            PatchCounters()[kShsErrors]++;
            sShsLastError = "showershare: error at " + kShsSites[site] + ": " + e.GetType().Name + ": " + e.Message;
            if (sShsSiteErrors[site]++ < 3) DiagLine(sShsLastError);
        }

        static void ShsActLine(string switchName, string text)
        {
            sShsLast = switchName + ": " + text;
            if (ShortcutOn("showersharelog") && sShsCap.Take(System.Environment.TickCount)) DiagLine(sShsLast);
        }

        // ---- showertogether ---------------------------------------------------------------------------------------------

        // The game's Take Shower at a shower or shower tub, or the all-in-one bathroom's use. 0 = something else.
        static int ShsKind(InteractionInstance ia, out GameObject obj)
        {
            obj = null;
            if (ia is Shower.TakeShower)
            {
                if (ia.Target is IShowerable) obj = ia.Target as GameObject;
                return obj == null ? 0 : kShsShower;
            }
            AllInOneBathroom aio = ia.Target as AllInOneBathroom;
            if (aio == null || ia.GetType().Name != "UseAllInOneBathroom") return 0;
            obj = aio;
            return kShsAio;
        }

        static bool ShsPlain(Sim s) { return s.IsHuman && s.SimDescription != null && !s.SimDescription.IsRobot; }

        // A Sim of a pair is there while its running interaction is at the pair's object.
        static bool ShsActive(Sim s, GameObject obj)
        {
            if (s == null || s.HasBeenDestroyed) return false;
            InteractionInstance cur = s.CurrentInteraction;
            return cur != null && object.ReferenceEquals(cur.Target, obj);
        }

        static bool ShsLive(ShsPair p) { return ShsActive(p.A, p.Obj) && ShsActive(p.B, p.Obj); }

        static void ShsDrop(ShsPair p)
        {
            ShsPair now;
            ulong id = p.Obj.ObjectId.Value;
            if (sShs.TryGetValue(id, out now) && object.ReferenceEquals(now, p)) sShs.Remove(id);
            sShsArmed = sShs.Count;
        }

        // Pairs whose Sims have gone on to something else (also what a world quit left behind).
        static void ShsPrune()
        {
            if (sShs.Count == 0) return;
            List<ShsPair> dead = null;
            foreach (ShsPair p in sShs.Values)
                if (!ShsLive(p)) { if (dead == null) dead = new List<ShsPair>(); dead.Add(p); }
            if (dead != null)
                foreach (ShsPair p in dead) ShsDrop(p);
        }

        // The pair of this object when the Sim is one of its two.
        static ShsPair ShsMember(Sim s, IGameObject obj)
        {
            ShsPair p;
            if (s == null || obj == null || !sShs.TryGetValue(obj.ObjectId.Value, out p)) return null;
            return p.A == s || p.B == s ? p : null;
        }

        // Other Sims that use the object or stand in its line. other = the first one found.
        static int ShsOthers(Sim a, GameObject obj, out Sim other)
        {
            other = null;
            int n = 0;
            List<Sim> users = obj.ActorsUsingMe;
            if (users != null)
                foreach (Sim u in users)
                    if (u != null && u != a) { if (other == null) other = u; n++; }
            SimQueue line = obj.SimLine;
            List<Sim> inLine = line == null ? null : line.SimsInQueue;  // null while nobody is in the line
            if (inLine != null)
                foreach (Sim u in inLine)
                    if (u != null && u != a && (users == null || !users.Contains(u))) { if (other == null) other = u; n++; }
            return n;
        }

        static int ShsDecide(Sim a, GameObject obj, int kind, out Sim other)
        {
            int n = ShsOthers(a, obj, out other);
            if (n == 0) return ShowerShareCore.kNone;
            bool hhOn = ShortcutOn("showertogetherhh");
            bool single = n == 1 && !other.HasBeenDestroyed && other.SimDescription != null && a.SimDescription != null;
            bool showering = false, adult = false, romantic = false, willing = false;
            if (single)
            {
                InteractionInstance cur = other.CurrentInteraction;
                GameObject t;
                Shower.TakeShower ts = cur as Shower.TakeShower;
                showering = cur != null && ShsKind(cur, out t) == kind && object.ReferenceEquals(t, obj) && (ts == null || !ts.HavingWooHoo) && ShsPlain(other);
                adult = a.SimDescription.YoungAdultOrAbove && other.SimDescription.YoungAdultOrAbove;
                romantic = a.IsInRomanticRelationshipWith(other);
                Relationship r = romantic ? a.GetRelationship(other, false) : null;
                willing = r != null && r.LTR != null && ShowerShareCore.Willing(r.LTR.Liking);
            }
            return ShowerShareCore.Join(ShortcutOn("showertogether"), n, showering, ShsPlain(a), adult, romantic, willing, ShlScope(a, hhOn), single && ShlScope(other, hhOn));
        }

        // SimQueue.WaitForTurn (LdsQueueHook.WaitHook) for a shower's or an all-in-one bathroom's own line. asks = a real
        // wait (the game's second, never-wait question before the walk only reads a pair). True = no line for this Sim.
        static bool ShsLine(InteractionInstance ia, SimQueue line, bool asks)
        {
            try
            {
                GameObject obj;
                int kind = ShsKind(ia, out obj);
                if (kind == 0) return false;
                Sim a = ia.InstanceActor;
                if (a == null || !object.ReferenceEquals(obj.SimLine, line)) return false;
                if (asks && kind == kShsShower) ShpNote(a, ia, obj);
                if (sShsMissing.Length > 0) return false;
                int[] c = PatchCounters();
                if (asks) ShsPrune();
                ShsPair p;
                if (sShs.TryGetValue(obj.ObjectId.Value, out p))
                {
                    if (p.A == a || p.B == a) return true;
                    if (asks) c[kShsKeptOther]++;  // a third Sim keeps the line
                    return false;
                }
                if (!asks) return false;
                Sim other;
                int d = ShsDecide(a, obj, kind, out other);
                if (d == ShowerShareCore.kWould) c[kShsWould]++;
                else if (d == ShowerShareCore.kExempt) c[kShsExempt]++;
                else if (d == ShowerShareCore.kKeepRomance) c[kShsKeptRomance]++;
                else if (d == ShowerShareCore.kKeepAge) c[kShsKeptAge]++;
                else if (d == ShowerShareCore.kKeepOther) c[kShsKeptOther]++;
                if (d != ShowerShareCore.kGo) return false;
                if (sShs.Count > 64) sShs.Clear();
                p = new ShsPair();
                p.Obj = obj; p.Kind = kind; p.A = other; p.B = a;
                sShs[obj.ObjectId.Value] = p;
                sShsArmed = sShs.Count;
                c[kind == kShsAio ? kShsAioJoins : kShsJoins]++;
                ShsActLine("showertogether", a.FullName + " " + ShlWho(a) + ", " + SwfIa(a) + ": joins " + other.FullName + " " + ShlWho(other) + " in " + obj.GetType().Name + " at " + V(obj.Position));
                return true;
            }
            catch (ResetException) { throw; }
            catch (Exception e) { ShsError(0, e); return false; }
        }

        // The in-use test after the walk: true = the Sim is of the object's pair and so is every Sim using it.
        static bool ShsUseOk(Sim s, IGameObject destObj)
        {
            try
            {
                ShsPair p = ShsMember(s, destObj);
                if (p == null) return false;
                List<Sim> users = p.Obj.ActorsUsingMe;
                int ours = 0;
                foreach (Sim u in users) if (u == p.A || u == p.B) ours++;
                return ShowerShareCore.UseOk(users.Count, ours);
            }
            catch (ResetException) { throw; }
            catch (Exception e) { ShsError(1, e); return false; }
        }

        // The walk to the shower failed for a Sim of a pair. A partner that leaves shuts the shower's exit footprint over
        // the slot, and the Sim skipped the line that would have held it until the partner is out (run sh-m1, 2026-11-22:
        // the joining Sim's Take Shower ended with a route fail). The Sim waits here until the partner no longer uses the
        // shower (cap kExitCapMinutes). True = the partner is out and only the failed walk stands against the order:
        // that exit reason is taken off and the caller walks again.
        static bool ShsWaitPartnerOut(Sim s, IGameObject destObj)
        {
            try
            {
                ShsPair p = ShsMember(s, destObj);
                if (p == null) return false;
                Sim other = p.A == s ? p.B : p.A;
                const ExitReason kOthers = ExitReason.Default & ~ExitReason.RouteFailed;
                long start = SimClock.CurrentTicks;
                while (other != null && !other.HasBeenDestroyed && p.Obj.IsActorUsingMe(other))
                {
                    if (s.HasExitReason(kOthers) || OjsMinutes(start) >= ShowerShareCore.kExitCapMinutes) return false;
                    s.LoopIdle();
                    Simulator.Sleep(10u);
                }
                long clear = SimClock.CurrentTicks;  // the partner steps off the slot
                while (OjsMinutes(clear) < kShsStepAwayMinutes)
                {
                    if (s.HasExitReason(kOthers)) return false;
                    s.LoopIdle();
                    Simulator.Sleep(10u);
                }
                if (s.HasExitReason(kOthers)) return false;
                s.RemoveExitReason(ExitReason.RouteFailed);
                ShsActLine("showertogether", s.FullName + " " + ShlWho(s) + ": the walk failed while " + (other != null ? other.FullName : "the partner") + " left " + p.Obj.GetType().Name
                    + ", waited " + OjsMinutes(start).ToString("0.0") + " sim-min, walks again");
                return true;
            }
            catch (ResetException) { throw; }
            catch (Exception e) { ShsError(1, e); return false; }
        }

        // An all-in-one bathroom shuts a footprint while a Sim is inside. A partner walking to it has it opened (its own
        // run shuts it again); shut = put it back after a walk that failed.
        static bool ShsAioDoor(Sim s, IGameObject destObj, bool shut)
        {
            try
            {
                ShsPair p = ShsMember(s, destObj);
                if (p == null || p.Kind != kShsAio || p.Obj.UseCount == 0) return false;
                ((AllInOneBathroom)p.Obj).ToggleFootprint(shut);
                return true;
            }
            catch (ResetException) { throw; }
            catch (Exception e) { ShsError(1, e); return false; }
        }

        // GameObject.PushSimsFromFootprint for a shower's exit footprint: the Sim is about to leave. Its partner is not
        // pushed away. A shower that filled the Sim's bladder ends here (showerpee).
        static List<Sim> ShsLeaving(GameObject obj, Sim actor, List<Sim> exceptions)
        {
            try
            {
                if (sShpArmed > 0) ShpLeave(actor, obj);
                ShsPair p = ShsMember(actor, obj);
                if (p == null) return exceptions;
                bool isA = p.A == actor;
                Sim other = isA ? p.B : p.A;
                if (!ShsActive(other, obj)) { ShsDrop(p); return exceptions; }  // the partner has left: the game's own exit
                if (isA) { if (p.StateA == ShowerShareCore.kJoined) p.StateA = ShowerShareCore.kLeaving; }
                else if (p.StateB == ShowerShareCore.kJoined) p.StateB = ShowerShareCore.kLeaving;
                if (exceptions == null) exceptions = new List<Sim>();
                if (!exceptions.Contains(other)) exceptions.Add(other);
            }
            catch (ResetException) { throw; }
            catch (Exception e) { ShsError(2, e); }
            return exceptions;
        }

        // GameObject.IsRoutingSlotObstructed(sim, RoutingSlot_0) from the game's wait before the exit, for a Sim of a pair
        // that is leaving. 0 = the game's test, 1 = wait (the partner plays its exit), 2 = go.
        static int ShsExitGate(GameObject obj, Sim s)
        {
            try
            {
                ShsPair p = ShsMember(s, obj);
                if (p == null) return 0;
                bool isA = p.A == s;
                int mine = isA ? p.StateA : p.StateB, others = isA ? p.StateB : p.StateA;
                if (mine == ShowerShareCore.kJoined) return 0;
                Sim other = isA ? p.B : p.A;
                bool using_ = other != null && !other.HasBeenDestroyed && obj.IsActorUsingMe(other);
                if (mine == ShowerShareCore.kLeaving && ShowerShareCore.ExitWait(others, using_, OjsMinutes(p.ExitStart)))
                {
                    if (!p.Waited) { p.Waited = true; PatchCounters()[kShsExitWaits]++; }
                    return 1;
                }
                if (mine != ShowerShareCore.kExiting)
                {
                    if (isA) p.StateA = ShowerShareCore.kExiting; else p.StateB = ShowerShareCore.kExiting;
                    p.ExitStart = SimClock.CurrentTicks;
                }
                return 2;
            }
            catch (ResetException) { throw; }
            catch (Exception e) { ShsError(3, e); return 0; }
        }

        // `this` is the game's SimRoutingComponent. With the switch off the game's body runs. With it on, the game's three
        // steps run here, so that a pair formed during the walk still counts at the in-use test after it.
        public class ShsRouteHook : SimRoutingComponent
        {
            public bool InUseHook(IGameObject destObj, Slot slotName, bool ignorePetFaceCheck)
            {
                if (destObj == null || !ShortcutOn("showertogether")) return InUseOrig(destObj, slotName, ignorePetFaceCheck);
                if (!PreRouteCheckForLine(destObj)) return false;
                bool opened = sShsArmed > 0 && ShsAioDoor(OwnerSim, destObj, false);
                if (!RouteToSlot(destObj, slotName, ignorePetFaceCheck))
                {
                    // the partner was on its way out: wait as the line would have, then walk again (the Sim that just
                    // left may still stand on the slot for a moment: up to three walks)
                    bool there = false;
                    for (int tries = 0; tries < 3 && !there && sShsArmed > 0 && ShsWaitPartnerOut(OwnerSim, destObj); tries++)
                        there = RouteToSlot(destObj, slotName, ignorePetFaceCheck);
                    if (!there)
                    {
                        if (opened) ShsAioDoor(OwnerSim, destObj, true);
                        return false;
                    }
                }
                if (destObj.InUse && !(sShsArmed > 0 && ShsUseOk(OwnerSim, destObj)))
                {
                    OwnerSim.AddExitReason(ExitReason.ObjectInUse);
                    return false;
                }
                return true;
            }
            public bool InUseOrig(IGameObject destObj, Slot slotName, bool ignorePetFaceCheck) { return false; }  // receives RouteToSlotAndCheckInUse's original body
        }

        // `this` is the game's object (a shower when a pair or a bladder shower is open for it).
        public abstract class ShsPushHook : GameObject
        {
            public bool PushHook(uint footprintNameHash, Sim actor, List<Sim> exceptions, bool waitForPushes, float footprintExtension, bool bGangwayPush)
            {
                if ((sShsArmed > 0 || sShpArmed > 0) && footprintNameHash == kShsExitFootprint && actor != null) exceptions = ShsLeaving(this, actor, exceptions);
                return PushOrig(footprintNameHash, actor, exceptions, waitForPushes, footprintExtension, bGangwayPush);
            }
            public bool PushOrig(uint footprintNameHash, Sim actor, List<Sim> exceptions, bool waitForPushes, float footprintExtension, bool bGangwayPush) { return false; }  // receives PushSimsFromFootprint's original body

            public bool ObstructedHook(Sim s, Slot slotName, GameObject objectToIgnore, bool checkForSims, Route.RouteOption routeOptions, CASAgeGenderFlags agsFlags, bool isExitSlot, out BlockedStatus blockedStatus)
            {
                if (sShsArmed > 0 && s != null && slotName == Slot.RoutingSlot_0)
                {
                    int gate = ShsExitGate(this, s);
                    if (gate == 1) { blockedStatus = BlockedStatus.BlockedBySim; return true; }
                    if (gate == 2) { blockedStatus = BlockedStatus.Unobstructed; return false; }
                }
                // blockeduse / woohoosnap (BlockedUse.cs, WooHooSnap.cs): a Sim that was let onto a closed slot of this object
                if (sBuExits.Count != 0 && s != null && !isExitSlot && BuLetOn(this, s)) { blockedStatus = BlockedStatus.Unobstructed; return false; }
                return ObstructedOrig(s, slotName, objectToIgnore, checkForSims, routeOptions, agsFlags, isExitSlot, out blockedStatus);
            }
            public bool ObstructedOrig(Sim s, Slot slotName, GameObject objectToIgnore, bool checkForSims, Route.RouteOption routeOptions, CASAgeGenderFlags agsFlags, bool isExitSlot, out BlockedStatus blockedStatus)
            {
                blockedStatus = BlockedStatus.Unobstructed;  // receives IsRoutingSlotObstructed's original body
                return false;
            }
        }

        // ---- showerpee --------------------------------------------------------------------------------------------------

        static bool ShpSimOk(Sim s)
        {
            return s != null && !s.HasBeenDestroyed && s.IsHuman && s.SimDescription != null && s.SimDescription.ChildOrAbove && !s.SimDescription.IsRobot
                && !(s.Posture is SwimmingInPool) && s.LotCurrent != null && !s.LotCurrent.IsWorldLot && s.InteractionQueue != null && s.Motives != null;
        }

        static bool ShpLow(Sim s)
        {
            BuffManager bm = s.BuffManager;
            return bm != null && (bm.HasElement(BuffNames.HasToPee) || bm.HasElement(BuffNames.ReallyHasToPee));
        }

        static bool ShpBroken(GameObject o) { return o.Repairable != null && o.Repairable.Broken; }

        static int ShpOthersInLine(Sim s, GameObject o)
        {
            SimQueue line = o.SimLine;
            List<Sim> inLine = line == null ? null : line.SimsInQueue;
            if (inLine == null) return 0;
            int n = 0;
            foreach (Sim u in inLine) if (u != null && u != s) n++;
            return n;
        }

        static bool ShpToiletUsable(Sim s, GameObject o)
        {
            bool usedByOther = o.UseCount > 0 && !o.IsActorUsingMe(s);
            return ShowerShareCore.ToiletUsable(ShpBroken(o), usedByOther, ShpOthersInLine(s, o), !(o is Urinal) || !s.IsFemale);
        }

        // The lot's toilets and showers for this Sim. shower = the nearest one it could use (null: none), found only
        // when no toilet is usable.
        static bool ShpToiletOnLot(Sim s, out GameObject shower)
        {
            shower = null;
            float best = float.MaxValue;
            foreach (GameObject o in s.LotCurrent.GetObjects<GameObject>())
            {
                if (o == null || o.HasBeenDestroyed) continue;
                if (o is IToiletOrUrinal || o is AllInOneBathroom)
                {
                    if (ShpToiletUsable(s, o)) { shower = null; return true; }
                    continue;
                }
                if (!(o is IShowerable)) continue;
                ShowerTub tub = o as ShowerTub;
                if (tub != null && tub.IsCatPlaying()) continue;
                Sim other;
                int n = ShsOthers(s, o, out other);
                int join = n == 0 ? ShowerShareCore.kNone : sShsMissing.Length > 0 ? ShowerShareCore.kKeepOther : ShsDecide(s, o, kShsShower, out other);
                if (!ShowerShareCore.ShowerUsable(ShpBroken(o), n, join)) continue;
                float d = (o.Position - s.Position).LengthSqr();
                if (d < best) { best = d; shower = o; }
            }
            return false;
        }

        static void ShpDrop(ShpRun r)
        {
            ShpRun now;
            ulong id = r.Sim.ObjectId.Value;
            if (sShp.TryGetValue(id, out now) && object.ReferenceEquals(now, r)) sShp.Remove(id);
            sShpArmed = sShp.Count;
        }

        static ShpRun ShpAdd(Sim s, InteractionInstance inst, GameObject target, bool pushed)
        {
            if (sShp.Count > 64) sShp.Clear();  // entries leave at the shower's end; this bounds what a world quit left behind
            ShpRun r = new ShpRun();
            r.Sim = s; r.Inst = inst; r.Target = target; r.Start = SimClock.CurrentTicks; r.State = kShpPending; r.Pushed = pushed;
            sShp[s.ObjectId.Value] = r;
            sShpArmed = sShp.Count;
            return r;
        }

        // A Sim starts a shower (the line hook): when it has to pee and the lot has no usable toilet, this shower
        // refills its bladder motive.
        static void ShpNote(Sim a, InteractionInstance ia, GameObject obj)
        {
            if (!ShortcutOn("showerpee")) return;
            ShpRun r;
            if (sShp.TryGetValue(a.ObjectId.Value, out r))
            {
                if (object.ReferenceEquals(r.Inst, ia)) return;
                if (r.State == kShpFailed) return;  // the cool-down of a shower that never began
                ShpDrop(r);
            }
            GameObject unused;
            if (!ShpSimOk(a) || !ShlScope(a, ShortcutOn("showerpeehh")) || !ShpLow(a) || ShpToiletOnLot(a, out unused)) return;
            ShpAdd(a, ia, obj, false);
        }

        static bool ShpHasPlayerOrders(Sim s)
        {
            if (!s.IsSelectable) return false;
            foreach (InteractionInstance i in s.InteractionQueue.InteractionList)
                if (i != null && !i.Autonomous) return true;
            return false;
        }

        // Queue the game's Take Shower for a Sim whose bladder is about to fail (failure = has failed). True = queued.
        static bool ShpPush(Sim s, bool failure)
        {
            if (sShpMissing.Length > 0 || !ShpSimOk(s) || s.InteractionQueue.HasInteractionOfType(Sim.BladderFailure.Singleton)) return false;
            int[] c = PatchCounters();
            GameObject shower;
            bool toilet = ShpToiletOnLot(s, out shower);
            int d = ShowerShareCore.Pee(ShortcutOn("showerpee"), true, toilet, shower != null, ShlScope(s, ShortcutOn("showerpeehh")));
            if (failure)
            {
                if (d == ShowerShareCore.kNoShower) c[kShpNoShower]++;
                else if (d == ShowerShareCore.kWould) c[kShpWould]++;
                else if (d == ShowerShareCore.kExempt) c[kShpExempt]++;
            }
            if (d != ShowerShareCore.kGo) return false;
            if (!failure && (ShpHasPlayerOrders(s) || s.InteractionQueue.HasInteractionOfType(Shower.TakeShower.Singleton))) return false;
            InteractionInstance inst = Shower.TakeShower.Singleton.CreateInstance(shower, s, new InteractionPriority(InteractionPriorityLevel.High), false, true);
            if (inst == null || !s.InteractionQueue.AddNext(inst)) return false;
            ShpAdd(s, inst, shower, true);
            c[failure ? kShpAvoided : kShpPushed]++;
            if (failure) s.AddExitReason(ExitReason.BuffFailureState);  // as the game does for its own failure interaction
            ShsActLine("showerpee", s.FullName + " " + ShlWho(s) + ", " + SwfIa(s) + ": no usable toilet, takes a shower in " + shower.GetType().Name + " at " + V(shower.Position)
                + (failure ? " instead of the bladder failure" : " at the last warning"));
            return true;
        }

        // Motive.MotiveDistress(Bladder). True = held back: a shower stands for the failure.
        static bool ShpDistress(Sim s)
        {
            try
            {
                ShpRun r;
                if (sShp.TryGetValue(s.ObjectId.Value, out r))
                {
                    float min = OjsMinutes(r.Start);
                    if (r.State == kShpFailed)
                    {
                        if (min < kShpCoolMinutes) return false;
                        ShpDrop(r);
                    }
                    else
                    {
                        bool queued = s.InteractionQueue != null && r.Inst != null && s.InteractionQueue.HasInteraction(r.Inst);
                        if (ShowerShareCore.PeeHolds(queued, r.State == kShpFilling, min)) return true;
                        if (!r.Pushed) ShpDrop(r);
                        else
                        {
                            r.State = kShpFailed;  // kept as the cool-down
                            r.Start = SimClock.CurrentTicks;
                            PatchCounters()[kShpFellBack]++;
                        }
                        return false;
                    }
                }
                return ShpPush(s, true);
            }
            catch (ResetException) { throw; }
            catch (Exception e) { ShsError(5, e); return false; }
        }

        // BuffReallyHasToPee was added: the last warning.
        static void ShpUrgent(Sim s)
        {
            try
            {
                if (s == null || sShp.ContainsKey(s.ObjectId.Value)) return;
                ShpPush(s, false);
            }
            catch (ResetException) { throw; }
            catch (Exception e) { ShsError(6, e); }
        }

        static void ShpFull(ShpRun r)
        {
            Sim s = r.Sim;
            s.Motives.SetMax(CommodityKind.Bladder);
            Motive m = s.Motives.GetMotive(CommodityKind.Bladder);
            if (m != null) m.PotionBladderDecayOverride = false;  // as the game's toilets do
        }

        // StateMachineClient.SetActor (SmcSetActor.cs, behind sShpArmed > 0): the shower becomes an actor of the
        // Sim's own state machine, which is the start of its shower. The bladder fills from here.
        static void ShpSetActor(IHasScriptProxy actor)
        {
            try
            {
                foreach (ShpRun r in sShp.Values)
                {
                    if (r.State != kShpPending || !object.ReferenceEquals(r.Target, actor) || Simulator.CurrentTask != r.Sim.ObjectId || !object.ReferenceEquals(r.Sim.CurrentInteraction, r.Inst)) continue;
                    r.State = kShpFilling;
                    r.Sim.Motives.LerpToFill(r.Inst, CommodityKind.Bladder, ShowerShareCore.kPeeFillMinutes);
                    PatchCounters()[kShpPeed]++;
                    ShsActLine("showerpee", r.Sim.FullName + " " + ShlWho(r.Sim) + ", " + SwfIa(r.Sim) + ": bladder fills in " + r.Target.GetType().Name + " at " + V(r.Target.Position));
                    return;
                }
            }
            catch (ResetException) { throw; }
            catch (Exception e) { ShsError(4, e); }
        }

        // The Sim steps out of the shower (ShsLeaving): its bladder is full.
        static void ShpLeave(Sim actor, GameObject obj)
        {
            ShpRun r;
            if (!sShp.TryGetValue(actor.ObjectId.Value, out r) || !object.ReferenceEquals(r.Target, obj) || r.State == kShpFailed) return;
            if (r.State == kShpPending) PatchCounters()[kShpPeed]++;  // the start was not seen; the Sim showered all the same
            ShpFull(r);
            ShpDrop(r);
        }

        public class ShpDistressHook
        {
            public void DistressHook(Sim sim, CommodityKind commodity)
            {
                if (commodity == CommodityKind.Bladder && sim != null && ShpDistress(sim)) return;
                DistressOrig(sim, commodity);
            }
            public void DistressOrig(Sim sim, CommodityKind commodity) { }  // receives MotiveDistress's original body
        }

        public class ShpBuffHook
        {
            public void AddHook(BuffManager bm, BuffInstance bi, bool travelReaddition)
            {
                AddOrig(bm, bi, travelReaddition);
                if (bm != null) ShpUrgent(bm.Actor);
            }
            public void AddOrig(BuffManager bm, BuffInstance bi, bool travelReaddition) { }  // receives OnAddition's original body
        }

        // ---- check ------------------------------------------------------------------------------------------------------

        static string ShsCounters()
        {
            int[] c = PatchCounters();
            string sites = "";
            for (int i = 0; i < sShsSiteErrors.Length; i++)
                if (sShsSiteErrors[i] > 0) sites += (sites.Length > 0 ? ", " : "") + kShsSites[i] + " " + sShsSiteErrors[i];
            return "showertogether: joined a partner in a shower " + c[kShsJoins] + ", in an all-in-one bathroom " + c[kShsAioJoins] + ", exits held for the partner's " + c[kShsExitWaits]
                + ", kept: not romantic or disliked " + c[kShsKeptRomance] + ", a teen or child " + c[kShsKeptAge] + ", other (several Sims, not showering, a third Sim) " + c[kShsKeptOther]
                + ", would " + c[kShsWould] + ", exempt " + c[kShsExempt] + ", open pairs " + sShs.Count
                + "; showerpee: bladders filled in a shower " + c[kShpPeed] + ", showers queued at the last warning " + c[kShpPushed] + ", failures replaced by a shower " + c[kShpAvoided]
                + ", given back to the game (the shower never began) " + c[kShpFellBack] + ", kept: no usable shower " + c[kShpNoShower] + ", would " + c[kShpWould] + ", exempt " + c[kShpExempt]
                + ", open " + sShp.Count
                + "; errors " + c[kShsErrors] + (sites.Length > 0 ? " (" + sites + "; last: " + sShsLastError + ")" : "") + "; " + GlMembers((sShsMissing + " " + sShpMissing).Trim())
                + (sShsLast.Length > 0 ? "; last: " + sShsLast : "");
        }

        // ShowerShareCheck run: the rules with controls (read-only). Bug cases: a Sim wants the shower its partner is in
        // (the game queues it, showertogether lets it in); a Sim's bladder fails on a lot with a shower and no usable
        // toilet (the game's accident, showerpee's shower). result: the counters and the last line. lot: the selected
        // Sim's lot: toilets and showers, who uses them, and what both rules would do for that Sim now.
        public static object ShowerShareCheck(string what)
        {
            if (what == "result") return ShsCounters();
            if (what == "lot") return ShsLot();
            if (what != "run") return "showershare <run|result|lot>";
            bool tgOn = ShortcutOn("showertogether"), peeOn = ShortcutOn("showerpee");
            const int Go = ShowerShareCore.kGo;
            bool tgFixed = ShowerShareCore.Join(tgOn, 1, true, true, true, true, true, true, true) == Go;
            bool peeFixed = ShowerShareCore.Pee(peeOn, true, false, true, true) == Go;
            bool ctlJoin = ShowerShareCore.Join(true, 0, false, true, true, true, true, true, true) == ShowerShareCore.kNone
                && ShowerShareCore.Join(true, 2, true, true, true, true, true, true, true) == ShowerShareCore.kKeepOther
                && ShowerShareCore.Join(true, 1, false, true, true, true, true, true, true) == ShowerShareCore.kKeepOther
                && ShowerShareCore.Join(true, 1, true, true, false, true, true, true, true) == ShowerShareCore.kKeepAge
                && ShowerShareCore.Join(true, 1, true, true, true, false, true, true, true) == ShowerShareCore.kKeepRomance
                && ShowerShareCore.Join(true, 1, true, true, true, true, false, true, true) == ShowerShareCore.kKeepRomance
                && ShowerShareCore.Join(true, 1, true, true, true, true, true, false, true) == ShowerShareCore.kExempt
                && ShowerShareCore.Join(true, 1, true, true, true, true, true, true, false) == ShowerShareCore.kExempt
                && ShowerShareCore.Join(false, 1, true, true, true, true, true, true, true) == ShowerShareCore.kWould
                && ShowerShareCore.Join(false, 1, true, true, false, true, true, true, true) == ShowerShareCore.kKeepAge;
            bool ctlExit = ShowerShareCore.ExitWait(ShowerShareCore.kExiting, true, 1f) && !ShowerShareCore.ExitWait(ShowerShareCore.kJoined, true, 1f)
                && !ShowerShareCore.ExitWait(ShowerShareCore.kLeaving, true, 1f) && !ShowerShareCore.ExitWait(ShowerShareCore.kExiting, false, 1f)
                && !ShowerShareCore.ExitWait(ShowerShareCore.kExiting, true, ShowerShareCore.kExitCapMinutes)
                && ShowerShareCore.UseOk(2, 2) && ShowerShareCore.UseOk(1, 1) && !ShowerShareCore.UseOk(2, 1);
            bool ctlPee = ShowerShareCore.Pee(true, true, true, true, true) == ShowerShareCore.kNone && ShowerShareCore.Pee(true, false, false, true, true) == ShowerShareCore.kNone
                && ShowerShareCore.Pee(true, true, false, false, true) == ShowerShareCore.kNoShower && ShowerShareCore.Pee(true, true, false, true, false) == ShowerShareCore.kExempt
                && ShowerShareCore.Pee(false, true, false, true, true) == ShowerShareCore.kWould
                && ShowerShareCore.ToiletUsable(false, false, 0, true) && !ShowerShareCore.ToiletUsable(true, false, 0, true) && !ShowerShareCore.ToiletUsable(false, true, 0, true)
                && !ShowerShareCore.ToiletUsable(false, false, 1, true) && !ShowerShareCore.ToiletUsable(false, false, 0, false)
                && ShowerShareCore.ShowerUsable(false, 0, ShowerShareCore.kNone) && ShowerShareCore.ShowerUsable(false, 1, Go) && !ShowerShareCore.ShowerUsable(false, 1, ShowerShareCore.kKeepRomance)
                && !ShowerShareCore.ShowerUsable(true, 0, ShowerShareCore.kNone)
                && ShowerShareCore.PeeHolds(true, false, 5f) && !ShowerShareCore.PeeHolds(false, false, 5f) && !ShowerShareCore.PeeHolds(true, false, ShowerShareCore.kPeeGiveUpMinutes)
                && ShowerShareCore.PeeHolds(true, true, 90f);
            bool controls = ctlJoin && ctlExit && ctlPee;
            bool members = sShsMissing.Length + sShpMissing.Length == 0;
            string verdict = tgOn != peeOn ? (controls && members ? "MIXED (one switch on)" : "UNEXPECTED") : GlVerdict(tgOn, tgFixed && peeFixed, controls, members);
            return "showershare check (showertogether " + (tgOn ? "on" : "off") + ", household " + (ShortcutOn("showertogetherhh") ? "on" : "off") + ", showerpee " + (peeOn ? "on" : "off") + ", household "
                + (ShortcutOn("showerpeehh") ? "on" : "off") + ", showersharelog " + (ShortcutOn("showersharelog") ? "on" : "off") + "): a partner is let into the shower=" + tgFixed
                + ", a failing bladder with no usable toilet takes a shower=" + peeFixed + "; control: free shower, several Sims, not showering, teen or child, not romantic, disliked, scope, off=" + ctlJoin
                + ", exit one at a time with a cap, strangers in the use list=" + ctlExit + ", usable toilet, no shower, scope, broken / used / queued, give-up time=" + ctlPee
                + "; controls ok=" + controls + "; verdict: " + verdict + "; " + ShsCounters();
        }

        static string ShsLot()
        {
            Sim sel = PlumbBob.SelectedActor;
            if (sel == null || sel.LotCurrent == null) return "showershare lot: no selected Sim on a lot";
            System.Text.StringBuilder b = new System.Text.StringBuilder("showershare lot: " + sel.FullName + ";");
            int toilets = 0, showers = 0;
            foreach (GameObject o in sel.LotCurrent.GetObjects<GameObject>())
            {
                if (o == null) continue;
                bool toilet = o is IToiletOrUrinal || o is AllInOneBathroom, shower = o is IShowerable;
                if (!toilet && !shower) continue;
                if (toilet) toilets++;
                if (shower) showers++;
                if (b.Length > 1400) continue;
                Sim other;
                int n = ShsOthers(sel, o, out other);
                b.Append(" " + o.GetType().Name + " at " + V(o.Position) + ": " + (ShpBroken(o) ? "broken, " : "") + "in use by " + o.UseCount + ", in line " + ShpOthersInLine(sel, o));
                if (toilet) b.Append(", usable as a toilet=" + ShpToiletUsable(sel, o));
                if (n > 0 && (shower || o is AllInOneBathroom)) b.Append(", join decision " + ShsDecide(sel, o, shower ? kShsShower : kShsAio, out other) + (other == null ? "" : " with " + other.FullName));
                b.Append(";");
            }
            GameObject pick = null;
            bool usable = ShpSimOk(sel) && ShpToiletOnLot(sel, out pick);
            return b.Append(" toilets " + toilets + ", showers " + showers + ", a toilet is usable=" + usable + ", shower for a failing bladder=" + (pick == null ? "none" : pick.GetType().Name + " at " + V(pick.Position))
                + ", bladder " + (sel.Motives == null ? "?" : sel.Motives.GetValue(CommodityKind.Bladder).ToString("0")) + ", has to pee=" + ShpLow(sel)).ToString();
        }
    }
}
