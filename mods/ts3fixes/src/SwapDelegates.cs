using System;
using System.Collections.Generic;
using System.Reflection;

namespace Ts3Fixes
{
    // swapdelegates (docs/576 §1, ON by default: it repairs a fault of our own swaps, like savedstacks): a saved delegate to
    // a swapped method comes back null, so EA drops the alarm or callback that held it, with every switch off.
    //
    // The save path [verified, ScriptCore decompiled]: DelegateFormatter.Write / MulticastDelegateFormatter.Write store
    // (Target, Method); PersistWriter's MethodInst case writes Method.DeclaringType, Method.Name and the parameter types.
    // MonoMethod.DeclaringType reads the native method's class (MonoMethodInfo.get_method_info -> parent), and SwapMethod
    // copies the hook's class word into the target (the body's tokens resolve through it), so the save names the hook class
    // with the original method name. PersistReader's MethodInst case resolves the class through PersistedClass ->
    // Persist.GetTypeByUnqualifiedName and calls type.GetMethod(name, all | FlattenHierarchy | ExactBinding, types): the hook
    // class has no method of that name (unless it derives from the game class), so the MethodInfo is null, the formatter's
    // Precreate returns null and AlarmManager.Read drops the alarm. Seen: TrickOrTreatSituation.SendNPCTrickOrTreaters
    // (Spooky Day NPC trick-or-treaters), NpcSeasonalPartyManager.ScheduleNpcParty; the full list is docs/576 §1.
    //
    // Fix: EA's own class remap. Persist.GetTypeByUnqualifiedName asks Persist.TypeFound, and the game's handler
    // (Simulator.TypeFoundHandler, registered in Simulator's static ctor) replaces a found type by
    // ScriptProxy.SubstituteClass(FullName), the table ScriptProxy.SetLogicReplacement fills. Each hook class whose swaps all
    // target one game class, and that has no methods besides its hook/stub pairs, is mapped to that game class: the reader
    // then finds the target under its own name, which runs our hook as before the save. The same lookup fixes a saved stack
    // frame in a hook body (it resumes in that body, the one it was saved in). A hook class with targets in two classes or
    // with other methods (helpers, anonymous methods, its own callbacks, which saves name by the hook class) is left alone and
    // listed by SwapDelegatesCheck. Nothing is written differently; saves made without our package are not touched.
    // Counters [2687] hook classes mapped, [2688] hook classes left alone (two owners or other methods).
    public static partial class Fixes
    {
        static readonly Dictionary<Type, List<Type>> sSdOwners = new Dictionary<Type, List<Type>>();
        static readonly Dictionary<Type, List<IntPtr>> sSdOwn = new Dictionary<Type, List<IntPtr>>();
        static readonly Dictionary<Type, string> sSdState = new Dictionary<Type, string>();  // hook -> "mapped Owner" / reason

        // Patch calls this for every swap that went in: owner = the target's class before the swap.
        static readonly List<KeyValuePair<Type, MethodInfo>> sSdTargets = new List<KeyValuePair<Type, MethodInfo>>();  // (hook, target)

        static void SdRecord(Type hook, Type owner, MethodInfo target, MethodBase replacement, MethodBase stub)
        {
            try
            {
                if (hook == null || owner == null) return;
                if (target != null) sSdTargets.Add(new KeyValuePair<Type, MethodInfo>(hook, target));
                List<Type> o;
                if (!sSdOwners.TryGetValue(hook, out o)) { o = new List<Type>(); sSdOwners[hook] = o; }
                if (!o.Contains(owner)) o.Add(owner);
                List<IntPtr> own;
                if (!sSdOwn.TryGetValue(hook, out own)) { own = new List<IntPtr>(); sSdOwn[hook] = own; }
                if (replacement != null) own.Add(replacement.MethodHandle.Value);
                if (stub != null) own.Add(stub.MethodHandle.Value);
                SdSync(hook);
            }
            catch (Exception) { PatchCounters()[2688]++; }
        }

        // Whether a hook class may stand for its game class in a save: one owner, no methods of its own besides the pairs.
        internal static bool SdEligible(int owners, int extraMethods)
        {
            return owners == 1 && extraMethods == 0;
        }

        static int SdExtra(Type hook, List<IntPtr> own)
        {
            int n = 0;
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (MethodInfo m in hook.GetMethods(all))
                if (!own.Contains(m.MethodHandle.Value)) n++;
            return n;
        }

        static void SdSync(Type hook)
        {
            List<Type> owners = sSdOwners[hook];
            int extra = SdExtra(hook, sSdOwn[hook]);
            bool on = ShortcutOn("swapdelegates");
            bool map = on && SdEligible(owners.Count, extra);
            // self = no remap (Simulator.TypeFoundHandler returns false when the name is unchanged)
            ScriptCore.ScriptProxy.SetLogicReplacement(hook.FullName, map ? owners[0].FullName : hook.FullName);
            sSdState[hook] = map ? "mapped " + owners[0].FullName
                : !on ? "off" : owners.Count != 1 ? "left alone: " + owners.Count + " owners" : "left alone: " + extra + " other methods";
            int mapped = 0, left = 0;
            foreach (KeyValuePair<Type, string> kv in sSdState) { if (kv.Value.StartsWith("mapped")) mapped++; else if (kv.Value.StartsWith("left")) left++; }
            int[] c = PatchCounters();
            c[2687] = mapped; c[2688] = left;
        }

        // World load (Instantiator): the tuning is read by now; re-apply the table so a tuning change takes effect for the
        // next load.
        public static void SwapDelegatesWorldLoad()
        {
            try { foreach (Type h in new List<Type>(sSdOwners.Keys)) SdSync(h); }
            catch (Exception) { PatchCounters()[2688]++; }
        }

        // What the save reader resolves for a method the writer saw as (declaringType, name, types): the reader's own lookup,
        // with the class remapped (on) or as written (off).
        internal static MethodInfo SdRead(Type written, Type remapped, string name, Type[] types)
        {
            Type t = remapped ?? written;
            if (t == null || name == null) return null;
            try
            {
                return t.GetMethod(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.FlattenHierarchy | BindingFlags.ExactBinding, null, types, null);
            }
            catch (Exception) { return null; }
        }

        static Type[] SdTypes(MethodInfo m)
        {
            ParameterInfo[] ps = m.GetParameters();
            Type[] r = new Type[ps.Length];
            for (int i = 0; i < ps.Length; i++) r[i] = ps[i].ParameterType;
            return r;
        }

        // One save/load round of a delegate's method as PersistWriter/PersistReader do it: true when the reader gets the same
        // method back. remap = the class the live lookup returns for the written class (Persist.GetTypeByUnqualifiedName).
        internal static bool SdRoundTrip(MethodInfo m, bool useRemap, out string written)
        {
            written = "";
            if (m == null) return false;
            Type decl = m.DeclaringType;
            written = (decl == null ? "?" : decl.FullName) + "::" + m.Name;
            Type remap = null;
            if (useRemap && decl != null)
            {
                Type live = ScriptCore.Persist.GetTypeByUnqualifiedName(decl.FullName);
                if (live != null && live != decl) remap = live;
            }
            MethodInfo back = SdRead(decl, remap, m.Name, SdTypes(m));
            return back != null && back.MethodHandle.Value == m.MethodHandle.Value;
        }
    
        // SwapDelegatesCheck run: every swapped target saved and read back as a delegate's method, the way the save does it.
        //   Off: the class is written and resolved as the hook class (EA's path today). On: the live lookup remaps it.
        //   Control: a method that is not swapped (AlarmManager.AddAlarm itself) round-trips both ways.
        //   Lists the targets that still fail with the switch on (their hook class is left alone) and whether EA passes
        //   them as a delegate (docs/576 §1 table).
        public static object SwapDelegatesCheck(string what)
        {
            string counters = "counters: [2687] hook classes mapped=" + PatchCounters()[2687] + ", [2688] left alone=" + PatchCounters()[2688];
            MethodInfo ctl = typeof(Sims3.Gameplay.Utilities.AlarmManager).GetMethod("AddAlarm", new Type[] { typeof(float), typeof(Sims3.Gameplay.Utilities.TimeUnit), typeof(Sims3.Gameplay.Utilities.AlarmTimerCallback), typeof(string), typeof(Sims3.Gameplay.Utilities.AlarmType), typeof(Sims3.Gameplay.Utilities.IAlarmOwner) });
            string missing = (ctl == null ? "AlarmManager.AddAlarm " : "") + (sSdTargets.Count == 0 ? "no swaps recorded" : "");
            missing = missing.Trim();
            if (what == "result")
            {
                List<string> left = new List<string>();
                foreach (KeyValuePair<Type, string> kv in sSdState) if (!kv.Value.StartsWith("mapped")) left.Add(kv.Key.Name + " (" + kv.Value + ")");
                return counters + "; members found=" + (missing.Length == 0) + "; left alone: " + string.Join(", ", left.ToArray());
            }
            if (what != "run") return "swapdelegates <run|result>";
            bool on = ShortcutOn("swapdelegates");
            SwapDelegatesWorldLoad();  // the table follows the switch now
            int ok = 0, bad = 0; string w; List<string> fails = new List<string>();
            foreach (KeyValuePair<Type, MethodInfo> kv in sSdTargets)
            {
                if (SdRoundTrip(kv.Value, true, out w)) ok++;
                else { bad++; if (fails.Count < 12) fails.Add(w); }
            }
            // the reported pair: TrickOrTreatSituation.SendNPCTrickOrTreaters when it is swapped, else the first target
            MethodInfo probe = typeof(Sims3.Gameplay.Situations.TrickOrTreatSituation).GetMethod("SendNPCTrickOrTreaters", BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (probe == null && sSdTargets.Count > 0) probe = sSdTargets[0].Value;
            bool fixedNow = SdRoundTrip(probe, true, out w);
            string probeName = w;
            bool ctlOk = SdRoundTrip(ctl, true, out w) && SdRoundTrip(ctl, false, out w);
            return "swapdelegates check (switch " + (on ? "on" : "off") + "): saved delegate to " + probeName + " read back=" + fixedNow
                + "; all swapped targets: " + ok + " read back, " + bad + " lost" + (fails.Count > 0 ? " (" + string.Join(", ", fails.ToArray()) + ")" : "")
                + "; control: unswapped AlarmManager.AddAlarm read back=" + ctlOk + "; controls ok=" + ctlOk
                + "; verdict: " + GlVerdict(on, fixedNow, ctlOk, missing.Length == 0) + "; " + counters + "; members found=" + (missing.Length == 0);
        }
    }
}
