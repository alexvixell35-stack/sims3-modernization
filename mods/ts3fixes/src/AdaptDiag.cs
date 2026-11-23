using System;
using System.Collections.Generic;
using System.Text;
using Sims3.Gameplay;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using Sims3.UI;

namespace Ts3Fixes
{
    // docs/316 ts3native [AdaptDiag] (UserDiag op 63, adaptive diag kit; the section exists only in the kit's diag ini). Asked
    // once per world load; a ts3native without it, or with Enabled=0, answers false and nothing below runs. Read-only: one
    // script task that tells ts3native the activity (live / CAS / Buy-Build / travel / paused) when it changes, once per
    // world load one "adaptinstall:" line (our patches that went in or failed), and at most one notice per game session when
    // the kit's install check found an expected part missing. Nothing is stored in the save; no Sim, switch or setting changes.
    public static partial class Fixes
    {
        const int kAdOp = 63;
        const uint kAdLive = 1, kAdCas = 2, kAdBuildBuy = 3, kAdTravel = 4, kAdPaused = 5;
        static bool sAdOn, sAdNoticed, sAdErrLogged;

        // op 63 sub 3: the kit's rule engine asked for swapinvokediag this session (docs/312, after repeated script errors)
        internal static bool AdaptWantsSwapInvokeDiag() { try { return DiagSend(kAdOp, 0, 3); } catch (Exception) { return false; } }

        // Instantiator's world-load handler (sim thread).
        public static void StartAdaptDiag()
        {
            sAdOn = DiagSend(kAdOp, 0xFF, 0);  // probe: 0xFF is no activity
            if (!sAdOn) return;
            AdInstallLine();
            Simulator.AddObject(new AdTask());
        }

        // Instantiator's world-quit handler: load until the next world is live; ts3native writes its report.
        public static void AdaptDiagWorldQuit()
        {
            if (sAdOn) DiagSend(kAdOp, 0, 2);
            sAdOn = false;
        }

        // "patched N, failed M: Type.Method: reason; ..." from the applied log (patches that went in say ": ok")
        static void AdInstallLine()
        {
            List<object> log = AppliedLog();
            if (log == null) return;
            int ok = 0, failed = 0;
            StringBuilder names = new StringBuilder();
            foreach (object o in log)
            {
                string s = o as string;
                if (s == null) continue;
                if (s.EndsWith(": ok") || s.EndsWith(" patched") || s.IndexOf(": ok ", StringComparison.Ordinal) >= 0) { ok++; continue; }
                if (s.IndexOf("REFUSED", StringComparison.Ordinal) < 0 && s.IndexOf("MISSING", StringComparison.Ordinal) < 0 && s.IndexOf("not patched", StringComparison.Ordinal) < 0
                    && s.IndexOf("not found", StringComparison.Ordinal) < 0 && s.IndexOf("Exception", StringComparison.Ordinal) < 0) continue;  // notes, not results
                failed++;
                if (names.Length < 300) names.Append(names.Length > 0 ? "; " : "").Append(s.Length > 90 ? s.Substring(0, 90) : s);
            }
            DiagLine("adaptinstall: patched " + ok + ", failed " + failed + ": " + names);
        }

        static uint AdActivity()
        {
            if (GameStates.IsTravelling) return kAdTravel;
            if (GameStates.IsCasState) return kAdCas;
            if (GameStates.IsBuildBuyLikeState) return kAdBuildBuy;
            if (Sims3.Gameplay.Gameflow.CurrentGameSpeed == Sims3.SimIFace.Gameflow.GameSpeed.Pause) return kAdPaused;
            return kAdLive;
        }

        // Real-time loop (Sleep(0) runs every frame, also while paused): the activity once a second, the notice after 3 minutes.
        class AdTask : Task
        {
            public override void Simulate()
            {
                uint last = 0xFFFFFFFF;
                int t0 = System.Environment.TickCount, next = t0, induce = AdInduceCount(), induced = 0;
                while (sAdOn && !GameStates.IsWorldShuttingDown)
                {
                    int now = System.Environment.TickCount;
                    if (now - next >= 0)
                    {
                        next = now + 1000;
                        try
                        {
                            uint a = AdActivity();
                            if (a != last) { DiagSend(kAdOp, a, 0); last = a; }
                            if (!sAdNoticed && now - t0 > 180000) AdMaybeNotice();
                            // kit test ([AdaptDiag] InduceScriptErrors, default 0): one throwing task every 20 s from 150 s on
                            if (induced < induce && now - t0 > 150000 + induced * 20000) { induced++; DiagLine("adaptdiag: induced script error " + induced + " of " + induce + " (kit test)"); Simulator.AddObject(new AdThrowTask()); }
                        }
                        catch (Exception e)
                        {
                            if (!sAdErrLogged) { sAdErrLogged = true; DiagLine("adaptdiag: activity threw " + e.GetType().Name + ": " + e.Message); }
                        }
                    }
                    Simulator.Sleep(0);
                }
            }
        }

        static int AdInduceCount()
        {
            int n = 0;
            for (int bit = 0; bit < 3; bit++) if (DiagSend(kAdOp, 0, (ushort)(4 | bit << 8))) n |= 1 << bit;
            return n;
        }

        // kit test: an uncaught exception in a task's Simulate, which the game reports as a ScriptError file [inferred, docs/51 §571]
        class AdThrowTask : Task
        {
            public override void Simulate() { throw new InvalidOperationException("adaptdiag induced script error (kit test, [AdaptDiag] InduceScriptErrors)"); }
        }

        // one notice per game session when the install check found something missing (count from ts3native, op 63 sub 1)
        static void AdMaybeNotice()
        {
            sAdNoticed = true;
            int n = 0;
            for (int bit = 0; bit < 8; bit++) if (DiagSend(kAdOp, 0, (ushort)(1 | bit << 8))) n |= 1 << bit;
            if (n == 0) return;
            string text = "TS3 diag kit: the install check found " + n + " expected part(s) missing. Please run TS3Kit.bat, option 8 (Make report), and send the zip. Details: adaptdiag-report.txt in the game's Bin folder.";
            try { StyledNotification.Show(new StyledNotification.Format(text, StyledNotification.NotificationStyle.kSystemMessage)); DiagLine("adaptdiag: notice shown: " + text); }
            catch (Exception e) { DiagLine("adaptdiag: notice failed: " + e.GetType().Name + ": " + e.Message); }
        }
    }
}
