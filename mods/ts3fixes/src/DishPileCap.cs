using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects.Appliances;
using Sims3.Gameplay.Objects.CookingObjects;
using Sims3.Gameplay.Objects.Miscellaneous;
using Sims3.Gameplay.Objects.Plumbing;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // dishpilecap (tuning kDishPileCap, default off; kDishPileCapPerLot 10, kDishPileCapBudget 20; docs/652). A safety net,
    // not the fix of the Isla Paradiso pile: that pile is on the active household's own home (docs/652 §1-§3), which EA's
    // nightly cleanup skips by design and which this switch never touches either (household rule).
    // [verified, decompiled] EA removes dirty dishes itself once a day: LotManager.CleanUpLotsAndInventories (3 AM, "Global
    // venue cleanup alarm"; easpreadlots runs the same steps spread over ticks, EaSpread.cs) calls Lot.MagicallyCleanUp on
    // every lot the active Sim cannot treat as home and that is not a controllable venue; Lot.DoesObjectNeedCleaning picks
    // every IThrowAwayable whose ShouldBeThrownAway holds (PreparedFood: empty or spoiled, or any food on a lot without a
    // household) unless it is InUse, held by a Sim or IsBlocked, and ThrowAwayImmediately destroys it. A lot with an active
    // job of the active household (Occupation.DoesLotHaveAnyActiveJobs) is skipped whole. Between two nights, and on those
    // skipped lots, only a Sim's ServingContainer_CleanUp (needs a sink, dishwasher or synthesizer on the lot) or an
    // eat-away destroy step (FinishEatingOutside, ShouldDestroyContainer, the 45 sim-min auto-destroy alarm) removes one.
    // This switch: once a sim-hour, dirty servings (ServingContainer: !HasFood || IsSpoiled) are counted per lot. On a lot
    // that is not the active household's home or venue, has no household / selected / unknown-scope Sim on it and is not
    // in view (UfLotSeen), the dirty dishes above kDishPileCapPerLot that nobody uses, routes to or holds, that are not on a
    // buffet, picnic blanket or bake sale table, have no EA auto-destroy alarm yet and lie outside the camera's reach
    // (FarFromViewers) get EA's nightly step early: Lot.DoesObjectNeedCleaning, then ThrowAwayImmediately, at most
    // kDishPileCapBudget per pass. Fresh servings with food left are never touched.
    // With the switch off the pass only counts (shadow) and logs the biggest pile once per sim-day (also the active home's,
    // named as such), so a normal run names the lot. Counters [4130] dishes removed, [4131] lots capped, [4132] over-cap lots
    // left alone (household home or venue, household Sim there, or in view), [4133] dishes skipped as used, held or not
    // needing cleaning, [4134] dishes skipped as near a viewer, [4135] errors, [4136] passes, [4137] dirty dishes on the
    // biggest lot (last pass), [4138] dirty dishes in the world (last pass), [4139] lots over the cap (last pass, any switch).
    // Log "dishpilecap: <lot> (<kind>, sinks N) D dirty (Plate p, Bowl b, ...), removed R", capped.
    public static partial class Fixes
    {
        const int kDpc = 4130;
        static AlarmHandle sDpcAlarm = AlarmHandle.kInvalidHandle;
        static readonly FieldInfo sDpcAutoDestroy = typeof(ServingContainer).GetField("mAutoDestroyAlarm", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        static int sDpcMinute = -1, sDpcLogged, sDpcLines, sDpcShadowDay = -1;

        static void DpcLog(string s)
        {
            int minute = System.Environment.TickCount / 60000;
            if (minute != sDpcMinute) { sDpcMinute = minute; sDpcLogged = 0; }
            if (sDpcLogged++ < 10 && sDpcLines++ < 300) DiagLine("dishpilecap: " + s);
        }

        // every world load (from UfTpCleanupWorldLoad, sim thread)
        static void DishPileWorldLoad()
        {
            sDpcShadowDay = -1;
            if (AlarmManager.Global == null) return;
            if (sDpcAlarm != AlarmHandle.kInvalidHandle) AlarmManager.Global.RemoveAlarm(sDpcAlarm);
            sDpcAlarm = AlarmManager.Global.AddAlarmRepeating(1f, TimeUnit.Hours, delegate { try { DpcPass(); } catch (Exception) { PatchCounters()[kDpc + 5]++; } },
                1f, TimeUnit.Hours, "Ts3Fixes.DishPileCap", AlarmType.NeverPersisted, null);
        }

        sealed class DpcLot { public Lot L; public List<ServingContainer> Eligible = new List<ServingContainer>(); public int Dirty, Pending, Used, Seen; public Dictionary<string, int> Types = new Dictionary<string, int>(); }

        static void DpcPass()
        {
            int[] c = PatchCounters();
            bool on = ShortcutOn("dishpilecap");
            c[kDpc + 6]++;
            int cap = DishPileCapCore.Cap(Instantiator.kDishPileCapPerLot), budget = DishPileCapCore.Budget(Instantiator.kDishPileCapBudget);
            Dictionary<ulong, DpcLot> lots = new Dictionary<ulong, DpcLot>();
            int world = 0;
            foreach (ServingContainer d in Sims3.Gameplay.Queries.GetObjects<ServingContainer>())
            {
                try
                {
                    if (d == null || d.HasBeenDestroyed) continue;
                    bool dirty = !d.HasFood || d.IsSpoiled;
                    if (!dirty) continue;
                    Lot l = d.LotCurrent;
                    bool place = l == null || d.InInventory || !d.InWorld || d.Parent is IBuffetTable || d.Parent is PicnicBlanket || d.Parent is BakeSaleTable;
                    if (l == null) continue;
                    world++;
                    DpcLot e;
                    if (!lots.TryGetValue(l.LotId, out e)) { e = new DpcLot { L = l }; lots[l.LotId] = e; }
                    e.Dirty++;
                    string tn = d.GetType().Name;
                    int n; e.Types.TryGetValue(tn, out n); e.Types[tn] = n + 1;
                    bool used = d.InUse || (d.ActorsUsingMe != null && d.ActorsUsingMe.Count > 0)
                        || (d.RoutingReferenceList != null && d.RoutingReferenceList.Count > 0) || d.Parent is Sim;
                    bool pending = sDpcAutoDestroy != null && !AlarmHandle.kInvalidHandle.Equals(sDpcAutoDestroy.GetValue(d));
                    // FarFromViewers only for dishes that pass everything else (it reads the camera)
                    bool far = place || used || pending || FarFromViewers(d.Position);
                    switch (DishPileCapCore.Dish(true, used, place, pending, far))
                    {
                        case DishPileCapCore.kEligible: e.Eligible.Add(d); break;
                        case DishPileCapCore.kPending: e.Pending++; break;
                        case DishPileCapCore.kSeen: e.Seen++; break;
                        default: e.Used++; break;
                    }
                }
                catch (Exception) { c[kDpc + 5]++; }
            }
            c[kDpc + 8] = world;
            DpcLot biggest = null;
            foreach (DpcLot e in lots.Values) if (biggest == null || e.Dirty > biggest.Dirty) biggest = e;
            c[kDpc + 7] = biggest != null ? biggest.Dirty : 0;
            int over = 0;
            foreach (DpcLot e in lots.Values) if (e.Dirty > cap) over++;
            c[kDpc + 9] = over;
            if (!on)
            {
                int day = (int)SimClock.ConvertFromTicks(SimClock.CurrentTicks, TimeUnit.Days);
                if (biggest != null && biggest.Dirty > cap && day != sDpcShadowDay)
                {
                    sDpcShadowDay = day;
                    DpcLog("off, biggest pile " + DpcDescribe(biggest) + ", " + over + " lots over cap " + cap);
                }
                return;
            }
            Household act = Household.ActiveHousehold;
            foreach (DpcLot e in lots.Values)
            {
                if (budget <= 0) break;
                if (e.Dirty <= cap) continue;
                try
                {
                    Lot l = e.L;
                    bool activeHome = l == Household.ActiveHouseholdLot || (act != null && l.Household == act);
                    bool owned = act != null && act.RealEstateManager != null && act.RealEstateManager.FindProperty(l) != null;
                    bool player = false;
                    foreach (Sim s in l.GetSims()) if (IsPlayerSim(s)) { player = true; break; }  // household rule: unknown scope counts
                    bool seen = !l.IsWorldLot && UfLotSeen(l);
                    if (!DishPileCapCore.LotOk(activeHome, owned, player, seen)) { c[kDpc + 2]++; continue; }
                    c[kDpc + 3] += e.Used; c[kDpc + 4] += e.Seen;
                    int q = DishPileCapCore.Quota(e.Eligible.Count, e.Pending, cap, budget);
                    if (q <= 0) continue;
                    int removed = 0;
                    for (int i = 0; i < e.Eligible.Count && removed < q; i++)
                    {
                        ServingContainer d = e.Eligible[i];
                        // EA's nightly test and body (Lot.DoesObjectNeedCleaning, then MagicallyCleanUp's per-object step for an
                        // IThrowAwayable: skip InUse, ThrowAwayImmediately), plus OnAutoDestroy's routing-reference test
                        if (d.HasBeenDestroyed || d.InUse || (d.RoutingReferenceList != null && d.RoutingReferenceList.Count > 0)) continue;
                        if (!Lot.DoesObjectNeedCleaning(d)) { c[kDpc + 3]++; continue; }
                        d.ThrowAwayImmediately();
                        removed++;
                    }
                    budget -= removed;
                    c[kDpc] += removed;
                    if (removed > 0) { c[kDpc + 1]++; DpcLog(DpcDescribe(e) + ", removed " + removed + " (cap " + cap + ")"); }
                }
                catch (Exception) { c[kDpc + 5]++; }
            }
        }

        static string DpcDescribe(DpcLot e)
        {
            Lot l = e.L;
            string kind = l == Household.ActiveHouseholdLot ? "ACTIVE HOUSEHOLD HOME" : l.IsWorldLot ? "world lot" : l.IsCommunityLot ? "community " + l.CommercialLotSubType : l.Household != null ? "residential, household " + l.Household.Name : "residential, empty";
            int sinks = 0;
            try { if (!l.IsWorldLot) sinks = l.GetObjects<Sink>().Length + l.GetObjects<Dishwasher>().Length; } catch (Exception) { sinks = -1; }
            List<string> t = new List<string>();
            foreach (KeyValuePair<string, int> kv in e.Types) t.Add(kv.Key + " " + kv.Value);
            return (l.Name ?? "?") + " 0x" + l.LotId.ToString("X") + " (" + kind + ", sinks " + sinks + ") " + e.Dirty + " dirty (" + string.Join(", ", t.ToArray())
                + "), pending EA auto-destroy " + e.Pending + ", used/held " + e.Used + ", near a viewer " + e.Seen;
        }
    }
}
