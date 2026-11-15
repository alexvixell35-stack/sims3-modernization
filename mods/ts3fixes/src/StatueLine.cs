using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects.CookingObjects;
using Sims3.Gameplay.Objects.Electronics;
using Sims3.Gameplay.Objects.Environment;
using Sims3.Gameplay.Objects.HobbiesSkills;
using Sims3.Gameplay.Objects.MinorPets;
using Sims3.Gameplay.Objects.Misc;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // statueline: docs/559 second-pass gameplay scan. Off by default. Harness check: StatueLineChecks.cs.
    // statueline (tuning kStatueLine; docs/559 §7). BoxOfDanger+DoTrick: the Human Statue trick queues every performer in a
    // static, persisted Queue<Sim> and waits `while (line.Peek() != Actor) Sleep(1)` inside an animation event [verified].
    // Cleanup does `if (line.Contains(Actor)) line.Dequeue()`, which removes the head (another performer) instead of Actor
    // when Actor was cancelled or reset while waiting; Actor's stale entry then blocks every later statue trick, and the
    // line is saved with the game. Fix: Cleanup removes Actor itself; before each pose wait, entries of Sims that are gone
    // or not doing a trick are dropped (a saved stale line clears on the first trick). Counters [2461] entries fixed
    // (wrong dequeues avoided + stale entries dropped; wrong dequeues counted with the switch off too), [2462] errors.
    public static partial class Fixes
    {
        static FieldInfo sSlLine;

        static void ApplyStatueLine()
        {
            const BindingFlags stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            sSlLine = typeof(BoxOfDanger).GetField("mStatueLine", stat);
            if (sSlLine == null) AppliedLog().Add("statueline: BoxOfDanger.mStatueLine not found, not patched");
            else
            {
                Patch(typeof(BoxOfDanger.DoTrick), "Cleanup", typeof(SlHook), "CleanupHook", "CleanupOrig");
                Patch(typeof(BoxOfDanger.DoTrick), "LowLODPose", typeof(SlHook), "LowHook", "LowOrig");
                Patch(typeof(BoxOfDanger.DoTrick), "HighLODPose", typeof(SlHook), "HighHook", "HighOrig");
            }
        }

        public class SlHook
        {
            public void CleanupHook()
            {
                try { SlBeforeCleanup((object)this as BoxOfDanger.DoTrick); } catch (Exception) { PatchCounters()[2462]++; }
                CleanupOrig();
            }
            public void CleanupOrig() { } // receives BoxOfDanger+DoTrick.Cleanup's original body

            public void LowHook()
            {
                try { SlPurge(); } catch (Exception) { PatchCounters()[2462]++; }
                LowOrig();
            }
            public void LowOrig() { } // receives BoxOfDanger+DoTrick.LowLODPose's original body

            public void HighHook()
            {
                try { SlPurge(); } catch (Exception) { PatchCounters()[2462]++; }
                HighOrig();
            }
            public void HighOrig() { } // receives BoxOfDanger+DoTrick.HighLODPose's original body
        }

        // Removes one Sim from the line wherever it stands (order of the others kept).
        static void SlRemove(Queue<Sim> line, Sim who)
        {
            Sim[] all = line.ToArray();
            line.Clear();
            foreach (Sim s in all) if (!ReferenceEquals(s, who)) line.Enqueue(s);
        }

        // True when EA's Cleanup would dequeue another performer (Actor listed, not at the head).
        static bool SlWrongDequeue(Queue<Sim> line, Sim actor)
        {
            return line != null && line.Count > 0 && line.Contains(actor) && !ReferenceEquals(line.Peek(), actor);
        }

        static void SlBeforeCleanup(BoxOfDanger.DoTrick ia)
        {
            Queue<Sim> line = ia == null ? null : sSlLine.GetValue(null) as Queue<Sim>;
            if (!SlWrongDequeue(line, ia.Actor)) return;
            PatchCounters()[2461]++;
            if (ShortcutOn("statueline")) SlRemove(line, ia.Actor);  // EA's Contains is then false: it dequeues nothing
        }

        // Before a performer joins the wait: entries of Sims gone or no longer doing a trick are dropped.
        static void SlPurge()
        {
            if (!ShortcutOn("statueline")) return;
            Queue<Sim> line = sSlLine.GetValue(null) as Queue<Sim>;
            if (line == null || line.Count == 0) return;
            foreach (Sim s in line.ToArray())
            {
                bool alive = !ReferenceEquals(s, null) && !s.HasBeenDestroyed && s.InteractionQueue != null
                    && s.InteractionQueue.GetCurrentInteraction() is BoxOfDanger.DoTrick;
                if (alive) continue;
                SlRemove(line, s);
                PatchCounters()[2461]++;
            }
        }
    }
}
