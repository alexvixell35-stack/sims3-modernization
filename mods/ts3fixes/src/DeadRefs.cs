using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Careers;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.EventSystem;
using Sims3.Gameplay.ObjectComponents;
using Sims3.Gameplay.Objects.Fishing;
using Sims3.Gameplay.Objects.HobbiesSkills;
using Sims3.Gameplay.Objects.Vehicles;
using Sims3.Gameplay.Skills;
using Sims3.Gameplay.Socializing;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // EA fields that keep disposed SimDescriptions and destroyed Sims, so the object graph behind them goes into every save
    // (issue #91, docs/205). Holders read in the decompiled game code [verified] unless marked; the per-holder counts are the
    // reporter's [community]. Two switches, both default off:
    //   deadrefguards: Relationship.Get(SimDescription) makes no new entry for an invalid description (it re-adds one for any
    //     description asked about, e.g. from Dispose's own later steps after RemoveSimDescriptionRelationships); an existing
    //     entry is still returned, so EA's removal walks see it. AgingManager.IsAgingStateValid is false for an invalid
    //     description (EA checks only null), so the day pass no longer ages or alarms a dead state. [393] Get refusals,
    //     [394] aging states refused.
    //   deadrefsweep: holders cleaned spread over ticks (~2 ms slices in one script task, cursor per phase, each item
    //     re-tested at its turn) once per sim hour, plus a cheap final pass before each save over the three big dictionaries
    //     (relationship keys, aging states, alarm owners). Changes save data. Every pass stands down while travelling, on
    //     vacation, saving, outside live mode, when an active-household member is invalid, or when most of a holder is "gone"
    //     (DeadRefsCore.LooksLikeLoad: a pass running before SimDescription.Fixup would see every member invalid; the
    //     reporter's warning, verified: mIsValidDescription is [Persistable(false)]).
    //     Phases: 1 relationship keys (gone key: both sides removed; live key: entries naming a gone description dropped),
    //     2 aging states of gone descriptions (AgingManager.RemoveSimDescription: state out, its alarms cancelled), 3 alarm
    //     owners that are gone descriptions (AlarmManager.OnDispose), 4 household descriptions (occupation and school
    //     Coworkers, FormerBoss, chess challenger, pregnancy dad, empty-outfit count for #100), 5 Sims (LookAtManager react
    //     times, unforgettable topics, push requester, last talking partner, reserved vehicle), 6 fish (fisher), 7 cars and
    //     boats (previous passengers), 8 horseshoe courts (throwers and scores), 9 dartboards (gone players quit through EA's
    //     Quit, gone current player and cheating Sim cleared; #166 build 42, docs/516), 10 object use, reference and
    //     routing-reference lists (gone Sims removed through EA's own Remove methods; #166 build 40, docs/517), 11 object owner
    //     components (a gone owner or thief Sim relinked to its live instance, a reward giver with none dropped; #166 build 43, docs/519),
    //     12 ObjectDisposed handlers of relationships nobody holds (a deleted description, or a decayed relationship; #166 build 41, docs/522),
    //     13 celebrity waiting-to-react lists (gone reactors removed through EA's RemoveSimFromWaitingToReactList; docs/523).
    //     [395] relationship keys removed, [396] relationship entries removed, [397] aging states removed, [398] alarm owners
    //     cleared, [399] react-time entries, [400] coworker / boss / chess entries, [401] topics, [402] fishers, [403] Sim
    //     fields (pusher, talking, vehicle, dad), [404] passenger and horseshoe entries, [2045] dartboard entries, [2046] use-list entries, [2047] owner entries, [2048] disposed handlers, [2049] celebrity reactors, [405] passes finished, [406] slices,
    //     [407] longest slice us, [408] total pass ms, [409] longest save pass us, [410] passes refused or stopped (unsafe state, switch off) and items that threw.
    //   Not built (docs/205 §4): CookingProcess appliance handlers,
    //   adoption pool pets, Service.mPreferredServiceNpc (= preferredservice, #86).
    public static partial class Fixes
    {
        const BindingFlags kDrAll = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        static readonly FieldInfo sAgingStates = typeof(AgingManager).GetField("AgingStates", kDrAll);
        static readonly FieldInfo sAgingDesc = typeof(AgingState).GetField("SimDescription", kDrAll);
        static readonly FieldInfo sAlarmIndex = typeof(AlarmManager).GetField("mGameObjectIndex", kDrAll);
        static readonly FieldInfo sLastReact = typeof(LookAtManager).GetField("mLastReactTime", kDrAll);
        static readonly FieldInfo sSimTopics = typeof(SocialComponent).GetField("mActiveTopics", kDrAll);
        static readonly FieldInfo sUnforgettable = typeof(SimActiveTopics).GetField("mUnforgettableTopics", kDrAll);
        static readonly FieldInfo sLastTalking = typeof(SocialComponent).GetField("mLastTalkingTo", kDrAll);
        static readonly FieldInfo sPusher = typeof(SimRoutingComponent).GetField("mPusherSim", kDrAll);
        static readonly FieldInfo sReservedVehicle = typeof(Sim).GetField("ReservedVehicle", kDrAll);
        static readonly FieldInfo sPregDad = typeof(Pregnancy).GetField("mDad", kDrAll);
        static readonly MethodInfo sPregCheckDad = typeof(Pregnancy).GetMethod("CheckForDad", kDrAll);
        static readonly FieldInfo sFisher = typeof(Fish).GetField("mFishingSim", kDrAll);
        static readonly FieldInfo sCarPrev = typeof(CarOwnable).GetField("mPreviousPassengers", kDrAll);
        static readonly FieldInfo sCarPrevCarried = typeof(CarOwnable).GetField("mPreviousCarriedPassengers", kDrAll);
        static readonly FieldInfo sBoatPrev = typeof(BoatOwnable).GetField("mPreviousPassengers", kDrAll);
        static readonly FieldInfo sBoatPrevCarried = typeof(BoatOwnable).GetField("mPreviousCarriedPassengers", kDrAll);
        static readonly FieldInfo sShoeThrown = typeof(HorseshoeCourt).GetField("mPlayersWhoHaveThrown", kDrAll);
        static readonly FieldInfo sShoeScores = typeof(HorseshoeCourt).GetField("mScores", kDrAll);
        static readonly MethodInfo sCancelAging = typeof(AgingManager).GetMethod("CancelAgingAlarmsForSim", kDrAll, null, new Type[] { typeof(AgingState) }, null);
        static readonly Dictionary<SimDescription, Relationship> sNoRelationships = new Dictionary<SimDescription, Relationship>();

        static AlarmHandle sDeadRefAlarm = AlarmHandle.kInvalidHandle;
        static ObjectGuid sDeadRefTask = ObjectGuid.InvalidObjectGuid;
        static StopWatch sDeadRefWatch;
        static long sDeadRefTotalUs;
        // Phases 10-12 walk every GameObject. They share one walk and run only when a Sim object was destroyed or a description
        // disposed since the last walk (EA's kSimDestroyed / kSimDescriptionDisposed), else once per kDeadRefObjHours: phase 12's
        // decayed-relationship case sends no event (docs/205 §2.3a). [2055] walks, [2056] ms in finished walks, [2057] walks an
        // event asked for (the first walk after a load counts as asked).
        const float kDeadRefObjHours = 24f;
        static EventListener sDeadRefDestroyedListener, sDeadRefDisposedListener;
        static bool sDeadRefObjDue = true;
        static float sDeadRefObjLastH;
        static long sDeadRefObjUs;

        static void ApplyDeadRefs()
        {
            Patch(typeof(Relationship), "Get", new Type[] { typeof(SimDescription) }, typeof(RelGetHook), "GetHook", "GetOrig");
            Patch(typeof(AgingManager), "IsAgingStateValid", typeof(AgingValidHook), "ValidHook", "ValidOrig");
        }

        static bool DescGone(SimDescription d) { return d == null || !d.IsValidDescription; }
        static bool SimGone(Sim s) { return s == null || s.HasBeenDestroyed; }

        // ---- deadrefguards ----

        public static class RelGetHook
        {
            public static IEnumerable<Relationship> GetHook(SimDescription d)
            {
                // only a new key for an invalid description is refused; null keeps EA's throw, an existing key EA's answer
                if (d != null && !d.IsValidDescription && !Relationship.sAllRelationships.ContainsKey(d) && ShortcutOn("deadrefguards"))
                {
                    PatchCounters()[393]++;
                    return sNoRelationships.Values;
                }
                return GetOrig(d);
            }
            public static IEnumerable<Relationship> GetOrig(SimDescription d) { return null; } // Relationship.Get(SimDescription)
        }

        public class AgingValidHook
        {
            public bool ValidHook(AgingState state)
            {
                if (state != null && sAgingDesc != null && ShortcutOn("deadrefguards"))
                {
                    SimDescription d = sAgingDesc.GetValue(state) as SimDescription;
                    if (d != null && !d.IsValidDescription && GameStates.IsInWorld() && !GameStates.IsTravelling) { PatchCounters()[394]++; return false; }
                }
                return ValidOrig(state);
            }
            public bool ValidOrig(AgingState state) { return false; } // receives AgingManager.IsAgingStateValid's original header
        }

        // ---- deadrefsweep ----

        // Called on every world load (Instantiator). NeverPersisted: not written into the save.
        public static void StartDeadRefSweep()
        {
            if (AlarmManager.Global == null) return;
            if (sDeadRefAlarm != AlarmHandle.kInvalidHandle) AlarmManager.Global.RemoveAlarm(sDeadRefAlarm);
            sDeadRefTask = ObjectGuid.InvalidObjectGuid;
            if (sDeadRefDestroyedListener != null) EventTracker.RemoveListener(sDeadRefDestroyedListener);
            if (sDeadRefDisposedListener != null) EventTracker.RemoveListener(sDeadRefDisposedListener);
            sDeadRefDestroyedListener = EventTracker.AddListener(EventTypeId.kSimDestroyed, OnDeadRefObjEvent);
            sDeadRefDisposedListener = EventTracker.AddListener(EventTypeId.kSimDescriptionDisposed, OnDeadRefObjEvent);
            sDeadRefObjDue = true;
            sDeadRefAlarm = AlarmManager.Global.AddAlarmRepeating(1f, TimeUnit.Hours, delegate { DeadRefSweepStart(); }, 1f, TimeUnit.Hours, "Ts3Fixes.DeadRefSweep", AlarmType.NeverPersisted, null);
        }

        // Null when a pass may act; else why not.
        static string DeadRefUnsafe(bool atSave)
        {
            if (WorldGone() || GameStates.IsGameShuttingDown) return "no world";
            if (GameStates.IsTravelling || GameStates.IsOnVacation) return "travelling or on vacation";
            if (!atSave && (SaveInProgress() || !GameStates.IsLiveState)) return "saving or not live";
            if (!ShortcutOn("deadrefsweep")) return "switch off";
            Household active = Household.ActiveHousehold;
            if (active == null) return "no active household";
            foreach (SimDescription d in active.AllSimDescriptions) if (DescGone(d)) return "active household member invalid (load not fixed up)";
            return null;
        }

        // Starts the spread pass (the hourly alarm, or the agent). False when one is running or the state is unsafe.
        public static bool DeadRefSweepStart()
        {
            if (!ShortcutOn("deadrefsweep") || TaskAlive(sDeadRefTask)) return false;
            if (DeadRefUnsafe(false) != null) { PatchCounters()[410]++; return false; }
            sDeadRefTask = Simulator.AddObject(new LoopTask(DeadRefSweepRun));
            return true;
        }

        // Sim.Destroy (a townie leaving, a reset) and SimDescription.Dispose: the next pass walks the objects.
        static ListenerAction OnDeadRefObjEvent(Sims3.Gameplay.EventSystem.Event e)
        {
            sDeadRefObjDue = true;
            return ListenerAction.Keep;
        }

        delegate List<object> Gather();

        static void DeadRefSweepRun()
        {
            int[] c = PatchCounters();
            if (sDeadRefWatch == null) sDeadRefWatch = StopWatch.Create(StopWatch.TickStyles.Microseconds);
            List<object> objs = null;
            Gather objWalk = delegate { if (objs == null) { objs = Objects<GameObject>(); c[2055]++; } return objs; };
            bool objByEvent = sDeadRefObjDue, objOpen = false;
            bool objRun = objByEvent || SimClock.ElapsedTime(TimeUnit.Hours) - sDeadRefObjLastH >= kDeadRefObjHours;
            long objUs0 = 0;
            Gather[] phases = {
                delegate { return ToObjects(Relationship.sAllRelationships.Keys); },
                delegate { return AgingStatesToCheck(); },
                delegate { return AlarmOwnersToCheck(); },
                delegate { return ToObjects(Household.EverySimDescription()); },
                delegate { return Objects<Sim>(); },
                delegate { return Objects<Fish>(); },
                delegate { List<object> l = Objects<CarOwnable>(); l.AddRange(Objects<BoatOwnable>()); return l; },
                delegate { return Objects<HorseshoeCourt>(); },
                delegate { return Objects<Dartboard>(); },
                objWalk,
                objWalk,
                objWalk,
                delegate { return Objects<Sim>(); },
            };
            try
            {
                for (int p = 0; p < phases.Length; p++)
                {
                    if (p >= 9 && p <= 11) // phases 10-12
                    {
                        if (!objRun) continue;
                        if (p == 9) { sDeadRefObjDue = false; objOpen = true; objUs0 = sDeadRefTotalUs; } // an event from here on asks again
                    }
                    sDeadRefWatch.Restart();
                    List<object> items = phases[p]();
                    if (p < 3)
                    {
                        int gone = 0;
                        if (p == 0) { foreach (object o in items) if (DescGone(o as SimDescription)) gone++; }
                        else gone = items.Count; // phases 2-3 gather gone items only
                        if (DeadRefsCore.LooksLikeLoad(gone, p == 0 ? items.Count : PhaseTotal(p))) { c[410]++; return; }
                    }
                    if (p == 3) sNoOutfitSeen = 0;
                    SlicedPass<object> pass = new SlicedPass<object>(items);
                    int phase = p;
                    while (true)
                    {
                        pass.RunSlice(delegate(object o) { DeadRefStep(phase, o); }, int.MaxValue, EaSpreadCore.kSliceUs, delegate { return sDeadRefWatch.GetElapsedTime(); });
                        DeadRefSliceDone(c);
                        if (pass.Done) break;
                        Simulator.Sleep(0);
                        if (DeadRefUnsafe(false) != null) { c[410]++; return; } // re-check before acting again
                        sDeadRefWatch.Restart();
                    }
                    if (p == 3) c[392] = sNoOutfitSeen; // #100 end state, counted here so the count is spread too
                    if (p == 11)
                    {
                        objOpen = false;
                        sDeadRefObjLastH = SimClock.ElapsedTime(TimeUnit.Hours);
                        sDeadRefObjUs += sDeadRefTotalUs - objUs0;
                        c[2056] = (int)(sDeadRefObjUs / 1000);
                        if (objByEvent) c[2057]++;
                    }
                    Simulator.Sleep(0);
                    if (DeadRefUnsafe(false) != null) { c[410]++; return; }
                }
                c[405]++;
            }
            finally
            {
                sDeadRefTask = ObjectGuid.InvalidObjectGuid;
                if (objOpen) sDeadRefObjDue = true; // stopped inside phases 10-12: walk again next pass
            }
        }

        static void DeadRefSliceDone(int[] c)
        {
            long us = sDeadRefWatch.GetElapsedTime();
            c[406]++;
            if (us > c[407]) c[407] = (int)Math.Min(us, int.MaxValue);
            sDeadRefTotalUs += us;
            c[408] = (int)(sDeadRefTotalUs / 1000);
        }

        static List<object> ToObjects(IEnumerable items)
        {
            List<object> l = new List<object>();
            foreach (object o in items) l.Add(o);
            return l;
        }

        static List<object> Objects<T>() where T : class
        {
            List<object> l = new List<object>();
            foreach (T o in Sims3.Gameplay.Queries.GetObjects<T>()) l.Add(o);
            return l;
        }

        static int PhaseTotal(int p)
        {
            if (p == 0) return Relationship.sAllRelationships.Count;
            if (p == 1) { IList states = sAgingStates != null && AgingManager.Singleton != null ? sAgingStates.GetValue(AgingManager.Singleton) as IList : null; return states != null ? states.Count : 0; }
            IDictionary idx = sAlarmIndex != null && AlarmManager.Global != null ? sAlarmIndex.GetValue(AlarmManager.Global) as IDictionary : null;
            return idx != null ? idx.Count : 0;
        }

        static List<object> AgingStatesToCheck()
        {
            List<object> l = new List<object>();
            IList states = sAgingStates != null && AgingManager.Singleton != null ? sAgingStates.GetValue(AgingManager.Singleton) as IList : null;
            if (states == null || sAgingDesc == null) return l;
            foreach (AgingState s in states) if (s != null && DescGone(sAgingDesc.GetValue(s) as SimDescription)) l.Add(s);
            return l;
        }

        static List<object> AlarmOwnersToCheck()
        {
            List<object> l = new List<object>();
            IDictionary idx = sAlarmIndex != null && AlarmManager.Global != null ? sAlarmIndex.GetValue(AlarmManager.Global) as IDictionary : null;
            if (idx == null) return l;
            foreach (object owner in idx.Keys) { SimDescription d = owner as SimDescription; if (d != null && !d.IsValidDescription) l.Add(d); }
            return l;
        }

        // One item of one phase, re-tested at its turn.
        static void DeadRefStep(int phase, object o)
        {
            try
            {
                switch (phase)
                {
                    case 0: if (DescGone(o as SimDescription)) RelRemoveKey(o as SimDescription); else RelDropGoneEntries(o as SimDescription); break;
                    case 1: AgingRemove(o as AgingState); break;
                    case 2: AlarmOwnerClear(o as SimDescription); break;
                    case 3: DescriptionHolders(o as SimDescription); break;
                    case 4: SimHolders(o as Sim); break;
                    case 5: FishHolder(o as Fish); break;
                    case 6: VehicleHolders(o); break;
                    case 7: HorseshoeHolders(o as HorseshoeCourt); break;
                    case 8: DartboardHolders(o as Dartboard); break;
                    case 9: UseListHolders(o as GameObject); break;
                    case 10: OwnerHolders(o as GameObject); break;
                    case 11: HandlerHolders(o as GameObject); break;
                    case 12: CelebrityHolders(o as Sim); break;
                }
            }
            catch (Exception) { PatchCounters()[410]++; }
        }

        static void RelRemoveKey(SimDescription d)
        {
            Dictionary<SimDescription, Relationship> mine;
            if (!DescGone(d) || !Relationship.sAllRelationships.TryGetValue(d, out mine)) return;
            int n = 0;
            if (mine != null)
                foreach (KeyValuePair<SimDescription, Relationship> kv in mine)
                {
                    Dictionary<SimDescription, Relationship> theirs;
                    if (kv.Key != null && kv.Key != d && Relationship.sAllRelationships.TryGetValue(kv.Key, out theirs) && theirs != null && theirs.Remove(d)) n++;
                }
            Relationship.sAllRelationships.Remove(d);
            PatchCounters()[395]++;
            PatchCounters()[396] += n;
        }

        static void RelDropGoneEntries(SimDescription d)
        {
            Dictionary<SimDescription, Relationship> mine;
            if (DescGone(d) || !Relationship.sAllRelationships.TryGetValue(d, out mine) || mine == null) return;
            PatchCounters()[396] += DeadRefsCore.DropKeys(mine, DescGone);
        }

        static void AgingRemove(AgingState s)
        {
            SimDescription d = s != null && sAgingDesc != null ? sAgingDesc.GetValue(s) as SimDescription : null;
            if (d == null || !DescGone(d) || AgingManager.Singleton == null) return;
            if (d.AgingState == s) AgingManager.Singleton.RemoveSimDescription(d); // EA's own removal: state out, alarms cancelled
            else
            {
                IList states = sAgingStates.GetValue(AgingManager.Singleton) as IList;
                if (states == null || !states.Contains(s)) return;
                if (sCancelAging != null) sCancelAging.Invoke(AgingManager.Singleton, new object[] { s });
                states.Remove(s);
            }
            PatchCounters()[397]++;
        }

        static void AlarmOwnerClear(SimDescription d)
        {
            if (d == null || d.IsValidDescription || AlarmManager.Global == null) return;
            AlarmManager.Global.OnDispose(d); // what SimDescription.Dispose itself calls; removes the owner's alarms
            PatchCounters()[398]++;
        }

        static void DescriptionHolders(SimDescription d)
        {
            if (DescGone(d)) return;
            int[] c = PatchCounters();
            CareerManager cm = d.CareerManager;
            if (cm != null)
            {
                foreach (Occupation job in new Occupation[] { cm.Occupation, cm.School })
                {
                    if (job == null) continue;
                    c[400] += DeadRefsCore.DropAll(job.Coworkers, DescGone); // not RemoveCoworker: its other side reaches the dead one's CareerManager
                    if (job.FormerBoss != null && DescGone(job.FormerBoss)) { job.FormerBoss = null; c[400]++; }
                }
            }
            if (d.SkillManager != null)
            {
                LogicSkill logic = d.SkillManager.GetSkill<LogicSkill>(SkillNames.Logic);
                if (logic != null && logic.mChessRankingNextChallenger != null && DescGone(logic.mChessRankingNextChallenger)) { logic.mChessRankingNextChallenger = null; c[400]++; } // the getter repicks
            }
            Pregnancy preg = d.Pregnancy;
            if (preg != null && sPregDad != null)
            {
                Sim dad = sPregDad.GetValue(preg) as Sim;
                if (dad != null && dad.HasBeenDestroyed)
                {
                    sPregDad.SetValue(preg, null);
                    if (sPregCheckDad != null) sPregCheckDad.Invoke(preg, null); // EA's own lookup by DadDescriptionId
                    c[403]++;
                }
            }
            if (d.IsValidDescription && d.GetOutfitCount(Sims3.SimIFace.CAS.OutfitCategories.Naked) == 0 && d.GetOutfitCount(Sims3.SimIFace.CAS.OutfitCategories.Everyday) == 0)
                sNoOutfitSeen++;
        }
        static int sNoOutfitSeen;

        static void SimHolders(Sim s)
        {
            if (SimGone(s)) return;
            int[] c = PatchCounters();
            if (s.LookAtManager != null && sLastReact != null)
                c[399] += DeadRefsCore.DropKeys(sLastReact.GetValue(s.LookAtManager) as Dictionary<SimDescription, DateAndTime>, DescGone);
            SocialComponent social = s.SocialComponent;
            if (social != null)
            {
                SimActiveTopics topics = sSimTopics != null ? sSimTopics.GetValue(social) as SimActiveTopics : null;
                if (topics != null && sUnforgettable != null)
                    c[401] += DeadRefsCore.DropAll(sUnforgettable.GetValue(topics) as List<ActiveTopic>, delegate(ActiveTopic t) { return t != null && t.Parameter != null && DescGone(t.Parameter); });
                if (sLastTalking != null) { Sim other = sLastTalking.GetValue(social) as Sim; if (other != null && other.HasBeenDestroyed) { sLastTalking.SetValue(social, null); c[403]++; } }
            }
            SimRoutingComponent routing = s.SimRoutingComponent;
            if (routing != null && sPusher != null) { Sim p = sPusher.GetValue(routing) as Sim; if (p != null && p.HasBeenDestroyed) { sPusher.SetValue(routing, null); c[403]++; } }
            if (sReservedVehicle != null)
            {
                Sims3.Gameplay.Abstracts.GameObject v = sReservedVehicle.GetValue(s) as Sims3.Gameplay.Abstracts.GameObject;
                if (v != null && v.HasBeenDestroyed) { sReservedVehicle.SetValue(s, null); c[403]++; }
            }
        }

        static void FishHolder(Fish f)
        {
            if (f == null || f.HasBeenDestroyed || sFisher == null) return;
            SimDescription d = sFisher.GetValue(f) as SimDescription;
            if (d != null && DescGone(d)) { sFisher.SetValue(f, null); PatchCounters()[402]++; } // EA tests the fisher for null everywhere
        }

        static void VehicleHolders(object v)
        {
            FieldInfo prev = v is CarOwnable ? sCarPrev : v is BoatOwnable ? sBoatPrev : null;
            FieldInfo carried = v is CarOwnable ? sCarPrevCarried : v is BoatOwnable ? sBoatPrevCarried : null;
            int[] c = PatchCounters();
            if (prev != null)
            {
                int removed;
                Sim[] kept = DeadRefsCore.Without(prev.GetValue(v) as Sim[], SimGone, out removed);
                if (kept != null) { prev.SetValue(v, kept); c[404] += removed; } // a new array: GetSimsThatGoHomeWithActor reads every slot
            }
            if (carried != null) c[404] += DeadRefsCore.DropAll(carried.GetValue(v) as List<Sim>, SimGone);
        }

        static void HorseshoeHolders(HorseshoeCourt court)
        {
            if (court == null || court.HasBeenDestroyed) return;
            int[] c = PatchCounters();
            if (sShoeThrown != null) c[404] += DeadRefsCore.DropAll(sShoeThrown.GetValue(court) as List<Sim>, SimGone);
            if (sShoeScores != null) c[404] += DeadRefsCore.DropKeys(sShoeScores.GetValue(court) as Dictionary<Sim, int>, SimGone);
        }

        // Phase 9 (issue #166 build 42, docs/516): EA clears a dartboard's lists only in DoFinishGame. Quit(sim), which every
        // Wait / TakeTurnThrowingDarts cleanup calls, takes the Sim out of its team and Players but leaves CurrentPlayer and
        // CheatingSim; a Sim reset or destroyed mid-game never reaches Quit, and the fields are saved with the board. Gone
        // players leave through EA's Quit (team turn index kept right, an empty team removed, ActorsUsingMe); a gone
        // CurrentPlayer / CheatingSim is cleared (EA tests both for null before use). [2045] entries.
        static void DartboardHolders(Dartboard board)
        {
            if (board == null || board.HasBeenDestroyed) return;
            int[] c = PatchCounters();
            foreach (Sim s in new List<Sim>(board.Players)) if (SimGone(s)) { board.Quit(s); c[2045]++; }
            if (board.CurrentPlayer != null && SimGone(board.CurrentPlayer)) { board.CurrentPlayer = null; c[2045]++; }
            if (board.CheatingSim != null && SimGone(board.CheatingSim)) { board.CheatingSim = null; c[2045]++; }
        }

        static int DartGone(Dartboard b)
        {
            int n = 0;
            foreach (Sim s in b.Players) if (SimGone(s)) n++;
            if (b.CurrentPlayer != null && SimGone(b.CurrentPlayer)) n++;
            if (b.CheatingSim != null && SimGone(b.CheatingSim)) n++;
            return n;
        }

        // Agent check for docs/516 (repro dart-gone-check), a hard state built and undone:
        //   "seed":   a dartboard made out of world holds one townie as team player, CurrentPlayer and CheatingSim; that Sim
        //             object is then destroyed (what a reset does; the description stays and is instantiated again at the
        //             end). The Sim: not selectable, no pet, not of the active household, not on its home lot, no role or service.
        //   "state":  DartState: gone entries on that board, players, teams, the current / cheating flags, destroyed.
        //   "expect left" (switch off) / "expect cleaned" (switch on): throws unless the board still holds the three gone
        //             entries (left) or none, with Players and Teams empty (cleaned). Either way the board is destroyed
        //             and the townie put back where it stood.
        public class DartState { public int gone, players, teams; public bool current, cheating, destroyed; }
        static Dartboard sDartBoard;
        static SimDescription sDartDesc;
        static Vector3 sDartPos;

        public static object DeadRefDartCheck(string what)
        {
            if (what == "seed")
            {
                // the Late Night catalog names: DartboardCheap, DartboardExpensive; the one-argument overload builds a base-game key
                Dartboard board = GlobalFunctions.CreateObjectOutOfWorld("DartboardCheap", ProductVersion.EP3) as Dartboard;
                if (board == null) throw new InvalidOperationException("dart seed: CreateObjectOutOfWorld(\"DartboardCheap\", EP3) made nothing");
                Sim victim = DrPickTownie();
                if (victim == null) { board.Destroy(); throw new InvalidOperationException("dart seed: no townie to use"); }
                Dartboard.Team team = new Dartboard.Team();
                team.AddPlayerToTeam(victim, board);
                board.Teams.Add(team); board.CurrentTeam = team; board.CurrentPlayer = victim; board.CheatingSim = victim;
                sDartBoard = board; sDartDesc = victim.SimDescription; sDartPos = victim.Position;
                string name = victim.FullName;
                victim.Destroy();
                return "dart seed: " + name + " on the board as player, current player and cheating Sim, then destroyed";
            }
            Dartboard b = sDartBoard;
            if (b == null) throw new InvalidOperationException("dart " + what + ": no seed");
            if (what == "state")
            {
                DartState st = new DartState();
                st.gone = DartGone(b); st.players = b.Players.Count; st.teams = b.Teams.Count;
                st.current = b.CurrentPlayer != null; st.cheating = b.CheatingSim != null;
                st.destroyed = sDartDesc.CreatedSim == null || sDartDesc.CreatedSim.HasBeenDestroyed;
                return st;
            }
            if (what != "expect left" && what != "expect cleaned") return "dart <seed|state|expect left|expect cleaned>";
            bool on = ShortcutOn("deadrefsweep");
            int gone = DartGone(b), players = b.Players.Count, teams = b.Teams.Count;
            bool ok = what == "expect left" ? !on && gone == 3 : on && gone == 0 && players == 0 && teams == 0 && b.CurrentPlayer == null && b.CheatingSim == null;
            string line = "gone " + gone + ", players " + players + ", teams " + teams + ", current " + (b.CurrentPlayer != null) + ", cheating " + (b.CheatingSim != null) + " (switch " + on + ")";
            sDartBoard = null;
            b.Destroy();
            string back = DrPutBack(sDartDesc, sDartPos);
            if (!ok) throw new InvalidOperationException("dart " + what + ": wrong outcome; " + line + "; " + sDartDesc.FullName + " " + back);
            return "as expected; " + line + "; " + sDartDesc.FullName + " " + back;
        }

        // Phase 10 (issue #166 build 40, docs/517): GameObject.mActorsUsingMe, mReferenceList and mRoutingReferenceList are
        // persisted List<Sim>s; a Sim reset or destroyed while using, referencing or routing to an object never runs the
        // paired Remove, so the destroyed Sim object stays in the list and goes into every save. Gone Sims leave through
        // EA's own RemoveFromUseList (science component, SimLine queue), RemoveFromReferenceList and
        // RemoveFromRoutingReferenceList, one call per occurrence. [2046] entries.
        static void UseListHolders(GameObject o)
        {
            if (o == null || o.HasBeenDestroyed) return;
            int[] c = PatchCounters();
            foreach (Sim s in new List<Sim>(o.ActorsUsingMe)) if (SimGone(s)) { o.RemoveFromUseList(s); c[2046]++; }
            foreach (Sim s in new List<Sim>(o.ReferenceList)) if (SimGone(s)) { o.RemoveFromReferenceList(s); c[2046]++; }
            List<Sim> routing = o.RoutingReferenceList; // null until the first route to the object
            if (routing != null) foreach (Sim s in new List<Sim>(routing)) if (SimGone(s)) { o.RemoveFromRoutingReferenceList(s); c[2046]++; }
        }

        static int UseListGone(GameObject o)
        {
            int n = 0;
            foreach (Sim s in o.ActorsUsingMe) if (SimGone(s)) n++;
            foreach (Sim s in o.ReferenceList) if (SimGone(s)) n++;
            List<Sim> routing = o.RoutingReferenceList;
            if (routing != null) foreach (Sim s in routing) if (SimGone(s)) n++;
            return n;
        }

        // Agent check for docs/517 (repro use-list-gone-check), a hard state built and undone, like DeadRefDartCheck:
        //   "seed":   a social jig made out of world holds one townie in its use list, reference list and routing reference
        //             list; that Sim object is then destroyed (the description stays; put back at the end).
        //   "state":  UseState: gone entries on that object, the three list counts, destroyed.
        //   "expect left" (switch off) / "expect cleaned" (switch on): throws unless the object still holds the three gone
        //             entries (left) or none, with all three lists empty (cleaned). Either way the jig is destroyed and
        //             the townie put back.
        public class UseState { public int gone, use, reference, routing; public bool destroyed; }
        static GameObject sUseObj;
        static SimDescription sUseDesc;
        static Vector3 sUsePos;

        public static object DeadRefUseListCheck(string what)
        {
            if (what == "seed")
            {
                GameObject jig = GlobalFunctions.CreateObjectOutOfWorld("SocialJigTwoPerson") as GameObject;
                if (jig == null) throw new InvalidOperationException("use seed: CreateObjectOutOfWorld(\"SocialJigTwoPerson\") made nothing");
                Sim victim = DrPickTownie();
                if (victim == null) { jig.Destroy(); throw new InvalidOperationException("use seed: no townie to use"); }
                jig.AddToUseList(victim); jig.AddToReferenceList(victim); jig.AddToRoutingReferenceList(victim);
                sUseObj = jig; sUseDesc = victim.SimDescription; sUsePos = victim.Position;
                string name = victim.FullName;
                victim.Destroy();
                return "use seed: " + name + " in the jig's use, reference and routing reference lists, then destroyed";
            }
            GameObject o = sUseObj;
            if (o == null) throw new InvalidOperationException("use " + what + ": no seed");
            if (what == "state")
            {
                UseState st = new UseState();
                st.gone = UseListGone(o); st.use = o.ActorsUsingMe.Count; st.reference = o.ReferenceList.Count;
                st.routing = o.RoutingReferenceList != null ? o.RoutingReferenceList.Count : 0;
                st.destroyed = sUseDesc.CreatedSim == null || sUseDesc.CreatedSim.HasBeenDestroyed;
                return st;
            }
            if (what != "expect left" && what != "expect cleaned") return "use <seed|state|expect left|expect cleaned>";
            bool on = ShortcutOn("deadrefsweep");
            int gone = UseListGone(o), use = o.ActorsUsingMe.Count, reference = o.ReferenceList.Count, routing = o.RoutingReferenceList != null ? o.RoutingReferenceList.Count : 0;
            bool ok = what == "expect left" ? !on && gone == 3 : on && gone == 0 && use == 0 && reference == 0 && routing == 0;
            string line = "gone " + gone + ", use " + use + ", reference " + reference + ", routing " + routing + " (switch " + on + ")";
            sUseObj = null;
            o.Destroy();
            string back = DrPutBack(sUseDesc, sUsePos);
            if (!ok) throw new InvalidOperationException("use " + what + ": wrong outcome; " + line + "; " + sUseDesc.FullName + " " + back);
            return "as expected; " + line + "; " + sUseDesc.FullName + " " + back;
        }

        // Phase 11 (issue #166 build 43, docs/519): ObjectOwnerComponent.mOwner (the Sim a reward came from, the tooltip's
        // "from <Sim>", or a theft's victim) and mThief are saved by description id and re-read at load as that description's
        // CreatedSim; in a running world nothing updates them when that Sim object is destroyed (a townie leaving, a reset).
        // As EA's ImportContent would at the next load: a gone Sim becomes the live instance of its description when there
        // is one; a reward giver with no live instance drops the component (RemoveComponent disposes it, which takes the
        // Return To Owner interaction away); a theft with no live instance is left, the stolen state belongs to it. [2047].
        static readonly FieldInfo sOwnerOwner = typeof(ObjectOwnerComponent).GetField("mOwner", kDrAll);
        static readonly FieldInfo sOwnerThief = typeof(ObjectOwnerComponent).GetField("mThief", kDrAll);

        static void OwnerHolders(GameObject o)
        {
            if (o == null || o.HasBeenDestroyed || sOwnerOwner == null || sOwnerThief == null) return;
            ObjectOwnerComponent c = o.ObjectOwnerComponent;
            if (c == null) return;
            Sim owner = sOwnerOwner.GetValue(c) as Sim, thief = sOwnerThief.GetValue(c) as Sim;
            bool ownerGone = owner != null && owner.HasBeenDestroyed, thiefGone = thief != null && thief.HasBeenDestroyed;
            if (!ownerGone && !thiefGone) return;
            int[] n = PatchCounters();
            Sim liveOwner = ownerGone ? LiveSim(owner) : null, liveThief = thiefGone ? LiveSim(thief) : null;
            if (ownerGone && liveOwner == null && thief == null) { o.RemoveComponent<ObjectOwnerComponent>(); n[2047]++; return; }
            if (ownerGone && liveOwner != null) { sOwnerOwner.SetValue(c, liveOwner); n[2047]++; }
            if (thiefGone && liveThief != null) { sOwnerThief.SetValue(c, liveThief); n[2047]++; }
        }

        // The live Sim object of a gone Sim's description, or null (a destroyed Sim keeps its description).
        static Sim LiveSim(Sim gone)
        {
            SimDescription d = gone != null ? gone.SimDescription : null;
            return d != null && d.IsValidDescription && d.CreatedSim != null && !d.CreatedSim.HasBeenDestroyed ? d.CreatedSim : null;
        }

        static int OwnerGone(GameObject o)
        {
            ObjectOwnerComponent c = o != null && !o.HasBeenDestroyed && sOwnerOwner != null ? o.ObjectOwnerComponent : null;
            if (c == null) return 0;
            Sim owner = sOwnerOwner.GetValue(c) as Sim, thief = sOwnerThief.GetValue(c) as Sim;
            return (owner != null && owner.HasBeenDestroyed ? 1 : 0) + (thief != null && thief.HasBeenDestroyed ? 1 : 0);
        }

        // Agent check for docs/519 (repro object-owner-gone-check), a hard state built and undone, like DeadRefDartCheck:
        //   "seed":    two social jigs made out of world, each a reward from one townie (SetRewardGiver); both Sim objects
        //              are then destroyed, as a reset does.
        //   "putback": the first townie's Sim object again where it stood (a live instance for the relink case; the second
        //              stays gone, the drop case).
        //   "state":   OwnerState: gone entries on the two jigs, relinked (the first jig's owner is the live Sim of that
        //              description), dropped (the second jig has no owner component), both Sims destroyed, the first live.
        //   "expect left" (switch off) / "expect cleaned" (switch on): throws unless both gone entries are still there
        //              (left) or the first jig was relinked and the second dropped (cleaned). Either way the jigs are
        //              destroyed and the second townie put back.
        public class OwnerState { public int gone; public bool relinked, dropped, destroyed, live; }
        static GameObject sOwnJigA, sOwnJigB;
        static SimDescription sOwnDescA, sOwnDescB;
        static Vector3 sOwnPosA, sOwnPosB;

        // The reward-giver component by hand: EA's GameObject.SetRewardGiver passes a null thief, and AddComponentInternal
        // types every argument with GetType() to find the constructor, so that call throws NullReferenceException for any
        // Sim [verified live 2026-11-02, repro run 1]. The one-argument constructor and the owner field by reflection instead.
        static void DrGiveReward(GameObject jig, Sim s)
        {
            if (!jig.AddComponent<ObjectOwnerComponent>(new object[0]) || jig.ObjectOwnerComponent == null) throw new InvalidOperationException("owner seed: AddComponent<ObjectOwnerComponent> refused on " + jig.GetType().Name);
            sOwnerOwner.SetValue(jig.ObjectOwnerComponent, s);
        }

        public static object DeadRefOwnerCheck(string what)
        {
            if (what == "seed")
            {
                Sim a = DrPickTownie(null), b = a != null ? DrPickTownie(a.SimDescription) : null;
                if (b == null) throw new InvalidOperationException("owner seed: fewer than two townies to use");
                GameObject jigA = GlobalFunctions.CreateObjectOutOfWorld("SocialJigTwoPerson") as GameObject;
                GameObject jigB = GlobalFunctions.CreateObjectOutOfWorld("SocialJigTwoPerson") as GameObject;
                if (jigA == null || jigB == null) throw new InvalidOperationException("owner seed: CreateObjectOutOfWorld(\"SocialJigTwoPerson\") made nothing");
                DrGiveReward(jigA, a); DrGiveReward(jigB, b);
                sOwnJigA = jigA; sOwnJigB = jigB; sOwnDescA = a.SimDescription; sOwnDescB = b.SimDescription; sOwnPosA = a.Position; sOwnPosB = b.Position;
                string names = a.FullName + " and " + b.FullName;
                a.Destroy(); b.Destroy();
                // the destroy completes at the end of the tick (CreatedSim is cleared then): "putback" follows a destroyed poll
                return "owner seed: rewards from " + names + ", both Sim objects destroyed";
            }
            GameObject ja = sOwnJigA, jb = sOwnJigB;
            if (ja == null || jb == null) throw new InvalidOperationException("owner " + what + ": no seed");
            if (what == "putback") return "owner putback: " + sOwnDescA.FullName + " " + DrPutBack(sOwnDescA, sOwnPosA);
            Sim liveA = sOwnDescA.CreatedSim != null && !sOwnDescA.CreatedSim.HasBeenDestroyed ? sOwnDescA.CreatedSim : null;
            ObjectOwnerComponent ca = ja.ObjectOwnerComponent;
            bool relinked = liveA != null && ca != null && sOwnerOwner.GetValue(ca) == liveA, dropped = jb.ObjectOwnerComponent == null;
            int gone = OwnerGone(ja) + OwnerGone(jb);
            if (what == "state")
            {
                OwnerState st = new OwnerState();
                st.gone = gone; st.relinked = relinked; st.dropped = dropped; st.live = liveA != null;
                st.destroyed = (sOwnDescA.CreatedSim == null || sOwnDescA.CreatedSim.HasBeenDestroyed) && (sOwnDescB.CreatedSim == null || sOwnDescB.CreatedSim.HasBeenDestroyed);
                return st;
            }
            if (what != "expect left" && what != "expect cleaned") return "owner <seed|putback|state|expect left|expect cleaned>";
            bool on = ShortcutOn("deadrefsweep");
            bool ok = what == "expect left" ? !on && gone == 2 && !relinked && !dropped : on && gone == 0 && relinked && dropped;
            string line = "gone " + gone + ", relinked " + relinked + ", dropped " + dropped + " (switch " + on + ")";
            sOwnJigA = sOwnJigB = null;
            ja.Destroy(); jb.Destroy();
            string back = DrPutBack(sOwnDescB, sOwnPosB);
            if (!ok) throw new InvalidOperationException("owner " + what + ": wrong outcome; " + line + "; " + sOwnDescB.FullName + " " + back);
            return "as expected; " + line + "; " + sOwnDescB.FullName + " " + back;
        }

        // Phase 12 (issue #166 build 41, docs/522): GameObjectRelationship's constructor subscribes its GameObjectDisposed handler
        // to the object's ObjectDisposed event (a car's or a bird's) and nothing unsubscribes it: SimDescription.Dispose leaves
        // GameObjectRelationships alone and a decayed relationship (RemoveRelationship) only leaves the Sim's list. The handler
        // is saved with the object, so a deleted description, or a relationship nobody holds, stays reachable from the object
        // (a load-time graph reset then resets the car). Such handlers leave the invocation list; run at disposal they would
        // only remove the relationship from a list that no longer has it, or from a deleted description's list. [2048].
        static readonly FieldInfo sObjDisposed = typeof(GameObject).GetField("ObjectDisposed", kDrAll);
        static readonly FieldInfo sRelDesc = typeof(GameObjectRelationship).GetField("mSimDesc", kDrAll);

        static bool RelGone(GameObjectRelationship r)
        {
            SimDescription d = sRelDesc != null ? sRelDesc.GetValue(r) as SimDescription : null;
            return d == null || !d.IsValidDescription || d.GameObjectRelationships == null || !d.GameObjectRelationships.Contains(r);
        }

        static void HandlerHolders(GameObject o)
        {
            if (o == null || o.HasBeenDestroyed || sObjDisposed == null || sRelDesc == null) return;
            Delegate ev = sObjDisposed.GetValue(o) as Delegate;
            if (ev == null) return;
            int[] c = PatchCounters(); bool changed = false;
            foreach (Delegate d in ev.GetInvocationList())
            {
                GameObjectRelationship r = d.Target as GameObjectRelationship;
                if (r == null || !RelGone(r)) continue;
                ev = Delegate.Remove(ev, d); c[2048]++; changed = true;
            }
            if (changed) sObjDisposed.SetValue(o, ev);
        }

        static int HandlerGone(GameObject o)
        {
            Delegate ev = o != null && !o.HasBeenDestroyed && sObjDisposed != null && sRelDesc != null ? sObjDisposed.GetValue(o) as Delegate : null;
            if (ev == null) return 0;
            int n = 0;
            foreach (Delegate d in ev.GetInvocationList()) { GameObjectRelationship r = d.Target as GameObjectRelationship; if (r != null && RelGone(r)) n++; }
            return n;
        }

        // Agent check for docs/522 (repro disposed-handler-gone-check), a hard state built and undone without destroying anything:
        //   "seed":   a car relationship of one townie's description with a vehicle of the world, made through EA's own
        //             Vehicle.GetNewRelationship (which subscribes the handler) and never added to the description's list:
        //             the decayed-relationship case of RelGone.
        //   "state":  HandlerState: gone handlers on that vehicle, mine (our relationship's handler still subscribed), handlers.
        //   "expect left" (switch off) / "expect cleaned" (switch on): throws unless our handler is still there (left) or gone
        //             (cleaned). Either way a handler of ours that is still subscribed is removed.
        public class HandlerState { public int gone, handlers; public bool mine, destroyed; }
        static GameObject sHdObj; static GameObjectRelationship sHdRel; static bool sHdMade;

        static bool HdMine(GameObject o)
        {
            Delegate ev = sObjDisposed.GetValue(o) as Delegate;
            if (ev == null) return false;
            foreach (Delegate d in ev.GetInvocationList()) if (d.Target == sHdRel) return true;
            return false;
        }

        public static object DeadRefHandlerCheck(string what)
        {
            if (sObjDisposed == null || sRelDesc == null) throw new InvalidOperationException("handler check: ObjectDisposed or mSimDesc field not found");
            if (what == "seed")
            {
                // a car made out of world, as the dart seed makes its board: a vehicle of the world may be a taxi or a carpool
                // that despawns within the minute (run 1 lost its LightweightTaxi 20 s after the seed, and a destroyed object
                // is outside the pass), destroyed again at "expect"; the pick of a world vehicle is the fallback
                Vehicle car = GlobalFunctions.CreateObjectOutOfWorld("CarHatchback") as Vehicle;
                sHdMade = car != null;
                if (car == null) foreach (Vehicle v in Sims3.Gameplay.Queries.GetObjects<Vehicle>()) if (!v.HasBeenDestroyed && (v.InInventory || v.LotCurrent != null) && !(v is CarNpc) && !(v is CarpoolWithDriver)) { car = v; break; }
                Sim townie = DrPickTownie();
                if (car == null || townie == null) throw new InvalidOperationException("handler seed: no vehicle or no townie");
                GameObjectRelationship rel = car.GetNewRelationship(townie.SimDescription);
                if (rel == null) throw new InvalidOperationException("handler seed: GetNewRelationship returned null");
                sHdObj = car; sHdRel = rel;
                return "handler seed: a car relationship of " + townie.FullName + " with " + car.GetType().Name + (sHdMade ? " made out of world" : " of the world") + ", subscribed and not in the description's list";
            }
            GameObject o = sHdObj;
            if (o == null || sHdRel == null) throw new InvalidOperationException("handler " + what + ": no seed");
            bool mine = HdMine(o);
            Delegate all = sObjDisposed.GetValue(o) as Delegate;
            if (what == "state") { HandlerState st = new HandlerState(); st.gone = HandlerGone(o); st.mine = mine; st.destroyed = o.HasBeenDestroyed; st.handlers = all != null ? all.GetInvocationList().Length : 0; return st; }
            if (what != "expect left" && what != "expect cleaned") return "handler <seed|state|expect left|expect cleaned>";
            if (o.HasBeenDestroyed) { sHdObj = null; sHdRel = null; throw new InvalidOperationException("handler " + what + ": the seeded vehicle was destroyed meanwhile (the hard state is gone, not cleaned)"); }
            bool on = ShortcutOn("deadrefsweep");
            bool ok = what == "expect left" ? !on && mine : on && !mine;
            string line = "mine " + mine + ", gone " + HandlerGone(o) + " (switch " + on + ")";
            if (mine) { foreach (Delegate d in all.GetInvocationList()) if (d.Target == sHdRel) all = Delegate.Remove(all, d); sObjDisposed.SetValue(o, all); }
            if (sHdMade) o.Destroy();
            sHdObj = null; sHdRel = null; sHdMade = false;
            if (!ok) throw new InvalidOperationException("handler " + what + ": wrong outcome; " + line);
            return "as expected; " + line + (mine ? "; our handler removed" : "");
        }

        // Phase 13 (issue #166, docs/205 §4 item, docs/523): a celebrity Sim's Celebrity object (Sim.mCelebrity, made when the
        // celebrity broadcaster starts) keeps mWaitingToReact, the Sims who rolled a reaction to the celebrity and have not
        // reacted yet. A reactor reset or destroyed before its reaction never leaves (only the reaction's own end removes it),
        // and the list is saved with the Sim. Gone reactors leave through EA's own RemoveSimFromWaitingToReactList. [2049].
        static readonly FieldInfo sSimCelebrity = typeof(Sim).GetField("mCelebrity", kDrAll);
        static readonly FieldInfo sCelebWaiting = typeof(Celebrity).GetField("mWaitingToReact", kDrAll);

        static List<Sim> CelebWaiting(Sim s)
        {
            Celebrity c = s != null && !s.HasBeenDestroyed && sSimCelebrity != null && sCelebWaiting != null ? sSimCelebrity.GetValue(s) as Celebrity : null;
            return c != null ? sCelebWaiting.GetValue(c) as List<Sim> : null;
        }

        static void CelebrityHolders(Sim s)
        {
            List<Sim> waiting = CelebWaiting(s);
            if (waiting == null || waiting.Count == 0) return;
            Celebrity c = (Celebrity)sSimCelebrity.GetValue(s);
            int[] n = PatchCounters();
            foreach (Sim r in new List<Sim>(waiting)) if (SimGone(r)) { c.RemoveSimFromWaitingToReactList(r); n[2049]++; }
        }

        static int CelebrityGone(Sim s)
        {
            List<Sim> waiting = CelebWaiting(s); int n = 0;
            if (waiting != null) foreach (Sim r in waiting) if (SimGone(r)) n++;
            return n;
        }

        // Agent check for docs/523 (repro celebrity-waiting-gone-check), a hard state built and undone, like DeadRefDartCheck:
        //   "seed":   a Sim of the world whose Celebrity object exists (a celebrity whose broadcaster has run) gets one townie
        //             in its waiting-to-react list; that townie's Sim object is then destroyed (put back at the end).
        //   "state":  CelebState: gone entries in that list, its count, destroyed.
        //   "expect left" (switch off) / "expect cleaned" (switch on): throws unless the gone entry is still there (left) or
        //             gone (cleaned). Either way an entry of ours still there is removed and the townie put back.
        public class CelebState { public int gone, waiting; public bool destroyed; }
        static Sim sCeHost; static SimDescription sCeDesc; static Vector3 sCePos;

        public static object DeadRefCelebrityCheck(string what)
        {
            if (sSimCelebrity == null || sCelebWaiting == null) throw new InvalidOperationException("celebrity check: mCelebrity or mWaitingToReact field not found");
            if (what == "seed")
            {
                Sim host = null;
                foreach (Sim s in Sims3.Gameplay.Queries.GetObjects<Sim>()) if (CelebWaiting(s) != null) { host = s; break; }
                Sim victim = DrPickTownie(host != null ? host.SimDescription : null);
                if (host == null || victim == null) throw new InvalidOperationException("celebrity seed: no Sim with a Celebrity object, or no townie");
                CelebWaiting(host).Add(victim);
                sCeHost = host; sCeDesc = victim.SimDescription; sCePos = victim.Position;
                string names = victim.FullName + " waiting to react to " + host.FullName;
                victim.Destroy();
                return "celebrity seed: " + names + ", the reactor's Sim object destroyed";
            }
            Sim h = sCeHost;
            if (h == null || sCeDesc == null) throw new InvalidOperationException("celebrity " + what + ": no seed");
            List<Sim> w = CelebWaiting(h);
            int gone = w != null ? CelebrityGone(h) : 0;
            if (what == "state")
            {
                CelebState st = new CelebState(); st.gone = gone; st.waiting = w != null ? w.Count : 0;
                st.destroyed = sCeDesc.CreatedSim == null || sCeDesc.CreatedSim.HasBeenDestroyed; return st;
            }
            if (what != "expect left" && what != "expect cleaned") return "celebrity <seed|state|expect left|expect cleaned>";
            bool on = ShortcutOn("deadrefsweep");
            bool ok = what == "expect left" ? !on && gone >= 1 : on && gone == 0;
            string line = "gone " + gone + ", waiting " + (w != null ? w.Count : 0) + " (switch " + on + ")";
            if (w != null) foreach (Sim r in new List<Sim>(w)) if (SimGone(r)) w.Remove(r);
            sCeHost = null;
            string back = DrPutBack(sCeDesc, sCePos);
            if (!ok) throw new InvalidOperationException("celebrity " + what + ": wrong outcome; " + line + "; " + sCeDesc.FullName + " " + back);
            return "as expected; " + line + "; " + sCeDesc.FullName + " " + back;
        }

        // A townie for a hard state: not selectable, no pet, teen or older, not of the active household, not on its home
        // lot, no role or service, a valid description.
        static Sim DrPickTownie() { return DrPickTownie(null); }
        static Sim DrPickTownie(SimDescription except)
        {
            Household active = Household.ActiveHousehold;
            Lot home = active != null ? active.LotHome : null;
            foreach (Sim s in Sims3.Gameplay.Queries.GetObjects<Sim>())
            {
                SimDescription d = s.SimDescription;
                if (s.IsSelectable || s.IsPet || s.HasBeenDestroyed || d == null || d == except || !d.IsValidDescription || !d.TeenOrAbove) continue;
                if (s.Household == active || (home != null && s.LotCurrent == home) || d.AssignedRole != null || d.CreatedByService != null) continue;
                return s;
            }
            return null;
        }

        // The townie's Sim object again where it stood (what a reset does after its Destroy).
        static string DrPutBack(SimDescription d, Vector3 pos)
        {
            if (d == null || !d.IsValidDescription) return "not put back (description gone)";
            if (d.CreatedSim != null && !d.CreatedSim.HasBeenDestroyed) return "already in the world";
            return d.Instantiate(pos) != null ? "put back" : "Instantiate returned null";
        }

        // Before a save (DisposeSaveHook): the three big dictionaries only, gone entries only (field reads per entry; the
        // spread pass did the rest during play). Returns the work line.
        public static string DeadRefSavePass()
        {
            if (!ShortcutOn("deadrefsweep")) return "off";
            int[] c = PatchCounters();
            string why = DeadRefUnsafe(true);
            if (why != null) { c[410]++; return "refused: " + why; }
            if (sDeadRefWatch == null) sDeadRefWatch = StopWatch.Create(StopWatch.TickStyles.Microseconds);
            sDeadRefWatch.Restart();
            int k0 = c[395], a0 = c[397], o0 = c[398];
            List<object> keys = new List<object>();
            foreach (SimDescription d in Relationship.sAllRelationships.Keys) if (DescGone(d)) keys.Add(d);
            if (DeadRefsCore.LooksLikeLoad(keys.Count, Relationship.sAllRelationships.Count)) { c[410]++; return "refused: most relationship keys invalid"; }
            foreach (object d in keys) RelRemoveKey(d as SimDescription);
            foreach (object s in AgingStatesToCheck()) AgingRemove(s as AgingState);
            foreach (object d in AlarmOwnersToCheck()) AlarmOwnerClear(d as SimDescription);
            long us = sDeadRefWatch.GetElapsedTime();
            if (us > c[409]) c[409] = (int)Math.Min(us, int.MaxValue);
            return "save pass: -" + (c[395] - k0) + " relationship keys, -" + (c[397] - a0) + " aging states, -" + (c[398] - o0) + " alarm owners, " + us + " us";
        }

        // Agent checks (repros bp-deadrefs-check, bp-deadrefs-sweep). No game state is changed except by "sweep"/"savepass"
        // and the made-up description of "relget" / "aging" (removed again).
        //   "count":    gone entries per holder now (no change).
        //   "relget":   Relationship.Get on a new, never-valid SimDescription: off = EA adds a key (removed again), on = none.
        //   "aging":    AgingManager.IsAgingStateValid on an AgingState of that kind of description: off true, on false.
        //   "sweep":    starts the spread pass now (deadrefsweep must be on); counters show it finish.
        //   "savepass": the pre-save pass now.
        delegate bool AgingValidFn(AgingState state);

        public static string DeadRefCheck(string what)
        {
            switch (what)
            {
                case "count": return DeadRefCounts();
                case "relget":
                {
                    SimDescription fake = new SimDescription(); // never Fixup'd: IsValidDescription false
                    try { Relationship.Get(fake); return "relget: key added " + Relationship.sAllRelationships.ContainsKey(fake); }
                    finally { Relationship.sAllRelationships.Remove(fake); }
                }
                case "aging":
                {
                    ConstructorInfo ctor = typeof(AgingState).GetConstructor(kDrAll, null, new Type[] { typeof(SimDescription) }, null);
                    MethodInfo valid = typeof(AgingManager).GetMethod("IsAgingStateValid", kDrAll);
                    if (ctor == null || valid == null || AgingManager.Singleton == null) return "aging: not found";
                    object state = ctor.Invoke(new object[] { new SimDescription() });
                    // swapped method: a delegate, not MethodInfo.Invoke (see EaFixes PrefRemoveFn)
                    return "aging: valid " + ((AgingValidFn)Delegate.CreateDelegate(typeof(AgingValidFn), AgingManager.Singleton, valid))((AgingState)state);
                }
                case "sweep": sDeadRefObjDue = true; return "sweep: started " + DeadRefSweepStart() + " (" + (DeadRefUnsafe(false) ?? "safe") + ")";
                case "savepass": return DeadRefSavePass();
                default: return "unknown check " + what + " (count, relget, aging, sweep, savepass; dartboards: DeadRefDartCheck, use lists: DeadRefUseListCheck, owner components: DeadRefOwnerCheck, disposed handlers: DeadRefHandlerCheck, celebrity reactors: DeadRefCelebrityCheck)";
            }
        }

        static string DeadRefCounts()
        {
            int relGone = 0, relEntries = 0;
            foreach (KeyValuePair<SimDescription, Dictionary<SimDescription, Relationship>> kv in Relationship.sAllRelationships)
            {
                if (DescGone(kv.Key)) relGone++;
                else if (kv.Value != null) foreach (SimDescription o in kv.Value.Keys) if (DescGone(o)) relEntries++;
            }
            int react = 0, topics = 0, coworkers = 0;
            foreach (Sim s in Sims3.Gameplay.Queries.GetObjects<Sim>())
            {
                if (s.LookAtManager != null && sLastReact != null)
                {
                    Dictionary<SimDescription, DateAndTime> r = sLastReact.GetValue(s.LookAtManager) as Dictionary<SimDescription, DateAndTime>;
                    if (r != null) foreach (SimDescription k in r.Keys) if (DescGone(k)) react++;
                }
                SimActiveTopics t = s.SocialComponent != null && sSimTopics != null ? sSimTopics.GetValue(s.SocialComponent) as SimActiveTopics : null;
                List<ActiveTopic> u = t != null && sUnforgettable != null ? sUnforgettable.GetValue(t) as List<ActiveTopic> : null;
                if (u != null) foreach (ActiveTopic a in u) if (a != null && a.Parameter != null && DescGone(a.Parameter)) topics++;
            }
            int uselists = 0, owners = 0, handlers = 0;
            int celebs = 0;
            foreach (Sim cs in Sims3.Gameplay.Queries.GetObjects<Sim>()) celebs += CelebrityGone(cs);
            foreach (GameObject g in Sims3.Gameplay.Queries.GetObjects<GameObject>()) { uselists += UseListGone(g); owners += OwnerGone(g); handlers += HandlerGone(g); }
            int darts = 0;
            foreach (Dartboard b in Sims3.Gameplay.Queries.GetObjects<Dartboard>()) darts += DartGone(b);
            foreach (SimDescription d in Household.EverySimDescription())
            {
                if (d == null || d.CareerManager == null) continue;
                foreach (Occupation job in new Occupation[] { d.CareerManager.Occupation, d.CareerManager.School })
                    if (job != null && job.Coworkers != null) foreach (SimDescription w in job.Coworkers) if (DescGone(w)) coworkers++;
            }
            return "count: relationship keys gone " + relGone + "/" + Relationship.sAllRelationships.Count + ", entries naming gone " + relEntries
                + ", aging states gone " + AgingStatesToCheck().Count + ", alarm owners gone " + AlarmOwnersToCheck().Count
                + ", react times gone " + react + ", topics gone " + topics + ", coworkers gone " + coworkers + ", dartboard entries gone " + darts + ", use-list entries gone " + uselists + ", owner entries gone " + owners + ", disposed handlers gone " + handlers + ", celebrity reactors gone " + celebs
                + ", no-outfit descriptions " + NoOutfitDescriptions().Count + " | " + (DeadRefUnsafe(false) ?? "safe");
        }
    }
}
