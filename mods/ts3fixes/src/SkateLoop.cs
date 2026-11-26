using System;
using System.Collections.Generic;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Objects;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // skateloop (2026-11-26, switch off by default): SkatingRink.Skate.DoSkate loops "CreateIceSkatingPath, then DoRoute" while
    // the Sim skates, with no sleep of its own, and CreateIceSkatingPath forces the plan result to Succeeded. A DoRoute that
    // returns at once therefore spins the Sim's script thread: one natural Sunset Valley run (RC m1124d asi) froze the agent
    // heartbeat for 60 s with the skater's stack in NRaas Hybrid SkateEx.Run > Skate.DoSkate > CreateIceSkatingPath >
    // Route_InsertCustomPathAtIndex and the thread busy in every sample. Hybrid only wraps EA's DoSkate, so the guard is here.
    // Fix: the kSpinCalls-th CreateIceSkatingPath for the same Sim at the same sim tick (no time passed, so nothing yielded
    // since the last call) returns the route with a failed plan result; EA's loop then ends (flag = false) and the Sim leaves
    // the rink as after any failed plan. Normal skating builds one path per DoRoute, seconds of sim time apart.
    // Counters: [4003] spins ended, [4004] spins seen with the switch off (once per spin), [4005] errors.
    public static partial class Fixes
    {
        const int kSkSpinCalls = 20;
        static readonly Dictionary<ulong, long[]> sSkLast = new Dictionary<ulong, long[]>();  // Sim object id -> {sim tick, calls at it}

        static void ApplySkateLoop()
        {
            Patch(typeof(GameObject), "CreateIceSkatingPath", new Type[] { typeof(List<Vector3>), typeof(ISkatableObject), typeof(bool) },
                typeof(SkLoopHook), "PathHook", "PathOrig");
        }

        // calls for this Sim at the current sim tick, this one included
        static int SkCalls(ulong id, long tick)
        {
            if (sSkLast.Count > 512) sSkLast.Clear();
            long[] e;
            if (!sSkLast.TryGetValue(id, out e)) { e = new long[2]; sSkLast[id] = e; }
            if (e[0] != tick) { e[0] = tick; e[1] = 0; }
            return (int)++e[1];
        }

        public class SkLoopHook
        {
            public Route PathHook(List<Vector3> targetPoints, ISkatableObject skatingObject, bool looping)
            {
                Route r = PathOrig(targetPoints, skatingObject, looping);
                try
                {
                    GameObject actor = (GameObject)(object)this;
                    int n = SkCalls(actor.ObjectId.Value, SimClock.CurrentTicks);
                    if (n < kSkSpinCalls || r == null) return r;
                    if (!ShortcutOn("skateloop")) { if (n == kSkSpinCalls) PatchCounters()[4004]++; return r; }
                    RoutePlanResult pr = r.PlanResult;
                    pr.mType = RoutePlanResultType.FailedBecausePlannerAborted;
                    r.PlanResult = pr;
                    PatchCounters()[4003]++;
                }
                catch (Exception) { PatchCounters()[4005]++; }
                return r;
            }
            public Route PathOrig(List<Vector3> targetPoints, ISkatableObject skatingObject, bool looping) { return null; } // receives GameObject.CreateIceSkatingPath's original header
        }
    }
}
