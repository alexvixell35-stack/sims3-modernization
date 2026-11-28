using System;
using System.Collections.Generic;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects.Plumbing;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // cleandefer / cleandeferhh / cleandeferlog (docs/686 §45, default off; rules in CleanDeferCore.cs).
    //
    // [verified, decompiled Toilet.Clean; household wait census s691 / s693] Cleaning the toilet is an ordinary use of the
    // toilet: a Sim that picks "Clean" takes its turn (or stands in the toilet's line) while other Sims wait to use it;
    // in the 8-Sim, one-bathroom house the toilet line costs 27 sim-min per Sim-day.
    //
    // Fix: when a Sim's own pick to clean a toilet starts (Toilet.Clean.Run, before its walk) and other Sims wait for that
    // toilet, the clean ends at once, quietly (no walk, no shrug); the Sim's autonomy picks something else and comes back
    // to the cleaning later. Waiting = in the toilet's line, the toilet as the target of a queued action, a claim or hold
    // on it in nextplan's table (HhHolder), or a household Sim at home whose bladder is at bathroomwait's level. An
    // ordered clean (the player's click, a maid's job push) is never deferred. A toilet is not deferred for more than
    // kCapMinutes in a row. This is ordering only: no autonomy weight, need or duration is changed.
    // Scope: cleandefer = Sims the player does not control, cleandeferhh = household and selected Sims.
    // Off = compare mode (would). Counters 5100-5109. Check: CleanDeferCheck.
    public static partial class Fixes
    {
        const int kCdRuns = 5100, kCdDeferred = 5101, kCdWould = 5102, kCdOrdered = 5103, kCdCap = 5104, kCdErrors = 5105, kCdLines = 5106, kCdNoWaiters = 5107;
        const float kCdBladder = -60f;  // EA's urgent bladder level

        static readonly Dictionary<ulong, long> sCdFirst = new Dictionary<ulong, long>();  // by toilet: tick of the first deferral in a row
        static readonly LineCapCore sCdCap = new LineCapCore(20, 60000, 300);
        static string sCdLast = "", sCdLastError = "";

        static void ApplyCleanDefer()
        {
            Patch(typeof(Toilet.Clean), "Run", Type.EmptyTypes, typeof(CdHook), "RunHook", "RunOrig");
            // the switches of this hook, named here for the live-path map (CheckLiveMap.cs reads the Apply section)
            if (ShortcutOn("cleandefer") || ShortcutOn("cleandeferhh") || ShortcutOn("cleandeferlog")) AppliedLog().Add("cleandefer: switched on at start");
        }

        public abstract class CdHook : Toilet.Clean
        {
            public bool RunHook()
            {
                if (CdDefer(this)) return false;
                return RunOrig();
            }
            public bool RunOrig() { return false; }
        }

        // other Sims waiting for the toilet (the cleaner itself never counts)
        static int CdWaiters(Toilet t, Sim cleaner, out string who)
        {
            who = "";
            int n = 0;
            ulong tid = t.ObjectId.Value;
            SimQueue line = t.SimLine;
            List<Sim> inLine = line == null ? null : line.SimsInQueue;  // null when the line is empty
            if (inLine != null) foreach (Sim s in inLine) if (s != null && s != cleaner) { n++; who = s.FullName + " (in line)"; }
            ulong h = HhHolder(tid);
            if (h != 0 && h != cleaner.ObjectId.Value && HhSim(h) != null) { n++; who = HhSim(h).FullName + " (claimed)"; }
            if (t.LotCurrent == null) return n;
            foreach (Sim s in t.LotCurrent.GetSims())
            {
                if (s == null || s == cleaner || s.HasBeenDestroyed || s.InteractionQueue == null || !LswHuman(s)) continue;
                bool queued = false;
                foreach (InteractionInstance ii in s.InteractionQueue.InteractionList)
                    if (ii != null && object.ReferenceEquals(ii.Target, t) && !(ii is Toilet.Clean)) { queued = true; break; }
                if (queued) { n++; who = s.FullName + " (queued)"; continue; }
                if (s.Household != null && s.Household.LotHome == t.LotCurrent && s.Motives != null && s.Motives.GetValue(CommodityKind.Bladder) <= kCdBladder && !t.IsActorUsingMe(s))
                { n++; who = s.FullName + " (bladder " + s.Motives.GetValue(CommodityKind.Bladder).ToString("0") + ")"; }
            }
            return n;
        }

        static bool CdDefer(Toilet.Clean ia)
        {
            try
            {
                Sim a = ia.Actor;
                Toilet t = ia.Target;
                if (a == null || t == null) return false;
                int[] c = PatchCounters();
                c[kCdRuns]++;
                string who;
                int waiters = CdWaiters(t, a, out who);
                ulong tid = t.ObjectId.Value;
                long now = SimClock.CurrentTicks, first;
                float deferred = sCdFirst.TryGetValue(tid, out first) ? (now - first) / (float)HhTicks(1f) : 0f;
                int scope = HhSharedCore.Scope(true, LswHuman(a), IsPlayerSim(a), ShortcutOn("cleandefer"), ShortcutOn("cleandeferhh"));
                int d = CleanDeferCore.Decide(HhDirected(ia), waiters, deferred, scope);
                string head = BuWho(a) + " " + a.FullName + " -> clean " + t.GetType().Name + " 0x" + tid.ToString("X") + ", " + waiters + " waiting (" + who + ")";
                if (d == CleanDeferCore.kDefer)
                {
                    if (!sCdFirst.ContainsKey(tid)) { if (sCdFirst.Count >= 64) sCdFirst.Clear(); sCdFirst[tid] = now; }
                    c[kCdDeferred]++;
                    CdLine(head + ": the clean waits");
                    return true;
                }
                sCdFirst.Remove(tid);  // cleaned (or nobody waits): the next deferral starts a new row
                if (d == CleanDeferCore.kClean) c[kCdNoWaiters]++;
                else if (d == CleanDeferCore.kWould) { c[kCdWould]++; CdLine(head + ": would wait"); }
                else if (d == CleanDeferCore.kOrdered) { c[kCdOrdered]++; CdLine(head + ": ordered, cleans now"); }
                else if (d == CleanDeferCore.kCap) { c[kCdCap]++; CdLine(head + ": deferred " + deferred.ToString("0") + " sim-min, cleans now"); }
                return false;
            }
            catch (ResetException) { throw; }
            catch (Exception e) { CdError(e); return false; }
        }

        static void CdLine(string text)
        {
            sCdLast = text;
            PatchCounters()[kCdLines]++;
            if (ShortcutOn("cleandeferlog") && sCdCap.Take(System.Environment.TickCount)) DiagLine("cleandefer: " + text);
        }

        static void CdError(Exception e)
        {
            int[] c = PatchCounters();
            c[kCdErrors]++;
            sCdLastError = "cleandefer: error: " + e.GetType().Name + ": " + e.Message;
            if (c[kCdErrors] <= 3) DiagLine(sCdLastError);
        }

        public static object CleanDeferCheck(string what)
        {
            try
            {
                int[] c = PatchCounters();
                if (what == "reset")
                {
                    for (int i = kCdRuns; i <= kCdNoWaiters; i++) c[i] = 0;
                    sCdLast = ""; sCdLastError = "";
                    return "cleandefer: reset";
                }
                if (what == "run")
                {
                    const int G = HhSharedCore.kGo;
                    bool fixedNow = CleanDeferCore.Decide(false, 1, 0f, G) == CleanDeferCore.kDefer;
                    bool controls = CleanDeferCore.Decide(false, 0, 0f, G) == CleanDeferCore.kClean && CleanDeferCore.Decide(true, 2, 0f, G) == CleanDeferCore.kOrdered
                        && CleanDeferCore.Decide(false, 1, CleanDeferCore.kCapMinutes, G) == CleanDeferCore.kCap && CleanDeferCore.Decide(false, 1, 0f, HhSharedCore.kWould) == CleanDeferCore.kWould
                        && CleanDeferCore.Decide(false, 1, 0f, HhSharedCore.kExempt) == CleanDeferCore.kExempt;
                    string live = CkLive("cleandefer");
                    return "cleandefer run: fixed " + (fixedNow && live.Length == 0 ? "yes" : "NO") + ", controls " + (controls ? "ok" : "FAILED") + (live.Length > 0 ? "; live path: " + live : "");
                }
                return "cleandefer " + (ShortcutOn("cleandefer") ? "on" : "off") + ", cleandeferhh " + (ShortcutOn("cleandeferhh") ? "on" : "off")
                    + ": toilet cleans started " + c[kCdRuns] + " (nobody waiting " + c[kCdNoWaiters] + "), deferred " + c[kCdDeferred] + ", would " + c[kCdWould]
                    + ", ordered: cleaned " + c[kCdOrdered] + ", deferred too long: cleaned " + c[kCdCap] + "; snaps 0, clips 0; errors " + c[kCdErrors]
                    + (sCdLastError.Length > 0 ? " (" + sCdLastError + ")" : "") + (sCdLast.Length > 0 ? "; last: " + sCdLast : "");
            }
            catch (ResetException) { throw; }
            catch (Exception e) { return "cleandefer check: " + e.GetType().Name + ": " + e.Message; }
        }
    }
}
