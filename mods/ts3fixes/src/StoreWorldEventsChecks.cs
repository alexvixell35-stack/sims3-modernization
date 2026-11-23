using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.SimIFace;
using Sims3.Store.Objects;

namespace Ts3Fixes
{
    // storeworldevents harness check (docs/557 §4.2). Production code: StoreWorldEvents.cs.
    public static partial class Fixes
    {
        // StoreWorldEventsCheck (repro store-world-events)
        //   run     adds to World.OnObjectPlacedInLotEventHandler a DeepFryerVegas.OnPlacedInWorldHandler bound to a fryer
        //           object that was never created in the simulator (no proxy, so HasBeenDestroyed: the state a deleted or
        //           previous-world fryer is in), as its OnStartup does, then runs the pass. Off arm: BROKEN = the handler
        //           is still in the list. On arm: FIXED = removed. The handler is taken out at the end in both arms.
        //   result  counters 2435-2437
        public static object StoreWorldEventsCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "storeworldevents: dead seen 2435=" + c[2435] + ", removed 2436=" + c[2436] + ", errors 2437=" + c[2437];
            if (what == "result") return counters;
            if (what != "run") return "storeworldevents <run|result>";
            if (sSwePlaced == null) return "storeworldevents check: World field not found (N/A)";
            object fryer = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(DeepFryerVegas));
            MethodInfo m = typeof(DeepFryerVegas).GetMethod("OnPlacedInWorldHandler", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (m == null) return "storeworldevents check: DeepFryerVegas.OnPlacedInWorldHandler not found (N/A)";
            EventHandler h = (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), fryer, m);
            World.OnObjectPlacedInLotEventHandler += h;
            StoreWorldEventsPass();
            bool still = false;
            Delegate ev = sSwePlaced.GetValue(null) as Delegate;
            if (ev != null) foreach (Delegate d in ev.GetInvocationList()) if (ReferenceEquals(d.Target, fryer)) still = true;
            World.OnObjectPlacedInLotEventHandler -= h;
            bool on = ShortcutOn("storeworldevents");
            string verdict = !on && still ? "BROKEN" : on && !still ? "FIXED" : "UNEXPECTED";
            return "storeworldevents check (switch " + (on ? "on" : "off") + "): dead fryer handler " + (still ? "still in" : "removed from") + " the placed-in-lot list; verdict: "
                + verdict + "; " + counters;
        }
    }
}
