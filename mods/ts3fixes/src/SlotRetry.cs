using System;
using System.Collections.Generic;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.ObjectComponents;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // slotretry (docs/686 §24, default off). A failed walk to one of an object's slots or seats ends the action with a
    // shrug.
    //
    // [verified, decompiled] Two game routes take a list and accept any member of it:
    //   RoutingComponent.RouteToSlot(obj, Slot[], preferred, out index, ...): every Slot[] overload of Sim.RouteToSlot and
    //     SimRoutingComponent.RouteToSlotList / RouteToSlotListAndCheckInUse ends here; one plan to all slots, DoRoute,
    //     the chosen index from the plan's slot.
    //   SimRoutingComponent.RouteToUnusedPart(parts, preferred, ...): seats (SittableComponent / MultiSeatObject
    //     RouteToForSitting, nearby seats of a seating group included), beds, sauna, trampoline; one plan to the routing
    //     slots of every free part, DoRoute; EA loops (5) only when the walk reached a part someone took meanwhile.
    // A failed plan in DoRoute plays the route-fail behaviour (balloon, animation) and adds ExitReason.RouteFailed.
    // Single-slot routes (RouteToSlot(obj, slot), RouteToSlotAndCheckInUse: sink, fridge, TV slot) need that one slot
    // and are not touched; neither are cones, jigs (pairsnap) or other objects than the caller's.
    // Fix: the game's try runs with the Sim's route-fail behaviour off (PlayRouteFailFrequency, EA's own pattern). When
    // it fails with no other exit reason, the object stands, no other Sim uses it (RouteToSlot; parts in use are never
    // planned to) and a free slot or part is left: up to 2 retries, the slot or part the failed walk led to dropped when
    // the Sim moved, else after 15 ticks from the same spot. The last retry has the Sim's own route-fail behaviour, so
    // a case nothing helps ends as in the game.
    // Scope: one switch for every human Sim (the result is a real walk). Off = compare mode: counted, nothing changed.
    // slotretrylog writes one line per case. Counters 4602-4609. Check: SlotRetryCheck. Swaps: RoutingComponent.RouteToSlot
    // (Slot[] core overload), SimRoutingComponent.RouteToUnusedPart (both with a stub).
    public static partial class Fixes
    {
        const int kSlrCases = 4602, kSlrOk1 = 4603, kSlrOk2 = 4604, kSlrFailed = 4605, kSlrWould = 4606, kSlrKept = 4607, kSlrErrors = 4608, kSlrParts = 4609;

        static readonly LineCapCore sSlrCap = new LineCapCore(20, 60000, 300);
        static readonly string[] kSlrWhy = { "", "another exit reason", "object gone", "in use by another Sim", "no free slot", "not a human" };
        static readonly int[] sSlrWhy = new int[6];
        static string sSlrLast = "", sSlrLastError = "", sSlrMissing = "";
        static bool sSlrBusy;

        static void ApplySlotRetry()
        {
            sSlrMissing = "";
            if (!Patch(typeof(RoutingComponent), "RouteToSlot", new Type[] { typeof(IGameObject), typeof(Slot[]), typeof(Slot), typeof(int).MakeByRefType(), typeof(bool), typeof(ObjectGuid) },
                typeof(SlrSlotsHook), "SlotsHook", "SlotsOrig")) sSlrMissing += "RoutingComponent.RouteToSlot ";
            if (!Patch(typeof(SimRoutingComponent), "RouteToUnusedPart", new Type[] { typeof(List<PartData>), typeof(PartData), typeof(bool), typeof(Slot).MakeByRefType(), typeof(PartData).MakeByRefType(), typeof(bool).MakeByRefType() },
                typeof(SlrPartsHook), "PartsHook", "PartsOrig")) sSlrMissing += "SimRoutingComponent.RouteToUnusedPart";
            sSlrMissing = sSlrMissing.Trim();
            if (sSlrMissing.Length > 0) AppliedLog().Add("slotretry: not found: " + sSlrMissing);
        }

        static void SlrLine(string text)
        {
            sSlrLast = text;
            if (ShortcutOn("slotretrylog") && sSlrCap.Take(System.Environment.TickCount)) DiagLine("slotretry: " + text);
        }

        static void SlrError(Exception e)
        {
            int n = PatchCounters()[kSlrErrors]++;
            sSlrLastError = "slotretry: error: " + e.GetType().Name + ": " + e.Message;
            if (n < 3) DiagLine(sSlrLastError);
        }

        static bool SlrHuman(Sim s)
        {
            return s != null && !s.HasBeenDestroyed && s.SimDescription != null && s.SimDescription.IsHuman && !s.IsInBeingRiddenPosture;
        }

        static string SlrHead(Sim s, IGameObject obj)
        {
            InteractionInstance ii = s.CurrentInteraction;
            return BuWho(s) + " " + s.FullName + ", " + (ii != null ? ii.GetType().Name : "none") + " -> " + (obj != null ? obj.GetType().Name : "?");
        }

        // After the game's failed try. Returns kGo / kWould / kKeep; an exit reason besides RouteFailed keeps the case.
        static int SlrDecide(Sim s, IGameObject obj, bool inUseByOther, int free, string head)
        {
            bool had = s.HasExitReason(ExitReason.RouteFailed);
            s.RemoveExitReason(ExitReason.RouteFailed);
            bool other = s.HasExitReason();
            int why;
            int d = SlotRetryCore.Decide(ShortcutOn("slotretry"), SlrHuman(s), other, obj != null && !obj.HasBeenDestroyed && obj.InWorld, inUseByOther, free, out why);
            int[] c = PatchCounters();
            if (d != SlotRetryCore.kGo && had) s.AddExitReason(ExitReason.RouteFailed);
            if (d == SlotRetryCore.kWould) { c[kSlrWould]++; SlrLine("would retry: " + head); }
            else if (d == SlotRetryCore.kKeep) { c[kSlrKept]++; sSlrWhy[why]++; SlrLine("kept, " + kSlrWhy[why] + (why == SlotRetryCore.kWhyExit ? " (" + s.ExitReason + ")" : "") + ": " + head); }
            return d;
        }

        // slotretry (docs/686 §24): a failed walk to a slot list of an object is retried quietly. `this` is the game's RoutingComponent.
        public abstract class SlrSlotsHook : RoutingComponent
        {
            public bool SlotsHook(IGameObject destObj, Slot[] slotNames, Slot preferredSlot, out int chosenSlotIndex, bool ignorePetFacingTest, ObjectGuid objectToIgnoreForRoute)
            {
                SimRoutingComponent src = (object)this as SimRoutingComponent;
                Sim s = src != null ? src.OwnerSim : null;
                if (s == null || sSlrBusy || slotNames == null || slotNames.Length == 0 || destObj == null || !SlrHuman(s) || Simulator.CurrentTask != s.ObjectId)
                    return SlotsOrig(destObj, slotNames, preferredSlot, out chosenSlotIndex, ignorePetFacingTest, objectToIgnoreForRoute);
                bool on = ShortcutOn("slotretry");
                Sim.RouteFailFrequency freq = s.PlayRouteFailFrequency;
                Vector3 from = s.Position;
                if (on) s.PlayRouteFailFrequency = Sim.RouteFailFrequency.NeverPlayRouteFail;
                bool ok;
                try { ok = SlotsOrig(destObj, slotNames, preferredSlot, out chosenSlotIndex, ignorePetFacingTest, objectToIgnoreForRoute); }
                finally { if (on) s.PlayRouteFailFrequency = freq; }
                if (ok) return true;
                Slot[] left = slotNames;
                string head = "";
                try
                {
                    bool moved = BuFlat(from, s.Position) >= SlotRetryCore.kMoved;
                    if (SlotRetryCore.Drop(moved, chosenSlotIndex, slotNames.Length))
                    {
                        left = new Slot[slotNames.Length - 1];
                        for (int i = 0, j = 0; i < slotNames.Length; i++) if (i != chosenSlotIndex) left[j++] = slotNames[i];
                    }
                    GameObject go = destObj as GameObject;
                    bool other = go != null && go.InUse && !go.IsActorUsingMe(s);
                    head = SlrHead(s, destObj) + " (" + slotNames.Length + " slots" + (moved ? ", walked " + BuFlat(from, s.Position).ToString("0.0") + " m" : ", plan failed") + ")";
                    if (SlrDecide(s, destObj, other, left.Length, head) != SlotRetryCore.kGo) return false;
                    PatchCounters()[kSlrCases]++;
                    if (!moved) Simulator.Sleep(SlotRetryCore.Wait(false));
                }
                catch (ResetException) { throw; }
                catch (Exception e) { SlrError(e); return false; }
                // No catch round the walks: a reset must unwind the Sim's stack.
                for (int t = 1; t <= SlotRetryCore.kTries; t++)
                {
                    if (s.HasBeenDestroyed || s.HasExitReason(ExitReason.Default) || destObj.HasBeenDestroyed) break;
                    s.RemoveExitReason(ExitReason.RouteFailed);
                    bool quiet = SlotRetryCore.Quiet(t);
                    if (quiet) s.PlayRouteFailFrequency = Sim.RouteFailFrequency.NeverPlayRouteFail;
                    int idx;
                    sSlrBusy = true;
                    try { ok = SlotsOrig(destObj, left, preferredSlot, out idx, ignorePetFacingTest, objectToIgnoreForRoute); }
                    finally { sSlrBusy = false; if (quiet) s.PlayRouteFailFrequency = freq; }
                    chosenSlotIndex = -1;
                    if (idx >= 0 && idx < left.Length) for (int i = 0; i < slotNames.Length; i++) if (slotNames[i] == left[idx]) { chosenSlotIndex = i; break; }
                    if (ok)
                    {
                        PatchCounters()[t == 1 ? kSlrOk1 : kSlrOk2]++;
                        SlrLine("retry " + t + " worked: " + head);
                        return true;
                    }
                }
                PatchCounters()[kSlrFailed]++;
                SlrLine("retries failed, the game's answer: " + head);
                return false;
            }

            public bool SlotsOrig(IGameObject destObj, Slot[] slotNames, Slot preferredSlot, out int chosenSlotIndex, bool ignorePetFacingTest, ObjectGuid objectToIgnoreForRoute) { chosenSlotIndex = -1; return false; }  // receives the original body
        }

        // slotretry: a failed walk to the free parts (seats, beds) of an object is retried quietly. `this` is the game's SimRoutingComponent.
        public class SlrPartsHook : SimRoutingComponent
        {
            public bool PartsHook(List<PartData> parts, PartData preferredPart, bool ignoreSlotObstruction, out Slot slotRoutedTo, out PartData partRoutedTo, out bool reachedObject)
            {
                Sim s = OwnerSim;
                if (s == null || sSlrBusy || parts == null || parts.Count == 0 || !SlrHuman(s) || Simulator.CurrentTask != s.ObjectId)
                    return PartsOrig(parts, preferredPart, ignoreSlotObstruction, out slotRoutedTo, out partRoutedTo, out reachedObject);
                bool on = ShortcutOn("slotretry");
                Sim.RouteFailFrequency freq = s.PlayRouteFailFrequency;
                Vector3 from = s.Position;
                if (on) s.PlayRouteFailFrequency = Sim.RouteFailFrequency.NeverPlayRouteFail;
                bool ok;
                try { ok = PartsOrig(parts, preferredPart, ignoreSlotObstruction, out slotRoutedTo, out partRoutedTo, out reachedObject); }
                finally { if (on) s.PlayRouteFailFrequency = freq; }
                if (ok) return true;
                List<PartData> left = new List<PartData>();
                IGameObject obj = null;
                string head = "";
                try
                {
                    bool moved = BuFlat(from, s.Position) >= SlotRetryCore.kMoved;
                    // the part the failed walk led to: the nearest part with the plan's slot
                    PartData failed = null;
                    if (moved && slotRoutedTo != Slot.None)
                    {
                        float best = float.MaxValue;
                        foreach (PartData p in parts)
                        {
                            if (p == null || p.RoutingSlot == null || p.RouteTarget == null) continue;
                            if (Array.IndexOf(p.RoutingSlot, slotRoutedTo) < 0) continue;
                            float d = BuFlat(p.RouteTarget.GetPositionOfSlot(slotRoutedTo), s.Position);
                            if (d < best) { best = d; failed = p; }
                        }
                    }
                    int free = 0;
                    foreach (PartData p in parts) if (p != null && !p.InUse && p.Container != null && !p.Container.HasBeenDestroyed) free++;
                    foreach (PartData p in parts) if (p != null && (p != failed || free <= 1)) left.Add(p);
                    if (failed != null && free > 1 && !failed.InUse) free--;
                    obj = parts[0] != null ? parts[0].Container : null;
                    head = SlrHead(s, obj) + " (" + parts.Count + " parts, " + free + " free" + (moved ? ", walked " + BuFlat(from, s.Position).ToString("0.0") + " m" : ", plan failed") + ")";
                    if (SlrDecide(s, obj, false, free, head) != SlotRetryCore.kGo) return false;
                    int[] c = PatchCounters();
                    c[kSlrCases]++; c[kSlrParts]++;
                    if (!moved) Simulator.Sleep(SlotRetryCore.Wait(false));
                }
                catch (ResetException) { throw; }
                catch (Exception e) { SlrError(e); return false; }
                for (int t = 1; t <= SlotRetryCore.kTries; t++)
                {
                    if (s.HasBeenDestroyed || s.HasExitReason(ExitReason.Default) || (obj != null && obj.HasBeenDestroyed)) break;
                    s.RemoveExitReason(ExitReason.RouteFailed);
                    bool quiet = SlotRetryCore.Quiet(t);
                    if (quiet) s.PlayRouteFailFrequency = Sim.RouteFailFrequency.NeverPlayRouteFail;
                    sSlrBusy = true;
                    try { ok = PartsOrig(left, preferredPart, ignoreSlotObstruction, out slotRoutedTo, out partRoutedTo, out reachedObject); }
                    finally { sSlrBusy = false; if (quiet) s.PlayRouteFailFrequency = freq; }
                    if (ok)
                    {
                        PatchCounters()[t == 1 ? kSlrOk1 : kSlrOk2]++;
                        SlrLine("retry " + t + " worked: " + head);
                        return true;
                    }
                }
                PatchCounters()[kSlrFailed]++;
                SlrLine("retries failed, the game's answer: " + head);
                return false;
            }

            public bool PartsOrig(List<PartData> parts, PartData preferredPart, bool ignoreSlotObstruction, out Slot slotRoutedTo, out PartData partRoutedTo, out bool reachedObject)
            { slotRoutedTo = Slot.None; partRoutedTo = null; reachedObject = false; return false; }  // receives the original body
        }

        // ---- check ----

        static string SlrCounters()
        {
            int[] c = PatchCounters();
            string why = "";
            for (int i = 1; i < sSlrWhy.Length; i++) if (sSlrWhy[i] > 0) why += (why.Length > 0 ? ", " : "") + kSlrWhy[i] + " " + sSlrWhy[i];
            return "slotretry: retried cases " + c[kSlrCases] + " (of them parts " + c[kSlrParts] + "); worked on retry 1 " + c[kSlrOk1] + ", on retry 2 " + c[kSlrOk2]
                + "; retries failed (the game's answer) " + c[kSlrFailed] + "; off: would " + c[kSlrWould] + "; kept " + c[kSlrKept] + (why.Length > 0 ? " (" + why + ")" : "")
                + "; errors " + c[kSlrErrors] + (sSlrLastError.Length > 0 ? " (last: " + sSlrLastError + ")" : "") + "; " + GlMembers(sSlrMissing)
                + (sSlrLast.Length > 0 ? "; last: " + sSlrLast : "");
        }

        // SlotRetryCheck run: the rules with controls. result: the counters and the last line.
        public static object SlotRetryCheck(string what)
        {
            if (what == "result") return SlrCounters();
            if (what != "run") return "slotretry <run|result>";
            bool on = ShortcutOn("slotretry");
            int why;
            bool fixedNow = on && SlotRetryCore.Decide(true, true, false, true, false, 2, out why) == SlotRetryCore.kGo && SlotRetryCore.Drop(true, 0, 3) && SlotRetryCore.Quiet(1) && !SlotRetryCore.Quiet(2);
            bool controls = SlotRetryCore.Decide(true, false, false, true, false, 2, out why) == SlotRetryCore.kKeep && why == SlotRetryCore.kWhyNotHuman
                && SlotRetryCore.Decide(true, true, true, true, false, 2, out why) == SlotRetryCore.kKeep && why == SlotRetryCore.kWhyExit
                && SlotRetryCore.Decide(true, true, false, false, false, 2, out why) == SlotRetryCore.kKeep && why == SlotRetryCore.kWhyObject
                && SlotRetryCore.Decide(true, true, false, true, true, 2, out why) == SlotRetryCore.kKeep && why == SlotRetryCore.kWhyInUse
                && SlotRetryCore.Decide(true, true, false, true, false, 0, out why) == SlotRetryCore.kKeep && why == SlotRetryCore.kWhyNoFree
                && SlotRetryCore.Decide(false, true, false, true, false, 2, out why) == SlotRetryCore.kWould
                && !SlotRetryCore.Drop(false, 0, 3) && !SlotRetryCore.Drop(true, 0, 1) && SlotRetryCore.Wait(false) > 0;
            return "slotretry check (switch " + (on ? "on" : "off") + "): a failed walk to an object's slot list or seats is retried quietly (2 tries)=" + fixedNow
                + "; control: pets, cancelled, object gone, in use by another Sim, no free slot kept=" + controls + "; verdict: " + GlVerdict(on, fixedNow, controls, sSlrMissing.Length == 0) + "; " + SlrCounters();
        }
    }
}
