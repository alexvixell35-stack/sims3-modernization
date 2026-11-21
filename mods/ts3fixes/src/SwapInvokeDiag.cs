using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace Ts3Fixes
{
    // swapinvokediag (docs/312, diagnostic, tuning kSwapInvokeDiag, off; on in the diag kit): who calls MethodInfo.Invoke on a
    // method this mod swapped. A swap copies the hook's class word into the target (Fixes.SwapMethod; SwapDelegates.cs), and
    // the game's Mono checks the invoke target against that class, so reflection on a swapped instance method throws
    // TargetException "Unable to invoke an invalid target." (RefTable.cs, docs/508; seen in game 2026-10-14) [verified in
    // game]. MonoMethod.Invoke (mscorlib, IL) rethrows TargetException unwrapped and wraps every other exception of the body
    // in TargetInvocationException [verified, mscorlib decompiled]; PropertyInfo.GetValue / SetValue and Delegate.DynamicInvoke
    // end in it. Player report 2026-11-20 (EA 1.69, CAS / Create-a-Style): 10 ScriptErrors with that exception and a
    // one-frame stack (Type.get_HasElementType, which Binder.ConvertArgs reaches inside MonoMethod.Invoke [inferred]), from a
    // CASLogic task and from NRaas MasterController's CASClothingCategory OnButtonMouseDown handler, so the caller is unknown.
    //
    // With the tuning on at load, MonoMethod.Invoke(object, BindingFlags, Binder, object[], CultureInfo) is swapped (it then
    // runs on every reflection call: hence a load-time tuning, not only the runtime switch). The hook runs EA's body inside a
    // try; on an exception it logs and rethrows unchanged (throw;), so the caller and its ScriptError see what they saw before.
    // Logged: a TargetException or any other exception from a method we swapped (name before the swap, the hook class, the
    // target's type, the calling assembly, the managed stack, every frame), and a TargetException from a method we did not
    // swap (another mod's swap, e.g. MonoPatcher, or a wrong target). Script code runs on one thread (this mscorlib has no
    // System.Threading.Thread); kSidPerMethod lines per method and
    // kSidLines per session. Logging only: reads, never writes game state; no Sim changes, so no household exemption.
    // Fallback when the swap is refused (MonoMethod.Invoke already compiled when we load): ScriptProxy.OnScriptError's hook
    // (ErrorTrapLoop.cs) logs the task's target type and the stack at the report for a TargetException.
    // Counters [1496] TargetException from a method we swapped, [1497] other exception from one, [1498] TargetException from
    // a method we did not swap, [1499] lines not written (limits, nested, switch off at run time, write failed).
    public static partial class Fixes
    {
        const int kSidLines = 40, kSidPerMethod = 3, kSidFrames = 60;
        static int sSidLines;
        static bool sSidIn;  // our own reflection while logging never logs again
        static readonly Dictionary<string, int> sSidPer = new Dictionary<string, int>();
        static readonly Type[] kSidArgs = { typeof(object), typeof(BindingFlags), typeof(Binder), typeof(object[]), typeof(CultureInfo) };

        static void ApplySwapInvokeDiag()
        {
            if (!Instantiator.kSwapInvokeDiag) return;
            Type mm = typeof(object).Assembly.GetType("System.Reflection.MonoMethod");
            if (mm == null) { AppliedLog().Add("swapinvokediag: System.Reflection.MonoMethod not found, not patched"); return; }
            Patch(mm, "Invoke", kSidArgs, typeof(SidHook), "InvokeHook", "InvokeOrig");
        }

        public class SidHook
        {
            public object InvokeHook(object obj, BindingFlags invokeAttr, Binder binder, object[] parameters, CultureInfo culture)
            {
                try { return InvokeOrig(obj, invokeAttr, binder, parameters, culture); }
                catch (Exception e) { SidNote((object)this as MethodBase, obj, e, null); throw; }  // `this` is really a MonoMethod
            }
            public object InvokeOrig(object obj, BindingFlags invokeAttr, Binder binder, object[] parameters, CultureInfo culture) { return null; } // receives MonoMethod.Invoke's original header
        }

        // Pure: which counter an exception out of MethodInfo.Invoke counts in; -1 = not ours to report.
        internal static int SidSlot(bool swappedByUs, bool targetException)
        {
            if (swappedByUs) return targetException ? 1496 : 1497;
            return targetException ? 1498 : -1;
        }

        // Pure: may this line be written? per = lines so far for this method, total = lines this session.
        internal static bool SidAllowed(int per, int total) { return per < kSidPerMethod && total < kSidLines; }

        static void SidNote(MethodBase m, object target, Exception e, string where)
        {
            try
            {
                if (m == null || e == null) return;
                IntPtr h = m.MethodHandle.Value;
                Type hook;
                bool ours = sPatched.TryGetValue(h, out hook);
                int slot = SidSlot(ours, e is TargetException);
                if (slot < 0) return;
                int[] c = PatchCounters();
                c[slot]++;
                if (sSidIn || !ShortcutOn("swapinvokediag")) { c[1499]++; return; }
                sSidIn = true;
                try
                {
                    string name;
                    if (!AutShadowCore.SwapNames.TryGetValue(h, out name))
                        name = (m.ReflectedType != null ? m.ReflectedType.FullName : "?") + "." + m.Name;
                    int per;
                    sSidPer.TryGetValue(name, out per);
                    if (!SidAllowed(per, sSidLines)) { c[1499]++; return; }
                    sSidPer[name] = per + 1;
                    sSidLines++;
                    Exception x = e is TargetInvocationException && e.InnerException != null ? e.InnerException : e;
                    string caller;
                    List<string> frames = SidStack(out caller);
                    string head = "swapinvokediag: " + e.GetType().Name + (x != e ? " (" + x.GetType().Name + ": " + x.Message + ")" : ": " + e.Message)
                        + " from " + (where ?? "MethodInfo.Invoke") + " on " + name + (ours ? " (ours, hook " + hook.Name + ")" : " (not swapped by us, class " + (m.DeclaringType != null ? m.DeclaringType.FullName : "?") + ")")
                        + ", target " + (target != null ? target.GetType().FullName : "null") + ", caller assembly " + caller
                        + ", #" + (per + 1) + " for this method, " + frames.Count + " frames";
                    if (!DiagLine(head) || !DiagList("swapinvokediag stack: ", frames)) c[1499]++;
                }
                finally { sSidIn = false; }
            }
            catch (Exception) { PatchCounters()[1499]++; }
        }

        // The managed frames above our hook, innermost first, as "Assembly:Type.Method"; caller = the first frame outside
        // mscorlib (the code that asked reflection for the call).
        static List<string> SidStack(out string caller)
        {
            caller = "?";
            List<string> o = new List<string>();
            System.Diagnostics.StackTrace st = new System.Diagnostics.StackTrace(1, false);
            for (int i = 0; i < st.FrameCount && o.Count < kSidFrames; i++)
            {
                MethodBase f = st.GetFrame(i).GetMethod();
                if (f == null) continue;
                Type t = f.DeclaringType;
                if (t == typeof(SidHook) || f.Name == "SidNote" || f.Name == "SidStack") continue;
                string asm = t != null ? t.Assembly.GetName().Name : "?";
                o.Add(asm + ":" + (t != null ? t.FullName : "?") + "." + f.Name);
                if (caller == "?" && asm != "mscorlib") caller = asm;
            }
            return o;
        }

        // ErrorTrapLoop.cs's OnScriptError hook: a TargetException that reached a task. The invoke hook names the call when it
        // is in; this line ties the ScriptError to the task and says whether it was.
        static void SidProxyNote(ScriptCore.ScriptProxy p, Exception e)
        {
            try
            {
                if (!(e is TargetException) || !ShortcutOn("swapinvokediag") || sSidIn) return;
                if (!SidAllowed(0, sSidLines)) { PatchCounters()[1499]++; return; }
                sSidIn = true;
                try
                {
                    sSidLines++;
                    string caller;
                    List<string> frames = SidStack(out caller);
                    object t = p != null ? (object)p.Target : null;
                    bool hookIn = EpSwapCount(typeof(SidHook)) > 0;
                    if (!DiagLine("swapinvokediag: TargetException reached task " + (t != null ? t.GetType().FullName : "null") + " (invoke hook " + (hookIn ? "in: see the line before" : "not in") + "), stack at the report:")
                        || !DiagList("swapinvokediag stack: ", frames)) PatchCounters()[1499]++;
                }
                finally { sSidIn = false; }
            }
            catch (Exception) { PatchCounters()[1499]++; }
        }

        // Harness check (repro cas-targetexc-check; reflect_call Ts3Fixes.Fixes.SwapInvokeDiagCheck): every instance method
        // we swapped in a CAS class (Sims3.UI.CAS*, Sims3.Gameplay.CAS*) is invoked by reflection on an uninitialized object
        // of its own class (no constructor ran; parameters null / default), the way a mod's reflection would call it.
        // Expected [verified for ReferenceHashtable]: TargetException before the body runs. "ran" = the body ran (the
        // hypothesis is wrong for that method). With the switch and the tuning on, counter [1496] grows by the TargetExceptions.
        public static string SwapInvokeDiagCheck()
        {
            StringBuilder b = new StringBuilder();
            int[] c = PatchCounters();
            int before = c[1496], te = 0, ran = 0, other = 0, n = 0;
            foreach (KeyValuePair<Type, MethodInfo> kv in sSdTargets)
            {
                MethodInfo m = kv.Value;
                Type owner = m != null ? m.ReflectedType : null;
                if (m == null || m.IsStatic || owner == null || owner.Namespace == null) continue;
                if (!owner.Namespace.StartsWith("Sims3.UI.CAS") && !owner.Namespace.StartsWith("Sims3.Gameplay.CAS")) continue;
                n++;
                string res;
                try
                {
                    object fake = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(owner);
                    ParameterInfo[] ps = m.GetParameters();
                    object[] args = new object[ps.Length];
                    for (int i = 0; i < ps.Length; i++)
                        if (ps[i].ParameterType.IsValueType) args[i] = Activator.CreateInstance(ps[i].ParameterType);
                    m.Invoke(fake, args);
                    res = "ran"; ran++;
                }
                catch (TargetException) { res = "TargetException"; te++; }
                catch (TargetInvocationException e) { res = "ran (" + (e.InnerException != null ? e.InnerException.GetType().Name : "?") + ")"; ran++; }
                catch (Exception e) { res = e.GetType().Name; other++; }
                string name;
                if (!AutShadowCore.SwapNames.TryGetValue(m.MethodHandle.Value, out name)) name = owner.Name + "." + m.Name;
                b.Append(name).Append(" [").Append(kv.Key.Name).Append("] ").Append(res).Append("; ");
            }
            bool hookIn = EpSwapCount(typeof(SidHook)) > 0, sw = ShortcutOn("swapinvokediag");
            string verdict = n == 0 ? "NONE (no CAS swap in place)"
                : te == n && (!hookIn || !sw || c[1496] - before == n) ? "OK" : "MISMATCH";
            return "swapinvokediag check: " + verdict + ": " + n + " CAS swaps, TargetException " + te + ", ran " + ran + ", other " + other
                + "; invoke hook " + (hookIn ? "in" : "not in") + ", switch " + (sw ? "on" : "off") + ", [1496] +" + (c[1496] - before)
                + "; pure " + (SidSlot(true, true) == 1496 && SidSlot(true, false) == 1497 && SidSlot(false, true) == 1498 && SidSlot(false, false) == -1
                    && SidAllowed(0, 0) && !SidAllowed(kSidPerMethod, 0) && !SidAllowed(0, kSidLines) ? "ok" : "FAIL")
                + "; " + b.ToString();
        }
    }
}
