using System;
using System.Collections.Generic;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Objects.HobbiesSkills.Inventing;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // simbotworld (tuning kSimbotWorld, off by default; docs/870). EA bug fix: Ambitions' "Make Simbot" on the inventing
    // workbench never shows in a Late Night Downtown world (Bridgeport) or a Future world.
    //
    // [verified, tuning read] EA's ITUN for InventionWorkbench+MakeFrankensim+Definition on InventionWorkbench (CodeVersion
    // EP2) has World RestrictionType Allow, Types Base, Names empty; the workbench's other three interactions have None.
    // EA widened 20 other Allow tunings to Downtown (11 of them also to Future) and missed this one.
    // [verified, decompiled] Autonomy.CheckAvailability refuses an Allow tuning (Tuning_World) when neither the world type
    // nor the world name is listed; InteractionObjectPair keeps the same InteractionTuning object AutonomyTuning holds, so a
    // change to its Availability list reaches every workbench.
    //
    // Fix (no method swap, data only): at world load and in the check, with the switch on and the tuning still in EA's
    // shipped shape (SbwCore.EaShape), Downtown and Future are added to its world types, as EA did in its later tunings;
    // what was added is kept. Switch off: exactly that is removed again. Any other shape (another mod already changed it,
    // e.g. the ModTheSims override) is left alone and counted. Household Sims are included (an EA bug fix); no Sim and no
    // save data change, only the tuning in memory.
    // Counters: [2366] tunings widened, [2367] tuning not in EA's shape, left alone, [2368] widening undone (switch off),
    // [2369] errors. Check: SimbotWorldCheck.
    public static partial class Fixes
    {
        static readonly WorldType[] kSbwAdd = { WorldType.Downtown, WorldType.Future };
        static InteractionTuning sSbwTuning;     // the tuning we widened (null: none)
        static List<WorldType> sSbwAdded;        // what we added to it

        // interaction: the nested definition's name ("MakeFrankensim" is not public); AutonomyTuning's key is its FullName.
        static InteractionTuning SbwTuning(string interaction)
        {
            string bench = typeof(InventionWorkbench).FullName;
            return AutonomyTuning.GetTuning(bench + "+" + interaction + "+Definition", bench);
        }

        internal static void SimbotWorldWorldLoad() { SimbotWorldSync(); }

        // Brings the MakeFrankensim tuning in line with the switch.
        static void SimbotWorldSync()
        {
            int[] c = PatchCounters();
            try
            {
                if (!ShortcutOn("simbotworld"))
                {
                    if (sSbwTuning == null) return;
                    SbwCore.Unwiden(sSbwTuning.Availability.WorldRestrictionWorldTypes, sSbwAdded);
                    sSbwTuning = null; sSbwAdded = null;
                    c[2368]++;
                    return;
                }
                // shortcut: one record; a tuning re-read by a store hot install after the widening stays EA's until the switch goes off and on
                if (sSbwTuning != null) return;
                InteractionTuning t = SbwTuning("MakeFrankensim");
                if (t == null || t.Availability == null) return;  // no Ambitions tuning
                Availability a = t.Availability;
                List<WorldName> names = a.WorldRestrictionWorldNames;
                if (!SbwCore.EaShape(a.WorldRestrictionType == WorldRestrictionType.Allow, a.WorldRestrictionWorldTypes, WorldType.Base, names == null ? -1 : names.Count))
                {
                    c[2367]++;
                    return;
                }
                sSbwAdded = SbwCore.Widen(a.WorldRestrictionWorldTypes, kSbwAdd);
                sSbwTuning = t;
                c[2366]++;
            }
            catch (Exception) { c[2369]++; }
        }

        static bool SbwBlocks(Availability a, WorldType type, WorldName name)
        {
            return SbwCore.Blocks(a.WorldRestrictionType == WorldRestrictionType.Allow, a.WorldRestrictionType == WorldRestrictionType.Disallow,
                a.WorldRestrictionWorldTypes, a.WorldRestrictionWorldNames, type, name);
        }

        static string SbwShape(Availability a)
        {
            return a.WorldRestrictionType + " types [" + SbwList(a.WorldRestrictionWorldTypes) + "] names [" + SbwList(a.WorldRestrictionWorldNames) + "]";
        }

        static string SbwList<T>(List<T> l)
        {
            if (l == null) return "null";
            string s = "";
            foreach (T x in l) s += (s.Length == 0 ? "" : ",") + x;
            return s;
        }

        // SimbotWorldCheck run: re-syncs the tuning to the switch, then evaluates EA's world test (Autonomy.CheckAvailability's
        // world branch) on the live MakeFrankensim tuning in this world. In a Downtown or Future world: off BROKEN (refused),
        // on FIXED (allowed). Control: MakeInvention's tuning must pass here. Any other world: N/A.
        //   result  counters 2366-2369
        public static object SimbotWorldCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "simbotworld: widened 2366=" + c[2366] + ", not EA's shape left alone 2367=" + c[2367] + ", undone 2368=" + c[2368]
                + ", errors 2369=" + c[2369];
            if (what == "result") return counters;
            if (what != "run") return "simbotworld <run|result>";
            SimbotWorldSync();
            bool on = ShortcutOn("simbotworld");
            WorldType wt = GameUtils.GetCurrentWorldType();
            WorldName wn = GameUtils.GetCurrentWorld();
            string head = "simbotworld check (" + (on ? "on" : "off") + "): world " + wn + " type " + wt;
            InteractionTuning t = SbwTuning("MakeFrankensim");
            InteractionTuning ctl = SbwTuning("MakeInvention");
            if (t == null || ctl == null) return head + "; MakeFrankensim / MakeInvention tuning not found (Ambitions?); verdict: N/A; " + counters;
            bool blocked = SbwBlocks(t.Availability, wt, wn), ctlBlocked = SbwBlocks(ctl.Availability, wt, wn);
            bool bugWorld = Array.IndexOf(kSbwAdd, wt) >= 0;
            string v = !bugWorld ? "N/A" : ctlBlocked ? "UNEXPECTED" : on ? (blocked ? "UNEXPECTED" : "FIXED") : (blocked ? "BROKEN" : "UNEXPECTED");
            return head + "; MakeFrankensim " + SbwShape(t.Availability) + " added by us [" + SbwList(sSbwAdded) + "]: EA's world test "
                + (blocked ? "refuses it here" : "allows it here") + "; control MakeInvention " + SbwShape(ctl.Availability) + " "
                + (ctlBlocked ? "refused" : "allowed") + "; verdict: " + v + "; " + counters;
        }
    }
}
