using System;
using Sims3.Gameplay;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Objects.Vehicles;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // travelcarnew (kTravelCarNew, docs/598 §6.3) [verified, decompiled; the travel path inferred]: every trip gives the
    // household's cars the "New Car Smell" moodlet again (player report 2026-11-20). CarOwnable.IsNewCar is "TimeOfPurchase at
    // most BuffNewCarSmell.DaysOfNewCarSmell (4) days ago", and a Sim who gets in a new car gets the moodlet. TimeOfPurchase is
    // written only in CarOwnable.OnCreation (= now); CarOwnable exports nothing of its own (its ImportContent only re-creates
    // the sound). On travel, Inventory.ImportContent fills an inventory with objects rebuilt from the travel stash, so each
    // rebuilt car ran OnCreation and counts as bought on arrival (and again on the way home). GameStates.IsTravelling
    // (sIsChangingWorlds) is set in TravelToVacationWorld / ReturnFromVacationWorld and cleared only after the arrival import
    // (OnArrivalAtVacationWorld, PostTravelingFixUp).
    // Fix: after CarOwnable.ImportContent during travel, a car that counts as new gets a TimeOfPurchase one day before the
    // new-car window, so it is not new. Cost: a car bought less than 4 days before a trip loses its last new-car days.
    // Outside travel (library bin, Edit Town) EA's behaviour is kept. Household Sims are affected on purpose: an EA bug fix
    // with nothing faked. The hook is newcartest's ImportHook (NewCar598.cs; one hook per method).
    // Counters: [2119] cars made old; errors go to newcartest's [2751]. Checks: TravelCarNewCheck below.
    public static partial class Fixes
    {
        // Pure: does an imported car lose its new-car status?
        internal static bool TcnMakeOld(bool on, bool travelling, bool isNew)
        {
            return on && travelling && isNew;
        }

        internal static bool TcnAfterImport(CarOwnable car, bool travelling)
        {
            try
            {
                if (car == null || !TcnMakeOld(ShortcutOn("travelcarnew"), travelling, car.IsNewCar)) return false;
                car.TimeOfPurchase = SimClock.Subtract(SimClock.CurrentTime(), TimeUnit.Days, BuffNewCarSmell.DaysOfNewCarSmell + 1f);
                PatchCounters()[2119]++;
                return true;
            }
            catch (ResetException) { throw; }
            catch (Exception) { PatchCounters()[2751]++; return false; }
        }

        // Harness: "run" pure decisions with controls. "live": on the newest car in the world, TimeOfPurchase set to now (what
        // OnCreation does to a rebuilt car), then the import step as during travel, IsNewCar read, the old value put back.
        public static object TravelCarNewCheck(string what)
        {
            string counters = B582Counters("travelcarnew", 2119, new[] { "cars made old" }, "");
            if (what == "result") return counters;
            bool on = ShortcutOn("travelcarnew");
            if (what == "live") return TcnLive();
            if (what != "run") return "travelcarnew <run|live|result>";
            bool fixedNow = TcnMakeOld(on, true, true);
            bool controls = !TcnMakeOld(on, false, true) && !TcnMakeOld(on, true, false);
            return B582Line("travelcarnew", on, "a new car imported during travel made old", fixedNow,
                "outside travel / an old car left to EA=" + controls, controls, "", counters);
        }

        static string TcnLive()
        {
            CarOwnable best = null;
            foreach (CarOwnable c in Sims3.Gameplay.Queries.GetObjects<CarOwnable>())
                if (c != null && (best == null || c.ObjectId.Value > best.ObjectId.Value)) best = c;
            if (best == null) return "no car in the world";
            DateAndTime saved = best.TimeOfPurchase;
            best.TimeOfPurchase = SimClock.CurrentTime();
            bool before = best.IsNewCar;
            TcnAfterImport(best, true);
            bool after = best.IsNewCar;
            best.TimeOfPurchase = saved;
            return "car " + best.ObjectId.Value + " (" + best.GetType().Name + "): rebuilt car new=" + before
                + ", after the travel import step new=" + after + (after ? " (moodlet comes back: BROKEN)" : " (not new: FIXED)")
                + "; TimeOfPurchase restored";
        }
    }
}
