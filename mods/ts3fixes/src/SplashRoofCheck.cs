using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Harness check for splashroof (SplashRoof.cs, docs/546 §16); test only, called through the agent's reflect_call. Works
    // on the active Sim's lot, wherever the active Sim stands.
    //   list    the lot's rain puddles: count, how many sit on a roof (SrRoofSpot), the first 12 with both level lookups,
    //           solid floor and EA's validity
    //   run     the whole case, deterministic: (1) a roof puddle: the first existing one, else a rain-puddle floor feature
    //           set on a roof tile above an indoor room of the lot (a tile one level above an object in a room, outside,
    //           with no solid floor); (2) one human NPC (IsNpcSim) placed directly in the room below that puddle with EA's
    //           AttemptToPutInSafeLocation sequence (as HiddenRoomCheck's trap; test shortcut); (3) EA's puddle choice of
    //           AutonomousSplashInPuddle.Run for that NPC, run here step by step (the same IsPuddleValid filter, distance
    //           sort and up to three PlanToPoint plans, inside SrBegin/SrEnd like the hooked Run), then EA's interaction is
    //           queued on the NPC for the natural path. Ends with "verdict: BROKEN|FIXED|NOT A BUG|N/A (...)".
    //           Off arm (switch off): BROKEN = a roof puddle was tried and its plan failed (EA's bug). On arm: FIXED = no
    //           roof puddle tried, 2384 rose, and the NPC picked a reachable puddle or none.
    //   clear   removes the puddle "run" added
    //   result  counters 2384-2386
    public static partial class Fixes
    {
        static ulong sSrAddLot;
        static LotLocation sSrAddLoc = LotLocation.Invalid;

        public static object SplashRoofCheck(string what)
        {
            if (what == "result")
                return "splashroof: roof puddles dropped 2384=" + PatchCounters()[2384] + ", NPC runs 2385=" + PatchCounters()[2385] + ", errors 2386=" + PatchCounters()[2386];
            if (what == "clear")
            {
                if (sSrAddLot == 0) return "splashroof: nothing added";
                SrWorld().SetFloorFeatures(sSrAddLot, sSrAddLoc, FloorFeatureFlags.RainPuddle, false, ushort.MaxValue);
                sSrAddLot = 0;
                return "splashroof: added puddle removed";
            }
            Sim sim = Sim.ActiveActor;
            Lot lot = sim != null ? sim.LotCurrent : null;
            if (lot == null || lot.IsWorldLot) return "splashroof check: no active Sim on a lot";
            if (what == "list") return SrList(lot);
            if (what == "run") return SrRun(lot);
            return "splashroof <list|run|clear|result>";
        }

        static IWorld SrWorld()
        {
            FieldInfo f = typeof(World).GetField("gWorld", BindingFlags.NonPublic | BindingFlags.Static);
            return f == null ? null : f.GetValue(null) as IWorld;
        }

        static string SrList(Lot lot)
        {
            List<Vector3> puddles = World.GetRainPuddles(lot.LotId);
            int roofs = 0;
            foreach (Vector3 p in puddles) if (SrRoofSpot(p)) roofs++;
            StringBuilder sb = new StringBuilder("splashroof list: " + puddles.Count + " rain puddles on " + lot.Name + " (0x" + lot.LotId.ToString("X") + "), " + roofs + " on a roof");
            for (int i = 0; i < puddles.Count && i < 12; i++) sb.Append("\n  " + SrDescribe(puddles[i]));
            return sb.ToString();
        }

        static string SrDescribe(Vector3 p)
        {
            LotLocation a = LotLocation.Invalid, f = LotLocation.Invalid;
            ulong la = World.GetLotLocation(p, ref a), lf = World.GetLotLocationAtFloor(p, ref f);
            return SrPos(p) + " lvl " + a.mLevel + "/" + f.mLevel + " room " + a.mRoom + " solid " + World.HasSolidFloor(la, a) + "/" + World.HasSolidFloor(lf, f)
                + " outdoor-valid " + Terrain.SplashInPuddle.IsPuddleValid(p, false, true) + " roof " + SrRoofSpot(p);
        }

        static string SrRun(Lot lot)
        {
            StringBuilder sb = new StringBuilder("splashroof run (switch " + (ShortcutOn("splashroof") ? "on" : "off") + ") on " + lot.Name + ":");
            // (1) a roof puddle
            Vector3 roof;
            string how = SrFindRoofPuddle(lot, out roof);
            sb.Append("\n  roof puddle: " + how);
            if (roof == Vector3.Invalid) return sb.Append("\nverdict: N/A (no roof puddle on the lot and none could be added)").ToString();
            // (2) an NPC in the room below it
            LotLocation below = LotLocation.Invalid;
            World.GetLotLocation(roof, ref below);
            below.mLevel--;
            Vector3 spot = Vector3.Invalid;
            World.GetWorldPosition(lot.LotId, below, ref spot);
            Sim npc = SrPickNpc(lot);
            if (npc == null) return sb.Append("\nverdict: N/A (no human NPC in the world)").ToString();
            SrPlace(npc, spot, below.mRoom);
            sb.Append("\n  npc: " + npc.FullName + " placed at " + SrPos(npc.Position) + " level " + npc.Level + " room " + npc.RoomId + " lot " + (npc.LotCurrent != null ? npc.LotCurrent.Name : "none") + " (test shortcut)");
            if (npc.LotCurrent != lot) return sb.Append("\nverdict: N/A (the NPC did not land on the lot)").ToString();
            // (3) EA's choice, step by step
            int c0 = PatchCounters()[2384], r0 = PatchCounters()[2385];
            bool roofTried = false, roofFailed = false;
            Vector3 picked = Vector3.OutOfWorld;
            int before;
            bool filtering = SrBegin(npc, out before);
            List<KeyValuePair<float, Vector3>> valid = new List<KeyValuePair<float, Vector3>>();
            try
            {
                foreach (Vector3 p in World.GetRainPuddles(lot.LotId))
                    if (Terrain.SplashInPuddle.IsPuddleValid(p, false, true)) valid.Add(new KeyValuePair<float, Vector3>((npc.Position - p).LengthSqr(), p));
            }
            finally { if (filtering) SrEnd(before); }
            valid.Sort((x, y) => x.Key.CompareTo(y.Key));
            sb.Append("\n  EA choice: " + valid.Count + " valid puddles");
            for (int i = 0; i < 3 && i < valid.Count; i++)
            {
                Vector3 p = valid[i].Value;
                RoutePlanResultType r = npc.CreateRoute().PlanToPoint(p).mType;
                bool isRoof = SrRoofSpot(p);
                if (isRoof) { roofTried = true; if (r != RoutePlanResultType.Succeeded) roofFailed = true; }
                sb.Append("\n    try " + (i + 1) + ": " + SrPos(p) + " (" + Math.Round(Math.Sqrt(valid[i].Key), 1) + " m) roof " + isRoof + " plan " + r);
                if (r == RoutePlanResultType.Succeeded) { picked = p; break; }
            }
            int dropped = PatchCounters()[2384] - c0, runs = PatchCounters()[2385] - r0;
            sb.Append("\n  picked: " + (picked == Vector3.OutOfWorld ? "none (EA's Run returns false)" : SrPos(picked) + " roof " + SrRoofSpot(picked))
                + "; counters 2384 +" + dropped + ", 2385 +" + runs + ", errors 2386=" + PatchCounters()[2386]);
            sb.Append("\n  EA interaction queued on the NPC: " + SrQueueSplash(npc));
            string verdict;
            if (!filtering)
                verdict = roofFailed ? "BROKEN (EA tried a roof puddle and its plan failed)"
                    : roofTried ? "NOT A BUG (the roof puddle planned fine)" : "N/A (EA did not try the roof puddle: other puddles nearer)";
            else
                verdict = roofTried ? "BROKEN (a roof puddle was still tried)"
                    : dropped > 0 ? "FIXED (roof puddle dropped before any plan; picked " + (picked == Vector3.OutOfWorld ? "none" : "a reachable puddle") + ")"
                    : "N/A (filter on but nothing dropped)";
            return sb.Append("\nverdict: " + verdict).ToString();
        }

        // An existing roof puddle on the lot, else a rain-puddle feature set on a roof tile above an indoor room: the tile
        // one level above an object standing in a room (room != 0), outside, without a solid floor. Vector3.Invalid = none.
        static string SrFindRoofPuddle(Lot lot, out Vector3 roof)
        {
            roof = Vector3.Invalid;
            foreach (Vector3 p in World.GetRainPuddles(lot.LotId))
                if (SrRoofSpot(p)) { roof = p; return "existing " + SrDescribe(p); }
            IWorld w = SrWorld();
            if (w == null) return "none existing; World.gWorld not found";
            int tiles = 0;
            foreach (GameObject o in lot.GetObjects<GameObject>())
            {
                LotLocation loc = LotLocation.Invalid;
                if (o.InInventory || World.GetLotLocation(o.Position, ref loc) != lot.LotId || loc.mRoom == 0) continue;
                loc.mLevel++;
                if (World.HasSolidFloor(lot.LotId, loc)) continue;
                Vector3 at = Vector3.Invalid;
                World.GetWorldPosition(lot.LotId, loc, ref at);
                if (!World.IsPositionOutside(at) || !SrRoofSpot(at)) continue;
                tiles++;
                w.SetFloorFeatures(lot.LotId, loc, FloorFeatureFlags.RainPuddle, true, ushort.MaxValue);
                foreach (Vector3 p in World.GetRainPuddles(lot.LotId))
                    if (Math.Abs(p.x - at.x) < 0.75f && Math.Abs(p.z - at.z) < 0.75f && SrRoofSpot(p))
                    {
                        sSrAddLot = lot.LotId; sSrAddLoc = loc; roof = p;
                        return "added over " + o.GetType().Name + ": " + SrDescribe(p);
                    }
                w.SetFloorFeatures(lot.LotId, loc, FloorFeatureFlags.RainPuddle, false, ushort.MaxValue);
                if (tiles >= 3) break;
            }
            return "none existing; " + (tiles == 0 ? "no roof tile above an indoor room found" : tiles + " roof tiles tried, GetRainPuddles never listed the added feature");
        }

        // A human NPC (not toddler or younger, no service role), one already on the lot first.
        static Sim SrPickNpc(Lot lot)
        {
            Sim npc = null;
            foreach (Sim s in LotManager.Actors)
            {
                if (s == null || s.HasBeenDestroyed || s.SimDescription == null || !s.InWorld || s.Parent != null || s.SimInRabbitHolePosture) continue;
                if (!IsNpcSim(s) || !s.IsHuman || s.SimDescription.ToddlerOrBelow || s.Service != null) continue;
                if (npc == null || (s.LotCurrent == lot && npc.LotCurrent != lot)) npc = s;
            }
            return npc;
        }

        // Direct placement in the room (EA's AttemptToPutInSafeLocation sequence: SetPosition, SetForward, out of and back
        // into the world, footprint), which moves LotCurrent and RoomId at once; a queued teleport would wait behind the
        // NPC's interaction. Same sequence as HiddenRoomCheck's trap.
        static void SrPlace(Sim npc, Vector3 spot, int room)
        {
            Vector3 fwd = npc.ForwardVector;
            World.FindGoodLocationParams fgl = new World.FindGoodLocationParams(spot, FindGoodLocationBooleans.StayInRoom | FindGoodLocationBooleans.PreferEmptyTiles);
            if (!GlobalFunctions.FindGoodLocation(npc, fgl, out spot, out fwd)) fwd = npc.ForwardVector;
            npc.InteractionQueue.CancelAllInteractions();
            if (npc.Posture != npc.Standing) npc.Posture = npc.Standing;
            npc.SetPosition(spot);
            npc.SetForward(fwd);
            npc.RemoveFromWorld();
            npc.AddToWorld();
            npc.SimRoutingComponent.ForceUpdateDynamicFootprint();
        }

        static string SrQueueSplash(Sim npc)
        {
            Type t = typeof(Sim).GetNestedType("AutonomousSplashInPuddle", BindingFlags.Public | BindingFlags.NonPublic);
            FieldInfo f = t == null ? null : t.GetField("Singleton", BindingFlags.Public | BindingFlags.Static);
            ISoloInteractionDefinition def = f == null ? null : f.GetValue(null) as ISoloInteractionDefinition;
            if (def == null) return "AutonomousSplashInPuddle.Singleton not found";
            InteractionInstance ii = def.CreateInstance(npc, npc, new InteractionPriority(InteractionPriorityLevel.High), true, true);
            return npc.InteractionQueue.AddNext(ii) ? "yes (routefail job lines name AutonomousSplashInPuddle)" : "refused by the queue";
        }

        static string SrPos(Vector3 p) { return p.x.ToString("F1") + "," + p.y.ToString("F1") + "," + p.z.ToString("F1"); }
    }
}
