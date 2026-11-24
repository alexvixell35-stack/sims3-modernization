using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.ObjectComponents;
using Sims3.Gameplay.Objects.Beds;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // readwalk (docs/686 §22, default off). The parent of a bedtime story walks twice and stands in between.
    //
    // [verified, decompiled] Bed.Bed_ReadToSleep.Run (parent; Target = the child): once the child's head is its
    // Bed_BeReadToSleep half and the child is moving into a bed, the parent walks to 3-5 m of the bed
    // (Sim.RouteToObjectRadialRange(bed, 3, 5); only when farther than 5 m or standing on a routing slot's footprint),
    // then loops LoopIdle + Sleep(0) until the child's half runs (the child is in the bed), and only then walks to the
    // sit slot of the child's part (RouteToSlot(part.RouteTarget, part.RoutingSlotForSit)). The child's part is chosen by
    // EnterRelaxing when it reaches the bed (mEntryPart; Bed.SetContainedObject claims it).
    // Fix: as soon as the child's part is known (claimed, EnterRelaxing's entry part, or the bed's only part) the parent
    // walks straight to that part's sit slot: in place of the 3-5 m walk (RouteToObjectRadialRange hook) or from inside
    // the idle loop (LoopIdle hook, the parent's own task). EA's last RouteToSlot then finds the parent there. The game's
    // path stays when the sit slot lies within 0.7 m of the child's way (its position to its entry slot), the part has
    // no sit slot, or the plan fails (planned with DoRouteFail off: no route-fail balloon; EA then walks as before). One
    // try per story.
    // Scope: one switch for every Sim, household included (user-approved 2026-11-24). Off = compare mode: counted.
    // readwalklog writes one line per act. Counters 4590-4595. Check: ReadWalkCheck. Swaps: Sim.LoopIdle,
    // Sim.RouteToObjectRadialRange (both with a stub).
    public static partial class Fixes
    {
        const int kRwWalkFar = 4590, kRwWalkNear = 4591, kRwWould = 4592, kRwKept = 4593, kRwFailed = 4594, kRwErrors = 4595;

        static readonly Type sRwType = typeof(Bed).GetNestedType("Bed_ReadToSleep", BindingFlags.NonPublic);
        static readonly Type sRwEnterType = typeof(Bed).Assembly.GetType("Sims3.Gameplay.Objects.Beds.EnterRelaxing");
        static readonly FieldInfo sRwEntryPart = sRwEnterType != null ? sRwEnterType.GetField("mEntryPart", kInstAll) : null;
        static readonly Dictionary<ulong, bool> sRwTried = new Dictionary<ulong, bool>();  // Bed_ReadToSleep instance ids with a decision
        static readonly LineCapCore sRwCap = new LineCapCore(20, 60000, 300);
        static readonly string[] kRwWhy = { "", "", "no sit slot", "sit slot on the child's way", "" };
        static string sRwLast = "", sRwLastError = "", sRwMissing = "";

        static void ApplyReadWalk()
        {
            sRwMissing = (sRwType == null ? "Bed.Bed_ReadToSleep " : "") + (sRwEntryPart == null ? "EnterRelaxing.mEntryPart " : "");
            if (!Patch(typeof(Sim), "LoopIdle", Type.EmptyTypes, typeof(RwIdleHook), "IdleHook", "IdleOrig")) sRwMissing += "Sim.LoopIdle ";
            if (!Patch(typeof(Sim), "RouteToObjectRadialRange", new Type[] { typeof(IGameObject), typeof(float), typeof(float) }, typeof(RwRadialHook), "RadialHook", "RadialOrig"))
                sRwMissing += "Sim.RouteToObjectRadialRange";
            sRwMissing = sRwMissing.Trim();
            if (sRwMissing.Length > 0) AppliedLog().Add("readwalk: not found: " + sRwMissing);
        }

        // readwalk (docs/686 §22): the story's parent idles while the child gets into bed: walk to the sit slot now.
        public class RwIdleHook
        {
            public void IdleHook()
            {
                Sim me = (Sim)(object)this;
                if (sRwType != null && me.InteractionQueue != null)
                {
                    InteractionInstance ii = me.InteractionQueue.GetCurrentInteraction();
                    if (ii != null && ii.GetType() == sRwType) RwTry(me, ii, null);
                }
                IdleOrig();
            }

            public void IdleOrig() { }  // receives Sim.LoopIdle's original body (or the hook stacked before ours)
        }

        // readwalk: the 3-5 m walk to the child's bed becomes the walk to the sit slot when the part is known.
        public class RwRadialHook
        {
            public bool RadialHook(IGameObject destObj, float minRadius, float maxRadius)
            {
                Sim me = (Sim)(object)this;
                if (sRwType != null && destObj is Bed && me.InteractionQueue != null)
                {
                    InteractionInstance ii = me.InteractionQueue.GetCurrentInteraction();
                    if (ii != null && ii.GetType() == sRwType && RwTry(me, ii, (Bed)destObj)) return true;
                }
                return RadialOrig(destObj, minRadius, maxRadius);
            }

            public bool RadialOrig(IGameObject destObj, float minRadius, float maxRadius) { return false; }  // receives the original body
        }

        static void RwLine(string text)
        {
            sRwLast = text;
            if (ShortcutOn("readwalklog") && sRwCap.Take(System.Environment.TickCount)) DiagLine("readwalk: " + text);
        }

        // True = the parent walked to the sit slot (the radial walk is skipped). No catch round the walk: a reset must
        // unwind the Sim's stack.
        static bool RwTry(Sim me, InteractionInstance ii, Bed far)
        {
            if (sRwTried.ContainsKey(ii.Id)) return false;
            Sim child = ii.Target as Sim;
            InteractionInstance link = ii.LinkedInteractionInstance;
            if (child == null || link == null || child.InteractionQueue == null || Simulator.CurrentTask != me.ObjectId) return false;
            BedData part = null;
            GameObject target = null;
            Slot slot = Slot.None;
            int step;
            try
            {
                bool childIn = child.CurrentInteraction == link;
                if (!childIn && child.InteractionQueue.GetHeadInteraction() != link) return false;  // the child's way to a bed is not the story's yet
                InteractionInstance trans = child.InteractionQueue.TransitionInteraction;
                Bed bed = far ?? (trans != null ? trans.Target as Bed : null);
                if (bed == null || bed.PartComp == null) return false;  // the child is not on its way to a bed yet
                part = bed.PartComp.GetPartSimIsIn(child);
                if (part == null && trans != null && sRwEntryPart != null && trans.GetType() == sRwEnterType) part = sRwEntryPart.GetValue(trans) as BedData;
                if (part == null && bed.PartComp.PartDataList.Count == 1) foreach (BedData p in bed.PartComp.PartDataList.Values) part = p;
                float clear = 0f;
                if (part != null && part.RoutingSlot != null && part.RoutingSlot.Length > 0)
                {
                    slot = part.RoutingSlotForSit;
                    target = part.RouteTarget;
                    if (slot != Slot.None && target != null)
                    {
                        Vector3 sp = target.GetPositionOfSlot(slot), cp = child.Position;
                        Vector3 ep = part.ContainedSim == child ? cp : target.GetPositionOfSlot(part.RoutingSlot[0]);
                        clear = ReadWalkCore.SegDistSq(sp.x, sp.z, cp.x, cp.z, ep.x, ep.z);
                    }
                }
                step = ReadWalkCore.Step(childIn, part != null, slot != Slot.None && target != null, clear);
            }
            catch (ResetException) { throw; }
            catch (Exception e) { RwError(e); sRwTried[ii.Id] = true; return false; }
            if (step == ReadWalkCore.kWaitPart || step == ReadWalkCore.kKeepChildIn) return false;  // later, or EA walks now anyway
            if (sRwTried.Count >= 256) sRwTried.Clear();
            sRwTried[ii.Id] = true;
            int[] c = PatchCounters();
            string who = me.FullName + " to " + child.FullName + "'s bed (" + (far != null ? "instead of the 3-5 m walk" : "while the child gets in") + ")";
            if (step != ReadWalkCore.kWalk) { c[kRwKept]++; RwLine("kept, " + kRwWhy[step] + ": " + who); return false; }
            if (!ShortcutOn("readwalk")) { c[kRwWould]++; RwLine("would walk: " + who); return false; }
            Route r = me.CreateRoute();
            r.DoRouteFail = false;
            if (!r.PlanToSlot(target, slot).Succeeded()) { c[kRwFailed]++; RwLine("plan failed, the game's walk: " + who); return false; }
            if (!me.DoRoute(r)) { c[kRwFailed]++; RwLine("walk ended early, the game's walk: " + who); return false; }
            c[far != null ? kRwWalkFar : kRwWalkNear]++;
            RwLine("walked: " + who);
            return true;
        }

        static void RwError(Exception e)
        {
            int n = PatchCounters()[kRwErrors]++;
            sRwLastError = "readwalk: error: " + e.GetType().Name + ": " + e.Message;
            if (n < 3) DiagLine(sRwLastError);
        }

        // ---- check ----

        static string RwCounters()
        {
            int[] c = PatchCounters();
            return "readwalk: walks to the sit slot: instead of the 3-5 m walk " + c[kRwWalkFar] + ", while the child gets in " + c[kRwWalkNear]
                + "; off: would " + c[kRwWould] + "; kept (no sit slot, slot on the child's way) " + c[kRwKept] + "; plan or walk failed (the game's walk) " + c[kRwFailed]
                + "; errors " + c[kRwErrors] + (sRwLastError.Length > 0 ? " (last: " + sRwLastError + ")" : "") + "; " + GlMembers(sRwMissing)
                + (sRwLast.Length > 0 ? "; last: " + sRwLast : "");
        }

        // ReadWalkCheck run: the rules with controls. result: the counters and the last line.
        public static object ReadWalkCheck(string what)
        {
            if (what == "result") return RwCounters();
            if (what != "run") return "readwalk <run|result>";
            bool on = ShortcutOn("readwalk");
            bool fixedNow = ReadWalkCore.Step(false, true, true, 4f) == ReadWalkCore.kWalk && on;
            bool controls = ReadWalkCore.Step(true, true, true, 4f) == ReadWalkCore.kKeepChildIn && ReadWalkCore.Step(false, false, true, 4f) == ReadWalkCore.kWaitPart
                && ReadWalkCore.Step(false, true, false, 4f) == ReadWalkCore.kKeepNoSlot && ReadWalkCore.Step(false, true, true, 0.1f) == ReadWalkCore.kKeepOnRoute
                && ReadWalkCore.SegDistSq(0f, 1f, -1f, 0f, 1f, 0f) == 1f && ReadWalkCore.SegDistSq(3f, 0f, -1f, 0f, 1f, 0f) == 4f;
            return "readwalk check (switch " + (on ? "on" : "off") + "): the story's parent walks to the sit slot once the child's part is known=" + fixedNow
                + "; control: child in bed, part unknown, no slot, slot on the child's way kept=" + controls + "; verdict: " + GlVerdict(on, fixedNow, controls, sRwMissing.Length == 0) + "; " + RwCounters();
        }
    }
}
