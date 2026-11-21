using System;
using Sims3.UI;
using Sims3.UI.CAS;

namespace Ts3Fixes
{
    // castintreset (tuning kCasTintReset, default off; docs/658 §3 H1, §6.6). UNCONFIRMED candidate for the "body lighter
    // and greyer than the face" CAS glitch: built so a probe run can turn it on the moment cas_skin_probe shows the stale
    // value; do not ship it on before that. [verified, decompiled] CASClickOnSim.Dispose sets HighlightMode = None but
    // never resets HighlightIndex, and CASClickOnSim.SetState switches hover off (SimSelectionModeEnabled = false) with
    // the index left set; a later panel that sets the mode back to Parts lights the old pick id again, and no hover event
    // comes to clear it. [inferred] the native renderer tints that part (a swimsuit's mesh carries the neck, shoulder and
    // back skin). Fix candidate: at those two exact spots, after EA's code, set CASModel.HighlightIndex = 0 (EA's own
    // reset value, CASHair.cs). H2 (mouse leaves onto a panel) and H3 (clothing-tab SelectionIndex) are not covered:
    // the probe decides. Counters [4213] stale non-zero index cleared, [4214] errors. Log "castintreset: ..." capped.
    public static partial class Fixes
    {
        static readonly LineCapCore sCtrCap = new LineCapCore(10, 60000, 100);

        static void ApplyCasTintReset()
        {
            Patch(typeof(CASClickOnSim), "Dispose", Type.EmptyTypes, typeof(CasTintHook), "DisposeHook", "DisposeOrig");
            Patch(typeof(CASClickOnSim), "SetState", new Type[] { typeof(CASState) }, typeof(CasTintHook), "SetStateHook", "SetStateOrig");
        }

        static void CtrClear(string where)
        {
            if (!ShortcutOn("castintreset")) return;
            try
            {
                ICASModel m = Responder.Instance != null ? Responder.Instance.CASModel : null;
                if (m == null) return;
                uint was = m.HighlightIndex;
                if (!CasTintResetCore.ShouldClear(true, was)) return;
                m.HighlightIndex = 0;
                PatchCounters()[4213]++;
                if (sCtrCap.Take(System.Environment.TickCount))
                    DiagLine("castintreset: " + where + " left HighlightIndex " + was + " (mode " + m.HighlightMode + ", selection " + m.SelectionIndex + "): cleared to 0");
            }
            catch (Exception ex) { PatchCounters()[4214]++; if (sCtrCap.Take(System.Environment.TickCount)) DiagLine("castintreset: error " + ex.GetType().Name); }
        }

        public class CasTintHook
        {
            public void DisposeHook()
            {
                DisposeOrig();
                CtrClear("CASClickOnSim.Dispose");
            }
            public void DisposeOrig() { } // receives CASClickOnSim.Dispose

            public void SetStateHook(CASState state)
            {
                SetStateOrig(state);
                CtrClear("CASClickOnSim.SetState");
            }
            public void SetStateOrig(CASState state) { } // receives CASClickOnSim.SetState
        }
    }
}
