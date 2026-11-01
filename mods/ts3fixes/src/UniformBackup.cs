using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Careers;
using Sims3.SimIFace;
using Sims3.SimIFace.CAS;

namespace Ts3Fixes
{
    // uniformbackup (issue #101, docs/502): when the Sim wears its Career outfit, Occupation.ApplyOccupationUniform keeps the
    // old Career outfit in the static Occupation.backupOutfit (shared by all Sims) instead of releasing it. A later call,
    // made while the caller wears another outfit, uncaches that backup but never clears the field, so every later call
    // uncaches it again. Once the backup's key is free, the outfit store hands it to the next uniform of that name, and the
    // same call (it caches the new outfit before the backup step) uncaches the stale backup: the new Career outfit then has
    // no resource. It works until a save; after a reload it does not load.
    // Fix, switch on: after EA's call, a backup that EA uncached and left in the field is cleared, so it is uncached once.
    // EA's call runs unchanged. A backup left stale while the switch was off is not repaired. [2002] backups cleared.
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
                    PatchCounters()[2002]++;
                }
            }
            public void ApplyOrig(SimOutfit uniform) { } // receives Occupation.ApplyOccupationUniform's original header
        }

        static SimOutfit UniformCareer0(SimDescription sd)
        {
            return sd != null && sd.GetOutfitCount(OutfitCategories.Career) > 0 ? sd.GetOutfit(OutfitCategories.Career, 0) : null;
        }

        static SimDescription sUbSim;
        static OutfitCategories sUbWear; // the last "wear" category: "uniform" puts it back on if the Sim changed in between
        static readonly List<SimDescription> sUbUsed = new List<SimDescription>(); // the off arm leaves its Sim's Career outfit unloadable
        static Type sUbOccType; // the first pick's occupation: later picks prefer it (EA's uniform path differs by occupation)
        static object sUbCoreSwitch; // a patched Systems build's own switch (OutfitRepair.kClearBackupUniform), set to EA for the check
        static int sUbCleared0; // counter [2002] at the pick: each arm's "cleared" is counted from there
        // Agent checks for #101 (repro career-uniform-backup-check), the issue's live sequence with EA's own calls:
        //   "pick":    a Sim in the world with a Career outfit whose occupation hands out a uniform for its level
        //              (TryGetUniformForCurrentLevel true: only then does EA's SetOccupationOutfitForCurrentLevel call
        //              ApplyOccupationUniform; Film never does), active household first, never one picked before (the
        //              off arm leaves its Sim's Career outfit unloadable, so EA would make no new outfit for the on arm), with
        //              the first pick's occupation when one is left (Business stores a backup, Medical did not, 2026-11-01);
        //              clears the static backup so each arm starts from EA's state after load. When Career[0] holds K, one
        //              uniform call out of Career frees K first (#101's sequence starts with K free); throws if it does not.
        //   "wear <category>": SwitchToOutfitWithoutSpin, e.g. "wear Career", "wear Everyday".
        //   "uniform": the occupation's SetOccupationOutfitForCurrentLevel (what a promotion runs).
        //   "state":   Career outfit keys and whether each loads, the worn key, the name's key K and whether it loads, the
        //              backup, counter [2002] since the pick.
        //   "expect broken" (switch off) / "expect loads" (switch on): "state", but throws unless the arm ended as #101
        //              says: off, Career[0] does not load, the backup holds Career[0]'s key, nothing cleared; on, every
        //              Career outfit loads, no backup, at least one cleared.
        //   "stop":    forgets the Sim.
        // The load probe is EA's own (OutfitUtils.TryGenerateSimOutfit: new SimOutfit(key).IsValid, which EA runs on the
        // same key right after caching a uniform): it takes a load handle that the SimOutfit finalizer frees, and never
        // caches or uncaches. The expect rules use only the Career and backup keys, not K.
        public static string UniformBackupCheck(string what)
        {
            if (what == "pick")
            {
                sUbSim = null; sUbWear = OutfitCategories.None;
                List<SimDescription> cands = new List<SimDescription>();
                if (Household.ActiveHousehold != null) cands.AddRange(Household.ActiveHousehold.SimDescriptions);
                foreach (Sim w in Sims3.Gameplay.Queries.GetObjects<Sim>()) if (w.SimDescription != null) cands.Add(w.SimDescription);
                int noUniform = 0, notFollowing = 0;
                for (int pass = sUbOccType != null ? 0 : 1; pass < 2 && sUbSim == null; pass++) // pass 0: the first pick's occupation only
                foreach (SimDescription d in cands)
                {
                    if (sUbUsed.Contains(d) || d.Occupation == null || d.CreatedSim == null || !d.CreatedSim.InWorld || d.GetOutfitCount(OutfitCategories.Career) == 0) continue;
                    if (pass == 0 && d.Occupation.GetType() != sUbOccType) continue;
                    SimOutfit u;
                    if (!d.Occupation.TryGetUniformForCurrentLevel(out u) || u == null) { if (pass == 1) noUniform++; continue; }
                    // 2026-11-01 LR: a sleeping Sim kept its worn outfit through every switch, so EA never saw it in Career.
                    // Only a Sim whose worn outfit follows an instant switch to Career[0] can run the sequence.
                    Sim ps = d.CreatedSim;
                    OutfitCategories was = ps.CurrentOutfitCategory;
                    ps.SwitchToOutfitWithoutSpin(OutfitCategories.Career);
                    bool follows = CASUtils.GetOutfitInGameObject(ps.ObjectId) == d.GetOutfit(OutfitCategories.Career, 0).Key;
                    if (was != OutfitCategories.None && d.GetOutfitCount(was) > 0) ps.SwitchToOutfitWithoutSpin(was);
                    if (!follows) { notFollowing++; continue; }
                    sUbSim = d;
                    sUbUsed.Add(d);
                    break;
                }
                bool sameOcc = sUbOccType == null || (sUbSim != null && sUbSim.Occupation.GetType() == sUbOccType);
                if (sUbOccType == null && sUbSim != null) sUbOccType = sUbSim.Occupation.GetType();
                if (sUbSim == null) throw new InvalidOperationException("uniformbackup pick: no Sim left in the world (not picked before) with a Career outfit and an occupation that hands out a level uniform (" + noUniform + " with a Career outfit but no level uniform, " + notFollowing + " whose worn outfit did not follow a switch to Career)");
                if (!sameOcc) throw new InvalidOperationException("uniformbackup pick: " + sUbSim.FullName + " has " + sUbSim.Occupation.GetType().Name + ", not the first pick's " + sUbOccType.Name + " (EA's uniform path differs by occupation, so the arms are not comparable)");
                Occupation.backupOutfit = null;
                // 2026-11-01 LR rerun: Faith's Career[0] already held K at the pick, so every step got the next key and
                // K was never handed out again: #101's sequence needs K free at its start (Baseline-BP's Sim and LR run 1
                // had it free). One uniform call while the Sim is not in Career releases the old Career outfit, K included.
                string freed = "";
                if (sUbSim.GetOutfit(OutfitCategories.Career, 0).Key == UbKey(sUbSim))
                {
                    Sim fs = sUbSim.CreatedSim;
                    if (fs.CurrentOutfitCategory == OutfitCategories.Career) fs.SwitchToOutfitWithoutSpin(OutfitCategories.Everyday);
                    MethodInfo um = typeof(Occupation).GetMethod("SetOccupationOutfitForCurrentLevel", kInstAll, null, Type.EmptyTypes, null);
                    if (um != null) um.Invoke(sUbSim.Occupation, null);
                    Occupation.backupOutfit = null;
                    if (UniformLoads(UbKey(sUbSim)) || sUbSim.GetOutfit(OutfitCategories.Career, 0).Key == UbKey(sUbSim))
                        throw new InvalidOperationException("uniformbackup pick: " + sUbSim.FullName + "'s Career[0] holds K and one uniform call did not free it; " + UniformBackupCheck("state"));
                    freed = "; Career[0] held K: one uniform call freed it";
                }
                sUbCleared0 = PatchCounters()[2002];
                FieldInfo core = UniformCoreSwitch();
                if (core != null) { if (sUbCoreSwitch == null) sUbCoreSwitch = core.GetValue(null); core.SetValue(null, false); }
                return sUbSim.FullName + " (" + sUbSim.Occupation.GetType().Name + "); switch " + ShortcutOn("uniformbackup") + (core != null ? "; patched Systems build set to EA" : "") + freed + "; " + UniformBackupCheck("state");
            }
            if (what == "stop")
            {
                sUbSim = null; sUbWear = OutfitCategories.None;
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
                sUbWear = cat;
                return "wearing " + cat + "; " + UniformBackupCheck("state");
            }
            if (what == "uniform")
            {
                MethodInfo m = typeof(Occupation).GetMethod("SetOccupationOutfitForCurrentLevel", kInstAll, null, Type.EmptyTypes, null);
                if (m == null) return "SetOccupationOutfitForCurrentLevel not found";
                // 2026-11-01 runs 2-3: the on arm's Sim had left Career between the steps (a delayed swap of its own),
                // so EA released the old outfit instead of keeping it as the backup. Put the "wear" category back on
                // first, in the same call, so EA sees what the step asked for.
                string again = "";
                if (sim != null && sUbWear != OutfitCategories.None && sUbSim.GetOutfitCount(sUbWear) > 0
                    && CASUtils.GetOutfitInGameObject(sim.ObjectId) != sUbSim.GetOutfit(sUbWear, 0).Key)
                {
                    again = "re-wore " + sUbWear + " (the Sim had changed to " + sim.CurrentOutfitCategory + "); ";
                    sim.SwitchToOutfitWithoutSpin(sUbWear);
                }
                m.Invoke(sUbSim.Occupation, null);
                return again + "re-uniformed; " + UniformBackupCheck("state");
            }
            if (what == "state")
            {
                string career = "";
                for (int i = 0; i < sUbSim.GetOutfitCount(OutfitCategories.Career); i++)
                {
                    SimOutfit o = sUbSim.GetOutfit(OutfitCategories.Career, i);
                    career += (i > 0 ? ", " : "") + o.Key.InstanceId.ToString("x16") + (UniformLoads(o.Key) ? "" : " (does not load)");
                }
                ResourceKey k = UbKey(sUbSim);
                SimOutfit b = Occupation.backupOutfit;
                return "career [" + career + "], worn " + (sim != null ? CASUtils.GetOutfitInGameObject(sim.ObjectId).InstanceId.ToString("x16") : "no Sim")
                    + ", K " + k.InstanceId.ToString("x16") + " loads " + UniformLoads(k) + ", backup " + (b == null ? "null" : b.Key.InstanceId.ToString("x16")) + ", cleared " + (PatchCounters()[2002] - sUbCleared0);
            }
            if (what == "expect broken" || what == "expect loads")
            {
                string st = UniformBackupCheck("state");
                int n = sUbSim.GetOutfitCount(OutfitCategories.Career), cleared = PatchCounters()[2002] - sUbCleared0;
                SimOutfit c0 = n > 0 ? sUbSim.GetOutfit(OutfitCategories.Career, 0) : null, b = Occupation.backupOutfit;
                bool allLoad = n > 0;
                for (int i = 0; i < n; i++) if (!UniformLoads(sUbSim.GetOutfit(OutfitCategories.Career, i).Key)) allLoad = false;
                bool ok = what == "expect broken"
                    ? !ShortcutOn("uniformbackup") && c0 != null && !UniformLoads(c0.Key) && b != null && b.Key == c0.Key && cleared == 0
                    : ShortcutOn("uniformbackup") && allLoad && b == null && cleared >= 1;
                if (!ok) throw new InvalidOperationException("uniformbackup " + what + ": wrong outcome (switch " + ShortcutOn("uniformbackup") + "); " + st);
                return "as expected; " + st;
            }
            return "unknown check " + what + " (pick, wear <category>, uniform, state, expect broken, expect loads, stop)";
        }

        // K: the key EA's OutfitUtils.TryGenerateSimOutfit(name, BaseGame) builds for the name ApplyUniformToOutfit gives
        static ResourceKey UbKey(SimDescription d)
        {
            return ResourceKey.CreateOutfitKey("ApplyUniformToOutfit_Occupation.TryApplyUniformToOutfit_" + d.FullName, ResourceUtils.ProductVersionToGroupId(ProductVersion.BaseGame));
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
