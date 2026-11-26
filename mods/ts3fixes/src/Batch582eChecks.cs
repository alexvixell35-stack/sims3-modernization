using System.Collections.Generic;

namespace Ts3Fixes
{
    // Harness checks for Batch582eLogic.cs (docs/582 batch 6). Pure decisions with controls; nothing in the world is touched.
    public static partial class Fixes
    {
        // DisposeBaseCheck run: GameObject.Dispose runs after the override. Control: none needed beyond the switch; the
        // override's own cleanup always runs first (structure of the hook).
        public static object DisposeBaseCheck(string what)
        {
            string counters = B582Counters("disposebase", 2892, new[] { "base disposes run", "seen with switch off", "errors" }, "");
            if (what == "result") return counters;
            if (what != "run") return "disposebase <run|result>";
            bool on = ShortcutOn("disposebase");
            bool fixedNow = DbRunBase(on), controls = !DbRunBase(false);
            return B582Line("disposebase", on, "GameObject.Dispose runs after the override", fixedNow, "switch off keeps EA's skip=" + controls, controls, "", counters);
        }

        // BoardingSchoolMoneyCheck run: need 200,800 and reward 50,150 map to their own pairs. Controls: missing ranges give
        // EA's 100/500 fallbacks; a short list keeps the fallback.
        public static object BoardingSchoolMoneyCheck(string what)
        {
            string counters = B582Counters("boardingschoolmoney", 2895, new[] { "schools corrected", "would change with switch off", "errors" }, "");
            if (what == "result") return counters;
            if (what != "run") return "boardingschoolmoney <run|result>";
            bool on = ShortcutOn("boardingschoolmoney");
            int[] r = BsRanges(new List<int> { 200, 800 }, new List<int> { 50, 150 });
            bool fixedNow = on && r[0] == 200 && r[1] == 800 && r[2] == 50 && r[3] == 150;
            int[] d = BsRanges(null, new List<int> { 7 });
            bool controls = d[0] == 100 && d[1] == 500 && d[2] == 100 && d[3] == 500;
            return B582Line("boardingschoolmoney", on, "each range to its own pair (applied at world load)", fixedNow, "fallbacks 100/500=" + controls, controls, "", counters);
        }

        // FreshClothingResumeCheck run: a saved 90 min resumes, a saved 0 skips. Controls: no entry and buff still on -> EA.
        public static object FreshClothingResumeCheck(string what)
        {
            string counters = B582Counters("freshclothingresume", 2898, new[] { "resumed", "skipped (worn off)", "seen with switch off", "errors" }, sFcMap == null ? "LaundryManager.mFreshClothingTimeouts" : "");
            if (what == "result") return counters;
            if (what != "run") return "freshclothingresume <run|result>";
            bool on = ShortcutOn("freshclothingresume");
            bool fixedNow = FcDecide(on, true, 90f, false) == kFcResume && FcDecide(on, true, 0f, false) == kFcSkip;
            bool controls = FcDecide(on, false, 0f, false) == kFcEa && FcDecide(on, true, 90f, true) == kFcEa;
            return B582Line("freshclothingresume", on, "saved time resumed, worn-off skipped", fixedNow, "no entry / buff on -> EA=" + controls, controls, sFcMap == null ? "LaundryManager.mFreshClothingTimeouts" : "", counters);
        }

        // TwinBellyShapeCheck run: twins baby 1 -> 0.5, triplets baby 2 -> 1/3. Controls: last baby 0, a single baby and a
        // value that is not EA's formula unchanged.
        public static object TwinBellyShapeCheck(string what)
        {
            string counters = B582Counters("twinbellyshape", 2902, new[] { "shapes corrected", "seen with switch off", "errors" }, sPgMissing);
            if (what == "result") return counters;
            if (what != "run") return "twinbellyshape <run|result>";
            bool on = ShortcutOn("twinbellyshape");
            bool fixedNow = PgShape(on, 1f, 1, 2) == 0.5f && System.Math.Abs(PgShape(on, 1f, 2, 3) - 1f / 3f) < 1e-6f;
            bool controls = PgShape(on, 0f, 2, 2) == 0f && PgShape(on, 1f, 1, 1) == 1f && PgShape(on, 0.7f, 1, 2) == 0.7f;
            return B582Line("twinbellyshape", on, "twins 1 -> 0.5, triplets 2 -> 1/3", fixedNow, "last baby / single / other value unchanged=" + controls, controls, sPgMissing, counters);
        }
    }
}
