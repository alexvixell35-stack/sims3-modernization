using System;
using System.Collections;
using System.Collections.Generic;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Utilities;

namespace Ts3Agent
{
    // alarm_audit (read only; docs/546 row 5): alarms that cannot fire or were started badly, per AlarmManager (global +
    // every lot with its own). AlarmManager keeps each Timer twice [verified, decompiled]: mTimers (handle -> timers, what
    // RemoveAlarm and the owner index use) and mTimerQueue (the priority queue Simulate pops).
    //   not_queued:       in mTimers, not in the queue -> never fires, never cleaned up by firing
    //   unregistered:     in the queue, not in mTimers -> fires, cannot be removed by its handle
    //   overdue:          due more than 1 sim hour ago and still queued (Simulate should have popped it)
    //   duplicate:        same callback method + same target object + same owner under more than one handle (re-added
    //                     without removing the old one: the callback runs once per copy)
    //   bad_repeat:       repeating with RepeatLength <= 0
    //   far_future:       due more than 1 sim year ahead
    // Each class: count and top callbacks ("Type.Method"), with owner types.
    //   alarm_audit {"top":15}
    public static partial class Handlers
    {
        static void RegisterAlarmAudit() { Dispatcher.Register("alarm_audit", AlarmAudit); }

        static object AlarmAudit(Args a)
        {
            int top = (int)a.Long("top", 15);
            List<AlarmManager> mgrs = new List<AlarmManager>();
            if (AlarmManager.Global != null) mgrs.Add(AlarmManager.Global);
            foreach (object o in LotManager.AllLots)
            {
                Lot l = o as Lot; if (l == null) continue;
                AlarmManager m = null; try { m = l.AlarmManager; } catch (Exception) { }
                if (m != null && !mgrs.Contains(m)) mgrs.Add(m);
            }
            string[] kinds = { "not_queued", "unregistered", "overdue", "duplicate", "bad_repeat", "far_future" };
            Dictionary<string, Dictionary<string, int>> by = new Dictionary<string, Dictionary<string, int>>();
            Dictionary<string, int> counts = new Dictionary<string, int>();
            foreach (string k in kinds) { by[k] = new Dictionary<string, int>(); counts[k] = 0; }
            long now = SimClock.CurrentTime().Ticks;
            long hour = SimClock.ConvertToTicks(1f, TimeUnit.Hours), year = SimClock.ConvertToTicks(1f, TimeUnit.Days) * 28 * 4;
            int total = 0, queuedTotal = 0, cleared = 0;
            foreach (AlarmManager m in mgrs)
            {
                IDictionary timers = LcGet(m, "mTimers") as IDictionary;
                IEnumerable queue = LcGet(m, "mTimerQueue") as IEnumerable;
                Dictionary<object, bool> inQueue = new Dictionary<object, bool>(), inTable = new Dictionary<object, bool>();
                if (queue != null) foreach (object t in queue) if (t != null) { inQueue[t] = true; queuedTotal++; }
                Dictionary<string, int> sig = new Dictionary<string, int>();
                if (timers != null) foreach (object list in timers.Values)
                {
                    IEnumerable l = list as IEnumerable; if (l == null) continue;
                    Dictionary<string, bool> inHandle = new Dictionary<string, bool>();  // a day alarm keeps one timer per weekday under one handle
                    foreach (object t in l)
                    {
                        if (t == null) continue;
                        total++; inTable[t] = true;
                        Delegate cb = LcGet(t, "CallBack") as Delegate;
                        object owner = LcGet(t, "ObjectRef");
                        string name = LcMethod(cb) + " (" + LcTypeName(owner) + ")";
                        if (!inQueue.ContainsKey(t)) { counts["not_queued"]++; LcBump(by["not_queued"], name); }
                        object when = LcGet(t, "AlarmDateAndTime");
                        if (when is DateAndTime)
                        {
                            long due = ((DateAndTime)when).Ticks;
                            if (inQueue.ContainsKey(t) && due < now - hour) { counts["overdue"]++; LcBump(by["overdue"], name); }
                            if (due > now + year) { counts["far_future"]++; LcBump(by["far_future"], name); }
                        }
                        object data = LcGet(t, "mData"), len = LcGet(t, "RepeatLength");  // Timer.Repeating = mData & 0x80
                        if (data is byte && ((byte)data & 0x80) != 0 && len is float && (float)len <= 0f) { counts["bad_repeat"]++; LcBump(by["bad_repeat"], name); }
                        string key = LcMethod(cb) + "|" + (cb != null && cb.Target != null ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(cb.Target).ToString() : "static") + "|" + (owner != null ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(owner).ToString() : "null");
                        if (inHandle.ContainsKey(key)) continue;
                        inHandle[key] = true;
                        int n; sig.TryGetValue(key, out n); sig[key] = n + 1;
                        if (n >= 1) { counts["duplicate"]++; LcBump(by["duplicate"], name); }
                    }
                }
                if (queue != null) foreach (object t in queue)
                {
                    if (t == null || inTable.ContainsKey(t)) continue;
                    object md = LcGet(t, "mData");
                    if (md is byte && ((byte)md & 0x20) != 0) { cleared++; continue; }  // Timer.Cleared: RemoveAlarm leaves it queued, Simulate drops it
                    counts["unregistered"]++;
                    LcBump(by["unregistered"], LcMethod(LcGet(t, "CallBack") as Delegate) + " (" + LcTypeName(LcGet(t, "ObjectRef")) + ")");
                }
            }
            Dictionary<string, object> r = Util.Obj();
            r["managers"] = mgrs.Count; r["timers"] = total; r["queued"] = queuedTotal; r["cleared_in_queue"] = cleared;
            foreach (string k in kinds) { Dictionary<string, object> d = Util.Obj(); d["count"] = counts[k]; d["top"] = LcTop(by[k], top); r[k] = d; }
            return r;
        }
    }
}
