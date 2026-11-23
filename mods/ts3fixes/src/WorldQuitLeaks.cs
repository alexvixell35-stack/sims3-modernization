// docs/323 lane D: static collections that kept dead Sims or old-world objects alive after a quit to menu or a travel.
// Each was bounded (cap-clear at 256, or a few entries) but had no world-quit clear; called from HeldRefsWorldQuit
// (OutfitWait.cs), which Instantiator wires to World.OnWorldQuitEventHandler.
//   M3  AutLive sMetaStart: searches that never reached the push (now keyed by actor ObjectId, cleared here too)
//   L7  ErrorTrapLoop sErrLoop: proxies whose task threw once or twice
//   L8  EpBatch4bLogic sJgPending: look-out tower exits still waiting
//   L19 EnvListExact sExLotOff: lot ids of the old world (a new world can reuse them)
namespace Ts3Fixes
{
    public static partial class Fixes
    {
        static void LeaksWorldQuit()
        {
            sMetaStart.Clear();
            sErrLoop.Clear();
            sJgPending.Clear();
            sExLotOff.Clear();
        }
    }
}
