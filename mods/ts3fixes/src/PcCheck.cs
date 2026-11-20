using System;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using Sims3.UI;

namespace Ts3Fixes
{
    // docs/299 ts3native [PcCheck] (UserDiag op 62). Asked once per world load; a ts3native without it, or with
    // Enabled=0, answers false and nothing below runs. Read-only: one AlarmType.NeverPersisted alarm that tells ts3native
    // when a sim hour ends (it writes pccheck-summary.txt / pccheck.csv) and at most one notice per game session, only
    // for a real problem ([PcCheck] Notify=1). Nothing is stored in the save; no Sim, switch or setting is touched.
    public static partial class Fixes
    {
        const int kPcOp = 62;
        static bool sPcOn, sPcNoticed, sPcErrLogged;
        static int sPcHour = -1;
        static AlarmHandle sPcAlarm = AlarmHandle.kInvalidHandle;

        // PC_* bits of pccheck.cpp in notice order (the most important first) and their one-sentence hints
        static readonly int[] kPcBits = { 4, 2, 16, 8, 32, 1 };
        static readonly string[] kPcHints = {
            "The game runs on battery: plug in the charger for full CPU speed.",
            "Windows is in power-saver mode: choose Balanced or Best performance in the power settings.",
            "Efficiency mode is on for the game: turn it off in Task Manager.",
            "The game may only use some CPU cores (affinity): remove that setting.",
            "The game runs at low priority: set it back to normal.",
            "Your CPU slows down while playing (heat or power limit): check cooling and the power plan.",
        };

        // Instantiator's world-load handler (sim thread).
        public static void StartPcCheck()
        {
            sPcOn = false;
            if (AlarmManager.Global == null) return;
            if (sPcAlarm != AlarmHandle.kInvalidHandle) { AlarmManager.Global.RemoveAlarm(sPcAlarm); sPcAlarm = AlarmHandle.kInvalidHandle; }
            if (!DiagSend(kPcOp, 0, 0)) return;
            sPcOn = true;
            sPcHour = (int)SimClock.Hours24;
            PcMaybeNotice();
            sPcAlarm = AlarmManager.Global.AddAlarmRepeating(1f, TimeUnit.Minutes, delegate { PcTick(); }, 1f, TimeUnit.Minutes, "Ts3Fixes.PcCheck", AlarmType.NeverPersisted, null);
        }

        // Instantiator's world-quit handler: the summary written.
        public static void PcCheckWorldQuit()
        {
            if (sPcOn) DiagSend(kPcOp, 0, 2);
            sPcOn = false;
        }

        static void PcTick()
        {
            if (!sPcOn) return;
            try
            {
                int h = (int)SimClock.Hours24;
                if (h == sPcHour) return;
                DiagSend(kPcOp, (uint)sPcHour, 1);
                sPcHour = h;
                PcMaybeNotice();
            }
            catch (Exception e)
            {
                if (!sPcErrLogged) { sPcErrLogged = true; DiagLine("pccheck: tick threw " + e.GetType().Name + ": " + e.Message); }
            }
        }

        // one notice per game session, only when pccheck found a real problem (and Notify=1 there)
        static void PcMaybeNotice()
        {
            if (!sPcOn || sPcNoticed) return;
            for (int i = 0; i < kPcBits.Length; i++)
            {
                if (!DiagSend(kPcOp, (uint)kPcBits[i], 0)) continue;
                sPcNoticed = true;
                string text = "PC check: " + kPcHints[i] + " Details in pccheck-summary.txt in the game's Bin folder.";
                try { StyledNotification.Show(new StyledNotification.Format(text, StyledNotification.NotificationStyle.kSystemMessage)); DiagLine("pccheck: notice shown: " + text); }
                catch (Exception e) { DiagLine("pccheck: notice failed: " + e.GetType().Name + ": " + e.Message); }
                return;
            }
        }
    }
}
