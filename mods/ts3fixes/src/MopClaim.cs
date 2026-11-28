using System;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Controllers;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // mopclaim / mopclaimhh / mopclaimlog (docs/686 §43, default off). Rules in MopClaimCore.cs.
    //
    // [verified, decompiled Terrain.Mopping / PuddleInteraction] Mopping.Run takes the nearest puddle of the room
    // (GetClosestPuddle), walks there (RouteToPuddle) and only then asks PuddleManager.CanMopPuddle with Sims counted; the
    // Sim already mopping that tile refuses it, so a second Sim walks over, plays the route-fail shrug and gives up. A
    // puddle has no use list and no reservation, so every Sim that wants to mop picks the same nearest puddle.
    //
    // Fix: the tile a mopping Sim takes is held for it in nextplan's claim table (HhHold, key MopClaimCore.Key). A Sim
    // whose own pick (autonomous) lands on a tile another mopping Sim holds takes the nearest puddle of the room nobody
    // holds; with none left its mopping ends at once, quietly (no walk, no shrug). An ordered mop (the player's click, a
    // script's push) is never moved: it keeps EA's puddle and takes the hold over.
    // Probe (always counted): mop runs, and the runs that ended with a failed walk or EA's shrug.
    // Scope: mopclaim = Sims the player does not control, mopclaimhh = household and selected Sims (HhSharedCore.Scope).
    // Works with: nextplan (one claim table), seatclash (the same hold calls). Off = compare mode: counted, nothing
    // changed. Counters 5060-5079. Check: MopClaimCheck.
    public static partial class Fixes
    {
        const int kMcRuns = 5060, kMcRunFailed = 5061, kMcPicks = 5062, kMcHeld = 5063, kMcMoved = 5064, kMcNone = 5065, kMcWould = 5066,
            kMcOrdered = 5067, kMcHolds = 5068, kMcErrors = 5069, kMcLines = 5070, kMcHhRuns = 5071, kMcHhFailed = 5072;

        static readonly LineCapCore sMcCap = new LineCapCore(20, 60000, 300);
        static string sMcMiss = "", sMcLast = "", sMcLastError = "";
        static float[] sMcDist = new float[16]; static bool[] sMcOk = new bool[16];

        static void ApplyMopClaim()
        {
            sMcMiss = "";
            Patch(typeof(Terrain.PuddleInteraction), "GetClosestPuddle", new Type[] { typeof(ulong), typeof(Vector3) }, typeof(McPickHook), "ClosestHook", "ClosestOrig");
            Patch(typeof(Terrain.Mopping), "Run", Type.EmptyTypes, typeof(McRunHook), "RunHook", "RunOrig");
            sMcMiss = sMcMiss.Trim();
            if (sMcMiss.Length > 0) AppliedLog().Add("mopclaim: not found: " + sMcMiss);
            // the switches of these hooks, named here for the live-path map (CheckLiveMap.cs reads the Apply section)
            if (ShortcutOn("mopclaim") || ShortcutOn("mopclaimhh") || ShortcutOn("mopclaimlog")) AppliedLog().Add("mopclaim: switched on at start");
        }

        public abstract class McRunHook : Terrain.Mopping
        {
            public bool RunHook()
            {
                bool r = RunOrig();
                McRan(this);
                return r;
            }
            public bool RunOrig() { return false; }
        }

        public abstract class McPickHook : Terrain.PuddleInteraction
        {
            public Vector3 ClosestHook(ulong lotId, Vector3 startPos)
            {
                Vector3 r = ClosestOrig(lotId, startPos);
                Sim a = Actor;
                if (a == null || r == Vector3.Invalid || !((object)this is Terrain.Mopping)) return r;  // through object: the compiler folds "hook class is Mopping" to false
                try
                {
                    int[] c = PatchCounters();
                    c[kMcPicks]++;
                    LotLocation loc = LotLocation.Invalid;
                    World.GetLotLocationAtFloor(r, ref loc);
                    ulong key = MopClaimCore.Key(lotId, loc.mLevel, loc.mX, loc.mZ);
                    Sim other = McHolder(key, a);
                    int scope = HhSharedCore.Scope(true, true, IsPlayerSim(a), ShortcutOn("mopclaim"), ShortcutOn("mopclaimhh"));
                    int d = MopClaimCore.Decide(other != null, HhDirected(this), scope);
                    if (other != null) c[kMcHeld]++;
                    string head = BuWho(a) + " " + a.FullName + ": puddle at " + BuAt(r);
                    if (d == MopClaimCore.kOrdered) { c[kMcOrdered]++; McLine(head + " is being mopped by " + other.FullName + ": ordered, kept"); }
                    else if (d == MopClaimCore.kWould) { c[kMcWould]++; McLine(head + " is being mopped by " + other.FullName + ": would take another"); return r; }
                    else if (d == MopClaimCore.kExempt) return r;
                    else if (d == MopClaimCore.kMove)
                    {
                        Vector3 r2 = Unheld(lotId, startPos, a, out key);
                        if (r2 == Vector3.Invalid) { c[kMcNone]++; McLine(head + " is being mopped by " + other.FullName + ": no other puddle in the room, ends"); return r2; }
                        c[kMcMoved]++;
                        McLine(head + " is being mopped by " + other.FullName + ": takes the puddle at " + BuAt(r2));
                        r = r2;
                    }
                    if (scope != HhSharedCore.kExempt && HhHold(key, a, MopClaimCore.HoldMinutes(PuddleManager.TimeToMopPuddle, Instantiator.kNextPlanClaimMinutes))) c[kMcHolds]++;
                    return r;
                }
                catch (ResetException) { throw; }
                catch (Exception e) { McError(e); return r; }
            }
            public Vector3 ClosestOrig(ulong lotId, Vector3 startPos) { return Vector3.Invalid; }

            // the nearest puddle of the start's room and level that no other mopping Sim holds
            Vector3 Unheld(ulong lotId, Vector3 startPos, Sim a, out ulong key)
            {
                key = 0;
                Vector3[] tiles = GetValidTiles(lotId, startPos);
                int n = tiles.Length;
                if (sMcDist.Length < n) { sMcDist = new float[n]; sMcOk = new bool[n]; }
                LotLocation at = LotLocation.Invalid;
                World.GetLotLocationAtFloor(startPos, ref at);
                for (int i = 0; i < n; i++)
                {
                    LotLocation l = LotLocation.Invalid;
                    World.GetLotLocationAtFloor(tiles[i], ref l);
                    float dx = tiles[i].x - startPos.x, dz = tiles[i].z - startPos.z;
                    sMcDist[i] = dx * dx + dz * dz;
                    sMcOk[i] = l.mRoom == at.mRoom && l.mLevel == at.mLevel && TestTile(tiles[i]) && McHolder(MopClaimCore.Key(lotId, l.mLevel, l.mX, l.mZ), a) == null;
                }
                int pick = MopClaimCore.Nearest(sMcDist, sMcOk, n);
                if (pick < 0) return Vector3.Invalid;
                LotLocation p = LotLocation.Invalid;
                World.GetLotLocationAtFloor(tiles[pick], ref p);
                key = MopClaimCore.Key(lotId, p.mLevel, p.mX, p.mZ);
                return tiles[pick];
            }
        }

        // The other Sim that holds the tile and is still mopping; a hold whose Sim stopped mopping is dropped.
        static Sim McHolder(ulong key, Sim a)
        {
            ulong h = HhHolder(key);
            if (h == 0 || h == a.ObjectId.Value) return null;
            Sim o = HhSim(h);
            if (o != null && o.CurrentInteraction is Terrain.Mopping) return o;
            sNp.Drop(key, h);
            return null;
        }

        static void McRan(Terrain.Mopping m)
        {
            try
            {
                Sim a = m.Actor;
                if (a == null) return;
                int[] c = PatchCounters();
                bool hh = IsPlayerSim(a), failed = a.HasExitReason(ExitReason.RouteFailed);
                c[kMcRuns]++;
                if (hh) c[kMcHhRuns]++;
                if (!failed) return;
                c[kMcRunFailed]++;
                if (hh) c[kMcHhFailed]++;
                McLine(BuWho(a) + " " + a.FullName + ": mopping ended with a failed walk or a shrug at the puddle");
            }
            catch (ResetException) { throw; }
            catch (Exception e) { McError(e); }
        }

        static void McLine(string text)
        {
            sMcLast = text;
            PatchCounters()[kMcLines]++;
            if (ShortcutOn("mopclaimlog") && sMcCap.Take(System.Environment.TickCount)) DiagLine("mopclaim: " + text);
        }

        static void McError(Exception e)
        {
            int[] c = PatchCounters();
            c[kMcErrors]++;
            sMcLastError = "mopclaim: error: " + e.GetType().Name + ": " + e.Message;
            if (c[kMcErrors] <= 3) DiagLine(sMcLastError);
        }

        // Repro helper (test set-up): every Sim of the active household on the selected Sim's lot gets EA's own autonomous
        // "mop the floors" action (Sim.MopPuddles, which pushes Terrain.MopPuddle at the lot's first puddle), the way the
        // game queues it when several Sims decide to clean up at once.
        static string McMopAll()
        {
            Sims3.Gameplay.Core.Lot home = HhHomeLot();
            if (home == null) return "mopclaim mop: no active household with a home lot";
            int n = 0;
            foreach (Sim s in home.Household.Sims)
            {
                if (s == null || s.HasBeenDestroyed || s.LotCurrent != home || s.InteractionQueue == null) continue;
                InteractionInstance ii = Sim.MopPuddles.Singleton.CreateInstance(s, s, new InteractionPriority(InteractionPriorityLevel.Autonomous), true, true);
                if (ii != null && s.InteractionQueue.Add(ii)) n++;
            }
            return "mopclaim mop: queued for " + n + " Sims";
        }

        // MopClaimCheck: "result" counters and the last line, "reset" clears them (repro arms), "run" the rules with controls.
        public static object MopClaimCheck(string what)
        {
            try
            {
                int[] c = PatchCounters();
                if (what == "reset")
                {
                    for (int i = kMcRuns; i <= kMcHhFailed; i++) c[i] = 0;
                    sMcLast = ""; sMcLastError = "";
                    return "mopclaim: reset";
                }
                if (what == "mop") return McMopAll();
                if (what == "run")
                {
                    const int G = HhSharedCore.kGo;
                    bool fixedNow = MopClaimCore.Decide(true, false, G) == MopClaimCore.kMove
                        && MopClaimCore.Nearest(new float[] { 1f, 4f, 9f }, new bool[] { false, true, true }, 3) == 1;
                    bool controls = MopClaimCore.Decide(false, false, G) == MopClaimCore.kKeep && MopClaimCore.Decide(true, true, G) == MopClaimCore.kOrdered
                        && MopClaimCore.Decide(true, false, HhSharedCore.kWould) == MopClaimCore.kWould && MopClaimCore.Decide(true, false, HhSharedCore.kExempt) == MopClaimCore.kExempt
                        && MopClaimCore.Nearest(new float[] { 1f }, new bool[] { false }, 1) == -1 && MopClaimCore.Key(1, 0, 2, 3) != MopClaimCore.Key(1, 0, 3, 2)
                        && (MopClaimCore.Key(1, 0, 2, 3) >> 63) == 1;
                    string live = CkLive("mopclaim");
                    return "mopclaim run: fixed " + (fixedNow && live.Length == 0 ? "yes" : "NO") + ", controls " + (controls ? "ok" : "FAILED") + (live.Length > 0 ? "; live path: " + live : "") + (sMcMiss.Length > 0 ? "; not found: " + sMcMiss : "");
                }
                return "mopclaim " + (ShortcutOn("mopclaim") ? "on" : "off") + ", mopclaimhh " + (ShortcutOn("mopclaimhh") ? "on" : "off")
                    + ": mop runs " + c[kMcRuns] + " (ended with a failed walk or shrug " + c[kMcRunFailed] + "), household " + c[kMcHhRuns] + " (failed " + c[kMcHhFailed]
                    + "); puddle picks " + c[kMcPicks] + ", held by another mopping Sim " + c[kMcHeld] + " (took another puddle " + c[kMcMoved] + ", none left: ended quietly "
                    + c[kMcNone] + ", would " + c[kMcWould] + ", ordered: kept " + c[kMcOrdered] + "), holds " + c[kMcHolds] + "; snaps 0, clips 0; errors " + c[kMcErrors]
                    + (sMcLastError.Length > 0 ? " (" + sMcLastError + ")" : "") + (sMcMiss.Length > 0 ? "; not found: " + sMcMiss : "") + (sMcLast.Length > 0 ? "; last: " + sMcLast : "");
            }
            catch (ResetException) { throw; }
            catch (Exception e) { return "mopclaim check: " + e.GetType().Name + ": " + e.Message; }
        }
    }
}
