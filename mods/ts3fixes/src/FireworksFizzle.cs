using System;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects.Miscellaneous;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // fireworksfizzle (docs/533): when a firework fizzles (FireworksBase.Tuning kChanceOfLaunchFailure), EA's
    // FireworksBase.Watch.LoopDel asks every watcher's "FireworksBigBooms" state machine for a "LaunchFailure" state and
    // then adds ExitReason.Finished. The watcher's actor ("x", entered with Enter / LoopAnticipate / LoopWatch / Exit) has
    // no such state, so the request waits for the engine's timeout and throws SacsTimeoutException, and the watcher is
    // reset [verified in the decompiled game code; seen live 2026-10-25, Bridgeport Acres Summer Fest, issue #166 build
    // 23]. The launcher's own fizzle animation is another interaction. Fix: on the fizzle the hook does the rest of EA's
    // case (lastState, ExitReason.Finished) without the state request, so the watcher finishes as after a normal show.
    // Counter [2061] fizzles finished (registry fixes-2061). Default off.
    public static partial class Fixes
    {
        // Watch is a private nested class, and lastState / mCurrentState are not public in the game's assemblies
        static readonly Type sFwWatch = typeof(FireworksBase).GetNestedType("Watch", BindingFlags.Public | BindingFlags.NonPublic);
        static readonly FieldInfo sFwLastState = sFwWatch == null ? null : sFwWatch.GetField("lastState", kInstAll);
        static readonly FieldInfo sFwState = typeof(FireworksBase).GetField("mCurrentState", kInstAll);

        public class FireworksFizzleHook
        {
            public void LoopHook(StateMachineClient smc, InteractionInstance.LoopData loopData)
            {
                if (!FizzleFinish((Interaction<Sim, FireworksBase>)(object)this)) LoopOrig(smc, loopData);
            }
            public void LoopOrig(StateMachineClient smc, InteractionInstance.LoopData loopData) { } // receives FireworksBase.Watch.LoopDel's original body
        }

        // True when it finished the watch instead of letting EA's LoopDel request the LaunchFailure state.
        static bool FizzleFinish(Interaction<Sim, FireworksBase> w)
        {
            if (!ShortcutOn("fireworksfizzle") || sFwLastState == null || sFwState == null || w.Target == null
                || (FireworksBase.FireworkState)sFwState.GetValue(w.Target) != FireworksBase.FireworkState.ActiveFailure
                || (FireworksBase.FireworkState)sFwLastState.GetValue(w) == FireworksBase.FireworkState.ActiveFailure) return false;
            sFwLastState.SetValue(w, FireworksBase.FireworkState.ActiveFailure);
            w.Actor.AddExitReason(ExitReason.Finished);
            PatchCounters()[2061]++;
            return true;
        }

        // Harness check (repro fireworks-fizzle-check): a FireworksBigBooms made out of world, a Watch instance on it for
        // the active Sim (made, never run, so it has no state machine), the firework set to ActiveFailure and the watch's
        // lastState to Lit, then the hook's steps run once (FizzleFinish, else the original LoopDel body). "expect broken" (switch off) wants EA's path to reach its state
        // request: AnimateSim("LaunchFailure") throws (here NullReferenceException on the missing state machine; in play
        // the SacsTimeoutException), and no exit reason is added. "expect fixed" (switch on) wants no exception, lastState
        // ActiveFailure and ExitReason.Finished on the Sim. The exit reason the check added is removed again and the
        // firework destroyed.
        public static object FireworksFizzleCheck(string what)
        {
            if (what != "expect broken" && what != "expect fixed") return "fireworksfizzle <expect broken|expect fixed>";
            Sim sim = Sim.ActiveActor;
            if (sim == null) throw new InvalidOperationException("fireworksfizzle check: no active Sim");
            // the instance names EA sells fireworks by: the Shopping XML's FireworkBundles table, MedatorNames "name:EPx,..."
            // (FireworksBundle.ParseMedatorNameAndPV); the first one that makes a FireworksBase is used
            FireworksBase fw = null;
            string tried = "";
            XmlDbData x = XmlDbData.ReadData("Shopping");
            XmlDbTable tb = null;
            if (x != null && x.Tables != null) x.Tables.TryGetValue("FireworkBundles", out tb);
            if (tb != null)
                foreach (XmlDbRow row in tb.Rows)
                {
                    string[] names;
                    ParserFunctions.ParseCommaSeparatedString(row["MedatorNames"], out names);
                    if (names != null)
                        foreach (string nv in names)
                        {
                            if (fw != null) break;
                            string[] p = nv.Split(':');
                            ProductVersion pv;
                            if (p.Length < 2 || !ParserFunctions.TryParseEnum<ProductVersion>(p[1], out pv, ProductVersion.EP8) || !GameUtils.IsInstalled(pv)) continue;
                            tried += p[0] + " ";
                            IGameObject o = GlobalFunctions.CreateObjectOutOfWorld(p[0], pv);
                            fw = o as FireworksBase;
                            if (fw == null && o != null) o.Destroy();
                        }
                    if (fw != null) break;
                }
            if (fw == null) throw new InvalidOperationException("fireworksfizzle check: no FireworksBase from the FireworkBundles names (" + (tb == null ? "no table" : "tried " + tried) + ")");
            bool hadFinished = sim.HasExitReason(ExitReason.Finished);
            string threw = "nothing";
            bool finished, failState;
            try
            {
                if (sFwWatch == null || sFwLastState == null || sFwState == null) throw new InvalidOperationException("fireworksfizzle check: Watch, lastState or mCurrentState not found");
                InteractionDefinition def = sFwWatch.GetField("Singleton", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).GetValue(null) as InteractionDefinition;
                InteractionInstance w = def == null ? null : def.CreateInstance(fw, sim, new InteractionPriority(InteractionPriorityLevel.UserDirected), false, true);
                if (w == null || w.GetType() != sFwWatch) throw new InvalidOperationException("fireworksfizzle check: Watch.Singleton.CreateInstance made no Watch");
                sFwState.SetValue(fw, FireworksBase.FireworkState.ActiveFailure);
                sFwLastState.SetValue(w, FireworksBase.FireworkState.Lit);
                // the hook's own steps: reflection cannot invoke the swapped LoopDel on a Watch (its header names the hook's
                // class: TargetException), but the stub that received LoopDel's original header still names Watch
                try { if (!FizzleFinish((Interaction<Sim, FireworksBase>)w)) typeof(FireworksFizzleHook).GetMethod("LoopOrig").Invoke(w, new object[] { null, new InteractionInstance.LoopData() }); }
                catch (Exception e) { threw = (e.InnerException ?? e).GetType().Name; }
                finished = sim.HasExitReason(ExitReason.Finished);
                failState = (FireworksBase.FireworkState)sFwLastState.GetValue(w) == FireworksBase.FireworkState.ActiveFailure;
            }
            finally
            {
                if (!hadFinished) sim.RemoveExitReason(ExitReason.Finished);
                fw.Destroy();
            }
            bool on = ShortcutOn("fireworksfizzle");
            string line = "threw " + threw + ", Finished exit reason " + (hadFinished ? "already there" : finished ? "added" : "not added") + ", lastState ActiveFailure " + failState + " (switch " + on + ")";
            bool ok = what == "expect broken" ? !on && threw != "nothing" && (hadFinished || !finished)
                                              : on && threw == "nothing" && finished && failState;
            if (!ok) throw new InvalidOperationException("fireworksfizzle " + what + ": wrong outcome; " + line);
            return "as expected; " + line;
        }
    }
}
