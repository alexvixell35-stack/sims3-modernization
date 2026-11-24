using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Interactions;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // queuewake (docs/686 §23, default off; docs/684 E1). An idle Sim looks at its queue only when its loop wakes.
    //
    // [verified, decompiled] Sim.DoInteraction with an empty queue: posture and idle checks, LoopIdle(), then (no yield
    // in between) Simulator.Sleep(kSimLoopSleepTicksWhenAutonomyDisabled = 20) when autonomy is off for the Sim,
    // Sleep(kSimLoopSleepTicksWhenInAutonomyQueue = 10) while it waits in the autonomy manager's queue, else it adds
    // itself to that queue and sleeps 2. An action the player gives, or the one autonomy picks for it, is added from
    // another task and waits for that sleep to end: up to 10 or 20 ticks (0.27 / 0.53 s at speed 1).
    // Fix: at the end of LoopIdle (hook; its posture idle can yield, so the mark is set after it) of a household or selected Sim with an empty queue and nothing running, on its own task, sets
    // a deadline = now + the sleep the loop is about to take (10 or 20; 2-tick sleeps are left). InteractionInstance.
    // OnAddedToQueue (hook, after the game's body): the Sim's first entry added from another task before that deadline
    // wakes the Sim's task (Simulator.Wake, assertIfNotSleeping off), so it runs ProcessOneInteraction at once. Any add
    // drops the mark; past the deadline the loop has woken by itself and may be in another sleep (an animation's), so it
    // is never woken then. NPCs are never marked.
    // Scope: household and selected Sims (user-approved 2026-11-24). Off = compare mode: counted with the ticks it would
    // save. queuewakelog writes one line per wake (capped). Counters 4596-4601. Check: QueueWakeCheck. Swaps: Sim.LoopIdle,
    // InteractionInstance.OnAddedToQueue (both stacked, with a stub).
    public static partial class Fixes
    {
        const int kQwWakes = 4596, kQwWould = 4597, kQwTicks = 4598, kQwStale = 4599, kQwNotIdle = 4600, kQwErrors = 4601;

        static readonly Dictionary<ulong, long> sQwMarks = new Dictionary<ulong, long>();  // Sim ObjectId -> simulator tick the loop sleep ends
        static readonly LineCapCore sQwCap = new LineCapCore(20, 60000, 300);
        static uint sQwOff = 20, sQwIn = 10, sQwNot = 2;
        static string sQwLast = "", sQwLastError = "", sQwMissing = "";

        static void ApplyQueueWake()
        {
            sQwMissing = "";
            sQwOff = QwTicks("kSimLoopSleepTicksWhenAutonomyDisabled", 20); sQwIn = QwTicks("kSimLoopSleepTicksWhenInAutonomyQueue", 10); sQwNot = QwTicks("kSimLoopSleepTicksWhenNotInQueue", 2);
            if (!Patch(typeof(Sim), "LoopIdle", Type.EmptyTypes, typeof(QwIdleHook), "IdleHook", "IdleOrig")) sQwMissing += "Sim.LoopIdle ";
            if (!Patch(typeof(InteractionInstance), "OnAddedToQueue", Type.EmptyTypes, typeof(QwAddHook), "AddedHook", "AddedOrig")) sQwMissing += "InteractionInstance.OnAddedToQueue";
            sQwMissing = sQwMissing.Trim();
            if (sQwMissing.Length > 0) AppliedLog().Add("queuewake: not found: " + sQwMissing);
        }

        static uint QwTicks(string field, uint dflt)
        {
            FieldInfo f = typeof(Sim).GetField(field, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (f == null) { sQwMissing += "Sim." + field + " "; return dflt; }
            object v = f.GetValue(null);
            return v is uint ? (uint)v : dflt;
        }

        // world load: the old world's tasks are gone
        static void QueueWakeWorldLoad() { sQwMarks.Clear(); }

        // queuewake (docs/686 §23): the idle loop of a household Sim is about to sleep: note until when.
        public class QwIdleHook
        {
            public void IdleHook()
            {
                IdleOrig();  // first: the posture's idle can yield (RequestState); after it nothing yields before the loop's sleep
                Sim me = (Sim)(object)this;
                try
                {
                    Sims3.Gameplay.ActorSystems.InteractionQueue q = me.InteractionQueue;
                    if (q != null && q.Count == 0 && q.GetCurrentInteraction() == null && q.TransitionInteraction == null && Simulator.CurrentTask == me.ObjectId && IsPlayerSim(me))
                    {
                        uint t = QueueWakeCore.Sleep(AutonomyRestrictions.IsAnyAutonomyEnabled(me), me.Autonomy != null && me.Autonomy.InAutonomyManagerQueue, sQwOff, sQwIn, sQwNot);
                        if (t != 0) { if (sQwMarks.Count < 512) sQwMarks[me.ObjectId.Value] = Simulator.TicksElapsed() + t; }
                        else sQwMarks.Remove(me.ObjectId.Value);
                    }
                }
                catch (ResetException) { throw; }
                catch (Exception e) { QwError(e); }
            }

            public void IdleOrig() { }  // receives Sim.LoopIdle's original body (or the hook stacked before ours)
        }

        // queuewake: an entry was added; wake the Sim's idle loop when this is its first entry.
        public abstract class QwAddHook : InteractionInstance
        {
            public void AddedHook()
            {
                AddedOrig();
                if (sQwMarks.Count != 0) QwAdded(this);
            }

            public void AddedOrig() { }  // receives InteractionInstance.OnAddedToQueue's body (or the hook stacked before ours)
        }

        static void QwAdded(InteractionInstance ii)
        {
            try
            {
                Sim s = ii.InstanceActor;
                if (s == null) return;
                ulong id = s.ObjectId.Value;
                long deadline;
                if (!sQwMarks.TryGetValue(id, out deadline)) return;
                sQwMarks.Remove(id);
                Sims3.Gameplay.ActorSystems.InteractionQueue q = s.InteractionQueue;
                long now = Simulator.TicksElapsed();
                bool only = q != null && q.Count == 1 && q.GetHeadInteraction() == ii && q.GetCurrentInteraction() == null && q.TransitionInteraction == null;
                int step = QueueWakeCore.Decide(true, now, deadline, only, Simulator.CurrentTask == s.ObjectId, true);
                int[] c = PatchCounters();
                if (step == QueueWakeCore.kStale) { c[kQwStale]++; return; }
                if (step != QueueWakeCore.kWake) { if (step == QueueWakeCore.kNotIdle) c[kQwNotIdle]++; return; }
                int saved = (int)(deadline - now);
                if (!ShortcutOn("queuewake")) { c[kQwWould]++; c[kQwTicks] += saved; return; }
                Simulator.Wake(s.ObjectId, false);
                c[kQwWakes]++; c[kQwTicks] += saved;
                sQwLast = s.FullName + " woken " + saved + " ticks early for " + ii.GetType().Name;
                if (ShortcutOn("queuewakelog") && sQwCap.Take(System.Environment.TickCount)) DiagLine("queuewake: " + sQwLast);
            }
            catch (ResetException) { throw; }
            catch (Exception e) { QwError(e); }
        }

        static void QwError(Exception e)
        {
            int n = PatchCounters()[kQwErrors]++;
            sQwLastError = "queuewake: error: " + e.GetType().Name + ": " + e.Message;
            if (n < 3) DiagLine(sQwLastError);
        }

        // ---- check ----

        static string QwCounters()
        {
            int[] c = PatchCounters();
            int n = c[kQwWakes] + c[kQwWould];
            return "queuewake: idle loops woken " + c[kQwWakes] + "; off: would " + c[kQwWould] + "; loop ticks saved " + c[kQwTicks]
                + (n > 0 ? " (mean " + (c[kQwTicks] / (float)n).ToString("0.0") + " ticks)" : "") + "; left: past the deadline " + c[kQwStale] + ", not the first entry " + c[kQwNotIdle]
                + "; marks open " + sQwMarks.Count + "; loop sleeps " + sQwOff + "/" + sQwIn + "/" + sQwNot + "; errors " + c[kQwErrors] + (sQwLastError.Length > 0 ? " (last: " + sQwLastError + ")" : "")
                + "; " + GlMembers(sQwMissing) + (sQwLast.Length > 0 ? "; last: " + sQwLast : "");
        }

        // QueueWakeCheck run: the rules with controls. result: the counters.
        public static object QueueWakeCheck(string what)
        {
            if (what == "result") return QwCounters();
            if (what != "run") return "queuewake <run|result>";
            bool on = ShortcutOn("queuewake");
            bool fixedNow = QueueWakeCore.Decide(true, 100, 110, true, false, true) == QueueWakeCore.kWake && QueueWakeCore.Sleep(true, true, 20, 10, 2) == 10 && on;
            bool controls = QueueWakeCore.Decide(true, 110, 110, true, false, true) == QueueWakeCore.kStale && QueueWakeCore.Decide(true, 100, 110, false, false, true) == QueueWakeCore.kNotIdle
                && QueueWakeCore.Decide(true, 100, 110, true, true, true) == QueueWakeCore.kOwnTask && QueueWakeCore.Decide(false, 100, 110, true, false, true) == QueueWakeCore.kNoMark
                && QueueWakeCore.Sleep(true, false, 20, 10, 2) == 0;
            return "queuewake check (switch " + (on ? "on" : "off") + "): the first entry of an idle household Sim wakes its loop=" + fixedNow
                + "; control: past the deadline, not the first entry, own task, no mark, 2-tick sleep kept=" + controls + "; verdict: " + GlVerdict(on, fixedNow, controls, sQwMissing.Length == 0) + "; " + QwCounters();
        }
    }
}
