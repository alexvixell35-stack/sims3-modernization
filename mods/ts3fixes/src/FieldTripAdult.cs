using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Careers;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Situations;

namespace Ts3Fixes
{
    // fieldtripadult (tuning kFieldTripAdult, off by default; docs/565 §2). EA bug fixes in the school field trip
    // (FieldTripSituation, the chaperone search). Only NPC chaperones are affected; household Sims are never picked by EA.
    //
    // (a) [verified, decompiled] BringAdultToSchool: with no NPC educator at work on the school lot, EA loops
    //     `while (list.Count > 0 && mAdult == null)` over LotManager.Actors, picks a random Sim and keeps it only if
    //     FindRandomAdultTest passes, but never removes a rejected Sim from the list. When no instantiated Sim passes (every
    //     NPC adult at work or in mAdultsThatFailedToShow, which grows on each chaperone route failure) the loop never ends
    //     and the game freezes on the field-trip day. Fix: before EA's search, when neither EA's educator test nor its adult
    //     test can pass for anyone, the trip ends the way EA ends it with no adult (Exit), so the loop is never entered.
    // (b) [verified, decompiled] FindRandomAdultTest checks NPC, not teen-or-below, not at work, not failed before: an adult
    //     pet or horse, or a service NPC on the job (a repairman mid-repair, the maid), passes; EA then cancels its queue,
    //     maxes its motives and walks it to school as the chaperone. Fix: such Sims fail the test (human, not performing a
    //     service).
    // (c) [verified, decompiled] OnParticipantDeleted: when the chaperone is deleted EA calls Exit(), whose CleanUp nulls
    //     mAdultsThatFailedToShow, then reads that list: NullReferenceException out of Situation.NotifyParticipantDeleted
    //     during the Sim's deletion. Fix: return after EA's Exit. Also: RouteAdultToSchool.Init calls Exit() with no lot and
    //     then still runs BringAdultToSchool; that call now does nothing on an exited trip.
    // (d) [verified, decompiled] TriggerCarpoolHome (a participant's rabbit-hole loop, 15 minutes before school ends)
    //     sets mTriggeredCarpoolHome, then books the bus to `PlumbBob.SelectedActor.LotHome`: with no selected Sim at that
    //     tick, NullReferenceException out of the loop callback, which ends that Sim's trip early (no AttendedFieldTrip /
    //     kWentOnFieldTrip) and, the flag being set, nobody is bussed home. With a homeless selected household the bus is
    //     booked to a null lot. Fix: with no selected Sim while a selectable participant has a home, the call waits for the
    //     next tick (EA calls it every tick until school ends); with no selectable participant to bus (EA's SimsGoingHome
    //     would return nobody) or no home lot, the bus is skipped and the flag set as EA would.
    //     Not fixed, confirmed unreachable: InRabbitHole reads mFieldTripRabbithole after nulling it for the chaperone, but
    //     only for a selectable chaperone, and EA picks NPC chaperones only (both tests require IsNPC).
    //
    // Counters: [2527] trips ended with no possible chaperone (hang avoided), [2528] unfit chaperones skipped,
    // [2529] deleted-chaperone / exited-trip calls handled, [2533] errors, [2540] carpool home with no selected Sim / no home handled. Check: FieldTripAdultChecks.cs.
    public static partial class Fixes
    {
        const BindingFlags kFtaAll = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        static readonly FieldInfo sFtaSchoolLot = typeof(FieldTripSituation).GetField("mSchoolLot", kFtaAll);
        static readonly FieldInfo sFtaFailed = typeof(FieldTripSituation).GetField("mAdultsThatFailedToShow", kFtaAll);
        static readonly FieldInfo sFtaCarpool = typeof(FieldTripSituation).GetField("mTriggeredCarpoolHome", kFtaAll);

        static void ApplyFieldTripAdult()
        {
            Patch(typeof(FieldTripSituation), "FindRandomAdultTest", typeof(FtaHook), "AdultTestHook", "AdultTestOrig");
            Patch(typeof(FieldTripSituation), "OnParticipantDeleted", typeof(FtaHook), "DeletedHook", "DeletedOrig");
            if (sFtaSchoolLot != null && sFtaFailed != null) Patch(typeof(FieldTripSituation), "BringAdultToSchool", typeof(FtaHook), "BringHook", "BringOrig");
            else AppliedLog().Add("fieldtripadult: FieldTripSituation fields not found, chaperone search hang not patched");
            if (sFtaCarpool != null && sFtaCarpool.FieldType == typeof(bool)) Patch(typeof(FieldTripSituation), "TriggerCarpoolHome", typeof(FtaHook), "CarpoolHook", "CarpoolOrig");
            else AppliedLog().Add("fieldtripadult: FieldTripSituation.mTriggeredCarpoolHome not found, carpool part not patched");
        }

        // ---- decisions (also used by the check) ----
        // (b) a Sim EA's adult test let through that cannot chaperone: a pet / horse, or a service NPC on the job
        internal static bool FtaUnfit(bool on, bool isHuman, bool performingService)
        {
            return on && (!isHuman || performingService);
        }
        // EA's FindRandomAdultTest plus (b), as a pure decision
        internal static bool FtaAdultOk(bool on, bool isNpc, bool teenOrBelow, bool atWork, bool failedBefore, bool isHuman, bool performingService)
        {
            return isNpc && !teenOrBelow && !atWork && !failedBefore && !FtaUnfit(on, isHuman, performingService);
        }
        // EA's FindRandomEducatorTest
        internal static bool FtaEducatorOk(bool isNpc, bool teenOrBelow, bool failedBefore, bool atWork, bool isEducator)
        {
            return isNpc && !teenOrBelow && !failedBefore && atWork && isEducator;
        }
        // (a) EA's search would spin forever: no educator and no adult can pass
        internal static bool FtaNoChaperone(bool on, int educators, int adults)
        {
            return on && educators == 0 && adults == 0;
        }

        // (d) carpool home: 0 = EA's code, 1 = wait for a selected Sim (next tick), 2 = skip the bus (nobody to take home / no home)
        internal static int FtaCarpool(bool on, bool hasSelected, bool selectedHasHome, bool selectableParticipantWithHome)
        {
            if (!on || (hasSelected && selectedHasHome)) return 0;
            return !hasSelected && selectableParticipantWithHome ? 1 : 2;
        }

        static bool FtaAdultOk(Sim s, List<Sim> failed)
        {
            return s != null && !s.HasBeenDestroyed && s.SimDescription != null
                && FtaAdultOk(true, s.IsNPC, s.SimDescription.TeenOrBelow, s.IsAtWork, failed != null && failed.Contains(s), s.IsHuman, s.IsPerformingAService);
        }
        static bool FtaEducatorOk(Sim s, List<Sim> failed)
        {
            return s != null && s.SimDescription != null
                && FtaEducatorOk(s.IsNPC, s.SimDescription.TeenOrBelow, failed != null && failed.Contains(s), s.IsAtWork, s.CareerManager != null && s.CareerManager.Occupation is Education);
        }

        public class FtaHook
        {
            public bool AdultTestHook(Sim sim)
            {
                bool r = AdultTestOrig(sim);
                if (!r || !ShortcutOn("fieldtripadult")) return r;  // fast path: EA said no, or switch off
                if (!FtaUnfit(true, sim.IsHuman, sim.IsPerformingAService)) return true;
                PatchCounters()[2528]++;
                return false;
            }
            public bool AdultTestOrig(Sim sim) { return false; } // receives FieldTripSituation.FindRandomAdultTest's original body

            public void BringHook()
            {
                if (ShortcutOn("fieldtripadult"))
                {
                    try
                    {
                        object st = this;
                        FieldTripSituation trip = (FieldTripSituation)st;
                        if (trip.SituationExited) { PatchCounters()[2529]++; return; }  // RouteAdultToSchool.Init after its own Exit
                        Lot school = (Lot)sFtaSchoolLot.GetValue(st);
                        List<Sim> failed = (List<Sim>)sFtaFailed.GetValue(st);
                        int educators = 0, adults = 0;
                        if (school != null)
                            foreach (Sim s in school.GetObjects<Sim>())
                                if (FtaEducatorOk(s, failed)) { educators++; break; }
                        if (educators == 0)
                            foreach (Sim s in LotManager.Actors)
                                if (FtaAdultOk(s, failed)) { adults++; break; }
                        if (FtaNoChaperone(true, educators, adults))
                        {
                            PatchCounters()[2527]++;
                            trip.Exit();  // EA's own end when no adult was found
                            return;
                        }
                    }
                    catch (Exception) { PatchCounters()[2533]++; }
                }
                BringOrig();
            }
            public void BringOrig() { } // receives FieldTripSituation.BringAdultToSchool's original body

            public void DeletedHook(Sim participant)
            {
                if (ShortcutOn("fieldtripadult"))
                {
                    try
                    {
                        object st = this;
                        FieldTripSituation trip = (FieldTripSituation)st;
                        if (participant != null && (trip.SituationExited || participant == trip.Adult))
                        {
                            PatchCounters()[2529]++;
                            if (!trip.SituationExited) trip.Exit();  // EA's step; its CleanUp already drops every student
                            return;
                        }
                    }
                    catch (Exception) { PatchCounters()[2533]++; }
                }
                DeletedOrig(participant);
            }
            public void DeletedOrig(Sim participant) { }

            public void CarpoolHook()
            {
                if (ShortcutOn("fieldtripadult"))
                {
                    try
                    {
                        object st = this;
                        FieldTripSituation trip = (FieldTripSituation)st;
                        if (!(bool)sFtaCarpool.GetValue(st))
                        {
                            Sim sel = PlumbBob.SelectedActor;
                            bool any = trip.Adult != null && trip.Adult.IsSelectable && trip.Adult.LotHome != null;
                            foreach (Sim s in trip.Students)
                                if (s != null && s.IsSelectable && s.LotHome != null) any = true;
                            int k = FtaCarpool(true, sel != null, sel != null && sel.LotHome != null, any);
                            if (k != 0)
                            {
                                PatchCounters()[2540]++;
                                if (k == 2) sFtaCarpool.SetValue(st, true);
                                return;
                            }
                        }
                    }
                    catch (Exception) { PatchCounters()[2533]++; }
                }
                CarpoolOrig();
            }
            public void CarpoolOrig() { } // receives FieldTripSituation.TriggerCarpoolHome's original body // receives FieldTripSituation.OnParticipantDeleted's original body
        }
    }
}
