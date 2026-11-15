using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects.HobbiesSkills;
using Sims3.Gameplay.Objects.Register;
using Sims3.Gameplay.Objects.Vehicles;
using Sims3.Gameplay.Skills;
using Sims3.Gameplay.Utilities;

namespace Ts3Fixes
{
    // EP batch 2 (docs/587 §7; leads docs/583 §7.1 G1 and §7.3 C1-C2, plus docs/587 §14b). Every switch off by default;
    // none of these methods is changed by simler90 UPDATE242. Checks: EpBatch2Checks.cs. Slots 1466-1477 (block fixes-1400).
    public static partial class Fixes
    {
        // ---- EP2 §1 consignmentlastday (tuning kConsignmentLastDay) ---------------------------------------------------
        // [verified, decompiled + shipped tuning: kConsignmentLifespan 5, kConsignmentPrices 5 entries in all three
        // registers] ConsignmentRegister (Ambitions), PotionShopConsignmentRegister (Supernatural) and BotShopRegister
        // (Into the Future) OnDailyAlarm: Age++ comes before the sale roll and the return test is `Age > lifespan`, so a
        // sale on day 5 reads kConsignmentPrices[5] and a sale roll on day 6 reads [6]: IndexOutOfRangeException out of
        // the daily alarm. That aborts the day for every later owner (sold items already removed from the list are never
        // paid or reported) and the item stays consigned. Fix: the day-5 price is the last entry, and an item past its
        // lifespan does not roll a sale, so EA's own return branch mails it home that day (DaysRemaining reaches 0 on day 5).
        // Household-visible: consigned items sell on the last day and come back after it.
        // RE pass, not fixed (no trigger seen): an item whose object was deleted while consigned throws in the same loop.
        // Counters: [1466] last-day prices / day-6 returns handled, [1467] would throw with the switch off, [1468] errors.
        static readonly Type[] sCgTypes = { typeof(ConsignmentRegister), typeof(PotionShopConsignmentRegister), typeof(BotShopRegister) };
        static readonly Type[] sCgObj = new Type[3];
        static readonly PropertyInfo[] sCgAge = new PropertyInfo[3];
        static readonly FieldInfo[] sCgPrices = new FieldInfo[3], sCgLife = new FieldInfo[3];
        static string sCgMissing = "";

        // Pure: price index used for a sale at this age (EA: age itself).
        internal static int CgPriceIndex(bool on, int age, int prices) { return on && age >= prices ? prices - 1 : age; }
        // Pure: does an item of this age roll a sale?
        internal static bool CgRolls(bool on, int age, int lifespan) { return !on || age <= lifespan; }

        static void ApplyConsignmentLastDay()
        {
            System.Text.StringBuilder miss = new System.Text.StringBuilder();
            for (int i = 0; i < 3; i++)
            {
                Type t = sCgTypes[i];
                sCgObj[i] = t.GetNestedType("ConsignedObject", BindingFlags.NonPublic | BindingFlags.Public);
                sCgAge[i] = sCgObj[i] == null ? null : sCgObj[i].GetProperty("Age", kInstAll);
                sCgPrices[i] = t.GetField("kConsignmentPrices", kEpStatic);
                sCgLife[i] = t.GetField("kConsignmentLifespan", kEpStatic);
                if (sCgAge[i] == null || sCgPrices[i] == null || sCgLife[i] == null) miss.Append(miss.Length == 0 ? "" : ", ").Append(t.Name);
            }
            sCgMissing = miss.ToString();
            if (sCgMissing.Length > 0) { AppliedLog().Add("consignmentlastday: not found: " + sCgMissing); return; }
            Patch(typeof(ConsignmentRegister), "GetValueOfSale", new Type[] { sCgObj[0], typeof(SimDescription) }, typeof(BEp2Cg0), "ValueHook", "ValueOrig");
            Patch(typeof(ConsignmentRegister), "GetChanceOfSale", new Type[] { typeof(Sim), sCgObj[0] }, typeof(BEp2Cg0), "ChanceHook", "ChanceOrig");
            Patch(typeof(PotionShopConsignmentRegister), "GetValueOfSale", new Type[] { sCgObj[1], typeof(SimDescription) }, typeof(BEp2Cg1), "ValueHook", "ValueOrig");
            Patch(typeof(PotionShopConsignmentRegister), "GetChanceOfSale", new Type[] { typeof(Sim), sCgObj[1] }, typeof(BEp2Cg1), "ChanceHook", "ChanceOrig");
            Patch(typeof(BotShopRegister), "GetValueOfSale", new Type[] { sCgObj[2], typeof(SimDescription) }, typeof(BEp2Cg2), "ValueHook", "ValueOrig");
            Patch(typeof(BotShopRegister), "GetChanceOfSale", new Type[] { typeof(Sim), sCgObj[2] }, typeof(BEp2Cg2), "ChanceHook", "ChanceOrig");
        }

        public delegate int CgValueFn(object obj, SimDescription desc);
        public delegate float CgChanceFn(Sim actor, object obj);

        static int CgValue(int i, object obj, SimDescription desc, CgValueFn orig)
        {
            int age, n;
            try
            {
                age = (int)sCgAge[i].GetValue(obj, null);
                n = ((float[])sCgPrices[i].GetValue(null)).Length;
            }
            catch (Exception) { PatchCounters()[1468]++; return orig(obj, desc); }
            if (age < n || n == 0) return orig(obj, desc);
            if (!ShortcutOn("consignmentlastday")) { PatchCounters()[1467]++; return orig(obj, desc); }
            PatchCounters()[1466]++;
            sCgAge[i].SetValue(obj, CgPriceIndex(true, age, n), null);
            try { return orig(obj, desc); }
            finally { sCgAge[i].SetValue(obj, age, null); }
        }

        static float CgChance(int i, Sim actor, object obj, CgChanceFn orig)
        {
            try
            {
                int age = (int)sCgAge[i].GetValue(obj, null), life = (int)sCgLife[i].GetValue(null);
                if (age > life && ShortcutOn("consignmentlastday") && !CgRolls(true, age, life)) { PatchCounters()[1466]++; return 0f; }
            }
            catch (Exception) { PatchCounters()[1468]++; }
            return orig(actor, obj);
        }

        public class BEp2Cg0
        {
            public static int ValueHook(object obj, SimDescription desc) { return CgValue(0, obj, desc, ValueOrig); }
            public static int ValueOrig(object obj, SimDescription desc) { return 0; } // ConsignmentRegister.GetValueOfSale
            public static float ChanceHook(Sim actor, object obj) { return CgChance(0, actor, obj, ChanceOrig); }
            public static float ChanceOrig(Sim actor, object obj) { return 0f; } // ConsignmentRegister.GetChanceOfSale
        }
        public class BEp2Cg1
        {
            public static int ValueHook(object obj, SimDescription desc) { return CgValue(1, obj, desc, ValueOrig); }
            public static int ValueOrig(object obj, SimDescription desc) { return 0; } // PotionShopConsignmentRegister.GetValueOfSale
            public static float ChanceHook(Sim actor, object obj) { return CgChance(1, actor, obj, ChanceOrig); }
            public static float ChanceOrig(Sim actor, object obj) { return 0f; } // PotionShopConsignmentRegister.GetChanceOfSale
        }
        public class BEp2Cg2
        {
            public static int ValueHook(object obj, SimDescription desc) { return CgValue(2, obj, desc, ValueOrig); }
            public static int ValueOrig(object obj, SimDescription desc) { return 0; } // BotShopRegister.GetValueOfSale
            public static float ChanceHook(Sim actor, object obj) { return CgChance(2, actor, obj, ChanceOrig); }
            public static float ChanceOrig(Sim actor, object obj) { return 0f; } // BotShopRegister.GetChanceOfSale
        }

        // ---- EP2 §2 bartendinghourlypay (tuning kBartendingHourlyPay) -------------------------------------------------
        // [verified, decompiled + shipped bar data: HourlyPay 5-100 at 51 of 53 bar types] Bartending.OnStopBartending
        // computes hours x HourlyPay, adds it to the lifetime and session totals, sends kMadeMoneyBartending and shows the
        // "money made this session" notice, but nothing pays it: no ModifyFunds there, at its only caller
        // (BarAdvanced+StopTending.Run) or in any listener; only tips are paid (at the serve site). Fix: the hourly pay
        // EA added to the totals is paid to the bartender's household (SimDescription.ModifyFunds, which skips NPC
        // roommates and Sims without a household). Household-visible: a player bartender gets the wage the notice shows.
        // RE pass, not fixed: a shift that ends without StopTending (reset, posture pop) never reaches OnStopBartending,
        // so its tips roll into the next session's notice.
        // Counters: [1469] wages paid, [1470] wages unpaid with the switch off, [1471] errors.

        // Pure: money paid on stop for this hourly amount.
        internal static int BhPaid(bool on, int hourly) { return on && hourly > 0 ? hourly : 0; }

        static void ApplyBartendingHourlyPay()
        {
            Patch(typeof(Bartending), "OnStopBartending", new Type[] { typeof(Lot), typeof(DateAndTime) }, typeof(BEp2BhHook), "StopHook", "StopOrig");
        }

        public class BEp2BhHook
        {
            public void StopHook(Lot lot, DateAndTime timePostureStarted)
            {
                Bartending b = (Bartending)(object)this;
                int before = b.AmountOfMoneyMadeBartending;
                StopOrig(lot, timePostureStarted);
                try
                {
                    int hourly = b.AmountOfMoneyMadeBartending - before;
                    if (hourly <= 0) return;
                    if (!ShortcutOn("bartendinghourlypay")) { PatchCounters()[1470]++; return; }
                    SimDescription sd = b.SkillOwner;
                    if (sd == null) return;
                    sd.ModifyFunds(BhPaid(true, hourly));
                    PatchCounters()[1469]++;
                }
                catch (Exception) { PatchCounters()[1471]++; }
            }
            public void StopOrig(Lot lot, DateAndTime timePostureStarted) { } // Bartending.OnStopBartending
        }

        // ---- EP2 §3 influencefollowerbonus (tuning kInfluenceFollowerBonus) -------------------------------------------
        // [verified, decompiled + shipped tuning kFollowerEffectInfluence 500; intent inferred] SocialNetworkingSkill
        // .ModifierForInfluenceSkills divides the uint follower count by the int divisor in integer arithmetic and then
        // clamps the result as a float to [-1, 2]: the social-group influence skill-gain bonus is 0 below 500 followers,
        // then exactly 1, then 2 from 1,000. The float clamp and the sibling ModifierForProtestingAttendanceScore (double
        // division, rounded) say a scaled value was meant. Fix: after EA's call, each modifier it applied is moved to the
        // float quotient (same clamp) and the stored value updated, so EA's RemoveOldModifiers takes back exactly what was
        // added. Household-visible (influence skill gain for blogging students). Docs/583 §7.1 G1.
        // Counters: [1472] modifiers scaled, [1473] stepped with the switch off, [1474] errors.
        static readonly FieldInfo[] sIfFields = {
            typeof(SocialNetworkingSkill).GetField("mInfluenceNerdSkillModifier", kInstAll),
            typeof(SocialNetworkingSkill).GetField("mInfluenceRebelSkillModifier", kInstAll),
            typeof(SocialNetworkingSkill).GetField("mInfluenceSocialiteSkillModifier", kInstAll) };
        static readonly SkillNames[] sIfSkills = { SkillNames.InfluenceNerd, SkillNames.InfluenceRebel, SkillNames.InfluenceSocialite };

        // Pure: the modifier value (EA: integer quotient).
        internal static float IfValue(bool on, uint followers, int divisor)
        {
            if (divisor == 0) return 0f;
            float v = on ? followers / (float)divisor : (float)(followers / divisor);
            return v < -1f ? -1f : v > 2f ? 2f : v;
        }

        static void ApplyInfluenceFollowerBonus()
        {
            if (sIfFields[0] == null || sIfFields[1] == null || sIfFields[2] == null) { AppliedLog().Add("influencefollowerbonus: not found: SocialNetworkingSkill.mInfluence*SkillModifier"); return; }
            Patch(typeof(SocialNetworkingSkill), "ModifierForInfluenceSkills", new Type[] { typeof(uint) }, typeof(BEp2IfHook), "ModHook", "ModOrig");
        }

        public class BEp2IfHook
        {
            public void ModHook(uint followers)
            {
                ModOrig(followers);
                try
                {
                    SocialNetworkingSkill s = (SocialNetworkingSkill)(object)this;
                    Sim sim = s.SkillOwner == null ? null : s.SkillOwner.CreatedSim;
                    if (sim == null || sim.SkillManager == null) return;
                    int k = SocialNetworkingSkill.kFollowerEffectInfluence;
                    float ea = IfValue(false, followers, k), fix = IfValue(true, followers, k);
                    if (ea == fix) return;
                    for (int i = 0; i < 3; i++)
                    {
                        float cur = (float)sIfFields[i].GetValue(s);
                        if (float.IsNaN(cur) || !sim.SkillManager.HasElement(sIfSkills[i])) continue;  // EA applied nothing here
                        if (!ShortcutOn("influencefollowerbonus")) { PatchCounters()[1473]++; continue; }
                        sim.SkillManager.ChangeSkillGainModifier(sIfSkills[i], fix - cur);
                        sIfFields[i].SetValue(s, fix);
                        PatchCounters()[1472]++;
                    }
                }
                catch (Exception) { PatchCounters()[1474]++; }
            }
            public void ModOrig(uint followers) { } // SocialNetworkingSkill.ModifierForInfluenceSkills
        }

        // ---- EP2 §4 broomarenachoice (tuning kBroomArenaChoice) -------------------------------------------------------
        // [verified, decompiled; docs/587 §14b] Riding a broom in the arena from the broom's own menu (MagicBroom, three
        // sites) creates MagicBroomArena+RideBroomArena and calls SetMagicBroom(broom, fromInventory), which writes the
        // arena's shared mBroom / mFromInventory at push time. Run then sets Target.mBroom = null and AfterRouteRun sets
        // mFromInventory = false, so the arena always uses GetMagicBroomToUse's best broom, not the one the player picked.
        // RE pass: the push-time write also changes the broom of a ride already in progress on the arena (its Cleanup and
        // park step read Target.mBroom). Fix: SetMagicBroom keeps the choice with the interaction instead of the arena;
        // when that ride reaches its broom pick, GetMagicBroomToUse returns the chosen broom if it is still usable (in the
        // rider's inventory, or in the world), else EA's pick. Household-visible.
        // Counters: [1475] chosen brooms used, [1476] choices dropped with the switch off, [1477] errors.
        static readonly Type sBaRide = typeof(MagicBroomArena).GetNestedType("RideBroomArena", BindingFlags.NonPublic | BindingFlags.Public);
        static readonly Dictionary<object, BaChoice> sBaChoices = new Dictionary<object, BaChoice>();
        static BaChoice sBaPending;

        public class BaChoice { public Sim Actor; public MagicBroom Broom; public bool FromInventory; }

        // Pure: does the ride use the player's broom?
        internal static bool BaUsesChoice(bool on, bool chosen, bool usable) { return on && chosen && usable; }

        static void ApplyBroomArenaChoice()
        {
            if (sBaRide == null) { AppliedLog().Add("broomarenachoice: not found: MagicBroomArena+RideBroomArena"); return; }
            Patch(sBaRide, "SetMagicBroom", new Type[] { typeof(MagicBroom), typeof(bool) }, typeof(BEp2BaHook), "SetHook", "SetOrig");
            Patch(sBaRide, "AfterRouteRun", Type.EmptyTypes, typeof(BEp2BaHook), "AfterHook", "AfterOrig");
            Patch(typeof(MagicBroomArena), "GetMagicBroomToUse", new Type[] { typeof(Sim), typeof(bool).MakeByRefType() }, typeof(BEp2BaArenaHook), "PickHook", "PickOrig");
        }

        public class BEp2BaHook
        {
            public void SetHook(MagicBroom broom, bool fromInventory)
            {
                if (!ShortcutOn("broomarenachoice")) { if (broom != null) PatchCounters()[1476]++; SetOrig(broom, fromInventory); return; }
                try
                {
                    if (sBaChoices.Count >= 8) sBaChoices.Clear();  // rides that never started (cancelled in the queue)
                    BaChoice c = new BaChoice();
                    c.Actor = ((InteractionInstance)(object)this).InstanceActor; c.Broom = broom; c.FromInventory = fromInventory;
                    sBaChoices[this] = c;
                }
                catch (Exception) { PatchCounters()[1477]++; SetOrig(broom, fromInventory); }
            }
            public void SetOrig(MagicBroom broom, bool fromInventory) { } // RideBroomArena.SetMagicBroom

            public bool AfterHook()
            {
                BaChoice c;
                if (sBaChoices.TryGetValue(this, out c)) { sBaChoices.Remove(this); sBaPending = c; }
                try { return AfterOrig(); }  // EA's broom pick runs before its first yield and consumes sBaPending
                finally { if (sBaPending == c) sBaPending = null; }
            }
            public bool AfterOrig() { return false; } // RideBroomArena.AfterRouteRun
        }

        public class BEp2BaArenaHook
        {
            public MagicBroom PickHook(Sim sim, out bool fromInventory)
            {
                BaChoice c = sBaPending;
                if (c != null && c.Actor == sim)
                {
                    sBaPending = null;
                    try
                    {
                        MagicBroom b = c.Broom;
                        bool usable = b != null && !b.HasBeenDestroyed && (!b.Reserved || b.mReservingSim == sim)
                            && (c.FromInventory ? sim.Inventory != null && sim.Inventory.Contains(b) : b.InWorld);
                        if (BaUsesChoice(ShortcutOn("broomarenachoice"), true, usable)) { PatchCounters()[1475]++; fromInventory = c.FromInventory; return b; }
                    }
                    catch (Exception) { PatchCounters()[1477]++; }
                }
                return PickOrig(sim, out fromInventory);
            }
            public MagicBroom PickOrig(Sim sim, out bool fromInventory) { fromInventory = false; return null; } // MagicBroomArena.GetMagicBroomToUse
        }
    }
}
