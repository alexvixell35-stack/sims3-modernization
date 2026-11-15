using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;

namespace Ts3Fixes
{
    // Small helpers several fix families share. Household and selected-Sim exemptions live in PlayerSims.cs, gone-Sim
    // rules in ErrorTrapSims.cs (EtSimGone) and DeadRefs.cs (SimGone).
    public static partial class Fixes
    {
        // Reflection flags for EA's private members: every instance member, every static member, or both.
        const BindingFlags kInstAll = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags kStaticAll = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags kAnyAll = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        // List<Sim>.RemoveAll predicate for the dead-Sim passes: null, destroyed, or no longer its description's Sim
        // (EtSimGone is true for null). One cached delegate, so the hourly passes allocate none per Sim.
        static readonly Predicate<Sim> sSimGonePred = EtSimGone;

        // The one-time "<name>: pass threw <message>" applied-log line of a periodic pass (threw: that pass's flag).
        static void PassThrewOnce(ref bool threw, string name, Exception e)
        {
            if (threw) return;
            threw = true;
            AppliedLog().Add(name + ": pass threw " + e.Message);
        }
    }
}
