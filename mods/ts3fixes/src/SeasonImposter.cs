using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Objects;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // "seasonimposter" (issue #82, docs/44 §6.1, default on since 2026-11-25): lot imposter rebuilds after a season change, moved out of the
    // season alarm. SeasonalLotMarker.SetSeason and EventLotMarker.SetLayout end with World.RegenImposter(lot) +
    // World.SetImposterMode(false) for a community or inactive lot in a yielding context; those are the game's only
    // script callers [verified, decompiled]. RegenImposter is Lot_MarkImposterDirty 0xAC3680: the sim thread waits for the
    // main-thread imposter job (LotImposter_WaitOrPump 0xAD97E0) [verified, docs/44 §6], 0.3-1.4 s for a lot whose models are
    // not loaded [community, issue #82]. The 3 AM pass (LotManager.CleanUpLotsAndInventories) kicks every lot that needs a
    // season change with a 1-sim-minute yielding alarm, so several lots' rebuilds can land in the same tick [inferred].
    // With the switch on, a RegenImposter call in live mode only queues the lot id (once); our task rebuilds one lot at a
    // time, at most one per kSiGapMs of real time:
    //   - warm lots first: the active household's home, a lot an active-household Sim is on, the lot under the camera (drawn
    //     in full, so models loaded [inferred]: the cheap kind, 100-380 ms [community]);
    //   - cold lots only with "seasonimpostercold" (nearest to the camera first), else they wait until they turn warm or the
    //     next save, which rebuilds every queued lot before it writes (DisposeSaveHook), so a save always holds a fresh
    //     imposter. Until then only the lot's far view shows last season's items.
    // Travel flushes like a save; world unload drops the queue (nothing of it was saved). Our own calls pass through.
    // Counters [373-379] (registry fixes-373).
    public static partial class Fixes
    {
        const int kSiGapMs = 2000;   // real time between two rebuilds in play: one lot's wait per hitch, never several
        static readonly SiQueue sSiQueue = new SiQueue();
        static ObjectGuid sSiTask = ObjectGuid.InvalidObjectGuid;
        static StopWatch sSiWatch, sSiGap;
        static bool sSiOurs;          // our own RegenImposter call: the hook passes it to the game

        static void ApplySeasonImposter()
        {
            Patch(typeof(World), "RegenImposter", new Type[] { typeof(ulong) }, typeof(SeasonImposterHook), "RegenHook", "RegenOrig");
        }

        // True when queued; false = the game's call runs now.
        static bool SeasonImposterDefer(ulong lotId)
        {
            if (sSiOurs || !ShortcutOn("seasonimposter") || OnWorker()) return false;
            if (WorldGone() || !GameStates.IsLiveState || GameStates.IsTravelling || SaveInProgress()) return false;
            int[] c = PatchCounters();
            if (!TaskAlive(sSiTask))
            {
                c[377] += sSiQueue.Clear(); // left over from a task the world unload killed
                sSiTask = Simulator.AddObject(new LoopTask(SeasonImposterDrain));
            }
            sSiQueue.Add(lotId);
            c[373]++;
            if (sSiQueue.Count > c[379]) c[379] = sSiQueue.Count;
            return true;
        }

        static void SeasonImposterDrain()
        {
            if (sSiGap == null) sSiGap = StopWatch.Create(StopWatch.TickStyles.Milliseconds);
            try
            {
                while (sSiQueue.Count > 0)
                {
                    if (WorldGone()) { PatchCounters()[377] += sSiQueue.Clear(); return; }
                    if (GameStates.IsTravelling && !SaveInProgress()) { SeasonImposterFlush(); return; }
                    if (!GameStates.IsLiveState || SaveInProgress() || sSiGap.GetElapsedTime() < kSiGapMs) { Simulator.Sleep(0); continue; }
                    int pick = SeasonImposterPick(ShortcutOn("seasonimpostercold"));
                    if (pick < 0) { Simulator.Sleep(30); continue; } // only cold lots left: wait for warm or the save
                    ulong id = sSiQueue[pick];
                    bool warm = SeasonImposterWarm(LotManager.GetLot(id));
                    sSiQueue.RemoveAt(pick);
                    SeasonImposterRebuild(id, warm ? 374 : 375);
                    sSiGap.Restart();
                    Simulator.Sleep(0);
                }
            }
            finally { sSiTask = ObjectGuid.InvalidObjectGuid; }
        }

        // Queue index of the next lot: a warm one first, else (cold allowed) the one nearest to the camera; -1 none.
        static int SeasonImposterPick(bool cold)
        {
            Vector3 cam = CameraController.GetTarget();
            return sSiQueue.Pick(
                delegate(ulong id) { Lot lot = LotManager.GetLot(id); return lot == null || SeasonImposterWarm(lot); }, // a gone lot is dropped at its turn
                delegate(ulong id) { return (LotManager.GetLot(id).Position - cam).LengthSqr(); }, cold);
        }

        // Drawn in full near the player: the same lots as Lot.CalculateDetailPriority (home, a player Sim on it) or under the camera.
        static bool SeasonImposterWarm(Lot lot)
        {
            if (lot == null) return false;
            if (LotManager.GetLotAtPoint(CameraController.GetTarget()) == lot) return true;
            Household h = Household.ActiveHousehold;
            if (h == null) return false;
            if (h.LotHome == lot) return true;
            foreach (Sim s in h.AllActors)
                if (s.LotCurrent == lot && s.RabbitHoleCurrent == null) return true;
            return false;
        }

        // The game's own two calls, timed. A lot that is gone is dropped.
        static void SeasonImposterRebuild(ulong id, int counter)
        {
            int[] c = PatchCounters();
            if (LotManager.GetLot(id) == null) { c[377]++; return; }
            if (sSiWatch == null) sSiWatch = StopWatch.Create(StopWatch.TickStyles.Milliseconds);
            sSiWatch.Restart();
            sSiOurs = true;
            try { World.RegenImposter(id); World.SetImposterMode(false); c[counter]++; }
            catch (Exception) { c[377]++; }
            finally { sSiOurs = false; }
            int ms = (int)sSiWatch.GetElapsedTime();
            if (ms > c[378]) c[378] = ms;
        }

        // Every queued lot rebuilt now (before a save writes, at travel, or from the agent). Returns how many.
        public static int SeasonImposterFlush()
        {
            int n = 0;
            while (sSiQueue.Count > 0)
            {
                ulong id = sSiQueue[0];
                sSiQueue.RemoveAt(0);
                SeasonImposterRebuild(id, 376);
                n++;
            }
            return n;
        }

        public static class SeasonImposterHook
        {
            public static void RegenHook(ulong lotId)
            {
                if (!SeasonImposterDefer(lotId)) RegenOrig(lotId);
            }
            public static void RegenOrig(ulong lotId) { } // receives Sims3.SimIFace.World.RegenImposter(ulong)'s original header
        }

        // Agent trigger (repros.json bp-seasonimposter-*). Picks up to n community lots that are not warm.
        //   "season": the real path (stress-labelled, forced): each picked lot that has a SeasonalLotMarker gets its marker's
        //            recorded season set to another one, then Lot.MagicallyChangeSeason (the 3 AM kick: a 1-sim-minute
        //            yielding alarm -> Lot.ChangeSeason -> UpdateSeason -> SetSeason -> RegenImposter). Same final items.
        //   "regen":  World.RegenImposter + SetImposterMode(false) for each picked lot, in one task tick (EA's two calls).
        //   "flush":  SeasonImposterFlush now. "queue": queue length and ids.
        public static string SeasonImposterNow(string mode, int n)
        {
            if (mode == "flush") return "flushed " + SeasonImposterFlush();
            if (mode == "queue") return "queued " + sSiQueue.Count + ": " + sSiQueue.Join();
            if (mode != "season" && mode != "regen") return "unknown mode " + mode + " (season, regen, flush, queue)";
            FieldInfo season = typeof(SeasonalLotMarker).GetField("mLotSeason", BindingFlags.Instance | BindingFlags.NonPublic);
            List<Lot> picked = new List<Lot>();
            List<string> names = new List<string>();
            foreach (Lot lot in LotManager.AllLots)
            {
                if (picked.Count >= n) break;
                if (lot.IsWorldLot || !lot.IsCommunityLot || lot == LotManager.ActiveLot || SeasonImposterWarm(lot)) continue;
                if (mode == "season")
                {
                    SeasonalLotMarker[] m = lot.GetObjects<SeasonalLotMarker>();
                    if (m.Length == 0 || season == null) continue;
                    SeasonalLotMarker.LotSeason cur = (SeasonalLotMarker.LotSeason)season.GetValue(m[0]);
                    season.SetValue(m[0], cur == SeasonalLotMarker.LotSeason.Winter ? SeasonalLotMarker.LotSeason.Summer : SeasonalLotMarker.LotSeason.Winter);
                    if (!m[0].IsKickNeeded()) { season.SetValue(m[0], cur); continue; } // no Seasons, or nothing to swap
                    lot.MagicallyChangeSeason();
                }
                picked.Add(lot);
                names.Add(lot.Name);
            }
            if (mode == "regen" && picked.Count > 0)
                Simulator.AddObject(new LoopTask(delegate
                {
                    foreach (Lot lot in picked) { World.RegenImposter(lot.LotId); World.SetImposterMode(false); }
                }));
            return mode + ": " + picked.Count + " lots (" + string.Join(", ", names.ToArray()) + ")";
        }
    }
}
