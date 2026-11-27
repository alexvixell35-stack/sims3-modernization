using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Objects;

namespace Ts3Fixes
{
    // rinkexit (2026-11-27, switch off by default): skating rinks (ice and roller, every SkatingRink subclass) let a skater leave
    // only when it heads its exit's queue (CanSimExitNow: SimQueue.Peek() == the Sim). EA's FinishedWaitingAtExit, run from
    // Skate.Cleanup, checks that the leaving Sim is ANYWHERE in the queue and then Dequeue()s the HEAD. So a Sim that leaves
    // while 2nd in line (reset, or its skate ends another way) removes the Sim in front of it and leaves its own id behind. That
    // id becomes the head, CanSimExitNow is false for every Sim behind it, and DoSkate keeps them on the ice in TryingToExit;
    // a cancel does not help (DoSkate clears exit reasons each pass). Seen in game 2026-11-27 (repro skate-exit-ghost, ice and
    // roller): A then B queued, FinishedWaitingAtExit(B) left [B]; a skater sent to that exit was still skating 5 min after
    // its cancel while the other exit's skaters left.
    // Fix: FinishedWaitingAtExit removes the leaving Sim's own id (order kept); CanSimExitNow first drops head ids whose Sim is
    // gone or no longer skating this rink (ghosts left by EA's dequeue, or by a Sim reset without its cleanup), so an exit
    // already blocked in a save frees itself.
    // Counters: [4006] leaving Sim removed where EA would have removed the head instead, [4007] ghost heads dropped, [4008] errors.
    public static partial class Fixes
    {
        static readonly FieldInfo sRxExits = typeof(SkatingRink).GetField("mExits", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo sRxQueue = RxQueueField();

        static FieldInfo RxQueueField()
        {
            Type t = typeof(SkatingRink).GetNestedType("RinkExit", BindingFlags.NonPublic);
            return t != null ? t.GetField("SimQueue", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) : null;
        }

        static void ApplyRinkExit()
        {
            if (sRxExits == null || sRxQueue == null) { PatchCounters()[4008]++; return; }  // another game version: leave EA's code alone
            Patch(typeof(SkatingRink), "FinishedWaitingAtExit", new Type[] { typeof(int), typeof(Sim), typeof(SkatingRink.Skate) },
                typeof(RinkExitHook), "ExitHook", "ExitOrig");
            Patch(typeof(SkatingRink), "CanSimExitNow", new Type[] { typeof(int), typeof(Sim) },
                typeof(RinkExitHook), "CanHook", "CanOrig");
        }

        // the RinkExit element of exit `exit`, or null
        static object RxExit(SkatingRink rink, int exit)
        {
            Array a = sRxExits.GetValue(rink) as Array;
            if (a == null || exit < 0 || exit >= a.Length) return null;
            return a.GetValue(exit);
        }

        // true when the Sim behind `id` still skates `rink` (in its use list, or its current interaction is a Skate on it)
        static bool RxLive(SkatingRink rink, ulong id)
        {
            SimDescription d = SimDescription.Find(id);
            Sim s = d != null ? d.CreatedSim : null;
            if (s == null || s.HasBeenDestroyed) return false;
            if (rink.IsActorUsingMe(s)) return true;
            SkatingRink.Skate k = s.CurrentInteraction as SkatingRink.Skate;
            return k != null && object.ReferenceEquals(k.Target, rink);
        }

        public class RinkExitHook
        {
            public void ExitHook(int exit, Sim s, SkatingRink.Skate interaction)
            {
                if (!ShortcutOn("rinkexit")) { ExitOrig(exit, s, interaction); return; }
                try
                {
                    SkatingRink rink = (SkatingRink)(object)this;
                    object e = RxExit(rink, exit);
                    Queue<ulong> q = e != null ? sRxQueue.GetValue(e) as Queue<ulong> : null;
                    if (q == null || s == null || s.SimDescription == null) return;
                    ulong id = s.SimDescription.SimDescriptionId;
                    if (!q.Contains(id)) return;
                    if (q.Peek() != id) PatchCounters()[4006]++;
                    Queue<ulong> kept = new Queue<ulong>();
                    foreach (ulong x in q) if (x != id) kept.Enqueue(x);
                    sRxQueue.SetValue(e, kept.Count > 0 ? kept : null);  // EA nulls an empty queue too
                }
                catch (Exception) { PatchCounters()[4008]++; }
            }
            public void ExitOrig(int exit, Sim s, SkatingRink.Skate interaction) { } // receives SkatingRink.FinishedWaitingAtExit's original header

            public bool CanHook(int exit, Sim s)
            {
                if (ShortcutOn("rinkexit"))
                {
                    try
                    {
                        SkatingRink rink = (SkatingRink)(object)this;
                        object e = RxExit(rink, exit);
                        Queue<ulong> q = e != null ? sRxQueue.GetValue(e) as Queue<ulong> : null;
                        ulong me = s != null && s.SimDescription != null ? s.SimDescription.SimDescriptionId : 0UL;
                        bool changed = false;
                        while (q != null && q.Count > 0 && q.Peek() != me && !RxLive(rink, q.Peek())) { q.Dequeue(); PatchCounters()[4007]++; changed = true; }
                        if (changed && q.Count == 0) sRxQueue.SetValue(e, null);
                    }
                    catch (Exception) { PatchCounters()[4008]++; }
                }
                return CanOrig(exit, s);
            }
            public bool CanOrig(int exit, Sim s) { return false; } // receives SkatingRink.CanSimExitNow's original header
        }
    }
}
