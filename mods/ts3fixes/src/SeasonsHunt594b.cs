using System;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Objects;
using Sims3.Gameplay.Objects.Gardening;
using Sims3.Gameplay.Pools;
using Sims3.Gameplay.Seasons;
using Sims3.Gameplay.Services;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // docs/594 "Built (2)": the Seasons hot-path leads. Every switch off by default; all four act on household Sims too
    // (EA bug fix or proven-exact reorder, no skip or throttle).
    //
    // weathermanagerthrow (kWeatherManagerThrow; docs/594 §3). [verified, decompiled] SeasonsManager.Simulate runs the
    // progress, temperature, weather, terrain-cover and tree managers in one loop and catches only ResetException, so any
    // other throw ends the SeasonsManager script: weather, temperature, snow, ponds and leaves stop for the rest of the
    // session. RainEvent.PutOutFires (every 10 sim-minutes of rain) reads Firefighter.sFirefighterDictionary without a null
    // test after it extinguishes an unsheltered fire; Firefighter.Create allocates it only when the firefighter service is
    // valid for the world and DestroyFirefighter sets it to null, so a fire in the rain in such a world throws. Fix: the
    // lookup is skipped when the dictionary is null (nobody to mark RainAssisted). Audit of the rest of the loop: docs/594 §3.
    // Counters: [2135] null-dictionary lookups skipped, [2136] null dictionary hit with the switch off (EA's throw kept),
    // [2137] errors (fallback to EA's body).
    //
    // rainsheltercheap (kRainShelterCheap; docs/594 §4). Same pass: every Sim and every Soil gets
    // IsShelteredFromPrecipitation (LotManager.GetLotAtPoint + World.HasAnyRoofOrFloorAbovePos, both native) before the cheap
    // test that decides whether anything happens (Sim on fire / plant water below 100). Both tests are pure and nothing
    // yields between them, so testing the cheap one first gives the same result. A Sim without a BuffManager keeps EA's order
    // (so the throw, if any, stays where EA has it). Counters: [2138] shelter tests skipped, [2139] shelter tests run,
    // [2140] passes, [2141] errors.
    //
    // pondclearscan (kPondClearScan; docs/594 §5). [verified, decompiled] TerrainCoverManager.UpdatePondFreezing calls
    // PondManager.AllPondsClear on every SeasonsManager tick while the pond state is TryingToMelt, it is warm enough to melt
    // and somebody is on a pond; each call is a world-wide GetObjects<LotPond> before the one-field world-pond test. Fix,
    // same answer: the world pond is tested first, then the lot pond the last scan found in use (still a live object);
    // only when neither is in use does EA's scan run. Counters: [2142] scans skipped (world pond in use), [2143] scans
    // skipped (remembered lot pond in use), [2144] scans run, [2145] errors.
    //
    // soakedstat (kSoakedStat; docs/594 §6). [verified, decompiled] SimTemperature.AddSimTemperatureBuffsIfNecessary adds
    // Soaked (Origin.FromRain) to every Sim outside in the rain on each environment update (every 2 sim-minutes), and
    // BuffSoaked.OnReAddition increments TombStats "times soaked" on every re-add, so one walk in the rain counts ~30 soaks an
    // hour. The first soak still counts (OnAddition); a rain re-add of a buff the Sim already has does not. Other re-adds
    // (a trap or a balloon on a soaked Sim) count as before. Persisted stat, so opt-in. Counters: [2146] rain re-adds not
    // counted, [2147] rain re-adds counted (switch off), [2148] errors.
    public static partial class Fixes
    {
        // EA's private SeasonsManager.UpdateData, field for field (DateAndTime CurrentTime, float HoursElapsed): the hook
        // receives the caller's struct by value, so both layouts must match; ApplyRainFires checks that before patching.
        public struct RfUpdateData
        {
            public DateAndTime CurrentTime;
            public float HoursElapsed;
        }

        static readonly Type sRfUpdateData = B13Nested(typeof(SeasonsManager), "UpdateData");
        static readonly FieldInfo sRfNextField = sLwRain == null ? null : sLwRain.GetField("mNextPutOutFiresTime", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly MethodInfo sRfSchedule = sLwRain == null ? null : sLwRain.GetMethod("SchedulePutOutFires", BindingFlags.Instance | BindingFlags.NonPublic, null, new Type[] { typeof(DateAndTime) }, null);
        static readonly MethodInfo sRfTarget = sLwRain == null || sRfUpdateData == null ? null : sLwRain.GetMethod("PutOutFires", BindingFlags.Instance | BindingFlags.NonPublic, null, new Type[] { sRfUpdateData }, null);
        static readonly MethodInfo sSsTarget = typeof(BuffSoaked).GetMethod("OnReAddition", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
        static string sRfMissing = "";

        // "" when EA's UpdateData has exactly RfUpdateData's instance fields (same names, types, order), else the difference.
        static string RfLayout()
        {
            if (sRfUpdateData == null) return "SeasonsManager+UpdateData";
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            FieldInfo[] ea = sRfUpdateData.GetFields(inst), ours = typeof(RfUpdateData).GetFields(inst);
            if (ea.Length != ours.Length) return "UpdateData has " + ea.Length + " fields";
            for (int i = 0; i < ea.Length; i++)
                if (ea[i].Name != ours[i].Name || ea[i].FieldType != ours[i].FieldType) return "UpdateData field " + i + " is " + ea[i].FieldType.Name + " " + ea[i].Name;
            return "";
        }

        static void ApplyRainFires()
        {
            sRfMissing = EpJoin(SpNames("SeasonsManager+WeatherManager+RainEvent", sLwRain, "RainEvent.mNextPutOutFiresTime", sRfNextField,
                "RainEvent.SchedulePutOutFires", sRfSchedule, "RainEvent.PutOutFires(UpdateData)", sRfTarget), RfLayout()).Trim();
            if (sRfMissing.Length > 0) { AppliedLog().Add("weathermanagerthrow/rainsheltercheap: not found: " + sRfMissing); return; }
            Patch(sLwRain, "PutOutFires", new Type[] { sRfUpdateData }, typeof(RfHook), "PutOutHook", "PutOutOrig");
        }

        static void ApplyPondClearScan()
        {
            Patch(typeof(PondManager), "AllPondsClear", Type.EmptyTypes, typeof(PcsHook), "ClearHook", "ClearOrig");
        }

        static void ApplySoakedStat()
        {
            if (sSsTarget == null) { AppliedLog().Add("soakedstat: not found: BuffSoaked.OnReAddition"); return; }
            Patch(typeof(BuffSoaked), "OnReAddition", new Type[] { typeof(BuffManager), typeof(BuffInstance), typeof(BuffInstance), typeof(BuffNames), typeof(Origin), typeof(bool) },
                typeof(SoakReAddHook), "ReAddHook", "ReAddOrig");
        }

        // Pure: skip the firefighter lookup? (switch on and no dictionary; with it off EA's NullReferenceException stays)
        internal static bool WmtSkip(bool on, bool dictNull) { return on && dictNull; }

        // Pure: does the pass act on this Sim / Soil? EA: !sheltered && hit. Ours tests hit first; same truth table.
        internal static bool RscActs(bool hit, bool sheltered) { return hit && !sheltered; }
        internal static bool RscActsEa(bool hit, bool sheltered) { return !sheltered && hit; }

        // Pure: answer AllPondsClear without the world scan? (on, and the world pond or the remembered live pond is in use)
        internal static bool PcsShortcut(bool on, bool worldInUse, bool memoInUse) { return on && (worldInUse || memoInUse); }

        // Pure: count this re-add as a new soak? EA counts every re-add; a rain re-add keeps a soak going.
        internal static bool SsCount(bool on, Origin origin) { return !on || origin != Origin.FromRain; }

        // ---- RainEvent.PutOutFires ----------------------------------------------------------------------------------------
        // `this` is the RainEvent at run time. The next-pass time is cached per event (EA writes it only through
        // SchedulePutOutFires, from OnStart on a new event and from this pass); the off path drops the cache so a switch
        // flip mid-rain re-reads EA's field.
        public class RfHook
        {
            static object sRfLast;
            static DateAndTime sRfNext;

            public void PutOutHook(RfUpdateData ud)
            {
                bool guard = ShortcutOn("weathermanagerthrow"), cheap = ShortcutOn("rainsheltercheap");
                if (!guard && !cheap) { sRfLast = null; PutOutOrig(ud); return; }
                object me = this;
                DateAndTime now = ud.CurrentTime;
                try
                {
                    if (!ReferenceEquals(me, sRfLast)) { sRfNext = (DateAndTime)sRfNextField.GetValue(me); sRfLast = me; }
                    if (!(sRfNext != DateAndTime.Invalid) || !(now >= sRfNext)) return;
                    sRfSchedule.Invoke(me, new object[] { now });   // EA's SchedulePutOutFires(currentTime), first as in EA
                    sRfNext = (DateAndTime)sRfNextField.GetValue(me);
                }
                catch (Exception) { PatchCounters()[cheap ? 2141 : 2137]++; sRfLast = null; PutOutOrig(ud); return; }
                int[] c = PatchCounters();
                if (cheap) c[2140]++;
                // fires: EA's order (the leaf-pile test is not cheaper than the shelter test)
                Fire[] fires = Sims3.Gameplay.Queries.GetObjects<Fire>();
                foreach (Fire fire in fires)
                {
                    if (!SeasonsManager.IsShelteredFromPrecipitation(fire) && !fire.HasLeafPileAtLocation())
                    {
                        fire.ExtinguishFire();
                        System.Collections.Generic.Dictionary<ulong, Firefighter.FirefighterInformation> dict = Firefighter.sFirefighterDictionary;
                        if (dict == null)
                        {
                            if (WmtSkip(guard, true)) { c[2135]++; continue; }
                            c[2136]++;
                            throw new NullReferenceException();   // EA's throw with the switch off
                        }
                        Firefighter.FirefighterInformation value;
                        if (dict.TryGetValue(fire.FireLotId, out value)) value.RainAssisted = true;
                    }
                }
                Sim[] sims = Sims3.Gameplay.Queries.GetObjects<Sim>();
                foreach (Sim sim in sims)
                {
                    if (cheap && sim.BuffManager != null)
                    {
                        if (!FirefighterSituation.IsSimOnFire(sim)) { c[2138]++; continue; }
                        c[2139]++;
                        if (!SeasonsManager.IsShelteredFromPrecipitation(sim)) FirefighterSituation.PutFireOnSimOut(sim);
                    }
                    else
                    {
                        if (cheap) c[2139]++;   // no BuffManager: EA's order, so a throw stays where EA has it
                        if (!SeasonsManager.IsShelteredFromPrecipitation(sim) && FirefighterSituation.IsSimOnFire(sim)) FirefighterSituation.PutFireOnSimOut(sim);
                    }
                }
                Soil[] soils = Sims3.Gameplay.Queries.GetObjects<Soil>();
                foreach (Soil soil in soils)
                {
                    if (!cheap) { if (!SeasonsManager.IsShelteredFromPrecipitation(soil)) soil.OnSprinkled(); continue; }
                    // Soil.OnSprinkled: `if (plant in the Plant slot && plant.WaterLevel < 100) plant.WaterLevel = 100`
                    Plant plant = soil.GetContainedObject(Soil.ContainmentSlots.Plant) as Plant;
                    if (plant == null || !(plant.WaterLevel < 100f)) { c[2138]++; continue; }
                    c[2139]++;
                    if (!SeasonsManager.IsShelteredFromPrecipitation(soil)) plant.WaterLevel = 100f;
                }
            }

            public void PutOutOrig(RfUpdateData ud) { } // SeasonsManager.WeatherManager.RainEvent.PutOutFires
        }

        // ---- PondManager.AllPondsClear ------------------------------------------------------------------------------------
        public static class PcsHook
        {
            internal static LotPond sMemo;

            public static bool ClearHook()
            {
                if (!ShortcutOn("pondclearscan")) { sMemo = null; return ClearOrig(); }
                int[] c = PatchCounters();
                WorldPondHelper w = PondManager.WorldPondSingleton;
                if (w != null && w.UseCount > 0) { c[2142]++; return false; }
                LotPond m = sMemo;
                if (m != null && PcsLive(m) && m.UseCount > 0) { c[2143]++; return false; }
                c[2144]++;
                return PcsScan();
            }

            public static bool ClearOrig() { return false; } // PondManager.AllPondsClear
        }

        // A pond still registered with the simulator (GetObjects<LotPond> lists exactly those). [inferred: Queries.GetObjects
        // is native over the proxied script objects of the type; the check's 'run' compares the answer with EA's live]
        static bool PcsLive(LotPond p) { return Simulator.GetProxy(p.ObjectId) != null; }

        // EA's scan; remembers the first pond in use (EA breaks there too). World pond test after it, as EA.
        static bool PcsScan()
        {
            PcsHook.sMemo = null;
            LotPond[] ponds = Sims3.Gameplay.Queries.GetObjects<LotPond>();
            foreach (LotPond p in ponds)
                if (p.UseCount > 0) { PcsHook.sMemo = p; return false; }
            WorldPondHelper w = PondManager.WorldPondSingleton;
            return !(w != null && w.UseCount > 0);
        }

        // ---- BuffSoaked.OnReAddition --------------------------------------------------------------------------------------
        // Derives from Buff so `base.OnReAddition` compiles to EA's Buff.OnReAddition (non-virtual call, as BuffSoaked's own
        // body does); at run time `this` is the BuffSoaked.
        public abstract class SoakReAddHook : Buff
        {
            protected SoakReAddHook() : base(null) { }

            public bool ReAddHook(BuffManager bm, BuffInstance existingBuff, BuffInstance newBuff, BuffNames guid, Origin origin, bool timeoutPaused)
            {
                bool on = ShortcutOn("soakedstat");
                if (!on && origin != Origin.FromRain) return ReAddOrig(bm, existingBuff, newBuff, guid, origin, timeoutPaused);
                if (!on) { PatchCounters()[2147]++; return ReAddOrig(bm, existingBuff, newBuff, guid, origin, timeoutPaused); }
                // EA's body; the stat is counted unless this is a rain re-add
                BuffSoaked.BuffInstanceSoaked soaked = newBuff as BuffSoaked.BuffInstanceSoaked;
                if (soaked != null) soaked.AlarmOwner = bm.Actor;
                if (SsCount(true, origin)) bm.Actor.SimDescription.TombStats.IncrementTimesSoaked();
                else PatchCounters()[2146]++;
                return base.OnReAddition(bm, existingBuff, newBuff, guid, origin, timeoutPaused);
            }

            public bool ReAddOrig(BuffManager bm, BuffInstance existingBuff, BuffInstance newBuff, BuffNames guid, Origin origin, bool timeoutPaused) { return false; } // BuffSoaked.OnReAddition
        }

        // ---- checks ----------------------------------------------------------------------------------------------------

        // WeatherManagerThrowCheck run: the PutOutFires rule on a null dictionary (skip when on, EA's throw when off).
        // Controls: a live dictionary is always used; the UpdateData mirror matches EA's layout; RainEvent members resolve.
        // Also prints whether this world has the dictionary (null = the throw is reachable here).
        public static object WeatherManagerThrowCheck(string what)
        {
            string counters = B582Counters("weathermanagerthrow", 2135, new[] { "null-dictionary lookups skipped", "null dictionary hit (switch off)", "errors" }, sRfMissing);
            if (what == "result") return counters + "; dictionary null in this world=" + (Firefighter.sFirefighterDictionary == null);
            if (what != "run") return "weathermanagerthrow <run|result>";
            bool on = ShortcutOn("weathermanagerthrow");
            bool fixedNow = WmtSkip(on, true);
            bool controls = !WmtSkip(true, false) && !WmtSkip(false, false) && !WmtSkip(false, true) && RfLayout().Length == 0;
            return B582Line("weathermanagerthrow", on, "null firefighter dictionary skipped in PutOutFires", fixedNow,
                "live dictionary used, off keeps EA's throw, UpdateData layout same=" + controls, controls, sRfMissing, counters)
                + "; dictionary null in this world=" + (Firefighter.sFirefighterDictionary == null);
        }

        // RainShelterCheapCheck run: the reorder's truth table equals EA's, and on the live world every Sim and Soil gets
        // the same verdict both ways (read-only: both predicates, no action). 'result' times one shelter test against one
        // cheap test on the active Sim (20 each).
        public static object RainShelterCheapCheck(string what)
        {
            string counters = B582Counters("rainsheltercheap", 2138, new[] { "shelter tests skipped", "shelter tests run", "passes", "errors" }, sRfMissing);
            if (what == "result") return counters + "; " + RscTiming();
            if (what != "run") return "rainsheltercheap <run|result>";
            bool on = ShortcutOn("rainsheltercheap");
            bool controls = RfLayout().Length == 0;
            for (int i = 0; i < 4; i++) controls &= RscActs((i & 1) != 0, (i & 2) != 0) == RscActsEa((i & 1) != 0, (i & 2) != 0);
            int same = 0, n = 0;
            string missing = sRfMissing;
            try
            {
                foreach (Sim s in Sims3.Gameplay.Queries.GetObjects<Sim>())
                {
                    if (s.BuffManager == null) continue;
                    bool hit = FirefighterSituation.IsSimOnFire(s), sh = SeasonsManager.IsShelteredFromPrecipitation(s);
                    n++; if (RscActs(hit, sh) == RscActsEa(hit, sh)) same++;
                }
                foreach (Soil soil in Sims3.Gameplay.Queries.GetObjects<Soil>())
                {
                    Plant p = soil.GetContainedObject(Soil.ContainmentSlots.Plant) as Plant;
                    bool hit = p != null && p.WaterLevel < 100f, sh = SeasonsManager.IsShelteredFromPrecipitation(soil);
                    n++; if (RscActs(hit, sh) == RscActsEa(hit, sh)) same++;
                }
            }
            catch (Exception e) { missing = EpJoin(missing, "threw " + e.GetType().Name); }
            controls &= same == n;
            return B582Line("rainsheltercheap", on, "cheap test before the shelter natives", on,
                "truth table and live Sims/Soil same as EA (" + same + "/" + n + ")=" + controls, controls, missing, counters);
        }

        static string RscTiming()
        {
            try
            {
                Sim a = Sim.ActiveActor;
                if (a == null) return "timing: no active Sim";
                uint t0 = SrcNowUs();
                for (int i = 0; i < 20; i++) SeasonsManager.IsShelteredFromPrecipitation(a);
                uint t1 = SrcNowUs();
                for (int i = 0; i < 20; i++) FirefighterSituation.IsSimOnFire(a);
                uint t2 = SrcNowUs();
                return "timing: shelter test " + ((t1 - t0) / 20u) + " us, on-fire test " + ((t2 - t1) / 20u) + " us (Sims " + Sims3.Gameplay.Queries.GetObjects<Sim>().Length
                    + ", Soil " + Sims3.Gameplay.Queries.GetObjects<Soil>().Length + ", one pass per 10 sim-min of rain)";
            }
            catch (Exception e) { return "timing: threw " + e.GetType().Name; }
        }

        // PondClearScanCheck run: our answer (shortcuts included) equals EA's scan on the live world, and the rule keeps the
        // scan when nothing is in use. 'result' times EA's scan against the shortcut path (20 each).
        public static object PondClearScanCheck(string what)
        {
            string counters = B582Counters("pondclearscan", 2142, new[] { "scans skipped (world pond)", "scans skipped (remembered pond)", "scans run", "errors" }, "");
            if (what == "result") return counters + "; " + PcsTiming();
            if (what != "run") return "pondclearscan <run|result>";
            bool on = ShortcutOn("pondclearscan");
            bool fixedNow = PcsShortcut(on, true, false);
            bool controls = !PcsShortcut(true, false, false) && PcsShortcut(true, false, true) && !PcsShortcut(false, true, true);
            string missing = "";
            bool ea = false, ours = false;
            try
            {
                ea = PcsScan();   // EA's answer (also refreshes the memo, as a scan does)
                WorldPondHelper w = PondManager.WorldPondSingleton;
                LotPond m = PcsHook.sMemo;
                ours = !((w != null && w.UseCount > 0) || (m != null && PcsLive(m) && m.UseCount > 0)) && PcsScan();
                controls &= ea == ours;
            }
            catch (Exception e) { missing = "threw " + e.GetType().Name; }
            return B582Line("pondclearscan", on, "world/remembered pond tested before the world scan", fixedNow,
                "same answer as EA's scan (" + ea + "/" + ours + "), no shortcut when nothing in use=" + controls, controls, missing, counters);
        }

        static string PcsTiming()
        {
            try
            {
                uint t0 = SrcNowUs();
                for (int i = 0; i < 20; i++) PcsScan();
                uint t1 = SrcNowUs();
                WorldPondHelper w = PondManager.WorldPondSingleton;
                for (int i = 0; i < 20; i++) { bool b = w != null && w.UseCount > 0; if (b) w = PondManager.WorldPondSingleton; }
                uint t2 = SrcNowUs();
                return "timing: world scan " + ((t1 - t0) / 20u) + " us, shortcut " + ((t2 - t1) / 20u) + " us (lot ponds " + Sims3.Gameplay.Queries.GetObjects<LotPond>().Length
                    + "; runs per Seasons tick only while ponds try to melt with a skater on them)";
            }
            catch (Exception e) { return "timing: threw " + e.GetType().Name; }
        }

        // SoakedStatCheck run: a rain re-add is not counted when on; controls: other re-adds count, off counts as EA.
        public static object SoakedStatCheck(string what)
        {
            string counters = B582Counters("soakedstat", 2146, new[] { "rain re-adds not counted", "rain re-adds counted (switch off)", "errors" }, sSsTarget == null ? "BuffSoaked.OnReAddition" : "");
            if (what == "result") return counters;
            if (what != "run") return "soakedstat <run|result>";
            bool on = ShortcutOn("soakedstat");
            bool fixedNow = !SsCount(on, Origin.FromRain);
            bool controls = SsCount(true, Origin.None) && SsCount(false, Origin.FromRain) && SsCount(false, Origin.None);
            return B582Line("soakedstat", on, "rain re-add not counted as a soak", fixedNow, "other re-adds count, off as EA=" + controls, controls,
                sSsTarget == null ? "BuffSoaked.OnReAddition" : "", counters);
        }
    }
}
