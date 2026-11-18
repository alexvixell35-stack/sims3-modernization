namespace Ts3Fixes
{
    // Game-independent decisions of the sites where the household exemption was found unneeded (docs/289, 2026-11-18):
    // EA bug fixes and hang guards that household, selected and unknown-scope Sims take too. Checked offline by
    // tools/selftest/hhaudit. No scope input on a function = the same answer for every Sim.
    public static class HhAuditCore
    {
        // woohootywait (PhoneLogic.cs, docs/573 §4): polls of 5 ticks (EA's own sleep) before the WooHooty wait gives up.
        // A hard ceiling on a wait EA never ends, for every Sim.
        public const int kWwCap = 400;

        // One poll of the wait for the texter to be free: 1 = keep waiting, 0 = free (go on as EA), 2 = give up.
        public static int WwPoll(bool on, int polls, bool actorGone, bool simGone, bool actorBusy)
        {
            if (on && (actorGone || simGone)) return 2;
            if (!actorBusy) return 0;
            return on && polls >= kWwCap ? 2 : 1;
        }

        // serviceshowalarm (Batch582Logic.cs, docs/582): show the worker before CleanUp removes its show alarm?
        // (EA's destroy test inverted: destroy, alive, service household, no virtual home.) A worker left hidden is hidden
        // for good, whoever it is, so there is no scope input.
        public static bool SsShowAtCleanUp(bool on, bool pending, bool destroy, bool alive, bool serviceHousehold, bool virtualHome)
        {
            bool destroyed = destroy && alive && serviceHousehold && !virtualHome;
            return on && pending && alive && !destroyed;
        }

        // exitcbsweep (ExitCbSweep.cs): remove this route-adaptor callback from the Sim's OnExitReasonsAdded?
        // current = the adaptor is still its route action's ScriptAdaptor; killType = the action's RouteKillType.
        // A live route's own callback always stays. A killed action's callback is the one EA's Kill forgot (it can eat the
        // replan reason of the live route): removed for every Sim. A replaced adaptor on a live action may carry another
        // reason mask, so removing it is not proven equal to vanilla: townies only.
        public static bool XcbRemove(bool current, int killType, bool player)
        {
            if (killType != 0) return true;
            return !current && !player;
        }

        // hiddenroomspawn (HiddenRoomSpawn.cs, docs/289): act on a placement? No scope input: household, selected and unknown-scope
        // Sims too, because it only acts when EA's spot is a hidden or closed room (vanilla traps the Sim there for good).
        public static bool HrActs(bool on, bool spotBad) { return on && spotBad; }

        // residentspawnslot (ResidentSpawnSlot.cs, docs/289): test the resident's own door slot against its VirtualLotHome? No scope input.
        public static bool RsUseHome(bool on, bool hasHome, bool sameLot) { return on && hasHome && !sameLot; }

        // deadrefconvo (DeadRefs.cs) and ghostreactdeadsim (DeadSimHolders.cs), docs/289: drop a gone Sim from a live holder's list.
        // Saved data, household too (Rodney 2026-11-18); a live Sim is never dropped.
        public static bool DeadHolderDrop(bool on, bool simGone) { return on && simGone; }
    }
}
