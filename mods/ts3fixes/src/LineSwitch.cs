using System;
using System.Collections.Generic;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects;
using Sims3.Gameplay.Objects.Electronics;
using Sims3.Gameplay.Objects.HobbiesSkills;
using Sims3.Gameplay.Objects.Plumbing;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // lineswitch (docs/686 §25, default off). A Sim waits in an object's line (toilet, sink, shower, bath) for up to the
    // object's timeout (15 sim-min and more) while a second object of the same kind on the lot stands free.
    //
    // [verified, decompiled SimQueue.WaitForTurn] EA has one substitution: on the first pass of the wait loop, with
    // WaitBehavior TryAnotherObjectIf(New)LineExists (toilets: DefaultAllowSubstitution; sinks, showers and baths do not
    // ask for it), TryReplacementInteraction asks autonomy for the best action of the same motive on the lot and pushes it
    // as a continuation when its object has no line (WasInUse = true, ExitReason.FailedToStart, the loop breaks). It never
    // looks again, so a twin that frees up later is not used: storms s533-s536 had 2-20 sim-min waits at the Bunch
    // house's ToiletCheap with its ToiletModerate on the same lot.
    // Fix: the loop's per-minute wait (SimQueue.WaitForExitReasonOnSim) is swapped. From kLineSwitchMinutes (2) of
    // waiting, every check looks for a free object of the same kind (Toilet, Sink, shower, Bathtub) on the same lot: not
    // the one waited for, standing, no user, no line, no Sim with it as the target of a running or queued action, the
    // same interaction's test passes, and a plan to it works; the same room first, then the same level, then the nearest.
    // Then EA's own substitution: the same interaction definition on that object pushed as a continuation, WasInUse,
    // FailedToStart, and the loop breaks into EA's tail (queue removal, exit reasons). The route-fail balloon of that tail
    // is skipped for this Sim on this tick (SrfPlayHook, as subroutefail does for EA's own substitution).
    // Scope: human Sims on an action they picked themselves (a player's order keeps its object); NPCs with lineswitch,
    // household and selected Sims also with lineswitchhh. Off = compare mode: counted, nothing changed. lineswitchlog
    // writes one line per case. Counters 4615-4619. Check: LineSwitchCheck. Swap: SimQueue.WaitForExitReasonOnSim
    // (stub); the line hook is ShareLanes.cs's LdsQueueHook.
    // docs/686 §37 (UnseenSkip.cs): the same rule for computers, laptops and bookshelves (lineswitchpc; household with
    // lineswitchhh) and for workout benches and treadmills (gymtwin, NPCs only: at the first check when the NPC was unseen as
    // its wait began, after kLineSwitchMinutes otherwise). Only the waiter moves; a running use or workout is never ended.
    // The slide line's hang-out cap (slidehang) is checked from the same per-minute hook.
    public static partial class Fixes
    {
        const int kLswSwitched = 4615, kLswWould = 4616, kLswKept = 4617, kLswPlans = 4618, kLswErrors = 4619;

        sealed class LswWait
        {
            public object Queue;
            public InteractionInstance Ia;
            public GameObject Target;
            public long Start;
            public bool Due, Would, Done;
            public int Why;
            public int Group;    // docs/686 §37: UnseenSkipCore.kGroup*
            public bool Unseen;  // docs/686 §37 gymtwin: an NPC unseen when its wait began
            public bool FpSeen, FpWould, FpKept;  // docs/686 §36 (FacilityPlan.cs): the planner's own once-per-wait flags
            public List<GameObject> FpBad;     // docs/686 §36: objects this wait could not plan to (left out of its later checks)
        }

        static readonly Dictionary<Sim, LswWait> sLswWaits = new Dictionary<Sim, LswWait>();
        static readonly List<GameObject> sLswBuf = new List<GameObject>();
        static readonly LineCapCore sLswCap = new LineCapCore(20, 60000, 300);
        static readonly string[] kLswWhy = { "", "not a human", "player's action", "no free object", "household without lineswitchhh", "refused by the game" };
        static readonly int[] sLswWhy = new int[6];
        static string sLswLast = "", sLswLastError = "", sLswMissing = "";
        static Sim sLswSim;
        static long sLswTick;

        static void ApplyLineSwitch()
        {
            sLswMissing = "";
            if (!Patch(typeof(SimQueue), "WaitForExitReasonOnSim", new Type[] { typeof(Sim), typeof(float), typeof(ExitReason) }, typeof(LswExitHook), "ExitHook", "ExitOrig")) sLswMissing = "SimQueue.WaitForExitReasonOnSim";
            if (sLswMissing.Length > 0) AppliedLog().Add("lineswitch: not found: " + sLswMissing);
        }

        static void LswLine(string text)
        {
            sLswLast = text;
            if (ShortcutOn("lineswitchlog") && sLswCap.Take(System.Environment.TickCount)) DiagLine("lineswitch: " + text);
        }

        static void LswError(Exception e)
        {
            int n = PatchCounters()[kLswErrors]++;
            sLswLastError = "lineswitch: error: " + e.GetType().Name + ": " + e.Message;
            if (n < 3) DiagLine(sLswLastError);
        }

        // The kind of object a line belongs to, or null: lines of other objects are left alone.
        static Type LswKind(GameObject t, InteractionInstance ia)
        {
            if (t is Toilet) return typeof(Toilet);
            if (t is Sink) return typeof(Sink);
            bool shower = t is IShowerable;
            if (shower && ia.GetType().Name.IndexOf("Shower", StringComparison.Ordinal) >= 0) return typeof(IShowerable);
            if (t is Bathtub) return typeof(Bathtub);
            if (shower) return typeof(IShowerable);
            // docs/686 §37: lineswitchpc, gymtwin
            if (t is Computer) return typeof(Computer);
            if (t is Bookshelf) return typeof(Bookshelf);
            if (t is WorkoutBench) return typeof(WorkoutBench);
            if (t is Treadmill) return typeof(Treadmill);
            return null;
        }

        static int LswGroup(Type kind)
        {
            if (kind == null) return UnseenSkipCore.kGroupNone;
            if (kind == typeof(Computer) || kind == typeof(Bookshelf)) return UnseenSkipCore.kGroupPc;
            if (kind == typeof(WorkoutBench) || kind == typeof(Treadmill)) return UnseenSkipCore.kGroupGym;
            return UnseenSkipCore.kGroupBath;
        }

        static GameObject[] LswObjects(Lot lot, Type kind)
        {
            if (kind == typeof(Toilet)) return lot.GetObjects<Toilet>();
            if (kind == typeof(Sink)) return lot.GetObjects<Sink>();
            if (kind == typeof(Bathtub)) return lot.GetObjects<Bathtub>();
            if (kind == typeof(Computer)) return lot.GetObjects<Computer>();
            if (kind == typeof(Bookshelf)) return lot.GetObjects<Bookshelf>();
            if (kind == typeof(WorkoutBench)) return lot.GetObjects<WorkoutBench>();
            if (kind == typeof(Treadmill)) return lot.GetObjects<Treadmill>();
            IShowerable[] s = lot.GetObjects<IShowerable>();
            if (s == null) return null;
            GameObject[] g = new GameObject[s.Length];
            for (int i = 0; i < s.Length; i++) g[i] = s[i] as GameObject;
            return g;
        }

        // LdsQueueHook.WaitHook, before EA's wait: remember the wait of an object line (no rabbit holes, no portals).
        static LswWait LswBegin(SimQueue q, InteractionInstance ia, IGameObject target)
        {
            try
            {
                Sim s = ia == null ? null : ia.InstanceActor;
                GameObject t = target as GameObject;
                Type kind = s == null || t == null || !object.ReferenceEquals(ia.Target, t) ? null : LswKind(t, ia);
                if (kind == null) return null;
                if (sLswWaits.Count >= 64) sLswWaits.Clear();  // Sims destroyed mid-wait leave entries behind
                LswWait w = new LswWait();
                w.Queue = q; w.Ia = ia; w.Target = t; w.Start = SimClock.CurrentTicks;
                w.Group = LswGroup(kind);
                // gymtwin decides the view once, at the start of the wait (an NPC that somebody sees waits first)
                if (w.Group == UnseenSkipCore.kGroupGym && q.MemberCount() > 0 && !IsPlayerSim(s)) w.Unseen = UnsScopeUnseen(s, t, Vector3.Invalid, true);
                sLswWaits[s] = w;
                return w;
            }
            catch (ResetException) { throw; }
            catch (Exception e) { LswError(e); return null; }
        }

        // After EA's wait (also on a reset): a wait that reached the switch time and did not switch is counted as kept.
        static void LswEnd(Sim s, LswWait w)
        {
            try
            {
                LswWait cur;
                if (s != null && sLswWaits.TryGetValue(s, out cur) && object.ReferenceEquals(cur, w)) sLswWaits.Remove(s);
                FpWaitEnd(w);  // docs/686 §36: the line wait's length
                if (w.Due && !w.Done && !w.Would)
                {
                    PatchCounters()[kLswKept]++;
                    sLswWhy[w.Why]++;
                    LswLine("kept, " + kLswWhy[w.Why] + ": " + LswHead(s, w, null) + ", waited " + OjsMinutes(w.Start).ToString("0.0") + " sim-min");
                }
            }
            catch (ResetException) { throw; }
            catch (Exception e) { LswError(e); }
        }

        static bool LswHuman(Sim s)
        {
            return s != null && !s.HasBeenDestroyed && s.SimDescription != null && s.SimDescription.IsHuman && !s.IsInBeingRiddenPosture;
        }

        static string LswHead(Sim s, LswWait w, GameObject to)
        {
            return BuWho(s) + " " + (s == null ? "?" : s.FullName) + ", " + w.Ia.GetType().Name + " " + w.Target.GetType().Name + (to != null ? " -> " + to.GetType().Name : "");
        }

        static bool LswClaimed(GameObject o, Sim self)
        {
            foreach (Sim s in Sims3.Gameplay.Queries.GetObjects<Sim>())
            {
                if (s == null || s == self || s.InteractionQueue == null) continue;
                foreach (InteractionInstance ii in s.InteractionQueue.InteractionList)
                    if (ii != null && object.ReferenceEquals(ii.Target, o)) return true;
            }
            return false;
        }

        // The best free object of the same kind with a replacement instance that passes its test, or null.
        static GameObject LswFind(Sim sim, LswWait w, out InteractionInstance repl, out bool refused)
        {
            repl = null; refused = false;
            GameObject t = w.Target;
            Lot lot = t.LotCurrent;
            if (lot == null || lot.IsWorldLot) return null;
            GameObject[] objs = LswObjects(lot, LswKind(t, w.Ia));
            if (objs == null || objs.Length < 2) return null;
            sLswBuf.Clear();
            foreach (GameObject o in objs)
            {
                if (o == null || object.ReferenceEquals(o, t) || o.HasBeenDestroyed || !o.InWorld || o.InUse || o.ActorsUsingMe.Count > 0) continue;
                SimQueue line = o.SimLine;
                if (line != null && line.HasMembers()) continue;
                sLswBuf.Add(o);
            }
            // best first; at most two plans per check
            sLswBuf.Sort(delegate (GameObject a, GameObject b)
            {
                return LineSwitchCore.Rank(a.RoomId == t.RoomId, a.Level == t.Level, (a.Position - sim.Position).Length())
                    .CompareTo(LineSwitchCore.Rank(b.RoomId == t.RoomId, b.Level == t.Level, (b.Position - sim.Position).Length()));
            });
            int plans = 0;
            foreach (GameObject o in sLswBuf)
            {
                if (LswClaimed(o, sim)) continue;
                InteractionInstance ni = w.Ia.InteractionDefinition.CreateInstance(o, sim, w.Ia.GetPriority(), w.Ia.Autonomous, w.Ia.CancellableByPlayer);
                if (ni == null || !ni.Test()) { refused = true; continue; }
                if (plans++ >= 2) break;
                PatchCounters()[kLswPlans]++;
                Route r = sim.CreateRoute();
                r.DoRouteFail = false;
                r.PlanToPointRadialRange(o, o.PositionOnFloor, 0f, 1.5f, RouteDistancePreference.PreferNearestToRouteDestination,
                    RouteOrientationPreference.TowardsObject, lot.LotId, new int[] { o.RoomId });
                if (!r.PlanResult.Succeeded()) continue;
                repl = ni;
                return o;
            }
            return null;
        }

        // One per-minute check of the wait loop. True: the replacement is pushed, the loop breaks.
        static bool LswCheck(object queue, Sim sim)
        {
            LswWait w;
            if (sim == null || !sLswWaits.TryGetValue(sim, out w) || !object.ReferenceEquals(w.Queue, queue) || w.Done) return false;
            float waited = OjsMinutes(w.Start);
            if (!(waited >= UnseenSkipCore.SwitchAfter(w.Group, w.Unseen, Instantiator.kLineSwitchMinutes))) return false;
            w.Due = true;
            if (w.Target.HasBeenDestroyed || sim.HasExitReason()) return false;  // the game's own exit comes first
            bool on = ShortcutOn(w.Group == UnseenSkipCore.kGroupPc ? "lineswitchpc" : w.Group == UnseenSkipCore.kGroupGym ? "gymtwin" : "lineswitch");
            bool human = LswHuman(sim), auto = w.Ia.Autonomous;
            InteractionInstance repl = null;
            bool refused = false;
            GameObject to = human && auto ? LswFind(sim, w, out repl, out refused) : null;
            int why;
            int d = LineSwitchCore.Decide(on, human, auto, to != null, UnseenSkipCore.InScope(w.Group, !IsPlayerSim(sim), ShortcutOn("lineswitchhh")), out why);
            if (why == LineSwitchCore.kWhyNoFree && refused) why = LineSwitchCore.kWhyRefused;
            w.Why = why;
            string where = to == null ? "" : " (" + (to.RoomId == w.Target.RoomId ? "same room" : "other room") + ", " + (to.Position - sim.Position).Length().ToString("0.0") + " m, waited " + waited.ToString("0.0") + " sim-min)";
            if (d == LineSwitchCore.kWould)
            {
                if (!w.Would)
                {
                    w.Would = true;
                    PatchCounters()[w.Group == UnseenSkipCore.kGroupPc ? kUnsPcWould : w.Group == UnseenSkipCore.kGroupGym ? kUnsGymWould : kLswWould]++;
                    LswLine("would switch: " + LswHead(sim, w, to) + where);
                }
                return false;
            }
            if (d != LineSwitchCore.kGo) return false;
            sim.InteractionQueue.PushAsContinuation(repl, false);
            repl.WasInUse = true;
            sim.AddExitReason(ExitReason.FailedToStart);
            w.Done = true;
            sLswSim = sim; sLswTick = SimClock.CurrentTicks;
            int[] c = PatchCounters();
            c[w.Group == UnseenSkipCore.kGroupPc ? kUnsPcSwitched : w.Group == UnseenSkipCore.kGroupGym ? kUnsGymSwitched : kLswSwitched]++;
            if (w.Group == UnseenSkipCore.kGroupGym && w.Unseen) c[kUnsGymUnseen]++;
            LswLine("switched" + (w.Unseen ? " (unseen)" : "") + ": " + LswHead(sim, w, to) + where);
            return true;
        }

        // SrfPlayHook: EA's timeout tail right after our break plays no route fail for this Sim on this tick.
        static bool LswSkipRouteFail(Sim sim)
        {
            if (sim == null || sim != sLswSim) return false;
            sLswSim = null;
            return sLswTick == SimClock.CurrentTicks;
        }

        // `this` is the game's SimQueue.
        public class LswExitHook
        {
            public bool ExitHook(Sim sim, float minutesToWait, ExitReason exitReasons)
            {
                if (sLswWaits.Count > 0)
                {
                    // docs/686 §36 (FacilityPlan.cs): the facility planner first (a sooner object, or something else first);
                    // lineswitch's free-twin rule after it
                    try
                    {
                        LswWait fw;
                        if (sim != null && sLswWaits.TryGetValue(sim, out fw) && object.ReferenceEquals(fw.Queue, this) && FpWait(this, sim, fw)) return true;
                    }
                    catch (ResetException) { throw; }
                    catch (Exception e) { FpError(e); }
                    try { if (LswCheck(this, sim)) return true; }
                    catch (ResetException) { throw; }
                    catch (Exception e) { LswError(e); }
                }
                if (sUnsSlideHeads.Count > 0)  // docs/686 §37 (UnseenSkip.cs): a slide hang-out past EA's HangOutTimeMax ends
                {
                    try { UnsSlideCheck(this, sim); }
                    catch (ResetException) { throw; }
                    catch (Exception e) { UnsError(e); }
                }
                return ExitOrig(sim, minutesToWait, exitReasons);
            }
            public bool ExitOrig(Sim sim, float minutesToWait, ExitReason exitReasons) { return false; }  // receives SimQueue.WaitForExitReasonOnSim
        }

        // ---- check ----

        static string LswCounters()
        {
            int[] c = PatchCounters();
            string why = "";
            for (int i = 1; i < sLswWhy.Length; i++) if (sLswWhy[i] > 0) why += (why.Length > 0 ? ", " : "") + kLswWhy[i] + " " + sLswWhy[i];
            return "lineswitch: switched " + c[kLswSwitched] + "; off: would " + c[kLswWould] + "; kept " + c[kLswKept] + (why.Length > 0 ? " (" + why + ")" : "")
                + "; plans " + c[kLswPlans] + "; errors " + c[kLswErrors] + (sLswLastError.Length > 0 ? " (last: " + sLswLastError + ")" : "") + "; " + GlMembers(sLswMissing)
                + (sLswLast.Length > 0 ? "; last: " + sLswLast : "");
        }

        // LineSwitchCheck run: the rules with controls. result: the counters and the last line.
        public static object LineSwitchCheck(string what)
        {
            if (what == "result") return LswCounters();
            if (what != "run") return "lineswitch <run|result>";
            bool on = ShortcutOn("lineswitch");
            int why;
            bool fixedNow = on && LineSwitchCore.Decide(true, true, true, true, true, out why) == LineSwitchCore.kGo
                && LineSwitchCore.Due(2f, 2f) && !LineSwitchCore.Due(1.9f, 2f)
                && LineSwitchCore.Rank(true, true, 9f) < LineSwitchCore.Rank(false, true, 1f);
            bool controls = LineSwitchCore.Decide(true, false, true, true, true, out why) == LineSwitchCore.kKeep && why == LineSwitchCore.kWhyNotHuman
                && LineSwitchCore.Decide(true, true, false, true, true, out why) == LineSwitchCore.kKeep && why == LineSwitchCore.kWhyPlayer
                && LineSwitchCore.Decide(true, true, true, false, true, out why) == LineSwitchCore.kKeep && why == LineSwitchCore.kWhyNoFree
                && LineSwitchCore.Decide(true, true, true, true, false, out why) == LineSwitchCore.kKeep && why == LineSwitchCore.kWhyExempt
                && LineSwitchCore.Decide(false, true, true, true, false, out why) == LineSwitchCore.kWould;
            return "lineswitch check (switch " + (on ? "on" : "off") + ", lineswitchhh " + (ShortcutOn("lineswitchhh") ? "on" : "off")
                + "): a Sim in a line takes a free object of the same kind after " + LineSwitchCore.After(Instantiator.kLineSwitchMinutes).ToString("0.0") + " sim-min=" + fixedNow
                + "; control: pets, player's action, no free object, household without lineswitchhh kept=" + controls + "; verdict: " + GlVerdict(on, fixedNow, controls, sLswMissing.Length == 0) + "; " + LswCounters();
        }
    }
}
