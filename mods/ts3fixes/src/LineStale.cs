using System;
using System.Collections.Generic;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // linestale (docs/686 §29, default off). The head of an object's line keeps it while the action that took the turn
    // is not there any more.
    //
    // [verified, decompiled SimQueue] The head is mQueue[0]: the Sim whose WaitForTurn answered true. It leaves the line
    // only through RemoveFromQueue: GameObject.RemoveFromUseList (StandardExit), InteractionInstance.Cleanup's
    // RemoveFromLine (only for the action's own Target's line, and only when the Sim is not in its use list) and the
    // explicit calls of some objects. The head has no timeout; the waiters have one (Fridge 15 sim-min, counted from
    // their own walk to the waiting spot). An action that takes another object's line (ServingContainer_CleanUp at the
    // fridge, an inline child action) and ends without its own removal, or an action that took the turn and was put
    // back in the queue behind another action, leaves a head that is not coming. The waiters stand there until their
    // timeout, every time.
    // Fix: the waiter's per-minute check (SimQueue.WaitForExitReasonOnSim, LineSwitch.cs's LswExitHook) looks at the
    // head. Its turn was recorded in LdsQueueHook.WaitHook (the action, the action running at that time, the object).
    // The head is fresh while either action runs (it walks over from any distance, or uses the object) or while it is
    // in the object's use list within 3 m of it (seated on it, standing at it). A head in the use list whose action is
    // over and that stands farther away (s545c: Bill Racket at a computer, still the FridgeCheap head) is not using it:
    // EA's InteractionInstance.Cleanup leaves the line alone for a Sim in the use list (RemoveFromLine's guard). After kLineStaleMinutes (5) of not fresh without a break the head is taken out of the
    // line (RemoveFromQueue, EA's own call): a queued action asks again when it runs (WaitForTurn from its start), an
    // ended one has nothing left to ask. The next waiter's EA loop sees its new place on the same check.
    // Not touched: heads whose turn the fix did not see, heads whose action runs (the player's rule: a Sim walking over
    // keeps its turn, the nearer Sim does not go first), the order of the waiters.
    // Scope: the head is the one moved: NPC heads with linestale, household and selected heads also with linestalehh.
    // Off = compare mode (would). linestalelog: one line per case. Counters 4680-4686. Check: LineStaleCheck.
    public static partial class Fixes
    {
        const int kLstDropped = 4680, kLstWould = 4681, kLstKeptHh = 4682, kLstQueued = 4683, kLstGone = 4684, kLstErrors = 4685, kLstStrayUser = 4686;

        sealed class LstHead
        {
            public Sim Sim;
            public InteractionInstance Ia, Owner;
            public GameObject Target;
            public long StaleSince = -1, Granted;
            public int State;
            public bool Told, ToldFresh;
        }

        static readonly Dictionary<object, LstHead> sLstHeads = new Dictionary<object, LstHead>();
        static readonly LineCapCore sLstCap = new LineCapCore(20, 60000, 300);
        static string sLstLast = "", sLstLastError = "";

        static void LstError(Exception e)
        {
            int n = PatchCounters()[kLstErrors]++;
            sLstLastError = "linestale: error: " + e.GetType().Name + ": " + e.Message;
            if (n < 3) DiagLine(sLstLastError);
        }

        static void LstLine(string text)
        {
            sLstLast = text;
            if (ShortcutOn("linestalelog") && sLstCap.Take(System.Environment.TickCount)) DiagLine("linestale: " + text);
        }

        // LdsQueueHook.WaitHook after EA's wait answered true: this Sim holds the line now.
        static void LstGranted(SimQueue q, InteractionInstance ia, IGameObject target)
        {
            try
            {
                Sim s = ia == null ? null : ia.InstanceActor;
                if (s == null || q == null || !object.ReferenceEquals(q.FirstSim, s)) return;
                if (sLstHeads.Count >= 256) sLstHeads.Clear();  // lines of deleted objects leave entries behind
                LstHead h = new LstHead();
                h.Sim = s; h.Ia = ia; h.Owner = s.CurrentInteraction; h.Target = target as GameObject; h.Granted = SimClock.CurrentTicks;
                sLstHeads[q] = h;
            }
            catch (ResetException) { throw; }
            catch (Exception e) { LstError(e); }
        }

        static bool LstQueued(Sim s, InteractionInstance ia)
        {
            return ia != null && s.InteractionQueue != null && s.InteractionQueue.HasInteraction(ia);
        }

        // A waiter's per-minute check (LswExitHook.ExitHook): is the head still coming?
        static void LstCheck(object queue, Sim waiter)
        {
            SimQueue q = queue as SimQueue;
            if (q == null) return;
            Sim head = q.FirstSim;
            LstHead h;
            if (head == null || head == waiter || !sLstHeads.TryGetValue(q, out h)) return;
            if (!object.ReferenceEquals(h.Sim, head)) { sLstHeads.Remove(q); return; }  // another Sim holds it now
            bool destroyed = head.HasBeenDestroyed;
            InteractionInstance cur = destroyed ? null : head.CurrentInteraction;
            bool uses = !destroyed && h.Target != null && !h.Target.HasBeenDestroyed && h.Target.IsActorUsingMe(head);
            float dist = destroyed || h.Target == null || h.Target.HasBeenDestroyed ? -1f : (head.Position - h.Target.Position).Length();
            bool runs = cur != null && (object.ReferenceEquals(cur, h.Ia) || object.ReferenceEquals(cur, h.Owner));
            bool queued = !destroyed && (LstQueued(head, h.Ia) || LstQueued(head, h.Owner));
            int state = LineStaleCore.State(true, destroyed, uses, dist, runs, queued);
            if (state == LineStaleCore.kFresh)
            {
                h.StaleSince = -1;
                if (!h.ToldFresh && OjsMinutes(h.Granted) >= 10f)  // a long hold by a head that counts as coming: what it does (linestalelog)
                {
                    h.ToldFresh = true;
                    LstLine("kept, head holds " + OjsMinutes(h.Granted).ToString("0.0") + " sim-min and counts as coming: " + head.FullName + " " + ShlWho(head) + ", took the turn with "
                        + LstName(h.Ia) + (object.ReferenceEquals(h.Owner, h.Ia) ? "" : " (in " + LstName(h.Owner) + ")") + ", now " + LstName(cur) + (head.IsRouting ? " (walking)" : "")
                        + (uses ? ", in the use list" : "") + (dist < 0f ? "" : ", " + dist.ToString("0.0") + " m from the " + h.Target.GetType().Name) + ", waiter " + waiter.FullName);
                }
                return;
            }
            if (h.StaleSince < 0) { h.StaleSince = SimClock.CurrentTicks; h.State = state; return; }
            h.State = state;
            float stale = OjsMinutes(h.StaleSince);
            bool hhOn = ShortcutOn("linestalehh");
            bool inScope = !destroyed && ShlScope(head, hhOn) || destroyed;
            int d = LineStaleCore.Decide(ShortcutOn("linestale"), inScope, state, stale, Instantiator.kLineStaleMinutes);
            if (d == LineStaleCore.kWait) return;
            int[] c = PatchCounters();
            string what = (destroyed ? "(gone)" : head.FullName + " " + ShlWho(head)) + ", "
                + (state == LineStaleCore.kQueued ? "its " + LstName(h.Ia) + " queued behind " + LstName(cur) : "its " + LstName(h.Ia) + " ended" + (state == LineStaleCore.kStrayUser ? " (still in the use list)" : "") + ", now " + LstName(cur))
                + ", stale " + stale.ToString("0.0") + " sim-min, " + (h.Target == null ? "?" : h.Target.GetType().Name) + " line, waiter " + waiter.FullName + " " + ShlWho(waiter)
                + (dist < 0f ? "" : ", " + dist.ToString("0.0") + " m from it");
            if (d == LineStaleCore.kDrop)
            {
                q.RemoveFromQueue(head);  // EA's own removal; a queued action asks again when it runs
                sLstHeads.Remove(q);
                c[kLstDropped]++;
                c[state == LineStaleCore.kQueued ? kLstQueued : state == LineStaleCore.kStrayUser ? kLstStrayUser : kLstGone]++;
                LstLine("dropped head: " + what);
                return;
            }
            if (h.Told) return;  // one count per stale head
            h.Told = true;
            if (d == LineStaleCore.kWould) { c[kLstWould]++; c[state == LineStaleCore.kQueued ? kLstQueued : state == LineStaleCore.kStrayUser ? kLstStrayUser : kLstGone]++; LstLine("would drop head (off): " + what); }
            else { c[kLstKeptHh]++; LstLine("kept, household head without linestalehh: " + what); }
        }

        static string LstName(InteractionInstance ia)
        {
            if (ia == null) return "none";
            try { return ia.GetType().Name + (ia.Target == null ? "" : " target " + ia.Target.GetType().Name); }
            catch (ResetException) { throw; }
            catch (Exception) { return ia.GetType().Name; }
        }

        static string LstCounters()
        {
            int[] c = PatchCounters();
            return "linestale: dropped " + c[kLstDropped] + "; off: would " + c[kLstWould] + "; kept (household) " + c[kLstKeptHh]
                + "; stale kind: queued " + c[kLstQueued] + ", ended " + c[kLstGone] + ", ended but still in the use list " + c[kLstStrayUser] + "; heads watched " + sLstHeads.Count + "; errors " + c[kLstErrors]
                + (sLstLastError.Length > 0 ? " (last: " + sLstLastError + ")" : "") + (sLstLast.Length > 0 ? "; last: " + sLstLast : "");
        }

        // LineStaleCheck result: the counters and the last line. run: the rules.
        public static object LineStaleCheck(string what)
        {
            if (what == "result") return LstCounters();
            if (what != "run") return "linestale <run|result>";
            bool ok = LineStaleCore.Decide(true, true, LineStaleCore.State(true, false, false, -1f, false, true), 6f, 5f) == LineStaleCore.kDrop
                && LineStaleCore.Decide(true, true, LineStaleCore.State(true, false, false, -1f, true, true), 60f, 5f) == LineStaleCore.kKeep;
            return "linestale: rules " + (ok ? "ok" : "FAILED") + ", switch " + (ShortcutOn("linestale") ? "on" : "off") + (ShortcutOn("linestalehh") ? " (+hh)" : "") + "; " + LstCounters();
        }
    }
}
