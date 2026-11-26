using System.Collections.Generic;

namespace Ts3Fixes
{
    // Harness checks for Batch588Logic.cs (docs/588 batch 10). Pure decisions with controls; nothing in the world is touched.
    public static partial class Fixes
    {
        static bool SpSame(int[] a, int[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // SpRelCountCheck run: three mutual friends make EA's incremental build skew up. Controls: no relationships match.
        public static object SpRelCountCheck(string what)
        {
            string counters = B582Counters("sprelcount", 2929, new[] { "Collects with own counts", "incremental Collects (switch off)", "errors" }, sCoMissing);
            if (what == "result") return counters;
            if (what != "run") return "sprelcount <run|result>";
            bool on = ShortcutOn("sprelcount");
            int[][] tri = { new[] { 1, 2 }, new[] { 0, 2 }, new[] { 0, 1 } };
            int[] exact = SpRelHistogram(tri, true, 4);
            bool fixedNow = SpOwnOnly(on, true) && exact[2] == 3 && !SpSame(SpRelHistogram(tri, false, 4), exact);
            int[][] none = { new int[0], new int[0] };
            bool controls = !SpOwnOnly(on, false) && SpSame(SpRelHistogram(none, true, 4), SpRelHistogram(none, false, 4)) && SpRelHistogram(none, true, 4)[0] == 2;
            return B582Line("sprelcount", on, "triangle counted 3 x 2 friends", fixedNow, "no friends same both ways=" + controls, controls, sCoMissing, counters);
        }

        // SpCollectNullCheck run: a null household is skipped. Controls: special skipped, normal kept, off.
        public static object SpCollectNullCheck(string what)
        {
            string counters = B582Counters("spcollectnull", 2932, new[] { "null households skipped", "seen with switch off", "errors" }, sCoMissing);
            if (what == "result") return counters;
            if (what != "run") return "spcollectnull <run|result>";
            bool on = ShortcutOn("spcollectnull");
            bool fixedNow = on && SpCollectSkips(true, true, false);
            bool controls = SpCollectSkips(true, false, true) && !SpCollectSkips(true, false, false) && !SpCollectSkips(false, true, false);
            return B582Line("spcollectnull", on, "null household skipped", fixedNow, "special / normal / off unchanged=" + controls, controls, sCoMissing, counters);
        }

        // SpCloneDictCheck run: a change to the clone leaves the live dictionary alone. Controls: off shares; null stays.
        public static object SpCloneDictCheck(string what)
        {
            string counters = B582Counters("spclonedict", 2935, new[] { "clones given their own dictionary", "clones sharing (switch off)", "errors" }, sCdMissing);
            if (what == "result") return counters;
            if (what != "run") return "spclonedict <run|result>";
            bool on = ShortcutOn("spclonedict");
            Dictionary<ulong, int> live = new Dictionary<ulong, int>();
            live[7] = 2;
            Dictionary<ulong, int> c = SpCloneDict(true, live);
            c.Remove(7);
            bool fixedNow = on && live.ContainsKey(7) && c != live;
            bool controls = SpCloneDict(false, live) == live && SpCloneDict(true, null) == null;
            return B582Line("spclonedict", on, "clone edit leaves live", fixedNow, "off shares / null unchanged=" + controls, controls, sCdMissing, counters);
        }

        // SpLotLookupCheck run: a 6-Sim spec on a 4-bed lot is 2 over. Controls: no lot / dorm / fits.
        public static object SpLotLookupCheck(string what)
        {
            string counters = B582Counters("splotlookup", 2938, new[] { "specs counted on their lot", "dropped by EA (switch off)", "errors" }, sLlMissing);
            if (what == "result") return counters;
            if (what != "run") return "splotlookup <run|result>";
            bool on = ShortcutOn("splotlookup");
            bool fixedNow = on && SpLotExcess(true, false, 6, 4, true) == 2 && SpLotExcess(true, false, 2, 4, false) == 2;
            bool controls = SpLotExcess(false, false, 6, 4, true) == 0 && SpLotExcess(true, true, 6, 4, true) == 0 && SpLotExcess(true, false, 4, 4, true) == 0;
            return B582Line("splotlookup", on, "over / under counted", fixedNow, "no lot / dorm / fits unchanged=" + controls, controls, sLlMissing, counters);
        }

        // SpBucketIndexCheck run: pick 1 of buckets {3, 5, 6} forces bucket 5. Controls: off keeps EA's position; ends.
        public static object SpBucketIndexCheck(string what)
        {
            string counters = B582Counters("spbucketindex", 2941, new[] { "targeted actions with the bucket", "targeted draws (switch off)", "errors" }, sBiMissing);
            if (what == "result") return counters;
            if (what != "run") return "spbucketindex <run|result>";
            bool on = ShortcutOn("spbucketindex");
            List<int> b = new List<int> { 3, 5, 6 };
            bool fixedNow = on && SpForced(true, 1, b) == 5;
            bool controls = SpForced(false, 1, b) == 1 && SpIncrease(0, 3, 0f, 0f) && !SpIncrease(2, 3, 0f, 0f) && !SpIncrease(1, 3, -0.5f, -0.1f) && SpIncrease(1, 3, -0.1f, -0.5f);
            return B582Line("spbucketindex", on, "bucket index forced", fixedNow, "off / ends / neighbour test=" + controls, controls, sBiMissing, counters);
        }

        // SpCreateMoveInCheck run: the family spec is added once. Control: off adds twice.
        public static object SpCreateMoveInCheck(string what)
        {
            string counters = B582Counters("spcreatemovein", 2944, new[] { "predictions made once on the lot", "double predictions (switch off)", "errors" }, sCmMissing);
            if (what == "result") return counters;
            if (what != "run") return "spcreatemovein <run|result>";
            bool on = ShortcutOn("spcreatemovein");
            bool fixedNow = on && SpSpecAdds(true) == 1;
            bool controls = SpSpecAdds(false) == 2;
            return B582Line("spcreatemovein", on, "spec added once", fixedNow, "off twice=" + controls, controls, sCmMissing, counters);
        }

        // SpLotDensityCheck run: a household on an apartment lot is not counted. Controls: story lot / homeless / off.
        public static object SpLotDensityCheck(string what)
        {
            string counters = B582Counters("splotdensity", 2947, new[] { "left out by the lot test", "off-lot households EA counted (switch off)", "errors" }, sLdMissing);
            if (what == "result") return counters;
            if (what != "run") return "splotdensity <run|result>";
            bool on = ShortcutOn("splotdensity");
            bool fixedNow = on && !SpDensityCounts(true, true, false);
            bool controls = SpDensityCounts(true, true, true) && !SpDensityCounts(true, false, true) && SpDensityCounts(false, true, false);
            return B582Line("splotdensity", on, "off-lot household left out", fixedNow, "story lot / homeless / off unchanged=" + controls, controls, sLdMissing, counters);
        }

        // SpRoommateJobCheck run: a jobless non-University roommate gets the job. Controls: University / has a job / none.
        public static object SpRoommateJobCheck(string what)
        {
            string counters = B582Counters("sproommatejob", 2950, new[] { "roommates given the job", "left jobless (switch off)", "errors" }, sRjMissing);
            if (what == "result") return counters;
            if (what != "run") return "sproommatejob <run|result>";
            bool on = ShortcutOn("sproommatejob");
            bool fixedNow = on && SpRoommateNeedsJob(false, true, false);
            bool controls = !SpRoommateNeedsJob(true, true, false) && !SpRoommateNeedsJob(false, true, true) && !SpRoommateNeedsJob(false, false, false);
            return B582Line("sproommatejob", on, "roommate job acquired", fixedNow, "University / employed / no Sim unchanged=" + controls, controls, sRjMissing, counters);
        }

        // SpOccultPickCheck run: only occult Sims that pass the predicate are candidates. Controls: non-occult / failed.
        public static object SpOccultPickCheck(string what)
        {
            string counters = B582Counters("spoccultpick", 2953, new[] { "random occult picks", "first-in-list picks (switch off)", "errors" }, "");
            if (what == "result") return counters;
            if (what != "run") return "spoccultpick <run|result>";
            bool on = ShortcutOn("spoccultpick");
            bool fixedNow = on && SpOccultCandidate(true, true);
            bool controls = !SpOccultCandidate(true, false) && !SpOccultCandidate(false, true);
            return B582Line("spoccultpick", on, "occult candidate drawn at random", fixedNow, "non-occult / predicate false=" + controls, controls, "", counters);
        }

        // SpJobAtWorkCheck run: the old career's AssignOccupation(null) after a new job is a stale clear. Controls: own clear.
        public static object SpJobAtWorkCheck(string what)
        {
            string counters = B582Counters("spjobatwork", 2956, new[] { "new jobs kept", "new jobs EA dropped (switch off)", "errors" }, "");
            if (what == "result") return counters;
            if (what != "run") return "spjobatwork <run|result>";
            bool on = ShortcutOn("spjobatwork");
            bool fixedNow = on && SpStaleClear(true, true, false);
            bool controls = !SpStaleClear(true, true, true) && !SpStaleClear(true, false, false) && !SpStaleClear(false, true, false);
            return B582Line("spjobatwork", on, "new job kept on the deferred leave", fixedNow, "own clear / jobless / assign unchanged=" + controls, controls, "", counters);
        }

        // SpRecheckCheck run: an action reaching the active household is not run. Controls: NPC household / no household.
        public static object SpRecheckCheck(string what)
        {
            string counters = B582Counters("sprecheck", 2959, new[] { "actions not run on a played household", "rounds with switch off", "errors" }, sRcMissing);
            if (what == "result") return counters;
            if (what != "run") return "sprecheck <run|result>";
            bool on = ShortcutOn("sprecheck");
            bool fixedNow = on && SpPlayed(true, true, false, false) && SpPlayed(true, false, true, false) && SpPlayed(true, false, false, true);
            bool controls = !SpPlayed(true, false, false, false) && !SpPlayed(false, true, true, true)
                && SpIdKind("mPrimaryHouseholdId") == 0 && SpIdKind("HouseholdId") == 0 && SpIdKind("mLotId") == 1 && SpIdKind("mSimDescriptionId") == 2 && SpIdKind("mMom") == 2;
            return B582Line("sprecheck", on, "active / travel / selected's household blocked", fixedNow, "NPC / none / id kinds=" + controls, controls, sRcMissing, counters);
        }

        // SpPensionPromptCheck run: the case where EA asks after the leave. Controls: no job / school / other career / no UI.
        public static object SpPensionPromptCheck(string what)
        {
            string counters = B582Counters("sppensionprompt", 2962, new[] { "pension prompts asked first", "asked after the leave (switch off)", "errors" }, sPpMissing);
            if (what == "result") return counters;
            if (what != "run") return "sppensionprompt <run|result>";
            bool on = ShortcutOn("sppensionprompt");
            bool fixedNow = on && SpPensionAfterLeave(true, false, true, true, true);
            bool controls = !SpPensionAfterLeave(false, false, true, true, true) && !SpPensionAfterLeave(true, true, true, true, true)
                && !SpPensionAfterLeave(true, false, false, true, true) && !SpPensionAfterLeave(true, false, true, false, true) && !SpPensionAfterLeave(true, false, true, true, false);
            return B582Line("sppensionprompt", on, "pension asked before the leave", fixedNow, "no job / school / other / no UI / bad request=" + controls, controls, sPpMissing, counters);
        }

        // HhCleanupDupCheck run: a special or listed larger household is not folded into. Controls: off / normal.
        public static object HhCleanupDupCheck(string what)
        {
            string counters = B582Counters("hhcleanupdup", 2965, new[] { "cleanups run fixed", "shared-member loads (switch off)", "errors" }, sHdMissing);
            if (what == "result") return counters;
            if (what != "run") return "hhcleanupdup <run|result>";
            bool on = ShortcutOn("hhcleanupdup");
            bool fixedNow = on && !HdMayFold(true, true, false) && !HdMayFold(true, false, true);
            bool controls = HdMayFold(true, false, false) && HdMayFold(false, true, true);
            return B582Line("hhcleanupdup", on, "special / listed household not folded into", fixedNow, "normal / off unchanged=" + controls, controls, sHdMissing, counters);
        }

        // VirtualHomeShareCheck run: 10 lots at 0.5 draw from 0-5. Controls: off all; 1 lot; share 2 clamped.
        public static object VirtualHomeShareCheck(string what)
        {
            string counters = B582Counters("virtualhomeshare", 2968, new[] { "homes from the tuned share", "draws over every lot (switch off)", "errors" }, sVhMissing);
            if (what == "result") return counters;
            if (what != "run") return "virtualhomeshare <run|result>";
            bool on = ShortcutOn("virtualhomeshare");
            bool fixedNow = on && VhLast(true, 10, 0.5f) == 5;
            bool controls = VhLast(false, 10, 0.5f) == 9 && VhLast(true, 1, 0.5f) == 0 && VhLast(true, 10, 2f) == 9;
            return B582Line("virtualhomeshare", on, "cheapest half drawn", fixedNow, "off / one lot / share over 1=" + controls, controls, sVhMissing, counters);
        }

        // CelebAlarmNullCheck run: a valid handle with no Sim throws in EA. Controls: Sim present / no handle.
        public static object CelebAlarmNullCheck(string what)
        {
            string counters = B582Counters("celebalarmnull", 2971, new[] { "alarms removed with no Sim", "would have thrown (switch off)", "errors" }, sCaMissing);
            if (what == "result") return counters;
            if (what != "run") return "celebalarmnull <run|result>";
            bool on = ShortcutOn("celebalarmnull");
            bool fixedNow = on && CaThrows(true, false);
            bool controls = !CaThrows(true, true) && !CaThrows(false, false);
            return B582Line("celebalarmnull", on, "alarm removed without the Sim", fixedNow, "Sim present / no handle unchanged=" + controls, controls, sCaMissing, counters);
        }
    }
}
