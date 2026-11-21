using System;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects.Vehicles;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // ts3native [RouteAsync] NoPathMemo runtime switches (docs/643 §7). Both default off.
    //  - npmfuzzy: a plan of the same follower and request whose goal lies within 1 m of a goal EA just found no path to (same
    //    start within 0.5 m, same level, room and lot, short TTL, no success of that follower since) gets EA's own
    //    FailedBecauseNoPathToDestination at once. EA's FollowerMeetUpAtPoint retries a new Route per try around a meeting point
    //    moved 0.45 m each time (GoHereWithHoldingInteraction.Run), so the exact memo never repeats; 10 such tries in one tick
    //    were a 1 s hitch (2026-11-19 log).
    //  - npmhousehold: household, selected and unknown scope record and are answered too (the answer equals EA's no-path
    //    result). The selected or a selectable Sim's own command (its UserDirected, non-autonomous interaction that is not a follower's
    //    situation interaction) is marked for ts3native with op 39 and always gets a real search.
    // Wiring: UnseenFallback's plan hook calls NpmRouteSend right after the scope op. The switch state goes to ts3native as
    // UserDiag op 38 (bit 0 npmfuzzy, bit 1 npmhousehold) whenever it changed; ts3native keeps it process-wide
    // (ts3native_nopathmemo +0x64 / +0x68).
    // Counters: [4240] switch messages honoured (the memo is installed), [4241] not honoured (NoPathMemo=0 or an older
    // ts3native), [4242] player commands marked, [4243] errors. [4244-4249] spare.
    public static partial class Fixes
    {
        static int sNpmSent = -1;  // the op 38 value last sent (-1 = none yet)

        internal static int NpmFlags(bool fuzzy, bool household) { return (fuzzy ? 1 : 0) | (household ? 2 : 0); }

        // a player command: the selected (or a selectable) Sim's own user-directed interaction; a follower's situation interaction
        // (FollowerMeetUpAtPoint, created with the leader's priority) is not one
        internal static bool NpmIsPlayerCommand(bool selected, bool autonomous, bool userDirected, bool followerInteraction)
        {
            return selected && !autonomous && userDirected && !followerInteraction;
        }

        static void NpmRouteSend(Sim who, uint handle, bool ownCheck)
        {
            try
            {
                bool hh = ShortcutOn("npmhousehold");
                int flags = NpmFlags(ShortcutOn("npmfuzzy"), hh);
                if (flags != sNpmSent)
                {
                    sNpmSent = flags;
                    PatchCounters()[DiagSend(38, (uint)flags) ? 4240 : 4241]++;
                }
                if (!hh || who == null || ownCheck || handle == 0) return;
                InteractionInstance ia = who.CurrentInteraction;
                if (ia == null) return;
                if (!NpmIsPlayerCommand(who == PlumbBob.SelectedActor || who.IsSelectable, ia.Autonomous,
                        ia.GetPriority().Level == InteractionPriorityLevel.UserDirected, ia is FollowerMeetUpAtPoint)) return;
                if (DiagSend(39, handle)) PatchCounters()[4242]++;
            }
            catch (ResetException) { throw; }
            catch (Exception) { PatchCounters()[4243]++; }
        }

        // NpmCheck run: the flag encoding and the player-command rule with controls (pure; the native side has its selftest)
        public static object NpmCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "4240 honoured " + c[4240] + ", 4241 not honoured " + c[4241] + ", 4242 commands marked " + c[4242]
                + ", 4243 errors " + c[4243];
            if (what != "run") return "npm <run|result>: " + counters;
            bool flagsOk = NpmFlags(false, false) == 0 && NpmFlags(true, false) == 1 && NpmFlags(false, true) == 2 && NpmFlags(true, true) == 3;
            bool cmdOk = NpmIsPlayerCommand(true, false, true, false)
                && !NpmIsPlayerCommand(false, false, true, false)   // not a selectable Sim (an NPC or a pet the player does not control)
                && !NpmIsPlayerCommand(true, true, true, false)     // autonomous
                && !NpmIsPlayerCommand(true, false, false, false)   // not UserDirected
                && !NpmIsPlayerCommand(true, false, true, true);    // a follower's situation interaction
            return "npm: flags " + (flagsOk ? "ok" : "BROKEN") + ", player command rule " + (cmdOk ? "ok" : "BROKEN")
                + ", npmfuzzy " + ShortcutOn("npmfuzzy") + ", npmhousehold " + ShortcutOn("npmhousehold") + "; " + counters;
        }
    }
}
