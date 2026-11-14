using System;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.ChildAndTeenUpdates;

namespace Ts3Fixes
{
    // bsrefund (tuning kBsRefund, off by default; docs/546 §12). EA bug fix, every Sim: a refund and a school, no Sim moved.
    //
    // Enrolling in boarding school (BoardingSchool.SchedulePickUp) takes the tuition (BoardingSchoolData.Cost) at once and
    // drops the Sim's normal school; the next day at 9:00 BoardingSchoolPickUpSituation sends an NPC car. Its CreateCarAndRoute
    // state ends two ways without the Sim reaching the school [verified in the decompiled game code]:
    //   - OnFailure (the car's drive to the lot failed, the student's GetInCar was refused, the driver's wait ended): the
    //     student is withdrawn (OnRemovedFromSchool, AssignSchool) with no refund. EA refunds only in one branch of the
    //     driver's Run (the student aged out of child/teen), which already set the school type to None.
    //   - Init with no car (CarNpcManager.CreateNpcCarWithDriver null): the situation exits and the student stays enrolled
    //     but at home, with no normal school and no pickup alarm left; re-enrolling is refused ("already enrolled").
    // The fix refunds the tuition whenever a pickup ends with the student still enrolled in the school it was for, and on the
    // no-car exit also withdraws the student the way OnFailure does. EA's aged-out branch (school type already None) is not
    // refunded twice. Counter [2351] refunds, [2352] of them no-car withdrawals, [2353] errors (registry fixes-2345).
    // No harness check: the pickup needs an enrolment (a modal dialog) and an NPC car; see docs/546 §12.
    public static partial class Fixes
    {
        static readonly Type sBsCarState = typeof(BoardingSchoolPickUpSituation).GetNestedType("CreateCarAndRoute", BindingFlags.Public | BindingFlags.NonPublic);

        static void ApplyBoardingRefund()
        {
            if (sBsCarState == null) { AppliedLog().Add("BoardingSchoolPickUpSituation.CreateCarAndRoute: not found, bsrefund skipped"); return; }
            Patch(sBsCarState, "OnFailure", typeof(BoardingRefundHook), "OnFailureHook", "OnFailureOrig");
            Patch(sBsCarState, "Init", typeof(BoardingRefundHook), "InitHook", "InitOrig");
        }

        public class BoardingRefundHook
        {
            public void OnFailureHook(Sim actor, float x)
            {
                BsRefund(((ChildSituation<BoardingSchoolPickUpSituation>)(object)this).Parent, false);  // before EA withdraws
                OnFailureOrig(actor, x);
            }
            public void OnFailureOrig(Sim actor, float x) { } // receives CreateCarAndRoute.OnFailure's original body

            public void InitHook(BoardingSchoolPickUpSituation parent)
            {
                InitOrig(parent);
                if (parent != null && parent.Car == null) BsRefund(parent, true);  // EA exited without a car
            }
            public void InitOrig(BoardingSchoolPickUpSituation parent) { } // receives CreateCarAndRoute.Init's original body
        }

        // Refunds the pickup's tuition when its student is still enrolled in that school; withdraw = also take the student
        // out as EA's OnFailure does (no car, so no GetOut).
        static void BsRefund(BoardingSchoolPickUpSituation pickup, bool withdraw)
        {
            if (!ShortcutOn("bsrefund")) return;
            try
            {
                Sim student = pickup == null ? null : pickup.Student;
                SimDescription d = student == null ? null : student.SimDescription;
                if (d == null || d.BoardingSchool == null || pickup.BSD == null || d.BoardingSchool.CurrentSchoolType != pickup.BSD.SchoolType) return;
                if (withdraw)
                {
                    d.BoardingSchool.OnRemovedFromSchool();
                    d.AssignSchool();
                    PatchCounters()[2352]++;
                }
                student.ModifyFunds(pickup.BSD.Cost);
                PatchCounters()[2351]++;
            }
            catch (Exception) { PatchCounters()[2353]++; }
        }
    }
}
