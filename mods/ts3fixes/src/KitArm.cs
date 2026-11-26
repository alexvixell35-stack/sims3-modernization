using System;
using Sims3.Gameplay;
using Sims3.SimIFace;
using Sims3.UI;

namespace Ts3Fixes
{
    // Tester kit A/B mode (docs/314). kKitArm "" = off (the default: nothing below runs). The kit ships one ts3fixes.package
    // per arm; their tuning differs only in the arm's switches plus kKitArm (the arm's letter), kKitArmNotice (the text the
    // player sees) and kKitArmSwitches (switch names read back, space separated). At each world load: one notice and one
    // "kit: arm" log line with the read-back. While a world is loaded, a sim-thread task notes the real time between two
    // of its wakes (KitStats, KitArmCore.cs). A "kit: stats" line every minute and at world quit; the kit reads the last
    // one after the game closed. Read-only: no Sim, save, switch or setting is touched.
    public static partial class Fixes
    {
        static readonly KitStats sKit = new KitStats();
        static int sKitWorlds;
        static bool sKitErrLogged;

        static bool KitOn() { return !string.IsNullOrEmpty(Instantiator.kKitArm); }

        // Instantiator's world-load handler (sim thread).
        public static void KitArmWorldLoad()
        {
            if (!KitOn()) return;
            sKitWorlds++;
            string text = string.IsNullOrEmpty(Instantiator.kKitArmNotice) ? "TS3 test kit: arm " + Instantiator.kKitArm : Instantiator.kKitArmNotice;
            DiagLine("kit: arm " + Instantiator.kKitArm + ": " + text + " | read-back " + KitReadBack());
            try { StyledNotification.Show(new StyledNotification.Format(text, StyledNotification.NotificationStyle.kSystemMessage)); }
            catch (Exception e) { DiagLine("kit: notice failed: " + e.GetType().Name + ": " + e.Message); }
            Simulator.AddObject(new KitStatsTask());
        }

        // Instantiator's world-quit handler.
        public static void KitArmWorldQuit() { if (KitOn()) KitStatsLine("world quit"); }

        static string KitReadBack()
        {
            string s = "";
            foreach (string n in (Instantiator.kKitArmSwitches ?? "").Split(' '))
                if (n.Length > 0) s += (s.Length > 0 ? " " : "") + n + "=" + (ShortcutOn(n) ? "on" : "off");
            return s.Length > 0 ? s : "-";
        }

        static void KitStatsLine(string when)
        {
            int median, low;
            sKit.Rates(out median, out low);
            DiagLine("kit: stats arm " + Instantiator.kKitArm + " (" + when + "): in world " + (sKit.LiveMs / 60000.0).ToString("0.0") + " min, worlds " + sKitWorlds
                + ", sim updates/s median " + median + " min " + low + " (" + sKit.Secs + " s), hitches >= 250 ms " + sKit.H250 + ", >= 1 s " + sKit.H1000
                + ", longest " + sKit.MaxMs + " ms, save pauses " + sKit.SaveGaps + " (longest " + sKit.SaveMaxMs + " ms), read-back " + KitReadBack());
        }

        class KitStatsTask : Task
        {
            public override void Simulate()
            {
                int last = System.Environment.TickCount, lineAt = last;
                bool lastSave = false;
                sKit.Restart(last);
                while (true)
                {
                    Simulator.Sleep(0);
                    try
                    {
                        int now = System.Environment.TickCount;
                        if (!GameStates.IsInWorld() || GameStates.IsWorldShuttingDown) { last = now; sKit.Restart(now); continue; }
                        bool save = SaveInProgress();
                        sKit.Gap(now - last, save || lastSave, now);
                        last = now; lastSave = save;
                        if (now - lineAt >= 60000) { lineAt = now; KitStatsLine("minute"); }
                    }
                    catch (Exception e)
                    {
                        if (!sKitErrLogged) { sKitErrLogged = true; DiagLine("kit: stats task threw " + e.GetType().Name + ": " + e.Message); }
                    }
                }
            }
        }
    }
}
