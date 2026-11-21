using System;
using System.Collections.Generic;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Objects.Vehicles;
using Sims3.SimIFace;

namespace Ts3Agent
{
    // Repro trigger for docs/257 (ts3fixes boatparkgone): the EA NullReferenceException in BoatOwnable.Park fires when an NPC's
    // per-trip boat (GeneratedOwnableForNpc) is destroyed while its driver is still on the trip, then OnRouteFinished parks it.
    // The natural destroyer is likely NRaas Overwatch CleanupVehicles (UnParent + Destroy, docs/257 §4); this watch does the same
    // to a driving NPC so the case comes on demand. Household rule: only a Sim of a known, non-active household that is not
    // selectable or selected; anything else (no household = unknown scope) is skipped and counted.
    public static partial class Handlers
    {
        static void RegisterBoatGone()
        {
            Dispatcher.Register("boat_gone_watch", BoatGoneWatch);
        }

        static int sBgGen, sBgSamples, sBgSkippedScope;
        static List<object> sBgRows = new List<object>();
        static readonly Dictionary<ulong, int> sBgSeen = new Dictionary<ulong, int>();  // boat object id -> samples seen driven

        // boat_gone_watch {start: true, when?: "getout" | "route", after?: 6, max?: 3} | {stop: true} | {result: true, min?: 1, require?: true}:
        // a game task samples every tick; a non-household NPC driving its generated boat (SittingInBoat, Driver) gets the boat
        // destroyed as Overwatch does, at most `max` boats per start. when "route": after `after` samples of routing (run 1
        // 2026-11-11: 3 boats destroyed mid-route, 0 Park errors: the route ends without OnRouteFinished). when "getout" (default):
        // in Boat.GetOut's fade yield, the only yield between the route's end and OnRouteFinished's Park: the driver still sits in
        // the boat as Driver but GetOut has cleared its RoutingParent, which was the boat during the trip.
        // {result, require} throws unless at least `min` boats were destroyed (a run where no NPC took a boat proves nothing).
        static object BoatGoneWatch(Args a)
        {
            RequireLive();
            Dictionary<string, object> r = Util.Obj();
            if (a.Has("start"))
            {
                int gen = ++sBgGen, after = (int)a.Long("after", 6), max = (int)a.Long("max", 3);
                bool getout = a.Str("when", "getout") != "route";
                sBgSamples = sBgSkippedScope = 0;
                sBgRows = new List<object>();
                sBgSeen.Clear();
                Simulator.AddObject(new Sims3.Gameplay.OneShotFunctionTask(delegate
                {
                    while (gen == sBgGen && sBgRows.Count < max)
                    {
                        try { BoatGoneSample(after, getout); } catch (Exception) { }
                        Simulator.Sleep(1);
                    }
                }));
                r["started"] = true;
                return r;
            }
            if (a.Has("stop")) { sBgGen++; r["stopped"] = true; }
            r["samples"] = sBgSamples;
            r["destroyed"] = sBgRows.Count;
            r["skipped_scope"] = sBgSkippedScope;
            r["rows"] = sBgRows;
            int min = (int)a.Long("min", 1);
            r["check_ok"] = sBgRows.Count >= min;
            if (a.Bool("require", false) && sBgRows.Count < min)
                throw new McpException("boat_gone_watch: not exercised (" + sBgRows.Count + " boats destroyed < " + min + ", samples " + sBgSamples + ", scope skips " + sBgSkippedScope + ")");
            return r;
        }

        // household rule: false for selectable, selected, active-household or household-less (unknown scope) Sims
        static bool BoatGoneNpc(Sim s)
        {
            return s != null && !s.IsSelectable && s != PlumbBob.SelectedActor && s.Household != null && s.Household != Household.ActiveHousehold;
        }

        static void BoatGoneSample(int after, bool getout)
        {
            sBgSamples++;
            if (LotManager.Actors == null) return;
            foreach (Sim s in new List<Sim>(LotManager.Actors))
            {
                SittingInBoat seat = s != null ? s.Posture as SittingInBoat : null;
                BoatOwnable boat = seat != null ? seat.Container as BoatOwnable : null;
                if (boat == null || boat.HasBeenDestroyed || !boat.GeneratedOwnableForNpc || boat.Driver != s) continue;
                if (s.SimRoutingComponent == null) continue;
                if (!BoatGoneNpc(s)) { sBgSkippedScope++; continue; }
                ulong id = boat.ObjectId.Value;
                int n;
                sBgSeen.TryGetValue(id, out n);
                if (getout)
                {
                    if (s.SimRoutingComponent.RoutingParent == boat) { sBgSeen[id] = 1; continue; }  // on the trip: wait for GetOut
                    if (n == 0) continue;  // never seen on a trip (getting in): not the window
                }
                else
                {
                    if (!s.SimRoutingComponent.IsRouting) continue;
                    sBgSeen[id] = ++n;
                    if (n < after) continue;
                }
                Dictionary<string, object> o = Util.Obj();
                o["sim"] = s.FullName;
                o["household"] = s.Household.Name;
                o["boat"] = boat.GetType().Name;
                Vector3 p = boat.Position;
                o["x"] = Math.Round(p.x, 1); o["z"] = Math.Round(p.z, 1);
                o["when"] = getout ? "getout" : "route";
                o["interaction"] = s.CurrentInteraction != null ? s.CurrentInteraction.GetType().FullName : null;
                boat.UnParent();
                boat.Destroy();
                o["gone"] = boat.HasBeenDestroyed;
                sBgRows.Add(o);
                DiagLine("boat_gone_watch: destroyed " + o["boat"] + " of " + o["sim"] + " (" + o["household"] + ") at " + o["x"] + "," + o["z"]);
                return;  // one per sample
            }
        }
    }
}
