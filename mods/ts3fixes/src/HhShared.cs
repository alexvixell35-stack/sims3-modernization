using System;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // What the household fixes share (docs/686 §41; rules in HhSharedCore.cs). No switch and no hook of its own: each
    // helper only acts for a fix whose switch is on.
    //   HhGraceMin / HhGraceMs     the one grace (kClipGraceMinutes): clipwait, simpass, petrelax, seatreach, seatclash
    //   HhDirected / HhPlayerOrder the one "not the Sim's own pick" test: facilityplan, lineswitch, clipwait, seatclash, mopclaim
    //   HhHold / HhHolder / HhDrop reservations in nextplan's claim table (NextPlanCore.Hold): a chair walked to, a puddle
    //                              being mopped; every search (nextplan's pick factor) and every fix reads the same table
    //   HhDoorLockedAgainst        the player's door lock: a room whose doors the player locked against a Sim stays closed to it
    //   HhSnapSeen / HhSnap        a Sim put somewhere without a walk fades out and in when it can be seen (camsafe's
    //                              on-screen answer through UcOnScreen)
    public static partial class Fixes
    {
        static float HhGraceMin() { return HhSharedCore.GraceMin(Instantiator.kClipGraceMinutes); }
        static int HhGraceMs() { return HhSharedCore.GraceMs(Instantiator.kClipGraceMinutes); }

        static bool HhDirected(InteractionInstance ii) { return HhSharedCore.Directed(ii != null, ii != null && ii.Autonomous); }
        static bool HhPlayerOrder(InteractionInstance ii) { return ii != null && HhSharedCore.PlayerOrder(true, ii.Autonomous, IsPlayerSim(ii.InstanceActor)); }

        static long sHhTicksPerMin;
        static long HhTicks(float minutes)
        {
            if (sHhTicksPerMin <= 0) { long m = SimClock.ConvertToTicks(1f, TimeUnit.Minutes); sHhTicksPerMin = m > 0 ? m : 1; }
            return (long)(minutes * sHhTicksPerMin);
        }

        // sim reserves target (an object id, or a fix's own key such as a puddle's tile) for `minutes`. false: table full.
        static bool HhHold(ulong target, Sim sim, float minutes)
        {
            if (target == 0 || sim == null) return false;
            return sNp.Hold(target, sim.ObjectId.Value, SimClock.CurrentTicks, HhTicks(minutes));
        }

        // the object id of the Sim that claims or holds target, 0 when none (an expired entry is dropped here)
        static ulong HhHolder(ulong target) { return target == 0 || sNp.Count == 0 ? 0 : sNp.Holder(target, SimClock.CurrentTicks); }

        static bool HhHeldByOther(ulong target, Sim sim)
        {
            ulong h = HhHolder(target);
            return h != 0 && (sim == null || h != sim.ObjectId.Value);
        }

        static void HhDrop(ulong target, Sim sim) { if (target != 0 && sim != null) sNp.Drop(target, sim.ObjectId.Value); }

        static Sim HhSim(ulong id)
        {
            if (id == 0) return null;
            Sim s = GameObject.GetObject(new ObjectGuid(id)) as Sim;
            return s == null || s.HasBeenDestroyed ? null : s;
        }

        // A door the player locked (CommonDoor lock type other than Anybody) refuses this Sim, and the room has no door
        // that lets it through: the room is closed to the Sim by the player's choice. No fix may put a Sim in there or
        // tell it the room is open. (EA's automatic privacy is not a door lock and is not asked here.)
        static bool HhDoorLockedAgainst(Sim sim, Sims3.Gameplay.Core.Lot lot, int roomId)
        {
            if (sim == null || lot == null) return false;
            bool locked = false;
            foreach (CommonDoor d in lot.GetObjects<CommonDoor>())
            {
                if (d == null || d.HasBeenDestroyed) continue;
                int[] rc = d.GetRoomConnections();
                if (rc == null || rc.Length < 2 || (rc[0] != roomId && rc[1] != roomId)) continue;
                if (d.LockType == CommonDoor.tLock.Anybody || d.IsAllowedThrough(sim)) return false;  // a way in the player left open
                locked = true;
            }
            return locked;
        }

        static bool HhSnapSeen(Sim sim, Vector3 to)
        {
            try { return HhSharedCore.SnapFade(UcOnScreen(sim), !FarFromCamera(sim.Position), !FarFromCamera(to)); }
            catch (ResetException) { throw; }
            catch (Exception) { return true; }
        }

        // The move itself: position, facing when valid, faded when `fade` (the caller asks HhSnapSeen first and counts).
        static void HhSnap(Sim sim, Vector3 to, Vector3 forward, bool setForward, bool fade)
        {
            if (fade) sim.FadeOut(true, false);
            sim.SetPosition(to);
            if (setForward) sim.SetForward(forward);
            if (fade) sim.FadeIn();
        }
    }
}
