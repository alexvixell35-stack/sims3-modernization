namespace Ts3Fixes
{
    // Game-independent rules of SinkFree.cs (sinkfree / sinkfreehh, docs/686 §40), checked offline by tools/selftest/sinkfree.
    //
    // EA's dish cleanup (ServingContainer_CleanUp.GetBestObjectToWashDishes) takes the closest sink of the Sim's own room,
    // else of the lot, whether or not another Sim uses it (an in-use sink only counts as twice as far). In a small house the
    // Sim carrying dishes then waits in the bathroom sink's line behind a teeth brusher while the kitchen sink is free.
    // Rules:
    //   1. Kitchen sinks first: when the lot has a kitchen sink, dishes go to the nearest free kitchen sink; with none free,
    //      to EA's pick when it is a kitchen sink, else to the nearest kitchen sink. A bathroom sink gets dishes only on a lot
    //      without a kitchen sink (then: the nearest free sink when EA's is busy). A kitchen pick never moves to a bathroom.
    //   2. Drop it in the sink: at a kitchen sink where another Sim is washing dishes, with no other kitchen sink free, the
    //      Sim walks over and puts its dishes down beside the sink; the washer's wash runs a little longer (1/16 of EA's wash per dish, +20 % at most) and
    //      the dishes count as washed when that wash ends as EA's does. At most kHandCap dishes are handed in per wash; a
    //      wash that ends early leaves the dishes where they were put down (dirty, for EA's own cleanup).
    // Dishwashers and food synthesizers EA picks are never changed.
    public static class SinkFreeCore
    {
        public const int kNone = 0, kWould = 1, kExempt = 2, kGo = 3, kCapped = 4;
        public const int kHandCap = 3;

        // on: the switch for the Sim's group (sinkfree, and sinkfreehh for household Sims); inScope: false for a Sim the
        // switches leave to EA. eaIsSink: EA picked a sink; change: the rule picks another sink than EA's.
        public static int Decide(bool on, bool inScope, bool eaIsSink, bool change)
        {
            if (!eaIsSink || !change) return kNone;
            if (!inScope) return kExempt;
            return on ? kGo : kWould;
        }

        // Busy for the Sim that carries the dishes: anyone else uses the sink, or another Sim heads its line.
        public static bool Busy(int otherUsers, bool otherHeadsLine) { return otherUsers > 0 || otherHeadsLine; }

        // A kitchen sink: not a pedestal sink and not in a room with a toilet, shower or bath (EA has no room types; these
        // are the objects EA's own privacy situations lock rooms for).
        public static bool Kitchen(bool pedestal, bool roomHasBathObject) { return !pedestal && !roomHasBathObject; }

        // The nearest usable sink: distance divided by EA's level factor (GlobalFunctions.GetClosestObject's kDifferentLevelScore)
        // for a sink on another level, as EA ranks them.
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

        // Rule 1. ea: index of EA's pick in the lot's sinks (-1 = EA picked no sink of this lot); kitchen / busy per sink.
        // Returns the sink to use (ea when nothing changes).
        public static int Pick(int ea, bool[] kitchen, bool[] busy, float[] dist, bool[] otherLevel, float levelScore)
        {
            if (ea < 0 || kitchen == null || busy == null || dist == null) return ea;
            int n = dist.Length;
            bool anyKitchen = false;
            for (int i = 0; i < n; i++) if (kitchen[i]) anyKitchen = true;
            bool[] ok = new bool[n];
            if (anyKitchen)
            {
                if (kitchen[ea] && !busy[ea]) return ea;
                for (int i = 0; i < n; i++) ok[i] = kitchen[i] && !busy[i];
                int free = Nearest(dist, ok, otherLevel, levelScore);
                if (free >= 0) return free;
                if (kitchen[ea]) return ea;
                for (int i = 0; i < n; i++) ok[i] = kitchen[i];
                int k = Nearest(dist, ok, otherLevel, levelScore);
                return k >= 0 ? k : ea;
            }
            if (!busy[ea]) return ea;
            for (int i = 0; i < n; i++) ok[i] = !busy[i];
            int f = Nearest(dist, ok, otherLevel, levelScore);
            return f >= 0 ? f : ea;
        }

        // Rule 1b: a sink in a room locked for privacy (someone on the toilet or in the shower there; EA's route then fails with
        // FailedBecauseDestinationPortalLocked) is skipped while any other usable sink exists: Pick over the others, and when
        // EA's pick is the locked one, EA's own rule (the nearest) over the others stands in for it. locked: also true for an
        // unusable sink. All sinks locked: Pick as before.
        public static int PickOpen(int ea, bool[] kitchen, bool[] busy, bool[] locked, float[] dist, bool[] otherLevel, float levelScore)
        {
            int n = kitchen.Length;
            if (locked == null || locked.Length != n || ea < 0 || ea >= n) return Pick(ea, kitchen, busy, dist, otherLevel, levelScore);
            bool[] open = new bool[n]; bool anyOpen = false;
            for (int i = 0; i < n; i++) { open[i] = !locked[i]; if (open[i]) anyOpen = true; }
            if (!anyOpen) return Pick(ea, kitchen, busy, dist, otherLevel, levelScore);
            bool[] k2 = new bool[n], b2 = new bool[n];
            for (int i = 0; i < n; i++) { k2[i] = kitchen[i] && open[i]; b2[i] = busy[i] || !open[i]; }
            int ea2 = open[ea] ? ea : Nearest(dist, open, otherLevel, levelScore);
            if (ea2 < 0) return Pick(ea, kitchen, busy, dist, otherLevel, levelScore);
            int r = Pick(ea2, k2, b2, dist, otherLevel, levelScore);
            return r >= 0 && open[r] ? r : ea2;
        }

        // Rule 2: hand the dishes in? washerWashing: another Sim runs WashDishes at this sink now; freeKitchen: another
        // kitchen sink is free; dishes: in the carrier's stack; handed: dishes already handed in to this wash.
        public static int Handoff(bool on, bool inScope, bool kitchenSink, bool washerWashing, bool freeKitchen, int dishes, int handed)
        {
            if (!kitchenSink || !washerWashing || freeKitchen || dishes <= 0) return kNone;
            if (handed + dishes > kHandCap) return kCapped;
            if (!inScope) return kExempt;
            return on ? kGo : kWould;
        }

        // Rule 3: merge dish trips? Another Sim (the carrier) is on its way to the same kitchen sink with dishes (its WashDishes
        // runs, it does not wash yet) and no other kitchen sink is free. The Sim with the dishes walks to the carrier and adds
        // its dishes to the carrier's wash only when the carrier is near (kMergeNear) and nearer than the sink, so the walk
        // is short and on the way; and only while the carrier's stack plus what joined it plus these dishes fit in EA's
        // kMaxDishStackSize. kMergeFar: the carrier is not close (EA's own trip).
        public const float kMergeNear = 4f, kMergeReach = 1.5f;
        public const int kMergeFar = 5;
        public static int Merge(bool on, bool inScope, bool kitchenSink, bool carrierWalking, bool freeKitchen, float distToCarrier,
            float distToSink, int dishes, int carrierStack, int joined, int maxStack)
        {
            if (!kitchenSink || !carrierWalking || freeKitchen || dishes <= 0) return kNone;
            if (!(distToCarrier <= kMergeNear) || !(distToCarrier < distToSink)) return kMergeFar;
            if (maxStack <= 0 || carrierStack + joined + dishes > maxStack) return kCapped;
            if (!inScope) return kExempt;
            return on ? kGo : kWould;
        }

        // sim-minutes the wash runs longer for `dishes` handed in after `before` were already handed into the same wash: a
        // small fixed share of EA's wash length per dish (1/16), the whole wash at most kExtraCapShare (20 %) longer however
        // many dishes come in, so the washer is not held at the sink for long.
        public const float kExtraPerDishShare = 1f / 16f, kExtraCapShare = 0.2f;
        public static float ExtraMinutes(float washLengthMin, int before, int dishes)
        {
            if (washLengthMin <= 0f || dishes <= 0) return 0f;
            if (before < 0) before = 0;
            float cap = washLengthMin * kExtraCapShare;
            float had = System.Math.Min(washLengthMin * kExtraPerDishShare * before, cap);
            float now = System.Math.Min(washLengthMin * kExtraPerDishShare * (before + dishes), cap);
            return now - had;
        }

        // At the end of the wash a handed-in dish is destroyed as washed only when the wash ended as EA counts it done and the
        // dish still lies where it was put down; else it stays (dirty, EA's cleanup takes it, or another Sim already did).
        public const int kEndWashed = 0, kEndLeft = 1, kEndTaken = 2;
        public static int EndDish(bool washDone, bool stillDown)
        {
            if (!stillDown) return kEndTaken;
            return washDone ? kEndWashed : kEndLeft;
        }
    }
}
