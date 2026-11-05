using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using Sims3.SimIFace.CAS;

namespace Ts3Fixes
{
    // ageupswap (docs/503): after an age-up model rebuild, SimBuilderTask.FinishModelLoad asks
    // Sim.SwitchToOutfitDelayed(category, callback) for the outfit category the Sim wore, ignores the result, and then
    // sleeps one tick at a time until that callback runs. SwitchToOutfitDelayed returns false without starting a swap
    // when the category is not allowed for the Sim's new age (Sim.sOutfitAgeRules; Special, Massage and AuxCareer have no
    // rule at all) or when the new age has no outfit in a special category (Singed and up). Then the callback never runs:
    // the task never sets Done, AgingManager.StartAgingQueue is never called, and the age-up queue stops at that Sim for the
    // rest of the session.
    // Fix, switch on: when EA's call returns false for a callback whose target is a SimBuilderTask, ask EA again for
    // Everyday (every age may wear it); if that fails too, run the callback now so the task finishes. Other callers (the
    // mermaid paths) keep EA's result. [2006] age-up swaps moved to Everyday, [2007] callbacks run with no swap.
    public static partial class Fixes
    {
        public class AgeUpSwapHook
        {
            public bool SwapHook(OutfitCategories category, CASUtils.SwapReadyCallback finishedCallback)
            {
                bool ok = SwapOrig(category, finishedCallback);
                if (ok) return true; // the common case: no switch lookup
                bool builder = finishedCallback != null && finishedCallback.Target is SimBuilderTask;
                int next = AgeUpSwapCore.Next(ok, ShortcutOn("ageupswap"), builder, category == OutfitCategories.Everyday);
                if (next == AgeUpSwapCore.kEa) return ok;
                Sim sim = (Sim)(object)this;
                string who = sim.SimDescription != null ? sim.SimDescription.FullName : "?";
                if (next == AgeUpSwapCore.kEveryday && SwapOrig(OutfitCategories.Everyday, finishedCallback))
                {
                    PatchCounters()[2006]++;
                    ShortcutLog("ageupswap: " + who + " age-up outfit swap to " + category + " refused by EA; Everyday instead");
                    return true;
                }
                PatchCounters()[2007]++;
                ShortcutLog("ageupswap: " + who + " age-up outfit swap to " + category + " and Everyday refused by EA; age-up task released without a swap");
                finishedCallback();
                return true;
            }
            public bool SwapOrig(OutfitCategories category, CASUtils.SwapReadyCallback finishedCallback) { return false; } // receives EA's original header
        }

        static readonly FieldInfo sSbtPost = typeof(SimBuilderTask).GetField("mPostSwapCategory", kInstAll);
        static readonly FieldInfo sSbtOutfitLoaded = typeof(SimBuilderTask).GetField("mbOutfitLoaded", kInstAll);
        static readonly FieldInfo sSbtModelLoaded = typeof(SimBuilderTask).GetField("mbModelLoaded", kInstAll);
        static readonly FieldInfo sSbtDone = typeof(SimBuilderTask).GetField("Done", kInstAll);
        static readonly FieldInfo sAgsTask = typeof(AgingState).GetField("SimBuilderTask", kInstAll); // internal field
        static SimDescription sAusSim;
        static string sAusMode, sAusLog = "";
        static SimBuilderTask sAusTask;
        static ObjectGuid sAusWatch = ObjectGuid.InvalidObjectGuid;
        static long sAusAgedAt;
        static int sAusC0; // [2006] + [2007] when "age" ran
        static readonly List<SimDescription> sAusUsed = new List<SimDescription>();

        // Agent checks (repro ageup-swap-check). The harness does not save; each age-up is real for the session.
        //   "pick special|outerwear|everyday": a toddler, child or teen in the world (other households first) that this
        //       check has not aged yet; for outerwear it must have an Outerwear outfit.
        //   "age":     EA's AgingManager.AgeTransitionWithoutCake on that Sim, with a watcher task. EA rebuilds the model (and
        //       so runs FinishModelLoad) only when the Sim visibly wears its Naked[0] outfit as the task starts (run 1,
        //       2026-11-01: four NPC age-ups skipped it). EA compares the worn outfit with Naked[0] only after it has rebuilt
        //       the outfits for the new age, many ticks later (run 2: forcing Naked until the task was seen was not enough),
        //       so the watcher puts the old Naked[0] outfit (taken before the age-up) back on whenever the Sim wears something
        //       else, until the model has loaded or the task is done. It also sets the task's post-swap category to the mode's
        //       (Special, Outerwear, Everyday) until the model has loaded, as if the Sim had worn that category.
        //   "state":   age, the task's Done/Failed/model/outfit flags and post-swap category, the Sim's outfit, [2006] [2007].
        //   "expect stuck|released|control|done": "state", but throws unless the outcome is the expected one for that arm.
        //   "unstick": sets the task's mbOutfitLoaded (what the missing callback would do), so a stuck task finishes and the
        //              age-up queue goes on.
        //   "stop":    ends the watcher.
        public static string AgeUpSwapCheck(string what)
        {
            int[] c = PatchCounters();
            if (what.StartsWith("pick "))
            {
                string mode = what.Substring(5);
                OutfitCategories cat = mode == "outerwear" ? OutfitCategories.Outerwear : mode == "everyday" ? OutfitCategories.Everyday : OutfitCategories.None;
                if (mode != "special" && cat == OutfitCategories.None) return "pick special|outerwear|everyday";
                for (int pass = 0; pass < 2; pass++) // other households first: an active-household age-up can open the modal trait dialog
                foreach (Sim s in Sims3.Gameplay.Queries.GetObjects<Sim>())
                {
                    SimDescription d = s.SimDescription;
                    if (d == null || !s.InWorld || sAusUsed.Contains(d) || d.AgingState == null || !(d.Toddler || d.Child || d.Teen)) continue;
                    if ((d.Household == Household.ActiveHousehold) != (pass == 1)) continue;
                    if (cat != OutfitCategories.None && d.GetOutfitCount(cat) == 0) continue;
                    sAusSim = d; sAusMode = mode; sAusTask = null; sAusLog = ""; sAusUsed.Add(d);
                    return d.FullName + " (" + d.Age + "), mode " + mode + ", wearing " + s.CurrentOutfitCategory + ", switch " + ShortcutOn("ageupswap");
                }
                return "no toddler, child or teen in the world left to age" + (cat != OutfitCategories.None ? " with a " + cat + " outfit" : "");
            }
            if (sAusSim == null) return "pick first";
            SimDescription sd = sAusSim;
            switch (what)
            {
                case "age":
                {
                    if (sd.CreatedSim == null) return "the Sim left the world";
                    SimOutfit naked0 = sd.GetOutfitCount(OutfitCategories.Naked) > 0 ? sd.GetOutfit(OutfitCategories.Naked, 0) : null; // the pre-age-up outfit EA compares with
                    if (naked0 != null) sd.CreatedSim.SwitchToOutfitWithoutSpin(OutfitCategories.Naked, naked0, 0);
                    AgingManager.Singleton.AgeTransitionWithoutCake(sd.CreatedSim);
                    sAusAgedAt = SimClock.CurrentTicks;
                    sAusC0 = c[2006] + c[2007];
                    OutfitCategories post = sAusMode == "outerwear" ? OutfitCategories.Outerwear : sAusMode == "everyday" ? OutfitCategories.Everyday : OutfitCategories.Special;
                    if (sAusWatch != ObjectGuid.InvalidObjectGuid && Simulator.GetProxy(sAusWatch) != null) Simulator.DestroyObject(sAusWatch);
                    sAusWatch = Simulator.AddObject(new LoopTask(delegate
                    {
                        int forced = 0;
                        bool loadSeen = false;
                        for (int i = 0; i < 37 * 60 * 24; i++) // a sim-day at most
                        {
                            SimBuilderTask t = sd.AgingState != null && sAgsTask != null ? sAgsTask.GetValue(sd.AgingState) as SimBuilderTask : null;
                            Sim cs = sd.CreatedSim;
                            SimBuilderTask tt = t ?? sAusTask;
                            bool loaded = tt != null && sSbtModelLoaded != null && (bool)sSbtModelLoaded.GetValue(tt);
                            if (naked0 != null && cs != null && !loaded && CASUtils.GetOutfitInGameObject(cs.ObjectId) != naked0.Key)
                            {
                                if (forced++ < 3) sAusLog += "put Naked[0] back on (was " + cs.CurrentOutfitCategory + ") after " + (SimClock.CurrentTicks - sAusAgedAt) + " ticks; ";
                                cs.SwitchToOutfitWithoutSpin(OutfitCategories.Naked, naked0, 0);
                            }
                            if (loaded && !loadSeen) { loadSeen = true; sAusLog += "model loaded after " + (SimClock.CurrentTicks - sAusAgedAt) + " ticks (" + forced + " put-backs); "; }
                            if (t != null && sAusTask != t) { sAusTask = t; sAusLog += "task seen after " + (SimClock.CurrentTicks - sAusAgedAt) + " ticks; "; }
                            t = sAusTask; // EA clears AgingState.SimBuilderTask when the age-up completes
                            if (t != null && sSbtPost != null && sSbtModelLoaded != null && !(bool)sSbtModelLoaded.GetValue(t)) sSbtPost.SetValue(t, post);
                            if (t != null && sSbtDone != null && (bool)sSbtDone.GetValue(t)) { sAusLog += "Done after " + (SimClock.CurrentTicks - sAusAgedAt) + " ticks (" + forced + " put-backs); "; return; }
                            Simulator.Sleep(1);
                        }
                        sAusLog += "watcher gave up after a sim-day; ";
                    }));
                    return "AgeTransitionWithoutCake called; " + AgeUpSwapCheck("state");
                }
                case "state":
                {
                    SimBuilderTask t = sAusTask;
                    string ts = t == null ? "no age-up task seen" : "task Done " + Flag(sSbtDone, t) + ", Failed " + Flag(sSbtFailed, t) + ", model loaded " + Flag(sSbtModelLoaded, t)
                        + ", outfit loaded " + Flag(sSbtOutfitLoaded, t) + ", post-swap " + (sSbtPost != null ? sSbtPost.GetValue(t) : "?");
                    Sim s = sd.CreatedSim;
                    return sd.FullName + " now " + sd.Age + "; " + ts + "; wearing " + (s != null ? s.CurrentOutfitCategory.ToString() : "-") + "; " + sAusLog
                        + "moved to Everyday " + c[2006] + ", released " + c[2007];
                }
                case "expect stuck":    // switch off, special: the bug (FinishModelLoad ran, its callback never came)
                case "expect released": // switch on, special: the task finished and the hook moved or released it
                case "expect control":  // switch on, everyday: the task finished and the hook did nothing
                case "expect done":     // switch on, outerwear: the task finished (either way)
                {
                    string st = AgeUpSwapCheck("state");
                    SimBuilderTask t = sAusTask;
                    bool done = t != null && (bool)sSbtDone.GetValue(t), model = t != null && (bool)sSbtModelLoaded.GetValue(t), outfit = t != null && (bool)sSbtOutfitLoaded.GetValue(t);
                    int hooked = c[2006] + c[2007] - sAusC0;
                    bool ok = what == "expect stuck" ? t != null && model && !outfit && !done
                        : what == "expect released" ? done && hooked >= 1
                        : what == "expect control" ? done && hooked == 0
                        : done;
                    if (!ok) throw new InvalidOperationException("ageupswap " + what + ": wrong outcome"
                        + (t != null && !model ? " (inconclusive: model loaded False, so FinishModelLoad never ran and the hang cannot happen)" : "")
                        + "; hooked since age " + hooked + "; " + st);
                    return "as expected; hooked since age " + hooked + "; " + st;
                }
                case "unstick":
                    if (sAusTask == null || sSbtOutfitLoaded == null) return "no age-up task";
                    sSbtOutfitLoaded.SetValue(sAusTask, true);
                    return "mbOutfitLoaded set; " + AgeUpSwapCheck("state");
                case "stop":
                    if (sAusWatch != ObjectGuid.InvalidObjectGuid && Simulator.GetProxy(sAusWatch) != null) Simulator.DestroyObject(sAusWatch);
                    sAusWatch = ObjectGuid.InvalidObjectGuid;
                    sAusSim = null;
                    return "stopped";
                default: return "unknown check " + what + " (pick special|outerwear|everyday, age, state, unstick, stop)";
            }
        }

        static string Flag(FieldInfo f, object o) { return f == null ? "?" : f.GetValue(o).ToString(); }
    }
}
