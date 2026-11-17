using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.ActorSystems.Children;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // putdownplace harness check (docs/558 §2). Production code: PutDownPlace.cs.
    public static partial class Fixes
    {
        // ---------- harness check (pure decision + a read-only list; nothing in the world changes) ----------
        // PutDownPlaceCheck
        //   run     the bound for an NPC parent's 2nd route-only failure: off BROKEN = EA's failure stands (the child stays
        //           carried), on FIXED = in place; a household pair and a 1st failure always keep EA's code.
        //   who     Sims carrying a child now: scope, lot, failures counted in the window
        //   result  counters 2443-2447
        public static object PutDownPlaceCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "putdownplace: failed searches 2443=" + c[2443] + ", in place 2444=" + c[2444] + ", moved to safe spot 2445=" + c[2445]
                + ", declined 2446=" + c[2446] + ", errors 2447=" + c[2447];
            if (what == "result") return counters;
            if (what == "who") return PdWho() + counters;
            if (what != "run") return "putdownplace <run|who|result>";
            bool on = ShortcutOn("putdownplace");
            bool second = PutDownInPlace(on, true, 2), first = PutDownInPlace(on, true, 1), household = PutDownInPlace(on, false, 5);
            string verdict = first || household ? "UNEXPECTED" : second ? "FIXED" : "BROKEN";
            bool patched = sPdActor != null;
            return "putdownplace check (switch " + (on ? "on" : "off") + ", " + (patched ? "patched" : "NOT patched") + "): NPC 2nd failure in place " + second
                + " (1st failure " + first + ", household " + household + "); verdict: " + verdict + "; " + counters;
        }

        static string PdWho()
        {
            StringBuilder sb = new StringBuilder();
            int n = 0;
            foreach (Sim s in Sims3.Gameplay.Queries.GetObjects<Sim>())
            {
                CarryingChildPosture cc = s != null && !s.HasBeenDestroyed ? s.CarryingChildPosture : null;
                if (cc == null || cc.Child == null) continue;
                PdFails f;
                sPdFails.TryGetValue(s.ObjectId.Value, out f);
                sb.Append(s.FullName).Append(IsPlayerSim(s) ? " household" : " npc").Append(" carries ").Append(cc.Child.FullName)
                  .Append(", lot ").Append(s.LotCurrent != null ? s.LotCurrent.Name : "none").Append(" room ").Append(s.RoomId)
                  .Append(", doing ").Append(s.CurrentInteraction != null ? s.CurrentInteraction.GetType().Name : "none")
                  .Append(", failed put-downs ").Append(f != null ? f.N : 0).Append("\n");
                n++;
            }
            return "Sims carrying a child: " + n + "\n" + sb.ToString();
        }
    }
}
