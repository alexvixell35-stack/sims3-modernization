using System;
using System.Text;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using Sims3.UI;

namespace Ts3Fixes
{
    // docs/298 ts3native [Diag] ModTime (UserDiag op 61, default off). Asked once per world load; a ts3native without it,
    // or with the key at 0, answers false and nothing below runs. Read-only: one AlarmType.NeverPersisted alarm that tells
    // ts3native when a sim hour starts (its sampler thread writes the files) and, once 2 sim hours are sampled, the one
    // notice of the game session (the mod cost report, docs/302 §5; its text comes from ts3native bit by bit, op 61 hi 3/4).
    // Nothing is stored in the save and no Sim, switch or setting is touched.
    public static partial class Fixes
    {
        const int kMtOp = 61;
        static bool sMtOn, sMtNoticed, sMtErrLogged;
        static int sMtHour = -1;
        static AlarmHandle sMtAlarm = AlarmHandle.kInvalidHandle;

        // Instantiator's world-load handler (sim thread), after DiagWorldLoad.
        public static void StartModTime()
        {
            sMtOn = false;
            if (AlarmManager.Global == null) return;
            if (sMtAlarm != AlarmHandle.kInvalidHandle) { AlarmManager.Global.RemoveAlarm(sMtAlarm); sMtAlarm = AlarmHandle.kInvalidHandle; }
            if (!DiagSend(kMtOp, 0, 0)) return;
            sMtOn = true;
            sMtHour = (int)SimClock.Hours24;
            DiagSend(kMtOp, (uint)sMtHour, 1);  // opens the first hour: the load block ends, the live tables start
            DiagLine("modtime: world live, sim hour " + sMtHour);
            sMtAlarm = AlarmManager.Global.AddAlarmRepeating(1f, TimeUnit.Minutes, delegate { MtTick(); }, 1f, TimeUnit.Minutes, "Ts3Fixes.ModTime", AlarmType.NeverPersisted, null);
        }

        // Instantiator's world-quit handler: the summary written, samples go to the load block until the next world.
        public static void ModTimeWorldQuit()
        {
            if (sMtOn) DiagSend(kMtOp, 0, 2);
            sMtOn = false;
        }

        static void MtTick()
        {
            if (!sMtOn) return;
            try
            {
                if (!sMtNoticed && DiagSend(kMtOp, 0, 3)) MtShowReport();
                int h = (int)SimClock.Hours24;
                if (h == sMtHour) return;
                DiagSend(kMtOp, (uint)h, 1);
                sMtHour = h;
            }
            catch (Exception e)
            {
                if (!sMtErrLogged) { sMtErrLogged = true; DiagLine("modtime: tick threw " + e.GetType().Name + ": " + e.Message); }
            }
        }

        // The notice text (ASCII, NUL-terminated, < 480 bytes), one bit per call; once a game session.
        static void MtShowReport()
        {
            sMtNoticed = true;
            StringBuilder b = new StringBuilder();
            for (int i = 0; i < 479; i++)
            {
                int c = 0;
                for (int bit = 0; bit < 8; bit++) if (DiagSend(kMtOp, (uint)(i * 8 + bit), 4)) c |= 1 << bit;
                if (c == 0) break;
                b.Append((char)c);
            }
            string text = b.ToString();
            if (text.Length == 0) return;
            try { StyledNotification.Show(new StyledNotification.Format(text, StyledNotification.NotificationStyle.kSystemMessage)); DiagLine("modtime: notice shown: " + text); }
            catch (Exception e) { DiagLine("modtime: notice failed: " + e.GetType().Name + ": " + e.Message); }
        }
    }
}
