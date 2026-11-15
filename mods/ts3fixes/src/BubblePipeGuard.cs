using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Academics;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Careers;
using Sims3.Gameplay.EventSystem;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects.Miscellaneous;
using Sims3.SimIFace;
using Sims3.UI;

namespace Ts3Fixes
{
    // bubblepipeguard: docs/555 script logic audit. Off by default. Harness check: BubblePipeGuardChecks.cs.
    // bubblepipeguard (tuning kBubblePipeGuard; docs/555 §7). BubbleBar+BlowBubbles.Run calls SetHiddenFlags on the pipe
    // prop before its null check, so a prop that was not created throws a NullReferenceException instead of taking EA's
    // own exit (StandardExit, return false) [verified, decompiled]. The fix catches that one case (NRE, pipe still null,
    // state machine already entered: the only point between EnterStateMachine and the pipe's first use) and takes EA's
    // exit; anything else is rethrown. Counters [2426] clean exits, [2427] errors.
    public static partial class Fixes
    {
        static Type sBbBlow;

        static void ApplyBubblePipeGuard()
        {
            sBbBlow = typeof(BubbleBar).GetNestedType("BlowBubbles", BindingFlags.Public | BindingFlags.NonPublic);
            if (sBbBlow == null) AppliedLog().Add("bubblepipeguard: BubbleBar+BlowBubbles not found, not patched");
            else Patch(sBbBlow, "Run", typeof(BbHook), "RunHook", "RunOrig");
        }

        public class BbHook
        {
            public bool RunHook()
            {
                try { return RunOrig(); }
                catch (NullReferenceException)
                {
                    if (!BbPipeFailed((object)this as InteractionInstance)) throw;
                    ((object)this as InteractionInstance).StandardExit();
                    return false;
                }
            }
            public bool RunOrig() { return false; } // receives BubbleBar+BlowBubbles.Run's original body
        }

        // The NRE came from the pipe's first use: state machine entered, pipe still null. Switch on only.
        static bool BbPipeFailed(InteractionInstance ia)
        {
            try
            {
                if (ia == null || !ShortcutOn("bubblepipeguard")) return false;
                FieldInfo pipe = ia.GetType().GetField("PipeUsed", BindingFlags.Instance | BindingFlags.Public);
                if (pipe == null || pipe.GetValue(ia) != null || ia.mCurrentStateMachine == null) return false;
                PatchCounters()[2426]++;
                return true;
            }
            catch (Exception) { PatchCounters()[2427]++; return false; }
        }
    }
}
