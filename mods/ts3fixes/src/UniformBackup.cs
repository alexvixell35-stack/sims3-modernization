using System;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Careers;
using Sims3.SimIFace;
using Sims3.SimIFace.CAS;

namespace Ts3Fixes
{
    // uniformbackup (issue #101, docs/224): when the Sim wears its Career outfit, Occupation.ApplyOccupationUniform keeps the
    // old Career outfit in the static Occupation.backupOutfit (shared by all Sims) instead of releasing it. A later call,
    // made while the caller wears another outfit, uncaches that backup but never clears the field, so every later call
    // uncaches it again. Once the backup's key is free, the outfit store hands it to the next uniform of that name, and the
    // same call (it caches the new outfit before the backup step) uncaches the stale backup: the new Career outfit then has
    // no resource. It works until a save; after a reload it does not load.
    // Fix, switch on: after EA's call, a backup that EA uncached and left in the field is cleared, so it is uncached once.
    // EA's call runs unchanged. A backup left stale while the switch was off is not repaired. [612] backups cleared.
    public static partial class Fixes
    {
        public class UniformBackupHook
        {
            public void ApplyHook(SimOutfit uniform)
            {
                SimOutfit b0 = Occupation.backupOutfit;
                if (b0 == null || !ShortcutOn("uniformbackup"))
                {
                    ApplyOrig(uniform);
                    return;
                }
                Occupation occ = (Occupation)(object)this;
                SimDescription sd = occ.OwnerDescription;
                Sim s = occ.OwnerSim;
                ResourceKey worn = s != null ? CASUtils.GetOutfitInGameObject(s.ObjectId) : ResourceKey.kInvalidResourceKey; // EA's own worn key
                SimOutfit c0 = UniformCareer0(sd);
                ApplyOrig(uniform);
                if (UniformBackupCore.ClearAfter(true, Occupation.backupOutfit == b0, b0.Key == worn, UniformCareer0(sd) != c0))
                {
                    Occupation.backupOutfit = null;
                    PatchCounters()[612]++;
                }
            }
            public void ApplyOrig(SimOutfit uniform) { } // receives Occupation.ApplyOccupationUniform's original header
        }

        static SimOutfit UniformCareer0(SimDescription sd)
        {
            return sd != null && sd.GetOutfitCount(OutfitCategories.Career) > 0 ? sd.GetOutfit(OutfitCategories.Career, 0) : null;
        }

        static SimDescription sUbSim;
        static object sUbCoreSwitch; // a patched Systems build's own switch (OutfitRepair.kClearBackupUniform), set to EA for the check
        // Agent checks for #101 (repro career-uniform-backup-check), the issue's live sequence with EA's own calls:
        //   "pick":    the first active-household Sim with an occupation, a Sim in the world and a Career outfit; clears the
        //              static backup so each arm starts from EA's state after load.
        //   "wear <category>": SwitchToOutfitWithoutSpin, e.g. "wear Career", "wear Everyday".
        //   "uniform": the occupation's SetOccupationOutfitForCurrentLevel (what a promotion runs).
        //   "state":   Career outfit keys and whether each loads, the worn key, whether the name's key K loads, the backup,
        //              counter [612].
        //   "stop":    forgets the Sim.
        public static string UniformBackupCheck(string what)
        {
            if (what == "pick")
            {
                sUbSim = null;
                if (Household.ActiveHousehold != null)
                    foreach (SimDescription d in Household.ActiveHousehold.SimDescriptions)
                        if (d.Occupation != null && d.CreatedSim != null && d.CreatedSim.InWorld && d.GetOutfitCount(OutfitCategories.Career) > 0) { sUbSim = d; break; }
                if (sUbSim == null) return "no active-household Sim with an occupation, a Sim and a Career outfit";
                Occupation.backupOutfit = null;
                FieldInfo core = UniformCoreSwitch();
                if (core != null) { if (sUbCoreSwitch == null) sUbCoreSwitch = core.GetValue(null); core.SetValue(null, false); }
                return sUbSim.FullName + " (" + sUbSim.Occupation.GetType().Name + "); switch " + ShortcutOn("uniformbackup") + (core != null ? "; patched Systems build set to EA" : "") + "; " + UniformBackupCheck("state");
            }
            if (what == "stop")
            {
                sUbSim = null;
                FieldInfo core = UniformCoreSwitch();
                if (core != null && sUbCoreSwitch != null) core.SetValue(null, sUbCoreSwitch);
                sUbCoreSwitch = null;
                return "stopped";
            }
            if (sUbSim == null) return "pick first";
            Sim sim = sUbSim.CreatedSim;
            if (what.StartsWith("wear "))
            {
                if (sim == null) return "no Sim";
                OutfitCategories cat = (OutfitCategories)Enum.Parse(typeof(OutfitCategories), what.Substring(5));
                sim.SwitchToOutfitWithoutSpin(cat);
                return "wearing " + cat + "; " + UniformBackupCheck("state");
            }
            if (what == "uniform")
            {
                MethodInfo m = typeof(Occupation).GetMethod("SetOccupationOutfitForCurrentLevel", kInstAll, null, Type.EmptyTypes, null);
                if (m == null) return "SetOccupationOutfitForCurrentLevel not found";
                m.Invoke(sUbSim.Occupation, null);
                return "re-uniformed; " + UniformBackupCheck("state");
            }
            if (what == "state")
            {
                string career = "";
                for (int i = 0; i < sUbSim.GetOutfitCount(OutfitCategories.Career); i++)
                {
                    SimOutfit o = sUbSim.GetOutfit(OutfitCategories.Career, i);
                    career += (i > 0 ? ", " : "") + o.Key.InstanceId.ToString("x16") + (UniformLoads(o.Key) ? "" : " (does not load)");
                }
                SimOutfit fresh;
                bool k = OutfitUtils.TryGenerateSimOutfit("ApplyUniformToOutfit_Occupation.TryApplyUniformToOutfit_" + sUbSim.FullName, ProductVersion.BaseGame, out fresh);
                SimOutfit b = Occupation.backupOutfit;
                return "career [" + career + "], worn " + (sim != null ? CASUtils.GetOutfitInGameObject(sim.ObjectId).InstanceId.ToString("x16") : "no Sim")
                    + ", K loads " + k + ", backup " + (b == null ? "null" : b.Key.InstanceId.ToString("x16")) + ", cleared " + PatchCounters()[612];
            }
            return "unknown check " + what + " (pick, wear <category>, uniform, state, stop)";
        }

        static bool UniformLoads(ResourceKey k)
        {
            try { return new SimOutfit(k).IsValid; }
            catch (Exception) { return false; }
        }

        static FieldInfo UniformCoreSwitch()
        {
            Type t = typeof(Occupation).Assembly.GetType("Sims3.Gameplay.CAS.OutfitRepair");
            return t != null ? t.GetField("kClearBackupUniform", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) : null;
        }
    }
}
