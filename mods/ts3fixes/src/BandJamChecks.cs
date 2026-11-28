using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects.CookingObjects;
using Sims3.Gameplay.Objects.Electronics;
using Sims3.Gameplay.Objects.Environment;
using Sims3.Gameplay.Objects.HobbiesSkills;
using Sims3.Gameplay.Objects.MinorPets;
using Sims3.Gameplay.Objects.Misc;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // bandjam harness check (docs/559 §6). Throwaway objects, never added to the world; nothing in the world changes.
    public static partial class Fixes
    {
        // BandJamCheck run: (a) a throwaway JamController (never attached to an instrument) with one jammer in PendingExit
        //   (leaving) gets EA's SetSweetMove through the hook: off BROKEN = state turned into SweetMoveRequested (the
        //   leaver would hang); on FIXED = still PendingExit. (b) the stop rule for an empty controller with a sound array
        //   placeholder: off BROKEN = kept; on FIXED = stop chosen (decision only, no sound object exists).
        public static object BandJamCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "bandjam: refused requests 2458=" + c[2458] + ", sounds stopped 2459=" + c[2459] + ", errors 2460=" + c[2460];
            if (what == "result") return counters;
            if (what != "run") return "bandjam <run|result>";
            bool on = ShortcutOn("bandjam");
            Sim placeholder = (Sim)FormatterServices.GetUninitializedObject(typeof(Sim));
            JamController jc = new JamController();
            JamInfo info = (JamInfo)FormatterServices.GetUninitializedObject(typeof(JamInfo));
            info.Actor = placeholder;
            info.CurrentPlayState = JamInfo.PlayState.PendingExit;
            FieldInfo list = typeof(JamController).GetField("mJammers", BindingFlags.Instance | BindingFlags.NonPublic);
            List<JamInfo> jammers = new List<JamInfo>();
            jammers.Add(info);
            list.SetValue(jc, jammers);
            jc.SetSweetMove(placeholder, 99);  // 99: no other jammer qualifies, no skill lookup on the placeholder
            string a = info.CurrentPlayState == JamInfo.PlayState.PendingExit ? "FIXED" : info.CurrentPlayState == JamInfo.PlayState.SweetMoveRequested ? "BROKEN" : "UNEXPECTED";
            jammers.Clear();
            bool stop = on && jc.NumJammers == 0;
            string b = stop ? "FIXED" : "BROKEN";
            string verdict = a == b ? a : "UNEXPECTED";
            return "bandjam check (switch " + (on ? "on" : "off") + "): sweet move on a leaving jammer " + a + " (state " + info.CurrentPlayState + "), empty-jam sound stop " + b + "; verdict: " + verdict + "; " + counters;
        }
    }
}
