using System;
using System.Collections.Generic;

namespace Ts3Fixes
{
    // Far-object lot machine (docs/350 §4, §5; lane FO), the part FarLight needs. No EA types: compiled into tools/selftest/farobj.
    // Built: states Live, GoFarPending, FarLight, Excluded and transitions L1, L2, L4, L6, L11, L12, L13, L14 (FarLight side).
    // L6 as built: the caller resets the lot's pulse timers and pays the skipped zombie time once (Leave / TakeLeft).
    // Not built: FarParked, PendingLive (L3, L5, L7-L10, L15), F(t), due queue, seed, sidecar. G-model always fails here, so a
    // lot whose dwell is met takes FarLight, never FarParked.
    // A record exists only for GoFarPending and FarLight lots (Live and Excluded lots have none) and holds ids and numbers
    // only. One drop function removes a record and releases the lot from FarHold.
    public static class FarObjModel
    {
        public const int Live = 0, GoFarPending = 1, FarLight = 2, FarParked = 3, PendingLive = 4, Excluded = 5;
        // docs/350 §4.3 rows as bits; XBoat is ours (a houseboat lot moves: never far in this build)
        public const int X1 = 1, X2 = 2, X3 = 4, X4 = 8, X6 = 16, X7 = 32, X8 = 64, X9 = 128, X10 = 256, XBoat = 512;
        public const int kHouseholdRows = X1 | X2 | X3 | X4 | X6 | X8;
        // per-pulse answers
        public const int Run = 0, Skip = 1, Would = 2;

        // docs/350 §14 defaults; read at use, so a harness can shorten them
        public static float ParkEdgeM = 200f, NearEdgeM = 150f, MinPulseM = 10f;
        public static int ParkDwellMin = 30, BoundaryPassMin = 5;

        public sealed class Rec
        {
            public int State, Pass, Dec;
            public long Since, At = -1;      // dwell start (sim ticks); tick of the cached pulse answer
            public bool Skipped;             // a pulse was really skipped since the lot took FarLight (never in shadow)
            public float ZombieOwed;         // sim minutes of skipped UpdateNumZombiesOnLot calls, paid back at go-near (L6)
            public float X, Y, Z, R;         // lot centre and bounding radius, as of the last pass
        }

        static readonly Dictionary<ulong, Rec> sRecs = new Dictionary<ulong, Rec>();
        static readonly List<ulong> sTmp = new List<ulong>();

        public static int RecordsHigh, LightLots, LightIn, GoNear, Gone, BlockSim, ExclHousehold, ExclOther, Drops, NearCall, SimArrive, HhCaught, LeftLost;

        // L6 work for the caller: lots that left FarLight after a real skip, with the zombie time they are owed. The caller
        // takes the rows in the same call that produced them (pulse timers reset, zombie body paid once on its next run).
        public const int kLeftMax = 65536;
        static readonly List<ulong> sLeftIds = new List<ulong>();
        static readonly List<float> sLeftOwed = new List<float>();
        public static int LeftCount { get { return sLeftIds.Count; } }
        public static void TakeLeft(List<ulong> ids, List<float> owed) { ids.AddRange(sLeftIds); owed.AddRange(sLeftOwed); ClearLeft(); }
        public static void ClearLeft() { sLeftIds.Clear(); sLeftOwed.Clear(); }
        static void Leave(ulong lot, Rec r)
        {
            LightLots--;
            if (!r.Skipped) return;  // shadow, or nothing was skipped: the lot is exactly as EA left it
            if (sLeftIds.Count >= kLeftMax) { LeftLost += sLeftIds.Count; ClearLeft(); }  // a caller that never takes them
            sLeftIds.Add(lot); sLeftOwed.Add(r.ZombieOwed);
            r.Skipped = false; r.ZombieOwed = 0f;
        }
        public static int Records { get { return sRecs.Count; } }
        public static Rec Get(ulong lot) { Rec r; return sRecs.TryGetValue(lot, out r) ? r : null; }
        public static int State(ulong lot) { Rec r; return sRecs.TryGetValue(lot, out r) ? r.State : Live; }

        // The one drop function (docs/350 §13.3): the record goes and the lot leaves FarHold. Leaving FarLight after a real
        // skip queues the L6 row (Leave) for the caller.
        public static bool Drop(ulong lot)
        {
            Rec r;
            if (!sRecs.TryGetValue(lot, out r)) return false;
            if (r.State == FarLight) Leave(lot, r);
            sRecs.Remove(lot);
            FarHoldCore.ReleaseLot(lot);
            return true;
        }

        // The slow near test (every Sim against the lot's circle) is only worth asking for a lot that is, or is about to be, FarLight.
        public static bool NeedsNear(ulong lot, long now, long dwellTicks)
        {
            Rec r;
            return sRecs.TryGetValue(lot, out r) && (r.State == FarLight || now - r.Since >= dwellTicks);
        }

        // One lot in the boundary pass (EP1 and EP2 in one call: the rows are read on the tick of the transition).
        // edgeDist: nearest viewer to the lot's bounding circle. excl: §4.3 rows that hold. simNear: any Sim on the lot or
        // within its pulse radius (every Sim is EA-owned until the far-Sim model owns some). lightOn: farobjlight or the shadow.
        public static int Judge(ulong lot, int pass, long now, long dwellTicks, float edgeDist, int excl, bool simNear, bool lightOn, float x, float y, float z, float r)
        {
            Rec rec;
            bool has = sRecs.TryGetValue(lot, out rec);
            if (excl != 0)
            {
                if ((excl & kHouseholdRows) != 0) ExclHousehold++; else ExclOther++;
                if (has) { if (rec.State == FarLight) GoNear++; Drop(lot); }   // L11
                return Excluded;
            }
            if (!has)
            {
                if (!(edgeDist > ParkEdgeM) || simNear) return Live;
                rec = new Rec();                                               // L1
                rec.State = GoFarPending; rec.Since = now;
                sRecs[lot] = rec;
                if (sRecs.Count > RecordsHigh) RecordsHigh = sRecs.Count;
            }
            rec.Pass = pass; rec.X = x; rec.Y = y; rec.Z = z; rec.R = r;
            if (edgeDist < NearEdgeM || simNear)                               // L2, L6: any near trigger
            {
                if (simNear && !(edgeDist < NearEdgeM)) BlockSim++;
                if (rec.State == FarLight) GoNear++;
                Drop(lot);
                return Live;
            }
            if (rec.State == GoFarPending && lightOn && now - rec.Since >= dwellTicks) { rec.State = FarLight; rec.At = -1; rec.Skipped = false; rec.ZombieOwed = 0f; LightIn++; LightLots++; }  // L4
            else if (rec.State == FarLight && !lightOn) { Leave(lot, rec); rec.State = GoFarPending; }  // farobjlight off: no FarLight, the dwell stays met
            return rec.State;
        }

        // A skipped UpdateNumZombiesOnLot call: its time is owed (the zombie-dance time-out and the search interval run on it).
        public static void OweZombie(Rec rec, float minutes) { if (rec != null && minutes > 0f) rec.ZombieOwed += minutes; }

        // EP4, per call of a pulse hook on a FarLight lot (the three hooks of one lot tick share one answer).
        // frozen: FarHubPaused. scopeOk: an active household and a selected Sim exist. hhLot: X1-X3 re-read (home lot, active
        // lot, a household Sim's current lot). actors: Sims and animals on the lot right now. live: skip for real (else count).
        // Any failed check runs the body and the lot leaves FarLight at once.
        public static int Pulse(ulong lot, Rec rec, long now, bool frozen, bool scopeOk, bool hhLot, int actors, float edgeDist, bool live)
        {
            if (rec == null || rec.State != FarLight) return Run;
            if (frozen) return Run;              // before the cached answer: the sim tick stands still while frozen
            if (rec.At == now) return rec.Dec;
            int leave = 0;
            if (edgeDist < NearEdgeM) { NearCall++; leave = 1; }
            else if (!scopeOk || hhLot) { HhCaught++; leave = 1; }
            else if (actors != 0) { SimArrive++; leave = 1; }
            if (leave != 0) { GoNear++; Drop(lot); return Run; }
            rec.At = now;
            if (live) rec.Skipped = true;
            return rec.Dec = live ? Skip : Would;
        }

        // After a complete pass: a record the pass did not touch belongs to a lot that is gone (L13: no catch-up).
        public static int Sweep(int pass)
        {
            sTmp.Clear();
            foreach (KeyValuePair<ulong, Rec> kv in sRecs) if (kv.Value.Pass != pass) sTmp.Add(kv.Key);
            foreach (ulong id in sTmp) { Drop(id); Gone++; }
            int n = sTmp.Count;
            sTmp.Clear();
            return n;
        }

        public static void LotGone(ulong lot) { if (Drop(lot)) Gone++; }

        // L14 (world quit, travel, Edit Town, switch off, stand-down): every record goes; FarLight needs no catch-up.
        public static int DropAll()
        {
            int n = sRecs.Count;
            if (n == 0) return 0;
            sTmp.Clear();
            sTmp.AddRange(sRecs.Keys);
            foreach (ulong id in sTmp) Drop(id);
            sTmp.Clear();
            Drops += n;
            return n;
        }

        // docs/322 bands (50 / 150 / 400 / 1000 m) for the pulse probe
        public static int Band(float edgeDist) { return edgeDist < 50f ? 0 : edgeDist < 150f ? 1 : edgeDist < 400f ? 2 : edgeDist < 1000f ? 3 : 4; }

        // Selftests only.
        public static void Reset()
        {
            sRecs.Clear(); sTmp.Clear(); ClearLeft();
            RecordsHigh = LightLots = LightIn = GoNear = Gone = BlockSim = ExclHousehold = ExclOther = Drops = NearCall = SimArrive = HhCaught = LeftLost = 0;
            ParkEdgeM = 200f; NearEdgeM = 150f; MinPulseM = 10f; ParkDwellMin = 30; BoundaryPassMin = 5;
        }
    }
}
