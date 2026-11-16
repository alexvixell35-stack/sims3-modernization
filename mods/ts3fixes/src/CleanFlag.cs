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
    // cleanflag: docs/559 second-pass gameplay scan. Off by default. Harness check: CleanFlagChecks.cs.
    // cleanflag (tuning kCleanFlag; docs/559 §8). FirePit+PutAway and PicnicBasket+CleanUp set BeingCleaned, then return
    // true on their "already cleaned up" exit without clearing it [verified]. A pit then can never be lit, cleaned or
    // enjoyed (CanLight/CanEnjoy/CanClean all need !BeingCleaned) and a basket served again can never be eaten from or
    // cleaned up (two Sims queueing Clean Up is enough), until the object is reset. Fix: when the run that started with
    // the flag clear ends with it still set, it is cleared. Counters [2463] flags cleared (counted with the switch off
    // too), [2464] errors.
    public static partial class Fixes
    {

        static void ApplyCleanFlag()
        {
            Type pitPutAway = typeof(FirePit).GetNestedType("PutAway", BindingFlags.Public | BindingFlags.NonPublic);
            if (pitPutAway == null) AppliedLog().Add("cleanflag: FirePit+PutAway not found, fire pit part not patched");
            else Patch(pitPutAway, "Run", typeof(CfPitHook), "RunHook", "RunOrig");
            Patch(typeof(PicnicBasket.CleanUp), "Run", typeof(CfBasketHook), "RunHook", "RunOrig");
        }

        public class CfPitHook
        {
            public bool RunHook()
            {
                InteractionInstance ia = (object)this as InteractionInstance;
                FirePit pit = ia == null ? null : ia.Target as FirePit;
                bool was = pit != null && pit.BeingCleaned;
                try { return RunOrig(); }
                finally
                {
                    try { if (pit != null && CfLeftSet(was, pit.BeingCleaned)) pit.BeingCleaned = false; } catch (Exception) { PatchCounters()[2464]++; }
                }
            }
            public bool RunOrig() { return false; } // receives FirePit+PutAway.Run's original body
        }

        public class CfBasketHook
        {
            public bool RunHook()
            {
                InteractionInstance ia = (object)this as InteractionInstance;
                PicnicBasket basket = ia == null ? null : ia.Target as PicnicBasket;
                bool was = basket != null && basket.BeingCleaned;
                try { return RunOrig(); }
                finally
                {
                    try { if (basket != null && CfLeftSet(was, basket.BeingCleaned)) basket.BeingCleaned = false; } catch (Exception) { PatchCounters()[2464]++; }
                }
            }
            public bool RunOrig() { return false; } // receives PicnicBasket+CleanUp.Run's original body
        }

        // The run set the flag (clear at entry) and left it set: counted; true = clear it (switch on).
        static bool CfLeftSet(bool atEntry, bool atExit)
        {
            if (atEntry || !atExit) return false;
            PatchCounters()[2463]++;
            return ShortcutOn("cleanflag");
        }
    }
}
