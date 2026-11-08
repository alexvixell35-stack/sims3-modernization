using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects.Decorations;
using Sims3.Gameplay.Objects.Electronics;
using Sims3.Gameplay.Socializing;
using Sims3.SimIFace;

// Repro dance-together-floor (docs/253 §8): DanceFloor.FindAndRouteOntoADanceFloorWithFollower threw "Collection was modified"
// for an NPC pair on Waylon's Haunt (Bridgeport). The action brings two NPCs to a lot with a dance floor and pushes EA's
// Stereo.DanceTogetherA between them, so BaseRun / ClubRun route the pair onto a floor (the loop docs/253 §2 reads).
//   dance_together_floor {op: "setup", lot?: "Waylon"}  picks two NPCs (young adult or older, human, a household that is not the
//        active one, not selectable, not the active Sim; a Sim with no household = unknown scope = refused) and, if they are not
//        on the lot yet, sends them with EA's VisitCommunityLot (player-directed, as sim_visit_lot autonomous false).
//   {op: "fire"}   checks the pair again (scope, on the lot), cancels a dance still running, sets the pair's short-term context
//        to Friendly when it is not one BaseRun accepts (test shortcut, labelled), queues DanceTogetherA (actor = first NPC,
//        target = second, the lot's dance object). Counts a try.
//   {op: "cancel"} cancels the pair's interactions (the dance after its route; NPCs only).
//   {op: "state"}  on_lot, tries, running (a DanceTogetherA in the actor's queue), returned (ts3fixes FindAndRoute returns +
//        throws since the last fire; -1 without the counters), settled (returned >= 1 or not running), stalled (fail-fast: the
//        pair's positions and current interactions and the dance counters unchanged for 60 s = two 30 s checks).
// Never touches a household Sim: every op re-checks the scope and throws on a household, selectable or unknown-scope Sim.
namespace Ts3Agent
{
    public static partial class Handlers
    {
        static void RegisterDanceFloor()
        {
            Dispatcher.Register("dance_together_floor", DanceTogetherFloor);
        }

        static ulong sDtA, sDtB;
        static Lot sDtLot;
        static IGameObject sDtObj;
        static int sDtTries, sDtBase = -1, sDtSigAt;
        static string sDtSig;

        static object DanceTogetherFloor(Args a)
        {
            RequireLive();
            string op = a.Str("op", "state");
            Dictionary<string, object> r = Util.Obj();
            if (op == "setup")
            {
                sDtLot = FindLotByName(a.Str("lot", "Waylon"));
                sDtObj = null;
                int floors = 0;
                foreach (GameObject o in sDtLot.GetObjects<GameObject>())
                {
                    if (o is DanceFloor) { floors++; continue; }
                    if (o is IDanceable && (sDtObj == null || (o is Stereo && !(sDtObj is Stereo)))) sDtObj = o;
                }
                if (floors == 0 || sDtObj == null) throw new McpException("dance_together_floor: " + sDtLot.Name + " has " + floors + " dance floors and " + (sDtObj != null ? "a" : "no") + " dance object");
                List<Sim> on = new List<Sim>(), off = new List<Sim>();
                foreach (SimDescription sd in SimDescription.GetSimDescriptionsInWorld())
                {
                    if (!Util.InWorldNow(sd) || DtScope(sd.CreatedSim) != null || sd.TeenOrBelow || !sd.IsHuman) continue;
                    Sim s = sd.CreatedSim;
                    if (s.Service != null || s.IsLeavingLot) continue;
                    (s.LotCurrent == sDtLot ? on : off).Add(s);
                }
                on.AddRange(off);
                if (on.Count < 2) throw new McpException("dance_together_floor: fewer than 2 NPCs in the world fit (" + on.Count + ")");
                sDtA = on[0].SimDescription.SimDescriptionId;
                sDtB = on[1].SimDescription.SimDescriptionId;
                sDtTries = 0;
                sDtBase = -1;
                List<object> sent = new List<object>();
                foreach (Sim s in new Sim[] { on[0], on[1] })
                {
                    if (s.LotCurrent == sDtLot) continue;
                    InteractionInstance i = VisitCommunityLot.Singleton.CreateInstance(sDtLot, s, new InteractionPriority(InteractionPriorityLevel.UserDirected), false, true);
                    sent.Add(s.FullName + (s.InteractionQueue.Add(i) ? "" : ": refused"));
                }
                r["sims"] = new List<object> { on[0].FullName, on[1].FullName };
                r["sent"] = sent;
                r["floors"] = floors;
                r["object"] = Util.ObjectName((GameObject)sDtObj);
            }
            else
            {
                Sim sa = DtSim(sDtA), sb = DtSim(sDtB);
                if (op == "fire")
                {
                    if (sa.LotCurrent != sDtLot || sb.LotCurrent != sDtLot) throw new McpException("dance_together_floor fire: the pair is not on " + sDtLot.Name);
                    DtCancel(sa, sb);
                    Relationship rel = Relationship.Get(sa, sb, true);
                    CommodityTypes stc = rel != null ? rel.STC.CurrentCommodity : CommodityTypes.Undefined;
                    if (rel != null && stc != CommodityTypes.Neutral && stc != CommodityTypes.Friendly && stc != CommodityTypes.Funny && stc != CommodityTypes.Amorous)
                        rel.STC.Set(sb, sa, CommodityTypes.Friendly, 50f);  // test shortcut: BaseRun / ClubRun route only with an accepted context
                    r["stc"] = stc.ToString();
                    InteractionDefinition def = new Stereo.DanceTogetherA.Definition((IDanceable)sDtObj);
                    InteractionInstance inst = def.CreateInstance(sb, sa, new InteractionPriority(InteractionPriorityLevel.UserDirected), false, true);
                    if (inst == null || !sa.InteractionQueue.Add(inst)) throw new McpException("dance_together_floor fire: DanceTogetherA refused for " + sa.FullName + " with " + sb.FullName);
                    sDtTries++;
                    sDtBase = DtReturned();
                    sDtSig = null;
                }
                else if (op == "cancel") DtCancel(sa, sb);
                else if (op != "state") throw new McpException("dance_together_floor: op setup|fire|cancel|state");
                bool running = DtRunning(sa);
                int ret = DtReturned(), returned = ret < 0 || sDtBase < 0 ? -1 : ret - sDtBase;
                r["on_lot"] = sa.LotCurrent == sDtLot && sb.LotCurrent == sDtLot;
                r["tries"] = sDtTries;
                r["running"] = running;
                r["busy"] = running || DtRunning(sb);
                r["returned"] = returned;
                r["settled"] = returned >= 1 || !running;
                string sig = DtPos(sa) + "|" + DtPos(sb) + "|" + DtCur(sa) + "|" + DtCur(sb) + "|" + ret;
                int now = System.Environment.TickCount;
                if (sig != sDtSig) { sDtSig = sig; sDtSigAt = now; }
                r["stalled"] = now - sDtSigAt >= 60000;
                r["sims"] = new List<object> { sa.FullName + " (" + DtCur(sa) + ")", sb.FullName + " (" + DtCur(sb) + ")" };
            }
            r["lot"] = sDtLot.Name;
            return r;
        }

        // null = the Sim may be used; else why not (household rule: active household, selectable, active Sim, no household = unknown)
        static string DtScope(Sim s)
        {
            if (s == null || s.SimDescription == null) return "no Sim";
            Household h = s.SimDescription.Household;
            if (h == null) return "unknown scope (no household)";
            if (h == Household.ActiveHousehold) return "active household";
            if (s.IsSelectable || s == Sim.ActiveActor) return "selectable";
            return null;
        }

        static Sim DtSim(ulong id)
        {
            if (sDtLot == null) throw new McpException("dance_together_floor: setup first");
            SimDescription sd = SimDescription.Find(id);
            if (sd == null || !Util.InWorldNow(sd)) throw new McpException("dance_together_floor: a picked NPC left the world");
            string why = DtScope(sd.CreatedSim);
            if (why != null) throw new McpException("dance_together_floor: refused, " + sd.FullName + " is " + why);
            return sd.CreatedSim;
        }

        static bool DtRunning(Sim s)
        {
            foreach (InteractionInstance i in s.InteractionQueue.InteractionList) if (i is Stereo.DanceTogetherA || i is SocialInteractionB) return true;
            return false;
        }

        static void DtCancel(Sim sa, Sim sb)
        {
            if (DtRunning(sa)) sa.InteractionQueue.CancelAllInteractions();
            if (DtRunning(sb)) sb.InteractionQueue.CancelAllInteractions();
        }

        // ts3fixes [893] returns + [894] throws of FindAndRouteOntoADanceFloorWithFollower (DanceFloorDiag.cs); -1 without them
        static int DtReturned()
        {
            int[] c = AppDomain.CurrentDomain.GetData("ts3fixes.counters") as int[];
            return c != null && c.Length > 897 ? c[893] + c[894] : -1;
        }

        static string DtPos(Sim s) { Vector3 p = s.Position; return Math.Round(p.x * 2) + "," + Math.Round(p.z * 2); }
        static string DtCur(Sim s) { InteractionInstance i = s.CurrentInteraction; return i != null ? i.GetType().Name : "none"; }
    }
}
