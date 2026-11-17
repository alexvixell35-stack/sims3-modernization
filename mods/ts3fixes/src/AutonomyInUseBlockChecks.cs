using System;
using System.Collections.Generic;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Situations;

namespace Ts3Fixes
{
    // Harness checks for AutonomyInUseBlock.cs (docs/577 §1) and the read-only privacy census (docs/577 §3).
    public static partial class Fixes
    {
        static string AibCounters(string missing)
        {
            int[] c = PatchCounters();
            string last = "";
            for (int k = 0; k < sAibLast.Length; k++) if (sAibLast[k] != null) last += (last.Length == 0 ? "" : ", ") + sAibLast[k];
            return "autonomyinuseblock: taken objects blocked 2693=" + c[2693] + ", seen with switch off 2694=" + c[2694]
                + ", errors 2695=" + c[2695] + "; last: " + (last.Length == 0 ? "none" : last) + "; " + CkMembers(missing);
        }

        // AutonomyInUseBlockCheck run: an autonomous action whose Run() returned false (no exit reason) because another Sim
        //   holds its object is blocked. Controls: succeeded, posture/test failure path (head not active), player-directed,
        //   cancelled, an exit reason set (EA's own block), a Sim target, an invalid target, the object free or held only by
        //   the actor / its partner -> never blocked by the fix.
        //   off BROKEN = not blocked (autonomy re-picks the taken object); on FIXED = blocked.
        public static object AutonomyInUseBlockCheck(string what)
        {
            string missing = ((sAibHeadActive == null ? "InteractionQueue.mIsHeadInteractionActive " : "")
                + (typeof(InteractionQueue).GetMethod("DeQueue", new Type[] { typeof(bool) }) == null ? "InteractionQueue.DeQueue(bool)" : "")).Trim();
            string counters = AibCounters(missing);
            if (what == "result") return counters;
            if (what != "run") return "autonomyinuseblock <run|result>";
            bool on = ShortcutOn("autonomyinuseblock");
            bool fixedNow = AibShouldBlock(on, false, true, true, false, false, true, false, true);
            bool controls = !AibShouldBlock(on, true, true, true, false, false, true, false, true)
                && !AibShouldBlock(on, false, false, true, false, false, true, false, true)
                && !AibShouldBlock(on, false, true, false, false, false, true, false, true)
                && !AibShouldBlock(on, false, true, true, true, false, true, false, true)
                && !AibShouldBlock(on, false, true, true, false, true, true, false, true)
                && !AibShouldBlock(on, false, true, true, false, false, false, false, true)
                && !AibShouldBlock(on, false, true, true, false, false, true, true, true)
                && !AibShouldBlock(on, false, true, true, false, false, true, false, false);
            return "autonomyinuseblock check (switch " + (on ? "on" : "off") + "): taken object blocked=" + fixedNow
                + "; controls ok=" + controls + "; verdict: " + CkVerdict(on, fixedNow, controls, missing.Length == 0) + "; " + counters;
        }

        // PrivacyCensus list: read-only. Every live PrivacySituation: type, lot, room, its Sim's state and head interaction.
        //   An entry whose Sim is null / destroyed / off the lot, or whose head interaction is not a privacy action, is a
        //   candidate room lock with nobody left to end it (docs/577 §3). Changes nothing.
        public static object PrivacyCensus(string what)
        {
            if (what != "list") return "privacycensus <list>";
            List<string> rows = new List<string>();
            int total = 0, orphan = 0;
            foreach (Situation s in new List<Situation>(Situation.sAllSituations))
            {
                PrivacySituation p = s as PrivacySituation;
                if (p == null) continue;
                total++;
                Sim sim = p.SyncTarget;
                string state = sim == null ? "sim=null" : sim.HasBeenDestroyed ? "sim=destroyed" : sim.LotCurrent != p.Lot ? "sim=off-lot" : "sim=on-lot";
                InteractionInstance head = sim != null && !sim.HasBeenDestroyed && sim.InteractionQueue != null ? sim.InteractionQueue.GetHeadInteraction() : null;
                if (state != "sim=on-lot" || head == null) orphan++;
                rows.Add(p.GetType().Name + " lot=" + (p.Lot != null ? p.Lot.LotId.ToString() : "null") + " room=" + p.RoomId
                    + " " + state + " head=" + (head != null ? head.GetType().Name : "none") + " exited=" + p.SituationExited);
            }
            return "privacycensus: " + total + " privacy situations, " + orphan + " with no Sim on the lot or an empty queue" + (rows.Count > 0 ? "; " + string.Join("; ", rows.ToArray()) : "");
        }
    }
}
