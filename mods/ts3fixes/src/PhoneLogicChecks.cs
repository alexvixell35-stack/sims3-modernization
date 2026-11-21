using System;
using Sims3.Gameplay.Controllers;

namespace Ts3Fixes
{
    // Harness checks for PhoneLogic.cs (docs/573 §1-6). Pure decisions with controls; nothing in the world is touched.
    public static partial class Fixes
    {
        static string PhCounters(string name, int a, string an, string missing)
        {
            int[] c = PatchCounters();
            return name + ": " + an + " " + a + "=" + c[a] + ", errors " + (a + 1) + "=" + c[a + 1] + "; " + GlMembers(missing);
        }

        static string PhPick(string all, params string[] names)
        {
            string m = "";
            foreach (string n in names) if (all.IndexOf(n) >= 0) m += n + " ";
            return m.Trim();
        }

        // PhoneLoadResetCheck run: a wall phone loaded with an answered call and a lifted handset.
        //   Controls: a ringing (pending) call stays; a smartphone's handset is left to EA's OnLoad; an idle phone stays.
        public static object PhoneLoadResetCheck(string what)
        {
            string missing = PhPick(PhMissing(), "Phone.mIncomingCall", "Phone.HandsetPickedUp", "Phone.SetHandsetState", "PhoneService.sPhoneService", "PhoneService.mPhones");
            string counters = PhCounters("phoneloadreset", 2636, "phones cleared", missing);
            if (what == "result") return counters;
            if (what != "run") return "phoneloadreset <run|result>";
            bool on = ShortcutOn("phoneloadreset"), dc, dh;
            PhLoadAction(on, true, false, true, false, out dc, out dh);
            bool freed = dc && dh;
            PhLoadAction(on, true, true, false, false, out dc, out dh);
            bool ctlPending = !dc && !dh;
            PhLoadAction(on, false, false, true, true, out dc, out dh);
            bool ctlSmart = !dc && !dh;
            PhLoadAction(on, false, false, false, false, out dc, out dh);
            bool ctlIdle = !dc && !dh;
            bool controls = ctlPending && ctlSmart && ctlIdle;
            return "phoneloadreset check (switch " + (on ? "on" : "off") + "): stale answered call and handset cleared=" + freed
                + "; control: ringing call kept=" + ctlPending + ", smartphone handset left to EA=" + ctlSmart + ", idle phone untouched=" + ctlIdle
                + "; controls ok=" + controls + "; verdict: " + GlVerdict(on, freed, controls, missing.Length == 0) + "; " + counters;
        }

        // PhoneCallScheduleCheck run: world load at 3:00 (interval 0, calls 7-22, window 8): first try today from 7:00.
        //   Controls: a load at 10:00 (EA: now until 22:00) and a reschedule at 15:00 with interval 12 (EA: tomorrow 7:00) unchanged.
        public static object PhoneCallScheduleCheck(string what)
        {
            string missing = PhPick(PhMissing(), "Phone.mPhoneCallAttemptAlarm", "Phone.kMinimumCallWindow", "Phone.ConsiderGeneratingPhoneCall", "Phone.ScheduleNextCall");
            string counters = PhCounters("phonecallschedule", 2638, "windows corrected", missing);
            if (what == "result") return counters;
            if (what != "run") return "phonecallschedule <run|result>";
            bool on = ShortcutOn("phonecallschedule");
            int from, to;
            PhWindow(on, 3, 0, 7, 22, 8, out from, out to);
            bool today = from == 4 && to == 19;
            int f1, t1, f2, t2;
            PhWindow(on, 10, 0, 7, 22, 8, out f1, out t1);
            PhWindow(on, 15, 12, 7, 22, 8, out f2, out t2);
            bool ctlDay = f1 == 0 && t1 == 12, ctlNext = f2 == 16 && t2 == 31;
            bool controls = ctlDay && ctlNext;
            return "phonecallschedule check (switch " + (on ? "on" : "off") + "): 3:00 load window " + from + "-" + to + "h (today from 7:00=" + today + ")"
                + "; control: 10:00 load unchanged=" + ctlDay + ", 15:00 reschedule unchanged=" + ctlNext + "; controls ok=" + controls
                + "; verdict: " + GlVerdict(on, today, controls, missing.Length == 0) + "; " + counters;
        }

        // PhoneCallRearmCheck run: the call attempt throws (the prom-branch IsRelated on an uninstantiated teen).
        //   Controls: a normal attempt is not re-armed twice (EA's own last line does it); the body runs once either way.
        public static object PhoneCallRearmCheck(string what)
        {
            string missing = PhPick(PhMissing(), "Phone.kMinimumCallInterval", "Phone.ScheduleNextCall");
            string counters = PhCounters("phonecallrearm", 2640, "alarms re-armed", missing);
            if (what == "result") return counters;
            if (what != "run") return "phonecallrearm <run|result>";
            bool on = ShortcutOn("phonecallrearm");
            int rearms = 0, runs = 0;
            try { PhRunGuarded(on, delegate { runs++; throw new NullReferenceException("stand-in"); }, delegate { rearms++; return true; }); }
            catch (NullReferenceException) { }
            bool rearmed = rearms == 1;
            int rearms2 = 0, runs2 = 0;
            PhRunGuarded(on, delegate { runs2++; return true; }, delegate { rearms2++; return true; });
            bool ctlNormal = rearms2 == 0 && runs2 == 1, ctlOnce = runs == 1;
            bool controls = ctlNormal && ctlOnce;
            return "phonecallrearm check (switch " + (on ? "on" : "off") + "): random-call alarm set again after a throw=" + rearmed
                + "; control: normal attempt not re-armed=" + ctlNormal + ", attempt ran once=" + ctlOnce + "; controls ok=" + controls
                + "; verdict: " + GlVerdict(on, rearmed, controls, missing.Length == 0) + "; " + counters;
        }

        // WooHootyWaitCheck run: the texter stays busy for kWwCap + 1 polls, and separately the texter is destroyed.
        //   Controls: a texter who becomes free goes on as EA (0); a busy texter under the cap keeps waiting (1).
        public static object WooHootyWaitCheck(string what)
        {
            string missing = PhPick(PhMissing(), "Phone.SendWooHootyText.GoToLotSuccess");
            string counters = PhCounters("woohootywait", 2642, "waits ended early", missing);
            if (what == "result") return counters;
            if (what != "run") return "woohootywait <run|result>";
            bool on = ShortcutOn("woohootywait");
            bool capped = WwPoll(on, kWwCap + 1, false, false, true) == 2, gone = WwPoll(on, 3, true, false, true) == 2;
            bool ended = capped && gone;
            bool ctlFree = WwPoll(on, 3, false, false, false) == 0, ctlBusy = WwPoll(on, 3, false, false, true) == 1;
            bool controls = ctlFree && ctlBusy;
            return "woohootywait check (switch " + (on ? "on" : "off") + "): wait ends at the cap=" + capped + ", ends when the texter is gone=" + gone
                + "; control: free texter goes on=" + ctlFree + ", busy under cap waits=" + ctlBusy + "; controls ok=" + controls
                + "; verdict: " + GlVerdict(on, ended, controls, missing.Length == 0) + "; " + counters;
        }

        // InviteCallReplyCheck run: the player answers "No" to a random invitation, and a caller with no household accepts.
        //   Controls: "Yes" stays RespondPositively; an accepted call from a caller with a household runs EA's OnAnswered.
        public static object InviteCallReplyCheck(string what)
        {
            string missing = PhPick(PhMissing(), "Phone.RandomInvitationCall.mbInvitationAccepted", "Phone.RandomInvitationCall.mCaller");
            string counters = PhCounters("invitecallreply", 2644, "replies corrected", missing);
            if (what == "result") return counters;
            if (what != "run") return "invitecallreply <run|result>";
            bool on = ShortcutOn("invitecallreply");
            bool no = PiResponse(on, false, PhoneCall.QueryResponse.RespondPositively) == PhoneCall.QueryResponse.RespondNegatively;
            bool noThrow = PiEndEarly(on, true, false, true);
            bool fixedNow = no && noThrow;
            bool ctlYes = PiResponse(on, true, PhoneCall.QueryResponse.RespondPositively) == PhoneCall.QueryResponse.RespondPositively;
            bool ctlHome = !PiEndEarly(on, true, false, false);
            bool controls = ctlYes && ctlHome;
            return "invitecallreply check (switch " + (on ? "on" : "off") + "): \"No\" answers negatively=" + no + ", household-less caller ends without throwing=" + noThrow
                + "; control: \"Yes\" stays positive=" + ctlYes + ", normal caller runs EA=" + ctlHome + "; controls ok=" + controls
                + "; verdict: " + GlVerdict(on, fixedNow, controls, missing.Length == 0) + "; " + counters;
        }

        // GradVisitorsCheck run: a dead (not ghost) Sim and a toddler with enough liking in the graduation visitor list.
        //   Controls: a living adult stays; a playable ghost stays.
        public static object GradVisitorsCheck(string what)
        {
            string missing = PhPick(PhMissing(), "Phone.Call.GetAllValidCallees(Sim,float)");
            string counters = PhCounters("gradvisitors", 2646, "entries dropped", missing);
            if (what == "result") return counters;
            if (what != "run") return "gradvisitors <run|result>";
            bool on = ShortcutOn("gradvisitors");
            bool dead = GvDrop(on, true, false, true, false, false, false), toddler = GvDrop(on, false, false, true, true, false, false);
            bool dropped = dead && toddler;
            bool ctlAdult = !GvDrop(on, false, false, true, false, false, false), ctlGhost = !GvDrop(on, true, true, true, false, false, false);
            bool controls = ctlAdult && ctlGhost;
            return "gradvisitors check (switch " + (on ? "on" : "off") + "): dead Sim dropped=" + dead + ", toddler dropped=" + toddler
                + "; control: living adult kept=" + ctlAdult + ", playable ghost kept=" + ctlGhost + "; controls ok=" + controls
                + "; verdict: " + GlVerdict(on, dropped, controls, missing.Length == 0) + "; " + counters;
        }
    }
}
