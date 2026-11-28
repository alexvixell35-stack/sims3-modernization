using System;
using System.Collections.Generic;
using System.Text;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects.Miscellaneous;
using Sims3.Gameplay.Pools;
using Sims3.Gameplay.Situations;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // routecauselog (tuning kRouteCauseLog, off by default; logging only; docs/580). The log census (docs/579, cleanup
    // branch) lists route failures whose cause the existing lines cannot show:
    //   - NPC jobs that fail without a single plan node: DisposeOutside -> TrashPile (NoPath), GetInPool -> Pool and
    //     PlaceWelcomeKit -> Sim (NoDestinationSpecified), RouteAway -> Terrain (NoPath). ts3native's job line has no goal.
    //   - DestinationPortalLocked on household toilets / plates: which door was locked, and which privacy situation
    //     (owner, room, owner's interaction) locked it.
    // For each failed plan of those kinds this logs the goal, the Sim's spot, the interaction target, and per kind:
    // the pool's inside routing-slot count and fountain flag; the lot's outdoor trash cans and the carried object;
    // RouteAway's distances and room limit; the locked doors (SimRoutingComponent.LockedDoorsDuringPlan, rooms) and
    // the privacy situations of the goal room. Hook point: RouteFailLog.RflPlan (every plan) calls RclPlan for failed
    // plans; with the switch off that is one switch test. At most 3 lines per Sim, interaction and result, 400 per
    // session. Counters: [2709] lines, [2710] dropped by the caps.
    public static partial class Fixes
    {
        const int kRclMax = 400, kRclPerKey = 3;
        static readonly Dictionary<string, int> sRclKeys = new Dictionary<string, int>();
        static int sRclLines;
        static readonly string[] kRclNpcKinds = { "DisposeOutside", "GetInPool", "PlaceWelcomeKit", "RouteAway" };

        static void ApplyRouteCauseLog() { }  // no hook of its own: RouteFailLog.RflPlan calls RclPlan

        // Pure: does this failed plan get a line?
        internal static bool RclWants(bool on, bool npc, string ia, RoutePlanResultType res)
        {
            if (!on || res == RoutePlanResultType.Succeeded) return false;
            if (res == RoutePlanResultType.FailedBecauseDestinationPortalLocked) return true;
            if (!npc || ia == null) return false;
            return Array.IndexOf(kRclNpcKinds, ia) >= 0;
        }

        static void RclPlan(ScriptCore.Route r, Sim sim, int kind, RoutePlanResult res)
        {
            if (!ShortcutOn("routecauselog")) return;
            try
            {
                InteractionInstance ii = sim.CurrentInteraction;
                string ia = ii != null ? ii.GetType().Name : null;
                if (!RclWants(true, IsNpcSim(sim), ia, res.mType)) return;
                int[] c = PatchCounters();
                string key = sim.SimDescription.SimDescriptionId + "|" + ia + "|" + res.mType;
                int n;
                sRclKeys.TryGetValue(key, out n);
                if (n >= kRclPerKey || sRclLines >= kRclMax) { c[2710]++; return; }
                if (sRclKeys.Count > 2000) sRclKeys.Clear();
                sRclKeys[key] = n + 1;
                sRclLines++; c[2709]++;
                Vector3 goal = r.GetApproximateOriginalDestination();
                StringBuilder b = new StringBuilder("routecause: ");
                b.Append(sim.FullName).Append(IsNpcSim(sim) ? " npc" : " household").Append(", ").Append(ia ?? "none")
                    .Append(", ").Append(kRflKind[kind & 3]).Append(' ').Append(res.mType)
                    .Append(", goal ").Append(V(goal)).Append(", at ").Append(V(sim.Position)).Append(" level ").Append(sim.Level)
                    .Append(" room ").Append(sim.RoomId).Append(", lot ").Append(RclLot(sim.LotCurrent));
                IGameObject t = ii != null ? ii.Target : null;
                if (t != null && !(t is Sim && t == sim))
                    b.Append(", target ").Append(t.GetType().Name).Append(" at ").Append(V(t.Position)).Append(" level ").Append(t.Level)
                        .Append(" room ").Append(t.RoomId).Append(" lot ").Append(RclLot(t.LotCurrent)).Append(t.InUse ? " in use" : "");
                b.Append(RclDetail(sim, ii, t, res));
                DiagLine(b.ToString());
            }
            catch (Exception) { PatchCounters()[2710]++; }
        }

        static string RclLot(Lot l) { return l == null ? "none" : l.IsWorldLot ? "street" : "0x" + l.LotId.ToString("X"); }

        static string RclDetail(Sim sim, InteractionInstance ii, IGameObject t, RoutePlanResult res)
        {
            StringBuilder b = new StringBuilder();
            if (res.mType == RoutePlanResultType.FailedBecauseDestinationPortalLocked)
            {
                List<Door> locked = sim.SimRoutingComponent != null ? sim.SimRoutingComponent.LockedDoorsDuringPlan : null;
                b.Append(", locked doors ").Append(locked == null ? 0 : locked.Count);
                if (locked != null)
                    for (int i = 0; i < locked.Count && i < 4; i++)
                    {
                        int[] rc = locked[i].GetRoomConnections();
                        b.Append(" [").Append(V(locked[i].Position)).Append(" rooms ").Append(rc != null && rc.Length > 1 ? rc[0] + "/" + rc[1] : "?").Append(']');
                    }
                Lot gl = t != null ? t.LotCurrent : sim.LotCurrent;
                if (gl != null && t != null)
                {
                    List<PrivacySituation> ps = gl.GetRoomPrivacySituations(t.RoomId);
                    b.Append(", privacy in target room ").Append(ps == null ? 0 : ps.Count);
                    if (ps != null)
                        foreach (PrivacySituation p in ps)
                        {
                            Sim o = p.SyncTarget;
                            b.Append(" [").Append(p.GetType().Name).Append(' ').Append(o == null ? "no Sim" : o.FullName + (o.HasBeenDestroyed ? " destroyed" : "")
                                + " in " + (o != null && o.CurrentInteraction != null ? o.CurrentInteraction.GetType().Name : "nothing")).Append(" room ").Append(p.RoomId).Append(']');
                        }
                }
            }
            Pool pool = t as Pool;
            if (pool != null && pool.PoolPortalComponent != null)
            {
                Slot[] inside = pool.PoolPortalComponent.GetRoutingSlotsForGroup(PoolPortalComponent.RoutingSlotGroup.Inside);
                b.Append(", pool inside slots ").Append(inside == null ? -1 : inside.Length).Append(pool is IBuildableFountain ? " fountain" : "")
                    .Append(sim.IsHuman ? "" : " actor " + sim.SimDescription.Species);
            }
            if (t is TrashPile && sim.LotCurrent != null)
            {
                TrashcanOutside[] cans = sim.LotCurrent.GetObjects<TrashcanOutside>();
                b.Append(", outdoor cans on lot ").Append(cans.Length);
                for (int i = 0; i < cans.Length && i < 2; i++) b.Append(" [").Append(V(cans[i].Position)).Append(cans[i].InUse ? " in use" : "").Append(cans[i].IsTippedOver ? " tipped" : "").Append(']');
                GameObject held = sim.GetObjectInRightHand();
                b.Append(", holding ").Append(held != null ? held.GetType().Name : "nothing").Append(", outside ").Append(sim.IsOutside);
            }
            Terrain.RouteAway ra = ii as Terrain.RouteAway;
            if (ra != null) b.Append(", route away ").Append(ra.MinDistance.ToString("0.0")).Append('-').Append(ra.MaxDistance.ToString("0.0"))
                .Append(ra.LimitOutdoors ? " outdoors only" : "").Append(", outside ").Append(sim.IsOutside);
            if (RclIaName(ii) == "PlaceWelcomeKit") b.Append(", holding ").Append(sim.GetObjectInRightHand() != null ? sim.GetObjectInRightHand().GetType().Name : "nothing").Append(", outside ").Append(sim.IsOutside);
            return b.ToString();
        }

        static string RclIaName(InteractionInstance ii) { return ii != null ? ii.GetType().Name : ""; }
    }
}
