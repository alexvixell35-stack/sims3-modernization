using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects.CookingObjects;
using Sims3.Gameplay.Objects.Electronics;
using Sims3.Gameplay.Objects.Environment;
using Sims3.Gameplay.Objects.HobbiesSkills;
using Sims3.Gameplay.Objects.MinorPets;
using Sims3.Gameplay.Objects.Misc;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // bandjam: docs/559 second-pass gameplay scan. Off by default. Harness check: BandJamChecks.cs.
    // bandjam (tuning kBandJam; docs/559 §6). JamController [verified, decompiled]:
    //  (a) SetSweetMove tests `(j != null && s != Exited) || s != PlayingSweetMove`: a null jammer throws, and any state but
    //      Exited passes, so a Sweet Move clicked on a jammer that is already leaving (PendingExit, set by StopPlaying's
    //      RemoveJammer) turns it back into SweetMoveRequested. RemoveJammer then waits `while (state != Exited)` in a Sleep
    //      loop with no exit test, but the state cycles back to Playing and never reaches Exited: the leaving Sim hangs in
    //      the jam for good. Fix: the own request is applied only to a jammer that is Playing (the state the interaction's
    //      Test requires) or already requested; the other jammers' 50% chance runs as EA's. Counters [2458].
    //  (b) RemoveJammer(BandInstrument) (instrument DoReset) drops the entry without stopping the jam's SoundArray when it
    //      was the last jammer, and RemoveJammer(Sim) returns early for a Sim no longer listed, so the jam music loops on
    //      with nobody playing. Fix: after either overload, an empty controller stops its sound (EA's StopSoundArray) [2459].
    //  Errors [2460].
    public static partial class Fixes
    {
        static FieldInfo sBjSound;
        static MethodInfo sBjStop;

        static void ApplyBandJam()
        {
            sBjSound = typeof(JamController).GetField("mSoundArray", kInstAll);
            sBjStop = typeof(JamController).GetMethod("StopSoundArray", kInstAll);
            Patch(typeof(JamController), "SetSweetMove", typeof(BjHook), "SweetHook", "SweetOrig");
            if (sBjSound == null || sBjStop == null) AppliedLog().Add("bandjam: JamController sound members not found, sound part not patched");
            else
            {
                Patch(typeof(JamController), "RemoveJammer", new Type[] { typeof(BandInstrument) }, typeof(BjHook), "RemoveInstHook", "RemoveInstOrig");
                Patch(typeof(JamController), "RemoveJammer", new Type[] { typeof(Sim), typeof(bool) }, typeof(BjHook), "RemoveSimHook", "RemoveSimOrig");
            }
        }

        public class BjHook
        {
            public void SweetHook(Sim actor, int minLevelForAvailablity)
            {
                JamController jc = (object)this as JamController;
                JamInfo own = null;
                JamInfo.PlayState keep = JamInfo.PlayState.Undefined;
                bool refuse = false;
                try
                {
                    own = jc == null ? null : jc.GetJammerInfo(actor);
                    refuse = BjRefuse(own);
                    if (refuse) { PatchCounters()[2458]++; keep = own == null ? keep : own.CurrentPlayState; }
                    if (refuse && own == null && ShortcutOn("bandjam")) return;  // EA throws here: not jamming, nothing to request
                }
                catch (Exception) { PatchCounters()[2460]++; }
                SweetOrig(actor, minLevelForAvailablity);
                // EA's body has no yield: restoring right after it undoes only its own-state line, the others' rolls stay
                if (refuse && own != null && ShortcutOn("bandjam")) own.CurrentPlayState = keep;
            }
            public void SweetOrig(Sim actor, int minLevelForAvailablity) { } // receives JamController.SetSweetMove's original body

            public void RemoveInstHook(BandInstrument bandInstrument)
            {
                RemoveInstOrig(bandInstrument);
                BjStopIfEmpty((object)this as JamController);
            }
            public void RemoveInstOrig(BandInstrument bandInstrument) { } // receives JamController.RemoveJammer(BandInstrument)

            public void RemoveSimHook(Sim actor, bool playExitAnim)
            {
                RemoveSimOrig(actor, playExitAnim);
                BjStopIfEmpty((object)this as JamController);
            }
            public void RemoveSimOrig(Sim actor, bool playExitAnim) { } // receives JamController.RemoveJammer(Sim, bool)
        }

        // EA's own-state line should run only for a jammer that can start a sweet move.
        static bool BjRefuse(JamInfo own)
        {
            if (own == null) return true;
            JamInfo.PlayState s = own.CurrentPlayState;
            return s != JamInfo.PlayState.Playing && s != JamInfo.PlayState.SweetMoveRequested;
        }

        static void BjStopIfEmpty(JamController jc)
        {
            try
            {
                if (jc == null || jc.NumJammers != 0 || sBjSound.GetValue(jc) == null || !ShortcutOn("bandjam")) return;
                sBjStop.Invoke(jc, null);
                PatchCounters()[2459]++;
            }
            catch (Exception) { PatchCounters()[2460]++; }
        }
    }
}
