using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects.HobbiesSkills;

namespace Ts3Fixes
{
    // sculptpose (tuning kSculptPose, off by default; docs/568 §3). EA bug fix in SculptingStation "Sculpt Sim"; the
    // same defect docs/566 §4 (portraitpose) fixed for the easel.
    //
    // [verified, decompiled] SculptingStation.SculptSim.Run (private) first queues PoseForSculpture on the subject
    // (Sculptor = the sculptor), then routes to the station and syncs with the subject (Started, Routed, Committed).
    // Only the "no sculpture made / sculptor told to exit" branch and the end of the sculpting loop end the pose
    // (Sculptee.AddExitReason(Finished)). The other ways out do not: a failed route to the station returns while the
    // subject still walks over and waits up to 30 sim-minutes per sync step, and SculptingSkill.CanAfford failing
    // after both reached Committed (funds spent since the material was picked) - or a sculptor reset there - leaves the
    // subject in PoseForSculpture's DoLoop(ExitReason.Default), which never re-checks the sculptor: it poses until the
    // player cancels it or a motive fails.
    // Fix: SculptSim.Run runs inside try/finally; when it did not succeed (false or unwinding), the subject's
    // PoseForSculpture entries whose Sculptor is this sculptor are cancelled with EA's InteractionQueue.CancelInteraction.
    // A successful sculpture runs EA's code unchanged.
    //
    // Counters: [2562] poses cancelled after the sculptor failed, [2563] errors. Check: SculptPoseChecks.cs.
    public static partial class Fixes
    {
        static readonly Type sSpSculptType = typeof(SculptingStation).GetNestedType("SculptSim", BindingFlags.NonPublic | BindingFlags.Public);
        static readonly FieldInfo sSpSculptee = sSpSculptType == null ? null : sSpSculptType.GetField("Sculptee", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        static readonly Type sSpPoseType = typeof(SculptingStation).GetNestedType("PoseForSculpture", BindingFlags.NonPublic | BindingFlags.Public);
        static readonly FieldInfo sSpSculptor = sSpPoseType == null ? null : sSpPoseType.GetField("Sculptor", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        static void ApplySculptPose()
        {
            if (sSpSculptType == null || sSpSculptee == null || sSpPoseType == null || sSpSculptor == null)
            {
                AppliedLog().Add("sculptpose: SculptingStation sculpt-Sim members not found, not patched");
                return;
            }
            Patch(sSpSculptType, "Run", typeof(SpHook), "RunHook", "RunOrig");
        }

        // ---- decision (also used by the check): cancel this pose entry after the sculptor's Run? ----
        internal static bool SpCancelPose(bool on, bool sculptorSucceeded, bool poseOfThisSculptor)
        {
            return on && !sculptorSucceeded && poseOfThisSculptor;
        }

        public class SpHook
        {
            public bool RunHook()
            {
                bool ok = false;
                try
                {
                    ok = RunOrig();
                    return ok;
                }
                finally
                {
                    if (!ok && ShortcutOn("sculptpose")) SpRelease(this);
                }
            }
            public bool RunOrig() { return false; } // receives SculptingStation.SculptSim.Run's original body

            static void SpRelease(object st)
            {
                try
                {
                    Sim sculptor = ((InteractionInstance)st).InstanceActor;
                    Sim sculptee = (Sim)sSpSculptee.GetValue(st);
                    if (sculptor == null || sculptee == null || sculptee.HasBeenDestroyed || sculptee.InteractionQueue == null) return;
                    List<InteractionInstance> poses = new List<InteractionInstance>();
                    foreach (InteractionInstance i in sculptee.InteractionQueue.InteractionList)
                        if (i != null && sSpPoseType.IsInstanceOfType(i) && SpCancelPose(true, false, sSpSculptor.GetValue(i) == sculptor)) poses.Add(i);
                    foreach (InteractionInstance i in poses)
                    {
                        PatchCounters()[2562]++;
                        sculptee.InteractionQueue.CancelInteraction(i, false);
                    }
                }
                catch (Exception) { PatchCounters()[2563]++; }
            }
        }
    }
}
