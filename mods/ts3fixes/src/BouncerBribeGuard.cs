using System;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Objects.Miscellaneous;

namespace Ts3Fixes
{
    // bribeguard (tuning kBribeGuard, off by default; docs/561 §7). EA bug fix, exact for every Sim.
    //
    // VelvetRopes [verified, decompiled]: BribeBouncer.Run records the offer (AddBribe creates mBribeDictionary) and
    // forces the "Bouncer Bribe Success / Failure / Unnecessary" social; its callbacks in SocialCallback call
    // ChargeSimForBribe, which does `mBribeDictionary.TryGetValue(...)` with no null check. NightlyCleanUp (the bar's
    // closing TNS pass in Bartending, and LotManager's nightly pass for non-bar lots) sets mBribeDictionary = null. A
    // bribe offered just before that runs reaches the callback with a null dictionary: NullReferenceException in the
    // social callback (no TNS, the social's outcome is cut off).
    // Fix: with a null dictionary, ChargeSimForBribe returns false, EA's own result for a Sim with no recorded bribe
    // (no charge; the callbacks then show EA's "no money" line).
    // Counters: [2493] charges guarded, [2494] errors. Check: BouncerBribeGuardChecks.cs.
    public static partial class Fixes
    {
        static readonly FieldInfo sBrDict = typeof(VelvetRopes).GetField("mBribeDictionary", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        static void ApplyBouncerBribeGuard()
        {
            if (sBrDict == null) { AppliedLog().Add("bribeguard: VelvetRopes.mBribeDictionary not found, not patched"); return; }
            Patch(typeof(VelvetRopes), "ChargeSimForBribe", typeof(BrHook), "ChargeHook", "ChargeOrig");
        }

        // The decision (also used by the check): the dictionary is gone, EA would throw.
        internal static bool BrGuard(bool on, bool dictNull)
        {
            return on && dictNull;
        }

        public class BrHook
        {
            public bool ChargeHook(Sim s)
            {
                if (!ShortcutOn("bribeguard")) return ChargeOrig(s);  // fast path: switch off
                bool guard = false;
                try { guard = BrGuard(true, sBrDict.GetValue((object)this) == null); }
                catch (Exception) { PatchCounters()[2494]++; }
                if (!guard) return ChargeOrig(s);
                PatchCounters()[2493]++;
                return false;
            }
            public bool ChargeOrig(Sim s) { return false; } // receives VelvetRopes.ChargeSimForBribe's original body
        }
    }
}
