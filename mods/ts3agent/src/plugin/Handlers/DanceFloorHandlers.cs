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
// for NPCs on Waylon's Haunt (Bridgeport); hit 1 came after 5-6 NPCs had danced there for ~10 min, rotating (the floor used many
// times). The action pushes EA's Stereo.DanceTogetherA on NPC pairs, so BaseRun / ClubRun route each pair onto a floor (the loop
// docs/253 §2 reads), on up to 4 lots with a dance floor at once, each with its own NPC pool; the repro runs one pair, then
// rotating pairs, then a crowd (every pool pair at once). Every op below acts on all floors.
//   dance_together_floor {op: "setup", lots?: [name, ...], max?: 4, pool?: 6}  the lots (default: every community lot with a
//        DanceFloor and a dance object, the named / Waylon's Haunt first, at most max) and per floor a pool of NPCs (young adult or
//        older, human, a household that is not the active one, not selectable, not the active Sim; a Sim with no household =
//        unknown scope = refused; not a service Sim, not leaving a lot; NPCs on that lot first; never in two pools). A floor
//        without a full pool is dropped. The pools are picked:1..N in floor order; an NPC the game removes is re-picked by the
//        next lookup (Util.PickFilter). "found" lists every lot with a dance floor.
//   {op: "place", a?, b?, pool?: false, missing?: false}  the standard setup_sims (AGENTS.md Repro Sim setup; ReproHandlers.SetupSimsAt) per floor
//        for its picked a + b (1-based in the pool) or its whole pool: queues cancelled, Teleport Me Here onto the floor unless
//        there, motives maxed, moodlets cleared, autonomy off (NPCs on a club lot queue GoHome after a cancel; 2026-11-08); missing:
//        only the Sims not on their lot and not teleporting right now (the repro's until polls place with it, so a lost teleport is redone).
//        Before that, a pool NPC a teleport cannot move (DtStuck: group / follower situation, vehicle, no lot) is replaced by another
//        NPC that fits (DtSwapStuck; "swapped" lists them); setup never picks one.
//   {op: "fire", a?: 1, b?: 2, crowd?: false, phase?: ""}  per floor actor a, target b of its pool (crowd: pairs 1+2, 3+4, 5+6
//        at once); checks scope and the lot, cancels a dance still running, sets each pair's short-term context to Friendly when it
//        is not one BaseRun accepts (test shortcut, labelled), queues DanceTogetherA. One cycle per fire; one ts3native.log line per
//        pair: "dancefloor repro: cycle #n phase P lot L cam seen|far Dm actor X partner Y floor_sims K calls C" (C = ts3fixes [892]
//        calls before the fire: the next "dancefloor list: call #" lines (#C+1 on, their own "lot" field names the floor) and any
//        THROW / MISMATCH belong to this cycle). cam: the camera target within 80 m of the floor = seen [proxy of the
//        ts3fixes unseen rule, not the same test].
//   {op: "cancel"} cancels the fired Sims' dances; from the 4th call after a fire Sims still dancing get SetObjectToReset
//        (the repro polls cancel, not state, after a cancel step).
//   {op: "state", a?, b?, pool?: false}  over picked a + b of every floor, every pool, or (default) the Sims of the last fire:
//        on_lot, tries, cycle, running (a DanceTogetherA in a fired actor's queue), returned (ts3fixes FindAndRoute returns +
//        throws since the last fire, all floors; -1 without the counters), settled (returned >= fired pairs, none running, or 90 s after the fire: gave_up),
//        floors (lot, cam, floor_sims), stalled (fail-fast: positions, current interactions and the dance counters unchanged for
//        60 s = two 30 s checks; op state: never while a fired pair still dances inside its 90 s gave_up window, which bounds that wait).
// Never touches a household Sim: every op re-checks the scope and throws on a household, selectable or unknown-scope Sim.
namespace Ts3Agent
{
    public static partial class Handlers
    {
        static void RegisterDanceFloor()
        {
            Dispatcher.Register("dance_together_floor", DanceTogetherFloor);
        }

        class DtFloor
        {
            public Lot Lot;
            public IGameObject Obj;
            public GameObject Floor;
            public int First, Count;  // its pool = Util.Picked[First .. First + Count)
            public readonly List<int[]> Fired = new List<int[]>();  // pool indices (actor, target) of the last fire
            // through picked:N, so an NPC the game removed is re-picked (Util.PickFilter = DtFits) instead of failing the run
            public Sim Sim(int i) { return DtSim(Util.FindSimDescription(new Args(new Dictionary<string, object> { { "sim", "picked:" + (First + i + 1) } }), "sim").SimDescriptionId); }
        }

        static readonly List<DtFloor> sDtFloors = new List<DtFloor>();
        static readonly List<ulong> sDtDropped = new List<ulong>();  // NPCs DtSwapStuck took out of a pool: never picked again this setup
        static readonly Dictionary<ulong, int> sDtQueuedAt = new Dictionary<ulong, int>();  // System.Environment.TickCount when DtStalled first saw a Sim's teleport queued at the head
        static int sDtTries, sDtCycle, sDtBase = -1, sDtSigAt, sDtCancels, sDtFiredAt, sDtGaveUp;
        static string sDtSig;

        static object DanceTogetherFloor(Args a)
        {
            RequireLive();
            string op = a.Str("op", "state");
            Dictionary<string, object> r = Util.Obj();
            if (op == "setup")
            {
                List<DtFloor> found = new List<DtFloor>();
                foreach (Lot lot in LotManager.AllLots)
                {
                    if (lot == null || lot.IsWorldLot || !lot.IsCommunityLot) continue;  // clubs and venues only: no NPC pool dances in someone's home
                    DtFloor f = new DtFloor();
                    f.Lot = lot;
                    foreach (GameObject o in lot.GetObjects<GameObject>())
                    {
                        if (o is DanceFloor) { if (f.Floor == null) f.Floor = o; continue; }
                        if (o is IDanceable && (f.Obj == null || (o is Stereo && !(f.Obj is Stereo)))) f.Obj = o;
                    }
                    if (f.Floor != null && f.Obj != null) found.Add(f);
                }
                List<object> named = a.Has("lots") ? a.List("lots") : new List<object> { "Waylon" };
                List<DtFloor> order = new List<DtFloor>();
                foreach (object n in named)
                    foreach (DtFloor f in found)
                        if (!order.Contains(f) && f.Lot.Name.IndexOf(Convert.ToString(n), StringComparison.OrdinalIgnoreCase) >= 0) order.Add(f);
                if (a.Has("lots") && order.Count == 0) throw new McpException("dance_together_floor: none of the named lots has a dance floor and a dance object");
                if (!a.Has("lots")) foreach (DtFloor f in found) if (!order.Contains(f)) order.Add(f);
                int max = (int)a.Long("max", 4), want = (int)a.Long("pool", 6);
                if (want < 2) throw new McpException("dance_together_floor: pool must be >= 2");
                sDtFloors.Clear();
                sDtDropped.Clear();
                sDtQueuedAt.Clear();
                Util.Picked.Clear();
                Util.PickFilter = null;
                List<object> skipped = new List<object>();
                foreach (DtFloor f in order)
                {
                    if (sDtFloors.Count >= max) break;
                    List<Sim> on = new List<Sim>(), off = new List<Sim>();
                    foreach (SimDescription sd in SimDescription.GetSimDescriptionsInWorld())
                        if (Util.InWorldNow(sd) && DtFits(sd) && !Util.Picked.Contains(sd.SimDescriptionId)) (sd.CreatedSim.LotCurrent == f.Lot ? on : off).Add(sd.CreatedSim);
                    on.AddRange(off);
                    if (on.Count < want) { skipped.Add(f.Lot.Name + ": " + on.Count + " free NPCs"); continue; }
                    f.First = Util.Picked.Count;
                    f.Count = want;
                    for (int i = 0; i < want; i++) Util.Picked.Add(on[i].SimDescription.SimDescriptionId);
                    f.Fired.Add(new int[] { 0, 1 });
                    sDtFloors.Add(f);
                }
                if (sDtFloors.Count == 0) throw new McpException("dance_together_floor: no floor got a pool of " + want + " (" + string.Join("; ", Array.ConvertAll(skipped.ToArray(), Convert.ToString)) + ")");
                Util.PickFilter = DtFits;  // an NPC the game removed (2026-11-08: one left the world in phase B) is replaced at the next lookup
                sDtTries = sDtCycle = 0;
                sDtBase = -1;
                List<object> all = new List<object>(), floors = new List<object>();
                foreach (DtFloor f in found) all.Add(f.Lot.Name);
                foreach (DtFloor f in sDtFloors)
                {
                    Dictionary<string, object> d = Util.Obj();
                    List<object> names = new List<object>();
                    for (int i = 0; i < f.Count; i++) names.Add("picked:" + (f.First + i + 1) + " " + f.Sim(i).FullName);
                    d["lot"] = f.Lot.Name;
                    d["object"] = Util.ObjectName((GameObject)f.Obj);
                    d["cam"] = DtCam(f);
                    d["pool"] = names;
                    floors.Add(d);
                }
                r["found"] = all;
                r["floors"] = floors;
                r["skipped"] = skipped;
            }
            else
            {
                if (sDtFloors.Count == 0) throw new McpException("dance_together_floor: setup first");
                if (op == "place")
                {
                    List<object> done = new List<object>();
                    List<object> swapped = new List<object>();
                    foreach (DtFloor f in sDtFloors)
                    {
                        List<int> idx = new List<int>();
                        if (a.Bool("pool", false)) for (int i = 0; i < f.Count; i++) idx.Add(i);
                        else { idx.Add(DtIdx(a, f, "a", 1)); idx.Add(DtIdx(a, f, "b", 2)); }
                        DtSwapStuck(f, idx, swapped);
                        List<Sim> sims = idx.ConvertAll<Sim>(f.Sim);
                        // missing: the until's retry: only Sims not on the lot with no Teleport Me Here running or queued (never a second teleport) (2026-11-08: queued behind a job's IdleTend, or landed off the lot)
                        if (a.Bool("missing", false)) sims = sims.FindAll(delegate (Sim s) { return !DtHere(s, f) && !DtTeleporting(s); });
                        if (sims.Count == 0) continue;
                        done.Add(f.Lot.Name + ": " + string.Join(", ", Array.ConvertAll(SetupSimsAt(sims, f.Lot, f.Floor.Position, true, true, true, false).ToArray(), Convert.ToString)));
                    }
                    r["placed"] = done;
                    r["swapped"] = swapped;
                }
                else if (op == "fire")
                {
                    bool crowd = a.Bool("crowd", false);
                    // EA test Tuning_LotAvailability = the venue is closed (Lot.IsOpenVenue, hours of operation; 2026-11-09 cycle 7 at night): set the condition, the clock goes on to when every floor is open (at most a day)
                    for (int h = 0; h < 24; h++)
                    {
                        bool allOpen = true;
                        foreach (DtFloor f in sDtFloors) allOpen &= f.Lot.IsOpenVenue();
                        if (allOpen) break;
                        Sims3.Gameplay.Utilities.SimClock.AddOneHour();
                    }
                    foreach (DtFloor f in sDtFloors)
                    {
                        f.Fired.Clear();
                        if (crowd) for (int i = 0; i + 1 < f.Count; i += 2) f.Fired.Add(new int[] { i, i + 1 });
                        else f.Fired.Add(new int[] { DtIdx(a, f, "a", 1), DtIdx(a, f, "b", 2) });
                        foreach (int[] pr in f.Fired)
                        {
                            Sim pa = f.Sim(pr[0]), pb = f.Sim(pr[1]);
                            if (!DtHere(pa, f) || !DtHere(pb, f)) throw new McpException("dance_together_floor fire: not on " + f.Lot.Name + " (" + DtWhere(pa) + ", " + DtWhere(pb) + ")");
                        }
                    }
                    sDtCycle++;
                    int[] c = AppDomain.CurrentDomain.GetData("ts3fixes.counters") as int[];
                    int calls = c != null && c.Length > 897 ? c[892] : -1;
                    List<object> stcs = new List<object>(), refused = new List<object>();
                    int queued = 0;
                    foreach (DtFloor f in sDtFloors)
                    {
                        int floorSims = DtFloorSims(f);
                        string cam = DtCam(f);
                        foreach (int[] pr in f.Fired)
                        {
                            Sim sa = f.Sim(pr[0]), sb = f.Sim(pr[1]);
                            DtCancel(sa, sb);  // not CancelAllInteractions: the queue then refuses the new dance (2026-11-08); place cleared it
                            Relationship rel = Relationship.Get(sa, sb, true);
                            CommodityTypes stc = rel != null ? rel.STC.CurrentCommodity : CommodityTypes.Undefined;
                            if (rel != null && stc != CommodityTypes.Neutral && stc != CommodityTypes.Friendly && stc != CommodityTypes.Funny && stc != CommodityTypes.Amorous)
                                rel.STC.Set(sb, sa, CommodityTypes.Friendly, 50f);  // test shortcut: BaseRun / ClubRun route only with an accepted context
                            stcs.Add(stc.ToString());
                            InteractionDefinition def = new Stereo.DanceTogetherA.Definition((IDanceable)f.Obj);
                            InteractionInstance inst = def.CreateInstance(sb, sa, new InteractionPriority(InteractionPriorityLevel.UserDirected), false, true);
                            // one refused pair (2026-11-08: one of four, EA test said no) is logged and skipped; all refused = the step fails
                            if (inst == null || !sa.InteractionQueue.Add(inst)) { refused.Add(f.Lot.Name + ": " + sa.FullName + " with " + sb.FullName + " (" + DtCur(sa) + " / " + DtCur(sb) + ", " + DtWhy(def, sa, sb, inst) + ")"); DiagLine("dancefloor repro: cycle #" + sDtCycle + " lot " + f.Lot.Name + " actor " + sa.FullName + " partner " + sb.FullName + " refused"); continue; }
                            queued++;
                            DiagLine("dancefloor repro: cycle #" + sDtCycle + " phase " + a.Str("phase", "-") + " lot " + f.Lot.Name + " cam " + cam + " actor " + sa.FullName + " partner " + sb.FullName + " floor_sims " + floorSims + " calls " + calls);
                        }
                    }
                    if (queued == 0) throw new McpException("dance_together_floor fire: DanceTogetherA refused for every pair: " + string.Join("; ", Array.ConvertAll(refused.ToArray(), Convert.ToString)));
                    r["stc"] = stcs;
                    r["refused"] = refused;
                    sDtTries++;
                    sDtCancels = 0;
                    sDtBase = DtReturned();
                    sDtSig = null;
                    sDtFiredAt = System.Environment.TickCount;
                }
                else if (op == "cancel")
                {
                    // the cancel step's until repeats this op: a dance still queued after 3 cancels (2026-11-08 try 2: busy for 60 s) gets EA's reset
                    bool reset = ++sDtCancels > 3;
                    // the state wait may end by gave_up with dances unchanged for 60+ s (2026-11-09 false stall on the first cancel poll); the cancel is a change
                    if (sDtCancels == 1) sDtSigAt = System.Environment.TickCount;
                    foreach (DtFloor f in sDtFloors)
                        foreach (int[] pr in f.Fired)
                        {
                            Sim sa = f.Sim(pr[0]), sb = f.Sim(pr[1]);
                            if (!reset) DtCancel(sa, sb);
                            else foreach (Sim s in new Sim[] { sa, sb }) if (DtRunning(s)) { s.SetObjectToReset(); r["reset"] = true; }
                        }
                }
                else if (op != "state") throw new McpException("dance_together_floor: op setup|place|fire|cancel|state");
                bool running = false, busy = false, here = true;
                int pairs = 0;
                List<object> where = new List<object>(), cur = new List<object>(), floors = new List<object>();
                string sig = "";
                foreach (DtFloor f in sDtFloors)
                {
                    List<Sim> sims = new List<Sim>();
                    if (a.Bool("pool", false)) for (int i = 0; i < f.Count; i++) sims.Add(f.Sim(i));
                    else if (a.Has("a")) { sims.Add(f.Sim(DtIdx(a, f, "a", 1))); sims.Add(f.Sim(DtIdx(a, f, "b", 2))); }
                    else foreach (int[] pr in f.Fired) { sims.Add(f.Sim(pr[0])); sims.Add(f.Sim(pr[1])); }
                    foreach (int[] pr in f.Fired) { running |= DtRunning(f.Sim(pr[0])); pairs++; }
                    foreach (Sim s in sims)
                    {
                        busy |= DtRunning(s);
                        if (!DtHere(s, f)) { here = false; where.Add(DtWhere(s) + " (floor " + f.Lot.Name + ", queue " + DtQueue(s) + ")"); }
                        cur.Add(s.FullName + " (" + DtCur(s) + ")");
                        sig += DtPos(s) + "|" + DtCur(s) + "|";
                    }
                    floors.Add(f.Lot.Name + ": cam " + DtCam(f) + ", floor_sims " + DtFloorSims(f));
                }
                int ret = DtReturned(), returned = ret < 0 || sDtBase < 0 ? -1 : ret - sDtBase;
                r["on_lot"] = here;
                r["where"] = where;  // only the Sims not on their floor's lot (or leaving)
                r["tries"] = sDtTries;
                r["cycle"] = sDtCycle;
                r["running"] = running;
                r["busy"] = busy;
                r["returned"] = returned;
                // a pair whose route never returns (2026-11-08: one floor of four for 180 s) must not fail the whole run: settled after 90 s, logged once
                bool settled = returned >= pairs || !running;
                if (!settled && sDtCycle > 0 && System.Environment.TickCount - sDtFiredAt >= 90000)
                {
                    settled = true;
                    if (sDtGaveUp != sDtCycle)
                    {
                        sDtGaveUp = sDtCycle;
                        foreach (DtFloor f in sDtFloors) foreach (int[] pr in f.Fired) if (DtRunning(f.Sim(pr[0])))
                            DiagLine("dancefloor repro: cycle #" + sDtCycle + " lot " + f.Lot.Name + " actor " + f.Sim(pr[0]).FullName + " partner " + f.Sim(pr[1]).FullName + " no route return after 90 s (" + DtCur(f.Sim(pr[0])) + " / " + DtCur(f.Sim(pr[1])) + "), cancelled with the rest");
                    }
                    r["gave_up"] = true;
                }
                r["settled"] = settled;
                r["floors"] = floors;
                sig += ret;
                int now = System.Environment.TickCount;
                if (sig != sDtSig) { sDtSig = sig; sDtSigAt = now; }
                // a long dance is the expected state, not a stall: inside the 90 s window gave_up ends the wait (proof 16, 2026-11-08: 60 s stall
                // fired first in the even-cycle mem_gc wait with 2 of 4 floors still dancing)
                bool dancing = op == "state" && running && sDtCycle > 0 && now - sDtFiredAt < 90000;
                r["stalled"] = !dancing && now - sDtSigAt >= 60000;
                r["sims"] = cur;
            }
            return r;
        }

        // pool index (0-based) of picked a/b (1-based within the floor's pool)
        static int DtIdx(Args a, DtFloor f, string key, int dflt)
        {
            int n = (int)a.Long(key, dflt);
            if (n < 1 || n > f.Count) throw new McpException("dance_together_floor: " + key + " " + n + " outside the pool of " + f.Count + " on " + f.Lot.Name);
            return n - 1;
        }

        // Sims on the floor's lot doing a dance interaction (any type name with "Dance"): the floor's load
        static int DtFloorSims(DtFloor f)
        {
            int n = 0;
            foreach (Sim s in f.Lot.GetSims()) if (DtCur(s).IndexOf("Dance", StringComparison.Ordinal) >= 0) n++;
            return n;
        }

        // seen proxy: the camera target within 80 m of the floor (ts3fixes' own unseen test also checks the frustum)
        static string DtCam(DtFloor f)
        {
            Vector3 t = CameraController.GetTarget(), p = f.Floor.Position;
            double d = Math.Sqrt((t.x - p.x) * (t.x - p.x) + (t.z - p.z) * (t.z - p.z));
            return (d <= 80 ? "seen " : "far ") + Math.Round(d) + "m";
        }

        // a pool NPC: in scope (below), young adult or older, human, not a service Sim, no job role on a lot, not leaving a lot
        static bool DtFits(SimDescription sd)
        {
            Sim s = sd.CreatedSim;
            return s != null && DtScope(s) == null && !sd.TeenOrBelow && sd.IsHuman && s.Service == null && sd.AssignedRole == null && !s.IsLeavingLot  // no role: bartenders and bouncers keep IdleTend / WatchDoor ahead of any teleport
                && !sDtDropped.Contains(sd.SimDescriptionId) && DtStuck(s) == null;
        }

        // null = Teleport Me Here can run; else why not: a group / follower situation keeps its interaction at the queue head through
        // CancelAllInteractions (2026-11-09 rc1110b: Matty Crewe on the world lot, FollowerMeetUpAtPoint ahead of the queued teleport for 60 s; rc1110: Tom Wordy), a vehicle, no lot
        static string DtStuck(Sim s)
        {
            if (s.LotCurrent == null) return "no lot";
            if (s.Posture is Sims3.Gameplay.Objects.Vehicles.SittingInVehicle) return "in a vehicle";
            foreach (InteractionInstance i in s.InteractionQueue.InteractionList)
            {
                string n = i.GetType().Name;
                if (n.IndexOf("Follower", StringComparison.Ordinal) >= 0 || n.IndexOf("MeetUp", StringComparison.Ordinal) >= 0) return n;
            }
            Sims3.Gameplay.Autonomy.SituationComponent sc = s.Autonomy != null ? s.Autonomy.SituationComponent : null;
            if (sc != null && sc.Situations != null)
                foreach (object x in sc.Situations)
                    if (x is Sims3.Gameplay.Situations.GroupingSituation) return "in a group (" + x.GetType().Name + ")";
            return null;
        }

        // a queued Teleport Me Here that stays at the queue head, not running, with the Sim off the floor's lot for 20 s of real time (2026-11-09 cycle 8 phase B:
        // Kai Leiko on his home lot, 7 tries): the until's missing retry skips Sims with a queued teleport, so DtSwapStuck replaces him. Timed from the first place that sees it queued
        static string DtStalled(Sim s, DtFloor f)
        {
            ulong id = s.SimDescription.SimDescriptionId;
            InteractionInstance head = null;
            foreach (InteractionInstance i in s.InteractionQueue.InteractionList) { head = i; break; }
            if (!(head is TerrainInteraction) || s.LotCurrent == f.Lot) { sDtQueuedAt.Remove(id); return null; }
            int t;
            if (s.CurrentInteraction == head || !sDtQueuedAt.TryGetValue(id, out t)) { sDtQueuedAt[id] = System.Environment.TickCount; return null; }  // running, or first seen
            return System.Environment.TickCount - t > 20000 ? "teleport stalled" : null;
        }

        // the picker's rule at every place (an NPC can join a group after setup): a stuck pool NPC (DtStuck) is dropped, its queued
        // teleport cancelled and its autonomy back on (it is no longer ours), and another NPC that fits takes its pool slot (the floor's lot first)
        static void DtSwapStuck(DtFloor f, List<int> idx, List<object> log)
        {
            foreach (int i in idx)
            {
                Sim s = f.Sim(i);
                string why = DtStuck(s) ?? DtStalled(s, f);
                if (why == null) continue;
                SimDescription pick = null;
                foreach (SimDescription sd in SimDescription.GetSimDescriptionsInWorld())
                {
                    if (!Util.InWorldNow(sd) || Util.Picked.Contains(sd.SimDescriptionId) || !DtFits(sd)) continue;
                    if (pick == null || sd.CreatedSim.LotCurrent == f.Lot) pick = sd;
                    if (sd.CreatedSim.LotCurrent == f.Lot) break;
                }
                if (pick == null) { log.Add(f.Lot.Name + ": " + s.FullName + " stuck (" + why + "), no free NPC to replace it"); continue; }
                foreach (InteractionInstance q in new List<InteractionInstance>(s.InteractionQueue.InteractionList))
                    if (q is TerrainInteraction) s.InteractionQueue.CancelInteraction(q, false);
                AutonomyCmd(new Args(new Dictionary<string, object> { { "sim", s.SimDescription.SimDescriptionId.ToString() }, { "enabled", true } }));
                sDtDropped.Add(s.SimDescription.SimDescriptionId);
                Util.Picked[f.First + i] = pick.SimDescriptionId;
                log.Add(f.Lot.Name + ": " + s.FullName + " stuck (" + why + "), replaced by " + pick.FullName);
                DiagLine("dancefloor repro: " + f.Lot.Name + " pool " + (i + 1) + " " + s.FullName + " stuck (" + why + "), replaced by " + pick.FullName);
            }
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
            SimDescription sd = SimDescription.Find(id);
            if (sd == null || !Util.InWorldNow(sd)) throw new McpException("dance_together_floor: a picked NPC left the world");
            string why = DtScope(sd.CreatedSim);
            if (why != null) throw new McpException("dance_together_floor: refused, " + sd.FullName + " is " + why);
            return sd.CreatedSim;
        }

        static bool DtRunning(Sim s)
        {
            // only the dance's own B side: any other chat aimed at the Sim is a SocialInteractionB too (2026-11-08: busy for 60 s on the club lot)
            foreach (InteractionInstance i in s.InteractionQueue.InteractionList)
                if (i is Stereo.DanceTogetherA || (i is SocialInteractionB && i.LinkedInteractionInstance is Stereo.DanceTogetherA)) return true;
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

        // a Teleport Me Here running or queued (SetupSimsAt, >2 m from the spot) = not in place yet: fire waits for it (2026-11-09: fire mid-teleport, Add refused)
        static bool DtHere(Sim s, DtFloor f) { return s.LotCurrent == f.Lot && !s.IsLeavingLot && !DtTeleporting(s); }
        static bool DtTeleporting(Sim s) { foreach (InteractionInstance i in s.InteractionQueue.InteractionList) if (i is TerrainInteraction) return true; return s.CurrentInteraction is TerrainInteraction; }
        // EA's refusal reason, cheap: the definition test and its greyed-out tooltip, else the queue contents
        static string DtWhy(InteractionDefinition def, Sim sa, Sim sb, InteractionInstance inst)
        {
            if (inst == null) return "no instance";
            GreyedOutTooltipCallback cb = null;
            InteractionInstanceParameters p = new InteractionInstanceParameters(new Sims3.Gameplay.Autonomy.InteractionObjectPair(def, sb), sa, new InteractionPriority(InteractionPriorityLevel.UserDirected), false, true);
            InteractionTestResult res = def.Test(ref p, ref cb);
            return "test " + res + (cb != null ? ": " + cb() : "") + ", queue " + DtQueue(sa);
        }
        static string DtWhere(Sim s) { return s.FullName + " on " + (s.LotCurrent != null ? s.LotCurrent.Name : "no lot") + (s.IsLeavingLot ? ", leaving" : "") + (DtTeleporting(s) ? ", teleporting" : ""); }
        static string DtQueue(Sim s) { List<string> q = new List<string>(); foreach (InteractionInstance i in s.InteractionQueue.InteractionList) q.Add(i.GetType().Name); return q.Count > 0 ? string.Join(">", q.ToArray()) : "empty"; }
        static string DtPos(Sim s) { Vector3 p = s.Position; return Math.Round(p.x * 2) + "," + Math.Round(p.z * 2); }
        static string DtCur(Sim s) { InteractionInstance i = s.CurrentInteraction; return i != null ? i.GetType().Name : "none"; }
    }
}
