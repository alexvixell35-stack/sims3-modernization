using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using ScriptCore;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // savedstacks (docs/242, on by default): a swapped method keeps its saved Sims. EA's TaskStateCollection.Read drops a saved
    // method whose checksum no longer matches, or whose name no longer resolves, and that resets the Sim at load. Both happen
    // to a frame in a hooked method: the method under its own name now hashes as our hook, and a stub (the frame a save made
    // by our own build holds) is not in the game class's method list. The original IL still exists, in the stub, with the
    // saved checksum, so ReadTask (first call of a load) points each dropped entry at the stub of that checksum. The frame
    // resumes in EA's code and returns to its caller. The table holds the targets too: a save by our own build names a frame
    // in a hook's body by the hook's class (SwapMethod moves the class word), and a hook that does not derive from the game
    // class cannot find the method again. [760] entries remapped, [761] entries left dropped, [762] entries with no checksum.
    public static partial class Fixes
    {
        static readonly List<MethodBase> sStubs = new List<MethodBase>();
        static Dictionary<uint, RuntimeMethodHandle> sStubBySum;
        static int sStubsSummed;  // trip subclasses patch again at world load
        static WeakReference sSsDone;

        static void ApplySavedStacks()
        {
            Patch(typeof(TaskStateCollection), "ReadTask", typeof(SavedStackHook), "ReadTaskHook", "ReadTaskOrig");
        }

        // A stub takes the target's whole header, flags and vtable slot included, but it is only ever called directly and it
        // lives in the hook's class, whose vtable can be smaller: Type.GetMethods asserts method->slot < nslots there.
        static void StubDirect(MethodBase stub)
        {
            IntPtr m = stub.MethodHandle.Value;
            Marshal.WriteInt16(m, (short)(Marshal.ReadInt16(m) & ~(int)MethodAttributes.Virtual));
            IntPtr s = new IntPtr(m.ToInt32() + kSlotOffset);
            Marshal.WriteInt32(s, Marshal.ReadInt32(s) | ~0xFFF);
            sStubs.Add(stub);
        }

        static void SsAddSum(MethodBase m, int[] c)
        {
            uint sum;
            RuntimeMethodHandle had;
            if (!TaskControl.GetMethodChecksum(m.MethodHandle, out sum)) c[762]++;
            else if (sStubBySum.TryGetValue(sum, out had)) { if (had.Value != m.MethodHandle.Value) sStubBySum[sum] = default(RuntimeMethodHandle); }  // two bodies, one sum: refuse, as EA does
            else sStubBySum[sum] = m.MethodHandle;
        }

        static void SsRemap(TaskStateCollection ts)
        {
            int[] c = PatchCounters();
            if (sStubBySum == null || sStubsSummed != sStubs.Count)
            {
                sStubBySum = new Dictionary<uint, RuntimeMethodHandle>();
                sStubsSummed = sStubs.Count;
                foreach (MethodBase s in sStubs) SsAddSum(s, c);
                // our own frames too: a lambda or a helper the game's name lookup cannot find again. A hook has its target's
                // sum (the target took its header), so a sum already there stays.
                Dictionary<uint, bool> taken = new Dictionary<uint, bool>();
                foreach (uint k in sStubBySum.Keys) taken[k] = true;
                foreach (Type t in typeof(Fixes).Assembly.GetTypes())
                {
                    if (t.IsGenericType) continue;
                    foreach (MethodInfo m in t.GetMethods(kSsAll))
                    {
                        uint sum;
                        if (m.IsAbstract || m.IsGenericMethod) continue;
                        if (!TaskControl.GetMethodChecksum(m.MethodHandle, out sum) || !taken.ContainsKey(sum)) SsAddSum(m, c);
                    }
                }
            }
            IList list = (IList)typeof(TaskStateCollection).GetField("mMethodList", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(ts);
            for (int i = 0; i < list.Count; i++)
            {
                object e = list[i];
                FieldInfo hf = e.GetType().GetField("handle");
                if (((RuntimeMethodHandle)hf.GetValue(e)).Value != IntPtr.Zero) continue;
                RuntimeMethodHandle stub;
                if (!sStubBySum.TryGetValue((uint)e.GetType().GetField("checksum").GetValue(e), out stub) || stub.Value == IntPtr.Zero) { c[761]++; continue; }
                hf.SetValue(e, stub);
                list[i] = e;
                c[760]++;
            }
        }

        public class SavedStackHook : TaskStateCollection
        {
            public SavedTaskContext ReadTaskHook(FastStreamReader reader, IPersistReader persistReader)
            {
                if ((sSsDone == null || !ReferenceEquals(sSsDone.Target, this)) && ShortcutOn("savedstacks"))
                {
                    sSsDone = new WeakReference(this);
                    try { SsRemap(this); }
                    catch (Exception e) { AppliedLog().Add("savedstacks: " + e.GetType().Name + " " + e.Message); }
                }
                return ReadTaskOrig(reader, persistReader);
            }
            public SavedTaskContext ReadTaskOrig(FastStreamReader reader, IPersistReader persistReader) { return null; }  // receives TaskStateCollection.ReadTask's original header
        }

        // Dummies for repro stub-slot-check (docs/242): a target whose last virtual has a slot past a plain class's vtable.
        public class SsTarget
        {
            public virtual int F0() { return 0; } public virtual int F1() { return 0; } public virtual int F2() { return 0; } public virtual int F3() { return 0; }
            public virtual int F4() { return 0; } public virtual int F5() { return 0; } public virtual int F6() { return 0; } public virtual int F7() { return 0; }
            public virtual int F8() { return 7; }
        }
        public class SsDerived : SsTarget { public override int F8() { return 8; } }
        public class SsHookSafe { public int F8Orig() { return 0; } }
        public class SsHookRaw { public int F8Orig() { return 0; } }

        const BindingFlags kSsAll = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        // "hooks": GetMethods on every hook class. "dummy": the same on a stub with a slot past its class's vtable, made safe by
        // StubDirect; the stub still runs the original when called directly. "raw": the old form (KeepVirtual leaves a stub's
        // slot alone). EXPECTED TO KILL THE GAME if the Mono assert is what ended sim-gone6 and 7: a dedicated job only.
        public static object SavedStackCheck(string what)
        {
            if (what == "hooks")
            {
                int types = 0, methods = 0;
                Dictionary<Type, bool> seen = new Dictionary<Type, bool>();
                foreach (Type h in sPatched.Values)
                {
                    if (seen.ContainsKey(h)) continue;
                    seen[h] = true;
                    types++;
                    methods += h.GetMethods(kSsAll).Length;
                }
                return "GetMethods ok on " + types + " hook classes, " + methods + " methods; stubs " + sStubs.Count + ", remapped " + PatchCounters()[760] + ", left dropped " + PatchCounters()[761];
            }
            if (what != "dummy" && what != "raw") return "savedstack <hooks|dummy|raw>";
            MethodInfo t = typeof(SsTarget).GetMethod("F8", kSsAll);
            Type hook = what == "raw" ? typeof(SsHookRaw) : typeof(SsHookSafe);
            MethodInfo stub = hook.GetMethod("F8Orig", kSsAll);
            SwapMethod(stub, t);
            if (what == "dummy") { StubDirect(stub); sStubs.Remove(stub); }
            int n = hook.GetMethods(kSsAll).Length;
            if (what == "raw") return "raw GetMethods survived: " + n + " methods, the assert did not fire";
            int r = (int)stub.Invoke(new SsDerived(), null);
            if (r != 7) throw new InvalidOperationException("savedstack dummy: stub ran " + r + ", expected the original's 7");
            return "dummy ok: GetMethods " + n + " methods, stub runs the original (7) on a derived receiver";
        }
    }
}
