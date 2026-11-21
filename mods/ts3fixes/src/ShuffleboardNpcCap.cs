using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects.HobbiesSkills;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // shuffleboardnpccap (tuning kShuffleboardNpcCap, default off; docs/647). NPCs on Bridgeport bars play shuffleboard for
    // hours: Rafael Striker played alone from 16:00 to 02:00 (phase-20261116-1503, 29 route plans, one every ~17 sim-min).
    // EA [verified, decompiled Shuffleboard]: Play.Run does one throw, then PushNextInteractionAfterJoinOrPlay pushes the next
    // Play (or Wait) as a must-run continuation. EndRound counts rounds only with two teams (mRound++ under mTeams.Count > 1),
    // so a Sim playing alone never reaches game over: the rounds go on until a motive or an outside cancel ends them. With two
    // teams game over (kNumberOfRounds rounds) ends every player, and autonomy may pick Join again at once.
    // Fix: at a round start (a Play continuation that finds mShouldRestart or mShouldClearRound set, before EA's RouteToPlay
    // and SetupForNewGame), the board's streak is judged by ShuffleboardNpcCapCore. When every Sim using the board is a
    // non-exempt NPC and the streak is capped (kShuffleboardNpcCapGames games of EA's kNumberOfRounds rounds, or
    // kShuffleboardNpcCapHours sim-hours, or the night window kShuffleboardNpcCapNightFrom..To with a round done), the Play
    // ends by EA's own path for a Sim that cannot play on: Shuffleboard.Quit(Actor) and return true (what Run does when
    // RouteToPlay leaves an exit reason). Nothing has entered the state machine yet, so no animation is cut. Play.Cleanup
    // then runs MakeSureSomeoneIsPlaying: a remaining player gets the turn, its Play finds the same round start and ends too.
    // With every player gone mTeams is null, and the next Join sets mShouldRestart: SetupForNewGame(true) resets the board
    // (pucks, rounds) as after any EA quit. An ended Sim's autonomous Join tests on any board fail for
    // kShuffleboardNpcCapCooldownHours, so autonomy does not restart the streak at once; user-directed joins are EA's.
    // Household rule: a cut, so HouseholdExempt and IsPlayerSim; any household, selected or unknown-scope player keeps the
    // whole board on EA's path, and such a Sim is never refused a Join.
    // Counters [4090] Plays ended, [4091] round starts left to EA for an exempt player, [4092] errors, [4093] autonomous
    // re-joins refused during the cooldown. Log "shuffleboardnpccap: ...", at most 10 a real minute.
    public static partial class Fixes
    {
        static readonly ShuffleboardNpcCapCore sSnc = new ShuffleboardNpcCapCore();
        static FieldInfo sSncRestart, sSncClearRound;
        static int sSncMinute = -1, sSncLogged;

        static void ApplyShuffleboardNpcCap()
        {
            const BindingFlags nested = BindingFlags.Public | BindingFlags.NonPublic;
            Type play = typeof(Shuffleboard).GetNestedType("Play", nested);
            Type join = typeof(Shuffleboard).GetNestedType("Join", nested);
            Type joinDef = join != null ? join.GetNestedType("Definition", nested) : null;
            sSncRestart = typeof(Shuffleboard).GetField("mShouldRestart", kInstAll);
            sSncClearRound = typeof(Shuffleboard).GetField("mShouldClearRound", kInstAll);
            if (play == null || joinDef == null || sSncRestart == null || sSncClearRound == null)
            {
                AppliedLog().Add("shuffleboardnpccap: Shuffleboard members not found, not patched");
                return;
            }
            Patch(play, "Run", Type.EmptyTypes, typeof(SncPlayHook), "RunHook", "RunOrig");
            Patch(joinDef, "Test", new Type[] { typeof(Sim), typeof(Shuffleboard), typeof(bool), typeof(GreyedOutTooltipCallback).MakeByRefType() },
                typeof(SncJoinHook), "TestHook", "TestOrig");
        }

        static void SncLog(string s)
        {
            int minute = System.Environment.TickCount / 60000;
            if (minute != sSncMinute) { sSncMinute = minute; sSncLogged = 0; }
            if (sSncLogged++ < 10) DiagLine("shuffleboardnpccap: " + s);
        }

        static bool SncExempt(Sim s) { return HouseholdExempt(s) || IsPlayerSim(s); }

        static long SncTicks(float hours) { return hours > 0f ? SimClock.ConvertToTicks(hours, TimeUnit.Hours) : 0; }

        static ShuffleboardNpcCapCore.Tun SncTun()
        {
            ShuffleboardNpcCapCore.Tun t = new ShuffleboardNpcCapCore.Tun();
            t.RoundsCap = Instantiator.kShuffleboardNpcCapGames > 0 ? Instantiator.kShuffleboardNpcCapGames * (int)Math.Max(1u, Shuffleboard.kNumberOfRounds) : 0;
            t.CapTicks = SncTicks(Instantiator.kShuffleboardNpcCapHours);
            t.GapTicks = SncTicks(Instantiator.kShuffleboardNpcCapGapHours);
            t.NightFrom = Instantiator.kShuffleboardNpcCapNightFrom;
            t.NightTo = Instantiator.kShuffleboardNpcCapNightTo;
            t.CooldownTicks = SncTicks(Instantiator.kShuffleboardNpcCapCooldownHours);
            return t;
        }

        // the first exempt Sim using the board (the actor counts too), or null when every player is a non-exempt NPC
        static Sim SncExemptPlayer(Sim actor, Shuffleboard board)
        {
            if (SncExempt(actor)) return actor;
            List<Sim> users = board.ActorsUsingMe;
            if (users != null)
                for (int i = 0; i < users.Count; i++)
                    if (users[i] != null && users[i] != actor && SncExempt(users[i])) return users[i];
            return null;
        }

        // true = this Play ends here (the Sim has quit the board)
        static bool SncBefore(InteractionInstance play)
        {
            if (play == null || !play.PushedAsContinuation) return false;
            Sim actor = play.InstanceActor as Sim;
            Shuffleboard board = play.Target as Shuffleboard;
            if (actor == null || board == null || actor.SimDescription == null) return false;
            if (!(bool)sSncRestart.GetValue(board) && !(bool)sSncClearRound.GetValue(board)) return false;  // mid-round throw
            int[] c = PatchCounters();
            if (SncExemptPlayer(actor, board) != null) { c[4091]++; return false; }
            ShuffleboardNpcCapCore.Tun t = SncTun();
            long now = SimClock.CurrentTicks;
            ShuffleboardNpcCapCore.Board b;
            int v = sSnc.RoundStart(board.ObjectId.Value, now, (int)SimClock.CurrentTime().Hour, t, out b);
            if (v == ShuffleboardNpcCapCore.kNone) return false;
            board.Quit(actor);
            sSnc.Ended(actor.ObjectId.Value, now, t.CooldownTicks);
            c[4090]++;
            float hours = (float)(now - b.First) / Math.Max(1L, SncTicks(1f));
            SncLog(actor.FullName + " stops playing (" + ShuffleboardNpcCapCore.Why(v) + ") after " + b.Rounds + " rounds / "
                + hours.ToString("0.0") + " sim-h at " + (board.LotCurrent != null ? board.LotCurrent.Name : "?") + ", "
                + SimClock.CurrentTime().ToString() + "; no autonomous re-join for " + Instantiator.kShuffleboardNpcCapCooldownHours + " sim-h");
            return true;
        }

        public class SncPlayHook
        {
            public bool RunHook()
            {
                if (ShortcutOn("shuffleboardnpccap"))
                {
                    bool ended = false;
                    try { ended = SncBefore((object)this as InteractionInstance); }
                    catch (Exception ex) { PatchCounters()[4092]++; SncLog("error " + ex.GetType().Name); }
                    if (ended) return true;  // EA's quit path for a Sim that cannot play on; Cleanup hands the turn on
                }
                return RunOrig();
            }
            public bool RunOrig() { return false; } // receives Shuffleboard+Play.Run's original body
        }

        public class SncJoinHook
        {
            public bool TestHook(Sim a, Shuffleboard target, bool isAutonomous, ref GreyedOutTooltipCallback greyedOutTooltipCallback)
            {
                bool r = TestOrig(a, target, isAutonomous, ref greyedOutTooltipCallback);
                if (!r || !isAutonomous || sSnc.CoolCount == 0 || a == null || !ShortcutOn("shuffleboardnpccap")) return r;
                try
                {
                    if (!sSnc.Cooling(a.ObjectId.Value, SimClock.CurrentTicks, SncTicks(Instantiator.kShuffleboardNpcCapCooldownHours))) return r;
                    if (SncExempt(a)) { PatchCounters()[4091]++; return r; }
                    PatchCounters()[4093]++;
                    return false;
                }
                catch (Exception ex) { PatchCounters()[4092]++; SncLog("error in Join test " + ex.GetType().Name); return r; }
            }
            public bool TestOrig(Sim a, Shuffleboard target, bool isAutonomous, ref GreyedOutTooltipCallback greyedOutTooltipCallback)
            { return false; } // receives Shuffleboard+Join+Definition.Test's original body
        }
    }
}
