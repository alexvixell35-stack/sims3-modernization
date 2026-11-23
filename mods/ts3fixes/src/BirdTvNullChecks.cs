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
    // birdtvnull harness check (docs/559 §9). Throwaway objects, never added to the world; nothing in the world changes.
    public static partial class Fixes
    {
        // BirdTvNullCheck run: a throwaway TV (uninitialized, never placed) switched on with no channel. off BROKEN = EA's
        //   expression (tv.CurrentChannel.IsRomanticChannel) throws; on FIXED = the fixed test skips the TV.
        public static object BirdTvNullCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "birdtvnull: TVs skipped 2465=" + c[2465] + ", errors 2466=" + c[2466];
            if (what == "result") return counters;
            if (what != "run") return "birdtvnull <run|result>";
            bool on = ShortcutOn("birdtvnull");
            TV tv = (TV)FormatterServices.GetUninitializedObject(typeof(TV));
            tv.TurnedOn = true;
            string r;
            if (on)
            {
                TVChannelData ch;
                r = BirdTvChannel(tv, out ch) ? "UNEXPECTED" : "FIXED";
            }
            else
            {
                try { bool x = tv.CurrentChannel.IsRomanticChannel; r = x ? "UNEXPECTED" : "UNEXPECTED"; }
                catch (NullReferenceException) { r = "BROKEN"; }
            }
            return "birdtvnull check (switch " + (on ? "on" : "off") + "): TV on with no channel; verdict: " + r + "; " + counters;
        }
    }
}
