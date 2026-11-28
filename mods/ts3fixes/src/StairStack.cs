using System;
using System.Collections.Generic;
using System.Text;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.ObjectComponents;
using Sims3.SimIFace;
using Sims3.SimIFace.CAS;

namespace Ts3Fixes
{
    // StairStackCheck (read-only diag, docs/stairstack): stacked stairs (a basement flight under a ground-floor flight).
    //   "lot"  every level link on the active Sim's lot: object level, both lane-0 ends (entry and exit slot positions),
    //          the level and room the game reads at each end, and each pair of links whose ends lie within 1.5 m flat.
    //   "plan" the active Sim plans (no walk) to a point 1 m outside each end of each stair; per plan the result and each
    //          path's kind, portal object and start -> end (with height), so a down-and-up plan shows as two portals.
    public static partial class Fixes
    {
        static string SsAt(Vector3 v) { return v.x.ToString("0.00") + "," + v.y.ToString("0.00") + "," + v.z.ToString("0.00"); }

        static string SsId(ObjectGuid g) { return (g.Value & 0xFFFF).ToString("x4"); }

        public static object StairStackCheck(string what)
        {
            StringBuilder b = new StringBuilder();
            try
            {
                Sim sim = PlumbBob.SelectedActor;
                Lot lot = sim != null ? sim.LotCurrent : null;
                if (lot == null) return "stairstack: no selected Sim on a lot";
                int portals;
                List<LevelLink> links = LevelLinks(lot, out portals);
                b.Append("stairstack: lot ").Append(lot.Name).Append(" links ").Append(links.Count).Append(" portals ").Append(portals)
                 .Append(" sim ").Append(SsAt(sim.Position)).Append(" level ").Append(sim.Level).Append('\n');
                CASAgeGenderFlags ags = sim.SimDescription.AgeGenderSpecies;
                foreach (LevelLink k in links)
                {
                    GameObject o = k.Obj;
                    b.Append(" ").Append(k.Type).Append(' ').Append(SsId(o.ObjectId)).Append(" lvl ").Append(o.Level)
                     .Append(" pos ").Append(SsAt(o.Position)).Append(" fwd ").Append(SsAt(o.ForwardVector)).Append(" lanes ").Append(k.Lanes).Append('\n');
                    Slot[] s = LevelLinkLane(k, 0);
                    if (s == null) continue;
                    for (int e = 0; e < 2; e++)
                    {
                        Vector3 pin = o.GetPositionOfSlot(s[e], ags, false), pout = o.GetPositionOfSlot(s[e], ags, true);
                        LotLocation ld = LotLocation.Invalid, lf = LotLocation.Invalid;
                        World.GetLotLocation(pin, ref ld, eRoomDefinition.Default);
                        World.GetLotLocationAtFloor(pin, ref lf, eRoomDefinition.PlacementBlocking);
                        int room = -99;
                        try { room = o.GetRoomIdOfSlot(s[e]); } catch (Exception) { }
                        b.Append("   ").Append(e == 0 ? "bottom" : "top").Append(" in ").Append(SsAt(pin)).Append(" out ").Append(SsAt(pout))
                         .Append(" loc ").Append(ld.mX).Append(',').Append(ld.mZ).Append(" L").Append(ld.mLevel).Append(" room ").Append(ld.mRoom)
                         .Append(" atFloor L").Append(lf.mLevel).Append(" room ").Append(lf.mRoom).Append(" slotRoom ").Append(room)
                         .Append(" free ").Append(LevelLinkSlot(sim, o, s[e], false, e == 1)).Append('\n');
                    }
                    Vector3 mn = Vector3.Zero, mx = Vector3.Zero;
                    if (o is Stairs) { try { World.StairsGetFloorCoverExtents(o.ObjectId, out mn, out mx); b.Append("   cover ").Append(SsAt(mn)).Append(" .. ").Append(SsAt(mx)).Append('\n'); } catch (Exception) { } }
                }
                for (int i = 0; i < links.Count; i++)
                    for (int j = i + 1; j < links.Count; j++)
                    {
                        LevelLink p = links[i], q = links[j];
                        Vector3[] pe = { p.A, p.Z }, qe = { q.A, q.Z };
                        for (int x = 0; x < 2; x++)
                            for (int y = 0; y < 2; y++)
                            {
                                float d = BuFlat(pe[x], qe[y]);
                                if (d <= 1.5f)
                                    b.Append(" near: ").Append(SsId(p.Obj.ObjectId)).Append(x == 0 ? " bottom" : " top").Append(" ~ ")
                                     .Append(SsId(q.Obj.ObjectId)).Append(y == 0 ? " bottom" : " top").Append(" flat ").Append(d.ToString("0.00"))
                                     .Append(" dy ").Append((qe[y].y - pe[x].y).ToString("0.00")).Append('\n');
                            }
                    }
                if (what == "plan")
                    foreach (LevelLink k in links)
                    {
                        if (!(k.Obj is Stairs)) continue;
                        Slot[] s = LevelLinkLane(k, 0);
                        if (s == null) continue;
                        for (int e = 0; e < 2; e++)
                        {
                            Vector3 p = k.Obj.GetPositionOfSlot(s[e], ags, true), f = k.Obj.GetForwardOfSlot(s[e]);
                            Vector3 goal = p - f * 1f;
                            Route r = sim.CreateRoute();
                            RoutePlanResult res = r.PlanToPoint(goal);
                            b.Append(" plan to ").Append(SsId(k.Obj.ObjectId)).Append(e == 0 ? " bottom" : " top").Append(" goal ").Append(SsAt(goal))
                             .Append(": ").Append(res.mType).Append(" paths ").Append(r.GetNumPaths()).Append('\n');
                            for (uint n = 0; n < r.GetNumPaths() && n < 12; n++)
                            {
                                PathData pd = r.GetPathData(n);
                                Vector3 a0 = Vector3.Zero, a1 = Vector3.Zero;
                                r.GetPathStartPoint(n, ref a0);
                                r.GetPathEndPoint(n, ref a1);
                                b.Append("   ").Append(n).Append(' ').Append(pd.PathType).Append(pd.ObjectId.Value != 0 ? " obj " + SsId(pd.ObjectId) : "")
                                 .Append(' ').Append(SsAt(a0)).Append(" -> ").Append(SsAt(a1)).Append(" len ").Append(r.GetPathLength(n).ToString("0.0")).Append('\n');
                            }
                            
                        }
                    }
            }
            catch (Exception e) { b.Append("stairstack: error ").Append(e.GetType().Name).Append(": ").Append(e.Message); }
            string t = b.ToString();
            foreach (string line in t.Split('\n')) if (line.Length > 0) DiagLine(line);
            return t;
        }
    }
}
