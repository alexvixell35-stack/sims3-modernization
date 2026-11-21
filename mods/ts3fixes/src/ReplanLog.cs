using System;
using System.Reflection;
using System.Diagnostics;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // replanlog (docs/261 §6 L1, logging only, no behaviour change): who calls Route.Replan in the NPC mid-route bursts. On only
    // with ts3native [RouteAsync] ReplanLog=1 (GetOption2 q 10, asked once a session); off = one int compare per Replan.
    // Every own Replan (RouteBackoff.cs ReplanHook -> RouteSrcBegin / RflPlan) counts its ms per scope. A "replanlog:" line for
    // a call inside a burst or of 10 ms or more: ms, result, Sim, species, scope (household / selected / seenNpc / unseen /
    // unknown, from UfWhy), interaction and target, the managed caller (3 frames above Route.Replan), lot and goal lot, distance
    // to the goal, route length, this Sim's Replans in the last 1 s, and "would": the checks an own kind-1 START meets now in
    // GetOptionHook / AsyncStartBody order (notOwn = C# would not send it, KindsOff, asyncOff, notHooked, LOS, NoYield, M1,
    // Active; "start" = none), evaluated before the call without acting. "replanlog burst start / end" lines: more than 3 Replans
    // within 1 s (ReplanLogCore.ReplanBurst), with calls, ms, scope split and top Sims / interactions.
    // Cap: 60 call lines a real minute, 2000 a session; burst lines 400 a session. Counters [969] Replans seen, [970] their ms,
    // [971-975] calls per scope, [976-980] ms per scope, [981] bursts, [982] call lines, [983] lines dropped by a cap,
    // [984] calls whose checks all passed but KindsOff (would start with Kinds bit 0).
    public static partial class Fixes
    {
        const int kRlBase = 969, kRlLinesPerMin = 60, kRlLinesMax = 2000, kRlBurstLinesMax = 400, kRlSlowMs = 10;
        static int sRlOn = -1;  // -1 = not asked yet
        static int sRlMinute = -1, sRlInMinute, sRlLines, sRlBurstLines;
        static readonly ReplanBurst sRlBurst = new ReplanBurst();
        struct RlCall { public ScriptCore.Route Route; public string Name, Ia, Caller, Lot, Would; public int Why, Scope; public bool Start; public Vector3 From; public uint T0; }
        static RlCall sRl;

        static bool RlOn(ScriptCore.Route r)
        {
            if (sRlOn < 0) { try { sRlOn = HcWould(r, 10) ? 1 : 0; } catch (Exception) { sRlOn = 0; } }
            return sRlOn == 1;
        }

        // RouteSrcBegin, kind 1, before the plan
        static void RlBegin(ScriptCore.Route r)
        {
            sRl.Route = null;
            if (sRlOn == 0 || !RlOn(r)) return;
            try
            {
                RlCall c = default(RlCall);
                IScriptProxy f = r.Follower;
                Sim sim = f != null ? f.Target as Sim : null;
                c.Why = sim != null ? SafeUfWhy(sim) : -2;
                c.Scope = ReplanLogCore.Scope(c.Why, sim != null && sim == Sim.ActiveActor);
                if (sim != null && sim.SimDescription != null)
                {
                    c.Name = sim.FullName + " 0x" + sim.SimDescription.SimDescriptionId.ToString("X") + " " + sim.SimDescription.Species;
                    InteractionInstance ia = sim.CurrentInteraction;
                    c.Ia = ia != null ? ia.GetType().Name + (ia.Target != null ? ">" + ia.Target.GetType().Name : "") : "none";
                    Lot lot = sim.LotCurrent;
                    c.Lot = lot != null ? lot.LotId.ToString("X") + " " + lot.Name : "0";
                    c.From = sim.Position;
                }
                else
                {
                    c.Name = HcName(f != null ? f.Target : null);
                    c.Ia = "none";
                    c.Lot = "0";
                }
                c.Caller = "";
                int got = 0;
                for (int i = 1; i < 32 && got < 3; i++)
                {
                    MethodBase m = new StackFrame(i, false).GetMethod();
                    if (m == null) break;
                    string ft = m.DeclaringType != null ? m.DeclaringType.FullName : null;
                    if (ft == "Ts3Fixes.Fixes" || RouteSrcCore.IsPassThrough(ft, m.Name)) continue;
                    c.Caller += (got++ > 0 ? "<" : "") + m.DeclaringType.Name + "." + m.Name;
                }
                // ts3native: the own kind-1 START's checks in GetOptionHook / AsyncStartBody order, no side effect (ArWould)
                string w = "";
                bool own = sim != null && ShortcutOn("asyncreplan") && Simulator.CurrentTask == sim.ObjectId && Simulator.CheckYieldingContext(false);
                if (!own) w += ",notOwn";
                bool kinds = HcWould(r, 11);
                if (!kinds) w += ",KindsOff";
                bool rest = true;
                if (!HcWould(r, 1)) { w += ",asyncOff"; rest = false; }
                if (!HcWould(r, 12)) { w += ",notHooked"; rest = false; }
                if (HcWould(r, 13)) { w += ",LOS"; rest = false; }
                if (!HcWould(r, 5)) { w += ",NoYield"; rest = false; }
                if (!HcWould(r, 7)) { w += ",M1"; rest = false; }
                if (HcWould(r, 6)) { w += ",Active"; rest = false; }
                c.Start = own && rest;
                c.Would = w.Length > 0 ? w.Substring(1) : "start";
                c.Route = r;
                c.T0 = SrcNowUs();
                sRl = c;
            }
            catch (Exception) { sRl.Route = null; }
        }

        // RflPlan, kind 1, after the plan
        static void RlEnd(ScriptCore.Route r, RoutePlanResult res, bool async)
        {
            if (sRl.Route == null) return;
            RlCall c = sRl;
            sRl.Route = null;
            if (!ReferenceEquals(c.Route, r)) return;
            try
            {
                int ms = (int)(unchecked(SrcNowUs() - c.T0) / 1000);
                int now = System.Environment.TickCount;
                int[] k = PatchCounters();
                k[kRlBase]++;
                k[kRlBase + 1] += ms;
                k[kRlBase + 2 + c.Scope]++;
                k[kRlBase + 7 + c.Scope] += ms;
                if (c.Start) k[kRlBase + 15]++;
                ReplanBurst b = sRlBurst;
                if (b.EndIfQuiet(now)) RlBurstLine("replanlog burst end #" + b.Id + ": " + (unchecked(b.LastMs - b.StartMs) / 1000.0).ToString("0.0")
                    + " s, calls " + b.Calls + ", ms " + b.Ms + " (" + (b.Ms * 1000 / Math.Max(1000, unchecked(b.LastMs - b.StartMs))) + " ms/s), " + RlScopes(b)
                    + ", Sims " + b.Sims.Count + " top: " + ReplanLogCore.Top(b.Sims, 6) + ", interactions top: " + ReplanLogCore.Top(b.Ias, 4));
                string simKey = c.Name ?? "?";
                if (b.Add(now, ms, c.Scope, simKey, c.Ia))
                {
                    k[kRlBase + 12]++;
                    RlBurstLine("replanlog burst start #" + b.Id + ": " + b.Calls + " calls / " + b.Ms + " ms in the last 1 s, at " + now + " ms");
                }
                if (!b.Active && ms < kRlSlowMs) return;
                int minute = now / 60000;
                if (minute != sRlMinute) { sRlMinute = minute; sRlInMinute = 0; }
                if (sRlInMinute >= kRlLinesPerMin || sRlLines >= kRlLinesMax) { k[kRlBase + 14]++; return; }
                sRlInMinute++;
                sRlLines++;
                k[kRlBase + 13]++;
                Vector3 dest = r.GetApproximateOriginalDestination();
                Lot goal = LotManager.GetLotAtPoint(dest);
                DiagLine("replanlog: " + ms + " ms " + (async ? "async " : "sync ") + res.mType + ", " + (b.Active ? "burst #" + b.Id : "single")
                    + ", sim " + simKey + ", scope " + ReplanLogCore.kScope[c.Scope] + " why " + c.Why + ", would " + c.Would + ", simN1s " + b.InWindow(now, simKey)
                    + ", ia " + c.Ia + ", caller " + c.Caller + ", dist " + (dest - c.From).Length().ToString("0.0") + " route " + r.GetRouteLength().ToString("0.0")
                    + ", at " + now + ", goalLot " + (goal != null ? goal.LotId.ToString("X") : "0") + ", lot " + c.Lot);  // lot name last: a long line loses only its tail (470 chars)
            }
            catch (Exception) { }
        }

        static void RlBurstLine(string s)
        {
            if (sRlBurstLines >= kRlBurstLinesMax) { PatchCounters()[kRlBase + 14]++; return; }
            sRlBurstLines++;
            DiagLine(s);
        }

        static string RlScopes(ReplanBurst b)
        {
            string s = "scopes";
            for (int i = 0; i < 5; i++) s += " " + ReplanLogCore.kScope[i] + " " + b.ScopeCalls[i] + "/" + b.ScopeMs[i];
            return s;
        }
    }
}
