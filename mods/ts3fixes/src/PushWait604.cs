using System;
using System.Collections.Generic;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.ObjectComponents;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // pushwaitskip (kPushWaitSkip, default off; docs/604 §2). [verified, decompiled + IL] RoutingComponent.PushSimsStatic
    // (8 arguments; the 6-argument overload and Sim.PushSims... call it) pushes the Sims in the way and, with
    // waitForPushes, idles the pusher until every pushed Sim is done: each pass drops the Sims that are destroyed or no
    // longer in a push, then waits 10 sim-minutes, for at most TotalSimMinutesToWaitForSimsToBePushed (15). EA's pass is
    // `for (int i = 0; i < list.Count; i++) { if (done) list.RemoveAt(i); else i++; }`: the manual `i++` (the shape of
    // the conversation filter above it, a while loop) plus the for's own `i++` step two, so a busy Sim hides the next
    // one and a removal hides the one that slides into its index (IL_01c5-IL_021c: two `add 1` on the keep path). A Sim
    // that is done but was skipped keeps the pusher waiting one more 10-minute pass (or to the 15-minute timeout): with
    // two or more Sims in the way the pusher (often a household Sim going to a bed, sofa or stove) stands idle about 10
    // sim-minutes longer than EA's own test says. Fix as EA intended: our copy of the method, identical except that
    // the pass steps once on keep and not at all on remove. Same Sims pushed, same interactions, same order; only the
    // number of 10-minute waits can go down. Household Sims: EA bug fix, applies to everyone.
    // Saved stacks: the method yields (LoopIdle, WaitForExitReason, Sleep), so the swap keeps a stub (PushOrig) and a
    // swap-stack-audit row; a frame saved in our copy resumes in EA's IL at load (SavedStacks.cs), which is EA's method.
    // Counters: [2790] done Sims EA's pass would have skipped (each saves a wait pass), [2791] waiting calls run on EA's
    // body (switch off), [2792] errors (rethrown, as EA's body would throw).
    public static partial class Fixes
    {
        static bool sPwReady;

        static void ApplyPushWaitSkip()
        {
            Type[] args = { typeof(Sim), typeof(List<Sim>), typeof(float), typeof(float), typeof(bool), typeof(bool), typeof(InteractionPriority), typeof(bool) };
            Patch(typeof(RoutingComponent), "PushSimsStatic", args, typeof(PwHook), "PushHook", "PushOrig");
            sPwReady = EpLive(typeof(PwHook), 1).Length == 0;
        }

        // Pure: one wait pass over the done flags. EA's double step (fixedPass false) or one step (true). Returns the
        // flags left in the list, in order.
        internal static List<bool> PwPass(List<bool> done, bool fixedPass)
        {
            List<bool> l = new List<bool>(done);
            if (fixedPass)
            {
                int i = 0;
                while (i < l.Count) { if (l[i]) l.RemoveAt(i); else i++; }
            }
            else
            {
                for (int i = 0; i < l.Count; i++) { if (l[i]) l.RemoveAt(i); else i++; }
            }
            return l;
        }

        static int PwDoneLeft(List<bool> l) { int n = 0; foreach (bool b in l) if (b) n++; return n; }

        // EA's test for "this pushed Sim is finished" (no side effects: three getters and a queue scan).
        static bool PwDone(Sim sim)
        {
            return sim.HasBeenDestroyed || (!sim.InteractionQueue.HasInteractionOfType(SimRoutingComponent.BePushed.Singleton) && !sim.SimRoutingComponent.IsInvolvedInPush);
        }

        public class PwHook
        {
            public static bool PushHook(Sim actor, List<Sim> sims, float minPushAwayDist, float maxPushAwayDist, bool allowPushOutOfConversation, bool waitForPushes, InteractionPriority priority, bool bForceAllowPushOutOfInteraction)
            {
                if (!waitForPushes) return PushOrig(actor, sims, minPushAwayDist, maxPushAwayDist, allowPushOutOfConversation, waitForPushes, priority, bForceAllowPushOutOfInteraction);
                if (!ShortcutOn("pushwaitskip"))
                {
                    PatchCounters()[2791]++;
                    return PushOrig(actor, sims, minPushAwayDist, maxPushAwayDist, allowPushOutOfConversation, waitForPushes, priority, bForceAllowPushOutOfInteraction);
                }
                try { return PwBody(actor, sims, minPushAwayDist, maxPushAwayDist, allowPushOutOfConversation, priority, bForceAllowPushOutOfInteraction); }
                catch (ResetException) { throw; }
                catch (Exception) { PatchCounters()[2792]++; throw; }
            }

            public static bool PushOrig(Sim actor, List<Sim> sims, float minPushAwayDist, float maxPushAwayDist, bool allowPushOutOfConversation, bool waitForPushes, InteractionPriority priority, bool bForceAllowPushOutOfInteraction) { return false; }
        }

        // EA's PushSimsStatic with waitForPushes true, statement for statement, except the wait pass (marked).
        static bool PwBody(Sim actor, List<Sim> sims, float minPushAwayDist, float maxPushAwayDist, bool allowPushOutOfConversation, InteractionPriority priority, bool bForceAllowPushOutOfInteraction)
        {
            int num = 0;
            List<Sim> list = new List<Sim>(sims);
            if (actor != null) list.Remove(actor);
            int count = list.Count;
            RoutingComponent.RemoveNonPushableSims(actor, list);
            if (!allowPushOutOfConversation)
            {
                int k = 0;
                while (k < list.Count)
                {
                    if (list[k] == null || list[k].Conversation != null) list.RemoveAt(k);
                    else k++;
                }
            }
            if (list.Count > 0)
            {
                foreach (Sim item in list)
                {
                    InteractionInstance rel = RoutingComponent.GetPushRelevantInteractionForSim(item);
                    SimRoutingComponent.BePushed bePushed = rel as SimRoutingComponent.BePushed;
                    if (item.IsRouting && bePushed == null)
                    {
                        if (item.SimRoutingComponent.RequestMidRoutePush(actor, minPushAwayDist, maxPushAwayDist)) num++;
                        continue;
                    }
                    BePushedCanCancelResult res = BePushedCanCancelResult.Disallow;
                    IBePushedCanCancel canCancel = rel as IBePushedCanCancel;
                    if (rel == null) res = BePushedCanCancelResult.Allow;
                    else
                    {
                        if (bePushed != null && bePushed.Pusher == actor) { num++; continue; }
                        if (canCancel != null) res = canCancel.CanBePushedCancel(actor);
                        else if (bForceAllowPushOutOfInteraction) res = BePushedCanCancelResult.AllowForHigherPriority;
                    }
                    if (res == BePushedCanCancelResult.Disallow) continue;
                    SimRoutingComponent.BePushed push = SimRoutingComponent.BePushed.Singleton.CreateInstance(item, item, priority, false, false) as SimRoutingComponent.BePushed;
                    if (push == null) continue;
                    push.Pusher = actor;
                    push.Hidden = true;
                    push.PushAwayDistanceMin = minPushAwayDist;
                    push.PushAwayDistanceMax = maxPushAwayDist;
                    switch (res)
                    {
                        case BePushedCanCancelResult.Allow:
                            item.InteractionQueue.AddNext(push);
                            num++;
                            break;
                        case BePushedCanCancelResult.AllowForHigherPriority:
                            if (item.InteractionQueue.AddNextIfPossible(push) && item.HasExitReason(ExitReason.HigherPriorityNext)) num++;
                            break;
                    }
                }
                if (actor != null) actor.LoopIdle();
                float left = SimRoutingComponent.TotalSimMinutesToWaitForSimsToBePushed;
                uint step = 10u;
                bool stop = false;
                do
                {
                    // ---- the fix: one step on keep, none on remove (EA steps twice) ----
                    List<bool> flags = new List<bool>(list.Count);
                    for (int i = 0; i < list.Count; i++) flags.Add(PwDone(list[i]));
                    PatchCounters()[2790] += PwDoneLeft(PwPass(flags, false));
                    int j = 0;
                    while (j < list.Count)
                    {
                        if (flags[j]) { list.RemoveAt(j); flags.RemoveAt(j); }
                        else j++;
                    }
                    // ---- end of the fix ----
                    stop = list.Count == 0 || left < 0f;
                    if (stop) continue;
                    DateAndTime before = SimClock.CurrentTime();
                    if (actor != null)
                    {
                        actor.WaitForExitReason(SimClock.ConvertFromTicks(step, TimeUnit.Minutes), ExitReason.Canceled | ExitReason.TimedOut);
                        if (actor.HasExitReason(ExitReason.Canceled)) stop = true;
                    }
                    else Simulator.Sleep(step);
                    left -= SimClock.ElapsedTime(TimeUnit.Minutes, before);
                }
                while (!stop);
            }
            bool ok = true;
            if (actor != null) ok &= !actor.HasExitReason(ExitReason.Canceled | ExitReason.TimedOut);
            return ok & (num == count);
        }

        // PushWaitSkipCheck run: EA's pass vs ours on fixed patterns (pure), plus the swap live. fixed = switch on, our
        // pass leaves no done Sim where EA's leaves one; controls: an all-busy list and a single done Sim are the same
        // either way (EA's pass is right there), and EA's pass does leave a done Sim behind a busy one (the bug is real).
        public static object PushWaitSkipCheck(string what)
        {
            string missing = sPwReady ? "" : "RoutingComponent.PushSimsStatic hook";  // B582Line adds CkLive (EpLive of PwHook)
            string counters = B582Counters("pushwaitskip", 2790, new[] { "skipped done Sims checked", "waits on EA's body", "errors" }, missing);
            if (what == "result") return counters;
            if (what != "run") return "pushwaitskip <run|result>";
            bool on = ShortcutOn("pushwaitskip");
            bool fixedPure = false, controls = false;
            string ctl = "";
            try
            {
                List<bool> busyDone = new List<bool> { false, true }, doneDone = new List<bool> { true, true, true }, busy = new List<bool> { false, false, false }, one = new List<bool> { true };
                bool eaBug = PwDoneLeft(PwPass(busyDone, false)) == 1 && PwDoneLeft(PwPass(doneDone, false)) == 1;
                fixedPure = PwDoneLeft(PwPass(busyDone, true)) == 0 && PwDoneLeft(PwPass(doneDone, true)) == 0 && PwPass(busyDone, true).Count == 1;
                controls = eaBug && PwPass(busy, false).Count == 3 && PwPass(busy, true).Count == 3 && PwPass(one, false).Count == 0 && PwPass(one, true).Count == 0;
                ctl = "EA's pass leaves a done Sim behind a busy one (" + eaBug + "), all-busy and single-done lists the same both ways";
            }
            catch (Exception e) { missing = EpJoin(missing, "threw " + e.GetType().Name); }
            return B582Line("pushwaitskip", on, "one wait pass drops every done Sim", on && fixedPure && sPwReady, ctl, controls, missing, counters);
        }
    }
}
