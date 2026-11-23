using System;
using System.Collections.Generic;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects.CookingObjects;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Sims share more things (docs/686 §14). A player's wish: clipping is fine, waiting is not. Privacy objects
    // (toilet, shower, bath, bed) are not touched. Three parts, each default off; rules in ObjShareCore.cs.
    //
    // linewaitlog (log only)
    //   One "linewait:" line per finished wait in an object's line (SimQueue.WaitForTurn, from half a sim-minute) and
    //   per wait for a rabbit hole's enter slot. The counters run with the switch off too. The line hook is
    //   sharelanes' (LdsQueueHook.WaitHook in ShareLanes.cs).
    //
    // rhshare / rhsharehh
    //   [verified, decompiled] RabbitHole.AnimateEnterRabbitHole asks WaitForEnterSlot for its slot before each walk
    //   to it. One Sim holds a slot (ReservedEnterSlots, ReservedEnterSlotsPerPerson) until its walk has ended; the
    //   others stand in a loop that sleeps 10 ticks, never looks at exit reasons and breaks a reservation only after
    //   15 sim-minutes. The caller's cleanup hands the slot to the nearest Sim of WaitingToEnterSims.
    //   Fix, at the start of WaitForEnterSlot when another Sim holds the slot:
    //     - holder in scope: this Sim walks to the slot now. It takes no reservation and is not in the waiting list, so
    //       the holder's cleanup and the game's hand-over see what they always see.
    //     - the reservation names a Sim that no longer exists: it is removed and the game's code reserves for this Sim.
    //     - holder out of scope, or a horse / rider / carrier: this Sim waits in our loop, which is the game's with
    //       one more exit: a Sim with an exit reason leaves (its walk then fails at once and the caller ends).
    //   Horses, riders, Sims carrying a child and toddlers keep the game's wait.
    //
    // servingshare / servingsharehh
    //   [verified, decompiled] ServingContainerGroup.RunBodyForGrabAServing: a walk near the platter, the platter's
    //   line (servings - 1 long, 10 sim-minutes), the surface's line when it has one, the walk to the surface slot,
    //   then "in use -> give up", HasFoodLeft, StandardEntry, the take animation (only the Sim and its own plate are
    //   actors) and DecrementServings after it. Two Sims that both pass HasFoodLeft would both take the last serving;
    //   the game's line and use list prevent that.
    //   Fix, round the game's run for a Sim in scope:
    //     - the lines are skipped (LdsQueueHook.WaitHook) while every Sim in them is in scope;
    //     - the Sim leaves the platter's use list as soon as its take begins (first SetActor of its state machine,
    //       SmcSetActor.cs), so the next Sim's in-use test passes and both take at once;
    //     - HasFoodLeft, asked by a Sim about to take, counts servings other Sims are taking now as gone. With none
    //       left it answers false and the game's run ends as it does at an empty platter.
    //   Not exact for scope: a Sim out of scope that arrives while a Sim in scope takes finds the platter free.
    //   While a Sim takes, the platter is not "in use" for other interactions (pick up, clean up) for those seconds.
    //
    // Scope: the plain switch = Sims the player does not control, hh = household and selected Sims too. Off = compare
    // mode: counted, nothing done. Counters 4470-4493. Check: ObjShareCheck.
    public static partial class Fixes
    {
        const int kLwWaits = 4470, kLwTenths = 4471, kLwTimeouts = 4472, kLwCancelled = 4473,
            kLwRhWaits = 4474, kLwRhTenths = 4475, kLwRhTimeouts = 4476, kLwRhCancelled = 4477,
            kOjsErrors = 4478,
            kRhsShared = 4479, kRhsWould = 4480, kRhsExempt = 4481, kRhsKept = 4482, kRhsCancelLeft = 4483, kRhsStale = 4484,
            kSvsRuns = 4485, kSvsLineSkips = 4486, kSvsShared = 4487, kSvsWould = 4488, kSvsExempt = 4489, kSvsKept = 4490, kSvsLastGuard = 4491,
            kLwLineFull = 4492, kLwShort = 4493;
        const int kOjsArmed = 0, kOjsReserved = 1, kOjsTaking = 2, kOjsDone = 3;  // a serving run's steps

        sealed class OjsRhWait { public long Start; public int Length, Decision; public ObjectGuid Holder; }
        sealed class OjsServe { public Sim Sim; public ServingContainerGroup Target; public ulong Task; public int State; }

        static readonly Dictionary<ulong, OjsServe> sOjsServe = new Dictionary<ulong, OjsServe>();  // serving runs in scope, by Sim
        static int sOjsArmed;  // sOjsServe.Count, read by the hot hooks
        static readonly LineCapCore sOjsCap = new LineCapCore(30, 60000, 600);
        static readonly string[] kOjsSites = { "line wait", "rabbit hole begin", "rabbit hole end", "rabbit hole wait", "serving begin", "serving end", "serving line", "serving take", "food left" };
        static readonly int[] sOjsSiteErrors = new int[9];
        static string sOjsMissing = "", sOjsLast = "", sOjsLastShare = "", sOjsLastError = "";

        static void ApplyObjShare()
        {
            string missing = "";
            if (!Patch(typeof(RabbitHole), "WaitForEnterSlot", new Type[] { typeof(Slot), typeof(Sim) }, typeof(OjsRhHook), "WaitHook", "WaitOrig")) missing += " RabbitHole.WaitForEnterSlot";
            if (!Patch(typeof(ServingContainerGroup), "RunBodyForGrabAServing",
                new Type[] { typeof(Sim), typeof(ServingContainerGroup), typeof(ServingContainerGroup.ServingContainerGroup_GrabAServing), typeof(bool) },
                typeof(OjsServeHook), "GrabHook", "GrabOrig")) missing += " ServingContainerGroup.RunBodyForGrabAServing";
            else if (!Patch(typeof(ServingContainerGroup), "HasFoodLeft", Type.EmptyTypes, typeof(OjsServeHook), "FoodLeftHook", "FoodLeftOrig")) missing += " ServingContainerGroup.HasFoodLeft";
            sOjsMissing = missing.Trim();
            if (sOjsMissing.Length > 0) AppliedLog().Add("objshare: not found: " + sOjsMissing);
        }

        static void OjsError(int site, Exception e)
        {
            PatchCounters()[kOjsErrors]++;
            sOjsLastError = "objshare: error at " + kOjsSites[site] + ": " + e.GetType().Name + ": " + e.Message;
            if (sOjsSiteErrors[site]++ < 3) DiagLine(sOjsLastError);
        }

        static float OjsMinutes(long startTicks) { return SimClock.ConvertFromTicks(SimClock.CurrentTicks - startTicks, TimeUnit.Minutes); }

        static void OjsShareLine(string switchName, string text)
        {
            sOjsLastShare = switchName + ": " + text;
            if (ShortcutOn("linewaitlog") && sOjsCap.Take(System.Environment.TickCount)) DiagLine(sOjsLastShare);
        }

        // ---- linewaitlog ------------------------------------------------------------------------------------------------

        // One finished wait. rhdoor: every wait counts; line: from ObjShareCore.kLogMinutes.
        static void OjsWaitDone(Sim s, string targetType, bool rhDoor, float min, int length, int outcome)
        {
            int[] c = PatchCounters();
            c[rhDoor ? kLwRhWaits : kLwWaits]++;
            c[rhDoor ? kLwRhTenths : kLwTenths] += ShareLanesCore.Tenths(min);
            if (outcome == ObjShareCore.kTimedOut) c[rhDoor ? kLwRhTimeouts : kLwTimeouts]++;
            else if (outcome == ObjShareCore.kCancelled) c[rhDoor ? kLwRhCancelled : kLwCancelled]++;
            sOjsLast = "linewait: " + s.FullName + " " + ShlWho(s) + ", " + SwfIa(s) + ": " + targetType + (rhDoor ? " rhdoor" : " line") + ", waited " + min.ToString("0.0")
                + " sim-min, line length " + length + ", outcome " + ObjShareCore.kOutcomeNames[outcome] + ", at " + V(s.Position);
            if (ShortcutOn("linewaitlog") && sOjsCap.Take(System.Environment.TickCount)) DiagLine(sOjsLast);
        }

        // After SimQueue.WaitForTurn's own body (LdsQueueHook.WaitHook). lengthBefore: Sims in the line when this one asked.
        static void OjsLineWaitEnd(InteractionInstance interaction, IGameObject target, long startTicks, int lengthBefore, bool got, float timeoutInMinutes)
        {
            try
            {
                float min = OjsMinutes(startTicks);
                if (!ObjShareCore.LineLogged(min))
                {
                    PatchCounters()[!got && lengthBefore > 0 && min <= 0f ? kLwLineFull : kLwShort]++;
                    return;
                }
                Sim s = interaction == null ? null : interaction.InstanceActor;
                if (s == null) return;
                OjsWaitDone(s, target == null ? "?" : target.GetType().Name, false, min, lengthBefore, ObjShareCore.LineOutcome(got, min, timeoutInMinutes));
            }
            catch (ResetException) { throw; }
            catch (Exception e) { OjsError(0, e); }
        }

        // ---- rhshare ----------------------------------------------------------------------------------------------------

        static bool OjsRhPlain(Sim s)
        {
            return s.IsHuman && !s.IsInRidingPosture && s.CarryingChildPosture == null && s.SimDescription != null && s.SimDescription.ChildOrAbove;
        }

        static List<ObjectGuid> OjsRhList(RabbitHole rh, Slot slot)
        {
            List<ObjectGuid> list;
            if (!rh.WaitingToEnterSims.TryGetValue(slot, out list) || list == null)
            {
                list = new List<ObjectGuid>();
                rh.WaitingToEnterSims[slot] = list;
            }
            return list;
        }

        // Another Sim's reservation on the slot: its holder (invalid when the slot is listed without a name).
        static bool OjsRhHeld(RabbitHole rh, Slot slot, Sim s, out ObjectGuid holder)
        {
            if (rh.ReservedEnterSlotsPerPerson.TryGetValue(slot, out holder)) return holder != s.ObjectId;
            holder = ObjectGuid.InvalidObjectGuid;
            return rh.ReservedEnterSlots.Contains(slot);
        }

        static int OjsRhDecide(RabbitHole rh, Slot slot, Sim s, out ObjectGuid holder)
        {
            bool held = OjsRhHeld(rh, slot, s, out holder);
            if (!held) return ObjShareCore.kNone;
            bool hhOn = ShortcutOn("rhsharehh");
            Sim h = holder == ObjectGuid.InvalidObjectGuid ? null : GameObject.GetObject(holder) as Sim;
            bool known = h != null && !h.HasBeenDestroyed;
            return ObjShareCore.Rh(ShortcutOn("rhshare"), true, ShlScope(s, hhOn), OjsRhPlain(s), known, known && ShlScope(h, hhOn), known && OjsRhPlain(h));
        }

        // Null = the slot is free or this Sim's: the game's code as it is, nothing to count.
        static OjsRhWait OjsRhBegin(RabbitHole rh, Slot slot, Sim s)
        {
            if (rh == null || s == null) return null;
            ObjectGuid holder;
            int d = OjsRhDecide(rh, slot, s, out holder);
            if (d == ObjShareCore.kNone && !OjsRhHeld(rh, slot, s, out holder)) return null;
            int[] c = PatchCounters();
            OjsRhWait w = new OjsRhWait();
            w.Start = SimClock.CurrentTicks;
            w.Decision = d;
            w.Holder = holder;
            List<ObjectGuid> list;
            w.Length = rh.WaitingToEnterSims.TryGetValue(slot, out list) && list != null ? list.Count : 0;
            if (d == ObjShareCore.kWould) c[kRhsWould]++;
            else if (d == ObjShareCore.kExempt) c[kRhsExempt]++;
            else if (d == ObjShareCore.kGo) OjsRhShare(rh, slot, s, w);
            else if (d == ObjShareCore.kStale) OjsRhDrop(rh, slot, s, holder, "the holder is gone");
            else c[kRhsKept]++;  // kKeep: our wait; kNone here: a horse, rider, carrier or toddler, the game's wait
            return w;
        }

        static void OjsRhShare(RabbitHole rh, Slot slot, Sim s, OjsRhWait w)
        {
            PatchCounters()[kRhsShared]++;
            OjsRhList(rh, slot);  // the caller's cleanup reads the slot's list
            OjsShareLine("rhshare", s.FullName + " " + ShlWho(s) + " walks to enter slot " + slot + " of " + rh.GetType().Name + " held by another Sim, " + w.Length + " waiting, at " + V(s.Position));
        }

        // A reservation nobody uses is taken off; the game's code then reserves for the Sim that asked.
        static void OjsRhDrop(RabbitHole rh, Slot slot, Sim s, ObjectGuid holder, string why)
        {
            PatchCounters()[kRhsStale]++;
            rh.ReservedEnterSlots.Remove(slot);
            ObjectGuid now;
            if (rh.ReservedEnterSlotsPerPerson.TryGetValue(slot, out now) && now == holder) rh.ReservedEnterSlotsPerPerson.Remove(slot);
            OjsShareLine("rhshare", s.FullName + " " + ShlWho(s) + ": reservation of enter slot " + slot + " of " + rh.GetType().Name + " removed (" + why + ")");
        }

        // Our wait for a Sim in scope whose slot is held by a Sim it may not share with. The Sim is in the slot's waiting
        // list while it stands here, so the game's hand-over can pick it. Answer: kShared (walk now), kCancelled (leave;
        // the list entry stays for the caller's cleanup), kGotTurn (the game's code takes over: free slot, handed over,
        // or a reservation broken by age). No catch: a reset must unwind the Sim's stack.
        static int OjsRhWaitLoop(RabbitHole rh, Slot slot, Sim s, OjsRhWait w)
        {
            List<ObjectGuid> list = OjsRhList(rh, slot);
            if (!list.Contains(s.ObjectId)) list.Add(s.ObjectId);
            ObjectGuid first = w.Holder;
            long holderSince = w.Start;
            while (true)
            {
                s.LoopIdle();
                Simulator.Sleep(10u);
                list = OjsRhList(rh, slot);  // a rabbit hole reset clears the table
                ObjectGuid holder;
                if (!OjsRhHeld(rh, slot, s, out holder)) break;
                if (s.HasExitReason(ExitReason.Default))
                {
                    if (!list.Contains(s.ObjectId)) list.Add(s.ObjectId);
                    return ObjShareCore.kCancelled;
                }
                int d = OjsRhDecide(rh, slot, s, out holder);
                if (d == ObjShareCore.kGo) { list.Remove(s.ObjectId); OjsRhShare(rh, slot, s, w); return ObjShareCore.kShared; }
                if (d == ObjShareCore.kStale) { OjsRhDrop(rh, slot, s, holder, "the holder is gone"); break; }
                if (d != ObjShareCore.kKeep) break;  // switched off meanwhile: the game's wait
                if (holder != first) { first = holder; holderSince = SimClock.CurrentTicks; }
                else if (ObjShareCore.RhBreak(OjsMinutes(holderSince))) { OjsRhDrop(rh, slot, s, holder, "held " + ObjShareCore.kRhStaleMinutes + " sim-min"); break; }
            }
            list.Remove(s.ObjectId);  // the game's code enters the Sim again
            return ObjShareCore.kGotTurn;
        }

        static void OjsRhEnd(RabbitHole rh, Sim s, OjsRhWait w, int outcome)
        {
            try
            {
                float min = OjsMinutes(w.Start);
                if (outcome == ObjShareCore.kCancelled) PatchCounters()[kRhsCancelLeft]++;
                else if (outcome == ObjShareCore.kGotTurn) outcome = ObjShareCore.RhOutcome(min);
                OjsWaitDone(s, rh.GetType().Name, true, min, w.Length, outcome);
            }
            catch (ResetException) { throw; }
            catch (Exception e) { OjsError(2, e); }
        }

        // `this` is the game's rabbit hole. The tables are its RabbitHoleProxy's.
        public abstract class OjsRhHook : RabbitHole
        {
            public void WaitHook(Slot slotChosen, Sim s)
            {
                RabbitHole rh = RabbitHoleProxy;
                OjsRhWait w = null;
                try { w = OjsRhBegin(rh, slotChosen, s); }
                catch (ResetException) { throw; }
                catch (Exception e) { OjsError(1, e); }
                if (w == null) { WaitOrig(slotChosen, s); return; }
                int outcome = ObjShareCore.kGotTurn;
                if (w.Decision == ObjShareCore.kGo) outcome = ObjShareCore.kShared;
                else if (w.Decision == ObjShareCore.kKeep) outcome = OjsRhWaitLoop(rh, slotChosen, s, w);
                if (outcome == ObjShareCore.kGotTurn) WaitOrig(slotChosen, s);
                OjsRhEnd(rh, s, w, outcome);
            }
            public void WaitOrig(Slot slotChosen, Sim s) { }  // receives WaitForEnterSlot's original body
        }

        // ---- servingshare -----------------------------------------------------------------------------------------------

        static bool OjsServeOthersInScope(Sim a, SimQueue line, List<Sim> users, bool hhOn)
        {
            List<Sim> inLine = line == null ? null : line.SimsInQueue;  // null while nobody is in the line
            if (inLine != null)
                foreach (Sim o in inLine) if (o != null && o != a && !ShlScope(o, hhOn)) return false;
            if (users != null)
                foreach (Sim o in users) if (o != null && o != a && !ShlScope(o, hhOn)) return false;
            return true;
        }

        // Start of a Grab a Serving run. Null = the game's run as it is.
        static OjsServe OjsServeBegin(Sim a, ServingContainerGroup target, bool inInventory)
        {
            if (a == null || target == null || inInventory || sOjsMissing.Length > 0) return null;
            int[] c = PatchCounters();
            bool busy = target.UseCount > 0 || (target.SimLine != null && target.SimLine.MemberCount() > 0);
            if (busy) c[kSvsRuns]++;
            bool hhOn = ShortcutOn("servingsharehh");
            int d = ObjShareCore.Serve(ShortcutOn("servingshare"), false, ShlScope(a, hhOn), OjsServeOthersInScope(a, target.SimLine, target.ActorsUsingMe, hhOn));
            if (d != ObjShareCore.kGo)
            {
                if (busy) c[d == ObjShareCore.kWould ? kSvsWould : d == ObjShareCore.kExempt ? kSvsExempt : kSvsKept]++;
                return null;
            }
            if (sOjsServe.Count > 64) sOjsServe.Clear();  // entries leave when the run ends; this bounds what a world quit left behind
            OjsServe k = new OjsServe();
            k.Sim = a; k.Target = target; k.Task = Simulator.CurrentTask.Value; k.State = kOjsArmed;
            sOjsServe[a.ObjectId.Value] = k;
            sOjsArmed = sOjsServe.Count;
            return k;
        }

        static void OjsServeEnd(OjsServe k)
        {
            OjsServe now;
            if (sOjsServe.TryGetValue(k.Sim.ObjectId.Value, out now) && object.ReferenceEquals(now, k)) sOjsServe.Remove(k.Sim.ObjectId.Value);
            sOjsArmed = sOjsServe.Count;
        }

        // Sims of other runs that are taking from this platter now.
        static int OjsServeTaking(ServingContainerGroup target, OjsServe but)
        {
            int n = 0;
            foreach (OjsServe o in sOjsServe.Values)
                if (!object.ReferenceEquals(o, but) && object.ReferenceEquals(o.Target, target) && (o.State == kOjsReserved || o.State == kOjsTaking)) n++;
            return n;
        }

        // SimQueue.WaitForTurn for a Sim in a serving run that has not begun its take: the platter's line and the
        // surface's are skipped while every Sim in the line is in scope. The Sim is not entered in the line.
        static bool OjsServeLine(Sim a, SimQueue line)
        {
            try
            {
                OjsServe k;
                if (!sOjsServe.TryGetValue(a.ObjectId.Value, out k) || k.State != kOjsArmed || k.Task != Simulator.CurrentTask.Value) return false;
                if (!OjsServeOthersInScope(a, line, null, ShortcutOn("servingsharehh"))) { PatchCounters()[kSvsKept]++; return false; }
                if (line.MemberCount() > 0 || OjsServeTaking(k.Target, k) > 0) PatchCounters()[kSvsLineSkips]++;
                return true;
            }
            catch (ResetException) { throw; }
            catch (Exception e) { OjsError(6, e); return false; }
        }

        // StateMachineClient.SetActor (SmcSetActor.cs, behind sOjsArmed > 0): the first one for a Sim that has passed
        // the food test is its take. The Sim leaves the platter's use list before the animation's first yield.
        static void OjsSetActor(IHasScriptProxy actor)
        {
            try
            {
                Sim s = actor as Sim;
                OjsServe k;
                if (s == null || !sOjsServe.TryGetValue(s.ObjectId.Value, out k) || k.State != kOjsReserved) return;
                k.State = kOjsTaking;
                if (k.Target.IsActorUsingMe(s)) k.Target.RemoveFromUseList(s);
            }
            catch (ResetException) { throw; }
            catch (Exception e) { OjsError(7, e); }
        }

        // ServingContainerGroup.HasFoodLeft with a serving run open. Other callers get the game's answer. The run's
        // first call is the test before its take: servings being taken by others now are gone. Its second call is the
        // game's test right after DecrementServings: the run's serving is off the platter's count from there.
        static bool OjsFoodLeft(ServingContainerGroup target, bool ea)
        {
            try
            {
                if (!ea) return false;
                ulong task = Simulator.CurrentTask.Value;
                OjsServe k = null;
                foreach (OjsServe o in sOjsServe.Values)
                    if (o.Task == task && object.ReferenceEquals(o.Target, target)) { k = o; break; }
                if (k == null || k.State == kOjsDone) return true;
                if (k.State != kOjsArmed) { k.State = kOjsDone; return true; }
                int others = OjsServeTaking(target, k);
                if (!ObjShareCore.ServeLeft(target.NumServingsLeft, others, target.Parent is IBuffetTable))
                {
                    PatchCounters()[kSvsLastGuard]++;
                    OjsShareLine("servingshare", k.Sim.FullName + " " + ShlWho(k.Sim) + " does not take from " + target.GetType().Name + ": " + target.NumServingsLeft + " left, " + others + " being taken, at " + V(k.Sim.Position));
                    return false;
                }
                k.State = kOjsReserved;
                if (others > 0)
                {
                    PatchCounters()[kSvsShared]++;
                    OjsShareLine("servingshare", k.Sim.FullName + " " + ShlWho(k.Sim) + " takes from " + target.GetType().Name + " with " + others + " other Sim(s), " + target.NumServingsLeft + " left, at " + V(k.Sim.Position));
                }
                return true;
            }
            catch (ResetException) { throw; }
            catch (Exception e) { OjsError(8, e); return ea; }
        }

        // `this` is the game's platter (the same object as Target).
        public abstract class OjsServeHook : ServingContainerGroup
        {
            public bool GrabHook(Sim Actor, ServingContainerGroup Target, ServingContainerGroup_GrabAServing instance, bool bInInventory)
            {
                OjsServe k = null;
                try { k = OjsServeBegin(Actor, Target, bInInventory); }
                catch (ResetException) { throw; }
                catch (Exception e) { OjsError(4, e); }
                if (k == null) return GrabOrig(Actor, Target, instance, bInInventory);
                try { return GrabOrig(Actor, Target, instance, bInInventory); }
                finally
                {
                    try { OjsServeEnd(k); }
                    catch (Exception e) { OjsError(5, e); }
                }
            }
            public bool GrabOrig(Sim Actor, ServingContainerGroup Target, ServingContainerGroup_GrabAServing instance, bool bInInventory) { return false; }  // receives RunBodyForGrabAServing's original body

            public bool FoodLeftHook()
            {
                if (sOjsArmed == 0) return FoodLeftOrig();
                return OjsFoodLeft(this, FoodLeftOrig());
            }
            public bool FoodLeftOrig() { return false; }  // receives HasFoodLeft's original body
        }

        // ---- check ------------------------------------------------------------------------------------------------------

        static string OjsCounters()
        {
            int[] c = PatchCounters();
            string sites = "";
            for (int i = 0; i < sOjsSiteErrors.Length; i++)
                if (sOjsSiteErrors[i] > 0) sites += (sites.Length > 0 ? ", " : "") + kOjsSites[i] + " " + sOjsSiteErrors[i];
            return "linewait: line waits " + c[kLwWaits] + ", sim-min " + (c[kLwTenths] / 10f).ToString("0.0") + ", timed out " + c[kLwTimeouts] + ", cancelled " + c[kLwCancelled]
                + ", shorter (not written) " + c[kLwShort] + ", line full " + c[kLwLineFull]
                + "; rhdoor waits " + c[kLwRhWaits] + ", sim-min " + (c[kLwRhTenths] / 10f).ToString("0.0") + ", timed out " + c[kLwRhTimeouts] + ", cancelled " + c[kLwRhCancelled]
                + "; rhshare: walked to a held slot " + c[kRhsShared] + ", dead reservations removed " + c[kRhsStale] + ", left the wait on a cancel " + c[kRhsCancelLeft]
                + ", kept (holder out of scope, horse, rider, carrier) " + c[kRhsKept] + ", would " + c[kRhsWould] + ", exempt " + c[kRhsExempt]
                + "; servingshare: runs at a busy platter " + c[kSvsRuns] + ", lines skipped " + c[kSvsLineSkips] + ", took with another Sim " + c[kSvsShared] + ", last serving refused " + c[kSvsLastGuard]
                + ", kept (a Sim out of scope there) " + c[kSvsKept] + ", would " + c[kSvsWould] + ", exempt " + c[kSvsExempt] + ", open runs " + sOjsServe.Count
                + "; errors " + c[kOjsErrors] + (sites.Length > 0 ? " (" + sites + "; last: " + sOjsLastError + ")" : "") + "; " + GlMembers(sOjsMissing)
                + (sOjsLast.Length > 0 ? "; last: " + sOjsLast : "") + (sOjsLastShare.Length > 0 ? "; last share: " + sOjsLastShare : "");
        }

        // ObjShareCheck run: the rules with controls (read-only). Bug cases: a Sim's enter slot is held by another Sim
        // in scope (the game waits, rhshare walks); a Sim grabs a serving while others are at the platter (the game
        // queues, servingshare does not). result: the counters and the last lines. lot: lines with Sims in them on the
        // selected Sim's lot, platters there, and rabbit hole slots that are reserved now.
        public static object ObjShareCheck(string what)
        {
            if (what == "result") return OjsCounters();
            if (what == "lot") return OjsLot();
            if (what != "run") return "objshare <run|result|lot>";
            bool rhOn = ShortcutOn("rhshare"), svOn = ShortcutOn("servingshare");
            const int Go = ObjShareCore.kGo;
            bool rhFixed = ObjShareCore.Rh(rhOn, true, true, true, true, true, true) == Go;
            bool svFixed = ObjShareCore.Serve(svOn, false, true, true) == Go;
            bool ctlRh = ObjShareCore.Rh(true, false, true, true, true, true, true) == ObjShareCore.kNone && ObjShareCore.Rh(true, true, false, true, true, true, true) == ObjShareCore.kExempt
                && ObjShareCore.Rh(true, true, true, false, true, true, true) == ObjShareCore.kNone && ObjShareCore.Rh(true, true, true, true, true, false, true) == ObjShareCore.kKeep
                && ObjShareCore.Rh(true, true, true, true, true, true, false) == ObjShareCore.kKeep && ObjShareCore.Rh(true, true, true, true, false, false, false) == ObjShareCore.kStale
                && ObjShareCore.Rh(false, true, true, true, true, true, true) == ObjShareCore.kWould && !ObjShareCore.RhBreak(15f) && ObjShareCore.RhBreak(15.1f);
            bool ctlSv = ObjShareCore.Serve(true, true, true, true) == ObjShareCore.kNone && ObjShareCore.Serve(true, false, false, true) == ObjShareCore.kExempt
                && ObjShareCore.Serve(true, false, true, false) == ObjShareCore.kKeep && ObjShareCore.Serve(false, false, true, true) == ObjShareCore.kWould;
            bool ctlLast = !ObjShareCore.ServeLeft(1, 1, false) && ObjShareCore.ServeLeft(2, 1, false) && ObjShareCore.ServeLeft(1, 0, false) && !ObjShareCore.ServeLeft(0, 0, false) && ObjShareCore.ServeLeft(0, 3, true);
            bool ctlLog = ObjShareCore.LineOutcome(true, 3f, 10f) == ObjShareCore.kGotTurn && ObjShareCore.LineOutcome(false, 10f, 10f) == ObjShareCore.kTimedOut
                && ObjShareCore.LineOutcome(false, 3f, 10f) == ObjShareCore.kCancelled && !ObjShareCore.LineLogged(0.4f) && ObjShareCore.LineLogged(0.5f)
                && ObjShareCore.RhOutcome(2f) == ObjShareCore.kGotTurn && ObjShareCore.RhOutcome(15f) == ObjShareCore.kTimedOut;
            bool controls = ctlRh && ctlSv && ctlLast && ctlLog;
            bool on = rhOn && svOn, fixedNow = rhFixed && svFixed;
            string verdict = rhOn != svOn ? (controls && sOjsMissing.Length == 0 ? "MIXED (one switch on)" : "UNEXPECTED") : GlVerdict(on, fixedNow, controls, sOjsMissing.Length == 0);
            return "objshare check (rhshare " + (rhOn ? "on" : "off") + ", household " + (ShortcutOn("rhsharehh") ? "on" : "off") + ", servingshare " + (svOn ? "on" : "off") + ", household "
                + (ShortcutOn("servingsharehh") ? "on" : "off") + ", linewaitlog " + (ShortcutOn("linewaitlog") ? "on" : "off") + "): a held enter slot is walked to=" + rhFixed
                + ", servings are grabbed without the line=" + svFixed + "; control: free slot, out of scope, horse / rider / carrier, holder out of scope, dead holder, stale age=" + ctlRh
                + ", inventory, scope, a Sim out of scope at the platter=" + ctlSv + ", last serving taken once, buffet never empty=" + ctlLast + ", wait outcomes and the half-minute floor=" + ctlLog
                + "; controls ok=" + controls + "; verdict: " + verdict + "; " + OjsCounters();
        }

        static string OjsLot()
        {
            Sim sel = PlumbBob.SelectedActor;
            if (sel == null || sel.LotCurrent == null) return "objshare lot: no selected Sim on a lot";
            System.Text.StringBuilder b = new System.Text.StringBuilder("objshare lot: " + sel.FullName + ";");
            int lines = 0, busy = 0, platters = 0;
            foreach (GameObject o in sel.LotCurrent.GetObjects<GameObject>())
            {
                ServingContainerGroup g = o as ServingContainerGroup;
                if (g != null)
                {
                    platters++;
                    if (b.Length < 900) b.Append(" " + g.GetType().Name + " at " + V(g.Position) + ": " + g.NumServingsLeft + " servings, in use by " + g.UseCount + (g.Parent is IBuffetTable ? ", on a buffet" : "") + ";");
                }
                SimQueue q = o.SimLine;
                if (q == null) continue;
                lines++;
                int n = q.MemberCount();
                if (n == 0) continue;
                busy++;
                if (b.Length < 900) b.Append(" line of " + o.GetType().Name + " at " + V(o.Position) + ": " + n + " Sims;");
            }
            int holes = 0, reserved = 0;
            foreach (RabbitHole rh in Sims3.Gameplay.Queries.GetObjects<RabbitHole>())
            {
                holes++;
                RabbitHole p = rh.RabbitHoleProxy;
                if (p == null || p.ReservedEnterSlotsPerPerson == null || p.ReservedEnterSlotsPerPerson.Count == 0) continue;
                reserved++;
                int waiting = 0;
                foreach (List<ObjectGuid> l in p.WaitingToEnterSims.Values) if (l != null) waiting += l.Count;
                if (b.Length < 1400) b.Append(" " + rh.GetType().Name + ": " + p.ReservedEnterSlotsPerPerson.Count + " enter slots reserved, " + waiting + " Sims waiting;");
            }
            return b.Append(" objects with a line " + lines + ", with Sims in it " + busy + ", platters " + platters + ", rabbit holes " + holes + ", with a reserved enter slot " + reserved).ToString();
        }
    }
}
