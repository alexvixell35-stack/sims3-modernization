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
    // behind). Pet routes failed with DestinationPortalLocked on the Lionheart lot (phase-20261114-0117): which doors.
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
                o["rooms"] = World.GetRoomId(p + f * 0.6f) + "/" + World.GetRoomId(p - f * 0.6f);
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
