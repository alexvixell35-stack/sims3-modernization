using System;
using System.Collections.Generic;
using System.Text;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects.Appliances;
using Sims3.Gameplay.Objects.Plumbing;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Agent checks for repro fridge-busy-queue (user 11-24: "Sims route failing while queuing up a fridge interaction while
    // another sim is already using it or about to"). Check-only, no switch, changes nothing but the one queued action:
    //   "push <full name>":  queues EA's Fridge_Have on the nearest fridge of the Sim's lot as a player's click would
    //                        (UserDirected, not autonomous), from EA's own Fridge.GetCookRandomRecipeInteraction (a random
    //                        single-serving recipe); the repro's do_interaction could not pick a Fridge_Have menu entry.
    //                        When EA's test refuses, the answer names the reason (the greyed-out tooltip).
    //   "state <full name>": the state of the fridge nearest the Sim (FbDescribe), one line in the log and the answer.
    //   "watch <full name>": logs that state each time it changes (every 3 ticks, with the sim-minute), up to kFbWatchMax
    //                        lines; a new watch ends the last one. The 11-24 run's per-5-s snapshots of every residential
    //                        fridge were cut off before the household's own and too coarse to see who blocks the slot.
    // Only EA's own definition and instance go into the queue (a save keeps nothing of ours). The watch is static state on
    // a Simulator task (gone with the world), reads only.
    public static partial class Fixes
    {
        public static string FridgeBusyCheck(string what)
        {
            string[] a = (what ?? "").Split(new char[] { ' ' }, 2);
            string r;
            try
            {
                if (a[0] == "line") return FglResult();
                if (a.Length < 2) return "fridgebusy <push|state|watch> <full name> | line";
                if (a[0] == "push") r = FbPush(a[1]);
                else if (a[0] == "state" || a[0] == "watch")
                {
                    Sim s = FbSim(a[1]);
                    Fridge f = s == null ? null : FbFridge(s);
                    if (f == null) r = s == null ? "no Sim " + a[1] : "no fridge on " + a[1] + "'s lot";
                    else if (a[0] == "state") r = FbDescribe(f);
                    else
                    {
                        int gen = ++sFbWatchGen;
                        Simulator.AddObject(new LoopTask(delegate { FbWatchLoop(f, gen); }));
                        r = "watching " + f.GetType().Name + ": " + FbDescribe(f);
                    }
                }
                else return "fridgebusy <push|state|watch> <full name>";
            }
            catch (ResetException) { throw; }
            catch (Exception e) { r = "error " + e.GetType().Name + ": " + e.Message; }
            DiagLine("fridgebusy: " + what + ": " + r);
            return r;
        }

        static Fridge FbFridge(Sim s)
        {
            Fridge best = null;
            float bestD = float.MaxValue;
            if (s.LotCurrent == null) return null;
            foreach (Fridge f in s.LotCurrent.GetObjects<Fridge>())
            {
                if (f == null || f.HasBeenDestroyed) continue;
                float d = (f.Position - s.Position).LengthSqr();
                if (d < bestD) { bestD = d; best = f; }
            }
            return best;
        }

        static Sim FbSim(string name)
        {
            foreach (Sim x in Sims3.Gameplay.Queries.GetObjects<Sim>()) if (x.FullName == name) return x;
            return null;
        }

        static string FbPush(string name)
        {
            Sim s = FbSim(name);
            if (s == null) return "no Sim " + name;
            Fridge f = FbFridge(s);
            if (f == null) return "no fridge on " + name + "'s lot";
            InteractionDefinition def = f.GetCookRandomRecipeInteraction(s.LotCurrent, s, s, false);
            Fridge_Have.Definition fd = def as Fridge_Have.Definition;
            string recipe = fd != null && fd.ChosenRecipe != null ? fd.ChosenRecipe.Key : "?";
            InteractionPriority pr = new InteractionPriority(InteractionPriorityLevel.UserDirected);
            InteractionObjectPair iop = new InteractionObjectPair(def, f);
            InteractionInstanceParameters p = new InteractionInstanceParameters(iop, s, pr, false, true);
            GreyedOutTooltipCallback cb = null;
            if (!InteractionDefinitionUtilities.IsPass(def.Test(ref p, ref cb)))
            {
                string why = "?";
                try { if (cb != null) why = cb(); } catch (Exception) { }
                return "refused " + recipe + " (" + why + ")";
            }
            InteractionInstance ii = def.CreateInstance(f, s, pr, false, true);
            if (ii == null) return "no instance for " + recipe;
            if (!s.InteractionQueue.Add(ii)) return "queue refused " + recipe;
            return "queued " + recipe + " on " + f.GetType().Name + ", fridge in use " + f.InUse + ", line " + FbLine(f);
        }

        static string FbLine(Fridge f)
        {
            List<Sim> line = f.Line == null ? null : f.Line.SimsInQueue;
            if (line == null || line.Count == 0) return "empty";
            StringBuilder b = new StringBuilder();
            foreach (Sim o in line) b.Append(b.Length > 0 ? ", " : "").Append(o == null ? "?" : o.FirstName);
            return b.ToString();
        }

        // ---- fridgeline ------------------------------------------------------------------------------------------------
        // EA (decompiled): Fridge.RouteToOpen waits in Fridge.Line (a SimQueue of 3 Sims, 15 sim-min). WaitForTurn fails with
        // ObjectInUse (and TimedOut) plus PlayRouteFail (the route-fail balloon with the user's thumbnail and Sim.RouteFailure)
        // when the line is full, a higher-priority Sim evicts the last one, the 15 sim-min run out, or the walk to the wait spot
        // fails (11-24 repro prtest1124g: Ben's walk ended and his wait broke with a balloon). fridgeline makes such a soft fail
        // (no exit reason but ObjectInUse / TimedOut) quiet; a clicked action then idles kFglPauseMin where it stands and asks
        // the line again, up to kFglMaxMin from its first ask; an autonomous one just ends (autonomy picks something else).
        // Reasons from outside (a cancel, a higher-priority action) are never soft: EA's fail and balloon stay. Hooks: the
        // existing WaitForTurn (ShareLanes.cs LdsQueueHook) and PlayRouteFail (SleepQueue597b.cs SrfPlayHook) swaps. Nothing is
        // saved: an entry of the static sFglWaits lives inside one WaitForTurn call and the map is cleared at world load.
        const float kFglMaxMin = 45f, kFglPauseMin = 2f;
        const int kFglLogMax = 200;
        sealed class FglWait { public Fridge F; public float Deadline; public bool Soft, Filled; public int Tries; }
        static readonly Dictionary<Sim, FglWait> sFglWaits = new Dictionary<Sim, FglWait>();
        static int sFglQuiet, sFglRetries, sFglGot, sFglNoFridge, sFglAutoQuiet, sFglLeft, sFglErrors, sFglLog;

        // Pure: is this fail soft (only the line's own reasons) with time left?
        internal static bool FglSoft(ExitReason r, float now, float deadline)
        {
            return (r & (ExitReason.ObjectInUse | ExitReason.TimedOut)) != 0 && (r & ~(ExitReason.ObjectInUse | ExitReason.TimedOut)) == 0 && now < deadline;
        }

        static void FglBegin(InteractionInstance ii, SimQueue q)
        {
            try
            {
                Sim a = ii == null ? null : ii.InstanceActor;
                Fridge f = a == null ? null : ii.Target as Fridge;
                if (f == null || !object.ReferenceEquals(f.Line, q) || !ShortcutOn(IsPlayerSim(a) ? "fridgelinehh" : "fridgeline")) return;
                FglWait w = new FglWait();
                w.F = f;
                w.Deadline = SimClock.ElapsedTime(TimeUnit.Minutes) + kFglMaxMin;
                sFglWaits[a] = w;
            }
            catch (ResetException) { throw; }
            catch (Exception) { sFglErrors++; }
        }

        // PlayRouteFail of a Sim in a fridgeline wait: true = skip EA's balloon (the fail is soft)
        static bool FglQuiet(Sim sim)
        {
            FglWait w;
            if (sim == null || sFglWaits.Count == 0 || !sFglWaits.TryGetValue(sim, out w)) return false;
            try
            {
                if (!FglSoft(sim.ExitReason, SimClock.ElapsedTime(TimeUnit.Minutes), w.Deadline)) return false;
                w.Soft = true;
                sFglQuiet++;
                if (++sFglLog <= kFglLogMax)
                    DiagLine("fridgeline: " + sim.FullName + " " + ShlWho(sim) + ": quiet soft fail (" + sim.ExitReason + "), line " + FbLine(w.F)
                        + ", users " + w.F.ActorsUsingMe.Count + ", tries " + w.Tries);
                return true;
            }
            catch (ResetException) { throw; }
            catch (Exception) { sFglErrors++; return false; }
        }

        // after a quiet fail: true = ask the line again (a clicked action that idled kFglPauseMin with nothing new)
        static bool FglRetry(InteractionInstance ii, ExitReason exitReasons)
        {
            Sim a = ii == null ? null : ii.InstanceActor;
            FglWait w;
            if (a == null || !sFglWaits.TryGetValue(a, out w) || !w.Soft) return false;
            w.Soft = false;
            try
            {
                if (ii.Autonomous) { sFglAutoQuiet++; return false; }
                if (FglFill(ii, a)) { w.Filled = true; return false; }  // this fridge action ends quietly; WashHands, then a fresh one, are next in the queue
                a.RemoveExitReason(ExitReason.ObjectInUse | ExitReason.TimedOut);
                a.LoopIdle();
                if (a.WaitForExitReason(kFglPauseMin, exitReasons)) { a.AddExitReason(ExitReason.ObjectInUse); sFglLeft++; return false; }
                if (w.F.HasBeenDestroyed) { a.AddExitReason(ExitReason.ObjectInUse); return false; }
                w.Tries++;
                sFglRetries++;
                return true;
            }
            catch (ResetException) { throw; }
            catch (Exception) { sFglErrors++; return false; }
        }

        // fridgelinefill (stage 2, user 11-24: waiting Sims "washing their hands" / do something useful): instead of idling
        // where it stands, a clicked Sim whose fridge wait failed softly washes its hands at the nearest free sink (EA's
        // Sink.WashHands, the one EA queues after the toilet), then runs a fresh instance of the same fridge action (same
        // definition, target, priority: a fresh instance, since Fridge_Have.Cleanup nulls its ImpassableRegion). Both go in
        // with EA's InteractionQueue.AddNext (no duplicate check on that path), so the player sees them queued and can cancel
        // them; only EA types are queued. At most kFglFillMax fills per Sim within kFglFillWindowMin; then stage 1's idle
        // retry. No fill for a hydrophobic Sim or when no sink is free and passes EA's test.
        const int kFglFillMax = 2;
        const float kFglFillWindowMin = 120f;
        static readonly Dictionary<Sim, float[]> sFglFills = new Dictionary<Sim, float[]>();  // Sim -> {fills, first fill sim-min}
        static int sFglFilled, sFglGotAfterFill;

        static bool FglFill(InteractionInstance ii, Sim a)
        {
            if (!ShortcutOn("fridgelinefill") || a.InteractionQueue == null || a.LotCurrent == null || a.HasTrait(TraitNames.Hydrophobic)) return false;
            float now = SimClock.ElapsedTime(TimeUnit.Minutes);
            float[] fl;
            if (!sFglFills.TryGetValue(a, out fl) || now - fl[1] > kFglFillWindowMin) { fl = new float[] { 0f, now }; sFglFills[a] = fl; }
            if (fl[0] >= kFglFillMax) return false;
            InteractionPriority pr = ii.GetPriority();
            InteractionInstance wash = null;
            Sink sink = null;
            float best = float.MaxValue;
            foreach (Sink s in a.LotCurrent.GetObjects<Sink>())
            {
                if (s == null || s.HasBeenDestroyed || s.InUse) continue;
                float d = (s.Position - a.Position).LengthSqr() + (s.Level != a.Level ? 400f : 0f);  // another floor counts as 20 m
                if (d >= best) continue;
                InteractionInstanceParameters p = new InteractionInstanceParameters(new InteractionObjectPair(Sink.WashHands.Singleton, s), a, pr, false, true);
                GreyedOutTooltipCallback cb = null;
                if (!InteractionDefinitionUtilities.IsPass(Sink.WashHands.Singleton.Test(ref p, ref cb))) continue;
                best = d;
                sink = s;
            }
            if (sink == null) return false;
            InteractionInstance fresh = ii.InteractionDefinition.CreateInstance(ii.Target, a, pr, false, ii.CancellableByPlayer);
            wash = Sink.WashHands.Singleton.CreateInstance(sink, a, pr, false, true);
            if (fresh == null || wash == null || !a.InteractionQueue.AddNext(fresh)) return false;
            a.InteractionQueue.AddNext(wash);  // ahead of the fresh one; if refused, the fresh one alone is a plain retry
            fl[0]++;
            sFglFilled++;
            if (++sFglLog <= kFglLogMax)
                DiagLine("fridgeline: " + a.FullName + " " + ShlWho(a) + ": washes hands at " + sink.GetType().Name + " " + Math.Sqrt(best).ToString("0.0")
                    + " m, then " + ii.GetType().Name + " again (fill " + fl[0] + ")");
            return true;
        }

        // every route-fail balloon EA still plays at a fridge's line (switch on or off: the A/B's count)
        static int sFglBalloons;
        static void FglBalloon(IGameObject target, Sim sim)
        {
            try
            {
                if (!(target is Fridge) || sim == null) return;
                sFglBalloons++;
                if (++sFglLog <= kFglLogMax) DiagLine("fridgeline: " + sim.FullName + " " + ShlWho(sim) + ": balloon (" + sim.ExitReason + "), line " + FbLine((Fridge)target));
            }
            catch (ResetException) { throw; }
            catch (Exception) { sFglErrors++; }
        }

        static void FglEnd(InteractionInstance ii, bool got)
        {
            Sim a = ii == null ? null : ii.InstanceActor;
            FglWait w;
            if (a == null || sFglWaits.Count == 0 || !sFglWaits.TryGetValue(a, out w)) return;
            sFglWaits.Remove(a);
            if (w.Filled) return;  // handed to fridgelinefill (WashHands, then a fresh instance with its own wait)
            if (got && sFglFills.Remove(a))  // the fresh instance after a fill got it; removing also resets the Sim's fill cap
            {
                sFglGotAfterFill++;
                if (++sFglLog <= kFglLogMax) DiagLine("fridgeline: " + a.FullName + " " + ShlWho(a) + ": got the fridge after washing hands");
            }
            if (w.Tries == 0) return;
            if (!got)
            {
                sFglNoFridge++;  // retried, then the action ended without the fridge (cancel, exit reason, replaced, fridge gone)
                if (++sFglLog <= kFglLogMax) DiagLine("fridgeline: " + a.FullName + " " + ShlWho(a) + ": ended without the fridge after " + w.Tries + " quiet retries (" + a.ExitReason + ")");
                return;
            }
            sFglGot++;
            if (++sFglLog <= kFglLogMax) DiagLine("fridgeline: " + a.FullName + " " + ShlWho(a) + ": got the fridge after " + w.Tries + " quiet retries");
        }

        static string FglResult()
        {
            float d = 100f;
            bool self = FglSoft(ExitReason.ObjectInUse, 0f, d) && FglSoft(ExitReason.ObjectInUse | ExitReason.TimedOut, 0f, d)
                && !FglSoft(ExitReason.ObjectInUse | ExitReason.UserCanceled, 0f, d) && !FglSoft(ExitReason.TimedOut | ExitReason.HigherPriorityNext, 0f, d)
                && !FglSoft(ExitReason.ObjectInUse, d, d) && !FglSoft(ExitReason.None, 0f, d);
            return "fridgeline " + (ShortcutOn("fridgeline") ? "on" : "off") + ", fridgelinehh " + (ShortcutOn("fridgelinehh") ? "on" : "off")
                + ": quiet soft fails " + sFglQuiet + ", retries " + sFglRetries + ", got the fridge after a retry " + sFglGot + ", ended without it after a retry " + sFglNoFridge
                + ", fridgelinefill " + (ShortcutOn("fridgelinefill") ? "on" : "off") + " washed hands first " + sFglFilled + ", got the fridge after washing " + sFglGotAfterFill
                + ", autonomous ended quietly " + sFglAutoQuiet + ", left during the pause " + sFglLeft + ", balloons played " + sFglBalloons + ", errors " + sFglErrors
                + ", open waits " + sFglWaits.Count + ", self-check " + (self ? "ok" : "FAILED");
        }

        const int kFbWatchMax = 800;
        static int sFbWatchGen, sFbWatchN;

        static void FbWatchLoop(Fridge f, int gen)
        {
            string last = null;
            while (gen == sFbWatchGen && sFbWatchN < kFbWatchMax && !f.HasBeenDestroyed)
            {
                Simulator.Sleep(3);
                string st;
                try { st = FbDescribe(f); }
                catch (ResetException) { throw; }
                catch (Exception e) { st = "error " + e.GetType().Name + ": " + e.Message; }
                if (st == last) continue;
                last = st;
                ++sFbWatchN;
                DiagLine("fridgebusy: watch " + SimClock.ElapsedTime(TimeUnit.Minutes).ToString("0.0") + ": " + st);
            }
        }

        // users, the line in order, Sims on the routing slot, and Sims with this fridge queued elsewhere; each Sim as
        // name(running interaction, exit reasons when set, metres from the slot)
        static string FbDescribe(Fridge f)
        {
            Vector3 slot = f.GetPositionOfSlot(Slot.RoutingSlot_0);
            List<Sim> line = f.Line == null ? null : f.Line.SimsInQueue;
            StringBuilder u = new StringBuilder(), l = new StringBuilder(), on = new StringBuilder(), q = new StringBuilder();
            foreach (Sim o in f.ActorsUsingMe) FbSimText(u, o, slot);
            if (line != null) foreach (Sim o in line) FbSimText(l, o, slot);
            foreach (Sim s in Sims3.Gameplay.Queries.GetObjects<Sim>())
            {
                if (s == null || s.LotCurrent != f.LotCurrent || s.InteractionQueue == null) continue;
                if ((s.Position - slot).LengthSqr() < 0.81f && !f.IsActorUsingMe(s)) FbSimText(on, s, slot);
                if (f.IsActorUsingMe(s) || (line != null && line.Contains(s))) continue;
                foreach (InteractionInstance i in s.InteractionQueue.InteractionList)
                    if (i != null && i.Target == f) { FbSimText(q, s, slot); break; }
            }
            return "users [" + u + "] line [" + l + "] on slot [" + on + "] queued [" + q + "]";
        }

        static void FbSimText(StringBuilder b, Sim s, Vector3 slot)
        {
            if (b.Length > 0) b.Append(", ");
            if (s == null) { b.Append("?"); return; }
            InteractionInstance cur = s.CurrentInteraction;
            b.Append(s.FirstName).Append('(').Append(cur == null ? "nothing" : cur.GetType().Name);
            if (s.ExitReason != ExitReason.None) b.Append(' ').Append(s.ExitReason.ToString());
            b.Append(' ').Append((s.Position - slot).Length().ToString("0.0")).Append(')');
        }
    }
}
