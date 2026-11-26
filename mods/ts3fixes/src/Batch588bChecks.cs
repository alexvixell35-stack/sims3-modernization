using System.Collections.Generic;

namespace Ts3Fixes
{
    // Harness checks for Batch588bLogic.cs (docs/588 batch 10 §16-19). Pure decisions with controls; nothing is touched.
    public static partial class Fixes
    {
        // DisposeVirtualSlotCheck run: a virtual-only household leaves a slot in EA. Controls: real lot / no lot.
        public static object DisposeVirtualSlotCheck(string what)
        {
            string counters = B582Counters("disposevirtualslot", 2974, new[] { "virtual move-outs at Dispose", "left in their slot (switch off)", "stale entries swept at load" }, sDvMissing);
            if (what == "result") return counters;
            if (what != "run") return "disposevirtualslot <run|result>";
            bool on = ShortcutOn("disposevirtualslot");
            bool fixedNow = on && DvLeaves(false, true);
            bool controls = !DvLeaves(true, true) && !DvLeaves(true, false) && !DvLeaves(false, false);
            return B582Line("disposevirtualslot", on, "virtual-only household moved out", fixedNow, "real lot / homeless unchanged=" + controls, controls, sDvMissing, counters);
        }

        // AgingAlarmCancelCheck run: an armed graduation alarm is removed by the cancel. Controls: none armed / off.
        public static object AgingAlarmCancelCheck(string what)
        {
            string counters = B582Counters("agingalarmcancel", 2977, new[] { "graduation alarms cancelled", "left armed (switch off)", "errors" }, sAcMissing);
            if (what == "result") return counters;
            if (what != "run") return "agingalarmcancel <run|result>";
            bool on = ShortcutOn("agingalarmcancel");
            bool fixedNow = on && !AcLeaves(true, true);
            bool controls = AcLeaves(false, true) && !AcLeaves(true, false);
            return B582Line("agingalarmcancel", on, "graduation alarm cancelled", fixedNow, "off leaves / none armed=" + controls, controls, sAcMissing, counters);
        }

        // VirtualMoveInDupCheck run: a listed Sim is a duplicate. Controls: world lot / new Sim.
        public static object VirtualMoveInDupCheck(string what)
        {
            string counters = B582Counters("virtualmoveindup", 2980, new[] { "repeat move-ins not added twice", "added twice (switch off)", "errors" }, "");
            if (what == "result") return counters;
            if (what != "run") return "virtualmoveindup <run|result>";
            bool on = ShortcutOn("virtualmoveindup");
            bool fixedNow = on && VmDup(false, true);
            bool controls = !VmDup(true, true) && !VmDup(false, false);
            return B582Line("virtualmoveindup", on, "repeat move-in not doubled", fixedNow, "world lot / new Sim unchanged=" + controls, controls, "", counters);
        }

        // PetPoolGhostCheck run: ids 1-3 with 2 gone keep 1 and 3. Control: all present keeps all.
        public static object PetPoolGhostCheck(string what)
        {
            string counters = B582Counters("petpoolghost", 2983, new[] { "ghost ids dropped", "ghost ids seen (switch off)", "errors" }, sPpgMissing);
            if (what == "result") return counters;
            if (what != "run") return "petpoolghost <run|result>";
            bool on = ShortcutOn("petpoolghost");
            List<ulong> k = PgKeep(new List<ulong> { 1, 2, 3 }, delegate (ulong id) { return id != 2; });
            bool fixedNow = on && k.Count == 2 && k[0] == 1 && k[1] == 3;
            bool controls = PgKeep(new List<ulong> { 1, 2 }, delegate (ulong id) { return true; }).Count == 2;
            return B582Line("petpoolghost", on, "ghost id dropped", fixedNow, "all present kept=" + controls, controls, sPpgMissing, counters);
        }
    }
}
