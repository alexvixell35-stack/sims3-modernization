using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects.Environment;
using Sims3.Gameplay.Objects.Miscellaneous;
using Sims3.Gameplay.Objects.PerformanceObjects;
using Sims3.Gameplay.Services;
using Sims3.Gameplay.Socializing;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using Sims3.SimIFace.CAS;

namespace Ts3Fixes
{
    // EA script bugs reported in issues #86-#90, one switch each, all default off. Each cause was read in the decompiled
    // game code [verified]; the in-game sightings are the reporter's [community]. Counters [380-390] (registry fixes-380).
    //   preferredservice (#86): Service.mPreferredServiceNpc (household id -> Sim, saved) is cleaned only by RemovePreferredSim
    //     (first entry only) from FireSim / the pool checks; EndService and the other pool removals leave the entry, so
    //     FindSimForAssignment never sends that Sim again and never re-picks. Fix: RemovePreferredSim drops every entry naming
    //     the Sim; FindSimForAssignment drops this household's entry when its Sim is null or not in the pool (EA then picks
    //     anew, its own no-entry path); every service is swept before a save (changes save data: opt-in).
    //     [380] extra entries removed, [381] stale entries re-picked, [382] entries swept at save.
    //   socialworkeronlot (#87): the four social worker situations (child abuse, adoption, pet adoption, pet put-up) go
    //     WaitToRoute.Init -> RouteToLot.Init at once when the worker is already on the lot, inside the situation's
    //     constructor, and the visit then sleeps (child abuse: ShiftKidsToNewHouseholds) where Mono cannot yield. Fix: that
    //     on-lot RouteToLot.Init runs 1 sim-minute later from a yielding alarm, if the situation is still in that state.
    //     [383] visits deferred, [384] deferred visits dropped (situation moved on or ended).
    //   eanullguards (#88, four of five): VelvetRopes.ChargeSimForBribe with no bribe table (NightlyCleanUp nulls it) = false;
    //     ActiveTopic.AddToSim(null Sim, ...) returns like the first overload does without a SocialComponent; Motives.
    //     CreateMotives for a householdless pet swallows the NRE of its last statement (Household.UpdateMotivesToLookAfterPets);
    //     CarrySystem.EnterWhileHolding(sim, null, ..) returns (callers then PutDown, which returns for an empty right hand).
    //     [385] bribe, [386] topic, [387] motives, [388] carry. Row 5 (doorbell prank) not done: the throw is mid-body of
    //     Door.PrankDoorbellDitch.Run (RandomUtil.GetRandomObjectFromList<T>, a shared generic), a hook would copy the body.
    //   showstagethrowables (#90): ThrownObjectInfo's constructor (30 per session, CreateThrowableObjectDb) and WatchTheShow.
    //     PerformInventoryActions (per throw) create items out of the world and never destroy them; they go into every save.
    //     Fix: objects ThrownObjectInfo.CreateObjectFromMedatorName made during either call that end up out of the world, in no
    //     inventory and with no parent are destroyed. [389] items destroyed.
    //   firepitchair (#90): FirePit.mTestChair is [Persistable(false)] but the out-of-world chair itself is saved: one orphan
    //     per pit per session. Fix: before a save each pit's chair is destroyed and the field cleared (made again lazily).
    //     Changes save data: opt-in. [390] chairs destroyed.
    //   ageupkeepoutfits (#100, docs/205): SimBuilderTask.Simulate catches every exception of the outfit build (incl. the
    //     OperationCanceledException it throws itself once Stop set Abort: AgingManager.Enabled = false, CancelAgingAlarmsForSim)
    //     and still calls FinishOutfitSwap, which removes every outfit of every category and adds NewOutfits back: after an
    //     early failure nothing comes back. Fix: FinishOutfitSwap returns at once when the task Failed or NewOutfits holds no
    //     outfit; the old outfits stay, the Sim keeps its age (EA's age-up paths already stop on Failed). [391] swaps skipped,
    //     [392] descriptions with no Naked and no Everyday outfit at the last count (no safe source to rebuild: counted, logged).
    public static partial class Fixes
    {
        const BindingFlags kInstAll = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        static readonly FieldInfo sPrefNpc = typeof(Service).GetField("mPreferredServiceNpc", kInstAll);
        static readonly FieldInfo sSvcPool = typeof(Service).GetField("mPool", kInstAll);
        static readonly FieldInfo sBribeTable = typeof(VelvetRopes).GetField("mBribeDictionary", kInstAll);
        static readonly FieldInfo sTestChair = typeof(FirePit).GetField("mTestChair", kInstAll);
        static List<IGameObject> sThrowMade;  // non-null while a throwables call runs
        static bool sSwRunning;               // our deferred RouteToLot.Init: the hook passes it to the game
        static readonly FieldInfo sSbtFailed = typeof(SimBuilderTask).GetField("Failed", kInstAll);
        static readonly FieldInfo sSbtFailure = typeof(SimBuilderTask).GetField("FailureException", kInstAll);
        static readonly FieldInfo sSbtDesc = typeof(SimBuilderTask).GetField("SimDescription", kInstAll);
        static readonly FieldInfo sSbtNew = typeof(SimBuilderTask).GetField("NewOutfits", kInstAll);

        static void ApplyEaFixes()
        {
            Patch(typeof(Service), "RemovePreferredSim", typeof(PreferredServiceHook), "RemoveHook", "RemoveOrig");
            Patch(typeof(Service), "FindSimForAssignment", typeof(PreferredServiceHook), "FindHook", "FindOrig");
            Patch(typeof(SocialWorkerChildAbuseSituation.RouteToLot), "Init", typeof(SwAbuseHook), "InitHook", "InitOrig");
            Patch(typeof(SocialWorkerAdoptionSituation.RouteToLot), "Init", typeof(SwAdoptHook), "InitHook", "InitOrig");
            Patch(typeof(SocialWorkerPetAdoptionSituation.RouteToLot), "Init", typeof(SwPetAdoptHook), "InitHook", "InitOrig");
            Patch(typeof(SocialWorkerPetPutUpSituation.RouteToLot), "Init", typeof(SwPutUpHook), "InitHook", "InitOrig");
            Patch(typeof(VelvetRopes), "ChargeSimForBribe", typeof(BribeHook), "ChargeHook", "ChargeOrig");
            Patch(typeof(ActiveTopic), "AddToSim", new Type[] { typeof(Sim), typeof(string) }, typeof(TopicHook), "Add2Hook", "Add2Orig");
            Patch(typeof(ActiveTopic), "AddToSim", new Type[] { typeof(Sim), typeof(string), typeof(SimDescription) }, typeof(TopicHook), "Add3Hook", "Add3Orig");
            Patch(typeof(ActiveTopic), "AddToSim", new Type[] { typeof(Sim), typeof(string), typeof(SimDescription), typeof(SimDescription) }, typeof(TopicHook), "Add4dHook", "Add4dOrig");
            Patch(typeof(ActiveTopic), "AddToSim", new Type[] { typeof(Sim), typeof(string), typeof(Sim), typeof(Sim) }, typeof(TopicHook), "Add4sHook", "Add4sOrig");
            Patch(typeof(Motives), "CreateMotives", typeof(MotivesHook), "CreateHook", "CreateOrig");
            Patch(typeof(CarrySystem), "EnterWhileHolding", new Type[] { typeof(Sim), typeof(ICarryable), typeof(bool) }, typeof(CarryHook), "EnterHook", "EnterOrig");
            Patch(typeof(ThrownObjectList), "CreateThrowableObjectDb", typeof(ThrowHook), "DbHook", "DbOrig");
            Patch(typeof(ThrownObjectInfo), "CreateObjectFromMedatorName", new Type[] { typeof(Sims3.Gameplay.Objects.Quality) }, typeof(ThrowHook), "MakeHook", "MakeOrig");
            Patch(typeof(ShowStage.WatchTheShow), "PerformInventoryActions", typeof(ThrowHook), "PerformHook", "PerformOrig");
            Patch(typeof(SimBuilderTask), "FinishOutfitSwap", typeof(AgeUpOutfitHook), "SwapHook", "SwapOrig");
            Patch(typeof(Sims3.Gameplay.Careers.Occupation), "ApplyOccupationUniform", typeof(UniformBackupHook), "ApplyHook", "ApplyOrig");
            Patch(typeof(Sim.SwitchOutfitHelper), "Wait", typeof(OutfitWaitHook), "WaitHook", "WaitOrig");
            Patch(typeof(Sim), "SwitchToOutfitWithoutSpin", new Type[] { typeof(OutfitCategories), typeof(SimOutfit), typeof(int) }, typeof(OutfitClearHook), "InstantHook", "InstantOrig");
            Patch(typeof(Sim), "DoOutfitDelayedSwap", typeof(OutfitClearHook), "DoneHook", "DoneOrig");
            Patch(typeof(CASUtils), "RegisterCallback", typeof(SwapCbHook), "RegisterHook", "RegisterOrig");
            Patch(typeof(BakeSaleTable), "get_GetSeller", typeof(BakeSaleHook), "GetSellerHook", "GetSellerOrig");
            Patch(typeof(BakeSaleTable), "get_HasSeller", typeof(BakeSaleHook), "HasSellerHook", "HasSellerOrig");
            Patch(typeof(BakeSaleTable), "OnStartup", typeof(BakeSaleHook), "StartupHook", "StartupOrig");
            Patch(typeof(Sims3.Gameplay.Skills.Cooking), "IsMenuMaven", typeof(MenuMavenHook), "IsHook", "IsOrig");
            Patch(typeof(Services), "get_AllServices", typeof(NullServiceHook), "GetHook", "GetOrig");
            Patch(typeof(Services), "PostWorldLoadStartup", typeof(NullServiceHook), "StartupHook", "StartupOrig");
            Patch(typeof(RabbitHole), "AnimateDoorOpen", typeof(RabbitHoleDoorHook), "OpenHook", "OpenOrig");
            Patch(typeof(RabbitHole), "AnimateDoorClose", typeof(RabbitHoleDoorHook), "CloseHook", "CloseOrig");
            Patch(typeof(Sims3.Gameplay.Utilities.GreetingCardTakePhotoHelper), "SimDoneWithPhoto", typeof(PhotoSessionHook), "SimDoneHook", "SimDoneOrig");
            Patch(typeof(Sims3.Gameplay.Utilities.GreetingCardTakePhotoHelper), "SimMadeItInside", typeof(PhotoSessionHook), "MadeInsideHook", "MadeInsideOrig");
            Patch(typeof(Sims3.Gameplay.Objects.EggHuntController), "IsValidEggPosition", typeof(EggSpotHook), "ValidHook", "ValidOrig");
            Patch(typeof(Sims3.Gameplay.Careers.School.GraduateInCityHall), "WaitingLoopDel", typeof(GraduationTableHook), "CityHallWaitHook", "CityHallWaitOrig");
            Patch(typeof(Sims3.Gameplay.Careers.School.AttendGraduation), "WaitingLoopDel", typeof(GraduationTableHook), "AttendWaitHook", "AttendWaitOrig");
            Patch(typeof(Sim), "UpdateJazzParameter", new Type[] { typeof(string), typeof(bool) }, typeof(JazzParamHook), "UpdateHook", "UpdateOrig");
            Patch(typeof(BuffManager), "UpdateBuffs", new Type[] { typeof(float) }, typeof(BuffUpdateHook), "UpdateHook", "UpdateOrig");
            Patch(typeof(Sims3.Gameplay.Objects.Vehicles.CarNpcManager), "DriveFromPointToMatrix", new Type[] { typeof(Sims3.Gameplay.Objects.Vehicles.Car), typeof(Vector3), typeof(Matrix44) }, typeof(CarNoRouteHook), "DriveHook", "DriveOrig");
            Patch(typeof(SocialComponent), "ScoreSocialsOnSims", Type.EmptyTypes, typeof(ConvTurnHook), "ScoreHook", "ScoreOrig");
            Patch(typeof(Sims3.Gameplay.Objects.Vehicles.NpcDriversManager), "CreateNpcDriver", new Type[] { typeof(Sims3.Gameplay.Objects.Vehicles.NpcDriversManager.NpcDrivers) }, typeof(NpcDriverPoolHook), "CreateHook", "CreateOrig");
            Patch(typeof(Sims3.Gameplay.Objects.Book), "StopUsingBook", typeof(BookCarryHook), "StopHook", "StopOrig");
            Patch(typeof(AgingState), "PreInstantiateSim", new Type[] { typeof(ResourceKey).MakeByRefType() }, typeof(AgeUpAbortHook), "PreHook", "PreOrig");
            Patch(typeof(GameObject), "SetRewardGiver", typeof(RewardGiverHook), "GiverHook", "GiverOrig");  // RewardGiver.cs, docs/525
        }

        // ---- ageupkeepoutfits (#100) ----

        public class AgeUpOutfitHook
        {
            public void SwapHook()
            {
                object task = this; // `this` is really a SimBuilderTask
                if (ShortcutOn("ageupkeepoutfits") && sSbtFailed != null && sSbtNew != null)
                {
                    bool failed = (bool)sSbtFailed.GetValue(task);
                    int made = 0;
                    System.Collections.IDictionary outfits = sSbtNew.GetValue(task) as System.Collections.IDictionary;
                    if (outfits != null) foreach (System.Collections.ICollection l in outfits.Values) if (l != null) made += l.Count;
                    if (failed || made == 0)
                    {
                        PatchCounters()[391]++;
                        SimDescription d = sSbtDesc != null ? sSbtDesc.GetValue(task) as SimDescription : null;
                        Exception e = sSbtFailure != null ? sSbtFailure.GetValue(task) as Exception : null;
                        ShortcutLog("ageupkeepoutfits: " + (d != null ? d.FullName + " (" + d.Age + ")" : "?") + " kept its outfits; builder "
                            + (failed ? "failed: " + (e != null ? e.GetType().Name + " " + e.Message : "no exception") : "made no outfit"));
                        return;
                    }
                }
                SwapOrig();
            }
            public void SwapOrig() { } // receives SimBuilderTask.FinishOutfitSwap's original header
        }

        // Household and urnstone descriptions with no Naked and no Everyday outfit (the #100 end state); [392] = this count.
        public static List<SimDescription> NoOutfitDescriptions()
        {
            List<SimDescription> l = new List<SimDescription>();
            foreach (SimDescription d in Household.EverySimDescription())
                if (d != null && d.IsValidDescription && d.GetOutfitCount(OutfitCategories.Naked) == 0 && d.GetOutfitCount(OutfitCategories.Everyday) == 0) l.Add(d);
            PatchCounters()[392] = l.Count;
            return l;
        }

        static SimDescription sAgeUpTarget;

        // Agent checks for #100 (repro bp-ageup-outfits-check). Stress-labelled: "abort" really cancels a townie's age-up
        // builder, so with the switch off that townie loses every outfit (EA's bug), on a test save only.
        //   "abort":  a householder townie without a Sim object, aging on, below Elder, with outfits: its builder started and
        //             stopped at once (AgingState.StartSimBuilderTask then AbortSimBuilderTask, the CancelAgingAlarmsForSim path).
        //   "result": that townie's outfit counts now (read a few ticks after "abort").
        //   "empty":  descriptions with no Naked and no Everyday outfit, by name.
        public static string AgeUpCheck(string what)
        {
            switch (what)
            {
                case "abort":
                {
                    MethodInfo start = typeof(AgingState).GetMethod("StartSimBuilderTask", kInstAll, null, Type.EmptyTypes, null);
                    MethodInfo abort = typeof(AgingState).GetMethod("AbortSimBuilderTask", kInstAll);
                    if (start == null || abort == null) return "abort: AgingState methods not found";
                    foreach (SimDescription d in Household.EverySimDescription())
                    {
                        if (d == null || !d.IsValidDescription || d.CreatedSim != null || d.AgingState == null || !d.AgingEnabled || d.Elder) continue;
                        if (d.Household == null || d.Household == Household.ActiveHousehold || d.Household.IsSpecialHousehold) continue;
                        if (d.GetOutfitCount(OutfitCategories.Everyday) == 0) continue;
                        sAgeUpTarget = d;
                        int before = OutfitTotal(d);
                        start.Invoke(d.AgingState, null);
                        abort.Invoke(d.AgingState, null);
                        return "abort: " + d.FullName + " (" + d.Age + ") outfits before " + before;
                    }
                    return "abort: no householder townie without a Sim object, aging on, below Elder";
                }
                case "result":
                    if (sAgeUpTarget == null) return "result: run abort first";
                    return "result: " + sAgeUpTarget.FullName + " outfits " + OutfitTotal(sAgeUpTarget) + " (everyday " + sAgeUpTarget.GetOutfitCount(OutfitCategories.Everyday) + "), age " + sAgeUpTarget.Age;
                case "empty":
                {
                    List<string> names = new List<string>();
                    foreach (SimDescription d in NoOutfitDescriptions()) names.Add(d.FullName + " (" + d.Age + (d.CreatedSim != null ? ", Sim" : "") + ")");
                    return "empty: " + names.Count + (names.Count > 0 ? " " + string.Join(", ", names.ToArray()) : "");
                }
                default: return "unknown check " + what + " (abort, result, empty)";
            }
        }

        static int OutfitTotal(SimDescription d)
        {
            int n = 0;
            foreach (OutfitCategories c in Enum.GetValues(typeof(OutfitCategories)))
                if ((c & OutfitCategories.CategoryMask) != 0) n += d.GetOutfitCount(c);
            return n;
        }

        // ---- preferredservice (#86) ----

        static bool PrefStale(Service s, SimDescription d)
        {
            List<SimDescription> pool = sSvcPool.GetValue(s) as List<SimDescription>;
            return d == null || pool == null || !pool.Contains(d);
        }

        // Every service: entries whose Sim left the pool, removed. Before a save (DisposeSaveHook) and from the agent.
        public static int PreferredServiceSweep()
        {
            if (!ShortcutOn("preferredservice") || sPrefNpc == null || sSvcPool == null) return 0;
            int n = 0;
            foreach (Service s in Services.AllServices)
            {
                Dictionary<ulong, SimDescription> d = sPrefNpc.GetValue(s) as Dictionary<ulong, SimDescription>;
                if (d == null) continue;
                List<ulong> stale = new List<ulong>();
                foreach (KeyValuePair<ulong, SimDescription> kv in d) if (PrefStale(s, kv.Value)) stale.Add(kv.Key);
                foreach (ulong k in stale) d.Remove(k);
                n += stale.Count;
            }
            PatchCounters()[382] += n;
            return n;
        }

        // Calls a swapped method from our checks. MethodInfo.Invoke fails on it ("Unable to invoke an invalid target": Mono's
        // InternalInvoke checks the target against the swapped-in hook class, RefTable.cs); Delegate.CreateDelegate(Type,
        // object, MethodInfo) has no target check in this mscorlib [verified IL] and the delegate call runs the swapped body
        // with `this` = the game object, like the game's own callers [inferred, Mono 1.2 icall; non-virtual methods only].
        delegate void PrefRemoveFn(SimDescription preferredSim);

        public class PreferredServiceHook
        {
            public void RemoveHook(SimDescription preferredSim)
            {
                RemoveOrig(preferredSim);
                if (!ShortcutOn("preferredservice") || sPrefNpc == null) return;
                Dictionary<ulong, SimDescription> d = sPrefNpc.GetValue((object)this) as Dictionary<ulong, SimDescription>; // `this` is really a Service
                if (d == null) return;
                List<ulong> more = new List<ulong>();
                foreach (KeyValuePair<ulong, SimDescription> kv in d) if (kv.Value == preferredSim) more.Add(kv.Key);
                foreach (ulong k in more) d.Remove(k);
                PatchCounters()[380] += more.Count;
            }
            public void RemoveOrig(SimDescription preferredSim) { } // receives Service.RemovePreferredSim's original header

            public SimDescription FindHook(Lot lot)
            {
                Service s = (object)this as Service;
                if (s != null && lot != null && lot.Household != null && sPrefNpc != null && sSvcPool != null && ShortcutOn("preferredservice"))
                {
                    Dictionary<ulong, SimDescription> d = sPrefNpc.GetValue(s) as Dictionary<ulong, SimDescription>;
                    SimDescription had;
                    if (d != null && d.TryGetValue(lot.Household.HouseholdId, out had) && PrefStale(s, had))
                    {
                        d.Remove(lot.Household.HouseholdId); // EA's no-entry path then records the Sim it sends
                        PatchCounters()[381]++;
                    }
                }
                return FindOrig(lot);
            }
            public SimDescription FindOrig(Lot lot) { return null; } // receives Service.FindSimForAssignment's original header
        }

        // ---- socialworkeronlot (#87) ----

        // True when the on-lot RouteToLot.Init was moved to a yielding alarm; false = run it now. init runs the hook's InitOrig:
        // reflection cannot invoke the swapped Init (Mono's InternalInvoke checks the target against the hook class: "Unable
        // to invoke an invalid target", RefTable.cs), so the hook hands over a compiled call.
        static bool SwDefer(Situation child, Situation parent, Sim worker, AlarmTimerCallback init)
        {
            if (sSwRunning || worker == null || child.Lot == null || worker.LotCurrent != child.Lot || !ShortcutOn("socialworkeronlot")) return false;
            AlarmManager am = AlarmManager.Global;
            AlarmTimerCallback later = delegate
            {
                if (parent.SituationExited || child.SituationExited || parent.Child != child) { PatchCounters()[384]++; return; }
                sSwRunning = true;
                try { init(); }
                finally { sSwRunning = false; }
            };
            AlarmHandle h = am.AddAlarm(1f, TimeUnit.Minutes, later, "Ts3Fixes socialworkeronlot", AlarmType.DeleteOnReset, worker);
            am.AlarmWillYield(h);
            PatchCounters()[383]++;
            return true;
        }

        public class SwAbuseHook
        {
            public void InitHook(SocialWorkerChildAbuseSituation parent) { if (!SwDefer((object)this as Situation, parent, parent.Worker, delegate { InitOrig(parent); })) InitOrig(parent); }
            public void InitOrig(SocialWorkerChildAbuseSituation parent) { } // receives RouteToLot.Init's original header
        }
        public class SwAdoptHook
        {
            public void InitHook(SocialWorkerAdoptionSituation parent) { if (!SwDefer((object)this as Situation, parent, parent.Worker, delegate { InitOrig(parent); })) InitOrig(parent); }
            public void InitOrig(SocialWorkerAdoptionSituation parent) { }
        }
        public class SwPetAdoptHook
        {
            public void InitHook(SocialWorkerPetAdoptionSituation parent) { if (!SwDefer((object)this as Situation, parent, parent.Worker, delegate { InitOrig(parent); })) InitOrig(parent); }
            public void InitOrig(SocialWorkerPetAdoptionSituation parent) { }
        }
        public class SwPutUpHook
        {
            public void InitHook(SocialWorkerPetPutUpSituation parent) { if (!SwDefer((object)this as Situation, parent, parent.Worker, delegate { InitOrig(parent); })) InitOrig(parent); }
            public void InitOrig(SocialWorkerPetPutUpSituation parent) { }
        }

        // ---- eanullguards (#88) ----

        static bool NullGuard(bool hit, int counter)
        {
            if (!hit || !ShortcutOn("eanullguards")) return false;
            PatchCounters()[counter]++;
            return true;
        }

        public class BribeHook
        {
            public bool ChargeHook(Sim s)
            {
                if (sBribeTable != null && NullGuard(sBribeTable.GetValue((object)this) == null, 385)) return false; // no table = no bribe on record
                return ChargeOrig(s);
            }
            public bool ChargeOrig(Sim s) { return false; } // receives VelvetRopes.ChargeSimForBribe's original header
        }

        public static class TopicHook
        {
            static bool NoSim(Sim sim) { return NullGuard(sim == null || sim.SocialComponent == null, 386); }
            public static void Add2Hook(Sim sim, string type) { if (!NoSim(sim)) Add2Orig(sim, type); }
            public static void Add2Orig(Sim sim, string type) { }
            public static void Add3Hook(Sim sim, string type, SimDescription p) { if (!NoSim(sim)) Add3Orig(sim, type, p); }
            public static void Add3Orig(Sim sim, string type, SimDescription p) { }
            public static void Add4dHook(Sim sim, string type, SimDescription p, SimDescription a) { if (!NoSim(sim)) Add4dOrig(sim, type, p, a); }
            public static void Add4dOrig(Sim sim, string type, SimDescription p, SimDescription a) { }
            public static void Add4sHook(Sim sim, string type, Sim p, Sim a) { if (!NoSim(sim)) Add4sOrig(sim, type, p, a); }
            public static void Add4sOrig(Sim sim, string type, Sim p, Sim a) { }
        }

        public class MotivesHook
        {
            public void CreateHook(Sim sim, InteractionScorer scorer)
            {
                // the pet branch's last statement is mSim.Household.UpdateMotivesToLookAfterPets(): no household, no pets to tend
                if (sim == null || sim.Household != null || sim.SimDescription == null || !sim.SimDescription.IsPet || !ShortcutOn("eanullguards")) { CreateOrig(sim, scorer); return; }
                try { CreateOrig(sim, scorer); }
                catch (NullReferenceException) { PatchCounters()[387]++; }
            }
            public void CreateOrig(Sim sim, InteractionScorer scorer) { } // receives Motives.CreateMotives's original header
        }

        public static class CarryHook
        {
            public static void EnterHook(Sim a, ICarryable target, bool bAddToUseList)
            {
                if (NullGuard(target == null, 388)) return;
                EnterOrig(a, target, bAddToUseList);
            }
            public static void EnterOrig(Sim a, ICarryable target, bool bAddToUseList) { } // CarrySystem.EnterWhileHolding(Sim, ICarryable, bool)
        }

        // ---- showstagethrowables / firepitchair (#90) ----

        static List<IGameObject> ThrowBegin() { return ShortcutOn("showstagethrowables") && sThrowMade == null ? (sThrowMade = new List<IGameObject>()) : null; }

        // Objects made during the call that nobody took: out of the world, in no inventory, no parent.
        static void ThrowEnd(List<IGameObject> made)
        {
            if (made == null) return;
            sThrowMade = null;
            foreach (IGameObject o in made)
            {
                GameObject g = o as GameObject;
                if (g == null || g.HasBeenDestroyed || g.InWorld || g.InInventory || g.Parent != null) continue;
                try { g.Destroy(); PatchCounters()[389]++; } catch (Exception) { }
            }
        }

        public class ThrowHook
        {
            public static void DbHook()
            {
                List<IGameObject> made = ThrowBegin();
                try { DbOrig(); } finally { ThrowEnd(made); }
            }
            public static void DbOrig() { } // receives ThrownObjectList.CreateThrowableObjectDb's original header

            public IGameObject MakeHook(Sims3.Gameplay.Objects.Quality q)
            {
                IGameObject o = MakeOrig(q);
                if (o != null && sThrowMade != null) sThrowMade.Add(o);
                return o;
            }
            public IGameObject MakeOrig(Sims3.Gameplay.Objects.Quality q) { return null; } // ThrownObjectInfo.CreateObjectFromMedatorName(Quality)

            public void PerformHook()
            {
                List<IGameObject> made = ThrowBegin();
                try { PerformOrig(); } finally { ThrowEnd(made); }
            }
            public void PerformOrig() { } // receives ShowStage.WatchTheShow.PerformInventoryActions's original header
        }

        // Before a save (DisposeSaveHook): each fire pit's out-of-world test chair destroyed, field cleared.
        public static int FirePitChairSweep()
        {
            if (!ShortcutOn("firepitchair") || sTestChair == null) return 0;
            int n = 0;
            foreach (FirePit pit in Sims3.Gameplay.Queries.GetObjects<FirePit>())
            {
                GameObject chair = sTestChair.GetValue(pit) as GameObject;
                if (chair == null || chair.InWorld || chair.Parent != null) continue;
                sTestChair.SetValue(pit, null);
                if (chair.HasBeenDestroyed) continue;
                chair.Destroy();
                n++;
            }
            PatchCounters()[390] += n;
            return n;
        }

        // Agent checks (repros.json bp-eafixes-check, bp-socialworker-onlot). Each returns a line the repro reads; "threw" counts EA's exceptions.
        //   "bribe":  ChargeSimForBribe on every rope after NightlyCleanUp, with the active Sim.
        //   "topic":  ActiveTopic.AddToSim(null, "Ts3Fixes") (all four overloads).
        //   "carry":  CarrySystem.EnterWhileHolding(active Sim, null, true).
        //   "pref":   counts preferred-service entries whose Sim is not in its pool, per service (no change).
        //   "prefsweep": PreferredServiceSweep now. "prefdup": one pool Sim recorded for two made-up households, then the
        //             game's RemovePreferredSim (entries left; test entries always removed). "throwdb": rebuilds the throwables
        //             table, returns the out-of-world object delta. "swstate": social worker situations and their state.
        //   "orphans": out-of-world, inventory-less, parentless objects by type name. "chairs": FirePitChairSweep now.
        public static string EaFixCheck(string what)
        {
            Sim active = Sim.ActiveActor;
            switch (what)
            {
                case "bribe":
                {
                    int ropes = 0, threw = 0;
                    foreach (VelvetRopes r in Sims3.Gameplay.Queries.GetObjects<VelvetRopes>())
                    {
                        ropes++;
                        r.NightlyCleanUp();
                        try { r.ChargeSimForBribe(active); } catch (Exception) { threw++; }
                    }
                    return "bribe: ropes " + ropes + ", threw " + threw;
                }
                case "topic":
                {
                    int threw = 0;
                    try { ActiveTopic.AddToSim(null, "Ts3Fixes"); } catch (Exception) { threw++; }
                    try { ActiveTopic.AddToSim(null, "Ts3Fixes", (SimDescription)null); } catch (Exception) { threw++; }
                    try { ActiveTopic.AddToSim(null, "Ts3Fixes", (SimDescription)null, (SimDescription)null); } catch (Exception) { threw++; }
                    try { ActiveTopic.AddToSim(null, "Ts3Fixes", (Sim)null, (Sim)null); } catch (Exception) { threw++; }
                    return "topic: 4 calls, threw " + threw;
                }
                case "carry":
                {
                    if (active == null) return "carry: no active Sim";
                    try { CarrySystem.EnterWhileHolding(active, null, true); return "carry: threw 0"; }
                    catch (Exception e) { return "carry: threw 1 (" + e.GetType().Name + ")"; }
                }
                case "pref":
                {
                    if (sPrefNpc == null || sSvcPool == null) return "pref: fields not found";
                    List<string> l = new List<string>();
                    foreach (Service s in Services.AllServices)
                    {
                        Dictionary<ulong, SimDescription> d = sPrefNpc.GetValue(s) as Dictionary<ulong, SimDescription>;
                        if (d == null || d.Count == 0) continue;
                        int stale = 0;
                        foreach (SimDescription v in d.Values) if (PrefStale(s, v)) stale++;
                        l.Add(s.GetType().Name + " " + d.Count + "/" + stale + " stale");
                    }
                    return "pref: " + string.Join(", ", l.ToArray());
                }
                case "prefsweep": return "prefsweep: " + PreferredServiceSweep();
                case "prefdup":
                {
                    // one pool Sim recorded for two made-up household ids, then the game's RemovePreferredSim: EA leaves one
                    MethodInfo remove = typeof(Service).GetMethod("RemovePreferredSim", kInstAll);
                    foreach (Service s in Services.AllServices)
                    {
                        List<SimDescription> pool = sSvcPool.GetValue(s) as List<SimDescription>;
                        Dictionary<ulong, SimDescription> d = sPrefNpc.GetValue(s) as Dictionary<ulong, SimDescription>;
                        if (pool == null || pool.Count == 0 || d == null || remove == null) continue;
                        SimDescription sim = null;
                        foreach (SimDescription p in pool) if (p != null && !d.ContainsValue(p)) { sim = p; break; } // no real household's entry touched
                        if (sim == null) continue;
                        int after = 0;
                        d[ulong.MaxValue - 1] = sim;
                        d[ulong.MaxValue - 2] = sim;
                        try { ((PrefRemoveFn)Delegate.CreateDelegate(typeof(PrefRemoveFn), s, remove))(sim); } // swapped: see PrefRemoveFn
                        finally
                        {
                            foreach (SimDescription v in d.Values) if (v == sim) after++;
                            d.Remove(ulong.MaxValue - 1); d.Remove(ulong.MaxValue - 2); // never leave the test entries
                        }
                        return "prefdup: " + s.GetType().Name + " entries naming the Sim before 2, after " + after;
                    }
                    return "prefdup: no service with a pool Sim that has no entry";
                }
                case "throwdb":
                {
                    // the game builds the table once per world load (PostWorldInitializers); a rebuild gives the same 30 entries
                    int before = OutOfWorldCount();
                    ThrownObjectList.CreateThrowableObjectDb();
                    return "throwdb: out-of-world objects +" + (OutOfWorldCount() - before);
                }
                case "swstate":
                {
                    List<string> l = new List<string>();
                    foreach (Situation s in new List<Situation>(Situation.sAllSituations))
                        if (s.GetType().Name.StartsWith("SocialWorker") && s.Child != null)
                            l.Add(s.GetType().Name + "/" + s.Child.GetType().Name);
                    return "swstate: " + string.Join(", ", l.ToArray());
                }
                case "chairs": return "chairs: " + FirePitChairSweep();
                case "orphans":
                {
                    Dictionary<string, int> n = new Dictionary<string, int>();
                    foreach (GameObject g in Sims3.Gameplay.Queries.GetObjects<GameObject>())
                    {
                        if (g.InWorld || g.InInventory || g.Parent != null || g is Sim || g is Lot) continue;
                        string t = g.GetType().Name;
                        int k;
                        n.TryGetValue(t, out k);
                        n[t] = k + 1;
                    }
                    List<string> l = new List<string>();
                    foreach (KeyValuePair<string, int> kv in n) l.Add(kv.Key + " " + kv.Value);
                    l.Sort();
                    return "orphans: " + string.Join(", ", l.ToArray());
                }
                default: return "unknown check " + what + " (bribe, topic, carry, pref, prefsweep, prefdup, throwdb, swstate, chairs, orphans)";
            }
        }

        static int OutOfWorldCount()
        {
            int n = 0;
            foreach (GameObject g in Sims3.Gameplay.Queries.GetObjects<GameObject>())
                if (!g.InWorld && !g.InInventory && g.Parent == null && !(g is Sim)) n++;
            return n;
        }
    }
}
