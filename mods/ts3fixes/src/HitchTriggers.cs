using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Agent triggers for the script-hitch loop repros (docs/169 §script causes): fire a real path now, in the game's own
    // context, so one load sees many firings. Test entry points only; no switch, no hook.
    public static partial class Fixes
    {
        // The 3 AM stuck check without waiting for 3:00: arms LotManager.RunStuckCheckHelper for every actor exactly as the
        // venue cleanup's RunStuckCheckHelper.Init does (helper built with the Sim's id, its position now and `attempt`, its
        // Execute on an AlarmManager.Global NeverPersisted alarm with the Sim as owner), but due in 1 sim-minute instead of
        // 20-60 (spreadMinutes 0: every check in one alarm tick, the worst case of coinciding alarms; > 0: each Sim at
        // 1 + random(spreadMinutes), as the game spreads them). Execute -> RunStuckCheck goes through our hook, so the
        // stuckcheckbusy / stuckcheckasync / stuckchecknear switches pick the path. attempt 3 = the final round (all points
        // fail -> the game's reset, no re-armed alarm left behind); 1 = the game's first round.
        public static string StuckCheckNow(int spreadMinutes, int attempt)
        {
            Type helper = typeof(LotManager).GetNestedType("RunStuckCheckHelper", BindingFlags.NonPublic);
            ConstructorInfo ctor = helper == null ? null : helper.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
                new Type[] { typeof(ulong), typeof(Vector3), typeof(uint) }, null);
            MethodInfo exec = helper == null ? null : helper.GetMethod("Execute", BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            if (ctor == null || exec == null) return "RunStuckCheckHelper(ulong, Vector3, uint) or Execute() not found";
            int n = 0;
            foreach (Sim sim in new List<Sim>(LotManager.Actors))
            {
                if (sim == null || sim.HasBeenDestroyed || sim.SimDescription == null) continue;
                object h = ctor.Invoke(new object[] { sim.SimDescription.SimDescriptionId, sim.Position, (uint)Math.Max(1, attempt) });
                float at = 1f + (spreadMinutes > 0 ? RandomUtil.GetFloat(0f, spreadMinutes) : 0f);
                AlarmManager.Global.AddAlarm(at, TimeUnit.Minutes, (AlarmTimerCallback)Fixes.Bind(typeof(AlarmTimerCallback), h, exec),
                    "Ts3Fixes.StuckCheckNow", AlarmType.NeverPersisted, sim);
                n++;
            }
            return n + " stuck checks armed (attempt " + attempt + ", " + (spreadMinutes > 0 ? "1-" + (1 + spreadMinutes) : "1") + " sim-minutes)";
        }
    }
}
