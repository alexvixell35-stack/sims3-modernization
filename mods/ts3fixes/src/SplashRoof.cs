using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // splashroof (tuning kSplashRoof, off by default; docs/546 §16). EA bug fix for NPCs only; household Sims run EA's code.
    //
    // Sim+AutonomousSplashInPuddle.Run (autonomy's "splash in a rain puddle") takes every rain puddle on the actor's lot
    // (World.GetRainPuddles), keeps the ones Terrain.SplashInPuddle.IsPuddleValid(p, checkPuddleExists: false,
    // outdoorsOnly: true) accepts, sorts them by distance and plans a route to the nearest three in turn, one plan each,
    // until one succeeds [verified, decompiled]. IsPuddleValid asks only "outdoors" and "no object on the tile": it has no
    // floor test, while EA's own puddle manager (PuddleManager, mopping) requires World.HasSolidFloor [verified].
    // Measured (docs/546 §16): all 32 failed splash plans in the 2026-11-14 runs (5 runs, Bridgeport and Appaloosa Plains,
    // household Sims) aimed exactly 3.0 m above the Sim, often straight over it, while the Sim stood in an indoor room, and
    // every one ended FailedBecauseNoDestinationSpecified: rain puddles on the roof above a room [inferred from the
    // positions; SplashRoofCheck "list" prints level, floor and plan result per puddle to confirm in game]. Because the
    // definition's Test only asks the native "any valid rain puddle on the lot", autonomy offers the splash again after
    // every failure while it rains.
    //
    // The fix, NPC actors only (IsNpcSim): while their Run runs, IsPuddleValid also refuses a puddle that sits above the
    // ground floor on a tile with no solid floor (SrRoofSpot), so the three tries go to puddles a Sim can stand on, and a
    // lot with only roof puddles costs no plan at all (Run returns false as before). Household Sims, the selected Sim and
    // Sims of unknown scope run EA's Run unchanged, so their results equal vanilla. Other IsPuddleValid callers (the
    // player's pie-menu splash, pets' puddle interactions) pass through: the hook acts only inside a hooked Run's own task.
    // Counters: [2384] roof puddles dropped from NPC runs, [2385] NPC runs that dropped one, [2386] errors (registry
    // fixes-2384). Harness check: SplashRoofCheck (SplashRoofCheck.cs), repro splash-roof-puddle.
    public static partial class Fixes
    {
        static void ApplySplashRoof()
        {
            Type ia = typeof(Sim).GetNestedType("AutonomousSplashInPuddle", BindingFlags.Public | BindingFlags.NonPublic);
            if (ia == null) { AppliedLog().Add("splashroof: Sim+AutonomousSplashInPuddle not found"); return; }
            Patch(ia, "Run", typeof(SplashRoofRunHook), "RunHook", "RunOrig");
            Patch(typeof(Terrain.SplashInPuddle), "IsPuddleValid", new Type[] { typeof(Vector3), typeof(bool), typeof(bool) },
                typeof(SplashRoofValidHook), "ValidHook", "ValidOrig");
        }

        // tasks of the NPC splash runs now inside RunOrig (sim thread only). A run whose task is killed while it waits on
        // its plan never reaches finally: the list is capped so such leftovers cannot pile up.
        static readonly List<ulong> sSrTasks = new List<ulong>();
        const int kSrTasksMax = 16;

        public class SplashRoofRunHook
        {
            public bool RunHook()
            {
                Sim actor = null;
                try
                {
                    if (ShortcutOn("splashroof")) actor = ((InteractionInstance)(object)this).InstanceActor as Sim;
                }
                catch (Exception) { PatchCounters()[2386]++; }
                int before;
                if (!SrBegin(actor, out before)) return RunOrig();
                try { return RunOrig(); }
                finally { SrEnd(before); }
            }
            public bool RunOrig() { return false; } // receives Sim+AutonomousSplashInPuddle.Run's original body
        }

        // Starts the roof filter for a splash run of actor in the current task: false (nothing started) unless the switch
        // is on and actor is an NPC. Every true is paired with SrEnd(before) in a finally. Also used by SplashRoofCheck.
        internal static bool SrBegin(Sim actor, out int before)
        {
            before = PatchCounters()[2384];
            if (actor == null || !ShortcutOn("splashroof") || !IsNpcSim(actor)) return false;
            if (sSrTasks.Count >= kSrTasksMax) sSrTasks.RemoveAt(0);
            sSrTasks.Add(Simulator.CurrentTask.Value);
            return true;
        }

        internal static void SrEnd(int before)
        {
            sSrTasks.Remove(Simulator.CurrentTask.Value);
            if (PatchCounters()[2384] != before) PatchCounters()[2385]++;
        }

        public class SplashRoofValidHook
        {
            public static bool ValidHook(Vector3 worldPos, bool checkPuddleExists, bool outdoorsOnly)
            {
                bool ok = ValidOrig(worldPos, checkPuddleExists, outdoorsOnly);
                if (!ok || sSrTasks.Count == 0 || OnWorker()) return ok;
                try
                {
                    if (!sSrTasks.Contains(Simulator.CurrentTask.Value) || !SrRoofSpot(worldPos)) return true;
                    PatchCounters()[2384]++;
                    return false;
                }
                catch (Exception) { PatchCounters()[2386]++; return true; }
            }
            public static bool ValidOrig(Vector3 worldPos, bool checkPuddleExists, bool outdoorsOnly) { return false; } // receives Terrain.SplashInPuddle.IsPuddleValid(Vector3, bool, bool)'s original body
        }

        // A spot on a lot above the ground floor with no solid floor tile under it (a roof). Both of EA's lookups are asked
        // (IsPuddleValid's GetLotLocation and the puddle interactions' GetLotLocationAtFloor): it is a roof spot when either
        // names an upper level without a floor there. World lot and level 0 (terrain, foundations) are never roof spots.
        internal static bool SrRoofSpot(Vector3 p)
        {
            LotLocation a = LotLocation.Invalid, f = LotLocation.Invalid;
            ulong lotA = World.GetLotLocation(p, ref a), lotF = World.GetLotLocationAtFloor(p, ref f);
            return SrNoFloor(lotA, a) || SrNoFloor(lotF, f);
        }

        static bool SrNoFloor(ulong lot, LotLocation loc)
        {
            return lot != 0 && lot != ulong.MaxValue && loc.mLevel > 0 && !World.HasSolidFloor(lot, loc);
        }
    }
}
