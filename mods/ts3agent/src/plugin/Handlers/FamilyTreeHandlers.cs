using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Socializing;
using Sims3.SimIFace;
using Sims3.UI;
using Sims3.UI.CAS;

namespace Ts3Agent
{
    // family_tree {opens?: 6, clicks?: 8, wait_ms?: 400} | {status: true} | {stop: true} (docs/191 leak hunts):
    // STRESS, not a player flow. Opens EA's family tree dialog (FamilyTreeDialog.Show, as the Simology panel button does)
    // for the `opens` Sims with the most direct relatives, and in each one clicks `clicks` relatives' thumbnails
    // (FamilyTreeDialog.OnFamilyTreeThumbClick, which runs RefreshTree and rebuilds the whole tree), then presses OK.
    // The dialog is modal and blocks the task that opened it (UIManager.BeginModal), so a second task drives it.
    // Returns at once; status reports the progress. Reads only: no Sim or relation is changed.
    public static partial class Handlers
    {
        static int sFtGen, sFtOpened, sFtClicks, sFtThumbsSeen, sFtErrors;
        static bool sFtRunning;
        static string sFtLast = "";

        static void RegisterFamilyTree()
        {
            Dispatcher.Register("family_tree", FamilyTree);
        }

        static Dictionary<string, object> FtStatus()
        {
            Dictionary<string, object> r = Util.Obj();
            r["running"] = sFtRunning;
            r["opened"] = sFtOpened;
            r["clicks"] = sFtClicks;
            r["thumbs_seen"] = sFtThumbsSeen;
            r["errors"] = sFtErrors;
            r["last"] = sFtLast;
            return r;
        }

        static int FtRelatives(SimDescription d)
        {
            Genealogy g = d == null ? null : d.Genealogy;
            if (g == null) return 0;
            int n = 0;
            try { n += g.Parents.Count; n += g.Children.Count; n += g.GetSiblings().Count; } catch (Exception) { }
            return n;
        }

        static object FamilyTree(Args a)
        {
            if (a.Bool("status", false)) return FtStatus();
            if (a.Bool("stop", false)) { sFtGen++; return FtStatus(); }
            RequireLive();
            if (sFtRunning) throw new McpException("family_tree already running (stop: true first)");
            int opens = (int)a.Long("opens", 6), clicks = (int)a.Long("clicks", 8), waitMs = (int)a.Long("wait_ms", 400);
            const BindingFlags kAny = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
            Type dt = typeof(FamilyTreeDialog);
            MethodInfo show = dt.GetMethod("Show", kAny, null, new Type[] { typeof(IMiniSimDescription) }, null);
            FieldInfo sDlg = dt.GetField("sDialog", kAny), parentF = dt.GetField("mThumbParentWin", kAny);
            MethodInfo click = dt.GetMethod("OnFamilyTreeThumbClick", kAny), ok = dt.GetMethod("OnOKButtonClick", kAny);
            if (show == null || sDlg == null || parentF == null || click == null || ok == null)
                throw new McpException("FamilyTreeDialog members not found");
            if (!UIUtils.IsOkayToStartModalDialog()) throw new McpException("a modal dialog is already open");

            List<SimDescription> pick = new List<SimDescription>();
            foreach (Sim s in Sims3.Gameplay.Queries.GetObjects<Sim>())
                if (s != null && !s.HasBeenDestroyed && s.SimDescription != null && FtRelatives(s.SimDescription) > 0) pick.Add(s.SimDescription);
            pick.Sort(delegate(SimDescription x, SimDescription y) { return FtRelatives(y).CompareTo(FtRelatives(x)); });
            if (pick.Count == 0) throw new McpException("no Sim with relatives in the world");

            int gen = ++sFtGen;
            sFtOpened = sFtClicks = sFtThumbsSeen = sFtErrors = 0;
            sFtRunning = true;
            Random rnd = new Random(1234);
            Simulator.AddObject(new Sims3.Gameplay.OneShotFunctionTask(delegate
            {
                try
                {
                    for (int i = 0; i < opens && sFtGen == gen; i++)
                    {
                        SimDescription d = pick[i % pick.Count];
                        sFtLast = d.FullName;
                        // opener: blocks in StartModal until OK
                        bool done = false;
                        Simulator.AddObject(new Sims3.Gameplay.OneShotFunctionTask(delegate
                        {
                            try { show.Invoke(null, new object[] { d }); }
                            catch (Exception) { sFtErrors++; }
                            done = true;
                        }));
                        object dlg = null;
                        for (int w = 0; w < 200 && dlg == null && !done; w++) { Simulator.Sleep(10); dlg = sDlg.GetValue(null); }
                        if (dlg == null) { sFtErrors++; continue; }
                        sFtOpened++;
                        UIButtonClickEventArgs args = new UIButtonClickEventArgs();
                        args.Init(0, null, null, 0, 0, 0, 0f, 0f, 0f, 0f, false, null);
                        for (int c = 0; c < clicks && sFtGen == gen; c++)
                        {
                            WaitMs(waitMs);
                            WindowBase parent = parentF.GetValue(dlg) as WindowBase;
                            List<FamilyTreeThumb> thumbs = new List<FamilyTreeThumb>();
                            if (parent != null)
                                for (uint k = 0; ; k++)
                                {
                                    WindowBase ch = parent.GetChildByIndex(k);
                                    if (ch == null) break;
                                    FamilyTreeThumb t = ch as FamilyTreeThumb;
                                    if (t != null && t.SimDescription != null) thumbs.Add(t);
                                }
                            sFtThumbsSeen += thumbs.Count;
                            if (thumbs.Count == 0) break;
                            FamilyTreeThumb pickT = thumbs[rnd.Next(thumbs.Count)];
                            try { click.Invoke(dlg, new object[] { pickT, args }); sFtClicks++; }
                            catch (Exception) { sFtErrors++; }
                        }
                        WaitMs(waitMs);
                        try { ok.Invoke(dlg, new object[] { null, args }); } catch (Exception) { sFtErrors++; }
                        for (int w = 0; w < 300 && !done; w++) Simulator.Sleep(10);
                    }
                }
                catch (Exception) { sFtErrors++; }
                finally { sFtRunning = false; }
            }));
            return FtStatus();
        }

        static void WaitMs(int ms)
        {
            int start = System.Environment.TickCount;
            while (unchecked(System.Environment.TickCount - start) < ms) Simulator.Sleep(0);
        }
    }
}
