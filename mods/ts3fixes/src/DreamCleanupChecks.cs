using System;
using System.Collections;
using System.Collections.Generic;
using Sims3.Gameplay.DreamsAndPromises;

namespace Ts3Fixes
{
    // Harness check for DreamCleanup.cs (docs/580 §18). Pure: the decisions and the index edits run on a model manager
    // (an index, an active list, a panel list, a promise slot and a listener count); nothing in the game is touched.
    public static partial class Fixes
    {
        // DreamCleanupCheck run: a removed pet-subject node leaves the index; a woken repeatable node is filed again.
        //   Controls: a Sim-subject node is left to EA (no extra unfile), a promised (never sleeping) node is not filed,
        //   an already-filed woken node is not filed twice, a node with no subject key is not filed, and the model's
        //   listener count, panel list and promise slot are identical before and after.
        public static object DreamCleanupCheck(string what)
        {
            try { return DreamCleanupCheckRun(what); }
            catch (Exception e)  // never an empty line (live 2026-11-15): name the throw so the run can be read
            {
                string at = e.StackTrace ?? "";
                int nl = at.IndexOf('\n');
                return "dreamcleanup check: threw " + e.GetType().Name + ": " + e.Message + " at " + (nl > 0 ? at.Substring(0, nl).Trim() : at.Trim()) + "; verdict: ERROR";
            }
        }

        static object DreamCleanupCheckRun(string what)
        {
            int[] c = PatchCounters();
            string counters = "dreamcleanup: unfiled 2804=" + c[2804] + ", woken filed 2805=" + c[2805] + ", seen with switch off 2806=" + c[2806]
                + ", errors 2807=" + c[2807] + "; " + CkMembers(sDcMissing);
            if (what == "result") return counters;
            if (what != "run") return "dreamcleanup <run|result>";
            bool on = ShortcutOn("dreamcleanup");
            DreamNodePrimitive.InputSubjectType Sim = DreamNodePrimitive.InputSubjectType.Sim, Pet = DreamNodePrimitive.InputSubjectType.Pet,
                None = DreamNodePrimitive.InputSubjectType.None;
            // model manager
            Dictionary<object, List<object>> index = new Dictionary<object, List<object>>();
            object petNode = new object(), wokeNode = new object(), simNode = new object();
            object petKey = (ulong)7, wokeKey = (ulong)9;
            index[petKey] = new List<object> { petNode };
            List<string> panel = new List<string> { "a", "b", "c" };
            string promise = "p";
            int listeners = 42;
            string before = string.Join(",", panel.ToArray()) + "|" + promise + "|" + listeners;
            // (1) a pet node removed by EA (its listener already gone)
            if (on && DcUnfile(true, Pet, false)) DcIndexRemove(index, petKey, petNode);
            bool unfiled = !index.ContainsKey(petKey);
            // (2) a repeatable node woke (was sleeping, now active, not filed)
            int kind = DcFileKind(Sim, Sim);
            if (on && DcRefile(true, true, false, kind, DcIndexHas(index, wokeKey, wokeNode))) DcIndexAdd(index, wokeKey, wokeNode, typeof(List<object>));
            bool refiled = DcIndexHas(index, wokeKey, wokeNode);
            string after = string.Join(",", panel.ToArray()) + "|" + promise + "|" + listeners;
            bool ctlSim = !DcUnfile(true, Sim, false);
            DreamNodePrimitive.InputSubjectType Obj = DreamNodePrimitive.InputSubjectType.Object;
            bool objGone = DcUnfile(true, Obj, true), ctlObjLive = !DcUnfile(true, Obj, false);
            Dictionary<object, List<object>> idx2 = new Dictionary<object, List<object>>(); object deadObj = new object(), objNode = new object();
            idx2[deadObj] = new List<object> { objNode };
            bool foundKey = DcFindKey(idx2, objNode) == deadObj;
            if (on && objGone && foundKey) DcIndexRemove(idx2, deadObj, objNode);
            bool objUnfiled = idx2.Count == 0;
            bool fixedNow = unfiled && refiled && objUnfiled;
            bool ctlFlag = !DcUnfile(false, Pet, false);
            bool ctlPromised = !DcRefile(false, true, false, kind, false);
            bool ctlTwice = !DcRefile(true, true, false, kind, true);
            bool ctlNoKey = !DcRefile(true, true, false, DcFileKind(None, None), false) && DcFileKind(Sim, Pet) == 0;
            bool ctlStillAsleep = !DcRefile(true, false, true, kind, false);
            bool ctlSame = before == after && !DcIndexHas(index, petKey, simNode);
            bool controls = ctlObjLive && ctlSim && ctlFlag && ctlPromised && ctlTwice && ctlNoKey && ctlStillAsleep && ctlSame;
            return "dreamcleanup check (switch " + (on ? "on" : "off") + "): removed pet node unfiled=" + unfiled + ", woken repeatable filed=" + refiled + ", destroyed-object node found and unfiled=" + objUnfiled
                + "; control: live-object node left to EA=" + ctlObjLive + ", Sim-subject node left to EA=" + ctlSim + ", kept-in-index removal left alone=" + ctlFlag + ", promised node not filed=" + ctlPromised
                + ", no double filing=" + ctlTwice + ", no subject key not filed=" + ctlNoKey + ", still sleeping not filed=" + ctlStillAsleep
                + ", listeners / panel / promise identical=" + ctlSame + "; controls ok=" + controls
                + "; verdict: " + CkVerdict(on, fixedNow, controls, sDcMissing.Length == 0) + "; " + counters;
        }
    }
}
