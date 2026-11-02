using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Objects.Vehicles;

namespace Ts3Fixes
{
    // npcdriverpool (issue #166 build 37, docs/514): NpcDriversManager keeps released drivers' descriptions in one stack
    // per driver kind (mDescPools, saved with CarNpcManager) and CreateNpcDriver hands the top one out again whenever its
    // Sim is gone. Nothing checks that the description is still a live NPC: a disposed one, or one that left the NPC
    // household, is instantiated with no household, and Motives.CreateMotives and then SimDescription.Instantiate throw
    // NullReferenceException for a taxi, limo, bus or fire-truck driver.
    // Fix, switch on: before EA's method, the bad entries on top of the pool EA reads are popped (NpcDriverPoolCore).
    // EA then reuses the next good entry or makes a new driver, as for an empty pool. [2041] calls that found a bad
    // description EA would hand out (also with the switch off), [2042] entries dropped.
    public static partial class Fixes
    {
        public class NpcDriverPoolHook
        {
            public Sim CreateHook(NpcDriversManager.NpcDrivers driverType)
            {
                try
                {
                    Stack<SimDescription> pool = NdPool((NpcDriversManager)(object)this, driverType);
                    if (pool != null && pool.Count > 0 && NdBad(pool.Peek()) && NdHandedOut(pool.Peek()))
                    {
                        PatchCounters()[2041]++;
                        if (ShortcutOn("npcdriverpool"))
                        {
                            int n = NpcDriverPoolCore.PruneTop(pool, NdBad);
                            PatchCounters()[2042] += n;
                            if (sNdLines++ < 5) ShortcutLog("npcdriverpool: " + driverType + " pool: dropped " + n + " pooled description(s) that are not live NPCs; EA reuses the next one or makes a new driver");
                        }
                    }
                }
                catch (Exception) { } // the check must never stop EA's method
                return CreateOrig(driverType);
            }
            public Sim CreateOrig(NpcDriversManager.NpcDrivers driverType) { return null; } // receives NpcDriversManager.CreateNpcDriver's original header
        }
        static int sNdLines;
        static readonly FieldInfo sNdPools = typeof(NpcDriversManager).GetField("mDescPools", kInstAll);

        static bool NdBad(SimDescription d)
        {
            return NpcDriverPoolCore.Bad(d == null, d != null && !d.IsValidDescription, d != null && d.Household != Household.NpcHousehold);
        }
        // EA reuses the top entry only when it has no Sim; a null entry throws on that test itself
        static bool NdHandedOut(SimDescription d) { return d == null || d.CreatedSim == null; }

        // the stack EA's CreateNpcDriver reads: index = the driver kind minus Taxi, as EA computes it
        static Stack<SimDescription> NdPool(NpcDriversManager m, NpcDriversManager.NpcDrivers t)
        {
            Stack<SimDescription>[] pools = m != null && sNdPools != null ? sNdPools.GetValue(m) as Stack<SimDescription>[] : null;
            uint i = unchecked((uint)(ulong)t - (uint)(ulong)NpcDriversManager.NpcDrivers.Taxi);
            return pools != null && i < pools.Length ? pools[i] : null;
        }

        // Agent checks for docs/514 (repro npc-driver-pool-check), taxi pool:
        //   "make": EA makes a taxi driver (CreateNpcDriver), releases it (ReleaseNpcDriver pushes its description on the
        //           pool), destroys the Sim and takes the description out of the NPC household: the state of run 21 (a
        //           pooled driver with outfits and no household). Throws when EA made no driver.
        //   "expect handed out" (switch off) / "expect dropped" (switch on): throws unless that description is on top of
        //           the pool with no Sim, then calls CreateNpcDriver(Taxi). Off: throws unless EA handed it out (an
        //           exception, or a Sim built from it), seen +1, dropped +0. On: throws unless no exception, a new driver
        //           from another description in the NPC household, our description off the pool, seen +1, dropped +1.
        //           The on arm releases and destroys its new driver again.
        static SimDescription sNdDesc;
        public static string NpcDriverPoolCheck(string what)
        {
            CarNpcManager cm = CarNpcManager.Singleton;
            NpcDriversManager m = cm != null ? cm.NpcDriversManager : null;
            if (m == null) throw new InvalidOperationException("npcdriverpool " + what + ": no NpcDriversManager");
            NpcDriversManager.NpcDrivers taxi = NpcDriversManager.NpcDrivers.Taxi;
            if (what == "make")
            {
                Sim s = m.CreateNpcDriver(taxi);
                if (s == null) throw new InvalidOperationException("npcdriverpool make: EA made no taxi driver");
                SimDescription d = s.SimDescription;
                m.ReleaseNpcDriver(s);
                s.Destroy();
                if (d.Household != null) d.Household.Remove(d);
                sNdDesc = d;
                Stack<SimDescription> p = NdPool(m, taxi);
                return d.FullName + " released, destroyed, out of the NPC household; on top of the pool " + (p != null && p.Count > 0 && p.Peek() == d) + ", household " + (d.Household != null ? "still set" : "none");
            }
            if (what != "expect handed out" && what != "expect dropped") return "unknown check " + what + " (make, expect handed out, expect dropped)";
            SimDescription bad = sNdDesc;
            sNdDesc = null;
            if (bad == null) throw new InvalidOperationException("npcdriverpool " + what + ": make first");
            Stack<SimDescription> pool = NdPool(m, taxi);
            if (pool == null || pool.Count == 0 || pool.Peek() != bad || bad.CreatedSim != null || bad.Household != null)
                throw new InvalidOperationException("npcdriverpool " + what + ": the case was not built (top is ours " + (pool != null && pool.Count > 0 && pool.Peek() == bad) + ", Sim " + (bad.CreatedSim != null ? "still there" : "gone") + ", household " + (bad.Household != null ? "set" : "none") + ")");
            int seen0 = PatchCounters()[2041], drop0 = PatchCounters()[2042];
            Exception got = null;
            Sim r = null;
            try { r = m.CreateNpcDriver(taxi); }
            catch (Exception e) { got = e; }
            int seen = PatchCounters()[2041] - seen0, drop = PatchCounters()[2042] - drop0;
            bool on = ShortcutOn("npcdriverpool");
            bool fromBad = r != null && r.SimDescription == bad;
            bool stillPooled = pool.Contains(bad);
            bool ok = what == "expect handed out"
                ? !on && (got != null || fromBad) && seen == 1 && drop == 0
                : on && got == null && r != null && !fromBad && r.SimDescription.Household == Household.NpcHousehold && !stillPooled && seen == 1 && drop == 1;
            string st = bad.FullName + ": " + (got != null ? got.GetType().Name : r == null ? "no driver" : fromBad ? "driver built from it" : "new driver " + r.FullName)
                + ", still pooled " + stillPooled + ", seen +" + seen + ", dropped +" + drop;
            if (on && r != null && !fromBad) { try { m.ReleaseNpcDriver(r); r.Destroy(); } catch (Exception) { } }
            if (!ok) throw new InvalidOperationException("npcdriverpool " + what + ": wrong outcome (switch " + on + "); " + st);
            return "as expected; " + st;
        }
    }
}
