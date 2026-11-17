using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Careers;
using Sims3.Gameplay.Controllers;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.ObjectComponents;
using Sims3.Gameplay.Objects;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.PetSystems;
using Sims3.Gameplay.Skills;
using Sims3.Gameplay.Socializing;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // EA's own one-tick maintenance alarms, spread over many ticks with the same results (docs/147). Each switch is off by
    // default and swaps one alarm callback; off = the game's own method runs unchanged.
    //   easpreadlots (kEaSpreadLots): the 3 AM venue cleanup LotManager.CleanUpLotsAndInventories; the same per-lot and per-Sim
    //     steps in our task, a few lots or 2 ms per tick. It fires in the same tick as NRaas Overwatch's nightly alarm.
    //   easpreadpets (kEaSpreadPets): the pet pool alarm PetPoolManager.AlarmCallBack runs unchanged, but inside our task. EA's
    //     pet builder already sleeps between its CAS steps when it may (GeneticsPet.SleepIfPossible); an alarm may not, so
    //     today the whole build lands in one tick.
    //   easpreaddecay (kEaSpreadDecay): the midnight relationship decay (SocialComponent.RelationshipDecay -> DecayAll); the same
    //     per-pair step, a slice of Sims per tick, one step per pair per day (DailyPass), drops at the end as in DecayAll.
    // Verify switches easpreadlotsverify / easpreaddecayverify log a census before and after either path. Trigger: EaSpreadNow.
    // Counters [177-196] and [207] (registry): lots 177-183, pets 184-188 + 207 (extra alarm chains dropped), decay 189-196.
    public static partial class Fixes
    {
        static MethodInfo sStuckInit;                       // LotManager.RunStuckCheckHelper.Init(Sim), internal
        static FieldInfo sGenieRel, sZeroSince, sRomance;   // Relationship.mIsGenieWishRelationship / mWhenStartedBeingZero / RomanceVisibilityState
        static MethodInfo sDecayAway, sDecayVisibility;     // Relationship.CanRelationshipDecayAway(), RomanceVisibilityState.DecayVisibility()
        static MethodInfo sChangeSeason;                    // Lot.ChangeSeason(), the season timer callback (private)
        static FieldInfo sSeasonTimer;                      // Lot.mGoHomeForSeasonChangeTimer (private)
        static MethodInfo sCreatePet;                       // PetPoolManager.CreatePetByType(PetPoolType), for the build-only trigger
        static FieldInfo sPoolAlarm;                        // PetPoolManager.sPoolAlarm, the pool's one alarm handle
        static ObjectGuid sLotTask = ObjectGuid.InvalidObjectGuid, sPetTask = ObjectGuid.InvalidObjectGuid, sDecayTask = ObjectGuid.InvalidObjectGuid;
        static StopWatch sEaWatch;
        // simler90's Gameplay Systems Core Mod adds four simler90Extras.WorldMaintenance calls to the 3 AM cleanup our patch
        // replaces; we call them at its places (private static, by reflection). Its TuningExtras flags are read inside them.
        static MethodInfo sSimElectronics, sSimYoungster, sSimAging, sSimMissing;
        static bool sSimLooked, sSimFailLogged;

        static void ApplyEaSpread()
        {
            const BindingFlags st = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public, inst = BindingFlags.Instance | BindingFlags.NonPublic;
            Type helper = typeof(LotManager).GetNestedType("RunStuckCheckHelper", BindingFlags.NonPublic);
            sStuckInit = helper == null ? null : helper.GetMethod("Init", st, null, new Type[] { typeof(Sim) }, null);
            sChangeSeason = typeof(Lot).GetMethod("ChangeSeason", inst, null, Type.EmptyTypes, null);
            sSeasonTimer = typeof(Lot).GetField("mGoHomeForSeasonChangeTimer", inst);
            if (sStuckInit == null || sChangeSeason == null || sSeasonTimer == null || sSeasonTimer.FieldType != typeof(AlarmHandle))
                AppliedLog().Add("easpreadlots: RunStuckCheckHelper.Init or the lot season timer not found, off");
            else Patch(typeof(LotManager), "CleanUpLotsAndInventories", Type.EmptyTypes, typeof(LotCleanupHook), "CleanHook", "CleanOrig");

            Patch(typeof(PetPoolManager), "AlarmCallBack", Type.EmptyTypes, typeof(PetPoolHook), "AlarmHook", "AlarmOrig");
            sCreatePet = typeof(PetPoolManager).GetMethod("CreatePetByType", st, null, new Type[] { typeof(PetPoolType) }, null);
            sPoolAlarm = typeof(PetPoolManager).GetField("sPoolAlarm", st);
            if (sPoolAlarm == null || sPoolAlarm.FieldType != typeof(AlarmHandle)) { sPoolAlarm = null; AppliedLog().Add("easpreadpets: PetPoolManager.sPoolAlarm not found, no duplicate-chain guard"); }

            sGenieRel = typeof(Relationship).GetField("mIsGenieWishRelationship", inst);
            sZeroSince = typeof(Relationship).GetField("mWhenStartedBeingZero", inst);
            sRomance = typeof(Relationship).GetField("RomanceVisibilityState", inst);
            sDecayAway = typeof(Relationship).GetMethod("CanRelationshipDecayAway", inst, null, Type.EmptyTypes, null);
            sDecayVisibility = typeof(RomanceVisibilityState).GetMethod("DecayVisibility", inst, null, Type.EmptyTypes, null);
            if (sGenieRel == null || sZeroSince == null || sZeroSince.FieldType != typeof(DateAndTime) || sRomance == null || sDecayAway == null || sDecayVisibility == null)
                AppliedLog().Add("easpreaddecay: Relationship members differ from the Steam build, off");
            else Patch(typeof(SocialComponent), "RelationshipDecay", Type.EmptyTypes, typeof(DecayHook), "RunHook", "RunOrig");
        }

        static void EaLog(string line)
        {
            List<object> log = AppDomain.CurrentDomain.GetData("ts3fixes.easpread") as List<object>;
            if (log == null) { log = new List<object>(); AppDomain.CurrentDomain.SetData("ts3fixes.easpread", log); }
            if (log.Count >= 200) log.RemoveAt(0);
            log.Add("simmin " + SimClock.ElapsedTime(TimeUnit.Minutes).ToString("0") + " " + line);
        }

        static bool TaskAlive(ObjectGuid task) { return task.IsValid && Simulator.GetTask(task) != null; }

        static bool WorldGone() { return GameStates.IsWorldShuttingDown || !GameStates.IsInWorld(); }

        static StopWatch EaWatch()
        {
            if (sEaWatch == null) sEaWatch = StopWatch.Create(StopWatch.TickStyles.Microseconds);
            return sEaWatch;
        }

        // Runs the pass a slice per tick. False when the world went away meanwhile (the rest is dropped).
        static bool RunSliced<T>(SlicedPass<T> pass, Action<T> work, int maxItems, ref int ticks, ref long maxUs)
        {
            StopWatch w = EaWatch();
            while (true)
            {
                w.Restart();
                pass.RunSlice(work, maxItems, EaSpreadCore.kSliceUs, delegate { return w.GetElapsedTime(); });
                long us = w.GetElapsedTime();
                if (us > maxUs) maxUs = us;
                if (pass.Done) return true;
                Simulator.Sleep(0);
                ticks++;
                if (WorldGone()) return false;
            }
        }

        // Runs every owner's units (built at its turn) a slice per tick: kMaxLotUnits units or kSliceUs, resuming mid-owner.
        // False when the world went away meanwhile. n[6] counts units that threw.
        static bool RunUnits<K>(List<K> owners, PartnersOf<K, SpreadUnit> unitsOf, int maxUnits, int[] n, ref int ticks, ref long maxUs, ref int mostUnits)
        {
            DailyPairPass<K, SpreadUnit> pass = new DailyPairPass<K, SpreadUnit>();
            pass.Begin(owners, unitsOf, delegate(K owner, SpreadUnit unit) { try { unit(); } catch (Exception) { n[6]++; } return true; }, delegate { });
            StopWatch w = EaWatch();
            while (true)
            {
                w.Restart();
                int units;
                bool done = pass.Slice(maxUnits, EaSpreadCore.kSliceUs, delegate { return w.GetElapsedTime(); }, out units);
                long us = w.GetElapsedTime();
                if (us > maxUs) maxUs = us;
                if (units > mostUnits) mostUnits = units;
                if (done) return true;
                Simulator.Sleep(0);
                ticks++;
                if (WorldGone()) { pass.Abandon(); return false; }
            }
        }

        // ---- 3 AM venue cleanup -------------------------------------------------------------------------------------------

        // What EA's cleanup does with a lot: 0 clean up (with repairs off the world lot) and empty the household fridge,
        // 1 clean up only (virtual residential slots), 2 change season only. Same tests, same order as the game.
        static int LotKind(Lot lot, Sim active)
        {
            if ((!lot.CanSimTreatAsHome(active) && !lot.IsControllable) || lot.IsBaseCampLotType) return 0;
            return lot.HasVirtualResidentialSlots ? 1 : 2;
        }

        // n: [0..2] lots per kind, [3] velvet ropes reset, [4] NPCs seeded, [5] stuck checks armed, [6] steps that threw.
        // A lot's work as units, built at its turn: the game's per-lot step with Lot.MagicallyCleanUp opened up into
        // per-object units (AddCleanUpUnits), so a lot with many objects continues in the next tick.
        static List<SpreadUnit> LotUnits(Lot lot, Sim active, int[] n)
        {
            List<SpreadUnit> u = new List<SpreadUnit>();
            if (lot.HasBeenDestroyed) return u;
            int kind = LotKind(lot, active);
            n[kind]++;
            if (kind == 2) u.Add(delegate { lot.MagicallyChangeSeason(); });
            else
            {
                AddCleanUpUnits(u, lot, !lot.IsWorldLot);
                if (kind == 0)
                    u.Add(delegate
                    {
                        Household h = lot.Household;
                        if (h != null && h.SharedFridgeInventory != null && h.SharedFridgeInventory.Inventory != null) h.SharedFridgeInventory.Inventory.DestroyItems(false);
                    });
            }
            u.Add(delegate
            {
                if (!Bartending.IsBarVenue(lot))
                {
                    foreach (IVelvetRopes ropes in lot.GetObjects<IVelvetRopes>()) { ropes.NightlyCleanUp(); n[3]++; }
                    SimlerCall(sSimElectronics, lot, n, 7);
                }
            });
            return u;
        }

        // Looks up simler90Extras.WorldMaintenance once per session (loaded assemblies do not change in a session).
        // Absent = all four stay null and the cleanup is EA's.
        static void FindSimlerExtras()
        {
            if (sSimLooked) return;
            sSimLooked = true;
            try
            {
                Type t = null;
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies()) { t = a.GetType("simler90Extras.WorldMaintenance", false); if (t != null) break; }
                if (t == null) return;
                const BindingFlags st = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
                sSimElectronics = t.GetMethod("TurnOffDisturbingElectronicsOnLot", st, null, new Type[] { typeof(Lot) }, null);
                sSimYoungster = t.GetMethod("SendInactiveYoungsterHome", st, null, new Type[] { typeof(Sim) }, null);
                sSimAging = t.GetMethod("AgingFixHomelessPetsAndYoungsters", st, null, Type.EmptyTypes, null);
                sSimMissing = t.GetMethod("FixMissingSims", st, null, Type.EmptyTypes, null);
                EaLog("venue cleanup: simler90 extras found: electronics " + (sSimElectronics != null) + ", youngsters home " + (sSimYoungster != null)
                    + ", aging fix " + (sSimAging != null) + ", missing Sims " + (sSimMissing != null));
            }
            catch (Exception e) { EaLog("venue cleanup: simler90 extras lookup failed: " + e.GetType().Name); }
        }

        // One simler90 extra; n[slot] counts calls that returned. A throw is logged once and never stops our units.
        static void SimlerCall(MethodInfo m, object arg, int[] n, int slot)
        {
            if (m == null) return;
            try { m.Invoke(null, arg == null ? null : new object[] { arg }); n[slot]++; }
            catch (Exception e)
            {
                n[6]++;
                if (sSimFailLogged) return;
                sSimFailLogged = true;
                Exception inner = e.InnerException != null ? e.InnerException : e;
                EaLog("venue cleanup: simler90 " + m.Name + " threw " + inner.GetType().Name + ": " + inner.Message);
            }
        }

        // Lot.MagicallyCleanUp(repair) as units, the same steps in the same order: the objects to clean are found (one unit
        // per lot object, as the game's predicate query) and then cleaned one by one (an object destroyed meanwhile is
        // skipped); puddles and burnt tiles; unconscious cat prey; repairs (one unit per object); the season kick. A lot
        // with active jobs is left alone, as the game does. The object list is read at the lot's turn, where the game reads it.
        static void AddCleanUpUnits(List<SpreadUnit> u, Lot lot, bool repair)
        {
            GameObject[] objects = lot.GetObjects<GameObject>();
            bool skip = false, home = false, junkyard = false, repairHome = false;
            u.Add(delegate
            {
                Household.CleanupHighlightedDiveLotDictionary();
                skip = Occupation.DoesLotHaveAnyActiveJobs(lot);
                home = lot.CanSimTreatAsHome(Sim.ActiveActor);
            });
            EaSpreadCore.AddFindThenAct<GameObject>(u, objects,
                delegate(GameObject o) { return !skip && Lot.DoesObjectNeedCleaning(o); },
                delegate(GameObject o) { if (!skip) CleanObject(o); },
                delegate(GameObject o) { return !o.HasBeenDestroyed; });
            u.Add(delegate
            {
                if (skip) return;
                lot.FireManager.RestoreObjects();
                Lot current = lot.LotCurrent;
                foreach (LotLocation l in World.GetPuddles(current.LotId, LotLocation.Invalid))
                    if ((!home || lot.LotLocationIsPublicResidential(l)) && (lot.TombRoomManager == null || !lot.TombRoomManager.IsObjectInATombRoom(l)))
                        PuddleManager.RemovePuddle(current.LotId, l);
                foreach (LotLocation l in World.GetBurntTiles(current.LotId, LotLocation.Invalid))
                    if ((!home || lot.LotLocationIsPublicResidential(l)) && (lot.TombRoomManager == null || !lot.TombRoomManager.IsObjectInATombRoom(l)))
                        World.SetBurnt(lot.LotId, l, false);
                foreach (ICatPrey prey in Sims3.Gameplay.Queries.GetObjects<ICatPrey>(current))
                    if (prey.IsUnconscious) prey.Destroy();
            });
            if (repair)
            {
                u.Add(delegate { junkyard = lot.IsJunkyardLot; repairHome = lot.CanSimTreatAsHome(Sim.ActiveActor); });
                foreach (GameObject o in objects)
                {
                    GameObject obj = o;
                    u.Add(delegate { if (!skip && !obj.HasBeenDestroyed) RepairObject(obj, junkyard, repairHome); });
                }
            }
            u.Add(delegate { if (!skip) SeasonKick(lot); });
        }

        // The game's per-object body of the magic clean-up.
        static void CleanObject(GameObject item)
        {
            if (item.InUse) return;
            if (item.IsCleanable) item.Cleanable.ForceClean();
            ISnackBowl bowl = item as ISnackBowl;
            if (bowl != null) bowl.Refill();
            IThrowAwayable trash = item as IThrowAwayable;
            if (trash != null) trash.ThrowAwayImmediately();
            Book book = item as Book;
            if (book != null)
            {
                if (book.MyShelf != null) { if (!book.InInventory) book.MyShelf.Inventory.TryToAdd(book); }
                else
                {
                    Lot owner = book.GetOwnerLot();
                    if (owner != null && owner.LotId == Sim.ActiveActor.LotHome.LotId) Sim.ActiveActor.Household.SharedFamilyInventory.Inventory.TryToAdd(book);
                    else book.FadeOut(false, true);
                }
            }
            if (item is IDestroyOnMagicalCleanup) item.FadeOut(false, true);
        }

        // The game's per-object body of Lot.RepairAllObjects.
        static void RepairObject(GameObject o, bool junkyard, bool home)
        {
            if (o.InUse || !o.InWorld || (junkyard && o.IsOutside) || (home && !o.IsInPublicResidentialRoom && !o.IsInHiddenResidentialRoom)) return;
            if (o.Charred)
            {
                o.Charred = false;
                if (o is Windows) RepairableComponent.CreateReplaceObject(o);
            }
            RepairableComponent r = o.Repairable;
            if (r != null && r.Broken) r.ForceRepaired(null);
        }

        // The end of the magic clean-up: a lot whose seasonal marker needs a kick changes season by the lot's own timer
        // (an hour for a community lot with Sims on it, sent home first; else a minute). Private timer field and callback.
        static void SeasonKick(Lot lot)
        {
            SeasonalLotMarker[] markers = lot.GetObjects<SeasonalLotMarker>();
            if (markers.Length <= 0 || !markers[0].IsKickNeeded()) return;
            if (lot.IsCommunityLot && lot.GetAllActors().Count > 0)
            {
                foreach (Sim sim in lot.GetAllActors()) Sim.MakeSimGoHome(sim, true, new InteractionPriority(InteractionPriorityLevel.High));
                SeasonTimer(lot, 1f, TimeUnit.Hours, "timer to give sims chance to leave lot for season change");
            }
            else SeasonTimer(lot, 1f, TimeUnit.Minutes, "timer just to make it yieldable");
        }

        static void SeasonTimer(Lot lot, float time, TimeUnit unit, string name)
        {
            AlarmTimerCallback change = (AlarmTimerCallback)Delegate.CreateDelegate(typeof(AlarmTimerCallback), lot, sChangeSeason);
            AlarmHandle h = lot.AlarmManager.AddAlarm(time, unit, change, name, AlarmType.DeleteOnReset, lot);
            sSeasonTimer.SetValue(lot, h);
            lot.AlarmManager.AlarmWillYield(h);
        }

        static void ActorStep(Sim actor, int[] n)
        {
            if (actor.HasBeenDestroyed) return;
            try
            {
                if (!actor.IsSelectable)
                {
                    actor.SeedNpcInventoryIfNeeded();
                    actor.GrantVampireFruitToNpcVampires();
                    SimlerCall(sSimYoungster, actor, n, 8);
                    n[4]++;
                }
                sStuckInit.Invoke(null, new object[] { actor });
                n[5]++;
            }
            catch (Exception) { n[6]++; }
        }

        static List<Lot> AllLots()
        {
            List<Lot> lots = new List<Lot>();
            foreach (Lot lot in LotManager.AllLots) lots.Add(lot);
            return lots;
        }

        // Verify census: objects the cleanup would still clean (not in use) on the lots it cleans.
        static int LotsToClean()
        {
            Sim active = Sim.ActiveActor;
            int n = 0;
            foreach (Lot lot in AllLots())
            {
                if (lot.HasBeenDestroyed || LotKind(lot, active) == 2) continue;
                foreach (GameObject o in lot.FindAllObjectsToClean()) if (!o.InUse) n++;
            }
            return n;
        }

        static string LotCounts(int[] n)
        {
            return "cleaned " + n[0] + ", virtual-slot lots " + n[1] + ", season only " + n[2] + ", velvet ropes " + n[3]
                + ", NPCs seeded " + n[4] + ", stuck checks armed " + n[5] + ", failed steps " + n[6]
                + (n.Length > 7 && n[7] + n[8] + n[9] + n[10] > 0 ?", simler90 extras: electronics lots " + n[7] + ", youngster checks " + n[8] + ", aging fix " + n[9] + ", missing Sims " + n[10] : "");
        }

        static void LotCleanupTask(Sim active, int before)
        {
            int[] c = PatchCounters(), n = new int[11]; // [7..10] simler90 extras, logged only (no counter slots reserved)
            int ticks = 1;
            long maxUs = 0;
            FindSimlerExtras();
            List<Lot> lotList = AllLots();
            int maxUnits = 0;
            bool whole = RunUnits(lotList, delegate(Lot lot) { return LotUnits(lot, active, n); }, EaSpreadCore.kMaxLotUnits, n, ref ticks, ref maxUs, ref maxUnits);
            if (whole)
            {
                Simulator.Sleep(0);
                ticks++;
                whole = !WorldGone();
            }
            if (whole)
            {
                SlicedPass<Sim> actors = new SlicedPass<Sim>(new List<Sim>(LotManager.Actors));
                whole = RunSliced(actors, delegate(Sim s) { ActorStep(s, n); }, EaSpreadCore.kMaxActors, ref ticks, ref maxUs);
            }
            // simler90 runs these two after the Sim loop, before the hot-spot pick. shortcut: the aging fix runs whole in one
            // tick (its per-townie body is inline, no method to call per item), spread it later if it shows as a hitch.
            if (whole && sSimAging != null)
            {
                Simulator.Sleep(0);
                ticks++;
                whole = !WorldGone();
                if (whole)
                {
                    StopWatch aw = EaWatch();
                    aw.Restart();
                    SimlerCall(sSimAging, null, n, 9);
                    if (aw.GetElapsedTime() > maxUs) maxUs = aw.GetElapsedTime();
                }
            }
            if (whole && sSimMissing != null)
            {
                Simulator.Sleep(0);
                ticks++;
                whole = !WorldGone();
                if (whole) SimlerCall(sSimMissing, null, n, 10); // sleeps between households itself
            }
            if (whole)
            {
                Simulator.Sleep(0); // the hot-spot pick gets a tick of its own, timed into the longest tick (docs/147 §6)
                ticks++;
                whole = !WorldGone();
            }
            if (!whole) { c[183]++; EaLog("venue cleanup stopped: world gone"); return; }
            StopWatch w = EaWatch();
            w.Restart();
            MetaAutonomyManager.DecideTodaysHotSpotsAndDeadZones();
            if (w.GetElapsedTime() > maxUs) maxUs = w.GetElapsedTime();
            c[177]++;
            c[178] += lotList.Count;
            if (maxUnits > c[209]) c[209] = maxUnits;
            c[179] += n[5];
            c[180] += ticks;
            if (maxUs > c[181]) c[181] = (int)maxUs;
            string line = "venue cleanup (ours): " + LotCounts(n) + "; " + ticks + " ticks, longest " + (maxUs / 1000.0).ToString("0.0") + " ms, most units in a tick " + maxUnits;
            if (before >= 0) { c[182] = LotsToClean(); line += "; verify: to clean before " + before + ", after " + c[182]; }
            EaLog(line);
        }

        public static class LotCleanupHook
        {
            public static void CleanHook()
            {
                bool verify = ShortcutOn("easpreadlotsverify");
                if (ShortcutOn("easpreadlots") && !TaskAlive(sLotTask))
                {
                    Sim active = Sim.ActiveActor; // EA reads it once, when the alarm fires
                    int before = verify ? LotsToClean() : -1;
                    sLotTask = Simulator.AddObject(new LoopTask(delegate { LotCleanupTask(active, before); }));
                    return;
                }
                if (!verify) { CleanOrig(); return; }
                int[] n = new int[7];
                Sim a = Sim.ActiveActor;
                foreach (Lot lot in AllLots()) n[LotKind(lot, a)]++;
                int b = LotsToClean();
                CleanOrig();
                PatchCounters()[182] = LotsToClean();
                EaLog("venue cleanup (game's): lots per kind: cleaned " + n[0] + ", virtual-slot lots " + n[1] + ", season only " + n[2]
                    + ", Sims " + LotManager.Actors.Count + "; verify: to clean before " + b + ", after " + PatchCounters()[182]);
            }
            public static void CleanOrig() { } // receives LotManager.CleanUpLotsAndInventories' original header
        }

        // ---- pet pool alarm -----------------------------------------------------------------------------------------------

        static int PetsInPools()
        {
            int n = 0;
            foreach (PetPoolType t in Enum.GetValues(typeof(PetPoolType)))
            {
                PetPool pool;
                if (PetPoolManager.TryGetPetPool(t, out pool, true) && pool != null) n += pool.GetPoolSize();
            }
            return n;
        }

        // Runs body in our task (EA's own sleeps between the CAS steps then yield), counted. A second alarm while the
        // first build still runs takes the game's own path.
        static void PetTask(SpreadUnit body, string what)
        {
            int[] c = PatchCounters();
            c[184]++;
            sPetTask = Simulator.AddObject(new LoopTask(delegate
            {
                int before = PetsInPools();
                long t0 = Simulator.TicksElapsed();
                try { body(); }
                catch (Exception e) { EaLog(what + " (ours) failed: " + e.Message); }
                int ticks = (int)(Simulator.TicksElapsed() - t0) + 1, made = PetsInPools() - before;
                c[185] += Math.Max(0, made);
                c[186] += ticks;
                if (ticks > c[187]) c[187] = ticks;
                EaLog(what + " (ours): pets in pools " + before + " -> " + (before + made) + ", " + ticks + " ticks");
            }));
        }

        public static class PetPoolHook
        {
            public static void AlarmHook()
            {
                float left = StoredPoolAlarmMinutesLeft();
                if (EaSpreadCore.ExtraPoolFire(left))
                {
                    PatchCounters()[207]++;
                    EaLog("pet pool alarm: extra fire while the pool's alarm is due in " + left.ToString("0.0") + " sim-minutes, dropped (second chain ends)");
                    return;
                }
                if (ShortcutOn("easpreadpets"))
                {
                    if (!TaskAlive(sPetTask)) { PetTask(AlarmOrig, "pet pool alarm"); return; }
                    PatchCounters()[188]++;
                }
                int before = PetsInPools();
                AlarmOrig();
                EaLog("pet pool alarm (game's): pets in pools " + before + " -> " + PetsInPools());
            }
            public static void AlarmOrig() { } // receives PetPoolManager.AlarmCallBack's original header
        }

        static AlarmHandle StoredPoolAlarm() { return sPoolAlarm == null ? AlarmHandle.kInvalidHandle : (AlarmHandle)sPoolAlarm.GetValue(null); }

        // 0 at a genuine fire (the stored alarm is the one firing) and while a build runs (handle cleared).
        static float StoredPoolAlarmMinutesLeft() { return AlarmManager.Global.GetTimeLeft(StoredPoolAlarm(), TimeUnit.Minutes); }

        // Build-only trigger for full pools: one stray cat through the pool's own creator, then thrown away.
        static void BuildTestPet()
        {
            SimDescription pet = sCreatePet == null ? null : sCreatePet.Invoke(null, new object[] { PetPoolType.StrayCat }) as SimDescription;
            EaLog("test pet built: " + (pet != null));
            if (pet != null) pet.Dispose();
        }

        // ---- midnight relationship decay ----------------------------------------------------------------------------------

        static readonly DailyPairPass<SimDescription, SimDescription> sDecay = new DailyPairPass<SimDescription, SimDescription>();
        static readonly List<Relationship> sDecayDrops = new List<Relationship>();
        static int[] sDecayDay = new int[3];  // this pass: pairs stepped, pairs dropped, steps that threw
        static int sDecayBefore = -1, sDecayDue = -1;  // verify: relationships stored and drops due when the pass began

        static bool SuperFriendly(SimDescription sim)
        {
            Charisma skill = sim.SkillManager.GetSkill<Charisma>(SkillNames.Charisma);
            return skill != null && skill.IsSuperFriendly();
        }

        static bool sDecayFriends; // the current Sim is not a super-friendly charisma master (read at its turn, as DecayAll)

        // The current Sim's partners at its turn, as DecayAll reads them; none for a Sim gone since the alarm or a
        // long-distance friend (DecayAll skips those whole).
        static List<SimDescription> DecayPartners(SimDescription key)
        {
            Dictionary<SimDescription, Relationship> pairs;
            try
            {
                if (!Relationship.sAllRelationships.TryGetValue(key, out pairs) || key.TraitManager.HasElement(TraitNames.LongDistanceFriend)) return null;
                sDecayFriends = !SuperFriendly(key);
                return new List<SimDescription>(pairs.Keys);
            }
            catch (Exception) { sDecayDay[2]++; return null; }
        }

        // The game's per-pair decay for one pair this Sim owns (lower id), as in Relationship.DecayAll; drops are kept for
        // the end of the pass. Time checks use the alarm time "at", so a later slice decides as the alarm tick would have.
        // A partner removed or a Sim destroyed since the list was read is skipped.
        static bool DecayStep(SimDescription key, SimDescription other, DateAndTime at)
        {
            Dictionary<SimDescription, Relationship> pairs;
            Relationship r;
            if (!EaSpreadCore.OwnsPair(key.SimDescriptionId, other.SimDescriptionId)
                || !Relationship.sAllRelationships.TryGetValue(key, out pairs) || !pairs.TryGetValue(other, out r)) return false;
            try { DecayPair(r, other, sDecayFriends, at); }
            catch (Exception) { sDecayDay[2]++; }
            return true;
        }

        // DecayAll's skips before any liking change: genie-wish pairs, long-distance friends, boarding-school students.
        static bool DecayExcluded(Relationship r, SimDescription other)
        {
            return (bool)sGenieRel.GetValue(r) || other.TraitManager.HasElement(TraitNames.LongDistanceFriend) || other.IsEnrolledInBoardingSchool();
        }

        // ...and after the romance visibility step: a state younger than the hold time, friends of a super-friendly Sim.
        static bool DecayHeld(Relationship r, SimDescription other, bool decayFriends, DateAndTime at)
        {
            if (SimClock.ElapsedTime(TimeUnit.Hours, r.LTR.WhenStateStarted, at) < SocialComponent.MinHoursToHoldRelationshipState) return true;
            return LTRData.Get(r.LTR.CurrentLTR).IsFriendly && (!decayFriends || SuperFriendly(other));
        }

        // A pair already at 0 liking stays at 0 in the step, so whether DecayAll drops it is known before the pass.
        static bool DropDue(Relationship r, DateAndTime at)
        {
            if (!(Math.Abs(r.LTR.Liking) < 1E-05f) || !(bool)sDecayAway.Invoke(r, null)) return false;
            DateAndTime zeroSince = (DateAndTime)sZeroSince.GetValue(r);
            return zeroSince.Ticks != 0 && SimClock.ElapsedTime(TimeUnit.Days, zeroSince, at) >= SocialComponent.NumDaysAfterWhichZeroRelationshipIsDropped;
        }

        // Verify: the relationships DecayAll's rules drop in a pass starting now (both paths log it, so they compare in one run).
        static int DropsDue(DateAndTime at)
        {
            int n = 0;
            foreach (KeyValuePair<SimDescription, Dictionary<SimDescription, Relationship>> kv in Relationship.sAllRelationships)
            {
                if (kv.Key.TraitManager.HasElement(TraitNames.LongDistanceFriend)) continue;
                bool decayFriends = !SuperFriendly(kv.Key);
                foreach (KeyValuePair<SimDescription, Relationship> p in kv.Value)
                    if (EaSpreadCore.OwnsPair(kv.Key.SimDescriptionId, p.Key.SimDescriptionId) && !DecayExcluded(p.Value, p.Key)
                        && !DecayHeld(p.Value, p.Key, decayFriends, at) && DropDue(p.Value, at)) n++;
            }
            return n;
        }

        static void DecayPair(Relationship r, SimDescription other, bool decayFriends, DateAndTime at)
        {
            if (r.SimDescriptionA.Household == r.SimDescriptionB.Household || r.SimDescriptionA.Genealogy.IsBloodRelated(r.SimDescriptionB.Genealogy))
                Relationship.LearnInfoAboutEachOther(r);
            if (DecayExcluded(r, other)) return;
            object romance = sRomance.GetValue(r);
            if (romance != null) sDecayVisibility.Invoke(romance, null);
            if (DecayHeld(r, other, decayFriends, at)) return;
            LTRData ltr = LTRData.Get(r.LTR.CurrentLTR);
            STCData stc = STCData.Get(r.STC.CurrentSTC);
            float scale = 1f;
            bool npcA = r.SimDescriptionA.CreatedSim == null || r.SimDescriptionA.CreatedSim.IsNPC;
            bool npcB = r.SimDescriptionB.CreatedSim == null || r.SimDescriptionB.CreatedSim.IsNPC;
            if (npcA && npcB) scale = SocialComponent.NPCRelationshipDecayMultiplier;
            sDecayDay[0]++;
            if (r.LTR.Liking > 0f)
            {
                float was = r.LTR.Liking;
                r.LTR.UpdateLiking((stc.LTRDecayFromAbove + ltr.LTRDecayFromAbove) * scale);
                if (r.LTR.Liking < 0f) r.LTR.SetLiking(0f);
                sZeroSince.SetValue(r, new DateAndTime(0L));
                float friend = LTRData.Get(Sims3.UI.Controller.LongTermRelationshipTypes.Friend).Liking;
                if (!r.IsPetRel && r.LTR.Liking < friend && was >= friend)
                    r.LTR.AddInteractionBit(LongTermRelationship.InteractionBits.DistantFriends);
            }
            else if (r.LTR.Liking < 0f)
            {
                r.LTR.UpdateLiking((stc.LTRDecayFromBelow + ltr.LTRDecayFromBelow) * scale);
                if (r.LTR.Liking > 0f) r.LTR.SetLiking(0f);
                sZeroSince.SetValue(r, new DateAndTime(0L));
            }
            r.STC.DecayCurrentCommodityValue(SocialComponent.kSTCDecayRateMultiplier);
            if (!(Math.Abs(r.LTR.Liking) < 1E-05f) || !(bool)sDecayAway.Invoke(r, null)) return;
            DateAndTime zeroSince = (DateAndTime)sZeroSince.GetValue(r);
            if (zeroSince.Ticks == 0) { sZeroSince.SetValue(r, at); return; }
            if (SimClock.ElapsedTime(TimeUnit.Days, zeroSince, at) >= SocialComponent.NumDaysAfterWhichZeroRelationshipIsDropped) sDecayDrops.Add(r);
        }

        // End of a pass: the drops DecayAll makes after its loop, then counters and the log line.
        static void DecayEnd()
        {
            int[] c = PatchCounters();
            foreach (Relationship r in sDecayDrops)
            {
                Dictionary<SimDescription, Relationship> pairs;
                if (Relationship.sAllRelationships.TryGetValue(r.SimDescriptionA, out pairs)) pairs.Remove(r.SimDescriptionB);
                if (Relationship.sAllRelationships.TryGetValue(r.SimDescriptionB, out pairs)) pairs.Remove(r.SimDescriptionA);
            }
            sDecayDay[1] = sDecayDrops.Count;
            sDecayDrops.Clear();
            c[189]++;
            c[191] += sDecayDay[0];
            c[192] += sDecayDay[1];
            c[196] += sDecayDay[2];
            string line = "relationship decay (ours): pairs stepped " + sDecayDay[0] + ", dropped " + sDecayDay[1] + ", steps that threw " + sDecayDay[2];
            if (sDecayBefore >= 0) line += "; verify: drops due at start " + sDecayDue + "; " + DecayCensus("before", sDecayBefore);
            EaLog(line);
            sDecayDay = new int[3];
        }

        // Verify census: relationships stored (each pair counted under both Sims, as the game stores them).
        static int Relationships()
        {
            int n = 0;
            foreach (Dictionary<SimDescription, Relationship> pairs in Relationship.sAllRelationships.Values) n += pairs.Count;
            return n;
        }

        static string DecayCensus(string label, int before)
        {
            int nonZero = 0, above = 0;
            double sum = 0;
            foreach (KeyValuePair<SimDescription, Dictionary<SimDescription, Relationship>> kv in Relationship.sAllRelationships)
                foreach (KeyValuePair<SimDescription, Relationship> p in kv.Value)
                {
                    if (!EaSpreadCore.OwnsPair(kv.Key.SimDescriptionId, p.Key.SimDescriptionId)) continue;
                    float l = p.Value.LTR.Liking;
                    if (Math.Abs(l) >= 1E-05f) nonZero++;
                    if (l > 0f) above++;
                    sum += Math.Abs(l);
                }
            return "relationships " + label + " " + before + ", after " + Relationships() + "; pairs with liking " + nonZero + " (" + above
                + " above 0), sum |liking| " + sum.ToString("0.0");
        }

        static void DecayTask()
        {
            int[] c = PatchCounters();
            int ticks = 1, maxPairs = 0;
            long maxUs = 0;
            StopWatch w = EaWatch();
            while (true)
            {
                w.Restart();
                int stepped;
                bool done = sDecay.Slice(EaSpreadCore.kMaxDecayPairs, EaSpreadCore.kSliceUs, delegate { return w.GetElapsedTime(); }, out stepped);
                long us = w.GetElapsedTime();
                if (us > maxUs) maxUs = us;
                if (stepped > maxPairs) maxPairs = stepped;
                if (done) break;
                Simulator.Sleep(0);
                ticks++;
                if (WorldGone()) { sDecay.Abandon(); EaLog("relationship decay stopped: world gone"); return; }
            }
            c[193] += ticks;
            if (maxUs > c[194]) c[194] = (int)maxUs;
            if (maxPairs > c[208]) c[208] = maxPairs;
            EaLog("relationship decay (ours): " + ticks + " ticks, longest " + (maxUs / 1000.0).ToString("0.0") + " ms, most pairs in a tick " + maxPairs);
        }

        public static class DecayHook
        {
            public static void RunHook()
            {
                bool verify = ShortcutOn("easpreaddecayverify");
                if (!ShortcutOn("easpreaddecay"))
                {
                    if (sDecay.Running && TaskAlive(sDecayTask)) { sDecay.Finish(); PatchCounters()[195]++; } // switched off mid-pass
                    int before = verify ? Relationships() : -1, due = verify ? DropsDue(SimClock.CurrentTime()) : -1;
                    RunOrig();
                    if (verify) EaLog("relationship decay (game's): verify: drops due at start " + due + "; " + DecayCensus("before", before));
                    return;
                }
                int[] c = PatchCounters();
                if (!TaskAlive(sDecayTask)) { sDecay.Abandon(); sDecayDrops.Clear(); sDecayDay = new int[3]; } // left over from an unloaded world
                DateAndTime at = SimClock.CurrentTime();
                if (sDecay.Begin(new List<SimDescription>(Relationship.sAllRelationships.Keys), DecayPartners,
                    delegate(SimDescription key, SimDescription other) { return DecayStep(key, other, at); }, DecayEnd)) c[195]++;
                sDecayBefore = verify ? Relationships() : -1;
                sDecayDue = verify ? DropsDue(at) : -1;
                c[190] += Relationship.sAllRelationships.Count;
                if (!TaskAlive(sDecayTask)) sDecayTask = Simulator.AddObject(new LoopTask(DecayTask));
            }
            public static void RunOrig() { } // receives SocialComponent.RelationshipDecay's original header
        }

        // ---- trigger --------------------------------------------------------------------------------------------------------

        // Agent trigger (repros.json bp-easpread-*): runs one job now, from a one-minute alarm like the game's own, through
        // the path the switches pick. "lots", "pets" (the real pool alarm; nothing when every pool is full), "petbuild" (one
        // stray cat built and thrown away, same path rule as "pets"), "decay".
        public static string EaSpreadNow(string job)
        {
            AlarmTimerCallback run;
            switch (job)
            {
                case "lots": run = LotCleanupHook.CleanHook; break;
                case "pets": // takes the pending pool alarm's place, so the pool keeps one alarm chain
                    run = delegate { AlarmManager.Global.RemoveAlarm(StoredPoolAlarm()); PetPoolHook.AlarmHook(); };
                    break;
                case "decay": run = DecayHook.RunHook; break;
                case "petbuild":
                    run = delegate
                    {
                        if (ShortcutOn("easpreadpets")) PetTask(BuildTestPet, "test pet");
                        else { long t0 = Simulator.TicksElapsed(); BuildTestPet(); EaLog("test pet (game's path): " + (Simulator.TicksElapsed() - t0 + 1) + " ticks"); }
                    };
                    break;
                default: return "unknown job " + job + " (lots, pets, petbuild, decay)";
            }
            AlarmManager.Global.AddAlarm(1f, TimeUnit.Minutes, run, "Ts3Fixes.EaSpreadNow", AlarmType.NeverPersisted, null);
            return job + " in 1 sim-minute";
        }
    }
}
