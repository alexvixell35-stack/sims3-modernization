using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.ThoughtBalloons;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // reactoverlay (docs/686 §26, default off). A Sim busy with an action reacts 4-7 s late (household ReactionInteraction
    // queue up to 6.9 s in s496-s529): Sim.PlayReaction with NowOrLater or AfterInteraction plays EA's overlay only while
    // the Sim idles in a posture (mIsAlreadyIdling) or walks a long route; otherwise it queues a ReactionInteraction behind
    // the current action (InteractionQueue.AddNext, timeout kReactionTimeout = 60 sim-min), which plays when that action
    // ends, often long after the cause.
    // [verified, decompiled Sim.PlayReaction, OverlayComponent.RequestOverlay, ReactionInteraction.Run] EA's "Now" path
    // is OverlayComponent.PlayReaction(type, target, yield: false): an upper-body overlay over whatever animation runs,
    // gated by the parts the current clip holds (interaction + awareness part flags; All, then both arms and head, then
    // head, then face). The queued ReactionInteraction plays the same overlay component (IdleManager.PlayReactionAnimation).
    // Fix: Sim.PlayReaction is swapped. For a NowOrLater / AfterInteraction reaction of a human Sim busy with an action,
    // standing or seated (Sim.StandingPosture, SittingPosture; the posture allows reaction overlays), not routing, not in
    // a posture transition, carrying nothing, not at a toilet, sink, shower, bath, bed or pool, not in a conversation or a social,
    // with both arms and the head free in the current clip and an overlay clip of EA's for this reaction, EA's own Now
    // path runs: before callback, the overlay, the look-at, the thought balloon, the callback; nothing is queued. Any
    // other case runs EA's PlayReaction unchanged; a reaction it queued is watched (pass every 0.1 sim-min): at the
    // first safe moment before it is stale it is played as the overlay and taken out of the queue, and one that waited kReactOverlayStaleMs
    // (2000 ms real time) with no safe moment is dropped (a reaction to something long gone looks wrong). A queued
    // reaction at the head of the queue is never touched (removing it there would cancel the running action).
    // Scope: human Sims; NPCs with reactoverlay, household and selected Sims also with reactoverlayhh (a visible change,
    // docs/271 rule). Off = compare mode: would. reactoverlaylog: one line per case. Counters 4670-4679.
    // Check: ReactOverlayCheck. Swap: Sim.PlayReaction (13 arguments, stub).
    public static partial class Fixes
    {
        const int kRovNow = 4670, kRovLater = 4671, kRovWould = 4672, kRovQueued = 4673, kRovDropped = 4674, kRovWouldDrop = 4675,
            kRovRan = 4676, kRovRanMs = 4677, kRovRanMaxMs = 4678, kRovErrors = 4679;

        sealed class RovEntry
        {
            public ReactionInteraction Ia;
            public ReactionTypes Type;
            public int Start;
            public bool Would;
        }

        const BindingFlags kRovFlags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        static readonly FieldInfo sRovPartsIa = typeof(OverlayComponent).GetField("mInteractionPartsUsedFlags", kRovFlags);
        static readonly FieldInfo sRovPartsAw = typeof(OverlayComponent).GetField("mAwarenessPartsUsedFlags", kRovFlags);
        static readonly FieldInfo sRovTarget = typeof(ReactionInteraction).GetField("mLookAtTarget", kRovFlags);
        static readonly FieldInfo sRovBefore = typeof(ReactionInteraction).GetField("mPlayBeforeReactionCallback", kRovFlags);
        static readonly FieldInfo sRovPlayed = typeof(ReactionInteraction).GetField("mPlayedReaction", kRovFlags);
        static readonly Dictionary<Sim, RovEntry> sRovQueue = new Dictionary<Sim, RovEntry>();
        static readonly List<Sim> sRovGone = new List<Sim>();
        static readonly LineCapCore sRovCap = new LineCapCore(30, 60000, 300);
        static readonly string[] kRovWhy = { "", "not a human", "routing", "posture", "posture transition", "carrying", "toilet, bath, bed or pool",
            "conversation", "arms or head busy", "no overlay clip", "household without reactoverlayhh", "asleep or no reactions", "age transition" };
        static readonly int[] sRovWhy = new int[ReactOverlayCore.kWhyCount];
        static AlarmHandle sRovAlarm = AlarmHandle.kInvalidHandle;
        static string sRovLast = "", sRovLastError = "", sRovMissing = "";

        static void ApplyReactOverlay()
        {
            sRovMissing = "";
            if (sRovPartsIa == null || sRovPartsAw == null) sRovMissing += "OverlayComponent part flags ";
            if (sRovTarget == null || sRovBefore == null || sRovPlayed == null) sRovMissing += "ReactionInteraction fields ";
            if (!Patch(typeof(Sim), "PlayReaction", new Type[] { typeof(ReactionTypes), typeof(InteractionPriority), typeof(GameObject), typeof(string), typeof(ResourceKey),
                typeof(ThoughtBalloonAxis), typeof(ReactionSpeed), typeof(Sim.PlayReactionCallback), typeof(Sim.PlayReactionCallback), typeof(bool), typeof(float), typeof(bool), typeof(bool) },
                typeof(RovHook), "PlayHook", "PlayOrig")) sRovMissing += "Sim.PlayReaction";
            sRovMissing = sRovMissing.Trim();
            if (sRovMissing.Length > 0) AppliedLog().Add("reactoverlay: not found: " + sRovMissing);
        }

        // every world load (from UfTpCleanupWorldLoad, sim thread)
        static void ReactOverlayWorldLoad()
        {
            sRovQueue.Clear();
            if (AlarmManager.Global == null) return;
            if (sRovAlarm != AlarmHandle.kInvalidHandle) AlarmManager.Global.RemoveAlarm(sRovAlarm);
            sRovAlarm = AlarmManager.Global.AddAlarmRepeating(0.1f, TimeUnit.Minutes, delegate { try { RovPass(); } catch (ResetException) { throw; } catch (Exception e) { RovError(e); } },
                0.1f, TimeUnit.Minutes, "Ts3Fixes.ReactOverlay", AlarmType.NeverPersisted, null);
        }

        static void RovLine(string text)
        {
            sRovLast = text;
            if (ShortcutOn("reactoverlaylog") && sRovCap.Take(System.Environment.TickCount)) DiagLine("reactoverlay: " + text);
        }

        static void RovError(Exception e)
        {
            int n = PatchCounters()[kRovErrors]++;
            sRovLastError = "reactoverlay: error: " + e.GetType().Name + ": " + e.Message;
            if (n < 3) DiagLine(sRovLastError);
        }

        static string RovHead(Sim s, ReactionTypes type)
        {
            InteractionInstance cur = s.CurrentInteraction;
            return BuWho(s) + " " + s.FullName + ", " + type + " during " + (cur == null ? "nothing" : cur.GetType().Name) + " (" + (s.Posture == null ? "?" : s.Posture.GetType().Name) + ")";
        }

        static bool RovPrivateTarget(InteractionInstance cur)
        {
            IGameObject t = cur.Target;
            if (t == null) return false;
            string ns = t.GetType().Namespace ?? "";
            return ns.StartsWith("Sims3.Gameplay.Objects.Plumbing") || ns.StartsWith("Sims3.Gameplay.Objects.Beds") || ns.StartsWith("Sims3.Gameplay.Pools");
        }

        // The first reason this is no moment for an overlay (kWhyNone = safe).
        static int RovBlocker(Sim s, ReactionTypes type, GameObject target)
        {
            if (!RovHuman(s)) return ReactOverlayCore.kWhyNotHuman;
            if (s.IsSleeping || Sims3.SimIFace.Environment.HasEditInGameModeSwitch) return ReactOverlayCore.kWhyAsleep;
            InteractionInstance cur = s.CurrentInteraction;
            if (cur is IInteractionDoesntAllowReactions) return ReactOverlayCore.kWhyAsleep;
            if (cur is Sims3.Gameplay.ActorSystems.AgeUp) return ReactOverlayCore.kWhyAgeUp;  // s542: Wave over the age-up spin (NRaas AgeUpEx too); the rig and model swap mid-way
            if (s.Conversation != null || cur is Sims3.Gameplay.Socializing.SocialInteraction) return ReactOverlayCore.kWhyConversation;  // s538: Wave over a social's start; its own clips come next
            if (s.IsRouting) return ReactOverlayCore.kWhyRouting;
            Posture p = s.Posture;
            if (!(p is Sim.StandingPosture || p is SittingPosture) || p.CurrentStateMachine == null || !p.AllowsReactionOverlay() || !p.AllowsOverlays())
                return ReactOverlayCore.kWhyPosture;
            if (s.InteractionQueue == null || s.InteractionQueue.TransitionInteraction != null) return ReactOverlayCore.kWhyTransition;
            if (s.CarryStateMachine != null) return ReactOverlayCore.kWhyCarry;
            if (cur != null && RovPrivateTarget(cur)) return ReactOverlayCore.kWhyPrivate;
            OverlayComponent oc = s.OverlayComponent;
            if (oc == null || sRovPartsIa == null || sRovPartsAw == null) return ReactOverlayCore.kWhyBusyParts;
            int used = Convert.ToInt32(sRovPartsIa.GetValue(oc)) | Convert.ToInt32(sRovPartsAw.GetValue(oc));
            if (!ReactOverlayCore.PartsFree(used)) return ReactOverlayCore.kWhyBusyParts;
            bool ownChild = false;
            Sim child = target as Sim;
            if (child != null && child.SimDescription != null && child.SimDescription.ToddlerOrBelow && s.Genealogy != null && s.Genealogy.IsParentOrStepParent(child.Genealogy)) ownChild = true;
            if (OverlayComponent.sReactionEnumToStringCache == null || !OverlayComponent.sReactionEnumToStringCache.ContainsKey(type)
                || !IdleManager.sReactionAnimations.ContainsKey(type) || s.IdleManager == null || !s.IdleManager.TestReactionAnimation(type, ownChild, true))
                return ReactOverlayCore.kWhyNoClip;
            return ReactOverlayCore.kWhyNone;
        }

        static bool RovHuman(Sim s)
        {
            return s != null && !s.HasBeenDestroyed && s.SimDescription != null && s.SimDescription.IsHuman && !s.IsInBeingRiddenPosture;
        }

        static bool RovLive(GameObject t) { return t != null && t.ObjectId != ObjectGuid.InvalidObjectGuid && !t.HasBeenDestroyed && t.InWorld; }

        // EA's Now path of Sim.PlayReaction (the overlay branch), for a Sim that is busy instead of idle or walking.
        static bool RovPlayNow(Sim s, ReactionTypes type, GameObject target, string balloon, ResourceKey key, ThoughtBalloonAxis axis,
            Sim.PlayReactionCallback before, Sim.PlayReactionCallback after)
        {
            if (before != null) before(s, target, type, ReactionSpeed.Immediate, true);
            bool played = s.OverlayComponent.PlayReaction(type, target, false);
            if (!played) return false;
            bool live = RovLive(target);
            if (live) s.LookAtManager.ConsiderReactionLookAt(target, 50, (LookAtJointFilter)1);
            ThoughtBalloonManager.BalloonData bd = null;
            if (key != ResourceKey.kInvalidResourceKey) bd = new ThoughtBalloonManager.BalloonData(new ThumbnailKey(key, (ThumbnailSize)2));
            else if (!string.IsNullOrEmpty(balloon)) bd = ThoughtBalloonManager.GetBalloonData(balloon, s);
            else if (live) bd = new ThoughtBalloonManager.BalloonData(target.GetThoughtBalloonThumbnailKey());
            if (bd != null && bd.IsValid) { bd.LowAxis = axis; s.ThoughtBalloonManager.ShowBalloon(bd); }
            if (after != null) after(s, target, type, ReactionSpeed.Immediate, true);
            return true;
        }

        // 0: not a case (EA's PlayReaction runs); 1: played now; 2: EA's PlayReaction runs, its queued reaction is watched.
        static int RovBefore(Sim s, ReactionTypes type, InteractionPriority priority, GameObject target, string balloon, ResourceKey key, ThoughtBalloonAxis axis,
            ReactionSpeed speed, Sim.PlayReactionCallback before, Sim.PlayReactionCallback after, bool watch, bool doubleBalloon)
        {
            if (s == null || doubleBalloon || type == ReactionTypes.None || !ReactOverlayCore.LateKind((int)speed, watch)) return 0;
            InteractionInstance cur = s.CurrentInteraction;
            if (cur == null || cur is ReactionInteraction || !IdleManager.sReactionAnimations.ContainsKey(type)) return 0;  // idle: EA's queue runs it at once
            int why;
            int d = ReactOverlayCore.Decide(ShortcutOn("reactoverlay"), RovBlocker(s, type, target), ShlScope(s, ShortcutOn("reactoverlayhh")), out why);
            if (d == ReactOverlayCore.kGo && RovPlayNow(s, type, target, balloon, key, axis, before, after))
            {
                PatchCounters()[kRovNow]++;
                RovLine("overlaid now: " + RovHead(s, type));
                return 1;
            }
            if (d == ReactOverlayCore.kWould) { PatchCounters()[kRovWould]++; RovLine("would overlay: " + RovHead(s, type)); }
            else sRovWhy[why]++;
            return 2;
        }

        static void RovAfterQueued(Sim s, ReactionTypes type)
        {
            if (s.InteractionQueue == null) return;
            foreach (InteractionInstance ia in s.InteractionQueue.InteractionList)
            {
                ReactionInteraction ri = ia as ReactionInteraction;
                if (ri == null || ri.ReactionType != type) continue;
                RovEntry e;
                if (sRovQueue.TryGetValue(s, out e) && e.Ia == ri) return;
                sRovQueue[s] = new RovEntry { Ia = ri, Type = type, Start = System.Environment.TickCount };
                PatchCounters()[kRovQueued]++;
                return;
            }
        }

        // Index of the reaction in the Sim's queue: 0 = head (running or next), -1 = gone.
        static int RovIndex(Sim s, InteractionInstance ri)
        {
            int i = 0;
            foreach (InteractionInstance ia in s.InteractionQueue.InteractionList) { if (ia == ri) return i; i++; }
            return -1;
        }

        static void RovPass()
        {
            if (sRovQueue.Count == 0) return;
            int[] c = PatchCounters();
            int now = System.Environment.TickCount;
            bool on = ShortcutOn("reactoverlay"), hh = ShortcutOn("reactoverlayhh");
            sRovGone.Clear();
            foreach (KeyValuePair<Sim, RovEntry> kv in sRovQueue)
            {
                Sim s = kv.Key;
                RovEntry e = kv.Value;
                int age = now - e.Start;
                if (s.HasBeenDestroyed || s.InteractionQueue == null) { sRovGone.Add(s); continue; }
                int at = RovIndex(s, e.Ia);
                if (at < 0 || s.CurrentInteraction == e.Ia)
                {
                    if (s.CurrentInteraction == e.Ia || s.InteractionQueue.IsRunning(e.Ia, true))
                    {
                        c[kRovRan]++; c[kRovRanMs] += age; if (age > c[kRovRanMaxMs]) c[kRovRanMaxMs] = age;
                        RovLine("queued reaction ran after " + age + " ms: " + RovHead(s, e.Type));
                    }
                    sRovGone.Add(s);
                    continue;
                }
                if (at == 0) continue;  // the head: EA's own turn, never removed there
                GameObject target = sRovTarget == null ? null : sRovTarget.GetValue(e.Ia) as GameObject;
                bool stale = ReactOverlayCore.Stale(age, Instantiator.kReactOverlayStaleMs);
                int why;
                int d = ReactOverlayCore.Decide(on, RovBlocker(s, e.Type, target), ShlScope(s, hh), out why);
                if (!stale && d == ReactOverlayCore.kGo && sRovBefore != null && sRovPlayed != null)  // r6-rp1: never play a stale one late
                {
                    Sim.PlayReactionCallback before = sRovBefore.GetValue(e.Ia) as Sim.PlayReactionCallback;
                    sRovBefore.SetValue(e.Ia, null);
                    if (before != null) before(s, target, e.Type, ReactionSpeed.Immediate, true);
                    if (s.OverlayComponent.PlayReaction(e.Type, target, false))
                    {
                        if (RovLive(target)) s.LookAtManager.ConsiderReactionLookAt(target, 50, (LookAtJointFilter)1);
                        sRovPlayed.SetValue(e.Ia, true);  // its Cleanup hands the callback "played"
                        s.InteractionQueue.RemoveInteractionByRef(e.Ia);
                        c[kRovLater]++;
                        RovLine("overlaid from the queue after " + age + " ms: " + RovHead(s, e.Type));
                        sRovGone.Add(s);
                        continue;
                    }
                }
                if (!stale) continue;
                bool inScope = ShlScope(s, hh) && RovHuman(s);
                if (!on || !inScope)
                {
                    if (!e.Would && !on && RovHuman(s)) { e.Would = true; c[kRovWouldDrop]++; RovLine("would drop after " + age + " ms: " + RovHead(s, e.Type)); }
                    continue;
                }
                s.InteractionQueue.RemoveInteractionByRef(e.Ia);
                c[kRovDropped]++;
                RovLine("dropped stale after " + age + " ms (" + (why == 0 ? "safe only after the stale time" : kRovWhy[why]) + "): " + RovHead(s, e.Type));
                sRovGone.Add(s);
            }
            foreach (Sim s in sRovGone) sRovQueue.Remove(s);
        }

        // reactoverlay (docs/686 §26): EA's Now overlay for a busy Sim; `this` is the game's Sim.
        public class RovHook
        {
            public bool PlayHook(ReactionTypes reactionType, InteractionPriority priority, GameObject target, string thoughtBalloon, ResourceKey key, ThoughtBalloonAxis axis,
                ReactionSpeed reactionSpeed, Sim.PlayReactionCallback beforeCallback, Sim.PlayReactionCallback callback, bool ShouldWatchUntilExit, float timeout, bool doubleBalloon, bool allowWhileSleeping)
            {
                Sim me = (Sim)(object)this;
                int r = 0;
                try { r = RovBefore(me, reactionType, priority, target, thoughtBalloon, key, axis, reactionSpeed, beforeCallback, callback, ShouldWatchUntilExit, doubleBalloon); }
                catch (ResetException) { throw; }
                catch (Exception e) { RovError(e); r = 0; }
                if (r == 1) return true;
                bool ok = PlayOrig(reactionType, priority, target, thoughtBalloon, key, axis, reactionSpeed, beforeCallback, callback, ShouldWatchUntilExit, timeout, doubleBalloon, allowWhileSleeping);
                if (r == 2 && ok)
                {
                    try { RovAfterQueued(me, reactionType); }
                    catch (ResetException) { throw; }
                    catch (Exception e) { RovError(e); }
                }
                return ok;
            }
            public bool PlayOrig(ReactionTypes reactionType, InteractionPriority priority, GameObject target, string thoughtBalloon, ResourceKey key, ThoughtBalloonAxis axis,
                ReactionSpeed reactionSpeed, Sim.PlayReactionCallback beforeCallback, Sim.PlayReactionCallback callback, bool ShouldWatchUntilExit, float timeout, bool doubleBalloon, bool allowWhileSleeping)
            { return false; }  // receives Sim.PlayReaction
        }

        // ---- check ----

        static string RovCounters()
        {
            int[] c = PatchCounters();
            string why = "";
            for (int i = 1; i < sRovWhy.Length; i++) if (sRovWhy[i] > 0) why += (why.Length > 0 ? ", " : "") + kRovWhy[i] + " " + sRovWhy[i];
            return "reactoverlay: overlaid now " + c[kRovNow] + ", from the queue " + c[kRovLater] + "; off: would " + c[kRovWould] + ", would drop " + c[kRovWouldDrop]
                + "; EA's way at the call" + (why.Length > 0 ? " (" + why + ")" : " 0") + "; queued by EA and watched " + c[kRovQueued] + "; dropped stale " + c[kRovDropped]
                + "; queued that ran " + c[kRovRan] + " (mean " + (c[kRovRan] > 0 ? c[kRovRanMs] / c[kRovRan] : 0) + " ms, max " + c[kRovRanMaxMs] + " ms)"
                + "; errors " + c[kRovErrors] + (sRovLastError.Length > 0 ? " (last: " + sRovLastError + ")" : "") + "; " + GlMembers(sRovMissing)
                + (sRovLast.Length > 0 ? "; last: " + sRovLast : "");
        }

        // Repro aid (reactoverlay-force): the reaction a game event would ask for, NowOrLater, on a Sim busy with an
        // action (EA's Sim.PlayReaction, so the hook decides as for any caller); the answer says where it went.
        static string RovForce(string args)
        {
            string[] a = args.Split('|');
            if (a.Length != 2) return "force: Sim Name|Reaction";
            ReactionTypes type;
            try { type = (ReactionTypes)Enum.Parse(typeof(ReactionTypes), a[1]); } catch (ArgumentException) { return "force: no reaction " + a[1]; }
            Sim s = null;
            foreach (Sim x in Sims3.Gameplay.Queries.GetObjects<Sim>()) if (x.FullName == a[0]) { s = x; break; }
            if (s == null) return "force: Sim not found";
            int now = PatchCounters()[kRovNow], would = PatchCounters()[kRovWould], queued = PatchCounters()[kRovQueued];
            string head = RovHead(s, type);
            int why = RovBlocker(s, type, null);
            bool ok = s.PlayReaction(type, ReactionSpeed.NowOrLater);
            return "force: " + head + ": PlayReaction " + ok + ", overlaid now " + (PatchCounters()[kRovNow] - now) + ", would " + (PatchCounters()[kRovWould] - would)
                + ", queued " + (PatchCounters()[kRovQueued] - queued) + (why != 0 ? " (" + kRovWhy[why] + ")" : "");
        }

        // ReactOverlayCheck run: the rules with controls. result: the counters and the last line.
        public static object ReactOverlayCheck(string what)
        {
            if (what == "result") return RovCounters();
            if (what != null && what.StartsWith("force:")) return RovForce(what.Substring(6));
            if (what != "run") return "reactoverlay <run|result|force:Sim Name|Reaction>";
            bool on = ShortcutOn("reactoverlay");
            int why;
            bool fixedNow = on && ReactOverlayCore.LateKind((int)ReactionSpeed.NowOrLater, false) && ReactOverlayCore.LateKind((int)ReactionSpeed.AfterInteraction, false)
                && ReactOverlayCore.Decide(true, ReactOverlayCore.kWhyNone, true, out why) == ReactOverlayCore.kGo
                && ReactOverlayCore.PartsFree(0x1E) && ReactOverlayCore.Stale(ReactOverlayCore.StaleMs(Instantiator.kReactOverlayStaleMs), Instantiator.kReactOverlayStaleMs);
            bool controls = !ReactOverlayCore.LateKind((int)ReactionSpeed.ImmediateWithoutOverlay, false) && !ReactOverlayCore.LateKind((int)ReactionSpeed.CriticalWithRoute, false)
                && !ReactOverlayCore.LateKind((int)ReactionSpeed.NowOrLater, true) && !ReactOverlayCore.PartsFree(0x60)
                && ReactOverlayCore.Decide(true, ReactOverlayCore.kWhyRouting, true, out why) == ReactOverlayCore.kKeep
                && ReactOverlayCore.Decide(true, ReactOverlayCore.kWhyNoClip, true, out why) == ReactOverlayCore.kKeep
                && ReactOverlayCore.Decide(true, ReactOverlayCore.kWhyNone, false, out why) == ReactOverlayCore.kKeep && why == ReactOverlayCore.kWhyExempt
                && ReactOverlayCore.Decide(false, ReactOverlayCore.kWhyNone, false, out why) == ReactOverlayCore.kWould;
            return "reactoverlay check (switch " + (on ? "on" : "off") + ", reactoverlayhh " + (ShortcutOn("reactoverlayhh") ? "on" : "off")
                + "): a busy Sim reacts at once as EA's overlay, a queued reaction is dropped after " + ReactOverlayCore.StaleMs(Instantiator.kReactOverlayStaleMs) + " ms=" + fixedNow
                + "; control: full-body, run-to, watch reactions, arms busy, routing, no clip, household without reactoverlayhh kept=" + controls
                + "; verdict: " + GlVerdict(on, fixedNow, controls, sRovMissing.Length == 0) + "; " + RovCounters();
        }
    }
}
