using Sims3.Gameplay.Services;

namespace Ts3Fixes
{
    // serviceleave harness check (docs/564 §1). Read-only: nothing in the world changes.
    public static partial class Fixes
    {
        // ServiceLeaveCheck run: (a) a reset worker in a wait / leave state and in a work-limit state, (b) a repo man
        //   leaving a lot with no mailbox. Controls: a reset Sim that is not the worker, an exited situation, a bartender
        //   situation, an unrelated alarm field, the maid's finish alarm after she stopped working, a lot with a mailbox.
        //   Live (read-only): each service situation in the world reports its state and whether a timeout field the fix
        //   would re-arm is set.
        //   off BROKEN = nothing re-armed, repo man stays; on FIXED = re-armed, repo man drives away.
        //   (docs/564 §2) babysitter periodic / leave / hunker-down alarms, repo-man no-op states (no household / no object).
        //   result  counters 2517-2523
        public static object ServiceLeaveCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "serviceleave: wait/leave re-armed 2517=" + c[2517] + ", work limit re-armed 2518=" + c[2518] + ", repo man no mailbox 2519="
                + c[2519] + ", no delay found 2520=" + c[2520] + ", errors 2521=" + c[2521] + ", babysitter checks re-armed 2522=" + c[2522] + ", repo-man no-op states left 2523=" + c[2523];
            if (what == "result") return counters;
            if (what != "run") return "serviceleave <run|result>";
            bool on = ShortcutOn("serviceleave");
            bool wait = SlRearmKind(on, true, false, false, "mAlarmHandle", "TimeToRoute") == 1 && SlRearmKind(on, true, false, false, "mAlarmHandle", "TimeToGo") == 1;
            bool work = SlRearmKind(on, true, false, false, "mTimeUpHandle", "TimeUp") == 2 && SlRearmKind(on, true, false, false, "mTimeToFinishHandle", "TimeToFinish") == 2;
            bool repo = SlRepoLeave(on, false) && SlRepoIdleLeave(on, true, false, true) && SlRepoIdleLeave(on, true, true, false);
            bool sitter = SlRearmKind(on, true, false, false, "mParentsReturnAlarm", "CheckForParentsReturn") == 3 && SlRearmKind(on, true, false, false, "mBabysitterShouldLeaveAlarm", "TimeToLeave") == 1
                && SlRearmKind(on, true, false, false, "mBabysitterShouldHunkerDown", "TimeToHunkerDown") == 2;
            bool control = SlRearmKind(on, false, false, false, "mAlarmHandle", "TimeToRoute") != 0 || SlRearmKind(on, true, true, false, "mAlarmHandle", "TimeToRoute") != 0
                || SlRearmKind(on, true, false, true, "mAlarmHandle", "TimeToRoute") != 0 || SlRearmKind(on, true, false, false, "mDelayHandle", "DoSomething") != 0
                || SlMaidWorking("LeaveLot`1") || !SlMaidWorking("Cleaning") || SlRepoLeave(on, true) || SlRepoIdleLeave(on, true, true, true);
            bool any = wait || work || repo || sitter, all = wait && work && repo && sitter;
            string live = "";
            int n = 0;
            try
            {
                foreach (ServiceSituation svc in Sims3.Gameplay.Autonomy.Situation.GetSituations<ServiceSituation>())
                {
                    if (n++ >= 6) break;
                    live += (live.Length > 0 ? "; " : "") + svc.GetType().Name + " -> " + (svc.Child == null ? "no state" : svc.Child.GetType().Name);
                }
            }
            catch (System.Exception e) { live = "live read threw " + e.GetType().Name; }
            string verdict = control ? "UNEXPECTED" : !on ? (any ? "UNEXPECTED" : "BROKEN") : (all ? "FIXED" : "UNEXPECTED");
            return "serviceleave check (switch " + (on ? "on" : "off") + "): wait/leave re-armed=" + wait + ", work limit re-armed=" + work + ", babysitter re-armed=" + sitter + ", repo man leaves=" + repo
                + ", controls=" + control + "; live: " + (live.Length == 0 ? "no service situations" : live) + "; verdict: " + verdict + "; " + counters;
        }
    }
}
