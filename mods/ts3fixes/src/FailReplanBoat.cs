using System;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects.Vehicles;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // docs/851 §8 failreplanboat (default off). A Sim seated in a boat plans every route with the boat as follower
    // (SimRoutingComponent: Parent is IBoat). When such a plan fails, SimRoutingComponent.DoRoute runs
    // DoRouteFailureBehavior, whose GetRouteFailPath plans the same route again toward the blocker (a radial goal 1-3 m
    // around it, or the blocker's spot) so the Sim can go there and play the route-fail animation. From a boat on open water
    // that plan is the slow one: about 90 closest-waypoint searches that each test every waypoint of up to 36 chunks and
    // find none the boat can reach (s347: 18 plans of 1.2-3.6 s for one stranded NPC paddle boat in a minute, the route
    // worker holding the routing lock for each). failreplan already skips the fail path for an unseen townie, but only one
    // that stands (TownieUnseen); a Sim seated in a boat never passes it.
    // This switch: after a failed plan whose follower is a Boat with a driver, when the driver is unseen by native's
    // KindDeadlines rule (UfWhy kHeld = every camera test passed, UfVehicleNpcOnly = held only by the boat, no household or
    // selectable occupant), the route's DoRouteFail flag is cleared. That is EA's own flag for "no route-fail show";
    // DoRouteFailureBehavior tests it first. The plan's result is untouched: the caller still gets the failure and the
    // RouteFailed exit reason. Household, selected, unknown-scope and seen riders, driverless boats and routes whose flag is
    // off already (EA's fail-path plan clears it itself) are left alone and counted.
    // The flag lives on the route object: when the route cleared last is planned again and succeeds, the flag is set back
    // (one weak reference; a route that failed is normally dropped by its caller).
    // Counters [4320-4327], see Fixes.cs; log lines "failreplanboat: ..." (10 a minute).
    public static partial class Fixes
    {
        static int sFrbMinute, sFrbLogged;
        static WeakReference sFrbCleared;  // the route whose DoRouteFail this fix cleared last
        static void FrbLog(string s)
        {
            int minute = System.Environment.TickCount / 60000;
            if (minute != sFrbMinute) { sFrbMinute = minute; sFrbLogged = 0; }
            if (sFrbLogged++ < 10) { PatchCounters()[4326]++; DiagLine("failreplanboat: " + s); }
        }

        public partial class RouteBackoffHook
        {
            // after every plan (UfPlan); ms = the plan's wall time
            void FrbPost(RoutePlanResult r, int ms)
            {
                if (r.mType == RoutePlanResultType.Succeeded)
                {
                    WeakReference w = sFrbCleared;
                    if (w != null && ReferenceEquals(w.Target, this)) { sFrbCleared = null; DoRouteFail = true; PatchCounters()[4327]++; }
                    return;
                }
                int[] c = PatchCounters();
                try
                {
                    IScriptProxy f = Follower;
                    Boat boat = f != null ? f.Target as Boat : null;
                    if (boat == null) return;
                    Sim rider = boat.Driver;
                    bool flag = DoRouteFail;
                    int why = -1;
                    bool npcOnly = false;
                    if (rider != null && flag) { why = SafeUfWhy(rider); npcOnly = why == UnseenFallbackCore.kHeld && UfVehicleNpcOnly(rider); }
                    int v = FailReplanBoatCore.Decide(false, true, rider != null, flag, why == UnseenFallbackCore.kHeld, npcOnly, ShortcutOn("failreplanboat"));
                    c[4320]++;
                    string who = boat.GetType().Name + " " + (rider != null ? rider.FullName : "(no driver)") + ", " + r.mType + " in " + ms + " ms, blocker "
                        + r.mBlockerType + (r.mBlockerObjectId != 0 ? " 0x" + r.mBlockerObjectId.ToString("X") : "");
                    switch (v)
                    {
                        case FailReplanBoatCore.kOff:
                            DoRouteFail = false;
                            sFrbCleared = new WeakReference(this);
                            c[4323]++;
                            FrbLog(who + ": route-fail behaviour off (#" + c[4323] + ")");
                            break;
                        case FailReplanBoatCore.kWould:
                            c[4322]++;
                            FrbLog(who + ": would be off (switch off, #" + c[4322] + ")");
                            break;
                        case FailReplanBoatCore.kFlagOff:
                            c[4324]++;  // EA clears the flag after its first fail-path plan, so with the switch off this is mostly the second one
                            FrbLog(who + ": DoRouteFail off already (a fail-path plan, or the caller's choice)");
                            break;
                        default:
                            c[4321]++;
                            FrbLog(who + ": kept (" + (rider == null ? "no driver" : "why " + why + (why == UnseenFallbackCore.kHeld ? ", household or selectable aboard, or riding / carrying" : "")) + ")");
                            break;
                    }
                }
                catch (Exception) { c[4325]++; }
            }
        }
    }
}
