using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects.HobbiesSkills;

namespace Ts3Fixes
{
    // portraitpose (tuning kPortraitPose, off by default; docs/566 §4). EA bug fix in Easel "Paint Portrait".
    //
    // [verified, decompiled] Easel.PaintPortrait.Run first queues PoseForPortrait on the subject (Painter = the painter),
    // then routes to the easel and syncs with the subject (Started, Routed, Committed). Only the success path ends the pose
    // (Paintee.AddExitReason(Finished) after the portrait grab). The painter's failed sync waits flag the subject too
    // (OnWaitForSynchronizationLevelWithSimFailed), but its other ways out do not: a failed route to the easel (or a
    // failed UI check) returns with the subject's pose still queued or waiting (the subject walks over and stands
    // waiting up to 30 sim-minutes per sync step), and a painter reset after both reached Committed leaves the subject
    // in PoseForPortrait's DoLoop(ExitReason.Default), which has no callback and never re-checks the painter: it poses
    // until the player cancels it or a motive fails.
    // Fix: PaintPortrait.Run runs inside try/finally; when it did not succeed (false or unwinding), the subject's
    // PoseForPortrait entries whose Painter is this painter are cancelled with EA's InteractionQueue.CancelInteraction.
    // A successful portrait runs EA's code unchanged.
    //
    // Counters: [2546] poses cancelled after the painter failed, [2547] errors. Check: PortraitPoseChecks.cs.
    public static partial class Fixes
    {
        static readonly FieldInfo sPpPaintee = typeof(Easel.PaintPortrait).GetField("Paintee", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        static readonly Type sPpPoseType = typeof(Easel).GetNestedType("PoseForPortrait", BindingFlags.NonPublic);
        static readonly FieldInfo sPpPainter = sPpPoseType == null ? null : sPpPoseType.GetField("Painter", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        static void ApplyPortraitPose()
        {
            if (sPpPaintee == null || sPpPoseType == null || sPpPainter == null)
            {
                AppliedLog().Add("portraitpose: Easel portrait members not found, not patched");
                return;
            }
            Patch(typeof(Easel.PaintPortrait), "Run", typeof(PpHook), "RunHook", "RunOrig");
        }

        // ---- decision (also used by the check): cancel this pose entry after the painter's Run? ----
        internal static bool PpCancelPose(bool on, bool painterSucceeded, bool poseOfThisPainter)
        {
            return on && !painterSucceeded && poseOfThisPainter;
        }

        public class PpHook
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
                    if (!ok && ShortcutOn("portraitpose")) PpRelease(this);
                }
            }
            public bool RunOrig() { return false; } // receives Easel.PaintPortrait.Run's original body

            static void PpRelease(object st)
            {
                try
                {
                    Easel.PaintPortrait me = (Easel.PaintPortrait)st;
                    Sim paintee = (Sim)sPpPaintee.GetValue(st);
                    if (paintee == null || paintee.HasBeenDestroyed || paintee.InteractionQueue == null) return;
                    List<InteractionInstance> poses = new List<InteractionInstance>();
                    foreach (InteractionInstance i in paintee.InteractionQueue.InteractionList)
                        if (i != null && sPpPoseType.IsInstanceOfType(i) && PpCancelPose(true, false, sPpPainter.GetValue(i) == me.Actor)) poses.Add(i);
                    foreach (InteractionInstance i in poses)
                    {
                        PatchCounters()[2546]++;
                        paintee.InteractionQueue.CancelInteraction(i, false);
                    }
                }
                catch (Exception) { PatchCounters()[2547]++; }
            }
        }
    }
}
