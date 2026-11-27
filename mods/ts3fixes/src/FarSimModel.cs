using System;
using System.Collections.Generic;
using System.Text;

namespace Ts3Fixes
{
    // Far-Sim state machine core (docs/345 SD-1, package FS-1 of docs/340; build notes docs/354). No game types, no hooks:
    // everything that reads EA arrives through IInputs or as arguments, so the selftest (tools/selftest/farsim) and a later
    // replay tool (DT-1) can feed recorded values. The state of a far Sim at tick t is F(seed, snapshot, inputs, t) = the
    // block of Plan(day(t)) that holds t; the due queue only delivers the change. Draws are counter based (SplitMix64
    // finaliser keyed by world seed, id, model version, episode, day, revision, slot), so evaluation order cannot change a
    // result. Integer arithmetic only: ticks, sim minutes, centimetres, thousandths of an hour.
    // D9 (docs/345 §13.2, checked by tools/selftest/farsim/run.ts): this file has no EA or system random source, no wall
    // clock, no table keyed by an object and no floating-point field.
    public static class FarSimModel
    {
        public const int kModelVersion = 1;  // sidecar mv.fs; a new slot or a changed table bumps it (docs/345 §8.3)

        // States (docs/345 §4.1), ordinals are part of the trace hash.
        public const int Live = 0, GoFarPending = 1, FarHome = 2, FarSleep = 3, FarTransit = 4, FarWork = 5, FarSchool = 6,
            FarVenue = 7, VisitProposed = 8, PendingLive = 9, Excluded = 10, kStates = 11;
        public static readonly string[] kStateName = { "Live", "GoFarPending", "FarHome", "FarSleep", "FarTransit", "FarWork",
            "FarSchool", "FarVenue", "VisitProposed", "PendingLive", "Excluded" };

        // Reasons (docs/345 §5.2). "due" is ours: a plan edge reached on time (T5-T14, T16) [inferred addition].
        public const int WFar = 0, WNear = 1, WRing = 2, WTrigger = 3, WObserved = 4, WEaPush = 5, WVisit = 6, WScope = 7,
            WExcl = 8, WTie = 9, WNoSafePoint = 10, WFlush = 11, WSwitchOff = 12, WStandDown = 13, WQuit = 14, WCatchUp = 15,
            WPlan = 16, WGone = 17, WFallback = 18, WDue = 19;
        public static readonly string[] kWhyName = { "far", "near", "ring", "trigger", "observed", "eaPush", "visit", "scope",
            "excl", "tie", "noSafePoint", "flush", "switchOff", "standDown", "quit", "catchUp", "plan", "gone", "fallback", "due" };

        // Exclusion reasons (docs/345 §5.3). 1-3 are the scope reasons (household, selected Sim, unknown scope).
        public const int XNone = 0, XHousehold = 1, XSelected = 2, XUnknownScope = 3, XTie = 4, XNoHome = 5, XSituation = 6,
            XService = 7, XPregnant = 8, XAgeUp = 9, XDying = 10, XPendingEvent = 11, XPlaceholder = 12, XPet = 13,
            XTimedBuff = 14, XHeldClass = 15, XModOwned = 16, XReaderNeedsSim = 17, kExcl = 18;
        public static readonly string[] kExclName = { "none", "household", "selected", "unknownScope", "tie", "noHome", "situation",
            "service", "pregnant", "ageUp", "dying", "pendingEvent", "placeholder", "pet", "timedBuff", "heldClass", "modOwned",
            "readerNeedsSim" };
        public static bool ScopeExcl(int x) { return x >= XHousehold && x <= XUnknownScope; }

        // Counters (docs/345 §13.3). Sizes (0-3) are set by Sizes(); the rest count events of the session.
        public const int CRecords = 0, CRecordsHigh = 1, CDueSize = 2, CPlanCache = 3, CDwell = 4, CGoFar = 5, CGoLive = 6,
            CGoLiveFallback = 7, CNoSafePoint = 8, CFlap = 9, CCatchUp = 10, CGone = 11, CHouseholdInFar = 12, CHhCaughtInFar = 13,
            CDriftHousehold = 14, CHhBlockedAtGoFar = 15, COwnerConflict = 16, CFViolations = 17, CIllegal = 18, CScopePromoted = 19,
            CStoodDown = 20, CVisitsProposed = 21, CVisitHandover = 22, CStateOff = 23, CTransitions = 24, CEdgeOther = 25, kCounters = 26;

        // ---- §8 seed and draws ----
        public static ulong M(ulong z)
        {
            unchecked
            {
                z += 0x9E3779B97F4A7C15UL;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }
        public static ulong Seed(ulong worldSeed, ulong id, uint modelVersion) { return M(M(M(worldSeed) ^ id) ^ modelVersion); }
        public static ulong Draw(ulong seed, uint ep, uint day, uint rev, ulong slot) { return M(M(M(M(seed ^ ep) ^ day) ^ rev) ^ slot); }
        public static uint Range(ulong draw, uint n) { return (uint)(((draw >> 32) * n) >> 32); }

        public const uint kEpisodeDay = 0xFFFFFFFF;  // key day of episode-wide draws
        public const ulong SlotSleepJitter = 1, SlotVenueGo = 2, SlotVenuePick = 3, SlotStayLen = 4, SlotVisitDraw = 5,
            SlotVisitHour = 6, SlotPhase = 0x100, SlotDrift = 0x200;
        public const int DriftRel = 0, DriftSkill = 1, DriftCareer = 2;

        // §6.1: phase of one motive in thousandths of an hour, EA's own range of +-1 h (Motive.kMaxMotiveTimeDelta) [verified
        // decompiled]. Episode-wide draw, slot 0x100 + commodity id.
        public static int PhaseMilliHours(ulong seed, uint ep, ulong commodity)
        {
            return (int)Range(Draw(seed, ep, kEpisodeDay, 0, SlotPhase + commodity), 2001) - 1000;
        }

        // §6.4 relationship drift of one pair on one day with `shared` shared blocks: each block draws -1..+2, mirrored to
        // -2..+1 for a pair that dislikes each other; +-3 per day. The lower id owns the draw, so both sides get one answer.
        public static int RelDriftDay(ulong worldSeed, ulong a, ulong b, uint ep, uint day, int shared, int curLtr)
        {
            ulong lo = a < b ? a : b, hi = a < b ? b : a;
            ulong seed = Seed(worldSeed, lo, kModelVersion);
            int sum = 0;
            for (int i = 0; i < shared && i < 8; i++)
            {
                int d = (int)Range(M(Draw(seed, ep, day, 0, SlotDrift + DriftRel + ((ulong)i << 8)) ^ hi), 4) - 1;
                sum += curLtr < 0 ? -d : d;
            }
            return sum > 3 ? 3 : sum < -3 ? -3 : sum;
        }

        // Per far stretch cap (+-15 for relationships): the part of delta that still fits. applied = sum so far.
        public static int CapStretch(int applied, int delta, int cap)
        {
            int to = applied + delta;
            if (to > cap) to = cap;
            if (to < -cap) to = -cap;
            return to - applied;
        }

        // §6.4 skill drift: the seeded share (250..500 per mille) of EA's gain rate for one skill on one day.
        public static int SkillSharePermille(ulong seed, uint ep, uint day, ulong skill)
        {
            return 250 + (int)Range(M(Draw(seed, ep, day, 0, SlotDrift + DriftSkill) ^ skill), 251);
        }

        // ---- §13.1 trace ----
        public struct TraceLine { public long Tick; public ulong Id; public uint Ti; public int From, To, Why; }

        public static ulong HashLine(ulong h, TraceLine l)
        {
            h = M(h ^ (ulong)l.Tick); h = M(h ^ l.Id); h = M(h ^ l.Ti); h = M(h ^ (ulong)l.From); return M(h ^ (ulong)l.To);
        }

        static int CmpTrace(TraceLine a, TraceLine b)
        {
            if (a.Tick != b.Tick) return a.Tick < b.Tick ? -1 : 1;
            if (a.Id != b.Id) return a.Id < b.Id ? -1 : 1;
            return a.Ti < b.Ti ? -1 : a.Ti > b.Ti ? 1 : 0;
        }

        // Hash of the lines in (tick, id, ti) order; the list is sorted in place.
        public static ulong TraceHash(List<TraceLine> lines)
        {
            lines.Sort(CmpTrace);
            ulong h = 0;
            for (int i = 0; i < lines.Count; i++) h = HashLine(h, lines[i]);
            return h;
        }

        // ---- §5.1 transition table as data ----
        public const int GHh = 1, GElig = 2, GSafe = 4, GRun = 8;  // guard mask
        public const int TrPass = 0, TrDue = 1, TrNear = 2, TrForced = 3, TrDone = 4, TrGone = 5;  // trigger
        public const int AAdd = 0, ADrop = 1, AGoFar = 2, ANone = 3, AArrive = 4, AMove = 5, AEnqueueLive = 6, AEval = 7;  // action
        public sealed class Row
        {
            public readonly int T, From, To, Trigger, Guard, Action;
            public Row(int t, int from, int to, int trigger, int guard, int action) { T = t; From = from; To = to; Trigger = trigger; Guard = guard; Action = action; }
        }
        static int B(int s) { return 1 << s; }
        const int kFar = 0x1FC;      // states 2-8
        const int kFarPlan = 0xFC;   // states 2-7
        const int kAny = 0x7FF;
        // T15 / T18 / T19 carry no GRun: with reason flush, switchOff or quit they must run to give EA its Sim back.
        public static readonly Row[] kTable =
        {
            new Row(1, B(Live), B(GoFarPending), TrPass, GHh | GElig | GRun, AAdd),
            new Row(2, B(GoFarPending), B(Live), TrNear, GRun, ADrop),
            new Row(3, B(GoFarPending), B(Live), TrDue, GRun, ADrop),
            new Row(4, B(GoFarPending), kFarPlan, TrPass, GHh | GElig | GSafe | GRun, AGoFar),
            new Row(5, B(FarHome), B(FarSleep), TrDue, GRun, ANone),
            new Row(6, B(FarSleep), B(FarHome), TrDue, GRun, ANone),
            new Row(7, B(FarHome), B(FarTransit), TrDue, GRun, AArrive),
            new Row(8, B(FarTransit), B(FarWork) | B(FarSchool), TrDue, GRun, AMove),
            new Row(9, B(FarWork) | B(FarSchool), B(FarTransit), TrDue, GRun, AArrive),
            new Row(10, B(FarHome), B(FarTransit), TrDue, GRun, AArrive),
            new Row(11, B(FarTransit), B(FarVenue), TrDue, GRun, AMove),
            new Row(12, B(FarVenue), B(FarTransit), TrDue, GRun, AArrive),
            new Row(13, B(FarTransit), B(FarHome), TrDue, GRun, AMove),
            new Row(14, B(FarHome), B(VisitProposed), TrDue, GRun, ANone),
            new Row(15, kFar, B(PendingLive), TrNear, 0, AEnqueueLive),
            new Row(16, B(VisitProposed), B(PendingLive), TrDue, GRun, AEnqueueLive),
            new Row(17, B(VisitProposed), B(FarHome), TrDue, GRun, ANone),
            new Row(18, B(PendingLive), B(Live), TrDone, 0, ADrop),
            new Row(19, B(PendingLive), B(Live), TrDone, 0, ADrop),
            new Row(20, B(Live), B(Excluded), TrPass, GRun, AAdd),
            new Row(21, B(Excluded), B(Live), TrPass, GHh | GRun, ADrop),
            new Row(22, kFar, kFar, TrDue, GRun, AEval),
            new Row(23, kFar, kFar, TrForced, GRun, AEval),
            new Row(24, kAny, B(Live), TrGone, 0, ADrop),
        };
        public static bool Allowed(int t, int from, int to)
        {
            if (t < 1 || t > kTable.Length) return false;
            Row r = kTable[t - 1];
            return (r.From & B(from)) != 0 && (r.To & B(to)) != 0;
        }
        // The due edge for (from, to): a named row of T5-T14 / T16 / T17, else T22 (an edge the table has no name for, such as
        // FarSleep to FarTransit before an early shift; it is still F(t)).
        public static int DueRow(int from, int to)
        {
            for (int t = 5; t <= 17; t++) if (t != 15 && Allowed(t, from, to)) return t;
            return 22;
        }

        // ---- configuration and inputs ----
        public sealed class Cfg
        {
            public ulong WorldSeed;
            public int TpmNum = 75, TpmDen = 2;  // SimClock: 37.5 ticks per sim minute [verified decompiled], kept as a fraction
            public int LiveRadiusCm = 15000, HandoffRadiusCm = 20000, FarRadiusCm = 45000, RingCm = 0;
            public int FarDwellMin = 30, ParkedDwellMin = 60, BoundaryPassMin = 5, GoFarTimeoutMin = 120, GoFarRetryMin = 60,
                FlapWindowMin = 10, DueBudget = 8, PrebuildMin = 60, VenueGoPct = 40;
            public bool Work, Venue, Visit;
            public int StatesOff;  // bit per state: unreachable ([FarSim] StatesOff)
            public long Ticks(int minutes) { return (long)minutes * TpmNum / TpmDen; }
            public long TicksPerDay { get { return Ticks(1440); } }
            public int NearCm { get { int ring = FarRadiusCm - 5000 - LiveRadiusCm; if (ring > RingCm) ring = RingCm; return LiveRadiusCm + (ring > 0 ? ring : 0); } }
        }

        // I5 / I9 / I10 answers (docs/345 §3.2). An implementation records an answer before returning it and never changes
        // one it gave for the same (id, day) unless it also calls Machine.PlanChanged.
        public interface IInputs
        {
            int Excl(ulong id);                 // I8: exclusion reason now, 0 = eligible (EP3)
            int WakeMin(ulong id);              // I5: wake time, minutes after midnight
            bool Shift(ulong id, int day, out int startMin, out int endMin, out ulong place, out bool school);  // I10, with farsimwork
            int VenueCount(int day);            // I5: open-lot list, in a fixed order
            ulong Venue(int day, int index);
            int TravelMin(ulong from, ulong to);  // §6.2 travel time, at least 20
            int VisitPermille(ulong id, int day);  // §6.2 visit weight of the day, 0 = no candidate
            bool VisitOk(ulong id, int day, int minute);  // I10 ea_ok
        }

        // §6.2 travel time: straight line x 1.3 over the speed, whole sim minutes rounded up, at least 20.
        // Speeds in cm per sim minute [inferred first values, FS-4 calibrates]: car, bike, walk.
        public static readonly int[] kSpeedCmPerMin = { 1500, 600, 160 };
        public static int TravelMin(long distCm, int speedCmPerMin)
        {
            if (distCm < 0) distCm = -distCm;
            long m = (distCm * 13 + 10L * speedCmPerMin - 1) / (10L * speedCmPerMin);
            return m < 20 ? 20 : m > 720 ? 720 : (int)m;
        }

        public struct Block { public long Start; public int State; public ulong Place; }

        sealed class Piece { public int A, Z, State; public ulong Place; }

        static void Carve(List<Piece> l, int a, int z, int state, ulong place)
        {
            if (a < 0) a = 0;
            if (z > 1440) z = 1440;
            if (a >= z) return;
            List<Piece> o = new List<Piece>(l.Count + 2);
            bool put = false;
            for (int i = 0; i < l.Count; i++)
            {
                Piece p = l[i];
                if (p.Z <= a) { o.Add(p); continue; }
                if (p.A < a) { Piece left = new Piece(); left.A = p.A; left.Z = a; left.State = p.State; left.Place = p.Place; o.Add(left); }
                if (!put) { Piece n = new Piece(); n.A = a; n.Z = z; n.State = state; n.Place = place; o.Add(n); put = true; }
                if (p.Z > z) { Piece right = new Piece(); right.A = p.A > z ? p.A : z; right.Z = p.Z; right.State = p.State; right.Place = p.Place; o.Add(right); }
            }
            l.Clear();
            l.AddRange(o);
        }

        public const int kMaxBlocks = 12;

        // §6.2 Plan(day): at most 12 blocks sorted by start tick, covering the whole day.
        // shortcut: a shift or venue stay is cut at midnight (the next day's plan starts from its own answers); upgrade when
        // farsimwork is timed with night shifts.
        public static List<Block> BuildPlan(Cfg c, IInputs inp, ulong seed, ulong id, ulong home, uint ep, uint rev, int day)
        {
            uint ud = (uint)day;
            int w = inp.WakeMin(id);
            if (w < 0 || w > 1439) w = 360;
            List<Piece> l = new List<Piece>();
            Piece all = new Piece(); all.A = 0; all.Z = 1440; all.State = FarHome; all.Place = home; l.Add(all);
            // the night that ends on this day and the one that ends on the next
            int j0 = (int)Range(Draw(seed, ep, ud, rev, SlotSleepJitter), 91), j1 = (int)Range(Draw(seed, ep, ud + 1, rev, SlotSleepJitter), 91);
            Carve(l, w - 480 - j0, w, FarSleep, home);
            Carve(l, 1440 + w - 480 - j1, 1440 + w, FarSleep, home);
            bool shift = false;
            if (c.Work)
            {
                int s, e; ulong wp; bool school;
                if (inp.Shift(id, day, out s, out e, out wp, out school) && e > s)
                {
                    shift = true;
                    int t = inp.TravelMin(home, wp);
                    Carve(l, s - t, s, FarTransit, wp);
                    Carve(l, s, e, school ? FarSchool : FarWork, wp);
                    Carve(l, e, e + t, FarTransit, home);
                }
            }
            if (c.Venue)
            {
                int n = inp.VenueCount(day), left = shift ? 1 : 2;
                List<Piece> free = Free(l);
                for (int bi = 0; bi < free.Count && n > 0 && left > 0; bi++)
                {
                    Piece f = free[bi];
                    ulong k = (ulong)bi << 8;
                    if (f.Z - f.A < 120 || Range(Draw(seed, ep, ud, rev, SlotVenueGo | k), 100) >= (uint)c.VenueGoPct) continue;
                    ulong v = inp.Venue(day, (int)Range(Draw(seed, ep, ud, rev, SlotVenuePick | k), (uint)n));
                    int stay = 60 + (int)Range(Draw(seed, ep, ud, rev, SlotStayLen | k), 121), t = inp.TravelMin(home, v);
                    if (stay > f.Z - f.A - 2 * t) stay = f.Z - f.A - 2 * t;
                    if (stay < 1) continue;
                    Carve(l, f.A, f.A + t, FarTransit, v);
                    Carve(l, f.A + t, f.A + t + stay, FarVenue, v);
                    Carve(l, f.A + t + stay, f.A + 2 * t + stay, FarTransit, home);
                    left--;
                }
            }
            if (c.Visit && l.Count <= kMaxBlocks - 2)
            {
                int pm = inp.VisitPermille(id, day);
                List<Piece> free = Free(l);
                if (pm > 0 && free.Count > 0 && Range(Draw(seed, ep, ud, rev, SlotVisitDraw), 1000) < (uint)pm)
                {
                    Piece f = free[0];
                    int vm = f.A + (int)Range(Draw(seed, ep, ud, rev, SlotVisitHour), (uint)(f.Z - f.A));
                    if (inp.VisitOk(id, day, vm))
                    {
                        Carve(l, f.A, vm - c.PrebuildMin, VisitProposed, home);
                        Carve(l, vm - c.PrebuildMin, f.Z, PendingLive, home);
                    }
                }
            }
            List<Block> blocks = new List<Block>(l.Count);
            long day0 = (long)day * 1440;
            for (int i = 0; i < l.Count; i++)
            {
                Piece p = l[i];
                if (blocks.Count > 0 && blocks[blocks.Count - 1].State == p.State && blocks[blocks.Count - 1].Place == p.Place) continue;
                Block b = new Block(); b.Start = (day0 + p.A) * c.TpmNum / c.TpmDen; b.State = p.State; b.Place = p.Place;
                blocks.Add(b);
            }
            return blocks;
        }

        static List<Piece> Free(List<Piece> l)
        {
            List<Piece> f = new List<Piece>();
            for (int i = 0; i < l.Count; i++) if (l[i].State == FarHome) f.Add(l[i]);
            return f;
        }

        // Minutes of one plan day spent in a state (leisure hours of the skill drift, shadow tables).
        public static long TicksIn(Cfg c, List<Block> plan, int day, int state)
        {
            long end = (long)(day + 1) * c.TicksPerDay, sum = 0;
            for (int i = 0; i < plan.Count; i++)
                if (plan[i].State == state) sum += (i + 1 < plan.Count ? plan[i + 1].Start : end) - plan[i].Start;
            return sum;
        }
        public static int MinutesIn(Cfg c, List<Block> plan, int day, int state) { return (int)((TicksIn(c, plan, day, state) * c.TpmDen + c.TpmNum / 2) / c.TpmNum); }

        // Blocks of two plans of the same day at the same place in FarWork, FarSchool or FarVenue that overlap in time
        // (the "plan reason" of a relationship drift, docs/324 §2.7).
        public static int SharedBlocks(Cfg c, List<Block> a, List<Block> b, int day)
        {
            long end = (long)(day + 1) * c.TicksPerDay;
            int n = 0;
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i].State < FarWork || a[i].State > FarVenue) continue;
                long az = i + 1 < a.Count ? a[i + 1].Start : end;
                for (int k = 0; k < b.Count; k++)
                {
                    if (b[k].State != a[i].State || b[k].Place != a[i].Place) continue;
                    long bz = k + 1 < b.Count ? b[k + 1].Start : end;
                    if (a[i].Start < bz && b[k].Start < az) { n++; break; }
                }
            }
            return n;
        }

        // ---- §3.1 record ----
        public sealed class Rec
        {
            public ulong Id, Place, Home;
            public int State, Why, Excl, PendingExcl;
            public long EnteredAt, EvalAt, NextDue = -1, FarSince = -1, ParkedSince = -1, LastGoLive = -1, RetryAt = -1;
            public uint Ti, Ep, Rev;
            internal int PlanDay0 = -1, PlanDay1 = -1, PlanGen;
            internal List<Block> Plan0, Plan1;
        }

        struct DueE { public long Due; public ulong Id; }

        public delegate void DropSink(ulong id);

        // One machine per world. Sim thread only.
        public sealed class Machine
        {
            public readonly Cfg C;
            public readonly IInputs In;
            public readonly long[] N = new long[kCounters];
            public readonly long[] ExclN = new long[kExcl];
            public readonly long[] ByState = new long[kStates];
            public List<TraceLine> Trace;   // null = lines are not kept (in game); the selftest and a replay keep them
            public DropSink OnDrop;         // the hook side drops its snapshot of this id (one drop path, §13.3)
            public bool StoodDown;
            readonly List<Rec> mRecs = new List<Rec>();  // sorted by id (H7: never an object-keyed table)
            readonly List<DueE> mDue = new List<DueE>();
            int mGen;

            public Machine(Cfg c, IInputs inp) { C = c; In = inp; }

            public int Count { get { return mRecs.Count; } }
            public Rec At(int i) { return mRecs[i]; }

            int IndexOf(ulong id)
            {
                int lo = 0, hi = mRecs.Count - 1;
                while (lo <= hi)
                {
                    int mid = (lo + hi) >> 1;
                    ulong v = mRecs[mid].Id;
                    if (v == id) return mid;
                    if (v < id) lo = mid + 1; else hi = mid - 1;
                }
                return ~lo;
            }
            public Rec Find(ulong id) { int i = IndexOf(id); return i >= 0 ? mRecs[i] : null; }
            Rec Ensure(ulong id)
            {
                int i = IndexOf(id);
                if (i >= 0) return mRecs[i];
                Rec r = new Rec(); r.Id = id;
                mRecs.Insert(~i, r);
                return r;
            }

            // -- due heap, ordered by (due, id) (H11) --
            static bool Less(DueE a, DueE b) { return a.Due != b.Due ? a.Due < b.Due : a.Id < b.Id; }
            void Up(int i) { while (i > 0) { int p = (i - 1) >> 1; if (!Less(mDue[i], mDue[p])) break; DueE t = mDue[i]; mDue[i] = mDue[p]; mDue[p] = t; i = p; } }
            void Down(int i)
            {
                while (true)
                {
                    int a = 2 * i + 1, b = a + 1, m = i;
                    if (a < mDue.Count && Less(mDue[a], mDue[m])) m = a;
                    if (b < mDue.Count && Less(mDue[b], mDue[m])) m = b;
                    if (m == i) return;
                    DueE t = mDue[i]; mDue[i] = mDue[m]; mDue[m] = t; i = m;
                }
            }
            void Unschedule(Rec r)
            {
                r.NextDue = -1;
                for (int i = 0; i < mDue.Count; i++)
                {
                    if (mDue[i].Id != r.Id) continue;
                    int last = mDue.Count - 1;
                    mDue[i] = mDue[last];
                    mDue.RemoveAt(last);
                    if (i < last) { Down(i); Up(i); }
                    return;
                }
            }
            void Schedule(Rec r, long due)
            {
                Unschedule(r);  // one entry per record
                r.NextDue = due;
                DueE e = new DueE(); e.Due = due; e.Id = r.Id;
                mDue.Add(e);
                Up(mDue.Count - 1);
            }

            // -- the one drop path (§13.3): snapshot, plan cache and due entry go together --
            void Release(Rec r)
            {
                Unschedule(r);
                r.Plan0 = r.Plan1 = null; r.PlanDay0 = r.PlanDay1 = -1;
                if (OnDrop != null) OnDrop(r.Id);
            }
            void Drop(Rec r)
            {
                Release(r);
                int i = IndexOf(r.Id);
                if (i >= 0) mRecs.RemoveAt(i);
            }

            void Go(Rec r, int t, int to, long tick, int why)
            {
                if (!Allowed(t, r.State, to)) N[CIllegal]++;
                TraceLine l = new TraceLine(); l.Tick = tick; l.Id = r.Id; l.Ti = r.Ti; l.From = r.State; l.To = to; l.Why = why;
                if (Trace != null) Trace.Add(l);
                r.Ti++;
                r.State = to; r.EnteredAt = tick; r.Why = why;
                N[CTransitions]++;
                if (to == Live || to == Excluded) Release(r);
            }

            List<Block> PlanFor(Rec r, int day)
            {
                if (r.PlanGen != mGen) { r.Plan0 = r.Plan1 = null; r.PlanDay0 = r.PlanDay1 = -1; r.PlanGen = mGen; }
                if (r.PlanDay0 == day) return r.Plan0;
                if (r.PlanDay1 == day) return r.Plan1;
                List<Block> p = BuildPlan(C, In, Seed(C.WorldSeed, r.Id, kModelVersion), r.Id, r.Home, r.Ep, r.Rev, day);
                r.Plan1 = r.Plan0; r.PlanDay1 = r.PlanDay0;  // at most 2 days per record
                r.Plan0 = p; r.PlanDay0 = day;
                return p;
            }
            public List<Block> PlanOf(ulong id, int day) { Rec r = Find(id); return r == null ? null : PlanFor(r, day); }

            // §7.1 F(t): state and place at tick t, and the tick of the next block start (or of the next day).
            public void F(Rec r, long t, out int state, out ulong place, out long next)
            {
                long tpd = C.TicksPerDay;
                int day = (int)(t / tpd);
                List<Block> p = PlanFor(r, day);
                int i = 0;
                while (i + 1 < p.Count && p[i + 1].Start <= t) i++;
                state = p[i].State; place = p[i].Place;
                next = i + 1 < p.Count ? p[i + 1].Start : (long)(day + 1) * tpd;
            }

            bool Off(int state) { return (C.StatesOff & (1 << state)) != 0; }

            void ToExcluded(Rec r, long now, int excl)
            {
                Go(r, 20, Excluded, now, ScopeExcl(excl) ? WScope : excl == XTie ? WTie : WExcl);
                r.Excl = excl; r.PendingExcl = 0;
                r.FarSince = r.ParkedSince = -1;
                ExclN[excl]++;
            }

            // Boundary pass, one Sim (EP1, EP2; T1, T2, T4, T15, T20, T21). excl: its exclusion reason now (0 = eligible, asked
            // fresh by the caller on this tick); dCm: distance to the nearest viewer point; parked: in a rabbit hole or on a
            // lot no viewer point is on; trigger: a near trigger of §4.2 rule 3 holds; safe: the safe point of §5.4 holds and
            // the Sim is neither on screen nor on a household lot; home: its home lot id (snapshot input of T4).
            public void Boundary(ulong id, long now, int excl, int dCm, bool parked, bool trigger, bool safe, ulong home)
            {
                Rec r = Find(id);
                if (excl == XNone || !ScopeExcl(excl)) Settle(r, now);
                if (excl != XNone)
                {
                    if (r == null) r = Ensure(id);
                    if (r.State == Excluded) { r.Excl = excl; return; }
                    if (r.State == GoFarPending) { if (ScopeExcl(excl)) N[CHhBlockedAtGoFar]++; Go(r, 2, Live, now, ScopeExcl(excl) ? WScope : WExcl); }
                    if (r.State == Live) { ToExcluded(r, now, excl); return; }
                    if (r.State >= FarHome && r.State <= VisitProposed)
                    {
                        // a far Sim never goes to Excluded directly: T15 now, T18 and T20 when the restore is done
                        N[CScopePromoted]++;
                        r.PendingExcl = excl;
                        Go(r, 15, PendingLive, now, ScopeExcl(excl) ? WScope : WExcl);
                        Unschedule(r);
                    }
                    else if (r.State == PendingLive) r.PendingExcl = excl;
                    return;
                }
                if (r != null && r.State == Excluded) { Go(r, 21, Live, now, WFar); r.Excl = 0; }
                bool near = trigger || dCm < C.NearCm, far = dCm > C.FarRadiusCm, parkedFar = parked && dCm > C.HandoffRadiusCm;
                if (r == null || r.State == Live)
                {
                    if (near || (!far && !parkedFar))
                    {
                        if (r == null) return;
                        r.FarSince = r.ParkedSince = -1;
                        // the dwell entry goes once nothing waits on it (retry time, flap window)
                        if (r.RetryAt <= now && (r.LastGoLive < 0 || now - r.LastGoLive >= C.Ticks(C.FlapWindowMin))) Drop(r);
                        return;
                    }
                    if (r == null) r = Ensure(id);
                    if (!far) r.FarSince = -1; else if (r.FarSince < 0) r.FarSince = now;
                    if (!parkedFar) r.ParkedSince = -1; else if (r.ParkedSince < 0) r.ParkedSince = now;
                    bool dwell = (r.FarSince >= 0 && now - r.FarSince >= C.Ticks(C.FarDwellMin)) || (r.ParkedSince >= 0 && now - r.ParkedSince >= C.Ticks(C.ParkedDwellMin));
                    if (!dwell || StoodDown || now < r.RetryAt) return;
                    Go(r, 1, GoFarPending, now, WFar);
                    Schedule(r, now + C.Ticks(C.GoFarTimeoutMin));
                    return;
                }
                if (r.State == GoFarPending)
                {
                    if (near) { Go(r, 2, Live, now, trigger ? WTrigger : WNear); return; }
                    if (!safe || StoodDown) return;
                    r.Home = home;
                    uint ep = r.Ep; r.Ep = ep + 1; r.Rev = 0; r.PlanDay0 = r.PlanDay1 = -1;
                    int st; ulong place; long next;
                    F(r, now, out st, out place, out next);
                    r.EvalAt = now;
                    if (Off(st) || st == PendingLive)
                    {
                        // a killed state (StatesOff) or a visit hand-over already due: stay live, try again later
                        r.Ep = ep; N[CStateOff]++;
                        Go(r, 2, Live, now, WStandDown);
                        r.RetryAt = now + C.Ticks(C.GoFarRetryMin);
                        return;
                    }
                    if (r.LastGoLive >= 0 && now - r.LastGoLive < C.Ticks(C.FlapWindowMin)) N[CFlap]++;
                    Go(r, 4, st == VisitProposed ? FarHome : st, now, WFar);  // T4 lands in 2-7; a visit drawn for today follows as T14
                    r.Place = place;
                    N[CGoFar]++;
                    if (st == VisitProposed) { N[CVisitsProposed]++; Go(r, 14, VisitProposed, now, WVisit); }
                    Schedule(r, next);
                    return;
                }
                if (r.State >= FarHome && r.State <= VisitProposed && near) { Go(r, 15, PendingLive, now, trigger ? WTrigger : WNear); Unschedule(r); }
            }

            // I8: a forced event on one Sim (observation, EA push, scope change, flush, switch off, stand-down, quit).
            public void Forced(ulong id, long now, int why)
            {
                Rec r = Find(id);
                if (r == null) return;
                if (why != WScope && why != WStandDown) Settle(r, now);
                if (r.State == GoFarPending) Go(r, 2, Live, now, why);
                else if (r.State >= FarHome && r.State <= VisitProposed) { Go(r, 15, PendingLive, now, why); Unschedule(r); }
            }

            // T18 / T19: the restore of a PendingLive Sim is done (ok) or fell back.
            public void GoLiveDone(ulong id, long now, bool ok)
            {
                Rec r = Find(id);
                if (r == null || r.State != PendingLive) { N[CIllegal]++; return; }
                int excl = r.PendingExcl;
                Go(r, ok ? 18 : 19, Live, now, ok ? r.Why : WFallback);
                N[ok ? CGoLive : CGoLiveFallback]++;
                r.LastGoLive = now; r.FarSince = r.ParkedSince = -1;
                if (excl != XNone) ToExcluded(r, now, excl);
            }

            // T23: a plan-changing input (I9) was recorded for this Sim.
            public void PlanChanged(ulong id, long now)
            {
                Rec r = Find(id);
                if (r == null || r.State < FarHome || r.State > VisitProposed) return;
                Settle(r, now);
                if (r.State < FarHome || r.State > VisitProposed) return;
                r.Rev++; r.PlanDay0 = r.PlanDay1 = -1;
                Eval(r, now, now, 23, WPlan);
            }

            // T24: the description or the Sim object is gone.
            public void Gone(ulong id)
            {
                Rec r = Find(id);
                if (r == null) return;
                if (r.State != Live) N[CGone]++;
                Drop(r);
            }

            // End of a boundary pass: every record whose id the pass did not see is gone (V5). seen is sorted.
            public void EndPass(List<ulong> seen)
            {
                for (int i = mRecs.Count - 1; i >= 0; i--) if (seen.BinarySearch(mRecs[i].Id) < 0) Gone(mRecs[i].Id);
            }

            void Eval(Rec r, long t, long now, int row, int why)
            {
                int st; ulong place; long next;
                F(r, t, out st, out place, out next);
                r.EvalAt = t;
                if (Off(st)) { N[CStateOff]++; Go(r, 15, PendingLive, t, WStandDown); Unschedule(r); return; }
                if (st != r.State || place != r.Place)
                {
                    // a visit hand-over from any other state than VisitProposed (no room for the proposal block) is a T15
                    int t0 = st == PendingLive ? (r.State == VisitProposed ? 16 : 15) : row != 0 ? row : DueRow(r.State, st);
                    if (row == 0 && t0 == 22) N[CEdgeOther]++;
                    if (st == VisitProposed) N[CVisitsProposed]++;
                    Go(r, t0, st, t, st == PendingLive ? WVisit : why);
                    r.Place = place;
                    if (st == PendingLive) { N[CVisitHandover]++; Unschedule(r); return; }
                }
                Schedule(r, next);
            }

            // One due entry of one record, with its nominal tick.
            void DueOne(Rec r, long due, long now)
            {
                if (r.State == GoFarPending)
                {
                    Go(r, 3, Live, due, WNoSafePoint);
                    N[CNoSafePoint]++;
                    r.RetryAt = due + C.Ticks(C.GoFarRetryMin);
                    return;
                }
                if (r.State < FarHome || r.State > VisitProposed) { Unschedule(r); return; }
                int x = In.Excl(r.Id);  // EP3
                if (x != XNone)
                {
                    if (ScopeExcl(x)) N[CHhCaughtInFar]++;
                    r.PendingExcl = x;
                    Go(r, 15, PendingLive, due, ScopeExcl(x) ? WScope : WExcl);
                    Unschedule(r);
                    return;
                }
                if (now - due > C.TicksPerDay) { N[CCatchUp]++; Eval(r, now, now, 22, WCatchUp); }  // §7.3: never replay
                else Eval(r, due, now, 0, WDue);
            }

            // Edges of this record that are already due run before any other event on it, so a full DueBudget delays
            // side effects but never changes the trace (§7.2).
            void Settle(Rec r, long now)
            {
                while (r != null && r.NextDue >= 0 && r.NextDue <= now) DueOne(r, r.NextDue, now);
            }

            // §7.2: drain the due queue. Transitions carry their nominal due tick; budget is a count (0 = no limit).
            public int Drain(long now, int budget)
            {
                int n = 0;
                while (mDue.Count > 0 && mDue[0].Due <= now && (budget <= 0 || n < budget))
                {
                    DueE e = mDue[0];
                    Rec r = Find(e.Id);
                    if (r == null || r.NextDue != e.Due) { int last = mDue.Count - 1; mDue[0] = mDue[last]; mDue.RemoveAt(last); if (mDue.Count > 0) Down(0); continue; }
                    n++;
                    DueOne(r, e.Due, now);
                }
                return n;
            }

            // V2 and V4 (shadow asserts), then the automatic stand-down of §14.
            public void Audit(long now)
            {
                for (int i = 0; i < mRecs.Count; i++)
                {
                    Rec r = mRecs[i];
                    if (r.State == Live || r.State == Excluded) continue;
                    if (ScopeExcl(In.Excl(r.Id))) N[CHouseholdInFar]++;
                    if (r.State < FarHome || r.State > VisitProposed) continue;
                    int st; ulong place; long next;
                    F(r, r.EvalAt, out st, out place, out next);
                    if (st != r.State) N[CFViolations]++;
                }
                if (!StoodDown && N[CHouseholdInFar] + N[CHhCaughtInFar] + N[CDriftHousehold] + N[COwnerConflict] > 0) StandDown(now);
            }

            // §14: promote everything, no new go-far until the next world load (Reset).
            public void StandDown(long now)
            {
                StoodDown = true;
                N[CStoodDown] = 1;
                PromoteAll(now, WStandDown);
            }

            // T15 for every far record and T2 for every pending one; the caller finishes each PendingLive with GoLiveDone.
            public void PromoteAll(long now, int why)
            {
                for (int i = 0; i < mRecs.Count; i++) Forced(mRecs[i].Id, now, why);
            }

            // A switch or key changed: plans are built again and every far record is evaluated now (T17 for a dropped visit).
            public void Reconfigure(long now)
            {
                mGen++;
                for (int i = 0; i < mRecs.Count; i++) if (mRecs[i].State >= FarHome && mRecs[i].State <= VisitProposed) Eval(mRecs[i], now, now, 0, WPlan);
            }

            // Quit to menu, travel, world load, switch off: every table empty. Event counters stay (session totals).
            public void Reset()
            {
                for (int i = mRecs.Count - 1; i >= 0; i--) Release(mRecs[i]);
                mRecs.Clear();
                mDue.Clear();
                StoodDown = false;
                Sizes();
            }

            public void Sizes()
            {
                long plans = 0, recs = 0, dwell = 0;
                for (int i = 0; i < kStates; i++) ByState[i] = 0;
                for (int i = 0; i < mRecs.Count; i++)
                {
                    Rec r = mRecs[i];
                    ByState[r.State]++;
                    if (r.State == Live) dwell++; else recs++;
                    if (r.PlanGen == mGen) { if (r.PlanDay0 >= 0) plans++; if (r.PlanDay1 >= 0) plans++; }
                }
                N[CRecords] = recs; N[CDwell] = dwell; N[CDueSize] = mDue.Count; N[CPlanCache] = plans;
                if (recs > N[CRecordsHigh]) N[CRecordsHigh] = recs;
            }
        }

        // ---- §12 sidecar lines of the far-Sim machine (docs/343 §3.2 format; ids and ticks as decimal strings) ----
        public static string SLine(Rec r)
        {
            return "{\"t\":\"S\",\"id\":\"" + r.Id + "\",\"st\":\"" + kStateName[r.State] + "\",\"ti\":" + r.Ti + ",\"c\":{\"ep\":" + r.Ep + ",\"rev\":" + r.Rev + "}}";
        }
        public static string ELine(TraceLine l)
        {
            return "{\"t\":\"E\",\"ticks\":\"" + l.Tick + "\",\"id\":\"" + l.Id + "\",\"ti\":" + l.Ti + ",\"from\":\"" + kStateName[l.From] + "\",\"to\":\"" + kStateName[l.To] + "\",\"why\":\"" + kWhyName[l.Why] + "\"}";
        }
        public static string DLine(ulong id, ulong to, string kind, int v)
        {
            return "{\"t\":\"D\",\"id\":\"" + id + "\"" + (to != 0 ? ",\"to\":\"" + to + "\"" : "") + ",\"k\":\"" + kind + "\",\"v\":" + v + "}";
        }

        static bool Field(string line, string key, out ulong v)
        {
            v = 0;
            int i = line.IndexOf("\"" + key + "\":", StringComparison.Ordinal);
            if (i < 0) return false;
            i += key.Length + 3;
            if (i < line.Length && line[i] == '"') i++;
            int n = 0;
            while (i < line.Length && line[i] >= '0' && line[i] <= '9' && n < 20) { v = unchecked(v * 10 + (ulong)(line[i] - '0')); i++; n++; }
            return n > 0;
        }

        // Reads back what the machine wants from an S line: id, ti, ep, rev (st is for diagnosis only).
        public static bool ParseS(string line, out ulong id, out uint ti, out uint ep, out uint rev)
        {
            ulong a = 0, b = 0, c = 0;
            id = 0; ti = ep = rev = 0;
            if (line == null || !line.StartsWith("{\"t\":\"S\",", StringComparison.Ordinal) || !Field(line, "id", out id) || !Field(line, "ti", out a)
                || !Field(line, "ep", out b) || !Field(line, "rev", out c)) return false;
            ti = (uint)a; ep = (uint)b; rev = (uint)c;
            return true;
        }

        // docs/343 §4: with another model version in the W line, the S, D and E lines of Sims are discarded; W and K stay.
        public static bool KeepLine(string line, int fileFs)
        {
            if (fileFs == kModelVersion || line == null) return true;
            return !(line.StartsWith("{\"t\":\"S\",", StringComparison.Ordinal) || line.StartsWith("{\"t\":\"D\",", StringComparison.Ordinal) || line.StartsWith("{\"t\":\"E\",", StringComparison.Ordinal));
        }

        // ---- shadow error tables (FS-2 / FS-3), pure so the line format is tested offline ----
        public sealed class ErrTable
        {
            public readonly string Name;
            public readonly int[] Edges;   // upper bucket edges, last bucket = above
            public readonly long[] Bucket;
            public long Count, SumAbs, Max, SumSigned;
            public ErrTable(string name, params int[] edges) { Name = name; Edges = edges; Bucket = new long[edges.Length + 1]; }
            public void Add(long model, long ea)
            {
                long e = model - ea, a = e < 0 ? -e : e;
                int i = 0;
                while (i < Edges.Length && a > Edges[i]) i++;
                Bucket[i]++; Count++; SumAbs += a; SumSigned += e;
                if (a > Max) Max = a;
            }
            public void Clear() { Array.Clear(Bucket, 0, Bucket.Length); Count = SumAbs = Max = SumSigned = 0; }
            public string Line(string why)
            {
                StringBuilder b = new StringBuilder("farsim: shadow err ").Append(why).Append(' ').Append(Name).Append(" n ").Append(Count)
                    .Append(" meanabs ").Append(Count > 0 ? SumAbs / Count : 0).Append(" bias ").Append(Count > 0 ? SumSigned / Count : 0).Append(" max ").Append(Max).Append(" buckets");
                for (int i = 0; i < Bucket.Length; i++) b.Append(' ').Append(i < Edges.Length ? "le" + Edges[i] : "over").Append(':').Append(Bucket[i]);
                return b.ToString();
            }
        }

        // Model state against what EA's Sim was doing: rows = model state 2-8, columns = EA activity class.
        public const int EaHome = 0, EaSleep = 1, EaTransit = 2, EaWork = 3, EaSchool = 4, EaVenue = 5, EaVisit = 6, EaOther = 7, kEa = 8;
        public static readonly string[] kEaName = { "home", "sleep", "transit", "work", "school", "venue", "visit", "other" };
        public static readonly int[] kEaOfState = { -1, -1, EaHome, EaSleep, EaTransit, EaWork, EaSchool, EaVenue, EaHome, -1, -1 };

        public sealed class Confusion
        {
            public readonly long[] Cell = new long[kStates * kEa];
            public long Count, Agree;
            public void Add(int state, int ea)
            {
                if (state < FarHome || state > VisitProposed || ea < 0 || ea >= kEa) return;
                Cell[state * kEa + ea]++; Count++;
                if (kEaOfState[state] == ea) Agree++;
            }
            public void Clear() { Array.Clear(Cell, 0, Cell.Length); Count = Agree = 0; }
            public List<string> Lines(string why)
            {
                List<string> l = new List<string>();
                for (int s = FarHome; s <= VisitProposed; s++)
                {
                    StringBuilder b = new StringBuilder("farsim: shadow state ").Append(why).Append(' ').Append(kStateName[s]).Append(" ea");
                    for (int e = 0; e < kEa; e++) b.Append(' ').Append(kEaName[e]).Append(':').Append(Cell[s * kEa + e]);
                    l.Add(b.ToString());
                }
                l.Add("farsim: shadow state " + why + " total n " + Count + " agree " + Agree + " pct " + (Count > 0 ? Agree * 100 / Count : 0));
                return l;
            }
        }
    }
}
