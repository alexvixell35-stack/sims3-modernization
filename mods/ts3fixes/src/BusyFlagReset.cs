using System;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Objects.PerformanceObjects;
using Sims3.Gameplay.Objects.Pets;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // busyflagreset (kBusyFlagReset, docs/575 §8) [mechanism verified in the scripts; in-game repro: BusyFlagResetCheck
    //   live]: two objects set a saved "busy" bool, yield inside an animation, and clear the bool only when the code runs
    //   to the end. Nothing else clears it: not the object's DoReset, not a load.
    //   - LitterBoxUber (Pets, self-cleaning box): after a cat's use, UpdateMotivesAndDirtyLevel starts
    //     RunSelfCleanOneShotTask as a one-shot with OneShotFunctionDisposeFlag.OnReset. The task sets mIsCleaning and
    //     mSelfCleanStateMachine, plays SelfClean (RequestState yields), then clears both. An object reset kills the
    //     one-shot (GameObject.DoReset -> RemoveOneShotFunctionOnReset, after LitterBox.DoReset's own work), and a save
    //     taken during the animation stores mIsCleaning = true. Then every UseLitterBox waits 5 minutes in
    //     WaitForLitterbox and fails. Only a finished use starts a new self-clean, and RunSelfCleanOneShotTask skips when
    //     mSelfCleanStateMachine is not null, so the box stays unusable for good.
    //   - StageLightObject (EP6 stage lights, FX towers): StartSequence(Yield: true) (Set Motion) sets mTransitioning,
    //     yields in RequestState, then clears it. A reset or save in that window leaves it set: every light interaction
    //     greys out ("LightIsTransitioning"), and the next StartSequence spins in `while (mTransitioning) Sleep(1)` for
    //     good. StageLightObject.DoReset does not clear it.
    //   - ChocolateFountain (store): CleanOut.Run sets mBeingCleaned, StandardEntry, animates "Clean", and clears it at
    //     the end. A reset of the cleaning Sim also resets the fountain (Sim reset adds its current target when the Sim is
    //     on its use list), but ChocolateFountain.DoReset only clears the table slots, so CleanOut's Test refuses for good:
    //     the fountain can never be cleaned out again (its spoilage alarm was already removed at the start of the clean).
    //   Fix: after the object's DoReset (its own task is already gone; the fountain's reset also resets every Sim on its
    //   use list), and at world load (no task from before the save runs), the flag is cleared; the litter box's state
    //   machine is disposed and its material set back to "Default" (the self-clean animation sets "InUse" at event 117
    //   and "Default" at 118). Objects only; no Sim is touched.
    //   Counters [2714] litter boxes freed, [2715] stage lights freed, [2716] errors, [2717] fountains freed.
    //   Checks: BusyFlagResetChecks.cs.
    public static partial class Fixes
    {
        const BindingFlags kBfAll = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        static readonly FieldInfo sBfLbCleaning = typeof(LitterBox).GetField("mIsCleaning", kBfAll);
        static readonly FieldInfo sBfLbSmc = typeof(LitterBox).GetField("mSelfCleanStateMachine", kBfAll);
        static readonly FieldInfo sBfSlTrans = typeof(StageLightObject).GetField("mTransitioning", kBfAll);
        static readonly FieldInfo sBfCfClean = typeof(Sims3.Store.Objects.ChocolateFountain).GetField("mBeingCleaned", kBfAll);
        static readonly Type sBfResetInfo = typeof(GameObject).GetNestedType("ResetInformation", BindingFlags.NonPublic | BindingFlags.Public);

        // Pure: is the busy flag stale? Only when its task is gone: after the object's DoReset or at world load.
        internal static bool BfClear(bool on, bool flagSet, bool taskGone)
        {
            return on && flagSet && taskGone;
        }

        static void ApplyBusyFlagReset()
        {
            if (sBfResetInfo == null) { AppliedLog().Add("busyflagreset: not found: GameObject+ResetInformation"); return; }
            Patch(typeof(LitterBox), "DoReset", new Type[] { sBfResetInfo }, typeof(BfLitterHook), "ResetHook", "ResetOrig");
            Patch(typeof(StageLightObject), "DoReset", new Type[] { sBfResetInfo }, typeof(BfLightHook), "ResetHook", "ResetOrig");
            Patch(typeof(Sims3.Store.Objects.ChocolateFountain), "DoReset", new Type[] { sBfResetInfo }, typeof(BfFountainHook), "ResetHook", "ResetOrig");
        }

        // One litter box: clears mIsCleaning and the self-clean state machine. True when something was stale.
        static bool BfFreeLitter(LitterBox lb, bool dispose)
        {
            bool cleaning = (bool)sBfLbCleaning.GetValue(lb);
            StateMachineClient smc = sBfLbSmc.GetValue(lb) as StateMachineClient;
            if (!BfClear(ShortcutOn("busyflagreset"), cleaning || smc != null, true)) return false;
            sBfLbSmc.SetValue(lb, null);
            sBfLbCleaning.SetValue(lb, false);
            if (dispose && smc != null) { try { smc.Dispose(); } catch (ResetException) { throw; } catch (Exception) { PatchCounters()[2716]++; } }
            if (cleaning && Sims3.SimIFace.Objects.IsValid(lb.ObjectId)) lb.SetMaterial("Default");
            PatchCounters()[2714]++;
            return true;
        }

        static bool BfFreeLight(StageLightObject sl)
        {
            if (!BfClear(ShortcutOn("busyflagreset"), (bool)sBfSlTrans.GetValue(sl), true)) return false;
            sBfSlTrans.SetValue(sl, false);
            PatchCounters()[2715]++;
            return true;
        }

        static bool BfFreeFountain(Sims3.Store.Objects.ChocolateFountain cf)
        {
            if (!BfClear(ShortcutOn("busyflagreset"), (bool)sBfCfClean.GetValue(cf), true)) return false;
            sBfCfClean.SetValue(cf, false);
            PatchCounters()[2717]++;
            return true;
        }

        public class BfLitterHook
        {
            public void ResetHook(object resetInformation)
            {
                ResetOrig(resetInformation);
                try { if (sBfLbCleaning != null && sBfLbSmc != null) BfFreeLitter((LitterBox)(object)this, true); }
                catch (ResetException) { throw; }
                catch (Exception) { PatchCounters()[2716]++; }
            }
            public void ResetOrig(object resetInformation) { } // LitterBox.DoReset
        }

        public class BfLightHook
        {
            public void ResetHook(object resetInformation)
            {
                ResetOrig(resetInformation);
                try { if (sBfSlTrans != null) BfFreeLight((StageLightObject)(object)this); }
                catch (ResetException) { throw; }
                catch (Exception) { PatchCounters()[2716]++; }
            }
            public void ResetOrig(object resetInformation) { } // StageLightObject.DoReset
        }

        public class BfFountainHook
        {
            public void ResetHook(object resetInformation)
            {
                ResetOrig(resetInformation);
                try { if (sBfCfClean != null) BfFreeFountain((Sims3.Store.Objects.ChocolateFountain)(object)this); }
                catch (ResetException) { throw; }
                catch (Exception) { PatchCounters()[2716]++; }
            }
            public void ResetOrig(object resetInformation) { } // ChocolateFountain.DoReset
        }

        // World load finished (Instantiator): flags saved while the task ran. A saved state machine is not disposed (its
        // native side did not survive the load).
        public static void BusyFlagWorldLoad()
        {
            try
            {
                if (!ShortcutOn("busyflagreset")) return;
                if (sBfLbCleaning != null && sBfLbSmc != null)
                    foreach (LitterBox lb in Sims3.Gameplay.Queries.GetObjects<LitterBox>())
                    {
                        try { if (lb != null) BfFreeLitter(lb, false); }
                        catch (Exception) { PatchCounters()[2716]++; }
                    }
                if (sBfSlTrans != null)
                    foreach (StageLightObject sl in Sims3.Gameplay.Queries.GetObjects<StageLightObject>())
                    {
                        try { if (sl != null) BfFreeLight(sl); }
                        catch (Exception) { PatchCounters()[2716]++; }
                    }
                if (sBfCfClean != null)
                    foreach (Sims3.Store.Objects.ChocolateFountain cf in Sims3.Gameplay.Queries.GetObjects<Sims3.Store.Objects.ChocolateFountain>())
                    {
                        try { if (cf != null) BfFreeFountain(cf); }
                        catch (Exception) { PatchCounters()[2716]++; }
                    }
            }
            catch (Exception) { PatchCounters()[2716]++; }
        }
    }
}
