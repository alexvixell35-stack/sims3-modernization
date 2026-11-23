using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Roles;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // paparazzicooldown (tuning kPaparazziCooldown, default off; docs/648). RolePaparazzi.FollowCelebrity runs DoLoop with its
    // callback every kPaparazziRouteCheckTime (1 sim-minute): each call Wanders (a route plan), and when the celebrity is out
    // of sight or past kRefollowCelebrityRange it re-routes close (FindGoodLocation + plan) [verified, decompiled
    // RolePaparazzi]. A follow lasts 30-45 sim-minutes; at its end the callback calls ResetSimToFollow (mFollowingSimId = 0),
    // so the role's next SimulateRole pulse can push a new follow of the same celebrity at once. Logs: Patrick Patel
    // (phase-20261119-0746) made 14 plans in one follow; Becca Bijou (phase-20261115-1617) restarted it 9 times.
    // Fix, only for a non-exempt NPC paparazzo following a celebrity outside the household scope:
    // (a) while the paparazzo (TownieUnseen) and the celebrity (not on screen, FarFromViewers) are both unseen, the callback
    //     runs EA's body at most once per kPaparazziCooldownCheckMinutes (10) of loop lifetime; the other calls return at
    //     once. A due exit (follow time over, celebrity left the lot) always runs EA's body, which ends the follow as before.
    // (b) after a follow ends, the same paparazzo's next FollowCelebrity Run on the same celebrity within
    //     kPaparazziCooldownHours (2) returns false before doing anything (no jig, no route). EA's mFollowingSimId still names
    //     that celebrity, so the role's next pulses pick another celebrity on the lot or none. A follow the celebrity asked
    //     for (CalledToFollow, CallOverPaparazzi) is never refused.
    // Household rule: a cut/skip, so HouseholdExempt || IsPlayerSim for both Sims. A household (or selected, or unknown-scope)
    // celebrity keeps EA's paparazzi completely: for the player they are a feature, and a household Sim never counts as
    // unseen. A household paparazzo likewise.
    // Counters [4094] callbacks skipped (stretched checks), [4095] follows refused by the cooldown, [4096] follows left to EA
    // for an exempt Sim, [4097] errors. Log "paparazzicooldown: ...", at most 10 a real minute.
    public static partial class Fixes
    {
        static readonly PaparazziCooldownCore sPpc = new PaparazziCooldownCore();
        static FieldInfo sPpcLoopTime;
        static int sPpcMinute = -1, sPpcLogged;

        static void ApplyPaparazziCooldown()
        {
            Type fc = typeof(RolePaparazzi.FollowCelebrity);
            sPpcLoopTime = fc.GetField("mLoopTime", kInstAll);
            if (sPpcLoopTime == null) { AppliedLog().Add("paparazzicooldown: FollowCelebrity.mLoopTime not found, not patched"); return; }
            Patch(fc, "Run", Type.EmptyTypes, typeof(PpcHook), "RunHook", "RunOrig");
            Patch(fc, "PaparazziLoopCallback", new Type[] { typeof(StateMachineClient), typeof(InteractionInstance.LoopData) }, typeof(PpcHook), "LoopHook", "LoopOrig");
        }

        static void PpcLog(string s)
        {
            int minute = System.Environment.TickCount / 60000;
            if (minute != sPpcMinute) { sPpcMinute = minute; sPpcLogged = 0; }
            if (sPpcLogged++ < 10) DiagLine("paparazzicooldown: " + s);
        }

        static bool PpcExempt(Sim pap, Sim celeb)
        {
            return HouseholdExempt(pap) || IsPlayerSim(pap) || HouseholdExempt(celeb) || IsPlayerSim(celeb);
        }

        static long PpcCooldownTicks()
        {
            float h = Instantiator.kPaparazziCooldownHours;
            return h > 0f ? SimClock.ConvertToTicks(h, TimeUnit.Hours) : 0;
        }

        // true = refuse this follow
        static bool PpcBeforeRun(RolePaparazzi.FollowCelebrity fc)
        {
            Sim pap = fc != null ? fc.Actor : null, celeb = fc != null ? fc.Target : null;
            if (pap == null || celeb == null || pap.SimDescription == null || celeb.SimDescription == null) return false;
            if (PpcExempt(pap, celeb)) { PatchCounters()[4096]++; return false; }
            if (fc.CalledToFollow || sPpc.EndedCount == 0) return false;
            long now = SimClock.CurrentTicks, cd = PpcCooldownTicks();
            if (!sPpc.Cooling(pap.ObjectId.Value, celeb.ObjectId.Value, now, cd)) return false;
            PatchCounters()[4095]++;
            float left = (float)sPpc.Left(pap.ObjectId.Value, celeb.ObjectId.Value, now, cd) / Math.Max(1L, SimClock.ConvertToTicks(1f, TimeUnit.Minutes));
            PpcLog(pap.FullName + " does not follow " + celeb.FullName + " again yet (" + left.ToString("0") + " sim-min of the cooldown left)");
            return true;
        }

        static void PpcAfterRun(RolePaparazzi.FollowCelebrity fc)
        {
            Sim pap = fc != null ? fc.Actor : null, celeb = fc != null ? fc.Target : null;
            if (pap == null || celeb == null || PpcExempt(pap, celeb)) return;
            sPpc.Ended(pap.ObjectId.Value, celeb.ObjectId.Value, SimClock.CurrentTicks);
        }

        // true = skip EA's callback body this time
        static bool PpcSkipCallback(RolePaparazzi.FollowCelebrity fc, InteractionInstance.LoopData ld)
        {
            Sim pap = fc != null ? fc.Actor : null, celeb = fc != null ? fc.Target : null;
            if (pap == null || celeb == null) return false;
            float stretch = Instantiator.kPaparazziCooldownCheckMinutes;
            if (stretch <= 0f) return false;
            bool exitDue = ld.mLifeTime > (float)sPpcLoopTime.GetValue(fc) || pap.LotCurrent != celeb.LotCurrent;
            // exempt Sims are never eligible: IsPlayerSim inside TownieUnseen for the paparazzo, explicitly for the celebrity
            bool eligible = !exitDue && !PpcExempt(pap, celeb) && TownieUnseen(pap, null)
                && !UcOnScreen(celeb) && FarFromViewers(celeb.Position);
            bool first;
            int v = sPpc.Callback(pap.ObjectId.Value, RuntimeHelpers.GetHashCode(fc), ld.mLifeTime, eligible, exitDue, stretch, out first);
            if (v != PaparazziCooldownCore.kSkip) return false;
            PatchCounters()[4094]++;
            if (first) PpcLog(pap.FullName + " following " + celeb.FullName + ": both unseen, route checks every " + stretch + " sim-min");
            return true;
        }

        public class PpcHook
        {
            public bool RunHook()
            {
                bool on = ShortcutOn("paparazzicooldown");
                RolePaparazzi.FollowCelebrity fc = on ? (object)this as RolePaparazzi.FollowCelebrity : null;
                if (on)
                {
                    bool refuse = false;
                    try { refuse = PpcBeforeRun(fc); }
                    catch (Exception ex) { PatchCounters()[4097]++; PpcLog("error " + ex.GetType().Name); }
                    if (refuse) return false;  // nothing done yet; EA's mFollowingSimId keeps the role off this celebrity
                }
                bool r = RunOrig();
                if (on)
                {
                    try { PpcAfterRun(fc); }
                    catch (Exception ex) { PatchCounters()[4097]++; PpcLog("error after Run " + ex.GetType().Name); }
                }
                return r;
            }
            public bool RunOrig() { return false; } // receives RolePaparazzi+FollowCelebrity.Run's original body

            public void LoopHook(StateMachineClient smc, InteractionInstance.LoopData ld)
            {
                if (ShortcutOn("paparazzicooldown"))
                {
                    bool skip = false;
                    try { skip = PpcSkipCallback((object)this as RolePaparazzi.FollowCelebrity, ld); }
                    catch (Exception ex) { PatchCounters()[4097]++; PpcLog("error in the loop " + ex.GetType().Name); }
                    if (skip) return;
                }
                LoopOrig(smc, ld);
            }
            public void LoopOrig(StateMachineClient smc, InteractionInstance.LoopData ld) { } // receives FollowCelebrity.PaparazziLoopCallback's original body
        }
    }
}
