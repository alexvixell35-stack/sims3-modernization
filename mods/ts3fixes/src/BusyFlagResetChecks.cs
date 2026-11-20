using System;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Objects.PerformanceObjects;
using Sims3.Gameplay.Objects.Pets;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Harness checks for BusyFlagReset.cs (docs/575 §8). "run" is the pure decision with controls; "start" / "reset" /
    // "state" drive EA's own trigger on the newest self-cleaning litter box and stage light in the world (repro
    // busyflag-live): start their animation as a one-shot, reset the object while it plays, read the flags back.
    public static partial class Fixes
    {
        static readonly MethodInfo sBfLbRun = typeof(LitterBox).GetMethod("RunSelfCleanOneShotTask", kBfAll, null, Type.EmptyTypes, null);

        // BusyFlagResetCheck run: a flag left set with its task gone is cleared. Controls: the switch off, a flag that is
        //   not set, and a flag whose task still runs (never passed by the hooks) are left alone.
        public static object BusyFlagResetCheck(string what)
        {
            string miss = EpMiss("LitterBox.mIsCleaning", sBfLbCleaning, "LitterBox.mSelfCleanStateMachine", sBfLbSmc,
                "StageLightObject.mTransitioning", sBfSlTrans, "ChocolateFountain.mBeingCleaned", sBfCfClean, "GameObject+ResetInformation", sBfResetInfo, "LitterBox.RunSelfCleanOneShotTask", sBfLbRun);
            string counters = B582Counters("busyflagreset", 2714, new[] { "litter boxes freed", "stage lights freed", "errors", "fountains freed" }, miss);
            if (what == "result") return counters + "; " + BfState();
            if (what == "start") return BfStart();
            if (what == "reset") return BfReset();
            if (what == "state") return BfState();
            if (what != "run") return "busyflagreset <run|start|reset|state|result>";
            bool on = ShortcutOn("busyflagreset");
            bool fixedNow = BfClear(on, true, true), controls = !BfClear(on, false, true) && !BfClear(on, true, false);
            return B582Line("busyflagreset", on, "stale flag cleared after reset / at load", fixedNow, "flag not set / task still running left to EA=" + controls, controls, miss, counters);
        }

        static LitterBox BfNewestBox()
        {
            LitterBox best = null;
            foreach (LitterBox lb in Sims3.Gameplay.Queries.GetObjects<LitterBox>())
                if (lb is LitterBoxUber && lb.InWorld && (best == null || lb.ObjectId.Value > best.ObjectId.Value)) best = lb;
            return best;
        }

        static StageLightObject BfNewestLight()
        {
            StageLightObject best = null;
            foreach (StageLightObject sl in Sims3.Gameplay.Queries.GetObjects<StageLightObject>())
                if (sl.InWorld && (best == null || sl.ObjectId.Value > best.ObjectId.Value)) best = sl;
            return best;
        }

        // Starts EA's own self-clean on the box and Set Motion's StartSequence(Yield: true) on the light, both as one-shots
        // on the object (OnReset), as a cat's use and the Set Motion interaction do.
        static string BfStart()
        {
            string r = "";
            LitterBox lb = BfNewestBox();
            if (lb == null || sBfLbRun == null) r += "no self-cleaning litter box in the world; ";
            else
            {
                Sims3.Gameplay.Function f = (Sims3.Gameplay.Function)Delegate.CreateDelegate(typeof(Sims3.Gameplay.Function), lb, sBfLbRun);  // bind-ok: LitterBox.RunSelfCleanOneShotTask, private instance void()
                lb.StartOneShotFunction(f, GameObject.OneShotFunctionDisposeFlag.OnReset);
                r += "litter box " + lb.ObjectId.Value + " self-clean started; ";
            }
            StageLightObject sl = BfNewestLight();
            if (sl == null) r += "no stage light in the world";
            else
            {
                sl.StartOneShotFunction(delegate { sl.StartSequence(true); }, GameObject.OneShotFunctionDisposeFlag.OnReset);
                r += "stage light " + sl.ObjectId.Value + " sequence started";
            }
            return r + "; " + BfState();
        }

        static string BfReset()
        {
            string r = BfState() + " -> reset: ";
            LitterBox lb = BfNewestBox();
            if (lb != null) lb.SetObjectToReset();
            StageLightObject sl = BfNewestLight();
            if (sl != null) sl.SetObjectToReset();
            return r + BfState();
        }

        // Newest box and light, then how many of each in the world have the flag set (stuck when no task runs).
        static string BfState()
        {
            if (sBfLbCleaning == null || sBfLbSmc == null || sBfSlTrans == null) return "members missing";
            LitterBox lb = BfNewestBox();
            StageLightObject sl = BfNewestLight();
            int boxes = 0, boxesSet = 0, lights = 0, lightsSet = 0, founts = 0, fountsSet = 0;
            if (sBfCfClean != null) foreach (Sims3.Store.Objects.ChocolateFountain f in Sims3.Gameplay.Queries.GetObjects<Sims3.Store.Objects.ChocolateFountain>()) { founts++; if ((bool)sBfCfClean.GetValue(f)) fountsSet++; }
            foreach (LitterBox b in Sims3.Gameplay.Queries.GetObjects<LitterBox>()) { boxes++; if ((bool)sBfLbCleaning.GetValue(b) || sBfLbSmc.GetValue(b) != null) boxesSet++; }
            foreach (StageLightObject l in Sims3.Gameplay.Queries.GetObjects<StageLightObject>()) { lights++; if ((bool)sBfSlTrans.GetValue(l)) lightsSet++; }
            return "box " + (lb == null ? "-" : "cleaning=" + sBfLbCleaning.GetValue(lb) + " smc=" + (sBfLbSmc.GetValue(lb) != null))
                + ", light " + (sl == null ? "-" : "transitioning=" + sBfSlTrans.GetValue(sl))
                + "; world: litter boxes busy " + boxesSet + "/" + boxes + ", stage lights busy " + lightsSet + "/" + lights + ", chocolate fountains being cleaned " + fountsSet + "/" + founts;
        }
    }
}
