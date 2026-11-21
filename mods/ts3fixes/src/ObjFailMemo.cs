using System;
using System.Runtime.CompilerServices;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Pools;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // objfailmemo (tuning kObjFailMemo, default off; docs/650). poolfailmemo (docs/645 §2) for any object: NPC autonomy
    // keeps scoring an object the NPC cannot route to, so the same NPC re-picks it all day (OFF logs, phase-20261111..19:
    // Isla Paradiso ServingContainerGroup_GrabAServing on a Plate 1459 NoPath plans, Bridgeport GetInPool).
    // Record: RouteFailLog.RflPlan calls OfmPlan for each failed plan of a Sim follower (after the routefailprobe filter).
    // When the result is NoPath (16), NoDestinationSpecified (10) or DestinationPortalLocked (18) and the Sim's current
    // interaction targets a plain object, the (Sim, object) pair counts one failure per interaction instance (replans of
    // the same attempt count once). kObjFailMemoFails failures within kObjFailMemoWindowHours start a block of
    // kObjFailMemoHours sim-hours. Refuse: the autonomy candidate hook (AutRoomMemo.cs ArmPairHook on
    // Autonomy.CalculateScoreAndAddToCandidates, always patched) drops the pair from the scoring only while the search type
    // is Autonomy; pie menus, posture transitions, buff solves, social searches and pushed interactions never pass here,
    // so user-directed and pushed actions run EA's code. This is a per-Sim memo, unlike EA's per-object UpdateBlockTime
    // that autonomyinuseblock uses (that one would also steer household Sims).
    // Left to others (counted [4125], never recorded): Sim targets (social routing), Terrain (MopPuddle is mopunreachable's;
    // GoHere, puddles), Lot (GoHome / visits; the trip shortcuts own them), GetInPool (poolfailmemo's, unchanged),
    // DisposeOutside / EmptyTrash (trashoutsidefallback's, docs/646 §2).
    // Household, selected and unknown-scope Sims (HouseholdExempt or IsPlayerSim) are never recorded and never refused.
    // Rule: ObjFailMemoCore. Counters [4120] failures counted, [4121] pairs blocked, [4122] autonomous candidates refused,
    // [4123] exempt, [4124] errors, [4125] failures left to other switches. Log at each block, capped at 10 a minute.
    public static partial class Fixes
    {
        static readonly ObjFailMemoCore sOfm = new ObjFailMemoCore();
        static int sOfmMinute = -1, sOfmLogged;

        static void OfmLog(string s)
        {
            int minute = System.Environment.TickCount / 60000;
            if (minute != sOfmMinute) { sOfmMinute = minute; sOfmLogged = 0; }
            if (sOfmLogged++ < 10) DiagLine("objfailmemo: " + s);
        }

        static bool OfmExempt(Sim s) { return HouseholdExempt(s) || IsPlayerSim(s); }

        // objects another switch owns, or that hold far more than one interaction (Terrain, Lot)
        static bool OfmOwnedElsewhere(InteractionInstance ia, IGameObject t)
        {
            if (t is Sim || t is Terrain || t is Lot || ia is GetInPool) return true;
            string n = ia.GetType().Name;  // failed plans only, not a hot path
            return n == "DisposeOutside" || n == "EmptyTrash";  // trashoutsidefallback's (docs/646 §2)
        }

        static float OfmHours(float v, float dflt) { return v > 0f && v <= 1000f ? v : dflt; }

        // from RflPlan (RouteFailLog.cs), failed plans of a Sim follower only
        static void OfmPlan(Sim sim, RoutePlanResult res)
        {
            if (!ObjFailMemoCore.Fails((int)res.mType)) return;
            if (!ShortcutOn("objfailmemo")) { if (sOfm.Count != 0) sOfm.Clear(); return; }
            int[] c = PatchCounters();
            try
            {
                InteractionInstance ia = sim.CurrentInteraction;
                IGameObject t = ia != null ? ia.Target : null;
                if (t == null || t.ObjectId.Value == 0) return;
                if (OfmOwnedElsewhere(ia, t)) { c[4125]++; return; }
                float hours = OfmHours(Instantiator.kObjFailMemoHours, 1f);
                long window = SimClock.ConvertToTicks(OfmHours(Instantiator.kObjFailMemoWindowHours, 2f), TimeUnit.Hours);
                long block = SimClock.ConvertToTicks(hours, TimeUnit.Hours);
                int k = Instantiator.kObjFailMemoFails >= 1 && Instantiator.kObjFailMemoFails <= 100 ? Instantiator.kObjFailMemoFails : 2;
                int fails;
                int r = sOfm.Record(sim.ObjectId.Value, t.ObjectId.Value, OfmExempt(sim), (int)res.mType, RuntimeHelpers.GetHashCode(ia),
                    SimClock.CurrentTicks, window, block, k, out fails);
                if (r == ObjFailMemoCore.kExempt) { c[4123]++; return; }
                if (r == ObjFailMemoCore.kCounted) { c[4120]++; return; }
                if (r != ObjFailMemoCore.kBlocked) return;
                c[4120]++;
                c[4121]++;
                OfmLog(sim.FullName + " " + ia.GetType().Name + " on " + t.GetType().Name + " failed " + fails + " times (" + res.mType
                    + "), skipped for " + hours + " sim-h");
            }
            catch (Exception ex) { c[4124]++; OfmLog("error " + ex.GetType().Name); }
        }

        // from ArmPairHook (AutRoomMemo.cs) before EA scores the pair; true = leave the pair out of this autonomy search.
        // Cheap when nothing is blocked (one int read).
        static bool OfmRefuse(Autonomy a, InteractionObjectPair iop)
        {
            if (sOfm.BlockedCount == 0) return false;
            int[] c = PatchCounters();
            try
            {
                if (a == null || iop == null || a.CurrentSearchType != AutonomySearchType.Autonomy) return false;
                Sim sim = a.Actor;
                IGameObject t = iop.Target;
                if (sim == null || t == null) return false;
                long now = SimClock.CurrentTicks;
                ulong sid = sim.ObjectId.Value, oid = t.ObjectId.Value;
                // first ask without the exempt check (dictionary only); the Sim's scope is read for a blocked pair only
                if (sOfm.Test(sid, oid, true, false, now) != ObjFailMemoCore.kRefuse) return false;
                if (!ShortcutOn("objfailmemo")) { sOfm.Clear(); return false; }
                if (OfmExempt(sim)) { c[4123]++; return false; }
                c[4122]++;
                return true;
            }
            catch (Exception ex) { c[4124]++; OfmLog("error " + ex.GetType().Name); return false; }
        }
    }
}
