using System;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay;
using Sims3.Gameplay.EventSystem;
using Sims3.Gameplay.Objects.CookingObjects;
using Sims3.Gameplay.Utilities;

namespace Ts3Fixes
{
    // docs/536: harness check for SwapMethod's KeepVirtual (repro virtual-swap-check).
    public static partial class Fixes
    {
        // Two fresh base/derived pairs, each base method hooked at check time (before any call site that names it is
        // compiled): one the old way (the hook's header, no VIRTUAL flag), one with KeepVirtual. A hooked base returns 11.
        public class VsOldBase { public virtual int F() { return 1; } }
        public class VsOldDerived : VsOldBase { public override int F() { return 2; } }
        public class VsNewBase { public virtual int F() { return 1; } }
        public class VsNewDerived : VsNewBase { public override int F() { return 2; } }
        public class VsOldHook { public int FHook() { return 10 + FOrig(); } public int FOrig() { return 0; } }
        public class VsNewHook { public int FHook() { return 10 + FOrig(); } public int FOrig() { return 0; } }

        static bool sVsSwapped;
        static VsOldBase sVsOld, sVsOldB;
        static VsNewBase sVsNew, sVsNewB;

        static void VsSwap(Type target, Type hook, bool keep)
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly;
            MethodInfo t = target.GetMethod("F", all);
            SwapMethod(hook.GetMethod("FOrig", all), t, keep);
            SwapMethod(t, hook.GetMethod("FHook", all), keep);
        }

        // first compiled after VsSwap, so their callvirt sites resolve against the swapped headers
        static int VsCallOld(VsOldBase o) { return o.F(); }
        static int VsCallNew(VsNewBase o) { return o.F(); }

        static int sVsThrew;
        static ListenerAction VsOnThrew(Sims3.Gameplay.EventSystem.Event e) { sVsThrew++; return ListenerAction.Keep; }

        // "expect fixed": the old-way pair shows the bug (a derived receiver runs the hooked base, 11), the KeepVirtual pair
        // dispatches (derived 2, base 11), ScriptObject.Destroy (hooked by memalive before v0.2.6) still reads as virtual, and
        // Destroy() on a new bowl with a second bowl in its stacking slot runs PreparedFood.Destroy for both
        // (kCleanedOrThrewAwayObject twice; once when the stack could not be built out of the world).
        public static object VirtualSwapCheck(string what)
        {
            if (what != "expect fixed") return "virtualswap <expect fixed>";
            if (!sVsSwapped)
            {
                sVsOld = new VsOldBase(); sVsOldB = new VsOldDerived(); sVsNew = new VsNewBase(); sVsNewB = new VsNewDerived();  // vtables set up before the swap, as the game's are
                VsSwap(typeof(VsOldBase), typeof(VsOldHook), false);
                VsSwap(typeof(VsNewBase), typeof(VsNewHook), true);
                sVsSwapped = true;
            }
            int oldD = VsCallOld(sVsOldB), oldB = VsCallOld(sVsOld), newD = VsCallNew(sVsNewB), newB = VsCallNew(sVsNew);
            MethodInfo destroy = typeof(ScriptObject).GetMethod("Destroy", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            bool destroyVirtual = destroy != null && destroy.IsVirtual;

            Bowl a = GlobalFunctions.CreateObjectOutOfWorld("Bowl") as Bowl, b = GlobalFunctions.CreateObjectOutOfWorld("Bowl") as Bowl;
            if (a == null || b == null)
            {
                if (a != null) a.Destroy();
                if (b != null) b.Destroy();
                throw new InvalidOperationException("virtualswap check: CreateObjectOutOfWorld(\"Bowl\") made nothing");
            }
            bool stacked = b.ParentToSlot(a, ServingContainer.kStackingContainmentSlot) && a.GetContainedObject(ServingContainer.kStackingContainmentSlot) == b;
            sVsThrew = 0;
            EventListener l = EventTracker.AddListener(EventTypeId.kCleanedOrThrewAwayObject, VsOnThrew);
            try { a.Destroy(); }
            finally { EventTracker.RemoveListener(l); }
            int threw = sVsThrew;
            if (!stacked || threw < 2) b.Destroy();  // a's Destroy did not reach b

            string line = "old way: derived " + oldD + ", base " + oldB + "; KeepVirtual: derived " + newD + ", base " + newB
                + "; ScriptObject.Destroy virtual " + destroyVirtual + "; bowl stack built " + stacked + ", kCleanedOrThrewAwayObject x" + threw;
            bool ok = oldD == 11 && oldB == 11 && newD == 2 && newB == 11 && destroyVirtual && threw == (stacked ? 2 : 1);
            if (!ok) throw new InvalidOperationException("virtualswap " + what + ": wrong outcome; " + line);
            return "as expected; " + line;
        }
    }
}
