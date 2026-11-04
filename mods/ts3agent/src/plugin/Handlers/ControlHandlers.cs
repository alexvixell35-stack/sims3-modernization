using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Careers;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.UI;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using Sims3.SimIFace.BuildBuy;
using Sims3.UI;
using GF = Sims3.Gameplay.Gameflow;
using GameSpeed = Sims3.SimIFace.Gameflow.GameSpeed;

namespace Ts3Agent
{
    // Time, lots, objects, interactions, dialogs, cheats, saving, buy/sell.
    public static partial class Handlers
    {
        static void RegisterControl()
        {
            Dispatcher.Register("look", Look);
            Dispatcher.Register("time", TimeInfo);
            Dispatcher.Register("set_speed", SetSpeed);
            Dispatcher.Register("clock_state", ClockState);
            Dispatcher.Register("list_lots", ListLots);
            Dispatcher.Register("list_objects", ListObjects);
            Dispatcher.Register("object_details", ObjectDetails);
            Dispatcher.Register("list_interactions", ListInteractions);
            Dispatcher.Register("do_interaction", DoInteraction);
            Dispatcher.Register("cancel_interactions", CancelInteractions);
            Dispatcher.Register("go_here", GoHere);
            Dispatcher.Register("cheat", Cheat);
            Dispatcher.Register("save_game", SaveGame);
            Dispatcher.Register("pending_dialogs", PendingDialogs);
            Dispatcher.Register("respond_dialog", RespondDialog);
            Dispatcher.Register("catalog_search", CatalogSearch);
            Dispatcher.Register("buy_object", BuyObject);
            Dispatcher.Register("sell_object", SellObject);
            Dispatcher.Register("move_object", MoveObject);
            Dispatcher.Register("place_object", PlaceObject);
            Dispatcher.Register("replace_object", ReplaceObject);
            Dispatcher.Register("set_transform", SetTransform);
            Dispatcher.Register("lot_layout", LotLayout);
            Dispatcher.Register("put_on", PutOn);
            Dispatcher.Register("async_log", AsyncLog);
            Dispatcher.Register("tick_status", TickStatus);

            AddRoot("sim", delegate { return PlumbBob.SelectedActor; });
            AddRoot("household", delegate { return Household.ActiveHousehold; });
            AddRoot("lot", delegate { return PlumbBob.SelectedActor != null ? PlumbBob.SelectedActor.LotCurrent : null; });
        }

        // ------------------------------------------------------------ async work
        // Anything that may open a modal dialog puts its task to sleep until the
        // dialog closes, so it must never run on the mailbox poll task.

        static readonly List<string> sAsyncLog = new List<string>();

        public delegate void Work();

        static void RunAsync(string label, Work work)
        {
            Simulator.AddObject(new Sims3.Gameplay.OneShotFunctionTask(delegate
            {
                string result;
                try { work(); result = "ok"; }
                catch (Exception e) { result = "error: " + DescribeError(e); }
                lock (sAsyncLog)
                {
                    sAsyncLog.Add(SimClock.CurrentTime().ToString() + " " + label + " -> " + result);
                    if (sAsyncLog.Count > 30) sAsyncLog.RemoveAt(0);
                }
            }));
        }

        // Every exception of the InnerException chain (reflection wraps the real cause in TargetInvocationException,
        // docs/77 §2.2), then the first stack lines of the innermost one, where the cause was thrown.
        public static string DescribeError(Exception e)
        {
            List<string> chain = new List<string>();
            Exception inner = e;
            for (Exception x = e; x != null && chain.Count < 6; x = x.InnerException)
            {
                chain.Add(x.GetType().Name + ": " + x.Message);
                inner = x;
            }
            string[] stack = (inner.StackTrace ?? "").Split('\n');
            List<string> top = new List<string>();
            for (int i = 0; i < stack.Length && top.Count < 6; i++) if (stack[i].Trim().Length > 0) top.Add(stack[i].Trim());
            return string.Join(" > inner ", chain.ToArray()) + (top.Count > 0 ? " | stack: " + string.Join(" | ", top.ToArray()) : "");
        }

        static object AsyncLog(Args a)
        {
            lock (sAsyncLog) return new List<string>(sAsyncLog);
        }

        // ------------------------------------------------------------ overview

        static object Look(Args a)
        {
            Dictionary<string, object> d = Util.Obj();
            d["time"] = TimeInfo(a);
            Sim s = PlumbBob.SelectedActor;
            Household hh = Household.ActiveHousehold;
            if (hh != null)
            {
                d["household"] = hh.Name;
                d["funds"] = hh.FamilyFunds;
                List<object> members = new List<object>();
                foreach (SimDescription sd in hh.AllSimDescriptions)
                {
                    Dictionary<string, object> m = Util.SimSummary(sd);
                    if (sd.CreatedSim != null && sd.CreatedSim != s)
                    {
                        InteractionInstance cur = sd.CreatedSim.CurrentInteraction;
                        if (cur != null) m["doing"] = SafeName(cur);
                    }
                    members.Add(m);
                }
                d["members"] = members;
            }
            if (s != null)
            {
                Dictionary<string, object> me = Util.SimSummary(s.SimDescription);
                me["motives"] = MotiveValues(s);
                me["mood"] = s.MoodManager.MoodValue;
                me["mood_flavor"] = s.MoodManager.MoodFlavor.ToString();
                me["moodlets"] = BuffList(s);
                me["queue"] = QueueList(s);
                Occupation job = s.Occupation;
                if (job != null) me["career"] = job.CareerName + " L" + job.CareerLevel + ", work in " + Util.Round(job.HoursUntilWork) + "h";
                d["active_sim"] = me;
            }
            WindowBase modal = UIManager.GetModalWindow();
            if (modal != null) d["dialog_open"] = DescribeDialog(modal);
            lock (sAsyncLog) if (sAsyncLog.Count > 0) d["last_async"] = sAsyncLog[sAsyncLog.Count - 1];
            return d;
        }

        // Cheap snapshot polled by the server's wait tool.
        static object TickStatus(Args a)
        {
            Dictionary<string, object> d = Util.Obj();
            d["minutes_total"] = Math.Round(SimClock.ConvertFromTicks(SimClock.CurrentTicks, TimeUnit.Minutes), 1);
            d["clock"] = SimClock.Hours24.ToString("00") + ":" + SimClock.Minutes60.ToString("00");
            d["speed"] = GF.CurrentGameSpeed.ToString();
            d["dialog_open"] = UIManager.GetModalWindow() != null;
            Sim s = PlumbBob.SelectedActor;
            if (s != null)
            {
                d["sim"] = s.FullName;
                d["queue_count"] = s.InteractionQueue != null ? s.InteractionQueue.Count : 0;
                InteractionInstance cur = s.CurrentInteraction;
                if (cur != null) d["doing"] = SafeName(cur);
                string lowName = null;
                float low = float.MaxValue;
                if (s.Motives != null)
                    foreach (CommodityKind k in kShownMotives)
                        if (k != CommodityKind.Temperature && s.Motives.HasMotive(k))
                        {
                            float v = s.Motives.GetValue(k);
                            if (v < low) { low = v; lowName = k.ToString(); }
                        }
                if (lowName != null) { d["lowest_motive"] = lowName; d["lowest_value"] = Util.Round(low); }
                d["mood"] = s.MoodManager.MoodValue;
            }
            return d;
        }

        static object TimeInfo(Args a)
        {
            Dictionary<string, object> d = Util.Obj();
            d["day"] = SimClock.CurrentDayOfWeek.ToString();
            d["clock"] = SimClock.Hours24.ToString("00") + ":" + SimClock.Minutes60.ToString("00");
            d["hour"] = Util.Round(SimClock.HoursPassedOfDay);
            d["day_number"] = SimClock.ElapsedCalendarDays() + 1;
            d["minutes_total"] = Math.Round(SimClock.ConvertFromTicks(SimClock.CurrentTicks, TimeUnit.Minutes), 1);
            d["speed"] = GF.CurrentGameSpeed.ToString();
            d["speed_locked"] = GF.GameSpeedLocked;
            return d;
        }

        // clock_state (read-only, docs/85 §6 P-clock): the native GameClock at 0x11CDD48 (GameClock_IsPaused 0x594540 reads
        // +0x40 and +0x68 under its lock; single aligned dword reads here, no lock), the managed speed and game time, and the
        // game minutes since the previous clock_state call. The probe server adds the window state (iconic, foreground):
        // the game's Mono resolves DllImport through its internal-call table, so user32 is out of reach here (docs/06).
        const int kGameClock = 0x11CDD48;
        static double sLastClockMinutes = -1;

        static float ReadF32(int addr) { return BitConverter.ToSingle(BitConverter.GetBytes(Marshal.ReadInt32(new IntPtr(addr))), 0); }

        static object ClockState(Args a)
        {
            Dictionary<string, object> d = Util.Obj();
            int bits = Marshal.ReadInt32(new IntPtr(kGameClock + 0x40));
            float speed = ReadF32(kGameClock + 0x68);
            d["pause_bits"] = bits;
            d["script_pause"] = (bits & 1) != 0;   // IC_GameUtils_PauseImpl, modal pump
            d["minimize_pause"] = (bits & 2) != 0; // window minimized (0x1EE1008), unless SimWhileMinimized
            d["device_pause"] = (bits & 4) != 0;   // MainLoop while device +0x8D is set
            d["native_paused"] = bits != 0 || Math.Abs(speed) <= 1.2e-7; // GameClock_IsPaused 0x594540
            d["game_dt"] = ReadF32(kGameClock + 0x64);
            d["speed"] = speed;
            d["scale"] = ReadF32(kGameClock + 0x74);
            d["game_speed"] = GF.CurrentGameSpeed.ToString();
            double minutes = Math.Round(SimClock.ConvertFromTicks(SimClock.CurrentTicks, TimeUnit.Minutes), 1);
            d["minutes_total"] = minutes;
            d["minutes_moved"] = sLastClockMinutes < 0 ? null : (object)Math.Round(minutes - sLastClockMinutes, 1);
            sLastClockMinutes = minutes;
            d["clock"] = SimClock.Hours24.ToString("00") + ":" + SimClock.Minutes60.ToString("00");
            return d;
        }

        static object SetSpeed(Args a)
        {
            string s = a.Str("speed").ToLowerInvariant();
            GameSpeed target;
            switch (s)
            {
                case "pause": case "paused": case "0": target = GameSpeed.Pause; break;
                case "normal": case "play": case "1": target = GameSpeed.Normal; break;
                case "fast": case "2": target = GameSpeed.Double; break;
                case "ultra": case "faster": case "3": target = GameSpeed.Triple; break;
                case "skip": target = GameSpeed.Skip; break;
                default: throw new McpException("speed must be pause, normal, fast, ultra or skip");
            }
            if (GF.GameSpeedLocked && target != GameSpeed.Pause)
                throw new McpException("game speed is locked (a dialog is open or the game is saving)");
            // An explicit speed is the autopilot's new target: pause holds nothing (2026-10-17: the autopilot, left at
            // "ultra" by repro.ts, undid every pause within a second, so "paused" cleanups ran at x3 and Sims arrived)
            Dictionary<string, object> ap = ApCfg();
            ap["speed"] = target == GameSpeed.Pause ? "none" : target == GameSpeed.Normal ? "normal" : target == GameSpeed.Double ? "fast" : target == GameSpeed.Triple ? "ultra" : ap["speed"];
            // SetGameSpeed(Pause) toggles when already paused, so only call it when it changes something.
            if (GF.CurrentGameSpeed != target) GF.SetGameSpeed(target, GF.SetGameSpeedContext.User);
            return TimeInfo(a);
        }

        // ------------------------------------------------------------ lots & objects

        static object ListLots(Args a)
        {
            List<object> list = new List<object>();
            string q = a.Str("query", "").ToLowerInvariant();
            foreach (Lot lot in LotManager.Lots)
            {
                if (lot == null || lot.IsWorldLot) continue;
                Dictionary<string, object> d = Util.Obj();
                d["id"] = Util.Id(lot.LotId);
                d["target"] = "lot:" + Util.Id(lot.LotId);
                d["name"] = Util.LotName(lot);
                try { d["address"] = lot.Address; } catch (Exception) { }
                d["type"] = lot.IsCommunityLot ? "community:" + lot.CommercialLotSubType : "residential";
                if (lot.Household != null) d["household"] = lot.Household.Name;
                string hay = (d["name"] + " " + (d.ContainsKey("address") ? d["address"] : "") + " " + d["type"]).ToLowerInvariant();
                if (q.Length > 0 && hay.IndexOf(q) < 0) continue;
                list.Add(d);
            }
            return list;
        }

        static object ListObjects(Args a)
        {
            Sim me = PlumbBob.SelectedActor;
            GameObject[] objs;
            if (a.Has("radius"))
            {
                if (me == null) throw new McpException("no selected sim to search around");
                objs = Sims3.Gameplay.Queries.GetObjects<GameObject>(me.Position, (float)a.Double("radius"));
            }
            else
            {
                Lot lot = a.Has("lot") ? FindLot(a.Str("lot")) : (me != null ? me.LotCurrent : LotManager.ActiveLot);
                if (lot == null) throw new McpException("no lot to list objects on");
                objs = Sims3.Gameplay.Queries.GetObjects<GameObject>(lot);
            }
            string q = a.Str("query", "").ToLowerInvariant();
            bool includeSims = a.Bool("include_sims", true);
            int minLevel = (int)a.Long("min_level", int.MinValue);  // 1: objects on upper floors only
            List<KeyValuePair<float, object>> found = new List<KeyValuePair<float, object>>();
            foreach (GameObject o in objs)
            {
                if (o == null || !o.InWorld || o is Lot) continue;
                if (!includeSims && o is Sim) continue;
                if (o.Level < minLevel) continue;
                string name = Util.ObjectName(o);
                if (string.IsNullOrEmpty(name) || name.StartsWith("***")) continue;  // internal objects
                name = name.Trim();
                if (q.Length > 0 && name.ToLowerInvariant().IndexOf(q) < 0 && o.GetType().Name.ToLowerInvariant().IndexOf(q) < 0) continue;
                Dictionary<string, object> d = Util.Obj();
                d["id"] = Util.Id(o.ObjectId);
                d["name"] = name;
                d["type"] = o.GetType().Name;
                d["level"] = o.Level;
                float dist = me != null ? (o.Position - me.Position).Length() : 0f;
                if (me != null) d["distance"] = Util.Round(dist);
                if (o.InUse) d["in_use"] = true;
                found.Add(new KeyValuePair<float, object>(dist, d));
            }
            found.Sort(delegate(KeyValuePair<float, object> x, KeyValuePair<float, object> y) { return x.Key.CompareTo(y.Key); });
            int limit = (int)a.Long("limit", 150);
            List<object> list = new List<object>();
            foreach (KeyValuePair<float, object> kv in found)
            {
                list.Add(kv.Value);
                if (list.Count >= limit) break;
            }
            return list;
        }

        static object ObjectDetails(Args a)
        {
            GameObject o = FindTarget(a, "target");
            Dictionary<string, object> d = Util.Obj();
            d["id"] = Util.Id(o.ObjectId);
            d["name"] = Util.ObjectName(o);
            d["type"] = o.GetType().FullName;
            d["position"] = Pos(o.Position);
            d["level"] = o.Level;
            d["room"] = o.RoomId;
            if (o.LotCurrent != null) d["lot"] = Util.LotName(o.LotCurrent);
            try { d["value"] = o.Value; } catch (Exception) { }
            d["in_use"] = o.InUse;
            List<object> users = new List<object>();
            if (o.ActorsUsingMe != null) foreach (Sim u in o.ActorsUsingMe) users.Add(u.FullName);
            d["used_by"] = users;
            Sim s = o as Sim;
            if (s != null) d["sim"] = Util.SimSummary(s.SimDescription);
            return d;
        }

        static object Pos(Vector3 v)
        {
            Dictionary<string, object> d = Util.Obj();
            d["x"] = Util.Round(v.x);
            d["y"] = Util.Round(v.y);
            d["z"] = Util.Round(v.z);
            return d;
        }

        static Lot FindLot(string q)
        {
            if (q.StartsWith("lot:")) q = q.Substring(4);
            ulong id;
            if (ulong.TryParse(q, out id))
            {
                Lot l = LotManager.GetLot(id);
                if (l != null) return l;
            }
            Lot match = null;
            foreach (Lot lot in LotManager.Lots)
            {
                if (lot == null || lot.IsWorldLot) continue;
                string n = Util.LotName(lot);
                if (string.Compare(n, q, true) == 0) return lot;
                if (n != null && n.ToLowerInvariant().IndexOf(q.ToLowerInvariant()) >= 0) match = lot;
            }
            if (match != null) return match;
            throw new McpException("no lot matching '" + q + "' (see list_lots)");
        }

        // Targets: object id, sim name/id, "lot:<id or name>", "home", "terrain".
        static GameObject FindTarget(Args a, string key)
        {
            string q = a.Str(key).Trim();
            string lq = q.ToLowerInvariant();
            if (lq.StartsWith("lot:")) return FindLot(q.Substring(4));
            if (lq == "home")
            {
                Household hh = Household.ActiveHousehold;
                if (hh == null || hh.LotHome == null) throw new McpException("the active household has no home lot");
                return hh.LotHome;
            }
            if (lq == "terrain") return Terrain.Singleton;
            if (lq == "self" || lq == "me") return Util.ActiveSim();
            if (q.StartsWith("@"))  // a reflect root or path, e.g. "@door", "@upstairs" (ReproHandlers)
            {
                GameObject g = Resolve(q) as GameObject;
                if (g == null) throw new McpException(q + " is not a game object");
                return g;
            }
            return Util.FindObject(a, key);
        }

        // ------------------------------------------------------------ interactions

        class MenuEntry
        {
            public string Label;
            public bool Enabled;
            public string Reason;
            public InteractionObjectPair Iop;
            public InteractionInstanceParameters Params;
        }

        static GameObjectHit HitFor(GameObject target, Args a)
        {
            if (target is Terrain || target is Lot)
            {
                Vector3 p = a.Has("x") ? new Vector3((float)a.Double("x"), (float)a.Double("y", 0), (float)a.Double("z"))
                                       : (target is Lot ? ((Lot)target).GetCenterPosition() : Util.ActiveSim().Position);
                GameObjectHit hit = new GameObjectHit(target is Lot ? GameObjectHitType.LotTerrain : GameObjectHitType.Terrain);
                hit.mPoint = p;
                Lot at = target as Lot ?? LotManager.GetLotAtPoint(p);
                hit.mId = at != null ? at.LotId : 0;
                return hit;
            }
            GameObjectHit oh = new GameObjectHit(GameObjectHitType.Object);
            oh.mPoint = target.Position;
            oh.mId = target.ObjectId.Value;
            return oh;
        }

        // Mirrors PieMenu.TestAndBringUpPieMenu: what the player would see.
        static List<MenuEntry> BuildMenu(Sim actor, GameObject target, GameObjectHit hit)
        {
            List<MenuEntry> result = new List<MenuEntry>();
            List<InteractionObjectPair> iops = target.GetAllInteractionsForActor(actor);
            if (iops == null) return result;
            foreach (InteractionObjectPair iop in iops)
            {
                try
                {
                    if (!iop.CheckIfInteractionValid()) continue;
                    InteractionInstanceParameters p = new InteractionInstanceParameters(iop, actor,
                        new InteractionPriority(InteractionPriorityLevel.UserDirected), AutonomySearchType.None,
                        InteractionFlags.UserDirected, hit, null, false);
                    GreyedOutTooltipCallback tip = null;
                    InteractionTestResult r = iop.InteractionDefinition.Test(ref p, ref tip);
                    bool ok = IUtil.IsPass(r);
                    if (!ok && tip == null) continue;
                    string[] path = iop.InteractionDefinition.GetPath(actor.IsFemale);
                    List<string> parts = new List<string>();
                    IopWithPrependedPath pp = iop as IopWithPrependedPath;
                    if (pp != null && !string.IsNullOrEmpty(pp.PrependPath)) parts.Add(pp.PrependPath.Trim());
                    if (path != null) foreach (string seg in path) if (!string.IsNullOrEmpty(seg)) parts.Add(seg.Trim());
                    parts.Add(InteractionDefinitionUtilities.GetInteractionNameForUI(ref p));
                    MenuEntry e = new MenuEntry();
                    e.Label = string.Join(" / ", parts.ToArray());
                    e.Enabled = ok;
                    if (!ok)
                    {
                        try { e.Reason = tip(); } catch (Exception) { e.Reason = r.ToString(); }
                    }
                    e.Iop = iop;
                    e.Params = p;
                    result.Add(e);
                }
                catch (Exception) { }
            }
            return result;
        }

        static List<KeyValuePair<string, object>> PickerRows(MenuEntry e)
        {
            List<KeyValuePair<string, object>> rows = new List<KeyValuePair<string, object>>();
            InteractionInstanceParameters p = e.Params;
            List<ObjectPicker.TabInfo> tabs;
            List<ObjectPicker.HeaderInfo> headers;
            int n;
            try { e.Iop.InteractionDefinition.PopulatePieMenuPicker(ref p, out tabs, out headers, out n); }
            catch (Exception) { return rows; }
            if (tabs == null) return rows;
            foreach (ObjectPicker.TabInfo tab in tabs)
                if (tab.RowInfo != null)
                    foreach (ObjectPicker.RowInfo row in tab.RowInfo)
                        rows.Add(new KeyValuePair<string, object>(RowText(row), row.Item));
            return rows;
        }

        static string RowText(ObjectPicker.RowInfo row)
        {
            List<string> texts = new List<string>();
            if (row.ColumnInfo != null)
                foreach (ObjectPicker.ColumnInfo c in row.ColumnInfo)
                {
                    if (c == null) continue;
                    FieldInfo f = c.GetType().GetField("BodyText");
                    if (f != null)
                    {
                        string t = f.GetValue(c) as string;
                        if (!string.IsNullOrEmpty(t)) texts.Add(t);
                    }
                }
            if (texts.Count == 0 && row.Item != null)
            {
                GameObject go = row.Item as GameObject;
                texts.Add(go != null ? Util.ObjectName(go) : row.Item.ToString());
            }
            return string.Join(" | ", texts.ToArray());
        }

        static object ListInteractions(Args a)
        {
            Sim actor = a.Has("sim") ? Util.FindSim(a, "sim") : Util.ActiveSim();
            GameObject target = FindTarget(a, "target");
            List<MenuEntry> menu = BuildMenu(actor, target, HitFor(target, a));
            string q = a.Str("query", "").ToLowerInvariant();
            List<object> list = new List<object>();
            foreach (MenuEntry e in menu)
            {
                if (q.Length > 0 && e.Label.ToLowerInvariant().IndexOf(q) < 0) continue;
                if (!e.Enabled && !a.Bool("include_disabled", false)) continue;
                Dictionary<string, object> d = Util.Obj();
                d["interaction"] = e.Label;
                if (!e.Enabled) d["disabled_reason"] = e.Reason ?? "unavailable";
                if (q.Length > 0 && e.Enabled)
                {
                    List<KeyValuePair<string, object>> rows = PickerRows(e);
                    if (rows.Count > 0)
                    {
                        List<string> labels = new List<string>();
                        foreach (KeyValuePair<string, object> kv in rows) labels.Add(kv.Key);
                        d["pick_from"] = labels;
                    }
                }
                list.Add(d);
            }
            Dictionary<string, object> r = Util.Obj();
            r["actor"] = actor.FullName;
            r["target"] = Util.ObjectName(target);
            r["can_queue"] = actor.InteractionQueue.CanPlayerQueue();
            r["interactions"] = list;
            return r;
        }

        static MenuEntry MatchEntry(List<MenuEntry> menu, string wanted)
        {
            string w = wanted.Trim().ToLowerInvariant();
            List<MenuEntry> enabled = menu.FindAll(delegate(MenuEntry e) { return e.Enabled; });
            foreach (MenuEntry e in enabled) if (e.Label.ToLowerInvariant() == w) return e;
            // Match on the last path segment ("Prepare Food / Mac & Cheese" -> "Mac & Cheese").
            List<MenuEntry> hits = enabled.FindAll(delegate(MenuEntry e)
            {
                string l = e.Label.ToLowerInvariant();
                int slash = l.LastIndexOf(" / ");
                return (slash >= 0 && l.Substring(slash + 3) == w) || l.IndexOf(w) >= 0;
            });
            if (hits.Count == 1) return hits[0];
            if (hits.Count > 1)
            {
                List<string> names = new List<string>();
                foreach (MenuEntry e in hits) names.Add(e.Label);
                throw new McpException("'" + wanted + "' matches several interactions: " + string.Join("; ", names.ToArray()));
            }
            foreach (MenuEntry e in menu)
                if (!e.Enabled && e.Label.ToLowerInvariant().IndexOf(w) >= 0)
                    throw new McpException("'" + e.Label + "' is unavailable: " + (e.Reason ?? "greyed out"));
            throw new McpException("no interaction matching '" + wanted + "' (use list_interactions)");
        }

        static object DoInteraction(Args a)
        {
            Sim actor = a.Has("sim") ? Util.FindSim(a, "sim") : Util.ActiveSim();
            GameObject target = FindTarget(a, "target");
            GameObjectHit hit = HitFor(target, a);
            MenuEntry e = MatchEntry(BuildMenu(actor, target, hit), a.Str("interaction"));
            if (!actor.InteractionQueue.CanPlayerQueue())
                throw new McpException(actor.FullName + "'s queue is full; cancel something first");

            List<object> picked = null;
            List<object> wantPick = a.List("pick");
            if (wantPick != null && wantPick.Count > 0)
            {
                List<KeyValuePair<string, object>> rows = PickerRows(e);
                picked = new List<object>();
                foreach (object w in wantPick)
                {
                    string ws = Convert.ToString(w).ToLowerInvariant();
                    KeyValuePair<string, object>? best = null;
                    foreach (KeyValuePair<string, object> kv in rows)
                        if (kv.Key.ToLowerInvariant() == ws || (best == null && kv.Key.ToLowerInvariant().IndexOf(ws) >= 0)) best = kv;
                    if (best == null) throw new McpException("no picker row matching '" + w + "'");
                    picked.Add(best.Value.Value);
                }
            }

            InteractionInstanceParameters p = new InteractionInstanceParameters(e.Iop, actor,
                new InteractionPriority(InteractionPriorityLevel.UserDirected), AutonomySearchType.None,
                InteractionFlags.UserDirected, hit, picked, false);
            InteractionInstance inst = e.Iop.InteractionDefinition.CreateInstanceFromParameters(ref p);
            if (inst == null) throw new McpException("the game could not create '" + e.Label + "'");
            bool queued;
            if (inst is IImmediateInteraction)
            {
                Simulator.AddObject(new ImmediateInteractionTask(actor, inst));
                queued = true;
            }
            else queued = actor.InteractionQueue.Add(inst);
            if (!queued) throw new McpException("the game refused to queue '" + e.Label + "'");
            Dictionary<string, object> r = Util.Obj();
            r["queued"] = e.Label;
            r["actor"] = actor.FullName;
            r["queue"] = QueueList(actor);
            return r;
        }

        static string SafeName(InteractionInstance ii)
        {
            try { return ii.GetInteractionNameForUI(); } catch (Exception) { return ii.GetType().Name; }
        }

        public static List<object> QueueList(Sim s)
        {
            List<object> list = new List<object>();
            if (s.InteractionQueue == null) return list;
            foreach (InteractionInstance ii in s.InteractionQueue.InteractionList)
            {
                Dictionary<string, object> d = Util.Obj();
                d["id"] = Util.Id(ii.Id);
                d["name"] = SafeName(ii);
                if (ii.Target != null && ii.Target != s)
                {
                    GameObject t = ii.Target as GameObject;
                    if (t != null) d["target"] = Util.ObjectName(t);
                }
                if (ii.Autonomous) d["autonomous"] = true;
                if (!ii.CancellableByPlayer) d["locked"] = true;
                list.Add(d);
            }
            return list;
        }

        static object CancelInteractions(Args a)
        {
            Sim s = a.Has("sim") ? Util.FindSim(a, "sim") : Util.ActiveSim();
            if (a.Has("id")) s.InteractionQueue.CancelInteraction(a.ULong("id"));
            else s.InteractionQueue.CancelAllInteractions();
            return QueueList(s);
        }

        static object GoHere(Args a)
        {
            Sim s = a.Has("sim") ? Util.FindSim(a, "sim") : Util.ActiveSim();
            Vector3 pos;
            if (a.Has("target"))
            {
                GameObject t = FindTarget(a, "target");
                pos = t is Lot ? ((Lot)t).GetCenterPosition() : t.Position;
            }
            else if (a.Has("to")) pos = RequireRarePoint(s, a.Str("to"));  // "pond", "ocean", "far" (RareHandlers)
            else pos = new Vector3((float)a.Double("x"), (float)a.Double("y", 0), (float)a.Double("z"));
            InteractionInstance ii = Terrain.GoHere.GetSingleton(s, pos).CreateInstance(Terrain.Singleton, s,
                new InteractionPriority(InteractionPriorityLevel.UserDirected), false, true);
            Terrain.GoHere gh = ii as Terrain.GoHere;
            if (gh != null) gh.SetDestination(pos, false);
            if (!s.InteractionQueue.Add(ii)) throw new McpException("could not queue Go Here");
            return QueueList(s);
        }

        // ------------------------------------------------------------ cheats & saving

        static object Cheat(Args a)
        {
            string cmd = a.Str("command").Trim();
            if (cmd.Length == 0) throw new McpException("empty command");
            if (cmd == "help")
            {
                Dictionary<string, object> h = Util.Obj();
                h["commands"] = CommandSystem.GetCommandList();
                return h;
            }
            RunAsync("cheat " + cmd, delegate
            {
                if (!CommandSystem.ExecuteCommandString(cmd)) throw new McpException("unknown or failed command");
            });
            Dictionary<string, object> r = Util.Obj();
            r["queued"] = cmd;
            r["note"] = "runs on its own game task; check async_log or look for the result";
            return r;
        }

        static object SaveGame(Args a)
        {
            IOptionsModel om = Sims3.Gameplay.UI.Responder.Instance.OptionsModel;
            if (om.SaveGameInProgress) throw new McpException("a save is already in progress");
            WindowBase modal = UIManager.GetModalWindow();
            if (modal != null)
                throw new McpException("close the open dialog before saving: " + string.Join(" | ", ((List<string>)DescribeDialog(modal)["text"]).ToArray()));
            if (!om.CanSaveGame()) throw new McpException("the game cannot save right now");
            if (a.Has("name")) om.SaveName = a.Str("name");
            if (string.IsNullOrEmpty(om.SaveName))
                throw new McpException("this game has no save name yet; pass name=\"...\"");
            // forceUseSaveName: without it the game ignores SaveName and overwrites the currently loaded file.
            bool asNew = a.Has("name");
            sAgentSavePending = true; // save_watch labels this save "agent" (SaverHandlers.cs), never a Saver save
            sAgentSaveAt = System.Environment.TickCount;
            RunAsync("save " + om.SaveName, delegate { om.SaveGame(false, asNew, false); });
            Dictionary<string, object> r = Util.Obj();
            r["saving"] = om.SaveName;
            return r;
        }

        // ------------------------------------------------------------ dialogs

        const uint kButtonClick = 678582774u;
        const uint kPickerTableId = 99576784u;
        const uint kPickerOkId = 99576785u;

        static uint Handle(WindowBase w)
        {
            FieldInfo f = typeof(WindowBase).GetField("mWinHandle", BindingFlags.NonPublic | BindingFlags.Instance);
            return (uint)f.GetValue(w);
        }

        static bool IsInside(WindowBase w, uint rootHandle)
        {
            for (WindowBase p = w; p != null; p = p.Parent)
                if (Handle(p) == rootHandle) return true;
            return false;
        }

        // Every clickable control inside the modal window, with its (internal) glue
        // handler object from UIManager.mEventRegistry.
        static List<KeyValuePair<WindowBase, object>> DialogButtons(WindowBase modal)
        {
            List<KeyValuePair<WindowBase, object>> list = new List<KeyValuePair<WindowBase, object>>();
            FieldInfo reg = typeof(UIManager).GetField("mEventRegistry", BindingFlags.NonPublic | BindingFlags.Static);
            IDictionary registry = reg.GetValue(null) as IDictionary;
            if (registry == null) return list;
            uint root = Handle(modal);
            // Snapshot first: touching windows below can register handlers and invalidate the enumerator.
            List<object> entries = new List<object>();
            foreach (object v in registry.Values) entries.Add(v);
            foreach (object data in entries)
            {
                if (data == null) continue;
                Type dt = data.GetType();
                WindowBase win = dt.GetField("Window").GetValue(data) as WindowBase;
                IDictionary callbacks = dt.GetField("EventTypesAndCallbacks").GetValue(data) as IDictionary;
                if (win == null || callbacks == null || !callbacks.Contains(kButtonClick)) continue;
                if (!win.Visible || !IsInside(win, root)) continue;
                list.Add(new KeyValuePair<WindowBase, object>(win, callbacks[kButtonClick]));
            }
            return list;
        }

        static void CollectTexts(WindowBase w, List<string> texts, int depth)
        {
            if (w == null || depth > 12 || texts.Count > 60) return;
            if (w is Text && w.Visible)
            {
                string c = w.Caption;
                if (!string.IsNullOrEmpty(c)) texts.Add(c);
            }
            for (uint i = 0; i < 200; i++)
            {
                WindowBase child;
                try { child = w.GetChildByIndex(i); } catch (Exception) { break; }
                if (child == null) break;
                CollectTexts(child, texts, depth + 1);
            }
        }

        static ObjectPicker FindPicker(WindowBase modal)
        {
            try { return modal.GetChildByID(kPickerTableId, true) as ObjectPicker; } catch (Exception) { return null; }
        }

        static List<ObjectPicker.RowInfo> PickerRowsOf(ObjectPicker picker)
        {
            List<ObjectPicker.RowInfo> rows = new List<ObjectPicker.RowInfo>();
            FieldInfo f = typeof(ObjectPicker).GetField("mItems", BindingFlags.NonPublic | BindingFlags.Instance);
            List<ObjectPicker.TabInfo> tabs = f.GetValue(picker) as List<ObjectPicker.TabInfo>;
            if (tabs != null)
                foreach (ObjectPicker.TabInfo t in tabs)
                    if (t.RowInfo != null) rows.AddRange(t.RowInfo);
            return rows;
        }

        static Dictionary<string, object> DescribeDialog(WindowBase modal)
        {
            Dictionary<string, object> d = Util.Obj();
            List<string> texts = new List<string>();
            CollectTexts(modal, texts, 0);
            d["text"] = texts;
            List<object> buttons = new List<object>();
            foreach (KeyValuePair<WindowBase, object> kv in DialogButtons(modal))
            {
                string cap = kv.Key.Caption;
                buttons.Add(string.IsNullOrEmpty(cap) ? "#" + kv.Key.ID : cap);
            }
            d["buttons"] = buttons;
            ObjectPicker picker = FindPicker(modal);
            if (picker != null)
            {
                List<string> rows = new List<string>();
                foreach (ObjectPicker.RowInfo r in PickerRowsOf(picker)) rows.Add(RowText(r));
                d["picker_rows"] = rows;
            }
            return d;
        }

        static object PendingDialogs(Args a)
        {
            WindowBase modal = UIManager.GetModalWindow();
            Dictionary<string, object> d = Util.Obj();
            d["open"] = modal != null;
            if (modal != null) d["dialog"] = DescribeDialog(modal);
            return d;
        }

        static object RespondDialog(Args a)
        {
            WindowBase modal = UIManager.GetModalWindow();
            if (modal == null) throw new McpException("no dialog is open");
            List<object> wantPick = a.List("pick");
            if (wantPick != null && wantPick.Count > 0)
            {
                ObjectPicker picker = FindPicker(modal);
                if (picker == null) throw new McpException("this dialog has no picker");
                List<ObjectPicker.RowInfo> rows = PickerRowsOf(picker);
                List<ObjectPicker.RowInfo> sel = new List<ObjectPicker.RowInfo>();
                foreach (object w in wantPick)
                {
                    string ws = Convert.ToString(w).ToLowerInvariant();
                    ObjectPicker.RowInfo best = null;
                    foreach (ObjectPicker.RowInfo r in rows)
                    {
                        string t = RowText(r).ToLowerInvariant();
                        if (t == ws) { best = r; break; }
                        if (best == null && t.IndexOf(ws) >= 0) best = r;
                    }
                    if (best == null) throw new McpException("no picker row matching '" + w + "'");
                    sel.Add(best);
                }
                picker.Selected = sel;
            }
            List<KeyValuePair<WindowBase, object>> buttons = DialogButtons(modal);
            KeyValuePair<WindowBase, object>? chosen = null;
            string want = a.Str("button", wantPick != null && wantPick.Count > 0 ? "#" + kPickerOkId : "");
            if (want.Length == 0) throw new McpException("pass button (one of the dialog's buttons) and/or pick");
            string wl = want.ToLowerInvariant();
            foreach (KeyValuePair<WindowBase, object> kv in buttons)
            {
                string cap = (kv.Key.Caption ?? "").ToLowerInvariant();
                if (cap == wl || ("#" + kv.Key.ID) == wl) { chosen = kv; break; }
                if (chosen == null && cap.Length > 0 && cap.IndexOf(wl) >= 0) chosen = kv;
            }
            if (chosen == null) throw new McpException("no button '" + want + "' in the dialog");
            WindowBase btn = chosen.Value.Key;
            object glue = chosen.Value.Value;
            MethodInfo invoke = glue.GetType().GetMethod("Invoke", new Type[] { typeof(WindowBase), typeof(UIEventArgs) });
            UIButtonClickEventArgs args = new UIButtonClickEventArgs();
            args.Init(kButtonClick, btn, btn, (int)btn.ID, 0, 0, 0f, 0f, 0f, 0f, false, null);
            // The click handler may close this dialog and open another one; keep it off the poll task.
            RunAsync("click " + want, delegate { invoke.Invoke(glue, new object[] { btn, args }); });
            Dictionary<string, object> res = Util.Obj();
            res["clicked"] = string.IsNullOrEmpty(btn.Caption) ? "#" + btn.ID : btn.Caption;
            return res;
        }

        // ------------------------------------------------------------ buy / sell / move

        static List<BuildBuyProduct> sCatalog;

        static List<BuildBuyProduct> Catalog()
        {
            if (sCatalog != null) return sCatalog;
            List<BuildBuyProduct> list = new List<BuildBuyProduct>();
            foreach (object o in UserToolUtils.GetObjectProductListFiltered(3221225471u, 4026531839u, ulong.MaxValue,
                     ulong.MaxValue, ulong.MaxValue, 0, uint.MaxValue, 0, 0))
            {
                BuildBuyProduct p = o as BuildBuyProduct;
                if (p != null && p.ShowInCatalog && p.BuyCategoryFlags != 0) list.Add(p);
            }
            sCatalog = list;
            return list;
        }

        static string KeyString(ResourceKey k)
        {
            return k.TypeId.ToString("X8") + ":" + k.GroupId.ToString("X8") + ":" + k.InstanceId.ToString("X16");
        }

        static object CatalogSearch(Args a)
        {
            string q = a.Str("query", "").ToLowerInvariant();
            float maxPrice = (float)a.Double("max_price", float.MaxValue);
            List<object> list = new List<object>();
            foreach (BuildBuyProduct p in Catalog())
            {
                string name = p.CatalogName ?? "";
                if (q.Length > 0 && name.ToLowerInvariant().IndexOf(q) < 0
                    && (p.Description ?? "").ToLowerInvariant().IndexOf(q) < 0
                    && (p.ObjectInstanceName ?? "").ToLowerInvariant().IndexOf(q) < 0) continue;
                if (p.Price > maxPrice) continue;
                Dictionary<string, object> d = Util.Obj();
                d["product"] = KeyString(p.ProductResourceKey);
                d["name"] = name;
                d["price"] = (int)p.Price;
                d["instance"] = p.ObjectInstanceName;
                d["environment"] = Util.Round(p.EnvironmentScore);
                if (p.IsWallObject) d["wall"] = true;
                string desc = p.Description ?? "";
                d["description"] = desc.Length > 120 ? desc.Substring(0, 120) + "..." : desc;
                list.Add(d);
                if (list.Count >= (int)a.Long("limit", 40)) break;
            }
            return list;
        }

        static readonly Dictionary<string, bool> sNotInventoriable = new Dictionary<string, bool>();  // product keys a Sim inventory refused (buy_object sim_inventory)

        static object BuyObject(Args a)
        {
            // Live or Buy mode: GateHandlers wraps it (kLiveOrBuy), so the story buy flow runs inside Buy mode (docs/171 §16)
            if (a.Bool("pool", false)) return BuyFromInvPool(a);  // inv_pool products, distinct (BuildCcHandlers.cs)
            // product = key; or name = catalogue name part (e.g. "CCGen", docs/191 synthetic CC) + nth = which match (mod count)
            BuildBuyProduct prod = null;
            string key = a.Has("name") ? null : a.Str("product");
            List<BuildBuyProduct> hits = new List<BuildBuyProduct>();
            foreach (BuildBuyProduct p in Catalog())
                if (key != null ? KeyString(p.ProductResourceKey) == key.ToUpperInvariant() : (p.CatalogName ?? "").ToLowerInvariant().IndexOf(a.Str("name").ToLowerInvariant()) >= 0) hits.Add(p);
            if (hits.Count > 0) prod = hits[(int)(a.Long("nth", 0) % hits.Count)];
            if (prod == null) throw new McpException("unknown product '" + (key ?? a.Str("name")) + "' (use catalog_search)");
            Household hh = Household.ActiveHousehold;
            if (hh == null) throw new McpException("no active household");
            // sim_inventory + n: labelled stress step (docs/191 cc-soak), free, straight into a Sim's inventory, not a player flow
            if (a.Bool("sim_inventory", false))
            {
                Sim s = a.Has("sim") ? Util.FindSim(a, "sim") : Util.ActiveSim();
                int n = (int)a.Long("n", 1), added = 0, skipped = 0, start = (int)(a.Long("nth", 0) % hits.Count);
                // big objects (tables, beds) never fit an inventory: the game's own TryToAdd decides; on a refused first object
                // move on to the next match (cyclic from nth), remembered per product so later steps skip it without creating one
                for (int k = 0; k < hits.Count && added == 0; k++)
                {
                    prod = hits[(start + k) % hits.Count];
                    string pk = KeyString(prod.ProductResourceKey);
                    if (sNotInventoriable.ContainsKey(pk)) { skipped++; continue; }
                    for (; added < n; added++)
                    {
                        GameObject g = GlobalFunctions.CreateObjectOutOfWorld(prod.ProductResourceKey) as GameObject;
                        if (g == null) break;
                        if (!s.Inventory.TryToAdd(g)) { g.Destroy(); break; }
                        CreatedObjects().Add(g.ObjectId.Value);  // remove_objects takes them out again (docs/200 live cycle)
                    }
                    if (added == 0) { sNotInventoriable[pk] = true; skipped++; }
                }
                Dictionary<string, object> ri = Util.Obj();
                ri["sim"] = s.FullName; ri["name"] = prod.CatalogName; ri["added"] = added; ri["matches"] = hits.Count; ri["skipped"] = skipped;
                if (added == 0) throw new McpException(s.FullName + "'s inventory took no " + prod.CatalogName);
                return ri;
            }
            int price = (int)prod.Price;
            bool free = a.Bool("free", false);
            if (!free && hh.FamilyFunds < price) throw new McpException("not enough money: costs §" + price + ", have §" + hh.FamilyFunds);

            GameObject go;
            try { go = GlobalFunctions.CreateObjectOutOfWorld(prod.ProductResourceKey) as GameObject; }
            catch (Exception e) { throw new McpException("the game could not create " + prod.CatalogName + ": " + e.GetType().Name + " (" + Simulator.sCreateObjectError + ")"); }
            if (go == null) throw new McpException("the game could not create that object");
            // kind: a move-in essential (docs/181 §3.1) must create as that class (HouseholdHandlers.EssentialKind)
            string kind = a.Str("kind", null);
            if (kind != null && EssentialKind(go) != kind) { string made = go.GetType().Name; go.Destroy(); throw new McpException(prod.CatalogName + " made a " + made + ", not a " + kind); }
            Vector3 pos;
            // on_lot: inside the house on the home lot by the game's own in-house placement (HouseholdHandlers.PlaceInHouse, the
            // search make_household's fit test used), never the inventory: move-in essentials (docs/181 §3.1; chain157 found no
            // spot for beds from the lot centre)
            bool onLot = a.Bool("on_lot", false);
            Lot home = hh.LotHome;
            if (onLot && home == null) { go.Destroy(); throw new McpException("the active household has no home lot"); }
            if (onLot) pos = Vector3.Zero;
            else if (a.Has("near")) pos = FindTarget(a, "near").Position;
            else if (a.Has("x")) pos = new Vector3((float)a.Double("x"), (float)a.Double("y", 0), (float)a.Double("z"));
            else pos = Util.ActiveSim().Position;
            Vector3 fwd = Vector3.UnitZ;
            string where;
            if (onLot && !PlaceInHouse(go, home, null))
            {
                go.Destroy();
                throw new McpException("no free spot in the house on the home lot for " + prod.CatalogName);
            }
            if (onLot)
            {
                go.AddToWorld();
                where = "placed";
            }
            else if (!a.Bool("to_inventory", false) && GlobalFunctions.FindGoodLocationNearby(go, ref pos, ref fwd))
            {
                go.SetPosition(pos);
                go.SetForward(fwd);
                go.AddToWorld();
                where = "placed";
            }
            else if (hh.SharedFamilyInventory != null && hh.SharedFamilyInventory.Inventory.TryToAdd(go))
            {
                where = "family inventory";
            }
            else
            {
                go.Destroy();
                throw new McpException("no room to place it and the family inventory refused it");
            }
            if (!free) hh.ModifyFamilyFunds(-price);
            Dictionary<string, object> r = Util.Obj();
            r["id"] = Util.Id(go.ObjectId);
            r["name"] = Util.ObjectName(go);
            r["price"] = free ? 0 : price;
            r["where"] = where;
            if (where == "placed") r["position"] = Pos(go.Position);
            r["funds"] = hh.FamilyFunds;
            return r;
        }

        static object SellObject(Args a)
        {
            // Live or Buy mode: GateHandlers wraps it (kLiveOrBuy), so the story buy flow runs inside Buy mode (docs/171 §16)
            GameObject o = FindTarget(a, "target");
            if (o is Sim || o is Lot) throw new McpException("that cannot be sold");
            if (!o.CanBeSold()) throw new McpException(Util.ObjectName(o) + " cannot be sold");
            if (o.InUse) throw new McpException(Util.ObjectName(o) + " is in use right now");
            Household hh = Household.ActiveHousehold;
            int before = hh != null ? hh.FamilyFunds : 0;
            int value = o.Value;
            o.SellBase();
            // SellBase only pays out and hides the object outside buy mode; it stays in the save unless destroyed.
            o.Destroy();
            Dictionary<string, object> r = Util.Obj();
            r["sold"] = Util.ObjectName(o);
            r["value"] = value;
            if (hh != null) r["funds"] = hh.FamilyFunds;
            return r;
        }

        static object MoveObject(Args a)
        {
            RequireLive();
            GameObject o = FindTarget(a, "target");
            if (o is Sim || o is Lot) throw new McpException("use go_here to move sims");
            Vector3 pos;
            if (a.Has("near")) pos = FindTarget(a, "near").Position;
            else pos = new Vector3((float)a.Double("x"), (float)a.Double("y", 0), (float)a.Double("z"));
            Vector3 fwd = o.ForwardVector;
            if (!GlobalFunctions.FindGoodLocationNearby(o, ref pos, ref fwd))
                throw new McpException("no free spot found there");
            o.SetPosition(pos);
            o.SetForward(fwd);
            Dictionary<string, object> r = Util.Obj();
            r["moved"] = Util.ObjectName(o);
            r["position"] = Pos(o.Position);
            return r;
        }
    }
}
