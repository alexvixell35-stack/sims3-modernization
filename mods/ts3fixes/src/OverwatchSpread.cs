using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // NRaas Overwatch's nightly alarm (Overwatch.OnTimer) runs every cleanup option in one FunctionTask tick; with heavy
    // store content its inventory cleanup alone is a ~500 ms frame once per sim-day (docs/96 §4.1, docs/145).
    // Switch "overwatchspread" (tuning kOverwatchSpread, default off) takes over the alarm for the session:
    //   - each cleanup type runs at its own time, kGapMinutes sim-minutes apart, in its own one-shot task, in Overwatch's
    //     own order; every type except the inventory cleanup runs Overwatch's own code there (same work, later time);
    //   - the inventory cleanup is ours, written from what Overwatch's does (docs/145 §2.2), not from its source: same
    //     inventories, same repairs, no catalog-name lookups (Overwatch looks up a name per world object for its log),
    //     at most kMaxItemsPerSlice inventory items or 2 ms per tick (a big inventory continues in the next tick). It runs only when the player's Overwatch setting has
    //     the inventory cleanup on; the setting itself is never written.
    // Switch "overwatchchain" (tuning kOverwatchChain, default off; only when "overwatchspread" is off, docs/145 §10-11):
    // Overwatch's own OnTimer loop runs in a one-shot task of ours (OwChainTheirs), and a hook on AlarmOption.PerformAlarm
    // (the base every cleanup type but the console's RunAlarmCommands inherits) sleeps one sim tick before each type and
    // runs our sliced pass for the inventory cleanup. Order, settings, exception handling and notice batching stay
    // Overwatch's, and the call stack into each type is Overwatch's own (interface call from OnTimer), so overwatchstuck's
    // per-plan Sleep(0) inside StuckCheck works as in Overwatch's own path. The first chain (docs/145 §10) called
    // PerformAlarm from our loop (reflection, then a delegate) and StuckCheck got no yield there (docs/145 §11).
    // Counters: [244] chained alarms, [245] ticks yielded between types ([158]/[223]/[224] also count chained types).
    // Overwatch builds that spread the cleanups themselves carry OverwatchSpreadCore.MarkerField: then we patch nothing
    // and log "upstream spreading found". No Overwatch, or an OnTimer we do not recognise: logged, nothing patched.
    // Test switches: overwatchspreadverify (count what is left to repair before and after either path, one log line),
    // overwatchspreadfakeupstream (behave as if the marker were present). Trigger: OverwatchSpreadNow (repros.json).
    // Counters: [156] alarms taken over, [157] cleanup types scheduled, [158] Overwatch types run at their slot,
    // [159] our inventory passes finished, [160] inventories checked, [161] repairs made, [162] ticks used,
    // [163] longest tick of the pass in us, [164] repairs still pending after the last verified pass, [165] upstream
    // marker version (-1 = faked), [166] alarms left to Overwatch's own path, [223] longest type run whole at its slot (us),
    // [224] types that took >= kSlowTypeUs there (each logged by name). [162]/[163] include the world scan for inventories.
    public static partial class Fixes
    {
        static bool sOwSpreadTried, sOwSpreadReady, sOwUpstream;
        static bool sOwChainReady, sOwChaining, sOwChainStop; // PerformAlarm hook in place; a chain is running; world gone mid-chain
        static int sOwChainIndex;                             // types seen in the running chain
        static int sOwUpstreamVersion;                        // marker value read at world load (0 = none), see OverwatchStuck.cs
        static Type sOwType;
        static MethodInfo sOwTimer, sOwFind, sOwPerform;  // Overwatch.OnTimer(), DerivativeSearch.Find<IAlarmOption>(), IAlarmOption.PerformAlarm()
        static PropertyInfo sOwSettings;
        static StopWatch sOwTypeWatch;
        static FieldInfo sOwInvSetting;         // PersistedSettings.mCleanupInventory, read only

        internal static void ApplyOverwatchSpread(bool lastTry)
        {
            if (sOwSpreadTried) return;
            Assembly ow = null;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                if (a.GetName().Name == "NRaasOverwatch") { ow = a; break; }
            if (ow == null)
            {
                if (lastTry) { sOwSpreadTried = true; AppliedLog().Add("overwatchspread: Overwatch not loaded, off"); }
                return;
            }
            sOwSpreadTried = true;
            sOwType = ow.GetType("NRaas.Overwatch");
            const BindingFlags st = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            sOwTimer = sOwType == null ? null : sOwType.GetMethod("OnTimer", st, null, Type.EmptyTypes, null);
            Type ds = ow.GetType("NRaas.Common+DerivativeSearch"), alarm = ow.GetType("NRaas.OverwatchSpace.Interfaces.IAlarmOption");
            if (ds != null && alarm != null)
                foreach (MethodInfo m in ds.GetMethods(BindingFlags.Static | BindingFlags.Public))
                    if (m.Name == "Find" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0) { sOwFind = m.MakeGenericMethod(alarm); break; }
            sOwPerform = alarm == null ? null : alarm.GetMethod("PerformAlarm", Type.EmptyTypes);
            sOwSettings = sOwType == null ? null : sOwType.GetProperty("Settings", BindingFlags.Static | BindingFlags.Public);
            sOwInvSetting = sOwSettings == null ? null : sOwSettings.PropertyType.GetField("mCleanupInventory");
            if (OverwatchSpreadCore.HasMarker(sOwType)) { sOwUpstream = true; return; } // logged with its version at world load
            if (sOwTimer == null || sOwTimer.ReturnType != typeof(void) || sOwFind == null || sOwPerform == null || sOwInvSetting == null || sOwInvSetting.FieldType != typeof(bool))
            {
                AppliedLog().Add("overwatchspread: Overwatch.OnTimer or its option list differs from V123, off");
                return;
            }
            sOwSpreadReady = Patch(sOwType, "OnTimer", Type.EmptyTypes, typeof(OverwatchSpreadHook), "TimerHook", "TimerOrig");
            Type alarmBase = ow.GetType("NRaas.OverwatchSpace.Alarms.AlarmOption");
            sOwChainReady = sOwSpreadReady && alarmBase != null && Patch(alarmBase, "PerformAlarm", Type.EmptyTypes, typeof(OwAlarmHook), "PerformHook", "PerformOrig");
        }

        // World load: the marker's version is safe to read now (Overwatch's static constructor has run).
        internal static void OverwatchSpreadWorldLoad()
        {
            sOwChaining = false; // a chain task dropped with the last world never ran its finally
            sOwInStuck = false;  // same for a Stuck Check run (OverwatchStuck.cs)
            if (!sOwUpstream || sOwType == null) return;
            int v = 0;
            try { v = (int)sOwType.GetField(OverwatchSpreadCore.MarkerField).GetValue(null); } catch (Exception) { }
            PatchCounters()[165] = v;
            sOwUpstreamVersion = v;
            OwLog("upstream spreading found (v" + v + "), ours off" + (OverwatchSpreadCore.OurStuckYield(v) ? "" : ", stuck-check yields theirs"));
        }

        static void OwLog(string line)
        {
            AppliedLog().Add("overwatchspread: " + line);
            List<object> log = AppDomain.CurrentDomain.GetData("ts3fixes.overwatchspread") as List<object>;
            if (log == null) { log = new List<object>(); AppDomain.CurrentDomain.SetData("ts3fixes.overwatchspread", log); }
            if (log.Count >= 200) log.RemoveAt(0);
            log.Add("simmin " + SimClock.ElapsedTime(TimeUnit.Minutes).ToString("0") + " " + line);
        }

        static OverwatchSpreadCore.Mode OwMode()
        {
            return OverwatchSpreadCore.WithChain(OverwatchSpreadCore.Decide(ShortcutOn("overwatchspread"), sOwSpreadReady, sOwUpstream,
                ShortcutOn("overwatchspreadfakeupstream")), ShortcutOn("overwatchchain"), sOwChainReady);
        }

        // Chain: Overwatch's own OnTimer in a one-shot task of ours (named in hitch lines, docs/166), the PerformAlarm hook
        // spacing the types (OwAlarmHook).
        static void OwChainTheirs()
        {
            PatchCounters()[244]++;
            sOwChaining = true;
            sOwChainStop = false;
            sOwChainIndex = 0;
            try { OverwatchSpreadHook.TimerOrig(); }
            finally { sOwChaining = false; }
        }

        // One chained type, called by Overwatch's own loop in place of AlarmOption.PerformAlarm. Outside a chain (Overwatch's
        // own path, spread slots) it is the original.
        public class OwAlarmHook
        {
            public void PerformHook()
            {
                // the Stuck Check marked as running for overwatchstuckslice (OverwatchStuck.cs, docs/145 §12); inline: this body
                // runs with Overwatch's option as this, so no other instance method of the hook class is called
                bool stuck = OwName(this) == "StuckCheck";
                if (!sOwChaining)
                {
                    if (stuck) OwStuckBegin();
                    try { PerformOrig(); }
                    finally { if (stuck) OwStuckEnd(); }
                    return;
                }
                if (sOwChainStop) return;
                bool may = MayYield();
                if (OverwatchSpreadCore.SleepBefore(sOwChainIndex++, may))
                {
                    Simulator.Sleep(0);
                    PatchCounters()[245]++;
                    if (WorldGone()) { sOwChainStop = true; OwLog("chain stopped: world gone"); return; }
                }
                object option = this;
                bool inv = OwIsInventory(option);
                OverwatchSpreadCore.TypeRun run = OverwatchSpreadCore.ChainType(inv, inv && (bool)sOwInvSetting.GetValue(sOwSettings.GetValue(null, null)), may);
                if (run == OverwatchSpreadCore.TypeRun.Nothing) return;
                if (run == OverwatchSpreadCore.TypeRun.OursInventory)
                {
                    try { OwInventoryPass(ShortcutOn("overwatchspreadverify")); }
                    catch (Exception e) { OwLog("inventory pass failed: " + e.Message); }
                    return;
                }
                if (sOwTypeWatch == null) sOwTypeWatch = StopWatch.Create(StopWatch.TickStyles.Microseconds);
                sOwTypeWatch.Restart();
                long tick = Simulator.TicksElapsed();
                if (stuck) OwStuckBegin();
                try { PerformOrig(); }
                finally { if (stuck) OwStuckEnd(); }
                OwTypeTime(option, tick, sOwTypeWatch.GetElapsedTime());
            }
            public void PerformOrig() { } // receives AlarmOption.PerformAlarm's original header
        }

        // A type's run, if it stayed in one tick: [223] longest, [224] + a log line at >= kSlowTypeUs (docs/145 §9).
        static void OwTypeTime(object option, long tick, long us)
        {
            int[] c = PatchCounters();
            c[158]++;
            if (Simulator.TicksElapsed() != tick) return; // a type that slept is not one tick (RecoverStrandedToddlers, StuckCheck with overwatchstuck)
            if (us > c[223]) c[223] = (int)us;
            if (us >= OverwatchSpreadCore.kSlowTypeUs) { c[224]++; OwLog(OwName(option) + " (Overwatch's own code) took " + (us / 1000.0).ToString("0.0") + " ms in one tick"); }
        }

        static IList OwOptions() { return sOwFind.Invoke(null, null) as IList; }

        static string OwName(object option) { return option.GetType().Name; }

        static bool OwIsInventory(object option) { return OwName(option) == "CleanupInventory"; }

        // One cleanup type now: ours for the inventory cleanup (when the player has it on), else Overwatch's own option.
        static void OwRunType(object option, bool ours)
        {
            try
            {
                if (ours && OwIsInventory(option))
                {
                    if ((bool)sOwInvSetting.GetValue(sOwSettings.GetValue(null, null))) OwInventoryPass(ShortcutOn("overwatchspreadverify"));
                    return;
                }
                // Overwatch's own code runs whole at its slot: timed, so a heavy type shows as the burst left over (docs/145 §9)
                if (sOwTypeWatch == null) sOwTypeWatch = StopWatch.Create(StopWatch.TickStyles.Microseconds); // own watch: a type may sleep
                sOwTypeWatch.Restart();
                long tick = Simulator.TicksElapsed();
                OwPerformFn perform = OwPerformDelegate(option);
                if (perform == null) { OwLog(OwName(option) + ": no PerformAlarm delegate, skipped"); return; }
                perform();
                long us = sOwTypeWatch.GetElapsedTime();
                if (ours) OwTypeTime(option, tick, us);
            }
            catch (Exception e) { OwLog(OwName(option) + " failed: " + (e.InnerException ?? e).Message); }
        }

        // Spread slots and the single-type trigger only (the chain goes through Overwatch's own loop). A delegate, not
        // MethodInfo.Invoke (a nested native->managed call: no yield inside it); StuckCheck still got no per-plan yield
        // this way in the 2026-10-23 loop (docs/145 §11). Never sOwPerform.Invoke: it dispatches to the swapped
        // AlarmOption.PerformAlarm and Mono throws TargetException (tools/selftest/swapinvoke). No interface map = the
        // option type's own PerformAlarm; null = skip the type.
        delegate void OwPerformFn();
        static OwPerformFn OwPerformDelegate(object option)
        {
            try
            {
                InterfaceMapping map = option.GetType().GetInterfaceMap(sOwPerform.DeclaringType);
                for (int i = 0; i < map.InterfaceMethods.Length; i++)
                    if (map.InterfaceMethods[i] == sOwPerform)
                        return (OwPerformFn)Delegate.CreateDelegate(typeof(OwPerformFn), option, map.TargetMethods[i]);
            }
            catch (Exception e) { OwLog(OwName(option) + ": no interface map (" + e.Message + ")"); }
            try
            {
                MethodInfo m = option.GetType().GetMethod("PerformAlarm", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                if (m != null) return (OwPerformFn)Delegate.CreateDelegate(typeof(OwPerformFn), option, m);
            }
            catch (Exception e) { OwLog(OwName(option) + ": no delegate (" + e.Message + ")"); }
            return null;
        }

        static void OwStartTask(object option, bool ours)
        {
            Simulator.AddObject(new LoopTask(delegate { OwRunType(option, ours); }));
        }

        // Our alarm: Overwatch's option list, one alarm per type, gapMinutes apart (the first one right away).
        static void OwSpread(int gapMinutes)
        {
            IList options = OwOptions();
            int[] c = PatchCounters();
            c[156]++;
            for (int i = 0; i < options.Count; i++)
            {
                object option = options[i];
                c[157]++;
                if (i == 0) { OwStartTask(option, true); continue; }
                AlarmManager.Global.AddAlarm(OverwatchSpreadCore.SlotMinutes(i, gapMinutes), TimeUnit.Minutes,
                    delegate { OwStartTask(option, true); }, "Ts3Fixes.OverwatchSpread", AlarmType.NeverPersisted, null);
            }
            OwLog(options.Count + " cleanup types scheduled, " + gapMinutes + " sim-minutes apart");
        }

        // Inventories Overwatch's cleanup walks: every world object's, then each household's family and fridge inventory.
        static List<Inventory> OwInventories()
        {
            List<Inventory> list = new List<Inventory>();
            foreach (GameObject o in Sims3.Gameplay.Queries.GetObjects<GameObject>())
                if (o.Inventory != null) list.Add(o.Inventory);
            OwHouseholdInventories(list);
            return list;
        }

        static void OwHouseholdInventories(List<Inventory> list)
        {
            foreach (Household h in new List<Household>(Household.sHouseholdList))
            {
                if (h.SharedFamilyInventory != null && h.SharedFamilyInventory.Inventory != null) list.Add(h.SharedFamilyInventory.Inventory);
                if (h.SharedFridgeInventory != null && h.SharedFridgeInventory.Inventory != null) list.Add(h.SharedFridgeInventory.Inventory);
            }
        }

        // Repairs per kind: 0 empty or broken stacks removed, 1 items without an object, 2 items without an item component
        // (not in a household inventory), 3 destroyed items, 4 items given back their parent inventory, 5 broken jam jars.
        // fix false = only count (verify).
        static void OwCheckInventory(Inventory inv, bool fix, int[] n)
        {
            try
            {
                List<uint> emptyStacks = new List<uint>();
                foreach (KeyValuePair<uint, InventoryStack> kv in inv.InventoryItems)
                {
                    ListSorted<InventoryItem> items = kv.Value == null ? null : kv.Value.List;
                    if (items == null) { emptyStacks.Add(kv.Key); continue; }
                    int left = items.Count;
                    for (int i = items.Count - 1; i >= 0; i--) if (OwCheckItem(inv, items, i, fix, n)) left--;
                    if ((fix ? items.Count : left) == 0) emptyStacks.Add(kv.Key);
                }
                n[0] += emptyStacks.Count;
                if (fix) foreach (uint key in emptyStacks) inv.InventoryItems.Remove(key);
            }
            catch (Exception) { n[6]++; } // as Overwatch: a broken inventory is left as it is
        }

        // One item of a stack at index i, as Overwatch checks it; true when it is (fix false: would be) taken out of the stack.
        static bool OwCheckItem(Inventory inv, ListSorted<InventoryItem> items, int i, bool fix, int[] n)
        {
            IGameObject obj = items[i].Object;
            int kind = -1;
            if (obj == null) kind = 1;
            else if (obj.ItemComp == null) { if (!(inv.Owner is IHouseholdInventory)) kind = 2; }
            else if (obj.HasBeenDestroyed) kind = 3;
            else
            {
                if (obj.ItemComp.InventoryParent == null)
                {
                    n[4]++;
                    if (fix) obj.ItemComp.InventoryParent = inv.Owner.Inventory;
                }
                if (OwBrokenJamJar(obj))
                {
                    n[5]++;
                    if (fix)
                    {
                        try { obj.Dispose(); obj.Destroy(); items.RemoveAt(i); } catch (Exception) { }
                    }
                    return true;
                }
            }
            if (kind < 0) return false;
            n[kind]++;
            if (fix) items.RemoveAt(i);
            return true;
        }

        // One inventory as units, built at its turn: one per item (Overwatch's order, each found again in its stack), then
        // the stack clean-up for the stacks seen at the turn (missing or left empty). An exception leaves the rest of that
        // inventory as it is, as Overwatch does; an owner destroyed while we slept ends it.
        static List<Job> OwInventoryUnits(Inventory inv, int[] n)
        {
            List<Job> u = new List<Job>();
            IGameObject owner = inv.Owner;
            if (owner != null && owner.HasBeenDestroyed) return u;
            bool failed = false;
            Gate stop = delegate { IGameObject o = inv.Owner; return failed || (o != null && o.HasBeenDestroyed); };
            List<uint> keys = new List<uint>(inv.InventoryItems.Keys);
            foreach (uint key in keys)
            {
                InventoryStack stack = inv.InventoryItems[key];
                ListSorted<InventoryItem> items = stack == null ? null : stack.List;
                if (items == null) continue; // removed by the last unit
                EaSpreadCore.AddItemUnits<InventoryItem>(u, delegate { return items.Count; }, delegate(int i) { return items[i]; },
                    delegate(int i) { try { OwCheckItem(inv, items, i, true, n); } catch (Exception) { failed = true; n[6]++; } }, stop);
            }
            u.Add(delegate
            {
                if (stop()) return;
                foreach (uint key in keys)
                {
                    InventoryStack stack;
                    if (!inv.InventoryItems.TryGetValue(key, out stack)) continue;
                    if (stack == null || stack.List == null || stack.List.Count == 0) { inv.InventoryItems.Remove(key); n[0]++; }
                }
                PatchCounters()[160]++;
            });
            return u;
        }

        // A jam jar whose catalog name cannot be read is the one corrupt object Overwatch's cleanup deletes; only jam jars
        // need the name read, so no other item pays for a string-table lookup.
        static bool OwBrokenJamJar(IGameObject obj)
        {
            GameObject g = obj as GameObject;
            if (g == null || g.GetType().ToString().IndexOf("JamJar") < 0) return false;
            try { string name = g.CatalogName; return false; }
            catch (Exception) { return true; }
        }

        static int OwPending()
        {
            int[] n = new int[7];
            foreach (Inventory inv in OwInventories()) OwCheckInventory(inv, false, n);
            return n[0] + n[1] + n[2] + n[3] + n[4] + n[5];
        }

        // Our inventory cleanup, sliced over sim ticks. Runs in its own one-shot task.
        static void OwInventoryPass(bool verify)
        {
            int[] c = PatchCounters();
            int before = verify ? OwPending() : -1;
            int[] n = new int[7];
            int ticks = 1, mostItems = 0;
            long maxUs = 0;
            // The world scan was one tick over every world object (o.Inventory each), outside the measured slices
            // (docs/145 §9): now the query is timed and the per-object check runs a slice per tick like the items.
            StopWatch w = EaWatch();
            w.Restart();
            GameObject[] objects = Sims3.Gameplay.Queries.GetObjects<GameObject>();
            maxUs = w.GetElapsedTime();
            List<Inventory> all = new List<Inventory>();
            Simulator.Sleep(0);
            ticks++;
            if (WorldGone() || !RunSliced(new SlicedPass<GameObject>(objects), delegate(GameObject o) { if (!o.HasBeenDestroyed && o.Inventory != null) all.Add(o.Inventory); },
                    OverwatchSpreadCore.kMaxScanPerSlice, ref ticks, ref maxUs))
            {
                OwLog("inventory pass stopped: world gone");
                return;
            }
            OwHouseholdInventories(all);
            // the budget counts items, not inventories: one inventory with hundreds of items continues in the next tick
            if (!RunUnits(all, delegate(Inventory inv) { return OwInventoryUnits(inv, n); }, OverwatchSpreadCore.kMaxItemsPerSlice, n, ref ticks, ref maxUs, ref mostItems))
            {
                OwLog("inventory pass stopped: world gone");
                return;
            }
            if (mostItems > c[210]) c[210] = mostItems;
            int repairs = n[0] + n[1] + n[2] + n[3] + n[4] + n[5];
            c[159]++;
            c[161] += repairs;
            c[162] += ticks;
            if (maxUs > c[163]) c[163] = (int)maxUs;
            string line = "inventory pass: " + all.Count + " inventories, " + ticks + " ticks, longest " + (maxUs / 1000.0).ToString("0.0")
                + " ms, most items in a tick " + mostItems + "; stacks " + n[0] + ", no object " + n[1] + ", no item component " + n[2] + ", destroyed " + n[3]
                + ", reparented " + n[4] + ", jam jars " + n[5] + ", unreadable inventories " + n[6];
            if (verify)
            {
                int after = OwPending();
                c[164] = after;
                line += "; verify: pending before " + before + ", after " + after;
            }
            OwLog(line);
        }

        // Overwatch's own alarm path, counted; with overwatchspreadverify the inventory repairs left before and after it.
        static void OwTheirs()
        {
            PatchCounters()[166]++;
            bool verify = sOwSpreadReady && ShortcutOn("overwatchspreadverify");
            int before = verify ? OwPending() : 0;
            OverwatchSpreadHook.TimerOrig();
            if (!verify) return;
            int after = OwPending();
            PatchCounters()[164] = after;
            OwLog("Overwatch's own pass: verify: pending before " + before + ", after " + after);
        }

        // Agent trigger (repros.json overwatch-spread-*): "all" runs the nightly alarm now through whichever path the
        // switches pick (gapMinutes apart when ours); a type name (e.g. "CleanupInventory") runs only that type now, ours
        // with overwatchspread on, Overwatch's own with it off. Returns what it started.
        public static string OverwatchSpreadNow(string type, int gapMinutes)
        {
            if (sOwType == null) return "Overwatch not loaded";
            if (!sOwSpreadReady && !sOwUpstream) return "hook not in place (see applied log)";
            OverwatchSpreadCore.Mode mode = OwMode();
            if (type == "all")
            {
                if (mode == OverwatchSpreadCore.Mode.Ours)
                {
                    Simulator.AddObject(new LoopTask(delegate { OwSpread(gapMinutes > 0 ? gapMinutes : OverwatchSpreadCore.kGapMinutes); }));
                    return "ours, " + gapMinutes + " sim-minutes apart";
                }
                // with a spreading Overwatch build nothing is patched: its own OnTimer is the alarm
                Simulator.AddObject(new LoopTask(delegate { if (sOwSpreadReady) OverwatchSpreadHook.TimerHook(); else sOwTimer.Invoke(null, null); }));
                return mode.ToString();
            }
            if (sOwFind == null) return "option list not found";
            foreach (object option in OwOptions())
                if (OwName(option) == type) { OwStartTask(option, mode == OverwatchSpreadCore.Mode.Ours || mode == OverwatchSpreadCore.Mode.Chain); return mode + " " + type; }
            return "no Overwatch option " + type;
        }

        public static class OverwatchSpreadHook
        {
            public static void TimerHook()
            {
                OverwatchSpreadCore.Mode mode = OwMode();
                if (mode == OverwatchSpreadCore.Mode.Ours)
                {
                    try { OwSpread(OverwatchSpreadCore.kGapMinutes); return; }
                    catch (Exception e) { OwLog("spread failed, Overwatch's own path runs: " + e.Message); }
                }
                else if (mode == OverwatchSpreadCore.Mode.Chain)
                {
                    Simulator.AddObject(new LoopTask(OwChainTheirs)); // own task: a yieldable root, and its name marks the chain's hitch lines
                    return;
                }
                else if (mode == OverwatchSpreadCore.Mode.Upstream)
                {
                    PatchCounters()[165] = -1;
                    OwLog("upstream spreading found (faked), ours off");
                }
                OwTheirs();
            }
            public static void TimerOrig() { } // receives Overwatch.OnTimer's original header
        }
    }
}
