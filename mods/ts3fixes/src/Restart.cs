using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Sims3.Gameplay;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Interactions;
using Sims3.SimIFace;
using Sims3.UI;
using Sims3.UI.Hud;

namespace Ts3Fixes
{
    // Wrapper restart (docs/530 step 1): when the game is close to Error 12, restart the game process into the same save so
    // it gets a fresh 32-bit address space, the window staying up in ts3wrap.exe. ts3native decides and talks to the host
    // ([Wrapper] Channel=1 + Restart=1 in ts3native.ini, restart.cpp, text channel op 40); this file acts. Off unless
    // native says so. Shares Compaction's two hooks (a method is patched once): OptionsModel.SaveGameTask and
    // MainMenuState.Startup (Compaction.cs).
    //   1. After a save that ended with result 0, with Compaction's state gates (CpStateWhy: live mode, no travel / vacation
    //      / university / other town, no dialog, no emergency): native gives the verdict (threshold, re-arm, Apex), or the
    //      RestartHotkey's save is a yes.
    //   2. Optional prompt (RestartAsk=1). Record: Compaction's CpRecordNow (speed, HUD panel, selected Sim, camera, queues
    //      of the Sims in scope), as RestartCore lines to native, which writes them into the handoff file beside its logs
    //      (never into the save) and tells the host, waiting for its cover. Then EA's quit (TransitionToGameStateQuitNoCheck:
    //      no "save first?" question because the save just returned result 0). No process is killed.
    //   3. The host starts the game again with -worldFile; at the world load this file reads the record back from native
    //      (op 40 bit reads, only when native checked the file: schema, session, save name, size and write time of the save
    //      folder, age), checks the loaded save is the one named, and restores with Compaction's code one tick later.
    //   Any failure: one "wrapper:" line, the player plays on as the game is, Restart off for the session (native).
    // Counters [2150-2155]: saves checked, offers, declined, restarts started (the quit sent), given up before the quit,
    // boot restores done. Queue entries share Compaction's [434-438].
    public static partial class Fixes
    {
        const int kWrWhyState = 1, kWrWhyNoName = 2, kWrWhyQuit = 3, kWrWhyRecord = 4;  // native restart.cpp kWrWhy
        const int kWrBootMenu = 1, kWrBootOther = 2, kWrBootParse = 3;                 // native kWrBootWhy
        const int kWrMenuWaitMs = 15000;  // a restart boot at the main menu this long without a world load: -worldFile did not load

        static bool sWrOn, sWrBoot, sWrLoadSeen, sWrHotkey;
        static int sWrKeyGen;

        static bool WrAsk(uint lo) { try { return DiagSend(40, lo); } catch (Exception) { return false; } }

        // From ApplyCompaction (before its hooks): true when the restart needs them.
        static bool ApplyWrapRestart()
        {
            sWrOn = WrAsk(0);
            sWrBoot = WrAsk(8);  // this process was started by the host for a restart
            sWrHotkey = sWrOn && WrAsk(12);
            if (sWrBoot) World.OnWorldLoadStartedEventHandler += delegate { sWrLoadSeen = true; };
            return sWrOn || sWrBoot;
        }

        static void WrGiveUp(int why)
        {
            PatchCounters()[2154]++;
            WrAsk(7u | (uint)why << 8);  // native: one line, off for the session, the host drops its cover
            CpNotify(RestartCore.kSkippedText);
        }

        // After the save (Compaction.cs CompactAfterSave, inside its one-check-at-a-time guard). True: the restart took this
        // save (started, offered or given up), Compaction stays out.
        static bool WrapAfterSave()
        {
            if (!sWrOn) return false;
            int[] c = PatchCounters();
            c[2150]++;
            string why = CpWaitStateWhy("wrapper", c[2150]);
            if (why != null) { DiagLine("wrapper: save #" + c[2150] + " not considered: " + why); return false; }
            if (!WrAsk(2)) return false;  // native logs the verdict
            if (WrAsk(3))
            {
                c[2151]++;
                if (!TwoButtonDialog.Show(RestartCore.kAskText, "Refresh", "Not now"))
                {
                    c[2152]++;
                    WrAsk(4);
                    return true;
                }
                string why2 = CpStateWhy();
                if (why2 != null) { DiagLine("wrapper: state changed during the prompt: " + why2); WrGiveUp(kWrWhyState); return true; }
            }
            WrapGo();
            return true;
        }

        static void WrapGo()
        {
            int[] c = PatchCounters();
            string file = GameStates.LoadFileName;
            if (string.IsNullOrEmpty(file)) { WrGiveUp(kWrWhyNoName); return; }
            int dropped;
            List<string> lines = RestartCore.Lines(WrToRs(CpRecordNow(file)), out dropped);
            if (!WrAsk(5)) { WrGiveUp(kWrWhyRecord); return; }
            foreach (string l in lines)
                if (!DiagLine("wraprec " + l)) { WrGiveUp(kWrWhyRecord); return; }
            if (dropped > 0) DiagLine("wrapper: " + dropped + " queue entries not recorded (line or size limit)");
            if (!WrAsk(6)) { c[2154]++; CpNotify(RestartCore.kSkippedText); return; }  // native gave up and logged why
            c[2153]++;
            Simulator.AddObject(new LoopTask(delegate
            {
                try { GameStates.TransitionToGameStateQuitNoCheck(); }
                catch (Exception) { WrGiveUp(kWrWhyQuit); }
            }));
        }

        // MainMenuState.Startup (Compaction.cs CompactMenuHook): a restart boot that stays at the menu (-worldFile not taken).
        static void WrapMenuStartup()
        {
            if (!sWrBoot || sWrLoadSeen) return;
            int t0 = System.Environment.TickCount;
            Simulator.AddObject(new LoopTask(delegate
            {
                while (sWrBoot && !sWrLoadSeen && System.Environment.TickCount - t0 < kWrMenuWaitMs) Simulator.Sleep(0);
                if (!sWrBoot || sWrLoadSeen) return;
                sWrBoot = false;
                DiagLine("wrapper: restart boot: the main menu is up without a world load after " + kWrMenuWaitMs / 1000 + " s");
                WrAsk(10u | kWrBootMenu << 8);
            }));
        }

        // Compaction.cs CompactionWorldLoad, first: every world load.
        static void WrapWorldLoad()
        {
            if (sWrHotkey) WrStartKeyTask();
            if (!sWrBoot) return;
            sWrBoot = false;
            sWrLoadSeen = true;
            if (!WrAsk(14)) return;  // no usable handoff file: native says ready at its world line, the save just loads
            uint nn = WrLen(1), pn = WrLen(0);
            string name = nn > 0 && nn < 512 ? Encoding.UTF8.GetString(WrBytes(0x11u, nn)) : null;
            if (!RestartCore.SameSave(GameStates.LoadFileName, name))
            {
                DiagLine("wrapper: restart boot loaded another save than the handoff file names: nothing restored");
                WrAsk(10u | kWrBootOther << 8);
                return;
            }
            RsRecord rs = pn > 0 && pn <= 65536 ? RestartCore.Parse(Encoding.UTF8.GetString(WrBytes(0x10u, pn))) : null;
            if (rs == null)
            {
                DiagLine("wrapper: restart boot: the handoff record (" + pn + " bytes) did not parse: nothing restored");
                WrAsk(10u | kWrBootParse << 8);
                return;
            }
            int missing;
            CpRecord r = WrFromRs(rs, out missing);
            Simulator.AddObject(new LoopTask(delegate { WrapRestore(r, missing); }));
        }

        static void WrapRestore(CpRecord r, int missing)
        {
            int[] c = PatchCounters();
            int mask = 0, pushed = c[435];
            try
            {
                Simulator.Sleep(1);  // one sim tick after the world is up, as Compaction
                mask = CpRestoreBody(r);
                try { Sims3.Gameplay.UI.Responder.Instance.ClockSpeedModel.CurrentGameSpeed = r.Speed; mask |= 16; } catch (Exception) { }  // last
                c[2155]++;
            }
            catch (Exception) { }
            finally
            {
                DiagLine("wrapper: restore: " + (c[435] - pushed) + " queue entries pushed again, " + missing + " whose definition or object type is gone");
                WrAsk(9u | (uint)mask << 8);  // native: summary line, the host drops its cover
            }
            CpNotify(RestartCore.kDoneText);
        }

        // RestartHotkey: Ctrl+Shift+F12 in the game window (native counts it); polled in the world, a few times a second.
        static void WrStartKeyTask()
        {
            int gen = ++sWrKeyGen;
            Simulator.AddObject(new LoopTask(delegate
            {
                int last = System.Environment.TickCount;
                while (gen == sWrKeyGen && GameStates.IsInWorld())
                {
                    Simulator.Sleep(0);
                    if (System.Environment.TickCount - last < 250) continue;
                    last = System.Environment.TickCount;
                    if (WrAsk(11)) WrHotkey();
                }
            }));
        }

        static void WrHotkey()
        {
            var om = Sims3.Gameplay.UI.Responder.Instance.OptionsModel;
            string why = CpStateWhy();
            if (why == null && (om.SaveGameInProgress || !om.CanSaveGame() || string.IsNullOrEmpty(om.SaveName))) why = "the game cannot save now";
            if (why != null) { DiagLine("wrapper: hotkey ignored: " + why); return; }
            WrAsk(13);  // native: the verdict after this save is a yes (if the save worked)
            Simulator.AddObject(new LoopTask(delegate { om.SaveGame(false, false, false); }));  // EA's normal save -> SaveGameTask
        }

        // --- the record as plain values (RestartCore) and back

        static string WrTypeName(Type t)
        {
            string a = t.Assembly.FullName;
            int k = a.IndexOf(',');
            return t.FullName + ", " + (k > 0 ? a.Substring(0, k) : a);
        }

        static Type WrType(string name)
        {
            Type t = null;
            try { t = Type.GetType(name); } catch (Exception) { }
            if (t != null) return t;
            int k = name.LastIndexOf(", ");
            if (k <= 0) return null;
            string full = name.Substring(0, k), asm = name.Substring(k + 2);
            try
            {
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    string an = a.FullName;
                    int j = an.IndexOf(',');
                    if ((j > 0 ? an.Substring(0, j) : an) == asm) return a.GetType(full);
                }
            }
            catch (Exception) { }
            return null;
        }

        static RsRecord WrToRs(CpRecord r)
        {
            RsRecord o = new RsRecord();
            o.Speed = (int)r.Speed;
            o.Panel = (int)r.Panel;
            o.Selected = r.Selected;
            o.HasCamera = r.HasCamera;
            o.Cam[0] = r.CamPos.x; o.Cam[1] = r.CamPos.y; o.Cam[2] = r.CamPos.z;
            o.Cam[3] = r.CamTarget.x; o.Cam[4] = r.CamTarget.y; o.Cam[5] = r.CamTarget.z;
            foreach (CpSim s in r.Sims)
            {
                RsSim t = new RsSim();
                t.SimId = s.SimId;
                foreach (CpItem it in s.Items)
                {
                    RsItem x = new RsItem();
                    x.Kind = it.Kind;
                    x.Id = it.Id;
                    x.Level = (int)it.Priority.Level;
                    x.Value = it.Priority.Value;
                    x.Autonomous = it.Autonomous;
                    x.Cancellable = it.Cancellable;
                    x.X = it.Pos.x; x.Y = it.Pos.y; x.Z = it.Pos.z;
                    x.DefType = WrTypeName(it.Def.DeclaringType);
                    x.DefField = it.Def.Name;
                    x.ObjType = it.Kind == 1 && it.ObjType != null ? WrTypeName(it.ObjType) : "";
                    t.Items.Add(x);
                }
                o.Sims.Add(t);
            }
            return o;
        }

        static CpRecord WrFromRs(RsRecord rs, out int missing)
        {
            missing = 0;
            CpRecord r = new CpRecord();
            r.File = GameStates.LoadFileName;
            r.Tick = System.Environment.TickCount;
            r.Speed = (Sims3.SimIFace.Gameflow.GameSpeed)rs.Speed;
            r.Panel = (InfoState)rs.Panel;
            r.Selected = rs.Selected;
            r.HasCamera = rs.HasCamera;
            r.CamPos = new Vector3(rs.Cam[0], rs.Cam[1], rs.Cam[2]);
            r.CamTarget = new Vector3(rs.Cam[3], rs.Cam[4], rs.Cam[5]);
            const BindingFlags st = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (RsSim s in rs.Sims)
            {
                CpSim t = new CpSim();
                t.SimId = s.SimId;
                foreach (RsItem x in s.Items)
                {
                    Type dt = WrType(x.DefType);
                    FieldInfo f = dt != null ? dt.GetField(x.DefField, st) : null;
                    Type ot = x.Kind == 1 ? WrType(x.ObjType) : null;
                    if (f == null || !typeof(InteractionDefinition).IsAssignableFrom(f.FieldType) || (x.Kind == 1 && ot == null)) { missing++; continue; }
                    CpItem it = new CpItem();
                    it.Def = f;
                    it.Kind = x.Kind;
                    it.Id = x.Id;
                    it.ObjType = ot;
                    it.Pos = new Vector3(x.X, x.Y, x.Z);
                    it.Priority = new InteractionPriority((InteractionPriorityLevel)x.Level, x.Value);
                    it.Autonomous = x.Autonomous;
                    it.Cancellable = x.Cancellable;
                    t.Items.Add(it);
                }
                if (t.Items.Count > 0) r.Sims.Add(t);
            }
            return r;
        }

        // --- native reads (op 40 0x10-0x12): one bit per call

        static uint WrLen(int which)
        {
            bool[] b = new bool[32];
            for (int i = 0; i < 32; i++) b[i] = WrAsk(0x12u | (uint)i << 8 | (uint)which << 13);
            return RestartCore.FromBits(b);
        }

        static byte[] WrBytes(uint cmd, uint n)
        {
            byte[] o = new byte[n];
            for (uint i = 0; i < n; i++)
            {
                int v = 0;
                for (int b = 0; b < 8; b++) if (WrAsk(cmd | (uint)b << 8 | i << 11)) v |= 1 << b;
                o[i] = (byte)v;
            }
            return o;
        }
    }
}
