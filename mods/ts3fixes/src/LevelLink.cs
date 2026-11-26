using System;
using System.Collections.Generic;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.ObjectComponents;
using Sims3.SimIFace;
using Sims3.SimIFace.CAS;

namespace Ts3Fixes
{
    // The ways between the levels of a lot, read by ladderfootshoo (LadderFoot.cs, docs/183 §9). A level link is an object with a portal component whose lane ends lie on two levels
    // (Stairs, StaircaseSpiral, Ladder, FirefighterPole, ...) or that links levels through a second object (elevators);
    // it is found by its component, not by its class. All reads: the lane-end test is the game's own read-only slot test
    // (Route.IsSlotUnobstructed, what PortalComponent.IsLaneUnobstructed runs).
    public static partial class Fixes
    {
        sealed class LevelLink
        {
            public GameObject Obj;
            public PortalComponent Pc;
            public string Type;
            public uint Lanes;
            public bool Ends;  // lane 0 has two slots: A and Z are its ends, else the object's position
            public Vector3 A, Z;
            public LotLocation Al = LotLocation.Invalid, Zl = LotLocation.Invalid;
            public bool TwoLevels { get { return Al.mLevel != Zl.mLevel; } }
        }

        // Every level link on the lot; portals = objects with a portal component seen on the way.
        static List<LevelLink> LevelLinks(Lot lot, out int portals)
        {
            List<LevelLink> links = new List<LevelLink>();
            portals = 0;
            foreach (GameObject o in lot.GetObjects<GameObject>())
            {
                PortalComponent pc = o != null ? o.PortalComponent : null;
                if (pc == null) continue;
                portals++;
                LevelLink k = new LevelLink();
                k.Obj = o; k.Pc = pc; k.Type = o.GetType().Name;
                Slot[] s0 = null;
                try { k.Lanes = pc.GetNumLanes(); if (k.Lanes > 0) s0 = pc.GetSlotsForLaneIndex(0); } catch (Exception) { }
                k.Ends = s0 != null && s0.Length >= 2;
                k.A = k.Ends ? o.GetSlotPosition(s0[0]) : o.Position;
                k.Z = k.Ends ? o.GetSlotPosition(s0[1]) : o.Position;
                World.GetLotLocation(k.A, ref k.Al, eRoomDefinition.Default);
                World.GetLotLocation(k.Z, ref k.Zl, eRoomDefinition.Default);
                if (LadderFootCore.LinksLevels(k.Al.mLevel, k.Zl.mLevel, k.Type)) links.Add(k);
            }
            return links;
        }

        // Does the link lie on the way between the two levels (either direction)?
        static bool LevelLinkOnWay(LevelLink k, int simLevel, int goalLevel)
        {
            if (k.TwoLevels) return LadderFootCore.StairOnWay(k.Al.mLevel, k.Zl.mLevel, simLevel, goalLevel);
            return k.Al.mLevel >= Math.Min(simLevel, goalLevel) && k.Al.mLevel <= Math.Max(simLevel, goalLevel);
        }

        // The two slots of a lane, null when the component does not give them.
        static Slot[] LevelLinkLane(LevelLink k, uint lane)
        {
            Slot[] s = null;
            try { s = k.Pc.GetSlotsForLaneIndex(lane); } catch (Exception) { }
            return s != null && s.Length >= 2 ? s : null;
        }

        // The game's slot test at one lane end for this Sim: BlockedStatus as a number (0 = free, 1 = a Sim), -1 = it threw.
        // withSims false is what the script lane test of stairs and ladders asks; true also counts Sims standing there.
        static int LevelLinkSlot(Sim sim, GameObject link, Slot slot, bool withSims, bool exit)
        {
            try
            {
                Route.RouteOption opt = link is Stairs ? Route.RouteOption.PlatformsNotBlocking : Route.RouteOption.None;
                CASAgeGenderFlags ags = sim.SimDescription.AgeGenderSpecies;
                return (int)Route.IsSlotUnobstructed(sim.ObjectId, link.ObjectId, slot, withSims, opt, sim.ObjectId, ags, exit);
            }
            catch (Exception) { return -1; }
        }
    }
}
