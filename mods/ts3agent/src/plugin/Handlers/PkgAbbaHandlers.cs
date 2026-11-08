using System;
using System.Collections.Generic;
using Sims3.Gameplay;
using Sims3.Gameplay.UI;
using Sims3.SimIFace;
using Sims3.UI;
using Sims3.UI.GameEntry;

namespace Ts3Agent
{
    // docs/539 §13.9.5 repro pkg-abba (repros.json): EA's package-lock ABBA at the main menu. FORCED, not a player flow: with
    // ts3native [PkgLock] ReproWiden=N the main thread's idle close holds the file lock N ms on every update; these save-list
    // reads (GetSaveFileDetails, the first step of the LoadSave handler and of EA's Load screen) make a worker reopen the save
    // packages, which takes the database lock first. Mode 0 should deadlock ("[PkgLock] ABBA:" line), Mode 1 finish.
    public static partial class Handlers
    {
        static int sPkReads, sPkWant;
        static bool sPkDone;
        static string sPkError, sPkFirst;

        static void RegisterPkgAbba()
        {
            Dispatcher.Register("pkg_abba_repro", PkgAbbaRepro);
        }

        // pkg_abba_repro {arm: true, file: "<job save>", files?: [...], reads?: 30}: starts the reads on a sim task at the menu, returns
        // {pending: true} (cycle.ts then polls {status: true} until done). {status: true}: {reads, want, done, error}.
        static object PkgAbbaRepro(Args a)
        {
            Dictionary<string, object> r = Util.Obj();
            if (!a.Bool("status", false))
            {
                if (GameStates.IsInWorld()) throw new McpException("pkg_abba_repro runs at the main menu");
                List<string> files = PkSaveList(a);
                if (files.Count == 0) throw new McpException("no save to read (pass file)");
                sPkReads = 0; sPkWant = Math.Max(1, (int)a.Long("reads", 30)); sPkDone = false; sPkError = null; sPkFirst = files[0];
                RunAsync("pkg_abba_repro", delegate
                {
                    try
                    {
                        for (int i = 0; i < sPkWant; i++)
                        {
                            SaveGameMetadata info = new SaveGameMetadata();
                            info.mSaveFile = files[i % files.Count];
                            Sims3.Gameplay.UI.Responder.Instance.MainMenuModel.GetSaveFileDetails(ref info);
                            sPkReads++;
                        }
                    }
                    catch (Exception e) { sPkError = DescribeError(e); }
                    sPkDone = true;
                });
                r["pending"] = true;
                r["saves"] = files.Count;
            }
            r["reads"] = sPkReads;
            r["want"] = sPkWant;
            r["done"] = sPkDone;
            r["first"] = sPkFirst;
            if (sPkError != null) r["error"] = sPkError;
            return r;
        }

        // The job's save (cycle.ts passes `file` with arm), plus `files` when given. shortcut: no folder listing (the game's
        // mscorlib has no System.IO.Directory); one save read 30 times still reopens it on every read while ReproWiden closes
        // it on every update. Pass `files` for more packages if one save does not trigger it.
        static List<string> PkSaveList(Args a)
        {
            List<string> files = new List<string>();
            List<object> more = a.Has("files") ? a.List("files") : null;
            if (more != null) foreach (object f in more) if (f != null) files.Add(PkSaveName(f.ToString()));
            string file = a.Str("file", "");
            if (file.Length > 0) files.Insert(0, PkSaveName(file));
            return files;
        }

        static string PkSaveName(string f) { return f.EndsWith(".sims3", StringComparison.OrdinalIgnoreCase) ? f : f + ".sims3"; }
    }
}
