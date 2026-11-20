using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects.Vehicles;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // docs/853 §4 boatroamcap (default ON). An NPC driving a boat on a world lot stays in EA's Boat.BoatAround loop for many sim
    // hours, overnight too (Isla Paradiso 2026-11-19: one Sim roamed every ~2 sim-hours from 10 PM to the next afternoon, another
    // made 50 roam plans in one session [verified, log]). Each run is a CreateRoamingBoatPath plan (~150-275 ms on the route
    // worker) plus a plan back to the start. BoatAround.Definition.Test only asks that the Sim drives the boat, the boat is not
    // routing and stands on a world lot; Run has no exit of its own [verified, decompiled Boat.cs]. EA's way out,
    // Boat.ReturnToShore (drive to the shore by the boat's previous lot or the Sim's home, then get out), is apparently never
    // picked by autonomy for these NPCs [inferred: no ReturnToShore in the logged streaks].
    // This fix: before BoatAround.Run of a non-exempt NPC driver, BoatRoamCapCore keeps its streak (runs, first run's sim time,
    // boat). When the streak is long enough (kBoatRoamCapRoams roams or kBoatRoamCapHours since the first run), in the night
    // window (kBoatRoamCapNightFrom..To; SimClock.IsNightTime starts at sunset, too early for an evening sail) or with energy or
    // bladder below kBoatRoamCapMotive, the run does not roam: EA's Boat.ReturnToShore is pushed as the run's continuation at the
    // run's priority and the run ends. ReturnToShore's own early exits (posture, BoatOwnable, no lot, an invalid shore point by
    // its own GetLotPositionForRoute) are checked first; any of them, a refused push, a return that fails or never runs: EA's
    // roam runs as before, and the next try waits kBoatRoamCapBackoffHours. Never a teleport, never a deleted boat.
    // Household rule: Fixes.HouseholdExempt, and IsPlayerSim as well since this cuts an interaction (HouseholdToo never lifts a
    // cut, docs/271); unknown scope and the selected Sim count as household (IsPlayerSim), and so does a boat with any
    // household passenger. Diagnostics: a service or role NPC (Service, CreatedByService, AssignedRole) or a run from an
    // AlarmOneShot task is logged once per Sim per sim-hour, capped or not.
    // Counters [1392] runs capped (no roam), [1393] returns pushed, [1394] returns failed (precheck, push, run, never ran),
    // [1395] exempt runs, [1396] errors, [1397] returns that reached the shore, [1398] service / role / alarm-task boating
    // logged, [1399] caps by the night window or a low motive (of 1392).
    public static partial class Fixes
    {
        static readonly BoatRoamCapCore sBrc = new BoatRoamCapCore();
        static readonly Dictionary<ulong, long> sBrcSvcSeen = new Dictionary<ulong, long>();
        static MethodInfo sBrcShorePos;  // Boat.ReturnToShore.GetLotPositionForRoute(Lot), private
        static int sBrcMinute = -1, sBrcLogged;

        static void ApplyBoatRoamCap()
        {
            sBrcShorePos = typeof(Boat.ReturnToShore).GetMethod("GetLotPositionForRoute", kInstAll, null, new Type[] { typeof(Lot) }, null);
            if (sBrcShorePos == null) { AppliedLog().Add("boatroamcap: ReturnToShore.GetLotPositionForRoute not found, not patched"); return; }
            Patch(typeof(Boat.BoatAround), "Run", Type.EmptyTypes, typeof(BrcRunHook), "RunHook", "RunOrig");
            // the shore hook only does bookkeeping for returns BrcRunHook pushed
            Patch(typeof(Boat.ReturnToShore), "Run", Type.EmptyTypes, typeof(BrcShoreHook), "RunHook", "RunOrig");
        }

        static void BrcLog(string s)
        {
            int minute = System.Environment.TickCount / 60000;
            if (minute != sBrcMinute) { sBrcMinute = minute; sBrcLogged = 0; }
            if (sBrcLogged++ < 10) DiagLine("boatroamcap: " + s);
        }

        static long BrcTicks(float hours) { return hours > 0f ? SimClock.ConvertToTicks(hours, TimeUnit.Hours) : 0; }

        static BoatRoamCapCore.Tun BrcTun()
        {
            BoatRoamCapCore.Tun t = new BoatRoamCapCore.Tun();
            t.MaxRoams = Instantiator.kBoatRoamCapRoams;
            t.CapTicks = BrcTicks(Instantiator.kBoatRoamCapHours);
            t.GapTicks = BrcTicks(Instantiator.kBoatRoamCapGapHours);
            t.BackoffTicks = BrcTicks(Instantiator.kBoatRoamCapBackoffHours);
            t.NightFrom = Instantiator.kBoatRoamCapNightFrom;
            t.NightTo = Instantiator.kBoatRoamCapNightTo;
            t.MotiveLow = Instantiator.kBoatRoamCapMotive;
            return t;
        }

        // the driver or any occupant is household scope (unknown, selected, selectable or the active household)
        static bool BrcExempt(Sim sim, Boat boat)
        {
            if (HouseholdExempt(sim) || IsPlayerSim(sim)) return true;
            Sim[] occ = boat.Occupants;
            if (occ != null)
                for (int i = 0; i < occ.Length; i++)
                    if (occ[i] != null && IsPlayerSim(occ[i])) return true;
            return false;
        }

        static float BrcMotive(Sim sim, CommodityKind k)
        {
            Motives m = sim.Motives;
            return m != null ? m.GetValue(k) : 0f;
        }

        // a service or role NPC, or a run from an AlarmOneShot task: logged once per Sim per sim-hour
        static void BrcServiceNote(Sim sim, Boat boat, long now)
        {
            Sims3.Gameplay.CAS.SimDescription d = sim.SimDescription;
            string what = d.Service != null ? "service " + d.Service.GetType().Name
                : d.CreatedByService != null ? "created by " + d.CreatedByService.GetType().Name
                : d.AssignedRole != null ? "role " + d.AssignedRole.GetType().Name : null;
            IScriptProxy tp = Simulator.GetProxy(Simulator.CurrentTask);
            object tt = tp != null ? tp.Target : null;
            string task = tt == null ? "a non-object task" : tt == sim ? "its own task" : tt.GetType().Name + " task";
            bool alarm = tt != null && tt.GetType().Name == "AlarmOneShot";
            if (what == null && !alarm) return;
            ulong id = sim.ObjectId.Value;
            long last;
            if (sBrcSvcSeen.TryGetValue(id, out last) && now >= last && now - last < BrcTicks(1f)) return;
            if (sBrcSvcSeen.Count >= BoatRoamCapCore.kMax) sBrcSvcSeen.Clear();
            sBrcSvcSeen[id] = now;
            PatchCounters()[1398]++;
            BrcLog("service/role NPC " + sim.FullName + " (" + (what ?? "no role") + ") boating in " + boat.GetType().Name + ", in " + task + ", " + SimClock.CurrentTime().ToString());
        }

        // EA's ReturnToShore early exits, checked before the push: null = it can run, else why not
        static string BrcCannotReturn(Sim sim, Boat boat, Boat.ReturnToShore inst)
        {
            SittingInBoat sib = sim.Posture as SittingInBoat;
            if (sib == null || sib.Container != boat) return "not sitting in this boat";
            BoatOwnable own = boat as BoatOwnable;
            if (own == null) return "not an ownable boat";
            Lot lot = own.PreviousLot();
            if (lot == null || lot.LotId == 0) lot = sim.LotHome;
            if (lot == null) return "no previous lot or home";
            Vector3 p = (Vector3)sBrcShorePos.Invoke(inst, new object[] { lot });
            if (p == Vector3.Invalid) return "invalid shore position for " + lot.Name;
            return null;
        }

        // true = this run must not roam (ReturnToShore was pushed as its continuation)
        static bool BrcBefore(Boat.BoatAround ba)
        {
            Sim sim = ba != null ? ba.Actor : null;
            Boat boat = ba != null ? ba.Target : null;
            if (sim == null || boat == null || sim.SimDescription == null) return false;
            int[] c = PatchCounters();
            if (BrcExempt(sim, boat)) { c[1395]++; return false; }
            long now = SimClock.CurrentTicks;
            BrcServiceNote(sim, boat, now);
            ulong id = sim.ObjectId.Value;
            BoatRoamCapCore.Tun t = BrcTun();
            BoatRoamCapCore.Entry e;
            int v = sBrc.Check(id, boat.ObjectId.Value, now, (int)SimClock.CurrentTime().Hour, BrcMotive(sim, CommodityKind.Energy), BrcMotive(sim, CommodityKind.Bladder), t, out e);
            if (v == BoatRoamCapCore.kReturnLost)
            {
                c[1394]++;
                BrcLog(sim.FullName + " still boating after the pushed return; EA roams, next try in " + Instantiator.kBoatRoamCapBackoffHours + " sim-h");
                return false;
            }
            if (v != BoatRoamCapCore.kCap) return false;
            float hours = (float)(now - e.Start) / Math.Max(1L, BrcTicks(1f));
            Boat.ReturnToShore inst = Boat.ReturnToShore.Singleton.CreateInstance(boat, sim, ba.GetPriority(), ba.Autonomous, ba.CancellableByPlayer) as Boat.ReturnToShore;
            string why = inst == null ? "no ReturnToShore instance" : BrcCannotReturn(sim, boat, inst);
            if (why == null && !sim.InteractionQueue.TryPushAsContinuation(ba, inst)) why = "continuation push refused";
            if (why != null)
            {
                c[1394]++;
                sBrc.Failed(id, now, t.BackoffTicks);
                BrcLog(sim.FullName + " not sent to shore (" + why + ") after " + e.Roams + " roams / " + hours.ToString("0.0") + " sim-h; EA roams");
                return false;
            }
            sBrc.Pushed(id, now);
            c[1392]++;
            c[1393]++;
            if (e.Reason != BoatRoamCapCore.kStreak) c[1399]++;
            BrcLog(sim.FullName + " back to shore after " + e.Roams + " roams / " + hours.ToString("0.0") + " sim-h ("
                + BoatRoamCapCore.Why(e.Reason) + ", " + boat.GetType().Name + ", " + SimClock.CurrentTime().ToString() + ")");
            return true;
        }

        static void BrcAfterShore(Boat.ReturnToShore rs, bool ok)
        {
            Sim sim = rs != null ? rs.Actor : null;
            if (sim == null) return;
            if (!sBrc.Returned(sim.ObjectId.Value, ok, SimClock.CurrentTicks, BrcTicks(Instantiator.kBoatRoamCapBackoffHours))) return;
            int[] c = PatchCounters();
            if (ok) c[1397]++;
            else
            {
                c[1394]++;
                BrcLog(sim.FullName + " ReturnToShore failed; EA roams, next try in " + Instantiator.kBoatRoamCapBackoffHours + " sim-h");
            }
        }

        public class BrcRunHook
        {
            public bool RunHook()
            {
                if (ShortcutOn("boatroamcap"))
                {
                    bool capped = false;
                    try { capped = BrcBefore((object)this as Boat.BoatAround); }
                    catch (Exception ex) { PatchCounters()[1396]++; BrcLog("error " + ex.GetType().Name); }
                    if (capped) return true;  // no roam; the pushed ReturnToShore runs next
                }
                return RunOrig();
            }
            public bool RunOrig() { return false; } // receives Boat+BoatAround.Run's original body
        }

        public class BrcShoreHook
        {
            public bool RunHook()
            {
                bool r = RunOrig();
                if (!ShortcutOn("boatroamcap")) return r;
                try { BrcAfterShore((object)this as Boat.ReturnToShore, r); }
                catch (Exception ex) { PatchCounters()[1396]++; BrcLog("error after ReturnToShore " + ex.GetType().Name); }
                return r;
            }
            public bool RunOrig() { return false; } // receives Boat+ReturnToShore.Run's original body
        }
    }
}
