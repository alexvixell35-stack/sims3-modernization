using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.ActiveCareer.ActiveCareers;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Careers;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.ObjectComponents;
using Sims3.Gameplay.Objects;
using Sims3.Gameplay.Objects.Environment;
using Sims3.Gameplay.Objects.FoodObjects;
using Sims3.Gameplay.Objects.HobbiesSkills;
using Sims3.Gameplay.Objects.Misc;
using Sims3.Gameplay.Objects.Miscellaneous;
using Sims3.Gameplay.Objects.Register;
using Sims3.Gameplay.Objects.Rewards;
using Sims3.Gameplay.Pools;
using Sims3.Gameplay.Socializing;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // EP batch 3 (docs/587 §8, sweep tranche 2). Every switch off by default; none of these methods is changed by
    // simler90 UPDATE242. Checks: EpBatch3Checks.cs. Slots 3911-3931 (block fixes-1400) and 1500-1520 (block fixes-1500).
    public static partial class Fixes
    {
        // Shared pure rule: an object EA drops is destroyed only when it is still alive and neither in the world nor in
        // an inventory (so nothing a Sim holds, carries or sees is touched).
        internal static bool OrphanDestroy(bool on, bool live, bool inWorld, bool inInventory) { return on && live && !inWorld && !inInventory; }

        // ---- EP3 §1 fortunetellerscamodds (tuning kFortuneTellerScamOdds) ---------------------------------------------
        // [verified, decompiled] FortuneTellerCareer.CreateReadingChancesArrays (Supernatural) builds one float[4] per
        // level 1-9, fills it with the scam-artist interpolation, adds it to the scam table, then overwrites the same array
        // with the mystic interpolation and adds it to the mystic table. Both tables hold the same array, so scam-artist
        // readings at levels 1-9 use the mystic odds. Fix: each scam level 1-9 gets its own array from EA's own
        // interpolation (kBase -> kMaxScamArtist). Runs after EA builds the tables and again at world load (the tables are
        // built once, at the first FortuneTellerCareer, which can come before ts3fixes applies). Household-visible.
        // Counters: [3911] scam rows rebuilt, [3912] shared rows left with the switch off, [3913] errors.
        static readonly FieldInfo sFtBase = typeof(FortuneTellerCareer).GetField("kBasePsychicReadingChances", kEpStatic);
        static readonly FieldInfo sFtScam = typeof(FortuneTellerCareer).GetField("kMaxScamArtistPsychicReadingChances", kEpStatic);
        static readonly FieldInfo sFtMystic = typeof(FortuneTellerCareer).GetField("kMaxMysticPsychicReadingChances", kEpStatic);

        // Pure: the scam table's row at this level (EA: the mystic row, because the array is shared).
        internal static float[] FtScamRow(bool on, int level, float[] bas, float[] scam, float[] mystic)
        {
            float[] r = new float[bas.Length];
            for (int j = 0; j < r.Length; j++)
                r[j] = MathHelpers.LinearInterpolate(0f, 1f, bas[j], on ? scam[j] : mystic[j], level / 10f);
            return r;
        }

        static void ApplyFortuneTellerScamOdds()
        {
            string miss = EpMiss("kBasePsychicReadingChances", sFtBase, "kMaxScamArtistPsychicReadingChances", sFtScam, "kMaxMysticPsychicReadingChances", sFtMystic);
            if (miss.Length > 0) { AppliedLog().Add("fortunetellerscamodds: not found: " + miss); return; }
            Patch(typeof(FortuneTellerCareer), "CreateReadingChancesArrays", Type.EmptyTypes, typeof(BEp3FtHook), "CreateHook", "CreateOrig");
        }

        internal static void FortuneScamWorldLoad() { FtRepair(); }

        static void FtRepair()
        {
            try
            {
                Dictionary<int, float[]> scamT = FortuneTellerCareer.sScamArtistPsychicReadingChances;
                if (scamT == null || sFtBase == null || sFtScam == null || sFtMystic == null) return;
                float[] bas = (float[])sFtBase.GetValue(null), scam = (float[])sFtScam.GetValue(null), mystic = (float[])sFtMystic.GetValue(null);
                if (bas == null || scam == null || mystic == null || scam.Length < bas.Length || mystic.Length < bas.Length) return;
                bool on = ShortcutOn("fortunetellerscamodds");
                for (int i = 1; i < 10; i++)
                {
                    float[] cur;
                    if (!scamT.TryGetValue(i, out cur) || cur == null || cur.Length != bas.Length) continue;
                    // On: the scam interpolation. Off: EA's row (the mystic one), so a row rebuilt earlier in the session
                    // goes back to EA's values when the switch is turned off.
                    float[] row = FtScamRow(on, i, bas, scam, mystic);
                    if (FtSame(cur, row))
                    {
                        if (!on && !FtSame(cur, FtScamRow(true, i, bas, scam, mystic))) PatchCounters()[3912]++;
                        continue;
                    }
                    scamT[i] = row;  // the mystic table keeps EA's array
                    if (on) PatchCounters()[3911]++;
                }
            }
            catch (Exception) { PatchCounters()[3913]++; }
        }

        static bool FtSame(float[] a, float[] b)
        {
            if (a.Length != b.Length) return false;
            for (int j = 0; j < a.Length; j++) if (Math.Abs(a[j] - b[j]) > 0.001f) return false;
            return true;
        }

        public class BEp3FtHook
        {
            public static void CreateHook() { CreateOrig(); FtRepair(); }
            public static void CreateOrig() { } // FortuneTellerCareer.CreateReadingChancesArrays
        }

        // ---- EP3 §2 consignmentskillcurve (tuning kConsignmentSkillCurve) ---------------------------------------------
        // [verified, decompiled + shipped tuning] ConsignmentRegister, PotionShopConsignmentRegister and BotShopRegister
        // InitializeData build sSkillVsSaleChanceCurve from the frequency arrays (copy-paste of the next line), so
        // GetChanceOfSale feeds Consignment skill 0-10 into the frequency curve (shipped X 0,3,5 / Y 1,.9,0), which Curve.Fx
        // extrapolates: -2.25 at level 10 instead of the skill curve's 1 (shipped X 0,5,10 / Y .5,.75,1). With shipped
        // weights (.25 potion/bot, .1 consignment) a level-10 seller loses .81 / .325 of sale chance, so items stop selling
        // as the skill rises. Fix: the curve is rebuilt from EA's own kSaleProbabilitySkill arrays after InitializeData and
        // at world load. Household-visible. RE: FruitVeggieStand has the same line (not in this batch; store content).
        // Counters: [3914] curves rebuilt, [3915] frequency curve left with the switch off, [3916] errors.
        static readonly FieldInfo[] sCsCurve = new FieldInfo[3], sCsX = new FieldInfo[3], sCsY = new FieldInfo[3];
        static string sCsMissing = "";
        static readonly float[] kCsSkX = { 0f, 5f, 10f }, kCsSkY = { 0.5f, 0.75f, 1f }, kCsFqX = { 0f, 3f, 5f }, kCsFqY = { 1f, 0.9f, 0f };

        // Pure: piecewise-linear value with end extrapolation (as Curve.Fx).
        internal static float CsLerp(float[] xs, float[] ys, float x)
        {
            int n = xs.Length;
            if (n == 0) return 0f;
            if (n == 1) return ys[0];
            int k = n - 2;
            for (int i = 0; i < n - 1; i++) if (x <= xs[i + 1]) { k = i; break; }
            float dx = xs[k + 1] - xs[k];
            return dx == 0f ? ys[k] : ys[k] + (ys[k + 1] - ys[k]) * (x - xs[k]) / dx;
        }
        // Pure: the skill factor at this Consignment level with the shipped curves (EA: the frequency curve).
        internal static float CsSkillFactor(bool on, float level) { return on ? CsLerp(kCsSkX, kCsSkY, level) : CsLerp(kCsFqX, kCsFqY, level); }
        // Pure: is the curve rebuilt? have = the live curve at the skill curve's X points, want = its Y values.
        internal static bool CsRebuild(bool on, float[] have, float[] want)
        {
            if (!on || have == null || want == null || have.Length != want.Length) return false;
            for (int j = 0; j < have.Length; j++) if (Math.Abs(have[j] - want[j]) > 0.001f) return true;
            return false;
        }

        static void ApplyConsignmentSkillCurve()
        {
            System.Text.StringBuilder miss = new System.Text.StringBuilder();
            for (int i = 0; i < 3; i++)
            {
                Type t = sCgTypes[i];
                sCsCurve[i] = t.GetField("sSkillVsSaleChanceCurve", kEpStatic);
                sCsX[i] = t.GetField("kSaleProbabilitySkillXValues", kEpStatic);
                sCsY[i] = t.GetField("kSaleProbabilitySkillYValues", kEpStatic);
                if (sCsCurve[i] == null || sCsX[i] == null || sCsY[i] == null) miss.Append(miss.Length == 0 ? "" : ", ").Append(t.Name);
            }
            sCsMissing = miss.ToString();
            if (sCsMissing.Length > 0) { AppliedLog().Add("consignmentskillcurve: not found: " + sCsMissing); return; }
            Patch(typeof(ConsignmentRegister), "InitializeData", Type.EmptyTypes, typeof(BEp3Cs0), "InitHook", "InitOrig");
            Patch(typeof(PotionShopConsignmentRegister), "InitializeData", Type.EmptyTypes, typeof(BEp3Cs1), "InitHook", "InitOrig");
            Patch(typeof(BotShopRegister), "InitializeData", Type.EmptyTypes, typeof(BEp3Cs2), "InitHook", "InitOrig");
        }

        internal static void ConsignmentSkillCurveWorldLoad() { for (int i = 0; i < 3; i++) CsRepair(i); }

        static void CsRepair(int i)
        {
            try
            {
                if (sCsCurve[i] == null || sCsX[i] == null || sCsY[i] == null) return;
                bool on = ShortcutOn("consignmentskillcurve");
                Curve cur = sCsCurve[i].GetValue(null) as Curve;
                if (cur == null) return;
                float[] x = (float[])sCsX[i].GetValue(null), y = (float[])sCsY[i].GetValue(null);
                if (x == null || y == null || x.Length == 0 || x.Length != y.Length) return;
                float[] have = new float[x.Length];
                for (int j = 0; j < x.Length; j++) have[j] = cur.Fx(x[j]);  // Fx only moves the curve's last-x cache
                if (CsRebuild(on, have, y))
                {
                    sCsCurve[i].SetValue(null, new Curve(x, y));
                    PatchCounters()[3914]++;
                }
                else if (!on && CsRebuild(true, have, y)) PatchCounters()[3915]++;
            }
            catch (Exception) { PatchCounters()[3916]++; }
        }

        public class BEp3Cs0
        {
            public static void InitHook() { InitOrig(); CsRepair(0); }
            public static void InitOrig() { } // ConsignmentRegister.InitializeData
        }
        public class BEp3Cs1
        {
            public static void InitHook() { InitOrig(); CsRepair(1); }
            public static void InitOrig() { } // PotionShopConsignmentRegister.InitializeData
        }
        public class BEp3Cs2
        {
            public static void InitHook() { InitOrig(); CsRepair(2); }
            public static void InitOrig() { } // BotShopRegister.InitializeData
        }

        // ---- EP3 §3 consignmentstockleak (tuning kConsignmentStockLeak) -----------------------------------------------
        // [verified, decompiled] The three registers' shop stock (mObjectsForSale) holds out-of-world objects made by
        // CreateObjectOutOfWorld. ItemDictionary drops expired entries and PotionShop/BotShop OnMagicHourAlarm drops every
        // rotating entry (5 potions or bots a day) with RemoveAt, never Destroy; only Dispose and the purchase path destroy
        // them. Each drop leaks an object for the rest of the save. Fix: before EA drops an entry, its object is destroyed
        // if it is still out of the world and in no inventory (bought objects are removed from the list by EA first).
        // Household-visible (object count and save size). [inferred] the leaked objects persist in the save.
        // RE, not fixed (no trigger seen): OnMagicHourAlarm/ItemDictionary set ShowTooltip on Create()'s result with no null
        // check; PotionShop/BotShop data CreateObject calls AppreciateDepreciateDailyUpdate before its null check.
        // Counters: [3917] objects destroyed, [3918] leaked with the switch off, [3919] errors.
        static readonly string[] kStData = { "ConsignmentRegisterObjectData", "PotionShopConsignmentRegisterObjectData", "BotShopConsignmentRegisterObjectData" };
        static readonly FieldInfo[] sStList = new FieldInfo[3], sStObj = new FieldInfo[3];
        static readonly PropertyInfo[] sStExpired = new PropertyInfo[3], sStRotatable = new PropertyInfo[3];
        static string sStMissing = "";

        static void ApplyConsignmentStockLeak()
        {
            System.Text.StringBuilder miss = new System.Text.StringBuilder();
            for (int i = 0; i < 3; i++)
            {
                Type t = sCgTypes[i];
                Type d = t.GetNestedType(kStData[i], BindingFlags.NonPublic | BindingFlags.Public);
                sStList[i] = t.GetField("mObjectsForSale", i == 0 ? kInstAll : kEpStatic);
                sStObj[i] = d == null ? null : d.GetField("mObject", kInstAll);
                sStExpired[i] = d == null ? null : d.GetProperty("IsExpired", kInstAll);
                sStRotatable[i] = d == null || i == 0 ? null : d.GetProperty("IsRotatable", kInstAll);
                if (sStList[i] == null || sStObj[i] == null || sStExpired[i] == null || (i > 0 && sStRotatable[i] == null)) miss.Append(miss.Length == 0 ? "" : ", ").Append(t.Name);
            }
            sStMissing = miss.ToString();
            if (sStMissing.Length > 0) { AppliedLog().Add("consignmentstockleak: not found: " + sStMissing); return; }
            Patch(typeof(ConsignmentRegister), "ItemDictionary", new Type[] { typeof(Sim) }, typeof(BEp3St0), "DictHook", "DictOrig");
            Patch(typeof(PotionShopConsignmentRegister), "ItemDictionary", new Type[] { typeof(Sim) }, typeof(BEp3St1), "DictHook", "DictOrig");
            Patch(typeof(BotShopRegister), "ItemDictionary", new Type[] { typeof(Sim) }, typeof(BEp3St2), "DictHook", "DictOrig");
            Patch(typeof(PotionShopConsignmentRegister), "OnMagicHourAlarm", Type.EmptyTypes, typeof(BEp3St1), "MagicHook", "MagicOrig");
            Patch(typeof(BotShopRegister), "OnMagicHourAlarm", Type.EmptyTypes, typeof(BEp3St2), "MagicHook", "MagicOrig");
        }

        // Destroys the objects of the entries EA is about to drop (expired, or rotating at magic hour).
        static void StDrop(int i, object owner, bool rotating)
        {
            try
            {
                IList list = sStList[i].GetValue(owner) as IList;
                if (list == null) return;
                bool on = ShortcutOn("consignmentstockleak");
                PropertyInfo test = rotating ? sStRotatable[i] : sStExpired[i];
                for (int k = 0; k < list.Count; k++)
                {
                    object e = list[k];
                    if (e == null || !(bool)test.GetValue(e, null)) continue;
                    GameObject g = sStObj[i].GetValue(e) as GameObject;
                    if (g == null) continue;
                    bool live = !g.HasBeenDestroyed, inWorld = live && g.InWorld, inInv = live && g.InInventory;
                    if (OrphanDestroy(on, live, inWorld, inInv)) { g.Destroy(); PatchCounters()[3917]++; }
                    else if (!on && OrphanDestroy(true, live, inWorld, inInv)) PatchCounters()[3918]++;
                }
            }
            catch (Exception) { PatchCounters()[3919]++; }
        }

        public class BEp3St0
        {
            public Dictionary<string, List<StoreItem>> DictHook(Sim sim) { StDrop(0, this, false); return DictOrig(sim); }
            public Dictionary<string, List<StoreItem>> DictOrig(Sim sim) { return null; } // ConsignmentRegister.ItemDictionary
        }
        public class BEp3St1
        {
            public Dictionary<string, List<StoreItem>> DictHook(Sim sim) { StDrop(1, null, false); return DictOrig(sim); }
            public Dictionary<string, List<StoreItem>> DictOrig(Sim sim) { return null; } // PotionShopConsignmentRegister.ItemDictionary
            public static void MagicHook() { StDrop(1, null, true); try { MagicOrig(); } catch (ResetException) { throw; } catch (Exception e) { EaAlarmThrew("PotionShopConsignmentRegister.OnMagicHourAlarm", 4111, e); } }  // daily alarm (docs/651 §1)
            public static void MagicOrig() { } // PotionShopConsignmentRegister.OnMagicHourAlarm
        }
        public class BEp3St2
        {
            public Dictionary<string, List<StoreItem>> DictHook(Sim sim) { StDrop(2, null, false); return DictOrig(sim); }
            public Dictionary<string, List<StoreItem>> DictOrig(Sim sim) { return null; } // BotShopRegister.ItemDictionary
            public static void MagicHook() { StDrop(2, null, true); try { MagicOrig(); } catch (ResetException) { throw; } catch (Exception e) { EaAlarmThrew("BotShopRegister.OnMagicHourAlarm", 4111, e); } }  // daily alarm (docs/651 §1)
            public static void MagicOrig() { } // BotShopRegister.OnMagicHourAlarm
        }

        // ---- EP3 §4 herbqualityfloor (tuning kHerbQualityFloor) -------------------------------------------------------
        // [verified, decompiled] Herb+HerbalMedicineExperiment.DecreaseHerbQuality sets quality - 1 with no floor
        // (IncreaseHerbQuality has the Perfect cap), so a Horrifying herb goes to quality 0 and
        // Plant.GetQualityLevelMedian(0) is read out of range: the experiment throws. Fix: a Horrifying herb stays
        // Horrifying. Household-visible (science experiment on a household herb).
        // Counters: [3920] decreases floored, [3921] below-floor decreases with the switch off, [3922] errors.

        // Pure: does this decrease stop at the floor?
        internal static bool HqFloored(bool on, int quality) { return on && quality <= (int)Sims3.Gameplay.Objects.Quality.Horrifying; }

        static void ApplyHerbQualityFloor()
        {
            Patch(typeof(Herb.HerbalMedicineExperiment), "DecreaseHerbQuality", new Type[] { typeof(Herb) }, typeof(BEp3HqHook), "DecHook", "DecOrig");
        }

        public class BEp3HqHook
        {
            public void DecHook(Herb toDecrease)
            {
                try
                {
                    bool on = ShortcutOn("herbqualityfloor");
                    int q = toDecrease == null ? int.MaxValue : (int)toDecrease.GetQuality();
                    if (HqFloored(on, q)) { PatchCounters()[3920]++; return; }
                    if (!on && HqFloored(true, q)) PatchCounters()[3921]++;
                }
                catch (Exception) { PatchCounters()[3922]++; }
                DecOrig(toDecrease);
            }
            public void DecOrig(Herb toDecrease) { } // Herb+HerbalMedicineExperiment.DecreaseHerbQuality
        }

        // ---- EP3 §5 snackbowlslobvfx (tuning kSnackBowlSlobVfx) -------------------------------------------------------
        // [verified, decompiled] SnackBowl+HaveSomeSnack.StartOneShotSlobVFX (Showtime) creates "ep6FoodFalling_main",
        // parents it to the hand and starts it, but keeps no handle and never stops or disposes it, so every slob snack
        // leaks an effect on the Sim. Fix: EA's one-shot form (VisualEffect.FireOneShotEffect), which the engine cleans up.
        // Household-visible (slob Sims eating from a snack bowl).
        // Counters: [3923] one-shots fired, [3924] leaked effects with the switch off, [3925] errors.
        static readonly Type sSbSnack = typeof(SnackBowl).GetNestedType("HaveSomeSnack", BindingFlags.NonPublic | BindingFlags.Public);

        // Pure: does the slob effect leak?
        internal static bool SbLeaks(bool on) { return !on; }

        static void ApplySnackBowlSlobVfx()
        {
            if (sSbSnack == null) { AppliedLog().Add("snackbowlslobvfx: not found: SnackBowl+HaveSomeSnack"); return; }
            Patch(sSbSnack, "StartOneShotSlobVFX", Type.EmptyTypes, typeof(BEp3SbHook), "VfxHook", "VfxOrig");
        }

        public class BEp3SbHook
        {
            public void VfxHook()
            {
                if (SbLeaks(ShortcutOn("snackbowlslobvfx"))) { PatchCounters()[3924]++; VfxOrig(); return; }
                Sim a = null;
                try { a = ((InteractionInstance)(object)this).InstanceActor; }
                catch (Exception) { }
                if (a == null || a.HasBeenDestroyed) { PatchCounters()[3925]++; return; }
                try { VisualEffect.FireOneShotEffect("ep6FoodFalling_main", a, Sim.FXJoints.LeftHand, VisualEffect.TransitionType.SoftTransition); PatchCounters()[3923]++; }
                catch (Exception) { PatchCounters()[3925]++; }
            }
            public void VfxOrig() { } // SnackBowl+HaveSomeSnack.StartOneShotSlobVFX
        }

        // ---- EP3 §6 worldpondskatetest (tuning kWorldPondSkateTest) ---------------------------------------------------
        // [verified, decompiled] WorldPond.CanSkateOnWorldPond (Seasons) calls SkatingRink+Skate.SharedCanSkateOnObjectTest
        // and discards its result (`return true` after it), so a frozen world pond offers Skate to a Sim in robot form,
        // to a Sim already on that skating area, and when the chosen area is full or fails its own test; the tooltip the
        // shared test sets is shown on an enabled entry. Lot ponds and rinks return the shared result. Fix: EA's result is
        // ANDed with the shared test for the same area EA picked (the first area with room, as HasRoomForMoreSkaters).
        // Household-visible.
        // Counters: [3926] skate entries refused, [3927] offered against the shared test with the switch off, [3928] errors.
        static readonly FieldInfo sWpAreas = typeof(WorldPond).GetField("mSkatingAreas", kInstAll);

        // Pure: is Skate offered?
        internal static bool WpOffered(bool on, bool ea, bool shared) { return ea && (shared || !on); }

        static void ApplyWorldPondSkateTest()
        {
            if (sWpAreas == null) { AppliedLog().Add("worldpondskatetest: not found: WorldPond.mSkatingAreas"); return; }
            Patch(typeof(WorldPond), "CanSkateOnWorldPond", new Type[] { typeof(Sim), typeof(GreyedOutTooltipCallback).MakeByRefType() }, typeof(BEp3WpHook), "CanHook", "CanOrig");
        }

        public class BEp3WpHook
        {
            public bool CanHook(Sim actor, ref GreyedOutTooltipCallback greyedOutTooltipCallback)
            {
                bool ea = CanOrig(actor, ref greyedOutTooltipCallback);
                if (!ea) return false;  // EA refused: nothing to do, either way
                try
                {
                    bool on = ShortcutOn("worldpondskatetest");
                    WorldPondSkatingArea area = null;
                    List<WorldPondSkatingArea> areas = sWpAreas.GetValue(this) as List<WorldPondSkatingArea>;
                    if (areas != null)
                        foreach (WorldPondSkatingArea x in areas)
                            if (x != null && x.GetTotalNumberOfSkaters() < x.MaxSkatersTotal) { area = x; break; }
                    GreyedOutTooltipCallback cb = greyedOutTooltipCallback;
                    bool shared = SkatingRink.Skate.SharedCanSkateOnObjectTest(actor, area, area != null, ref cb);
                    bool offered = WpOffered(on, ea, shared);
                    if (!offered) { greyedOutTooltipCallback = cb; PatchCounters()[3926]++; }
                    else if (!on && !shared) PatchCounters()[3927]++;
                    return offered;
                }
                catch (Exception) { PatchCounters()[3928]++; return ea; }
            }
            public bool CanOrig(Sim actor, ref GreyedOutTooltipCallback greyedOutTooltipCallback) { return false; } // WorldPond.CanSkateOnWorldPond
        }

        // ---- EP3 §7 pingpongopponent (tuning kPingPongOpponent) -------------------------------------------------------
        // [verified, decompiled] PingPongTable.GetSimsOnLotWithSim (University; autonomous opponent pick) and
        // GetPotentialPlayers (the picker and the Definition tests) filter lot.GetSims() only by service, pregnancy, role,
        // sleep and passport; GetSimsOnLotWithSim(ignoreKids: false) has no age test at all and GetPotentialPlayers' Teen
        // test passes adult pets. So an autonomous game can pick a baby, toddler, dog, cat or horse as the opponent, and
        // the picker lists pets. Fix: both lists keep only humans of Teen age or older (EA's own picker rule; review: was
        // Child, which let the autonomous path pick a child the player can never pick). Household-visible.
        // RE, not fixed: an empty autonomous list throws in GetRandomObjectFromList (Run has no Count check); the
        // Definition test filters the same way, so only a Sim leaving between Test and Run reaches it, as in EA.
        // Counters: [3929] Sims removed, [3930] non-players listed with the switch off, [3931] errors.

        // Pure: does this Sim stay in the list?
        internal static bool PpKeep(bool on, bool human, bool teenOrAbove) { return !on || (human && teenOrAbove); }

        static void ApplyPingPongOpponent()
        {
            Patch(typeof(PingPongTable), "GetSimsOnLotWithSim", new Type[] { typeof(Sim), typeof(bool) }, typeof(BEp3PpHook), "OnLotHook", "OnLotOrig");
            Patch(typeof(PingPongTable), "GetPotentialPlayers", new Type[] { typeof(Sim), typeof(Lot), typeof(bool), typeof(bool) }, typeof(BEp3PpHook), "PlayersHook", "PlayersOrig");
        }

        static List<Sim> PpFilter(List<Sim> list)
        {
            if (list == null || list.Count == 0) return list;
            try
            {
                bool on = ShortcutOn("pingpongopponent");
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    SimDescription d = list[i] == null ? null : list[i].SimDescription;
                    bool human = d != null && d.IsHuman, teen = d != null && d.TeenOrAbove;
                    if (!PpKeep(on, human, teen)) { list.RemoveAt(i); PatchCounters()[3929]++; }
                    else if (!on && !PpKeep(true, human, teen)) PatchCounters()[3930]++;
                }
            }
            catch (Exception) { PatchCounters()[3931]++; }
            return list;
        }

        public class BEp3PpHook
        {
            public List<Sim> OnLotHook(Sim sim, bool ignoreKids) { return PpFilter(OnLotOrig(sim, ignoreKids)); }
            public List<Sim> OnLotOrig(Sim sim, bool ignoreKids) { return null; } // PingPongTable.GetSimsOnLotWithSim
            public static List<Sim> PlayersHook(Sim sim, Lot lot, bool skipKids, bool noBots) { return PpFilter(PlayersOrig(sim, lot, skipKids, noBots)); }
            public static List<Sim> PlayersOrig(Sim sim, Lot lot, bool skipKids, bool noBots) { return null; } // PingPongTable.GetPotentialPlayers
        }

        // ---- EP3 §8 mermaiddehydratelook (tuning kMermaidDehydrateLook) -----------------------------------------------
        // [verified, decompiled] OccultMermaid+Dehydrate.Run (Island Paradise) calls LookAtManager.DisableLookAts after
        // StandardEntry, and neither Run nor Cleanup re-enables them (Rehydrate's sibling path does), so after any
        // dehydration, finished or cancelled, the mermaid never turns its head at anything again until a reset.
        // Fix: Cleanup re-enables look-ats when the interaction got past StandardEntry. Household-visible.
        // Counters: [3932] look-ats re-enabled, [3933] left disabled with the switch off, [3934] errors.
        static readonly Type sMdType = typeof(OccultMermaid).GetNestedType("Dehydrate", BindingFlags.NonPublic | BindingFlags.Public);

        // Pure: are look-ats turned back on?
        internal static bool MdReenable(bool on, bool entered) { return on && entered; }

        static void ApplyMermaidDehydrateLook()
        {
            if (sMdType == null) { AppliedLog().Add("mermaiddehydratelook: not found: OccultMermaid+Dehydrate"); return; }
            Patch(sMdType, "Cleanup", Type.EmptyTypes, typeof(BEp3MdHook), "CleanHook", "CleanOrig");
        }

        public class BEp3MdHook
        {
            public void CleanHook()
            {
                try
                {
                    bool on = ShortcutOn("mermaiddehydratelook");
                    InteractionInstance ii = (InteractionInstance)(object)this;
                    Sim a = ii.InstanceActor;
                    if (a != null && !a.HasBeenDestroyed && a.LookAtManager != null)
                    {
                        bool entered = ii.StandardEntryCalled;
                        if (MdReenable(on, entered)) { a.LookAtManager.EnableLookAts(); PatchCounters()[3932]++; }
                        else if (!on && entered) PatchCounters()[3933]++;
                    }
                }
                catch (Exception) { PatchCounters()[3934]++; }
                CleanOrig();
            }
            public void CleanOrig() { } // OccultMermaid+Dehydrate.Cleanup
        }

        // ---- EP3 §9 meteormagnetorphan (tuning kMeteorMagnetOrphan) ---------------------------------------------------
        // [verified, decompiled] MeteorMagnet+AttractSpaceObject.RunCommon (Into the Future) creates the meteorite fragment
        // out of the world in SelectOutcome before the attract loop; it is only shown (event 100) and put in the inventory
        // (event 101) on the success animations. A cancelled loop plays "Cancel", so the fragment stays out of the world,
        // and Cleanup never destroys it: one leaked object per cancelled attract. Fix: after EA's Cleanup, a fragment that
        // is still alive, out of the world and in no inventory is destroyed. Not household-visible (out-of-world leak).
        // Counters: [3935] fragments destroyed, [3936] leaked with the switch off, [3937] errors.
        static readonly FieldInfo sMmFrag = typeof(MeteorMagnet.AttractSpaceObject).GetField("mMeteoriteFragment", kInstAll);

        static void ApplyMeteorMagnetOrphan()
        {
            if (sMmFrag == null) { AppliedLog().Add("meteormagnetorphan: not found: AttractSpaceObject.mMeteoriteFragment"); return; }
            Patch(typeof(MeteorMagnet.AttractSpaceObject), "Cleanup", Type.EmptyTypes, typeof(BEp3MmHook), "CleanHook", "CleanOrig");
        }

        public class BEp3MmHook
        {
            public void CleanHook()
            {
                CleanOrig();
                try
                {
                    bool on = ShortcutOn("meteormagnetorphan");
                    GameObject g = sMmFrag.GetValue(this) as GameObject;
                    if (g == null) return;
                    bool live = !g.HasBeenDestroyed, inWorld = live && g.InWorld, inInv = live && g.InInventory;
                    if (OrphanDestroy(on, live, inWorld, inInv))
                    {
                        g.Destroy();
                        sMmFrag.SetValue(this, null);
                        PatchCounters()[3935]++;
                    }
                    else if (!on && OrphanDestroy(true, live, inWorld, inInv)) PatchCounters()[3936]++;
                }
                catch (Exception) { PatchCounters()[3937]++; }
            }
            public void CleanOrig() { } // MeteorMagnet+AttractSpaceObject.Cleanup
        }

        // ---- EP3 §10 daycarelatealarm (tuning kDaycareLateAlarm) ------------------------------------------------------
        // [verified, decompiled] DaycareWorkdaySituation's late-parent pickup (Generations) is a persisted 1-3 hour alarm
        // on RemoveChildHelper.TriggerRemoval -> HandleRemoval(id), which calls SimDescription.Find(id).CreatedSim and
        // RemovePerson -> mChildMonitors[id] with no checks. If the toddler left first (aged up, force-removed, the
        // situation ended, the Sim was deleted), the alarm throws (NRE or KeyNotFound). Fix: the late alarm is skipped
        // when the child is gone or no longer monitored; force removals run as EA. Household-visible (daycare owner).
        // Counters: [3938] stale alarms skipped, [3939] would throw with the switch off, [3940] errors.
        static readonly Type sDcHelper = typeof(DaycareWorkdaySituation).GetNestedType("RemoveChildHelper", BindingFlags.NonPublic | BindingFlags.Public);
        static readonly FieldInfo sDcSit = sDcHelper == null ? null : sDcHelper.GetField("mSituation", kInstAll);
        static readonly FieldInfo sDcId = sDcHelper == null ? null : sDcHelper.GetField("mChildSimDescId", kInstAll);
        static readonly FieldInfo sDcForce = sDcHelper == null ? null : sDcHelper.GetField("mForceRemove", kInstAll);
        static readonly FieldInfo sDcMonitors = typeof(DaycareWorkdaySituation).GetField("mChildMonitors", kInstAll);

        // Pure: is the late pickup skipped?
        internal static bool DcSkip(bool on, bool force, bool childFound, bool monitored) { return on && !force && (!childFound || !monitored); }

        static void ApplyDaycareLateAlarm()
        {
            string miss = EpMiss("DaycareWorkdaySituation+RemoveChildHelper", sDcHelper, "mSituation", sDcSit, "mChildSimDescId", sDcId, "mForceRemove", sDcForce, "mChildMonitors", sDcMonitors);
            if (miss.Length > 0) { AppliedLog().Add("daycarelatealarm: not found: " + miss); return; }
            Patch(sDcHelper, "TriggerRemoval", Type.EmptyTypes, typeof(BEp3DcHook), "TrigHook", "TrigOrig");
        }

        public class BEp3DcHook
        {
            public void TrigHook()
            {
                try
                {
                    bool on = ShortcutOn("daycarelatealarm");
                    bool force = (bool)sDcForce.GetValue(this), found = true, monitored = true;
                    if (!force)  // force removals are EA's own checked path
                    {
                        object sit = sDcSit.GetValue(this);
                        ulong id = (ulong)sDcId.GetValue(this);
                        SimDescription sd = SimDescription.Find(id);
                        IDictionary mon = sit == null ? null : sDcMonitors.GetValue(sit) as IDictionary;
                        found = sd != null && sd.CreatedSim != null;
                        monitored = mon != null && mon.Contains(id);
                    }
                    if (DcSkip(on, force, found, monitored)) { PatchCounters()[3938]++; return; }
                    if (!on && DcSkip(true, force, found, monitored)) PatchCounters()[3939]++;
                }
                catch (Exception) { PatchCounters()[3940]++; }
                TrigOrig();
            }
            public void TrigOrig() { } // DaycareWorkdaySituation+RemoveChildHelper.TriggerRemoval
        }

        // ---- EP3 §11 boxofmysterywoohoo (tuning kBoxOfMysteryWooHoo) --------------------------------------------------
        // [verified, decompiled + shipped kBabyMadeChance 50] BoxOfMystery+TryForBaby.Run (Showtime) starts a pregnancy
        // when base.Run() is true and ShouldImpregnate rolls, but WooHooInBoxOfMystery.Run returns true on every path past
        // the route, including a failed StartSync (partner cancelled or never arrived): no woohoo, still a 50% pregnancy.
        // Fix: for the initiator of Try for Baby, Run counts as successful only if EA's own woohoo step
        // (SocialCallback.RunPostWoohoo with this box) ran. Household-visible.
        // Counters: [3941] failed woohoos without pregnancy roll, [3942] rolled after a failed woohoo with the switch off,
        // [3943] errors.
        static readonly List<Sim> sBmWoohooed = new List<Sim>();

        // Pure: does TryForBaby see a successful woohoo?
        internal static bool BmSucceeded(bool on, bool eaResult, bool woohooed) { return eaResult && (woohooed || !on); }

        static void ApplyBoxOfMysteryWooHoo()
        {
            Patch(typeof(BoxOfMystery.WooHooInBoxOfMystery), "Run", Type.EmptyTypes, typeof(BEp3BmHook), "RunHook", "RunOrig");
            Patch(typeof(SocialCallback), "RunPostWoohoo", new Type[] { typeof(Sim), typeof(Sim), typeof(IGameObject) }, typeof(BEp3BmPostHook), "PostHook", "PostOrig");
        }

        public class BEp3BmHook
        {
            public bool RunHook()
            {
                object self = this;
                if (self.GetType().Name != "TryForBaby") return RunOrig();
                Sim a = ((InteractionInstance)self).InstanceActor;
                sBmWoohooed.Remove(a);
                bool r = RunOrig();
                bool did = sBmWoohooed.Remove(a);
                bool on = ShortcutOn("boxofmysterywoohoo");
                bool res = BmSucceeded(on, r, did);
                if (r && !res) PatchCounters()[3941]++;
                else if (!on && r && !BmSucceeded(true, r, did)) PatchCounters()[3942]++;
                return res;
            }
            public bool RunOrig() { return false; } // BoxOfMystery+WooHooInBoxOfMystery.Run
        }

        public class BEp3BmPostHook
        {
            public static void PostHook(Sim actor, Sim target, IGameObject obj)
            {
                try
                {
                    if (obj is BoxOfMystery && actor != null)
                    {
                        if (sBmWoohooed.Count >= 16) sBmWoohooed.Clear();  // Runs that ended by reset
                        if (!sBmWoohooed.Contains(actor)) sBmWoohooed.Add(actor);
                    }
                }
                catch (Exception) { PatchCounters()[3943]++; }
                PostOrig(actor, target, obj);
            }
            public static void PostOrig(Sim actor, Sim target, IGameObject obj) { } // SocialCallback.RunPostWoohoo(Sim, Sim, IGameObject)
        }

        // ---- EP3 §12 bodysculptorweight (tuning kBodySculptorWeight) --------------------------------------------------
        // [verified, decompiled] BodySculptor+ChangeShape.Run (reward object) works on weight+1 in [0, 2], decrements it
        // and, when the result is negative (a thin Sim), stores 1 + w in num5 and sets weight num4 - num5 = -(1 + w)
        // instead of w: thin results are mirrored around -0.5. "Thin" on a Sim at -0.5 (Medium, 50%) lands at -0.25
        // instead of -0.75, so thinning a thin Sim makes them fatter; Weak/Strong (weight unchanged) flip a thin Sim's
        // weight. Failure outcomes set explicit values and are kept. Fix: on the SetBodyShape call this Run makes (not a
        // failure, not "Original"), the weight is EA's own target w = (weight+1 adjusted by option and power) - 1, if the
        // incoming value is EA's mirrored one. Household-visible.
        // Counters: [3944] weights corrected, [3945] mirrored with the switch off, [3946] errors.
        static readonly Type sBsShape = typeof(BodySculptor).GetNestedType("ChangeShape", BindingFlags.NonPublic | BindingFlags.Public);
        static readonly Type sBsItem = sBsShape == null ? null : sBsShape.GetNestedType("ItemDefinition", BindingFlags.NonPublic | BindingFlags.Public);
        static readonly FieldInfo sBsOption = sBsItem == null ? null : sBsItem.GetField("mOption", kInstAll);
        static readonly FieldInfo sBsFailure = sBsShape == null ? null : sBsShape.GetField("mFailure", kInstAll);
        static readonly FieldInfo sBsPower = typeof(BodySculptor).GetField("mPowerLevel", kInstAll);
        static readonly Dictionary<SimDescription, object> sBsActive = new Dictionary<SimDescription, object>();
        const int kBsThin = 0, kBsFat = 1, kBsOriginal = 4;

        // Pure: the weight set for this start weight, option and power (EA: mirrored when below 0).
        internal static float BsWeight(bool on, float w0, int option, float power)
        {
            float n4 = w0 + 1f;
            if (option == kBsFat) n4 += power * (2f - n4);
            else if (option == kBsThin) n4 -= power * n4;
            float w = n4 - 1f;
            return on || w >= 0f ? w : -(1f + w);
        }

        static void ApplyBodySculptorWeight()
        {
            string miss = EpMiss("BodySculptor+ChangeShape", sBsShape, "ChangeShape+ItemDefinition.mOption", sBsOption, "ChangeShape.mFailure", sBsFailure, "BodySculptor.mPowerLevel", sBsPower);
            if (miss.Length > 0) { AppliedLog().Add("bodysculptorweight: not found: " + miss); return; }
            Patch(sBsShape, "Run", Type.EmptyTypes, typeof(BEp3BsHook), "RunHook", "RunOrig");
            Patch(typeof(SimDescription), "SetBodyShape", new Type[] { typeof(float), typeof(float) }, typeof(BEp3BsDescHook), "SetHook", "SetOrig");
        }

        static float BsFix(SimDescription d, object inst, float weight)
        {
            if ((bool)sBsFailure.GetValue(inst)) return weight;
            InteractionInstance ii = (InteractionInstance)inst;
            int option = Convert.ToInt32(sBsOption.GetValue(ii.InteractionDefinition));
            BodySculptor t = ii.Target as BodySculptor;
            if (option == kBsOriginal || t == null) return weight;
            int pl = Convert.ToInt32(sBsPower.GetValue(t));
            float[] pct = BodySculptor.kPowerLevelPercentages;
            if (pct == null || pl < 0 || pl >= pct.Length) return weight;
            bool on = ShortcutOn("bodysculptorweight");
            if (Math.Abs(BsWeight(false, d.Weight, option, pct[pl]) - weight) > 0.001f) return weight;  // not EA's value: nothing to do, either way
            float w = BsWeight(on, d.Weight, option, pct[pl]);
            if (Math.Abs(w - weight) >= 0.0001f) { PatchCounters()[3944]++; return w; }
            if (!on && Math.Abs(BsWeight(true, d.Weight, option, pct[pl]) - weight) >= 0.0001f) PatchCounters()[3945]++;
            return weight;
        }

        public class BEp3BsHook
        {
            public bool RunHook()
            {
                SimDescription d = null;
                try
                {
                    Sim a = ((InteractionInstance)(object)this).InstanceActor;
                    d = a == null ? null : a.SimDescription;
                    if (d != null) sBsActive[d] = this;
                }
                catch (Exception) { PatchCounters()[3946]++; }
                try { return RunOrig(); }
                finally { if (d != null) sBsActive.Remove(d); }
            }
            public bool RunOrig() { return false; } // BodySculptor+ChangeShape.Run
        }

        public class BEp3BsDescHook
        {
            public void SetHook(float weight, float fitness)
            {
                if (sBsActive.Count > 0)
                {
                    SimDescription d = (SimDescription)(object)this;
                    object inst;
                    if (sBsActive.TryGetValue(d, out inst))
                    {
                        sBsActive.Remove(d);  // only the Run's own SetBodyShape call
                        try { weight = BsFix(d, inst, weight); }
                        catch (Exception) { PatchCounters()[3946]++; }
                    }
                }
                SetOrig(weight, fitness);
            }
            public void SetOrig(float weight, float fitness) { } // SimDescription.SetBodyShape
        }

        // ---- EP3 §13 nectarconsignvalue (tuning kNectarConsignValue) --------------------------------------------------
        // [verified, decompiled + shipped consignment table: rows 45,55,30,60 / 45,55,60,120 / 90,110,80,120] The
        // "NectarBottle" case of ConsignmentRegister+ConsignmentRegisterData.CreateObjectInitParameters (Ambitions shop
        // stock) rolls the value as GetInt(p[2], p[2]) (copy-paste of the age roll), so every stocked nectar bottle has the
        // minimum value. Fix: the value is rolled from p[2]..p[3]; the age roll is EA's. Household-visible (low).
        // RE, not fixed (no trigger): the PotionShop and BotShop data have the same line but ship no nectar rows; CreateObject
        // calls AppreciateDepreciateDailyUpdate before checking the created object for null.
        // Counters: [3947] values re-rolled, [3948] minimum values with the switch off, [3949] errors.
        static readonly Type sNvData = typeof(ConsignmentRegister).GetNestedType("ConsignmentRegisterData", BindingFlags.NonPublic | BindingFlags.Public);
        static readonly FieldInfo sNvParams = sNvData == null ? null : sNvData.GetField("mParameters", kInstAll);

        // Pure: the top of the value roll (EA: the bottom).
        internal static int NvMax(bool on, int p2, int p3) { return on && p3 > p2 ? p3 : p2; }

        static void ApplyNectarConsignValue()
        {
            string miss = EpMiss("ConsignmentRegister+ConsignmentRegisterData", sNvData, "mParameters", sNvParams);
            if (miss.Length > 0) { AppliedLog().Add("nectarconsignvalue: not found: " + miss); return; }
            Patch(sNvData, "CreateObjectInitParameters", new Type[] { typeof(Simulator.ObjectInitParameters).MakeByRefType() }, typeof(BEp3NvHook), "IpHook", "IpOrig");
        }

        public class BEp3NvHook
        {
            public bool IpHook(out Simulator.ObjectInitParameters initParameters)
            {
                bool r = IpOrig(out initParameters);
                try
                {
                    TreasureComponent.NectarBottleTreasureComponentObjectInitParams np = initParameters as TreasureComponent.NectarBottleTreasureComponentObjectInitParams;
                    if (!r || np == null) return r;
                    string[] p = sNvParams.GetValue(this) as string[];
                    if (p == null || p.Length < 4) return r;
                    bool on = ShortcutOn("nectarconsignvalue");
                    int lo = ParserFunctions.ParseInt(p[2], 0), hi = ParserFunctions.ParseInt(p[3], 0);
                    int top = NvMax(on, lo, hi);
                    if (top != lo) { np.Value = RandomUtil.GetInt(lo, top); PatchCounters()[3947]++; }
                    else if (!on && NvMax(true, lo, hi) != lo) PatchCounters()[3948]++;
                }
                catch (Exception) { PatchCounters()[3949]++; }
                return r;
            }
            public bool IpOrig(out Simulator.ObjectInitParameters initParameters) { initParameters = null; return false; } // ConsignmentRegisterData.CreateObjectInitParameters
        }

        // ---- EP3 §14 straygnometns (tuning kStrayGnomeTns) ------------------------------------------------------------
        // [verified, decompiled] StrayWildlifeGnome+DisallowStrayWildlife.Run (Pets) calls SetAllowStrays(false) before it
        // sets CurrentDisallowType, and SetAllowStrays shows the TNS from CurrentDisallowType, so the notice names the
        // previous choice ("No deer" after picking raccoons). RE: the loop over the lot's other wildlife gnomes repeats
        // SetAllowStrays, so each gnome shows its own copy of the notice. Fix: the new type is set on every gnome before
        // EA's Run, and the disallow notice shows once per Run. Household-visible (cosmetic).
        // Counters: [3950] runs with the right notice, [3951] stale notices with the switch off, [3952] errors.
        static readonly Type sGnRun = typeof(StrayWildlifeGnome).GetNestedType("DisallowStrayWildlife", BindingFlags.NonPublic | BindingFlags.Public);
        static readonly Type sGnDef = sGnRun == null ? null : sGnRun.GetNestedType("Definition", BindingFlags.NonPublic | BindingFlags.Public);
        static readonly FieldInfo sGnType = sGnDef == null ? null : sGnDef.GetField("mDisallowType", kInstAll);
        static bool sGnRunning;
        static int sGnLeft;

        // Pure: the disallow type the notice names, and how many notices one Run shows for this many gnomes.
        internal static int GnTnsType(bool on, int oldType, int newType) { return on ? newType : oldType; }
        internal static int GnTnsCount(bool on, int gnomes) { return on && gnomes > 0 ? 1 : gnomes; }

        static void ApplyStrayGnomeTns()
        {
            string miss = EpMiss("StrayWildlifeGnome+DisallowStrayWildlife", sGnRun, "Definition.mDisallowType", sGnType);
            if (miss.Length > 0) { AppliedLog().Add("straygnometns: not found: " + miss); return; }
            Patch(sGnRun, "Run", Type.EmptyTypes, typeof(BEp3GnHook), "RunHook", "RunOrig");
            Patch(typeof(StrayWildlifeGnome), "ShowTns", new Type[] { typeof(bool) }, typeof(BEp3GnTnsHook), "TnsHook", "TnsOrig");
        }

        public class BEp3GnHook
        {
            public bool RunHook()
            {
                bool active = false;
                try
                {
                    bool on = ShortcutOn("straygnometns");
                    InteractionInstance ii = (InteractionInstance)(object)this;
                    StrayWildlifeGnome t = ii.Target as StrayWildlifeGnome;
                    object def = ii.InteractionDefinition;
                    if (t != null && def != null && sGnDef.IsInstanceOfType(def))
                    {
                        int want = (int)(StrayWildlifeGnome.DisallowType)sGnType.GetValue(def);
                        // EA's Run: SetAllowStrays(false) (the notice) on the target, then on each other gnome on the home lot.
                        List<StrayWildlifeGnome> gnomes = new List<StrayWildlifeGnome>();
                        gnomes.Add(t);
                        foreach (StrayGnome g in t.GetAllStrayGnomesOfThisType())
                        {
                            StrayWildlifeGnome w = g as StrayWildlifeGnome;
                            if (w != null) gnomes.Add(w);
                        }
                        bool retyped = false, stale = false;
                        foreach (StrayWildlifeGnome w in gnomes)
                        {
                            int cur = (int)w.CurrentDisallowType, named = GnTnsType(on, cur, want);
                            if (named != cur) { w.CurrentDisallowType = (StrayWildlifeGnome.DisallowType)named; retyped = true; }
                            else if (GnTnsType(true, cur, want) != cur) stale = true;
                        }
                        int limit = GnTnsCount(on, gnomes.Count);
                        if (retyped || limit < gnomes.Count) PatchCounters()[3950]++;
                        else if (!on && (stale || GnTnsCount(true, gnomes.Count) < gnomes.Count)) PatchCounters()[3951]++;
                        if (limit < gnomes.Count) { active = true; sGnLeft = limit; }
                    }
                }
                catch (Exception) { PatchCounters()[3952]++; }
                if (!active) return RunOrig();
                sGnRunning = true;
                try { return RunOrig(); }
                finally { sGnRunning = false; }
            }
            public bool RunOrig() { return false; } // StrayWildlifeGnome+DisallowStrayWildlife.Run
        }

        public class BEp3GnTnsHook
        {
            public void TnsHook(bool allowed)
            {
                if (sGnRunning && !allowed)
                {
                    if (sGnLeft <= 0) return;  // the Run's repeat notices
                    sGnLeft--;
                }
                TnsOrig(allowed);
            }
            public void TnsOrig(bool allowed) { } // StrayWildlifeGnome.ShowTns
        }
    }
}
