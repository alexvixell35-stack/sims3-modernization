using System;
using System.Collections.Generic;
using Sims3.Gameplay.Actors;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // errortraploop (tuning kErrorTrapLoop, off by default). ScriptCore.ScriptProxy.Simulate runs mTarget.Simulate() in a
    // loop and asks OnScriptError(e) after an exception: false = OnReset + rethrow, the task ends. EA's OnScriptError is
    // ScriptErrorWindow.DisplayScriptError, which returns false. NRaas ErrorTrap ships its own ScriptCore whose
    // OnScriptError returns true whenever ErrorTrap's handler is set (ExceptionTrap.Exception), so the proxy sleeps one tick
    // and runs Simulate again from the top [verified: decompiled both ScriptCore.dll copies]. A task whose state makes it
    // throw at once (af4 2026-11-17: LiteDriver after EA's finally nulled mCar) then throws, is logged and re-run again and
    // again. Fix: when one non-Sim proxy throws the same exception at the same top frame kErrLoopRepeats times, each within
    // kErrLoopWindowMs of the last, OnScriptError answers false: EA's own outcome (the task ends). ErrorTrap's handler has
    // already run for that error (log, object reset). Sims are left to ErrorTrap (it resets them through its own task).
    // Without ErrorTrap, OnScriptError is already false and nothing changes. Counters [2778] loops ended, [2779] errors, [2784] hook calls with a re-run answer and the switch on.
    // sErrLoopTypes: the target types ended (first 32), for the harness.
    public static partial class Fixes
    {
        const int kErrLoopRepeats = 3;
        const int kErrLoopWindowMs = 5000;

        sealed class ErrLoopEntry { public string Sig; public int Count; public int Tick; }
        static readonly Dictionary<ScriptCore.ScriptProxy, ErrLoopEntry> sErrLoop = new Dictionary<ScriptCore.ScriptProxy, ErrLoopEntry>();
        public static readonly List<string> sErrLoopTypes = new List<string>();

        public class ErrorTrapLoopHook
        {
            public bool ErrHook(Exception e)
            {
                SidProxyNote((ScriptCore.ScriptProxy)(object)this, e);  // swapinvokediag (SwapInvokeDiag.cs): a TargetException that reached a task
                bool rerun = ErrOrig(e);
                if (!rerun || !ShortcutOn("errortraploop")) return rerun;
                PatchCounters()[2784]++;
                try { return !ErrLoopSeen((ScriptCore.ScriptProxy)(object)this, e); }
                catch (Exception) { PatchCounters()[2779]++; return rerun; }
            }
            public bool ErrOrig(Exception e) { return false; } // receives ScriptCore.ScriptProxy.OnScriptError's original body
        }

        // True when this error is the kErrLoopRepeats-th identical one in a row on this proxy (the task should end).
        static bool ErrLoopSeen(ScriptCore.ScriptProxy p, Exception e)
        {
            IScriptLogic t = p.Target;
            if (t == null || t is Sim || t.GetType().Name == "SimUpdate") return false;
            // same exception type and message from the same target type = the same error again (no StackTrace: TS3 builds
            // stack text natively; etl3 2026-11-17 ended no loop with a StackTrace signature)
            string sig = e.GetType().FullName + "|" + e.Message + "|" + t.GetType().FullName;
            int now = System.Environment.TickCount;
            if (sErrLoop.Count > 256) sErrLoop.Clear();
            ErrLoopEntry en;
            if (!sErrLoop.TryGetValue(p, out en)) { en = new ErrLoopEntry(); sErrLoop[p] = en; }
            if (en.Sig == sig && unchecked(now - en.Tick) < kErrLoopWindowMs) en.Count++;
            else { en.Sig = sig; en.Count = 1; }
            en.Tick = now;
            if (en.Count < kErrLoopRepeats) return false;
            sErrLoop.Remove(p);
            PatchCounters()[2778]++;
            string name = t.GetType().FullName;
            if (sErrLoopTypes.Count < 32 && !sErrLoopTypes.Contains(name)) sErrLoopTypes.Add(name);
            return true;
        }

        // Harness check (repro errortraploop-check; reflect_call_async Ts3Fixes.Fixes.ErrorTrapLoopCheck "expect broken" |
        // "expect fixed", then reflect_call Ts3Fixes.Fixes.ErrorTrapLoopResult): a task that throws the same exception on its
        // first 6 runs and returns on the 7th. With ErrorTrap: switch off = 7 runs (re-run until it stops throwing), on = 3.
        // Without ErrorTrap: 1 run either way (EA ends the task at the first error).
        // The logic runs through a ScriptProxy of its own (Load + Simulate on the check's task): a plain Task added with
        // Simulator.AddObject is run natively without ScriptProxy, so OnScriptError never sees its errors (etl4 2026-11-18:
        // ErrorTrap on, switch off, 10 runs and 0 hook calls; the native host re-ran it every tick after a throw and after a
        // normal return alike).
        sealed class ErrLoopTestLogic : IScriptLogic
        {
            public int Runs, Resets;
            IScriptProxy mProxy;
            public IScriptProxy Proxy { get { return mProxy; } set { mProxy = value; } }
            public ScriptExecuteType Init(bool postLoad) { return (ScriptExecuteType)1; }
            public void Simulate()
            {
                Runs++;
                if (Runs <= 6) throw new InvalidOperationException("errortraploop check");
            }
            public void Stop() { }
            public void Dispose() { }
            public void OnReset() { Resets++; }
        }

        public static string sErrLoopResult = "not run";

        public static object ErrorTrapLoopResult()
        {
            if (!sErrLoopResult.StartsWith("as expected")) throw new InvalidOperationException("errortraploop check: " + sErrLoopResult);
            return sErrLoopResult;
        }

        public static void ErrorTrapLoopCheck(string what)
        {
            sErrLoopResult = "running";
            // reflect_call runs this through runtime_invoke, whose frame cannot yield (docs/119 §11.2): wait on our own task
            Simulator.AddObject(new LoopTask(delegate { ErrorTrapLoopRun(what); }));
        }

        static void ErrorTrapLoopRun(string what)
        {
            try
            {
                Type trap = typeof(ScriptCore.ScriptProxy).Assembly.GetType("ScriptCore.ExceptionTrap");
                System.Reflection.FieldInfo f = trap == null ? null : trap.GetField("OnScriptError");
                bool hasTrap = f != null && f.GetValue(null) != null;
                int endedBefore = PatchCounters()[2778], errBefore = PatchCounters()[2779], callsBefore = PatchCounters()[2784];
                ErrLoopTestLogic logic = new ErrLoopTestLogic();
                ScriptCore.ScriptProxy proxy = new ScriptCore.ScriptProxy();
                proxy.Load(logic);
                string thrown = "none";
                // EA's proxy loop on this task: re-runs while OnScriptError says true, else OnReset + rethrow
                try { proxy.Simulate(); }
                catch (ResetException) { throw; }
                catch (Exception e) { thrown = e.GetType().Name; }
                bool on = ShortcutOn("errortraploop");
                int want = !hasTrap ? 1 : on ? kErrLoopRepeats : 7;
                bool ok = logic.Runs == want && (what == "expect broken" ? !on : on);
                sErrLoopResult = (ok ? "as expected (" : "WRONG (") + what + "); ErrorTrap " + hasTrap + ", switch " + on + ", runs " + logic.Runs + " (want " + want + "), resets " + logic.Resets
                    + ", thrown " + thrown + ", loops ended " + (PatchCounters()[2778] - endedBefore)
                    + ", hook calls " + (PatchCounters()[2784] - callsBefore) + ", hook errors " + (PatchCounters()[2779] - errBefore);
            }
            catch (ResetException) { sErrLoopResult = "check task reset"; throw; }
            catch (Exception e) { sErrLoopResult = "error " + e.GetType().Name + ": " + e.Message; }
        }
    }
}
