using System;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;

namespace Ts3Fixes
{
    // Issue #89 (docs/51 §386 part A, switch posturepopcycle, default off): EA's Sim.set_Posture first asks
    // newPosture.Compatible(current); when true it links the new posture on top (new.PreviousPosture = current) and keeps the
    // current one entered. On a pop (Sim.PopPosture, or any set to current.PreviousPosture) that makes the two postures point at
    // each other and drops whatever was below [verified, decompiled]; every later Posture.Satisfies on that Sim recurses until
    // the interpreter's stack limit. Compatible is true on a pop when the posture being left satisfies a carrying condition
    // through its chain (SittingInVehicle / SittingInBoat / SittingPosture / Socializing / InRabbitHole ... over a carry posture).
    // With the switch on, such a pop takes the setter's other branch, the unwind, for its one step: the posture being left is
    // removed from its interaction map and exited, then the target (already in the chain, so its interactions stay) becomes
    // current, PostureChanged fires under the setter's own rule and the idle logic is queued. Every other set (pushes, non-pops,
    // pops whose target is not Compatible) runs the game's setter unchanged. Credit: design by the issue #89 reporter.
    // Counter [372]: pops sent down the unwind. Cost on every Posture set: one interpreter frame and three compares.
    public static partial class Fixes
    {
        static FieldInfo fPosPosture, fPosIdling, fPosChanged;

        static void ApplyPosturePop()
        {
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.NonPublic;
            fPosPosture = typeof(Sim).GetField("mPosture", inst);
            fPosIdling = typeof(Sim).GetField("mIsAlreadyIdling", inst);
            fPosChanged = typeof(Sim).GetField("PostureChanged", inst);  // field-like event: the backing field has the event's name
            if (fPosPosture == null || fPosIdling == null || fPosChanged == null) { AppliedLog().Add("PosturePop: private Sim members not found, skipped"); return; }
            Patch(typeof(Sim), "set_Posture", new Type[] { typeof(Posture) }, typeof(PosturePopHook), "SetHook", "SetOrig");
        }

        public class PosturePopHook
        {
            public void SetHook(Posture value)
            {
                Sim s = (Sim)(object)this;
                Posture cur = s.Posture;
                if (sBgo.N != 0) BgoNote(s, cur, value);  // boatgetoutdiag (BoatGetOutDiag.cs): one compare while no GetOut is watched
                if (!UnseenFallbackCore.IsPop(cur, cur != null ? cur.PreviousPosture : null, value) || !ShortcutOn("posturepopcycle") || !value.Compatible(cur))
                {
                    SetOrig(value);
                    return;
                }
                PatchCounters()[372]++;
                if (cur.CommodityInteractionMap != null) cur.CommodityInteractionMap.RemovePosture(cur);
                cur.OnExitPosture();
                bool named = (cur.Name != string.Empty && !cur.Implicit) || (value.Name != string.Empty && !value.Implicit);
                fPosPosture.SetValue(s, value);
                fPosIdling.SetValue(s, false);
                Sim.PostureChangedCallback cb = named ? fPosChanged.GetValue(s) as Sim.PostureChangedCallback : null;
                if (cb != null) cb();
                s.QueueIdleLogic();
            }
            public void SetOrig(Posture value) { } // receives Sim.set_Posture's original header
        }
    }
}
