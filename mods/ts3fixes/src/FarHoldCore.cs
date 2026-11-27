using System;
using System.Collections.Generic;

namespace Ts3Fixes
{
    // FarHold owner table (docs/346 §3.1, docs/351 §2; lane MS). No EA types: compiled into tools/selftest/farhold.
    // Two sets of 64-bit ids, each id with one owner tag: held Sims (only FS adds) and parked lots (only FO adds).
    // Runtime only, never saved; ids only, no reference to a Sim, a lot, a timer or a delegate.
    // Built here: the tables, the conflict rule, the Hook A decision for a parked lot (docs/346 §3.2 steps 1-2).
    // Not built: take mode for held Sims and for pass-through timers (steps 3-4, Hook B). Until it is, a held Sim id is
    // only a table row (Fixes.FarHoldSim answers false) and a parked lot is simulated while the pass-through set has a row.
    public static class FarHoldCore
    {
        public const int OwnerFS = 1, OwnerFO = 2, OwnerLZ = 3;
        // Hook A answers
        public const int Simulate = 0, Skip = 1, Would = 2, Evict = 3;

        static readonly Dictionary<ulong, int> sSims = new Dictionary<ulong, int>(), sLots = new Dictionary<ulong, int>();
        static readonly Dictionary<string, bool> sPass = new Dictionary<string, bool>();

        // sizes (gauges) and events; Fixes copies them into its counter slots
        public static int LotsHigh, SimsHigh, Parks, Releases, ReleaseAlls, OwnerConflict, Skipped, WouldSkip, Evicted, Refused, PassBlocked, Errors;
        public static string LastReleaseAll = "";
        public static int Lots { get { return sLots.Count; } }
        public static int Sims { get { return sSims.Count; } }
        public static int PassThroughs { get { return sPass.Count; } }

        // False (ownerConflict +1) when the tag may not own this kind or the id is held under another tag. The same owner again: true.
        static bool Add(Dictionary<ulong, int> set, ulong id, int owner, int allowed, ref int high)
        {
            if (owner != allowed) { OwnerConflict++; return false; }
            int cur;
            if (set.TryGetValue(id, out cur))
            {
                if (cur != owner) OwnerConflict++;
                return cur == owner;
            }
            set[id] = owner;
            Parks++;
            if (set.Count > high) high = set.Count;
            return true;
        }

        public static bool HoldSim(ulong simId, int owner) { return Add(sSims, simId, owner, OwnerFS, ref SimsHigh); }
        public static bool ParkLot(ulong lotId, int owner) { return Add(sLots, lotId, owner, OwnerFO, ref LotsHigh); }
        public static bool ReleaseSim(ulong simId) { bool r = sSims.Remove(simId); if (r) Releases++; return r; }
        public static bool ReleaseLot(ulong lotId) { bool r = sLots.Remove(lotId); if (r) Releases++; return r; }
        public static bool IsSimHeld(ulong simId) { return sSims.ContainsKey(simId); }
        public static bool IsLotParked(ulong lotId) { return sLots.ContainsKey(lotId); }
        public static void CopyLots(List<ulong> into) { into.AddRange(sLots.Keys); }
        public static int LotOwner(ulong lotId) { int o; return sLots.TryGetValue(lotId, out o) ? o : 0; }

        // Reasons (docs/351 §2): worldQuit, switchOff, standDown, selftest. Returns how many ids went.
        public static int ReleaseAll(string reason)
        {
            int n = sSims.Count + sLots.Count;
            sSims.Clear();
            sLots.Clear();
            ReleaseAlls++;
            Releases += n;
            LastReleaseAll = reason ?? "";
            return n;
        }

        // docs/351 §3: keyed by callback. Ships empty; bounded by the callers' start-up lists.
        public static void PassThrough(string declaringType, string method) { if (declaringType != null && method != null) sPass[declaringType + "." + method] = true; }
        public static bool IsPassThrough(string declaringType, string method) { return sPass.Count != 0 && sPass.ContainsKey(declaringType + "." + method); }

        // Hook A for one manager (docs/346 §3.2 steps 1-2, §3.6). parked: the manager's lot id is in the parked set.
        // exempt: world lot, active lot, the active household's home lot, a lot with a household, selected or unknown-scope Sim
        // on it, or scope not readable. frozen: pause, CAS, Build / Buy, Edit Town, travel, shutdown (never skip there).
        // Evict: the caller removes the lot from the set and logs (docs/350 EP5). Would: shadow, the manager is simulated.
        public static int Decide(bool on, bool shadow, bool parked, bool exempt, bool frozen)
        {
            if (!parked || (!on && !shadow)) return Simulate;
            if (exempt) { Evicted++; return Evict; }
            if (frozen) return Simulate;
            if (sPass.Count != 0) { PassBlocked++; return Simulate; }  // take mode not built: a lot with pass-through timers runs as EA wrote it
            if (shadow) { WouldSkip++; return Would; }  // shadow wins over on: count only
            Skipped++;
            return Skip;
        }

        // Selftests only: tables empty, counters to 0. World quit is ReleaseAll (the pass-through set is start-up data and
        // the event counters run for the session, as every other ts3fixes counter).
        public static void Reset()
        {
            sSims.Clear(); sLots.Clear(); sPass.Clear();
            LotsHigh = SimsHigh = Parks = Releases = ReleaseAlls = OwnerConflict = Skipped = WouldSkip = Evicted = Refused = PassBlocked = Errors = 0;
            LastReleaseAll = "";
        }
    }
}
