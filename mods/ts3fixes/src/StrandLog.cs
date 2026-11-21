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
using Sims3.SimIFace.CAS;

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
    //   "routefail strand stairs:" each stair between the two levels: rooms and positions of both ends, hidden flag, and
    //       per lane EA's slot test at both ends without and with Sims, plus what stands within 0.75 m of each end.
    // ts3native's job text on the "routefail:" line carries the other half: the portal asks of that job and the first
    // locked answer (plandeadline.cpp RfText). Per Sim: the first 3 such fails, then every 25th; 240 fails a session.
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
                StrandStairs(sim, lot, sim.Level, gl.mLevel);
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

        static void StrandStairs(Sim sim, Lot lot, int simLevel, int goalLevel)
        {
            try
            {
                Stairs[] all = lot.GetObjects<Stairs>();
                int shown = 0, onWay = 0;
                foreach (Stairs st in all)
                {
                    Vector3 bot = st.GetSlotPosition(Stairs.RoutingSlots.Stairs_Bottom0), top = st.GetSlotPosition(Stairs.RoutingSlots.Stairs_Top0);
                    LotLocation bl = LotLocation.Invalid, tl = LotLocation.Invalid;
                    World.GetLotLocation(bot, ref bl, eRoomDefinition.Default);
                    World.GetLotLocation(top, ref tl, eRoomDefinition.Default);
                    if (!StrandCore.StairOnWay(bl.mLevel, tl.mLevel, simLevel, goalLevel)) continue;
                    onWay++;
                    if (shown++ >= StrandCore.kMaxStairs) continue;
                    int[] rc = st.GetRoomConnections();
                    StringBuilder b = new StringBuilder("routefail strand stairs: for ").Append(sim.FullName).Append(": ").Append(st.GetType().Name)
                        .Append(" 0x").Append(st.ObjectId.Value.ToString("X")).Append(st.IsHidden ? " HIDDEN" : "").Append(st.InUse ? " in use" : "")
                        .Append(", bottom ").Append(V(bot)).Append(" level ").Append(bl.mLevel).Append(" room ").Append(rc != null && rc.Length > 0 ? rc[0] : -1)
                        .Append(", top ").Append(V(top)).Append(" level ").Append(tl.mLevel).Append(" room ").Append(rc != null && rc.Length > 1 ? rc[1] : -1);
                    Stairs.StairsPortalComponent pc = st.StairsPortal;
                    uint lanes = pc != null ? pc.GetNumLanes() : 0;
                    b.Append(", lanes ").Append(lanes);
                    for (uint i = 0; i < lanes && i < 3; i++)
                    {
                        Slot[] s = pc.GetSlotsForLaneIndex(i);
                        b.Append(" [").Append(i).Append(": bottom ").Append(StrandSlot(sim, st, s[0], false)).Append(", top ").Append(StrandSlot(sim, st, s[1], true)).Append(']');
                    }
                    b.Append(", near bottom:").Append(StrandNear(st, sim, bot)).Append(", near top:").Append(StrandNear(st, sim, top));
                    DiagLine(b.ToString());
                }
                if (onWay == 0) DiagLine("routefail strand stairs: for " + sim.FullName + ": no stair between level " + simLevel + " and " + goalLevel + " (" + all.Length + " on the lot)");
                else if (onWay > shown) DiagLine("routefail strand stairs: for " + sim.FullName + ": " + (onWay - StrandCore.kMaxStairs) + " more not shown");
            }
            catch (Exception) { }
        }

        // EA's slot test as StairsPortalComponent.IsLaneUnobstructed runs it (Sims not counted), then with Sims counted
        static string StrandSlot(Sim sim, Stairs st, Slot slot, bool exit)
        {
            try
            {
                Route.RouteOption opt = Route.RouteOption.PlatformsNotBlocking;
                CASAgeGenderFlags ags = sim.SimDescription.AgeGenderSpecies;
                BlockedStatus a = Route.IsSlotUnobstructed(sim.ObjectId, st.ObjectId, slot, false, opt, sim.ObjectId, ags, exit);
                BlockedStatus s = Route.IsSlotUnobstructed(sim.ObjectId, st.ObjectId, slot, true, opt, sim.ObjectId, ags, exit);
                return StrandCore.Blocked((int)a) + (s != a ? " (with Sims: " + StrandCore.Blocked((int)s) + ")" : "");
            }
            catch (Exception) { return "threw"; }
        }

        static string StrandNear(Stairs st, Sim sim, Vector3 p)
        {
            try
            {
                GameObject[] near = Sims3.Gameplay.Queries.GetObjects<GameObject>(p, 0.75f);
                StringBuilder b = new StringBuilder();
                int n = 0;
                foreach (GameObject o in near)
                {
                    if (o == null || o == st || o == sim || o is Stairs) continue;
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
