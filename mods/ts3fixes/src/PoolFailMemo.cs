using System;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Pools;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // poolfailmemo (tuning kPoolFailMemo, default off; docs/645 §2). NPCs pick GetInPool again and again on a pool they cannot
    // enter: GetInPool.Run plans PlanToSlot to the pool's Inside routing slots, and on pools with none ("pool inside slots 0",
    // routecauselog) the plan fails at once with FailedBecauseNoDestinationSpecified (OFF runs: Bridgeport 816 fails in 25
    // runs, Isla Paradiso 135). Autonomy scores the pool again on the next pass, so the same NPC retries all day.
    // Fix: when a non-exempt NPC's plan inside GetInPool (its current interaction, target a Pool) fails with no path or no
    // destination, the pair (Sim, pool) is remembered for kPoolFailMemoHours sim-hours (RouteFailLog.RflPlan calls PfmPlan
    // for failed plans), and GetInPool's Definition.Test refuses that pair for autonomous picks only (GenericFail, the same
    // answer EA gives a Fatigued Sim). User-directed picks always run EA's test. Household, selected and unknown-scope Sims
    // (HouseholdExempt or IsPlayerSim) are never recorded and never refused. Rule: PoolFailMemoCore.
    // Counters [4602] recorded, [4603] autonomous tests refused, [4608] exempt (shared), [4609] errors (shared).
    // Log "poolfailmemo: recorded|refused <Sim>, pool 0x<id> ...", capped at 10 a minute.
    public static partial class Fixes
    {
        static readonly PoolFailMemoCore sPfm = new PoolFailMemoCore();
        static int sPfmMinute = -1, sPfmLogged;

        static void ApplyPoolFailMemo()
        {
            Type def = typeof(GetInPool).GetNestedType("Definition", BindingFlags.Public | BindingFlags.NonPublic);
            if (def == null) { AppliedLog().Add("poolfailmemo: GetInPool.Definition not found, not patched"); return; }
            Patch(def, "Test", new Type[] { typeof(InteractionInstanceParameters).MakeByRefType(), typeof(GreyedOutTooltipCallback).MakeByRefType() }, typeof(PoolFailMemoHook), "TestHook", "TestOrig");
        }

        static void PfmLog(string s)
        {
            int minute = System.Environment.TickCount / 60000;
            if (minute != sPfmMinute) { sPfmMinute = minute; sPfmLogged = 0; }
            if (sPfmLogged++ < 10) DiagLine("poolfailmemo: " + s);
        }

        static bool PfmExempt(Sim s) { return HouseholdExempt(s) || IsPlayerSim(s); }

        // from RflPlan (RouteFailLog.cs), failed plans of a Sim follower only
        static void PfmPlan(Sim sim, RoutePlanResult res)
        {
            if (!ShortcutOn("poolfailmemo")) return;
            int[] c = PatchCounters();
            try
            {
                if (!PoolFailMemoCore.Fails((int)res.mType)) return;
                GetInPool gip = sim.CurrentInteraction as GetInPool;
                Pool pool = gip != null ? gip.Target : null;
                if (pool == null) return;
                long life = SimClock.ConvertToTicks(Instantiator.kPoolFailMemoHours, TimeUnit.Hours);
                int r = sPfm.Record(sim.ObjectId.Value, pool.ObjectId.Value, PfmExempt(sim), (int)res.mType, SimClock.CurrentTicks, life);
                if (r == PoolFailMemoCore.kExempt) { c[4608]++; return; }
                if (r != PoolFailMemoCore.kRecorded) return;
                c[4602]++;
                PfmLog("recorded " + sim.FullName + ", pool 0x" + pool.ObjectId.Value.ToString("X") + ", " + res.mType + ", "
                    + Instantiator.kPoolFailMemoHours + " sim-h");
            }
            catch (Exception ex) { c[4609]++; PfmLog("error " + ex.GetType().Name); }
        }

        public class PoolFailMemoHook
        {
            public InteractionTestResult TestHook(ref InteractionInstanceParameters parameters, ref GreyedOutTooltipCallback greyedOutTooltipCallback)
            {
                InteractionTestResult r = TestOrig(ref parameters, ref greyedOutTooltipCallback);
                if (r != InteractionTestResult.Pass || !parameters.Autonomous || sPfm.Count == 0 || !ShortcutOn("poolfailmemo")) return r;
                int[] c = PatchCounters();
                try
                {
                    Sim sim = parameters.Actor as Sim;
                    Sims3.Gameplay.Interfaces.IGameObject pool = parameters.Target;
                    if (sim == null || pool == null) return r;
                    ulong sid = sim.ObjectId.Value, pid = pool.ObjectId.Value;
                    long now = SimClock.CurrentTicks;
                    if (!sPfm.Live(sid, pid, now)) return r;
                    int t = sPfm.Test(sid, pid, true, PfmExempt(sim), now);
                    if (t == PoolFailMemoCore.kPassExempt) { c[4608]++; return r; }
                    if (t != PoolFailMemoCore.kRefuse) return r;
                    c[4603]++;
                    PfmLog("refused " + sim.FullName + ", pool 0x" + pid.ToString("X") + " (autonomous)");
                    return InteractionTestResult.GenericFail;
                }
                catch (Exception ex) { c[4609]++; PfmLog("error " + ex.GetType().Name); return r; }
            }
            public InteractionTestResult TestOrig(ref InteractionInstanceParameters parameters, ref GreyedOutTooltipCallback greyedOutTooltipCallback)
            { return InteractionTestResult.Pass; } // receives GetInPool.Definition.Test(ref, ref)'s original body
        }
    }
}
