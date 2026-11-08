using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects.Decorations;

namespace Ts3Fixes
{
    // dancefloorlistdiag (docs/253 §7, diagnostics only, default off): DanceFloor.FindAndRouteOntoADanceFloorWithFollower threw
    // "Collection was modified" on a list that only its own frame can reach (docs/253 §2). The hook replaces the method:
    //   switch off: EA's original body runs (counters only: calls, returns, that throw);
    //   switch on:  the same steps as EA's IL (follower.RoomId read, GetDanceFloorsNearObject, a foreach over List<DanceFloor> with
    //               the same struct enumerator, so MoveNext throws exactly where and how EA's would, rethrown unchanged), plus
    //               reads around each RouteOntoADanceFloorWithFollower: the enumerator's own l / ver words (boxed copy), the
    //               list's _version (through a second enumerator: the same field read MoveNext makes), Count, a WeakReference taken
    //               before the loop (cleared = the GC freed the list while our frame held it), and, only while that reference is
    //               alive, the items and identity hash. No read dereferences the list when it may be freed beyond what EA's next
    //               MoveNext reads anyway. GC count (GC.CollectionCount(0)) and wall ms around each step; async route STARTs
    //               (counter [56]) during the step tell the route mode.
    // The list object address is not readable from managed code without pinning (which would change the GC case); the identity
    // hash stands in for it. Applies to every Sim and changes no result (no cap, skip or fake): household rule not touched.
    // Counters [892] calls, [893] returns, [894] "Collection was modified" throws out of the method (all three also with the
    // switch off), [895] route steps checked, [896] steps with a mismatch, [897] of those with the list freed.
    // Lines (ts3native.log): "dancefloor list: call #n ..." per call (first 40), "dancefloor list: MISMATCH ..." / "... THROW ..."
    // (first 40 together).
    public static partial class Fixes
    {
        public static class DanceFloorDiagHook
        {
            public static DanceFloor FindHook(Sim actor, Sim follower, IDanceable danceObject)
            {
                int[] c = PatchCounters();
                c[892]++;
                if (!ShortcutOn("dancefloorlistdiag") || !DfReady())
                {
                    DanceFloor r0;
                    try { r0 = FindOrig(actor, follower, danceObject); }
                    catch (InvalidOperationException e) { if (e.Message != null && e.Message.StartsWith("Collection was modified")) c[894]++; throw; }
                    c[893]++;
                    return r0;
                }
                DanceFloor r = DfFind(actor, follower, danceObject, c);
                c[893]++;
                return r;
            }
            public static DanceFloor FindOrig(Sim actor, Sim follower, IDanceable danceObject) { return null; } // receives EA's original body
        }

        delegate List<DanceFloor> DfNearFn(IDanceable danceObject);
        delegate bool DfRouteFn(Sim actor, Sim follower);
        static MethodInfo sDfNear, sDfRoute;
        static FieldInfo sDfEnumList, sDfEnumVer;
        static int sDfReady, sDfCalls, sDfCallLines, sDfBadLines;

        // EA's private members, looked up once; any miss = the switch acts as off (EA's body runs)
        static bool DfReady()
        {
            if (sDfReady == 0)
            {
                sDfReady = -1;
                try
                {
                    const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                    sDfNear = typeof(DanceFloor).GetMethod("GetDanceFloorsNearObject", all, null, new Type[] { typeof(IDanceable) }, null);
                    sDfRoute = typeof(DanceFloor).GetMethod("RouteOntoADanceFloorWithFollower", all, null, new Type[] { typeof(Sim), typeof(Sim) }, null);
                    sDfEnumList = typeof(List<DanceFloor>.Enumerator).GetField("l", all);
                    sDfEnumVer = typeof(List<DanceFloor>.Enumerator).GetField("ver", all);
                    if (sDfNear != null && sDfRoute != null && sDfEnumList != null && sDfEnumVer != null) sDfReady = 1;
                    else DiagLine("dancefloor list: diag off, EA member missing (near " + (sDfNear != null) + ", route " + (sDfRoute != null) + ", enumerator fields " + (sDfEnumList != null && sDfEnumVer != null) + ")");
                }
                catch (Exception e) { DiagLine("dancefloor list: diag off, lookup failed: " + e.Message); }
            }
            return sDfReady == 1;
        }

        static DanceFloor DfFind(Sim actor, Sim follower, IDanceable danceObject, int[] c)
        {
            int call = ++sDfCalls;
            int unused = follower.RoomId;  // EA reads it first (IL_0001): a null follower throws here, as in EA's body
            List<DanceFloor> list = ((DfNearFn)Delegate.CreateDelegate(typeof(DfNearFn), sDfNear))(danceObject);
            WeakReference weak = new WeakReference(list);
            int ver0 = DfVersion(list), cnt0 = list.Count, hash0 = RuntimeHelpers.GetHashCode(list), gc0 = GC.CollectionCount(0);
            DanceFloor[] items0 = list.ToArray();
            string who = DfWho(actor, follower, danceObject);
            int steps = 0, t0 = Environment.TickCount;
            string end = "exception";  // a throw out of a route step (e.g. a reset) leaves it so
            List<DanceFloor>.Enumerator e = list.GetEnumerator();
            try
            {
                while (true)
                {
                    bool more;
                    try { more = e.MoveNext(); }
                    catch (InvalidOperationException ex)
                    {
                        c[894]++;
                        end = "throw";
                        DfBad("THROW call #" + call + " step " + steps + " " + ex.GetType().Name + " '" + ex.Message + "' " + DfState(e, list, weak, ver0, cnt0, hash0, items0) + " gc " + gc0 + "->" + GC.CollectionCount(0) + " " + who);
                        throw;
                    }
                    if (!more) { end = "null"; break; }
                    DanceFloor item = e.Current;
                    int g0 = GC.CollectionCount(0), a0 = c[56], ms0 = Environment.TickCount;
                    bool routed = ((DfRouteFn)Delegate.CreateDelegate(typeof(DfRouteFn), item, sDfRoute))(actor, follower);
                    int ms = Environment.TickCount - ms0, g1 = GC.CollectionCount(0), async = c[56] - a0;
                    steps++;
                    c[895]++;
                    int k = DfClass(e, list, weak, ver0, cnt0, items0);
                    if (k != DanceFloorDiagCore.kSame)
                    {
                        c[896]++;
                        if (k == DanceFloorDiagCore.kFreed) c[897]++;
                        DfBad("MISMATCH call #" + call + " step " + steps + " " + DanceFloorDiagCore.Name(k) + " " + DfState(e, list, weak, ver0, cnt0, hash0, items0)
                            + " gc " + g0 + "->" + g1 + " slept " + ms + " ms async starts " + async + " " + who);
                    }
                    if (routed) { end = "floor " + DfItem(item); return item; }
                }
            }
            finally
            {
                e.Dispose();
                if (sDfCallLines < 40)
                {
                    sDfCallLines++;
                    DiagLine("dancefloor list: call #" + call + " floors " + cnt0 + " ver " + ver0 + " steps " + steps + " result " + end + " gc " + gc0 + "->" + GC.CollectionCount(0)
                        + " " + (Environment.TickCount - t0) + " ms " + who);
                }
            }
            return null;
        }

        // the list's _version as List<T>.Enumerator's constructor reads it (a direct field read, the one MoveNext compares)
        static int DfVersion(List<DanceFloor> list)
        {
            object probe = list.GetEnumerator();
            return (int)sDfEnumVer.GetValue(probe);
        }

        static int DfClass(List<DanceFloor>.Enumerator e, List<DanceFloor> list, WeakReference weak, int ver0, int cnt0, DanceFloor[] items0)
        {
            object eb = e;
            bool enumOk = ReferenceEquals(sDfEnumList.GetValue(eb), list) && (int)sDfEnumVer.GetValue(eb) == ver0;
            bool alive = weak.IsAlive;
            int ver1 = DfVersion(list), cnt1 = list.Count;
            bool same = true;
            if (alive && cnt1 == cnt0) { for (int i = 0; i < cnt1; i++) if (!ReferenceEquals(list[i], items0[i])) { same = false; break; } }
            else same = alive ? false : true;  // freed: items not read (classified kFreed before items count)
            return DanceFloorDiagCore.Classify(enumOk, alive, ver0, ver1, cnt0, cnt1, same);
        }

        static string DfState(List<DanceFloor>.Enumerator e, List<DanceFloor> list, WeakReference weak, int ver0, int cnt0, int hash0, DanceFloor[] items0)
        {
            object eb = e;
            bool alive = weak.IsAlive;
            StringBuilder b = new StringBuilder();
            b.Append("ver ").Append(ver0).Append("->").Append(DfVersion(list)).Append(" count ").Append(cnt0).Append("->").Append(list.Count);
            b.Append(" enum list ").Append(ReferenceEquals(sDfEnumList.GetValue(eb), list) ? "same" : "other").Append(" ver ").Append(sDfEnumVer.GetValue(eb));
            b.Append(" alive ").Append(alive ? "yes" : "no").Append(" hash ").Append(hash0.ToString("X8"));
            if (alive) b.Append("->").Append(RuntimeHelpers.GetHashCode(list).ToString("X8")).Append(" type ").Append(list.GetType().Name);
            b.Append(" items [");
            for (int i = 0; i < items0.Length; i++) b.Append(i > 0 ? " " : "").Append(DfItem(items0[i]));
            b.Append("]");
            if (alive)
            {
                b.Append(" now [");
                for (int i = 0; i < list.Count; i++) b.Append(i > 0 ? " " : "").Append(DfItem(list[i]));
                b.Append("]");
            }
            return b.ToString();
        }

        static string DfItem(DanceFloor f)
        {
            return f == null ? "null" : f.GetType().Name + "#" + (f.ObjectId.Value & 0xFFFFFF).ToString("X");
        }

        static string DfWho(Sim actor, Sim follower, IDanceable danceObject)
        {
            return "actor " + DfSim(actor) + " follower " + DfSim(follower) + " lot " + (actor.LotCurrent != null ? actor.LotCurrent.Name : "none")
                + " object " + (danceObject != null ? danceObject.GetType().Name : "none");
        }

        static string DfSim(Sim s)
        {
            if (s == null) return "none";
            bool hh = s.Household != null && s.Household == Household.ActiveHousehold;
            return s.FullName + " (household " + (hh ? "yes" : "no") + (s.IsSelectable ? ", selectable" : "") + ")";
        }

        static void DfBad(string s)
        {
            if (sDfBadLines >= 40) return;
            sDfBadLines++;
            DiagLine("dancefloor list: " + s);
        }
    }
}
