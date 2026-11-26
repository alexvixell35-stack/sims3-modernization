using System;
using Sims3.Gameplay;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Utilities;

namespace Ts3Fixes
{
    // Rodney 2026-11-23: nothing of ours does sim-state work while the game is paused, in CAS, Buy/Build, Edit Town or menus.
    // Per-frame tasks (LoopTask + Simulator.Sleep(0)) keep running in all of these: ScriptTick and TaskObjects run while the
    // sim runs 0 steps (docs/85 I-85-2), so each such task asks this before acting. True = do not act this frame.
    // [verified, decompiled GameStates.cs] IsLiveState = in-world substate LiveMode, so Buy, Build, CAS and Edit Town are not live.
    // A minimized window stops the clock through GameClock pause bit 2 without touching CurrentGameSpeed (docs/85 I-85-1,
    // clock-minimize-pause PASS 2026-10-17; docs/162 §7): the clock check covers it. Offline part: SimFrozenCore.
    public static partial class Fixes
    {
        static long sSfTicks;
        static int sSfMovedMs;

        public static bool SimFrozenOrNotLive()
        {
            if (SimFrozenCore.FrozenOrNotLive(GameStates.IsLiveState,
                    GameStates.IsTravelling || GameStates.IsGameShuttingDown || GameStates.IsWorldShuttingDown,
                    Gameflow.CurrentGameSpeed == Sims3.SimIFace.Gameflow.GameSpeed.Pause, false)) return true;
            return SimFrozenCore.ClockStill(SimClock.CurrentTicks, Environment.TickCount, ref sSfTicks, ref sSfMovedMs);
        }

        // docs/205 §2.3c: a one-shot task or a drain holds its queued work a frame at a time while SimFrozenOrNotLive; the
        // world going away (shutdown, travel, out of the world) ends the wait so the caller's own exit runs. Task context only.
        // c[slot]: frames waited. True = it waited. saveExits: a save ends the wait too (the caller stops at a save).
        static bool SfWait(int slot) { return SfWait(slot, false); }
        static bool SfWait(int slot, bool saveExits)
        {
            bool waited = false;
            while (SimFrozenCore.Hold(SimFrozenOrNotLive(), SfLeaving(), saveExits && SaveInProgress()))
            {
                PatchCounters()[slot]++;
                waited = true;
                Sims3.SimIFace.Simulator.Sleep(0);
            }
            return waited;
        }

        static bool SfLeaving() { return GameStates.IsWorldShuttingDown || GameStates.IsGameShuttingDown || GameStates.IsTravelling || !GameStates.IsInWorld(); }

        // PauseGateCheck (repro pausegate-drains-check): mark / state print the group 2 waits and action counters since the
        // mark and whether the household's Sims kept position and interaction; replan / boatsweep queue work while paused.
        static readonly int[] kPgWait = { 1734, 1735, 1736, 1737, 1738, 1743, 1739, 1740, 1741, 1742 };
        static readonly string[] kPgWaitName = { "stuck", "watchdog", "replan", "replanhh", "autlive", "autliveslice", "wildflower", "dispose", "aptleave", "boat" };
        static readonly int[] kPgAct = { 128, 135, 281, 31 };  // stuck checks run, replans replayed, spread destroys, watchdog resets
        static readonly string[] kPgActName = { "stuckrun", "replayed", "destroyed", "wdresets" };
        static int[] sPgMark = new int[14];
        static string sPgHh = "";

        public static string PauseGateCheck(string what)
        {
            switch (what)
            {
                case "mark": sPgMark = PgNow(); sPgHh = PgHousehold(); return "mark: " + PgState();
                case "state": return "state: " + PgState();
                case "replan": LotManager.ForceReplanOfAllSimRoutes(); return "replan: queued " + sSpread.Count + " | " + PgState();
                case "boatsweep": BtSweepSoon(); return "boatsweep: queued " + sBtSweepQueued + ", boats " + sBtBoats.Count;
                default: return "unknown check " + what + " (mark, state, replan, boatsweep)";
            }
        }

        static int[] PgNow()
        {
            int[] k = PatchCounters(), n = new int[kPgWait.Length + kPgAct.Length];
            for (int i = 0; i < kPgWait.Length; i++) n[i] = k[kPgWait[i]];
            for (int i = 0; i < kPgAct.Length; i++) n[kPgWait.Length + i] = k[kPgAct[i]];
            return n;
        }

        static string PgState()
        {
            int[] n = PgNow();
            System.Text.StringBuilder b = new System.Text.StringBuilder("acted");
            for (int i = 0; i < kPgAct.Length; i++) b.Append(' ').Append(kPgActName[i]).Append(" +").Append(n[kPgWait.Length + i] - sPgMark[kPgWait.Length + i]);
            b.Append(", waits");
            for (int i = 0; i < kPgWait.Length; i++) b.Append(' ').Append(kPgWaitName[i]).Append(" +").Append(n[i] - sPgMark[i]);
            return b.Append(", spreadq ").Append(sSpread.Count).Append(", household ").Append(PgHousehold() == sPgHh ? "same" : "changed").Append(", frozen ").Append(SimFrozenOrNotLive()).ToString();
        }

        // each active-household Sim: id, position to 0.1 m, current interaction type (paused, none of these may change)
        static string PgHousehold()
        {
            Household hh = Household.ActiveHousehold;
            if (hh == null) return "none";
            System.Text.StringBuilder b = new System.Text.StringBuilder();
            foreach (Sim s in hh.AllActors)
            {
                if (s == null || s.HasBeenDestroyed) { b.Append("gone;"); continue; }
                b.Append(s.ObjectId.Value).Append('@').Append(Math.Round(s.Position.x, 1)).Append(',').Append(Math.Round(s.Position.z, 1))
                 .Append(':').Append(s.CurrentInteraction != null ? s.CurrentInteraction.GetType().Name : "-").Append(';');
            }
            return b.ToString();
        }
    }
}
