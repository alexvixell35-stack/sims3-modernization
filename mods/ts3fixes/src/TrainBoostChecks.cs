namespace Ts3Fixes
{
    // trainboost harness check (docs/569 §1). Read-only: nothing in the world changes.
    public static partial class Fixes
    {
        // TrainBoostCheck run: a Train Sim session that unwound after raising the trainee's gain has it undone.
        //   Controls: a normal return (EA lowers it), an unwind before the raise (delta 0), a delta that is not EA's
        //   amount (something else changed it) -> left alone.
        //   off BROKEN = boost kept; on FIXED = undone.
        //   result  counters 2566-2567 and whether the members were found
        public static object TrainBoostCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "trainboost: undone 2566=" + c[2566] + ", errors 2567=" + c[2567]
                + "; members found=" + (sTbSkillMult != null && sTbShapeMult != null && sTbShape != null);
            if (what == "result") return counters;
            if (what != "run") return "trainboost <run|result>";
            bool on = ShortcutOn("trainboost");
            bool undo = TbUndo(on, false, 1f, 1f);
            bool control = TbUndo(on, true, 1f, 1f) || TbUndo(on, false, 0f, 1f) || TbUndo(on, false, 0.5f, 1f);
            string verdict = control ? "UNEXPECTED" : !on ? (undo ? "UNEXPECTED" : "BROKEN") : (undo ? "FIXED" : "UNEXPECTED");
            return "trainboost check (switch " + (on ? "on" : "off") + "): boost undone after an unwound session=" + undo + ", controls=" + control + "; verdict: " + verdict + "; " + counters;
        }
    }
}
