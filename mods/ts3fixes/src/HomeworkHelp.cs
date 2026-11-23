using System;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects.Miscellaneous;

namespace Ts3Fixes
{
    // homeworkhelp (tuning kHomeworkHelp, off by default; docs/565 §7). EA bug fix in "Get Help with Homework".
    //
    // [verified, decompiled] Homework.GetHelpWithHomework.Run creates the two-person jig, starts the child's loop and
    // AddNext-queues HelpWithHomework on the helper. When the child's loop ends (homework done, cancelled) it destroys the
    // jig and sets Homework.HomeworkTwoPersonJig = null. A helper who was busy and starts HelpWithHomework only then runs
    // `Target.HomeworkTwoPersonJig.RouteToJigA(Actor)` with no null check (the interaction's Test always passes):
    // NullReferenceException out of Run, a script error on the helper (often a household parent). The same happens when
    // the child's Sim is gone (`OwningSimDescription.CreatedSim` null) on the next line.
    // Fix: before EA's Run, with no jig or no child Sim, HelpWithHomework ends as EA's own "child is not waiting" check
    // ends it (returns false). With a jig, EA's code runs unchanged (it routes, sees the child is not waiting and returns
    // false itself).
    //
    // Counters: [2538] helpers stopped (no jig / no child), [2539] errors. Check: HomeworkHelpChecks.cs.
    public static partial class Fixes
    {
        static readonly Type sHwHelp = typeof(Homework).GetNestedType("HelpWithHomework", BindingFlags.NonPublic | BindingFlags.Public);

        static void ApplyHomeworkHelp()
        {
            if (sHwHelp == null) { AppliedLog().Add("homeworkhelp: Homework.HelpWithHomework not found, not patched"); return; }
            Patch(sHwHelp, "Run", typeof(HwhHook), "RunHook", "RunOrig");
        }

        // ---- decision (also used by the check) ----
        internal static bool HwhStop(bool on, bool hasJig, bool hasChild)
        {
            return on && (!hasJig || !hasChild);
        }

        public class HwhHook
        {
            public bool RunHook()
            {
                if (ShortcutOn("homeworkhelp"))
                {
                    try
                    {
                        object st = this;
                        Homework hw = ((Interaction<Sim, Homework>)st).Target;
                        Sim child = hw == null || hw.OwningSimDescription == null ? null : hw.OwningSimDescription.CreatedSim;
                        if (HwhStop(true, hw != null && hw.HomeworkTwoPersonJig != null, child != null))
                        {
                            PatchCounters()[2538]++;
                            return false;
                        }
                    }
                    catch (Exception) { PatchCounters()[2539]++; }
                }
                return RunOrig();
            }
            public bool RunOrig() { return false; } // receives Homework.HelpWithHomework.Run's original body
        }
    }
}
