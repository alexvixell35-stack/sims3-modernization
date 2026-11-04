using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Careers;
using Sims3.Gameplay.Core;
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
    //     boats (previous passengers), 8 horseshoe courts (throwers and scores).
    //     [395] relationship keys removed, [396] relationship entries removed, [397] aging states removed, [398] alarm owners
    //     cleared, [399] react-time entries, [400] coworker / boss / chess entries, [401] topics, [402] fishers, [403] Sim
    //     fields (pusher, talking, vehicle, dad), [404] passenger and horseshoe entries, [405] passes finished, [406] slices,
    //     [407] longest slice us, [408] total pass ms, [409] longest save pass us, [410] passes refused or stopped (unsafe state, switch off) and items that threw.
    //   Not built (docs/205 §4): CookingProcess appliance handlers, ObjectOwnerComponent.mThief, Celebrity.mWaitingToReact,
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

        delegate List<object> Gather();

        static void DeadRefSweepRun()
        {
            int[] c = PatchCounters();
            if (sDeadRefWatch == null) sDeadRefWatch = StopWatch.Create(StopWatch.TickStyles.Microseconds);
            Gather[] phases = {
                delegate { return ToObjects(Relationship.sAllRelationships.Keys); },
                delegate { return AgingStatesToCheck(); },
                delegate { return AlarmOwnersToCheck(); },
                delegate { return ToObjects(Household.EverySimDescription()); },
                delegate { return Objects<Sim>(); },
                delegate { return Objects<Fish>(); },
                delegate { List<object> l = Objects<CarOwnable>(); l.AddRange(Objects<BoatOwnable>()); return l; },
                delegate { return Objects<HorseshoeCourt>(); },
            };
            try
            {
                for (int p = 0; p < phases.Length; p++)
                {
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
                    Simulator.Sleep(0);
                    if (DeadRefUnsafe(false) != null) { c[410]++; return; }
                }
                c[405]++;
            }
            finally { sDeadRefTask = ObjectGuid.InvalidObjectGuid; }
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
                case "sweep": return "sweep: started " + DeadRefSweepStart() + " (" + (DeadRefUnsafe(false) ?? "safe") + ")";
                case "savepass": return DeadRefSavePass();
                default: return "unknown check " + what + " (count, relget, aging, sweep, savepass)";
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
            foreach (SimDescription d in Household.EverySimDescription())
            {
                if (d == null || d.CareerManager == null) continue;
                foreach (Occupation job in new Occupation[] { d.CareerManager.Occupation, d.CareerManager.School })
                    if (job != null && job.Coworkers != null) foreach (SimDescription w in job.Coworkers) if (DescGone(w)) coworkers++;
            }
            return "count: relationship keys gone " + relGone + "/" + Relationship.sAllRelationships.Count + ", entries naming gone " + relEntries
                + ", aging states gone " + AgingStatesToCheck().Count + ", alarm owners gone " + AlarmOwnersToCheck().Count
                + ", react times gone " + react + ", topics gone " + topics + ", coworkers gone " + coworkers
                + ", no-outfit descriptions " + NoOutfitDescriptions().Count + " | " + (DeadRefUnsafe(false) ?? "safe");
        }
    }
}
