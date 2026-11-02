using System;
using System.Collections.Generic;
using Sims3.Gameplay;
using Sims3.SimIFace;
using GF = Sims3.Gameplay.Gameflow;
using GameSpeed = Sims3.SimIFace.Gameflow.GameSpeed;

namespace Ts3Agent
{
    // Test-harness autopilot, in-game so it reacts within a frame instead of the harness's 30 s loop:
    // holds the game at the target speed (the game drops to normal on events) and clears popups as they open.
    // Popups pause or lock the speed, so GameSpeedChanged triggers an immediate dialog check; a 250 ms poll
    // catches the rest. Dialogs in any in-world state (loading, Edit Town, live; never the main menu, where
    // accepting "Mod Scripts Found" during a load hangs it); speed in live mode only, and not at all with
    // speed "none". The harness turns it off around saves (a save pauses on purpose).
    public static partial class Handlers
    {
        // Settings and generation live in AppDomain data so they survive a hot reload; an old plugin's task
        // and event handler see a newer generation and stop.
        const string kApCfg = "ts3agent.autopilot.cfg", kApGen = "ts3agent.autopilot.gen";
        static int sApGen;
        static volatile bool sApPoke;
        static int sApSpeedFixes;
        static volatile int sApLastStep = int.MinValue / 2; // TickCount of the task's last step
        static readonly List<object> sApCleared = new List<object>();
        // every click line with its UTC time, never drained (cap 400): `cleared` is drained by each autopilot call, and a call whose reply the harness gave up on
        // (game busy loading a world, Round 6 vh-move) lost its lines. dialog_expect {status:true} and the autopilot reply `history` return it.
        static readonly List<object> sApHistory = new List<object>();
        static void ApHist(string line) { lock (sApHistory) { if (sApHistory.Count >= 400) sApHistory.RemoveAt(0); sApHistory.Add(System.DateTime.UtcNow.ToString("HH:mm:ss") + " " + line); } }
        // dialog_expect {seconds?, title?, accept?, rules?}: the harness is about to answer a dialog itself (auto_dialog accept:true); until it times out
        // (seconds 0 clears) the autopilot leaves a matching dialog alone (title = lowercase text fragment, none = any dialog).
        // accept:true (accept-on-match): the autopilot itself clicks the accepting button of the first matching dialog inside the window,
        // logs it and ends the window; no race with a harness poll that comes after the window (docs/236 H-move run 1). kDialogRules still
        // apply (e.g. Edit Town "save your town" -> just continue) unless rules:false (H-move: accept the move offer the move rule declines).
        // accept_all:true: every dialog is accepted, one log line per click, ends with "dialog_expect accept_all ended: N clicks". Without title the window is
        // `seconds`. With title the window `seconds` only waits for the first matching dialog (trigger); that click starts the accept_all window of all_seconds (180).
        // A window that ends without a match logs "dialog_expect expired (no match)".
        static int sExpectUntil;
        static string sExpectTitle, sExpectLogged;
        static bool sExpectAccept, sExpectMatched, sExpectRules = true, sExpectAll;
        // accept_all: every dialog in the window is accepted (rules and safe default off), the window stays open; reproduces the old unattended autopilot for a whole flow (docs/236 Round 6)
        static int sExpectAllClicks, sExpectAllTick, sExpectAllSec;
        static object sExpectAllModal;
        static string sApBlocked; // dialog the autopilot would not click (AutoDialog: no safe button), null once no modal is up; logged once per dialog

        static void StartAutopilot()
        {
            object g = AppDomain.CurrentDomain.GetData(kApGen);
            sApGen = (g is int ? (int)g : 0) + 1;
            AppDomain.CurrentDomain.SetData(kApGen, sApGen);
            int myGen = sApGen;
            GF.GameSpeedChanged += delegate (GameSpeed s, bool locked)
            {
                if (myGen == sApGen && (s == GameSpeed.Pause || locked)) sApPoke = true;
            };
            Simulator.AddObject(new AutopilotTask(myGen));
        }

        static Dictionary<string, object> ApCfg()
        {
            Dictionary<string, object> c = AppDomain.CurrentDomain.GetData(kApCfg) as Dictionary<string, object>;
            if (c == null)
            {
                c = new Dictionary<string, object>();
                c["enabled"] = false; c["speed"] = "ultra"; c["dialogs"] = true;
                AppDomain.CurrentDomain.SetData(kApCfg, c);
            }
            return c;
        }

        // autopilot {enabled?, speed?: normal|fast|ultra|none, dialogs?}: updates settings, returns them plus what it
        // did since the last call (speed corrections and cleared dialogs).
        static object Autopilot(Args a)
        {
            Dictionary<string, object> c = ApCfg();
            if (a.Has("enabled")) c["enabled"] = a.Bool("enabled", false);
            if (a.Has("speed")) c["speed"] = a.Str("speed");
            if (a.Has("dialogs")) c["dialogs"] = a.Bool("dialogs", true);
            // the task is a Simulator object: a world switch (travel, story-smoke 2026-10-23) can end it [inferred: the loader
            // restarts its own PollTask on every world load], and then no dialog is answered in the new world. A task that has not stepped for 2 s while in a world is started again.
            bool restarted = GameStates.IsInWorld() && System.Environment.TickCount - sApLastStep > 2000;
            if (restarted) StartAutopilot();
            Dictionary<string, object> r = Util.Obj();
            r["restarted"] = restarted;
            foreach (KeyValuePair<string, object> kv in c) r[kv.Key] = kv.Value;
            r["speed_fixes"] = sApSpeedFixes;
            r["cleared"] = new List<object>(sApCleared);
            r["blocked"] = sApBlocked;
            lock (sApHistory) r["history"] = new List<object>(sApHistory);
            sApSpeedFixes = 0;
            sApCleared.Clear();
            return r;
        }

        static object DialogExpect(Args a)
        {
            if (a.Bool("status", false)) // read only: the window stays as it is
            {
                Dictionary<string, object> st = Util.Obj();
                st["armed"] = sExpectUntil != 0; st["accept_all"] = sExpectAll; st["clicks"] = sExpectAllClicks;
                lock (sApHistory) st["history"] = new List<object>(sApHistory);
                return st;
            }
            int sec = (int)a.Long("seconds", 30);
            sExpectTitle = a.Has("title") ? a.Str("title").ToLowerInvariant() : null;
            sExpectAccept = a.Bool("accept", false);
            sExpectRules = a.Bool("rules", true);
            sExpectAll = a.Bool("accept_all", false); sExpectAllClicks = 0; sExpectAllModal = null;
            sExpectAllSec = (int)a.Long("all_seconds", 180);
            if (sExpectAll) { sExpectAccept = true; sExpectRules = false; }
            sExpectUntil = sec > 0 ? System.Environment.TickCount + Math.Min(sec, 900) * 1000 : 0;
            sExpectLogged = null; sExpectMatched = false;
            Dictionary<string, object> r = Util.Obj();
            r["seconds"] = sec; r["title"] = sExpectTitle; r["accept"] = sExpectAccept; r["rules"] = sExpectRules; r["accept_all"] = sExpectAll; r["all_seconds"] = sExpectAllSec;
            return r;
        }

        // true (and logged once per dialog) while the harness expects to answer this dialog itself; with accept:true it is answered here
        static bool ApExpected(Sims3.UI.WindowBase modal)
        {
            if (sExpectUntil == 0 || sExpectUntil - System.Environment.TickCount <= 0) return false;
            List<string> texts = new List<string>();
            CollectTexts(modal, texts, 0);
            string all = string.Join(" ", texts.ToArray());
            if (sExpectTitle != null && all.ToLowerInvariant().IndexOf(sExpectTitle) < 0) return false;
            sExpectMatched = true;
            string t = texts.Count > 0 ? texts[0] : "";
            if (sExpectAll)
            {
                if (sExpectTitle != null) { sExpectTitle = null; sExpectUntil = System.Environment.TickCount + Math.Min(sExpectAllSec, 900) * 1000; } // trigger: the accept_all window starts now
                // the dialog may stay modal for a few ticks after the click: do not click the same one twice within 2 s
                if (object.ReferenceEquals(modal, sExpectAllModal) && System.Environment.TickCount - sExpectAllTick < 2000) return true;
                sExpectAllModal = modal; sExpectAllTick = System.Environment.TickCount;
                Dictionary<string, object> args = new Dictionary<string, object>();
                args["accept"] = true; args["rules"] = false;
                string clicked;
                try { Dictionary<string, object> d = AutoDialog(new Args(args)) as Dictionary<string, object>; clicked = d != null ? d["clicked"] as string : "(no dialog)"; }
                catch (McpException e) { clicked = "(not clicked: " + e.Message + ")"; }
                sExpectAllClicks++;
                ApLog(t, "dialog_expect accept_all: " + clicked);
                if (sApCleared.Count < 200) sApCleared.Add("dialog_expect accept_all: " + t + " -> " + clicked);
                return true;
            }
            if (sExpectAccept)
            {
                Dictionary<string, object> args = new Dictionary<string, object>();
                args["accept"] = true; args["rules"] = sExpectRules;
                string clicked;
                try { Dictionary<string, object> d = AutoDialog(new Args(args)) as Dictionary<string, object>; clicked = d != null ? d["clicked"] as string : "(no dialog)"; }
                catch (McpException e) { clicked = "(not clicked: " + e.Message + ")"; }
                sExpectUntil = 0; // one accept per window
                ApLog(t, "dialog_expect accept: " + clicked);
                if (sApCleared.Count < 200) sApCleared.Add("dialog_expect accept: " + t + " -> " + clicked);
                return true;
            }
            if (t != sExpectLogged) { sExpectLogged = t; ApLog(t, "(left to the harness, dialog_expect)"); }
            return true;
        }

        // a window that ran out without any matching dialog: one line in the log and in `cleared` (the clicks poll reads it without ts3native)
        static void ApExpectExpiry()
        {
            if (sExpectUntil == 0 || sExpectUntil - System.Environment.TickCount > 0) return;
            sExpectUntil = 0;
            if (sExpectAll && sExpectMatched)
            {
                string end = "dialog_expect accept_all ended: " + sExpectAllClicks + " clicks";
                DiagLine("ap click: " + end);
                ApHist(end);
                if (sApCleared.Count < 200) sApCleared.Add(end);
                return;
            }
            if (sExpectMatched) return;
            string line = "dialog_expect expired (no match)" + (sExpectTitle != null ? ": " + sExpectTitle : "") + (sExpectAccept ? " [accept]" : "");
            DiagLine("ap click: " + line);
            ApHist(line);
            if (sApCleared.Count < 200) sApCleared.Add(line);
        }

        // safe-default clicks carry the full button list, so a run can tell which dialogs lacked a rule (docs/236 Round 6)
        static string ApClicked(Dictionary<string, object> d)
        {
            string c = d["clicked"] as string;
            object how; object buttons;
            if (c == null || !d.TryGetValue("how", out how) || (how as string) != "safe default" || !d.TryGetValue("buttons", out buttons)) return c;
            List<string> names = new List<string>();
            foreach (object o in (List<object>)buttons) names.Add(System.Text.RegularExpressions.Regex.Replace((string)o, @"^(#\d+) '(.*?)'( \(disabled\))? score -?\d+$", "$1 $2$3"));
            return c.Substring(0, c.Length - 1) + "; buttons: " + string.Join(", ", names.ToArray()) + ")";
        }

        static GameSpeed ApTarget(string s)
        {
            switch (s) { case "normal": return GameSpeed.Normal; case "fast": return GameSpeed.Double; default: return GameSpeed.Triple; }
        }

        // One ts3native log line per cleared dialog at once (UserDiag text channel, as ts3fixes DiagLine), so a crash keeps the click history.
        static void ApLog(string title, string clicked)
        {
            string t = (title ?? "").Replace("\r", " ").Replace("\n", " ");
            if (t.Length > 80) t = t.Substring(0, 80);
            DiagLine("ap click: " + t + " -> " + clicked);
            ApHist(t + " -> " + clicked);
        }

        static void AutopilotStep(ref int lastDialog, ref int lastSpeed)
        {
            ApExpectExpiry();
            Dictionary<string, object> c = ApCfg();
            if (!(bool)c["enabled"] || !GameStates.IsInWorld()) return;
            int now = System.Environment.TickCount;
            if ((bool)c["dialogs"] && (sApPoke || now - lastDialog >= 250))
            {
                sApPoke = false;
                lastDialog = now;
                Sims3.UI.WindowBase modal = Sims3.UI.UIManager.GetModalWindow();
                if (modal != null)
                {
                    if (ApExpected(modal)) return;
                    Dictionary<string, object> d;
                    try { d = AutoDialog(new Args(new Dictionary<string, object>())) as Dictionary<string, object>; }
                    catch (McpException e)
                    {
                        // the harness fails the run on this line (queue.ts mode guard): a click here could change the game state
                        if (e.Message != sApBlocked)
                        {
                            sApBlocked = e.Message;
                            if (e.Message.EndsWith("buttons: "))
                            {
                                // docs/236 Round 9: nothing to click (a notification or transition window): not a block line; the harness
                                // fails only when it stays (queue.ts vanillaBlockFor). Type logged to learn what it is.
                                string bl = "ap buttonless: " + System.Text.RegularExpressions.Regex.Replace(e.Message, @"^.*nothing clicked: '(.*)' buttons: $", "'$1'") + " " + modal.GetType().FullName;
                                DiagLine(bl); ApHist(bl); if (sApCleared.Count < 200) sApCleared.Add(bl);
                            }
                            else { ApLog("(not clicked)", e.Message); if (sApCleared.Count < 200) sApCleared.Add("BLOCKED " + e.Message); }
                        }
                        return;
                    }
                    sApBlocked = null;
                    if (d != null && sApCleared.Count < 200) sApCleared.Add(d["title"] + " -> " + ApClicked(d));
                    if (d != null) ApLog(d["title"] as string, ApClicked(d));
                    return; // let the dialog close before touching the speed
                }
                sApBlocked = null; // the window went away by itself (Round 9: a buttonless window stayed in `blocked` and failed vh-repro)
            }
            if (now - lastSpeed < 1000 || (string)c["speed"] == "none" || SubState() != "LiveModeState") return;
            lastSpeed = now;
            GameSpeed target = ApTarget((string)c["speed"]);
            if (!GF.GameSpeedLocked && GF.CurrentGameSpeed != target)
            {
                GF.SetGameSpeed(target, GF.SetGameSpeedContext.User);
                sApSpeedFixes++;
            }
        }

        class AutopilotTask : Task
        {
            readonly int mGen;
            public AutopilotTask(int gen) { mGen = gen; }

            public override void Simulate()
            {
                int lastDialog = 0, lastSpeed = 0;
                while (mGen == sApGen && object.Equals(AppDomain.CurrentDomain.GetData(kApGen), mGen))
                {
                    sApLastStep = System.Environment.TickCount;
                    try { AutopilotStep(ref lastDialog, ref lastSpeed); }
                    catch (Exception) { }
                    Simulator.Sleep(0);
                }
                Simulator.DestroyObject(ObjectId);
            }
        }
    }
}
