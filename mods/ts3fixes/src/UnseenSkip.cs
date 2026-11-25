using System;
using System.Collections.Generic;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects;
using Sims3.Gameplay.Objects.Environment;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // "Skip when nobody sees it" (docs/686 §37). Default off; rules in UnseenSkipCore.cs. The parts:
    //
    //   rhskipunseen / rhskiphhunseen (ObjShare.cs, RabbitHole.WaitForEnterSlot): a Sim whose rabbit hole enter slot is
    //     held, unseen when it asks, walks to the slot now and goes in (rhshare's walk, whoever holds the slot). A door
    //     portal: the two may clip at the door at once.
    //   lineswitchpc (LineSwitch.cs): lineswitch's free-twin rule for computers, laptops and bookshelves.
    //   gymtwin (LineSwitch.cs): an NPC in a workout bench or treadmill line takes a free twin; at the first check when it
    //     was unseen as its wait began, after lineswitch's wait otherwise. A workout is never ended.
    //   bookskipunseen / bookskiphhunseen (here, Bookshelf_ReadSomething.Run): an unseen Sim that would wait in a bookshelf's
    //     line walks to a free spot beside the shelf's slot (never onto the slot in use) and runs EA's own pick-up there
    //     (ChooseBook, Read_GetBook: StandardEntry, the take animation, the carry, ReadBook pushed), with EA's effects in full.
    //   servingsharehhunseen (ObjShare.cs): servingshare counts an unseen household Sim (and unseen household Sims at the
    //     platter) as in scope.
    //   slidehang / slidehanghh (here, the waiter's per-minute line check): EA bug, see below.
    //
    // Camera rule, for all of them: decided once at the start of the wait; if the Sim or its object is on screen then, or
    // near a viewer, EA's wait runs as it is. No walk is skipped or teleported; nothing touches motives, skills or saves.
    //
    // Household rule (CLAUDE.md: household Sims never count as unseen). Each `...hhunseen` switch (rhskiphhunseen,
    // bookskiphhunseen, servingsharehhunseen) is an explicit player opt-in, default off, that lifts that rule for its own
    // case only: a household Sim that is not the selected Sim, off screen, with its object off screen and nobody else of the
    // household near (UnsFar leaves the Sim itself out of the viewers). The selected Sim never counts as unseen.
    //
    // slidehang [verified, decompiled JungleGym / JungleGymSlide / SimQueue]: JungleGymSlide's ladder line
    // (SimQueueBehindSlot, waiters time out after TimeToWaitInQueue 20 sim-min) is held by its head for the whole
    // interaction (the head leaves the queue only in InteractionInstance.Cleanup). PlayOn ends its hang-around loop after
    // RandomUtil(HangOutTimeMin 10, HangOutTimeMax 20) sim-min (ExitReason.Finished in PlayOnLoopDelegate); HangOutOn runs
    // the same DoLoop with no time test at all, so it never reads the HangOut* tuning that names it. Storm logs: Lisa Bunch
    // (HangOutOn) held the Bunch slide 70-92 sim-min while Arlo and Darlene timed out behind her, every time.
    // Fix: a waiter's per-minute check (LswExitHook) sees a head on HangOutOn that took the turn HangOutTimeMax sim-min ago
    // and adds ExitReason.Finished, the exit PlayOn ends by: the head slides down and leaves through EA's own exit. Only
    // with a Sim waiting behind it. Scope: NPC heads (slidehang), household heads on an autonomous hang-out too
    // (slidehanghh); the selected Sim keeps its hang-out.
    //
    // linewaitlog / lanewaitlog view: each wait line gets "view <start>><end>" (seen / unseen) for the Sim and its object,
    // and counters 4800-4803 split the line and rabbit hole waits by the view at their start (not capped like the log).
    //
    // Counters 4780-4803. Check: UnseenSkipCheck.
    public static partial class Fixes
    {
        const int kUnsRhNpc = 4780, kUnsRhHh = 4781, kUnsRhSeen = 4782, kUnsRhWould = 4783,
            kUnsPcSwitched = 4784, kUnsPcWould = 4785,
            kUnsBookNpc = 4786, kUnsBookHh = 4787, kUnsBookSeen = 4788, kUnsBookWould = 4789, kUnsBookNoSpot = 4790,
            kUnsGymSwitched = 4791, kUnsGymWould = 4792, kUnsGymUnseen = 4793,
            kUnsServeHh = 4794,
            kUnsSlideNpc = 4795, kUnsSlideHh = 4796, kUnsSlideWould = 4797, kUnsSlideKept = 4798,
            kUnsErrors = 4799,
            kUnsLineUnseen = 4800, kUnsLineUnseenTenths = 4801, kUnsRhUnseen = 4802, kUnsRhUnseenTenths = 4803;

        static readonly LineCapCore sUnsCap = new LineCapCore(30, 60000, 600);
        static string sUnsMissing = "", sUnsLast = "", sUnsLastError = "";

        static void ApplyUnseenSkip()
        {
            sUnsMissing = "";
            if (!Patch(typeof(Bookshelf_ReadSomething), "Run", Type.EmptyTypes, typeof(UnsBookHook), "RunHook", "RunOrig")) sUnsMissing = "Bookshelf_ReadSomething.Run";
            if (sUnsMissing.Length > 0) AppliedLog().Add("unseenskip: not found: " + sUnsMissing);
        }

        static void UnsError(Exception e)
        {
            int n = PatchCounters()[kUnsErrors]++;
            sUnsLastError = "unseenskip: error: " + e.GetType().Name + ": " + e.Message;
            if (n < 3) DiagLine(sUnsLastError);
        }

        static void UnsLine(string sw, string text)
        {
            sUnsLast = sw + ": " + text;
            if ((ShortcutOn("linewaitlog") || ShortcutOn("lineswitchlog")) && sUnsCap.Take(System.Environment.TickCount)) DiagLine(sUnsLast);
        }

        // ---- views ----

        // FarFromViewers with the Sim itself left out of the viewers (an active-household Sim is always within 80 m of
        // itself, so the plain test never passes for a household Sim). Other household Sims near still count.
        static bool UnsFar(Vector3 p, Sim self)
        {
            if (self == null || !self.IsInActiveHousehold) return FarFromViewers(p);
            long now = SimClock.CurrentTicks;
            if (now != sViewTick) RefreshViewers(now);
            float dx = p.x - sCamTarget.x, dy = p.y - sCamTarget.y, dz = p.z - sCamTarget.z;
            if (dx * dx + dy * dy + dz * dz < sReach2) return false;
            dx = p.x - sCamPos.x; dy = p.y - sCamPos.y; dz = p.z - sCamPos.z;
            if (dx * dx + dy * dy + dz * dz < sReach2) return false;
            Vector3 me = self.Position;
            const float kMin2 = kShortcutMinDistance * kShortcutMinDistance;
            for (int i = 0; i < sViewerCount; i++)
            {
                Vector3 v = sViewers[i];
                if (v.x == me.x && v.y == me.y && v.z == me.z) continue;  // the Sim itself (positions read this tick)
                dx = p.x - v.x; dy = p.y - v.y; dz = p.z - v.z;
                if (dx * dx + dy * dy + dz * dz < kMin2) return false;
            }
            return true;
        }

        // What the camera shows: the Sim and its object off screen, the Sim away from the camera and other viewers.
        // No household rule here (the log and the opt-ins use it; NPC shortcuts use UnsScopeUnseen).
        static bool UnsUnseenRaw(Sim s, IHasScriptProxy o)
        {
            if (s == null || s.HasBeenDestroyed || UcOnScreen(s)) return false;
            if (o != null && UcOnScreen(o)) return false;
            return UnsFar(s.Position, s);
        }

        // The unseen test of a skip: NPCs by TownieUnseen (standing, not in a hidden room, off screen, far from viewers);
        // household Sims (only asked for an opt-in) by the raw test, never the selected Sim. `at`: a second point that must
        // be away from viewers too (a slot), or Vector3.Invalid.
        static bool UnsScopeUnseen(Sim s, IHasScriptProxy o, Vector3 at, bool npc)
        {
            if (s == null) return false;
            if (npc)
            {
                if (!TownieUnseen(s, null)) return false;
                if (o != null && UcOnScreen(o)) return false;
                return at == Vector3.Invalid || FarFromViewers(at);
            }
            if (s == PlumbBob.SelectedActor || !UnsUnseenRaw(s, o)) return false;
            return at == Vector3.Invalid || UnsFar(at, s);
        }

        static bool UnsPlain(Sim s)
        {
            return s != null && !s.HasBeenDestroyed && s.IsHuman && !s.IsInRidingPosture && s.CarryingChildPosture == null && s.SimDescription != null && s.SimDescription.ChildOrAbove;
        }

        // linewaitlog / lanewaitlog: the view when a wait begins (only asked while one of the logs is on).
        static string UnsViewNow(Sim s, IHasScriptProxy o)
        {
            try { return UnseenSkipCore.View(UnsUnseenRaw(s, o)); }
            catch (ResetException) { throw; }
            catch (Exception e) { UnsError(e); return "?"; }
        }

        // A finished line or rabbit hole wait that began unseen (counters 4800-4803, with the log off too).
        static void UnsCountUnseenWait(bool rhDoor, float min)
        {
            int[] c = PatchCounters();
            c[rhDoor ? kUnsRhUnseen : kUnsLineUnseen]++;
            c[rhDoor ? kUnsRhUnseenTenths : kUnsLineUnseenTenths] += ShareLanesCore.Tenths(min);
        }

        // ---- rhskipunseen / rhskiphhunseen (ObjShare.cs's OjsRhBegin) ----

        // A held enter slot rhshare does not walk to (off, or the holder out of scope): an unseen Sim walks now. True = go.
        static bool UnsRhSkip(RabbitHole rh, Slot slot, Sim s, int ojsDecision, int length)
        {
            try
            {
                if (ojsDecision == ObjShareCore.kGo || ojsDecision == ObjShareCore.kStale || !UnsPlain(s)) return false;
                bool npcOn = ShortcutOn("rhskipunseen"), hhOn = ShortcutOn("rhskiphhunseen");
                bool npc = !IsPlayerSim(s), selected = s == PlumbBob.SelectedActor;
                Vector3 at = rh.GetPositionOfSlot(slot);
                bool unseen = (npc || hhOn || !selected) && UnsScopeUnseen(s, rh, at, npc);
                int d = UnseenSkipCore.Skip(npcOn, hhOn, true, npc, selected, unseen);
                int[] c = PatchCounters();
                if (d == UnseenSkipCore.kWould) { c[kUnsRhWould]++; return false; }
                if (d == UnseenSkipCore.kSeen) { c[kUnsRhSeen]++; return false; }
                if (d != UnseenSkipCore.kGo) return false;
                c[npc ? kUnsRhNpc : kUnsRhHh]++;
                UnsLine(npc ? "rhskipunseen" : "rhskiphhunseen", s.FullName + " " + ShlWho(s) + " walks to held enter slot " + slot + " of " + rh.GetType().Name + " unseen, " + length + " waiting, at " + V(s.Position));
                return true;
            }
            catch (ResetException) { throw; }
            catch (Exception e) { UnsError(e); return false; }
        }

        // ---- servingsharehhunseen (ObjShare.cs) ----

        // servingshare's scope with the household opt-in: an unseen household Sim (not the selected Sim) counts as in scope.
        static bool UnsServeScope(Sim o, bool hhOn, bool hhUnseenOn, IHasScriptProxy platter)
        {
            if (ShlScope(o, hhOn)) return true;
            if (!hhUnseenOn) return false;
            try { return UnsScopeUnseen(o, platter, Vector3.Invalid, false); }
            catch (ResetException) { throw; }
            catch (Exception e) { UnsError(e); return false; }
        }

        // ---- bookskipunseen / bookskiphhunseen ----

        // Start of Bookshelf_ReadSomething.Run: true = our run (beside the slot, no line).
        static bool UnsBookDecide(Bookshelf_ReadSomething ia)
        {
            Sim a = ia.Actor;
            Bookshelf t = ia.Target;
            if (a == null || t == null || t.Line == null || t.LotCurrent == null || !UnsPlain(a)) return false;
            List<Sim> users = t.ActorsUsingMe;
            bool busy = t.Line.MemberCount() > 0 || (users != null && users.Count > 0 && !(users.Count == 1 && users[0] == a));
            if (!busy || t.Line.FirstSim == a) return false;  // nobody ahead: EA's run has no wait
            bool npcOn = ShortcutOn("bookskipunseen"), hhOn = ShortcutOn("bookskiphhunseen");
            bool npc = !IsPlayerSim(a), selected = a == PlumbBob.SelectedActor;
            bool unseen = (npc || hhOn || !selected) && UnsScopeUnseen(a, t, Vector3.Invalid, npc);
            int d = UnseenSkipCore.Skip(npcOn, hhOn, true, npc, selected, unseen);
            int[] c = PatchCounters();
            if (d == UnseenSkipCore.kWould) c[kUnsBookWould]++;
            else if (d == UnseenSkipCore.kSeen) c[kUnsBookSeen]++;
            return d == UnseenSkipCore.kGo;
        }

        // EA's Run without the line: a walk to a free spot 0.6-1.3 m from the shelf's slot (facing the shelf), then EA's
        // own pick-up. Null = no spot found: EA's run (line and all) follows.
        static object UnsBookRun(Bookshelf_ReadSomething ia)
        {
            Sim a = ia.Actor;
            Bookshelf t = ia.Target;
            Vector3 slotPos = t.GetPositionOfSlot(Slots.Hash("Route"));
            Route r = a.CreateRoute();
            r.DoRouteFail = false;
            r.PlanToPointRadialRange(t, slotPos, 0.6f, 1.3f, RouteDistancePreference.PreferNearestToRouteDestination,
                RouteOrientationPreference.TowardsObject, t.LotCurrent.LotId, new int[] { t.RoomId });
            if (!r.PlanResult.Succeeded()) { PatchCounters()[kUnsBookNoSpot]++; return null; }
            bool npc = !IsPlayerSim(a);
            PatchCounters()[npc ? kUnsBookNpc : kUnsBookHh]++;
            UnsLine(npc ? "bookskipunseen" : "bookskiphhunseen", a.FullName + " " + ShlWho(a) + " takes a book beside " + t.GetType().Name + " (line " + t.Line.MemberCount() + ", users " + t.ActorsUsingMe.Count + ") unseen, at " + V(a.Position));
            if (!a.DoRoute(r)) return false;
            List<IGameObject> books = t.Inventory.GetTopStackItems();
            IGameObject book = t.ChooseBook(a, books);
            if (book == null)
            {
                a.AddExitReason(ExitReason.FailedToRemoveFromInventory);
                return false;
            }
            return t.Read_GetBook(a, book, ia);
        }

        // `this` is the game's interaction.
        public abstract class UnsBookHook : Bookshelf_ReadSomething
        {
            public bool RunHook()
            {
                bool go = false;
                try { go = UnsBookDecide(this); }
                catch (ResetException) { throw; }
                catch (Exception e) { UnsError(e); }
                if (go)
                {
                    object r = UnsBookRun(this);  // no catch: a reset must unwind the Sim's stack
                    if (r != null) return (bool)r;
                }
                return RunOrig();
            }
            public bool RunOrig() { return false; }  // receives Bookshelf_ReadSomething.Run's original body
        }

        // ---- slidehang / slidehanghh (LineSwitch.cs's LswExitHook, a waiter's per-minute check) ----

        sealed class UnsSlideHead { public Sim Sim; public InteractionInstance Ia; public long Granted; public bool Told; }
        static readonly Dictionary<object, UnsSlideHead> sUnsSlideHeads = new Dictionary<object, UnsSlideHead>();  // jungle gym lines by queue

        // LdsQueueHook.WaitHook after EA's wait answered true at a jungle gym: this Sim holds the ladder line now.
        static void UnsSlideGranted(SimQueue q, InteractionInstance ia, IGameObject target)
        {
            try
            {
                Sim s = ia == null ? null : ia.InstanceActor;
                JungleGym gym = target as JungleGym;
                if (s == null || gym == null || !object.ReferenceEquals(gym.SimLine, q) || !object.ReferenceEquals(q.FirstSim, s)) return;
                if (sUnsSlideHeads.Count >= 64) sUnsSlideHeads.Clear();  // lines of deleted objects leave entries behind
                UnsSlideHead h = new UnsSlideHead();
                h.Sim = s; h.Ia = ia; h.Granted = SimClock.CurrentTicks;
                sUnsSlideHeads[q] = h;
            }
            catch (ResetException) { throw; }
            catch (Exception e) { UnsError(e); }
        }

        static void UnsSlideCheck(object queue, Sim waiter)
        {
            SimQueue q = queue as SimQueue;
            UnsSlideHead h;
            if (q == null || !sUnsSlideHeads.TryGetValue(q, out h)) return;
            Sim head = q.FirstSim;
            if (head == null || head.HasBeenDestroyed || !object.ReferenceEquals(h.Sim, head)) { sUnsSlideHeads.Remove(q); return; }  // another Sim holds it now
            if (head == waiter || h.Told) return;
            InteractionInstance cur = head.CurrentInteraction;
            JungleGym gym = cur == null ? null : cur.Target as JungleGym;
            if (gym == null || !object.ReferenceEquals(cur, h.Ia) || cur.GetType().Name != "HangOutOn") return;
            float held = OjsMinutes(h.Granted);
            float cap = gym.JungleGymTuning == null ? 20f : gym.JungleGymTuning.HangOutTimeMax;
            bool npc = !IsPlayerSim(head);
            int d = UnseenSkipCore.Slide(ShortcutOn("slidehang"), ShortcutOn("slidehanghh"), true, npc, cur.Autonomous, head == PlumbBob.SelectedActor, held, cap);
            if (d == UnseenSkipCore.kWait || d == UnseenSkipCore.kNone) return;
            h.Told = true;
            int[] c = PatchCounters();
            string what = head.FullName + " " + ShlWho(head) + " on " + gym.GetType().Name + " (HangOutOn" + (cur.Autonomous ? "" : ", player's order") + ") held the ladder line "
                + held.ToString("0.0") + " sim-min (HangOutTimeMax " + cap.ToString("0") + "), waiter " + waiter.FullName + " " + ShlWho(waiter);
            if (d == UnseenSkipCore.kGo)
            {
                head.AddExitReason(ExitReason.Finished);  // PlayOn's own end of the hang-around loop; EA's slide down and exit follow
                c[npc ? kUnsSlideNpc : kUnsSlideHh]++;
                UnsLine(npc ? "slidehang" : "slidehanghh", "ends the hang-out: " + what);
            }
            else if (d == UnseenSkipCore.kWould) { c[kUnsSlideWould]++; UnsLine("slidehang", "would end the hang-out (off): " + what); }
            else { c[kUnsSlideKept]++; UnsLine("slidehang", "kept (household without slidehanghh, selected, or a player's order): " + what); }
        }

        // linewaitlog: every 2 real minutes one "unscount:" line with the counters (the native log keeps 400 lines per
        // prefix, so a storm reads its totals from the last of these, not from counting wait lines).
        static int sUnsDumpAt;
        static void UnsDumpMaybe()
        {
            int now = System.Environment.TickCount;
            if (sUnsDumpAt != 0 && now - sUnsDumpAt < 120000 && now - sUnsDumpAt >= 0) return;
            sUnsDumpAt = now;
            if (!ShortcutOn("linewaitlog")) return;
            int[] c = PatchCounters();
            DiagLine("unscount: line waits " + c[kLwWaits] + " (" + (c[kLwTenths] / 10f).ToString("0.0") + " sim-min, timed out " + c[kLwTimeouts] + ", cancelled " + c[kLwCancelled]
                + "), rhdoor waits " + c[kLwRhWaits] + " (" + (c[kLwRhTenths] / 10f).ToString("0.0") + " sim-min); rhshare walked " + c[kRhsShared] + ", kept " + c[kRhsKept] + ", would " + c[kRhsWould]
                + "; servingshare lines skipped " + c[kSvsLineSkips] + ", took together " + c[kSvsShared] + ", would " + c[kSvsWould] + "; lineswitch switched " + c[kLswSwitched] + ", would " + c[kLswWould]
                + "; " + UnsCounters(false));
        }

        // ---- check ----

        static string UnsCounters(bool withLast = true)
        {
            int[] c = PatchCounters();
            return "unseenskip: rh walked unseen NPC " + c[kUnsRhNpc] + ", household " + c[kUnsRhHh] + ", seen kept " + c[kUnsRhSeen] + ", would " + c[kUnsRhWould]
                + "; lineswitchpc switched " + c[kUnsPcSwitched] + ", would " + c[kUnsPcWould]
                + "; bookskip NPC " + c[kUnsBookNpc] + ", household " + c[kUnsBookHh] + ", seen kept " + c[kUnsBookSeen] + ", would " + c[kUnsBookWould] + ", no spot " + c[kUnsBookNoSpot]
                + "; gymtwin switched " + c[kUnsGymSwitched] + " (unseen at once " + c[kUnsGymUnseen] + "), would " + c[kUnsGymWould]
                + "; servingsharehhunseen runs " + c[kUnsServeHh]
                + "; slidehang ended NPC " + c[kUnsSlideNpc] + ", household " + c[kUnsSlideHh] + ", would " + c[kUnsSlideWould] + ", kept " + c[kUnsSlideKept]
                + "; waits begun unseen: line " + c[kUnsLineUnseen] + " (" + (c[kUnsLineUnseenTenths] / 10f).ToString("0.0") + " sim-min), rhdoor " + c[kUnsRhUnseen] + " (" + (c[kUnsRhUnseenTenths] / 10f).ToString("0.0") + " sim-min)"
                + "; errors " + c[kUnsErrors] + (sUnsLastError.Length > 0 ? " (last: " + sUnsLastError + ")" : "") + "; " + GlMembers(sUnsMissing)
                + (withLast && sUnsLast.Length > 0 ? "; last: " + sUnsLast : "");
        }

        // UnseenSkipCheck run: the rules with controls (read-only). result: the counters and the last line.
        public static object UnseenSkipCheck(string what)
        {
            try
            {
                if (what == "result") return UnsCounters();
                if (what != "run") return "unseenskip <run|result>";
                const int Go = UnseenSkipCore.kGo;
                bool on = ShortcutOn("rhskipunseen") || ShortcutOn("bookskipunseen") || ShortcutOn("gymtwin") || ShortcutOn("lineswitchpc") || ShortcutOn("slidehang");
                bool fixedNow = on && UnseenSkipCore.Skip(true, false, true, true, false, true) == Go && UnseenSkipCore.Slide(true, false, true, true, true, false, 20f, 20f) == Go
                    && UnseenSkipCore.SwitchAfter(UnseenSkipCore.kGroupGym, true, 2f) == 0f;
                bool controls = UnseenSkipCore.Skip(true, true, true, true, false, false) == UnseenSkipCore.kSeen
                    && UnseenSkipCore.Skip(true, false, true, false, false, true) == UnseenSkipCore.kWould
                    && UnseenSkipCore.Skip(true, true, true, false, true, true) == UnseenSkipCore.kExempt
                    && UnseenSkipCore.Skip(true, true, true, false, false, true) == Go
                    && UnseenSkipCore.Skip(true, true, false, true, false, true) == UnseenSkipCore.kNone
                    && UnseenSkipCore.Slide(true, true, false, true, true, false, 99f, 20f) == UnseenSkipCore.kNone
                    && UnseenSkipCore.Slide(true, true, true, true, true, false, 19f, 20f) == UnseenSkipCore.kWait
                    && UnseenSkipCore.Slide(true, false, true, false, true, false, 30f, 20f) == UnseenSkipCore.kKeep
                    && UnseenSkipCore.Slide(true, true, true, false, false, false, 30f, 20f) == UnseenSkipCore.kKeep
                    && UnseenSkipCore.Slide(true, true, true, false, true, true, 30f, 20f) == UnseenSkipCore.kKeep
                    && !UnseenSkipCore.InScope(UnseenSkipCore.kGroupGym, false, true) && UnseenSkipCore.InScope(UnseenSkipCore.kGroupPc, false, true);
                return "unseenskip check (rhskipunseen " + (ShortcutOn("rhskipunseen") ? "on" : "off") + ", rhskiphhunseen " + (ShortcutOn("rhskiphhunseen") ? "on" : "off")
                    + ", bookskipunseen " + (ShortcutOn("bookskipunseen") ? "on" : "off") + ", bookskiphhunseen " + (ShortcutOn("bookskiphhunseen") ? "on" : "off")
                    + ", lineswitchpc " + (ShortcutOn("lineswitchpc") ? "on" : "off") + ", gymtwin " + (ShortcutOn("gymtwin") ? "on" : "off")
                    + ", servingsharehhunseen " + (ShortcutOn("servingsharehhunseen") ? "on" : "off") + ", slidehang " + (ShortcutOn("slidehang") ? "on" : "off") + ", slidehanghh " + (ShortcutOn("slidehanghh") ? "on" : "off")
                    + "): unseen skips, gym at once, hang-out ended=" + fixedNow + "; control: seen, opt-in off, selected, not plain, no waiter, under the cap, household scope, gym NPC only=" + controls
                    + "; verdict: " + GlVerdict(on, fixedNow, controls, sUnsMissing.Length == 0) + "; " + UnsCounters();
            }
            catch (Exception e) { return "UnseenSkipCheck: " + e.GetType().Name + ": " + e.Message; }
        }
    }
}
