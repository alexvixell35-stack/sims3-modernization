using System;
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
    // NRaas ErrorTrap runs its load-up checks one sim-minute after every world load in one FunctionTask tick up to the
    // first sleep of its dereference scan: a 0.5-0.9 s frame per load (docs/146 §2, 7 of 7 /taskstall loads), about three
    // quarters in its inventory check, which looks up the localized name of every world object for its log text before
    // finding that most objects have no inventory.
    // Switch "errortrapspread" (tuning kErrorTrapSpread, default off) takes over that inventory check (ErrorTrap's own copy
    // of the NRaas helper Inventories.CheckInventories): it runs in a task of its own, one tick break before it (the
    // role/recovery/outfit/lot checks stay in their tick), then ErrorTrap's own per-inventory check for every object that
    // has an inventory and for each household's family and fridge inventory, in ErrorTrap's order, with the same names in
    // its log; objects without an inventory are skipped without the name lookup (their check returns at once for them); at
    // most kSliceUs of work per tick. ErrorTrap's task waits one tick only, so the dereference controllers start in a fresh
    // tick at their usual moment: run inline, the spread held their PreProcess back ~15 sim-minutes, and a Sim whose saved
    // route had ended by then was queued for ErrorTrap's reset (docs/242). Nothing else of ErrorTrap changes and its settings
    // are never written.
    // ErrorTrap builds that spread the scan themselves carry ErrorTrapSpreadCore.MarkerField: then we patch nothing and
    // log "upstream spreading found". No ErrorTrap, or a check we do not recognise: logged, nothing patched.
    // Test switches: errortrapspreadverify (count the corrections either path logs), errortrapspreadfakeupstream (behave
    // as if the marker were present). Trigger: ErrorTrapSpreadNow (repros.json errortrap-spread-*).
    // Counters: [167] our passes, [168] inventories checked, [169] ticks used, [170] longest tick of a pass in us,
    // [171] name lookups skipped (no inventory), [172] objects destroyed while we slept (skipped), [173] upstream marker
    // version (-1 = faked), [174] checks left to ErrorTrap's own path, [175] corrections logged by the last verified
    // pass, [176] our passes that failed and fell back to ErrorTrap's own check.
    public static partial class Fixes
    {
        static bool sEtTried, sEtReady, sEtUpstream;
        static Type sEtType, sEtLogger;     // NRaas.ErrorTrap, NRaas.CommonSpace.Helpers.Inventories+Logger
        static MethodInfo sEtCheck, sEtGetName, sEtCheckAll;  // CheckInventory(log, debugLog, name, inventory, checkDestroyed), GetName(obj), CheckInventories
        static StopWatch sEtWatch;
        static Delegate sEtRealLog, sEtRealDebugLog;  // verify: the delegates our counting ones forward to
        static int sEtCorrections;

        internal static void ApplyErrorTrapSpread(bool lastTry)
        {
            if (sEtTried) return;
            Assembly et = null;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                if (a.GetName().Name == "NRaasErrorTrap") { et = a; break; }
            if (et == null)
            {
                if (lastTry) { sEtTried = true; AppliedLog().Add("errortrapspread: ErrorTrap not loaded, off"); }
                return;
            }
            sEtTried = true;
            sEtType = et.GetType("NRaas.ErrorTrap");
            if (ErrorTrapSpreadCore.HasMarker(sEtType)) { sEtUpstream = true; return; } // logged with its version at world load
            Type inv = et.GetType("NRaas.CommonSpace.Helpers.Inventories");
            sEtLogger = inv == null ? null : inv.GetNestedType("Logger");
            const BindingFlags st = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            if (sEtLogger != null)
            {
                sEtCheckAll = inv.GetMethod("CheckInventories", st, null, new Type[] { sEtLogger, sEtLogger, typeof(bool) }, null);
                sEtCheck = inv.GetMethod("CheckInventory", st, null, new Type[] { sEtLogger, sEtLogger, typeof(string), typeof(Inventory), typeof(bool) }, null);
                sEtGetName = inv.GetMethod("GetName", st, null, new Type[] { typeof(IGameObject) }, null);
            }
            if (sEtType == null || sEtCheckAll == null || sEtCheckAll.ReturnType != typeof(void) || sEtCheck == null || sEtGetName == null || sEtGetName.ReturnType != typeof(string))
            {
                AppliedLog().Add("errortrapspread: ErrorTrap's CheckInventories differs from the known build, off");
                return;
            }
            sEtReady = Patch(inv, "CheckInventories", new Type[] { sEtLogger, sEtLogger, typeof(bool) }, typeof(ErrorTrapSpreadHook), "CheckHook", "CheckOrig");
        }

        // World load: the marker's value is safe to read now (ErrorTrap's static constructor has run).
        internal static void ErrorTrapSpreadWorldLoad()
        {
            if (!sEtUpstream || sEtType == null) return;
            int v = 0;
            try { v = (int)sEtType.GetField(ErrorTrapSpreadCore.MarkerField).GetValue(null); } catch (Exception) { }
            PatchCounters()[173] = v;
            EtLog("upstream spreading found (v" + v + "), ours off");
        }

        static void EtLog(string line)
        {
            AppliedLog().Add("errortrapspread: " + line);
            List<object> log = AppDomain.CurrentDomain.GetData("ts3fixes.errortrapspread") as List<object>;
            if (log == null) { log = new List<object>(); AppDomain.CurrentDomain.SetData("ts3fixes.errortrapspread", log); }
            if (log.Count >= 200) log.RemoveAt(0);
            log.Add("simmin " + SimClock.ElapsedTime(TimeUnit.Minutes).ToString("0") + " " + line);
        }

        static OverwatchSpreadCore.Mode EtMode()
        {
            return OverwatchSpreadCore.Decide(ShortcutOn("errortrapspread"), sEtReady, sEtUpstream, ShortcutOn("errortrapspreadfakeupstream"));
        }

        // Verify: delegates of ErrorTrap's Logger type that count each line and pass it on to ErrorTrap's own.
        public static void EtCountLog(string text) { sEtCorrections++; if (sEtRealLog != null) sEtRealLog.DynamicInvoke(new object[] { text }); }
        public static void EtCountDebugLog(string text) { sEtCorrections++; if (sEtRealDebugLog != null) sEtRealDebugLog.DynamicInvoke(new object[] { text }); }

        static object[] EtCounting(object log, object debugLog)
        {
            sEtRealLog = log as Delegate;
            sEtRealDebugLog = debugLog as Delegate;
            sEtCorrections = 0;
            return new object[] {
                Delegate.CreateDelegate(sEtLogger, typeof(Fixes).GetMethod("EtCountLog")),
                Delegate.CreateDelegate(sEtLogger, typeof(Fixes).GetMethod("EtCountDebugLog")) };
        }

        static void EtSleep()
        {
            if (MayYield()) Simulator.Sleep(0);
        }

        static void EtCheckOne(object log, object debugLog, string name, Inventory inv, bool checkDestroyed)
        {
            sEtCheck.Invoke(null, new object[] { log, debugLog, name, inv, checkDestroyed });
            PatchCounters()[168]++;
        }

        // Items in an inventory (all stacks); the slice budget's unit. The largest one checked is counted: ErrorTrap's own
        // check runs an inventory whole, so a giant one is still one tick.
        static int EtItems(Inventory inv)
        {
            int n = 0;
            if (inv == null) return 0;
            foreach (InventoryStack s in inv.InventoryItems.Values) if (s != null && s.List != null) n += s.List.Count;
            if (n > PatchCounters()[211]) PatchCounters()[211] = n;
            return Math.Max(1, n);
        }

        // Our inventory check, sliced over sim ticks; runs in its own task.
        static void EtPass(object log, object debugLog, bool checkDestroyed)
        {
            int[] c = PatchCounters();
            EtSleep();
            if (sEtWatch == null) sEtWatch = StopWatch.Create(StopWatch.TickStyles.Microseconds);
            GameObject[] objects = Sims3.Gameplay.Queries.GetObjects<GameObject>();
            List<Household> houses = new List<Household>(Household.sHouseholdList);
            int ticks = 1, checkedBefore = c[168], noInv = 0, gone = 0, sliceItems = 0, mostItems = 0;
            long maxUs = 0;
            sEtWatch.Restart();
            for (int i = 0; i < objects.Length + houses.Count; i++)
            {
                long us = sEtWatch.GetElapsedTime();
                if (ErrorTrapSpreadCore.SliceFull(sliceItems, us))
                {
                    if (us > maxUs) maxUs = us;
                    if (sliceItems > mostItems) mostItems = sliceItems;
                    sliceItems = 0;
                    EtSleep();
                    if (GameStates.IsWorldShuttingDown || !GameStates.IsInWorld()) { EtLog("inventory pass stopped: world gone"); return; }
                    ticks++;
                    sEtWatch.Restart();
                }
                if (i < objects.Length)
                {
                    GameObject o = objects[i];
                    if (o.HasBeenDestroyed) { gone++; continue; } // gone while we slept
                    Inventory inv = o.Inventory;
                    if (inv == null) { noInv++; continue; }       // their check returns at once; the name only fed its log
                    sliceItems += EtItems(inv);
                    EtCheckOne(log, debugLog, (string)sEtGetName.Invoke(null, new object[] { o }), inv, checkDestroyed);
                    continue;
                }
                Household h = houses[i - objects.Length];
                if (h.SharedFamilyInventory != null) sliceItems += EtItems(h.SharedFamilyInventory.Inventory);
                if (h.SharedFridgeInventory != null) sliceItems += EtItems(h.SharedFridgeInventory.Inventory);
                if (h.SharedFamilyInventory != null) EtCheckOne(log, debugLog, h.Name + " Family", h.SharedFamilyInventory.Inventory, checkDestroyed);
                if (h.SharedFridgeInventory != null) EtCheckOne(log, debugLog, h.Name + " Fridge", h.SharedFridgeInventory.Inventory, checkDestroyed);
            }
            long last = sEtWatch.GetElapsedTime();
            if (last > maxUs) maxUs = last;
            if (sliceItems > mostItems) mostItems = sliceItems;
            if (mostItems > c[212]) c[212] = mostItems;
            c[167]++;
            c[169] += ticks;
            c[171] += noInv;
            c[172] += gone;
            if (maxUs > c[170]) c[170] = (int)maxUs;
            EtLog("inventory pass: " + objects.Length + " objects, " + (c[168] - checkedBefore) + " inventories checked, "
                + noInv + " name lookups skipped, " + gone + " destroyed meanwhile, " + ticks + " ticks, longest " + (maxUs / 1000.0).ToString("0.0") + " ms, most items in a tick " + mostItems + ", largest inventory " + PatchCounters()[211] + " items");
        }

        // One check now, through the switches (the load-up hook) or forced: "ours" or "theirs".
        static void EtRun(object log, object debugLog, bool checkDestroyed, OverwatchSpreadCore.Mode mode)
        {
            int[] c = PatchCounters();
            bool verify = ShortcutOn("errortrapspreadverify");
            object[] logs = verify ? EtCounting(log, debugLog) : new object[] { log, debugLog };
            if (mode == OverwatchSpreadCore.Mode.Ours)
            {
                try
                {
                    EtPass(logs[0], logs[1], checkDestroyed);
                    if (verify) { c[175] = sEtCorrections; EtLog("verify: our pass logged " + sEtCorrections + " corrections"); }
                    return;
                }
                catch (Exception e) { c[176]++; EtLog("pass failed, ErrorTrap's own check runs: " + (e.InnerException ?? e).Message); }
            }
            else if (mode == OverwatchSpreadCore.Mode.Upstream && !sEtUpstream)
            {
                c[173] = -1;
                EtLog("upstream spreading found (faked), ours off");
            }
            c[174]++;
            if (sEtReady) ErrorTrapSpreadHook.CheckOrig(logs[0], logs[1], checkDestroyed);
            else sEtCheckAll.Invoke(null, new object[] { logs[0], logs[1], checkDestroyed });
            if (verify) { c[175] = sEtCorrections; EtLog("verify: ErrorTrap's own check logged " + sEtCorrections + " corrections"); }
        }

        // Agent trigger (repros.json errortrap-spread-*): runs the inventory check now in its own task with ErrorTrap's own
        // log methods: "ours" (sliced), "theirs" (ErrorTrap's one-tick check) or "hook" (what the switches pick at load).
        // The rest of the load-up scan only runs at load.
        public static string ErrorTrapSpreadNow(string path)
        {
            if (sEtType == null) return "ErrorTrap not loaded";
            if (sEtCheckAll == null) return "check not found (see applied log)";
            MethodInfo logM = sEtType.GetMethod("LogCorrection", new Type[] { typeof(string) }), dbgM = sEtType.GetMethod("DebugLogCorrection", new Type[] { typeof(string) });
            if (logM == null || dbgM == null) return "ErrorTrap log methods not found";
            Delegate log = Delegate.CreateDelegate(sEtLogger, logM), dbg = Delegate.CreateDelegate(sEtLogger, dbgM);
            OverwatchSpreadCore.Mode mode = path == "ours" ? OverwatchSpreadCore.Mode.Ours : path == "theirs" ? OverwatchSpreadCore.Mode.Theirs : EtMode();
            Simulator.AddObject(new LoopTask(delegate { EtRun(log, dbg, true, mode); }));
            return mode.ToString();
        }

        public static class ErrorTrapSpreadHook
        {
            // Inventories.CheckInventories(Logger, Logger, bool) in ErrorTrap's assembly; its Logger delegates as object.
            public static void CheckHook(object log, object debugLog, bool checkDestroyed)
            {
                OverwatchSpreadCore.Mode mode = EtMode();
                if (mode != OverwatchSpreadCore.Mode.Ours) { EtRun(log, debugLog, checkDestroyed, mode); return; }
                Simulator.AddObject(new LoopTask(delegate { EtRun(log, debugLog, checkDestroyed, mode); }));
                EtSleep(); // the dereference controllers start in a fresh tick
            }
            public static void CheckOrig(object log, object debugLog, bool checkDestroyed) { } // receives CheckInventories' original header
        }
    }
}
