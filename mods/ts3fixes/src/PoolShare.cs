using System;
using Sims3.Gameplay.Pools;

namespace Ts3Fixes
{
    // Pool ladders shared in both directions (docs/686 §35). Default off; the search is sharelanes' (ShareLanes.cs
    // ShlLaneHook.FreeLaneHook), with its own switch pair and counters.
    //
    // [verified, decompiled PoolPortalComponent / PoolLadder / PortalComponent.GetFreeRoutingLane] A pool ladder is a
    // portal of one lane per ladder (outside slot, inside slot). The game's lane table gives the lane to a second Sim
    // entering at the same end and refuses a Sim coming the other way; that Sim walks to a waiting spot and asks again
    // every quarter sim-minute. The climb itself is one state machine per Sim (PoolLadder.GetStateMachine acquires it on
    // the Sim, the ladder is only an actor; EA already runs two at once for Sims entering at the same end), and the
    // footprint the climb turns on is the climber's own end of the lane. Storm logs s532-s560: 7 NPC pool-ladder waits
    // that gave up, 14 sim-min (NPCs routing home across a pool, a follower behind its leader).
    //
    // poolshare (NPCs) / poolsharehh (household and selected Sims too)
    //   Fix: a pool ladder lane refused only because a Sim holds it from the other end is searched again with the game's
    //   own steps (nearest start slot first, the pool's extra checks and obstruction test, its fallback lane), as
    //   sharelanes does for stairs, doors and ladders: the swimmer climbs while the other climbs the other way (the two
    //   may clip at the ladder; a player's rule allows clipping at portals). The pool's own edge portal and fountains keep
    //   the game's table. Counters 4716-4720. Check: PoolShareCheck.
    public static partial class Fixes
    {
        const int kPlsFails = 4716, kPlsSharable = 4717, kPlsShared = 4718, kPlsWould = 4719, kPlsExempt = 4720;

        static string PlsCounters()
        {
            int[] c = PatchCounters();
            return "poolshare: pool ladder refusals " + c[kPlsFails] + ", held from the other end " + c[kPlsSharable] + ", shared " + c[kPlsShared]
                + ", would, off " + c[kPlsWould] + ", household exempt " + c[kPlsExempt];
        }

        // PoolShareCheck result: counters. run: the table rule with controls (read-only). lot: the pool ladders of the
        // selected Sim's lot with their lane holders.
        public static object PoolShareCheck(string what)
        {
            try
            {
                if (what == "run")
                {
                    ShareLanesCore.Holder[] h = new ShareLanesCore.Holder[1];
                    h[0].Lane = 0; h[0].StartSlot = 11; h[0].InScope = true;
                    bool opposite = ShareLanesCore.Lane(h, 1, 0, 22) == ShareLanesCore.kLaneOpposite;
                    bool eaRefuses = !ShareLanesCore.EaFree(h, 1, 0, 22) && ShareLanesCore.EaFree(h, 1, 0, 11);
                    bool on = ShortcutOn("poolshare");
                    bool fix = ShareLanesCore.Decide(true, true, true, true) == ShareLanesCore.kGo;
                    bool off = ShareLanesCore.Decide(false, true, true, true) == ShareLanesCore.kWould;
                    bool ctl = opposite && eaRefuses && ShareLanesCore.Decide(true, false, true, true) == ShareLanesCore.kExempt
                        && ShareLanesCore.kKindNames[ShareLanesCore.kPoolLadder] == "pool ladder";
                    return "PoolShareCheck run: verdict " + (on ? (fix ? "FIXED" : "BROKEN") : (off ? "BROKEN" : "?")) + ", controls ok=" + ctl + "; " + PlsCounters();
                }
                if (what == "lot")
                {
                    Sims3.Gameplay.Actors.Sim a = Sims3.Gameplay.Actors.Sim.ActiveActor;
                    if (a == null || a.LotCurrent == null) return "PoolShareCheck lot: no selected Sim on a lot";
                    string r = "";
                    foreach (PoolLadder l in a.LotCurrent.GetObjects<PoolLadder>())
                    {
                        int n; string names;
                        ShlHolders(l.PortalComponent, true, out n, out names);
                        r += " 0x" + l.ObjectId.Value.ToString("X") + " at " + V(l.Position) + ": " + (n > 0 ? names : "free") + ";";
                    }
                    return "PoolShareCheck lot:" + (r.Length > 0 ? r : " no pool ladder") + " " + PlsCounters();
                }
                return PlsCounters();
            }
            catch (Exception e) { return "PoolShareCheck: " + e.GetType().Name + ": " + e.Message; }
        }
    }
}
