using System;
using System.Reflection;
using Sims3.Gameplay.ActorSystems;

namespace Ts3Fixes
{
    // mooddrift (tuning kMoodDrift, default ON since 2026-11-12; docs/191 "moodlets").
    //
    // EA bug [verified decompiled]: MoodManager.mTargetMoodAxis, the mood a Sim's mood bar moves to, is kept only by
    // AddEffect / RemoveEffect pairs (plain adds: BuffManager 1031/1180/1306/1360-1362, BuffInstance.OnLoadFixup 268;
    // UpdateMoodLerp's MeanSpirited term is added and taken back inside the method). Removal subtracts the buff's CURRENT
    // mEffectValue. Several buffs change a live instance's value without the pair: BuffFear.OnReAddition adds the new
    // Fear's value to the existing one and returns true, and BuffManager then returns before touching the mood (1143);
    // likewise MissingAppointment, PhoneJunkie, SugarHigh, SuccessfulPrank, GoodNectar, WashAwayTheWorry, Werewolf,
    // FreshClothing (LaundryManager), VaultOfAntiquity, Wow buffs. While the buff is up the mood uses the old value; after
    // it ends the difference stays in the Sim's mood until the save is reloaded (load rebuilds the target from the live
    // buffs). E.g. Fear re-added twice at -20 leaves the Sim +20 happier for good.
    //
    // The pass (switch on; EA bug fix, affects household Sims' mood): after BuffManager.UpdateBuffs (each Sim, at most once
    // per sim minute), the target is set to the sum of the live buffs' mEffectValue per mAxisEffected when it differs:
    // exactly what a reload computes. Counters: [2251] Sims corrected (also counted with the switch off: drift seen),
    // [2252] total mood points corrected, [2253] errors.
    public static partial class Fixes
    {
        static FieldInfo sMdTarget, sMdActor;

        static void ApplyMoodDrift()
        {
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            sMdTarget = typeof(MoodManager).GetField("mTargetMoodAxis", inst);
            sMdActor = typeof(BuffManager).GetField("mActor", inst);
            if (sMdTarget == null || sMdActor == null) { AppliedLog().Add("mooddrift: fields not found, not patched"); return; }
            Patch(typeof(BuffManager), "UpdateBuffs", new Type[] { typeof(float) }, typeof(MdHook), "UpdateHook", "UpdateOrig");
        }

        public class MdHook
        {
            public void UpdateHook(float simMinutesPassed)
            {
                UpdateOrig(simMinutesPassed);
                try
                {
                    BuffManager bm = (BuffManager)(object)this;
                    Sims3.Gameplay.Actors.Sim actor = sMdActor.GetValue(bm) as Sims3.Gameplay.Actors.Sim;
                    if (actor == null || actor.HasBeenDestroyed || actor.MoodManager == null) return;
                    int[] target = sMdTarget.GetValue(actor.MoodManager) as int[];
                    if (target == null) return;
                    int[] sum = new int[target.Length];
                    foreach (BuffInstance bi in bm.List)
                    {
                        if (bi == null) continue;
                        int ax = (int)bi.mAxisEffected;
                        if (ax >= 0 && ax < sum.Length) sum[ax] += bi.mEffectValue;
                    }
                    int diff = 0;
                    for (int i = 0; i < sum.Length; i++) diff += Math.Abs(target[i] - sum[i]);
                    if (diff == 0) return;
                    int[] c = PatchCounters();
                    c[2251]++;
                    if (!ShortcutOn("mooddrift")) return;
                    for (int i = 0; i < sum.Length; i++) target[i] = sum[i];
                    c[2252] += diff;
                }
                catch (Exception) { PatchCounters()[2253]++; }
            }
            public void UpdateOrig(float simMinutesPassed) { }
        }
    }
}
