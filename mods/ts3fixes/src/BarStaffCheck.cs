using System;
using System.Collections.Generic;
using System.Text;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.ObjectComponents;
using Sims3.Gameplay.Objects.Counters;
using Sims3.Gameplay.Skills;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // BarStaffCheck (docs/558 §3, diagnostics only, no switch, read-only). The 2026-11-14 Bridgeport run phase-1835 had an
    // unseen bartender whose Tend on a BarProfessionalLounge failed 10 times (twice NoDestinationSpecified, then
    // FailedBecauseDestinationPortalLocked before any search), but no log line names the lot or the door. EA's door rules
    // [verified, CommonDoor.IsAllowedThrough]: a hidden room closes every door for everyone; only Firefighter, GrimReaper,
    // Police, SocialWorkerChildProtection, Burglar and Repoman services ignore locks; a Bartender role has no exemption from
    // Nobody / SelectedHousehold / SelectedActor / OwnerList locks (velvet ropes do let Bartender roles through,
    // VelvetRopes.kAllowedRoles). This check finds bars whose bartender spot cannot be reached so the next run names the case.
    //   scan  every BarAdvanced on a non-world lot: lot, the bartender (role Sim, or the bar's current mBartender), the room of
    //         the bar's bartender spot (RoutingSlot_6, else the bar), whether that room reaches outside for the bartender
    //         (HrReachesOutside: the game's own IsAllowedThrough per portal, hidden rooms included), and every portal of that
    //         room closed to the bartender with its lock type. "closed" rows are the PortalLocked candidates.
    public static partial class Fixes
    {
        public static object BarStaffCheck(string what)
        {
            if (what != "scan") return "barstaff <scan>";
            if (!GameStates.IsInWorld()) return "barstaff check: not in a world";
            Dictionary<BarAdvanced, Sim> roleSims = new Dictionary<BarAdvanced, Sim>();
            foreach (Sim s in LotManager.Actors)
            {
                if (s == null || s.SimDescription == null) continue;
                Bartending.Bartender role = s.SimDescription.AssignedRole as Bartending.Bartender;
                BarAdvanced b = role != null ? role.RoleGivingObject as BarAdvanced : null;
                if (b != null) roleSims[b] = s;
            }
            StringBuilder sb = new StringBuilder();
            int bars = 0, closed = 0;
            foreach (BarAdvanced bar in Sims3.Gameplay.Queries.GetObjects<BarAdvanced>())
            {
                Lot lot = bar != null ? bar.LotCurrent : null;
                if (lot == null || lot.IsWorldLot) continue;
                bars++;
                Sim staff;
                if (!roleSims.TryGetValue(bar, out staff)) staff = bar.Bartender;
                Sim probe = staff ?? HrProbe(lot, false);
                Vector3 spot = Vector3.Invalid;
                try { spot = bar.GetPositionOfSlot(Slot.RoutingSlot_6); } catch (Exception) { }
                if (spot == Vector3.Invalid || spot == Vector3.Zero) spot = bar.Position;
                int room = World.GetRoomId(spot);
                string reach = probe == null ? "no probe" : HrReach(lot, room, probe);
                if (reach == "closed") closed++;
                sb.Append(lot.Name).Append(" (0x").Append(lot.LotId.ToString("X")).Append(lot.IsCommunityLot ? " com" : " res").Append(") ")
                  .Append(bar.GetType().Name).Append(" @").Append(Math.Round(bar.Position.x, 1)).Append(",").Append(Math.Round(bar.Position.z, 1))
                  .Append(": bartender ").Append(staff != null ? staff.FullName + (roleSims.ContainsKey(bar) ? " (role)" : " (current)") : "none")
                  .Append(", spot room ").Append(room).Append(lot.IsRoomHidden(room) ? "(H)" : "").Append(", reach ").Append(reach)
                  .Append(probe != null && probe != staff ? " (NPC probe " + probe.FullName + ")" : "");
                if (probe != null && reach != "out")
                {
                    foreach (IPortalConnectionObject p in lot.GetObjectsInRoom<IPortalConnectionObject>(room))
                    {
                        int to = p.GetAdjoiningRoom(room);
                        PortalRestriction r = PortalRestriction.None;
                        try { r = p.IsAllowedThrough(room, to, probe); } catch (Exception) { }
                        if (r == PortalRestriction.None) continue;
                        CommonDoor d = p as CommonDoor;
                        sb.Append("\n    ").Append(p.GetType().Name).Append(" to room ").Append(to).Append(lot.IsRoomHidden(to) ? "(H)" : "")
                          .Append(": ").Append(r).Append(d != null ? ", lock " + d.LockType : "");
                    }
                }
                sb.Append("\n");
            }
            return "barstaff scan: bars " + bars + ", bartender spot closed " + closed + "\n" + sb.ToString();
        }
    }
}
