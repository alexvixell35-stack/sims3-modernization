using System.Collections.Generic;
using Sims3.Gameplay;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Objects;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Socializing;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Agent check for the spread-pass pause gate (docs/205 §2.3d, repro pausegate-spread-check; Sg* names: group 2 has its own
    // PauseGateCheck / Pg* in SimFrozen.cs). "start" starts every spread
    // pass now without its alarm (venue cleanup, test pet build, relationship decay, Overwatch inventory, ErrorTrap
    // inventory, save cleanup) and names those that started; "mark" keeps the counters and the active household's
    // relationships and inventories; "state" prints what changed since the mark.
    public static partial class Fixes
    {
        // The wait every spread pass runs before a slice (docs/205 §2.3d): while SimFrozenOrNotLive, a frame at a time with the
        // caller's place kept, so the pass finishes after unpause. -1 = stop() said stop (the caller's existing end), 0 = act
        // now, 1 = act after waiting. c[slot] counts frames waited. Callers inside a task only (Simulator.Sleep).
        // Group 2's SfWait (SimFrozen.cs, bugfix/pausegate-drains-1123) is the same loop with a fixed stop (SfLeaving, which also
        // ends on travel and game shutdown); kept apart because these passes need their own stop (WorldGone waits through travel,
        // the save cleanup's SpreadCleanupUnsafe, NotLiveOrGone).
        static int WaitWhileFrozen(int slot, Gate stop)
        {
            int[] c = PatchCounters();
            for (int waited = 0; ; waited = 1)
            {
                if (stop()) return -1;
                if (!SimFrozenOrNotLive()) return waited;
                c[slot]++;
                Simulator.Sleep(0);
            }
        }

        // stop() for a yield inside another mod's or EA's code (Overwatch's StuckCheck, ErrorTrap's load-up chain): wait only
        // while paused or the clock stopped in live mode; out of live mode (loading, CAS, Buy/Build, Edit Town, travel) act as
        // before, so a load-time caller never waits for a live mode that only comes after it returns.
        static bool NotLiveOrGone() { return !GameStates.IsLiveState || GameStates.IsTravelling || WorldGone(); }

        static readonly string[] kSgName = { "lots", "pets", "decay", "owinv", "et", "sc" };
        static readonly int[] kSgDone = { 177, 186, 189, 159, 167, 411 }; // passes finished per group ([186] pets: ticks, added once at the end)
        static bool[] sSgStarted = new bool[6];
        static int[] sSgMark = new int[12];
        static string sSgHhMark = "";

        public static string SpreadGateCheck(string what)
        {
            switch (what)
            {
                case "start": return "start: " + SgStart();
                case "mark": sSgMark = SgCounts(); sSgHhMark = SgHousehold(); return "mark: " + SgState();
                case "state": return "state: " + SgState();
                default: return "unknown check " + what + " (start, mark, state)";
            }
        }

        static string SgStart()
        {
            sSgStarted = new bool[6];
            if (ShortcutOn("easpreadlots") && !TaskAlive(sLotTask)) { LotCleanupHook.CleanHook(); sSgStarted[0] = true; }
            if (ShortcutOn("easpreadpets") && !TaskAlive(sPetTask)) { PetTask(BuildTestPet, "test pet"); sSgStarted[1] = true; }
            if (ShortcutOn("easpreaddecay") && !TaskAlive(sDecayTask)) { DecayHook.RunHook(); sSgStarted[2] = true; }
            if (sOwFind != null && sOwInvSetting != null && (bool)sOwInvSetting.GetValue(sOwSettings.GetValue(null, null)))
                foreach (object option in OwOptions()) if (OwIsInventory(option)) { OwStartTask(option, true); sSgStarted[3] = true; break; }
            sSgStarted[4] = ErrorTrapSpreadNow("ours") == "Ours";
            sSgStarted[5] = SaveCleanupSpreadStart();
            string s = "";
            for (int g = 0; g < 6; g++) if (sSgStarted[g]) s += kSgName[g] + " ";
            return s + "| " + (SpreadCleanupUnsafe() ?? "safe");
        }

        static int[] SgCounts()
        {
            int[] c = PatchCounters(), n = new int[12];
            for (int g = 0; g < 6; g++) { n[g] = c[kSgDone[g]]; n[6 + g] = c[1728 + g]; }
            return n;
        }

        // started S groups, done +D of them, W of them waited | per group +done/+waits | household fingerprint mark -> now
        static string SgState()
        {
            int[] n = SgCounts(), m = sSgMark;
            int started = 0, done = 0, waited = 0;
            string per = "";
            for (int g = 0; g < 6; g++)
            {
                int d = n[g] - m[g], w = n[6 + g] - m[6 + g];
                per += kSgName[g] + " +" + d + "/+" + w + (g < 5 ? ", " : "");
                if (!sSgStarted[g]) continue;
                started++;
                if (d > 0) done++;
                if (w > 0) waited++;
            }
            return "started " + started + ", done +" + done + ", waited " + waited + " of " + started + " | " + per
                + " | household " + sSgHhMark + " -> " + SgHousehold() + ", frozen " + SimFrozenOrNotLive();
        }

        // Active household: relationship pairs and summed liking, items in member, family and fridge inventories.
        static string SgHousehold()
        {
            Household hh = Household.ActiveHousehold;
            if (hh == null) return "none";
            int pairs = 0, items = 0;
            double liking = 0;
            foreach (SimDescription d in hh.AllSimDescriptions)
            {
                if (d == null) continue;
                Dictionary<SimDescription, Relationship> rel;
                if (Relationship.sAllRelationships.TryGetValue(d, out rel))
                    foreach (Relationship r in rel.Values) { pairs++; liking += r.LTR.Liking; }
                Sim s = d.CreatedSim;
                if (s != null) items += SgItems(s.Inventory);
            }
            if (hh.SharedFamilyInventory != null) items += SgItems(hh.SharedFamilyInventory.Inventory);
            if (hh.SharedFridgeInventory != null) items += SgItems(hh.SharedFridgeInventory.Inventory);
            return "rel " + pairs + "/" + liking.ToString("0.000") + " inv " + items;
        }

        static int SgItems(Inventory inv)
        {
            int n = 0;
            if (inv != null) foreach (InventoryStack st in inv.InventoryItems.Values) if (st != null && st.List != null) n += st.List.Count;
            return n;
        }
    }
}
