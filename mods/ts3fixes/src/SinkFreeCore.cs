namespace Ts3Fixes
{
    // Game-independent rules of SinkFree.cs (sinkfree / sinkfreehh, docs/686 §40), checked offline by tools/selftest/sinkfree.
    //
    // EA's dish cleanup (ServingContainer_CleanUp.GetBestObjectToWashDishes) takes the closest sink of the Sim's own room,
    // else of the lot, whether or not another Sim uses it (an in-use sink only counts as twice as far). In a small house the
    // Sim carrying dishes then waits in the bathroom sink's line behind a teeth brusher while the kitchen sink is free. The
    // rule: when EA's pick is a sink that another Sim uses or heads the line of, the nearest free sink of the lot is used
    // instead; when no sink is free, EA's pick stays. Dishwashers and food synthesizers EA picks are never changed.
    public static class SinkFreeCore
    {
        public const int kNone = 0, kWould = 1, kExempt = 2, kGo = 3;

        // on: the switch for the Sim's group (sinkfree, and sinkfreehh for household Sims); inScope: false for a Sim the
        // switches leave to EA (a household Sim with only sinkfree on). eaIsSink: EA picked a sink; eaBusy: another Sim uses
        // it or heads its line; free: index of the nearest free sink, -1 = none.
        public static int Decide(bool on, bool inScope, bool eaIsSink, bool eaBusy, int free)
        {
            if (!eaIsSink || !eaBusy || free < 0) return kNone;
            if (!inScope) return kExempt;
            return on ? kGo : kWould;
        }

        // Busy for the Sim that carries the dishes: anyone else uses the sink, or another Sim heads its line.
        public static bool Busy(int otherUsers, bool otherHeadsLine) { return otherUsers > 0 || otherHeadsLine; }

        // The nearest usable sink: distance divided by EA's level factor (GlobalFunctions.GetClosestObject's kDifferentLevelScore)
        // for a sink on another level, so a free sink upstairs loses to a free one on the Sim's level as it does for EA.
        public static int Nearest(float[] dist, bool[] usable, bool[] otherLevel, float levelScore)
        {
            int best = -1; float bestD = float.MaxValue;
            if (dist == null || usable == null) return -1;
            for (int i = 0; i < dist.Length && i < usable.Length; i++)
            {
                if (!usable[i]) continue;
                float d = dist[i];
                if (otherLevel != null && i < otherLevel.Length && otherLevel[i] && levelScore > 0f) d /= levelScore;
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }
    }
}
