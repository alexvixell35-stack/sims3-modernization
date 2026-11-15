using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.ActiveCareer.ActiveCareers;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects;
using Sims3.Gameplay.Objects.Appliances;
using Sims3.Gameplay.Objects.CookingObjects;
using Sims3.Gameplay.Objects.Entertainment;
using Sims3.Gameplay.Objects.Environment;
using Sims3.Gameplay.Objects.FoodObjects;
using Sims3.Gameplay.Objects.Gardening;
using Sims3.Gameplay.Objects.HobbiesSkills;
using Sims3.Gameplay.Objects.Insect;
using Sims3.Gameplay.Objects.Misc;
using Sims3.Gameplay.Objects.Pets;
using Sims3.Gameplay.Skills;
using Sims3.Gameplay.Socializing;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // EP batch 1, part b (docs/587 leads 12-20 plus extras found while re-reading the patched functions; batch EP1).
    // Every switch off by default; none of these methods is changed by simler90 UPDATE242. Checks: EpBatch1Checks.cs.
    public static partial class Fixes
    {
        // ---- EP1 §12 mummycursetrait (tuning kMummyCurseTrait) ---------------------------------------------------------
        // [verified, decompiled] BuffMummysCurse.OnTimeout (a cursed non-Evil Sim without Good) removes
        // TraitManager.GetRandomElement(), which can be a hidden or occult trait, before it adds Evil; every sibling that
        // swaps a trait uses GetRandomVisibleElement(). With a hidden trait removed a full visible list stays full, so
        // AddElement(Evil) fails too. Fix: the visible pick; otherwise EA's code (Good removed first, death unchanged).
        // Household-visible (cursed household Sims). Counters: [1430] visible picks, [1431] any-trait picks with the switch off, [1432] errors.

        // Pure: may the removed trait be hidden?
        internal static bool McMayRemoveHidden(bool on, bool evil, bool good) { return !on && !evil && !good; }

        static void ApplyMummyCurseTrait()
        {
            Patch(typeof(BuffMummysCurse), "OnTimeout", new Type[] { typeof(BuffManager), typeof(BuffInstance), typeof(Buff.OnTimeoutReasons) }, typeof(BMcHook), "TimeoutHook", "TimeoutOrig");
        }

        public class BMcHook
        {
            public void TimeoutHook(BuffManager bm, BuffInstance bi, Buff.OnTimeoutReasons reason)
            {
                Sim actor = bm == null ? null : bm.Actor;
                TraitManager tm = actor == null ? null : actor.TraitManager;
                if (tm == null || tm.HasElement(TraitNames.Evil) || tm.HasElement(TraitNames.Good)) { TimeoutOrig(bm, bi, reason); return; }
                if (!ShortcutOn("mummycursetrait")) { PatchCounters()[1431]++; TimeoutOrig(bm, bi, reason); return; }
                try
                {
                    Trait t = tm.GetRandomVisibleElement();
                    if (t != null) tm.RemoveElement(t.Guid);
                    tm.AddElement(TraitNames.Evil);
                    PatchCounters()[1430]++;
                }
                catch (Exception) { PatchCounters()[1432]++; }
                if (actor.CanBeKilled()) actor.Kill(SimDescription.DeathType.MummyCurse);
            }
            public void TimeoutOrig(BuffManager bm, BuffInstance bi, Buff.OnTimeoutReasons reason) { } // BuffMummysCurse.OnTimeout
        }

        // ---- EP1 §13 bullridercleanup (tuning kBullRiderCleanup) -------------------------------------------------------
        // [verified, decompiled] MechanicalBull+RideBull.Cleanup sets Target.mCurrentRider = null for every RideBull that
        // ends, including a second Sim's ride that was cancelled or dequeued while the first Sim is still on the bull.
        // Watchers then stop (WatchBull ends on a null rider) and Test lets more rides queue on a bull in use. Fix: when
        // the recorded rider is another Sim whose current interaction is still a RideBull on this bull, the rider is kept.
        // Household-visible. Counters: [1433] riders kept, [1434] riders cleared under a rider with the switch off, [1435] errors.
        static readonly Type sBrRide = typeof(MechanicalBull).GetNestedType("RideBull", BindingFlags.NonPublic | BindingFlags.Public);
        static readonly FieldInfo sBrRider = typeof(MechanicalBull).GetField("mCurrentRider", kInstAll);

        // Pure: keep the recorded rider after this Cleanup?
        internal static bool BrKeep(bool on, bool riderIsOther, bool riderStillRiding) { return on && riderIsOther && riderStillRiding; }

        static void ApplyBullRiderCleanup()
        {
            if (sBrRide == null || sBrRider == null) { AppliedLog().Add("bullridercleanup: not found: MechanicalBull+RideBull/mCurrentRider"); return; }
            Patch(sBrRide, "Cleanup", Type.EmptyTypes, typeof(BBrHook), "CleanupHook", "CleanupOrig");
        }

        public class BBrHook
        {
            public void CleanupHook()
            {
                MechanicalBull bull = null; Sim rider = null; bool riding = false;
                try
                {
                    InteractionInstance me = (InteractionInstance)(object)this;
                    bull = me.Target as MechanicalBull;
                    rider = bull == null ? null : sBrRider.GetValue(bull) as Sim;
                    if (rider != null && rider != me.InstanceActor)
                    {
                        InteractionInstance cur = rider.CurrentInteraction;
                        riding = cur != null && cur.GetType() == sBrRide && cur.Target == bull;
                    }
                }
                catch (Exception) { PatchCounters()[1435]++; rider = null; }
                CleanupOrig();
                if (rider == null || !riding) return;
                if (!ShortcutOn("bullridercleanup")) { PatchCounters()[1434]++; return; }
                try { if (BrKeep(true, true, true)) { sBrRider.SetValue(bull, rider); PatchCounters()[1433]++; } }
                catch (Exception) { PatchCounters()[1435]++; }
            }
            public void CleanupOrig() { } // MechanicalBull+RideBull.Cleanup
        }

        // ---- EP1 §14 broomtipperformer (tuning kBroomTipPerformer) -----------------------------------------------------
        // [verified, decompiled] MagicBroomArena+WatchStuntRide: after RouteToTipJar the watcher reads mCurrentPerformer,
        // which the ride's Cleanup nulls when the ride ends during the route; the tip animation plays, a selectable watcher
        // pays, then `mCurrentPerformer.IsSelectable` throws. Fix: RouteToTipJar reports failure when the performer is gone
        // after the route, so the watcher skips the tip (EA's own route-failed path). Household-visible: no lost tip money,
        // no script error. Parked (docs/587 §14b, low): RideBroomArena.Run replaces the broom the player picked.
        // Counters: [1436] tips skipped with no performer, [1437] routes ending without one with the switch off, [1438] errors.
        static readonly FieldInfo sBtPerformer = typeof(MagicBroomArena).GetField("mCurrentPerformer", kInstAll);

        // Pure: does the watcher go on to tip (and EA throw) after the route?
        internal static bool BtTips(bool on, bool routed, bool performer) { return routed && (performer || !on); }

        static void ApplyBroomTipPerformer()
        {
            if (sBtPerformer == null) { AppliedLog().Add("broomtipperformer: not found: MagicBroomArena.mCurrentPerformer"); return; }
            Patch(typeof(MagicBroomArena), "RouteToTipJar", new Type[] { typeof(Sim) }, typeof(BBtHook), "RouteHook", "RouteOrig");
        }

        public class BBtHook
        {
            public bool RouteHook(Sim sim)
            {
                bool routed = RouteOrig(sim);
                if (!routed) return false;
                try
                {
                    if (sBtPerformer.GetValue(this) != null) return true;
                    if (!ShortcutOn("broomtipperformer")) { PatchCounters()[1437]++; return true; }
                    PatchCounters()[1436]++;
                    return BtTips(true, true, false);
                }
                catch (Exception) { PatchCounters()[1438]++; return true; }
            }
            public bool RouteOrig(Sim sim) { return false; } // MagicBroomArena.RouteToTipJar
        }

        // ---- EP1 §15 balancingballcareer (tuning kBalancingBallCareer) -------------------------------------------------
        // [verified code, reach inferred] BalancingBall+BalanceBall.Run dereferences `Occupation as PerformanceArtistCareer`
        // (EpicFail and Success branches, unguarded) and OccupationAsPerformanceCareer, but its Test is only
        // CommonStageTest; AddInteractions offers tricks to any performance career (Singer, Magician) at the unlock level,
        // so they hit a NullReferenceException mid-animation. GiantRingOfFire's Test checks the career. Fix: the same check
        // in BalanceBall's Test. Household-visible (the trick is no longer offered to non-acrobats).
        // Counters: [1439] non-acrobats refused, [1440] allowed with the switch off, [1441] errors.
        static readonly Type sBbDef = typeof(BalancingBall.BalanceBall).GetNestedType("Definition", BindingFlags.NonPublic | BindingFlags.Public);

        // Pure: does Test let the Sim start the trick?
        internal static bool BbAllowed(bool on, bool eaTest, bool performanceArtist) { return eaTest && (performanceArtist || !on); }

        static void ApplyBalancingBallCareer()
        {
            if (sBbDef == null) { AppliedLog().Add("balancingballcareer: not found: BalancingBall+BalanceBall+Definition"); return; }
            Patch(sBbDef, "Test", new Type[] { typeof(Sim), typeof(BalancingBall), typeof(bool), typeof(GreyedOutTooltipCallback).MakeByRefType() }, typeof(BBbHook), "TestHook", "TestOrig");
        }

        public class BBbHook
        {
            public bool TestHook(Sim a, BalancingBall target, bool isAutonomous, ref GreyedOutTooltipCallback greyedOutTooltipCallback)
            {
                if (!TestOrig(a, target, isAutonomous, ref greyedOutTooltipCallback)) return false;
                try
                {
                    if (a == null || a.Occupation is PerformanceArtistCareer) return true;
                    if (!ShortcutOn("balancingballcareer")) { PatchCounters()[1440]++; return true; }
                    PatchCounters()[1439]++;
                    return BbAllowed(true, true, false);
                }
                catch (Exception) { PatchCounters()[1441]++; return true; }
            }
            public bool TestOrig(Sim a, BalancingBall target, bool isAutonomous, ref GreyedOutTooltipCallback greyedOutTooltipCallback) { return false; } // BalanceBall.Definition.Test
        }

        // ---- EP1 §16 elvisgnomeguard + elvisgnomecap (tunings kElvisGnomeGuard, kElvisGnomeCap) ------------------------
        // MagicGnomeElvis.TryToCreateElvisGnome runs from a daily alarm (PostWorldInit, EP6).
        // elvisgnomeguard [verified, decompiled; parts found by the RE pass marked RE]:
        //   - no ActiveActor / no home lot (no household selected): the alarm, PlayElvisMusic and StopElvisMusic throw;
        //   - RE: TryToCreateElvisGnomeNearFPBB reads plate.CookingProcess.Recipe for every plate; a plate without a cooking
        //     process throws and ends the night's roll;
        //   - SpawnElvis(position, forward) leaves the out-of-world gnome behind when FindGoodLocationNearby fails and then
        //     creates a second one on the lot;
        //   - RE: PlayElvisMusic starts a new looping sound and a new stop alarm on the same gnome for every Sim on the lot,
        //     overwriting the handle, so all but the last loop never stop; StopElvisMusic looks on the home lot while the
        //     music plays on the current lot, so music started while visiting never stops.
        //   Fix: guards; the failed gnome destroyed; one sound and one stop alarm per play (the earlier sound stopped);
        //   StopElvisMusic stops every Elvis gnome's sound. Household-visible (gnome and music).
        // elvisgnomecap [verified code, intent inferred]: no cap, so a level 7+ Singer (20% a night) or a Fried PB&B plate
        //   (10%) keeps adding gnomes to the home lot; MagicGnomeVampire's spawn stops when one is on the lot. Fix: the same
        //   one-per-lot rule. Household-visible; a separate switch because the intent is inferred.
        // Counters: [1442] guarded throws / leaks, [1443] seen with the switch off, [1444] errors;
        //           [1445] spawns skipped (gnome present), [1446] spawn rolls on a lot with a gnome with the switch off, [1447] errors.
        static readonly FieldInfo sEgChanceSandwich = typeof(MagicGnomeElvis).GetField("kChanceGnomeElvisSpawnsSandwich", kEpStatic);
        static readonly FieldInfo sEgPlaysFor = typeof(MagicGnomeElvis).GetField("kMusicPlaysFor", kEpStatic);
        static readonly FieldInfo sEgHandle = typeof(MagicGnomeElvis).GetField("mElvisMusicHandle", kInstAll);
        static readonly MethodInfo sEgSpawnAt = typeof(MagicGnomeElvis).GetMethod("SpawnElvis", kEpStatic, null, new Type[] { typeof(Vector3), typeof(Vector3) }, null);
        static readonly MethodInfo sEgSpawnLot = typeof(MagicGnomeElvis).GetMethod("SpawnElvis", kEpStatic, null, Type.EmptyTypes, null);
        static AlarmHandle sEgStopAlarm = AlarmHandle.kInvalidHandle;

        // Pure: does the nightly spawn run? (EA: always, and throws without a home lot)
        internal static bool EgSpawnRuns(bool guardOn, bool capOn, bool hasHome, int gnomesOnLot)
        {
            if (!hasHome) return !guardOn;
            return gnomesOnLot == 0 || !capOn;
        }
        // Pure: looping sounds left playing after one PlayElvisMusic with n Sims on the lot.
        internal static int EgLoops(bool on, int sims) { return sims <= 0 ? 0 : on ? 1 : sims; }

        static void ApplyElvisGnome()
        {
            if (sEgChanceSandwich == null || sEgPlaysFor == null || sEgHandle == null || sEgSpawnAt == null || sEgSpawnLot == null)
            { AppliedLog().Add("elvisgnomeguard: not found: MagicGnomeElvis members"); return; }
            Patch(typeof(MagicGnomeElvis), "TryToCreateElvisGnome", Type.EmptyTypes, typeof(BEgHook), "CreateHook", "CreateOrig");
            Patch(typeof(MagicGnomeElvis), "TryToCreateElvisGnomeNearFPBB", Type.EmptyTypes, typeof(BEgHook), "FpbbHook", "FpbbOrig");
            Patch(typeof(MagicGnomeElvis), "SpawnElvis", new Type[] { typeof(Vector3), typeof(Vector3) }, typeof(BEgHook), "SpawnHook", "SpawnOrig");
            Patch(typeof(MagicGnomeElvis), "PlayElvisMusic", Type.EmptyTypes, typeof(BEgHook), "PlayHook", "PlayOrig");
            Patch(typeof(MagicGnomeElvis), "StopElvisMusic", Type.EmptyTypes, typeof(BEgHook), "StopHook", "StopOrig");
        }

        public class BEgHook
        {
            public static void CreateHook()
            {
                bool guard = ShortcutOn("elvisgnomeguard"), cap = ShortcutOn("elvisgnomecap");
                int gnomes = 0; bool home = false;
                try
                {
                    Sim a = Sim.ActiveActor;
                    home = a != null && a.LotHome != null;
                    gnomes = home ? a.LotHome.GetObjects<MagicGnomeElvis>().Length : 0;
                }
                catch (Exception) { PatchCounters()[1444]++; CreateOrig(); return; }
                if (!home) { PatchCounters()[guard ? 1442 : 1443]++; }
                else if (gnomes > 0) { PatchCounters()[cap ? 1445 : 1446]++; }
                if (EgSpawnRuns(guard, cap, home, gnomes)) CreateOrig();
            }
            public static void CreateOrig() { } // MagicGnomeElvis.TryToCreateElvisGnome

            public static bool FpbbHook()
            {
                if (!ShortcutOn("elvisgnomeguard")) return FpbbOrig();
                Sim a = Sim.ActiveActor;
                if (a == null || a.LotHome == null) return false;
                Plate plate = null;
                foreach (Plate p in a.LotHome.GetObjects<Plate>())
                {
                    if (p.CookingProcess == null || p.CookingProcess.Recipe == null) { PatchCounters()[1442]++; continue; }  // EA: NullReferenceException
                    if (p.CookingProcess.Recipe.Key == "FriedPeanutButterBanana") plate = p;  // EA keeps the last match
                }
                if (plate != null && RandomUtil.RandomChance((float)sEgChanceSandwich.GetValue(null)))
                {
                    sEgSpawnAt.Invoke(null, new object[] { plate.Position, plate.ForwardVector });
                    return true;
                }
                return false;
            }
            public static bool FpbbOrig() { return false; } // MagicGnomeElvis.TryToCreateElvisGnomeNearFPBB

            public static void SpawnHook(Vector3 objectPosition, Vector3 objectForwardVector)
            {
                if (!ShortcutOn("elvisgnomeguard")) { SpawnOrig(objectPosition, objectForwardVector); return; }
                MagicGnomeElvis g = GlobalFunctions.CreateObjectOutOfWorld("magicGnomeSinger", ProductVersion.EP6) as MagicGnomeElvis;
                if (g == null) return;
                if (GlobalFunctions.FindGoodLocationNearby(g, ref objectPosition, ref objectForwardVector))
                {
                    g.SetPosition(objectPosition);
                    g.SetForward(objectForwardVector);
                    g.AddToWorld();
                    MagicGnomeElvis.PlayElvisMusic();
                    return;
                }
                g.Destroy();  // EA leaves it out of the world
                PatchCounters()[1442]++;
                if (Sim.ActiveActor != null && Sim.ActiveActor.LotHome != null) sEgSpawnLot.Invoke(null, null);
            }
            public static void SpawnOrig(Vector3 objectPosition, Vector3 objectForwardVector) { } // MagicGnomeElvis.SpawnElvis(Vector3, Vector3)

            public static void PlayHook()
            {
                if (!ShortcutOn("elvisgnomeguard"))
                {
                    if (Sim.ActiveActor == null) PatchCounters()[1443]++;
                    PlayOrig();
                    return;
                }
                try
                {
                    Sim aa = Sim.ActiveActor;
                    Lot lot = aa == null ? null : aa.LotCurrent;
                    if (lot == null || lot.LotType != LotType.Residential) return;
                    MagicGnomeElvis[] gnomes = lot.GetObjects<MagicGnomeElvis>();
                    if (gnomes.Length <= 0) return;
                    int sims = 0;
                    foreach (Sim s in lot.GetSims())
                    {
                        if (s.LotCurrent != lot) continue;
                        sims++;
                        s.BuffManager.AddElement(BuffNames.FeelingElvish, Origin.FromElvisGnome);
                    }
                    if (EgLoops(true, sims) == 0) return;
                    if (sims > 1) PatchCounters()[1442]++;
                    ObjectSound old = sEgHandle.GetValue(gnomes[0]) as ObjectSound;
                    if (old != null) old.Stop();
                    ObjectSound snd = new ObjectSound(gnomes[0].ObjectId, "lvis_gnome_music_lp");
                    sEgHandle.SetValue(gnomes[0], snd);
                    snd.Start(true);
                    AlarmManager.Global.RemoveAlarm(sEgStopAlarm);
                    sEgStopAlarm = AlarmManager.Global.AddAlarm((float)sEgPlaysFor.GetValue(null), TimeUnit.Minutes, MagicGnomeElvis.StopElvisMusic, "Turn off music", AlarmType.NeverPersisted, null);
                }
                catch (Exception) { PatchCounters()[1444]++; }
            }
            public static void PlayOrig() { } // MagicGnomeElvis.PlayElvisMusic

            public static void StopHook()
            {
                if (!ShortcutOn("elvisgnomeguard")) { StopOrig(); return; }
                try
                {
                    sEgStopAlarm = AlarmHandle.kInvalidHandle;
                    foreach (MagicGnomeElvis g in Sims3.Gameplay.Queries.GetObjects<MagicGnomeElvis>())
                    {
                        ObjectSound h = sEgHandle.GetValue(g) as ObjectSound;
                        if (h != null) h.Stop();
                    }
                }
                catch (Exception) { PatchCounters()[1444]++; }
            }
            public static void StopOrig() { } // MagicGnomeElvis.StopElvisMusic
        }

        // ---- EP1 §17 cattoypickerrel (tuning kCatToyPickerRel) ---------------------------------------------------------
        // [verified, decompiled + shipped tuning] CatToyWallDancer.GetValidSims admits a Sim with no relationship only when
        // `requiredLtr > 0`, the reverse of the threshold (no relationship counts as liking 0): with the shipped -10, Sims
        // and pets the actor has never met are left out of Play With, and the interaction is hidden when nobody else on the
        // lot is known. Fix: no relationship passes when 0 >= requiredLtr; everything else as EA (order kept).
        // Household-visible. Counters: [1448] strangers added, [1449] left out with the switch off, [1450] errors.
        static readonly MethodInfo sCtValidSim = typeof(CatToyWallDancer).GetMethod("IsValidSim", kEpStatic, null, new Type[] { typeof(Sim) }, null);
        static readonly MethodInfo sCtValidPet = typeof(CatToyWallDancer).GetMethod("IsValidPet", kEpStatic, null, new Type[] { typeof(Sim) }, null);
        static Predicate<Sim> sCtSimOk, sCtPetOk;

        // Pure: is a candidate admitted?
        internal static bool CtAdmit(bool on, bool hasRel, float liking, float requiredLtr)
        {
            if (hasRel) return liking >= requiredLtr;
            return on ? requiredLtr <= 0f : requiredLtr > 0f;
        }

        static void ApplyCatToyPickerRel()
        {
            if (sCtValidSim == null || sCtValidPet == null) { AppliedLog().Add("cattoypickerrel: not found: CatToyWallDancer.IsValidSim/IsValidPet"); return; }
            sCtSimOk = (Predicate<Sim>)Delegate.CreateDelegate(typeof(Predicate<Sim>), sCtValidSim);
            sCtPetOk = (Predicate<Sim>)Delegate.CreateDelegate(typeof(Predicate<Sim>), sCtValidPet);
            Patch(typeof(CatToyWallDancer), "GetValidSims", new Type[] { typeof(Sim), typeof(Lot), typeof(bool), typeof(float) }, typeof(BCtHook), "ValidHook", "ValidOrig");
        }

        public class BCtHook
        {
            public static List<Sim> ValidHook(Sim actor, Lot lot, bool petsOnly, float requiredLtr)
            {
                if (requiredLtr > 0f) return ValidOrig(actor, lot, petsOnly, requiredLtr);  // EA right there
                bool on = ShortcutOn("cattoypickerrel");
                List<Sim> r = null;
                try
                {
                    Predicate<Sim> ok = petsOnly ? sCtPetOk : sCtSimOk;
                    foreach (Sim s in petsOnly ? lot.GetAnimals() : lot.GetSims())
                    {
                        if (s == actor || !ok(s)) continue;
                        Relationship rel = actor.GetRelationship(s, false);
                        bool admit = CtAdmit(on, rel != null, rel == null ? 0f : rel.CurrentLTRLiking, requiredLtr);
                        if (rel == null) PatchCounters()[on ? 1448 : 1449]++;
                        if (!admit) continue;
                        if (r == null) r = new List<Sim>();
                        r.Add(s);
                    }
                }
                catch (Exception) { PatchCounters()[1450]++; return ValidOrig(actor, lot, petsOnly, requiredLtr); }
                return r;
            }
            public static List<Sim> ValidOrig(Sim actor, Lot lot, bool petsOnly, float requiredLtr) { return null; } // CatToyWallDancer.GetValidSims
        }

        // ---- EP1 §18 gourmetpetrecipe (tuning kGourmetPetRecipe) -------------------------------------------------------
        // [verified, decompiled] MakeGourmetFoodForPet.Run: in the horse branch, a Sim already carrying Horse Granola flips a
        // coin to cook a random other pet recipe even when the household has no dogs or cats (the no-granola branch has the
        // `(dogs || cats)` guard). RE pass: when only Horse Granola is available, the dog/cat branches draw from an empty
        // list and throw ArgumentException. Fix: our copy of Run (non-yielding) with the guard and no empty draws.
        // Household-visible (pet food the Sim cooks). Counters: [1451] choices corrected, [1452] seen with the switch off, [1453] errors.

        static readonly Type sGpType = typeof(Fridge).Assembly.GetType("Sims3.Gameplay.Objects.Appliances.MakeGourmetFoodForPet");

        // Pure: may Run pick a non-horse recipe for this household? (has granola, coin heads)
        internal static bool GpOtherForHorses(bool on, bool dogsOrCats) { return dogsOrCats || !on; }

        static void ApplyGourmetPetRecipe()
        {
            if (sGpType == null) { AppliedLog().Add("gourmetpetrecipe: not found: MakeGourmetFoodForPet"); return; }
            Patch(sGpType, "Run", Type.EmptyTypes, typeof(BGpHook), "RunHook", "RunOrig");
        }

        public abstract class BGpHook : Interaction<Sim, Fridge>
        {
            public bool RunHook()
            {
                bool dogs, horses, cats;
                Actor.Household.HasPetsOfType(out dogs, out horses, out cats);
                if (!ShortcutOn("gourmetpetrecipe"))
                {
                    if (horses && !dogs && !cats) PatchCounters()[1452]++;
                    return RunOrig();
                }
                int skill = Actor.SkillManager.GetSkillLevel(SkillNames.Cooking);
                List<Recipe> recipes = Food.GetAvailableRecipes(Target.LotCurrent, Actor, true, false, true);
                for (int i = recipes.Count - 1; i >= 0; i--)
                    if (!recipes[i].IsPetFood || recipes[i].CookingSkillLevelRequired > skill) recipes.RemoveAt(i);
                Recipe pick = null;
                foreach (Recipe r in recipes) if (r.Key == "HorseGranola") { pick = r; break; }
                recipes.Remove(pick);
                bool hasPetFood = Actor.Inventory.ContainsType(typeof(PetFood), 1);
                if ((dogs || cats) && (!hasPetFood || !horses))
                {
                    if (recipes.Count > 0) pick = RandomUtil.GetRandomObjectFromList(recipes);
                    else PatchCounters()[1451]++;  // EA: ArgumentException
                }
                if (horses)
                {
                    bool granola = Actor.Inventory.Find<Ingredient>(MakeGourmetFoodForPetIsHorseFood) != null;
                    // EA: with granola the coin flip ignores (dogs || cats); without it the flip is guarded
                    if (GpOtherForHorses(true, dogs || cats) && RandomUtil.CoinFlip())
                    {
                        if (recipes.Count > 0) pick = RandomUtil.GetRandomObjectFromList(recipes);
                        else PatchCounters()[1451]++;
                    }
                    else if (granola && !dogs && !cats) PatchCounters()[1451]++;
                }
                if (pick == null) return false;
                Fridge_Have have = Fridge_Have.Singleton.CreateInstance(Target, Actor, GetPriority(), true, true) as Fridge_Have;
                have.ObjectClickedOn = Target;
                have.ChosenRecipe = pick;
                have.Quantity = Recipe.MealQuantity.Single;
                Actor.InteractionQueue.PushAsContinuation(have, false);
                return true;
            }
            public bool RunOrig() { return false; } // MakeGourmetFoodForPet.Run
        }

        // EA's MakeGourmetFoodForPet.IsHorseFood, null-safe (the type is internal).
        static bool MakeGourmetFoodForPetIsHorseFood(IGameObject obj, object customData)
        {
            Ingredient i = obj as Ingredient;
            return i != null && i.IngredientKey == "horseGranola";
        }

        // ---- EP1 §19 chipfamilycrystal, chipanycount, chipcomponentorphan ----------------------------------------------
        // TraitChipStaticData.RemoveComponentsFromInventory (BotMakingStation, after HasComponents passed):
        // chipfamilycrystal [verified]: RemoveComponent(sim, count, chargeType) finds crystals in the family inventory but
        //   removes them from sim.Inventory (TryToRemove fails), so family crystals are counted but never used. Fix: removed
        //   from the family inventory. Household-visible (the player's crystals are now consumed, as the recipe says).
        // chipanycount [found by the RE pass]: an "Any" chip component removes up to count small blanks, then up to count
        //   medium, then up to count large: with 1 small and 3 medium and count 2 it takes 3 chips. Fix: the medium and
        //   large calls ask only for what is still missing. Household-visible (fewer chips taken).
        // chipcomponentorphan [found by the RE pass]: Inventory.TryToRemove puts every consumed component back into the
        //   scene and nothing destroys it: each crafted chip leaves 1-6 orphan objects in the world and the save. Fix: the
        //   objects that left the actor's and the family inventory during the call and are in no inventory are destroyed.
        // Counters: [1454] family crystals consumed, [1455] family crystals left with the switch off, [1456] errors;
        //           [1457] orphans destroyed, [1458] orphans left with the switch off, [1459] errors;
        //           [1460] over-asks trimmed, [1461] over-asks with the switch off, [1462] errors.
        static readonly MethodInfo sChRemoveType = typeof(TraitChipStaticData).GetMethod("RemoveComponent", kInstAll, null, new Type[] { typeof(Sim), typeof(uint), typeof(Type) }, null);
        static readonly MethodInfo sChRemoveCrystal = typeof(TraitChipStaticData).GetMethod("RemoveComponent", kInstAll, null, new Type[] { typeof(Sim), typeof(uint), typeof(CrystalPlant.ChargeTypes) }, null);
        static int sChDepth;
        static Type sChLastType;
        static uint sChLastCount, sChRemoved;

        // Pure: how many the next blank call asks for (EA: always count).
        internal static uint ChAsk(bool on, uint count, bool continuesAny, uint removedSoFar)
        {
            return on && continuesAny && removedSoFar < count ? count - removedSoFar : count;
        }
        // Pure: is a crystal found in the family inventory removed from it? (EA tries the Sim's own inventory: fails)
        internal static bool ChFamilyRemoved(bool on, bool foundInFamily) { return on && foundInFamily; }
        // Pure: is an object that left the inventories during the craft destroyed?
        internal static bool ChDestroy(bool on, bool leftInventory, bool inAnyInventory, bool destroyed) { return on && leftInventory && !inAnyInventory && !destroyed; }

        static void ApplyChipComponents()
        {
            if (sChRemoveType == null || sChRemoveCrystal == null) { AppliedLog().Add("chipfamilycrystal: not found: TraitChipStaticData.RemoveComponent overloads"); return; }
            Patch(typeof(TraitChipStaticData), "RemoveComponentsFromInventory", new Type[] { typeof(Sim) }, typeof(BChHook), "AllHook", "AllOrig");
            Patch(typeof(TraitChipStaticData), "RemoveComponent", new Type[] { typeof(Sim), typeof(uint), typeof(Type) }, typeof(BChHook), "TypeHook", "TypeOrig");
            Patch(typeof(TraitChipStaticData), "RemoveComponent", new Type[] { typeof(Sim), typeof(uint), typeof(CrystalPlant.ChargeTypes) }, typeof(BChHook), "CrystalHook", "CrystalOrig");
        }

        static Inventory ChFamily(Sim sim)
        {
            return sim == null || sim.Household == null || sim.Household.SharedFamilyInventory == null ? null : sim.Household.SharedFamilyInventory.Inventory;
        }

        public class BChHook
        {
            public void AllHook(Sim sim)
            {
                List<GameObject> before = null;
                try
                {
                    before = new List<GameObject>();
                    foreach (Inventory inv in new Inventory[] { sim == null ? null : sim.Inventory, ChFamily(sim) })
                        if (inv != null) foreach (IGameObject o in inv.FindAll<IGameObject>(false)) { GameObject g = o as GameObject; if (g != null) before.Add(g); }
                }
                catch (Exception) { PatchCounters()[1459]++; before = null; }
                sChDepth++; sChLastType = null; sChLastCount = 0; sChRemoved = 0;
                try { AllOrig(sim); }
                finally { sChDepth--; sChLastType = null; }
                if (before == null) return;
                bool on = ShortcutOn("chipcomponentorphan");
                foreach (GameObject g in before)
                {
                    try
                    {
                        if (g.HasBeenDestroyed || g.InInventory) continue;
                        if (!on) { PatchCounters()[1458]++; continue; }
                        if (ChDestroy(true, true, false, false)) { g.Destroy(); PatchCounters()[1457]++; }
                    }
                    catch (Exception) { PatchCounters()[1459]++; }
                }
            }
            public void AllOrig(Sim sim) { } // TraitChipStaticData.RemoveComponentsFromInventory

            public uint TypeHook(Sim sim, uint count, Type componentType)
            {
                bool chain = sChDepth > 0 && sChLastCount == count && sChRemoved < count
                    && ((componentType == typeof(ChipBlankMedium) && sChLastType == typeof(ChipBlankSmall)) || (componentType == typeof(ChipBlankLarge) && sChLastType == typeof(ChipBlankMedium)));
                uint ask = count;
                if (chain)
                {
                    if (!ShortcutOn("chipanycount")) PatchCounters()[1461]++;
                    else { ask = ChAsk(true, count, true, sChRemoved); PatchCounters()[1460]++; }
                }
                uint r;
                if (ChFamily(sim) == null && ShortcutOn("chipfamilycrystal"))
                {
                    // EA dereferences the family inventory unguarded once the Sim's own inventory falls short
                    r = 0;
                    foreach (InventoryItem it in sim.Inventory.FindItemListNotInUse(componentType, null, null))
                        if (sim.Inventory.TryToRemove(it.Object) && ++r >= ask) break;
                }
                else r = TypeOrig(sim, ask, componentType);
                sChRemoved = chain ? sChRemoved + r : r;
                sChLastType = componentType; sChLastCount = count;
                return r;
            }
            public uint TypeOrig(Sim sim, uint count, Type componentType) { return 0; } // TraitChipStaticData.RemoveComponent(Sim, uint, Type)

            public void CrystalHook(Sim sim, uint count, CrystalPlant.ChargeTypes chargeType)
            {
                sChLastType = null;
                if (!ShortcutOn("chipfamilycrystal"))
                {
                    if (ChFamily(sim) != null) PatchCounters()[1455]++;
                    CrystalOrig(sim, count, chargeType);
                    return;
                }
                try
                {
                    uint n = 0;
                    foreach (InventoryItem it in sim.Inventory.FindItemListNotInUse(typeof(CrystalFlower), null, null))
                    {
                        CrystalFlower c = it.Object as CrystalFlower;
                        if (c != null && c.ChargeType == chargeType && sim.Inventory.TryToRemove(c) && ++n >= count) return;
                    }
                    Inventory fam = ChFamily(sim);
                    if (fam == null) return;  // EA: NullReferenceException
                    foreach (InventoryItem it in fam.FindItemListNotInUse(typeof(CrystalFlower), null, null))
                    {
                        CrystalFlower c = it.Object as CrystalFlower;
                        if (c != null && c.ChargeType == chargeType && ChFamilyRemoved(true, true) && fam.TryToRemove(c))  // EA: sim.Inventory.TryToRemove
                        {
                            PatchCounters()[1454]++;
                            if (++n >= count) return;
                        }
                    }
                }
                catch (Exception) { PatchCounters()[1456]++; }
            }
            public void CrystalOrig(Sim sim, uint count, CrystalPlant.ChargeTypes chargeType) { } // TraitChipStaticData.RemoveComponent(Sim, uint, ChargeTypes)
        }

        // ---- EP1 §20 nanitemerge (tuning kNaniteMerge) -----------------------------------------------------------------
        // [verified, decompiled] BotBuildingSkill.MergeTravelData copies NanitesCollectedCount but not the list of collected
        // nanite types, so after a trip to the future an already-collected nanite counts again (count and list disagree).
        // Fix: the list is merged too (a copy). RE pass, folded in: mTraitChipSchematicsLearned is merged by reference,
        // so the home skill shares the travel copy's list; it gets its own copy (no behaviour change).
        // Household-visible (bot-building collection progress). Counters: [1463] lists merged, [1464] lists left with the switch off, [1465] errors.
        static readonly FieldInfo sNmList = typeof(BotBuildingSkill).GetField("mNanitesCollected", kInstAll);
        static readonly FieldInfo sNmChips = typeof(BotBuildingSkill).GetField("mTraitChipSchematicsLearned", kInstAll);

        // Pure: does a nanite collected before the trip count again after it?
        internal static bool NmCountsAgain(bool on, bool collectedBefore) { return !on && collectedBefore; }

        static void ApplyNaniteMerge()
        {
            if (sNmList == null || sNmChips == null) { AppliedLog().Add("nanitemerge: not found: BotBuildingSkill.mNanitesCollected/mTraitChipSchematicsLearned"); return; }
            Patch(typeof(BotBuildingSkill), "MergeTravelData", new Type[] { typeof(Skill) }, typeof(BNmHook), "MergeHook", "MergeOrig");
        }

        public class BNmHook
        {
            public void MergeHook(Skill skill)
            {
                MergeOrig(skill);
                BotBuildingSkill src = skill as BotBuildingSkill;
                if (src == null) return;
                if (!ShortcutOn("nanitemerge")) { PatchCounters()[1464]++; return; }
                try
                {
                    List<InsectType> l = src.NanitesCollected;
                    if (l != null) sNmList.SetValue(this, new List<InsectType>(l));
                    List<TraitChipName> c = sNmChips.GetValue(this) as List<TraitChipName>;
                    if (c != null) sNmChips.SetValue(this, new List<TraitChipName>(c));
                    PatchCounters()[1463]++;
                }
                catch (Exception) { PatchCounters()[1465]++; }
            }
            public void MergeOrig(Skill skill) { } // BotBuildingSkill.MergeTravelData
        }
    }
}
