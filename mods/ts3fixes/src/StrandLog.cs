using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Situations;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Strand lines (docs/183 §8; logging only, part of routefaillog, no switch and no counter of its own). In four Isla
    // Paradiso storms (s229, s241, s265, s321) the active household could not plan from the third floor of its home to the
    // floors below: NoPathToDestination, blocker None, for hours, while the way up worked. The existing lines do not say
    // which answer closed the stair. For a failed plan of an active-household Sim to another level of the lot it stands
    // on, this logs what the planner's inputs were at that moment:
    //   "routefail strand:" the Sim's cached level / room against the live lot location of its position, the goal's, its
    //       current and transition interaction with the autonomous flag and priority, the rooms connected to its room;
    //   "routefail strand privacy:" each privacy situation on the lot (EA's only script lock for a stair: portals into the
    //       room its Sim stands in, PrivacySituation.OnPortalPathPlan), where that Sim is, and whether the rule locks
    //       this Sim out;
    //   "routefail strand link:" each object with a portal component whose lane ends lie on two levels of the way (found by
    //       the component, any class: Stairs, StaircaseSpiral, Ladder, FirefighterPole, elevators): both ends with level and
    //       room, per lane EA's slot test at both ends without and with Sims, what stands within 0.75 m of each end; then
    //       "routefail strand links:" always: how many on the way, and every level link on the lot by class and levels;
    //   "routefail strand rooms:" (a Sim's first line) the chain of rooms from its room to the goal's and back, walked over
    //       World.GetConnectedRooms (EA has no "is there a path" call), and World.ArePointsConnectedByLand for the two points.
    // ts3native's job text on the "routefail:" line carries the other half: the portal asks of that job and the first
    // locked answer (plandeadline.cpp RfText). Per Sim: the first 3 such fails, then every 25th; 240 fails a session.
    // The link scan and the slot test live in LevelLink.cs, shared with ladderfootshoo (LadderFoot.cs, docs/183 §9), the fix for
    // the answer these lines gave.
    // Off path: one bool test per failed plan. All reads; the slot tests are EA's own read-only ICs (Route.IsSlotUnobstructed).
    public static partial class Fixes
    {
        static readonly Dictionary<ulong, int> sStrandFails = new Dictionary<ulong, int>();  // by SimDescriptionId
        static int sStrandLines;
        static FieldInfo sStrandHandlers;
        static bool sStrandLooked;

        // RouteFailLog.RflPlan, every failed plan of a Sim
        static void StrandPlan(ScriptCore.Route r, Sim sim, int kind, RoutePlanResult res)
        {
            try
            {
                bool noPath = res.mType == RoutePlanResultType.FailedBecauseNoPathToDestination || res.mType == RoutePlanResultType.FailedBecauseNoDestinationSpecified;
                if (!noPath || !sim.IsInActiveHousehold || sStrandLines >= StrandCore.kMaxLines) return;
                Lot lot = sim.LotCurrent;
                if (lot == null || lot.IsWorldLot) return;
                Vector3 goal = r.GetApproximateOriginalDestination(), at = sim.Position;
                LotLocation gl = LotLocation.Invalid, sl = LotLocation.Invalid;
                ulong goalLot = World.GetLotLocation(goal, ref gl, eRoomDefinition.Default);
                ulong simLot = World.GetLotLocation(at, ref sl, eRoomDefinition.Default);
                if (!StrandCore.Wants(ShortcutOn("routefaillog"), true, noPath, true, goalLot == lot.LotId, sim.Level, gl.mLevel)) return;
                ulong id = sim.SimDescription.SimDescriptionId;
                int n;
                sStrandFails.TryGetValue(id, out n);
                if (sStrandFails.Count > 256) sStrandFails.Clear();
                sStrandFails[id] = ++n;
                if (!StrandCore.Due(n)) return;
                sStrandLines++;

                InteractionInstance cur = sim.InteractionQueue != null ? sim.InteractionQueue.GetCurrentInteraction() : null;
                InteractionInstance tr = sim.InteractionQueue != null ? sim.InteractionQueue.TransitionInteraction : null;
                InteractionInstance ruled = cur != null ? cur : tr;  // the one EA's privacy rule reads
                StringBuilder b = new StringBuilder("routefail strand: ");
                b.Append(sim.FullName).Append(" #").Append(n).Append(", ").Append(kRflKind[kind & 3]).Append(' ').Append(res.mType)
                    .Append(", at ").Append(V(at)).Append(" cached level ").Append(sim.Level).Append(" room ").Append(sim.RoomId)
                    .Append(", live level ").Append(sl.mLevel).Append(" room ").Append(sl.mRoom).Append(simLot == lot.LotId ? "" : " OFF THE LOT")
                    .Append(", goal ").Append(V(goal)).Append(" level ").Append(gl.mLevel).Append(" room ").Append(gl.mRoom)
                    .Append(", doing ").Append(StrandIa(cur)).Append(", transition ").Append(StrandIa(tr))
                    .Append(", posture ").Append(sim.Posture != null ? sim.Posture.GetType().Name : "none")
                    .Append(", lot privacy ").Append(lot.HasPrivacySituation ? "yes" : "no");
                try
                {
                    int[] rooms = World.GetConnectedRooms(lot.LotId, sim.RoomId, sim.Level);
                    b.Append(", rooms connected to ").Append(sim.RoomId).Append(':');
                    if (rooms == null || rooms.Length == 0) b.Append(" none");
                    else for (int i = 0; i < rooms.Length && i < 12; i++) b.Append(' ').Append(rooms[i]);
                }
                catch (Exception) { b.Append(", connected rooms threw"); }
                DiagLine(b.ToString());
                StrandPrivacy(sim, lot, ruled);
                StrandLinks(sim, lot, sim.Level, gl.mLevel);
                if (n == 1) StrandRooms(sim, lot, sim.RoomId, sim.Level, gl.mRoom, gl.mLevel, at, goal);
            }
            catch (Exception) { }
        }

        static string StrandIa(InteractionInstance ia)
        {
            if (ia == null) return "none";
            string s = ia.GetType().Name + (ia.Autonomous ? " auto" : " pushed");
            try { s += " prio " + ia.GetPriority().Level; } catch (Exception) { }
            return s;
        }

        // every PrivacySituation subscribed to the lot's portal event (how EA itself finds them, Lot.GetRoomPrivacySituations)
        static void StrandPrivacy(Sim sim, Lot lot, InteractionInstance ruled)
        {
            try
            {
                if (!sStrandLooked)
                {
                    sStrandLooked = true;
                    sStrandHandlers = typeof(Lot).GetField("mOnPortalPathPlanHandler", BindingFlags.NonPublic | BindingFlags.Instance);
                }
                Delegate d = sStrandHandlers != null ? sStrandHandlers.GetValue(lot) as Delegate : null;
                if (d == null) { if (sStrandHandlers == null) DiagLine("routefail strand privacy: handler list not readable"); return; }
                int shown = 0;
                foreach (Delegate h in d.GetInvocationList())
                {
                    PrivacySituation p = h.Target as PrivacySituation;
                    if (p == null) continue;
                    if (shown++ >= StrandCore.kMaxPrivacy) break;
                    Sim o = p.SyncTarget;
                    StringBuilder b = new StringBuilder("routefail strand privacy: for ").Append(sim.FullName).Append(": ").Append(p.GetType().Name);
                    if (o == null) { b.Append(", no Sim, start room ").Append(p.RoomId); DiagLine(b.ToString()); continue; }
                    bool canIgnore = false, locks = false;
                    try
                    {
                        canIgnore = p.OtherSimCanIgnorePrivacy(sim);
                        locks = StrandCore.PrivacyLocks(ruled != null, ruled != null && ruled.Autonomous, ruled != null ? (int)ruled.GetPriority().Level : 0, canIgnore);
                    }
                    catch (Exception) { b.Append(" (rule threw)"); }
                    LotLocation ol = LotLocation.Invalid;
                    World.GetLotLocation(o.Position, ref ol, eRoomDefinition.Default);
                    b.Append(" of ").Append(o.FullName).Append(o.HasBeenDestroyed ? " destroyed" : "").Append(o == sim ? " (this Sim)" : "")
                        .Append(", start room ").Append(p.RoomId).Append(", its Sim at ").Append(V(o.Position)).Append(" cached level ").Append(o.Level)
                        .Append(" room ").Append(o.RoomId).Append(", live level ").Append(ol.mLevel).Append(" room ").Append(ol.mRoom)
                        .Append(o.LotCurrent == lot ? "" : " on another lot")
                        .Append(", doing ").Append(StrandIa(o.CurrentInteraction))
                        .Append(", in its situation ").Append(p.SimIsInPrivacySituation(sim) ? "yes" : "no")
                        .Append(", may ignore ").Append(canIgnore ? "yes" : "no")
                        .Append(", portals into room ").Append(o.RoomId).Append(locks ? " LOCKED for this plan" : " open for this plan");
                    DiagLine(b.ToString());
                }
                if (shown == 0 && lot.HasPrivacySituation) DiagLine("routefail strand privacy: the lot counts a privacy situation, none is subscribed");
            }
            catch (Exception) { }
        }

        // Every level link of the lot (LevelLink.cs: found by its portal component, not by its class: Colonial Casa's link from
        // level 1 to level 2 is not a Stairs, s372), those on the way in full. A summary line always, also with 0 found.
        static void StrandLinks(Sim sim, Lot lot, int simLevel, int goalLevel)
        {
            try
            {
                int shown = 0, onWay = 0, portals;
                Dictionary<string, int> census = new Dictionary<string, int>();  // "Type a>b" of every level link on the lot
                foreach (LevelLink k in LevelLinks(lot, out portals))
                {
                    GameObject o = k.Obj;
                    string key = k.Type + " " + k.Al.mLevel + ">" + k.Zl.mLevel;
                    int c;
                    census.TryGetValue(key, out c);
                    census[key] = c + 1;
                    if (!LevelLinkOnWay(k, simLevel, goalLevel)) continue;
                    onWay++;
                    if (shown >= StrandCore.kMaxStairs) continue;
                    shown++;
                    Stairs st = o as Stairs;
                    StringBuilder b = new StringBuilder("routefail strand link: for ").Append(sim.FullName).Append(": ").Append(k.Type)
                        .Append(" 0x").Append(o.ObjectId.Value.ToString("X")).Append(st != null && st.IsHidden ? " HIDDEN" : "").Append(o.InUse ? " in use" : "")
                        .Append(", at ").Append(V(o.Position)).Append(" level ").Append(o.Level).Append(" room ").Append(o.RoomId)
                        .Append(", end A ").Append(V(k.A)).Append(" level ").Append(k.Al.mLevel).Append(" room ").Append(k.Al.mRoom)
                        .Append(", end B ").Append(V(k.Z)).Append(" level ").Append(k.Zl.mLevel).Append(" room ").Append(k.Zl.mRoom)
                        .Append(", lanes ").Append(k.Lanes);
                    for (uint i = 0; k.Ends && i < k.Lanes && i < 3; i++)
                    {
                        Slot[] s = LevelLinkLane(k, i);
                        if (s == null) { b.Append(" [").Append(i).Append(": no slots]"); continue; }
                        b.Append(" [").Append(i).Append(": A ").Append(StrandSlot(sim, o, s[0], false)).Append(", B ").Append(StrandSlot(sim, o, s[1], true)).Append(']');
                    }
                    b.Append(", near A:").Append(StrandNear(o, sim, k.A)).Append(", near B:").Append(StrandNear(o, sim, k.Z));
                    DiagLine(b.ToString());
                }
                StringBuilder t = new StringBuilder("routefail strand links: for ").Append(sim.FullName).Append(": ").Append(onWay).Append(" between level ")
                    .Append(simLevel).Append(" and ").Append(goalLevel).Append(onWay > shown ? " (" + (onWay - shown) + " not shown)" : "")
                    .Append(", portal objects on the lot ").Append(portals).Append(", level links on the lot:");
                if (census.Count == 0) t.Append(" none");
                int n = 0;
                foreach (KeyValuePair<string, int> e in census)
                {
                    if (n++ >= 10) { t.Append(" ..."); break; }
                    t.Append(' ').Append(e.Key).Append(e.Value > 1 ? " x" + e.Value : "").Append(';');
                }
                DiagLine(t.ToString());
            }
            catch (Exception x) { DiagLine("routefail strand links: for " + sim.FullName + ": threw " + x.GetType().Name); }
        }

        // The room graph as EA's own API gives it (World.GetConnectedRooms; no EA call answers "is there a path", so this walks
        // it): the chain of rooms from the Sim's room to the goal's and back, or that none exists. First strand line of a Sim.
        static void StrandRooms(Sim sim, Lot lot, int simRoom, int simLevel, int goalRoom, int goalLevel, Vector3 at, Vector3 goal)
        {
            try
            {
                ulong id = lot.LotId;
                int lo = Math.Min(simLevel, goalLevel), hi = Math.Max(simLevel, goalLevel);
                StrandCore.Neighbours next = delegate(int room)
                {
                    List<int> all = new List<int>();
                    for (int lv = lo; lv <= hi; lv++)
                    {
                        int[] n = null;
                        try { n = World.GetConnectedRooms(id, room, lv); } catch (Exception) { }
                        if (n != null) foreach (int x in n) if (!all.Contains(x)) all.Add(x);
                    }
                    return all.ToArray();
                };
                int v1, v2;
                string down = StrandCore.RoomPath(simRoom, goalRoom, StrandCore.kMaxRooms, next, out v1);
                string back = StrandCore.RoomPath(goalRoom, simRoom, StrandCore.kMaxRooms, next, out v2);
                string land = "threw";
                try { land = World.ArePointsConnectedByLand(at, goal) ? "yes" : "no"; } catch (Exception) { }
                DiagLine("routefail strand rooms: for " + sim.FullName + ": room graph " + simRoom + " to " + goalRoom + ": " + (down ?? "NO PATH (" + v1 + " rooms walked)")
                    + ", back " + goalRoom + " to " + simRoom + ": " + (back ?? "NO PATH (" + v2 + " rooms walked)") + ", points connected by land: " + land);
            }
            catch (Exception) { }
        }

        // EA's slot test as StairsPortalComponent.IsLaneUnobstructed runs it (Sims not counted), then with Sims counted
        static string StrandSlot(Sim sim, GameObject st, Slot slot, bool exit)
        {
            int a = LevelLinkSlot(sim, st, slot, false, exit), s = LevelLinkSlot(sim, st, slot, true, exit);
            if (a < 0 || s < 0) return "threw";
            return StrandCore.Blocked(a) + (s != a ? " (with Sims: " + StrandCore.Blocked(s) + ")" : "");
        }

        static string StrandNear(GameObject st, Sim sim, Vector3 p)
        {
            try
            {
                GameObject[] near = Sims3.Gameplay.Queries.GetObjects<GameObject>(p, 0.75f);
                StringBuilder b = new StringBuilder();
                int n = 0;
                foreach (GameObject o in near)
                {
                    if (o == null || o == st || o == sim) continue;
                    if (Math.Abs(o.Position.y - p.y) > 1.5f) continue;  // another floor
                    if (n++ >= StrandCore.kMaxNear) continue;
                    Sim s = o as Sim;
                    b.Append(' ').Append(s != null ? s.FullName : o.GetType().Name).Append('@').Append(V(o.Position));
                }
                if (n == 0) return " nothing";
                if (n > StrandCore.kMaxNear) b.Append(" +").Append(n - StrandCore.kMaxNear);
                return b.ToString();
            }
            catch (Exception) { return " threw"; }
        }
    }
}
