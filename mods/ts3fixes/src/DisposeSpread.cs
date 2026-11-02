using System;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects.Vehicles;
using Sims3.Gameplay.UI;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // "disposespread" (docs/74 §12, default off): world-object clean-up bursts spread over ticks. GameObject.Destroy only
    // queues the native destroy; the expensive part is the ScriptProxy.Dispose task the engine runs for it afterwards (~100 ms
    // for a world-lot object, docs/74 §11 a). When several Sims leave the world or several traffic cars despawn in one tick,
    // those Disposes land in one tick (BP 148 + 139 ms, SV 497 ms). Three burst callers queue their object instead of calling
    // Destroy, and our one task destroys kDisposeSpreadPerTick per tick, oldest first:
    //   1. GameObject.DestroyObjectOnFadeOutCallBack (FadeOut(.., destroyObject: true)) for a Sim only: townies leaving through
    //      Sim.GoToVirtualHome(Internal), alarm fades of NPCs. The Sim is at opacity 0 when it runs; every caller sleeps
    //      forever or returns after it (none reads the Sim again).
    //   2. Sim.DestroyMyself.Run: service Sims whose assignment ended (Service.OnAssignmentFinished), already out of the world.
    //   3. CarNpcWithNoDriver.DriveOffscreenAndRelease: a traffic car at the end of its drive, out of the world after Release.
    // Only non-active-household, non-selectable objects that are out of the world, or off screen and FarFromViewers. Anything
    // else, a full queue, or a non-live game state takes the game's path. The queue is destroyed at once at a save
    // (OptionsModel.SaveGame hook and a per-tick check), travel and build/buy; dropped at world unload (the world destroys
    // everything then). Counters [280-289] (registry).
    public static partial class Fixes
    {
        const int kDisposeSpreadPerTick = 1;  // Destroys per tick: the Dispose cost lands after our slice, so a count, not a time budget
        const int kDisposeSpreadMax = 16;     // queue cap: more waiting = the game's immediate Destroy
        static readonly SliceQueue<GameObject> sDisposeQueue = new SliceQueue<GameObject>();
        static ObjectGuid sDisposeTask = ObjectGuid.InvalidObjectGuid;
        static StopWatch sDisposeWatch;

        static void ApplyDisposeSpread()
        {
            Patch(typeof(GameObject), "DestroyObjectOnFadeOutCallBack", Type.EmptyTypes, typeof(FadeDestroyHook), "CallbackHook", "CallbackOrig");
            Patch(typeof(Sim.DestroyMyself), "Run", Type.EmptyTypes, typeof(DestroyMyselfHook), "RunHook", "RunOrig");
            Patch(typeof(CarNpcWithNoDriver), "DriveOffscreenAndRelease", Type.EmptyTypes, typeof(TrafficReleaseHook), "ReleaseHook", "ReleaseOrig");
            Patch(typeof(OptionsModel), "SaveGame", new Type[] { typeof(bool), typeof(bool), typeof(bool) }, typeof(DisposeSaveHook), "SaveHook", "SaveOrig");
        }

        // 0 run, 1 destroy everything now (save, travel, build/buy or any other non-live state), 2 drop (world going away)
        static int DisposeSpreadState()
        {
            if (WorldGone()) return 2;
            if (SaveInProgress() || GameStates.IsTravelling || !GameStates.IsLiveState) return 1;
            return 0;
        }

        // Not visible and not near anything the player watches: out of the world, or off screen and FarFromViewers.
        static bool DisposeSpreadUnseen(GameObject o)
        {
            long t = UcBegin(kUcDispose, o is Sim && !o.HasBeenDestroyed ? o.ObjectId.Value : 0);  // docs/227 probe
            bool r = DisposeSpreadUnseenBody(o);
            UcEnd(kUcDispose, t);
            return r;
        }
        static bool DisposeSpreadUnseenBody(GameObject o)
        {
            if (o == null || o.HasBeenDestroyed) return false;
            Sim s = o as Sim;
            if (s != null && (s.IsSelectable || s.Household == Household.ActiveHousehold)) return false;
            if (!o.InWorld) return true;
            int b = s != null ? UtbBits(s) : -1;  // docs/227 §6 central table (Sims only; objects keep the old rule)
            if (b >= 0) return sUtbCheck ? UtbCompare(UtbUnseen(b), !UcOnScreen(o.ObjectId) && FarFromViewers(o.Position)) : UtbUnseen(b);
            return !UcOnScreen(o.ObjectId) && FarFromViewers(o.Position);
        }

        // True when queued; false = the caller destroys now (the game's path).
        static bool DisposeSpreadDefer(GameObject o)
        {
            if (!ShortcutOn("disposespread")) return false;
            int[] c = PatchCounters();
            if (DisposeSpreadState() != 0 || !DisposeSpreadUnseen(o)) { c[283]++; return false; }
            if (!TaskAlive(sDisposeTask))
            {
                c[288] += sDisposeQueue.Clear(); // left over from a task the world unload killed
                sDisposeTask = Simulator.AddObject(new LoopTask(DisposeSpreadDrain));
            }
            if (sDisposeQueue.Count >= kDisposeSpreadMax) { c[287]++; return false; }
            sDisposeQueue.Add(o, Simulator.TicksElapsed());
            c[280]++;
            if (sDisposeQueue.MaxDepth > c[284]) c[284] = sDisposeQueue.MaxDepth;
            return true;
        }

        static void DisposeSpreadDrain()
        {
            int[] c = PatchCounters();
            if (sDisposeWatch == null) sDisposeWatch = StopWatch.Create(StopWatch.TickStyles.Microseconds);
            try
            {
                while (sDisposeQueue.Count > 0)
                {
                    int state = DisposeSpreadState();
                    if (state == 2) { c[288] += sDisposeQueue.Clear(); return; }
                    sDisposeWatch.Restart();
                    GameObject o;
                    for (int n = 0; (state == 1 || n < kDisposeSpreadPerTick) && sDisposeQueue.Next(Simulator.TicksElapsed(), out o); n++)
                        DisposeSpreadDestroy(o, state == 1 ? 282 : 281);
                    uint us = sDisposeWatch.GetElapsedTime();
                    if (us > (uint)c[285]) c[285] = (int)Math.Min(us, int.MaxValue);
                    if (sDisposeQueue.MaxWait > c[289]) c[289] = (int)sDisposeQueue.MaxWait;
                    if (sDisposeQueue.Count > 0) Simulator.Sleep(0);
                }
            }
            finally { sDisposeTask = ObjectGuid.InvalidObjectGuid; }
        }

        static void DisposeSpreadDestroy(GameObject o, int counter)
        {
            int[] c = PatchCounters();
            if (o.HasBeenDestroyed) { c[286]++; return; } // someone else destroyed it meanwhile
            try { o.Destroy(); c[counter]++; }
            catch (Exception) { c[288]++; }
        }

        // Everything waiting, destroyed now (before a save writes, or from the agent).
        public static int DisposeSpreadFlush()
        {
            int n = 0;
            GameObject o;
            while (sDisposeQueue.Next(Simulator.TicksElapsed(), out o)) { DisposeSpreadDestroy(o, 282); n++; }
            return n;
        }

        public class FadeDestroyHook
        {
            public void CallbackHook()
            {
                Sim sim = (object)this as Sim; // `this` is really the faded GameObject (swapped body)
                if (sim != null && DisposeSpreadDefer(sim)) return;
                CallbackOrig();
            }
            public void CallbackOrig() { } // receives GameObject.DestroyObjectOnFadeOutCallBack's original header
        }

        public class DestroyMyselfHook
        {
            public bool RunHook()
            {
                Sim actor = ((object)this as InteractionInstance).IInstanceActor as Sim; // `this` is really a Sim.DestroyMyself
                if (actor == null || !ShortcutOn("disposespread")) return RunOrig();
                // the game's own Run, Destroy queued instead; the Sim waits out of the world until our task destroys it
                actor.FadeOut();
                actor.RemoveFromWorld();
                Simulator.Sleep(30u);
                if (!DisposeSpreadDefer(actor)) { actor.Destroy(); return true; }
                Simulator.Sleep(uint.MaxValue);
                return true;
            }
            public bool RunOrig() { return false; } // receives Sim.DestroyMyself.Run's original header
        }

        public abstract class TrafficReleaseHook : CarNpcWithNoDriver
        {
            public void ReleaseHook()
            {
                if (!ShortcutOn("disposespread")) { ReleaseOrig(); return; }
                // the game's own body, Destroy queued instead (Release(fadeOut) already took the car out of the world)
                CarNpcManager.Singleton.DriveAwayInNpcCar(this, null, LotCurrent);
                base.Release(true);
                if (!DisposeSpreadDefer(this)) Destroy();
            }
            public void ReleaseOrig() { } // receives CarNpcWithNoDriver.DriveOffscreenAndRelease's original header
        }

        public class DisposeSaveHook
        {
            public bool SaveHook(bool synchronous, bool forceUseSaveName, bool forceSaveAs)
            {
                try { PatchCounters(); DisposeSpreadFlush(); } catch (Exception) { } // never block a save
                try { SeasonImposterFlush(); } catch (Exception) { } // seasonimposter: the save holds fresh imposters (SeasonImposter.cs)
                try { PreferredServiceSweep(); } catch (Exception) { } // preferredservice (EaFixes.cs)
                try { FirePitChairSweep(); } catch (Exception) { } // firepitchair (EaFixes.cs)
                try { DeadRefSavePass(); } catch (Exception) { } // deadrefsweep: cheap final pass, the rest is spread hourly (DeadRefs.cs)
                bool saved = SaveOrig(synchronous, forceUseSaveName, forceSaveAs);
                try { PlayedSyncKey(); } catch (Exception) { } // Save As: the played-household sidecar follows the new name (PlayedHouseholds.cs)
                return saved;
            }
            public bool SaveOrig(bool synchronous, bool forceUseSaveName, bool forceSaveAs) { return false; } // OptionsModel.SaveGame(bool, bool, bool)
        }

        // Agent trigger (repros.json *-disposespread-*). "fade": up to n unseen homeless NPC Sims fade out and are destroyed in
        // one alarm tick (stress: forced burst through hook 1); "gohome": the same Sims sent home through Sim.MakeSimGoHome
        // (the game's own leave path, bursts only where they arrive together); "cars": up to n traffic cars told to drive off
        // (their own cleanup alarm's flag). Returns how many it picked.
        public static string DisposeSpreadNow(string mode, int n)
        {
            int picked = 0;
            if (mode == "cars")
            {
                System.Reflection.MethodInfo off = typeof(CarNpcWithNoDriver).GetMethod("TriggerRouteAwayAndDestroy", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                foreach (CarNpcWithNoDriver car in Sims3.Gameplay.Queries.GetObjects<CarNpcWithNoDriver>())
                {
                    if (picked >= n) break;
                    if (car.InWorld && !car.ShouldDriveOff) { off.Invoke(car, null); picked++; }
                }
                return "cars told to drive off: " + picked;
            }
            if (mode != "fade" && mode != "gohome") return "unknown mode " + mode + " (fade, gohome, cars)";
            foreach (Sim sim in Sims3.Gameplay.Queries.GetObjects<Sim>())
            {
                if (picked >= n) break;
                if (sim.LotHome != null || sim.SimDescription.IsBonehilda || !sim.InWorld || !DisposeSpreadUnseen(sim) || !TownieUnseen(sim, null)) continue;
                if (mode == "fade") sim.FadeOut(false, true);
                else if (!Sim.MakeSimGoHome(sim, false)) continue;
                picked++;
            }
            return mode + ": " + picked + " Sims";
        }
    }
}
