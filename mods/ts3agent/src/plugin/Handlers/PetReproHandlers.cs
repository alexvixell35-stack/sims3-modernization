using System;
using System.Collections.Generic;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay;
using Sims3.Gameplay.PetSystems;
using Sims3.SimIFace;

namespace Ts3Agent
{
    // Repro for the docs/656 pet fixes. pet_repro {mode, start: true, seconds?} | {result: true} | {stop: true}. Same setup in
    // the OFF and ON arm; only the switch differs. NPC picks only (known non-active household, not selectable or selected).
    //  - mode "arrive" (petarrivepoint, §4.1): an NPC horse whose home lot's front door refuses it (EA's
    //    CommonDoor.IsAllowedThrough; spawn_horse first when the save has none) is set down (setup_sims, autonomy off) at the
    //    entry of a lot 60-250 m from its home, then EA's GoHome is pushed. OFF: the first plan aims inside the front door,
    //    "routecause: <horse> npc, GoHome, Plan FailedBecauseDestinationPortalLocked" (switch routecauselog). ON: 4170 +1 and a
    //    "petarrivepoint: <horse> (Horse) -> <lot>: front door closed to it" line, no PortalLocked GoHome line for it.
    //  - mode "stray" (strayleave, §4.2): a stray from EA's pool (not instantiated) is sent by EA's StrayPets.SendStrayToLot to
    //    the active household's lot, its StrayGoToLot is cancelled (the arrival callback that adds the StrayPet buff never
    //    runs: the "never got the buff" case) and it is set down on that lot with its buffs cleared. OFF: it stays for the window.
    //    ON: after kStrayLeaveHours (the repro sets 1 via reflect_set) the next hourly StrayPetHelper pushes EA's go-home, 4174 +1,
    //    "strayleave: ... MakeSimGoHome pushed".
    //  - mode "visit" (petlongvisit, §4.3): an owned NPC pet (not a stray, not a pet-pool animal) is set down at the entry of a
    //    lot far from the active Sim, then EA's VisitCommunityLot to a community lot > 200 m from it and from the active Sim is
    //    pushed. OFF: it walks. ON (unseen): 4179 +1, "petlonghome: placed ... via visit".
    //  - mode "home" (petlonghome, docs/645 §4): the same pick, set down at a lot 200-1500 m from its own home lot (both far
    //    from the active Sim), then EA's GoHome. OFF: it walks. ON (unseen): 4076 +1, "petlonghome: placed ... via gohome".
    // The result: per pet its lot, interaction, queue, whether it reached the target / left, and the 4170-4180 deltas.
    public static partial class Handlers
    {
        static void RegisterPetRepro() { Dispatcher.Register("pet_repro", PetRepro); }

        static int sPrGen;
        static string sPrMode, sPrError, sPrTarget;
        static Sim sPrPet;
        static Lot sPrLot;
        static bool sPrDone, sPrReached, sPrLeft;
        static int sPrLeftAtS = -1, sPrReachedAtS = -1;
        static readonly int[] sPrBase = new int[11];
        static int sPrPlhBase;
        static readonly string[] kPrCtr = { "arrive_skipped", "arrive_unvalidated", "arrive_exempt", "arrive_errors", "stray_sent", "stray_seen",
            "stray_exempt", "stray_errors", "nospot_saved", "visit_placed", "visit_refused" };

        static int PrCtr(int k) { int[] c = BgsCounters(); return c != null && c.Length > 4180 ? c[4170 + k] : -1; }

        static object PetRepro(Args a)
        {
            RequireLive();
            Dictionary<string, object> r = Util.Obj();
            if (a.Has("start"))
            {
                int gen = ++sPrGen, seconds = Math.Max(10, (int)a.Long("seconds", 300));
                sPrMode = a.Has("mode") ? a.Str("mode") : "arrive";
                sPrDone = sPrReached = sPrLeft = false; sPrError = null; sPrLeftAtS = sPrReachedAtS = -1; sPrPet = null; sPrLot = null; sPrTarget = null;
                for (int k = 0; k < 11; k++) sPrBase[k] = PrCtr(k);
                { int[] c0 = BgsCounters(); sPrPlhBase = c0 != null && c0.Length > 4077 ? c0[4076] : -1; }
                if (sPrMode == "arrive") PrArrive(r);
                else if (sPrMode == "stray") PrStray(r);
                else if (sPrMode == "visit") PrVisit(r, false);
                else if (sPrMode == "home") PrVisit(r, true);
                else throw new McpException("pet_repro: mode is arrive, stray, visit or home");
                DiagLine("pet_repro: start " + sPrMode + ", " + (sPrPet != null ? sPrPet.FullName : "no pet") + " -> " + sPrTarget + ", " + seconds
                    + " s, petarrivepoint " + ModOn("petarrivepoint") + ", strayleave " + ModOn("strayleave") + ", petlongvisit " + ModOn("petlongvisit"));
                Simulator.AddObject(new OneShotFunctionTask(delegate
                {
                    int t0 = System.Environment.TickCount;
                    try
                    {
                        while (gen == sPrGen && System.Environment.TickCount - t0 < seconds * 1000)
                        {
                            PrTick((System.Environment.TickCount - t0) / 1000);
                            Simulator.Sleep(30);
                        }
                    }
                    catch (Exception e) { sPrError = e.GetType().Name + ": " + e.Message; DiagLine("pet_repro: error " + sPrError); }
                    finally
                    {
                        if (gen == sPrGen)
                        {
                            sPrDone = true;
                            DiagLine("pet_repro: " + sPrMode + " done, reached " + sPrReached + " (" + sPrReachedAtS + " s), left " + sPrLeft + " (" + sPrLeftAtS + " s)");
                        }
                    }
                }));
                r["started"] = true; r["mode"] = sPrMode;
                return r;
            }
            if (a.Has("stop")) { sPrGen++; r["stopped"] = true; return r; }
            Dictionary<string, object> ctr = Util.Obj();
            for (int k = 0; k < 11; k++) { int v = PrCtr(k); ctr[kPrCtr[k] + "_" + (4170 + k)] = v < 0 ? -1 : v - sPrBase[k]; }
            { int[] c1 = BgsCounters(); ctr["petlonghome_placed_4076"] = c1 != null && c1.Length > 4077 && sPrPlhBase >= 0 ? c1[4076] - sPrPlhBase : -1; }
            r["mode"] = sPrMode; r["done"] = sPrDone; r["error"] = sPrError; r["counters"] = ctr; r["target"] = sPrTarget;
            r["reached"] = sPrReached; r["reached_s"] = sPrReachedAtS; r["left"] = sPrLeft; r["left_s"] = sPrLeftAtS;
            r["petarrivepoint"] = ModOn("petarrivepoint"); r["strayleave"] = ModOn("strayleave"); r["petlongvisit"] = ModOn("petlongvisit"); r["petlonghome"] = ModOn("petlonghome");
            Sim p = sPrPet;
            if (p != null && !p.HasBeenDestroyed)
            {
                r["pet"] = p.FullName;
                r["lot"] = p.LotCurrent != null ? p.LotCurrent.Name : null;
                r["current"] = p.CurrentInteraction != null ? p.CurrentInteraction.GetType().Name : null;
                r["queue"] = p.InteractionQueue != null ? p.InteractionQueue.Count : 0;
                r["stray_buff"] = p.BuffManager != null && p.BuffManager.HasElement(BuffNames.StrayPet);
            }
            else r["pet"] = p != null ? "gone (de-instantiated)" : null;
            return r;
        }

        static void PrTick(int s)
        {
            Sim p = sPrPet;
            if (p == null) return;
            if (p.HasBeenDestroyed || !p.InWorld)
            {
                if (!sPrLeft) { sPrLeft = true; sPrLeftAtS = s; }
                return;
            }
            if (!sPrReached && sPrLot != null && p.LotCurrent == sPrLot && sPrMode != "stray") { sPrReached = true; sPrReachedAtS = s; DiagLine("pet_repro: " + p.FullName + " on " + sPrLot.Name + " after " + s + " s"); }
            if (sPrMode == "stray" && !sPrLeft)
            {
                InteractionInstance cur = p.CurrentInteraction;
                if (cur is Sim.GoToVirtualHome || cur is GoHome || (sPrLot != null && p.LotCurrent != sPrLot))
                {
                    sPrLeft = true; sPrLeftAtS = s;
                    DiagLine("pet_repro: stray " + p.FullName + " leaving after " + s + " s (" + (cur != null ? cur.GetType().Name : "off the lot") + ")");
                }
            }
        }

        static bool PrNpc(Sim s)
        {
            return BgsNpc(s) && !s.HasBeenDestroyed && s.InWorld && s.Parent == null && s.SimDescription != null;
        }

        // a non-world lot with an entry 60-250 m (min..max) from `from`, and far (>= away) from the active Sim when away > 0
        static Lot PrLotNear(Vector3 from, float min, float max, Lot not, bool community, float away)
        {
            Sim me = Util.ActiveSim();
            foreach (Lot l in LotManager.AllLotsWithoutCommonExceptions)
            {
                if (l == null || l.IsWorldLot || l == not || l == Household.ActiveHouseholdLot || (community && !l.IsCommunityLot)) continue;
                if (l.IsHouseboatLot() || l.IsDivingLot) continue;
                float d = (l.EntryPoint() - from).Length();
                if (d < min || d > max) continue;
                if (away > 0f && me != null && (l.Position - me.Position).Length() < away) continue;
                return l;
            }
            return null;
        }

        static void PrArrive(Dictionary<string, object> r)
        {
            foreach (Sim s in new List<Sim>(LotManager.Actors))
            {
                if (!PrNpc(s) || !s.IsHorse || s.SimDescription.IsUnicorn || s.LotHome == null || s.LotHome.HasVirtualResidentialSlots) continue;
                Door door = s.LotHome.FindFrontDoor();
                if (door == null || door.IsAllowedThrough(s)) continue;
                Lot from = PrLotNear(s.LotHome.EntryPoint(), 60f, 250f, s.LotHome, false, 0f);
                if (from == null) continue;
                sPrPet = s; sPrLot = s.LotHome; sPrTarget = s.LotHome.Name;
                r["setup"] = SetupSimsAt(new List<Sim> { s }, from, from.EntryPoint(), true, true, true, false);
                r["from"] = from.Name;
                r["front_door_allows"] = false;
                Simulator.AddObject(new OneShotFunctionTask(delegate
                {
                    for (int i = 0; i < 20 && s.LotCurrent != from; i++) Simulator.Sleep(10);  // Teleport Me Here runs first
                    s.InteractionQueue.Add(GoHome.Singleton.CreateInstance(s.LotHome, s, new InteractionPriority(InteractionPriorityLevel.High), true, true));
                    DiagLine("pet_repro: GoHome pushed for " + s.FullName + " from " + from.Name + " to " + s.LotHome.Name);
                }));
                return;
            }
            throw new McpException("pet_repro arrive: no NPC horse whose home front door refuses it with a lot 60-250 m away (run spawn_horse first)");
        }

        static void PrStray(Dictionary<string, object> r)
        {
            Lot home = Household.ActiveHouseholdLot;
            if (home == null) throw new McpException("pet_repro stray: needs an active household with a home lot");
            List<SimDescription> pool = StrayPets.GetStrayPets(true);
            if (pool == null || pool.Count == 0) throw new McpException("pet_repro stray: no stray left in EA's pool (all instantiated)");
            SimDescription d = pool[0];
            StrayPets.SendStrayToLot(d, home, 60f);
            Sim s = d.CreatedSim;
            if (s == null) throw new McpException("pet_repro stray: SendStrayToLot made no Sim");
            sPrPet = s; sPrLot = home; sPrTarget = home.Name;
            r["setup"] = SetupSimsAt(new List<Sim> { s }, home, home.EntryPoint(), true, true, true, true);  // cancels StrayGoToLot, clears buffs
            r["stray"] = s.FullName;
        }

        // visit: VisitCommunityLot to a far community lot (petlongvisit); home: GoHome to its own far home lot (petlonghome)
        static void PrVisit(Dictionary<string, object> r, bool home)
        {
            Sim me = Util.ActiveSim();
            foreach (Sim s in new List<Sim>(LotManager.Actors))
            {
                SimDescription d = s.SimDescription;
                if (!PrNpc(s) || d == null || !d.IsPet || d.IsStray || d.IsUnicorn || d.Household == null || d.Household.IsPetHousehold) continue;
                Lot from, to;
                if (home)
                {
                    to = s.LotHome;
                    if (to == null || to == Household.ActiveHouseholdLot || to.HasVirtualResidentialSlots || to.IsHouseboatLot()) continue;
                    if (me != null && (to.Position - me.Position).Length() < 250f) continue;
                    from = PrLotNear(to.EntryPoint(), 200f, 1500f, to, false, 250f);
                }
                else
                {
                    from = PrLotNear(me != null ? me.Position : s.Position, 250f, 2000f, null, false, 250f);
                    to = from != null ? PrLotNear(from.EntryPoint(), 200f, 1500f, from, true, 250f) : null;
                }
                if (from == null || to == null) continue;
                sPrPet = s; sPrLot = to; sPrTarget = to.Name;
                r["setup"] = SetupSimsAt(new List<Sim> { s }, from, from.EntryPoint(), true, true, true, false);
                r["from"] = from.Name;
                Simulator.AddObject(new OneShotFunctionTask(delegate
                {
                    for (int i = 0; i < 20 && s.LotCurrent != from; i++) Simulator.Sleep(10);
                    InteractionPriority pr = new InteractionPriority(InteractionPriorityLevel.High);
                    s.InteractionQueue.Add(home ? GoHome.Singleton.CreateInstance(to, s, pr, true, true) : VisitCommunityLot.Singleton.CreateInstance(to, s, pr, true, true));
                    DiagLine("pet_repro: " + (home ? "GoHome" : "VisitCommunityLot") + " pushed for " + s.FullName + " from " + from.Name + " to " + to.Name);
                }));
                return;
            }
            throw new McpException("pet_repro " + (home ? "home" : "visit") + ": no owned NPC pet, or no far lot pair away from the active Sim");
        }
    }
}
