using System;
using System.Collections.Generic;
using System.Reflection;
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
    // Two phases: setup (the teleport to the start spot, EA's Teleport Me Here) and only once the pet stands there, the push of
    // EA's interaction and the timer. A pet that is not at the start spot within kPrSetupS real seconds = error (no timer).
    //  - mode "arrive" (petarrivepoint, §4.1): an owned NPC pet whose home lot (no apartment slots) has a front door that refuses
    //    it. Preferred: a horse (EA refuses horses at every non-barn door). Else a cat or dog whose home front door gets EA's
    //    "Pets" lock (CommonDoor.SetLockType, restored at the end of the window or on stop). The lot's arrival point is marked
    //    unvalidated (EA's own public BestRouteIntoLotLocationValidated, which build mode also clears) so the pet is the first
    //    to validate it. Set down 40-145 m from home (under petlonghome's 150 m, so that switch leaves it alone), then EA's GoHome.
    //    OFF: the first plan aims inside the front door, PortalLocked (routecauselog), and the lot's point ends Invalid
    //    (after.validated true, after.has_point false). ON: 4170 +1 and 4171 +1, a "petarrivepoint:" line, the lot left
    //    unvalidated for the next Sim. `before` / `after` report the lot's cache and the door the hook judges.
    //  - mode "stray" (strayleave, §4.2): a stray from EA's pool (not instantiated) is sent by EA's StrayPets.SendStrayToLot to
    //    the active household's lot, its StrayGoToLot is cancelled (the arrival callback that adds the StrayPet buff never
    //    runs: the "never got the buff" case) and it is set down on that lot with its buffs cleared. OFF: it stays for the window.
    //    ON: after kStrayLeaveHours (the repro sets 1 via reflect_set) the next hourly StrayPetHelper pushes EA's go-home, 4174 +1,
    //    "strayleave: ... MakeSimGoHome pushed".
    //  - mode "visit" (petlongvisit, §4.3): an owned NPC pet (not a stray, not a pet-pool animal; cats and dogs before horses,
    //    which gallop) is set down at a lot >= 300 m from the camera target and the active Sim, then EA's VisitCommunityLot to a
    //    community lot 400-1500 m from there, also >= 300 m from both. OFF: it walks. ON (unseen): 4179 +1, "placed ... via visit".
    //  - mode "home" (petlonghome, docs/656 §8.2): the same pick, set down at a lot 400-1500 m from its own home lot (both far
    //    from the camera and the active Sim), then EA's GoHome. OFF: it walks. ON (unseen): 4622 +1, "placed ... via gohome".
    // The result: the pet, species, its lot now, whether it is on the target now, reached / left and when (seconds after the
    // push), jumped_s (first move > 30 m between two samples: a placement), distance at the push, and the counter deltas.
    public static partial class Handlers
    {
        static void RegisterPetRepro() { Dispatcher.Register("pet_repro", PetRepro); }

        const int kPrSetupS = 30;
        static int sPrGen;
        static string sPrMode, sPrError, sPrTarget, sPrFromName;
        static Sim sPrPet;
        static Lot sPrLot, sPrFrom;
        static Vector3 sPrSpot;
        delegate void PrPushFn();
        static PrPushFn sPrPush;
        static bool sPrDone, sPrReached, sPrLeft, sPrPushed;
        static int sPrLeftAtS = -1, sPrReachedAtS = -1, sPrJumpS = -1, sPrPushDist = -1;
        static Vector3 sPrLastPos;
        static CommonDoor sPrDoor;
        static CommonDoor.tLock sPrDoorLock;
        static Dictionary<string, object> sPrBefore, sPrAfter;
        static readonly int[] sPrBase = new int[11];
        static int sPrPlhBase;
        static readonly string[] kPrCtr = { "arrive_skipped", "arrive_unvalidated", "arrive_exempt", "arrive_errors", "stray_sent", "stray_seen",
            "stray_exempt", "stray_errors", "nospot_saved", "visit_placed", "visit_refused" };
        static FieldInfo sPrLocField;

        static int PrCtr(int k) { int[] c = BgsCounters(); return c != null && c.Length > 4180 ? c[4170 + k] : -1; }

        static object PetRepro(Args a)
        {
            RequireLive();
            Dictionary<string, object> r = Util.Obj();
            if (a.Has("start"))
            {
                PrRestoreDoor();
                int gen = ++sPrGen, seconds = Math.Max(10, (int)a.Long("seconds", 300));
                sPrMode = a.Has("mode") ? a.Str("mode") : "arrive";
                sPrDone = sPrReached = sPrLeft = sPrPushed = false; sPrError = null; sPrLeftAtS = sPrReachedAtS = sPrJumpS = sPrPushDist = -1;
                sPrPet = null; sPrLot = sPrFrom = null; sPrTarget = sPrFromName = null; sPrPush = null; sPrSpot = Vector3.Invalid;
                sPrBefore = sPrAfter = null;
                for (int k = 0; k < 11; k++) sPrBase[k] = PrCtr(k);
                { int[] c0 = BgsCounters(); sPrPlhBase = c0 != null && c0.Length > 4623 ? c0[4622] : -1; }
                if (sPrMode == "arrive") PrArrive(r);
                else if (sPrMode == "stray") PrStray(r);
                else if (sPrMode == "visit") PrVisit(r, false);
                else if (sPrMode == "home") PrVisit(r, true);
                else throw new McpException("pet_repro: mode is arrive, stray, visit or home");
                DiagLine("pet_repro: start " + sPrMode + ", " + PrName(sPrPet) + " from " + sPrFromName + " -> " + sPrTarget + ", " + seconds
                    + " s, petarrivepoint " + ModOn("petarrivepoint") + ", strayleave " + ModOn("strayleave") + ", petlongvisit " + ModOn("petlongvisit")
                    + ", petlonghome " + ModOn("petlonghome"));
                Simulator.AddObject(new OneShotFunctionTask(delegate { PrRun(gen, seconds); }));
                r["started"] = true; r["mode"] = sPrMode; r["pet"] = PrName(sPrPet); r["target"] = sPrTarget; r["from"] = sPrFromName;
                return r;
            }
            if (a.Has("stop")) { sPrGen++; PrRestoreDoor(); r["stopped"] = true; return r; }
            Dictionary<string, object> ctr = Util.Obj();
            for (int k = 0; k < 11; k++) { int v = PrCtr(k); ctr[kPrCtr[k] + "_" + (4170 + k)] = v < 0 ? -1 : v - sPrBase[k]; }
            { int[] c1 = BgsCounters(); ctr["petlonghome_placed_4622"] = c1 != null && c1.Length > 4623 && sPrPlhBase >= 0 ? c1[4622] - sPrPlhBase : -1; }
            r["mode"] = sPrMode; r["done"] = sPrDone; r["error"] = sPrError; r["counters"] = ctr; r["target"] = sPrTarget; r["from"] = sPrFromName;
            r["pushed"] = sPrPushed; r["push_dist_m"] = sPrPushDist; r["jumped_s"] = sPrJumpS;
            r["reached"] = sPrReached; r["reached_s"] = sPrReachedAtS; r["left"] = sPrLeft; r["left_s"] = sPrLeftAtS;
            r["before"] = sPrBefore; r["after"] = sPrAfter ?? (sPrMode == "arrive" ? PrLotState(sPrLot, sPrPet) : null);
            r["petarrivepoint"] = ModOn("petarrivepoint"); r["strayleave"] = ModOn("strayleave"); r["petlongvisit"] = ModOn("petlongvisit"); r["petlonghome"] = ModOn("petlonghome");
            Sim p = sPrPet;
            if (p != null && !p.HasBeenDestroyed)
            {
                r["pet"] = p.FullName;
                r["species"] = p.SimDescription != null ? p.SimDescription.Species.ToString() : null;
                r["lot"] = p.LotCurrent != null ? p.LotCurrent.Name : null;
                r["on_target_now"] = sPrLot != null && p.LotCurrent == sPrLot;
                r["current"] = p.CurrentInteraction != null ? p.CurrentInteraction.GetType().Name : null;
                r["queue"] = p.InteractionQueue != null ? p.InteractionQueue.Count : 0;
                r["stray_buff"] = p.BuffManager != null && p.BuffManager.HasElement(BuffNames.StrayPet);
            }
            else r["pet"] = p != null ? "gone (de-instantiated)" : null;
            return r;
        }

        static string PrName(Sim s) { return s == null ? "no pet" : s.FullName + " (" + (s.SimDescription != null ? s.SimDescription.Species.ToString() : "?") + ")"; }

        // setup phase (wait for the teleport), then the push and the timer
        static void PrRun(int gen, int seconds)
        {
            try
            {
                Sim p = sPrPet;
                int s0 = System.Environment.TickCount;
                while (gen == sPrGen && p != null && !PrAtStart(p))
                {
                    if (System.Environment.TickCount - s0 > kPrSetupS * 1000)
                    {
                        sPrError = "setup: " + p.FullName + " not at the start spot after " + kPrSetupS + " s (lot " + (p.LotCurrent != null ? p.LotCurrent.Name : "street") + "), no push";
                        DiagLine("pet_repro: " + sPrError);
                        return;
                    }
                    Simulator.Sleep(10);
                }
                if (gen != sPrGen) return;
                if (p != null && sPrLot != null) sPrPushDist = (int)(p.Position - sPrLot.Position).Length();
                if (sPrMode == "arrive") sPrBefore = PrLotState(sPrLot, p);
                if (sPrPush != null) sPrPush();
                sPrPushed = true;
                if (p != null) sPrLastPos = p.Position;
                int t0 = System.Environment.TickCount;
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
                    if (sPrMode == "arrive") sPrAfter = PrLotState(sPrLot, sPrPet);
                    PrRestoreDoor();
                    sPrDone = true;
                    DiagLine("pet_repro: " + sPrMode + " done, " + PrName(sPrPet) + ", pushed " + sPrPushed + " at " + sPrPushDist + " m, reached " + sPrReached
                        + " (" + sPrReachedAtS + " s), jumped " + sPrJumpS + " s, left " + sPrLeft + " (" + sPrLeftAtS + " s), now on "
                        + (sPrPet != null && sPrPet.LotCurrent != null ? sPrPet.LotCurrent.Name : "street"));
                }
            }
        }

        static bool PrAtStart(Sim p)
        {
            if (p.HasBeenDestroyed || !p.InWorld) return false;
            if (sPrFrom != null && p.LotCurrent != sPrFrom) return false;
            if (sPrSpot == Vector3.Invalid) return true;
            float dx = p.Position.x - sPrSpot.x, dz = p.Position.z - sPrSpot.z;
            return dx * dx + dz * dz < 25f;
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
            Vector3 pos = p.Position;
            if (sPrJumpS < 0 && (pos - sPrLastPos).Length() > 30f) { sPrJumpS = s; DiagLine("pet_repro: " + p.FullName + " moved " + (int)(pos - sPrLastPos).Length() + " m at once after " + s + " s"); }
            sPrLastPos = pos;
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

        // far (>= away) from the active Sim and from the camera target
        static bool PrFarFromPlayer(Vector3 p, float away)
        {
            if (away <= 0f) return true;
            Sim me = Util.ActiveSim();
            if (me != null && (p - me.Position).Length() < away) return false;
            Vector3 cam = CameraController.GetTarget();
            return (p - cam).Length() >= away;
        }

        // a non-world lot with an entry min..max m from `from`, far (>= away) from the active Sim and the camera when away > 0
        static Lot PrLotNear(Vector3 from, float min, float max, Lot not, bool community, float away)
        {
            foreach (Lot l in LotManager.AllLotsWithoutCommonExceptions)
            {
                if (l == null || l.IsWorldLot || l == not || l == Household.ActiveHouseholdLot || (community && !l.IsCommunityLot)) continue;
                if (l.IsHouseboatLot() || l.IsDivingLot || l.HasVirtualResidentialSlots) continue;
                float d = (l.EntryPoint() - from).Length();
                if (d < min || d > max) continue;
                if (!PrFarFromPlayer(l.Position, away) || !PrFarFromPlayer(l.EntryPoint(), away)) continue;
                return l;
            }
            return null;
        }

        // owned NPC pets, cats and dogs before horses (horses gallop: a short walk); preferHorse reverses that
        static List<Sim> PrOwnedPets(bool preferHorse)
        {
            List<Sim> first = new List<Sim>(), second = new List<Sim>();
            foreach (Sim s in new List<Sim>(LotManager.Actors))
            {
                SimDescription d = s.SimDescription;
                if (!PrNpc(s) || d == null || !d.IsPet || d.IsStray || d.IsUnicorn || d.Household == null || d.Household.IsPetHousehold) continue;
                if (WildHorses.IsWildHorse(d)) continue;
                if (s.IsHorse == preferHorse) first.Add(s); else second.Add(s);
            }
            first.AddRange(second);
            return first;
        }

        // what PetArrivePoint's hook reads: the lot's cache, its front door and whether that door lets the pet through
        static Dictionary<string, object> PrLotState(Lot lot, Sim s)
        {
            if (lot == null || s == null || s.HasBeenDestroyed) return null;
            Dictionary<string, object> o = Util.Obj();
            try
            {
                if (sPrLocField == null) sPrLocField = typeof(Lot).GetField("mBestRouteIntoLotLocation", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                o["validated"] = lot.BestRouteIntoLotLocationValidated;
                o["has_point"] = sPrLocField != null && (Vector3)sPrLocField.GetValue(lot) != Vector3.Invalid;
                Door door = lot.FindFrontDoor();
                o["front_door"] = door != null ? door.GetType().Name : null;
                o["door_allows"] = door != null ? (object)door.IsAllowedThrough(s) : null;
                CommonDoor cd = door as CommonDoor;
                o["lock"] = cd != null ? cd.LockType.ToString() : null;
            }
            catch (Exception e) { o["error"] = e.GetType().Name; }
            return o;
        }

        static void PrRestoreDoor()
        {
            CommonDoor d = sPrDoor;
            sPrDoor = null;
            if (d == null || d.HasBeenDestroyed) return;
            try { d.SetLockType(sPrDoorLock); DiagLine("pet_repro: front door lock back to " + sPrDoorLock); }
            catch (Exception e) { DiagLine("pet_repro: door lock restore failed " + e.GetType().Name); }
        }

        static void PrSetup(Dictionary<string, object> r, Sim s, Lot from, Lot target, PrPushFn push)
        {
            sPrPet = s; sPrLot = target; sPrTarget = target.Name; sPrFrom = from; sPrFromName = from.Name; sPrSpot = from.EntryPoint(); sPrPush = push;
            r["setup"] = SetupSimsAt(new List<Sim> { s }, from, sPrSpot, true, true, true, false);
        }

        static void PrArrive(Dictionary<string, object> r)
        {
            foreach (Sim s in PrOwnedPets(true))
            {
                Lot home = s.LotHome;
                if (home == null || home.IsWorldLot || home.HasVirtualResidentialSlots || home.IsHouseboatLot() || home == Household.ActiveHouseholdLot) continue;
                CommonDoor door = home.FindFrontDoor() as CommonDoor;
                if (door == null) continue;
                bool lockIt = false;
                if (door.IsAllowedThrough(s))
                {
                    if (s.IsHorse) continue;  // a barn door or a large-animal gate: no case
                    lockIt = true;            // a cat or dog: EA's Pets lock on its front door
                }
                Lot from = PrLotNear(home.EntryPoint(), 40f, 145f, home, false, 0f);
                if (from == null) continue;
                if (lockIt)
                {
                    sPrDoor = door; sPrDoorLock = door.LockType;
                    door.SetLockType(CommonDoor.tLock.Pets);
                    if (door.IsAllowedThrough(s)) { PrRestoreDoor(); continue; }
                    r["door_locked_for_pets"] = true;
                }
                Sim pet = s;
                PrSetup(r, s, from, home, delegate
                {
                    home.BestRouteIntoLotLocationValidated = false;  // the pet validates the lot's point first (EA clears it in build mode too)
                    pet.InteractionQueue.Add(GoHome.Singleton.CreateInstance(home, pet, new InteractionPriority(InteractionPriorityLevel.High), true, true));
                    DiagLine("pet_repro: GoHome pushed for " + pet.FullName + " from " + sPrFromName + " to " + home.Name);
                });
                return;
            }
            throw new McpException("pet_repro arrive: no owned NPC pet with a home lot (no apartment), a front door and a lot 40-145 m from it");
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
            sPrPet = s; sPrLot = home; sPrTarget = home.Name; sPrFrom = home; sPrFromName = home.Name; sPrSpot = home.EntryPoint();
            r["setup"] = SetupSimsAt(new List<Sim> { s }, home, sPrSpot, true, true, true, true);  // cancels StrayGoToLot, clears buffs
            r["stray"] = s.FullName;
        }

        // visit: VisitCommunityLot to a far community lot (petlongvisit); home: GoHome to its own far home lot (petlonghome)
        static void PrVisit(Dictionary<string, object> r, bool home)
        {
            const float away = 300f, min = 400f, max = 1500f;
            foreach (Sim s in PrOwnedPets(false))
            {
                Lot from, to;
                if (home)
                {
                    to = s.LotHome;
                    if (to == null || to == Household.ActiveHouseholdLot || to.HasVirtualResidentialSlots || to.IsHouseboatLot()) continue;
                    if (!PrFarFromPlayer(to.Position, away)) continue;
                    from = PrLotNear(to.EntryPoint(), min, max, to, false, away);
                }
                else
                {
                    Sim me = Util.ActiveSim();
                    from = PrLotNear(me != null ? me.Position : s.Position, away, 2500f, s.LotHome, false, away);
                    to = from != null ? PrLotNear(from.EntryPoint(), min, max, from, true, away) : null;
                }
                if (from == null || to == null) continue;
                Sim pet = s; Lot target = to;
                PrSetup(r, s, from, to, delegate
                {
                    InteractionPriority pr = new InteractionPriority(InteractionPriorityLevel.High);
                    pet.InteractionQueue.Add(home ? GoHome.Singleton.CreateInstance(target, pet, pr, true, true) : VisitCommunityLot.Singleton.CreateInstance(target, pet, pr, true, true));
                    DiagLine("pet_repro: " + (home ? "GoHome" : "VisitCommunityLot") + " pushed for " + pet.FullName + " from " + sPrFromName + " to " + target.Name
                        + ", " + (int)(pet.Position - target.Position).Length() + " m");
                });
                return;
            }
            throw new McpException("pet_repro " + (home ? "home" : "visit") + ": no owned NPC pet with a lot pair " + (int)min + "-" + (int)max + " m apart, both >= " + (int)away + " m from the camera and the active Sim");
        }
    }
}
