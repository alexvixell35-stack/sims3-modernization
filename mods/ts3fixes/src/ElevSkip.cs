using System;
using System.Collections.Generic;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects.Elevator;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Elevator waits (docs/686 §33). Default off; rules in ElevSkipCore.cs.
    //
    // [verified, decompiled ElevatorDoors / ElevatorInterior / PortalComponent.AcquireLaneRouteAction] One car serves a
    // column: a Sim boarding on any floor puts itself in the use list of every other floor's car object
    // (ElevatorInterior.AddElevatorColumnToUseList), and the doors give no lane while their car is in use
    // (GetStartSlotsForApproachingSim answers no slots, ElevatorDoorsPortalComponent.OnPortalStart route-fails). A ride
    // keeps the column busy for the boarding (7 sim-min of "arrive" effects when the doors are closed,
    // ElevatorDoors.kLongEffectsTime), the hold for more riders (5, kMinutesToHoldElevator, renewed by each rider), the
    // leave effects (7) and the arrival on the far floor (7). Every Sim on another floor stands in the doors' line loop
    // meanwhile, asked again every quarter sim-minute for up to 30 sim-minutes (kElevatorGiveupTime), and EA's teleport
    // skip (TryTeleportSkip) only helps when the car on its floor is free with open doors. Storm logs s532-s560: 1,462
    // elevator lane waits, 9,730 sim-min, most of them NPCs in Bridgeport towers; 341 of them gave up after a mean of 13
    // sim-min with no Sim holding the doors lane (the car was elsewhere).
    //
    // elevskip (NPCs) / elevskiphh (household and selected Sims, and NPCs in view) / elevskiphhunseen (unseen household Sims)
    //   Fix: a Sim in the doors' line (the line hook, CrowdPort.cs CpLineHook) whose route rides this column is set at the
    //   doors of the floor its ride ends on: the line answers "timed out", so EA's WaitInLine runs TryTeleportSkip, and our
    //   hook of TryTeleportSkip places the Sim (EA's FindGoodLocation near the far doors' outer slot, as the game's own exit
    //   does) and answers true, so the game replans from there exactly as after its own skip. The car, its use lists,
    //   its riders and the doors are not touched. An unseen NPC (the Sim and the far doors off screen and away from
    //   viewers) goes at once with no fade; with elevskiphh the others go after kElevSkipHhWait sim-minutes with EA's fade.
    //   elevskiphhunseen (default off, only with elevskip + elevskiphh) is an explicit player opt-in that lifts the household
    //   rule (household Sims never count as unseen) for elevator skips only: a household Sim that is not the selected Sim,
    //   off screen itself, with the far doors off screen and away from viewers, goes at once with no fade like an unseen NPC.
    //   The selected Sim never counts as unseen; a household Sim on screen keeps elevskiphh's wait and fade.
    //   A Sim set through an elevator in the last kElevSkipRepeatMin sim-minutes keeps the game's wait.
    // elevtriplog: one line per car launch (riders, the wait in the car) and per Sim set through; either switch state.
    //
    // Horses, pets, Sims carrying a child, toddlers and rides for the elevator's own sake (its interactions, WooHoo) keep
    // the game's code. Counters 4700-4710 and 4721 (household, unseen). Check: ElevSkipCheck.
    public static partial class Fixes
    {
        // The camera half of FarFromViewers, for household opt-ins (elevskiphhunseen, fridgeffunseen): FarFromViewers also
        // counts every active-household Sim within reach as a viewer, so for a household Sim at home (its own viewer at
        // distance 0) it is never true. This asks only whether the player can see the spot: away from the camera's target
        // and eye (callers also test UcOnScreen).
        static bool FarFromCamera(Sims3.SimIFace.Vector3 p)
        {
            long now = Sims3.Gameplay.Utilities.SimClock.CurrentTicks;
            if (now != sViewTick) RefreshViewers(now);
            float dx = p.x - sCamTarget.x, dy = p.y - sCamTarget.y, dz = p.z - sCamTarget.z;
            if (dx * dx + dy * dy + dz * dz < sReach2) return false;
            dx = p.x - sCamPos.x; dy = p.y - sCamPos.y; dz = p.z - sCamPos.z;
            return dx * dx + dy * dy + dz * dz >= sReach2;
        }

        const int kEskNpc = 4700, kEskHh = 4701, kEskWould = 4702, kEskSeen = 4703, kEskExempt = 4704, kEskRepeat = 4705,
            kEskNoExit = 4706, kEskErrors = 4707, kEskLaunches = 4708, kEskRiders = 4709, kEskSolo = 4710,
            kEskHhUnseen = 4721;

        sealed class EskArm { public ElevatorDoors Exit; public Vector3 Pos, Fwd; public bool Fade; public long At; }
        sealed class EskLaunch { public long At; }
        static readonly Dictionary<ulong, EskArm> sEskArmed = new Dictionary<ulong, EskArm>();     // per Sim: set through at the next skip call
        static readonly Dictionary<ulong, long> sEskLastSkip = new Dictionary<ulong, long>();      // per Sim: when it was last set through
        static readonly Dictionary<ulong, long> sEskNoExitAt = new Dictionary<ulong, long>();      // per Sim: no ride found (asked again after a minute)
        static readonly Dictionary<ulong, EskLaunch> sEskLaunchOf = new Dictionary<ulong, EskLaunch>();  // per car: its last launch
        static readonly LineCapCore sEskCap = new LineCapCore(30, 60000, 600);
        static string sEskMissing = "", sEskLast = "", sEskLastError = "";

        static void ApplyElevSkip()
        {
            sEskMissing = "";
            if (!Patch(typeof(ElevatorDoors.ElevatorDoorsPortalComponent), "TryTeleportSkip", new Type[] { typeof(Sim) }, typeof(EskSkipHook), "SkipHook", "SkipOrig"))
                sEskMissing = "ElevatorDoorsPortalComponent.TryTeleportSkip";
            if (!Patch(typeof(ElevatorInterior.ElevatorPortalComponent), "OnPortalStart", new Type[] { typeof(Sim) }, typeof(EskRideHook), "StartHook", "StartOrig"))
                sEskMissing = (sEskMissing + " ElevatorPortalComponent.OnPortalStart").Trim();
            if (sEskMissing.Length > 0) AppliedLog().Add("elevskip: not found: " + sEskMissing);
        }

        static void EskError(Exception e)
        {
            PatchCounters()[kEskErrors]++;
            sEskLastError = "elevskip: error: " + e.GetType().Name + ": " + e.Message;
            DiagLine(sEskLastError);
        }

        static void EskLine(string text)
        {
            sEskLast = "elevskip: " + text;
            if (ShortcutOn("elevtriplog") && sEskCap.Take(System.Environment.TickCount)) DiagLine(sEskLast);
        }

        // The floor from the position (a car is a contained object; its own Level property is not its floor).
        static int EskLevel(GameObject o)
        {
            LotLocation loc = LotLocation.Invalid;
            World.GetLotLocation(o.Position, ref loc);
            return loc.mLevel;
        }

        static bool EskRecent(Dictionary<ulong, long> d, ulong id, float minutes)
        {
            long t;
            return d.TryGetValue(id, out t) && OjsMinutes(t) < minutes;
        }

        // The doors the Sim's route leaves this column by: the portal paths after `doors` (ElevSkipCore.ExitDoors).
        static ElevatorDoors EskExitDoors(Sim a, ElevatorDoors doors)
        {
            Route r = a.RoutingComponent == null ? null : a.RoutingComponent.GetCurrentRoute();
            if (r == null) return null;
            uint n = r.GetNumPaths();
            uint start = uint.MaxValue;
            for (uint i = 0; i < n; i++)
            {
                PathData pd = r.GetPathData(i);
                if (pd.PathType == PathType.PortalPath && pd.ObjectId == doors.ObjectId) { start = i; break; }
            }
            if (start == uint.MaxValue) return null;
            // the portal paths after the Sim's doors (walks inside the car between hops do not break the ride)
            int[] portals = new int[32];
            ElevatorDoors[] pdoor = new ElevatorDoors[32];
            int p = 0;
            for (uint i = start + 1; i < n && p < 32; i++)
            {
                PathData pd = r.GetPathData(i);
                if (pd.PathType != PathType.PortalPath) continue;
                GameObject o = GlobalFunctions.ConvertGuidToObject<GameObject>(pd.ObjectId);
                pdoor[p] = o as ElevatorDoors;
                portals[p] = o is ElevatorInterior ? ElevSkipCore.kStepCar : pdoor[p] != null ? ElevSkipCore.kStepDoors : ElevSkipCore.kStepOther;
                p++;
            }
            int x = ElevSkipCore.ExitDoors(portals, p);
            if (x < 0) return null;
            ElevatorDoors exit = pdoor[x];
            if (exit == null || exit == doors || exit.LotCurrent != doors.LotCurrent || EskLevel(exit) == EskLevel(doors)) return null;
            return exit;
        }

        // The line hook's elevator step (CrowdPort.cs CpLineHook, before crowdport's). True = the line answers "timed out" now
        // and the game's own TryTeleportSkip (our hook) sets the Sim through.
        static bool EskLineAsk(InteractionInstance interaction, Sim a, ElevatorDoors doors)
        {
            if (sEskMissing.Length > 0) return false;
            int[] c = PatchCounters();
            try
            {
                ElevatorInterior car = doors.InteriorObj;
                ElevatorInterior.ElevatorPortalComponent ep = car == null ? null : car.ElevatorPortal;
                if (ep == null || a.SimDescription == null || ep.IsSimInElevator(a)) return false;
                ulong id = a.ObjectId.Value;
                bool plain = a.IsHuman && !a.IsInRidingPosture && a.CarryingChildPosture == null && a.SimDescription.ChildOrAbove
                    && a.ExitReason == ExitReason.None && !(interaction.Target is ElevatorDoors) && !(a.CurrentInteraction is ElevatorDoors.WooHoo);
                if (!plain) return false;
                bool on = ShortcutOn("elevskip"), hhOn = ShortcutOn("elevskiphh"), hhUnseenOn = hhOn && ShortcutOn("elevskiphhunseen");
                CpElev e;
                float waited = CpElevWaited(a, doors, out e);
                if (!on)
                {
                    if (!e.EskCounted) { c[kEskWould]++; e.EskCounted = true; }  // off: one count per wait, no route read
                    return false;
                }
                if (EskRecent(sEskNoExitAt, id, 1f)) return false;
                bool npc = !IsPlayerSim(a);
                bool recent = EskRecent(sEskLastSkip, id, Instantiator.kElevSkipRepeatMin);
                if (!npc && !hhOn)
                {
                    if (!e.EskCounted) { c[kEskExempt]++; e.EskCounted = true; }
                    return false;
                }
                ElevatorDoors exit = EskExitDoors(a, doors);
                if (exit == null)
                {
                    if (sEskNoExitAt.Count > 256) sEskNoExitAt.Clear();
                    sEskNoExitAt[id] = SimClock.CurrentTicks;
                    return false;
                }
                Vector3 want = exit.GetPositionOfSlot(Slot.RoutingSlot_0);
                // Household rule (CLAUDE.md): household Sims never count as unseen. elevskiphhunseen (default off) is the player's
                // explicit opt-in that lifts it for elevator skips only: a household Sim other than the selected Sim, off screen
                // itself, with the far doors off screen and away from viewers, then goes at once with no fade. TownieUnseen
                // rejects household Sims by design, so its off-screen test is done here directly.
                bool selected = a == PlumbBob.SelectedActor;
                // camsafe (CamSafe.cs, docs/690): the same test through the shared CsDecide (camera-safe reach, holds, jump pause)
                bool unseen = sCsOn ? CsDecide(a, hhUnseenOn, exit, want)
                    : npc ? TownieUnseen(a, null) && !UcOnScreen(exit) && FarFromViewers(want)
                    : hhUnseenOn && !selected && !UcOnScreen(a) && FarFromCamera(a.Position) && !UcOnScreen(exit) && FarFromCamera(want);
                int d = ElevSkipCore.Decide(on, hhOn, hhUnseenOn, true, npc, selected, unseen, waited, Instantiator.kElevSkipHhWait, recent);
                if (d != ElevSkipCore.kGo)
                {
                    if (!e.EskCounted)
                    {
                        if (d == ElevSkipCore.kSeen) { c[kEskSeen]++; e.EskCounted = true; }
                        else if (d == ElevSkipCore.kExempt) { c[kEskExempt]++; e.EskCounted = true; }
                        else if (d == ElevSkipCore.kRepeat) { c[kEskRepeat]++; e.EskCounted = true; }
                    }
                    return false;
                }
                // EA's exit spot: FindGoodLocation near the far doors' outer slot, routable, empty tiles first, that room
                World.FindGoodLocationParams fgl = new World.FindGoodLocationParams(want);
                fgl.BooleanConstraints |= FindGoodLocationBooleans.Routable | FindGoodLocationBooleans.PreferEmptyTiles | FindGoodLocationBooleans.StayInRoom;
                Vector3 pos, fwd;
                if (!GlobalFunctions.FindGoodLocation(a, fgl, out pos, out fwd))
                {
                    c[kEskNoExit]++;
                    sEskNoExitAt[id] = SimClock.CurrentTicks;
                    return false;
                }
                if (fwd == Vector3.Invalid || fwd.LengthSqr() < 1e-4f) fwd = exit.GetForwardOfSlot(Slot.RoutingSlot_0) * -1f;
                EskArm arm = new EskArm();
                arm.Exit = exit; arm.Pos = pos; arm.Fwd = fwd; arm.Fade = ElevSkipCore.Fade(hhOn, hhUnseenOn, npc, selected, unseen); arm.At = SimClock.CurrentTicks;
                if (sEskArmed.Count > 64) sEskArmed.Clear();
                sEskArmed[id] = arm;
                a.AddExitReason(ExitReason.TimedOut);
                return true;
            }
            catch (ResetException) { throw; }
            catch (Exception ex) { EskError(ex); }
            return false;
        }

        // `this` is the game's doors portal component. EA's WaitInLine calls it after a timed-out line.
        public class EskSkipHook : ElevatorDoors.ElevatorDoorsPortalComponent
        {
            public bool SkipHook(Sim s)
            {
                EskArm arm = null;
                try
                {
                    if (s != null && sEskArmed.TryGetValue(s.ObjectId.Value, out arm)) sEskArmed.Remove(s.ObjectId.Value);
                    if (arm != null && OjsMinutes(arm.At) > 0.5f) arm = null;  // a stale arm (the line returned some other way)
                }
                catch (ResetException) { throw; }
                catch (Exception e) { EskError(e); arm = null; }
                if (arm == null) return SkipOrig(s);
                ElevatorDoors doors = Owner as ElevatorDoors;
                int[] c = PatchCounters();
                try
                {
                    float waited = 0f;
                    CpElev e;
                    if (doors != null && sCpElev.TryGetValue(s.ObjectId.Value, out e)) waited = OjsMinutes(e.First);
                    sCpElev.Remove(s.ObjectId.Value);
                    // camsafe: armed unseen, but the Sim or its exit spot came into view since: set through behind a fade (the
                    // end state elevskiphh uses for Sims in view) instead of a visible jump
                    if (!arm.Fade && CsSnap(s, arm.Pos)) arm.Fade = true;
                    if (arm.Fade) s.FadeOut(true, false);
                    s.SetPosition(arm.Pos);
                    s.SetForward(arm.Fwd);
                    if (arm.Fade) s.FadeIn();
                    if (sEskLastSkip.Count > 256) sEskLastSkip.Clear();
                    sEskLastSkip[s.ObjectId.Value] = SimClock.CurrentTicks;
                    bool npc = !IsPlayerSim(s);
                    c[arm.Fade ? kEskHh : npc ? kEskNpc : kEskHhUnseen]++;
                    EskLine(s.FullName + " " + ShlWho(s) + " goes through the elevator from level " + (doors == null ? -1 : EskLevel(doors)) + " to level " + EskLevel(arm.Exit)
                        + " after " + waited.ToString("0.0") + " sim-min (" + (arm.Fade ? (npc ? "fade" : "fade, household") : (npc ? "unseen" : "household, unseen")) + "), car "
                        + (doors != null && doors.InteriorObj != null && doors.InteriorObj.InUse ? "in use" : "free") + ", doors " + (doors != null && doors.mIsOpen ? "open" : "closed") + ", at " + V(arm.Pos));
                    return true;
                }
                catch (ResetException) { throw; }
                catch (Exception e) { EskError(e); }
                return SkipOrig(s);
            }
            public bool SkipOrig(Sim s) { return false; }  // receives TryTeleportSkip's original body
        }

        // `this` is the game's car portal component (one per floor). Its OnPortalStart holds the rider until the launch.
        public class EskRideHook : ElevatorInterior.ElevatorPortalComponent
        {
            public bool StartHook(Sim s)
            {
                long t0 = SimClock.CurrentTicks;
                bool r = StartOrig(s);
                try
                {
                    float inCar = OjsMinutes(t0);
                    if (r && s != null && inCar >= 0.5f)  // a launch from a boarding floor (the hops between floors take ticks)
                    {
                        ulong car = Owner == null ? 0 : Owner.ObjectId.Value;
                        EskLaunch l;
                        bool same = sEskLaunchOf.TryGetValue(car, out l) && ElevSkipCore.SameLaunch(true, OjsMinutes(l.At), 0.2f);
                        if (!same)
                        {
                            if (sEskLaunchOf.Count > 128) sEskLaunchOf.Clear();
                            l = new EskLaunch(); l.At = SimClock.CurrentTicks; sEskLaunchOf[car] = l;
                            int n = mAssignedSims.Count;
                            int[] c = PatchCounters();
                            c[kEskLaunches]++;
                            c[kEskRiders] += n;
                            if (n <= 1) c[kEskSolo]++;
                            EskLine("car launch: level " + (Owner == null ? -1 : EskLevel(Owner)) + ", " + n + " rider" + (n == 1 ? "" : "s") + " (" + s.FullName + " " + ShlWho(s)
                                + " waited " + inCar.ToString("0.0") + " sim-min in the car)" + (IsElevatorGoingUp() ? ", up" : ", down"));
                        }
                    }
                }
                catch (ResetException) { throw; }
                catch (Exception e) { EskError(e); }
                return r;
            }
            public bool StartOrig(Sim s) { return false; }  // receives OnPortalStart's original body
        }

        static string EskCounters()
        {
            int[] c = PatchCounters();
            return "elevskip: set through " + c[kEskNpc] + " unseen NPCs, " + c[kEskHhUnseen] + " household, unseen, " + c[kEskHh] + " with fade; would, off " + c[kEskWould] + ", NPC in view kept " + c[kEskSeen]
                + ", household exempt " + c[kEskExempt] + ", repeat kept " + c[kEskRepeat] + ", no exit spot " + c[kEskNoExit] + ", errors " + c[kEskErrors]
                + "; car launches " + c[kEskLaunches] + " (riders " + c[kEskRiders] + ", one rider " + c[kEskSolo] + ")"
                + (sEskMissing.Length > 0 ? "; not found: " + sEskMissing : "") + (sEskLast.Length > 0 ? "; last: " + sEskLast : "") + (sEskLastError.Length > 0 ? "; " + sEskLastError : "");
        }

        // ElevSkipCheck result: counters and the last line. run: the rules with controls (read-only).
        public static object ElevSkipCheck(string what)
        {
            try
            {
                if (what == "run")
                {
                    bool fix = ElevSkipCore.Decide(true, false, true, true, true, 0f, 1f, false) == ElevSkipCore.kGo;
                    bool off = ElevSkipCore.Decide(false, true, true, true, true, 9f, 1f, false) == ElevSkipCore.kWould;
                    bool ctl = ElevSkipCore.Decide(true, false, true, false, false, 9f, 1f, false) == ElevSkipCore.kExempt
                        && ElevSkipCore.Decide(true, false, true, true, false, 9f, 1f, false) == ElevSkipCore.kSeen
                        && ElevSkipCore.Decide(true, true, true, false, false, 0.5f, 1f, false) == ElevSkipCore.kWait
                        && ElevSkipCore.Decide(true, true, true, false, false, 1f, 1f, false) == ElevSkipCore.kGo
                        && ElevSkipCore.Decide(true, true, true, true, true, 0f, 1f, true) == ElevSkipCore.kRepeat
                        && ElevSkipCore.Decide(true, true, false, true, true, 9f, 1f, false) == ElevSkipCore.kNone
                        && ElevSkipCore.Decide(true, true, true, true, false, false, true, 0f, 1f, false) == ElevSkipCore.kGo
                        && !ElevSkipCore.Fade(true, true, false, false, true)
                        && ElevSkipCore.Decide(true, true, false, true, false, false, true, 0f, 1f, false) == ElevSkipCore.kWait
                        && ElevSkipCore.Decide(true, true, true, true, false, true, true, 0f, 1f, false) == ElevSkipCore.kWait
                        && ElevSkipCore.Fade(true, true, false, true, true)
                        && ElevSkipCore.ExitDoors(new int[] { 1, 1, 2, 0 }, 4) == 2 && ElevSkipCore.ExitDoors(new int[] { 2 }, 1) == -1;
                    bool on = ShortcutOn("elevskip");
                    return "ElevSkipCheck run: verdict " + (on ? (fix ? "FIXED" : "BROKEN") : (off ? "BROKEN" : "?")) + ", controls ok=" + ctl + "; " + EskCounters();
                }
                return EskCounters();
            }
            catch (Exception e) { return "ElevSkipCheck: " + e.GetType().Name + ": " + e.Message; }
        }
    }
}
