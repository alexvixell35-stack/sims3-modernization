using System;
using System.Collections.Generic;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Services;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // burglarapt (tuning kBurglarApt, off by default; docs/866). EA bug fix: the burglar picks loot no Sim can reach on
    // apartment lots.
    //
    // [verified, decompiled] BurglarSituation.Stealing.FindObjectToSteal lists the lot's objects through the private
    // Stealing.CanBeStolen (value > 0, not in use, stealable, not in IgnoreObjects, in the world, on the burglar's lot, has
    // containment slots and holds nothing) and picks the most valuable that fits the quota. CanBeStolen has no room test.
    // On an apartment lot (Lot.HasVirtualResidentialSlots) it accepts objects in the neighbours' hidden units, whose doors
    // refuse every Sim (CommonDoor.IsAllowedThrough answers DoorRestriction for a hidden room before any service or lock
    // rule), and in public rooms (hallways, lobby, outdoors: Lot.IsRoomPublic counts room 0), which EA's own
    // Steal+Definition.Test refuses. So each such valuable costs the burglar one Stealing state: a failed route plan into the
    // hidden unit (Steal.Run returns false) or a refused queue test (public room), then StealFailed ignores the object and
    // StealAgain starts the next state a sim-minute later. Neither path stalls: InteractionQueue.InsertInteraction and
    // OnValidInteractionFailed -> DeQueue -> CleanupInteraction both call the failure callback [verified].
    // Fix: a swap on CanBeStolen answers no for an object on an apartment lot in a hidden or public residential room, the
    // test EA's GameObject.CanBeRepossessed already uses (IsInHiddenResidentialRoom || IsInPublicResidentialRoom). Only
    // objects no Sim can steal there are dropped; the household's own unit is unchanged. It changes the burglar's choice on
    // the household's own home (EA bug fix, AGENTS.md household rule); no household Sim is touched. Switch off: EA's answer,
    // the would-refuse answers are counted.
    // Counters (per CanBeStolen answer, so one object counts once per search): [2195] answers turned to no, [2196] would be
    // turned to no with the switch off, [2197] spare, [2198] errors. Check: BurglarAptCheck.
    public static partial class Fixes
    {
        static bool sBaHooked;

        static void ApplyBurglarApt()
        {
            if (sBaHooked) return;
            sBaHooked = true;
            Patch(typeof(BurglarSituation.Stealing), "CanBeStolen", typeof(BaHook), "StealHook", "StealOrig");
        }

        // EA's CanBeStolen conditions without the situation's IgnoreObjects (read-only, for the scan).
        static bool BaEaAccepts(GameObject o, Lot lot)
        {
            if (o == null || o.HasBeenDestroyed || o.Value <= 0 || o.InUse || !o.IsStealable() || !o.InWorld || o.LotCurrent != lot) return false;
            Slot[] slots = o.GetContainmentSlots();
            return slots != null && o.GetContainedObjectList<GameObject>(slots).Count == 0;
        }

        // `this` is the BurglarSituation.Stealing at run time (method swap).
        public class BaHook
        {
            public bool StealHook(GameObject obj)
            {
                if (!StealOrig(obj)) return false;
                try
                {
                    Lot lot = obj.LotCurrent;
                    if (lot == null || !lot.HasVirtualResidentialSlots) return true;
                    if (!BaCore.Refuse(true, true, obj.IsInHiddenResidentialRoom, obj.IsInPublicResidentialRoom)) return true;
                    if (!ShortcutOn("burglarapt")) { PatchCounters()[2196]++; return true; }
                    PatchCounters()[2195]++;
                    return false;
                }
                catch (Exception) { PatchCounters()[2198]++; return true; }
            }
            public bool StealOrig(GameObject obj) { return false; } // receives Stealing.CanBeStolen's original body
        }

        // BurglarAptCheck run: BaCore for the bug layouts and controls; read-only.
        //   off BROKEN = EA keeps (a) a valuable in a hidden neighbour unit and (b) one in a public room of an apartment lot;
        //   on FIXED = both refused. Controls (same answer on and off): a house-lot object kept, a hidden room on a lot that is
        //   not an apartment (tomb) kept, the household's own-unit object kept, an object EA refuses stays refused.
        //   scan    every apartment lot: objects EA's CanBeStolen rules accept, split own unit / hidden / public, top hidden
        //           value, and the class of EA's most valuable pick; no burglar needed
        //   result  counters 2195-2198
        public static object BurglarAptCheck(string what)
        {
            int[] c = PatchCounters();
            string live = CkLive("burglarapt");  // "" when the CanBeStolen swap is in (CheckLiveMap.cs)
            string counters = "burglarapt: refused 2195=" + c[2195] + ", would refuse (off) 2196=" + c[2196] + ", errors 2198=" + c[2198]
                + "; live path: " + (live.Length == 0 ? "ok" : live);
            if (what == "result") return counters;
            if (what == "scan") return "burglarapt scan: " + BaScan() + "; " + counters;
            if (what != "run") return "burglarapt <run|scan|result>";
            bool on = ShortcutOn("burglarapt");
            bool a = !BaCore.Answer(on, true, true, true, false), b = !BaCore.Answer(on, true, true, false, true);
            int fixedN = (a ? 1 : 0) + (b ? 1 : 0);
            bool controlsOk = BaCore.Answer(on, true, false, false, false) && BaCore.Answer(on, true, false, true, false)
                && BaCore.Answer(on, true, true, false, false) && !BaCore.Answer(on, false, true, true, true) && !BaCore.Answer(on, false, false, false, false);
            string v = !on ? (fixedN == 0 ? "BROKEN" : "UNEXPECTED") : (fixedN == 2 ? "FIXED" : "UNEXPECTED");
            string verdict = !controlsOk || live.Length > 0 ? "UNEXPECTED" : v;
            return "burglarapt check (" + (on ? "on" : "off") + "): hidden-unit object refused=" + a + ", public-room object refused=" + b
                + " (" + v + "); controls ok=" + controlsOk + "; verdict: " + verdict + "; " + counters;
        }

        static string BaScan()
        {
            string r = "";
            int lots = 0;
            Lot home = Household.ActiveHouseholdLot;
            foreach (Lot lot in LotManager.AllLots)
            {
                if (lot == null || !lot.HasVirtualResidentialSlots) continue;
                lots++;
                int own = 0, hidden = 0, pub = 0, topHidden = 0, top = 0;
                string topHiddenName = "", topClass = "none";
                foreach (GameObject o in lot.GetObjects<GameObject>())
                {
                    if (!BaEaAccepts(o, lot)) continue;
                    bool h = o.IsInHiddenResidentialRoom, p = !h && o.IsInPublicResidentialRoom;
                    if (h) { hidden++; if (o.Value > topHidden) { topHidden = o.Value; topHiddenName = o.CatalogName; } }
                    else if (p) pub++;
                    else own++;
                    if (o.Value > top) { top = o.Value; topClass = h ? "hidden" : p ? "public" : "own"; }
                }
                r += " | lot " + lot.LotId + (lot == home ? " (home)" : "") + ": own " + own + ", hidden " + hidden + ", public " + pub
                    + (hidden > 0 ? ", top hidden " + topHidden + " " + topHiddenName : "") + ", EA's top pick " + topClass + (top > 0 ? " " + top : "");
            }
            return lots == 0 ? "no apartment lot in this world" : lots + " apartment lot(s)" + r;
        }
    }
}
