using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.DreamsAndPromises;
using Sims3.Gameplay.EventSystem;
using Sims3.Gameplay.Opportunities;
using Sims3.Gameplay.Situations;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Generations free vacation (issue #97, docs/210): ParentsLeavingTownSituation sends the adults off for two days,
    // FreeVacationPickupSituation.Dispose destroys their Sims, ParentsAreBack.BringAdultsBackToWorld re-creates them. Every
    // cause below was read in the decompiled game code [verified]; the in-game sightings are the reporter's [community].
    // One switch per defect, all default off. Our state is in memory only: nothing of ours goes into a save (vacexport fills
    // EA's own travel fields DnPExportData / NeedsOpportunityImport / OpportunityHistory, as EA's travel does).
    // Counters [466-480] (registry fixes-466).
    //   vacexport (row 1): the pickup destroys the adults without EA's travel export (dreams, opportunities, travel buffs), and
    //     SetupSelectableSimManagers honours DnPExportData only in a travel world or for a Passport Sim. Fix: the pickup's
    //     Dispose exports first; SetupSelectableSimManagers builds the dream manager from the export for an adult on the trip.
    //     [466] adults exported, [467] dream managers built from the export.
    //   vacstoreguard (row 2): OpportunityHistory.StoreOpportunityForTravel reads its category slot unchecked. Fix: a missing
    //     slot is re-added the way OpportunityManager.Fixup does; a store that throws drops only that slot. Load repair for
    //     trips already stuck: no alarm, no teen or a pickup that died -> bring-back alarm in 60 sim-minutes (not persisted, the
    //     repair re-runs at every load); a dead pickup whose adults never left ends the trip. [468] slots re-added, [469] stores
    //     that threw, [470] bring-backs scheduled at load, [471] dead-pickup trips ended at load.
    //   vacreturnskip (row 3): the return re-creates dead adults and adults who left the household. Fix: those are dropped
    //     from the trip list first; a return that throws or brings nobody back ends the trip (else it sits in ParentsAreBack
    //     forever: OnGetHome is the only exit). [472] adults skipped, [473] returns ended.
    //   vacteendelete (row 4): a teen's Sim deleted while the parents are away runs the whole return inside that Sim's
    //     Dispose. Fix: the teen leaves the trip and a check runs 10 sim-minutes later: household teens with a Sim rejoin;
    //     with none the parents come back then. [474] deletions deferred, [475] teens rejoined, [476] returns from the check.
    //   vacexitreturn (row 5): an Exit while the parents are away (NRaas reset, AutonomyManager.Shutdown) runs CleanUp ->
    //     ResetStatics with nobody brought back. Fix: CleanUp first re-creates every stored adult who is alive, in the
    //     household and has no Sim, on the home lot (not while travelling or quitting). [477] adults brought back.
    //   vaclistenernull (row 6): the travel import reads ListenerStates[i] once per listener SetupOneListener makes now; a
    //     null slot reaches RestoreState(null) (base + 4 overrides throw). Fix: RestoreState(null) = fresh listener, false.
    //     [478] null states skipped.
    //   vacimportfix (row 7, EA's vacation return home too): (a) OriginalTarget/OriginalSource ids that no longer find the
    //     exported Sim are pointed at that description's Sim; (b) ExpirationTime tick 0 (no time-based timeout) is not
    //     "expired": raised for the import call only. [479] ids refreshed, [480] expiry checks skipped.
    public static partial class Fixes
    {
        static readonly BindingFlags kVacAll = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        static readonly Type sVacAway = typeof(ParentsLeavingTownSituation).GetNestedType("ParentsAreAway", BindingFlags.NonPublic);
        static readonly Type sVacBack = typeof(ParentsLeavingTownSituation).GetNestedType("ParentsAreBack", BindingFlags.NonPublic);
        static readonly FieldInfo sVacHousehold = typeof(ParentsLeavingTownSituation).GetField("mHouseholdId", kVacAll);
        static readonly string[] kVacAlarmFields = { "mNormalVacationLengthAlarm", "mEarlyArrivalRecurringAlarm", "mFriendPartySuggestionRecurringAlarm", "mBringParentsBackAlarm" };
        static bool sVacQuitting;

        static void ApplyEaVacation()
        {
            Patch(typeof(FreeVacationPickupSituation), "Dispose", typeof(VacPickupHook), "DisposeHook", "DisposeOrig");
            Patch(typeof(Sim), "SetupSelectableSimManagers", typeof(VacSelectHook), "SetupHook", "SetupOrig");
            Patch(typeof(OpportunityHistory), "StoreOpportunityForTravel", typeof(VacStoreHook), "StoreHook", "StoreOrig");
            if (sVacBack != null) Patch(sVacBack, "BringAdultsBackToWorld", typeof(VacBackHook), "BackHook", "BackOrig");
            else AppliedLog().Add("ParentsAreBack: nested type not found");
            Patch(typeof(ParentsLeavingTownSituation), "OnParticipantDeleted", typeof(VacTripHook), "DeletedHook", "DeletedOrig");
            Patch(typeof(ParentsLeavingTownSituation), "CleanUp", typeof(VacTripHook), "CleanUpHook", "CleanUpOrig");
            Patch(typeof(EventListener), "RestoreState", typeof(VacListenerHook), "BaseHook", "BaseOrig");
            Patch(typeof(CountListener), "RestoreState", typeof(VacListenerHook), "CountHook", "CountOrig");
            Patch(typeof(CountListenerDistinct), "RestoreState", typeof(VacListenerHook), "DistinctHook", "DistinctOrig");
            Patch(typeof(CountListenerExclusive), "RestoreState", typeof(VacListenerHook), "ExclusiveHook", "ExclusiveOrig");
            Patch(typeof(TotaledListener), "RestoreState", typeof(VacListenerHook), "TotaledHook", "TotaledOrig");
            Patch(typeof(OpportunityManager), "AddOpportunityFromTravel", typeof(VacImportHook), "AddHook", "AddOrig");
            World.OnWorldQuitEventHandler += delegate { sVacQuitting = true; };
        }

        static Household VacHousehold(ParentsLeavingTownSituation s)
        {
            return s == null || sVacHousehold == null ? null : Household.Find((ulong)sVacHousehold.GetValue(s));
        }

        static int VacVerdict(ulong id, Household h)
        {
            SimDescription d = SimDescription.Find(id);
            bool botNoChips = d != null && d.IsEP11Bot && (ParentsLeavingTownSituation.sAdultBotsTraitChips == null || !ParentsLeavingTownSituation.sAdultBotsTraitChips.ContainsKey(id));
            return EaVacationCore.AdultVerdict(d != null, d != null && d.CreatedSim != null, botNoChips, d != null && d.IsDead, d != null && h != null && d.Household == h);
        }

        static List<ulong> VacStoredIds()
        {
            List<ulong> ids = new List<ulong>();
            if (ParentsLeavingTownSituation.sAdultsInventories != null) ids.AddRange(ParentsLeavingTownSituation.sAdultsInventories.Keys);
            return ids;
        }

        static void VacDrop(ulong id)
        {
            ParentsLeavingTownSituation.sAdultsInventories.Remove(id);
            if (ParentsLeavingTownSituation.sAdultBotsTraitChips != null) ParentsLeavingTownSituation.sAdultBotsTraitChips.Remove(id);
        }

        static void VacLater(Lot owner, float minutes, string name, AlarmTimerCallback f)
        {
            AlarmManager am = AlarmManager.Global;
            AlarmHandle h = am.AddAlarm(minutes, TimeUnit.Minutes, f, name, AlarmType.DeleteOnReset, owner);
            am.AlarmWillYield(h); // the return instantiates Sims, like EA's own bring-back alarm
        }

        // ---- vacexport (row 1) ----

        public class VacPickupHook
        {
            public void DisposeHook()
            {
                FreeVacationPickupSituation s = (object)this as FreeVacationPickupSituation;
                if (s != null && !s.ParentSituationIsCancelled && s.Car != null && s.Adults != null && ParentsLeavingTownSituation.sAdultsInventories != null && ShortcutOn("vacexport"))
                    foreach (Sim a in s.Adults.ToArray())
                    {
                        if (a == null || a.HasBeenDestroyed || a.IsDying() || !ParentsLeavingTownSituation.sAdultsInventories.ContainsKey(a.SimDescription.SimDescriptionId)) continue;
                        VacExport(a);
                    }
                DisposeOrig();
            }
            public void DisposeOrig() { } // receives FreeVacationPickupSituation.Dispose's original header
        }

        // EA's travel export (GameStates early departure, TravelUtil), each step on its own so one failure keeps the rest.
        static void VacExport(Sim a)
        {
            SimDescription d = a.SimDescription;
            try { if (a.DreamsAndPromisesManager != null) d.DnPExportData = new DnPExportData(d); }
            catch (Exception e) { ShortcutLog("vacexport: dreams of " + d.FullName + " " + e.GetType().Name); }
            try { if (a.OpportunityManager != null) a.OpportunityManager.StoreOpportunitiesForTravel(); }
            catch (Exception e) { ShortcutLog("vacexport: opportunities of " + d.FullName + " " + e.GetType().Name); }
            try { if (a.BuffManager != null) a.BuffManager.StoreBuffsForTravel(d); } // read back by Sim.OnCreation (not saved: lost on reload)
            catch (Exception e) { ShortcutLog("vacexport: buffs of " + d.FullName + " " + e.GetType().Name); }
            PatchCounters()[466]++;
        }

        public class VacSelectHook
        {
            public void SetupHook()
            {
                Sim sim = (object)this as Sim; // `this` is really a Sim
                SimDescription d = sim != null ? sim.SimDescription : null;
                if (d != null && d.DnPExportData != null && sim.DreamsAndPromisesManager == null && d.ChildOrAbove
                    && ParentsLeavingTownSituation.IsSimOnFreeVacation(d.SimDescriptionId) && ShortcutOn("vacexport"))
                {
                    try { sim.SetUpDnPManagerFromExport(); PatchCounters()[467]++; }
                    catch (Exception e) { ShortcutLog("vacexport: dream import of " + d.FullName + " " + e.GetType().Name); sim.NullDnPManager(); } // EA then makes a fresh one
                }
                SetupOrig();
            }
            public void SetupOrig() { } // receives Sim.SetupSelectableSimManagers's original header
        }

        // ---- vacstoreguard (row 2) ----

        public class VacStoreHook
        {
            public void StoreHook(Opportunity op)
            {
                OpportunityHistory h = (object)this as OpportunityHistory;
                if (h == null || op == null || !ShortcutOn("vacstoreguard")) { StoreOrig(op); return; }
                if (h.GetCurrentOpportunity(op.OpportunityCategory) == null)
                {
                    h.AddCurrentOpportunity(op.OpportunityCategory, op.Guid, null, op.TargetObject as Sim, new List<OpportunityNames>(), op.TargetInteractionNumberItemsRequiredList(), op.Name, op.Description, op.DeadlineString);
                    PatchCounters()[468]++;
                }
                try { StoreOrig(op); }
                catch (Exception e)
                {
                    PatchCounters()[469]++;
                    h.RemoveCurrentOpportunity(op.OpportunityCategory); // a half-written entry would import wrong: lose this one only
                    ShortcutLog("vacstoreguard: " + op.Guid + " not stored: " + e.GetType().Name + " " + e.Message);
                }
            }
            public void StoreOrig(Opportunity op) { } // receives OpportunityHistory.StoreOpportunityForTravel's original header
        }

        // World load: trips already stuck in a save (or by our not-persisted teen check) get going again.
        public static void VacationWorldLoad()
        {
            sVacQuitting = false;
            if (!ShortcutOn("vacstoreguard")) return;
            foreach (ParentsLeavingTownSituation s in Situation.GetSituations<ParentsLeavingTownSituation>())
            {
                if (s == null || s.SituationExited || !s.AreParentsOutOfTown()) continue;
                VacRepair(s, "load");
            }
        }

        static void VacRepair(ParentsLeavingTownSituation s, string why)
        {
            VacRejoin(s);
            bool pickupAlive = false, allHaveSims = true, anyAlarm = false;
            foreach (ulong id in VacStoredIds())
            {
                SimDescription d = SimDescription.Find(id);
                Sim sim = d != null ? d.CreatedSim : null;
                if (sim == null) { allHaveSims = false; continue; }
                if (sim.GetSituationOfType<FreeVacationPickupSituation>() != null) pickupAlive = true;
            }
            if (sVacAway != null && s.Child != null && s.Child.GetType() == sVacAway)
                foreach (string f in kVacAlarmFields)
                {
                    FieldInfo fi = sVacAway.GetField(f, kVacAll);
                    if (fi != null && (AlarmHandle)fi.GetValue(s.Child) != AlarmHandle.kInvalidHandle) anyAlarm = true;
                }
            int act = EaVacationCore.Repair(s.ParentsAreGettingPickedUp, pickupAlive, allHaveSims, anyAlarm, s.Teens == null ? 0 : s.Teens.Count);
            if (act == EaVacationCore.kRepairEnd)
            {
                PatchCounters()[471]++;
                ShortcutLog("vacstoreguard: " + why + ": pickup gone, adults never left: trip ended");
                s.Exit();
            }
            else if (act == EaVacationCore.kRepairBringBack)
            {
                PatchCounters()[470]++;
                s.ParentsAreGettingPickedUp = false; // else ParentsNeedToReturn looks for the dead pickup and returns
                ShortcutLog("vacstoreguard: " + why + ": trip stuck (teens " + (s.Teens == null ? 0 : s.Teens.Count) + ", alarm " + anyAlarm + "): parents back in 60 sim-minutes");
                VacLater(s.Lot, 60f, "Ts3Fixes vacstoreguard", delegate { if (!s.SituationExited && s.AreParentsOutOfTown()) s.BringParentsBack(); });
            }
        }

        // ---- vacreturnskip (row 3) ----

        public class VacBackHook
        {
            public void BackHook()
            {
                ChildSituation<ParentsLeavingTownSituation> c = (object)this as ChildSituation<ParentsLeavingTownSituation>;
                ParentsLeavingTownSituation s = c != null ? c.Parent : null;
                if (s == null || ParentsLeavingTownSituation.sAdultsInventories == null || !ShortcutOn("vacreturnskip")) { BackOrig(); return; }
                Household h = VacHousehold(s);
                int makes = 0;
                foreach (ulong id in VacStoredIds())
                {
                    int v = VacVerdict(id, h);
                    if (v == EaVacationCore.kEaMakes) { makes++; continue; }
                    if (v != EaVacationCore.kWeSkip) continue;
                    SimDescription d = SimDescription.Find(id);
                    ShortcutLog("vacreturnskip: " + d.FullName + (d.IsDead ? " died" : " left the household") + " during the trip: not re-created");
                    VacDrop(id);
                    PatchCounters()[472]++;
                }
                try { BackOrig(); }
                catch (Exception)
                {
                    PatchCounters()[473]++;
                    if (!s.SituationExited) s.Exit();
                    throw; // ErrorTrap still sees it
                }
                if (makes == 0 && !s.SituationExited) { PatchCounters()[473]++; s.Exit(); } // no GoHome pushed: OnGetHome never ends the trip
            }
            public void BackOrig() { } // receives ParentsAreBack.BringAdultsBackToWorld's original header
        }

        // ---- vacteendelete (row 4) + vacexitreturn (row 5) ----

        public class VacTripHook
        {
            public void DeletedHook(Sim participant)
            {
                ParentsLeavingTownSituation s = (object)this as ParentsLeavingTownSituation;
                if (s == null || participant == null || s.Teens == null || !s.Teens.Contains(participant) || !s.AreParentsOutOfTown() || !ShortcutOn("vacteendelete"))
                {
                    DeletedOrig(participant);
                    return;
                }
                participant.RemoveRole(s);
                s.Teens.Remove(participant);
                PatchCounters()[474]++;
                VacLater(s.Lot, 10f, "Ts3Fixes vacteendelete", delegate { VacTeenCheck(s); });
            }
            public void DeletedOrig(Sim participant) { } // receives ParentsLeavingTownSituation.OnParticipantDeleted's original header

            public void CleanUpHook()
            {
                ParentsLeavingTownSituation s = (object)this as ParentsLeavingTownSituation;
                if (s != null && ParentsLeavingTownSituation.sAdultsInventories != null && !s.ParentsAreGettingPickedUp
                    && !sVacQuitting && !GameStates.IsTravelling && ShortcutOn("vacexitreturn"))
                    VacBringBackNow(s);
                CleanUpOrig();
            }
            public void CleanUpOrig() { } // receives ParentsLeavingTownSituation.CleanUp's original header
        }

        static void VacTeenCheck(ParentsLeavingTownSituation s)
        {
            if (s.SituationExited || !s.AreParentsOutOfTown()) return;
            VacRejoin(s);
            if (s.Teens.Count > 0) return;
            PatchCounters()[476]++;
            ShortcutLog("vacteendelete: no teen left on the trip: parents come back");
            s.BringParentsBack();
        }

        static void VacRejoin(ParentsLeavingTownSituation s)
        {
            Household h = VacHousehold(s);
            if (h == null || s.Teens == null) return;
            foreach (Sim t in h.Sims)
            {
                if (t == null || t.HasBeenDestroyed || t.SimDescription == null || !t.SimDescription.Teen || s.Teens.Contains(t)) continue;
                t.AssignRole(s);
                s.Teens.Add(t);
                PatchCounters()[475]++;
            }
        }

        // Every stored adult who is alive, in the household and without a Sim, re-created on the home lot with its stored
        // inventory and bot chips (what BringAdultsBackToWorld restores), before CleanUp's ResetStatics drops them.
        static void VacBringBackNow(ParentsLeavingTownSituation s)
        {
            Household h = VacHousehold(s);
            if (h == null || h.LotHome == null) return;
            foreach (ulong id in VacStoredIds())
            {
                if (VacVerdict(id, h) != EaVacationCore.kEaMakes) continue;
                SimDescription d = SimDescription.Find(id);
                try
                {
                    Sim sim = d.InstantiateOffScreen(h.LotHome);
                    if (sim == null) continue;
                    List<InventoryItem> items = ParentsLeavingTownSituation.sAdultsInventories[id];
                    if (items != null) { sim.Inventory.DestroyItems(); sim.Inventory.RestoreInventoryFromList(items); }
                    List<TraitChip> chips;
                    if (sim.IsEP11Bot && ParentsLeavingTownSituation.sAdultBotsTraitChips != null && ParentsLeavingTownSituation.sAdultBotsTraitChips.TryGetValue(id, out chips) && chips != null)
                        foreach (TraitChip c in chips) sim.SimDescription.TraitChipManager.AddTraitChip(c, c.TraitChipSlot);
                    PatchCounters()[477]++;
                    ShortcutLog("vacexitreturn: " + d.FullName + " brought back at the trip's exit");
                }
                catch (Exception e) { ShortcutLog("vacexitreturn: " + d.FullName + " " + e.GetType().Name + " " + e.Message); }
            }
        }

        // ---- vaclistenernull (row 6) ----

        static bool VacNullState(EventListenerExportInfo info)
        {
            if (info != null || !ShortcutOn("vaclistenernull")) return false;
            PatchCounters()[478]++;
            return true;
        }

        public class VacListenerHook
        {
            public bool BaseHook(EventListenerExportInfo info) { return VacNullState(info) ? false : BaseOrig(info); }
            public bool BaseOrig(EventListenerExportInfo info) { return false; } // EventListener.RestoreState
            public bool CountHook(EventListenerExportInfo info) { return VacNullState(info) ? false : CountOrig(info); }
            public bool CountOrig(EventListenerExportInfo info) { return false; } // CountListener.RestoreState
            public bool DistinctHook(EventListenerExportInfo info) { return VacNullState(info) ? false : DistinctOrig(info); }
            public bool DistinctOrig(EventListenerExportInfo info) { return false; } // CountListenerDistinct.RestoreState
            public bool ExclusiveHook(EventListenerExportInfo info) { return VacNullState(info) ? false : ExclusiveOrig(info); }
            public bool ExclusiveOrig(EventListenerExportInfo info) { return false; } // CountListenerExclusive.RestoreState
            public bool TotaledHook(EventListenerExportInfo info) { return VacNullState(info) ? false : TotaledOrig(info); }
            public bool TotaledOrig(EventListenerExportInfo info) { return false; } // TotaledListener.RestoreState
        }

        // ---- vacimportfix (row 7) ----

        static void VacRefresh(Opportunity.SourceOrTargetInfo info, ulong descId)
        {
            if (info == null || descId == 0) return;
            SimDescription d = SimDescription.Find(descId);
            Sim sim = d != null ? d.CreatedSim : null;
            bool idValid = info.ObjectId != ObjectGuid.InvalidObjectGuid;
            GameObject cur = idValid ? GlobalFunctions.ConvertGuidToObject<GameObject>(info.ObjectId) : null;
            if (!EaVacationCore.RefreshId(idValid, info.WorldId == GameUtils.GetCurrentWorld(), sim != null && !sim.HasBeenDestroyed, cur != null, cur is Sim, cur == sim)) return;
            info.ObjectId = sim.ObjectId;
            PatchCounters()[479]++;
        }

        public class VacImportHook
        {
            public bool AddHook(OpportunityHistory.OpportunityExportInfo exportInfo, OpportunityNames guid, OpportunityManager.OpportunityPropagateInfo source, OpportunityManager.OpportunityPropagateInfo target, List<OpportunityNames> opportunityChain, bool allowReject)
            {
                if (exportInfo == null || !ShortcutOn("vacimportfix")) return AddOrig(exportInfo, guid, source, target, opportunityChain, allowReject);
                VacRefresh(exportInfo.OriginalTarget, exportInfo.TargetSimDescriptionId);
                VacRefresh(exportInfo.OriginalSource, exportInfo.SourceSimDescriptionId);
                if (EaVacationCore.ExpiryApplies(exportInfo.ExpirationTime.Ticks)) return AddOrig(exportInfo, guid, source, target, opportunityChain, allowReject);
                PatchCounters()[480]++;
                DateAndTime was = exportInfo.ExpirationTime;
                exportInfo.ExpirationTime = new DateAndTime(long.MaxValue); // never "expired"; put back after the call
                try { return AddOrig(exportInfo, guid, source, target, opportunityChain, allowReject); }
                finally { exportInfo.ExpirationTime = was; }
            }
            public bool AddOrig(OpportunityHistory.OpportunityExportInfo exportInfo, OpportunityNames guid, OpportunityManager.OpportunityPropagateInfo source, OpportunityManager.OpportunityPropagateInfo target, List<OpportunityNames> opportunityChain, bool allowReject) { return false; } // OpportunityManager.AddOpportunityFromTravel
        }

        // ---- agent checks (repros bp-vacation-*) ----

        static Dictionary<ulong, string> sVacBefore = new Dictionary<ulong, string>();

        static ParentsLeavingTownSituation VacTrip()
        {
            List<ParentsLeavingTownSituation> l = Situation.GetSituations<ParentsLeavingTownSituation>();
            foreach (ParentsLeavingTownSituation s in l) if (s != null && !s.SituationExited) return s;
            return null;
        }

        static string VacSimState(SimDescription d)
        {
            Sim s = d.CreatedSim;
            if (s == null) return "no Sim";
            int dreams = s.DreamsAndPromisesManager != null ? s.DreamsAndPromisesManager.ActiveNodes.Count : -1;
            int opps = s.OpportunityManager != null ? s.OpportunityManager.List.Count : -1;
            int buffs = 0;
            if (s.BuffManager != null) foreach (object b in s.BuffManager.Buffs) buffs++;
            return "dreams " + dreams + ", opportunities " + opps + ", buffs " + buffs + ", inventory " + (s.Inventory != null ? s.Inventory.NumItemsStored : -1);
        }

        // Stress-labelled, test saves only: "start" skips the dialog, "teendelete" deletes and re-creates a teen's Sim (what a
        // reset mod does), "orphan" takes a stored adult out of the household, "exit" ends the trip, "stall" removes its alarms.
        //   info | maketeen | start | back | compare | teendelete | orphan | exit | stall | repair
        public static string VacationCheck(string what)
        {
            ParentsLeavingTownSituation s = VacTrip();
            switch (what)
            {
                case "info":
                {
                    Household h = Household.ActiveHousehold;
                    int teens = 0, adults = 0;
                    if (h != null) foreach (SimDescription d in h.SimDescriptions) { if (d.Teen) teens++; else if (d.YoungAdultOrAbove) adults++; }
                    string trip = s == null ? "no trip" : "trip " + (s.Child != null ? s.Child.GetType().Name : "?") + ", teens " + (s.Teens == null ? 0 : s.Teens.Count)
                        + ", pickup " + s.ParentsAreGettingPickedUp;
                    List<string> st = new List<string>();
                    foreach (ulong id in VacStoredIds()) { SimDescription d = SimDescription.Find(id); st.Add(d == null ? id + " gone" : d.FullName + " (" + VacSimState(d) + ")"); }
                    return "info: household teens " + teens + ", adults " + adults + "; " + trip + "; stored " + (ParentsLeavingTownSituation.sAdultsInventories == null ? "null" : string.Join("; ", st.ToArray()));
                }
                case "maketeen":
                {
                    Household h = Household.ActiveHousehold;
                    if (h == null) return "maketeen: no active household";
                    foreach (Sim t in h.Sims) if (t.SimDescription.Teen) return "maketeen: has teen " + t.FullName;
                    foreach (Sim c in h.Sims)
                        if (c.SimDescription.Child) { AgingManager.Singleton.AgeTransitionWithoutCake(c); return "maketeen: ageing up " + c.FullName + " (child -> teen, ~40 s)"; }
                    return "maketeen: no teen and no child in the active household (use another save)";
                }
                case "start":
                {
                    if (s != null) return "start: a trip is already running";
                    Household h = Household.ActiveHousehold;
                    List<Sim> teens, adults;
                    if (!ParentsLeavingTownSituation.TryGetAdultsAndTeens(h, out teens, out adults)) return "start: active household needs a teen and an adult in the world";
                    sVacBefore.Clear();
                    List<string> names = new List<string>();
                    foreach (Sim a in adults) { sVacBefore[a.SimDescription.SimDescriptionId] = VacSimState(a.SimDescription); names.Add(a.FullName + " (" + sVacBefore[a.SimDescription.SimDescriptionId] + ")"); }
                    ParentsLeavingTownSituation.Create(adults, teens, h.LotHome);
                    return "start: " + adults.Count + " adults, " + teens.Count + " teens: " + string.Join("; ", names.ToArray());
                }
                case "back":
                    if (s == null || !s.AreParentsOutOfTown()) return "back: no trip with the parents away";
                    s.BringParentsBack();
                    return "back: asked";
                case "compare":
                {
                    List<string> l = new List<string>();
                    foreach (KeyValuePair<ulong, string> kv in sVacBefore)
                    {
                        SimDescription d = SimDescription.Find(kv.Key);
                        l.Add(d == null ? kv.Key + " gone" : d.FullName + ": before " + kv.Value + " / now " + VacSimState(d));
                    }
                    return "compare: " + (s == null ? "no trip" : "trip " + (s.Child != null ? s.Child.GetType().Name : "?")) + "; " + string.Join("; ", l.ToArray());
                }
                case "teendelete":
                {
                    if (s == null || !s.AreParentsOutOfTown() || s.Teens.Count == 0) return "teendelete: no trip with a teen and the parents away";
                    Sim t = s.Teens[0];
                    SimDescription d = t.SimDescription;
                    Vector3 at = t.Position;
                    if (t.IsActiveSim) foreach (Sim o in t.Household.Sims) if (o != t) { PlumbBob.SelectActor(o); break; }
                    t.Destroy();
                    Sim n = d.Instantiate(at);
                    return "teendelete: " + d.FullName + " deleted and re-created (" + (n != null) + "); trip " + (s.SituationExited ? "exited" : s.Child != null ? s.Child.GetType().Name : "?") + ", stored " + VacStoredIds().Count;
                }
                case "orphan":
                {
                    foreach (ulong id in VacStoredIds())
                    {
                        SimDescription d = SimDescription.Find(id);
                        if (d == null || d.CreatedSim != null || d.Household == null) continue;
                        d.Household.Remove(d);
                        return "orphan: " + d.FullName + " taken out of the household (no household now)";
                    }
                    return "orphan: no stored adult without a Sim";
                }
                case "exit":
                    if (s == null) return "exit: no trip";
                    s.Exit();
                    return "exit: done; stored " + (ParentsLeavingTownSituation.sAdultsInventories == null ? "null" : "" + ParentsLeavingTownSituation.sAdultsInventories.Count);
                case "stall":
                {
                    if (s == null || s.Child == null || s.Child.GetType() != sVacAway) return "stall: no trip in ParentsAreAway";
                    MethodInfo rm = sVacAway.GetMethod("RemoveAlarms", kVacAll); // not swapped: Invoke is fine
                    rm.Invoke(s.Child, null);
                    return "stall: alarms removed (trip never returns on its own)";
                }
                case "repair":
                    if (s == null || !s.AreParentsOutOfTown()) return "repair: no trip with the parents away";
                    if (!ShortcutOn("vacstoreguard")) return "repair: vacstoreguard off";
                    VacRepair(s, "agent");
                    return "repair: ran";
                default: return "unknown check " + what + " (info, maketeen, start, back, compare, teendelete, orphan, exit, stall, repair)";
            }
        }
    }
}
