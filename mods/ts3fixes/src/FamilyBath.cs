using System;
using System.Collections.Generic;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects.Plumbing;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Situations;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // familybath / familybathhh / familybathlog (docs/686 §44, default off; rules in FamilyBathCore.cs). THIS CHANGES AN EA
    // RULE and is the player's choice: it is a switch of its own and nothing else turns it on.
    //
    // [verified, decompiled GenericPrivacySituation / PrivacySituation / Toilet / Shower / Bathtub] A Sim that showers,
    // bathes or uses the toilet starts a privacy situation for its room. Everyone else is kept out (the room restriction,
    // the door's portal check for autonomous routes) and shooed when inside, except Sims the situation's
    // OtherSimCanIgnorePrivacy accepts: toddlers and babies, and for these three the Sim's romantic partner
    // (Sim.CanIgnoreNormalPrivacySituationsWith). In an 8-Sim house with one bathroom a bath closes the toilet and the
    // sink for 51-76 sim-min (s691 / s693).
    //
    // Fix: the base OtherSimCanIgnorePrivacy (every subclass asks it first) also accepts a Sim of the same household that
    // comes for a different fixture: the toilet or the sink while someone showers or bathes, the sink while someone uses
    // the toilet (its running or next action targets that fixture in that room). Such a Sim holds a pass for
    // kPassMinutes after its last such ask (it washes its hands and walks out without being shooed), and the Sim it
    // joined is accepted by the newcomer's own privacy situation (a toilet user does not shoo the bather).
    // Never: guests, visitors and service Sims (not the household); a Sim that is Never Nude or Shy on either side; pets;
    // the same fixture; WooHoo and every other privacy situation.
    // The player's door lock is another mechanism (CommonDoor lock type, asked by the door itself) and is not changed:
    // a locked door still refuses the route, and a room whose doors the player locked against the Sim is never granted
    // here (HhDoorLockedAgainst).
    // Scope: familybath = households the player does not control, familybathhh = the active household (HhSharedCore.Scope).
    // Works with: privacyroom (its answer is unchanged; a let-in Sim is accepted by the rule it defers to), bathroomwait /
    // facilityplan (FbOpenFor: a toilet in a room held only by a household member's shower or bath counts as free),
    // camsafe (nothing is skipped or snapped: the Sims walk and use EA's animations).
    // Off = EA's rule; with familybathlog the would-be passes are counted. Counters 5090-5099. Check: FamilyBathCheck.
    public static partial class Fixes
    {
        const int kFbGrants = 5090, kFbWould = 5091, kFbPairs = 5092, kFbModest = 5093, kFbNotFamily = 5094, kFbDoorLocked = 5095,
            kFbSeen = 5096, kFbErrors = 5097, kFbLines = 5098, kFbOpenFor = 5099;

        struct FbPass { public ulong User; public long Until, Asked; public int Verdict; }
        static readonly Dictionary<ulong, FbPass> sFbPass = new Dictionary<ulong, FbPass>();  // by entering Sim; bounded
        static readonly Dictionary<Type, int> sFbKinds = new Dictionary<Type, int>();
        static readonly LineCapCore sFbCap = new LineCapCore(20, 60000, 300);
        static bool sFbLive;
        static int sFbPoll;
        static string sFbLast = "", sFbLastError = "";

        static void ApplyFamilyBath()
        {
            Patch(typeof(GenericPrivacySituation), "OtherSimCanIgnorePrivacy", new Type[] { typeof(Sim) }, typeof(FbHook), "IgnoreHook", "IgnoreOrig");
            // the switches of this hook, named here for the live-path map (CheckLiveMap.cs reads the Apply section)
            if (ShortcutOn("familybath") || ShortcutOn("familybathhh") || ShortcutOn("familybathlog")) AppliedLog().Add("familybath: switched on at start");
            World.OnWorldLoadFinishedEventHandler -= FbWorldLoaded;
            World.OnWorldLoadFinishedEventHandler += FbWorldLoaded;
        }

        static void FbWorldLoaded(object sender, EventArgs e) { sFbPass.Clear(); }

        public class FbHook
        {
            // EA's body folded in (toddlers and babies), so the off path adds no interpreter frame: the game asks this
            // inside route planning's portal checks (issue #92). IgnoreOrig is only the fallback after an error.
            public bool IgnoreHook(Sim sim)
            {
                try
                {
                    if (sim.SimDescription.ToddlerOrBelow) return true;
                    if ((++sFbPoll & 255) == 1) sFbLive = ShortcutOn("familybath") || ShortcutOn("familybathhh") || ShortcutOn("familybathlog");
                    if (!sFbLive) return false;
                    return FbAsk((GenericPrivacySituation)(object)this, sim, null);
                }
                catch (ResetException) { throw; }
                catch (Exception e) { FbError(e); }
                return IgnoreOrig(sim);
            }
            public bool IgnoreOrig(Sim sim) { return false; }  // receives GenericPrivacySituation.OtherSimCanIgnorePrivacy's body
        }

        // toilet / urinal = kUserToilet, shower / bathtub = kUserWash, everything else (WooHoo, sponge bath, mods) none
        static int FbKind(Type t)
        {
            int k;
            if (sFbKinds.TryGetValue(t, out k)) return k;
            string n = t.Name;
            k = n == "ToiletSituation" || n == "UrinalSituation" ? FamilyBathCore.kUserToilet
                : (n == "ShowerPrivacySituation" && t.DeclaringType == typeof(Shower)) || n == "BathtubPrivacySituation" ? FamilyBathCore.kUserWash : FamilyBathCore.kUserNone;
            if (sFbKinds.Count < 64) sFbKinds[t] = k;
            return k;
        }

        static int FbTargetKind(GameObject t) { return t is Toilet ? FamilyBathCore.kTargetToilet : t is Sink ? FamilyBathCore.kTargetSink : FamilyBathCore.kTargetOther; }

        // The fixture the entering Sim comes for: its running or next action's target, a toilet or sink in the user's room.
        static GameObject FbIntent(Sim sim, Sim user)
        {
            InteractionQueue q = sim.InteractionQueue;
            if (q == null) return null;
            for (int pass = 0; pass < 2; pass++)
            {
                InteractionInstance ii = pass == 0 ? q.GetCurrentInteraction() : q.GetHeadInteraction();
                GameObject t = ii == null ? null : ii.Target as GameObject;
                if (t != null && FbTargetKind(t) != FamilyBathCore.kTargetOther && t.RoomId == user.RoomId && t.LotCurrent == user.LotCurrent) return t;
            }
            return null;
        }

        static bool FbModest(Sim s) { return s.HasTrait(TraitNames.NeverNude) || s.HasTrait(TraitNames.Shy); }

        // The rule for sim entering user's room (situation g) for `target` (null: read from its queue). True = let in.
        static bool FbAsk(GenericPrivacySituation g, Sim sim, GameObject target)
        {
            Sim user = g.SyncTarget;
            if (user == null || user == sim || user.HasBeenDestroyed) return false;
            int kind = FbKind(g.GetType());
            if (kind == FamilyBathCore.kUserNone) return false;
            long now = SimClock.CurrentTicks;
            ulong uid = user.ObjectId.Value, sid = sim.ObjectId.Value;
            int[] c = PatchCounters();
            FbPass p;
            // the Sim that was joined, asked by the newcomer's own situation (the toilet user's about the bather)
            if (sFbPass.TryGetValue(uid, out p) && p.User == sid && p.Verdict == FamilyBathCore.kGrant && FamilyBathCore.PassLive(now, p.Until)) { c[kFbPairs]++; return true; }
            bool had = sFbPass.TryGetValue(sid, out p) && p.User == uid && FamilyBathCore.PassLive(now, p.Until);
            bool probe = target != null;
            if (target == null) target = FbIntent(sim, user);
            if (target == null) return had && p.Verdict == FamilyBathCore.kGrant;  // on its way out: the pass
            bool human = LswHuman(sim) && LswHuman(user);
            bool family = sim.Household != null && sim.Household == user.Household;
            int scope = HhSharedCore.Scope(true, true, IsPlayerSim(sim) || IsPlayerSim(user), ShortcutOn("familybath"), ShortcutOn("familybathhh"));
            // the door scan and the traits are asked again at most once a sim-minute per pair (route planning asks often)
            bool fresh = had && !probe && now - p.Asked < HhTicks(1f);
            int d = fresh ? p.Verdict : FamilyBathCore.Decide(kind, FbTargetKind(target), target.IsActorUsingMe(user),
                family && human && HhDoorLockedAgainst(sim, user.LotCurrent, user.RoomId), family, human, human && (FbModest(sim) || FbModest(user)), scope);
            if (probe) return d == FamilyBathCore.kGrant;  // FbOpenFor: an answer only, no pass
            if (!fresh) p.Asked = now;
            if (!had || p.Verdict != d)
            {
                // a new answer for this pair: counted and logged once, not per ask
                if (d == FamilyBathCore.kGrant) { c[kFbGrants]++; if (HhSnapSeen(sim, user.Position)) c[kFbSeen]++; }
                else if (d == FamilyBathCore.kWould) c[kFbWould]++;
                else if (d == FamilyBathCore.kModest) c[kFbModest]++;
                else if (d == FamilyBathCore.kNotFamily) c[kFbNotFamily]++;
                else if (d == FamilyBathCore.kDoorLocked) c[kFbDoorLocked]++;
                if (d != FamilyBathCore.kNone && d != FamilyBathCore.kExempt && d != FamilyBathCore.kNotFamily)
                    FbLine(BuWho(sim) + " " + sim.FullName + " -> " + target.GetType().Name + " while " + user.FullName + " has privacy (" + g.GetType().Name + "): " + FamilyBathCore.Names[d]);
            }
            if (sFbPass.Count >= 64 && !sFbPass.ContainsKey(sid)) sFbPass.Clear();
            p.User = uid; p.Verdict = d; p.Until = now + HhTicks(FamilyBathCore.kPassMinutes);
            sFbPass[sid] = p;
            return d == FamilyBathCore.kGrant;
        }

        // For the planners (bathroomwait, facilityplan): would `sim` be let into `target`'s room now although a privacy
        // situation holds it? True only when every privacy situation of that room is one familybath opens for this Sim.
        static bool FbOpenFor(Sim sim, GameObject target)
        {
            try
            {
                if (sim == null || target == null || target.LotCurrent == null) return false;
                if (!ShortcutOn("familybath") && !ShortcutOn("familybathhh")) return false;
                bool any = false;
                foreach (Sim o in target.LotCurrent.GetSims())
                {
                    if (o == null || o == sim || o.RoomId != target.RoomId || o.Autonomy == null || o.Autonomy.SituationComponent == null) continue;
                    foreach (Situation s in o.Autonomy.SituationComponent.Situations)
                    {
                        GenericPrivacySituation g = s as GenericPrivacySituation;
                        if (g == null || g.SyncTarget != o) continue;
                        if (!FbAsk(g, sim, target)) return false;
                        any = true;
                    }
                }
                if (any) PatchCounters()[kFbOpenFor]++;
                return any;
            }
            catch (ResetException) { throw; }
            catch (Exception e) { FbError(e); return false; }
        }

        static void FbLine(string text)
        {
            sFbLast = text;
            PatchCounters()[kFbLines]++;
            if (ShortcutOn("familybathlog") && sFbCap.Take(System.Environment.TickCount)) DiagLine("familybath: " + text);
        }

        static void FbError(Exception e)
        {
            int[] c = PatchCounters();
            c[kFbErrors]++;
            sFbLastError = "familybath: error: " + e.GetType().Name + ": " + e.Message;
            if (c[kFbErrors] <= 3) DiagLine(sFbLastError);
        }

        // FamilyBathCheck: "result" counters and the last line, "reset" clears them, "run" the rules with controls.
        public static object FamilyBathCheck(string what)
        {
            try
            {
                int[] c = PatchCounters();
                if (what == "reset")
                {
                    for (int i = kFbGrants; i <= kFbOpenFor; i++) c[i] = 0;
                    sFbLast = ""; sFbLastError = "";
                    return "familybath: reset";
                }
                if (what == "run")
                {
                    const int G = HhSharedCore.kGo, W = FamilyBathCore.kUserWash, T = FamilyBathCore.kUserToilet, TT = FamilyBathCore.kTargetToilet, TS = FamilyBathCore.kTargetSink;
                    bool fixedNow = FamilyBathCore.Decide(W, TT, false, false, true, true, false, G) == FamilyBathCore.kGrant
                        && FamilyBathCore.Decide(T, TS, false, false, true, true, false, G) == FamilyBathCore.kGrant;
                    bool controls = FamilyBathCore.Decide(W, TT, false, true, true, true, false, G) == FamilyBathCore.kDoorLocked
                        && FamilyBathCore.Decide(W, TT, false, false, false, true, false, G) == FamilyBathCore.kNotFamily
                        && FamilyBathCore.Decide(W, TT, false, false, true, true, true, G) == FamilyBathCore.kModest
                        && FamilyBathCore.Decide(T, TT, false, false, true, true, false, G) == FamilyBathCore.kNone
                        && FamilyBathCore.Decide(W, TT, true, false, true, true, false, G) == FamilyBathCore.kNone
                        && FamilyBathCore.Decide(W, TT, false, false, true, true, false, HhSharedCore.kWould) == FamilyBathCore.kWould;
                    string live = CkLive("familybath");
                    return "familybath run: fixed " + (fixedNow && live.Length == 0 ? "yes" : "NO") + ", controls " + (controls ? "ok" : "FAILED") + (live.Length > 0 ? "; live path: " + live : "");
                }
                return "familybath " + (ShortcutOn("familybath") ? "on" : "off") + ", familybathhh " + (ShortcutOn("familybathhh") ? "on" : "off")
                    + ": household members let into a room with privacy for another fixture " + c[kFbGrants] + " (on screen " + c[kFbSeen] + "), would " + c[kFbWould]
                    + "; the joined Sim accepted by the newcomer's situation " + c[kFbPairs] + "; EA's rule kept: modest " + c[kFbModest] + ", not family " + c[kFbNotFamily]
                    + ", door locked by the player " + c[kFbDoorLocked] + "; planner asks answered open " + c[kFbOpenFor] + "; snaps 0, clips 0; errors " + c[kFbErrors]
                    + (sFbLastError.Length > 0 ? " (" + sFbLastError + ")" : "") + (sFbLast.Length > 0 ? "; last: " + sFbLast : "");
            }
            catch (ResetException) { throw; }
            catch (Exception e) { return "familybath check: " + e.GetType().Name + ": " + e.Message; }
        }
    }
}
