using System;
using System.Collections.Generic;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;

namespace Ts3Agent
{
    // A test "lazy mod" for the leak hunt (issue #40, the dead-Sim hollowing idea: other mods may keep references to
    // destroyed Sims): a static list that keeps every Sim object it is shown and never lets go, as a mod
    // with a static List<Sim> cache would. With it on, every destroyed Sim stays in memory whatever ts3fixes releases, so a
    // run measures what deadsimhollow gives back from Sims that an unknown mod holds. Test only: the list lives in the
    // agent (not in saves) and empties when the game exits.
    public static partial class Handlers
    {
        static void RegisterHoldSims()
        {
            Dispatcher.Register("hold_sims", HoldSims);
        }

        static readonly List<Sim> sHeldSims = new List<Sim>();

        // hold_sims {clear?: false}: adds every Sim in LotManager.Actors that the list does not hold yet (clear: true empties
        // it first). Returns held (list size), added, destroyed (held Sims that have been destroyed).
        static object HoldSims(Args a)
        {
            RequireLive();
            if (a.Bool("clear", false)) sHeldSims.Clear();
            int added = 0, destroyed = 0;
            if (LotManager.Actors != null)
                foreach (Sim s in new List<Sim>(LotManager.Actors))
                    if (s != null && !sHeldSims.Contains(s)) { sHeldSims.Add(s); added++; }
            foreach (Sim s in sHeldSims) if (s.HasBeenDestroyed) destroyed++;
            Dictionary<string, object> r = Util.Obj();
            r["held"] = sHeldSims.Count;
            r["added"] = added;
            r["destroyed"] = destroyed;
            return r;
        }
    }
}
