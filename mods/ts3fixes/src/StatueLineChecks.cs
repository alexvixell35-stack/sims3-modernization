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
    // statueline harness check (docs/559 §7). Throwaway objects, never added to the world; nothing in the world changes.
    public static partial class Fixes
    {
        // StatueLineCheck run: a local copy of the line [other, actor] (placeholder Sims, the real static line untouched):
        //   off BROKEN = EA's Cleanup step removes `other`; on FIXED = `actor` removed, `other` kept at the head.
        public static object StatueLineCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "statueline: entries fixed 2461=" + c[2461] + ", errors 2462=" + c[2462];
            if (what == "result") return counters;
            if (what != "run") return "statueline <run|result>";
            bool on = ShortcutOn("statueline");
            Sim other = (Sim)FormatterServices.GetUninitializedObject(typeof(Sim));
            Sim actor = (Sim)FormatterServices.GetUninitializedObject(typeof(Sim));
            Queue<Sim> line = new Queue<Sim>();
            line.Enqueue(other);
            line.Enqueue(actor);
            bool wrong = SlWrongDequeue(line, actor);
            if (wrong && on) SlRemove(line, actor);
            if (line.Contains(actor)) line.Dequeue();  // EA's Cleanup step
            bool otherKept = line.Contains(other), actorGone = !line.Contains(actor);
            string verdict = !wrong ? "UNEXPECTED" : otherKept && actorGone ? "FIXED" : !otherKept && !actorGone ? "BROKEN" : "UNEXPECTED";
            return "statueline check (switch " + (on ? "on" : "off") + "): head kept=" + otherKept + ", cancelled performer removed=" + actorGone + "; verdict: " + verdict + "; " + counters;
        }
    }
}
