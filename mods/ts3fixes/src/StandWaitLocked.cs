using System;
using System.Collections.Generic;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Situations;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // standwaitlocked / standwaitlockedhh (docs/686 §7, default off). Behaviour fix: a Sim that stands outside a door for
    // the whole obstacle stand-and-wait (20 sim-minutes in the game's tuning) because the room behind it went private
    // (toilet, shower, bath, sponge bath, woohoo) while the Sim was on its way.
    //
    // [verified, decompiled] A privacy situation locks every portal into its room for a Sim whose running (else
    // transition) interaction is autonomous up to the Privacy priority, or ordered below UserDirected, unless the Sim may
    // ignore privacy (toddlers and below, partners). A route planned to that room then fails at once with "destination
    // portal locked", the interaction ends and autonomy leaves targets in that room out. Only a Sim already on its way
    // that meets an obstacle is different: the obstacle stand-and-wait replans every cycle, each replan fails on the lock
    // and the cycle handler answers "keep waiting" (the early return of StandWait.cs) until the wait runs out. Then the
    // route fails as it would have at once.
    //
    // Fix (rules in StandWaitLockedCore): in a blocked cycle whose plan failed with "destination portal locked", when a
    // privacy situation of the destination's room (or of the room of the Sim the interaction targets) holds for this Sim
    // by the game's own rule, the wait ends after kStandWaitLockedCycles such cycles in a row with the game's own end of
    // a wait that ran out: the handler answers "stop" without electing an exit, the action returns Terminate and the
    // route fails the game's way. Nothing is teleported, no lock is opened.
    //
    // Kept as the game has it: a player's own order (never privacy-locked by the game's rule, so never seen here), Sims
    // that may ignore privacy, door locks set by the player and every other lock (no privacy situation in the room: the
    // game's wait), a Sim already inside the room. With standwaitfix on, a pending push or cancel is answered first.
    //
    // Scope: standwaitlocked covers Sims the player does not control; household and selected Sims need standwaitlockedhh
    // as well (or [Experimental] HouseholdToo). Off = compare mode: counts and the standwait: line say what it would do.
    // Counters 4340-4351. Check: StandWaitLockedCheck.
    public static partial class Fixes
    {
        const int kSwlLockedCycles = 4340, kSwlLockedWaits = 4341, kSwlPrivacyCycles = 4342, kSwlPrivacyWaits = 4343, kSwlGaveUp = 4344, kSwlWouldGiveUp = 4345,
            kSwlExempt = 4346, kSwlOtherWaits = 4347, kSwlCyclesLeft = 4348, kSwlPrivacyTimeouts = 4349, kSwlPrivacyCleared = 4350, kSwlErrors = 4351;

        // The privacy situations of one room of a lot: all of them, and those that hold for sim by the game's rule.
        static void SwlRoom(Sim sim, Lot lot, int room, bool gate, ref int all, ref int locking, ref string who)
        {
            if (lot == null || lot.IsWorldLot) return;
            List<PrivacySituation> list = lot.GetRoomPrivacySituations(room);
            for (int i = 0; i < list.Count; i++)
            {
                PrivacySituation ps = list[i];
                Sim owner = ps.SyncTarget;
                if (owner == null || owner.HasBeenDestroyed || owner.RoomId != room || owner.LotCurrent != lot) continue;  // the game's handler tests the owner's room as it is now
                all++;
                if (!gate || ps.OtherSimCanIgnorePrivacy(sim)) continue;
                if (locking == 0) who = ps.GetType().Name + " of " + owner.FullName + (ps.RoomId != room ? ", begun in room " + ps.RoomId : "");
                locking++;
            }
        }

        // What locks the destination of a route whose plan just failed with "destination portal locked".
        static int SwlLockKind(Sim sim, Route path, out string who)
        {
            who = "";
            InteractionInstance ia = sim.InteractionQueue != null ? (sim.InteractionQueue.GetCurrentInteraction() ?? sim.InteractionQueue.TransitionInteraction) : null;
            bool gate = ia != null && StandWaitLockedCore.PrivacyLocks(true, ia.Autonomous, (int)ia.GetPriority().Level,
                (int)InteractionPriorityLevel.Privacy, (int)InteractionPriorityLevel.UserDirected);
            int all = 0, locking = 0;
            bool inRoom = false;
            Vector3 dest = path.GetApproximateOriginalDestination();
            if (dest != Vector3.Invalid && dest != Vector3.Origin)
            {
                LotLocation loc = LotLocation.Invalid;
                Lot lot = LotManager.GetLot(World.GetLotLocation(dest, ref loc));
                if (lot != null && loc != LotLocation.Invalid)
                {
                    inRoom = sim.LotCurrent == lot && sim.RoomId == loc.mRoom;
                    SwlRoom(sim, lot, loc.mRoom, gate, ref all, ref locking, ref who);
                }
            }
            // a route to a Sim: the room the Sim is in now (the destination point is where it stood at the plan)
            InteractionInstance head = ia ?? (sim.InteractionQueue != null ? sim.InteractionQueue.GetHeadInteraction() : null);
            Sim target = head != null ? head.Target as Sim : null;
            if (locking == 0 && !inRoom && target != null && target != sim && !target.HasBeenDestroyed && target.LotCurrent != null
                && !(target.LotCurrent == sim.LotCurrent && target.RoomId == sim.RoomId))
                SwlRoom(sim, target.LotCurrent, target.RoomId, gate, ref all, ref locking, ref who);
            return StandWaitLockedCore.LockKind(inRoom, all, locking, sim.SimRoutingComponent != null ? sim.SimRoutingComponent.LockedDoorsDuringPlan.Count : 0);
        }

        // A blocked cycle whose replan failed with "destination portal locked". True = end the wait now.
        static bool SwlCycle(SwfState st, Sim sim, Route path, bool yieldDue, int[] c)
        {
            try
            {
                st.Locked++;
                c[kSwlLockedCycles]++;
                if (st.Locked == 1) c[kSwlLockedWaits]++;
                string who;
                int kind = SwlLockKind(sim, path, out who);
                st.LockKind = kind;
                st.LockText = StandWaitLockedCore.kLockNames[kind] + (who.Length > 0 ? " (" + who + ")" : "");
                if (kind != StandWaitLockedCore.kLockPrivacy) { st.LockRun = 0; return false; }
                st.Privacy++; st.LockRun++;
                c[kSwlPrivacyCycles]++;
                if (st.Privacy == 1) c[kSwlPrivacyWaits]++;
                bool fixOn = ShortcutOn("standwaitlocked");
                st.LockOn = fixOn && SwfInScope(sim, ShortcutOn("standwaitlockedhh"));
                switch (StandWaitLockedCore.Decide(kind, st.LockRun, Instantiator.kStandWaitLockedCycles, fixOn, st.LockOn, yieldDue))
                {
                    case StandWaitLockedCore.kWould:
                        if (st.WouldGiveUp == 0) { st.WouldGiveUp = st.Blocked; c[kSwlWouldGiveUp]++; }
                        break;
                    case StandWaitLockedCore.kExempt:
                        if (!st.LockExempt) { st.LockExempt = true; c[kSwlExempt]++; }
                        break;
                    case StandWaitLockedCore.kGiveUp:
                        st.GaveUp = true; st.GaveUpCycle = st.Blocked;
                        c[kSwlGaveUp]++;
                        return true;
                }
            }
            catch (ResetException) { throw; }
            catch (Exception) { c[kSwlErrors]++; }
            return false;
        }

        // The plan succeeded again after privacy-locked cycles: the lock ended while the Sim waited.
        static void SwlCleared(SwfState st, int[] c)
        {
            if (st.ClearedCycle == 0) c[kSwlPrivacyCleared]++;
            st.ClearedCycle = st.Cycles;
        }

        // The lock part of a wait's standwait: line; counts how the wait ended.
        static string SwlEnd(SwfState st, int endKind, int cyclesLeft, int[] c)
        {
            if (st.Locked == 0) return "";
            if (st.Privacy == 0) c[kSwlOtherWaits]++;
            else if (st.GaveUp) c[kSwlCyclesLeft] += cyclesLeft;
            else if (endKind == StandWaitCore.kEndTimeout) c[kSwlPrivacyTimeouts]++;
            string did = st.Privacy == 0 ? "kept (not a privacy lock for this Sim)"
                : st.GaveUp ? "gave up cycle " + st.GaveUpCycle
                : st.LockExempt ? "exempt (household)"
                : st.WouldGiveUp > 0 ? "would give up cycle " + st.WouldGiveUp
                : "kept (under " + Instantiator.kStandWaitLockedCycles + " cycles)";
            return ", lock " + st.LockText + ": " + st.Locked + " locked cycles, " + st.Privacy + " privacy, fix " + (st.LockOn ? "on" : st.LockExempt ? "exempt" : "off") + ": " + did
                + (st.ClearedCycle > 0 ? ", lock ended in cycle " + st.ClearedCycle : "");
        }

        // StandWaitLockedCheck run: the rules with controls (read-only, nothing in the world is touched).
        //   Bug case: an autonomous Sim's wait at a privacy-locked room; with the switch on it ends in cycle kStandWaitLockedCycles.
        // StandWaitLockedCheck result: the counters and the last wait's line.
        // StandWaitLockedCheck lot: the privacy situations on the selected Sim's lot and whether they lock that Sim now (read-only).
        public static object StandWaitLockedCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "standwaitlocked: locked cycles " + c[kSwlLockedCycles] + " in " + c[kSwlLockedWaits] + " waits, privacy-locked cycles " + c[kSwlPrivacyCycles] + " in "
                + c[kSwlPrivacyWaits] + " waits; on: gave up " + c[kSwlGaveUp] + " (timeout cycles left " + c[kSwlCyclesLeft] + "), exempt waits " + c[kSwlExempt]
                + "; off: would give up " + c[kSwlWouldGiveUp] + "; privacy waits that ran to the timeout " + c[kSwlPrivacyTimeouts] + ", whose lock ended while waiting "
                + c[kSwlPrivacyCleared] + "; waits at another lock (kept) " + c[kSwlOtherWaits] + ", errors " + c[kSwlErrors] + "; give up after " + Instantiator.kStandWaitLockedCycles
                + " cycles; " + GlMembers(sSwfMissing) + (sSwfLast.Length > 0 ? "; last: " + sSwfLast : "");
            if (what == "result") return counters;
            if (what == "lot") return SwlLot();
            if (what != "run") return "standwaitlocked <run|result|lot>";
            bool on = ShortcutOn("standwaitlocked"), hh = ShortcutOn("standwaitlockedhh");
            int n = Instantiator.kStandWaitLockedCycles, privacy = (int)InteractionPriorityLevel.Privacy, user = (int)InteractionPriorityLevel.UserDirected;
            // an autonomous Sim on its way to a Sim in a room that went private; one privacy situation there holds for it
            bool gate = StandWaitLockedCore.PrivacyLocks(true, true, (int)InteractionPriorityLevel.Autonomous, privacy, user);
            int kind = StandWaitLockedCore.LockKind(false, 1, gate ? 1 : 0, 0);
            bool fixedNow = n > 0 && StandWaitLockedCore.Decide(kind, n, n, on, true, false) == StandWaitLockedCore.kGiveUp;
            bool wouldNow = n > 0 && StandWaitLockedCore.Decide(kind, n, n, false, true, false) == StandWaitLockedCore.kWould;
            bool ctlEarly = n <= 1 || StandWaitLockedCore.Decide(kind, n - 1, n, true, true, false) == StandWaitLockedCore.kKeep;
            bool ctlOrder = !StandWaitLockedCore.PrivacyLocks(true, false, user, privacy, user)
                && StandWaitLockedCore.LockKind(false, 1, 0, 0) == StandWaitLockedCore.kLockPrivacyOpen;  // a player's order: the game does not lock it
            bool ctlNoIa = !StandWaitLockedCore.PrivacyLocks(false, true, 0, privacy, user);
            bool ctlDoor = StandWaitLockedCore.LockKind(false, 0, 0, 1) == StandWaitLockedCore.kLockDoor
                && StandWaitLockedCore.Decide(StandWaitLockedCore.kLockDoor, 99, n, true, true, false) == StandWaitLockedCore.kKeep;
            bool ctlOther = StandWaitLockedCore.Decide(StandWaitLockedCore.LockKind(false, 0, 0, 0), 99, n, true, true, false) == StandWaitLockedCore.kKeep;
            bool ctlInside = StandWaitLockedCore.LockKind(true, 1, 1, 0) != StandWaitLockedCore.kLockPrivacy;
            bool ctlScope = StandWaitLockedCore.Decide(kind, n, n, true, false, false) == StandWaitLockedCore.kExempt;
            bool ctlYield = StandWaitLockedCore.Decide(kind, n, n, true, true, true) == StandWaitLockedCore.kYieldFirst;
            bool controls = gate && wouldNow && ctlEarly && ctlOrder && ctlNoIa && ctlDoor && ctlOther && ctlInside && ctlScope && ctlYield;
            return "standwaitlocked check (switch " + (on ? "on" : "off") + ", household " + (hh ? "on" : "off") + "): wait at a privacy lock ends in cycle " + n + "=" + fixedNow
                + "; control: off counts it=" + wouldNow + ", an earlier cycle waits=" + ctlEarly + ", a player's order is not locked=" + ctlOrder + ", no interaction not locked=" + ctlNoIa
                + ", door lock kept=" + ctlDoor + ", other lock kept=" + ctlOther + ", Sim inside the room kept=" + ctlInside + ", household without its switch exempt=" + ctlScope
                + ", push or cancel first=" + ctlYield + "; controls ok=" + controls + "; verdict: " + GlVerdict(on, fixedNow, controls, sSwfMissing.Length == 0) + "; " + counters;
        }

        static string SwlLot()
        {
            try
            {
                Sim sim = PlumbBob.SelectedActor;
                if (sim == null || sim.LotCurrent == null) return "standwaitlocked lot: no selected Sim on a lot";
                Lot lot = sim.LotCurrent;
                InteractionInstance ia = sim.InteractionQueue.GetCurrentInteraction() ?? sim.InteractionQueue.TransitionInteraction;
                bool gate = ia != null && StandWaitLockedCore.PrivacyLocks(true, ia.Autonomous, (int)ia.GetPriority().Level,
                    (int)InteractionPriorityLevel.Privacy, (int)InteractionPriorityLevel.UserDirected);
                string s = "standwaitlocked lot: " + sim.FullName + " in room " + sim.RoomId + ", " + RflIa(ia) + ", lockable by privacy now=" + gate + "; private rooms:";
                int rooms = 0;
                Dictionary<int, bool> seen = new Dictionary<int, bool>();
                foreach (Sim other in lot.GetSims())
                {
                    if (other == null || other.HasBeenDestroyed || seen.ContainsKey(other.RoomId)) continue;
                    seen[other.RoomId] = true;
                    int all = 0, locking = 0;
                    string who = "";
                    SwlRoom(sim, lot, other.RoomId, gate, ref all, ref locking, ref who);
                    if (all == 0) continue;
                    rooms++;
                    s += " room " + other.RoomId + " (" + all + " situations, " + locking + " lock this Sim" + (who.Length > 0 ? ": " + who : "") + ")";
                }
                return rooms == 0 ? s + " none" : s;
            }
            catch (ResetException) { throw; }
            catch (Exception e) { return "standwaitlocked lot: " + e.GetType().Name; }
        }
    }
}
