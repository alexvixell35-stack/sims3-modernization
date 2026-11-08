using System.Collections.Generic;

namespace Ts3Fixes
{
    // Game-independent parts of the docs/202 unseen-Sim fallback (UnseenFallback.cs), checked offline by tools/selftest/unseenfb.
    public static class UnseenFallbackCore
    {
        // docs/202 §2: the first failing test of the scope rule, 0 = unseen (act). Order = the doc's table, so counters and
        // logs name the cheapest reason first.
        public const int kSeen = 0, kNotLive = 1, kGone = 2, kHousehold = 3, kActiveLot = 4, kOnScreen = 5, kNearViewers = 6, kHeld = 7, kSocialSeen = 8, kGoalHousehold = 9;  // 9: from UfGoalWhy only, never Why or UfWhy
        public static int Why(bool live, bool exists, bool household, bool activeLot, bool onScreen, bool far, bool held, bool targetSeen)
        {
            if (!live) return kNotLive;
            if (!exists) return kGone;
            if (household) return kHousehold;
            if (activeLot) return kActiveLot;
            if (onScreen) return kOnScreen;
            if (!far) return kNearViewers;
            if (held) return kHeld;
            return targetSeen ? kSocialSeen : kSeen;
        }

        // D-leave (docs/202 §3.6): a Sim the player sees only through the camera (on the camera's / household's lot, on screen,
        // near a viewer) may start a split route; the household, held Sims, Sims with a seen social partner and out-of-live never
        public static bool SeenButMovable(int why) { return why == kActiveLot || why == kOnScreen || why == kNearViewers; }

        // Household or scope unknown (Why stopped before the household test, or -1: a failed check): counted as household.
        public static bool HouseholdScope(int why) { return why != kSeen && why <= kHousehold; }

        // docs/197 §7 KdSeenNpcCeilMs: the reasons that prove a known NPC the player sees. Household, not live, gone, -1 (a failed
        // check) and anything else stay on the household ceiling.
        public static bool SeenNpc(int why) { return why >= kActiveLot && why <= kGoalHousehold; }

        // docs/197 §7 goalhhexempt: why an unseen NPC's route works for the household. A check that did not finish exempts (ok false).
        // Each reason counts in ts3fixes slot kGoalCounter + reason.
        public const int kGoalNone = 0, kGoalLot = 1, kGoalTarget = 2, kGoalWanted = 3, kGoalFailed = 4, kGoalCounter = 861;
        public static int GoalReason(bool ok, bool onLot, bool targetHousehold, bool wantedByHousehold)
        {
            return !ok ? kGoalFailed : onLot ? kGoalLot : targetHousehold ? kGoalTarget : wantedByHousehold ? kGoalWanted : kGoalNone;
        }

        // Issue #89: a Sim's posture chain (PreviousPosture) is a few postures deep; one that does not end within kPostureChainCap
        // steps is a loop (EA's set_Posture on a pop) or otherwise broken. ChainStepOk(n) for the n-th posture walked (1-based).
        public const int kPostureChainCap = 16;
        public static bool ChainStepOk(int n) { return n <= kPostureChainCap; }

        // Issue #89: EA's Sim.set_Posture asks the new posture's Compatible(current) first and, when true, links the new one on
        // top (new.PreviousPosture = current). For a pop the new posture already is current.PreviousPosture, so the two then
        // point at each other [verified, decompiled]. True = this set is such a pop (posturepopcycle sends it down the unwind).
        public static bool IsPop(object current, object currentPrevious, object next)
        {
            return current != null && next != null && !ReferenceEquals(current, next) && ReferenceEquals(currentPrevious, next);
        }

        // 1 m destination cell, as RouteBackoff's BackoffKey
        public static int Cell(float f) { return (int)System.Math.Floor(f); }

        // docs/202 §5.6 C-site push (UnseenFallbackSites.cs). Simulator.Sleep counts SimClock ticks (37.5 per sim-minute), and at
        // speed 3 the clock moves ~8 ticks per iteration, so a fixed Sleep(5) woke every iteration (~42 pushes/s in v020-natural
        // IP). The loop pushes every kPushMs of wall time and sizes its sleep from the tick rate it just saw.
        public const int kPushMs = 500, kRecheckMs = 2000;
        public const uint kSleepDefault = 5, kSleepMax = 2000;
        public static uint SitesSleepTicks(long dTicks, int dMs, int wantMs)
        {
            if (dTicks <= 0 || dMs <= 0) return kSleepDefault;  // no rate yet (first wake, paused clock)
            long t = dTicks * System.Math.Max(wantMs, 1) / dMs;
            return t < 1 ? 1u : t > kSleepMax ? kSleepMax : (uint)t;
        }
        // a cached verdict is reused unless the Sim moved >= 1 m, the camera moved >= 1 m (cache cleared), or it is kRecheckMs old;
        // ts3native re-tests frustum, reach and EA's on-screen byte at the wait itself, so a stale "unseen" never acts on a seen Sim
        // docs/202 §5.8: also when the Sim started routing or changed interaction since the cached verdict
        public static bool SitesRecheck(float movedSq, int ageMs, bool changed) { return changed || movedSq >= 1f || ageMs < 0 || ageMs >= kRecheckMs; }

        // docs/202 §5.8 (Rodney's per-Sim loop rule): a push's list is built over several ticks, at most kSitesPerTick Sims per wake
        // from one snapshot (round-robin cursor), so no tick pays for every Sim in the world (the 0.36.104 push: 2.7-5 ms in one tick)
        public const int kSitesPerTick = 8;
    }

    public sealed class UfSitesPass
    {
        int mNext, mCount = 0;
        public void Begin(int count) { mCount = count < 0 ? 0 : count; mNext = 0; }
        public bool Done { get { return mNext >= mCount; } }
        // this wake's slice: [from, from + return value), never more than kSitesPerTick
        public int Slice(out int from)
        {
            from = mNext;
            int n = System.Math.Min(UnseenFallbackCore.kSitesPerTick, mCount - mNext);
            if (n < 0) n = 0;
            mNext += n;
            return n;
        }
    }

    // docs/202 §3.5 D (pre-emptive teleport): walk-time hold, interaction deny list, the "in transit" table.
    public static class UfPreCore
    {
        // shortcut: the walk speed is a constant [inferred: ~1.5-2 m/s animation at 1 sim-minute per real second at speed 1,
        // minus stops]; upgrade to the route's walk style speed once a verify run compares hold times with real walks.
        // D-arrive / D-leave hand-off candidates: every kHandOffStepM along the line, at least kHandOffGapM from both ends
        public const float kSplitMinDistM = 40f, kHandOffStepM = 10f, kHandOffGapM = 10f;
        public static int HandOffSteps(float distM) { return distM < 2f * kHandOffGapM ? 0 : (int)((distM - 2f * kHandOffGapM) / kHandOffStepM) + 1; }
        public static float HandOffAt(int i) { return kHandOffGapM + i * kHandOffStepM; }

        public const float kMinDistM = 20f, kDetour = 1.3f, kWalkMPerSimMin = 2.5f, kMinHoldMin = 1f, kMaxHoldMin = 120f;

        // straight-line distance x detour / walk speed, in sim-minutes; capped at 2 sim-hours (§3.7: shorter than Overwatch V1's
        // pass spacing, so one hold never spans two passes)
        public static float HoldMinutes(float distM)
        {
            float m = distM * kDetour / kWalkMPerSimMin;
            return m < kMinHoldMin ? kMinHoldMin : m > kMaxHoldMin ? kMaxHoldMin : m;
        }

        // docs/202 §3.6.4 lot trips: 0 = no shortcut, 1 = place at once (the trip shortcuts as shipped), 2 = D's walk-time hold, then
        // place. Kinds the shortcuts already cover (newKind false; their own switch was checked by the caller) keep 1 unless
        // unseenfbtriphold; kinds they miss (mod subclasses that override the trip's Run) act only with unseenfbtrip, always held.
        public static int TripMode(bool newKind, bool tripOn, bool holdOn)
        {
            if (newKind) return tripOn ? 2 : 0;
            return holdOn ? 2 : 1;
        }

        // assemblies scanned for mod subclasses of the trip interactions: not the game's, not the runtime's, not ours
        static readonly string[] kGameAssemblies = { "mscorlib", "System", "System.Xml", "ScriptCore", "SimIFace", "Sims3GameplaySystems",
            "Sims3GameplayObjects", "Sims3StoreObjects", "Sims3Metadata", "UI" };
        public static bool TripScanAssembly(string name, string ours)
        {
            if (name == null || name == ours) return false;
            foreach (string g in kGameAssemblies) if (name == g) return false;
            return true;
        }

        // interactions whose point is the walk (§3.5 (5)): never shortcut. Matched on the interaction's type full name (nested
        // Definition classes included); every NRaas interaction too (GoHere's own walks, Overwatch/ErrorTrap helpers).
        static readonly string[] kDeny = { "Jog", "Walk", "GoHere", "Stroll", "Sightsee", "Sneak", "Haunt", "Parade", "Chase", "Follow",
            "Wander", "Leash", "Patrol", "Tour", "Explore", "Flee", "Swim", "Dive", "Trick", "Mount", "Ride", "Carry", "Stalk" };
        public static bool Denied(string fullTypeName)
        {
            if (fullTypeName == null || fullTypeName.StartsWith("NRaas")) return true;
            foreach (string d in kDeny) if (fullTypeName.IndexOf(d, System.StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        // D acts only when the plan's caller follows that route right after (docs/141 §2: the RoutingComponent helpers that
        // plan once and then call DoRoute on the same route [verified, decompiled]); never for plans made only to test or score
        // (definition Tests, lot picks, safe-spot checks), which must not move the Sim. frame = "FullType.Method" of the first
        // frame above the route funnels (RouteSrcCore.IsPassThrough).
        static readonly string[] kFollowers = { "RouteToSlot", "RouteToPoint", "RouteToPointRadius", "RouteToPointRadialRange", "RouteToLot",
            "RouteToObjectRadialRange", "RouteToMatrix" };
        const string kRoutingComponent = "Sims3.Gameplay.ObjectComponents.RoutingComponent.";
        public static bool FollowsRoute(string frame)
        {
            if (frame == null || !frame.StartsWith(kRoutingComponent)) return false;
            string m = frame.Substring(kRoutingComponent.Length);
            foreach (string f in kFollowers) if (m == f) return true;
            return false;
        }
        // the same with the frame above it: ViewObjects.CreateRouteToObject plans (a second plan only for a pool goal) and returns the
        // route; only ViewObjects.RouteToObject then calls DoRoute on it at once [verified, decompiled]. Its other callers
        // (Trampoline compares two routes, InteriorDesigner, Aromainator) stay refused.
        const string kViewObjects = "Sims3.Gameplay.InteractionsShared.ViewObjects.";
        public static bool FollowsRoute(string frame, string above)
        {
            return FollowsRoute(frame) || (frame == kViewObjects + "CreateRouteToObject" && above == kViewObjects + "RouteToObject");
        }

        // §3.5 (5) vehicles, gate 332: refuse a route that starts in a car or boat, or may not walk.
        // EnablePlanningAsCar and EnablePlanningAsBoat alone are NOT vehicle routes: SimRoutingComponent.SetRouteOptions sets
        // the car option on every child-or-older Sim's route and, with Island Paradise installed, EnablePlanningAsBoat = (on a
        // boat || child-or-older, not scuba / CPR / reaper / rider) [verified, decompiled]. Refusing either refused nearly every
        // D candidate (10-30 run: 2083 at 332; kit run 2026-10-31, issue #135: 1457-1932 per job, 0 acts). They only let the
        // planner pick a drive or a boat leg; walking stays allowed. relaxed (docs/202 §3.9, unseenroute): only a Sim that
        // starts in a vehicle is refused (its vehicle would not move with it); no-walk routes get the fast-leg estimate.
        public static bool VehicleRoute(bool beginAsCar, bool noPedestrian, bool beginAsBoat, bool relaxed)
        {
            return beginAsCar || beginAsBoat || (noPedestrian && !relaxed);
        }

        // docs/202 §3.9 relaxed hold: a route that may drive, take the subway / hover train or a boat and is long enough that the
        // game would pick that leg is held for min(walk, overhead + distance x detour / fast speed). shortcut: one fast speed
        // for car, taxi, subway, hover train and boat [inferred, not measured]; upgrade per mode once a verify run compares holds
        // with the game's own trips.
        public const float kFastMinDistM = 150f, kFastMPerSimMin = 10f, kFastOverheadMin = 3f;
        public static float TravelMinutes(float distM, bool fastLegs)
        {
            float walk = HoldMinutes(distM);
            if (!fastLegs || !(distM >= kFastMinDistM)) return walk;
            float fast = kFastOverheadMin + distM * kDetour / kFastMPerSimMin;
            return fast < walk ? (fast < kMinHoldMin ? kMinHoldMin : fast) : walk;
        }

        // docs/202 §3.9 snap-back: the share of the trip done when a hold ends early (0..1; no hold length = 0)
        public static float TripShare(long heldTicks, long holdTicks)
        {
            if (holdTicks <= 0 || heldTicks <= 0) return 0f;
            return heldTicks >= holdTicks ? 1f : (float)heldTicks / holdTicks;
        }
        // snap-back candidates: distance from the start of the i-th point, from the share done back towards the start in
        // kHandOffStepM steps, never closer than kHandOffGapM to either end; < 0 = no more candidates
        public static float SnapAt(int i, float share, float distM)
        {
            float d = share * distM - i * kHandOffStepM;
            if (d > distM - kHandOffGapM) d = distM - kHandOffGapM - i * kHandOffStepM;
            return d >= kHandOffGapM ? d : -1f;
        }

        // our own frames between the plan hook and D's caller read (UfFollows, UfCaller, UfPre's helpers in Fixes): never the caller.
        // Before 2026-10-30 UfCaller returned UfFollows itself, so gate 336 refused every plan (10-27 run: unseenfbpre_callers
        // = "6 Ts3Fixes.Fixes.UfFollows")
        public static bool OwnFrame(string fullType, string method)
        {
            return fullType == "Ts3Fixes.Fixes" && method != null && method.StartsWith("Uf");
        }

        // RoutingComponent.RouteToLot plans through Lot.PlanToLot (static) -> Lot.PlanToLot / PlanToLotEx [verified, decompiled]:
        // those frames are skipped when D reads the caller, so RouteToLot is the frame FollowsRoute sees; lot trips (VisitLot.Run
        // etc. -> PlanToLotEx) then show their own Run frame and stay refused
        public static bool PlanToLotFrame(string fullType, string method)
        {
            return fullType == "Sims3.Gameplay.Core.Lot" && (method == "PlanToLot" || method == "PlanToLotEx");
        }

        // the hold table (AppDomain ts3fixes.unseenfb.holding: Sim object id -> hold end in sim ticks), read by our watchdogs and
        // the agent's sims_activity (in_transit); an entry past its end counts as not in transit (a hold that never ended = stuck)
        public static bool InTransit(Dictionary<ulong, long> holds, ulong sim, long now)
        {
            long until;
            return holds != null && holds.TryGetValue(sim, out until) && now <= until;
        }
    }

    // docs/202 §3.9.9 per-event timestamps (unseenroute7): one log line per act / ghost start / ghost exit / placement / sweep
    // check, at most kMax per kind in a kWindowMs window (the rest are counted, not logged), so a hitch can be matched to an action
    public sealed class UrEvLog
    {
        public const int kMax = 6, kWindowMs = 1000;
        readonly System.Collections.Generic.Dictionary<string, int[]> mKinds = new System.Collections.Generic.Dictionary<string, int[]>();  // kind -> { window start ms, count }
        public int Dropped;
        public bool May(string kind, int nowMs)
        {
            int[] k;
            if (!mKinds.TryGetValue(kind, out k)) { k = new int[] { nowMs, 0 }; mKinds[kind] = k; }
            if (unchecked(nowMs - k[0]) >= kWindowMs || unchecked(nowMs - k[0]) < 0) { k[0] = nowMs; k[1] = 0; }
            if (k[1] >= kMax) { Dropped++; return false; }
            k[1]++;
            return true;
        }
        // "ur: act sim-min=1234.5 wall-ms=56789 sim=1a2b3c4d"; the id hash keeps the full Sim id out of the log
        public static string Format(string kind, double simMin, int wallMs, ulong simId)
        {
            return "ur: " + kind + " sim-min=" + simMin.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " wall-ms=" + wallMs + " sim=" + ((uint)(simId ^ (simId >> 32))).ToString("x8");
        }
    }

    // docs/202 §3.9.6-§3.9.8 unseenroute2 rules (Rodney 2026-10-31), offline-tested by tools/selftest/unseenfb.
    public static class UrCore
    {
        // §3.9.7 (B): relaxed without a verdict only when the start and the goal are proven in one flood colour and the goal is
        // on the street (world lot) or a public (community) lot, outdoors (checked by the caller)
        public static bool BPass(int sameColour, bool streetGoal, bool communityGoal) { return sameColour == 1 && (streetGoal || communityGoal); }

        // §3.9.7 (C) cache keys, coarse so a verdict repeats: a lot goal = its lot id; a street goal = its 64 m street area
        // (bit 63 set). The start side = the start lot id, 0 on the street.
        public const float kAreaM = 64f;
        public static ulong GoalKey(ulong lotId, bool street, float x, float z)
        {
            if (!street) return lotId & 0x7FFFFFFFFFFFFFFFUL;
            uint ax = (uint)((int)System.Math.Floor(x / kAreaM) & 0xFFFFF), az = (uint)((int)System.Math.Floor(z / kAreaM) & 0xFFFFF);
            return 0x8000000000000000UL | (ulong)ax << 20 | az;
        }
        public static ulong StartKey(ulong lotId, bool street) { return street ? 0UL : lotId & 0x7FFFFFFFFFFFFFFFUL; }

        // C hit rate per mille (agent: unseenroute_c_hit_pm); -1 = nothing asked yet
        public static int HitPm(int hits, int learns) { int n = hits + learns; return n <= 0 ? -1 : (int)((long)hits * 1000 / n); }

        // §3.9.7b relaxed lot rules (2026-11-01): an outdoor goal needs no door, so only a lock refuses a lot; single-gender and
        // one-way doors do not (they refused every beach / park goal of sv-ur-car-loop). The start lot counts only when the Sim
        // stands indoors.
        public static bool DoorRefuses(bool locked, bool singleGender, bool oneWay) { return locked; }
        public static bool StartLotCounts(bool startOutdoor) { return !startOutdoor; }
        // §3.9.7b placement near an unroutable (object) goal: distances tried back towards the start; < 0 = no more
        static readonly float[] kNearGoalM = { 1f, 2f, 3f, 4f, 6f, 8f };
        public static float NearGoalAt(int i) { return i >= 0 && i < kNearGoalM.Length ? kNearGoalM[i] : -1f; }

        // §3.9.7 (D) sweep over the placed Sims: gone = drop, seen = drop and run the reachability check, unseen = keep
        public const int kPlacedKeep = 0, kPlacedDrop = 1, kPlacedCheck = 2;
        public static int PlacedCheck(bool exists, bool seen) { return !exists ? kPlacedDrop : seen ? kPlacedCheck : kPlacedKeep; }
        // after the SafeSpot check of a seen placed Sim: no verdict (the shared one-check-per-tick budget is spent: the watchdog,
        // SafeSpot or another listed Sim of the same sweep) keeps it listed for the next sweep; reachable = drop; unreachable = repair
        public const int kPlacedRepair = 3;
        public static int PlacedVerdict(bool? spotOk) { return spotOk == null ? kPlacedKeep : spotOk.Value ? kPlacedDrop : kPlacedRepair; }

        // §3.9.6 groups move together: only the leader's plan acts, and only when every member is unseen and relaxed
        public static bool GroupOk(bool isLeader, int members, int membersOk) { return isLeader && members > 0 && membersOk == members; }
    }

    // §3.9.7 (C): learned reachability per (goal key, start key); in memory only (no save data), cleared on world load and
    // for a lot edited in Build/Buy (Edit Town: all)
    public sealed class UrCCache
    {
        public const int kMax = 2048;
        struct K { public ulong G, S; }
        sealed class Cmp : IEqualityComparer<K>
        {
            public bool Equals(K a, K b) { return a.G == b.G && a.S == b.S; }
            public int GetHashCode(K k) { return (k.G ^ (k.G >> 32) ^ (k.S * 31) ^ (k.S >> 32)).GetHashCode(); }
        }
        readonly Dictionary<K, bool> mMap = new Dictionary<K, bool>(new Cmp());
        public int Count { get { return mMap.Count; } }
        public bool? Get(ulong goal, ulong start) { bool v; return mMap.TryGetValue(new K { G = goal, S = start }, out v) ? (bool?)v : null; }
        public void Set(ulong goal, ulong start, bool ok)
        {
            if (mMap.Count >= kMax) mMap.Clear();  // shortcut: a full cache starts over (it re-learns one plan per key)
            mMap[new K { G = goal, S = start }] = ok;
        }
        // drop every entry whose goal or start is this lot
        public int ClearLot(ulong lotId)
        {
            ulong k = lotId & 0x7FFFFFFFFFFFFFFFUL;
            List<K> drop = new List<K>();
            foreach (K x in mMap.Keys) if (x.G == k || x.S == k) drop.Add(x);
            foreach (K x in drop) mMap.Remove(x);
            return drop.Count;
        }
        public void Clear() { mMap.Clear(); }
    }

    // §3.9.6 plans in progress per script task: a plan nested in another plan's callback runs in the same task (depth 2);
    // plans of other tasks that run while one task sleeps inside its plan are not nested
    public sealed class UrTaskDepth
    {
        readonly Dictionary<ulong, int> mDepth = new Dictionary<ulong, int>();
        public int Enter(ulong task) { int d; mDepth.TryGetValue(task, out d); mDepth[task] = ++d; return d; }
        public void Exit(ulong task) { int d; if (!mDepth.TryGetValue(task, out d)) return; if (d <= 1) mDepth.Remove(task); else mDepth[task] = d - 1; }
        public int Depth(ulong task) { int d; return mDepth.TryGetValue(task, out d) ? d : 0; }
        public int Tasks { get { return mDepth.Count; } }
    }

    // §3.9.5 ghost ledger: every ghost start must end exactly once (the footprint re-insert); a second start or an end without
    // a start is refused and counted by the caller
    public sealed class UrGhostLedger
    {
        readonly Dictionary<ulong, bool> mLive = new Dictionary<ulong, bool>();
        public int Live { get { return mLive.Count; } }
        public bool Start(ulong id) { if (mLive.ContainsKey(id)) return false; mLive[id] = true; return true; }
        public bool End(ulong id) { return mLive.Remove(id); }
    }

    // docs/202 §3.4.2 finish the walk (UnseenFallbackFinish.cs, switch unseenfbfinish): pure rules, offline-tested
    public static class UfFinishCore
    {
        public const uint kSimPath = 3328183143u;  // PathType.SimPath: an ordinary walk (no portal, vehicle, boat, water, stroller)

        // remaining planned path length / walk speed, in sim-minutes; no detour factor (it is the path's own length); 0 = finish at
        // the next check; capped at D's 2 sim-hours (§3.7: shorter than Overwatch V1's pass spacing). NaN or negative = 0.
        public static float HoldMinutes(float remainingM)
        {
            if (!(remainingM > 0f)) return 0f;
            float m = remainingM / UfPreCore.kWalkMPerSimMin;
            return m > UfPreCore.kMaxHoldMin ? UfPreCore.kMaxHoldMin : m;
        }

        // the route can be finished by placing the Sim at its end: the game's own FollowSubPathRouteAction (not a vehicle, boat or
        // UFO subclass), not killed yet, no slave Sims, the last subpath (no portal, door or stairs left between the Sim and the end:
        // those are separate subpaths), an ordinary walking path, and no route action queued after it
        public static bool RouteShape(bool exactType, bool killed, int slaves, uint numPaths, uint pathType, bool actionAfter)
        {
            return exactType && !killed && slaves == 0 && numPaths == 1 && pathType == kSimPath && !actionAfter;
        }

        // D's deny list (interactions whose point is the walk), except a townie's plain Go Here: Rodney 2026-10-27 excludes Go Here
        // only for the player's Sims, which the scope rule (UfWhy: household, selectable) never lets through; GoHereWith and NRaas
        // GoHere stay denied (groups, its own walks)
        public const string kTerrainGoHere = "Sims3.Gameplay.Core.Terrain+GoHere";
        public static bool Denied(string fullTypeName) { return fullTypeName != kTerrainGoHere && UfPreCore.Denied(fullTypeName); }

        // a claimed hold at a check: 0 keep holding, 1 finish now, 2 give up (the Sim walks on from where it stood)
        public const int kKeep = 0, kFinish = 1, kGiveUp = 2;
        public static int Step(bool nativeHeld, bool sameAction, bool stillOk, long now, long until)
        {
            if (!nativeHeld || !sameAction || !stillOk) return kGiveUp;
            return now >= until ? kFinish : kKeep;
        }
    }

    // docs/248 unseen boat shortcut (UnseenBoat.cs, switch unseenboat): pure rules, offline-tested
    // docs/248 unseen boat trips: what a trip is, from the route and its goal
    public static class UbCore
    {
        public const float kMinTripM = 40f;
        public const int kExact = 0, kTransport = 1, kHouseboat = 2;
        // UfWhy answer of a Sim in a boat: 0, or kHeld (the boat is a Vehicle), which UfWhyBody returns only after every camera test passed
        public static bool Unseen(int why) { return why == UnseenFallbackCore.kSeen || why == UnseenFallbackCore.kHeld; }
        // a boat replan's class: UbCountReplan and the boatreplan: line (docs/247 §11.1: the line used to test why == 0, so every Sim in a boat logged seen)
        public static bool ReplanUnseen(int simWhy, bool boatUnseen) { return Unseen(simWhy) && boatUnseen; }
        // real water at the start (docs/247 §11.3): the start height refused dry raised spots of room 0. Names, so the offline test needs no game types
        static readonly string[] kWaterPostures = { "SwimmingInPool", "ScubaDiving", "PondAndOceanRoutingPosture" };
        public static bool WaterPosture(string postureType) { return System.Array.IndexOf(kWaterPostures, postureType) >= 0; }
        public static bool FarEnough(float distM) { return distM >= kMinTripM; }

        // colours: UfSameComponent, -1 = another land component, the only proof that the water must be crossed. Unknown stays exact
        public static int Trip(bool houseboatGoal, int colours, bool sameLot) { return Trip(houseboatGoal, colours, sameLot, false); }
        // leaving a houseboat (unseenboatstart): its lot has no land colour, and every way off it is a crossing or a gangway the Sim skips
        public static int Trip(bool houseboatGoal, int colours, bool sameLot, bool leavingHouseboat)
        {
            if (sameLot) return kExact;
            if (houseboatGoal) return kHouseboat;
            return leavingHouseboat || colours < 0 ? kTransport : kExact;
        }

        // why a Sim may not leave this houseboat now; null = it may. Docked or at sea both pass, with or without a gangway: the placement needs no route off.
        // Under way the Sim would keep its seasickness alarm [verified, decompiled], a driver ends the boat's route
        public static string LeaveWhy(bool hasBoat, bool moving, bool driver, bool activeHome)
        {
            if (!hasBoat) return "no houseboat";
            if (moving) return "boat moving";
            if (driver) return "driver";
            return activeHome ? "start:household lot" : null;
        }
        // the unseenboat_log and unseenboat_ms row of a Sim starting on a houseboat
        public static string LeaveReason(bool leaving, string why) { return leaving ? "houseboat start: " + why : why; }

        // the finish deny list, plus interactions whose point is the water itself
        static readonly string[] kDenyWater = { "Boat", "Waterski", "Surf", "Sail", "Fish", "Scuba" };
        public static bool Denied(string fullTypeName)
        {
            if (UfFinishCore.Denied(fullTypeName)) return true;
            foreach (string k in kDenyWater) if (fullTypeName.Contains(k)) return true;
            return false;
        }

        public static float HoldMinutes(float distM) { return UfPreCore.TravelMinutes(distM, true); }

        // The plan's caller follows the route. D's helpers (UfPreCore.FollowsRoute) are not how a Sim crosses the water: lot trips and
        // rabbit holes plan in their own bodies, so D's rule refused the crossings (2026-11-06 run: rj_route = candidates). Bodies read
        // [verified, decompiled]: they plan, then DoRoute(WithFollowers) that route. Hook frames are how those bodies show on the stack
        // when patched; VclSubHook* is a mod's override of VisitCommunityLot.Run, a copy of it [inferred, no source here].
        // GoHere is left out: UfPreCore.Denied refuses it in UfPreSim. VisitLot.Run plans with doRoute false, then DoRouteWithFollowers
        // that route [verified, decompiled]; UbAlone reads its private followers, the goal verdict stays in UbGate.
        static readonly string[] kFollowers = { "Sims3.Gameplay.Core.GoHome.Run", "Sims3.Gameplay.Core.GoToLot.Run", "Sims3.Gameplay.Core.GoToCommunityLot.Run",
            "Sims3.Gameplay.Core.VisitCommunityLot.Run", "Sims3.Gameplay.Core.VisitLot.Run", "Sims3.Gameplay.Abstracts.RabbitHole.RouteOutside", "Ts3Fixes.Fixes+GoHomeHook.Run", "Ts3Fixes.Fixes+GoToLotHook.Run",
            "Ts3Fixes.Fixes+GoToCommunityLotHook.Run", "Ts3Fixes.Fixes+VisitCommunityLotHook.Run", "Ts3Fixes.Fixes+VisitLotHook.Run", "Ts3Fixes.Fixes+VclSubHook0.Run", "Ts3Fixes.Fixes+VclSubHook1.Run", "Ts3Fixes.Fixes+VclSubHook2.Run" };
        // Lot helpers between a trip's Run and the plan; each plans once [verified, decompiled]. No proof alone: RouteToDoor with doRoute
        // false only hands the route back, so the frame above them decides
        static readonly string[] kLotHelpers = { "PlanToLot", "PlanToLotEx", "PlanRouteIntoLot", "RouteToDoor", "RouteToFrontDoor", "RouteToFrontDoorOrMailbox", "RouteToMailbox", "PlanToMailbox" };
        const string kLot = "Sims3.Gameplay.Core.Lot.";
        static bool LotHelper(string frame)
        {
            if (frame == null || !frame.StartsWith(kLot)) return false;
            return System.Array.IndexOf(kLotHelpers, frame.Substring(kLot.Length)) >= 0;
        }
        // a hook's stub shows as "GameType.RunOrig" between a Lot helper and the hook's own frame (docs/247 §11.3): the frame above decides
        static bool OrigStub(string frame) { return frame != null && frame.EndsWith("Orig", System.StringComparison.Ordinal); }
        // frames: "FullType.Method" from the plan's caller up
        public static bool Follows(IList<string> frames)
        {
            int i = 0;
            while (i < frames.Count && (LotHelper(frames[i]) || OrigStub(frames[i]))) i++;
            if (i >= frames.Count) return false;
            return System.Array.IndexOf(kFollowers, frames[i]) >= 0 || UfPreCore.FollowsRoute(frames[i], i + 1 < frames.Count ? frames[i + 1] : null);
        }
    }

    // docs/247 §11.3: boat replan ms per refusal reason of the Sim's last trip. Reasons hold caller names, so the row count is capped
    public sealed class UbReasonMs
    {
        public const int kMaxRows = 64;
        public const string kNoTrip = "no refusal", kOther = "other reasons";
        readonly Dictionary<string, long[]> mRows = new Dictionary<string, long[]>();

        public void Add(string why, uint ms)
        {
            string k = why ?? kNoTrip;
            long[] r;
            if (!mRows.TryGetValue(k, out r))
            {
                if (mRows.Count >= kMaxRows - 1) k = kOther;
                if (!mRows.TryGetValue(k, out r)) mRows[k] = r = new long[2];
            }
            r[0]++;
            r[1] += ms;
        }

        // most ms first; object rows, as the agent's other lists
        public List<object> Lines()
        {
            List<KeyValuePair<string, long[]>> rows = new List<KeyValuePair<string, long[]>>(mRows);
            rows.Sort(delegate(KeyValuePair<string, long[]> a, KeyValuePair<string, long[]> b) { return b.Value[1].CompareTo(a.Value[1]); });
            List<object> o = new List<object>(rows.Count);
            foreach (KeyValuePair<string, long[]> r in rows) o.Add(r.Key + " | " + r.Value[0] + " replans, " + r.Value[1] + " ms");
            return o;
        }
    }

    // docs/202 §2/§3.2 per-Sim limits. Ticks are Environment.TickCount (ms, wraps; NoStartCore.Within handles that).
    //   NoRetry: a cut plan of this Sim to the same 1 m cell inside kNoRetryMs answers with the recorded result, no search.
    //   MayMark: a Sim cut kMaxCuts times inside kWindowMs is not marked again until the window ends (its plans run uncut,
    //            as without the switch), so a route the Sim must take is never failed for time forever.
    public sealed class UfLimiter
    {
        public const int kNoRetryMs = 1500, kWindowMs = 10000, kMaxCuts = 4, kMaxSims = 512;
        struct E { public int First, Cuts, CutTick, X, Y, Z; public object Result; }
        readonly Dictionary<ulong, E> mSims = new Dictionary<ulong, E>();

        public int Count { get { return mSims.Count; } }

        public bool MayMark(ulong sim, int now)
        {
            E e;
            return !mSims.TryGetValue(sim, out e) || !NoStartCore.Within(now, e.First, kWindowMs) || e.Cuts < kMaxCuts;
        }

        public bool NoRetry(ulong sim, int x, int y, int z, int now, out object result)
        {
            E e;
            result = null;
            if (!mSims.TryGetValue(sim, out e) || e.Result == null || e.X != x || e.Y != y || e.Z != z || !NoStartCore.Within(now, e.CutTick, kNoRetryMs)) return false;
            result = e.Result;
            return true;
        }

        public void Cut(ulong sim, int x, int y, int z, int now, object result)
        {
            E e;
            if (!mSims.TryGetValue(sim, out e))
            {
                if (mSims.Count >= kMaxSims) mSims.Clear(); // shortcut: a burst of distinct Sims starts over; entries are short-lived anyway
                e = new E();
                e.First = now;
            }
            else if (!NoStartCore.Within(now, e.First, kWindowMs)) { e.First = now; e.Cuts = 0; }
            e.Cuts++;
            e.CutTick = now;
            e.X = x; e.Y = y; e.Z = z;
            e.Result = result;
            mSims[sim] = e;
        }

        public void Clear() { mSims.Clear(); }
    }
    // docs/202 §5.7 E: loop detector. A loop = the same interaction definition on the same target started again (a new
    // instance) by one Sim, after the previous instance of it had a failed plan, kLoopRestarts times inside the window (sim
    // ticks). Flags once per streak; a different key, a success-only restart or the window's end start over.
    public sealed class UfLoopDetector
    {
        public const int kLoopRestarts = 4, kMaxSims = 512;
        sealed class E { public string Key; public object Inst; public int Restarts; public long First; public bool Failed, Flagged; }
        readonly Dictionary<ulong, E> mSims = new Dictionary<ulong, E>();
        readonly long mWindow;
        public UfLoopDetector(long windowTicks) { mWindow = windowTicks; }
        public int Count { get { return mSims.Count; } }
        public int Restarts(ulong sim) { E e; return mSims.TryGetValue(sim, out e) ? e.Restarts : 0; }

        // one plan of sim inside interaction inst (definition + target = key); true = the plan that makes it a loop
        public bool Note(ulong sim, string key, object inst, bool planFailed, long now)
        {
            E e;
            if (!mSims.TryGetValue(sim, out e))
            {
                if (mSims.Count >= kMaxSims) mSims.Clear();  // shortcut: a burst of distinct Sims starts over
                mSims[sim] = e = new E();
            }
            if (e.Key != key || now - e.First > mWindow)
            {
                e.Key = key; e.Inst = inst; e.Restarts = 0; e.First = now; e.Failed = planFailed; e.Flagged = false;
                return false;
            }
            if (ReferenceEquals(e.Inst, inst)) { e.Failed |= planFailed; return false; }
            bool count = e.Failed;  // a restart after a failure
            e.Inst = inst;
            e.Failed = planFailed;
            if (!count) return false;
            e.Restarts++;
            if (e.Restarts < kLoopRestarts || e.Flagged) return false;
            e.Flagged = true;
            return true;
        }
    }

    // docs/202 §5.7 E: loop breaker scope (Rodney 2026-10-27) and per-tick budget
    public static class UfLoopBreakCore
    {
        public const int kObjectsPerTick = 4;
        // Sim side (motives, negative moodlets): live mode and UfWhy == 0 (unseen: not the household, not selected, not on a seen
        // lot, off screen, outside the 80 m reach of the camera and the household)
        public static bool SimFix(bool live, int why) { return live && why == UnseenFallbackCore.kSeen; }
        // lot side: the Sim's own home lot or a community lot, not the world lot, not in view, not owned by the active household
        // (home, real estate, vacation home, houseboat), and no active-household member on it or heading there. Checked before
        // every step: a household Sim arriving mid-way stops the fix.
        public static bool LotFix(bool live, bool worldLot, bool simHomeOrCommunity, bool lotSeen, bool ownedByActive, bool activeMemberThere)
        {
            return live && !worldLot && simHomeOrCommunity && !lotSeen && !ownedByActive && !activeMemberThere;
        }
    }

    // at most PerTick steps per sim tick (a tick = one SimClock value)
    public sealed class UfTickBudget
    {
        readonly int mPerTick;
        long mTick = long.MinValue;
        int mUsed;
        public UfTickBudget(int perTick) { mPerTick = perTick; }
        public bool Try(long tick)
        {
            if (tick != mTick) { mTick = tick; mUsed = 0; }
            if (mUsed >= mPerTick) return false;
            mUsed++;
            return true;
        }
    }
}
