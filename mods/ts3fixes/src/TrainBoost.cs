using System;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Skills;

namespace Ts3Fixes
{
    // trainboost (tuning kTrainBoost, off by default; docs/569 §1). EA bug fix in AthleticGameObject "Train Sim".
    //
    // [verified, decompiled] AthleticGameObject.TrainSim.Run raises the trainee's Athletic skill-gain modifier
    // (SkillManager.AddToSkillGainModifier, + kAthleticSkillMultiplierOfTrainedSim - 1) and shape-delta multiplier
    // (SimDescription.UpdateShapeDeltaMultiplier, + kBodyShapeMultiplierOfTrainedSim - 1) before its DoLoop and lowers
    // them only after DoLoop returns, with no try/finally and nothing in Cleanup. A trainer reset during the session
    // (ResetException unwinds Run) skips the lowering: both values are saved with the trainee (mSkillModifiers,
    // mShapeDeltaMultiplier are persisted), so the trainee gains Athletic and body shape twice as fast for good, and
    // each further interrupted session adds another +1. Cancels and normal ends return from DoLoop and are fine.
    // Fix: Run runs inside try/finally. Before EA's body the trainee's two values are read; when the body unwinds
    // (did not return) and the trainee's values are exactly EA's added amounts above the start, EA's own
    // SubtractFromSkillGainModifier / UpdateShapeDeltaMultiplier(-amount) undo them, as EA's normal path does.
    // A normal return (success or failure) runs EA's code unchanged. The store BalletBarre TrainSimBB overrides Run and
    // is not covered.
    //
    // Counters: [2566] boosts undone after an unwound session, [2567] errors. Check: TrainBoostChecks.cs.
    public static partial class Fixes
    {
        static readonly FieldInfo sTbSkillMult = typeof(AthleticGameObject.TrainSim).GetField("kAthleticSkillMultiplierOfTrainedSim", kStaticAll);
        static readonly FieldInfo sTbShapeMult = typeof(AthleticGameObject.TrainSim).GetField("kBodyShapeMultiplierOfTrainedSim", kStaticAll);
        static readonly FieldInfo sTbShape = typeof(SimDescription).GetField("mShapeDeltaMultiplier", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        static void ApplyTrainBoost()
        {
            if (sTbSkillMult == null || sTbShapeMult == null || sTbShape == null)
            {
                AppliedLog().Add("trainboost: TrainSim tunables or SimDescription.mShapeDeltaMultiplier not found, not patched");
                return;
            }
            Patch(typeof(AthleticGameObject.TrainSim), "Run", typeof(TbHook), "RunHook", "RunOrig");
        }

        // ---- decision (also used by the check): undo an added boost after Run? ----
        // delta = trainee value now minus at the start; added = EA's amount for that value.
        internal static bool TbUndo(bool on, bool returnedNormally, float delta, float added)
        {
            return on && !returnedNormally && added != 0f && Math.Abs(delta - added) < 0.001f;
        }

        public class TbHook
        {
            public bool RunHook()
            {
                object st = this;
                AthleticGameObject.TrainSim me = (AthleticGameObject.TrainSim)st;
                Sim trainee = null;
                float skill0 = 0f, shape0 = 0f;
                if (ShortcutOn("trainboost"))
                {
                    try
                    {
                        trainee = me.Trainee ?? (me.Target == null ? null : me.Target.OtherActor(me.Actor));
                        if (trainee != null)
                        {
                            skill0 = trainee.SkillManager.GetSkillGainModifier(SkillNames.Athletic);
                            shape0 = (float)sTbShape.GetValue(trainee.SimDescription);
                        }
                    }
                    catch (Exception) { trainee = null; PatchCounters()[2567]++; }
                }
                bool normal = false;
                try
                {
                    bool r = RunOrig();
                    normal = true;
                    return r;
                }
                finally
                {
                    if (!normal && trainee != null && me.Trainee == trainee) TbRepair(trainee, skill0, shape0);
                }
            }
            public bool RunOrig() { return false; } // receives AthleticGameObject.TrainSim.Run's original body

            static void TbRepair(Sim trainee, float skill0, float shape0)
            {
                try
                {
                    if (trainee.HasBeenDestroyed) return;
                    float skillAdded = (float)sTbSkillMult.GetValue(null) - 1f;
                    float shapeAdded = (float)sTbShapeMult.GetValue(null) - 1f;
                    bool undone = false;
                    if (TbUndo(true, false, trainee.SkillManager.GetSkillGainModifier(SkillNames.Athletic) - skill0, skillAdded))
                    {
                        trainee.SkillManager.SubtractFromSkillGainModifier(SkillNames.Athletic, skillAdded);
                        undone = true;
                    }
                    if (TbUndo(true, false, (float)sTbShape.GetValue(trainee.SimDescription) - shape0, shapeAdded))
                    {
                        trainee.SimDescription.UpdateShapeDeltaMultiplier(-shapeAdded);
                        undone = true;
                    }
                    if (undone) PatchCounters()[2566]++;
                }
                catch (Exception) { PatchCounters()[2567]++; }
            }
        }
    }
}
