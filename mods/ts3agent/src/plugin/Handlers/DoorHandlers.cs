using System;
using System.Collections.Generic;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.SimIFace;

namespace Ts3Agent
{
    // doors (read only; docs/546 §8): every door on the active household's lot (or {"lot":"<lot id>"}) with its lock type
    // (CommonDoor.tLock: Anybody, Pets, ...), position, level and the room on each side (World.GetRoomId 0.6 m in front and
    // behind), lock owner (name, still a resident), owner-list size and gone ids, hidden sides, NPC door. Pet routes failed with DestinationPortalLocked on the Lionheart lot (phase-20261114-0117): which doors.
    public static partial class Handlers
    {
        static void RegisterDoors() { Dispatcher.Register("doors", Doors); }

        static object Doors(Args a)
        {
            Lot lot = null;
            if (a.Has("lot")) lot = LotManager.GetLot(a.ULong("lot"));
            else if (Household.ActiveHousehold != null) lot = Household.ActiveHousehold.LotHome;
            Dictionary<string, object> r = Util.Obj();
            if (lot == null) { r["error"] = "no lot"; return r; }
            r["lot"] = lot.Name;
            r["household"] = lot.Household == null ? "none" : lot.Household.Name;
            List<object> list = new List<object>();
            Dictionary<string, object> byLock = Util.Obj();
            foreach (CommonDoor d in lot.GetObjects<CommonDoor>())
            {
                if (d == null) continue;
                Dictionary<string, object> o = Util.Obj();
                Vector3 p = d.Position, f = d.ForwardVector;
                o["type"] = d.GetType().Name;
                o["lock"] = d.LockType.ToString();
                o["pos"] = Util.Round(p.x) + "," + Util.Round(p.y) + "," + Util.Round(p.z);
                o["level"] = d.Level;
                int ra = World.GetRoomId(p + f * 0.6f), rb = World.GetRoomId(p - f * 0.6f);
                o["rooms"] = ra + "/" + rb;
                // docs/582 B4 §1 / spawn-slot lead: who the lock is for, whether that owner still lives here, hidden sides
                SimDescription owner = d.LockOwner;
                if (owner != null)
                {
                    o["owner"] = owner.FullName;
                    o["owner_resident"] = lot.Household != null && owner.Household == lot.Household;
                    o["owner_household"] = owner.Household == null ? "none" : owner.Household.Name;
                }
                if (d.LockType == CommonDoor.tLock.OwnerList)
                {
                    System.Reflection.FieldInfo fl = typeof(CommonDoor).GetField("mOwnerList", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    List<ulong> ids = fl == null ? null : fl.GetValue(d) as List<ulong>;
                    int gone = 0;
                    if (ids != null) foreach (ulong id in ids) { SimDescription sd = SimDescription.Find(id); if (sd == null || lot.Household == null || sd.Household != lot.Household) gone++; }
                    o["owner_list"] = ids == null ? -1 : ids.Count;
                    o["owner_list_gone"] = gone;
                }
                if (lot.IsRoomHidden(ra) || lot.IsRoomHidden(rb)) o["hidden"] = (lot.IsRoomHidden(ra) ? ra + " " : "") + (lot.IsRoomHidden(rb) ? rb.ToString() : "");
                Door door = d as Door;
                if (door != null && door.IsNPCDoor) o["npc_door"] = true;
                list.Add(o);
                string k = d.LockType.ToString();
                byLock[k] = (byLock.ContainsKey(k) ? (int)byLock[k] : 0) + 1;
            }
            r["by_lock"] = byLock;
            r["doors"] = list;
            return r;
        }
    }
}
