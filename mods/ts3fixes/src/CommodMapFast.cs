using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // commodmapfast / commodmapshadow (docs/313), both default OFF. Exact speedup of CommodityInteractionMap.RemoveObject, the
    // removal half of every object's room or lot change (GameObject.ChangeCommodityInteractionMap). EA removes each pair of the
    // object from each of its commodity lists with List.Remove, one pair.Equals per element from the list start [verified,
    // decompiled CommodityInteractionMap.cs; simler90 242 core mod same body]: k pairs on a list of n cost about k * n Equals
    // chains, and with many mods adding interactions a mod moving many objects took 57 s (docs/856 §4, I-566-1).
    // Here: one pass per commodity list with reference compares (CommodMapFastCore.cs, proof there), only when every pair's
    // Equals is InteractionObjectPair's own and its definition keeps object.Equals; any other pair, a null definition or an
    // exception while planning runs EA's body unchanged (planning changes nothing, so EA then sees the map as it was).
    // Proven exact (same keys, list objects, entries and order; no Equals calls a mod could watch), so it applies to every
    // map, household Sims included (CLAUDE.md: proven-exact speedups stay).
    // commodmapshadow: plan, run EA's body (its result is kept), compare; the first mismatch turns both switches off for the
    // session and is logged. Counters 1500 calls (a switch on), 1501 batch applied, 1502 EA ran: pair or definition with its
    // own Equals / null definition, 1503 EA ran: exception while planning, 1504 shadow compares, 1505 shadow mismatches,
    // 1506 EA body us (shadow, fallbacks), 1507 batch us (plan + apply; shadow: plan only).
    public static partial class Fixes
    {
        static bool sCmfOff;  // a shadow mismatch: off for the session
        static int sCmfSlowLines;
        static FieldInfo sCmfTable;
        static readonly Dictionary<Type, bool> sCmfStdDef = new Dictionary<Type, bool>(), sCmfStdPair = new Dictionary<Type, bool>();
        static StopWatch sCmfWatch;
        static readonly CmfKeyFn<InteractionObjectPair> sCmfDef = delegate(InteractionObjectPair p) { return p.InteractionDefinition; };
        static readonly CmfKeyFn<InteractionObjectPair> sCmfTarget = delegate(InteractionObjectPair p) { return p.Target; };

        static uint CmfNowUs()
        {
            if (sCmfWatch == null) { sCmfWatch = StopWatch.Create(StopWatch.TickStyles.Microseconds); sCmfWatch.Start(); }
            return sCmfWatch.GetElapsedTime();  // free-running; unchecked uint differences
        }

        // does t's Equals(object) come from root (InteractionObjectPair for pairs, object for definitions)? cached per type
        static bool CmfStd(Dictionary<Type, bool> cache, Type t, Type root)
        {
            bool ok;
            if (cache.TryGetValue(t, out ok)) return ok;
            MethodInfo m = t.GetMethod("Equals", BindingFlags.Instance | BindingFlags.Public, null, new Type[] { typeof(object) }, null);
            ok = m != null && m.DeclaringType == root;
            cache[t] = ok;
            return ok;
        }

        // EA's CommodityChange.ValidForCommodityInteractionMap (internal) from its public parts
        static bool CmfValid(CommodityChange cc)
        {
            float v = cc.ConstantChange;
            if (v == 0f) return false;
            CommodityKind k = cc.Commodity;
            if (k == CommodityKind.Temperature) return true;
            return Commodities.IsCommodityGenerallyPositive(k) ? v > 0f : v < 0f;
        }

        // The plan for removing pairs from map as EA's RemoveObject would, or null when EA's body must run (why set).
        internal static CmfPlan<InteractionObjectPair> CmfMakePlan(CommodityInteractionMap map, List<InteractionObjectPair> pairs, out int why)
        {
            why = 0;
            List<ulong> cs = new List<ulong>();
            List<InteractionObjectPair> ps = new List<InteractionObjectPair>();
            try
            {
                foreach (InteractionObjectPair p in pairs)  // EA's loop, same getters, nothing changed
                {
                    Tradeoff t = p.Tradeoff;
                    if (t == null) continue;
                    bool std = false;
                    foreach (CommodityChange cc in t.mOutputs)
                    {
                        if (!CmfValid(cc)) continue;
                        if (!std)
                        {
                            InteractionDefinition d = p.InteractionDefinition;
                            if (d == null || !CmfStd(sCmfStdPair, p.GetType(), typeof(InteractionObjectPair)) || !CmfStd(sCmfStdDef, d.GetType(), typeof(object))) { why = 1502; return null; }
                            std = true;
                        }
                        cs.Add((ulong)cc.Commodity);
                        ps.Add(p);
                    }
                }
                return CommodMapFastCore.Plan(cs, ps, delegate(ulong c) { return map.InteractionsForCommodity((CommodityKind)c); }, sCmfDef, sCmfTarget);
            }
            catch (ResetException) { throw; }
            catch (Exception) { why = 1503; return null; }
        }

        internal static void CmfApply(CommodityInteractionMap map, CmfPlan<InteractionObjectPair> plan)
        {
            CommodMapFastCore.Apply(plan, delegate(ulong c) { ((Dictionary<ulong, List<InteractionObjectPair>>)sCmfTable.GetValue(map)).Remove(c); });
        }

        public partial class CmfHook : CommodityInteractionMap
        {
            CmfHook() : base(null, null) { }  // never constructed: the class only lends its bodies

            public void RemoveHook(IGameObject x)
            {
                if (sCmfOff) { RemoveOrig(x); return; }
                bool shadow = ShortcutOn("commodmapshadow");
                if (!shadow && !ShortcutOn("commodmapfast")) { RemoveOrig(x); return; }
                int[] c = PatchCounters();
                c[1500]++;
                if (sCmfTable == null)
                {
                    sCmfTable = typeof(CommodityInteractionMap).GetField("mTable", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (sCmfTable == null) { sCmfOff = true; ShortcutLog("commodmapfast: CommodityInteractionMap.mTable not found, off"); RemoveOrig(x); return; }
                }
                uint t0 = CmfNowUs();
                int why;
                CmfPlan<InteractionObjectPair> plan = CmfMakePlan(this, x.Interactions, out why);
                uint t1 = CmfNowUs();
                if (plan == null) { c[why]++; RemoveOrig(x); c[1506] += (int)(CmfNowUs() - t1); return; }
                if (!shadow)
                {
                    CmfApply(this, plan);
                    c[1501]++;
                    c[1507] += (int)(CmfNowUs() - t0);
                    return;
                }
                c[1507] += (int)(t1 - t0);
                List<InteractionObjectPair[]> exp = CommodMapFastCore.Expected(plan);
                uint t2 = CmfNowUs();
                RemoveOrig(x);
                uint ea = CmfNowUs() - t2;
                c[1506] += (int)ea;
                c[1504]++;
                if (ea >= 50000 && sCmfSlowLines < 20)  // the hitch case: EA's time next to the plan's, per call
                {
                    sCmfSlowLines++;
                    DiagLine("commodmapfast: shadow RemoveObject " + x.GetType().Name + ": EA " + ea / 1000 + " ms, plan " + (t1 - t0) / 1000 + " ms, "
                        + plan.Keys.Count + " lists, " + plan.Compared + " entries looked at; totals EA " + c[1506] / 1000 + " ms, plan " + c[1507] / 1000 + " ms in " + c[1504] + " calls");
                }
                string m = CommodMapFastCore.Compare(plan, exp, delegate(ulong k) { return InteractionsForCommodity((CommodityKind)k); });
                if (m == null) return;
                c[1505]++;
                sCmfOff = true;
                string line = "commodmapfast: shadow mismatch on " + x.GetType().Name + " (" + m + "), commodmapfast/commodmapshadow off for the session";
                ShortcutLog(line);
                DiagLine(line);
            }
            public void RemoveOrig(IGameObject x) { }  // receives CommodityInteractionMap.RemoveObject's original body
        }
    }
}
