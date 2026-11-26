using System;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects.Counters;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // barorderlog (tuning kBarOrderLog, off by default; logging only; docs/578 §1). The bartender of a bar conversation
    // (BarAdvanced+MakeConversation) waits 17.2 sim-min on average at Committed, with no giveup, for a customer chat EA
    // times at 5-10 sim-min (OrderConversation.RunOrderBehavior: DoTimedLoop(GetInt(kMinConversationTime,
    // kMaxConversationTime))). The customer sets Committed only after RunOrderBehavior and the "Exit" animation.
    // This log times the customer side so the gap can be placed: a prefix/postfix on OrderConversation.RunOrderBehavior
    // writes
    //   "barorder: <customer> -> <bartender>, start, bartender sync <level>, customer queue <n>, day d hour h"
    //   "barorder: <customer> -> <bartender>, end <ok|fail> after N sim-min, exit reason <r>, day d hour h"
    // Read with the bartender's syncwait line (its Committed wait ends when the customer sets Committed) and the
    // customer's "Routed ok" line: Routed -> start = order animation, end -> bartender wait end = exit animation.
    // Capped at 300 lines per session. Counters: [3974] conversations timed, [3975] lines dropped by the cap.
    public static partial class Fixes
    {
        const int kBolMaxLines = 300;
        static int sBolLines;
        static readonly Type sBolOrderConv = typeof(BarAdvanced).GetNestedType("OrderConversation", BindingFlags.Public | BindingFlags.NonPublic);

        static void ApplyBarOrderLog()
        {
            if (sBolOrderConv == null) { AppliedLog().Add("BarAdvanced+OrderConversation: not found, barorderlog off"); return; }
            Patch(sBolOrderConv, "RunOrderBehavior", new Type[0], typeof(BarOrderLogHook), "OrderHook", "OrderOrig");
        }

        static void BolLine(string text)
        {
            if (sBolLines >= kBolMaxLines) { PatchCounters()[3975]++; return; }
            sBolLines++;
            HcLine(text);
        }

        static string BolWho(InteractionInstance ii)
        {
            Sim c = ii.InstanceActor;
            InteractionInstance link = ii.LinkedInteractionInstance;
            Sim b = link != null ? link.InstanceActor : null;
            return (c != null ? SimLabel(c) : "none") + " -> " + (b != null ? SimLabel(b) : "none");
        }

        public class BarOrderLogHook
        {
            public bool OrderHook()
            {
                if (!ShortcutOn("barorderlog")) return OrderOrig();
                InteractionInstance ii = (InteractionInstance)(object)this;
                long t0 = SimClock.CurrentTicks;
                try
                {
                    InteractionInstance link = ii.LinkedInteractionInstance;
                    Sim b = link != null ? link.InstanceActor : null;
                    Sim c = ii.InstanceActor;
                    int q = 0;
                    if (c != null && c.InteractionQueue != null) foreach (InteractionInstance x in c.InteractionQueue.InteractionList) if (x != null) q++;
                    BolLine("barorder: " + BolWho(ii) + ", start, bartender sync " + (b != null ? b.SynchronizationLevel.ToString() : "none")
                        + ", customer queue " + q + ", " + HcSimTime());
                }
                catch (Exception) { }
                bool r = false;
                try { r = OrderOrig(); return r; }
                finally
                {
                    try
                    {
                        PatchCounters()[3974]++;
                        float min = SimClock.ConvertFromTicks(SimClock.CurrentTicks - t0, TimeUnit.Minutes);
                        Sim c = ii.InstanceActor;
                        BolLine("barorder: " + BolWho(ii) + ", end " + (r ? "ok" : "fail") + " after " + min.ToString("0.0") + " sim-min, exit reason "
                            + (c != null ? c.ExitReason.ToString() : "none") + ", " + HcSimTime());
                    }
                    catch (Exception) { }
                }
            }
            public bool OrderOrig() { return false; } // receives OrderConversation.RunOrderBehavior's original body
        }
    }
}
