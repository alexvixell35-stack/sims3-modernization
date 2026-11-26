using System;
using System.Collections.Generic;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.ObjectComponents;
using Sims3.Gameplay.Objects.Beds;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // makebedboth / makebedbothhh (docs/686 §13, default off). A player's wish: when one half of a bed cannot be
    // reached, the Sim makes it from the other side. Rules in MakeBedBothCore.cs.
    //
    // [verified, decompiled] BedMakeBed.Run walks to a routing slot of a part that is unmade with no Sim in it, plays
    // the state machine "BedMakeBed" (Enter_MakeBed_<Route0|Route1>, MakeBed, Exit_MakeBed_<...>), sets that part made
    // and, when the other part is unmade, pushes Make Bed again as a continuation. When the other part's slot cannot be
    // reached the continuation's walk fails, the bed stays half made, the definition's Test keeps passing and autonomy
    // keeps trying. A bed whose only unmade part is the closed one fails the same way on the first walk.
    // [verified, decompiled + JAZZ "BedMakeBed"] A half's made or unmade look is the bed model's last animation pose:
    // no script call sets it (BedData.BedMade only keeps the flag and the dirty alarm). The Sim's clips
    // (a2o_bed_make_start/pat/smooth/stop_x) and the bed's (..._bed) are separate tracks. Route1 mirrors both. The
    // state machine has a parameter "MakeOppositeSide" that flips the bed's mirror alone: the Sim plays at the side it
    // stands on and the bed plays the other half. The game sets it only for single beds (BedSingleData, where both
    // parts share one flag, so it is never true in Make Bed).
    //
    // Fix, at the start of BedMakeBed.Run, for a bed with two plain parts of which exactly one is unmade and free:
    //   1. A route to that part's slots exists: the game's run (double beds are still walked round).
    //   2. Otherwise, when the other part is made, free and reachable: the two parts' made flags are swapped for the
    //      run, so the game's own run walks to the other part's slot, and BedData.SetPartParameters sets
    //      "MakeOppositeSide" for that part while this Sim is in it. The game's run sets the part made at its end, so
    //      both are made, no continuation is pushed and the events fire as usual.
    //   3. The run fails (walk failed, part taken, reset): the flags are put back.
    // This covers the second half (the game's continuation is such a run) and a first make of a closed half. With the
    // bed made the definition's Test no longer passes.
    //
    // Scope: makebedboth = Sims the player does not control, makebedbothhh = household and selected Sims; each stands
    // alone. Off = compare mode: counted, nothing done. makebedbothlog writes one line per act. Counters 4462-4469.
    // Check: MakeBedBothCheck.
    public static partial class Fixes
    {
        const int kMbTries = 4462, kMbBoth = 4463, kMbFirst = 4464, kMbKeptEa = 4465, kMbKeptNoSide = 4466, kMbErrors = 4467, kMbWould = 4468, kMbPutBack = 4469;

        sealed class MbCase { public Sim Sim; public BedData Unmade, Stand; public bool Second; public string Head; }

        static readonly List<MbCase> sMbOpen = new List<MbCase>();  // runs with swapped flags (a handful at most)
        static readonly LineCapCore sMbCap = new LineCapCore(20, 60000, 300);
        static readonly string[] kMbSites = { "begin", "end", "parameter", "run" };
        static readonly int[] sMbSiteErrors = new int[4];
        static string sMbLast = "", sMbLastError = "", sMbMissing = "";
        static ulong sMbLastSim, sMbLastBed;  // the last game run that made a half

        static void ApplyMakeBedBoth()
        {
            string missing = "";
            if (!Patch(typeof(BedData), "SetPartParameters", new Type[] { typeof(StateMachineClient) }, typeof(MbParamHook), "ParamHook", "ParamOrig")) missing += " BedData.SetPartParameters";
            if (!Patch(typeof(BedSingleData), "SetPartParameters", new Type[] { typeof(StateMachineClient) }, typeof(MbSingleParamHook), "ParamHook", "ParamOrig")) missing += " BedSingleData.SetPartParameters";
            if (!Patch(typeof(BedMakeBed), "Run", Type.EmptyTypes, typeof(MbRunHook), "RunHook", "RunOrig")) missing += " BedMakeBed.Run";
            sMbMissing = missing.Trim();
            if (sMbMissing.Length > 0) AppliedLog().Add("makebedboth: not found: " + sMbMissing);
        }

        static void MbError(int site, Exception e)
        {
            PatchCounters()[kMbErrors]++;
            sMbLastError = "makebedboth: error at " + kMbSites[site] + ": " + e.GetType().Name + ": " + e.Message;
            if (sMbSiteErrors[site]++ < 3) DiagLine(sMbLastError);
        }

        static void MbLine(string text)
        {
            sMbLast = text;
            if (ShortcutOn("makebedbothlog") && sMbCap.Take(System.Environment.TickCount)) DiagLine("makebedboth: " + text);
        }

        static string MbWho(Sim s) { return s == PlumbBob.SelectedActor ? "selected" : IsPlayerSim(s) ? "household" : "npc"; }

        static string MbAt(Vector3 v) { return v.x.ToString("0.0") + "," + v.y.ToString("0.0") + "," + v.z.ToString("0.0"); }

        static bool MbPlain(BedData p) { Type t = p.GetType(); return t == typeof(BedData) || t == typeof(BedSingleData); }

        static bool MbPlan(Sim sim, Bed bed, BedData part)
        {
            if (part.RoutingSlot == null || part.RoutingSlot.Length == 0) return false;
            Route r = sim.CreateRoute();
            return r != null && r.PlanToSlot(bed, part.RoutingSlot).Succeeded();
        }

        // Made first, unmade second: a single bed's parts copy a "made" to the other part.
        static void MbSetFlags(BedData made, BedData unmade)
        {
            made.BedMade = true;
            unmade.BedMade = false;
        }

        // Start of a Make Bed run. Null = the game's run as it is. Otherwise the flags are swapped and the case is open.
        static MbCase MbBegin(Sim sim, Bed bed)
        {
            if (sim == null || bed == null || sMbMissing.Length > 0 || !bed.CanBeUsedAsBed || bed.PartComp == null) return null;
            Dictionary<PartArea, BedData> parts = bed.PartComp.PartDataList;
            if (parts == null) return null;
            BedData unmade = null, other = null;
            int unmadeFree = 0;
            bool plain = true;
            foreach (BedData p in parts.Values)
            {
                if (p == null || !MbPlain(p)) { plain = false; break; }
                if (p.ContainedSim == null && !p.BedMade) { unmadeFree++; unmade = p; }
                else other = p;
            }
            if (!MakeBedBothCore.NeedPlans(parts.Count, plain, unmadeFree) || other == null) return null;
            for (int i = 0; i < sMbOpen.Count; i++)
                if (sMbOpen[i].Stand == unmade || sMbOpen[i].Unmade == unmade) return null;  // another Sim's open case on this bed
            int[] c = PatchCounters();
            c[kMbTries]++;
            bool planUnmade = MbPlan(sim, bed, unmade);
            bool usable = other.BedMade && other.ContainedSim == null && !other.InUse && !unmade.InUse;
            bool planOther = !planUnmade && usable && MbPlan(sim, bed, other);
            int d = MakeBedBothCore.Decide(parts.Count, plain, unmadeFree, usable, planUnmade, planOther);
            if (d == MakeBedBothCore.kKeepEa) { c[kMbKeptEa]++; return null; }
            string head = MbWho(sim) + " " + sim.FullName + " -> " + bed.GetType().Name + " at " + MbAt(bed.Position) + ", " + unmade.Area + " half closed";
            if (d != MakeBedBothCore.kGo)
            {
                c[kMbKeptNoSide]++;
                MbLine(head + ": kept, " + (usable ? "the other side cannot be reached either" : "the other half is unmade or taken"));
                return null;
            }
            if (BlockedUseCore.Decide(true, IsPlayerSim(sim), ShortcutOn("makebedboth"), ShortcutOn("makebedbothhh")) != BlockedUseCore.kGo)
            {
                c[kMbWould]++;
                MbLine(head + ": would be made from the " + other.Area + " side (switch off for this Sim)");
                return null;
            }
            MbCase q = new MbCase();
            q.Sim = sim; q.Unmade = unmade; q.Stand = other; q.Head = head;
            q.Second = MakeBedBothCore.Second(sMbLastSim, sMbLastBed, sim.ObjectId.Value, bed.ObjectId.Value);
            MbSetFlags(unmade, other);
            sMbOpen.Add(q);
            return q;
        }

        // End of a run with swapped flags, however it ended.
        static void MbEnd(MbCase q, bool runOk)
        {
            sMbOpen.Remove(q);
            int[] c = PatchCounters();
            if (MakeBedBothCore.Made(runOk, q.Stand.BedMade))
            {
                c[q.Second ? kMbBoth : kMbFirst]++;
                MbLine(q.Head + ": made from the " + q.Stand.Area + " side" + (q.Second ? " (second half)" : " (first make)"));
                return;
            }
            if (q.Stand.Container != null && !q.Stand.Container.HasBeenDestroyed) MbSetFlags(q.Stand, q.Unmade);
            c[kMbPutBack]++;
            MbLine(q.Head + ": kept, the run from the " + q.Stand.Area + " side ended early");
        }

        static void MbParam(object part, StateMachineClient smc)
        {
            if (sMbOpen.Count == 0 || smc == null) return;
            try
            {
                for (int i = 0; i < sMbOpen.Count; i++)
                {
                    MbCase q = sMbOpen[i];
                    if (!MakeBedBothCore.Opposite(ReferenceEquals(q.Stand, part), q.Stand.ContainedSim == q.Sim)) continue;
                    smc.SetParameter("MakeOppositeSide", true);
                    return;
                }
            }
            catch (ResetException) { throw; }
            catch (Exception e) { MbError(2, e); }
        }

        // `this` is the game's BedData. No yield.
        public class MbParamHook
        {
            public void ParamHook(StateMachineClient smc)
            {
                ParamOrig(smc);
                MbParam(this, smc);
            }

            public void ParamOrig(StateMachineClient smc) { }  // receives BedData.SetPartParameters's original body
        }

        // `this` is the game's BedSingleData (its override sets the parameter after the base call).
        public class MbSingleParamHook
        {
            public void ParamHook(StateMachineClient smc)
            {
                ParamOrig(smc);
                MbParam(this, smc);
            }

            public void ParamOrig(StateMachineClient smc) { }  // receives BedSingleData.SetPartParameters's original body
        }

        // `this` is the game's BedMakeBed. The game's run does all the walking and animating.
        public class MbRunHook
        {
            public bool RunHook()
            {
                MbCase q = null;
                Sim sim = null;
                Bed bed = null;
                try
                {
                    InteractionInstance ia = (object)this as InteractionInstance;
                    if (ia != null) { sim = ia.InstanceActor as Sim; bed = ia.Target as Bed; }
                    q = MbBegin(sim, bed);
                }
                catch (ResetException) { throw; }
                catch (Exception e) { MbError(0, e); }
                if (q == null)
                {
                    bool made = RunOrig();
                    try
                    {
                        if (made && sim != null && bed != null) { sMbLastSim = sim.ObjectId.Value; sMbLastBed = bed.ObjectId.Value; }
                    }
                    catch (Exception e) { MbError(3, e); }
                    return made;
                }
                bool ok = false;
                try { ok = RunOrig(); return ok; }
                finally
                {
                    try { MbEnd(q, ok); }
                    catch (Exception e) { MbError(1, e); }
                }
            }

            public bool RunOrig() { return false; }  // receives BedMakeBed.Run's original body
        }

        // ---- check ----

        static string MbCounters()
        {
            int[] c = PatchCounters();
            string sites = "";
            for (int i = 0; i < sMbSiteErrors.Length; i++)
                if (sMbSiteErrors[i] > 0) sites += (sites.Length > 0 ? ", " : "") + kMbSites[i] + " " + sMbSiteErrors[i];
            return "makebedboth: runs with one unmade free half " + c[kMbTries] + "; on: second half made from the first side " + c[kMbBoth] + ", closed half made from the other side " + c[kMbFirst]
                + "; kept: the half can be reached " + c[kMbKeptEa] + ", no side to make it from " + c[kMbKeptNoSide] + ", run ended early, flags put back " + c[kMbPutBack]
                + "; off or exempt: would " + c[kMbWould] + "; open " + sMbOpen.Count + "; errors " + c[kMbErrors] + (sites.Length > 0 ? " (" + sites + "; last: " + sMbLastError + ")" : "")
                + "; " + GlMembers(sMbMissing) + (sMbLast.Length > 0 ? "; last: " + sMbLast : "");
        }

        // MakeBedBothCheck run: the rules with controls (read-only). Bug case: a double bed's unmade half has no route
        // to its slot and the other half is made, free and reachable; the bed is made from there. result: the counters
        // and the last line. lot: the beds of the selected Sim's lot with an unmade half, and which plans exist.
        public static object MakeBedBothCheck(string what)
        {
            if (what == "result") return MbCounters();
            if (what == "lot") return MbLot();
            if (what != "run") return "makebedboth <run|result|lot>";
            bool on = ShortcutOn("makebedboth"), hh = ShortcutOn("makebedbothhh");
            const int Go = MakeBedBothCore.kGo;
            bool rule = MakeBedBothCore.Decide(2, true, 1, true, false, true) == Go && MakeBedBothCore.Made(true, true) && MakeBedBothCore.Opposite(true, true)
                && MakeBedBothCore.Second(5, 9, 5, 9) && !MakeBedBothCore.Second(5, 9, 6, 9);
            bool fixedNow = rule && BlockedUseCore.Decide(true, false, on, hh) == BlockedUseCore.kGo;
            bool ctlEa = MakeBedBothCore.Decide(2, true, 1, true, true, true) == MakeBedBothCore.kKeepEa && MakeBedBothCore.Decide(2, true, 2, false, false, false) == MakeBedBothCore.kNone
                && MakeBedBothCore.Decide(2, true, 0, true, false, true) == MakeBedBothCore.kNone;
            bool ctlSide = MakeBedBothCore.Decide(2, true, 1, false, false, true) == MakeBedBothCore.kKeepNoSide && MakeBedBothCore.Decide(2, true, 1, true, false, false) == MakeBedBothCore.kKeepNoSide;
            bool ctlKind = MakeBedBothCore.Decide(2, false, 1, true, false, true) == MakeBedBothCore.kNone && MakeBedBothCore.Decide(1, true, 1, true, false, true) == MakeBedBothCore.kNone
                && !MakeBedBothCore.NeedPlans(2, true, 2) && !MakeBedBothCore.NeedPlans(3, true, 1);
            bool ctlEnd = !MakeBedBothCore.Made(false, true) && !MakeBedBothCore.Made(true, false) && !MakeBedBothCore.Opposite(true, false) && !MakeBedBothCore.Opposite(false, true);
            bool ctlScope = BlockedUseCore.Decide(true, true, true, false) == BlockedUseCore.kExempt && BlockedUseCore.Decide(true, true, false, true) == BlockedUseCore.kGo
                && BlockedUseCore.Decide(true, false, false, true) == BlockedUseCore.kWould && BlockedUseCore.Decide(true, false, false, false) == BlockedUseCore.kWould;
            bool controls = ctlEa && ctlSide && ctlKind && ctlEnd && ctlScope;
            return "makebedboth check (switch " + (on ? "on" : "off") + ", household " + (hh ? "on" : "off") + "): a closed half is made from the other side=" + fixedNow
                + "; control: a half that can be reached, both halves unmade, a made bed kept=" + ctlEa + ", other half taken or closed too kept=" + ctlSide
                + ", tents, pods, other part counts kept=" + ctlKind + ", a run that ends early puts the flags back, the parameter only for the Sim in the part=" + ctlEnd
                + ", scope per switch=" + ctlScope + "; controls ok=" + controls + "; verdict: " + GlVerdict(on, fixedNow, controls, sMbMissing.Length == 0) + "; " + MbCounters();
        }

        static string MbLot()
        {
            Sim sim = PlumbBob.SelectedActor;
            if (sim == null || sim.LotCurrent == null) return "makebedboth lot: no selected Sim on a lot";
            string s = "";
            int beds = 0, shown = 0;
            foreach (Bed bed in sim.LotCurrent.GetObjects<Bed>())
            {
                beds++;
                if (bed.PartComp == null || bed.PartComp.PartDataList == null || shown >= 20) continue;
                string row = "";
                bool any = false;
                foreach (BedData p in bed.PartComp.PartDataList.Values)
                {
                    if (p == null) continue;
                    if (!p.BedMade) any = true;
                    row += " " + p.Area + (p.BedMade ? " made" : " unmade") + (p.ContainedSim != null ? " taken" : "") + (MbPlain(p) ? "" : " (" + p.GetType().Name + ")") + " plan=" + MbPlan(sim, bed, p) + ";";
                }
                if (!any) continue;
                shown++;
                s += " | " + bed.GetType().Name + " at " + MbAt(bed.Position) + ":" + row;
            }
            return "makebedboth lot: " + sim.FullName + ", beds " + beds + ", with an unmade half " + shown + s;
        }
    }
}
