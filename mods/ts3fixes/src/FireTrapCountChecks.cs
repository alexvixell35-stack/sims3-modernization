using Sims3.Gameplay.Careers;
using Sims3.Gameplay.Situations;

namespace Ts3Fixes
{
    // firetrapcount harness check (docs/570 §5). Pure: nothing in the world changes.
    public static partial class Fixes
    {
        // FireTrapCountCheck run: a trapped Sim (TrappedInRoom, in the job's trapped list) leaves the lot, a rubble victim is
        // deleted, and an apartment trap spot sits in a hidden room.
        //   Controls: a Sim being carried out leaving, a convinced passer-by leaving, a trapped Sim still on the lot, a
        //   hidden-room spot on a lot without virtual residential slots -> EA.
        //   off BROKEN = tasks kept / hidden spot kept; on FIXED = tasks dropped / spot removed. The verdict needs every
        //   control to hold and every EA member the hooks use to be found (else UNEXPECTED, with the missing names).
        //   result  counters 3956-3959 and whether the internal members were found
        public static object FireTrapCountCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "firetrapcount: tasks dropped 2573=" + c[3956] + ", hidden spots removed 2574=" + c[3957] + ", jobs completed by a drop 2575=" + c[3958]
                + ", errors 2576=" + c[3959] + "; members missing: " + (FtcMissing().Length == 0 ? "none" : FtcMissing());
            if (what == "result") return counters;
            if (what != "run") return "firetrapcount <run|result>";
            bool on = ShortcutOn("firetrapcount");
            const FirefighterEmergencySituation.VictimState trapped = FirefighterEmergencySituation.VictimState.TrappedInRoom,
                rubble = FirefighterEmergencySituation.VictimState.TrappedUnderRubble,
                carried = FirefighterEmergencySituation.VictimState.TryingToBeCarriedOut,
                convinced = FirefighterEmergencySituation.VictimState.ConvincedToRun;
            bool dropRoom = FtcDropTask(on, true, false, trapped, true) == TaskId.TrappedSims;
            bool dropRubble = FtcDropTask(on, false, true, rubble, true) == TaskId.TrappedRubble;
            bool dropSpot = !FtcKeepSpot(on, true, true);
            // controls, each must hold (EA's outcome kept):
            bool cCarried = FtcDropTask(on, true, false, carried, true) == TaskId.Invalid;     // a Sim being carried out leaves: its task is completed by the carry, not dropped
            bool cPasserBy = FtcDropTask(on, false, false, convinced, true) == TaskId.Invalid; // a convinced passer-by (no task) leaves
            bool cUnlisted = FtcDropTask(on, false, false, trapped, true) == TaskId.Invalid;  // a TrappedInRoom Sim the job did not trap (InteractionFail) leaves
            bool cStays = FtcDropTask(on, true, false, trapped, false) == TaskId.Invalid;     // a trapped Sim still on the lot keeps the task
            bool cPlainLot = FtcKeepSpot(on, false, true);                                     // a hidden-room spot on a lot without virtual residential slots is kept
            bool cOpenRoom = FtcKeepSpot(on, true, false);                                     // an apartment spot in a normal room is kept
            bool controlsOk = cCarried && cPasserBy && cUnlisted && cStays && cPlainLot && cOpenRoom;
            string missing = FtcMissing();
            bool members = missing.Length == 0;
            bool all = dropRoom && dropRubble && dropSpot, none = !dropRoom && !dropRubble && !dropSpot;
            string verdict = !controlsOk || !members ? "UNEXPECTED" : !on ? (none ? "BROKEN" : "UNEXPECTED") : (all ? "FIXED" : "UNEXPECTED");
            return "firetrapcount check (switch " + (on ? "on" : "off") + "): trapped Sim left -> task dropped=" + dropRoom + ", rubble Sim deleted -> dropped=" + dropRubble
                + ", hidden apartment spot removed=" + dropSpot + "; controls ok=" + controlsOk + " (carried-out Sim kept=" + cCarried + ", passer-by=" + cPasserBy
                + ", untrapped TrappedInRoom=" + cUnlisted + ", Sim still on lot=" + cStays + ", non-apartment spot=" + cPlainLot + ", open apartment room=" + cOpenRoom
                + "); members " + (members ? "found" : "MISSING: " + missing) + "; verdict: " + verdict + "; " + counters;
        }
    }
}
