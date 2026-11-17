using Sims3.Gameplay.Situations;

namespace Ts3Fixes
{
    // visittoolate harness check (docs/565 §5). Read-only: nothing in the world changes.
    public static partial class Fixes
    {
        // VisitTooLateCheck run: an NPC guest whose household is all outside, too late to visit, still in the
        //   TimeForGuestToLeave state, is sent home. Controls: a selectable guest, a service Sim, someone still inside, not
        //   too late, the state no longer current, the guest off the lot, already going home -> never.
        //   Live (read-only): visits in TimeForGuestToLeave with the guest outside, and whether it is too late now.
        //   off BROKEN = guest stays in the yard; on FIXED = sent home.
        //   result  counters 2534-2535
        public static object VisitTooLateCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "visittoolate: NPC guests sent home 2534=" + c[2534] + ", errors 2535=" + c[2535];
            if (what == "result") return counters;
            if (what != "run") return "visittoolate <run|result>";
            bool on = ShortcutOn("visittoolate");
            bool send = VtlSendHome(on, true, true, false, true, true, true, false);
            bool control = VtlSendHome(on, true, false, false, true, true, true, false) || VtlSendHome(on, true, true, true, true, true, true, false)
                || VtlSendHome(on, true, true, false, false, true, true, false) || VtlSendHome(on, true, true, false, true, false, true, false)
                || VtlSendHome(on, false, true, false, true, true, true, false) || VtlSendHome(on, true, true, false, true, true, false, false)
                || VtlSendHome(on, true, true, false, true, true, true, true);
            string live = "";
            try
            {
                int n = 0;
                foreach (VisitSituation v in Sims3.Gameplay.Autonomy.Situation.GetSituations<VisitSituation>())
                {
                    if (v.Child == null || v.Child.GetType() != sVtlState || v.Guest == null) continue;
                    if (n++ >= 6) break;
                    live += (live.Length > 0 ? "; " : "") + v.Guest.FullName + (v.Guest.IsOutside ? " outside" : " inside") + (v.Guest.IsSelectable ? " (selectable)" : "");
                }
                live = "too late now=" + VisitSituation.IsTooLateToVisit() + "; told to leave: " + (live.Length == 0 ? "none" : live);
            }
            catch (System.Exception e) { live = "live read threw " + e.GetType().Name; }
            string verdict = control ? "UNEXPECTED" : !on ? (send ? "UNEXPECTED" : "BROKEN") : (send ? "FIXED" : "UNEXPECTED");
            return "visittoolate check (switch " + (on ? "on" : "off") + "): NPC guest sent home=" + send + ", controls=" + control + "; live: " + live + "; verdict: " + verdict + "; " + counters;
        }
    }
}
