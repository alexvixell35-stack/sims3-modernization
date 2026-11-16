using System;
using System.Collections.Generic;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.ObjectComponents;
using Sims3.SimIFace;
using Sims3.Gameplay.Utilities;

namespace Ts3Fixes
{
    // Batch 12c checks (docs/592): each runs the hook's own pure decision with controls, the hook's members found at
    // Apply (same handles) and a live-path assert (the hook class is in sPatched). Read-only.
    public static partial class Fixes
    {
        public static object LeaveConvNullCheck(string what)
        {
            bool f = false, c = false;
            try {  f = LvSkip(false); c = !LvSkip(true); } catch (Exception) { }
            return B12Check(what, "leaveconvnull", 3172, new string[] { "empty leaves skipped", "counted (switch off)", "errors" }, "", "no conversation skipped", f, "conversation leaves as EA", c);
        }

        public static object VacationAlarmsCheck(string what)
        {
            bool f = false, c = false;
            try {  f = VaFixTicks(true, 100, 40) == 100; c = VaFixTicks(false, 100, 40) == 140; } catch (Exception) { }
            return B12Check(what, "vacationalarms", 3175, new string[] { "arrivals fixed", "arrivals with the double shift (switch off)", "errors" }, sVaM12, "arrival fixed at the current tick", f, "EA adds the advance again", c);
        }

        public static object CleanupVfxCheck(string what)
        {
            bool f = false, c = false;
            try {  f = CvDispose(true, false); c = !CvDispose(true, true) && CvDispose(false, true) && !CvDispose(false, false); } catch (Exception) { }
            return B12Check(what, "cleanupvfx", 3178, new string[] { "effect lists disposed", "left to the finalizer (switch off)", "errors" }, sCvM12, "live game disposes", f, "shutdown / EA rule", c);
        }

        public static object LookAtCopyCheck(string what)
        {
            bool f = false, c = false;
            try { List<GameObject> live = new List<GameObject>(); live.Add(null); List<GameObject> lf = LaFilter(live);  f = lf != null && !ReferenceEquals(lf, live) && live.Count == 1; c = LaFilter(null) == null && LaFilter(new List<GameObject>()) == null; } catch (Exception) { }
            return B12Check(what, "lookatcopy", 3181, new string[] { "copies filtered", "live lists cut (switch off)", "errors" }, "", "filtered copy, live list kept", f, "null / empty answer null", c);
        }

        public static object LottoTooltipCheck(string what)
        {
            bool f = false, c = false;
            try {  f = LtClosed(19f, 20f, 18f) && LtTicks(20.5f) == SimClock.ConvertToTicks(20, TimeUnit.Hours) + SimClock.ConvertToTicks(30, TimeUnit.Minutes); c = !LtClosed(21f, 20f, 18f) && !LtClosed(10f, 20f, 18f); } catch (Exception) { }
            return B12Check(what, "lottotooltip", 3184, new string[] { "tooltips fixed", "shifted tooltips (switch off)", "errors" }, sLtM12, "19:00 closed, 20:30 as a time of day", f, "21:00 / 10:00 open", c);
        }

        public static object TraitIconDislikeCheck(string what)
        {
            bool f = false, c = false;
            try {  f = TiIcon(true, true, -1f) == 2 && TiIcon(false, true, -1f) == 0; c = TiIcon(true, true, 1f) == 1 && TiIcon(true, false, -1f) == 0; } catch (Exception) { }
            return B12Check(what, "traiticondislike", 3187, new string[] { "dislike icons shown", "hidden (switch off)", "errors" }, "", "lowered trait motive shows dislike", f, "raised motive trait icon, no motive skipped", c);
        }

        public static object RoleUnprotectCheck(string what)
        {
            bool f = false, c = false;
            try {  f = RuUnprotect(true, false); c = !RuUnprotect(true, true) && !RuUnprotect(false, false); } catch (Exception) { }
            return B12Check(what, "roleunprotect", 3190, new string[] { "protections removed", "left (switch off)", "errors" }, sRuM12, "held protection removed", f, "shutdown / not protected untouched", c);
        }

        public static object PutDownPlacedCheck(string what)
        {
            bool f = false, c = false;
            try {  f = PpAction(true, true, SurfaceAddOn.Action.ItemRemoved) == SurfaceAddOn.Action.ItemPlaced; c = PpAction(false, true, SurfaceAddOn.Action.ItemRemoved) == SurfaceAddOn.Action.ItemRemoved && PpAction(true, false, SurfaceAddOn.Action.ItemRemoved) == SurfaceAddOn.Action.ItemRemoved; } catch (Exception) { }
            return B12Check(what, "putdownplaced", 3193, new string[] { "placements fired as placed", "fired as removed (switch off)", "errors" }, "", "own removal fired as placed", f, "EA rule / other events unchanged", c);
        }
    }
}
