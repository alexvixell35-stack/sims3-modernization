using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Routing;

namespace Ts3Fixes
{
    // exitcbsweep (default on; household, selected and unknown-scope Sims only lose the callbacks of killed route actions, docs/289; runs in errortrapsims' hourly alarm under its own switch; not save data): every route action
    // that gets a ScriptAdaptor (ScriptAdaptor.Init, from FollowRouteAction.CreateAdaptors, e.g. in
    // VehicleFollowSubPathRouteAction.PerformAction) adds adaptor.RouteExitReasonsAddedCallback to Sim.OnExitReasonsAdded
    // of the adaptor's associated Sim. The only removal is ScriptAdaptor.Kill, reached through FollowRouteAction.Kill, which
    // returns at once when mKillType != 0. It is skipped when (1) CreateAdaptors runs again on the same action and
    // overwrites mScriptAdaptor with a new adaptor, the old one never killed (PerformAction run twice, ReplanWhenFinished),
    // (2) PerformRoute sets mKillType = 5 itself because RouteStart failed, so a later Kill returns early, (3) the action is
    // killed before PerformAction and PerformAction then runs CreateAdaptors anyway. The delegate holds adaptor ->
    // mRouteAction -> the route's Sims, so a stale one on a long-lived Sim pins whatever Sim that route named, and every
    // AddExitReason runs it (it may even consume exit reason 0x400000 and ForceReplan a dead route before the live one sees it).
    // The pass removes, from each Sim's list, the delegates whose target adaptor is no longer its route action's current
    // ScriptAdaptor, or whose route action has a kill type set. Both mean the game's own Kill would have removed it or is
    // never going to. A live route's delegate stays. Counters: [2120] stale delegates removed, [2121] longest delegate list
    // seen on one Sim before the pass (a growing value with the switch off is the leak).
    public static partial class Fixes
    {
        static FieldInfo sXcbEvent, sXcbRoute;  // Sim.OnExitReasonsAdded (event backing field), ScriptAdaptor.mRouteAction
        static bool sXcbTried, sXcbThrew;

        static void ExitCbSweepPass()
        {
            try
            {
                if (!ShortcutOn("exitcbsweep")) return;
                if (!sXcbTried)
                {
                    sXcbTried = true;
                    FieldInfo e = typeof(Sim).GetField("OnExitReasonsAdded", kInstAll);
                    FieldInfo r = typeof(ScriptAdaptor).GetField("mRouteAction", kInstAll);
                    bool ok = e != null && r != null && typeof(Delegate).IsAssignableFrom(e.FieldType);
                    sXcbEvent = ok ? e : null;
                    sXcbRoute = ok ? r : null;
                    AppliedLog().Add("exitcbsweep: " + (ok ? "ok" : "Sim or ScriptAdaptor differs from the known build, off"));
                }
                if (sXcbEvent == null) return;
                int[] c = PatchCounters();
                int removed = 0, longest = 0;
                foreach (Sim s in new List<Sim>(Sims3.Gameplay.Queries.GetObjects<Sim>()))
                {
                    if (s == null || s.HasBeenDestroyed) continue;
                    try { removed += ExitCbSweepSim(s, IsPlayerSim(s), ref longest); }  // player (PlayerSims.cs): killed actions only
                    catch (Exception e) { PassThrewOnce(ref sXcbThrew, "exitcbsweep", e); }  // one bad Sim skips only itself
                }
                c[2120] += removed;
                if (longest > c[2121]) c[2121] = longest;
            }
            catch (Exception e)
            {
                PassThrewOnce(ref sXcbThrew, "exitcbsweep", e);
            }
        }

        // One Sim's OnExitReasonsAdded: removes the callbacks of route adaptors that no longer belong to a live route
        // (HhAuditCore.XcbRemove: a household-scope Sim keeps a replaced adaptor's callback on a live action).
        static int ExitCbSweepSim(Sim s, bool player, ref int longest)
        {
            Delegate head = sXcbEvent.GetValue(s) as Delegate;
            if (head == null) return 0;
            Delegate[] list = head.GetInvocationList();
            if (list.Length > longest) longest = list.Length;
            int removed = 0;
            for (int i = 0; i < list.Length; i++)
            {
                ScriptAdaptor a = list[i].Target as ScriptAdaptor;
                FollowRouteAction ra = a == null ? null : sXcbRoute.GetValue(a) as FollowRouteAction;
                if (ra == null) continue;
                if (!HhAuditCore.XcbRemove(object.ReferenceEquals(ra.ScriptAdaptor, a), (int)ra.RouteKillType, player)) continue;  // a live route's own
                s.OnExitReasonsAdded -= (Sim.ExitReasonsAddedCallback)list[i];
                removed++;
            }
            return removed;
        }
    }
}
