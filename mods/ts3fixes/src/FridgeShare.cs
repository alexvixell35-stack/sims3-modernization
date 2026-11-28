using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects.Appliances;
using Sims3.Gameplay.Objects.CookingObjects;
using Sims3.Gameplay.Objects.FoodObjects;
using Sims3.Gameplay.Objects.Miscellaneous;
using Sims3.Gameplay.Skills;
using Sims3.Gameplay.Socializing;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // fridgeshare (NPCs) / fridgesharehh (household and selected Sims) / fridgesharelog, docs/686 §46, default off. Rules:
    // FridgeShareCore.cs.
    //
    // [verified, decompiled Fridge / SimQueue / Fridge_Have / Fridge_EatLeftovers / Fridge_CleanOutBadFood] Fridge.RouteToOpen
    // asks Fridge.Line (WaitForTurn, CutAheadOfLowerPrioritySims, 15 sim-min) BEFORE the walk to RoutingSlot_0, and the head
    // keeps mQueue[0] until StandardExit (RemoveFromUseList -> RemoveFromQueue) or its Cleanup's RemoveFromLine. A Sim that asks
    // from across the house holds the fridge for its whole walk; the Sim standing at the fridge waits at its line spot
    // (1.2-3 m away), looks at its place once a sim-minute, then walks to the slot. Fridge_Have and Fridge_EatLeftovers then
    // refuse with RouteFailed when Target.InUse (any Sim in the use list). Fridge_CleanOutBadFood carries the trash bag to the
    // can (TrashPile.PutHeldTrashInCan) before its StandardExit: it holds the fridge and its line for that whole walk.
    //
    // What this does (all through EA's own animations, walks, state machine and side effects):
    //   - Fridge_Have (and Prepare / GetRandomSnack through base.Run) and Fridge_EatLeftovers run a copy of EA's Run whose
    //     only changes are the line, the spot and the in-use test (FrsRouteToOpen, FridgeShareCore.Ready instead of InUse):
    //     ingredients, the cooking process, skill, trait tips, "Has Made Food", commodity updates, the pushed follow-up are EA's.
    //   - A short use next to another short use: off screen at once from the spot beside the slot, on screen the Sim waits
    //     beside the slot and steps onto it with a real walk once it is clear (rule 2).
    //   - A waiter at the fridge goes before a head that is still walking over after the shared grace (rule 3), the head keeps
    //     the next place; the waiter's per-minute check is LswExitHook (LineSwitch.cs), the reorder is EA's own mQueue order.
    //   - Fridge_CleanOutBadFood: the Sim leaves the fridge's use list, line and impassable region once the bag is in its hand,
    //     before the walk to the can (the trash still goes in the can as EA's).
    // Works with: fridgeff (it decides first: an unseen grab at once wins), fridgeline (its quiet fail and retry stay inside
    // EA's WaitForTurn, which this still calls), nextplan (a claimed fridge with room for a second short user keeps EA's
    // pick factor: NpPick), facilityplan (FpBusy: a fridge with room counts as free off screen), clipwait (a Sim that could
    // not reach a spot beside the slot walks to the slot and gets clipwait's grace rule), camsafe (CsDecide decides at once
    // vs beside), servingshare (platters only, never the fridge). Never a teleport or snap; a player's queued fridge order
    // keeps its turn and its fridge.
    // Counters 5040-5059 (kCounterSlots 5060). Check: FridgeShareCheck.
    public static partial class Fixes
    {
        const int kFrsUses = 5040, kFrsAtOnce = 5041, kFrsBeside = 5042, kFrsYield = 5043, kFrsWouldYield = 5044, kFrsWouldShare = 5045,
            kFrsExempt = 5046, kFrsSideFail = 5047, kFrsWaitAfter = 5048, kFrsTimedOut = 5049, kFrsCleanRelease = 5050, kFrsErrors = 5051,
            kFrsOverlap = 5052, kFrsStepIn = 5053, kFrsCapped = 5054, kFrsPlayerKept = 5055, kFrsFromLine = 5056, kFrsContainerWait = 5057,
            kFrsGaveWayWaited = 5058, kFrsCleanOrder = 5059;

        static readonly Dictionary<Sim, Fridge> sFrsAsk = new Dictionary<Sim, Fridge>();      // in WaitForTurn from our copy
        static readonly Dictionary<Sim, Fridge> sFrsWalk = new Dictionary<Sim, Fridge>();     // walking to / standing at the fridge in our copy
        static readonly Dictionary<Sim, InteractionInstance> sFrsWalkIa = new Dictionary<Sim, InteractionInstance>();
        static readonly Dictionary<Sim, Fridge> sFrsPending = new Dictionary<Sim, Fridge>();  // let in to share, not in the use list yet
        static readonly Dictionary<Sim, int> sFrsMode = new Dictionary<Sim, int>();           // share mode granted from the line
        static readonly Dictionary<Sim, long> sFrsGrant = new Dictionary<Sim, long>();        // when the head got its turn
        static readonly Dictionary<Sim, long> sFrsSeenWalk = new Dictionary<Sim, long>();     // off arm: when a waiter first saw the head walk
        static readonly Dictionary<Sim, bool> sFrsGaveWay = new Dictionary<Sim, bool>();
        static readonly Dictionary<long, bool> sFrsTold = new Dictionary<long, bool>();
        static bool FrsTell(long key) { if (sFrsTold.ContainsKey(key)) return false; if (sFrsTold.Count > 512) sFrsTold.Clear(); sFrsTold[key] = true; return true; }
        static readonly LineCapCore sFrsCap = new LineCapCore(20, 60000, 300);
        static float sFrsLineMin = 15f;
        static FieldInfo sFrsQueueF, sFrsRecSimF, sFrsLeftRegionF, sFrsLeftAddedF, sFrsCleanRegionF;
        static Type sFrsCleanType;
        static string sFrsLast = "", sFrsLastError = "";

        static void ApplyFridgeShare()
        {
            try
            {
                FieldInfo t = typeof(Fridge).GetField("kTimeToWaitInLine", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                if (t != null) sFrsLineMin = (float)t.GetValue(null);
                sFrsQueueF = typeof(SimQueue).GetField("mQueue", BindingFlags.Instance | BindingFlags.NonPublic);
                Type rec = typeof(SimQueue).GetNestedType("QueueRecord", BindingFlags.NonPublic);
                sFrsRecSimF = rec == null ? null : rec.GetField("Sim");
                if (sFfLeftType != null)
                {
                    sFrsLeftRegionF = sFfLeftType.GetField("mImpassableRegion", BindingFlags.Instance | BindingFlags.NonPublic);
                    sFrsLeftAddedF = sFfLeftType.GetField("mbAddedToUseList", BindingFlags.Instance | BindingFlags.NonPublic);
                }
                sFrsCleanType = typeof(Fridge).Assembly.GetType("Sims3.Gameplay.Objects.Appliances.Fridge_CleanOutBadFood");  // internal sealed
                if (sFrsCleanType != null)
                {
                    sFrsCleanRegionF = sFrsCleanType.GetField("mImpassableRegion", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (sFrsCleanRegionF != null) Patch(sFrsCleanType, "Run", Type.EmptyTypes, typeof(FrsCleanHook), "RunHook", "RunOrig");
                }
                AppliedLog().Add("fridgeshare: line " + (sFrsQueueF != null && sFrsRecSimF != null ? "ok" : "MISSING") + ", leftovers fields "
                    + (sFrsLeftRegionF != null && sFrsLeftAddedF != null ? "ok" : "MISSING") + ", clean-out " + (sFrsCleanRegionF != null ? "ok" : "MISSING")
                    + ", line timeout " + sFrsLineMin.ToString("0.#") + " sim-min");
            }
            catch (Exception e) { FrsError(e); }
        }

        static void FrsError(Exception e)
        {
            int n = PatchCounters()[kFrsErrors]++;
            sFrsLastError = "fridgeshare: error: " + e.GetType().Name + ": " + e.Message;
            if (n < 3) DiagLine(sFrsLastError + " " + e.StackTrace);
        }

        static void FrsLine(string text)
        {
            sFrsLast = text;
            if (ShortcutOn("fridgesharelog") && sFrsCap.Take(System.Environment.TickCount)) DiagLine("fridgeshare: " + text);
        }

        static bool FrsHumanUser(Sim a) { return a != null && a.IsHuman && a.SimDescription != null && a.SimDescription.ChildOrAbove; }

        // Our copy runs for this Sim (switch on and in scope). Out of scope with the switch on: EA's body, counted.
        static bool FrsWanted(Sim a)
        {
            if (!ShortcutOn("fridgeshare") || !FrsHumanUser(a) || sFrsQueueF == null || sFrsRecSimF == null || sFrsLeftAddedF == null || sFrsLeftRegionF == null) return false;
            if (ShlScope(a, ShortcutOn("fridgesharehh"))) return true;
            PatchCounters()[kFrsExempt]++;
            return false;
        }

        static bool FrsScope(Sim s) { return s != null && ShlScope(s, ShortcutOn("fridgesharehh")); }

        // A short fridge use: EA's grab-and-go actions (rule 1).
        static bool FrsShortUse(InteractionInstance ii, Fridge f)
        {
            if (ii == null) return false;
            if (ii is Fridge_Have) return object.ReferenceEquals(ii.Target, f);
            string n = ii.GetType().Name;
            if (n == "Fridge_EatLeftovers") return object.ReferenceEquals(ii.Target, f);
            return n == "ServingContainer_CleanUp";  // putting leftovers away: its Target is the dish, the Sim is in the fridge's use list
        }

        static bool FrsClean(InteractionInstance ii) { return ii != null && ii.GetType().Name == "Fridge_CleanOutBadFood"; }

        // Swap the line records at i-1 and i (a quick grab before a clean-out that waits ahead of it).
        static bool FrsSwapAhead(SimQueue q, Sim w)
        {
            IList list = sFrsQueueF == null ? null : sFrsQueueF.GetValue(q) as IList;
            if (list == null) return false;
            int i = FrsIndex(list, w);
            if (i < 2) return false;  // never past the head
            object rec = list[i]; list[i] = list[i - 1]; list[i - 1] = rec;
            return true;
        }

        // Other Sims at this fridge: users and Sims let in to share, by kind of use.
        static void FrsOthers(Fridge f, Sim a, out int shortN, out int longN, out bool inScope) { FrsOthers(f, a, true, out shortN, out longN, out inScope); }

        // pending: count Sims let in to share that have not begun (false for a Sim on the slot that holds the line's turn: it
        // goes first, the Sim beside the slot waits for it; no two Sims ever wait for each other).
        static void FrsOthers(Fridge f, Sim a, bool pending, out int shortN, out int longN, out bool inScope)
        {
            shortN = 0; longN = 0; inScope = true;
            List<Sim> users = f.ActorsUsingMe;
            for (int i = 0; i < users.Count; i++)
            {
                Sim u = users[i];
                if (u == null || u == a) continue;
                if (FrsShortUse(u.CurrentInteraction, f)) shortN++; else longN++;
                if (!FrsScope(u)) inScope = false;
            }
            if (!pending) return;
            foreach (KeyValuePair<Sim, Fridge> kv in sFrsPending)
                if (kv.Key != a && object.ReferenceEquals(kv.Value, f) && !users.Contains(kv.Key)) { shortN++; if (!FrsScope(kv.Key)) inScope = false; }
            // the line's head on EA's own body (a Sim the switches leave to EA, putting leftovers away): it holds the turn, so a
            // Sim beside the slot waits for it as EA's line would (our own heads wait beside the slot for that Sim instead)
            Sim head = f.Line == null ? null : f.Line.FirstSim;
            if (head != null && head != a && !users.Contains(head) && !sFrsPending.ContainsKey(head) && !sFrsWalk.ContainsKey(head) && !sFrsAsk.ContainsKey(head))
            {
                shortN++;
                if (!FrsScope(head)) inScope = false;
            }
        }

        static int FrsUsers(Fridge f, Sim a)
        {
            int n = 0;
            foreach (Sim u in f.ActorsUsingMe) if (u != null && u != a) n++;
            return n;
        }

        // Sims in the line that do not use the fridge yet (other than a).
        static int FrsWaiting(Fridge f, Sim a)
        {
            List<Sim> line = f.Line == null ? null : f.Line.SimsInQueue;
            int n = 0;
            if (line != null) foreach (Sim s in line) if (s != null && s != a && !f.IsActorUsingMe(s) && !sFrsPending.ContainsKey(s)) n++;
            return n;
        }

        static Vector3 FrsSlot(Fridge f) { return f.GetPositionOfSlot(Slot.RoutingSlot_0); }

        // The nearest other Sim to the slot (-1 = none on the lot within 2 m).
        static float FrsNearestAtSlot(Fridge f, Sim a)
        {
            Lot lot = f.LotCurrent;
            if (lot == null) return -1f;
            Vector3 slot = FrsSlot(f);
            float best = -1f;
            foreach (Sim s in lot.GetSims())
            {
                if (s == null || s == a || s.Level != f.Level) continue;
                float d = (s.Position - slot).Length();
                if (d < 2f && (best < 0f || d < best)) best = d;
            }
            return best;
        }

        static Vector3 FrsSide(Fridge f, int side)
        {
            Vector3 fwd = f.GetForwardOfSlot(Slot.RoutingSlot_0);
            fwd.y = 0f;
            float len = fwd.Length();
            if (len < 0.01f) fwd = f.ForwardVector; else fwd = fwd * (1f / len);
            Vector3 right = new Vector3(fwd.z, 0f, -fwd.x);
            return FrsSlot(f) + right * (FridgeShareCore.kSideM * side);
        }

        // ---- the line: reorder by EA's own record list ---------------------------------------------------------------------

        static int FrsIndex(IList q, Sim s)
        {
            for (int i = 0; i < q.Count; i++) if (object.ReferenceEquals(sFrsRecSimF.GetValue(q[i]), s)) return i;
            return -1;
        }

        // Put `first` at mQueue[0] and the old head right behind it. EA's waiter loop reads its new place after our check.
        static bool FrsFront(SimQueue q, Sim first)
        {
            IList list = sFrsQueueF == null ? null : sFrsQueueF.GetValue(q) as IList;
            if (list == null || list.Count < 2) return false;
            int i = FrsIndex(list, first);
            if (i <= 0) return false;
            object rec = list[i];
            list.RemoveAt(i);
            list.Insert(0, rec);
            return true;
        }

        // ---- the waiter's per-minute check (LswExitHook) ----------------------------------------------------------------------

        // true = the line was reordered (this waiter goes now).
        static bool FrsCheck(object queue, Sim w)  // `queue` is the game's SimQueue (`this` of LswExitHook)
        {
            SimQueue q = queue as SimQueue;
            if (q == null || w == null || q.MemberCount() < 2) return false;
            Fridge f;
            bool mine = sFrsAsk.TryGetValue(w, out f);
            if (!mine)
            {
                InteractionInstance cur = w.CurrentInteraction;
                f = cur == null ? null : cur.Target as Fridge;
            }
            if (f == null || !object.ReferenceEquals(f.Line, q)) return false;
            List<Sim> line = q.SimsInQueue;
            if (line == null || line.Count < 2) return false;
            int wi = line.IndexOf(w);
            if (wi >= 2 && line[wi - 1] != null && !f.IsActorUsingMe(line[wi - 1]))
            {
                Sim cl = line[wi - 1];
                InteractionInstance cIa = cl.CurrentInteraction;
                if (!FrsClean(cIa) || !object.ReferenceEquals(cIa.Target, f)) return false;
                bool onC = ShortcutOn("fridgeshare");
                InteractionInstance wIa = w.CurrentInteraction;
                int o = FridgeShareCore.CleanOrder(onC, !onC || FrsScope(cl) && FrsScope(w), !cIa.Autonomous, FrsShortUse(wIa, f), wIa != null && !wIa.Autonomous);
                if (o == FridgeShareCore.kGo && FrsSwapAhead(q, w))
                {
                    PatchCounters()[kFrsCleanOrder]++;
                    FrsLine(w.FullName + " " + ShlWho(w) + " takes a quick grab before " + cl.FullName + "'s clean-out in the " + f.GetType().Name + " line");
                }
                else if (o == FridgeShareCore.kWould && FrsTell(((long)w.ObjectId.Value) ^ ((long)cl.ObjectId.Value << 2))) PatchCounters()[kFrsWouldYield]++;
                return false;  // EA's loop goes on: its place is still behind the head
            }
            if (line[1] != w) return false;
            Sim head = line[0];
            if (head == null || head == w) return false;
            int[] c = PatchCounters();
            bool on = ShortcutOn("fridgeshare");
            Vector3 slot = FrsSlot(f);
            float wDist = (w.Position - slot).Length();
            if (f.IsActorUsingMe(head))
            {
                // rule 2 for a Sim already in the line: only our copy can stand beside the slot
                int sN, lN; bool othersIn;
                FrsOthers(f, w, out sN, out lN, out othersIn);
                if (!mine)
                {
                    // off arm (compare mode): a short use in the line behind a short user
                    if (on || !FrsHumanUser(w) || !FrsShortUse(w.CurrentInteraction, f)) return false;
                    if (FridgeShareCore.Share(false, true, true, sN, lN, 0, false) == FridgeShareCore.kWould  // compare mode: as if both switches were on
                        && FrsTell(((long)w.ObjectId.Value) ^ ((long)head.ObjectId.Value << 1))) c[kFrsWouldShare]++;
                    return false;
                }
                bool unseen = sN > 0 && lN == 0 && FfUnseen(f, w, slot, FrsSide(f, 1));
                int d = FridgeShareCore.Share(on, FrsScope(w), othersIn, sN, lN, 0, unseen);
                if (d == FridgeShareCore.kAtOnce || d == FridgeShareCore.kBeside)
                {
                    if (!FrsFront(q, w)) return false;
                    sFrsPending[w] = f; sFrsMode[w] = d;
                    c[d == FridgeShareCore.kAtOnce ? kFrsAtOnce : kFrsBeside]++; c[kFrsFromLine]++;
                    FrsLine(w.FullName + " " + ShlWho(w) + " shares the " + f.GetType().Name + " with " + head.FullName + " " + (d == FridgeShareCore.kAtOnce ? "at once (unseen)" : "from beside the slot") + ", from the line");
                    return true;
                }
                return false;
            }
            // rule 3: the head walks over
            InteractionInstance hIa;
            Fridge hf;
            bool headMine = sFrsWalk.TryGetValue(head, out hf) && object.ReferenceEquals(hf, f) && sFrsWalkIa.TryGetValue(head, out hIa);
            if (on && !headMine) return false;  // EA's own fridge action at the head: not ours to move
            if (!headMine) hIa = head.CurrentInteraction;
            else sFrsWalkIa.TryGetValue(head, out hIa);
            bool walks = head.IsRouting && hIa != null && (FrsShortUse(hIa, f) || FrsClean(hIa));
            float hDist = (head.Position - slot).Length();
            long since;
            if (headMine && sFrsGrant.TryGetValue(head, out since)) { }
            else
            {
                if (!walks) { sFrsSeenWalk.Remove(head); return false; }
                if (!sFrsSeenWalk.TryGetValue(head, out since)) { if (sFrsSeenWalk.Count > 64) sFrsSeenWalk.Clear(); since = SimClock.CurrentTicks; sFrsSeenWalk[head] = since; }
            }
            bool player = hIa != null && !hIa.Autonomous;
            int y = FridgeShareCore.Yield(on && headMine, !on || FrsScope(head) && FrsScope(w), walks, player, sFrsGaveWay.ContainsKey(head), hDist, OjsMinutes(since),
                true, wDist, w.IsRouting, Instantiator.kClipGraceMinutes);
            if (y == FridgeShareCore.kNone) return false;
            long key = ((long)head.ObjectId.Value) ^ (since << 1);
            bool first = FrsTell(key);
            string what = head.FullName + " " + ShlWho(head) + " (" + hIa.GetType().Name + ", " + hDist.ToString("0.0") + " m from the slot after " + OjsMinutes(since).ToString("0.0")
                + " sim-min) and " + w.FullName + " " + ShlWho(w) + " at " + wDist.ToString("0.0") + " m, " + f.GetType().Name;
            if (y == FridgeShareCore.kGo)
            {
                if (!FrsFront(q, w)) return false;
                sFrsGaveWay[head] = true;
                c[kFrsYield]++;
                FrsLine("head gives way: " + what);
                return true;
            }
            if (!first) return false;
            if (y == FridgeShareCore.kWould) { c[kFrsWouldYield]++; FrsLine("would give way (off): " + what); }
            else if (y == FridgeShareCore.kKept) { c[kFrsPlayerKept]++; FrsLine("kept, the player's order: " + what); }
            else c[kFrsExempt]++;
            return false;
        }

        // ---- our copy's line, walk and wait ------------------------------------------------------------------------------

        static bool FrsRouteSlot(InteractionInstance ii, Fridge f, bool playRouteFail)
        {
            Sim a = ii.InstanceActor;
            Route route = a.CreateRoute();
            route.PlanToSlot(f, Slot.RoutingSlot_0);
            if (!route.PlanResult.Succeeded())
            {
                if (playRouteFail) a.PlayRouteFailure();
                return false;
            }
            route.DoRouteFail = false;  // a walk that ends short (another Sim on the slot) is answered by the caller: beside the slot
            return a.DoRoute(route);
        }

        // 1 = beside the slot, facing the fridge; 0 = no spot (walk to the slot instead); -1 = the walk ended on an exit reason.
        static int FrsRouteSide(Sim a, Fridge f)
        {
            Vector3 r = FrsSide(f, 1), l = FrsSide(f, -1);
            int s0 = FridgeShareCore.FirstSide((a.Position - r).Length(), (a.Position - l).Length());
            for (int k = 0; k < 2; k++)
            {
                Vector3 p = k == 0 ? (s0 > 0 ? r : l) : (s0 > 0 ? l : r);
                Route route = a.CreateRoute();
                route.PlanToPoint(p);
                if (!route.PlanResult.Succeeded()) continue;
                route.DoRouteFail = false;
                bool ok = a.DoRoute(route);
                if (a.HasExitReason(ExitReason.Default)) return -1;
                if (!ok || (a.Position - p).Length() > FridgeShareCore.kSideArriveM) continue;
                a.RouteTurnToFace(f.Position);
                return 1;
            }
            return 0;
        }

        // Fridge.RouteToOpen for our copy: the line (or a share without it), the walk, then the wait until this Sim may use the
        // fridge (FridgeShareCore.Ready). atSide: the use is played from beside the slot (no impassable region then).
        static bool FrsRouteToOpen(InteractionInstance ii, Fridge f, bool playRouteFail, bool mineShort, out bool atSide)
        {
            atSide = false;
            Sim a = ii.InstanceActor;
            int[] c = PatchCounters();
            c[kFrsUses]++;
            int mode = FridgeShareCore.kNone;
            int sN, lN; bool othersIn;
            FrsOthers(f, a, out sN, out lN, out othersIn);
            if (mineShort && sN + lN > 0 && FrsUsers(f, a) > 0)  // a share only next to a Sim really at it (a Sim let in that waits beside the slot is no user)
            {
                int waiting = FrsWaiting(f, a);
                bool unseen = sN > 0 && lN == 0 && waiting == 0 && FfUnseen(f, a, FrsSlot(f), FrsSide(f, 1));
                mode = FridgeShareCore.Share(true, true, othersIn, sN, lN, waiting, unseen);
                if (mode == FridgeShareCore.kCapped) c[kFrsCapped]++;
                else if (mode == FridgeShareCore.kExempt) c[kFrsExempt]++;
                if (mode == FridgeShareCore.kAtOnce || mode == FridgeShareCore.kBeside)
                {
                    sFrsPending[a] = f;
                    c[mode == FridgeShareCore.kAtOnce ? kFrsAtOnce : kFrsBeside]++;
                    FrsLine(a.FullName + " " + ShlWho(a) + " shares the " + f.GetType().Name + " (users " + f.ActorsUsingMe.Count + ") " + (mode == FridgeShareCore.kAtOnce ? "at once (unseen)" : "from beside the slot"));
                }
            }
            if (mode != FridgeShareCore.kAtOnce && mode != FridgeShareCore.kBeside)
            {
                if (sFrsAsk.Count > 64) sFrsAsk.Clear();
                sFrsAsk[a] = f;
                bool got;
                try { got = f.Line.WaitForTurn(ii, SimQueue.WaitBehavior.CutAheadOfLowerPrioritySims, ExitReason.Default, sFrsLineMin); }
                finally { sFrsAsk.Remove(a); }
                if (!sFrsMode.TryGetValue(a, out mode)) mode = FridgeShareCore.kNone;
                sFrsMode.Remove(a);
                if (!got) return false;
                sFrsGrant[a] = SimClock.CurrentTicks;
            }
            if (sFrsWalk.Count > 64) { sFrsWalk.Clear(); sFrsWalkIa.Clear(); }
            sFrsWalk[a] = f; sFrsWalkIa[a] = ii;
            try
            {
                bool atOnce = mode == FridgeShareCore.kAtOnce;
                bool side = mode == FridgeShareCore.kAtOnce || mode == FridgeShareCore.kBeside || (FrsUsers(f, a) > 0 && !FridgeShareCore.SlotClear(FrsNearestAtSlot(f, a)));  // a stander alone is EA's walk's job
                if (side)
                {
                    int r = FrsRouteSide(a, f);
                    if (r < 0) return false;
                    if (r == 0) { side = false; atOnce = false; c[kFrsSideFail]++; }
                }
                if (!side && !FrsRouteSlot(ii, f, false))
                {
                    // the slot is taken (a Sim went first, a user stands on it): wait beside it instead, then step in
                    if (a.HasExitReason(ExitReason.Default) || FrsRouteSide(a, f) != 1)
                    {
                        if (playRouteFail && !a.HasExitReason(ExitReason.Default)) a.PlayRouteFailure();  // one balloon, as EA's failed walk
                        return false;
                    }
                    side = true; atOnce = false;
                }
                float deadline = SimClock.ElapsedTime(TimeUnit.Minutes) + sFrsLineMin;
                bool waited = false, ready = false;
                for (int round = 0; round < 400; round++)
                {
                    FrsOthers(f, a, side, out sN, out lN, out othersIn);
                    int d = FridgeShareCore.Ready(side, atOnce, sN, lN);
                    if (d == FridgeShareCore.kReady) { ready = true; break; }
                    if (d == FridgeShareCore.kStepIn)
                    {
                        if (!FrsRouteSlot(ii, f, false))
                        {
                            if (a.HasExitReason(ExitReason.Default)) return false;
                            if (FrsUsers(f, a) == 0)
                            {
                                // nobody uses it and EA's walk to the slot failed: EA's own outcome (one balloon), no wait that holds the line
                                if (playRouteFail) a.PlayRouteFailure();
                                return false;
                            }
                            if (a.WaitForExitReason(0.25f, ExitReason.Default)) return false;  // somebody stepped on it first: stay beside
                            if (SimClock.ElapsedTime(TimeUnit.Minutes) > deadline) return FrsTimedOut(a, f, playRouteFail);
                            FrsRouteSide(a, f);  // back beside the slot if the walk ended elsewhere
                            continue;
                        }
                        side = false;
                        c[kFrsStepIn]++;
                        continue;
                    }
                    if (!waited) { waited = true; c[kFrsWaitAfter]++; if (sFrsGaveWay.ContainsKey(a)) c[kFrsGaveWayWaited]++; a.LoopIdle(); }
                    if (a.WaitForExitReason(0.25f, ExitReason.Default)) return false;
                    if (SimClock.ElapsedTime(TimeUnit.Minutes) > deadline) return FrsTimedOut(a, f, playRouteFail);
                }
                if (!ready) return FrsTimedOut(a, f, playRouteFail);
                atSide = side;
                return true;
            }
            finally { sFrsWalk.Remove(a); sFrsWalkIa.Remove(a); sFrsGrant.Remove(a); }
        }

        // EA's line time-out at our wait after the walk: ObjectInUse + TimedOut, one balloon (none with fridgeline's quiet fail).
        static bool FrsTimedOut(Sim a, Fridge f, bool playRouteFail)
        {
            PatchCounters()[kFrsTimedOut]++;
            a.AddExitReason(ExitReason.ObjectInUse);
            a.AddExitReason(ExitReason.TimedOut);
            if (playRouteFail && !ShortcutOn(IsPlayerSim(a) ? "fridgelinehh" : "fridgeline")) a.PlayRouteFailure();
            FrsLine(a.FullName + " " + ShlWho(a) + " timed out at the " + f.GetType().Name + " (users " + f.ActorsUsingMe.Count + ")");
            return false;
        }

        // StandardEntry of our copy: the share is a use now.
        static void FrsEntered(Fridge f, Sim a, bool atSide)
        {
            sFrsPending.Remove(a);
            int others = 0;
            foreach (Sim u in f.ActorsUsingMe) if (u != null && u != a) others++;
            if (others > 0)
            {
                PatchCounters()[kFrsOverlap]++;
                FrsLine(a.FullName + " " + ShlWho(a) + " uses the " + f.GetType().Name + (atSide ? " from beside the slot" : " on the slot") + " with " + others + " other Sim(s)");
            }
        }

        static void FrsDone(Sim a)
        {
            sFrsPending.Remove(a);
            sFrsGaveWay.Remove(a);
            sFrsMode.Remove(a);
            sFrsSeenWalk.Remove(a);
        }

        // Room for a second short user (nextplan's NpPick, facilityplan's FpBusy): the switch covers forSim, nobody else in the
        // line, at most one short user and no long one.
        static bool FrsRoom(GameObject o, Sim forSim)
        {
            Fridge f = o as Fridge;
            if (f == null || forSim == null || !ShortcutOn("fridgeshare") || !FrsScope(forSim) || !FrsHumanUser(forSim)) return false;
            try
            {
                int sN, lN; bool inScope;
                FrsOthers(f, forSim, out sN, out lN, out inScope);
                return inScope && FrsUsers(f, forSim) == 1 && FridgeShareCore.Room(sN, lN, FrsWaiting(f, forSim));
            }
            catch (ResetException) { throw; }
            catch (Exception e) { FrsError(e); return false; }
        }

        // ---- copies of EA's Run bodies ---------------------------------------------------------------------------------------

        public abstract partial class FfHaveHook : Fridge_Have
        {
            // Fridge_Have.Run; changes: FrsRouteToOpen instead of Target.RouteToOpen + the InUse refusal, no impassable region for
            // a use from beside the slot.
            bool FrsHave()
            {
                try
                {
                    if (CheckForCancelAndCleanup()) return false;
                    bool atSide;
                    if (!FrsRouteToOpen(this, Target, true, true, out atSide)) return false;
                    if (!atSide)
                    {
                        mImpassableRegion.AddMember(Actor);
                        mImpassableRegion.AddMember(Target);
                        mImpassableRegion.UpdateFootprint();
                    }
                    StandardEntry();
                    FrsEntered(Target, Actor, atSide);
                    if (Actor.SimDescription.TeenOrAbove && !ChosenRecipe.IsSnack) Actor.SkillManager.AddElement(SkillNames.Cooking);
                    bool flag = true;
                    List<Ingredient> chosen = new List<Ingredient>();
                    if (ChosenRecipe.UseUpIngredientsFrom(Actor, ref chosen, Quantity) || Actor.IsNPC)
                    {
                        Fridge.EnterStateMachine(this);
                        IRemovableFromFridgeAsInitialRecipeStep item = GlobalFunctions.CreateObjectOutOfWorld(ChosenRecipe.ObjectToCreateInFridge, ChosenRecipe.ModelCodeVersion) as IRemovableFromFridgeAsInitialRecipeStep;
                        if (item == null) return false;
                        GameObject go = item as GameObject;
                        go.AddToUseList(Actor);
                        try
                        {
                            Target.PutOnFridgeShelf(go);
                            IPartOfCookingProcess part = item as IPartOfCookingProcess;
                            if (sFfThing != null) sFfThing.SetValue(this, part);
                            part.CookingProcess = new CookingProcess(ChosenRecipe, chosen, ObjectClickedOn, Target.LotCurrent, Destination, Quantity, Repetition, MenuText, MenuPath, part, Actor);
                            item.InitializeForRecipe(ChosenRecipe, false);
                            CookingProcess.MoveToNextStep(part, Actor);
                            SetActor(item.ActorNameForFridge, go);
                            if (mbWasHaveSomething) AnimateSim("Ponder");
                            AnimateSim("Remove - " + item.ActorNameForFridge);
                            TriggerWatchCookingReactionBroadcaster();
                        }
                        catch { go.Destroy(); throw; }  // EA's form: a reset during the grab destroys the prop too
                        CarrySystem.EnterWhileHolding(Actor, item, false);
                        if (CheckForCancelAndCleanup()) return false;
                        if (Actor.HasTrait(TraitNames.NaturalCook))
                            TraitTipsManager.ShowTraitTip(13271263770231522448uL, Actor, TraitTipsManager.TraitTipCounterIndex.NaturalCook, TraitTipsManager.kNaturalCookCountOfMealsCooked);
                        if (Actor.HasTrait(TraitNames.Vegetarian))
                            TraitTipsManager.ShowTraitTip(13271263770231522928uL, Actor, TraitTipsManager.TraitTipCounterIndex.Vegetarian, TraitTipsManager.kVegetarianCountOfMealsCooked);
                        PushNextInteraction(item, go);
                        AnimateSim("Exit - Standing");
                    }
                    else flag = false;
                    StandardExit();
                    if (flag) ActiveTopic.AddToSim(Actor, "Has Made Food");
                    return flag;
                }
                finally { FrsDone(Actor); }
            }
        }

        public abstract partial class FfLeftHook : Interaction<Sim, Fridge>
        {
            ImpassableRegion FrsRegion { get { return sFrsLeftRegionF == null ? null : sFrsLeftRegionF.GetValue(this) as ImpassableRegion; } }

            // Fridge_EatLeftovers.Run with GrabAServing / GrabContainer; changes: FrsRouteToOpen, no region beside the slot, and a
            // short wait while another Sim takes from the same dish (the last serving is never taken twice).
            bool FrsLeft()
            {
                try
                {
                    if (ChosenContainer == null || !ChosenContainer.HasFoodLeft()) { Actor.AddExitReason(ExitReason.NullValueFound); return false; }
                    bool atSide;
                    if (!FrsRouteToOpen(this, Target, true, true, out atSide)) { Actor.AddExitReason(ExitReason.RouteFailed); return false; }
                    if (ChosenContainer.HasBeenDestroyed || ChosenContainer.ItemComp.InventoryParent != Target.FridgeInventory) return false;
                    if (CheckForCancelAndCleanup()) return false;
                    for (int i = 0; i < 12 && ChosenContainer.ActorsUsingMe.Count > 0; i++)
                    {
                        if (i == 0) PatchCounters()[kFrsContainerWait]++;
                        if (Actor.WaitForExitReason(0.25f, ExitReason.Default)) return false;
                    }
                    if (ChosenContainer.ActorsUsingMe.Count > 0 || ChosenContainer.HasBeenDestroyed || !ChosenContainer.HasFoodLeft()
                        || ChosenContainer.ItemComp.InventoryParent != Target.FridgeInventory) { Actor.AddExitReason(ExitReason.NullValueFound); return false; }
                    ImpassableRegion region = FrsRegion;
                    if (!atSide && region != null)
                    {
                        region.AddMember(Actor);
                        region.AddMember(Target);
                        region.UpdateFootprint();
                    }
                    StandardEntry();
                    FrsEntered(Target, Actor, atSide);
                    BeginCommodityUpdates();
                    if (Target is FridgeCheap) ChosenContainer.CookingProcess.IsLeftover = true;
                    ChosenContainer.AddToUseList(Actor);
                    sFrsLeftAddedF.SetValue(this, true);
                    if (ChosenContainer is ServingContainerGroup) FrsGrabAServing();
                    else if (ChosenContainer is ServingContainerSingle) FrsGrabContainer();
                    EndCommodityUpdates(true);
                    StandardExit();
                    return true;
                }
                finally { FrsDone(Actor); }
            }

            void FrsGrabAServing()
            {
                ServingContainerSingle single = ChosenContainer.CookingProcess.CreateSingleServingOfFood(ChosenContainer, true, true);
                single.AddToUseList(Actor);
                try
                {
                    Target.PutOnFridgeShelf(single);
                    Fridge.EnterStateMachine(this);
                    SetActor(single.ActorNameForFridge, single);
                    AnimateSim("Remove - " + single.ActorNameForFridge);
                }
                catch { single.Destroy(); throw; }  // EA's form
                (ChosenContainer as ServingContainerGroup).DecrementServings();
                ChosenContainer.RemoveFromUseList(Actor);
                if (!ChosenContainer.HasFoodLeft())
                {
                    SharedFridgeInventory shared = Target.LotCurrent.GetSharedFridgeInventory(Actor);
                    if (shared != null) { shared.Inventory.RemoveByForce(ChosenContainer); ChosenContainer.Destroy(); }
                }
                ChosenContainer = null;
                CarrySystem.EnterWhileHolding(Actor, single, false);
                if (PushEat) Actor.InteractionQueue.PushAsContinuation(EatHeldFood.Singleton, single, false);
                AnimateSim("Exit - Standing");
            }

            void FrsGrabContainer()
            {
                SharedFridgeInventory shared = Target.LotCurrent.GetSharedFridgeInventory(Actor);
                if (shared == null || !shared.Inventory.TryToRemove(ChosenContainer)) return;
                ServingContainerSingle single = ChosenContainer as ServingContainerSingle;
                ChosenContainer.AddToWorld();
                try
                {
                    Target.PutOnFridgeShelf(single);
                    Fridge.EnterStateMachine(this);
                    SetActor(single.ActorNameForFridge, single);
                    AnimateSim("Remove - " + single.ActorNameForFridge);
                }
                catch { single.Destroy(); throw; }  // EA's form
                ChosenContainer.RemoveFromUseList(Actor);
                ChosenContainer = null;
                CarrySystem.EnterWhileHolding(Actor, single, false);
                if (PushEat) Actor.InteractionQueue.PushAsContinuation(EatHeldFood.Singleton, single, false);
                AnimateSim("Exit - Standing");
            }
        }

        // Fridge_CleanOutBadFood.Run (internal sealed, by name); change: with the bag in hand the Sim leaves the fridge's use
        // list, line and impassable region before EA's walk to the can.
        public abstract class FrsCleanHook : Interaction<Sim, Fridge>
        {
            public bool RunHook()
            {
                bool mine = false;
                try { mine = FrsWanted(Actor); }
                catch (ResetException) { throw; }
                catch (Exception e) { FrsError(e); }
                if (!mine) return RunOrig();
                try { return FrsCleanBody(); }
                finally { FrsDone(Actor); }
            }

            bool FrsCleanBody()
            {
                bool atSide;
                if (!FrsRouteToOpen(this, Target, true, false, out atSide)) return false;  // a long use: never shared, waits for every user
                ImpassableRegion region = sFrsCleanRegionF.GetValue(this) as ImpassableRegion;
                if (region != null)
                {
                    region.AddMember(Actor);
                    region.AddMember(Target);
                    region.UpdateFootprint();
                }
                StandardEntry();
                Fridge.EnterStateMachine(this);
                mCurrentStateMachine.AddOneShotScriptEventHandler(1008u, Target.PlayCleanoutSound);
                BeginCommodityUpdates();
                SharedFridgeInventory shared = Target.LotCurrent == null ? null : Target.LotCurrent.GetSharedFridgeInventory(Actor);
                if (shared == null || !shared.RemoveSpoiledFood())
                {
                    AnimateSim("Exit - Standing");
                    EndCommodityUpdates(false);
                    StandardExit();
                    return false;
                }
                TrashPile trash = (TrashPile)GlobalFunctions.CreateObject("recyclingPile", Vector3.OutOfWorld, 0, Vector3.UnitZ, null, new TrashPileInitParameters(true));
                trash.SwitchToBag();
                SetActor("TrashBag", trash);
                AnimateSim("Remove - BadFood");
                EndCommodityUpdates(true);
                AnimateSim("Exit - Standing");
                CarrySystem.EnterWhileHolding(Actor, trash);
                try
                {
                    Target.RemoveFromUseList(Actor);  // also EA's RemoveFromQueue: the next Sim gets the fridge now
                    if (region != null) { region.Cleanup(); sFrsCleanRegionF.SetValue(this, new ImpassableRegion()); }  // EA's Cleanup cleans the fresh one
                    PatchCounters()[kFrsCleanRelease]++;
                    FrsLine(Actor.FullName + " " + ShlWho(Actor) + " leaves the " + Target.GetType().Name + " with the bad food before the walk to the can");
                }
                catch (ResetException) { throw; }
                catch (Exception e) { FrsError(e); }
                TrashPile.PutHeldTrashInCan(this, false, 1);
                StandardExit();
                return true;
            }
            public bool RunOrig() { return false; }  // receives Fridge_CleanOutBadFood.Run's original body
        }

        // ---- check -------------------------------------------------------------------------------------------------------------

        static string FrsCounters()
        {
            int[] c = PatchCounters();
            return "fridgeshare (" + (ShortcutOn("fridgeshare") ? "on" : "off") + (ShortcutOn("fridgesharehh") ? " +hh" : "") + "): uses " + c[kFrsUses]
                + ", shared at once (unseen) " + c[kFrsAtOnce] + ", beside the slot " + c[kFrsBeside] + " (from the line " + c[kFrsFromLine] + "), stepped in " + c[kFrsStepIn]
                + ", overlapping uses " + c[kFrsOverlap] + ", heads gave way " + c[kFrsYield] + " (then waited " + c[kFrsGaveWayWaited] + "), waited at the fridge " + c[kFrsWaitAfter]
                + ", timed out " + c[kFrsTimedOut] + ", capped " + c[kFrsCapped] + ", no spot beside " + c[kFrsSideFail] + ", dish wait " + c[kFrsContainerWait]
                + ", clean-outs released " + c[kFrsCleanRelease] + ", quick grabs before a clean-out " + c[kFrsCleanOrder] + "; off: would give way " + c[kFrsWouldYield] + ", would share " + c[kFrsWouldShare]
                + "; kept for a player's order " + c[kFrsPlayerKept] + ", exempt " + c[kFrsExempt] + ", errors " + c[kFrsErrors]
                + (sFrsLastError.Length > 0 ? " (last: " + sFrsLastError + ")" : "") + (sFrsLast.Length > 0 ? "; last: " + sFrsLast : "");
        }

        // FridgeShareCheck: "run" = rules + members found, "result" = counters.
        public static object FridgeShareCheck(string what)
        {
            if (what == "result") return FrsCounters();
            if (what != "run") return "fridgeshare <run|result>";
            bool ok = FridgeShareCore.Share(true, true, true, 1, 0, 0, false) == FridgeShareCore.kBeside
                && FridgeShareCore.Ready(true, false, 0, 0) == FridgeShareCore.kStepIn
                && FridgeShareCore.Yield(true, true, true, false, false, 6f, 3f, true, 2f, false, 2.5f) == FridgeShareCore.kGo;
            bool members = sFrsQueueF != null && sFrsRecSimF != null && sFrsLeftRegionF != null && sFrsLeftAddedF != null && sFrsCleanRegionF != null;
            return "fridgeshare: rules " + (ok ? "ok" : "FAILED") + ", members found=" + members + ", line timeout " + sFrsLineMin.ToString("0.#") + "; " + FrsCounters();
        }
    }
}
