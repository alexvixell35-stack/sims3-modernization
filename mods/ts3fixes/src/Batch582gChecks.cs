namespace Ts3Fixes
{
    // Harness checks for Batch582gLogic.cs (docs/582 batch 8). Pure decisions with controls; nothing in the world is touched.
    public static partial class Fixes
    {
        // WeddingListenerCheck run: 1 registered listener -> 1 removed. Controls: switch off / none registered -> 0.
        public static object WeddingListenerCheck(string what)
        {
            string missing = sWlList == null ? "WeddingParty.mEventListeners" : "";
            string counters = B582Counters("weddinglistener", 2920, new[] { "listeners removed", "left with switch off", "errors" }, missing);
            if (what == "result") return counters;
            if (what != "run") return "weddinglistener <run|result>";
            bool on = ShortcutOn("weddinglistener");
            bool fixedNow = on && WlRemove(true, 1) == 1;
            bool controls = WlRemove(false, 1) == 0 && WlRemove(true, 0) == 0;
            return B582Line("weddinglistener", on, "ended wedding party drops its marriage listener", fixedNow, "off / none registered unchanged=" + controls, controls, missing, counters);
        }
    

    }
}
