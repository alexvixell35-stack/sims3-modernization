using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.ObjectComponents;
using Sims3.Gameplay.Pools;
using Sims3.Gameplay.Objects;
using Sims3.Gameplay.Routing;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Sims share stairs, doors and ladders and walk through each other instead of standing (docs/686 §8). Four behaviours,
    // each default off with its own household switch; rules in ShareLanesCore.cs. A player's wish: clipping is fine,
    // waiting is not. Nothing here moves a Sim: every step is one the game takes itself somewhere else.
    //
    // sharelanes / sharelaneshh
    //   [verified, decompiled] PortalComponent keeps a table Sim -> lane. Its lane search gives a lane that another Sim
    //   holds from the same end, and refuses one held from the other end. With every lane refused the Sim's portal action
    //   sends it to a waiting spot 0.75-3 m away and asks again every quarter sim-minute for 20 sim-minutes (one-lane doors:
    //   every Sim coming the other way, from the moment the first Sim starts the path that ends at the door). The table is
    //   per Sim, the door's open count is per door leaf and counted, the stair walk is one native body per Sim
    //   (BeginPortalRouting 0x74D720 -> 0x955FB0 makes a new "AnimatedSimStairsBody" each call and tests geometry only), so
    //   two directions on one lane break nothing but looks.
    //   Fix: when the game refuses a lock on stairs, a door, a gate or a ladder only because of that direction test, the
    //   search is made once more with the game's own steps (nearest start slot first, the portal's extra checks, the
    //   obstruction test, the game's fallback lane) and the lane is shared. Elevators and the other portal kinds keep
    //   the game's table; spiral stairs have their own switch (spiralshare below).
    //   Also: a stand-and-wait cycle whose plan answered "destination portal locked" plans once without "blocked by people"
    //   at once (the game's route search calls a portal locked when a standing Sim's footprint covers a lane end, docs/183
    //   §9); when that plan succeeds the Sim walks on with the game's ignore-obstacles timer.
    // laddershare / laddersharehh
    //   [verified, decompiled] The ladder climb is an interaction that first waits its turn in the ladder's own line (one
    //   climber at a time, three Sims at most, the fourth fails with "object in use"); the waiting Sims stand on the lane
    //   ends. Fix: the climb does not wait for its turn.
    // spiralshare / spiralsharehh
    //   [verified, decompiled] A spiral staircase has one lane (enter bottom / enter top) and, like the ladder, its own
    //   line: the walk is an interaction that waits its turn (one Sim at a time, three in the line, the fourth fails with
    //   "object in use"), and the Sims in the line hold the lane meanwhile, so a Sim coming the other way waits at the
    //   waiting spot until the line is empty. The walk itself is one state machine per Sim with the Sim as its only
    //   actor; the staircase keeps no state per walker (its use list and the table are per Sim, the route's end cleans
    //   both), and on stacked spiral stairs the game itself moves a Sim onto the next staircase without its lane or line.
    //   Fix: the lane is shared in both directions by the sharelanes search, and the walk does not wait for its turn.
    //   The railing slide keeps its line.
    // simpass / simpasshh
    //   [verified, decompiled] A walking Sim that meets Sims stops and runs the obstacle stand-and-wait, unless its
    //   "ignore all obstacles" timer runs (the game starts that timer itself after a portal and after a wait in a crowd).
    //   Fix: with only Sims in the way and none of them at the route's end, the timer is started for kSimPassMinutes, so
    //   the game's own handler lets the Sim walk on. When (docs/686 §8.8, a player's rule: clipping is for doors and
    //   stairs, not the standard): at once, at the obstacle event or at a wait that still starts, only when the walker
    //   or a Sim in its way is within kClipPortalNear of a portal lane (door, gate, stairs, ladder, elevator; on stairs
    //   counts). Anywhere else the game's wait begins and runs, and its first cycle after kClipGraceMinutes ends it the
    //   same way. petrelax and clipwait keep the same rule with the same two tunables.
    // angleentry / angleentryhh
    //   [verified, decompiled + native] The stair walk sets the Sim's place and facing itself when it starts (0x8555E0:
    //   facing from the lane's two ends); the ladder climb up does the same in script. So neither needs the Sim to arrive
    //   facing the portal. Fix: stairs get the game's own "path to lane" approach (a plan to the lane's start point with
    //   no facing), which the game uses whenever the Sim did not get the lane it planned for; a ladder climbed from below
    //   is approached by that point plan instead of the plan to the slot with its facing. The climb down keeps the slot.
    //
    // Off = compare mode: the hooks count what each switch would do, and the lane waits (how many, how long) either way.
    // lanewaitlog writes one line per lane wait. Counters 4364-4397. Check: ShareLanesCheck. The waits that still begin: ClipWait.cs (clipwait).
    public static partial class Fixes
    {
        const int kShlFails = 4364, kShlSharable = 4365, kShlShared = 4366, kShlErrors = 4367, kShlWould = 4368, kShlExempt = 4369,
            kShlWaits = 4370, kShlWaitsHh = 4371, kShlWaitTenths = 4372, kShlWaitTenthsHh = 4373, kShlWaitGaveUp = 4374,
            kShlPortalPass = 4375, kShlPortalWould = 4376, kShlPortalStillLocked = 4377,
            kLdsSkips = 4378, kLdsWould = 4379, kLdsExempt = 4380,
            kSpsEvents = 4381, kSpsActions = 4382, kSpsWould = 4383, kSpsExempt = 4384, kSpsKept = 4385,
            kAneStairs = 4386, kAneLadder = 4387, kAneWould = 4388, kAneKept = 4389,
            kSpiFails = 4390, kSpiSharable = 4391, kSpiShared = 4392, kSpiWould = 4393, kSpiExempt = 4394,
            kSpiLineSkips = 4395, kSpiLineWould = 4396, kSpiLineExempt = 4397;
        const int kShlPortalPassEvery = 3;  // blocked cycles between two plans without people (the stand-and-wait's own pass spacing)
        const float kSpsDestRadius = 0.6f;  // the game's radius for Sims at a route's end

        const int kSpsAtEvent = 0, kSpsAtBegin = 1, kSpsAtGrace = 2;  // where simpass judges: obstacle event, wait begin, wait cycle past the grace
        const float kShlLanesMinutes = 30f;  // sim-minutes a lot's lane list is kept (build mode can move a door)
        const int kShlLanesMaxLots = 16;

        static FieldInfo sShlLocked, sShlAcqSim, sShlAcqPortal, sSpsSim, sSpsAction;
        class ShlLanes { public DateAndTime Stamp; public float[] Ends; }  // six numbers per lane: its two ends
        static readonly Dictionary<ulong, ShlLanes> sShlLanes = new Dictionary<ulong, ShlLanes>();
        static readonly List<float> sShlLaneBuf = new List<float>();
        static readonly List<Sim> sSpsSeen = new List<Sim>(4);
        static int sSpsDeferred, sSpsGrace;  // this session: waits left to the grace (not near a portal) / walks after it
        static string sShlMissing = "", sShlLast = "";
        static readonly LineCapCore sShlCap = new LineCapCore(20, 60000, 200);

        // One catch site each: which hook threw, how often, and the first few as lines.
        static readonly string[] kShlSites = { "lane", "ladder approach", "wait begin", "wait end", "ladder line", "pass", "obstacle event", "pass at the wait", "stairs approach", "pass after the grace" };
        static readonly int[] sShlSiteErrors = new int[10];
        const int kShlErrorLines = 3;  // lines per site
        static string sShlLastError = "";

        static void ShlError(int site, Exception e)
        {
            PatchCounters()[kShlErrors]++;
            string where = "";
            try { string st = e.StackTrace; if (st != null) { where = st.Trim(); int nl = where.IndexOf('\n'); if (nl > 0) where = where.Substring(0, nl).Trim(); if (where.Length > 160) where = where.Substring(0, 160); } }
            catch (Exception) { }
            sShlLastError = "sharelanes: error at " + kShlSites[site] + ": " + e.GetType().Name + ": " + e.Message + (where.Length > 0 ? " [" + where + "]" : "");
            if (sShlSiteErrors[site]++ < kShlErrorLines) DiagLine(sShlLastError);
        }

        static string ShlErrorSites()
        {
            string r = "";
            for (int i = 0; i < sShlSiteErrors.Length; i++)
                if (sShlSiteErrors[i] > 0) r += (r.Length > 0 ? ", " : "") + kShlSites[i] + " " + sShlSiteErrors[i];
            return r.Length > 0 ? " (" + r + "; last: " + sShlLastError + ")" : "";
        }

        class ShlWait { public long Start; public string Holders; public int Kind; }
        static readonly Dictionary<ulong, ShlWait> sShlWaitOf = new Dictionary<ulong, ShlWait>();

        static void ApplyShareLanes()
        {
            sShlMissing = "";
            Type pc = typeof(PortalComponent);
            sShlLocked = pc.GetField("mLockedLaneIndices", kInstAll);
            if (sShlLocked == null || !typeof(IDictionary).IsAssignableFrom(sShlLocked.FieldType)) sShlMissing += "mLockedLaneIndices ";
            Type acq = pc.GetNestedType("AcquireLaneRouteAction", BindingFlags.Public | BindingFlags.NonPublic);
            if (acq != null)
            {
                sShlAcqSim = acq.GetField("mRoutingSim", kInstAll);
                sShlAcqPortal = acq.GetField("mPortalObject", kInstAll);
            }
            if (acq == null || sShlAcqSim == null || sShlAcqPortal == null) sShlMissing += "AcquireLaneRouteAction ";
            sSpsSim = typeof(SimSteeringAdaptor).GetField("mRoutingSim", kInstAll);
            sSpsAction = typeof(SteeringAdaptor).GetField("mRouteAction", kInstAll);
            if (sSpsSim == null || sSpsAction == null) sShlMissing += "SimSteeringAdaptor.mRoutingSim/mRouteAction ";
            sShlMissing = sShlMissing.Trim();
            if (sShlLocked != null)
            {
                if (!Patch(pc, "GetFreeRoutingLane", null, typeof(ShlLaneHook), "FreeLaneHook", "FreeLaneOrig")) sShlMissing = (sShlMissing + " GetFreeRoutingLane").Trim();
                Patch(pc, "WaitForLaneAndPlantToItRouteInterruptCallback", new Type[] { typeof(Sim), typeof(Route) }, typeof(ShlLaneHook), "WaitHook", "WaitOrig");
                if (acq != null && sShlAcqSim != null && sShlAcqPortal != null) Patch(acq, "PerformAction", Type.EmptyTypes, typeof(ShlAcquireHook), "PerformHook", "PerformOrig");
            }
            if (!Patch(pc, "GetPathToLane", null, typeof(ShlLaneHook), "PathToLaneHook", "PathToLaneOrig")) sShlMissing = (sShlMissing + " GetPathToLane").Trim();
            if (!Patch(typeof(SimQueue), "WaitForTurn", new Type[] { typeof(InteractionInstance), typeof(IGameObject), typeof(SimQueue.WaitBehavior), typeof(ExitReason), typeof(float) },
                typeof(LdsQueueHook), "WaitHook", "WaitOrig")) sShlMissing = (sShlMissing + " SimQueue.WaitForTurn").Trim();
            if (sSpsSim != null && sSpsAction != null
                && !Patch(typeof(SimSteeringAdaptor), "HandleEventEncounteredObstacles", null, typeof(SpsSteerHook), "ObstHook", "ObstOrig"))
                sShlMissing = (sShlMissing + " HandleEventEncounteredObstacles").Trim();
            if (sShlMissing.Length > 0) AppliedLog().Add("sharelanes: not found: " + sShlMissing);
        }

        static bool ShlScope(Sim s, bool hhOn) { return hhOn || !IsPlayerSim(s); }  // not HouseholdExempt: HouseholdToo never lifts lane skips, clip-through or path changes

        static string ShlWho(Sim s) { return s == PlumbBob.SelectedActor ? "selected" : s.IsInActiveHousehold ? "household" : "npc"; }

        // Exact types only: a subclass (turnstile, tomb door) may keep state per lane in its lock hooks.
        static int ShlKind(PortalComponent pc)
        {
            Type t = pc.GetType();
            if (t == typeof(Stairs.StairsPortalComponent)) return ShareLanesCore.kStairs;
            if (t == typeof(Door.DoorPortalComponent)) return ShareLanesCore.kDoor;
            if (t == typeof(NonAnimatedDoor.NonAnimatedDoorPortalComponent)) return ShareLanesCore.kGate;
            if (t == typeof(Ladder.LadderPortalComponent)) return ShareLanesCore.kLadder;
            if (t == typeof(StaircaseSpiral.StaircaseSpiralPortalComponent)) return ShareLanesCore.kSpiral;
            if (t == typeof(PoolPortalComponent) && pc.Owner != null && pc.Owner.GetType() == typeof(PoolLadder)) return ShareLanesCore.kPoolLadder;  // poolshare (docs/686 §35)
            return ShareLanesCore.kOther;
        }

        // The portal's table as plain rows. A new array each call: the lane checks may sleep, another Sim's call runs meanwhile.
        static ShareLanesCore.Holder[] ShlHolders(PortalComponent pc, bool hhOn, out int n, out string names)
        {
            n = 0; names = "";
            IDictionary d = sShlLocked.GetValue(pc) as IDictionary;
            if (d == null || d.Count == 0) return null;
            ShareLanesCore.Holder[] h = new ShareLanesCore.Holder[d.Count];
            foreach (DictionaryEntry e in d)
            {
                Sim o = e.Key as Sim;
                PortalComponent.LaneInfo li = (PortalComponent.LaneInfo)e.Value;
                if (o == null || li.mLaneSlots == null || li.mLaneSlots.Length < 1) continue;
                h[n].Lane = li.mLaneIndex;
                h[n].StartSlot = (uint)li.mLaneSlots[0];
                h[n].Elevator = li.ElevatorDoor;
                h[n].InScope = !o.HasBeenDestroyed && o.SimDescription != null && ShlScope(o, hhOn);
                n++;
                if (names.Length < 200) names += (names.Length > 0 ? ", " : "") + (o.HasBeenDestroyed ? "(gone)" : o.FullName + " (" + SwfIa(o) + ")") + " lane " + li.mLaneIndex;
            }
            return h;
        }

        // `this` is the game's portal component; the base class gives the protected lane checks by ordinary virtual calls.
        public abstract class ShlLaneHook : PortalComponent
        {
            public bool FreeLaneHook(Sim s, Vector3 preferredPos, bool checkForSims, Route.RouteOption routeOptions, out LaneInfo laneInfo, out bool preferredLaneSelected)
            {
                if (FreeLaneOrig(s, preferredPos, checkForSims, routeOptions, out laneInfo, out preferredLaneSelected)) return true;
                int[] c = PatchCounters();
                try
                {
                    int kind = s == null || sShlLocked == null ? ShareLanesCore.kOther : ShlKind(this);
                    if (kind == ShareLanesCore.kOther) return false;
                    bool spiral = kind == ShareLanesCore.kSpiral, pool = kind == ShareLanesCore.kPoolLadder;  // own switch pairs and counters, same search
                    int iFails = pool ? kPlsFails : spiral ? kSpiFails : kShlFails, iSharable = pool ? kPlsSharable : spiral ? kSpiSharable : kShlSharable,
                        iShared = pool ? kPlsShared : spiral ? kSpiShared : kShlShared, iWould = pool ? kPlsWould : spiral ? kSpiWould : kShlWould,
                        iExempt = pool ? kPlsExempt : spiral ? kSpiExempt : kShlExempt;
                    c[iFails]++;
                    bool on = ShortcutOn(pool ? "poolshare" : spiral ? "spiralshare" : "sharelanes"), hhOn = ShortcutOn(pool ? "poolsharehh" : spiral ? "spiralsharehh" : "sharelaneshh");
                    int n; string names;
                    ShareLanesCore.Holder[] h = ShlHolders(this, hhOn || !on, out n, out names);  // off: count what both switches would do
                    bool pref = true;
                    List<Slot> starts = GetStartSlotsForApproachingSim(s, preferredPos, ref pref);
                    bool sharable = false;
                    if (starts != null && n > 0)
                        foreach (Slot st in starts)
                            if (st != Slot.None && ShareLanesCore.Lane(h, n, GetLaneIndexFromSlot(st), (uint)st) == ShareLanesCore.kLaneOpposite) { sharable = true; break; }
                    int d = ShareLanesCore.Decide(on, ShlScope(s, hhOn), true, sharable);
                    if (d == ShareLanesCore.kNone) return false;  // refused for another reason (the portal's extra checks, an elevator lane)
                    c[iSharable]++;
                    if (d == ShareLanesCore.kWould) { c[iWould]++; return false; }
                    if (d == ShareLanesCore.kExempt) { c[iExempt]++; return false; }

                    List<Slot> list = new List<Slot>(starts.Count);
                    foreach (Slot st in starts) if (st != Slot.None) list.Add(st);
                    bool haveFallback = false;
                    LaneInfo fallback = laneInfo;
                    while (list.Count > 0)
                    {
                        Slot near = Owner.GetNearestSlot(s, false, preferredPos, list.ToArray());
                        if (!list.Remove(near)) break;  // a slot that is not in the list: nothing more to ask
                        uint lane = GetLaneIndexFromSlot(near);
                        h = ShlHolders(this, hhOn, out n, out names);  // read again: a check below may have slept
                        if (lane == uint.MaxValue || (n > 0 && ShareLanesCore.Lane(h, n, lane, (uint)near) == ShareLanesCore.kLaneClosed)) { pref = false; continue; }
                        Slot[] slots = new Slot[] { near, GetPairedSlot(near) };
                        LaneInfo li = new LaneInfo(lane, slots, GetLaneFlagsForLaneIndex(lane));
                        bool extraOk = DoAdditionalLaneChecks(pref, s, li);
                        bool free = extraOk && IsLaneUnobstructed(slots, s, true, Route.RouteOption.None);
                        bool isFallback;
                        if (ShareLanesCore.Take(extraOk, free, li.NoLanesAvailableSkipsAdditionalLaneChecks, ref haveFallback, out isFallback))
                        {
                            laneInfo = li; preferredLaneSelected = pref;
                            c[iShared]++;
                            return true;
                        }
                        if (isFallback) fallback = li;
                        pref = false;
                    }
                    if (haveFallback)
                    {
                        laneInfo = fallback; preferredLaneSelected = false;
                        c[iShared]++;
                        return true;
                    }
                }
                catch (ResetException) { throw; }
                catch (Exception e) { ShlError(0, e); }
                return false;
            }
            public bool FreeLaneOrig(Sim s, Vector3 preferredPos, bool checkForSims, Route.RouteOption routeOptions, out LaneInfo laneInfo, out bool preferredLaneSelected)
            { laneInfo = new LaneInfo(); preferredLaneSelected = false; return false; }  // receives GetFreeRoutingLane's original body

            // the Sim is sent to the waiting spot: a lane wait begins
            public bool WaitHook(Sim s, Route interruptedPath)
            {
                ShlWaitBegin(this, s);
                return WaitOrig(s, interruptedPath);
            }
            public bool WaitOrig(Sim s, Route interruptedPath) { return true; }  // receives WaitForLaneAndPlantToItRouteInterruptCallback's original body

            // angleentry, ladder climbed from below: the game's point plan instead of its plan to the slot with facing
            public Route PathToLaneHook(Sim sim, ref LaneInfo laneInfo)
            {
                bool swap = false;
                try { swap = AneLadder(this, sim, ref laneInfo); }
                catch (ResetException) { throw; }
                catch (Exception e) { ShlError(1, e); }
                if (!swap) return PathToLaneOrig(sim, ref laneInfo);
                LaneInfo li = laneInfo;
                li.mLaneFlags &= ~LaneInfoFlags.PlanToSlotNotPosition;
                return PathToLaneOrig(sim, ref li);
            }
            public Route PathToLaneOrig(Sim sim, ref LaneInfo laneInfo) { return null; }  // receives GetPathToLane's original body
        }

        static void ShlWaitBegin(PortalComponent pc, Sim s)
        {
            int[] c = PatchCounters();
            try
            {
                if (s == null || pc == null || sShlLocked == null) return;
                c[kShlWaits]++;
                if (s.IsInActiveHousehold) c[kShlWaitsHh]++;
                if (sShlWaitOf.Count > 64) sShlWaitOf.Clear();  // entries leave when the wait ends; this bounds what killed routes left behind
                ShlWait w = new ShlWait();
                w.Start = SimClock.CurrentTicks;
                w.Kind = ShlKind(pc);
                int n;
                ShlHolders(pc, true, out n, out w.Holders);
                sShlWaitOf[s.ObjectId.Value] = w;
            }
            catch (ResetException) { throw; }
            catch (Exception e) { ShlError(2, e); }
        }

        static void ShlWaitEnd(Sim s, GameObject portal, bool got)
        {
            int[] c = PatchCounters();
            try
            {
                ShlWait w;
                if (s == null || !sShlWaitOf.TryGetValue(s.ObjectId.Value, out w)) return;
                sShlWaitOf.Remove(s.ObjectId.Value);
                float min = SimClock.ConvertFromTicks(SimClock.CurrentTicks - w.Start, TimeUnit.Minutes);
                int tenths = ShareLanesCore.Tenths(min);
                bool hh = s.IsInActiveHousehold;
                c[kShlWaitTenths] += tenths;
                if (hh) c[kShlWaitTenthsHh] += tenths;
                if (!got) c[kShlWaitGaveUp]++;
                sShlLast = "lanewait: " + s.FullName + " " + ShlWho(s) + ", " + SwfIa(s) + ": " + (portal == null ? "?" : portal.GetType().Name + " 0x" + portal.ObjectId.Value.ToString("X"))
                    + " (" + ShareLanesCore.kKindNames[w.Kind] + "), " + min.ToString("0.0") + " sim-min from the refusal, " + (got ? "got a lane" : "gave up")
                    + ", held then by: " + (w.Holders.Length > 0 ? w.Holders : "nobody") + ", at " + V(s.Position);
                if (ShortcutOn("lanewaitlog") && sShlCap.Take(System.Environment.TickCount)) DiagLine(sShlLast);
            }
            catch (ResetException) { throw; }
            catch (Exception e) { ShlError(3, e); }
        }

        // `this` is the game's private AcquireLaneRouteAction: the wait at the waiting spot, until a lane is given or 20 sim-minutes are over.
        public class ShlAcquireHook
        {
            public RouteAction.ActionResult PerformHook()
            {
                RouteAction.ActionResult r = RouteAction.ActionResult.Terminate;
                try { r = PerformOrig(); }
                finally { ShlWaitEnd(sShlAcqSim.GetValue(this) as Sim, sShlAcqPortal.GetValue(this) as GameObject, r == RouteAction.ActionResult.Continue); }
                return r;
            }
            public RouteAction.ActionResult PerformOrig() { return RouteAction.ActionResult.Terminate; }  // receives PerformAction's original body
        }

        // StandWait.cs, a blocked cycle: plan without people now? (the stand-and-wait hook then runs its own pass step)
        static bool ShlPortalPass(Sim sim, RoutePlanResult res, int blockedCycle)
        {
            int d = ShareLanesCore.PortalPass(ShortcutOn("sharelanes"), ShlScope(sim, ShortcutOn("sharelaneshh")),
                res.mType == RoutePlanResultType.FailedBecauseDestinationPortalLocked, blockedCycle, kShlPortalPassEvery);
            if (d == ShareLanesCore.kWould) PatchCounters()[kShlPortalWould]++;
            else if (d == ShareLanesCore.kExempt) PatchCounters()[kShlExempt]++;
            return d == ShareLanesCore.kGo;
        }

        // ---- laddershare, spiralshare (the line) -----------------------------------------------------------------------

        // `this` is the game's SimQueue. Every line in the game comes through here: ladders and spiral stairs (below),
        // servingshare's platters and linewaitlog (ObjShare.cs).
        public class LdsQueueHook : SimQueue
        {
            public bool WaitHook(InteractionInstance interaction, IGameObject target, WaitBehavior behavior, ExitReason exitReasons, float timeoutInMinutes)
            {
                int[] c = PatchCounters();
                try
                {
                    Sim a = interaction == null ? null : interaction.InstanceActor;
                    Ladder ladder = a == null ? null : interaction.Target as Ladder;
                    StaircaseSpiral spiral = a == null || ladder != null ? null : interaction.Target as StaircaseSpiral;
                    bool isLadder = ladder != null && object.ReferenceEquals(ladder.Line, this) && interaction.GetType().Name == "PortalClimb";
                    bool isSpiral = spiral != null && object.ReferenceEquals(spiral.SimLine, this) && interaction.GetType().Name == "UseInternal";  // not the railing slide
                    if (isLadder || isSpiral)
                    {
                        bool on = ShortcutOn(isSpiral ? "spiralshare" : "laddershare"), hhOn = ShortcutOn(isSpiral ? "spiralsharehh" : "laddersharehh");
                        bool others = true;
                        List<Sim> line = SimsInQueue;  // null while nobody is in the line
                        if (line != null)
                            foreach (Sim o in line) if (o != null && o != a && !ShlScope(o, hhOn)) others = false;
                        int d = ShareLanesCore.LadderLine(on, true, ShlScope(a, hhOn), others);
                        if (d == ShareLanesCore.kGo) { c[isSpiral ? kSpiLineSkips : kLdsSkips]++; return true; }  // not entered in the line: the game's cleanup finds nothing to remove
                        if (d == ShareLanesCore.kWould && MemberCount() > 0) c[isSpiral ? kSpiLineWould : kLdsWould]++;  // off: a climb that finds somebody in the line
                        else if (d == ShareLanesCore.kExempt) c[isSpiral ? kSpiLineExempt : kLdsExempt]++;
                    }
                }
                catch (ResetException) { throw; }
                catch (Exception e) { ShlError(4, e); }
                // docs/686 §15 (ShowerShare.cs): a partner is let into a shower or an all-in-one bathroom without the line
                if (interaction != null && ShsLine(interaction, this, behavior != WaitBehavior.NeverWait)) return true;
                // docs/686 §14 (ObjShare.cs): servingshare skips a platter's and its surface's line; linewaitlog times the rest
                if (sOjsArmed > 0 && interaction != null && interaction.InstanceActor != null && OjsServeLine(interaction.InstanceActor, this)) return true;
                // docs/686 §19 (CrowdPort.cs): a crowded elevator's line is answered "timed out" (the game's teleport skip follows) or gets a short timeout
                if (interaction != null && CpLineHook(interaction, target, this, behavior, ref timeoutInMinutes)) return false;
                long start = SimClock.CurrentTicks;
                int length = MemberCount();
                LswWait lsw = LswBegin(this, interaction, target);  // docs/686 §25 (LineSwitch.cs): a free twin object is used after a while
                bool got;
                try { got = WaitOrig(interaction, target, behavior, exitReasons, timeoutInMinutes); }
                finally { if (lsw != null) LswEnd(interaction.InstanceActor, lsw); }
                OjsLineWaitEnd(interaction, target, start, length, got, timeoutInMinutes);
                return got;
            }
            public bool WaitOrig(InteractionInstance interaction, IGameObject target, WaitBehavior behavior, ExitReason exitReasons, float timeoutInMinutes) { return false; }  // receives WaitForTurn's original body
        }

        // ---- simpass ----------------------------------------------------------------------------------------------------

        // The lanes of a lot's portals, both ends each. Read from the lot at most every kShlLanesMinutes.
        static float[] ShlLanesOf(Lot lot)
        {
            ShlLanes l;
            ulong id = lot.LotId;
            if (sShlLanes.TryGetValue(id, out l) && SimClock.ElapsedTime(TimeUnit.Minutes, l.Stamp) < kShlLanesMinutes) return l.Ends;
            sShlLaneBuf.Clear();
            foreach (GameObject o in lot.GetObjects<GameObject>())
            {
                PortalComponent pc = o == null ? null : o.PortalComponent;
                if (pc == null) continue;
                uint lanes = Math.Min(pc.GetNumLanes(), 8u);
                for (uint i = 0; i < lanes; i++)
                {
                    Slot[] sl = pc.GetSlotsForLaneIndex(i);
                    if (sl == null || sl.Length < 2) continue;
                    Vector3 a = o.GetPositionOfSlot(sl[0]), b = o.GetPositionOfSlot(sl[1]);
                    sShlLaneBuf.Add(a.x); sShlLaneBuf.Add(a.y); sShlLaneBuf.Add(a.z);
                    sShlLaneBuf.Add(b.x); sShlLaneBuf.Add(b.y); sShlLaneBuf.Add(b.z);
                }
            }
            if (sShlLanes.Count >= kShlLanesMaxLots) sShlLanes.Clear();
            l = new ShlLanes();
            l.Stamp = SimClock.CurrentTime(); l.Ends = sShlLaneBuf.ToArray();
            sShlLanes[id] = l;
            return l.Ends;
        }

        // docs/686 §8.8: p is within kClipPortalNear of a portal lane of the lot (ShareLanesCore.NearLane). The world lot
        // has no portals of its own (its object list is the whole town): never near.
        static bool ShlNearPortal(Lot lot, Vector3 p)
        {
            float near = Instantiator.kClipPortalNear;
            if (lot == null || lot.IsWorldLot || !(near > 0f)) return false;
            float[] e = ShlLanesOf(lot);
            for (int i = 0; i + 5 < e.Length; i += 6)
                if (ShareLanesCore.NearLane(p.x, p.y, p.z, e[i], e[i + 1], e[i + 2], e[i + 3], e[i + 4], e[i + 5], near)) return true;
            return false;
        }

        // The walker or one of the Sims and pets in its way is near a portal: the walk-through needs no grace there.
        static bool ShlNearPortal(Sim walker, List<Sim> inWay)
        {
            Lot lot = walker.LotCurrent;
            if (ShlNearPortal(lot, walker.Position)) return true;
            for (int i = 0; inWay != null && i < inWay.Count; i++)
                if (ShlNearPortal(lot, inWay[i].Position)) return true;
            return false;
        }

        // Who is in the way, and whether the walk-through is due here (ShareLanesCore.Timed). site = kSpsAtEvent (the
        // obstacle event), kSpsAtBegin (the stand-and-wait is about to begin) or kSpsAtGrace (a wait cycle past the grace;
        // counted keeps the kept / would / exempt counters to one per wait there).
        static bool SpsPass(Sim sim, Route route, IList<ObjectGuid> obstacles, int site, ref bool counted)
        {
            int[] c = PatchCounters();
            try
            {
                if (sim == null || obstacles == null || obstacles.Count == 0 || sim.SimRoutingComponent == null) return false;
                bool on = ShortcutOn("simpass"), hhOn = ShortcutOn("simpasshh");
                Vector3 dest = route == null ? Vector3.Invalid : route.GetApproximateOriginalDestination();
                bool haveDest = dest != Vector3.Invalid && dest != Vector3.Origin;
                int sims = 0, others = 0;
                bool all = true, atDest = false;
                bool scopeAll = hhOn || !on;  // off: count what both switches would do
                sSpsSeen.Clear();
                for (int i = 0; i < obstacles.Count; i++)
                {
                    GameObject o = GameObject.GetObject(obstacles[i]);
                    if (o == null || o == sim) continue;
                    Sim b = o as Sim;
                    if (b == null || b.HasBeenDestroyed || b.SimDescription == null || !b.SimDescription.ToddlerOrAbove) { others++; continue; }
                    sims++;
                    sSpsSeen.Add(b);
                    if (!ShlScope(b, scopeAll)) all = false;
                    if (haveDest && (b.Position - dest).LengthSqr() < kSpsDestRadius * kSpsDestRadius) atDest = true;
                }
                bool kept;
                int d = ShareLanesCore.Pass(on, ShlScope(sim, scopeAll), sims, others, all, atDest, out kept);
                bool atGrace = site == kSpsAtGrace;
                if (d != ShareLanesCore.kNone && !atGrace) d = ShareLanesCore.Timed(d, ShlNearPortal(sim, sSpsSeen), 0f, Instantiator.kClipGraceMinutes);
                sSpsSeen.Clear();
                if (d == ShareLanesCore.kDefer)
                {
                    if (site == kSpsAtBegin) sSpsDeferred++;
                    return false;
                }
                if (!atGrace || !counted)
                {
                    if (kept) c[kSpsKept]++;
                    if (d == ShareLanesCore.kWould) c[kSpsWould]++;
                    else if (d == ShareLanesCore.kExempt) c[kSpsExempt]++;
                    if (kept || d != ShareLanesCore.kNone) counted = true;
                }
                if (d != ShareLanesCore.kGo) return false;
                sim.SimRoutingComponent.StartIgnoringObstacles(Math.Max(0.5f, Instantiator.kSimPassMinutes));  // the game's own timer
                c[site == kSpsAtEvent ? kSpsEvents : kSpsActions]++;
                if (atGrace) sSpsGrace++;
                return true;
            }
            catch (ResetException) { throw; }
            catch (Exception e) { ShlError(5, e); return false; }
        }

        // `this` is the game's SimSteeringAdaptor. With the timer running the game's handler skips every Sim in the list.
        public class SpsSteerHook
        {
            public bool ObstHook(RouteContext context, ref List<ObjectGuid> obstacles, bool bForceReplan)
            {
                try
                {
                    Sim sim = sSpsSim.GetValue(this) as Sim;
                    if (sim != null && obstacles != null && obstacles.Count > 0 && sim.SimRoutingComponent != null && !sim.SimRoutingComponent.ShouldIgnoreAllObstacles)
                    {
                        FollowRouteAction fa = sSpsAction.GetValue(this) as FollowRouteAction;
                        Route route = fa == null ? null : fa.RouteToBeFollowed;
                        bool counted = false;
                        if (!PrxPass(sim, route, obstacles, false)) SpsPass(sim, route, obstacles, kSpsAtEvent, ref counted);  // petrelax (PetRelax.cs, §11) first: small pets
                    }
                }
                catch (ResetException) { throw; }
                catch (Exception e) { ShlError(6, e); }
                return ObstOrig(context, ref obstacles, bForceReplan);
            }
            public bool ObstOrig(RouteContext context, ref List<ObjectGuid> obstacles, bool bForceReplan) { return false; }  // receives HandleEventEncounteredObstacles' original body
        }

        // StandWait.cs, before the game's stand-and-wait runs: true = walk on (the game's own answer after a wait in a crowd).
        // Near a portal only; elsewhere the wait begins and SpsObstacleCycle ends it after the grace.
        static bool SpsAction(Sim sim, Route path)
        {
            try
            {
                if (sim == null || path == null || !path.PlanResult.Succeeded()) return false;
                ObjectGuid[] obstacles = path.FindObstaclesForSim(sim.Proxy);
                bool counted = false;
                return obstacles != null && (PrxPass(sim, path, obstacles, true) || SpsPass(sim, path, obstacles, kSpsAtBegin, ref counted));
            }
            catch (ResetException) { throw; }
            catch (Exception e) { ShlError(7, e); return false; }
        }

        // StandWait.cs, after the game's obstacle stand-and-wait cycle (and standwaitfix) answered "keep waiting": the
        // grace path of simpass (docs/686 §8.8). It needs no other switch: a wait kClipGraceMinutes old with only Sims in
        // the way, none at the route's end, ends with the game's own "continue and follow the path". true = keep waiting.
        static bool SpsObstacleCycle(object action, Sim sim, Route path)
        {
            try
            {
                if (sim == null || path == null || sSwfMissing.Length > 0 || !path.PlanResult.Succeeded()) return true;
                SwfState st = SwfStateOf(sim, action);
                if (!ShareLanesCore.GraceDue(SimClock.ElapsedTime(TimeUnit.Minutes, st.Start), Instantiator.kClipGraceMinutes)) return true;
                if (st.SpsCounted && !ShortcutOn("simpass")) return true;  // compare mode: one judgement per wait
                ObjectGuid[] obstacles = path.FindObstaclesForSim(sim.Proxy);
                if (obstacles == null || !SpsPass(sim, path, obstacles, kSpsAtGrace, ref st.SpsCounted)) return true;
                sSwfReturn.SetValue(action, RouteAction.ActionResult.ContinueAndFollowPath);
                sSwfElected.SetValue(action, true);
                return false;
            }
            catch (ResetException) { throw; }
            catch (Exception e) { ShlError(9, e); return true; }
        }

        // ---- angleentry -------------------------------------------------------------------------------------------------

        // Stairs, before the game's portal step (PortalAdaptorLeak.cs's hook of PortalAnimateThroughAction.DoPortal): the
        // first path of the route, the planned approach to the lane's start, is replaced by the game's own "path to lane"
        // when that plan succeeds; a failed plan leaves the route as it was.
        internal static void AneStairs(Sim sim, Route route, GameObject portal, Vector3 portalStart, ref PortalComponent.LaneInfo laneInfo, bool preferredLaneSelected)
        {
            int[] c = PatchCounters();
            try
            {
                PortalComponent pc = portal == null || sim == null || route == null ? null : portal.PortalComponent;
                if (pc == null || pc.GetType() != typeof(Stairs.StairsPortalComponent)) return;
                int d = ShareLanesCore.AngleStairs(ShortcutOn("angleentry"), ShlScope(sim, ShortcutOn("angleentryhh")), true, preferredLaneSelected, laneInfo.ForcePortalPlan, (int)route.GetNumPaths());
                if (d == ShareLanesCore.kWould) c[kAneWould]++;
                else if (d == ShareLanesCore.kExempt) c[kAneKept]++;
                if (d != ShareLanesCore.kGo) return;
                Route approach = pc.GetPathToLane(sim, ref laneInfo);
                if (approach == null || !approach.PlanResult.Succeeded()) { c[kAneKept]++; return; }
                PathData data = new PathData();
                data.ObjectId = portal.ObjectId;
                data.PortalStartPos = portalStart;
                data.PathType = PathType.PortalPath;
                approach.SetPathData(ref data);
                route.PopPath();
                route.InsertRouteSubPathsAtIndex(0u, approach);
                c[kAneStairs]++;
            }
            catch (ResetException) { throw; }
            catch (Exception e) { ShlError(8, e); }
        }

        // The ladder's lower slots (its lanes are {top, bottom} pairs: RoutingSlot_1/_4 left, _2/_5 right).
        static bool AneLadderBottom(Slot s) { return s == Slot.RoutingSlot_4 || s == Slot.RoutingSlot_5; }

        static bool AneLadder(PortalComponent pc, Sim sim, ref PortalComponent.LaneInfo laneInfo)
        {
            if (sim == null || pc.GetType() != typeof(Ladder.LadderPortalComponent) || laneInfo.mLaneSlots == null || laneInfo.mLaneSlots.Length < 2) return false;
            int d = ShareLanesCore.AngleLadder(ShortcutOn("angleentry"), ShlScope(sim, ShortcutOn("angleentryhh")), true, AneLadderBottom(laneInfo.mLaneSlots[0]), laneInfo.PlanToSlotNotPosition);
            int[] c = PatchCounters();
            if (d == ShareLanesCore.kWould) c[kAneWould]++;
            else if (d == ShareLanesCore.kExempt) c[kAneKept]++;
            if (d != ShareLanesCore.kGo) return false;
            c[kAneLadder]++;
            return true;
        }

        // ---- check ------------------------------------------------------------------------------------------------------

        static string ShlCounters()
        {
            int[] c = PatchCounters();
            return "sharelanes: locks refused on stairs/doors/gates/ladders " + c[kShlFails] + ", of them by direction only " + c[kShlSharable] + ", shared " + c[kShlShared]
                + ", would share " + c[kShlWould] + ", exempt " + c[kShlExempt] + "; lane waits " + c[kShlWaits] + " (household " + c[kShlWaitsHh] + "), sim-min waited "
                + (c[kShlWaitTenths] / 10f).ToString("0.0") + " (household " + (c[kShlWaitTenthsHh] / 10f).ToString("0.0") + "), gave up " + c[kShlWaitGaveUp]
                + "; portal closed by standing Sims: passed " + c[kShlPortalPass] + ", would " + c[kShlPortalWould] + ", locked without people too " + c[kShlPortalStillLocked]
                + "; laddershare: line skipped " + c[kLdsSkips] + ", would " + c[kLdsWould] + ", exempt " + c[kLdsExempt]
                + "; spiralshare: locks refused on spiral stairs " + c[kSpiFails] + ", of them by direction only " + c[kSpiSharable] + ", shared " + c[kSpiShared] + ", would share " + c[kSpiWould]
                + ", exempt " + c[kSpiExempt] + ", line skipped " + c[kSpiLineSkips] + ", would " + c[kSpiLineWould] + ", exempt " + c[kSpiLineExempt]
                + "; simpass: at the obstacle event (near a portal) " + c[kSpsEvents] + ", at the wait " + c[kSpsActions] + " (this session: after the grace " + sSpsGrace
                + ", waits begun away from a portal and left to the grace " + sSpsDeferred + "), would " + c[kSpsWould] + ", exempt " + c[kSpsExempt] + ", kept (object or Sim at the route's end) " + c[kSpsKept]
                + "; angleentry: stairs " + c[kAneStairs] + ", ladder " + c[kAneLadder] + ", would " + c[kAneWould] + ", kept " + c[kAneKept]
                + "; errors " + c[kShlErrors] + ShlErrorSites() + ", open waits " + sShlWaitOf.Count + "; " + GlMembers(sShlMissing) + (sShlLast.Length > 0 ? "; last: " + sShlLast : "");
        }

        // ShareLanesCheck run: the rules with controls (read-only). Bug case: a Sim wants lane 0 of a one-lane door from the
        // front while another holds it from the rear: the game refuses, sharelanes shares. result: the counters and the last
        // lane wait. lot: the portal tables of the selected Sim's lot that hold a Sim now.
        public static object ShareLanesCheck(string what)
        {
            if (what == "result") return ShlCounters();
            if (what == "lot") return ShlLot();
            if (what != "run") return "sharelanes <run|result|lot>";
            bool on = ShortcutOn("sharelanes");
            ShareLanesCore.Holder[] h = new ShareLanesCore.Holder[2];
            h[0].Lane = 0; h[0].StartSlot = 20; h[0].InScope = true;
            bool eaRefuses = !ShareLanesCore.EaFree(h, 1, 0, 10);
            bool opposite = ShareLanesCore.Lane(h, 1, 0, 10) == ShareLanesCore.kLaneOpposite;
            bool fixedNow = eaRefuses && opposite && ShareLanesCore.Decide(on, true, true, opposite) == ShareLanesCore.kGo;
            bool ctlSame = ShareLanesCore.EaFree(h, 1, 0, 20) && ShareLanesCore.Lane(h, 1, 0, 20) == ShareLanesCore.kLaneFree;
            h[0].Elevator = true;
            bool ctlElevator = ShareLanesCore.Lane(h, 1, 0, 10) == ShareLanesCore.kLaneClosed;
            h[0].Elevator = false; h[0].InScope = false;
            bool ctlScope = ShareLanesCore.Lane(h, 1, 0, 10) == ShareLanesCore.kLaneClosed && ShareLanesCore.Decide(true, false, true, true) == ShareLanesCore.kExempt;
            bool ctlKind = ShareLanesCore.Decide(true, true, false, true) == ShareLanesCore.kNone;
            bool kept;
            bool ctlPass = ShareLanesCore.Pass(true, true, 2, 0, true, false, out kept) == ShareLanesCore.kGo && ShareLanesCore.Pass(true, true, 1, 1, true, false, out kept) == ShareLanesCore.kNone
                && ShareLanesCore.Pass(true, true, 1, 0, true, true, out kept) == ShareLanesCore.kNone && ShareLanesCore.Pass(true, true, 1, 0, false, false, out kept) == ShareLanesCore.kExempt;
            bool ctlLadder = ShareLanesCore.AngleLadder(true, true, true, false, true) == ShareLanesCore.kNone && ShareLanesCore.AngleLadder(true, true, true, true, true) == ShareLanesCore.kGo
                && ShareLanesCore.LadderLine(true, false, true, true) == ShareLanesCore.kNone;
            float grace = Instantiator.kClipGraceMinutes;
            bool ctlTimed = ShareLanesCore.Timed(ShareLanesCore.kGo, true, 0f, grace) == ShareLanesCore.kGo && ShareLanesCore.Timed(ShareLanesCore.kGo, false, 0f, 2.5f) == ShareLanesCore.kDefer
                && ShareLanesCore.Timed(ShareLanesCore.kGo, false, 2.5f, 2.5f) == ShareLanesCore.kGo && ShareLanesCore.NearLane(1f, 0f, 1f, 0f, 0f, 0f, 0f, 0f, 2f, 1.5f)
                && !ShareLanesCore.NearLane(2f, 0f, 1f, 0f, 0f, 0f, 0f, 0f, 2f, 1.5f) && !ShareLanesCore.NearLane(0f, 3f, 1f, 0f, 0f, 0f, 0f, 0f, 2f, 1.5f);
            bool controls = ctlSame && ctlElevator && ctlScope && ctlKind && ctlPass && ctlLadder && ctlTimed;
            return "sharelanes check (switch " + (on ? "on" : "off") + ", household " + (ShortcutOn("sharelaneshh") ? "on" : "off") + ", laddershare " + (ShortcutOn("laddershare") ? "on" : "off") + ", spiralshare " + (ShortcutOn("spiralshare") ? "on" : "off")
                + ", simpass " + (ShortcutOn("simpass") ? "on" : "off") + ", angleentry " + (ShortcutOn("angleentry") ? "on" : "off") + "): a lane held from the other end is shared=" + fixedNow
                + "; control: same end free either way=" + ctlSame + ", elevator lane closed=" + ctlElevator + ", holder out of scope closed=" + ctlScope + ", other portal kinds kept=" + ctlKind
                + ", walk through Sims only, not at the route's end=" + ctlPass
                + ", at once only near a portal, elsewhere after the grace (" + grace.ToString("0.0") + " sim-min, near = " + Instantiator.kClipPortalNear.ToString("0.0") + " m)=" + ctlTimed + ", ladder: climb down keeps the slot, other lines kept=" + ctlLadder + "; controls ok=" + controls
                + "; verdict: " + GlVerdict(on, fixedNow, controls, sShlMissing.Length == 0) + "; " + ShlCounters();
        }

        static string ShlLot()
        {
            Sim sel = PlumbBob.SelectedActor;
            if (sel == null || sel.LotCurrent == null || sShlLocked == null) return "sharelanes lot: no selected Sim on a lot";
            System.Text.StringBuilder b = new System.Text.StringBuilder("sharelanes lot:");
            int portals = 0, held = 0;
            foreach (GameObject o in sel.LotCurrent.GetObjects<GameObject>())
            {
                PortalComponent pc = o.PortalComponent;
                if (pc == null) continue;
                portals++;
                int n; string names;
                ShlHolders(pc, true, out n, out names);
                if (n == 0) continue;
                held++;
                if (b.Length < 400) b.Append(" " + o.GetType().Name + " 0x" + o.ObjectId.Value.ToString("X") + " (" + ShareLanesCore.kKindNames[ShlKind(pc)] + ", " + pc.GetNumLanes() + " lanes): " + names + ";");
            }
            return b.Append(" portal objects " + portals + ", with a lane held " + held).ToString();
        }
    }
}
