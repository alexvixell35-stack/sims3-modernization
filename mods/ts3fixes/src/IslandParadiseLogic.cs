using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Controllers;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Objects.Island;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Island Paradise logic fixes (docs/572 §6-7), each off by default.
    //
    // seasickpaused (kSeasickPaused, docs/572 §6) [verified, Seasickness.cs / Houseboat.cs SetStatus / Sim.LotChanged]:
    //   the seasickness alarm adds Seasick and pauses it at once (it should not run down while the boat sails). Only
    //   Houseboat.SetStatus(Docked / StationaryAtSea) unpauses it, and only for Sims on the houseboat lot at that moment.
    //   Sim.LotChanged removes the alarm when a Sim leaves a houseboat lot but never unpauses (the small Boat does on exit),
    //   and it adds the alarm for any status but Docked - also StationaryAtSea, where SetStatus removes alarms on purpose.
    //   A Sim who leaves a sailing houseboat, or comes aboard a parked one, keeps a paused Seasick that never times out
    //   (with its vomit alarm). Fix: after EA's LotChanged, leaving a houseboat lot unpauses Seasick, and arriving on a
    //   houseboat that is not sailing removes the alarm and unpauses, as SetStatus does for the Sims aboard.
    //   Counters [2616] buffs unpaused / alarms dropped, [2617] errors.
    // resortweddingalarm (kResortWeddingAlarm, docs/572 §7) [verified, ResortManager.cs SetNPCWedding / WeddingCallback]:
    //   SetNPCWedding schedules the NPC arch wedding only while mWeddingArchAlarm is invalid; WeddingCallback resets it
    //   only when the wedding happens, and retries only before the latest hour. A failed wedding at or after that hour (or
    //   an exception in TryHaveWedding) leaves the fired handle set, so that resort never schedules another NPC wedding
    //   until the game is restarted. Fix: when WeddingCallback ends without setting a new alarm, the handle is set back to
    //   invalid. Counters [2618] handles released, [2619] errors.
    // (The RefreshVIPRooms collection-modified throw is fixed by viproomrefresh, VipRoomRefresh.cs, docs/555 §3.)
    // Checks: IslandParadiseLogicChecks.cs.
    public static partial class Fixes
    {
        static readonly FieldInfo sRwAlarm = typeof(ResortManager).GetField("mWeddingArchAlarm", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        static void ApplyIslandParadiseLogic()
        {
            Patch(typeof(Sim), "LotChanged", typeof(SeasickHook), "LotHook", "LotOrig");
            if (sRwAlarm == null) AppliedLog().Add("resortweddingalarm: ResortManager.mWeddingArchAlarm not found, not patched");
            else Patch(typeof(ResortManager), "WeddingCallback", typeof(ResortWeddingHook), "WeddingHook", "WeddingOrig");
        }

        // ---- seasickpaused ----
        internal static bool SsUnpauseOnLeave(bool on, bool leftHouseboat, bool nowOnSameHouseboat, bool hasSeasick)
        {
            return on && leftHouseboat && !nowOnSameHouseboat && hasSeasick;
        }

        internal static bool SsFixArrival(bool on, bool arrivedOnHouseboat, bool sailing)
        {
            return on && arrivedOnHouseboat && !sailing;
        }

        public class SeasickHook
        {
            public void LotHook(Lot previousLot, int previousLevel, int previousRoomId)
            {
                LotOrig(previousLot, previousLevel, previousRoomId);
                try
                {
                    if (!ShortcutOn("seasickpaused")) return;
                    Sim me = (Sim)(object)this;
                    BuffManager bm = me.BuffManager;
                    Lot now = me.LotCurrent;
                    if (bm == null) return;
                    bool leftHb = previousLot != null && previousLot.IsHouseboatLot();
                    if (SsUnpauseOnLeave(true, leftHb, now == previousLot, bm.HasElement(BuffNames.Seasick)))
                    {
                        bm.UnpauseBuff(BuffNames.Seasick);
                        PatchCounters()[2616]++;
                    }
                    Houseboat hb = now != null && now != previousLot && now.IsHouseboatLot() ? now.GetHouseboat() : null;
                    if (hb != null && SsFixArrival(true, true, hb.CurrentStatus == Houseboat.Status.Routing))
                    {
                        bool did = false;
                        if (hb.CurrentStatus == Houseboat.Status.StationaryAtSea) { Sims3.Gameplay.ActorSystems.Seasickness.RemoveSeasicknessAlarm(hb, me); did = true; }
                        if (bm.HasElement(BuffNames.Seasick)) { bm.UnpauseBuff(BuffNames.Seasick); did = true; }
                        if (did) PatchCounters()[2616]++;
                    }
                }
                catch (Exception) { PatchCounters()[2617]++; }
            }
            public void LotOrig(Lot previousLot, int previousLevel, int previousRoomId) { }
        }

        // ---- resortweddingalarm ----
        internal static bool RwRelease(bool on, bool handleUnchanged)
        {
            return on && handleUnchanged;
        }

        public class ResortWeddingHook
        {
            public void WeddingHook()
            {
                AlarmHandle before = AlarmHandle.kInvalidHandle;
                try { before = (AlarmHandle)sRwAlarm.GetValue((object)this); } catch (Exception) { PatchCounters()[2619]++; }
                try { WeddingOrig(); }
                finally
                {
                    try
                    {
                        AlarmHandle after = (AlarmHandle)sRwAlarm.GetValue((object)this);
                        if (after != AlarmHandle.kInvalidHandle && RwRelease(ShortcutOn("resortweddingalarm"), after == before))
                        {
                            sRwAlarm.SetValue((object)this, AlarmHandle.kInvalidHandle);  // the alarm that just fired; no retry was set
                            PatchCounters()[2618]++;
                        }
                    }
                    catch (Exception) { PatchCounters()[2619]++; }
                }
            }
            public void WeddingOrig() { }
        }
    }
}
