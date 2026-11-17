using System;
using System.Collections.Generic;
using Sims3.Gameplay.EventSystem;

namespace Ts3Fixes
{
    // eventcost (docs/545 §5, diagnostics only, switch off by default; no counter slots): EventTracker.ProcessEvent times
    // each dispatch per event id (EventCostCore). Off: one switch lookup per event, then EA's body. The table lives in
    // AppDomain data "ts3fixes.eventcost" (Dictionary<ulong, long[]>); ts3agent event_cost turns the switch on, reads and
    // resets it. Changes no game state: the original runs exactly as before, for every Sim.
    public static partial class Fixes
    {
        static Dictionary<ulong, long[]> sEcTable;

        static void ApplyEventCost()
        {
            sEcTable = new Dictionary<ulong, long[]>();
            AppDomain.CurrentDomain.SetData("ts3fixes.eventcost", sEcTable);
            Patch(typeof(EventTracker), "ProcessEvent", new Type[] { typeof(Event) }, typeof(EcHook), "ProcessEventHook", "ProcessEventOrig");
        }

        public class EcHook
        {
            public void ProcessEventHook(Event e)
            {
                if (e == null || !SwOn(kSwEventCost)) { ProcessEventOrig(e); return; }
                ulong id = (ulong)e.Id;
                long saved = EventCostCore.Enter();
                uint t0 = SrcNowUs();
                try { ProcessEventOrig(e); }
                finally { EventCostCore.Exit(sEcTable, id, (long)unchecked(SrcNowUs() - t0), saved); }
            }
            public void ProcessEventOrig(Event e) { } // receives EventTracker.ProcessEvent's original header
        }
    }
}
