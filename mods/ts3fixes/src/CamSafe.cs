using System;
using System.Collections.Generic;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // camsafe (docs/690, default off): one camera-safe "unseen" rule for every off-screen shortcut. Rules in CamSafeCore.cs.
    //
    // Every off-screen shortcut asks the same primitives: TownieUnseen (an NPC), FarFromViewers (a spot, NPC shortcuts),
    // FarFromCamera / UnsFar (a spot, household opt-ins), UcOnScreen (a Sim or object on screen now). camsafe works inside
    // them, so every shortcut moves to the new rule with one switch and none keeps its own copy:
    //   - the camera reach those tests compare with (sReach2) becomes CamSafeCore's predictive reach (margin, lead from
    //     the smoothed camera speed, the recent camera path) and is unlimited for JumpHoldS after a camera jump;
    //   - UcOnScreen also answers true for an object seen on screen in the last HoldS real seconds;
    //   - TownieUnseen's camera half is judged by both rules and counted.
    // With camsafe off every test is the old one, unchanged. camsafelog (or camsafe) samples the camera and counts what the
    // other rule would say ("old unseen, camsafe seen"), so the off arm measures how often the old test acts in a risky spot.
    //
    // Household scope is unchanged: NPCs use TownieUnseen's rules, household Sims only reach these tests through a fix's own
    // `...hhunseen` opt-in, and the selected Sim is never unseen (CsDecide refuses it too).
    //
    // Rule for callers (the alibi rule): decide once, at the start, with CsDecide (or the primitives). A shortcut that ends
    // later than it starts (an armed teleport, a state set over several ticks) asks CsSnap before its last step: true = the
    // Sim or its end spot came into view, so finish into a valid end state at once (the game's own result, or the step behind
    // a fade) and never leave a half state on screen.
    //
    // Counters 4880-4899 (registry): see the k* names. Check: CamSafeCheck.
    public static partial class Fixes
    {
        const int kCsAsk = 4880, kCsOldUnseen = 4881, kCsUnseen = 4882, kCsVeto = 4883, kCsVetoJump = 4884, kCsVetoHold = 4885,
            kCsVetoMargin = 4886, kCsSpotVeto = 4887, kCsObjHold = 4888, kCsJumps = 4889, kCsSnaps = 4890, kCsDecides = 4891,
            kCsDecideUnseen = 4892, kCsLate = 4893, kCsWatched = 4894, kCsLateVetoed = 4895, kCsSpeedMax = 4896, kCsReachMax = 4897,
            kCsSamples = 4898, kCsErrors = 4899;

        static readonly CamSafeCore sCs = new CamSafeCore();
        static bool sCsOn, sCsShadow;                       // read at each camera refresh (once a sim tick that asks)
        static float sCsReach2 = float.MaxValue, sOldReach2; // camsafe's and the old reach, squared
        static string sCsLastError = "";

        static void CsError(Exception e)
        {
            PatchCounters()[kCsErrors]++;
            sCsLastError = "camsafe: error: " + e.GetType().Name + ": " + e.Message;
        }

        // RefreshViewersBody, after the old reach is set: one camera sample.
        static void CsRefresh()
        {
            sOldReach2 = sReach2;
            sCsOn = ShortcutOn("camsafe");
            sCsShadow = sCsOn || ShortcutOn("camsafelog");
            if (!sCsShadow) return;
            try
            {
                CamSafeCore k = sCs;
                k.MarginPct = Instantiator.kCamSafeMarginPct; k.LeadS = Instantiator.kCamSafeLeadS; k.SpeedMax = Instantiator.kCamSafeSpeedMax;
                k.SpeedTauMs = Instantiator.kCamSafeSpeedTauMs; k.HoldS = Instantiator.kCamSafeHoldS; k.JumpM = Instantiator.kCamSafeJumpM;
                k.JumpHoldS = Instantiator.kCamSafeJumpHoldS;
                int now = System.Environment.TickCount;
                int[] c = PatchCounters();
                if (k.Sample(now, sCamTarget.x, sCamTarget.y, sCamTarget.z, sCamPos.x, sCamPos.y, sCamPos.z, sReach)) c[kCsJumps]++;
                c[kCsSamples]++;
                c[kCsSpeedMax] = Math.Max(c[kCsSpeedMax], (int)k.Speed);
                if (k.Reach != float.MaxValue) c[kCsReachMax] = Math.Max(c[kCsReachMax], (int)k.Reach);
                sCsReach2 = k.Reach == float.MaxValue ? float.MaxValue : k.Reach * k.Reach;
                if (sCsOn) sReach2 = Math.Max(sReach2, sCsReach2);  // every test on sReach2 (FarFromViewers, FarFromCamera, UnsFar)
                CsAudit(now);
            }
            catch (ResetException) { throw; }
            catch (Exception e) { CsError(e); sCsReach2 = float.MaxValue; if (sCsOn) sReach2 = float.MaxValue; }  // unsure = seen
        }

        // FarCompute's camera part with both reaches. m2: squared distance to the nearer of the camera target and eye.
        // Returns the in-force answer for "camera far" given the old answer (viewers already passed).
        static bool CsFarSpot(float m2)
        {
            if (!sCsShadow || m2 >= sCsReach2) return true;
            PatchCounters()[kCsSpotVeto]++;
            return !sCsOn;
        }

        // UcOnScreen with the recently-seen hold (r: on screen now).
        static bool CsScreen(IHasScriptProxy o, bool r)
        {
            if (o == null || o.Proxy == null) return r;
            return CsScreen(o.Proxy.ObjectId, r);
        }
        static bool CsScreen(ObjectGuid id, bool r)
        {
            try
            {
                int now = System.Environment.TickCount;
                if (r) { sCs.NoteSeen(id.Value, now); return true; }
                if (!sCs.Held(id.Value, now)) return false;
                PatchCounters()[kCsObjHold]++;
                return sCsOn;
            }
            catch (ResetException) { throw; }
            catch (Exception e) { CsError(e); return r || sCsOn; }
        }

        // TownieUnseen's camera half with both rules: the table answer (b >= 0) or the old test, then camsafe's. Counts
        // each verdict, returns the rule in force.
        static bool CsTownieCamera(Sim sim, int b)
        {
            long tick = SimClock.CurrentTicks;
            if (tick != sViewTick) RefreshViewers(tick);
            if (!sCsShadow)
            {
                if (b >= 0) return sUtbCheck ? UtbCompare(UtbUnseen(b), !UcOnScreen(sim) && FarFromViewers(sim.Position)) : UtbUnseen(b);
                if (UcOnScreen(sim)) return false;
                return FarFromViewers(sim.Position);
            }
            int[] c = PatchCounters();
            c[kCsAsk]++;
            UcCount(kUcOnScreen);
            bool onNow = World.IsObjectOnScreen(sim);
            int now = System.Environment.TickCount;
            ulong id = sim.ObjectId.Value;
            bool old;
            Vector3 p = sim.Position;
            if (b >= 0) old = sUtbCheck ? UtbCompare(UtbUnseen(b), !onNow && FarOld(p)) : UtbUnseen(b);
            else old = !onNow && FarOld(p);
            bool held = false;
            if (onNow) sCs.NoteSeen(id, now);
            else held = sCs.Held(id, now);
            int v = CamSafeCore.Verdict(onNow, old, sCs.Jumping(now), held, sCs.Near(p.x, p.y, p.z));
            if (old) c[kCsOldUnseen]++;
            if (v == CamSafeCore.kUnseen) c[kCsUnseen]++;
            else if (old)
            {
                c[kCsVeto]++;
                c[v == CamSafeCore.kVetoJump ? kCsVetoJump : v == CamSafeCore.kVetoHold ? kCsVetoHold : kCsVetoMargin]++;
            }
            bool r = sCsOn ? v == CamSafeCore.kUnseen : old;
            if (r) CsWatch(id, now, 1);
            else if (old) CsWatch(id, now, 2);
            return r;
        }

        // The old FarFromViewers answer, uncached (sOldReach2: the reach before camsafe grew it).
        static bool FarOld(Vector3 p)
        {
            float dx = p.x - sCamTarget.x, dy = p.y - sCamTarget.y, dz = p.z - sCamTarget.z;
            if (dx * dx + dy * dy + dz * dz < sOldReach2) return false;
            dx = p.x - sCamPos.x; dy = p.y - sCamPos.y; dz = p.z - sCamPos.z;
            if (dx * dx + dy * dy + dz * dz < sOldReach2) return false;
            const float kMin2 = kShortcutMinDistance * kShortcutMinDistance;
            for (int i = 0; i < sViewerCount; i++)
            {
                dx = p.x - sViewers[i].x; dy = p.y - sViewers[i].y; dz = p.z - sViewers[i].z;
                if (dx * dx + dy * dy + dz * dz < kMin2) return false;
            }
            return true;
        }

        // ---- late-seen audit: unseen verdicts whose Sim is on screen within kCamSafeAuditS real seconds ----
        // kind 1: judged unseen by the rule in force (on screen soon = a shortcut may have acted in view: [4893]);
        // kind 2: unseen by the old test, seen by camsafe (on screen soon = camsafe's veto was right: [4895]);
        // kind 3: a Sim alibiwalk cut to its tail (on screen soon after the cut: [4933], AlibiWalk.cs).
        static readonly Dictionary<ulong, long> sCsWatch = new Dictionary<ulong, long>();
        static readonly List<ulong> sCsDone = new List<ulong>();
        static int sCsAuditAt;

        static void CsWatch(ulong id, int now, int kind)
        {
            if (sCsWatch.ContainsKey(id) || sCsWatch.Count >= 128) return;
            sCsWatch[id] = ((long)now << 2) | (uint)kind;
            PatchCounters()[kCsWatched]++;
        }

        static void CsAudit(int now)
        {
            if (sCsWatch.Count == 0 || unchecked(now - sCsAuditAt) < 250) return;
            sCsAuditAt = now;
            int auditMs = (int)(Instantiator.kCamSafeAuditS * 1000f);
            int[] c = PatchCounters();
            sCsDone.Clear();
            foreach (KeyValuePair<ulong, long> kv in sCsWatch)
            {
                int at = (int)(kv.Value >> 2), kind = (int)(kv.Value & 3);
                if (unchecked(now - at) > auditMs) { sCsDone.Add(kv.Key); continue; }
                if (!World.IsObjectOnScreen(new ObjectGuid(kv.Key))) continue;
                c[kind == 1 ? kCsLate : kind == 3 ? kAlwLateSeen : kCsLateVetoed]++;  // kind 3: a Sim alibiwalk set at its tail (AlibiWalk.cs)
                sCsDone.Add(kv.Key);
            }
            foreach (ulong id in sCsDone) sCsWatch.Remove(id);
        }

        // ---- the shared API ----

        // The decision of one off-screen shortcut, made once at its start. hhOptIn: the fix's own `...hhunseen` switch (a
        // household Sim is only ever asked with it; the selected Sim never counts). obj: the object the Sim uses (or null);
        // spots: where the Sim will be (its end spot, a slot). NPC: TownieUnseen + obj off screen + spots away from every
        // viewer; household (opt-in): the Sim and obj off screen, Sim and spots away from the camera (other household Sims
        // are no viewers here, FarFromCamera).
        static bool CsDecide(Sim s, bool hhOptIn, IHasScriptProxy obj, params Vector3[] spots)
        {
            int[] c = PatchCounters();
            c[kCsDecides]++;
            if (s == null || s.HasBeenDestroyed || s == PlumbBob.SelectedActor) return false;
            bool npc = !IsPlayerSim(s);
            if (npc) { if (!TownieUnseen(s, null)) return false; }
            else if (!hhOptIn || UcOnScreen(s) || !FarFromCamera(s.Position)) return false;
            if (obj != null && UcOnScreen(obj)) return false;
            if (spots != null)
                foreach (Vector3 p in spots)
                    if (p != Vector3.Invalid && !(npc ? FarFromViewers(p) : FarFromCamera(p))) return false;
            c[kCsDecideUnseen]++;
            return true;
        }

        // Mid-shortcut: true when, with camsafe on, the Sim or its end spot came into view since the decision; the caller
        // then finishes into a valid end state at once (see the rule above). Counts a snap.
        static bool CsSnap(Sim s, Vector3 end)
        {
            if (!sCsOn || s == null) return false;
            try
            {
                bool npc = !IsPlayerSim(s);
                bool seen = UcOnScreen(s) || (npc ? !FarFromViewers(s.Position) : !FarFromCamera(s.Position))
                    || (end != Vector3.Invalid && (npc ? !FarFromViewers(end) : !FarFromCamera(end)));
                if (seen) PatchCounters()[kCsSnaps]++;
                return seen;
            }
            catch (ResetException) { throw; }
            catch (Exception e) { CsError(e); return true; }
        }

        // CamSafeCheck: "run" evaluates CamSafeCore with a scripted camera (read-only, no game state); "result" prints the
        // counters, the camera state and the last error.
        public static object CamSafeCheck(string what)
        {
            try
            {
                int[] c = PatchCounters();
                string counts = "asks " + c[kCsAsk] + ", old unseen " + c[kCsOldUnseen] + ", camsafe unseen " + c[kCsUnseen] + ", old unseen but camsafe seen " + c[kCsVeto]
                    + " (jump " + c[kCsVetoJump] + ", hold " + c[kCsVetoHold] + ", margin " + c[kCsVetoMargin] + "), spot vetoes " + c[kCsSpotVeto] + ", object holds " + c[kCsObjHold]
                    + ", jumps " + c[kCsJumps] + ", snaps " + c[kCsSnaps] + ", decides " + c[kCsDecides] + " (unseen " + c[kCsDecideUnseen] + "), watched " + c[kCsWatched]
                    + ", on screen soon after unseen " + c[kCsLate] + ", after a camsafe veto " + c[kCsLateVetoed] + ", speed max " + c[kCsSpeedMax] + " m/s, reach max " + c[kCsReachMax]
                    + " m, samples " + c[kCsSamples] + ", errors " + c[kCsErrors];
                string sw = "camsafe " + (ShortcutOn("camsafe") ? "on" : "off") + ", camsafelog " + (ShortcutOn("camsafelog") ? "on" : "off");
                if (what == "result")
                    return "camsafe result (" + sw + "): " + counts + "; now speed " + sCs.Speed.ToString("0.0") + " m/s, reach "
                        + (sCs.Reach == float.MaxValue ? "jump hold" : sCs.Reach.ToString("0")) + " m (old " + Math.Sqrt(sOldReach2).ToString("0") + " m)" + (sCsLastError.Length > 0 ? "; " + sCsLastError : "");
                bool ok = CamSafeCore.Verdict(false, true, false, false, false) == CamSafeCore.kUnseen
                    && CamSafeCore.Verdict(true, true, false, false, false) == CamSafeCore.kSeenOld
                    && CamSafeCore.Verdict(false, true, true, false, false) == CamSafeCore.kVetoJump
                    && CamSafeCore.Verdict(false, true, false, true, false) == CamSafeCore.kVetoHold
                    && CamSafeCore.Verdict(false, true, false, false, true) == CamSafeCore.kVetoMargin;
                CamSafeCore t = new CamSafeCore();
                t.Sample(0, 0, 0, 0, 0, 40, -30, 80);          // first sample: a jump
                bool jumpFirst = t.Jumping(100);
                t.Sample(5000, 0, 0, 0, 0, 40, -30, 80);       // settled, still camera
                bool settled = !t.Jumping(5000) && !t.Near(300, 0, 0) && t.Near(90, 0, 0);
                t.Sample(5100, 50, 0, 0, 50, 40, -30, 80);     // 50 m in 0.1 s: a jump
                bool jump = t.Jumping(5100) && t.Near(5000, 0, 0);
                bool verdict = ok && jumpFirst && settled && jump;
                return "camsafe check (" + sw + "): rules " + (verdict ? "ok" : "BAD") + "; verdict " + (ShortcutOn("camsafe") && verdict ? "FIXED" : "BROKEN") + "; " + counts;
            }
            catch (Exception e) { return "CamSafeCheck: " + e.GetType().Name + ": " + e.Message; }
        }
    }
}
