using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.ObjectComponents;
using Sims3.Gameplay.Objects.CookingObjects;
using Sims3.Gameplay.Objects.Seating;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // seatclash / seatclashhh / seatclashlog (docs/686 §42, default off). The player's report: "the clashing animations when
    // getting into the table". No wait log sees these: the Sim walks to a chair, plays the route-fail shrug and gives up.
    //
    // [verified, decompiled] Three ways a walk to a dining chair ends in a shrug (ChairDining.RouteToForSitting; the bar
    // stool has the same steps):
    //   1. taken on arrival. SitHereForEating.Run picks its chair with EatingSurfaceImpl.GetClosestFreeSlotPair (nearest
    //      chair not in use), holds it in the chair's use list only while the plate is put down and lets go of it before
    //      the walk to the chair. A second Sim picking in that gap gets the same chair; the later one shrugs at it.
    //   2. push failed. At the chair the game pushes the Sims standing behind it (PushSimsFromFootprint, waitForPushes)
    //      and waits up to 15 sim-min; a Sim that cannot be pushed (talking, busy) makes the answer false at once: shrug.
    //   3. pull-out refused. CanScootBack, the build-mode placement test with only the sitting Sim allowed: a Sim still
    //      standing behind the chair refuses it: shrug. (Doors and furniture: doorseat / seatreach, docs/686 §39.)
    //
    // Probe (always counted, lines with seatclashlog): every approach's exit by cause, the push waits in sim-min, and the
    // time from the chair pick (or the approach's start) to seated.
    // Fix, for the Sims the switches cover (seatclash = Sims the player does not control, seatclashhh = household and
    // selected Sims; HhSharedCore.Scope):
    //   a. The picked chair is held for the Sim in nextplan's claim table (HhHold) until it sits or the walk fails. A pick
    //      that lands on a chair another Sim holds or claims is made again without those chairs (EA's own rule over the
    //      rest); when every free chair is held the table counts as full for this Sim (EA's "no chair" answer: no walk,
    //      no shrug). The table picks the chair in this path, so a chair the player clicked is never changed.
    //   b. The push wait: EA's pushes are started as before, then the Sim waits until the area behind the chair is clear,
    //      at most the shared grace (HhGraceMin, 2.5 sim-min; none when nobody sees it, camsafe). After the grace the
    //      chair pulls out past the neighbour (a brief partial overlap; nobody walks through a standing Sim).
    //   c. The pull-out test after such a grace is made again with Sims allowed (the chair's own mAllowIntersectionAllSims,
    //      which the game uses for the feet test); furniture, walls and doors still refuse it.
    // Works with: nextplan (one claim table), doorseat / seatreach (their CanScootBack swap runs inside this one: they
    // answer first, this only adds the Sims-only retry), clipwait (the same grace), camsafe (unseen = no grace).
    // Off = compare mode: counted, nothing changed. Counters 5020-5039. Check: SeatClashCheck.
    public static partial class Fixes
    {
        const int kScApproach = 5020, kScSeated = 5021, kScTaken = 5022, kScPushFail = 5023, kScScoot = 5024, kScOther = 5025,
            kScShrug = 5026, kScPushWaits = 5027, kScPushTenths = 5028, kScPickHeld = 5029, kScRepick = 5030, kScRepickWould = 5031,
            kScFull = 5032, kScHolds = 5033, kScPushGrace = 5034, kScPushWould = 5035, kScScootAllowed = 5036, kScClipSeen = 5037,
            kScErrors = 5038, kScLines = 5039;

        sealed class ScRun
        {
            public ulong Chair; public long PickTick, EnterTick; public int PickMs, EnterMs;
            public bool Approach, Stool, PushFailed, PushGrace, ScootRefused;
        }

        static readonly Dictionary<ulong, ScRun> sScRuns = new Dictionary<ulong, ScRun>();  // by Sim; bounded, cleared at world load
        static readonly LineCapCore sScCap = new LineCapCore(30, 60000, 600);
        static readonly NextPlanCore.Hist sScSeatMs = new NextPlanCore.Hist(), sScSeatTenths = new NextPlanCore.Hist(), sScPushHist = new NextPlanCore.Hist();
        static readonly int[] sScHh = new int[SeatClashCore.kExits];  // household Sims' exits by cause
        static FieldInfo sScAllSims, sScAllow;
        static Type sScSitType;  // SitHereForEating (internal to the game)
        static string sScMiss = "", sScLast = "", sScLastError = "";
        static float[] sScDist = new float[16]; static bool[] sScFree = new bool[16], sScFood = new bool[16];

        static void ApplySeatClash()
        {
            sScMiss = "";
            BindingFlags bf = BindingFlags.Instance | BindingFlags.NonPublic;
            sScAllSims = typeof(ChairDining).GetField("mAllowIntersectionAllSims", bf);
            sScAllow = typeof(ChairDining).GetField("mAllowIntersectionObject", bf);
            if (sScAllSims == null || sScAllow == null) sScMiss += "ChairDining.mAllowIntersection* ";
            sScSitType = typeof(EatingSurfaceImpl).Assembly.GetType("Sims3.Gameplay.Objects.CookingObjects.SitHereForEating");
            if (sScSitType == null) sScMiss += "SitHereForEating ";
            Type[] route = { typeof(Sim), typeof(Slot), typeof(bool), typeof(SitData).MakeByRefType(), typeof(Slot).MakeByRefType(), typeof(object).MakeByRefType() };
            Patch(typeof(ChairDining), "RouteToForSitting", route, typeof(ScChairHook), "RouteHook", "RouteOrig");
            Patch(typeof(ChairBarStool), "RouteToForSitting", route, typeof(ScStoolHook), "RouteHook", "RouteOrig");
            Patch(typeof(ChairDining), "CanScootBack", new Type[] { typeof(Sim) }, typeof(ScChairHook), "ScootHook", "ScootOrig");
            Patch(typeof(GameObject), "PushSimsFromFootprint", new Type[] { typeof(uint), typeof(Sim), typeof(List<Sim>), typeof(bool), typeof(float), typeof(bool) },
                typeof(ScPushHook), "PushHook", "PushOrig");
            Patch(typeof(EatingSurfaceImpl), "GetClosestFreeSlotPair",
                new Type[] { typeof(IEatingSurface), typeof(Sim), typeof(IEdible), typeof(bool), typeof(bool).MakeByRefType() }, typeof(ScPickHook), "PickHook", "PickOrig");
            sScMiss = sScMiss.Trim();
            if (sScMiss.Length > 0) AppliedLog().Add("seatclash: not found: " + sScMiss);
            // the switches of these hooks, named here for the live-path map (CheckLiveMap.cs reads the Apply section)
            if (ShortcutOn("seatclash") || ShortcutOn("seatclashhh") || ShortcutOn("seatclashlog")) AppliedLog().Add("seatclash: switched on at start");
            World.OnWorldLoadFinishedEventHandler -= ScWorldLoaded;
            World.OnWorldLoadFinishedEventHandler += ScWorldLoaded;
        }

        static void ScWorldLoaded(object sender, EventArgs e) { sScRuns.Clear(); }

        public abstract class ScChairHook : ChairDining
        {
            public bool RouteHook(Sim s, Slot preferredSlot, bool bCheckScootBack, out SitData partToSitDownIn, out Slot routingSlot, out object context)
            {
                ScRun run = ScEnter(this, s, false);
                bool r = RouteOrig(s, preferredSlot, bCheckScootBack, out partToSitDownIn, out routingSlot, out context);
                if (run != null) ScExit(this, s, run, r);
                return r;
            }
            public bool RouteOrig(Sim s, Slot preferredSlot, bool bCheckScootBack, out SitData partToSitDownIn, out Slot routingSlot, out object context)
            {
                partToSitDownIn = null; routingSlot = Slot.None; context = null;
                return false;
            }

            public bool ScootHook(Sim s)
            {
                bool r = ScootOrig(s);
                return r || (s != null && sScRuns.Count > 0 && ScScoot(this, s));
            }
            public bool ScootOrig(Sim s) { return false; }
        }

        public abstract class ScStoolHook : ChairBarStool
        {
            public bool RouteHook(Sim s, Slot preferredSlot, bool bCheckScootBack, out SitData partToSitDownIn, out Slot routingSlot, out object sitContext)
            {
                ScRun run = ScEnter(this, s, true);
                bool r = RouteOrig(s, preferredSlot, bCheckScootBack, out partToSitDownIn, out routingSlot, out sitContext);
                if (run != null) ScExit(this, s, run, r);
                return r;
            }
            public bool RouteOrig(Sim s, Slot preferredSlot, bool bCheckScootBack, out SitData partToSitDownIn, out Slot routingSlot, out object sitContext)
            {
                partToSitDownIn = null; routingSlot = Slot.None; sitContext = null;
                return false;
            }
        }

        public abstract class ScPushHook : GameObject
        {
            public bool PushHook(uint footprintNameHash, Sim actor, List<Sim> exceptions, bool waitForPushes, float footprintExtension, bool bGangwayPush)
            {
                ScRun run = waitForPushes && actor != null && sScRuns.Count > 0 ? ScRunAt(this, actor) : null;
                if (run == null) return PushOrig(footprintNameHash, actor, exceptions, waitForPushes, footprintExtension, bGangwayPush);
                int scope = ScScope(actor);
                DateAndTime t0 = SimClock.CurrentTime();
                bool r;
                if (scope != HhSharedCore.kGo)
                {
                    r = PushOrig(footprintNameHash, actor, exceptions, true, footprintExtension, bGangwayPush);
                    ScPushed(this, actor, run, SimClock.ElapsedTime(TimeUnit.Minutes, t0), r, scope, SeatClashCore.kWait);
                    return r;
                }
                // EA's pushes start (no wait), then the Sim waits for a clear area, the shared grace at most
                r = PushOrig(footprintNameHash, actor, exceptions, false, footprintExtension, bGangwayPush);
                int step = ScPushWait(this, footprintNameHash, footprintExtension, actor, exceptions, t0);
                ScPushed(this, actor, run, SimClock.ElapsedTime(TimeUnit.Minutes, t0), r, scope, step);
                return step != SeatClashCore.kWait;
            }
            public bool PushOrig(uint footprintNameHash, Sim actor, List<Sim> exceptions, bool waitForPushes, float footprintExtension, bool bGangwayPush) { return false; }
        }

        public class ScPickHook
        {
            public static SlotPair PickHook(IEatingSurface implementor, Sim sim, IEdible food, bool bCheckForFoodSlot, ref bool bHasFreeFoodSlot)
            {
                SlotPair r = PickOrig(implementor, sim, food, bCheckForFoodSlot, ref bHasFreeFoodSlot);
                if (r == null || sim == null || implementor == null) return r;
                try { return ScPick(implementor, sim, food, bCheckForFoodSlot, ref bHasFreeFoodSlot, r); }
                catch (ResetException) { throw; }
                catch (Exception e) { ScError(e); return r; }
            }
            public static SlotPair PickOrig(IEatingSurface implementor, Sim sim, IEdible food, bool bCheckForFoodSlot, ref bool bHasFreeFoodSlot) { return null; }
        }

        static int ScScope(Sim s)
        {
            return HhSharedCore.Scope(true, LswHuman(s), IsPlayerSim(s), ShortcutOn("seatclash"), ShortcutOn("seatclashhh"));
        }

        static string ScHead(Sim s, GameObject seat)
        {
            return BuWho(s) + " " + s.FullName + " -> " + seat.GetType().Name + " 0x" + seat.ObjectId.Value.ToString("X");
        }

        static void ScLine(string text)
        {
            sScLast = text;
            PatchCounters()[kScLines]++;
            if (ShortcutOn("seatclashlog") && sScCap.Take(System.Environment.TickCount)) DiagLine("seatclash: " + text);
        }

        static void ScError(Exception e)
        {
            int[] c = PatchCounters();
            c[kScErrors]++;
            sScLastError = "seatclash: error: " + e.GetType().Name + ": " + e.Message;
            if (c[kScErrors] <= 3) DiagLine(sScLastError);
        }

        static ScRun ScRunOf(Sim s, bool make)
        {
            ulong id = s.ObjectId.Value;
            ScRun run;
            if (sScRuns.TryGetValue(id, out run) || !make) return run;
            if (sScRuns.Count >= 256) sScRuns.Clear();  // bounded: a cleared Sim only loses one probe sample
            run = new ScRun();
            sScRuns[id] = run;
            return run;
        }

        // the Sim's running approach at this seat (null: none, another seat, or a stale record)
        static ScRun ScRunAt(GameObject seat, Sim s)
        {
            ScRun run;
            if (!sScRuns.TryGetValue(s.ObjectId.Value, out run) || !run.Approach || run.Chair != seat.ObjectId.Value) return null;
            if (SeatClashCore.Stale((SimClock.CurrentTicks - run.EnterTick) / (float)HhTicks(1f))) { run.Approach = false; return null; }
            return run;
        }

        static ScRun ScEnter(GameObject seat, Sim s, bool stool)
        {
            if (s == null) return null;
            try
            {
                ScRun run = ScRunOf(s, true);
                ulong id = seat.ObjectId.Value;
                long now = SimClock.CurrentTicks;
                if (run.Chair != id || SeatClashCore.Stale((now - run.PickTick) / (float)HhTicks(1f))) { run.Chair = id; run.PickTick = 0; }
                run.Approach = true; run.Stool = stool; run.PushFailed = run.PushGrace = run.ScootRefused = false;
                run.EnterTick = now; run.EnterMs = System.Environment.TickCount;
                PatchCounters()[kScApproach]++;
                return run;
            }
            catch (ResetException) { throw; }
            catch (Exception e) { ScError(e); return null; }
        }

        static void ScExit(GameObject seat, Sim s, ScRun run, bool ok)
        {
            try
            {
                run.Approach = false;
                int[] c = PatchCounters();
                bool taken = !ok && ((seat.InUse && !seat.IsActorUsingMe(s)) || s.HasExitReason(ExitReason.ObjectInUse));
                int exit = SeatClashCore.Exit(ok, run.PushFailed, taken, run.ScootRefused);
                c[kScSeated + exit]++;
                bool hh = IsPlayerSim(s);
                if (hh) sScHh[exit]++;
                long t0 = run.PickTick != 0 ? run.PickTick : run.EnterTick;
                int ms0 = run.PickTick != 0 ? run.PickMs : run.EnterMs;
                long tenths = (SimClock.CurrentTicks - t0) * 10 / HhTicks(1f);
                if (ok) { sScSeatMs.Add(System.Environment.TickCount - ms0); sScSeatTenths.Add(tenths); }
                else
                {
                    HhDrop(seat.ObjectId.Value, s);  // the chair is free for the next pick
                    bool shrug = SeatClashCore.Shrug(exit, run.Stool);
                    if (shrug) c[kScShrug]++;
                    ScLine(ScHead(s, seat) + ": " + SeatClashCore.ExitNames[exit] + (shrug ? " (shrug)" : "") + " after " + (tenths / 10f).ToString("0.0") + " sim-min");
                }
                run.PickTick = 0;
            }
            catch (ResetException) { throw; }
            catch (Exception e) { ScError(e); }
        }

        // Another Sim holds or claims the chair (nextplan's table: a seatclash hold, or a Sim whose next action sits there).
        static bool ScHeldByOther(GameObject chair, Sim sim)
        {
            ulong h = HhHolder(chair.ObjectId.Value);
            if (h == 0 || h == sim.ObjectId.Value) return false;
            return HhSim(h) != null;
        }

        static SlotPair ScPick(IEatingSurface table, Sim sim, IEdible food, bool wantFood, ref bool hasFood, SlotPair r)
        {
            GameObject chair = table.GetContainedObject(r.ChairSlot) as GameObject;
            if (chair == null) return r;
            InteractionInstance cur = sim.CurrentInteraction;
            bool sitting = cur != null && sScSitType != null && cur.GetType() == sScSitType && object.ReferenceEquals(cur.Target, table);  // the pick that is walked, not a score
            int[] c = PatchCounters();
            if (ScHeldByOther(chair, sim))
            {
                c[kScPickHeld]++;
                int scope = ScScope(sim);
                if (scope != HhSharedCore.kGo)
                {
                    if (scope == HhSharedCore.kWould && sitting) { c[kScRepickWould]++; ScLine(ScHead(sim, chair) + ": picked a chair another Sim walks to: would pick again"); }
                    return r;  // compare mode: EA's pick, the first Sim's hold stays
                }
                bool has;
                SlotPair r2 = ScRepick(table, sim, food, wantFood, out has);
                if (sitting)
                {
                    if (r2 == null) { c[kScFull]++; ScLine(ScHead(sim, chair) + ": every free chair is taken by a Sim on its way: no chair"); }
                    else { c[kScRepick]++; ScLine(ScHead(sim, chair) + ": another Sim walks to it, takes " + ScSeatName(table, r2) + " instead"); }
                }
                if (r2 == null) return null;
                r = r2; hasFood = has;
                chair = table.GetContainedObject(r.ChairSlot) as GameObject;
                if (chair == null) return r;
            }
            if (!sitting) return r;
            ScRun run = ScRunOf(sim, true);
            if (run.Chair != 0 && run.Chair != chair.ObjectId.Value) HhDrop(run.Chair, sim);
            run.Chair = chair.ObjectId.Value; run.PickTick = SimClock.CurrentTicks; run.PickMs = System.Environment.TickCount; run.Approach = false;
            if (HhHold(run.Chair, sim, Instantiator.kNextPlanClaimMinutes)) c[kScHolds]++;
            return r;
        }

        static string ScSeatName(IEatingSurface table, SlotPair p)
        {
            GameObject o = table.GetContainedObject(p.ChairSlot) as GameObject;
            return o == null ? "?" : o.GetType().Name + " 0x" + o.ObjectId.Value.ToString("X");
        }

        // EA's pick rule (nearest free chair, with wantFood the nearest whose food slot is free) over the chairs no other
        // Sim holds. null: none.
        static SlotPair ScRepick(IEatingSurface table, Sim sim, IEdible food, bool wantFood, out bool hasFood)
        {
            hasFood = false;
            SlotPair[] pairs = table.FoodSlotPairs;
            int n = pairs.Length;
            if (sScDist.Length < n) { sScDist = new float[n]; sScFree = new bool[n]; sScFood = new bool[n]; }
            for (int i = 0; i < n; i++)
            {
                sScFree[i] = sScFood[i] = false; sScDist[i] = 0f;
                GameObject ch = table.GetContainedObject(pairs[i].ChairSlot) as GameObject;
                ISittable st = ch as ISittable;
                if (st == null || st.BeingUsed || !ch.InWorld || ScHeldByOther(ch, sim) || !st.CanGetInAndOut(sim)) continue;
                sScFree[i] = true;
                sScDist[i] = sim.GetDistanceToObject(ch);
                sScFood[i] = !table.IsSlotOccupied(pairs[i].FoodSlot) && table.Surface.PutDownTest(table.Surface.GetSurfaceSlotFromContainmentSlot(pairs[i].FoodSlot), food);
            }
            int pick = SeatClashCore.Pick(sScDist, sScFree, sScFood, n, wantFood, out hasFood);
            return pick < 0 ? null : pairs[pick];
        }

        // The wait behind the chair after EA's pushes started: until clear, cancelled, or the grace.
        static int ScPushWait(GameObject seat, uint footprint, float extension, Sim actor, List<Sim> exceptions, DateAndTime t0)
        {
            try
            {
                float grace = HhGraceMin();
                bool unseen = !HhSnapSeen(actor, seat.Position);
                actor.LoopIdle();
                for (int i = 0; i < 400; i++)
                {
                    int step = SeatClashCore.PushStep(ScBehindClear(seat, footprint, extension, actor, exceptions), unseen, SimClock.ElapsedTime(TimeUnit.Minutes, t0), grace);
                    if (step != SeatClashCore.kWait) return step;
                    actor.WaitForExitReason(SimClock.ConvertFromTicks(10u, TimeUnit.Minutes), ExitReason.Canceled | ExitReason.TimedOut);
                    if (actor.HasBeenDestroyed || actor.HasExitReason(ExitReason.Canceled | ExitReason.TimedOut)) return SeatClashCore.kWait;  // EA's answer for a cancelled wait: false
                }
                return SeatClashCore.kGraceOver;
            }
            catch (ResetException) { throw; }
            catch (Exception e) { ScError(e); return SeatClashCore.kGraceOver; }
        }

        static bool ScBehindClear(GameObject seat, uint footprint, float extension, Sim actor, List<Sim> exceptions)
        {
            Sim[] there = Sims3.Gameplay.Queries.GetSimsIntersectingFootprint(seat.ObjectId, footprint, extension);
            if (there == null) return true;
            for (int i = 0; i < there.Length; i++)
            {
                Sim o = there[i];
                if (o == null || o == actor || o.HasBeenDestroyed || (exceptions != null && exceptions.Contains(o))) continue;
                if (actor.Posture != null && object.ReferenceEquals(actor.Posture.Container, o)) continue;  // a carried toddler's carrier (EA's AddExceptions)
                return false;
            }
            return true;
        }

        static void ScPushed(GameObject seat, Sim actor, ScRun run, float minutes, bool eaAnswer, int scope, int step)
        {
            try
            {
                int[] c = PatchCounters();
                if (minutes < 0f) minutes = 0f;
                int tenths = (int)(minutes * 10f + 0.5f);
                bool waited = tenths > 0;
                if (waited) { c[kScPushWaits]++; c[kScPushTenths] += tenths; sScPushHist.Add(tenths); }
                if (scope != HhSharedCore.kGo)
                {
                    run.PushFailed = !eaAnswer;
                    if (scope == HhSharedCore.kWould && (!eaAnswer || minutes > HhGraceMin())) c[kScPushWould]++;
                    if (!eaAnswer || waited) ScLine(ScHead(actor, seat) + ": push wait " + minutes.ToString("0.0") + " sim-min, " + (eaAnswer ? "cleared" : "failed (shrug)"));
                    return;
                }
                if (step == SeatClashCore.kWait) { run.PushFailed = true; return; }  // cancelled: EA's own exit
                if (step == SeatClashCore.kGraceOver)
                {
                    run.PushGrace = true;
                    c[kScPushGrace]++;
                    bool seen = HhSnapSeen(actor, seat.Position);
                    if (seen) c[kScClipSeen]++;
                    ScLine(ScHead(actor, seat) + ": a Sim behind the chair after " + minutes.ToString("0.0") + " sim-min" + (eaAnswer ? "" : " (not pushable)")
                        + ": the chair pulls out past it" + (seen ? " (on screen)" : " (unseen)"));
                }
                else if (waited) ScLine(ScHead(actor, seat) + ": push wait " + minutes.ToString("0.0") + " sim-min, cleared");
            }
            catch (ResetException) { throw; }
            catch (Exception e) { ScError(e); }
        }

        // The game (and doorseat / seatreach inside) refused the pull-out test. True = allowed: the push grace ended for
        // this approach and only Sims are in the way.
        static bool ScScoot(ChairDining chair, Sim s)
        {
            try
            {
                ScRun run = ScRunAt(chair, s);
                if (run == null) return false;
                bool pass = run.PushGrace && ScScootWithSims(chair, s);
                if (SeatClashCore.ScootAllow(false, run.PushGrace, pass, ScScope(s)))
                {
                    PatchCounters()[kScScootAllowed]++;
                    ScLine(ScHead(s, chair) + ": pull-out refused by a Sim behind the chair: allowed");
                    return true;
                }
                run.ScootRefused = true;
                return false;
            }
            catch (ResetException) { throw; }
            catch (Exception e) { ScError(e); return false; }
        }

        // The game's pull-out test with every Sim allowed to intersect (its own flag for the feet test). Footprints are put
        // back as the game leaves them.
        static bool ScScootWithSims(ChairDining ch, Sim s)
        {
            if (sScAllSims == null || sScAllow == null) return false;
            uint feet = ResourceUtils.HashString32("ftp_Feet");
            ch.DisableFootprint("ftp_Center");
            if (ch.IsFootprintEnabled(feet)) ch.DisableFootprint(feet);
            sScAllow.SetValue(ch, s);
            sScAllSims.SetValue(ch, true);
            try { return World.TestObjectPlacement(ch.ObjectId, TestObjectPlacementBooleans.TemporaryPlacement, ch.Position, ch.ForwardVector); }
            finally
            {
                sScAllow.SetValue(ch, null);
                sScAllSims.SetValue(ch, false);
                ch.EnableFootprint("ftp_Center");
            }
        }

        // Repro helpers (test set-up, not part of the fix). clear: every plate and platter on the selected Sim's lot that no
        // Sim holds or uses is destroyed, so each round starts with an empty table. seats: the lot's dining tables with their
        // chairs and who sits or holds.
        static string ScClear()
        {
            Sim sel = Sims3.Gameplay.Core.PlumbBob.SelectedActor;
            if (sel == null || sel.LotCurrent == null) return "seatclash clear: no selected Sim on a lot";
            int gone = 0, kept = 0;
            foreach (IEdible e in sel.LotCurrent.GetObjects<IEdible>())
            {
                GameObject o = e as GameObject;
                if (o == null || o.HasBeenDestroyed) continue;
                if (o.InUse || o.Parent is Sim) { kept++; continue; }
                o.Destroy(); gone++;
            }
            return "seatclash clear: destroyed " + gone + " plates / platters, kept " + kept + " held or in use";
        }

        static string ScSeats()
        {
            Sim sel = Sims3.Gameplay.Core.PlumbBob.SelectedActor;
            if (sel == null || sel.LotCurrent == null) return "seatclash seats: no selected Sim on a lot";
            System.Text.StringBuilder b = new System.Text.StringBuilder("seatclash seats:");
            int seated = 0;
            foreach (Sims3.Gameplay.Objects.TableDining t in sel.LotCurrent.GetObjects<Sims3.Gameplay.Objects.TableDining>())
            {
                int chairs = 0, used = 0, held = 0;
                foreach (SlotPair sp in t.FoodSlotPairs)
                {
                    GameObject ch = t.GetContainedObject(sp.ChairSlot) as GameObject;
                    if (ch == null) continue;
                    chairs++;
                    if (ch.InUse) used++;
                    if (HhHolder(ch.ObjectId.Value) != 0) held++;
                }
                seated += used;
                b.Append(" " + t.GetType().Name + " 0x" + t.ObjectId.Value.ToString("X") + " chairs " + chairs + " of " + t.FoodSlotPairs.Length + " slots, in use " + used + ", held or claimed " + held + ";");
            }
            return b.Append(" seated " + seated).ToString();
        }

        // SeatClashCheck: "result" the counters and the last line; "reset" clears the session figures (repro arms); "clear" / "seats" the repro helpers;
        // "run" the rules with controls (read-only).
        public static object SeatClashCheck(string what)
        {
            try
            {
                int[] c = PatchCounters();
                if (what == "reset")
                {
                    for (int i = kScApproach; i <= kScLines; i++) c[i] = 0;
                    sScSeatMs.Reset(); sScSeatTenths.Reset(); sScPushHist.Reset(); Array.Clear(sScHh, 0, sScHh.Length);
                    sScLast = ""; sScLastError = "";
                    return "seatclash: reset";
                }
                if (what == "clear") return ScClear();
                if (what == "seats") return ScSeats();
                if (what == "run")
                {
                    const int G = HhSharedCore.kGo;
                    bool has;
                    bool fixedNow = SeatClashCore.Pick(new float[] { 1f, 2f, 3f }, new bool[] { false, true, true }, new bool[] { false, false, true }, 3, true, out has) == 2 && has
                        && SeatClashCore.PushStep(false, false, 2.5f, 2.5f) == SeatClashCore.kGraceOver && SeatClashCore.ScootAllow(false, true, true, G);
                    bool controls = SeatClashCore.Pick(new float[] { 1f }, new bool[] { false }, new bool[] { true }, 1, true, out has) == -1
                        && SeatClashCore.PushStep(false, false, 1f, 2.5f) == SeatClashCore.kWait && SeatClashCore.PushStep(true, false, 9f, 2.5f) == SeatClashCore.kClear
                        && !SeatClashCore.ScootAllow(false, true, false, G) && !SeatClashCore.ScootAllow(false, false, true, G) && !SeatClashCore.ScootAllow(false, true, true, HhSharedCore.kWould)
                        && HhSharedCore.Scope(true, true, true, true, false) == HhSharedCore.kExempt;
                    string live = CkLive("seatclash");
                    return "seatclash run: fixed " + (fixedNow && live.Length == 0 ? "yes" : "NO") + ", controls " + (controls ? "ok" : "FAILED") + (live.Length > 0 ? "; live path: " + live : "") + (sScMiss.Length > 0 ? "; not found: " + sScMiss : "");
                }
                return "seatclash " + (ShortcutOn("seatclash") ? "on" : "off") + ", seatclashhh " + (ShortcutOn("seatclashhh") ? "on" : "off")
                    + ": approaches " + c[kScApproach] + " (seated " + c[kScSeated] + ", taken on arrival " + c[kScTaken] + ", push failed " + c[kScPushFail]
                    + ", pull-out refused " + c[kScScoot] + ", walk failed / other " + c[kScOther] + "); shrugs " + c[kScShrug]
                    + "; household exits seated/taken/push/pull-out/other " + sScHh[0] + "/" + sScHh[1] + "/" + sScHh[2] + "/" + sScHh[3] + "/" + sScHh[4]
                    + "; push waits " + c[kScPushWaits] + ", " + (c[kScPushTenths] / 10f).ToString("0.0") + " sim-min (tenths: " + sScPushHist.Text() + ")"
                    + "; pick to seated ms: " + sScSeatMs.Text() + "; sim-min tenths: " + sScSeatTenths.Text()
                    + "; picks of a chair another Sim walks to " + c[kScPickHeld] + " (picked again " + c[kScRepick] + ", would " + c[kScRepickWould] + ", no chair left " + c[kScFull]
                    + "), holds " + c[kScHolds] + "; push grace ended " + c[kScPushGrace] + " (would " + c[kScPushWould] + "), pull-outs allowed past a Sim " + c[kScScootAllowed]
                    + ", of the grace ends on screen " + c[kScClipSeen] + ", snaps 0; errors " + c[kScErrors] + (sScLastError.Length > 0 ? " (" + sScLastError + ")" : "")
                    + (sScMiss.Length > 0 ? "; not found: " + sScMiss : "") + (sScLast.Length > 0 ? "; last: " + sScLast : "");
            }
            catch (ResetException) { throw; }
            catch (Exception e) { return "seatclash check: " + e.GetType().Name + ": " + e.Message; }
        }
    }
}
