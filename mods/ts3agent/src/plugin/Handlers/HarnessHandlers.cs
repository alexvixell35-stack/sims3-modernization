using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.SimIFace;
using Sims3.SimIFace.Enums;
using Sims3.UI;
using Sims3.UI.GameEntry;

namespace Ts3Agent
{
    // Test-harness commands (not in upstream Sims3-MCP): drive the game from the main menu to a playable,
    // saved world without clicks, using the same calls as EA's menu code (MainMenuState, PlayFlowModel).
    public static partial class Handlers
    {
        static void RegisterHarness()
        {
            Dispatcher.Register("game_state", GameState);
            Dispatcher.Register("load_world", LoadWorld);
            Dispatcher.Register("load_save", LoadSave);
            Dispatcher.Register("world_households", WorldHouseholds);
            Dispatcher.Register("houseboats", Houseboats);
            RegisterRepro();
            RegisterRare();
            RegisterRareVehicles();
            RegisterCas();
            RegisterContent();
            RegisterMainThreadRepro();
            RegisterLabels();
            RegisterTravel();
            RegisterRouteFreeze();
            RegisterStory();
            RegisterHouseholds();
            RegisterStartPaths();
            Dispatcher.Register("patch_test", PatchTest);
            Dispatcher.Register("method_handle", MethodHandle);
            Dispatcher.Register("patch_test_cold", PatchTestCold);
            Dispatcher.Register("patches", Patches);
            Dispatcher.Register("play_household", PlayHousehold);
            Dispatcher.Register("play_existing", PlayExisting);
            Dispatcher.Register("ui_single_task_probe", UiSingleTaskProbe);
            Dispatcher.Register("ui_single_task_result", UiSingleTaskResult);
            Dispatcher.Register("quit_game", QuitGame);
            Dispatcher.Register("main_menu", GoMainMenu);
            Dispatcher.Register("auto_dialog", AutoDialog);
            Dispatcher.Register("enter_live_mode", EnterLiveMode);
            Dispatcher.Register("reflect_call_async", ReflectCallAsync);
            Dispatcher.Register("sims_activity", SimsActivity);
            Dispatcher.Register("keep_alive", KeepAlive);
            Dispatcher.Register("decline_requests", DeclineRequests);
            Dispatcher.Register("queues", Queues);
            Dispatcher.Register("seed_rng", SeedRng);
            Dispatcher.Register("test_env", TestEnv);
            RegisterFreshWorld();
            Dispatcher.Register("quiet_ui", QuietUi);
            Dispatcher.Register("autopilot", Autopilot);
            RegisterGates(); // docs/179: game-mode gates wrap the handlers registered above
            StartAutopilot();
        }

        // Less on screen during unattended runs: turn the memory system off (no memory popups, photos or
        // bookkeeping; the game checks the option each time) and clear the notifications in the top-right corner.
        static object QuietUi(Args a)
        {
            Dictionary<string, object> r = Util.Obj();
            if (a.Bool("memories_off", true))
            {
                // EnableMemories stores MemoriesMode + 1: Enabled 1, NotificationsDisabled 2, Disabled 3
                Sims3.Gameplay.UI.OptionsModel.SetOptionSetting("EnableMemories", 3u);
                r["memories"] = Sims3.Gameplay.UI.OptionsModel.GetMemoryMode().ToString();
            }
            NotificationManager nm = NotificationManager.Instance;
            if (nm != null) { nm.ClearAll(); r["notifications"] = "cleared"; }
            return r;
        }

        // Diagnostic: every queued interaction of the active household, with its runtime type and target.
        static object Queues(Args a)
        {
            Household hh = Household.ActiveHousehold;
            if (hh == null) throw new McpException("no active household");
            List<object> list = new List<object>();
            foreach (Sim s in hh.AllActors)
            {
                if (s.InteractionQueue == null) continue;
                foreach (InteractionInstance i in s.InteractionQueue.InteractionList)
                {
                    Dictionary<string, object> o = Util.Obj();
                    o["sim"] = s.FirstName;
                    string name;
                    try { name = i.GetInteractionName(); } catch (Exception) { name = null; }
                    o["name"] = name;
                    o["type"] = i.GetType().FullName;
                    Sim t = i.Target as Sim;
                    o["target"] = t != null ? t.FullName : (i.Target != null ? i.Target.GetType().Name : null);
                    o["target_in_household"] = t != null && t.Household == hh;
                    o["autonomous"] = i.Autonomous;
                    list.Add(o);
                }
            }
            return list;
        }

        // Socials that other Sims start with our household (chat, ask to dance...): our side runs as a
        // SocialInteractionB, while socials our Sims start themselves run as SocialInteractionA. Cancelling the
        // B side is what clicking it away in the queue does. Optionally ignores requests from household members.
        static object DeclineRequests(Args a)
        {
            Household hh = Household.ActiveHousehold;
            if (hh == null) throw new McpException("no active household");
            bool fromHousehold = a.Bool("include_household", false);
            List<object> declined = new List<object>();
            foreach (Sim s in hh.AllActors)
            {
                if (s.InteractionQueue == null) continue;
                List<InteractionInstance> incoming = new List<InteractionInstance>();
                foreach (InteractionInstance i in s.InteractionQueue.InteractionList)
                {
                    // answering an incoming call ("Chat with X" on the phone) is the other common kind of request
                    if (i.GetType().FullName.EndsWith("Phone+Answer")) { incoming.Add(i); continue; }
                    if (!(i is Sims3.Gameplay.Socializing.SocialInteractionB)) continue;
                    Sim other = i.Target as Sim;
                    if (!fromHousehold && other != null && other.Household == hh) continue;
                    incoming.Add(i);
                }
                foreach (InteractionInstance i in incoming)
                {
                    Sim other = i.Target as Sim;
                    string what;
                    try { what = i.GetInteractionName(); } catch (Exception) { what = i.GetType().Name; }
                    s.InteractionQueue.CancelInteraction(i, false);
                    declined.Add(s.FirstName + ": '" + what + "'" + (other != null ? " from " + other.FullName : ""));
                }
            }
            Dictionary<string, object> r = Util.Obj();
            r["declined"] = declined;
            return r;
        }

        // Safety net for unattended runs (like NRaas MasterController's max motives, but only in the danger zone):
        // any active-household motive below the threshold goes to full. Above it the Sims still eat, sleep and use
        // the toilet themselves, so autonomy and routing load stay realistic.
        static object KeepAlive(Args a)
        {
            Household hh = Household.ActiveHousehold;
            if (hh == null) throw new McpException("no active household");
            float threshold = (float)a.Double("threshold", -50);
            List<object> topped = new List<object>();
            foreach (Sim s in hh.AllActors)
            {
                Motives m = s.Motives;
                if (m == null) continue;
                foreach (CommodityKind k in kShownMotives)
                {
                    if (k == CommodityKind.Temperature || !m.HasMotive(k)) continue;
                    float v = m.GetValue(k);
                    if (v >= threshold) continue;
                    m.SetMax(k);
                    topped.Add(s.FirstName + " " + k + " " + Math.Round(v));
                }
            }
            // Test-only upkeep: broken home objects (sink, bathtub, toilet...) mean puddles, bad moods and repair
            // interactions that skew the measurement, so fix them silently (same as the game's DEBUG Force Repaired).
            List<object> repaired = new List<object>();
            if (hh.LotHome != null)
            {
                foreach (IGameObject o in hh.LotHome.GetObjects<IGameObject>())
                {
                    if (o == null || o.Repairable == null || !o.Repairable.Broken) continue;
                    o.Repairable.ForceRepaired(null);
                    repaired.Add(o.GetType().Name);
                }
            }
            Dictionary<string, object> r = Util.Obj();
            r["topped_up"] = topped;
            r["repaired"] = repaired;
            return r;
        }

        // Every instantiated Sim with where it is and what it is doing; the watchdog diffs this over time to spot
        // Sims that stay on the same interaction at the same spot (stuck) or keep routing without arriving.
        static object SimsActivity(Args a)
        {
            List<object> list = new List<object>();
            foreach (Sim s in Sims3.Gameplay.Queries.GetObjects<Sim>())
            {
                if (s == null || s.SimDescription == null) continue;
                Dictionary<string, object> o = Util.Obj();
                o["id"] = Util.Id(s.ObjectId);
                o["name"] = s.FullName;
                Vector3 p = s.Position;
                o["x"] = Math.Round(p.x, 1);
                o["z"] = Math.Round(p.z, 1);
                o["lot"] = s.LotCurrent != null ? s.LotCurrent.Name : null;
                InteractionInstance cur = null;
                try { cur = s.CurrentInteraction; } catch (Exception) { }
                string curName = null;
                if (cur != null) { try { curName = cur.GetInteractionName(); } catch (Exception) { curName = cur.GetType().Name; } }
                o["interaction"] = curName;
                o["queue"] = s.InteractionQueue != null ? s.InteractionQueue.Count : 0;
                bool routing = false;
                try { routing = s.SimRoutingComponent != null && s.SimRoutingComponent.IsRouting; } catch (Exception) { }
                o["routing"] = routing;
                o["in_transit"] = InTransit(s);  // docs/202 §3.7: ts3fixes unseenfbpre walk-time hold (stuck-rule.ts: never stuck)
                o["active_household"] = s.Household != null && s.Household == Household.ActiveHousehold;
                list.Add(o);
                if (list.Count >= 400) break;
            }
            return list;
        }

        // ts3fixes' hold table (AppDomain ts3fixes.unseenfb.holding: Sim object id -> hold end in sim ticks), as UfPreCore.InTransit
        static bool InTransit(Sim s)
        {
            Dictionary<ulong, long> d = AppDomain.CurrentDomain.GetData("ts3fixes.unseenfb.holding") as Dictionary<ulong, long>;
            long until;
            return d != null && d.TryGetValue(s.ObjectId.Value, out until) && Sims3.Gameplay.Utilities.SimClock.CurrentTicks <= until;
        }

        // Current in-world sub state (LiveMode, EditTown, BuildMode, CAS...), read from GameStates' private state machines.
        static string SubState()
        {
            try
            {
                object gs = typeof(GameStates).GetField("sSingleton", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
                object inWorld = typeof(GameStates).GetField("mInWorldState", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(gs);
                if (inWorld == null) return null;
                object sm = inWorld.GetType().GetField("mStateMachine", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(inWorld);
                object cur = sm.GetType().GetProperty("CurState").GetValue(sm, null);
                if (cur == null) return null;
                // mods replace game states with subclasses (NRaas Traveler: LiveModeStateEx): report the game's own state name
                Type t = cur.GetType();
                while (t.BaseType != null && t.Assembly != typeof(GameStates).Assembly) t = t.BaseType;
                return t.Name;
            }
            catch (Exception e) { return "unknown: " + e.Message; }
        }

        // Edit Town pauses the clock; this is what the Edit Town "Play" button ends in.
        static object EnterLiveMode(Args a)
        {
            RunAsync("enter_live_mode", delegate { GameStates.TransitionToLiveMode(); });
            Dictionary<string, object> r = Util.Obj();
            r["from"] = SubState();
            return r;
        }

        // reflect_call for methods that yield (state transitions, waits): runs as a game task, result lands in async_log.
        static object ReflectCallAsync(Args a)
        {
            RunAsync("reflect_call " + a.Str("path"), delegate { ReflectCall(a); });
            Dictionary<string, object> r = Util.Obj();
            r["queued"] = a.Str("path");
            return r;
        }

        // ------------------------------------------------------------ blocking dialogs

        static readonly string[] kAcceptWords = { "ok", "accept", "yes", "continue", "done", "confirm", "close", "got it" };
        static readonly string[] kRejectWords = { "cancel", "no", "back", "quit", "exit", "decline", "never", "customize" }; // customize opens a second picker (lifetime wish) the autopilot cannot answer
        static readonly string[] kNames = { "Alex", "Sam", "Jamie", "Robin", "Casey", "Morgan", "Riley", "Quinn", "Avery", "Reese" };
        static int sNameIndex;

        static void FindTextEdits(WindowBase w, List<TextEdit> found, int depth)
        {
            if (w == null || depth > 12) return;
            TextEdit te = w as TextEdit;
            if (te != null && te.Visible) found.Add(te);
            for (uint i = 0; i < 200; i++)
            {
                WindowBase child;
                try { child = w.GetChildByIndex(i); } catch (Exception) { break; }
                if (child == null) break;
                FindTextEdits(child, found, depth + 1);
            }
        }

        static string Words(WindowBase b)
        {
            string tip = null;
            Button button = b as Button;
            if (button != null) { try { tip = button.TooltipText; } catch (Exception) { } }
            return ((b.Caption ?? "") + " " + (tip ?? "")).Trim().ToLowerInvariant();
        }

        // Known dialogs where "accept" is the wrong answer for an unattended test run: the first rule whose text
        // fragment appears in the dialog picks the first button containing one of its preferred words.
        // Anything else falls back to Score(). Text matching is plain substrings (lowercase).
        static readonly string[][][] kDialogRules = {
            new[] { new[] { "placing the following lots", "new lots available" }, new[] { "don't place", "dont place", "no" } },
            new[] { new[] { "save your town before continuing" }, new[] { "just continue", "continue" } },
            new[] { new[] { "name your saved game" }, new[] { "cancel" } },
            new[] { new[] { "come over", "invite", "would like to visit", "wants to visit", "join them" }, new[] { "decline", "not now", "no", "cancel" } },
            new[] { new[] { "mod scripts found" }, new[] { "accept", "ok", "continue" } },
        };

        static string[] RuleFor(string text)
        {
            foreach (string[][] rule in kDialogRules)
                foreach (string frag in rule[0])
                    if (text.IndexOf(frag) >= 0) return rule[1];
            return null;
        }

        static int Score(string words)
        {
            foreach (string w in kRejectWords) if (words == w || words.StartsWith(w + " ")) return -1;
            foreach (string w in kAcceptWords) if (words == w || words.StartsWith(w + " ") || words.IndexOf(w) >= 0) return 2;
            return words.Length == 0 ? 0 : 1;
        }

        // EA's lifetime wish dialog (a Sim ages up): Okay stays disabled until a wish is picked, and the buttons without
        // a wish tag open WishPickerDialog, whose OK also waits for a pick. Button scoring cannot answer either, so use
        // their own handlers: cancel an open picker, else select the first offered wish and press Okay.
        static bool AnswerLifetimeWish(Dictionary<string, object> r)
        {
            const BindingFlags kAny = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
            Assembly ui = typeof(ModalDialog).Assembly;
            Type pickerType = ui.GetType("Sims3.UI.WishPickerDialog"), wishType = ui.GetType("Sims3.UI.LifetimeWishSelectionDialog");
            object picker = pickerType != null ? pickerType.GetField("sDialog", kAny).GetValue(null) : null;
            object dlg = wishType != null ? wishType.GetField("sDialog", kAny).GetValue(null) : null;
            if (picker == null && dlg == null) return false;
            r["title"] = picker != null ? "lifetime wish picker" : "lifetime wish";
            object wish = null;
            if (picker == null)
                foreach (string f in new string[] { "mPossibleWishes", "mGenericWishes" })
                {
                    System.Collections.IList l = wishType.GetField(f, kAny).GetValue(dlg) as System.Collections.IList;
                    if (l != null && l.Count > 0) { wish = l[0]; break; }
                }
            if (picker == null && wish == null) { r["clicked"] = "(no wish offered)"; return false; }
            object target = picker ?? dlg;
            MethodInfo select = wishType.GetMethod("SelectWish", kAny);
            MethodInfo press = target.GetType().GetMethod(picker != null ? "OnCancelButtonClick" : "OnOkayButtonClick", kAny);
            UIButtonClickEventArgs args = new UIButtonClickEventArgs();
            args.Init(kButtonClick, null, null, 0, 0, 0, 0f, 0f, 0f, 0f, false, null);
            RunAsync("auto_dialog " + r["title"], delegate
            {
                if (picker == null) select.Invoke(dlg, new object[] { wish });
                press.Invoke(target, new object[] { null, args });
            });
            r["clicked"] = picker != null ? "picker cancel" : "first wish + okay";
            return true;
        }

        // Clears whatever modal dialog blocks the game: fills empty text fields (e.g. naming a newborn),
        // then clicks the most "accepting" enabled button. Prefers tooltips because EA icon buttons have no caption.
        static object AutoDialog(Args a)
        {
            WindowBase modal = UIManager.GetModalWindow();
            Dictionary<string, object> r = Util.Obj();
            if (modal == null) { r["open"] = false; return r; }
            r["open"] = true;
            // a start-path step answers this dialog itself (StartPathHandlers: purchase confirm, Edit Town place dialog)
            if (HarnessOwnsDialog()) { r["title"] = null; r["clicked"] = "(left to a harness step)"; return r; }
            if (AnswerLifetimeWish(r)) return r;
            List<string> texts = new List<string>();
            CollectTexts(modal, texts, 0);
            r["title"] = texts.Count > 0 ? texts[0] : null;
            string allText = string.Join(" ", texts.ToArray()).ToLowerInvariant();
            string[] preferred = RuleFor(allText);
            r["rule"] = preferred != null ? preferred[0] : null;

            KeyValuePair<WindowBase, object>? best = null;
            int bestScore = int.MinValue;
            List<object> seen = new List<object>();
            List<KeyValuePair<WindowBase, object>> buttons = DialogButtons(modal);
            if (preferred != null)
            {
                foreach (string want in preferred)
                {
                    foreach (KeyValuePair<WindowBase, object> kv in buttons)
                    {
                        bool en = true;
                        try { en = kv.Key.Enabled; } catch (Exception) { }
                        if (en && Words(kv.Key).IndexOf(want) >= 0) { best = kv; bestScore = 100; break; }
                    }
                    if (best != null) break;
                }
            }

            // Only fill name fields when we are about to accept (a newborn); never for a dialog we cancel.
            List<object> filled = new List<object>();
            if (preferred == null)
            {
                List<TextEdit> edits = new List<TextEdit>();
                FindTextEdits(modal, edits, 0);
                foreach (TextEdit te in edits)
                {
                    if (!string.IsNullOrEmpty(te.Caption)) continue;
                    string name = kNames[sNameIndex++ % kNames.Length];
                    te.Caption = name;
                    filled.Add(name);
                }
            }
            r["filled"] = filled;

            foreach (KeyValuePair<WindowBase, object> kv in buttons)
            {
                string words = Words(kv.Key);
                bool enabled = true;
                try { enabled = kv.Key.Enabled; } catch (Exception) { }
                int s = enabled ? Score(words) : -2;
                seen.Add("#" + kv.Key.ID + " '" + words + "'" + (enabled ? "" : " (disabled)") + " score " + s);
                if (s > bestScore) { bestScore = s; best = kv; }
            }
            r["buttons"] = seen;
            if (best == null || bestScore < 0) throw new McpException("no acceptable button: " + string.Join(", ", seen.ConvertAll(o => (string)o).ToArray()));
            WindowBase btn = best.Value.Key;
            object glue = best.Value.Value;
            MethodInfo invoke = glue.GetType().GetMethod("Invoke", new Type[] { typeof(WindowBase), typeof(UIEventArgs) });
            UIButtonClickEventArgs args = new UIButtonClickEventArgs();
            args.Init(kButtonClick, btn, btn, (int)btn.ID, 0, 0, 0f, 0f, 0f, 0f, false, null);
            RunAsync("auto_dialog #" + btn.ID, delegate { invoke.Invoke(glue, new object[] { btn, args }); });
            r["clicked"] = "#" + btn.ID + " " + Words(btn);
            return r;
        }

        static object GameState(Args a)
        {
            Dictionary<string, object> r = Util.Obj();
            r["in_world"] = GameStates.IsInWorld();
            r["sub_state"] = SubState();
            r["shutting_down"] = GameStates.IsGameShuttingDown;
            Household active = Household.ActiveHousehold;
            r["active_household"] = active != null ? active.Name : null;
            r["households"] = Household.sHouseholdList != null ? Household.sHouseholdList.Count : 0;
            r["save_name"] = Sims3.Gameplay.UI.Responder.Instance.OptionsModel.SaveName;
            r["load_file"] = GameStates.LoadFileName; // set by load_save too; save_name stays null until a save
            r["save_in_progress"] = Sims3.Gameplay.UI.Responder.Instance.OptionsModel.SaveGameInProgress;
            return r;
        }

        static InWorldState.SubState ParseState(string s)
        {
            return (InWorldState.SubState)Enum.Parse(typeof(InWorldState.SubState), s, true);
        }

        // file: a world such as "Sunset Valley.world" (new game) ; state: LiveMode (default), PlayFlow or EditTown.
        static object LoadWorld(Args a)
        {
            if (GameStates.IsInWorld()) throw new McpException("already in a world; quit to the main menu first");
            string file = a.Str("file");
            InWorldState.SubState state = ParseState(a.Str("state", "LiveMode"));
            WorldFileMetadata info = new WorldFileMetadata();
            info.mWorldFile = file;
            if (!Sims3.Gameplay.UI.Responder.Instance.MainMenuModel.GetWorldFileDetails(ref info))
                throw new McpException("unknown world file '" + file + "'");
            RunAsync("load_world " + file, delegate { GameStates.TransitionToGameStateToInWorld(file, info, state, true); });
            Dictionary<string, object> r = Util.Obj();
            r["loading"] = file;
            r["state"] = state.ToString();
            return r;
        }

        // file: a save such as "Baseline-SV.sims3" from Documents\Electronic Arts\The Sims 3\Saves.
        static object LoadSave(Args a)
        {
            // reload: true loads another save from inside a world with EA's in-game load path (ReloadWorld), so several
            // saves can be made in one game session (spring base saves, 2026-10-17)
            bool reload = a.Bool("reload", false);
            if (GameStates.IsInWorld() && !reload) throw new McpException("already in a world; quit to the main menu first, or pass reload: true");
            string file = a.Str("file");
            SaveGameMetadata info = new SaveGameMetadata();
            info.mSaveFile = file;
            if (!Sims3.Gameplay.UI.Responder.Instance.MainMenuModel.GetSaveFileDetails(ref info))
                throw new McpException("unknown save '" + file + "'");
            if (GameStates.IsInWorld())
                RunAsync("load_save " + file, delegate { GameStates.TransitionToGameStateReloadWorld(file, info, InWorldState.SubState.LiveMode, true); });
            else
                RunAsync("load_save " + file, delegate { GameStates.TransitionToGameStateToInWorld(file, info, InWorldState.SubState.LiveMode, true); });
            Dictionary<string, object> r = Util.Obj();
            r["loading"] = file;
            return r;
        }

        // Households that live on a lot, so a fresh world can start with a premade family.
        static object WorldHouseholds(Args a)
        {
            List<object> list = new List<object>();
            foreach (Household h in Household.sHouseholdList)
            {
                if (h == null || h.LotHome == null || h.IsServiceNpcHousehold || h.IsSpecialHousehold) continue;
                Dictionary<string, object> o = Util.Obj();
                o["name"] = h.Name;
                o["id"] = Util.Id(h.HouseholdId);
                o["members"] = h.NumMembers;
                o["lot"] = h.LotHome.Name;
                list.Add(o);
            }
            return list;
        }

        // Every houseboat with its position and household, so a profile run can count which ones moved.
        static object Houseboats(Args a)
        {
            List<object> list = new List<object>();
            foreach (Sims3.Gameplay.Objects.Island.Houseboat b in Sims3.Gameplay.Queries.GetObjects<Sims3.Gameplay.Objects.Island.Houseboat>())
            {
                Dictionary<string, object> o = Util.Obj();
                Lot lot = b.GetHouseboatLot();
                o["lot"] = lot != null ? lot.Name : null;
                o["port"] = b.LotCurrent != null ? b.LotCurrent.Name : null;
                o["household"] = lot != null && lot.Household != null ? lot.Household.Name : null;
                o["x"] = Math.Round(b.Position.x, 1);
                o["z"] = Math.Round(b.Position.z, 1);
                list.Add(o);
            }
            return list;
        }

        // Makes a household the active one and switches to live mode, as PlayFlowModel does after "Play".
        static object PlayHousehold(Args a)
        {
            string name = a.Str("name");
            Household target = null;
            foreach (Household h in Household.sHouseholdList)
                if (h != null && h.LotHome != null && string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase)) { target = h; break; }
            if (target == null) throw new McpException("no household on a lot named '" + name + "'");
            RunAsync("play_household " + name, delegate
            {
                PlumbBob.ForceSelectActor(target.GetRandomNonRoommateSim());
                target.LotHome.EnsureLotObjects();
                // PlayFlowModel.GotoLiveMode is private; call it when the play flow is up, else switch directly.
                MethodInfo m = typeof(PlayFlowModel).GetMethod("GotoLiveMode", BindingFlags.NonPublic | BindingFlags.Instance);
                if (PlayFlowModel.Singleton != null && m != null) m.Invoke(PlayFlowModel.Singleton, null);
                else GameStates.TransitionToLiveMode();
            });
            Dictionary<string, object> r = Util.Obj();
            r["playing"] = target.Name;
            return r;
        }

        // The family picker's Play arrow on an existing household: PlayFlowModel.PlayExisting(lot) -> MiniLoad with
        // waitForSims, the path players take (play_household skips that wait). Needs the play flow (load_world state PlayFlow).
        static object PlayExisting(Args a)
        {
            string name = a.Str("name");
            Household target = null;
            foreach (Household h in Household.sHouseholdList)
                if (h != null && h.LotHome != null && string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase)) { target = h; break; }
            if (target == null) throw new McpException("no household on a lot named '" + name + "'");
            if (PlayFlowModel.Singleton == null) throw new McpException("the play flow is not up (load_world with state PlayFlow)");
            ulong lotId = target.LotHome.LotId;
            Household hh = target;
            RunAsync("play_existing " + name, delegate
            {
                // a world loaded by load_world has no picker UI (PlayFlowController.Singleton null): ActivateExisting would
                // throw at HidePlayFlowUI, so wait for it briefly, else run its MiniLoad with the model's own callbacks
                for (int i = 0; i < 120 && Sims3.UI.GameEntry.PlayFlowController.Singleton == null; i++) Simulator.Sleep(0);
                if (Sims3.UI.GameEntry.PlayFlowController.Singleton != null) { PlayFlowModel.Singleton.PlayExisting(lotId); return; }
                // still call it once: hooks on PlayExisting (ts3fixes play trace) run and log; without the UI it throws at
                // HidePlayFlowUI before MiniLoad, then the fallback below does that MiniLoad
                try { PlayFlowModel.Singleton.PlayExisting(lotId); } catch (Exception) { }
                const BindingFlags np = BindingFlags.NonPublic | BindingFlags.Instance;
                PlayFlowModel m = PlayFlowModel.Singleton;
                BoolFuncWithParam masked = (BoolFuncWithParam)Delegate.CreateDelegate(typeof(BoolFuncWithParam), m, typeof(PlayFlowModel).GetMethod("ActivateSimScreenMaskedFunc", np));
                BoolFuncWithParam loaded = (BoolFuncWithParam)Delegate.CreateDelegate(typeof(BoolFuncWithParam), m, typeof(PlayFlowModel).GetMethod("LotsLoadedFunc", np));
                BoolFuncWithParam unmasked = (BoolFuncWithParam)Delegate.CreateDelegate(typeof(BoolFuncWithParam), m, typeof(PlayFlowModel).GetMethod("ScreenUnmaskedFunc", np));
                Sims3.UI.GameEntry.MiniLoad.Show(true, lotId, masked, loaded, unmasked, new Tuple(hh, hh.LotHome));
            });
            Dictionary<string, object> r = Util.Obj();
            r["playing"] = target.Name;
            r["lot"] = lotId;
            return r;
        }

        // Queues a UI single task the way the family picker's Play arrow does (OneShotFunctionTask(f, singleTask: true): it only
        // runs once UIManager.TaskCount is 1); ui_single_task_result says whether it ran. A counted endless script task of a mod
        // keeps it from ever running (ts3fixes v0.2.0-v0.2.5: Play grayed out, the family never loaded).
        static bool sSingleRan;
        static object UiSingleTaskProbe(Args a)
        {
            sSingleRan = false;
            long before = Sims3.UI.UIManager.TaskCount;
            Simulator.AddObject(new Sims3.UI.OneShotFunctionTask(delegate { sSingleRan = true; }, true));
            // no waiting task here: a Gameplay OneShotFunctionTask counts in TaskCount too and would hold the probe back itself
            Dictionary<string, object> r = Util.Obj();
            r["ui_tasks_before"] = before;
            r["note"] = "read ui_single_task_result after ~5 s";
            return r;
        }

        static object UiSingleTaskResult(Args a)
        {
            Dictionary<string, object> r = Util.Obj();
            r["ran"] = sSingleRan;
            r["ui_tasks"] = Sims3.UI.UIManager.TaskCount;
            return r;
        }

        static object QuitGame(Args a)
        {
            RunAsync("quit_game", delegate { GameStates.TransitionToGameStateQuitNoCheck(); });
            Dictionary<string, object> r = Util.Obj();
            r["quitting"] = true;
            return r;
        }

        // main_menu: Options > Quit to Main Menu > Quit without Saving (save first with save_game), the call EA's own
        // InWorldState.ExitToMainMenuNoCheck makes: GameStates.TransitionToLeaveInWorld (world unload -> MainMenu state).
        // Refused where EA's menu entry is blocked (CAS, Build/Buy, a sub-state switch running) or a save is running.
        // docs/204 measurement (save-reload.ts): unload + in-process load_save of the same save.
        static object GoMainMenu(Args a)
        {
            if (!GameStates.IsInWorld()) throw new McpException("not in a world");
            if (GameStates.IsCasState || GameStates.IsBuildBuyLikeState) throw new McpException("leave CAS / Build / Buy first (state " + SubState() + ")");
            if (Sims3.Gameplay.UI.Responder.Instance.OptionsModel.SaveGameInProgress) throw new McpException("a save is in progress");
            if (UIManager.GetModalWindow() != null) throw new McpException("close the open dialog first");
            RunAsync("main_menu", delegate { GameStates.TransitionToLeaveInWorld(); });
            Dictionary<string, object> r = Util.Obj();
            r["leaving"] = SubState();
            return r;
        }

        // xorshift128+ seeded with splitmix64: the script RNG stream for seed_rng (docs/27 §B). NextDouble in [0,1).
        sealed class SeededRandom : IRandom
        {
            ulong mA, mB;
            public SeededRandom(long seed)
            {
                ulong z = (ulong)seed;
                mA = Mix(ref z);
                mB = Mix(ref z);
            }
            static ulong Mix(ref ulong z)
            {
                ulong x = z += 0x9E3779B97F4A7C15UL;
                x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
                x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
                return x ^ (x >> 31);
            }
            public double NextDouble()
            {
                ulong x = mA, y = mB;
                mA = y;
                x ^= x << 23;
                mB = x ^ y ^ (x >> 17) ^ (y >> 26);
                return (mB + y >> 11) * (1.0 / 9007199254740992.0);
            }
        }

        // test_env: world conditions that skew measurements, corrected without making runs unrealistic (Rodney, 2026-10-17:
        // a Sim frozen solid in winter counted as stuck; the goal is no skew, not a fake world). Only the running session
        // changes (forced season, weather queue, moon phase); no option is written.
        // mode realistic (default): the save's own season, weather and moon play out; winter or extreme cold switches to spring
        //   (freezing needs extreme cold), and a full moon closer than min_days_to_full (default 3) moves the moon to first
        //   quarter. Idempotent: profile-world calls it again every 30 s as a guard (min_days_to_full 1 there).
        // mode fixed: season (summer|fall|winter|spring, default summer), sunny weather for weather_hours (default 48),
        //   moon_phase (0 full .. 7 waning crescent, LunarCycleManager.LunarPhase; default 2 first quarter): identical runs.
        static object TestEnv(Args a)
        {
            Dictionary<string, object> r = Util.Obj();
            string mode = a.Str("mode", "realistic").ToLowerInvariant();
            bool fixedMode = mode == "fixed", readOnly = mode == "read"; // read: report only (polling)
            List<object> changed = new List<object>();
            // vacation worlds (France, University...) have no SeasonsManager instance: CurrentSeason throws there
            bool seasonsLive = GameUtils.IsInstalled(ProductVersion.EP8) && typeof(Sims3.Gameplay.Seasons.SeasonsManager)
                .GetField("sInstance", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null) != null;
            if (GameUtils.IsInstalled(ProductVersion.EP8) && !seasonsLive) r["seasons"] = "none in this world";
            if (seasonsLive)
            {
                if (readOnly) { }
                else if (fixedMode)
                {
                    string s = a.Str("season", "summer").ToLowerInvariant();
                    Season season = s == "fall" ? Season.Fall : s == "winter" ? Season.Winter : s == "spring" ? Season.Spring : Season.Summer;
                    Sims3.Gameplay.Seasons.SeasonsManager.ForceRequestSeason(season);
                    // weather (Weather enum name, default sunny) with intensity (Light/Moderate/Heavy) for rain or snow:
                    // heavy rain = thunderstorm (lightning strikes need Heavy). docs/117 rare-scenario repros.
                    float hours = (float)a.Double("weather_hours", 48);
                    Weather w = (Weather)Enum.Parse(typeof(Weather), a.Str("weather", "sunny"), true);
                    PrecipitationIntensity pi = (PrecipitationIntensity)Enum.Parse(typeof(PrecipitationIntensity), a.Str("intensity", "Moderate"), true);
                    if (w == Weather.Rain && a.Has("intensity")) Sims3.Gameplay.Seasons.SeasonsManager.ForceRequestRainWithIntensity(pi, hours);
                    else if (w == Weather.Snow && a.Has("intensity")) Sims3.Gameplay.Seasons.SeasonsManager.ForceRequestSnowWithIntensity(pi, hours);
                    else Sims3.Gameplay.Seasons.SeasonsManager.ForceRequestWeather(w, hours);
                    changed.Add("season and weather fixed");
                    if (a.Has("temperature"))
                    {
                        Sims3.Gameplay.Seasons.SeasonsManager.Debug_AddFlags(Sims3.Gameplay.Seasons.SeasonsDebugFlag.TemperatureOverride);
                        Sims3.Gameplay.Seasons.SeasonsManager.Debug_SetTemperatureOverride((float)a.Double("temperature"));
                        changed.Add("temperature override");
                    }
                    else Sims3.Gameplay.Seasons.SeasonsManager.Debug_RemoveFlags(Sims3.Gameplay.Seasons.SeasonsDebugFlag.TemperatureOverride);
                    if (a.Has("snow_level"))
                    {
                        Sims3.Gameplay.Seasons.SeasonsManager.Debug_SetSnowLevel((Sims3.Gameplay.Seasons.SnowLevel)Enum.Parse(typeof(Sims3.Gameplay.Seasons.SnowLevel), a.Str("snow_level"), true));
                        changed.Add("snow level");
                    }
                    if (a.Bool("freeze_ponds", false))
                    {
                        // freeze level 100 and the end state of the game's own Liquid -> FreezingMelting -> FrozenSolid
                        // steps (TerrainCoverManager.UpdatePondFreezing; that last step fires no event either). Routing then
                        // sets EnablePondPlanning for human child+ Sims (SimRoutingComponent, docs/100 §1 item 6)
                        Sims3.Gameplay.Seasons.SeasonsManager.Debug_SetPondFreezeLevel(100f);
                        const BindingFlags kPriv = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
                        object sm = typeof(Sims3.Gameplay.Seasons.SeasonsManager).GetField("sInstance", kPriv).GetValue(null);
                        object tcm = sm.GetType().GetField("mTerrainCoverManager", kPriv).GetValue(sm);
                        tcm.GetType().GetField("mPondState", kPriv).SetValue(tcm, Sims3.Gameplay.Seasons.PondState.FrozenSolid);
                        changed.Add("ponds frozen");
                    }
                }
                else if (Sims3.Gameplay.Seasons.SeasonsManager.CurrentSeason == Season.Winter || Sims3.Gameplay.Seasons.SeasonsManager.IsExtremeCold())
                {
                    changed.Add("winter or extreme cold -> spring");
                    if (Sims3.Gameplay.Seasons.SeasonsManager.CurrentSeason != Season.Spring)
                        Sims3.Gameplay.Seasons.SeasonsManager.ForceRequestSeason(Season.Spring);
                    // the season switch keeps the running weather: snow or hail would keep it freezing into spring
                    // (2026-10-17 first run: spring, snow, 21.5 degrees); end it with a few hours of sun, then the
                    // spring weather queue takes over
                    Weather w = Sims3.Gameplay.Seasons.SeasonsManager.CurrentWeather;
                    if (w == Weather.Snow || w == Weather.Hail)
                    {
                        Sims3.Gameplay.Seasons.SeasonsManager.ForceRequestWeather(Weather.Sunny, 6f);
                        changed.Add("snow or hail -> 6 h sun");
                    }
                }
                r["season"] = Sims3.Gameplay.Seasons.SeasonsManager.CurrentSeason.ToString();
                r["weather"] = Sims3.Gameplay.Seasons.SeasonsManager.CurrentWeather.ToString();
                r["temperature"] = Sims3.Gameplay.Seasons.SeasonsManager.Temperature;
                r["snow_level"] = Sims3.Gameplay.Seasons.SeasonsManager.CurrentSnowLevel.ToString();
                r["pond_state"] = Sims3.Gameplay.Seasons.SeasonsManager.CurrentPondState.ToString();
            }
            else r["season"] = "Seasons (EP8) not installed";
            if (GameUtils.IsInstalled(ProductVersion.EP7))
            {
                if (readOnly) { }
                else if (fixedMode)
                {
                    int phase = (int)a.Long("moon_phase", 2);
                    if (phase < 0 || phase > 7) throw new McpException("moon_phase must be 0..7");
                    World.ForceLunarPhase(phase);
                    changed.Add("moon phase fixed");
                }
                // GetNumDaysUntilFullMoon returns -1 after a forced phase (2026-10-17): unknown, not "today"
                else if (World.GetLunarPhase() == 0 || (World.GetNumDaysUntilFullMoon() >= 0 && World.GetNumDaysUntilFullMoon() < a.Long("min_days_to_full", 3)))
                {
                    changed.Add("full moon near -> first quarter");
                    World.ForceLunarPhase(2);
                }
                r["moon_phase"] = World.GetLunarPhase();
                r["days_to_full_moon"] = World.GetNumDaysUntilFullMoon();
            }
            else r["moon_phase"] = "Supernatural (EP7) not installed";
            if (!readOnly && a.Has("hour"))
            {
                // jump the clock forward to the hour (Sims3.Gameplay.Utilities.SimClock.AddOneHour, as the debug clock cheat); at most a day
                int hour = (int)a.Long("hour");
                for (int i = 0; i < 24 && Sims3.Gameplay.Utilities.SimClock.Hours24 != hour; i++) Sims3.Gameplay.Utilities.SimClock.AddOneHour();
                changed.Add("clock -> " + hour + ":00");
            }
            r["hour"] = Sims3.Gameplay.Utilities.SimClock.Hours24;
            r["changed"] = changed;
            return r;
        }

        // seed_rng {seed}: swap RandomGen.gRandomGen (every RandomUtil draw) for a seeded stream; {seed: null} puts the
        // game's back. Call at the start of the measured window. firstDraws come from a twin stream (ours is not consumed):
        // same seed = same draws. System.Random() and the native RDTSC-seeded RNGs stay unseeded (docs/27).
        static object SeedRng(Args a)
        {
            FieldInfo f = typeof(RandomGen).GetField("gRandomGen", BindingFlags.NonPublic | BindingFlags.Static);
            if (f == null) throw new McpException("RandomGen.gRandomGen not found");
            const string kSaved = "ts3agent.rng.original";
            object cur = f.GetValue(null);
            bool seeded = cur is SeededRandom || (cur != null && cur.GetType().Name == "SeededRandom"); // older plugin build after hot reload
            Dictionary<string, object> r = Util.Obj();
            if (!a.Has("seed"))
            {
                if (seeded) f.SetValue(null, AppDomain.CurrentDomain.GetData(kSaved));
                r["seed"] = null;
                r["restored"] = seeded;
            }
            else
            {
                long seed = a.Long("seed");
                if (!seeded) AppDomain.CurrentDomain.SetData(kSaved, cur);
                f.SetValue(null, new SeededRandom(seed));
                SeededRandom twin = new SeededRandom(seed);
                r["seed"] = seed;
                r["firstDraws"] = new List<object> { twin.NextDouble(), twin.NextDouble(), twin.NextDouble() };
            }
            object orig = AppDomain.CurrentDomain.GetData(kSaved);
            r["previous"] = orig != null ? orig.GetType().FullName : "null (game RNG not cached yet)";
            return r;
        }
    }
}
