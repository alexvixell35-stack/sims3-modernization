using System;
using Sims3.Gameplay;
using Sims3.Gameplay.Actors;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // docs/202 §3.4 C1-C4 (switch unseenfbsites, off): the ids of the Sims that pass the unseen rule (UfWhy == 0: live mode,
    // not the household, not on a seen lot, off screen, far from the camera and the household, not riding / carried /
    // carrying, not with a visible Sim) go to ts3native's [UnseenFallback] Sites=1 about every 0.5 s. Its four sites then
    // try a running route job's lock once (SiteWaitMs, default 0) on behalf of a listed Sim and skip that step with a harmless
    // answer. Channel: GetOption2 with optHi top byte 0x83 and a code in bits 48-55 (no RouteOption2 name): 7 BEGIN, 8 id low,
    // 9 id high (appends one id), 10 END with the count, 14 C4 log query (§5.6). Out of live mode the list is empty;
    // ts3native treats a list older than 3 s as none, so nothing acts when this loop stops. BEGIN refused (no site installed,
    // older ts3native, the game) ends the loop for this world load.
    // docs/202 §5.6 (cost): wall-clock cadence (Sleep sized from the tick rate, UnseenFallbackCore.SitesSleepTicks; a fixed
    // Sleep(5) woke every iteration at speed 3), only Sims that are routing or running an interaction (C1 / C2 owners walk,
    // C3 / C4 owners run their own interaction), and a per-Sim verdict cache (UfWhy again only after a 1 m move, a 1 m camera
    // move or 2 s).
    // docs/202 §3.4.2 (unseenfbfinish, UnseenFallbackFinish.cs): routing Sims that can have their walk finished are flagged after
    // their id (code 15); after END the claimed holds are checked, finished or given up (codes 16-20).
    // docs/202 §5.8 (Rodney's per-Sim loop rule): the list is built over several ticks from one snapshot, at most
    // UnseenFallbackCore.kSitesPerTick Sims per wake (round-robin cursor, UfSitesPass), cheap early-outs first, UfWhy again only
    // for Sims that moved, started routing or changed interaction (or after 2 s); the list goes out when the pass ends.
    // Counters: [323] lists pushed, [324] ids pushed (sum), [325] pushes refused (BEGIN refused or a field refused).
    public static partial class Fixes
    {
        const int kUfSitesMax = 256;       // ts3native's list size
        const int kUfC4LogMax = 20;        // C4 "not free" answers named in ts3native.log per session (§5.6)
        static int sUfSitesGen, sUfC4Logged;

        internal static void StartUfSitesPush()
        {
            int gen = ++sUfSitesGen;  // a loop left from an earlier load ends at its next wake-up
            sUfSiteCache.Clear();
            UfFinishReset();  // docs/202 §3.4.2: holds of an earlier load are gone
            Simulator.AddObject(new LoopTask(delegate { UfSitesLoop(gen); }));
        }

        static void UfSitesLoop(int gen)
        {
            int last = unchecked(System.Environment.TickCount - UnseenFallbackCore.kPushMs), wake = System.Environment.TickCount;
            long ticks = Sims3.Gameplay.Utilities.SimClock.CurrentTicks;
            Sim[] pass = null;
            UfSitesPass cursor = new UfSitesPass();
            System.Collections.Generic.List<Sim> sims = new System.Collections.Generic.List<Sim>();
            while (gen == sUfSitesGen)
            {
                int now = System.Environment.TickCount;
                long t = Sims3.Gameplay.Utilities.SimClock.CurrentTicks;
                try
                {
                    if (pass == null && unchecked(now - last) >= UnseenFallbackCore.kPushMs)
                    {
                        last = now;
                        if (ShortcutOn("unseenfbsites"))
                        {
                            pass = UfSitesBegin();
                            sims.Clear();
                            cursor.Begin(pass.Length);
                        }
                    }
                    if (pass != null)
                    {
                        // docs/202 §5.8: at most kSitesPerTick Sims this wake; the list goes out once the pass is done
                        int from, n = cursor.Slice(out from);
                        for (int i = from; i < from + n; i++) UfSitesEval(pass[i], now, sims);
                        if (cursor.Done)
                        {
                            pass = null;
                            if (!UfSitesSendList(sims)) return;
                        }
                    }
                }
                catch (Exception) { return; }
                // leaving live mode pauses the clock (LiveModeState.Shutdown), so a tick sleep would never wake and the last live
                // list would stay fresh in Build/Buy for ts3native's 3 s age: yield per frame there so the empty list goes out
                uint sleep = pass != null || !UfSitesLive() ? 0u : UnseenFallbackCore.SitesSleepTicks(t - ticks, unchecked(now - wake), unchecked(last + UnseenFallbackCore.kPushMs - now));
                ticks = t;
                wake = now;
                Simulator.Sleep(sleep);  // 0 = next tick while a pass runs
            }
        }

        sealed class UfSiteSeen { public Vector3 Pos; public int Why, At; public object Inst; public bool Routing; }
        static readonly System.Collections.Generic.Dictionary<ulong, UfSiteSeen> sUfSiteCache = new System.Collections.Generic.Dictionary<ulong, UfSiteSeen>();
        static Vector3 sUfSiteCam = Vector3.Invalid;

        static int UfSiteWhy(Sim s, ulong id, int now)
        {
            Vector3 p = s.Position;
            object inst = s.CurrentInteraction;
            bool routing = s.IsRouting;
            UfSiteSeen e;
            if (sUfSiteCache.TryGetValue(id, out e)
                && !UnseenFallbackCore.SitesRecheck((p - e.Pos).LengthSqr(), unchecked(now - e.At), !ReferenceEquals(inst, e.Inst) || (routing && !e.Routing)))
                return e.Why;
            if (e == null) sUfSiteCache[id] = e = new UfSiteSeen();
            e.Pos = p;
            e.At = now;
            e.Inst = inst;
            e.Routing = routing;
            return e.Why = UfWhy(s);
        }

        static bool UfSitesSend(int code, uint value)
        {
            return sYieldProbe.GetOption2((Sims3.SimIFace.Route.RouteOption2)(0x8300000000000000UL | (ulong)(uint)code << 48 | value));
        }

        static bool UfSitesLive() { return GameStates.IsLiveState && !GameStates.IsTravelling && !GameStates.IsWorldShuttingDown; }

        static readonly Sim[] sUfNoSims = new Sim[0];  // shared: an empty array cannot change

        // one snapshot per push; empty out of live mode (the list then goes out empty)
        static Sim[] UfSitesBegin()
        {
            if (!UfSitesLive()) { sUfSiteCache.Clear(); return sUfNoSims; }
            Vector3 cam = CameraController.GetPosition();
            if (sUfSiteCam == Vector3.Invalid || (cam - sUfSiteCam).LengthSqr() >= 1f) { sUfSiteCam = cam; sUfSiteCache.Clear(); }
            if (sUfSiteCache.Count > 4 * kUfSitesMax) sUfSiteCache.Clear();  // shortcut: Sims gone since are dropped wholesale
            Sim[] all = Sims3.Gameplay.Queries.GetObjects<Sim>();
            return all ?? sUfNoSims;
        }

        static void UfSitesEval(Sim s, int now, System.Collections.Generic.List<Sim> sims)
        {
            if (sims.Count >= kUfSitesMax) return;
            // cheap early-outs first: pets and horses never (as the plan marks); idle Sims reach no site (no route, no own interaction)
            if (s == null || s.HasBeenDestroyed || s.SimDescription == null || s.SimDescription.IsPet || (!s.IsRouting && s.CurrentInteraction == null)) return;
            ulong id = s.ObjectId.Value;
            if (UfSiteWhy(s, id, now) == 0) sims.Add(s);
        }

        // false = BEGIN refused: no C site this load
        static bool UfSitesSendList(System.Collections.Generic.List<Sim> sims)
        {
            int[] c = PatchCounters();
            if (!UfSitesSend(7, 0)) { c[325]++; return false; }
            uint n = 0;
            bool live = UfSitesLive();  // left live mode during the pass: send the empty list
            // docs/202 §3.4.2: flag walkers whose walk may be finished (UfFinishable), at most kUfFinishEvalMax per push
            // (code 16 answers 1 only when ts3native has FinishWalk installed)
            bool finishOn = live && ShortcutOn("unseenfbfinish") && UfSitesSend(16, 0);
            int finishBudget = finishOn ? kUfFinishEvalMax : 0;
            // a hold starts only for a Sim flagged in the last list, so last push's flagged Sims keep their place (else an unclaimed
            // hold could lose its flag to the budget and end at END)
            sUfFinishPrev.Clear();
            foreach (Sim f in sUfFinishFlagged) sUfFinishPrev[f.ObjectId.Value] = true;
            sUfFinishFlagged.Clear();
            if (live)
                foreach (Sim s in sims)
                {
                    if (s.HasBeenDestroyed) continue;  // the pass spans ticks
                    ulong id = s.ObjectId.Value;
                    // a refused field: the old list stays and goes stale, so nothing acts
                    if (!UfSitesSend(8, (uint)id) || !UfSitesSend(9, (uint)(id >> 32))) { c[325]++; return true; }
                    n++;
                    bool held = sUfFinish.ContainsKey(id) || sUfFinishPrev.ContainsKey(id);  // held or may be: re-checked every push
                    if (s.IsRouting && finishOn && (held || finishBudget > 0))
                    {
                        if (!held) finishBudget--;
                        if (UfFinishable(s) && UfSitesSend(15, 1)) { c[367]++; sUfFinishFlagged.Add(s); }
                    }
                }
            if (!UfSitesSend(10, n)) { c[325]++; return true; }
            c[323]++;
            c[324] += (int)n;
            if (ShortcutOn("unseenfbfinish") || sUfFinish.Count > 0) UfFinishStep();
            if (sUfC4Logged < kUfC4LogMax && UfSitesSend(14, 64)) UfC4Log();
            return true;
        }

        // docs/202 §5.6 (3): ts3native holds the id of the last Sim its C4 site answered "not free"; code 14 b (0-63) reads bit b,
        // 64 = any pending, 65 = clear. Names the Sim and its interaction in ts3native.log for the first kUfC4LogMax answers.
        static void UfC4Log()
        {
            ulong id = 0;
            for (int b = 0; b < 64; b++) if (UfSitesSend(14, (uint)b)) id |= 1UL << b;
            UfSitesSend(14, 65);
            sUfC4Logged++;
            Sim s = Sims3.Gameplay.Abstracts.GameObject.GetObject<Sim>(new ObjectGuid(id));
            Sims3.Gameplay.Interactions.InteractionInstance cur = s != null ? s.CurrentInteraction : null;
            UfGuardNote(id, "C4 not free" + (cur != null ? " in " + cur.GetType().Name : ""));
            DiagLine("ufc4: not free for " + (s != null ? s.FullName : "?") + " 0x" + id.ToString("X") + ", interaction "
                + (cur != null ? cur.GetType().FullName : "none") + (cur != null && cur.Target != null ? " on " + cur.Target.GetType().Name : ""));
        }
    }
}
