using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects.Beds;
using Sims3.Gameplay.TuningValues;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // docs/597 §2: need-object and elevator lines. Off by default; acts on household Sims too (EA bug fix, no skip or throttle).
    //
    // queueproximity (kQueueProximity; docs/597 §2). [verified, decompiled] SimQueue.FindNewPlaceInLine's OrderByProximity
    // branch compares the waiting Sim's SQUARED distance to the target with each queued Sim's plain distance
    // (GetDistanceToObject), while SortLineByDistance (run every check period after) sorts on squared distances. A Sim more
    // than 1 m away is placed behind Sims nearer than its squared distance, and when the line is full (place >= max) it is
    // refused (ObjectInUse) where EA's own sort would have put it ahead and evicted the farthest. Lines: elevators
    // (PortalComponent, StayAtCurrentLocation), skating rink and greeting-card booth entrances. Fix: squared on both sides.
    // Counters: [2760] places corrected, [2761] places that differ with the switch off, [2762] errors.
    public static partial class Fixes
    {
        // ---- queueproximity ----

        static void ApplyQueueProximity()
        {
            Patch(typeof(SimQueue), "FindNewPlaceInLine", typeof(SqPlaceHook), "PlaceHook", "PlaceOrig");
        }

        // Pure: EA's OrderByProximity place. others = the queued Sims' distances to the target in line order (EA passes
        // plain distances, the fix squared ones), mine = the new Sim's squared distance. Walks from the back like EA.
        internal static int SqPlace(float[] others, float mine)
        {
            int num = others.Length;
            if (num == 0) return 0;
            while (num > 1)
            {
                if (others[num - 1] < mine) break;
                num--;
            }
            return num;
        }

        public abstract class SqPlaceHook : SimQueue
        {
            protected int PlaceHook(InteractionInstance interaction, QueueRecord record, IGameObject target)
            {
                // Only EA's OrderByProximity branch is touched (CutToHeadOfLine / CutAheadOfLowerPrioritySims are tested first).
                WaitBehavior b = record != null ? record.Behavior : WaitBehavior.BasicWaiting;
                if (record == null || record.Sim == null || target == null || mQueue == null || mQueue.Count < 2
                    || (b & (WaitBehavior.CutToHeadOfLine | WaitBehavior.CutAheadOfLowerPrioritySims)) != 0 || (b & WaitBehavior.OrderByProximity) == 0)
                    return PlaceOrig(interaction, record, target);
                bool on = ShortcutOn("queueproximity");
                int ours;
                try
                {
                    float[] sq = new float[mQueue.Count];
                    for (int i = 0; i < sq.Length; i++) sq[i] = mQueue[i].Sim.GetDistanceToObjectSquared(target);
                    ours = SqPlace(sq, record.Sim.GetDistanceToObjectSquared(target));
                }
                catch (Exception) { PatchCounters()[2762]++; return PlaceOrig(interaction, record, target); }
                if (on) { PatchCounters()[2760]++; return ours; }
                int ea = PlaceOrig(interaction, record, target);
                if (ea != ours) PatchCounters()[2761]++;
                return ea;
            }
            protected int PlaceOrig(InteractionInstance interaction, QueueRecord record, IGameObject target) { return 0; } // SimQueue.FindNewPlaceInLine
        }

        // QueueProximityCheck run: Sims at 0.5 / 3 m, newcomer at 2 m: EA's mixed units put it last (place 2), squared puts
        // it between (1). Controls: empty line 0, one Sim 1 (EA's head is never passed), all nearer stays last.
        public static object QueueProximityCheck(string what)
        {
            string counters = RlCounters("queueproximity", 2760, new[] { "places by squared distance", "places that differ (switch off)", "errors" }, "");
            if (what == "result") return counters;
            if (what != "run") return "queueproximity <run|result>";
            bool on = ShortcutOn("queueproximity");
            int ea = SqPlace(new float[] { 0.5f, 3f }, 4f), sq = SqPlace(new float[] { 0.25f, 9f }, 4f);
            bool fixedNow = on && ea == 2 && sq == 1;
            bool controls = SqPlace(new float[0], 4f) == 0 && SqPlace(new float[] { 9f }, 0f) == 1 && SqPlace(new float[] { 0.25f, 1f }, 4f) == 2;
            return RlLine("queueproximity", on, "2 m newcomer placed ahead of the 3 m Sim (EA's units: last)", fixedNow,
                "empty/one-Sim/all-nearer lines unchanged", controls, "", counters);
        }
    }
}
