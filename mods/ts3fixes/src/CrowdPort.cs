using System;
using System.Collections.Generic;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects;
using Sims3.Gameplay.Objects.Elevator;
using Sims3.Gameplay.Objects.RabbitHoles;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Crowded transport portals are skipped (docs/686 §19). A player's wish: at elevators, rabbit holes and stations a
    // Sim may be teleported when too many Sims are going in. Default off; rules in CrowdPortCore.cs.
    //
    // crowdport (Sims the player does not control) / crowdporthh (household and selected Sims too) / crowdportlog
    // A crowd = kCrowdPortMin Sims (3) waiting for or using the portal, this one included; or this Sim has waited
    // kCrowdPortWait sim-minutes (1) behind another Sim. A single Sim with nobody in its way walks as in the game.
    //
    // elevator [verified, decompiled PortalComponent.AcquireLaneRouteAction.WaitInLine]
    //   A Sim at the doors asks the doors' line (12 long) for its turn, 15 sim-minutes; a refusal with "timed out" makes
    //   the caller run ElevatorDoorsPortalComponent.TryTeleportSkip (doors open, car free, same direction: the Sim fades
    //   and is set into the car), as does a car with two riders. Fix, in the line's hook (LdsQueueHook.WaitHook):
    //     - crowd or long wait, and the skip's own conditions hold: the wait is answered "timed out" at once, so the
    //       game's caller runs its own skip;
    //     - else the Sim stands in the line with a short timeout, after which the game tries its skip itself.
    //
    // rabbit hole, going in [verified, decompiled RabbitHole.AnimateEnterRabbitHole / WaitForEnterSlot]
    //   One Sim holds an enter slot until its walk ends; the others wait up to 15 sim-minutes per holder. Fix, in
    //   objshare's WaitForEnterSlot hook when rhshare does not already walk the Sim there: the Sim waits in our loop
    //   (kCrowdPortWait at most; a cancel, a free slot or a hand-over ends it), then fades, is set on the enter slot and
    //   fades in, as the game's elevator skip does. It takes no reservation; the game's own route (now zero long), door,
    //   walk-in and bookkeeping follow unchanged. A holder out of scope keeps the game's wait.
    //
    // rabbit hole, coming out [verified, decompiled RabbitHole.AnimateExitRabbitHole]
    //   An NPC sleeps until kNpcExitFreq (15 sim-minutes) has passed since the last Sim left: one NPC per 15 minutes.
    //   Fix (swap of AnimateExitRabbitHole): the wait is ours, the game's with one more exit: with kCrowdPortMin NPCs
    //   waiting at one rabbit hole all of them go now. The game's exit slots (one Sim per slot) pace the door.
    //
    // station (Subway, HoverTrainStation) [verified, decompiled]: its portal lanes are shared by Sims going the same way
    //   and have no line, so nothing is skipped going in; riders arriving wait in the rabbit hole exit pacing above and
    //   are counted as "station". Tube elevators and firefighter poles (line, then an animation with the object as an
    //   actor) keep the game's line; a crowd there is counted as "other".
    //
    // Horses, riders, Sims carrying a child and toddlers keep the game's code everywhere. Household Sims are in scope
    // only with crowdporthh (the experimental HouseholdToo flag does not lift this: these are teleports).
    // Counters 4550-4565. Check: CrowdPortCheck.
    public static partial class Fixes
    {
        const int kCpElevator = 4550, kCpRhIn = 4551, kCpRhOut = 4552, kCpStation = 4553, kCpOther = 4554, kCpKept = 4555, kCpErrors = 4556,
            kCpWould = 4557, kCpExempt = 4558, kCpNotPlain = 4559, kCpElevShort = 4560, kCpElevNoSkip = 4561, kCpRhHolderKept = 4562,
            kCpRhEnded = 4563, kCpOutWaits = 4564, kCpOutPaced = 4565;

        sealed class CpElev { public ulong Doors; public long First, Last; public bool Counted; }
        sealed class CpExit { public int Pending; public long Burst; }

        static readonly Dictionary<ulong, CpElev> sCpElev = new Dictionary<ulong, CpElev>();  // a Sim's wait at an elevator, by Sim
        static readonly Dictionary<ulong, CpExit> sCpExit = new Dictionary<ulong, CpExit>();  // NPCs in the exit wait, by rabbit hole
        static readonly Dictionary<ulong, long> sCpSet = new Dictionary<ulong, long>();  // ticks at which a Sim was last set on an enter slot, by Sim
        static readonly LineCapCore sCpCap = new LineCapCore(30, 60000, 600);
        static readonly string[] kCpSites = { "elevator line", "rabbit hole in", "rabbit hole teleport", "rabbit hole out", "other line" };
        static readonly int[] sCpSiteErrors = new int[5];
        static string sCpMissing = "", sCpLast = "", sCpLastError = "";

        static void ApplyCrowdPort()
        {
            sCpMissing = "";
            if (!Patch(typeof(RabbitHole), "AnimateExitRabbitHole", new Type[] { typeof(Sim), typeof(Slot), typeof(Sim), typeof(RabbitHole.SimRabbitHoleState), typeof(bool) },
                typeof(CpExitHook), "ExitHook", "ExitOrig")) sCpMissing = "RabbitHole.AnimateExitRabbitHole";
            if (sCpMissing.Length > 0) AppliedLog().Add("crowdport: not found: " + sCpMissing);
        }

        static void CpError(int site, Exception e)
        {
            PatchCounters()[kCpErrors]++;
            sCpLastError = "crowdport: error at " + kCpSites[site] + ": " + e.GetType().Name + ": " + e.Message;
            if (sCpSiteErrors[site]++ < 3) DiagLine(sCpLastError);
        }

        static void CpLine(string text)
        {
            sCpLast = "crowdport: " + text;
            if (ShortcutOn("crowdportlog") && sCpCap.Take(System.Environment.TickCount)) DiagLine(sCpLast);
        }

        // Teleports: the household exemption is the plain player-Sim test, not the HouseholdToo-aware one.
        static bool CpScope(Sim s) { return ShortcutOn("crowdporthh") || !IsPlayerSim(s); }

        static bool CpStation(RabbitHole rh) { return rh is Subway || rh is HoverTrainStation; }

        static int CpMin() { return CrowdPortCore.Min(Instantiator.kCrowdPortMin); }

        // ---- elevator ---------------------------------------------------------------------------------------------------

        static float CpElevWaited(Sim a, ElevatorDoors doors, out CpElev e)
        {
            long now = SimClock.CurrentTicks;
            ulong id = a.ObjectId.Value, d = doors.ObjectId.Value;
            if (!sCpElev.TryGetValue(id, out e) || !CrowdPortCore.SameEpisode(e.Doors == d, OjsMinutes(e.Last)))
            {
                if (sCpElev.Count > 256) sCpElev.Clear();  // entries of Sims that left; bounded
                e = new CpElev();
                e.Doors = d; e.First = now;
                sCpElev[id] = e;
            }
            e.Last = now;
            return OjsMinutes(e.First);
        }

        // SimQueue.WaitForTurn on an elevator's line (LdsQueueHook.WaitHook), before the game's body. True = answered
        // "timed out" now: the game's caller (WaitInLine) then runs its teleport skip. Else the timeout may be cut.
        static bool CpElevatorLine(Sim a, ElevatorDoors doors, SimQueue line, ref float timeoutInMinutes)
        {
            try
            {
                ElevatorInterior car = doors.InteriorObj;
                ElevatorInterior.ElevatorPortalComponent ep = car == null ? null : car.ElevatorPortal;
                if (ep == null || a.SimDescription == null || ep.IsSimInElevator(a)) return false;
                int[] c = PatchCounters();
                bool plain = a.IsHuman && !a.IsInRidingPosture && a.CarryingChildPosture == null && a.SimDescription.ChildOrAbove;
                int there = line.MemberCount() + doors.UseCount + (doors.IsActorUsingMe(a) ? 0 : 1);
                CpElev e;
                float waited = CpElevWaited(a, doors, out e), wait = Instantiator.kCrowdPortWait;
                bool canSkip = doors.mIsOpen && !car.InUse && a.ExitReason == ExitReason.None
                    && (ep.IsElevatorEmpty() || ep.IsSimGoingUp(a) == ep.IsElevatorGoingUp());
                int d = CrowdPortCore.Elevator(ShortcutOn("crowdport"), CpScope(a), plain, there, CpMin(), waited, wait, canSkip);
                if (d == CrowdPortCore.kGo)
                {
                    c[kCpElevator]++;
                    sCpElev.Remove(a.ObjectId.Value);
                    CpLine("elevator: " + a.FullName + " " + ShlWho(a) + " skips the line of " + there + " Sims after " + waited.ToString("0.0") + " sim-min (the game's teleport skip), at " + V(a.Position));
                    a.AddExitReason(ExitReason.TimedOut);
                    return true;
                }
                if (d == CrowdPortCore.kWait || d == CrowdPortCore.kKeep)
                {
                    float t = CrowdPortCore.ElevatorTimeout(timeoutInMinutes, d, waited, wait);
                    if (t < timeoutInMinutes) { timeoutInMinutes = t; c[kCpElevShort]++; }
                }
                if (e.Counted) return false;  // the game asks every quarter minute: one count per wait
                if (d == CrowdPortCore.kWould) { c[kCpWould]++; e.Counted = true; }
                else if (d == CrowdPortCore.kExempt) { c[kCpExempt]++; e.Counted = true; }
                else if (d == CrowdPortCore.kWait)
                {
                    c[kCpElevNoSkip]++; e.Counted = true;
                    CpLine("elevator: " + a.FullName + " " + ShlWho(a) + " stays in the line of " + there + " Sims (doors " + (doors.mIsOpen ? "open" : "closed") + ", car " + (car.InUse ? "in use" : "free") + "), at " + V(a.Position));
                }
                else if (d == CrowdPortCore.kNone && !plain && CrowdPortCore.Crowd(there, CpMin())) { c[kCpNotPlain]++; e.Counted = true; }
            }
            catch (ResetException) { throw; }
            catch (Exception ex) { CpError(0, ex); }
            return false;
        }

        // A crowd in the line of a tube elevator or a firefighter pole: counted, the game's line kept.
        static void CpOtherLine(Sim a, IGameObject target, SimQueue line)
        {
            try
            {
                int there = line.MemberCount() + 1;
                if (!CrowdPortCore.Crowd(there, CpMin())) return;
                PatchCounters()[kCpOther]++;
                CpLine("other: " + a.FullName + " " + ShlWho(a) + " joins a line of " + there + " Sims at " + target.GetType().Name + " (the game's line kept), at " + V(a.Position));
            }
            catch (ResetException) { throw; }
            catch (Exception ex) { CpError(4, ex); }
        }

        // LdsQueueHook.WaitHook's step. True = WaitForTurn answers false now.
        // The elevator step only for WaitInLine's call (its wait behaviour); WooHoo asks the same line and is left alone.
        static bool CpLineHook(InteractionInstance interaction, IGameObject target, SimQueue line, SimQueue.WaitBehavior behavior, ref float timeoutInMinutes)
        {
            Sim a = interaction.InstanceActor;
            if (a == null || target == null) return false;
            ElevatorDoors doors = target as ElevatorDoors;
            if (doors != null)
                return behavior == (SimQueue.WaitBehavior.DontPlayRouteFail | SimQueue.WaitBehavior.OrderByProximity) && object.ReferenceEquals(doors.Line, line)
                    && CpElevatorLine(a, doors, line, ref timeoutInMinutes);
            if (target is TubeElevator || target is FirefighterPole) CpOtherLine(a, target, line);
            return false;
        }

        // ---- rabbit hole, going in --------------------------------------------------------------------------------------

        static int CpRhDecide(RabbitHole rh, Slot slot, Sim s, float waited, bool count, out int there)
        {
            ObjectGuid holder;
            bool held = OjsRhHeld(rh, slot, s, out holder);
            List<ObjectGuid> list;
            there = 2;  // the holder and this Sim
            if (rh.WaitingToEnterSims.TryGetValue(slot, out list) && list != null)
                foreach (ObjectGuid g in list) if (g != s.ObjectId && g != holder) there++;
            Sim h = holder == ObjectGuid.InvalidObjectGuid ? null : GameObject.GetObject(holder) as Sim;
            bool known = h != null && !h.HasBeenDestroyed, plain = OjsRhPlain(s);
            Vector3 at = rh.GetPositionOfSlot(slot);
            long set;
            bool near = (at - s.Position).Length() <= CrowdPortCore.kRhMaxDistance
                && CrowdPortCore.RhClear(known ? (at - h.Position).Length() : 0f, sCpSet.TryGetValue(s.ObjectId.Value, out set) ? OjsMinutes(set) : -1f);
            int d = CrowdPortCore.RhIn(ShortcutOn("crowdport"), held, CpScope(s), plain, known, known && CpScope(h) && OjsRhPlain(h), near, there, CpMin(), waited, Instantiator.kCrowdPortWait);
            if (!count) return d;
            int[] c = PatchCounters();
            if (d == CrowdPortCore.kWould) c[kCpWould]++;
            else if (d == CrowdPortCore.kExempt) c[kCpExempt]++;
            else if (d == CrowdPortCore.kKeep) c[kCpRhHolderKept]++;
            else if (d == CrowdPortCore.kNone && held && !plain && CrowdPortCore.Crowd(there, CpMin())) c[kCpNotPlain]++;
            return d;
        }

        // The game's elevator skip, at a rabbit hole: fade, set on the slot, fade in. False = the Sim stays where it is.
        static bool CpRhTeleport(RabbitHole rh, Slot slot, Sim s, int there, float waited)
        {
            Vector3 from = s.Position;
            try
            {
                Vector3 pos = rh.GetPositionOfSlot(slot), fwd = rh.GetForwardOfSlot(slot);
                s.FadeOut(true, false);
                s.SetForward(fwd);
                s.SetPosition(pos);
                s.FadeIn();
            }
            catch (ResetException) { throw; }
            catch (Exception ex)
            {
                CpError(2, ex);
                try { s.FadeIn(); } catch (ResetException) { throw; } catch (Exception) { }
                return false;
            }
            if (sCpSet.Count > 256) sCpSet.Clear();  // bounded; an entry only matters for kRhAgain sim-minutes
            sCpSet[s.ObjectId.Value] = SimClock.CurrentTicks;
            bool station = CpStation(rh);
            PatchCounters()[station ? kCpStation : kCpRhIn]++;
            CpLine((station ? "station in: " : "rabbit hole in: ") + s.FullName + " " + ShlWho(s) + " is set on enter slot " + slot + " of " + rh.GetType().Name + ", " + there + " Sims there, waited "
                + waited.ToString("0.0") + " sim-min, from " + V(from));
            return true;
        }

        // OjsRhHook.WaitHook's step for a held slot rhshare does not walk to. Answer: ObjShareCore.kShared (the Sim is on
        // the slot, no reservation), kCancelled (leave; the list entry stays for the caller's cleanup), kGotTurn (nothing
        // done: rhshare's wait or the game's follows). No catch round the sleeps: a reset must unwind the Sim's stack.
        static int CpRhIn(RabbitHole rh, Slot slot, Sim s, OjsRhWait w)
        {
            int d, there = 0;
            try
            {
                d = CpRhDecide(rh, slot, s, 0f, true, out there);
                ObjectGuid h;
                w.CpOpen = d == CrowdPortCore.kNone && !ShortcutOn("crowdport") && OjsRhPlain(s) && OjsRhHeld(rh, slot, s, out h);
            }
            catch (ResetException) { throw; }
            catch (Exception ex) { CpError(1, ex); return ObjShareCore.kGotTurn; }
            if (d != CrowdPortCore.kGo && d != CrowdPortCore.kWait) return ObjShareCore.kGotTurn;
            if (d == CrowdPortCore.kWait)
            {
                List<ObjectGuid> list = OjsRhList(rh, slot);
                if (!list.Contains(s.ObjectId)) list.Add(s.ObjectId);  // in the list while it stands here: the game's hand-over can pick it
                while (true)
                {
                    s.LoopIdle();
                    Simulator.Sleep(10u);
                    list = OjsRhList(rh, slot);  // a rabbit hole reset clears the table
                    ObjectGuid holder;
                    if (!OjsRhHeld(rh, slot, s, out holder)) { list.Remove(s.ObjectId); PatchCounters()[kCpRhEnded]++; return ObjShareCore.kGotTurn; }
                    if (s.HasExitReason(ExitReason.Default))
                    {
                        if (!list.Contains(s.ObjectId)) list.Add(s.ObjectId);
                        PatchCounters()[kCpRhEnded]++;
                        return ObjShareCore.kCancelled;
                    }
                    d = CpRhDecide(rh, slot, s, OjsMinutes(w.Start), false, out there);
                    if (d == CrowdPortCore.kGo) break;
                    if (d != CrowdPortCore.kWait) { list.Remove(s.ObjectId); PatchCounters()[kCpKept]++; return ObjShareCore.kGotTurn; }  // switched off, holder changed
                }
                list.Remove(s.ObjectId);
            }
            OjsRhList(rh, slot);  // the caller's cleanup reads the slot's list
            if (!CpRhTeleport(rh, slot, s, there, OjsMinutes(w.Start))) return ObjShareCore.kGotTurn;
            return ObjShareCore.kShared;
        }

        // After the game's wait with the switch off: the wait behind one Sim that lasted kCrowdPortWait counts as "would".
        static void CpRhLate(OjsRhWait w)
        {
            if (w.CpOpen && CrowdPortCore.WouldLate(ShortcutOn("crowdport"), true, OjsMinutes(w.Start), Instantiator.kCrowdPortWait)) PatchCounters()[kCpWould]++;
        }

        // ---- rabbit hole, coming out ------------------------------------------------------------------------------------

        // Our exit wait for an NPC in scope: the game's (sleep 30 ticks until kNpcExitFreq has passed since the last Sim
        // left), ended early for every Sim waiting when kCrowdPortMin of them wait. No catch: a reset must unwind.
        static void CpExitWait(RabbitHole piece, RabbitHole rh, Sim a)
        {
            ulong id = rh.ObjectId.Value;
            CpExit x;
            if (!sCpExit.TryGetValue(id, out x)) { x = new CpExit(); sCpExit[id] = x; }
            long start = SimClock.CurrentTicks;
            bool burst = false;
            x.Pending++;
            try
            {
                while (SimClock.ElapsedTime(TimeUnit.Minutes, rh.TimeLastSimLeft) < piece.RabbitHoleTuning.kNpcExitFreq)
                {
                    if (ShortcutOn("crowdport") && CrowdPortCore.Burst(x.Pending, CpMin())) x.Burst = SimClock.CurrentTicks;
                    if (CrowdPortCore.Released(x.Burst, start)) { burst = true; break; }
                    Simulator.Sleep(30u);
                }
            }
            finally
            {
                x.Pending--;
                if (x.Pending <= 0) sCpExit.Remove(id);
            }
            if (!burst) { PatchCounters()[kCpOutPaced]++; return; }
            bool station = CpStation(rh);
            PatchCounters()[station ? kCpStation : kCpRhOut]++;
            CpLine((station ? "station out: " : "rabbit hole out: ") + a.FullName + " " + ShlWho(a) + " leaves " + rh.GetType().Name + " with the crowd after " + OjsMinutes(start).ToString("0.0")
                + " sim-min, " + x.Pending + " more waiting");
        }

        // `this` is the game's rabbit hole (a piece of a combo building has its own tuning; the clock is its proxy's).
        public abstract class CpExitHook : RabbitHole
        {
            public Slot ExitHook(Sim a, Slot slotToUse, Sim horse, SimRabbitHoleState state, bool skipWait)
            {
                // off: the game's body alone, nothing asked
                if (skipWait || sCpMissing.Length > 0 || !ShortcutOn("crowdport")) return ExitOrig(a, slotToUse, horse, state, skipWait);
                int d = CrowdPortCore.kNone;
                RabbitHole rh = null;
                bool eaSkip = false;
                try
                {
                    rh = RabbitHoleProxy;
                    eaSkip = a == null || CanSimSkipExitWait(a);
                    if (!eaSkip)
                    {
                        int[] c = PatchCounters();
                        c[kCpOutWaits]++;
                        bool plain = horse == null && state == SimRabbitHoleState.Standing && a.IsHuman;
                        d = rh == null || RabbitHoleTuning == null ? CrowdPortCore.kNone : CrowdPortCore.RhOut(true, false, CpScope(a), plain);
                        if (d == CrowdPortCore.kExempt) c[kCpExempt]++;
                        else if (d == CrowdPortCore.kNone && !plain) c[kCpNotPlain]++;
                    }
                }
                catch (ResetException) { throw; }
                catch (Exception ex) { CpError(3, ex); d = CrowdPortCore.kNone; eaSkip = false; }
                if (eaSkip) return ExitOrig(a, slotToUse, horse, state, true);  // the game's answer, asked once
                if (d != CrowdPortCore.kGo) return ExitOrig(a, slotToUse, horse, state, false);
                CpExitWait(this, rh, a);
                return ExitOrig(a, slotToUse, horse, state, true);
            }
            public Slot ExitOrig(Sim a, Slot slotToUse, Sim horse, SimRabbitHoleState state, bool skipWait) { return Slot.None; }  // receives AnimateExitRabbitHole's original body
        }

        // ---- check ------------------------------------------------------------------------------------------------------

        static string CpCounters()
        {
            int[] c = PatchCounters();
            string sites = "";
            for (int i = 0; i < sCpSiteErrors.Length; i++)
                if (sCpSiteErrors[i] > 0) sites += (sites.Length > 0 ? ", " : "") + kCpSites[i] + " " + sCpSiteErrors[i];
            int pending = 0;
            foreach (CpExit x in sCpExit.Values) pending += x.Pending;
            return "crowdport: elevator skips " + c[kCpElevator] + ", rabbit hole in " + c[kCpRhIn] + ", rabbit hole out " + c[kCpRhOut] + ", station " + c[kCpStation]
                + ", other lines with a crowd (kept) " + c[kCpOther] + "; kept " + c[kCpKept] + ", would " + c[kCpWould] + ", exempt " + c[kCpExempt] + ", horse / rider / carrier / toddler " + c[kCpNotPlain]
                + "; elevator: timeouts cut " + c[kCpElevShort] + ", no skip now " + c[kCpElevNoSkip] + "; rabbit hole in: holder out of scope, at the door or far " + c[kCpRhHolderKept]
                + ", short wait ended by a free slot or a cancel " + c[kCpRhEnded] + "; rabbit hole out: NPC exit waits " + c[kCpOutWaits] + ", ended by the game's pacing " + c[kCpOutPaced]
                + ", waiting now " + pending + "; min " + CpMin() + ", wait " + CrowdPortCore.Wait(Instantiator.kCrowdPortWait).ToString("0.0")
                + "; errors " + c[kCpErrors] + (sites.Length > 0 ? " (" + sites + "; last: " + sCpLastError + ")" : "") + "; " + GlMembers(sCpMissing)
                + (sCpLast.Length > 0 ? "; last: " + sCpLast : "");
        }

        // CrowdPortCheck run: the rules with controls (read-only). Bug cases: a third Sim at an open elevator stands in
        // the line; a third Sim at a held enter slot waits; a third NPC leaving a rabbit hole waits for the pacing.
        // result: the counters and the last line. lot: elevators on the selected Sim's lot, rabbit holes with reserved
        // enter slots or NPCs in our exit wait.
        public static object CrowdPortCheck(string what)
        {
            if (what == "result") return CpCounters();
            if (what == "lot") return CpLot();
            if (what != "run") return "crowdport <run|result|lot>";
            bool on = ShortcutOn("crowdport");
            const int Go = CrowdPortCore.kGo, None = CrowdPortCore.kNone, Would = CrowdPortCore.kWould, Exempt = CrowdPortCore.kExempt, Keep = CrowdPortCore.kKeep, Wait = CrowdPortCore.kWait;
            bool elev = CrowdPortCore.Elevator(on, true, true, 3, 3, 0f, 1f, true) == Go;
            bool rhIn = CrowdPortCore.RhIn(on, true, true, true, true, true, true, 3, 3, 0f, 1f) == Go;
            bool rhOut = CrowdPortCore.RhOut(on, false, true, true) == Go && CrowdPortCore.Burst(3, 3) && CrowdPortCore.Released(100, 90);
            bool ctlElev = CrowdPortCore.Elevator(true, true, true, 1, 3, 5f, 1f, true) == Keep && CrowdPortCore.Elevator(true, true, true, 2, 3, 0.5f, 1f, true) == Keep
                && CrowdPortCore.Elevator(true, true, true, 2, 3, 1f, 1f, true) == Go && CrowdPortCore.Elevator(true, true, true, 3, 3, 0f, 1f, false) == Wait
                && CrowdPortCore.Elevator(true, false, true, 3, 3, 0f, 1f, true) == Exempt && CrowdPortCore.Elevator(true, true, false, 3, 3, 0f, 1f, true) == None
                && CrowdPortCore.Elevator(false, true, true, 3, 3, 0f, 1f, true) == Would && CrowdPortCore.Elevator(false, true, true, 1, 3, 0f, 1f, true) == None
                && CrowdPortCore.ElevatorTimeout(15f, Wait, 0f, 1f) == 1f && CrowdPortCore.ElevatorTimeout(15f, Keep, 0.4f, 1f) == 0.6f && CrowdPortCore.ElevatorTimeout(15f, None, 0f, 1f) == 15f;
            bool ctlIn = CrowdPortCore.RhIn(true, false, true, true, true, true, true, 3, 3, 0f, 1f) == None && CrowdPortCore.RhIn(true, true, true, true, true, true, true, 2, 3, 0f, 1f) == Wait
                && CrowdPortCore.RhIn(true, true, true, true, true, true, true, 2, 3, 1f, 1f) == Go && CrowdPortCore.RhIn(true, true, true, true, true, false, true, 3, 3, 0f, 1f) == Keep
                && CrowdPortCore.RhIn(true, true, true, true, true, true, false, 3, 3, 0f, 1f) == Keep && CrowdPortCore.RhIn(true, true, true, false, true, true, true, 3, 3, 0f, 1f) == None
                && CrowdPortCore.RhIn(true, true, false, true, true, true, true, 3, 3, 0f, 1f) == Exempt && CrowdPortCore.RhIn(true, true, true, true, false, false, true, 3, 3, 0f, 1f) == None
                && CrowdPortCore.RhIn(false, true, true, true, true, true, true, 3, 3, 0f, 1f) == Would;
            bool ctlOut = CrowdPortCore.RhOut(true, true, true, true) == None && CrowdPortCore.RhOut(true, false, false, true) == Exempt && CrowdPortCore.RhOut(true, false, true, false) == None
                && CrowdPortCore.RhOut(false, false, true, true) == Would && !CrowdPortCore.Burst(2, 3) && !CrowdPortCore.Released(100, 101) && !CrowdPortCore.Released(0, 0)
                && CrowdPortCore.Min(1) == 2 && CrowdPortCore.Wait(-1f) == 0f && CrowdPortCore.Wait(99f) == 15f;
            bool controls = ctlElev && ctlIn && ctlOut, fixedNow = elev && rhIn && rhOut;
            return "crowdport check (crowdport " + (on ? "on" : "off") + ", household " + (ShortcutOn("crowdporthh") ? "on" : "off") + ", log " + (ShortcutOn("crowdportlog") ? "on" : "off")
                + "): a third Sim at an open elevator takes the game's skip=" + elev + ", a third Sim at a held enter slot is set on it=" + rhIn + ", three NPCs waiting to leave go together=" + rhOut
                + "; control: elevator (single Sim, short wait, long wait, no skip now, scope, carrier, off, timeouts)=" + ctlElev
                + ", going in (free slot, two Sims, long wait, holder out of scope, far, carrier, scope, dead holder, off)=" + ctlIn
                + ", coming out (the game's own skip, scope, horse, off, two waiting, a later Sim, floors)=" + ctlOut
                + "; controls ok=" + controls + "; verdict: " + GlVerdict(on, fixedNow, controls, sCpMissing.Length == 0) + "; " + CpCounters();
        }

        static string CpLot()
        {
            Sim sel = PlumbBob.SelectedActor;
            System.Text.StringBuilder b = new System.Text.StringBuilder("crowdport lot:");
            int doorsN = 0;
            if (sel != null && sel.LotCurrent != null)
            {
                b.Append(" " + sel.FullName + ";");
                foreach (ElevatorDoors d in sel.LotCurrent.GetObjects<ElevatorDoors>())
                {
                    doorsN++;
                    int line = d.Line == null ? 0 : d.Line.MemberCount();
                    if ((line > 0 || d.UseCount > 0) && b.Length < 900)
                        b.Append(" elevator doors level " + d.Level + ": line " + line + ", in use by " + d.UseCount + ", " + (d.mIsOpen ? "open" : "closed") + ";");
                }
            }
            int holes = 0, reserved = 0, stations = 0;
            foreach (RabbitHole rh in Sims3.Gameplay.Queries.GetObjects<RabbitHole>())
            {
                holes++;
                if (CpStation(rh)) stations++;
                RabbitHole p = rh.RabbitHoleProxy;
                if (p == null || !object.ReferenceEquals(p, rh)) continue;
                int waiting = 0;
                if (p.WaitingToEnterSims != null)
                    foreach (List<ObjectGuid> l in p.WaitingToEnterSims.Values) if (l != null) waiting += l.Count;
                CpExit x;
                int leaving = sCpExit.TryGetValue(p.ObjectId.Value, out x) ? x.Pending : 0;
                int held = p.ReservedEnterSlotsPerPerson == null ? 0 : p.ReservedEnterSlotsPerPerson.Count;
                if (held == 0 && waiting == 0 && leaving == 0) continue;
                reserved++;
                if (b.Length < 1400) b.Append(" " + rh.GetType().Name + ": " + held + " enter slots reserved, " + waiting + " waiting to go in, " + leaving + " NPCs in our exit wait, last Sim left "
                    + SimClock.ElapsedTime(TimeUnit.Minutes, p.TimeLastSimLeft).ToString("0") + " sim-min ago;");
            }
            return b.Append(" elevator doors on the lot " + doorsN + ", rabbit holes " + holes + " (stations " + stations + "), busy " + reserved).ToString();
        }
    }
}
