using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects.Decorations;
using Sims3.Gameplay.Situations;
using Sims3.Gameplay.Utilities;

namespace Ts3Fixes
{
    // weddingarchkeep (tuning kWeddingArchKeep, off by default; docs/546 §12). EA bug fix, every Sim; no Sim is moved and no
    // route is skipped.
    //
    // "Get Married" at a wedding arch (or a resort's NPC wedding) starts WeddingCeremony in its RouteToWeddingArch state: both
    // partners get RouteToWeddingArchSlot (route to the arch's slot 0 / 1, then idle there until both arrived). Its failure
    // callback, OnRouteFailed, ends the whole ceremony (Parent.Exit) when either partner's interaction fails, so a route
    // failure of one partner silently ends the wedding: no ring exchange, the couple stays engaged, the guests leave
    // [verified in the decompiled game code]. The same callback also serves a player cancel of either walk, where ending the
    // wedding is intended. EA's ceremony without an arch (CallOut, then GatherRound and ExchangeRings, the path EA takes when
    // the lot has no arch) needs no route to the arch.
    //
    // The fix tells the two apart in RouteToWeddingArchSlot.Run: Run returned false while the ceremony is still in
    // RouteToWeddingArch, the Sim never reached the arch (not in its use list) and carries no cancel exit reason (kEvrStop,
    // EventRouteRetry.cs) = the route failed. For that failure only, OnRouteFailed switches the ceremony to EA's no-arch path
    // instead of ending it: the partner's arch walk or idle is cancelled (EA's own CleanUp idiom) and the state becomes
    // CallOut. The replaced state's later failure callbacks (the partner's cancelled walk) are ignored, as they would end the
    // kept ceremony. The arch stays set so EA's final CleanUp still frees its use list. Cancels and every other path run EA's
    // code unchanged. Counter [2345] weddings kept, [2346] errors (registry fixes-2345). Harness check: WeddingArchKeepCheck
    // (EventChecks.cs).
    public static partial class Fixes
    {
        static readonly Type sWaSlotRoute = typeof(WeddingArch).GetNestedType("RouteToWeddingArchSlot", BindingFlags.Public | BindingFlags.NonPublic);

        // the Sim whose arch route just failed, and the tick it failed in: OnRouteFailed runs right after Run returns, in the
        // same tick (InteractionQueue's CleanupInteraction), so an older mark is stale
        static Sim sWaFailSim;
        static long sWaFailTick;
        // ceremony states this fix replaced: their failure callbacks must not end the kept ceremony (few weddings, short list)
        static readonly List<WeddingCeremony.RouteToWeddingArch> sWaReplaced = new List<WeddingCeremony.RouteToWeddingArch>();
        const int kWaReplacedMax = 8;

        static void ApplyWeddingArchKeep()
        {
            if (sWaSlotRoute == null) { AppliedLog().Add("WeddingArch.RouteToWeddingArchSlot: not found, weddingarchkeep skipped"); return; }
            Patch(sWaSlotRoute, "Run", typeof(WeddingArchRouteHook), "RunHook", "RunOrig");
            Patch(typeof(WeddingCeremony.RouteToWeddingArch), "OnRouteFailed", typeof(WeddingArchKeepHook), "OnRouteFailedHook", "OnRouteFailedOrig");
        }

        public class WeddingArchRouteHook
        {
            public bool RunHook()
            {
                bool r = RunOrig();
                if (!r) WaNoteRouteFail((InteractionInstance)(object)this);
                return r;
            }
            public bool RunOrig() { return false; } // receives WeddingArch.RouteToWeddingArchSlot.Run's original body
        }

        public class WeddingArchKeepHook
        {
            public void OnRouteFailedHook(Sim s, float f)
            {
                if (WaKeepWedding((WeddingCeremony.RouteToWeddingArch)(object)this, s)) return;
                OnRouteFailedOrig(s, f);
            }
            public void OnRouteFailedOrig(Sim s, float f) { } // receives WeddingCeremony.RouteToWeddingArch.OnRouteFailed's original body
        }

        // After a failed RouteToWeddingArchSlot.Run: marks the Sim when the failure was the route (see the header).
        static void WaNoteRouteFail(InteractionInstance route)
        {
            if (!ShortcutOn("weddingarchkeep")) return;
            try
            {
                Sim actor = route.InstanceActor;
                if (actor == null || route.Target == null || actor.HasExitReason(kEvrStop) || route.Target.IsActorUsingMe(actor)) return;
                WeddingCeremony c = actor.GetSituationOfType<WeddingCeremony>();
                if (c == null || c.SituationExited || !(c.Child is WeddingCeremony.RouteToWeddingArch)) return;
                sWaFailSim = actor;
                sWaFailTick = SimClock.CurrentTicks;
            }
            catch (Exception) { PatchCounters()[2346]++; }
        }

        // OnRouteFailed of state: true = handled here (EA's Parent.Exit skipped), false = run EA's body.
        static bool WaKeepWedding(WeddingCeremony.RouteToWeddingArch state, Sim s)
        {
            if (!ShortcutOn("weddingarchkeep")) return false;
            try
            {
                if (sWaReplaced.Contains(state)) return true;  // a callback of the state replaced below
                bool routeFailed = s != null && s == sWaFailSim && sWaFailTick == SimClock.CurrentTicks;
                sWaFailSim = null;
                WeddingCeremony c = state.Parent;
                if (!routeFailed || c == null || c.SituationExited || c.Child != state || c.WeddingArch == null) return false;
                Sim partner = s == c.RingGiver ? c.RingReceiver : c.RingGiver;
                if (partner == null || partner.HasBeenDestroyed || partner.InteractionQueue == null) return false;
                if (sWaReplaced.Count >= kWaReplacedMax) sWaReplaced.RemoveAt(0);
                sWaReplaced.Add(state);
                c.SetState(new WeddingCeremony.CallOut(c));  // EA's ceremony without an arch; RouteToWeddingArch.CleanUp runs first
                // the partner's walk to or idle at the arch ends as in EA's CleanUp (any definition of the type matches)
                partner.InteractionQueue.CancelAllInteractionsByType(c.WeddingArch.GetRouteToWeddingArchDefinition(true));
                PatchCounters()[2345]++;
                return true;
            }
            catch (Exception) { PatchCounters()[2346]++; return false; }
        }
    }
}
