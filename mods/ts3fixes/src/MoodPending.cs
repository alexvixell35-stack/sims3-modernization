using System;
using Sims3.Gameplay.ActorSystems;

namespace Ts3Fixes
{
    // moodpending (tuning kMoodPending, default ON since 2026-11-12; docs/545 "mood").
    //
    // EA bug [verified decompiled]: BuffManager.ChangeMoodValue(BuffInstance, int) always moves the Sim's mood target
    // (RemoveEffect old value, AddEffect new value). BuffWellRested.OnAddition calls Bed.UpdateWellRestedBuffBase, which calls
    // ChangeMoodValue(bi, WellRestedStrength = 10) on the new instance while it is still being added: AddElement runs
    // OnAddition before it puts the instance in the list and before its own AddEffect, so the strength is added twice and
    // removed once. Every WellRested from a bed (household Sims included) leaves +10 on the Uncomfortable axis for the
    // session (mTargetMoodAxis is not saved; a load rebuilds it from the buffs). BuffConqueredTheBull does the same from
    // OnAddition / OnReAddition (+15 / +30 Happy on medium / hard). Measured 2026-11-11: one household Sim at +10
    // Uncomfortable with a live WellRested, matching exactly.
    //
    // The pass: when ChangeMoodValue is called on an instance that is not the Sim's live buff of that guid
    // (GetElement(guid) is another object or null), only mEffectValue is set; the mood target gets the value once, from
    // AddElement's own AddEffect right after. For a live buff the original runs unchanged. Exact: the result equals what
    // EA's code would give if the instance were already listed. Counters: [2278] calls on a pending instance (also off),
    // [2279] mood moves skipped, [2280] errors.
    public static partial class Fixes
    {
        static void ApplyMoodPending()
        {
            Patch(typeof(BuffManager), "ChangeMoodValue", new Type[] { typeof(BuffInstance), typeof(int) }, typeof(MpHook), "ChangeHook", "ChangeOrig");
        }

        public class MpHook
        {
            public void ChangeHook(BuffInstance buff, int moodValue)
            {
                try
                {
                    BuffManager bm = (BuffManager)(object)this;
                    if (buff != null && buff.mEffectValue != moodValue && !object.ReferenceEquals(bm.GetElement(buff.Guid), buff))
                    {
                        int[] c = PatchCounters();
                        c[2278]++;
                        if (ShortcutOn("moodpending")) { buff.mEffectValue = moodValue; c[2279]++; return; }
                    }
                }
                catch (Exception) { PatchCounters()[2280]++; }
                ChangeOrig(buff, moodValue);
            }
            public void ChangeOrig(BuffInstance buff, int moodValue) { } // receives ChangeMoodValue(BuffInstance, int)'s original header
        }
    }
}
