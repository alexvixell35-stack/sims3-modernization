using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects;
using Sims3.Gameplay.Objects.Environment;
using Sims3.Gameplay.Objects.Electronics;
using Sims3.Gameplay.Objects.HobbiesSkills;
using Sims3.Gameplay.Objects.Plumbing;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Looping interactions of NPCs nobody can see (docs/632). Default off. One generic switch at EA's shared loop
    // (Interaction<,>.DoLoop) instead of a shortcut per object (docs/628 was the bar-only version).
    public static partial class Fixes
    {
        // ---- docs/632 unseenloopskip ----------------------------------------------------------------------------------------
        // [verified, ilspycmd Interaction<TActor,TTarget>::DoLoop] each pass waits WaitForExitReason(kWaitForExitReasonDefaultTime
        // = 1 sim-min), sends kUsedObect, calls loopMe once when the lifetime passed the next notify step (mDeltaTime = time
        // since the previous pass, mLifeTime = since the start), runs UpdateLoop; and when the actor is low LOD
        // (!IsSufficientlyOnScreenForHighLODSimulation) and the interaction is autonomous, EA itself ends the loop with
        // ExitReason.Finished once mLifeTime passes Autonomy.MaxTimeForLowLODInteractionDuringTheDay (420 min, 4:00-22:00) or
        // ...DuringTheNight (120 min). Those two static getters have no other reader (callgraph + decompiled grep), so they
        // are the one choke point where EA already decides "end this loop early, nobody is watching".
        // Fix (b) early end: for an allow-listed class, an in-scope actor's getter answers min(EA, kUnseenLoopSkipCapMin); the
        // loop then ends through EA's own branch (Finished) and the interaction's own post-loop steps (EndCommodityUpdates,
        // StandardExit, skill stop) run as at EA's 420/120-minute end. Commodity rates lost: none, [verified, SimUpdate::Simulate]
        // a low-LOD Sim skips Autonomy.Update (UpdateMotives/UpdateSkills) in EA's path too.
        // Fix (a) fewer passes (only with kUnseenLoopSkipStep, patched at ApplyTuned: Sim.WaitForExitReason is a hot path): for
        // a class whose callback is delta-aware, an in-scope actor's 1-minute DoLoop wait becomes kUnseenLoopSkipStepMin; the
        // callback then sees mDeltaTime = the whole step, so its per-minute outcome is the same in fewer passes. Exit reasons
        // still end the wait on the same tick (EA's own poll). Re-checked at every step.
        // Scope (both): the actor (and a Sim target) not a household / selectable / selected Sim, low LOD, autonomous
        // interaction (EA's own test), own task; tier 1 `unseenloopskip`: UfWhy 0; tier 2 `unseenloopskip_offscreen` (needs
        // tier 1 on): UfWhy says near viewers or on screen and ZoomHidden (docs/631: not on screen, or with `zoomtier` on too
        // small to see). Sims are never moved.
        // Counters: [3700-3701] (b) loops given the lower cap, tier 1 / tier 2 (once per instance), [3702-3703] (a) stepped
        // waits tier 1 / tier 2, [3704] allow-listed but out of scope, [3705] (b) asks for a class not on the list, [3706]
        // asks off the actor's own task, [3707] errors, [3708] Check mismatches; out-of-scope reasons (3704 split): [3709]
        // household / selected, [3710] high LOD by our re-check, [3711] not autonomous, [3712] partner out of scope, [3713-3722]
        // UfWhy code 1-10 (3715 household, 3716 active lot, 3717 on screen, 3718 near viewers, 3719 held, 3720 social seen);
        // [3723-3739] spare.
        internal const int kLsEnd = 1, kLsStep = 2;
        static readonly Dictionary<Type, int> sLsClasses = new Dictionary<Type, int>();
        static string sLsMissing = "";
        static bool sLsStepPatched;
        static InteractionInstance sLsLastCapped;
        // EA's live low-LOD caps: the tuning XML sets them (Bridgeport r88: 360 day / 180 night, not the C# 420 / 120)
        static readonly FieldInfo sLsDayField = typeof(Autonomy).GetField("kMaxTimeForLowLODInteractionDuringTheDay", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        static readonly FieldInfo sLsNightField = typeof(Autonomy).GetField("kMaxTimeForLowLODInteractionDuringTheNight", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        // unlisted classes that reached EA's low-LOD cap test (the Check lists the biggest), bounded
        static readonly Dictionary<Type, int> sLsUnlisted = new Dictionary<Type, int>();
        const int kLsWhyBase = 3712;  // 3712 + UfWhy code (1..10 -> 3713-3722) when the Sim is out of scope by UfWhy

        static float LsField(FieldInfo f, float fallback)
        {
            try { object v = f == null ? null : f.GetValue(null); return v is float ? (float)v : fallback; }
            catch (Exception) { return fallback; }
        }

        static void LsAdd(Type t, int kind, string name, List<string> miss)
        {
            if (t == null) miss.Add(name); else sLsClasses[t] = kind;
        }

        // ---- docs/634 per-family switches ---------------------------------------------------------------------------------
        // One switch per townie loop family, on the same machinery (tiers, scope, getters, step hook) but gated by its own
        // switch instead of `unseenloopskip`; tier 2 still needs `unseenloopskip_offscreen`. A family class with its switch
        // off is EA's (and counts as unlisted under `unseenloopskip`). Counters per family at base: [+0] unseen tier,
        // [+1] off-screen tier (once per capped instance / per stepped wait), [+2] out of scope, [+3] Check mismatches.
        static readonly string[] kLsFamSwitch = { "unseenpianist", "unseenmusicwatch", "unseenjam", "unseenshow" };
        static readonly int[] kLsFamBase = { 3750, 3756, 3762, 3768 };
        static readonly int[] kLsFamKind = { kLsStep, kLsEnd, kLsStep, kLsStep };
        static readonly string[] kLsFamName = { "Piano.PianistPerform", "Piano.Watch", "Guitar.Jam", "ShowVenue.AutonomouslyAttendShow" };
        static readonly Type[] sLsFamType = new Type[4];
        static readonly Dictionary<Type, int> sLsFamily = new Dictionary<Type, int>();

        static void LsAddFam(int fam, Type t, List<string> miss)
        {
            sLsFamType[fam] = t;
            LsAdd(t, kLsFamKind[fam], kLsFamName[fam], miss);
            if (t != null) sLsFamily[t] = fam;
        }

        static int LsFam(Type t)
        {
            int f;
            return t != null && sLsFamily.TryGetValue(t, out f) ? f : -1;
        }

        // Any switch of the machinery on (generic or a family): the getters and the step hook look further only then.
        static bool LsAnyOn()
        {
            if (ShortcutOn("unseenloopskip")) return true;
            for (int i = 0; i < kLsFamSwitch.Length; i++) if (ShortcutOn(kLsFamSwitch[i])) return true;
            return false;
        }

        // Counter slot for a tier-1 / tier-2 action or an out-of-scope ask: the family's own block, else the generic one.
        static int LsSlot(int fam, int genericSlot, int famOffset)
        {
            return fam >= 0 ? kLsFamBase[fam] + famOffset : genericSlot;
        }

        // The allow list (docs/632 §3). Exact types: subclasses (Violin/FairyHouse dances, ProSports/PoliceStation workouts)
        // have their own Run and stay EA's.
        static void ApplyUnseenLoopSkip()
        {
            List<string> miss = new List<string>();
            const BindingFlags nested = BindingFlags.Public | BindingFlags.NonPublic;
            LsAdd(typeof(TV.WatchTV), kLsEnd, "TV.WatchTV", miss);
            LsAdd(typeof(ReadBook), kLsEnd | kLsStep, "ReadBook", miss);
            LsAdd(typeof(AthleticGameObject.WorkOut), kLsEnd | kLsStep, "AthleticGameObject.WorkOut", miss);
            LsAdd(typeof(Stereo.DanceSolo), kLsEnd, "Stereo.DanceSolo", miss);
            LsAdd(typeof(Stereo.DanceTogetherA), kLsEnd, "Stereo.DanceTogetherA", miss);
            LsAdd(typeof(HotTubBase.Relax), kLsEnd, "HotTubBase.Relax", miss);
            LsAdd(typeof(PoolTable).GetNestedType("PerformForTips", nested), kLsEnd, "PoolTable.PerformForTips", miss);
            // docs/632 §3b [verified, ilspycmd SwingSet+Swing.Run]: DoLoop(Default, LoopCallback); after the loop EA unregisters
            // group talk, stops swinging unless BePushed is next (AnimateSim Neutral, UpdateFootprint), EndCommodityUpdates,
            // TryMovingToAnotherObject. The callback reads mLifeTime only (playground move), so end only, no step.
            LsAdd(typeof(SwingSet).GetNestedType("Swing", nested), kLsEnd, "SwingSet.Swing", miss);
            // docs/634 families, each behind its own switch [verified, ilspycmd]:
            // Piano+PianistPerform (job loop, step only): PlayBandInstrument.PlayLoop sends one IncrementalEvent(kPlayedPiano,
            // mDeltaTime) per pass; nothing else per pass. The loop keeps running (no early end).
            LsAddFam(0, typeof(Piano).GetNestedType("PianistPerform", nested), miss);
            // Piano+Watch (leisure, end only): WatchLoopBase plays a pending sweet-move reaction; TippingLoop (tips mode) rolls
            // by lifetime, so no step. Post-loop: RemoveAlarm, RemoveWatcher, SongFinished(stop), listeners off, EndCommodityUpdates.
            LsAddFam(1, typeof(Piano).GetNestedType("Watch", nested), miss);
            // Guitar+Jam (leisure, step only): JamWithInstrument.PlayLoop = kPlayedGuitar IncrementalEvent + kSimJammed
            // TimePassedOnLotEvent, both with mDeltaTime. No end: the jam controller holds partners.
            LsAddFam(2, typeof(Guitar).GetNestedType("Jam", nested), miss);
            // ShowVenue+AutonomouslyAttendShow (MetaRabbitHoleInteraction.Run, step only): DoLoop(Default) with no callback inside
            // the venue; the visit ends by EA's TimerFinished alarm (an exit reason, ends the stepped wait on the same tick).
            LsAddFam(3, typeof(ShowVenue).GetNestedType("AutonomouslyAttendShow", nested), miss);
            sLsMissing = string.Join(", ", miss.ToArray());
            if (sLsDayField == null || sLsNightField == null) miss.Add("Autonomy.kMaxTimeForLowLODInteraction*");
            sLsMissing = string.Join(", ", miss.ToArray());
            if (sLsMissing.Length > 0) AppliedLog().Add("unseenloopskip: not found: " + sLsMissing);
            Patch(typeof(Autonomy), "get_MaxTimeForLowLODInteractionDuringTheDay", Type.EmptyTypes, typeof(LsCapHook), "DayHook", "DayOrig");
            Patch(typeof(Autonomy), "get_MaxTimeForLowLODInteractionDuringTheNight", Type.EmptyTypes, typeof(LsCapHook), "NightHook", "NightOrig");
        }

        // ApplyTuned: option (a) only with kUnseenLoopSkipStep (an extra call on every exit-reason poll otherwise).
        static void ApplyUnseenLoopSkipStep()
        {
            if (!Instantiator.kUnseenLoopSkipStep) return;
            sLsStepPatched = Patch(typeof(Sim), "WaitForExitReason", new Type[] { typeof(float), typeof(ExitReason) }, typeof(LsWaitHook), "WaitHook", "WaitOrig");
            TunedLog("kUnseenLoopSkipStep", sLsStepPatched, "unseenloopskip step hook");
        }

        internal static int LsKind(Type t)
        {
            int k;
            if (t == null || !sLsClasses.TryGetValue(t, out k)) return 0;
            int fam = LsFam(t);
            return ShortcutOn(fam >= 0 ? kLsFamSwitch[fam] : "unseenloopskip") ? k : 0;  // each class behind its own switch
        }

        // Pure: 0 = EA, 1 = unseen tier, 2 = off-screen tier. why = UfWhy (0 unseen, 5 on screen, 6 near viewers).
        internal static int LsTier(bool on, bool offOn, bool player, bool lowLod, bool autonomous, int why, bool zoomHidden)
        {
            if (!on || player || !lowLod || !autonomous) return 0;
            if (why == UnseenFallbackCore.kSeen) return 1;
            bool near = why == UnseenFallbackCore.kOnScreen || why == UnseenFallbackCore.kNearViewers;
            return offOn && near && zoomHidden ? 2 : 0;
        }

        // Pure: the cap the getter answers. EA's value unless in scope; never longer than EA's.
        internal static float LsCapFor(int tier, int kind, float ea, float cap)
        {
            return tier > 0 && (kind & kLsEnd) != 0 && cap >= 0f && cap < ea ? cap : ea;
        }

        // Pure: the wait DoLoop asks for, stepped only for its own 1-minute pass wait of a delta-aware class.
        internal static float LsStepFor(int tier, int kind, float asked, float defaultWait, float step)
        {
            return tier > 0 && (kind & kLsStep) != 0 && asked == defaultWait && step > asked ? step : asked;
        }

        // count: add the reason to the 3709-3723 breakdown (live asks only, not the Check's census)
        static int LsSimTier(Sim s, bool on, bool offOn, bool count)
        {
            int[] c = PatchCounters();
            if (s == null || s.HasBeenDestroyed || IsPlayerSim(s)) { if (count) c[3709]++; return 0; }  // household / selectable / selected
            Autonomy a = s.Autonomy;
            bool lowLod = a != null && !a.IsSufficientlyOnScreenForHighLODSimulation;
            if (!lowLod) { if (count) c[3710]++; return 0; }
            int why = UfWhy(s);
            bool hidden = why != UnseenFallbackCore.kSeen && offOn && ZoomHidden(s);  // re-checks World.IsObjectOnScreen now
            int t = LsTier(on, offOn, false, true, true, why, hidden);
            if (t == 0 && count && why >= 1 && why <= 10) c[kLsWhyBase + why]++;
            return t;
        }

        // Live scope for the Sim running on this task: tier of the actor, and of a Sim target (a partner must be in scope too).
        static int LsLive(InteractionInstance ia, Sim s, int kind)
        {
            if (kind == 0)  // not listed, or its switch off (LsKind)
            {
                if (!ShortcutOn("unseenloopskip")) return 0;
                PatchCounters()[3705]++;
                Type ut = ia.GetType();
                int n;
                if (sLsUnlisted.TryGetValue(ut, out n)) sLsUnlisted[ut] = n + 1; else if (sLsUnlisted.Count < 256) sLsUnlisted[ut] = 1;
                return 0;
            }
            bool offOn = ShortcutOn("unseenloopskip_offscreen");
            bool auto = ia.Autonomous || ia.GetPriority().Level <= InteractionPriorityLevel.Autonomous;  // EA's DoLoop test
            if (!auto) { PatchCounters()[3711]++; PatchCounters()[LsSlot(LsFam(ia.GetType()), 3704, 2)]++; return 0; }
            int fam = LsFam(ia.GetType());
            int t = LsSimTier(s, true, offOn, true);
            Sim partner = ia.Target as Sim;
            if (t > 0 && partner != null && partner != s)
            {
                int pt = LsSimTier(partner, true, offOn, false);  // the weaker tier of the pair decides; a seen partner keeps EA
                if (pt == 0) PatchCounters()[3712]++;
                t = pt == 0 ? 0 : Math.Max(t, pt);
            }
            if (t == 0) PatchCounters()[LsSlot(fam, 3704, 2)]++;
            return t;
        }

        static Sim LsTaskSim()
        {
            ObjectGuid task = Simulator.CurrentTask;
            return task.IsValid ? GameObject.GetObject<Sim>(task) : null;
        }

        static float LsCap(float ea)
        {
            try
            {
                if (!LsAnyOn()) return ea;
                Sim s = LsTaskSim();
                InteractionInstance ia = s == null ? null : s.CurrentInteraction;
                if (ia == null) { PatchCounters()[3706]++; return ea; }
                int kind = LsKind(ia.GetType());
                int tier = LsLive(ia, s, kind);
                float r = LsCapFor(tier, kind, ea, Instantiator.kUnseenLoopSkipCapMin);
                if (r < ea && !object.ReferenceEquals(ia, sLsLastCapped)) { sLsLastCapped = ia; PatchCounters()[LsSlot(LsFam(ia.GetType()), tier == 1 ? 3700 : 3701, tier == 1 ? 0 : 1)]++; }
                return r;
            }
            catch (ResetException) { throw; }
            catch (Exception) { PatchCounters()[3707]++; return ea; }
        }

        // Static getters: plain static hook class. Only Interaction<,>.DoLoop reads them, right after its low-LOD and
        // autonomous test, on the actor's own task with no yield in between.
        public static class LsCapHook
        {
            public static float DayHook() { return LsCap(DayOrig()); }
            public static float NightHook() { return LsCap(NightOrig()); }
            public static float DayOrig() { return 420f; }   // receives Autonomy.get_MaxTimeForLowLODInteractionDuringTheDay
            public static float NightOrig() { return 120f; } // receives Autonomy.get_MaxTimeForLowLODInteractionDuringTheNight
        }

        // Sim has a parameterless constructor, but nothing here needs its members: a plain class as the other Sim (WpSimHook).
        public class LsWaitHook
        {
            public bool WaitHook(float simMinutes, ExitReason acceptedExitReasons)
            {
                float w = simMinutes;
                try
                {
                    if (simMinutes == Sim.kWaitForExitReasonDefaultTime && LsAnyOn())
                    {
                        Sim s = (Sim)(object)this;
                        if (Simulator.CurrentTask == s.ObjectId)
                        {
                            InteractionInstance ia = s.CurrentInteraction;
                            int kind = ia == null ? 0 : LsKind(ia.GetType());
                            if ((kind & kLsStep) != 0)
                            {
                                int tier = LsLive(ia, s, kind);
                                w = LsStepFor(tier, kind, simMinutes, Sim.kWaitForExitReasonDefaultTime, Instantiator.kUnseenLoopSkipStepMin);
                                if (w != simMinutes) PatchCounters()[LsSlot(LsFam(ia.GetType()), tier == 1 ? 3702 : 3703, tier == 1 ? 0 : 1)]++;
                            }
                        }
                    }
                }
                catch (ResetException) { throw; }
                catch (Exception) { PatchCounters()[3707]++; w = simMinutes; }
                return WaitOrig(w, acceptedExitReasons);
            }
            public bool WaitOrig(float simMinutes, ExitReason acceptedExitReasons) { return false; } // receives Sim.WaitForExitReason's original header
        }

        // UnseenLoopSkipCheck run: pure scope and cap decisions with controls (off: EA's 420/120-minute low-LOD end, BROKEN;
        // on: an unseen allow-listed loop ends at the cap, FIXED), then live: the allow list resolved, the getter hook in,
        // EA's getters still answer EA's values outside scope, and a census of allow-listed loops running now by tier.
        public static object UnseenLoopSkipCheck(string what)
        {
            string counters = B582Counters("unseenloopskip", 3700, new[] { "capped unseen", "capped offscreen", "stepped unseen", "stepped offscreen",
                "listed, out of scope", "class not listed", "off own task", "errors", "check mismatches",
                "out: household", "out: high LOD", "out: not autonomous", "out: partner", "out: why1 notlive", "out: why2 gone", "out: why3 household",
                "out: why4 active lot", "out: why5 on screen", "out: why6 near viewers", "out: why7 held", "out: why8 social seen", "out: why9", "out: why10" }, sLsMissing);
            if (what == "result") return counters;
            if (what != "run") return "unseenloopskip <run|result>";
            bool on = ShortcutOn("unseenloopskip"), offOn = ShortcutOn("unseenloopskip_offscreen");
            float cap = Instantiator.kUnseenLoopSkipCapMin;
            float eaDay = LsField(sLsDayField, 420f), eaNight = LsField(sLsNightField, 120f);  // EA's live tuning
            bool eaBug = LsCapFor(LsTier(false, false, false, true, true, 0, true), kLsEnd, eaDay, cap) == eaDay;  // EA: its own cap
            bool fixedPure = on && LsCapFor(LsTier(on, offOn, false, true, true, 0, true), kLsEnd, eaDay, cap) == cap && cap < eaNight
                && LsStepFor(LsTier(on, offOn, false, true, true, 0, true), kLsEnd | kLsStep, 1f, 1f, 10f) == 10f;
            bool controls = eaBug
                && LsTier(true, true, true, true, true, 0, true) == 0         // household / selected
                && LsTier(true, true, false, false, true, 0, true) == 0       // high LOD
                && LsTier(true, true, false, true, false, 0, true) == 0       // user-directed (EA's own test)
                && LsTier(true, true, false, true, true, 5, false) == 0       // on screen and big enough
                && LsTier(true, false, false, true, true, 6, true) == 0       // off-screen tier needs its own switch
                && LsTier(true, true, false, true, true, 6, true) == 2
                && LsTier(true, true, false, true, true, 4, true) == 0        // active lot
                && LsTier(true, true, false, true, true, 7, true) == 0        // held
                && LsCapFor(1, 0, 420f, 30f) == 420f                          // class not listed
                && LsCapFor(1, kLsEnd, 20f, 30f) == 20f                       // never longer than EA
                && LsStepFor(1, kLsEnd, 1f, 1f, 10f) == 1f                    // step only for delta-aware classes
                && LsStepFor(1, kLsStep, 5f, 1f, 10f) == 5f;                  // only DoLoop's own 1-minute wait
            string live;
            bool liveOk = sLsMissing.Length == 0;
            try
            {
                float day = Autonomy.MaxTimeForLowLODInteractionDuringTheDay, night = Autonomy.MaxTimeForLowLODInteractionDuringTheNight;
                if (day != eaDay || night != eaNight) { liveOk = false; PatchCounters()[3708]++; }  // this task is no looping Sim: EA's live values
                int listed = 0, t1 = 0, t2 = 0;
                foreach (Sim s in Sims3.Gameplay.Queries.GetObjects<Sim>())
                {
                    InteractionInstance ia = s == null ? null : s.CurrentInteraction;
                    if (ia == null || LsKind(ia.GetType()) == 0) continue;
                    listed++;
                    int t = LsSimTier(s, true, true, false);
                    if (t == 1) t1++; else if (t == 2) t2++;
                }
                List<KeyValuePair<Type, int>> top = new List<KeyValuePair<Type, int>>(sLsUnlisted);
                top.Sort(delegate(KeyValuePair<Type, int> x, KeyValuePair<Type, int> y) { return y.Value.CompareTo(x.Value); });
                System.Text.StringBuilder ub = new System.Text.StringBuilder();
                for (int i = 0; i < top.Count && i < 15; i++) ub.Append((i == 0 ? "" : ", ") + top[i].Key.FullName.Replace("Sims3.Gameplay.", "") + " " + top[i].Value);
                live = "getters day " + day + " night " + night + " (EA tuning " + eaDay + "/" + eaNight + " off-scope), allow list " + sLsClasses.Count + " classes, step hook "
                    + (sLsStepPatched ? "in" : "not patched (kUnseenLoopSkipStep off)") + ", listed loops now " + listed + " (unseen " + t1 + ", off-screen " + t2 + ")"
                    + ", cap " + cap + " min, step " + Instantiator.kUnseenLoopSkipStepMin + " min, unlisted asks by class: " + (ub.Length > 0 ? ub.ToString() : "none");
            }
            catch (Exception e) { liveOk = false; live = "live probe threw " + (e.InnerException ?? e).GetType().Name; }
            return B582Line("unseenloopskip", on, "an unseen allow-listed autonomous loop ends at the cap through EA's low-LOD end", fixedPure && liveOk,
                "EA ends at its tuned " + eaDay + "/" + eaNight + " min (" + eaBug + "), household / high LOD / user-directed / seen / active lot / held keep EA, off-screen tier only with unseenloopskip_offscreen ("
                + offOn + "), unlisted classes keep EA, " + live, controls && liveOk, sLsMissing, counters);
        }

        // docs/634 family Checks: the family's pure decision for an unseen low-LOD autonomous NPC (off: EA's 1-minute pass /
        // EA's 360/180-minute end, BROKEN; on: stepped / capped, FIXED). Controls: household / high LOD / user-directed / seen /
        // active lot / held keep EA, the off-screen tier only with unseenloopskip_offscreen, a step family is never ended early
        // (job loops keep running), an end family is never stepped. Live: class resolved, step hook in, loops of the class now.
        public static object UnseenPianistCheck(string what) { return LsFamilyCheck(0, what, "an unseen pianist's play loop passes every step minutes (one kPlayedPiano delta event per pass), the job keeps running"); }
        public static object UnseenMusicWatchCheck(string what) { return LsFamilyCheck(1, what, "an unseen townie watching a piano ends at the cap through EA's low-LOD end"); }
        public static object UnseenJamCheck(string what) { return LsFamilyCheck(2, what, "an unseen guitar jam passes every step minutes (two delta events per pass), the jam keeps running"); }
        public static object UnseenShowCheck(string what) { return LsFamilyCheck(3, what, "an unseen show-venue visit waits in step-minute passes (no callback), ends by EA's alarm"); }

        static object LsFamilyCheck(int fam, string what, string desc)
        {
            string sw = kLsFamSwitch[fam];
            int b = kLsFamBase[fam], kind = kLsFamKind[fam];
            bool step = (kind & kLsStep) != 0;
            string missing = sLsFamType[fam] == null ? kLsFamName[fam] : "";
            string counters = B582Counters(sw, b, new[] { step ? "stepped unseen" : "capped unseen", step ? "stepped offscreen" : "capped offscreen", "out of scope", "check mismatches" }, missing);
            if (what == "result") return counters;
            if (what != "run") return sw + " <run|result>";
            bool on = ShortcutOn(sw), offOn = ShortcutOn("unseenloopskip_offscreen");
            float eaDay = LsField(sLsDayField, 420f), cap = Instantiator.kUnseenLoopSkipCapMin, stepMin = Instantiator.kUnseenLoopSkipStepMin;
            int tierOn = LsTier(on, offOn, false, true, true, 0, true);
            bool fixedNow = on && (step ? LsStepFor(tierOn, kind, 1f, 1f, stepMin) == stepMin && stepMin > 1f
                                        : LsCapFor(tierOn, kind, eaDay, cap) == cap && cap < eaDay);
            bool controls = (step ? LsStepFor(LsTier(false, false, false, true, true, 0, true), kind, 1f, 1f, 10f) == 1f   // off: EA's pass
                                  : LsCapFor(LsTier(false, false, false, true, true, 0, true), kind, 420f, 30f) == 420f)
                && LsTier(true, true, true, true, true, 0, true) == 0         // household / selected
                && LsTier(true, true, false, false, true, 0, true) == 0       // high LOD
                && LsTier(true, true, false, true, false, 0, true) == 0       // user-directed
                && LsTier(true, true, false, true, true, 5, false) == 0       // on screen and big enough
                && LsTier(true, false, false, true, true, 6, true) == 0       // off-screen tier needs unseenloopskip_offscreen
                && LsTier(true, true, false, true, true, 4, true) == 0        // active lot
                && LsTier(true, true, false, true, true, 7, true) == 0        // held
                && (step ? LsCapFor(1, kind, 360f, 30f) == 360f               // a step family is never ended early
                         : LsStepFor(1, kind, 1f, 1f, 10f) == 1f)             // an end family is never stepped
                && LsStepFor(1, kind, 5f, 1f, 10f) == 5f;                     // only DoLoop's own 1-minute wait
            if (!controls) PatchCounters()[b + 3]++;
            int t1 = 0, t2 = 0, other = 0;
            try
            {
                Type ty = sLsFamType[fam];
                if (ty != null)
                    foreach (Sim s in Sims3.Gameplay.Queries.GetObjects<Sim>())
                    {
                        InteractionInstance ia = s == null ? null : s.CurrentInteraction;
                        if (ia == null || ia.GetType() != ty) continue;
                        int t = LsSimTier(s, true, true, false);
                        if (t == 1) t1++; else if (t == 2) t2++; else other++;
                    }
            }
            catch (Exception) { }
            string live = "class " + (missing.Length == 0 ? "found" : "missing") + (step ? ", step " + stepMin + " min, step hook " + (sLsStepPatched ? "in" : "not patched (kUnseenLoopSkipStep off)") : ", cap " + cap + " min")
                + ", loops now unseen " + t1 + " off-screen " + t2 + " EA " + other;
            return B582Line(sw, on, desc, fixedNow,
                "off as EA, household / high LOD / user-directed / seen / active lot / held keep EA, off-screen tier only with unseenloopskip_offscreen (" + offOn + "), "
                + (step ? "never ended early" : "never stepped") + ", " + live, controls, missing, counters);
        }
    }
}
