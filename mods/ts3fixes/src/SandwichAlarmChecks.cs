using System;
using System.Collections.Generic;

namespace Ts3Fixes
{
    // Harness check for SandwichAlarm.cs (docs/580 §16). Pure decision with controls; nothing in the world is touched.
    public static partial class Fixes
    {
        // SandwichAlarmCheck run: the daily handle held when EA re-adds the alarm is removed first.
        //   Controls: no handle held (invalid) -> nothing removed.
        public static object SandwichAlarmCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "sandwichalarm: stale handles removed 2800=" + c[2800] + ", seen with switch off 2801=" + c[2801] + ", errors 2802=" + c[2802] + ", saved duplicates removed at load 2803=" + c[2803]
                + "; " + CkMembers(sSaMissing);
            if (what == "result") return counters;
            if (what != "run") return "sandwichalarm <run|result>";
            bool on = ShortcutOn("sandwichalarm");
            bool fixedNow = SaRemoveFirst(on, true);
            bool ctlNone = !SaRemoveFirst(on, false);
            // load sweep (switch independent: the sweep itself runs only with the switch on)
            List<int> dup = SaPick(new[] { "ResetSandwichBoards", "ResetSandwichBoards", "ResetSandwichBoards" }, new[] { true, true, true }, new[] { true, false, false });
            bool sweepOk = dup.Count == 2 && dup[0] == 1 && dup[1] == 2;
            bool ctlSingle = SaPick(new[] { "ResetSandwichBoards" }, new[] { true }, new[] { true }).Count == 0;
            bool ctlOtherCb = SaPick(new[] { "ResetSandwichBoards", "UpdateSandwichBoards", "StartMoveORShow" }, new[] { true, true, true }, new[] { true, false, false }).Count == 0;
            bool ctlOtherTheatre = SaPick(new[] { "ResetSandwichBoards", "ResetSandwichBoards" }, new[] { true, false }, new[] { true, false }).Count == 0;
            List<int> stale = SaPick(new[] { "ResetSandwichBoards", "ResetSandwichBoards" }, new[] { true, true }, new[] { false, false });
            bool ctlStale = stale.Count == 1 && stale[0] == 1;
            bool midShow = SaSkipMidShow(on, Sims3.Gameplay.Abstracts.ShowVenue.ShowTypes.kPlayerConcert);
            bool ctlNoShow = !SaSkipMidShow(on, Sims3.Gameplay.Abstracts.ShowVenue.ShowTypes.kNoShow) && !SaSkipMidShow(on, Sims3.Gameplay.Abstracts.ShowVenue.ShowTypes.kMovie);
            fixedNow = fixedNow && sweepOk && midShow;
            bool controls = ctlStale && ctlNone && ctlSingle && ctlOtherCb && ctlOtherTheatre && ctlNoShow;
            return "sandwichalarm check (switch " + (on ? "on" : "off") + "): held daily handle removed before the re-add=" + fixedNow
                + "; load sweep removes the 2 extra of 3=" + sweepOk + "; one-shot skipped during a player show=" + midShow + "; control: no show / movie run EA=" + ctlNoShow + "; control: no handle held unchanged=" + ctlNone
                + ", single alarm untouched=" + ctlSingle + ", other alarm types untouched=" + ctlOtherCb + ", other theatre untouched=" + ctlOtherTheatre + ", stale held handle keeps one=" + ctlStale + "; controls ok=" + controls
                + "; verdict: " + CkVerdict(on, fixedNow, controls, sSaMissing.Length == 0) + "; " + counters;
        }
    }
}
