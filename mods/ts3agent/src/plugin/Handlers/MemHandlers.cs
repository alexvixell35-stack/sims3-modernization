using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.SimIFace;
using Sims3.SimIFace.BuildBuy;
using Sims3.UI;

namespace Ts3Agent
{
    // docs/191 module 2 (memory tool): the forced-GC probe and the leak-suspect loops of docs/192 / docs/193, each bracketed
    // by mem_mark samples (ts3native [MemDiag] diag: mem + ts3fixes diag: memsus lines). Loops run in a game task and
    // answer {result: true} with their progress; a repro waits for them with waitS.
    public static partial class Handlers
    {
        // ts3fixes Fixes.MemSusPoll (MemSuspects.cs): answers the suspect counters of the mark just taken
        static MethodInfo sSusPoll;
        static bool sSusLooked;
        static bool SusPoll()
        {
            if (!sSusLooked)
            {
                sSusLooked = true;  // shortcut: looked up once per session (ts3fixes loads at startup, before any agent call)
                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type t = asm.GetType("Ts3Fixes.Fixes");
                    if (t != null) { sSusPoll = t.GetMethod("MemSusPoll", BindingFlags.Public | BindingFlags.Static); break; }
                }
            }
            try { return sSusPoll != null && (bool)sSusPoll.Invoke(null, null); } catch (Exception) { return false; }
        }

        // mem_gc {rounds?: 2} | {result: true}: mark gc-before, then rounds x (GC.Collect(), sleep 2 ticks), mark gc-after.
        // No GC.WaitForPendingFinalizers(): in TS3W.exe it returns at once without running anything (icall 0xE78F80 waits on
        // pending_done_event [0x11F4308], which nothing ever creates; docs/51 §351). Finalizers run only from
        // MonoScriptHost_Simulate (mono_gc_invoke_finalizers 0xE697F0, docs/130), so each round sleeps two ticks for that drain;
        // the next round's collection then frees what the finalizers released (Layout / UIImage natives, docs/193 L2/L3).
        // The gap gc-before -> gc-after is the collectable-but-uncollected share; L1 (static dictionary) must not drop.
        static Dictionary<string, object> sGcResult;
        static object MemGc(Args a)
        {
            if (a.Has("result"))
            {
                if (sGcResult == null) throw new McpException("no mem_gc started");
                return sGcResult;
            }
            if (sGcResult != null && !(bool)sGcResult["done"]) throw new McpException("mem_gc still running");
            int rounds = Math.Max(1, (int)a.Long("rounds", 2));
            Dictionary<string, object> r = Util.Obj();
            bool sus;
            r["accepted"] = MemMarkTag("gc-before", out sus);
            r["suspects"] = sus;
            r["done"] = false;
            r["collections_before"] = GC.CollectionCount(0);
            sGcResult = r;
            RunAsync("mem_gc", delegate
            {
                try
                {
                    int t0 = System.Environment.TickCount;
                    for (int i = 0; i < rounds; i++)
                    {
                        GC.Collect();
                        Ticks(2);
                    }
                    bool s2;
                    r["gc_ms"] = System.Environment.TickCount - t0;
                    r["collections_after"] = GC.CollectionCount(0);
                    r["after_accepted"] = MemMarkTag("gc-after", out s2);
                    r["after_suspects"] = s2;
                }
                catch (Exception e) { r["error"] = DescribeError(e); }
                finally { r["done"] = true; }  // a waiting repro never polls forever
            });
            return r;
        }

        // catalog_loop {pucks?: [4099, ...], rounds?: 5, dwell_ticks?: 30} | {result: true}: Build mode must be open (build_mode
        // on). Per round each puck's catalogue is opened with the game's own puck click handler
        // (BuildController.OnToolSelectionPuckButtonClick, what the button calls; EA calls it with null args itself) and
        // closed with the middle-puck back button's handler (OnCatalogBackButtonClick), dwell_ticks after each; after each
        // round a mark cat-r<N>. Default pucks: wall paint 4099, flowers 4106, doors 4101, shrubs 4108, fireplaces 4096,
        // columns 4103, ground cover 4107, trees 4097 (docs/193 §5 L1/L2: each populate wraps new CustomContentIcon cells).
        static readonly long[] kCatalogPucks = { 4099, 4106, 4101, 4108, 4096, 4103, 4107, 4097 };
        static Dictionary<string, object> sCatResult;
        static object CatalogLoop(Args a)
        {
            if (a.Has("result"))
            {
                if (sCatResult == null) throw new McpException("no catalog_loop started");
                return sCatResult;
            }
            if (sCatResult != null && !(bool)sCatResult["done"]) throw new McpException("catalog_loop still running");
            object ctl = typeof(BuildController).GetField("sController", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            if (ctl == null) throw new McpException("Build mode is not open (build_mode on first)");
            MethodInfo open = typeof(BuildController).GetMethod("OnToolSelectionPuckButtonClick", BindingFlags.NonPublic | BindingFlags.Instance);
            MethodInfo back = typeof(BuildController).GetMethod("OnCatalogBackButtonClick", BindingFlags.NonPublic | BindingFlags.Instance);
            if (open == null || back == null) throw new McpException("BuildController puck / back handlers not found");
            List<long> pucks = new List<long>();
            if (a.Has("pucks")) foreach (object o in a.List("pucks")) pucks.Add(Convert.ToInt64(o));
            else pucks.AddRange(kCatalogPucks);
            int rounds = Math.Max(1, (int)a.Long("rounds", 5));
            uint dwell = (uint)Math.Max(1, a.Long("dwell_ticks", 30));
            List<object> missing = new List<object>();
            List<WindowBase> buttons = new List<WindowBase>();
            foreach (long id in pucks)
            {
                WindowBase b = ((WindowBase)ctl).GetChildByID((uint)id, true);
                if (b == null) missing.Add(id); else buttons.Add(b);
            }
            if (buttons.Count == 0) throw new McpException("none of the puck buttons found: " + Json.Write(missing));
            Dictionary<string, object> r = Util.Obj();
            r["done"] = false;
            r["rounds_done"] = 0;
            r["opened"] = 0;
            r["missing"] = missing;
            sCatResult = r;
            RunAsync("catalog_loop", delegate
            {
                bool sus;
                try
                {
                    for (int round = 1; round <= rounds; round++)
                    {
                        foreach (WindowBase b in buttons)
                        {
                            object c = typeof(BuildController).GetField("sController", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
                            if (c == null) { r["error"] = "Build mode closed during the loop"; return; }
                            open.Invoke(c, new object[] { b, null });
                            r["opened"] = (int)r["opened"] + 1;
                            Ticks(dwell);
                            back.Invoke(c, new object[] { null, null });
                            Ticks(dwell);
                        }
                        MemMarkTag("cat-r" + round, out sus);
                        r["rounds_done"] = round;
                    }
                }
                catch (Exception e) { r["error"] = DescribeError(e); }
                finally { r["done"] = true; }
            });
            return r;
        }

        // buy_sell_loop {product | name, n?: 20, mark_every?: 5, dwell_ticks?: 10} | {result: true}: live mode; n times buy_object
        // (free, near the active Sim: the object is created and added to the world, so its OnStartup runs as after a
        // purchase) then sell_object; a mark place-r<i> every mark_every rounds. docs/192 #2/#3: store objects and spawners
        // subscribe World / LoadSaveManager / GameEntryMovingModel events in OnStartup and never unsubscribe.
        static Dictionary<string, object> sBuySellResult;
        static object BuySellLoop(Args a)
        {
            if (a.Has("result"))
            {
                if (sBuySellResult == null) throw new McpException("no buy_sell_loop started");
                return sBuySellResult;
            }
            if (sBuySellResult != null && !(bool)sBuySellResult["done"]) throw new McpException("buy_sell_loop still running");
            RequireLive();
            string key = a.Str("product", null), r0name = null;
            if (key == null)
            {
                string want = a.Str("name").ToLowerInvariant();
                foreach (BuildBuyProduct p in Catalog())  // catalogue name or object instance name, as catalog_search matches
                    if ((p.CatalogName ?? "").ToLowerInvariant().IndexOf(want) >= 0 || (p.ObjectInstanceName ?? "").ToLowerInvariant().IndexOf(want) >= 0)
                    { key = KeyString(p.ProductResourceKey); r0name = p.CatalogName; break; }
                if (key == null) throw new McpException("no catalogue product named like '" + want + "'");
            }
            int n = Math.Max(1, (int)a.Long("n", 20)), every = Math.Max(1, (int)a.Long("mark_every", 5));
            uint dwell = (uint)Math.Max(1, a.Long("dwell_ticks", 10));
            Dictionary<string, object> r = Util.Obj();
            r["product"] = key;
            if (r0name != null) r["name"] = r0name;
            r["done"] = false;
            r["rounds_done"] = 0;
            sBuySellResult = r;
            RunAsync("buy_sell_loop", delegate
            {
                bool sus;
                try
                {
                    for (int i = 1; i <= n; i++)
                    {
                        Dictionary<string, object> buy = Util.Obj();
                        buy["product"] = key;
                        buy["free"] = true;
                        Dictionary<string, object> got = (Dictionary<string, object>)BuyObject(new Args(buy));
                        Ticks(dwell);
                        Dictionary<string, object> sell = Util.Obj();
                        sell["target"] = got["id"];
                        SellObject(new Args(sell));
                        Ticks(dwell);
                        r["rounds_done"] = i;
                        if (i % every == 0) MemMarkTag("place-r" + i, out sus);
                    }
                }
                catch (Exception e) { r["error"] = DescribeError(e); }
                finally { r["done"] = true; }
            });
            return r;
        }

        // place_cancel_loop {name?: "sleeping bag", n?: 50, mark_every?: 10, dwell_ticks?: 10, sim?} | {result: true}: docs/192 #1
        // (leak-place-mapview). One catalogue object that is an IPlaceableObject (sleeping bag, tent, fire pit...) is created
        // into the Sim's inventory (default: the selected Sim, the player's flow; it leaves again at the end). n times the
        // game's own IPlaceableObject_Place runs from the inventory (RunFromInventory) in a task of its own, the hand tool
        // opens, and after dwell_ticks UserToolUtils.OnClose() closes it (EA's own Cancel): the item stays in the inventory.
        // EA's Run adds a GameStateChanging and an OnCameraMapViewEnabledCallback handler each run and never removes them
        // [verified decompiled]: gsc / mapv before and after (invocation-list lengths) show +n each if the leak is real;
        // a mark place-mv-r<i> every mark_every rounds carries ts3fixes' memsus gsc / mapv counts.
        static Dictionary<string, object> sPlaceResult;
        static object PlaceCancelLoop(Args a)
        {
            if (a.Has("result"))
            {
                if (sPlaceResult == null) throw new McpException("no place_cancel_loop started");
                return sPlaceResult;
            }
            if (sPlaceResult != null && !(bool)sPlaceResult["done"]) throw new McpException("place_cancel_loop still running");
            RequireLive();
            Sims3.Gameplay.Actors.Sim s = a.Has("sim") ? Util.FindSim(a, "sim") : Util.ActiveSim();
            string want = a.Str("name", "sleeping bag").ToLowerInvariant();
            GameObject item = null;
            string pname = null;
            foreach (BuildBuyProduct p in Catalog())
            {
                if ((p.CatalogName ?? "").ToLowerInvariant().IndexOf(want) < 0) continue;
                GameObject g = null;
                try { g = Sims3.Gameplay.GlobalFunctions.CreateObjectOutOfWorld(p.ProductResourceKey) as GameObject; } catch (Exception) { }
                if (g is IPlaceableObject) { item = g; pname = p.CatalogName; break; }
                if (g != null) g.Destroy();
            }
            if (item == null) throw new McpException("no catalogue product named like '" + want + "' makes an IPlaceableObject");
            if (!s.Inventory.TryToAdd(item)) { item.Destroy(); throw new McpException(s.FullName + "'s inventory took no " + pname); }
            int n = Math.Max(1, (int)a.Long("n", 50)), every = Math.Max(1, (int)a.Long("mark_every", 10));
            uint dwell = (uint)Math.Max(1, a.Long("dwell_ticks", 10));
            Dictionary<string, object> r = Util.Obj();
            r["sim"] = s.FullName; r["item"] = pname; r["done"] = false; r["rounds_done"] = 0; r["rounds_stuck"] = 0;
            r["gsc_before"] = PlaceHandlerCount("gsc"); r["mapv_before"] = PlaceHandlerCount("mapv");
            sPlaceResult = r;
            RunAsync("place_cancel_loop", delegate
            {
                bool sus;
                try
                {
                    for (int i = 1; i <= n; i++)
                    {
                        bool[] ended = { false };
                        Sims3.Gameplay.Interactions.InteractionInstance ii = IPlaceableObject_Place.Singleton.CreateInstance(item, s,
                            new Sims3.Gameplay.Interactions.InteractionPriority(Sims3.Gameplay.Interactions.InteractionPriorityLevel.UserDirected), false, true);
                        RunAsync("place_cancel round " + i, delegate { try { ii.RunInteraction(); } finally { ended[0] = true; } });
                        Ticks(dwell);
                        UserToolUtils.OnClose();
                        for (int w = 0; w < 300 && !ended[0]; w++) { Ticks(1); if (w % 30 == 29) UserToolUtils.OnClose(); }
                        if (!ended[0]) { r["rounds_stuck"] = (int)r["rounds_stuck"] + 1; r["error"] = "round " + i + " did not end in 300 ticks after OnClose"; return; }
                        r["rounds_done"] = i;
                        if (i % every == 0) MemMarkTag("place-mv-r" + i, out sus);
                    }
                }
                catch (Exception e) { r["error"] = DescribeError(e); }
                finally
                {
                    r["gsc_after"] = PlaceHandlerCount("gsc"); r["mapv_after"] = PlaceHandlerCount("mapv");
                    try { if (s.Inventory != null && s.Inventory.Contains(item)) s.Inventory.TryToRemove(item); item.Destroy(); } catch (Exception) { }
                    r["done"] = true;
                }
            });
            return r;
        }

        // invocation-list length of Responder.Instance's GameStateChanging (gsc) or CameraController.OnCameraMapViewEnabledCallback
        // (mapv), the fields ts3fixes MemSuspects reads; -1 = field not found
        static int PlaceHandlerCount(string which)
        {
            const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            object owner = which == "gsc" ? (object)Responder.Instance : null;
            Type t = which == "gsc" ? (owner != null ? owner.GetType() : null) : typeof(CameraController);
            for (; t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField(which == "gsc" ? "GameStateChanging" : "OnCameraMapViewEnabledCallback", any | (owner != null ? BindingFlags.Instance : BindingFlags.Static));
                if (f == null) continue;
                Delegate d = f.GetValue(owner) as Delegate;
                return d == null ? 0 : d.GetInvocationList().Length;
            }
            return -1;
        }
    }
}
