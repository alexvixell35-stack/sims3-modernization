namespace Ts3Fixes
{
    // retryscope (tuning kRetryScope, off by default). Every plan through UfPlan first tells ts3native the route's scope
    // (UfPlanBody: UserDiag op 11 unseen, 13 seen NPC, 10 household or unknown, docs/197 §7) and ends with op 12 (plan over).
    // The async job's START consumes the answer (KdScopeTake). boattrip's own sync plans that run after UfPlan returns
    // (BtAfter: BtStart / BtBoat call EA's PlanOrig / PlanFromPointOrig directly) therefore reach KdSyncCeilUs with no
    // answer: unknown scope = household, so they run exempt up to the KdSeenCeilMs ceiling (10 s). [verified in the logs of a
    // contributor build: an unseen Sim's own Plan cut by UnseenFallback at 236 ms, then BtStart's on-foot PlanOrig ran sync
    // 4960 ms "scope unknown"; another 3033 ms. Stacks: PlanOrig < BtStart < BtAfter < RouteBackoffHook.PlanHook.]
    // Fix: UfPlanBody remembers the op it sent for its route; right before each of those sync plans the same op is sent
    // again for the same route handle, and op 12 after BtAfter. So the retry gets exactly the scope its own plan had: an
    // unseen route gets the unseen sync cap, a household, selected, unknown-scope (op 10) or seen-NPC (op 13) route keeps the
    // ceiling it would have had as its own plan. Another route's plan in between (a callback) only overwrites native's single
    // answer slot, which leaves ours unknown = household: the safe side. Counters [4076] scope answers sent again.
    public static partial class Fixes
    {
        static uint sKdLastH, sKdRescopedH;  // UfPlanBody's last scope answer: route handle, op, interaction id; the route re-sent
        static int sKdLastOp;
        static ushort sKdLastIa;

        public partial class RouteBackoffHook
        {
            // right before a sync PlanOrig / PlanFromPointOrig of the boat code (BtAfter's callees)
            void KdRescope()
            {
                uint h = Handle;
                if (sKdLastH == 0 || sKdLastH != h || !ShortcutOn("retryscope")) return;
                DiagSend(sKdLastOp, h, sKdLastIa);
                sKdRescopedH = h;
                PatchCounters()[4076]++;
            }
        }

        // after BtAfter: the re-sent answer is over (as UfPlan's own op 12)
        static void KdRescopeEnd()
        {
            if (sKdRescopedH == 0) return;
            DiagSend(12, sKdRescopedH);
            sKdRescopedH = 0;
        }
    }
}
