using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects.Environment;
using Sims3.Gameplay.Objects.Gardening;
using Sims3.Gameplay.Objects.Plumbing;
using Sims3.Gameplay.PetSystems;
using Sims3.Gameplay.Pools;
using Sims3.Gameplay.Situations;
using Sims3.Gameplay.Skills;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // EP batch 4b (docs/587 §10, sweep tranche 3: EP types ranked by real use, docs/data/usage-rank-1115.csv, then scanner
    // rank). Every switch off by default; none is changed by simler90 UPDATE242. Checks: EpBatch4Checks.cs.
    // Slots 3262-3282 (block fixes-1500).
    public static partial class Fixes
    {
        // ---- EP4 §6 junglegymexitqueue (tuning kJungleGymExitQueue) ---------------------------------------------------
        // [verified, decompiled + shipped kTimeToWaitForNextExit 3, kTimeMaxToWait 45; reach inferred]
        // JungleGymLookOutTower.RouteOutOfJungleGym (Generations) enqueues the exiting Sim, sleeps while the queue head is
        // someone else (up to 45 min), then adds a 3-minute alarm whose ResetSafeToExit dequeues the head, whoever it is. A
        // Sim reset or deleted while waiting leaves its entry and never adds its alarm, so the queue holds one stale entry
        // for good: every later Sim waits the full 45 minutes on top of the tower, and its alarm removes the previous Sim's
        // entry, leaving its own. Only an object reset clears it. Fix (copy, yields as EA): before and while waiting, entries
        // of Sims that are gone or no longer on the tower and have no exit alarm pending are dropped; each exit alarm removes
        // that Sim's own entry; ResetSafeToExit (EA's alarms from a save) returns on an empty queue. Household-visible.
        // Counters: [3262] stale entries dropped, [3263] stale heads waited on with the switch off, [3264] errors.
        static readonly FieldInfo sJgQueue = typeof(JungleGymLookOutTower).GetField("mExitQueue", kInstAll);
        static readonly FieldInfo sJgNext = typeof(JungleGymLookOutTower).GetField("kTimeToWaitForNextExit", kEpStatic);
        static readonly FieldInfo sJgMax = typeof(JungleGymLookOutTower).GetField("kTimeMaxToWait", kEpStatic);
        static readonly List<Sim> sJgPending = new List<Sim>();

        // Pure: does an entry stay in the exit queue? Does ResetSafeToExit return on this queue (EA dequeues an empty one)?
        internal static bool JgKeep(bool on, bool live, bool onTower, bool alarmPending) { return !on || (live && (onTower || alarmPending)); }
        internal static bool JgSafeSkips(bool on, int queued) { return on && queued == 0; }

        static void ApplyJungleGymExitQueue()
        {
            string miss = EpMiss("mExitQueue", sJgQueue, "kTimeToWaitForNextExit", sJgNext, "kTimeMaxToWait", sJgMax);
            if (miss.Length > 0) { AppliedLog().Add("junglegymexitqueue: not found: " + miss); return; }
            Patch(typeof(JungleGymLookOutTower), "RouteOutOfJungleGym", new Type[] { typeof(Sim) }, typeof(BEp4JgHook), "ExitHook", "ExitOrig");
            Patch(typeof(JungleGymLookOutTower), "ResetSafeToExit", Type.EmptyTypes, typeof(BEp4JgHook), "SafeHook", "SafeOrig");
        }

        static void JgPurge(bool on, JungleGymLookOutTower t, Queue<Sim> q, Sim self, bool dropSelf)
        {
            if (q.Count == 0) return;
            List<Sim> keep = new List<Sim>();
            int dropped = 0;
            foreach (Sim s in q)
            {
                bool live = s != null && !s.HasBeenDestroyed;
                if ((s == self ? !dropSelf : JgKeep(on, live, live && t.ActorsUsingMe.Contains(s), live && sJgPending.Contains(s)))) keep.Add(s);   // review: before enqueueing, the Sim's own stale entries go too
                else dropped++;
            }
            if (dropped == 0) return;
            q.Clear();
            foreach (Sim s in keep) q.Enqueue(s);
            PatchCounters()[3262] += dropped;
        }

        public class JgExitDone
        {
            public JungleGymLookOutTower Tower; public Sim Actor;
            public void Fire()
            {
                try
                {
                    sJgPending.Remove(Actor);
                    Queue<Sim> q = sJgQueue.GetValue(Tower) as Queue<Sim>;
                    if (q == null || !q.Contains(Actor)) return;
                    List<Sim> rest = new List<Sim>(q);
                    rest.Remove(Actor);
                    q.Clear();
                    foreach (Sim s in rest) q.Enqueue(s);
                }
                catch (Exception) { PatchCounters()[3264]++; }
            }
        }

        public class BEp4JgHook
        {
            public bool ExitHook(Sim actor)
            {
                JungleGymLookOutTower t = (JungleGymLookOutTower)(object)this;
                bool on = ShortcutOn("junglegymexitqueue");
                Queue<Sim> q; float next, max;
                try { q = (Queue<Sim>)sJgQueue.GetValue(t); next = (float)sJgNext.GetValue(null); max = (float)sJgMax.GetValue(null); }
                catch (Exception) { PatchCounters()[3264]++; return ExitOrig(actor); }
                if (!on)
                {
                    try
                    {
                        if (q != null && q.Count > 0)
                        {
                            Sim h = q.Peek();
                            bool live = h != null && !h.HasBeenDestroyed;
                            if (!(live && (t.ActorsUsingMe.Contains(h) || sJgPending.Contains(h)))) PatchCounters()[3263]++;   // EA waits on a stale head
                        }
                    }
                    catch (Exception) { PatchCounters()[3264]++; }
                    return ExitOrig(actor);
                }
                if (q == null) return ExitOrig(actor);
                // EA's body, with the marked changes
                JgPurge(on, t, q, actor, true);  // changed
                q.Enqueue(actor);
                DateAndTime start = SimClock.CurrentTime();
                while (q.Count > 0 && q.Peek() != actor && SimClock.ElapsedTime(TimeUnit.Minutes, start) < max)
                {
                    Simulator.Sleep(10u);
                    JgPurge(on, t, q, actor, false);  // changed
                }
                JgExitDone done = new JgExitDone(); done.Tower = t; done.Actor = actor;
                if (!sJgPending.Contains(actor)) sJgPending.Add(actor);
                t.AddAlarm(next, TimeUnit.Minutes, done.Fire, "Look out tower exit alarm", AlarmType.NeverPersisted);  // changed: removes this Sim's entry
                t.PushSimsFromFootprint(ResourceUtils.HashString32("jungleGymLookOut_ftpShape"), actor, t.ActorsUsingMe, true);
                return true;
            }
            public bool ExitOrig(Sim actor) { return false; } // JungleGymLookOutTower.RouteOutOfJungleGym

            public void SafeHook()
            {
                try
                {
                    bool on = ShortcutOn("junglegymexitqueue");
                    Queue<Sim> q = sJgQueue.GetValue(this) as Queue<Sim>;
                    if (q != null && JgSafeSkips(on, q.Count)) { PatchCounters()[3262]++; return; }
                }
                catch (Exception) { PatchCounters()[3264]++; }
                SafeOrig();
            }
            public void SafeOrig() { } // JungleGymLookOutTower.ResetSafeToExit
        }

        // ---- EP4 §7 wildhorselot (tuning kWildHorseLot) ---------------------------------------------------------------
        // [verified, decompiled + shipped tuning: preferred kBigPark, kSmallPark, kBeach, kFishingSpot; weights 2, 2, 16, 24;
        // commercial multiplier 1] WildHorses.FindGoodLot (Pets) tests `IndexOf(...) <= 0`, so the first preferred lot type
        // (big park) is weighted as an ordinary commercial lot (x1, not x2). RE: lots whose stray-wildlife gnome disallows the
        // horse are skipped for weighting but were already added to the fallback list, so a gnome-protected lot can still be
        // picked when no lot has weight. Fix (copy, static, no yield): `< 0`, and a disallowed lot is not a fallback either.
        // Household-visible (where wild horses appear; a household gnome is honoured).
        // Counters: [3265] picks with the corrected weights, [3266] big parks under-weighted with the switch off, [3267] errors.

        // Pure: weight multiplier for a commercial lot at this preferred index (EA: index 0 counts as not preferred).
        internal static float WhMult(bool on, int index, float[] weights, float commercial)
        {
            bool preferred = on ? index >= 0 : index > 0;
            return preferred && index < weights.Length ? weights[index] : commercial;
        }

        static void ApplyWildHorseLot()
        {
            Patch(typeof(WildHorses), "FindGoodLot", new Type[] { typeof(SimDescription) }, typeof(BEp4WhHook), "FindHook", "FindOrig");
        }

        // WildHorses.FindGoodLot's lists: weighted lots and weights, and the unweighted fallback list. on = wildhorselot's
        // corrections (index 0 preferred, a gnome-protected lot is no fallback); off = EA's loop exactly. Also used by
        // wildlotskip (WildLotSkip.cs, docs/656 §8.3) to draw again without the lots a horse could not reach.
        internal static void WhLists(SimDescription horse, bool on, List<Lot> list, List<float> weights, List<Lot> fallback)
        {
            foreach (Lot lot in LotManager.AllLotsWithoutCommonExceptions)
            {
                if (lot.CommercialLotSubType == CommercialLotSubType.kEP10_Port) continue;
                if (!on) fallback.Add(lot);  // EA: before the gnome test
                IStrayWildlifeGnome[] g = lot.GetObjects<IStrayWildlifeGnome>();
                if (g != null && g.Length > 0 && g[0].DoesGnomeDisallowWildlifeType(horse)) continue;
                if (on) fallback.Add(lot);  // changed: after the gnome test
                float num = WildHorses.kDefaultLotWeight;
                List<Sim> sims = new List<Sim>(lot.GetSims());
                if (sims.Count > 0) num = WildHorses.kDefaultPopulatedLotWeight;
                foreach (Sim s in sims) if (s.HasTrait(TraitNames.FriendOfTheHerdPet)) num += WildHorses.kFriendOfTheHerdLotWeightIncrease;
                if (lot.IsResidentialLot) num *= WildHorses.kVisitResidentialLotMultiplier;
                else num *= WhMult(on, Array.IndexOf(WildHorses.kPreferredCommercialLots, lot.CommercialLotSubType), WildHorses.kPreferredCommercialLotWeights, WildHorses.kVisitCommercialLotMultiplier);  // changed when on: index 0 is preferred
                if (num != 0f) { list.Add(lot); weights.Add(num); }
            }
        }

        public class BEp4WhHook
        {
            public static Lot FindHook(SimDescription horse)
            {
                bool on = ShortcutOn("wildhorselot");
                Lot pick = null;
                if (!on)
                {
                    if (WildHorses.kPreferredCommercialLots != null && WildHorses.kPreferredCommercialLots.Length > 0) PatchCounters()[3266]++;
                    pick = FindOrig(horse);
                }
                else
                {
                    try
                    {
                        List<Lot> list = new List<Lot>(), list2 = new List<Lot>();
                        List<float> list3 = new List<float>();
                        WhLists(horse, true, list, list3, list2);
                        PatchCounters()[3265]++;
                        if (list.Count <= 0) pick = list2.Count > 0 ? RandomUtil.GetRandomObjectFromList(list2) : null;
                        else pick = RandomUtil.GetWeightedRandomObjectFromList(list3, list);
                    }
                    catch (Exception) { PatchCounters()[3267]++; pick = FindOrig(horse); }
                }
                // docs/656 §8.3 wildlotskip: a lot this horse or its herd could not reach lately is drawn again (same weights)
                if (pick != null && ShortcutOn("wildlotskip")) pick = WlsHorsePick(horse, pick, on);
                return pick;
            }
            public static Lot FindOrig(SimDescription horse) { return null; } // WildHorses.FindGoodLot
        }

        // ---- EP4 §8 harvesterunripe (tuning kHarvesterUnripe) ---------------------------------------------------------
        // [verified, decompiled + shipped Gatherer 0.3 / Science bonus tuning] The Harvester's run (Ambitions invention)
        // accepts any ISuctionable as a candidate, and HarvestPlant.OnSuctioned calls DoHarvest with no HarvestTest; every
        // other DoHarvest caller tests first. On an unripe or dead plant that yields no produce but still rolls the Gatherer
        // and Science extra harvestables (seed copies), Gardening skill and Collecting. Fix: OnSuctioned on a plant that
        // fails HarvestTest does nothing and returns false. Household-visible (player-favourable exploit removed).
        // Counters: [3268] unripe suctions skipped, [3269] harvested unripe with the switch off, [3270] errors.

        // Pure: is the suctioned plant harvested?
        internal static bool HuHarvests(bool on, bool ripe) { return ripe || !on; }

        static void ApplyHarvesterUnripe()
        {
            Patch(typeof(HarvestPlant), "OnSuctioned", new Type[] { typeof(Sim), typeof(bool) }, typeof(BEp4HuHook), "SuckHook", "SuckOrig");
        }

        public class BEp4HuHook
        {
            public bool SuckHook(Sim actor, bool isOutOfControl)
            {
                try
                {
                    HarvestPlant p = (HarvestPlant)(object)this;
                    bool on = ShortcutOn("harvesterunripe");
                    bool ripe = HarvestPlant.HarvestTest(p, null);
                    if (!HuHarvests(on, ripe)) { PatchCounters()[3268]++; return false; }
                    else if (!ripe && !on) PatchCounters()[3269]++;
                }
                catch (Exception) { PatchCounters()[3270]++; }
                return SuckOrig(actor, isOutOfControl);
            }
            public bool SuckOrig(Sim actor, bool isOutOfControl) { return false; } // HarvestPlant.OnSuctioned
        }

        // ---- EP4 §9 hottubseatposture (tuning kHotTubSeatPosture) -----------------------------------------------------
        // [verified, decompiled] HotTubPosture.EnsureSeatPosture (Late Night), called by every seated cuddle, chat and
        // move-seat social, re-runs InitPosture (SittingPosture's version re-requests the idle state instead). Each call
        // adds another AlwaysPersisted sore-buff alarm over mSoreBuffTimer, so the older one escapes OnExitPosture and
        // removes Sore after the Sim left the tub; a call before the first Idle also makes Idle init a second time. Fix:
        // while the sore alarm is pending the init is skipped; otherwise EA's init runs once and the first Idle no longer
        // repeats it. Household-visible (minor).
        // Counters: [3271] duplicate inits skipped, [3272] duplicate alarms with the switch off, [3273] errors.
        static readonly FieldInfo sHtFirst = typeof(HotTubPosture).GetField("mFirstIdle", kInstAll);
        static readonly FieldInfo sHtTimer = typeof(HotTubPosture).GetField("mSoreBuffTimer", kInstAll);

        // Pure: does EnsureSeatPosture init again?
        internal static bool HtInits(bool on, bool timerPending) { return !on || !timerPending; }

        static void ApplyHotTubSeatPosture()
        {
            string miss = EpMiss("HotTubPosture.mFirstIdle", sHtFirst, "HotTubPosture.mSoreBuffTimer", sHtTimer);
            if (miss.Length > 0) { AppliedLog().Add("hottubseatposture: not found: " + miss); return; }
            Patch(typeof(HotTubPosture), "EnsureSeatPosture", Type.EmptyTypes, typeof(BEp4HtHook), "EnsureHook", "EnsureOrig");
        }

        public class BEp4HtHook
        {
            public void EnsureHook()
            {
                bool on = ShortcutOn("hottubseatposture");
                try
                {
                    bool pending = (AlarmHandle)sHtTimer.GetValue(this) != AlarmHandle.kInvalidHandle;
                    if (pending && !on) PatchCounters()[3272]++;
                    if (!HtInits(on, pending)) { PatchCounters()[3271]++; return; }
                }
                catch (Exception) { PatchCounters()[3273]++; }
                EnsureOrig();
                if (!on) return;
                try { if ((AlarmHandle)sHtTimer.GetValue(this) != AlarmHandle.kInvalidHandle && (bool)sHtFirst.GetValue(this)) sHtFirst.SetValue(this, false); }   // review: only when InitPosture ran
                catch (Exception) { PatchCounters()[3273]++; }
            }
            public void EnsureOrig() { } // HotTubPosture.EnsureSeatPosture
        }

        // ---- EP4 §10 teenpartybreakup (tuning kTeenPartyBreakUp) ------------------------------------------------------
        // [verified, decompiled; timing inferred] TeenParty+BreakUpParty.Run (Generations) routes to the guest, then reads
        // Target.GetSituationOfType<TeenParty>().Lot with no null check and never uses it (MakeEveryoneGoHome uses the
        // actor). If the guest left the party or it ended during the route, Run throws. The JuiceKeggerParty sibling is
        // fixed in ilpatch-convert row 73; this copy is not. Fix (copy, yields as EA): the unused read is dropped.
        // Household-visible (the selectable teen host).
        // Counters: [3274] runs past a missing party, [3275] would throw with the switch off, [3276] errors.

        // Pure: does Run throw?
        internal static bool TpThrows(bool on, bool partyGone) { return !on && partyGone; }

        static void ApplyTeenPartyBreakUp()
        {
            Patch(typeof(TeenParty.BreakUpParty), "Run", Type.EmptyTypes, typeof(BEp4TpHook), "RunHook", "RunOrig");
        }

        public abstract class BEp4TpHook : Interaction<Sim, Sim>
        {
            public bool RunHook()
            {
                bool on = ShortcutOn("teenpartybreakup");
                if (!on) return RunOrig();
                // EA's body; changed: the situation is not dereferenced
                Actor.RouteToDynamicObjectRadius(Target, 1f, null, null);
                StandardEntry(false);
                BeginCommodityUpdates();
                bool gone = Target.GetSituationOfType<TeenParty>() == null;
                if (gone && !TpThrows(on, gone)) PatchCounters()[3274]++;   // EA would throw here
                Actor.PlaySoloAnimation("a_teenParty_breakParty_x", ProductVersion.EP4);
                TeenParty.MakeEveryoneGoHome(Actor, null);
                EndCommodityUpdates(true);
                StandardExit(false);
                return true;
            }
            public bool RunOrig() { return false; } // TeenParty+BreakUpParty.Run
        }

        // ---- EP4 §11 dogswimaroundlist (tuning kDogSwimAroundList) ----------------------------------------------------
        // [verified, decompiled + shipped kChanceToSwimNearOtherSims 0.6] DoggySwimAround.Run (Pets/Island Paradise, pushed
        // repeatedly by SwimmingInPool for every swimming dog) does `pool.ActorsUsingMe.Remove(Actor)` on the pool's live
        // use list to build "other swimmers", so the dog drops off the pool's use list for the rest of the swim: a pool reset
        // no longer resets it and other dogs never swim near it. Fix: after EA's Run (also on a throw) a dog still swimming
        // in that pool is put back on its use list. Household-visible (household dogs).
        // Counters: [3277] dogs put back on the use list, [3278] left off with the switch off, [3279] errors.

        // Pure: is the dog put back?
        internal static bool DaRestore(bool on, bool stillSwimming, bool onList) { return on && stillSwimming && !onList; }

        static readonly Type sDaType = typeof(Pool).Assembly.GetType("Sims3.Gameplay.Pools.DoggySwimAround");

        static void ApplyDogSwimAroundList()
        {
            if (sDaType == null) { AppliedLog().Add("dogswimaroundlist: not found: Sims3.Gameplay.Pools.DoggySwimAround"); return; }
            Patch(sDaType, "Run", Type.EmptyTypes, typeof(BEp4DaHook), "RunHook", "RunOrig");
        }

        public class BEp4DaHook
        {
            public bool RunHook()
            {
                Sim a = null; Pool pool = null;
                try
                {
                    InteractionInstance ii = (InteractionInstance)(object)this;
                    a = ii.InstanceActor;
                    pool = a == null || a.Posture == null ? null : a.Posture.Container as Pool;
                    if (pool != null && !pool.IsActorUsingMe(a)) pool = null;  // EA's removal only matters when it was on the list
                }
                catch (Exception) { PatchCounters()[3279]++; }
                bool reset = false;
                try { return RunOrig(); }
                catch (ResetException) { reset = true; throw; }
                finally
                {
                    try
                    {
                        if (!reset && pool != null && !a.HasBeenDestroyed)
                        {
                            bool on = ShortcutOn("dogswimaroundlist");
                            bool swimming = a.Posture != null && a.Posture.Container == pool, onList = pool.IsActorUsingMe(a);
                            if (DaRestore(on, swimming, onList)) { pool.AddToUseList(a); PatchCounters()[3277]++; }
                            else if (swimming && !onList && !on) PatchCounters()[3278]++;
                        }
                    }
                    catch (Exception) { PatchCounters()[3279]++; }
                }
            }
            public bool RunOrig() { return false; } // DoggySwimAround.Run
        }

        // ---- EP4 §12 tutorballoonalarm (tuning kTutorBalloonAlarm) ----------------------------------------------------
        // [verified, decompiled; the queue-reject leak reach inferred] TutorSkillInteraction.ConfigureInteraction
        // (University) starts the repeating AlwaysPersisted thought-balloon alarm when the interaction is created, not when
        // tutoring starts: a queued "Tutor Sim in Skill" pops skill balloons every 30 minutes while the tutor does other
        // things, and an instance rejected at queue insert (no Cleanup) leaks the alarm into the save for good. Fix: the alarm
        // is removed after ConfigureInteraction and started in PreSocialLoop (when tutoring begins) with EA's own timing;
        // Cleanup removes it as before. Household-visible.
        // Counters: [3280] alarms moved to the tutoring start, [3281] started at creation with the switch off, [3282] errors.
        static readonly MethodInfo sTuShow = typeof(TutorSkillInteraction).GetMethod("ShowBalloon", kInstAll, null, Type.EmptyTypes, null);
        static readonly FieldInfo sTuFirst = typeof(TutorSkillInteraction).GetField("kTimeToFirstBalloon", kEpStatic);
        static readonly FieldInfo sTuEvery = typeof(TutorSkillInteraction).GetField("kTimeBetweenThoughtBalloons", kEpStatic);

        // Pure: does creating the interaction start the balloon alarm?
        internal static bool TuAlarmAtCreate(bool on) { return !on; }

        static void ApplyTutorBalloonAlarm()
        {
            string miss = EpMiss("TutorSkillInteraction.ShowBalloon", sTuShow, "kTimeToFirstBalloon", sTuFirst, "kTimeBetweenThoughtBalloons", sTuEvery);
            if (miss.Length > 0) { AppliedLog().Add("tutorballoonalarm: not found: " + miss); return; }
            Patch(typeof(TutorSkillInteraction), "ConfigureInteraction", Type.EmptyTypes, typeof(BEp4TuHook), "ConfigHook", "ConfigOrig");
            Patch(typeof(TutorSkillInteraction), "PreSocialLoop", Type.EmptyTypes, typeof(BEp4TuHook), "LoopHook", "LoopOrig");
        }

        public class BEp4TuHook
        {
            public void ConfigHook()
            {
                ConfigOrig();
                try
                {
                    TutorSkillInteraction t = (TutorSkillInteraction)(object)this;
                    if (t.BalloonAlarm == AlarmHandle.kInvalidHandle) return;
                    bool on = ShortcutOn("tutorballoonalarm");
                    if (!TuAlarmAtCreate(on))
                    {
                        AlarmManager.Global.RemoveAlarm(t.BalloonAlarm);
                        t.BalloonAlarm = AlarmHandle.kInvalidHandle;
                        PatchCounters()[3280]++;
                    }
                    else if (!on) PatchCounters()[3281]++;
                }
                catch (Exception) { PatchCounters()[3282]++; }
            }
            public void ConfigOrig() { } // TutorSkillInteraction.ConfigureInteraction

            public void LoopHook()
            {
                try
                {
                    TutorSkillInteraction t = (TutorSkillInteraction)(object)this;
                    bool on = ShortcutOn("tutorballoonalarm");
                    if (t.BalloonAlarm == AlarmHandle.kInvalidHandle && !TuAlarmAtCreate(on))
                    {
                        AlarmTimerCallback cb = (AlarmTimerCallback)Fixes.Bind(typeof(AlarmTimerCallback), t, sTuShow);
                        t.BalloonAlarm = AlarmManager.Global.AddAlarmRepeating((float)sTuFirst.GetValue(null), TimeUnit.Minutes, cb, (float)sTuEvery.GetValue(null), TimeUnit.Minutes, "ShowSkillBalloon", AlarmType.AlwaysPersisted, null);
                    }
                }
                catch (Exception) { PatchCounters()[3282]++; }
                LoopOrig();
            }
            public void LoopOrig() { } // TutorSkillInteraction.PreSocialLoop
        }
    }
}
