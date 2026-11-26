using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.CAS;

namespace Ts3Fixes
{
    // LC-7d (docs/336 §13): hook slots for pinned NRaas writers of CareerManager.mJob / mSchool. A body swap needs one hook
    // class per target (its stub keeps that target's original body), so each shape has slots, taken in order. NRaas types are
    // object, value-type parameters keep their type (DdPinSlot checks). "Dirty" hooks force the next phase-4 run to be a seed
    // walk (exact for any write, costs one full walk); SafeStore hooks note the store's Sim (its Dispose restores only mSim's
    // job / school).
    public static partial class Fixes
    {
        static readonly Dictionary<string, int> sDdSlotsUsed = new Dictionary<string, int>();
        static readonly Dictionary<Type, FieldInfo> sDdSafeSim = new Dictionary<Type, FieldInfo>();
        static readonly Dictionary<string, Type[]> kDdSlots = DdSlotTable();

        static Dictionary<string, Type[]> DdSlotTable()
        {
            Dictionary<string, Type[]> kDdSlots = new Dictionary<string, Type[]>();
            kDdSlots["I0V"] = new Type[] { typeof(DdPinI0V0Hook), typeof(DdPinI0V1Hook), typeof(DdPinI0V2Hook), typeof(DdPinI0V3Hook), typeof(DdPinI0V4Hook), typeof(DdPinI0V5Hook), typeof(DdPinI0V6Hook), typeof(DdPinI0V7Hook), typeof(DdPinI0V8Hook), typeof(DdPinI0V9Hook), typeof(DdPinI0V10Hook), typeof(DdPinI0V11Hook), typeof(DdPinI0V12Hook), typeof(DdPinI0V13Hook), typeof(DdPinI0V14Hook), typeof(DdPinI0V15Hook), typeof(DdPinI0V16Hook), typeof(DdPinI0V17Hook), typeof(DdPinI0V18Hook), typeof(DdPinI0V19Hook), typeof(DdPinI0V20Hook), typeof(DdPinI0V21Hook), typeof(DdPinI0V22Hook), typeof(DdPinI0V23Hook), typeof(DdPinI0V24Hook), typeof(DdPinI0V25Hook), typeof(DdPinI0V26Hook), typeof(DdPinI0V27Hook), typeof(DdPinI0V28Hook), typeof(DdPinI0V29Hook), typeof(DdPinI0V30Hook), typeof(DdPinI0V31Hook) };
            kDdSlots["S4B"] = new Type[] { typeof(DdPinS4B0Hook), typeof(DdPinS4B1Hook), typeof(DdPinS4B2Hook), typeof(DdPinS4B3Hook), typeof(DdPinS4B4Hook), typeof(DdPinS4B5Hook), typeof(DdPinS4B6Hook), typeof(DdPinS4B7Hook) };
            kDdSlots["I1B"] = new Type[] { typeof(DdPinI1B0Hook), typeof(DdPinI1B1Hook), typeof(DdPinI1B2Hook), typeof(DdPinI1B3Hook) };
            kDdSlots["I2B"] = new Type[] { typeof(DdPinI2B0Hook), typeof(DdPinI2B1Hook), typeof(DdPinI2B2Hook), typeof(DdPinI2B3Hook) };
            kDdSlots["S3V"] = new Type[] { typeof(DdPinS3V0Hook), typeof(DdPinS3V1Hook), typeof(DdPinS3V2Hook), typeof(DdPinS3V3Hook) };
            kDdSlots["S3B"] = new Type[] { typeof(DdPinS3B0Hook), typeof(DdPinS3B1Hook), typeof(DdPinS3B2Hook), typeof(DdPinS3B3Hook) };
            return kDdSlots;
        }

        // the next free slot class of m's shape whose value-type parameters match m's, or null
        static Type DdPinSlot(MethodBase m)
        {
            MethodInfo mi = m as MethodInfo;
            ParameterInfo[] ps = m.GetParameters();
            if (mi == null || (mi.ReturnType != typeof(void) && mi.ReturnType != typeof(bool))) return null;
            string shape = (m.IsStatic ? "S" : "I") + ps.Length + (mi.ReturnType == typeof(void) ? "V" : "B");
            Type[] slots;
            if (!kDdSlots.TryGetValue(shape, out slots)) return null;
            int used; sDdSlotsUsed.TryGetValue(shape, out used);
            if (used >= slots.Length) return null;
            ParameterInfo[] hp = slots[used].GetMethod("Hook").GetParameters();
            for (int i = 0; i < ps.Length; i++) if (ps[i].ParameterType.IsValueType != hp[i].ParameterType.IsValueType || (ps[i].ParameterType.IsValueType && ps[i].ParameterType != hp[i].ParameterType)) return null;
            sDdSlotsUsed[shape] = used + 1;
            return slots[used];
        }

        // SafeStore.Dispose (instance, no arguments): note the store's mSim; any other I0V target: dirty
        static void DdPinAfter(object self)
        {
            FieldInfo f;
            if (self != null && sDdSafeSim.TryGetValue(self.GetType(), out f)) { try { DdNote(f.GetValue(self) as SimDescription); } catch (Exception) { DdDirty(); } }
            else DdDirty();
        }

        public class DdPinI0V0Hook { public void Hook() { try { Orig(); } finally { DdPinAfter(this); } } public void Orig() { } }
        public class DdPinI0V1Hook { public void Hook() { try { Orig(); } finally { DdPinAfter(this); } } public void Orig() { } }
        public class DdPinI0V2Hook { public void Hook() { try { Orig(); } finally { DdPinAfter(this); } } public void Orig() { } }
        public class DdPinI0V3Hook { public void Hook() { try { Orig(); } finally { DdPinAfter(this); } } public void Orig() { } }
        public class DdPinI0V4Hook { public void Hook() { try { Orig(); } finally { DdPinAfter(this); } } public void Orig() { } }
        public class DdPinI0V5Hook { public void Hook() { try { Orig(); } finally { DdPinAfter(this); } } public void Orig() { } }
        public class DdPinI0V6Hook { public void Hook() { try { Orig(); } finally { DdPinAfter(this); } } public void Orig() { } }
        public class DdPinI0V7Hook { public void Hook() { try { Orig(); } finally { DdPinAfter(this); } } public void Orig() { } }
        public class DdPinI0V8Hook { public void Hook() { try { Orig(); } finally { DdPinAfter(this); } } public void Orig() { } }
        public class DdPinI0V9Hook { public void Hook() { try { Orig(); } finally { DdPinAfter(this); } } public void Orig() { } }
        public class DdPinI0V10Hook { public void Hook() { try { Orig(); } finally { DdPinAfter(this); } } public void Orig() { } }
        public class DdPinI0V11Hook { public void Hook() { try { Orig(); } finally { DdPinAfter(this); } } public void Orig() { } }
        public class DdPinI0V12Hook { public void Hook() { try { Orig(); } finally { DdPinAfter(this); } } public void Orig() { } }
        public class DdPinI0V13Hook { public void Hook() { try { Orig(); } finally { DdPinAfter(this); } } public void Orig() { } }
        public class DdPinI0V14Hook { public void Hook() { try { Orig(); } finally { DdPinAfter(this); } } public void Orig() { } }
        public class DdPinI0V15Hook { public void Hook() { try { Orig(); } finally { DdPinAfter(this); } } public void Orig() { } }
        public class DdPinI0V16Hook { public void Hook() { try { Orig(); } finally { DdPinAfter(this); } } public void Orig() { } }
        public class DdPinI0V17Hook { public void Hook() { try { Orig(); } finally { DdPinAfter(this); } } public void Orig() { } }
        public class DdPinI0V18Hook { public void Hook() { try { Orig(); } finally { DdPinAfter(this); } } public void Orig() { } }
        public class DdPinI0V19Hook { public void Hook() { try { Orig(); } finally { DdPinAfter(this); } } public void Orig() { } }
        public class DdPinI0V20Hook { public void Hook() { try { Orig(); } finally { DdPinAfter(this); } } public void Orig() { } }
        public class DdPinI0V21Hook { public void Hook() { try { Orig(); } finally { DdPinAfter(this); } } public void Orig() { } }
        public class DdPinI0V22Hook { public void Hook() { try { Orig(); } finally { DdPinAfter(this); } } public void Orig() { } }
        public class DdPinI0V23Hook { public void Hook() { try { Orig(); } finally { DdPinAfter(this); } } public void Orig() { } }
        public class DdPinI0V24Hook { public void Hook() { try { Orig(); } finally { DdPinAfter(this); } } public void Orig() { } }
        public class DdPinI0V25Hook { public void Hook() { try { Orig(); } finally { DdPinAfter(this); } } public void Orig() { } }
        public class DdPinI0V26Hook { public void Hook() { try { Orig(); } finally { DdPinAfter(this); } } public void Orig() { } }
        public class DdPinI0V27Hook { public void Hook() { try { Orig(); } finally { DdPinAfter(this); } } public void Orig() { } }
        public class DdPinI0V28Hook { public void Hook() { try { Orig(); } finally { DdPinAfter(this); } } public void Orig() { } }
        public class DdPinI0V29Hook { public void Hook() { try { Orig(); } finally { DdPinAfter(this); } } public void Orig() { } }
        public class DdPinI0V30Hook { public void Hook() { try { Orig(); } finally { DdPinAfter(this); } } public void Orig() { } }
        public class DdPinI0V31Hook { public void Hook() { try { Orig(); } finally { DdPinAfter(this); } } public void Orig() { } }
        public static class DdPinS4B0Hook { public static bool Hook(object a0, object a1, object a2, object a3) { try { return Orig(a0, a1, a2, a3); } finally { DdDirty(); } } public static bool Orig(object a0, object a1, object a2, object a3) { return false; } }
        public static class DdPinS4B1Hook { public static bool Hook(object a0, object a1, object a2, object a3) { try { return Orig(a0, a1, a2, a3); } finally { DdDirty(); } } public static bool Orig(object a0, object a1, object a2, object a3) { return false; } }
        public static class DdPinS4B2Hook { public static bool Hook(object a0, object a1, object a2, object a3) { try { return Orig(a0, a1, a2, a3); } finally { DdDirty(); } } public static bool Orig(object a0, object a1, object a2, object a3) { return false; } }
        public static class DdPinS4B3Hook { public static bool Hook(object a0, object a1, object a2, object a3) { try { return Orig(a0, a1, a2, a3); } finally { DdDirty(); } } public static bool Orig(object a0, object a1, object a2, object a3) { return false; } }
        public static class DdPinS4B4Hook { public static bool Hook(object a0, object a1, object a2, object a3) { try { return Orig(a0, a1, a2, a3); } finally { DdDirty(); } } public static bool Orig(object a0, object a1, object a2, object a3) { return false; } }
        public static class DdPinS4B5Hook { public static bool Hook(object a0, object a1, object a2, object a3) { try { return Orig(a0, a1, a2, a3); } finally { DdDirty(); } } public static bool Orig(object a0, object a1, object a2, object a3) { return false; } }
        public static class DdPinS4B6Hook { public static bool Hook(object a0, object a1, object a2, object a3) { try { return Orig(a0, a1, a2, a3); } finally { DdDirty(); } } public static bool Orig(object a0, object a1, object a2, object a3) { return false; } }
        public static class DdPinS4B7Hook { public static bool Hook(object a0, object a1, object a2, object a3) { try { return Orig(a0, a1, a2, a3); } finally { DdDirty(); } } public static bool Orig(object a0, object a1, object a2, object a3) { return false; } }
        public class DdPinI1B0Hook { public bool Hook(object a0) { try { return Orig(a0); } finally { DdDirty(); } } public bool Orig(object a0) { return false; } }
        public class DdPinI1B1Hook { public bool Hook(object a0) { try { return Orig(a0); } finally { DdDirty(); } } public bool Orig(object a0) { return false; } }
        public class DdPinI1B2Hook { public bool Hook(object a0) { try { return Orig(a0); } finally { DdDirty(); } } public bool Orig(object a0) { return false; } }
        public class DdPinI1B3Hook { public bool Hook(object a0) { try { return Orig(a0); } finally { DdDirty(); } } public bool Orig(object a0) { return false; } }
        public class DdPinI2B0Hook { public bool Hook(object a0, bool a1) { try { return Orig(a0, a1); } finally { DdDirty(); } } public bool Orig(object a0, bool a1) { return false; } }
        public class DdPinI2B1Hook { public bool Hook(object a0, bool a1) { try { return Orig(a0, a1); } finally { DdDirty(); } } public bool Orig(object a0, bool a1) { return false; } }
        public class DdPinI2B2Hook { public bool Hook(object a0, bool a1) { try { return Orig(a0, a1); } finally { DdDirty(); } } public bool Orig(object a0, bool a1) { return false; } }
        public class DdPinI2B3Hook { public bool Hook(object a0, bool a1) { try { return Orig(a0, a1); } finally { DdDirty(); } } public bool Orig(object a0, bool a1) { return false; } }
        public static class DdPinS3V0Hook { public static void Hook(object a0, object a1, bool a2) { try { Orig(a0, a1, a2); } finally { DdDirty(); } } public static void Orig(object a0, object a1, bool a2) { } }
        public static class DdPinS3V1Hook { public static void Hook(object a0, object a1, bool a2) { try { Orig(a0, a1, a2); } finally { DdDirty(); } } public static void Orig(object a0, object a1, bool a2) { } }
        public static class DdPinS3V2Hook { public static void Hook(object a0, object a1, bool a2) { try { Orig(a0, a1, a2); } finally { DdDirty(); } } public static void Orig(object a0, object a1, bool a2) { } }
        public static class DdPinS3V3Hook { public static void Hook(object a0, object a1, bool a2) { try { Orig(a0, a1, a2); } finally { DdDirty(); } } public static void Orig(object a0, object a1, bool a2) { } }
        public static class DdPinS3B0Hook { public static bool Hook(object a0, object a1, object a2) { try { return Orig(a0, a1, a2); } finally { DdDirty(); } } public static bool Orig(object a0, object a1, object a2) { return false; } }
        public static class DdPinS3B1Hook { public static bool Hook(object a0, object a1, object a2) { try { return Orig(a0, a1, a2); } finally { DdDirty(); } } public static bool Orig(object a0, object a1, object a2) { return false; } }
        public static class DdPinS3B2Hook { public static bool Hook(object a0, object a1, object a2) { try { return Orig(a0, a1, a2); } finally { DdDirty(); } } public static bool Orig(object a0, object a1, object a2) { return false; } }
        public static class DdPinS3B3Hook { public static bool Hook(object a0, object a1, object a2) { try { return Orig(a0, a1, a2); } finally { DdDirty(); } } public static bool Orig(object a0, object a1, object a2) { return false; } }
    }
}
