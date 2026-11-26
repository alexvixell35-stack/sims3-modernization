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
        // Household-visible (cursed household Sims). Counters: [3866] visible picks, [3867] any-trait picks with the switch off, [3868] errors.

        // Pure: may the removed trait be hidden?
        internal static bool McMayRemoveHidden(bool on, bool evil, bool good) { return !evil && !good && !McVisiblePick(on, evil, good); }
        // Pure: does the hook run the visible pick? (the hook's decision)
        internal static bool McVisiblePick(bool on, bool evil, bool good) { return on && !evil && !good; }

        static void ApplyMummyCurseTrait()
        {
            Patch(typeof(BuffMummysCurse), "OnTimeout", new Type[] { typeof(BuffManager), typeof(BuffInstance), typeof(Buff.OnTimeoutReasons) }, typeof(BMcCurseHook), "TimeoutHook", "TimeoutOrig");
        }

        public class BMcCurseHook
        {
            public void TimeoutHook(BuffManager bm, BuffInstance bi, Buff.OnTimeoutReasons reason)
            {
                Sim actor = bm == null ? null : bm.Actor;
                TraitManager tm = actor == null ? null : actor.TraitManager;
                if (tm == null) { TimeoutOrig(bm, bi, reason); return; }
                bool on = ShortcutOn("mummycursetrait");
                bool evil = tm.HasElement(TraitNames.Evil), good = tm.HasElement(TraitNames.Good);
                if (!McVisiblePick(on, evil, good))
                {
                    if (McMayRemoveHidden(on, evil, good)) PatchCounters()[3867]++;
                    TimeoutOrig(bm, bi, reason);
                    return;
                }
                try
                {
                    Trait t = tm.GetRandomVisibleElement();
                    if (t != null) tm.RemoveElement(t.Guid);
                    tm.AddElement(TraitNames.Evil);
                    PatchCounters()[3866]++;
                }
                catch (Exception) { PatchCounters()[3868]++; }
                if (actor.CanBeKilled()) actor.Kill(SimDescription.DeathType.MummyCurse);
            }
            public void TimeoutOrig(BuffManager bm, BuffInstance bi, Buff.OnTimeoutReasons reason) { } // BuffMummysCurse.OnTimeout
        }

        // ---- EP1 §13 bullridercleanup (tuning kBullRiderCleanup) -------------------------------------------------------
        // [verified, decompiled] MechanicalBull+RideBull.Cleanup sets Target.mCurrentRider = null for every RideBull that
        // ends, including a second Sim's ride that was cancelled or dequeued while the first Sim is still on the bull.
        // Watchers then stop (WatchBull ends on a null rider) and Test lets more rides queue on a bull in use. Fix: when
        // the recorded rider is another Sim whose current interaction is still a RideBull on this bull, the rider is kept.
        // Household-visible. Counters: [3869] riders kept, [3870] riders cleared under a rider with the switch off, [3871] errors.
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
                bool on = ShortcutOn("bullridercleanup");
                MechanicalBull bull = null; Sim rider = null; bool other = false, riding = false;
                try
                {
                    InteractionInstance me = (InteractionInstance)(object)this;
                    bull = me.Target as MechanicalBull;
                    rider = bull == null ? null : sBrRider.GetValue(bull) as Sim;
                    other = rider != null && rider != me.InstanceActor;
                    if (other)
                    {
                        InteractionInstance cur = rider.CurrentInteraction;
                        riding = cur != null && cur.GetType() == sBrRide && cur.Target == bull;
                    }
                }
                catch (Exception) { PatchCounters()[3871]++; other = false; }
                CleanupOrig();
                try
                {
                    if (BrKeep(on, other, riding)) { sBrRider.SetValue(bull, rider); PatchCounters()[3869]++; }
                    else if (!on && BrKeep(true, other, riding)) PatchCounters()[3870]++;
                }
                catch (Exception) { PatchCounters()[3871]++; }
            }
            public void CleanupOrig() { } // MechanicalBull+RideBull.Cleanup
        }

        // ---- EP1 §14 broomtipperformer (tuning kBroomTipPerformer) -----------------------------------------------------
        // [verified, decompiled] MagicBroomArena+WatchStuntRide: after RouteToTipJar the watcher reads mCurrentPerformer,
        // which the ride's Cleanup nulls when the ride ends during the route; the tip animation plays, a selectable watcher
        // pays, then `mCurrentPerformer.IsSelectable` throws. Fix: RouteToTipJar reports failure when the performer is gone
        // after the route, so the watcher skips the tip (EA's own route-failed path). Household-visible: no lost tip money,
        // no script error. Parked (docs/587 §14b, low): RideBroomArena.Run replaces the broom the player picked.
        // Counters: [3872] tips skipped with no performer, [3873] routes ending without one with the switch off, [3874] errors.
        static readonly FieldInfo sBtPerformer = typeof(MagicBroomArena).GetField("mCurrentPerformer", kInstAll);

        // Pure: does the watcher go on to tip (and EA throw) after the route?
        internal static bool BtTips(bool on, bool routed, bool performer) { return routed && (performer || !on); }

        static void ApplyBroomTipPerformer()
        {
            if (sBtPerformer == null) { AppliedLog().Add("broomtipperformer: not found: MagicBroomArena.mCurrentPerformer"); return; }
            Patch(typeof(MagicBroomArena), "RouteToTipJar", new Type[] { typeof(Sim) }, typeof(BBtTipHook), "RouteHook", "RouteOrig");
        }

        public class BBtTipHook
        {
            public bool RouteHook(Sim sim)
            {
                bool routed = RouteOrig(sim);
                if (!routed) return false;
                try
                {
                    bool on = ShortcutOn("broomtipperformer");
                    bool performer = sBtPerformer.GetValue(this) != null;
                    bool tips = BtTips(on, routed, performer);
                    if (!tips) PatchCounters()[3872]++;
                    else if (!on && !BtTips(true, routed, performer)) PatchCounters()[3873]++;
                    return tips;
                }
                catch (Exception) { PatchCounters()[3874]++; return true; }
            }
            public bool RouteOrig(Sim sim) { return false; } // MagicBroomArena.RouteToTipJar
        }

        // ---- EP1 §15 balancingballcareer (tuning kBalancingBallCareer) -------------------------------------------------
        // [verified code, reach inferred] BalancingBall+BalanceBall.Run dereferences `Occupation as PerformanceArtistCareer`
        // (EpicFail and Success branches, unguarded) and OccupationAsPerformanceCareer, but its Test is only
        // CommonStageTest; AddInteractions offers tricks to any performance career (Singer, Magician) at the unlock level,
        // so they hit a NullReferenceException mid-animation. GiantRingOfFire's Test checks the career. Fix: the same check
        // in BalanceBall's Test. Household-visible (the trick is no longer offered to non-acrobats).
        // Counters: [3875] non-acrobats refused, [3876] allowed with the switch off, [3877] errors.
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
                    if (a == null) return true;
                    bool on = ShortcutOn("balancingballcareer");
                    bool artist = a.Occupation is PerformanceArtistCareer;
                    bool allowed = BbAllowed(on, true, artist);   // EA's Test passed above
                    if (!allowed) PatchCounters()[3875]++;
                    else if (!on && !BbAllowed(true, true, artist)) PatchCounters()[3876]++;
                    return allowed;
                }
                catch (Exception) { PatchCounters()[3877]++; return true; }
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
        // Counters: [3878] guarded throws / leaks, [3879] seen with the switch off, [3880] errors;
        //           [3881] spawns skipped (gnome present), [3882] spawn rolls on a lot with a gnome with the switch off, [3883] errors.
        static readonly FieldInfo sEgChanceSandwich = typeof(MagicGnomeElvis).GetField("kChanceGnomeElvisSpawnsSandwich", kEpStatic);
        static readonly FieldInfo sEgPlaysFor = typeof(MagicGnomeElvis).GetField("kMusicPlaysFor", kEpStatic);
        static readonly FieldInfo sEgHandle = typeof(MagicGnomeElvis).GetField("mElvisMusicHandle", kInstAll);
        static readonly MethodInfo sEgSpawnAt = typeof(MagicGnomeElvis).GetMethod("SpawnElvis", kEpStatic, null, new Type[] { typeof(Vector3), typeof(Vector3) }, null);
        static readonly MethodInfo sEgSpawnLot = typeof(MagicGnomeElvis).GetMethod("SpawnElvis", kEpStatic, null, Type.EmptyTypes, null);
        static AlarmHandle sEgStopAlarm = AlarmHandle.kInvalidHandle;
        internal static void ElvisGnomeWorldLoad() { sEgStopAlarm = AlarmHandle.kInvalidHandle; }   // a handle from the last world could match another alarm

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
                catch (Exception) { PatchCounters()[3880]++; CreateGuarded(); return; }
                if (!home) { PatchCounters()[guard ? 3878 : 3879]++; }
                else if (gnomes > 0) { PatchCounters()[cap ? 3881 : 3882]++; }
                if (EgSpawnRuns(guard, cap, home, gnomes)) CreateGuarded();
            }
            // daily alarm (docs/651 §1): a throw in EA's body would drop it for good
            static void CreateGuarded()
            {
                try { CreateOrig(); }
                catch (ResetException) { throw; }
                catch (Exception e) { EaAlarmThrew("MagicGnomeElvis.TryToCreateElvisGnome", 4111, e); }
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
                    if (p.CookingProcess == null || p.CookingProcess.Recipe == null) { PatchCounters()[3878]++; continue; }  // EA: NullReferenceException
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
                PatchCounters()[3878]++;
                if (Sim.ActiveActor != null && Sim.ActiveActor.LotHome != null) sEgSpawnLot.Invoke(null, null);
            }
            public static void SpawnOrig(Vector3 objectPosition, Vector3 objectForwardVector) { } // MagicGnomeElvis.SpawnElvis(Vector3, Vector3)

            public static void PlayHook()
            {
                bool on = ShortcutOn("elvisgnomeguard");
                if (!on)
                {
                    if (Sim.ActiveActor == null) PatchCounters()[3879]++;
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
                    if (EgLoops(on, sims) == 0) return;
                    if (EgLoops(false, sims) > EgLoops(on, sims)) PatchCounters()[3878]++;
                    ObjectSound old = sEgHandle.GetValue(gnomes[0]) as ObjectSound;
                    if (old != null) old.Stop();
                    ObjectSound snd = new ObjectSound(gnomes[0].ObjectId, "lvis_gnome_music_lp");
                    sEgHandle.SetValue(gnomes[0], snd);
                    snd.Start(true);
                    AlarmManager.Global.RemoveAlarm(sEgStopAlarm);
                    sEgStopAlarm = AlarmManager.Global.AddAlarm((float)sEgPlaysFor.GetValue(null), TimeUnit.Minutes, MagicGnomeElvis.StopElvisMusic, "Turn off music", AlarmType.NeverPersisted, null);
                }
                catch (Exception) { PatchCounters()[3880]++; }
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
                catch (Exception) { PatchCounters()[3880]++; }
            }
            public static void StopOrig() { } // MagicGnomeElvis.StopElvisMusic
        }

        // ---- EP1 §17 cattoypickerrel (tuning kCatToyPickerRel) ---------------------------------------------------------
        // [verified, decompiled + shipped tuning] CatToyWallDancer.GetValidSims admits a Sim with no relationship only when
        // `requiredLtr > 0`, the reverse of the threshold (no relationship counts as liking 0): with the shipped -10, Sims
        // and pets the actor has never met are left out of Play With, and the interaction is hidden when nobody else on the
        // lot is known. Fix: no relationship passes when 0 >= requiredLtr; everything else as EA (order kept).
        // Household-visible. Counters: [3244] strangers added, [3245] left out with the switch off, [3246] errors.
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
                // Our copy is EA's loop with CtAdmit as the test (same order, null when nobody), so the switch-off result
                // is EA's; with the switch on a stranger passes when 0 >= requiredLtr (also for a positive threshold,
                // where EA admitted strangers: the same reversed test).
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
                        if (rel == null && admit != CtAdmit(!on, false, 0f, requiredLtr) && CtAdmit(true, false, 0f, requiredLtr))
                            PatchCounters()[on ? 3244 : 3245]++;   // a stranger the fix lists: added (on) / left out (off)
                        if (!admit) continue;
                        if (r == null) r = new List<Sim>();
                        r.Add(s);
                    }
                }
                catch (Exception) { PatchCounters()[3246]++; return ValidOrig(actor, lot, petsOnly, requiredLtr); }
                return r;
            }
            public static List<Sim> ValidOrig(Sim actor, Lot lot, bool petsOnly, float requiredLtr) { return null; } // CatToyWallDancer.GetValidSims
        }

        // ---- EP1 §18 gourmetpetrecipe (tuning kGourmetPetRecipe) -------------------------------------------------------
        // [verified, decompiled] MakeGourmetFoodForPet.Run: in the horse branch, a Sim already carrying Horse Granola flips a
        // coin to cook a random other pet recipe even when the household has no dogs or cats (the no-granola branch has the
        // `(dogs || cats)` guard). RE pass: when only Horse Granola is available, the dog/cat branches draw from an empty
        // list and throw ArgumentException. Fix: our copy of Run (non-yielding) with the guard and no empty draws.
        // Household-visible (pet food the Sim cooks). Counters: [3884] choices corrected, [3885] seen with the switch off, [3886] errors.

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
                bool on = ShortcutOn("gourmetpetrecipe");
                Household hh = Actor.Household;
                if (hh == null) return RunOrig();   // was dereferenced before EA's Run, also with the switch off
                bool dogs, horses, cats;
                hh.HasPetsOfType(out dogs, out horses, out cats);
                if (!on)
                {
                    if (horses && !dogs && !cats) PatchCounters()[3885]++;
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
                    else { PatchCounters()[3884]++; if (!horses) pick = null; }  // EA: ArgumentException; never horse food without horses
                }
                if (horses)
                {
                    bool granola = Actor.Inventory.Find<Ingredient>(MakeGourmetFoodForPetIsHorseFood) != null;
                    // EA: with granola the coin flip ignores (dogs || cats); without it the flip is guarded
                    if (GpOtherForHorses(on, dogs || cats) && RandomUtil.CoinFlip())
                    {
                        if (recipes.Count > 0) pick = RandomUtil.GetRandomObjectFromList(recipes);
                        else PatchCounters()[3884]++;
                    }
                    else if (granola && !dogs && !cats) PatchCounters()[3884]++;
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
        // Counters: [3887] family crystals consumed, [3888] family crystals left with the switch off, [3889] errors;
        //           [3890] orphans destroyed, [3891] orphans left with the switch off, [3892] errors;
        //           [3893] over-asks trimmed, [3894] over-asks with the switch off, [3895] errors.
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
        // Pure: does a component call without a family inventory take the guarded path? (EA dereferences it: NRE)
        internal static bool ChNoFamilyGuard(bool on, bool hasFamily) { return on && !hasFamily; }

        static void ApplyChipComponents()
        {
            if (sChRemoveType == null || sChRemoveCrystal == null) { AppliedLog().Add("chipfamilycrystal: not found: TraitChipStaticData.RemoveComponent overloads"); return; }
            Patch(typeof(TraitChipStaticData), "RemoveComponentsFromInventory", new Type[] { typeof(Sim) }, typeof(BChHook), "AllHook", "AllOrig");
            Patch(typeof(TraitChipStaticData), "RemoveComponent", new Type[] { typeof(Sim), typeof(uint), typeof(Type) }, typeof(BChHook), "TypeHook", "TypeOrig");
            Patch(typeof(TraitChipStaticData), "RemoveComponent", new Type[] { typeof(Sim), typeof(uint), typeof(CrystalPlant.ChargeTypes) }, typeof(BChHook), "CrystalHook", "CrystalOrig");
        }

        // Every object in the Sim's and the family inventory.
        static List<GameObject> ChItems(Sim sim)
        {
            List<GameObject> r = new List<GameObject>();
            foreach (Inventory inv in new Inventory[] { sim == null ? null : sim.Inventory, ChFamily(sim) })
                if (inv != null) foreach (IGameObject o in inv.FindAll<IGameObject>(false)) { GameObject g = o as GameObject; if (g != null) r.Add(g); }
            return r;
        }

        // Unused crystals of this charge in inv, counted up to cap.
        static uint ChCrystals(Inventory inv, CrystalPlant.ChargeTypes chargeType, uint cap)
        {
            uint n = 0;
            foreach (InventoryItem it in inv.FindItemListNotInUse(typeof(CrystalFlower), null, null))
            {
                CrystalFlower c = it.Object as CrystalFlower;
                if (c != null && c.ChargeType == chargeType && ++n >= cap) break;
            }
            return n;
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
                try { before = ChItems(sim); }
                catch (Exception) { PatchCounters()[3892]++; before = null; }
                sChDepth++; sChLastType = null; sChLastCount = 0; sChRemoved = 0;
                try { AllOrig(sim); }
                finally { sChDepth--; sChLastType = null; }
                if (before == null) return;
                bool on = ShortcutOn("chipcomponentorphan");
                Dictionary<GameObject, bool> after = new Dictionary<GameObject, bool>();   // set: one pass, not Contains per object
                try { foreach (GameObject g in ChItems(sim)) after[g] = true; }
                catch (Exception) { PatchCounters()[3892]++; return; }
                foreach (GameObject g in before)
                {
                    try
                    {
                        bool left = !after.ContainsKey(g), inAny = g.InInventory, gone = g.HasBeenDestroyed;
                        if (ChDestroy(on, left, inAny, gone)) { g.Destroy(); PatchCounters()[3890]++; }
                        else if (!on && ChDestroy(true, left, inAny, gone)) PatchCounters()[3891]++;
                    }
                    catch (Exception) { PatchCounters()[3892]++; }
                }
            }
            public void AllOrig(Sim sim) { } // TraitChipStaticData.RemoveComponentsFromInventory

            public uint TypeHook(Sim sim, uint count, Type componentType)
            {
                bool chain = sChDepth > 0 && sChLastCount == count && sChRemoved < count
                    && ((componentType == typeof(ChipBlankMedium) && sChLastType == typeof(ChipBlankSmall)) || (componentType == typeof(ChipBlankLarge) && sChLastType == typeof(ChipBlankMedium)));
                bool on = ShortcutOn("chipanycount");
                uint ask = ChAsk(on, count, chain, sChRemoved);
                if (ask != count) PatchCounters()[3893]++;
                else if (!on && ChAsk(true, count, chain, sChRemoved) != count) PatchCounters()[3894]++;
                uint r;
                if (ChNoFamilyGuard(ShortcutOn("chipfamilycrystal"), ChFamily(sim) != null))
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
                bool on = ShortcutOn("chipfamilycrystal");
                if (!on)
                {
                    try
                    {
                        // a family crystal is needed (the Sim has too few): EA fails to remove it
                        Inventory fam0 = ChFamily(sim);
                        if (fam0 != null && sim.Inventory != null && ChCrystals(sim.Inventory, chargeType, count) < count && ChCrystals(fam0, chargeType, 1) > 0)
                            PatchCounters()[3888]++;
                    }
                    catch (Exception) { PatchCounters()[3889]++; }
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
                        if (c != null && c.ChargeType == chargeType && ChFamilyRemoved(on, true) && fam.TryToRemove(c))  // EA: sim.Inventory.TryToRemove
                        {
                            PatchCounters()[3887]++;
                            if (++n >= count) return;
                        }
                    }
                }
                catch (Exception) { PatchCounters()[3889]++; }
            }
            public void CrystalOrig(Sim sim, uint count, CrystalPlant.ChargeTypes chargeType) { } // TraitChipStaticData.RemoveComponent(Sim, uint, ChargeTypes)
        }

        // ---- EP1 §20 nanitemerge (tuning kNaniteMerge) -----------------------------------------------------------------
        // [verified, decompiled] BotBuildingSkill.MergeTravelData copies NanitesCollectedCount but not the list of collected
        // nanite types, so after a trip to the future an already-collected nanite counts again (count and list disagree).
        // Fix: the list is merged too (a copy). RE pass, folded in: mTraitChipSchematicsLearned is merged by reference,
        // so the home skill shares the travel copy's list; it gets its own copy (no behaviour change).
        // Household-visible (bot-building collection progress). Counters: [3896] lists merged, [3897] lists left with the switch off, [3898] errors.
        static readonly FieldInfo sNmList = typeof(BotBuildingSkill).GetField("mNanitesCollected", kInstAll);
        static readonly FieldInfo sNmChips = typeof(BotBuildingSkill).GetField("mTraitChipSchematicsLearned", kInstAll);

        // Pure: does a nanite collected before the trip count again after it?
        internal static bool NmCountsAgain(bool on, bool collectedBefore) { return collectedBefore && !NmMerge(on, true); }
        // Pure: does the hook merge the list? (the travel skill has one)
        internal static bool NmMerge(bool on, bool srcHasList) { return on && srcHasList; }

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
                bool on = ShortcutOn("nanitemerge");
                List<InsectType> l = src.NanitesCollected;
                if (!NmMerge(on, l != null))
                {
                    if (!on && NmMerge(true, l != null)) PatchCounters()[3897]++;
                    return;
                }
                try
                {
                    sNmList.SetValue(this, new List<InsectType>(l));
                    List<TraitChipName> c = sNmChips.GetValue(this) as List<TraitChipName>;
                    if (c != null) sNmChips.SetValue(this, new List<TraitChipName>(c));
                    PatchCounters()[3896]++;
                }
                catch (Exception) { PatchCounters()[3898]++; }
            }
            public void MergeOrig(Skill skill) { } // BotBuildingSkill.MergeTravelData
        }
    }
}
