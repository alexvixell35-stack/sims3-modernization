using System;
using System.Reflection;

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
    }
}
