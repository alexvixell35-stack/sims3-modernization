using System;
using Sims3.Gameplay;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Interfaces;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // fglinbound (docs/682). The sim-thread frames of 350-460 ms whose stall leaf is World_FindGoodLocation
    // [fgl prof pairs of five storms, docs/682 §1]: GlobalFunctions.FindGoodLocationNearby's "indoors" stage, a lot-wide
    // native search whose managed check IsInside (light-blocking room id > 0) is asked only for a spot that already
    // passed the placement test. A guitar jig placed from a small room on a 64 x 64 lot: 3344 tiles tested, 196 spots
    // passed, all outdoors, all refused; 230-270 ms to learn there is no indoor spot.
    //
    // Switch on: the stage's search gets MaximumSearchRadius = the distance of the farthest tile of the lot that may be
    // indoors (room id > 0 on the start's level or the one above, or a diagonal wall on the tile) plus
    // FglInBoundCore.kMargin. The native search steps over tiles outside the radius without testing them [verified,
    // disassembly 0xB475C7: tile integer coordinates against the start, squared, radius <= 1e6] and its ring walk and
    // end are unchanged, so every spot IsInside can accept is still tested, in the same order: the same answer as EA's
    // stage, without the placement tests on the tiles that can only be outdoors. Before the tile pass a probe search
    // with kFglInBoundProbeRadius answers the common case (a spot a few tiles away) at EA's cost: its spot is taken
    // only when it lies on a ring no tile of which the radius can cut (FglInBoundCore.ProbeExact).
    //
    // Household and selected Sims (and an unknown task Sim) keep EA's stage unless fglinboundhh is on as well.
    // kFglInBoundShadow: EA's stage runs too, its answer is returned and a difference is counted and logged.
    // Counters 4250-4259. Lives in RrNearbyHook (RouteRelax.cs); fglnonescan / fglnnearspot, when on, take the call first.
    public static partial class Fixes
    {
        const int kFibStages = 4250, kFibProbe = 4251, kFibBounded = 4252, kFibNoGain = 4253, kFibExempt = 4254, kFibShadow = 4255, kFibShadowDiff = 4256,
            kFibErrors = 4257, kFibMs = 4258, kFibEaMs = 4259;
        static uint sFibUsRest, sFibEaUsRest;   // sub-millisecond remainders of the two ms counters
        static int sFibEaSlow;                  // switch off: FindGoodLocationNearby calls of 100 ms or more
        static string sFibLast = "";

        // the tiles of the lot that may be indoors for the search from `start`: union over the start's level and the one
        // above (the only levels the native search tests, 0xB47648). asked = native lookups made.
        static bool FibClass(ulong lot, LotLocation start, out byte[] cls, out int w, out int h, out int asked)
        {
            cls = null; w = h = 0; asked = 0;
            // the room grid's extent: the lookup answers -1 outside it (0xC05430, bounds checked)
            while (w < 128 && World.GetRoomId(lot, new LotLocation(w, start.mZ, start.mLevel, 0), eRoomDefinition.LightBlocking) >= 0) w++;
            while (h < 128 && World.GetRoomId(lot, new LotLocation(start.mX, h, start.mLevel, 0), eRoomDefinition.LightBlocking) >= 0) h++;
            asked = w + h + 2;
            if (w <= 0 || h <= 0 || w > 127 || h > 127 || start.mX < 0 || start.mZ < 0 || start.mX >= w || start.mZ >= h) return false;
            cls = new byte[w * h];
            for (int up = 0; up < 2; up++)
            {
                int lv = start.mLevel + up;
                if (lv > sbyte.MaxValue) break;
                sbyte level = (sbyte)lv;
                asked++;
                if (World.GetRoomId(lot, new LotLocation(start.mX, start.mZ, level, 0), eRoomDefinition.LightBlocking) < 0) continue;  // no such level
                for (int z = 0; z < h; z++)
                    for (int x = 0; x < w; x++)
                    {
                        int i = z * w + x;
                        if (cls[i] != 0) continue;
                        LotLocation t = new LotLocation(x, z, level, 0);
                        asked++;
                        int room = World.GetRoomId(lot, t, eRoomDefinition.LightBlocking);
                        if (room > 0) { cls[i] = 1; continue; }
                        // a tile cut by a diagonal wall has a room per part and the lookup above names one of them
                        asked++;
                        if (room == 0 && World.HasDiagonalWall(lot, t)) cls[i] = 2;
                    }
            }
            return true;
        }

        static bool FibSearch(ObjectGuid id, ObjectGuid[] ids, Vector3 p0, FindGoodLocationBooleans bools, float radius, out Vector3 position, out Vector3 forward)
        {
            // EA's indoors stage, word for word, plus the radius
            World.FindGoodLocationParams prm = new World.FindGoodLocationParams(p0, bools);
            prm.BooleanConstraints &= ~FindGoodLocationBooleans.StayInRoom;
            prm.RequiredRoomID = -1;
            prm.StartPosition = p0;
            prm.PlacementCheckFunc = GlobalFunctions.IsInside;
            prm.MaximumSearchRadius = radius;
            return World.FindGoodLocation(id, prm, ids, out position, out forward);
        }

        static void FibAddMs(int slot, ref uint rest, uint us)
        {
            rest += us;
            PatchCounters()[slot] += (int)(rest / 1000u);
            rest %= 1000u;
        }

        // The indoors stage by our path. Returns 1 found (position / forward set), 0 nothing found, -1 run EA's stage
        // (nothing was changed). Called only when EA would run the stage: lot != 0, start room > 0, Indoors asked for.
        static int FibStage(IGameObject obj, ObjectGuid[] ids, Vector3 p0, Vector3 f0, float offset, FindGoodLocationBooleans bools, ulong lot, LotLocation loc,
            out Vector3 position, out Vector3 forward)
        {
            int[] c = PatchCounters();
            position = p0; forward = f0;
            uint t0 = SrcNowUs();
            ObjectGuid id = obj.ObjectId;
            c[kFibStages]++;
            bool found = false, done = false;
            string how = "";
            uint probeUs = 0, boundUs = 0, scanUs = 0;
            float probeR = Instantiator.kFglInBoundProbeRadius;
            if (probeR >= 1f && probeR <= 64f)
            {
                found = FibSearch(id, ids, p0, bools, probeR, out position, out forward);
                probeUs = unchecked(SrcNowUs() - t0);
                if (found)
                {
                    LotLocation at = LotLocation.Invalid;
                    ulong lot2 = World.GetLotLocation(position, ref at);
                    int ring = lot2 == lot ? Math.Max(Math.Abs(at.mX - loc.mX), Math.Abs(at.mZ - loc.mZ)) : -1;
                    if (FglInBoundCore.ProbeExact(ring, probeR)) { done = true; c[kFibProbe]++; how = "probe spot on ring " + ring; }
                    else found = false;   // a nearer ring may hold a spot the radius cut: the bounded search decides
                }
            }
            if (!done)
            {
                uint t1 = SrcNowUs();
                byte[] cls;
                int w, h, asked, marked = 0, within = 0;
                float radius = 0f;
                int rc = FibClass(lot, loc, out cls, out w, out h, out asked) ? FglInBoundCore.Bound(cls, w, h, loc.mX, loc.mZ, out radius, out marked, out within) : FglInBoundCore.kBadStart;
                boundUs = unchecked(SrcNowUs() - t1);
                if (rc != FglInBoundCore.kOk)
                {
                    c[rc == FglInBoundCore.kNoGain ? kFibNoGain : kFibErrors]++;
                    FibAddMs(kFibMs, ref sFibUsRest, unchecked(SrcNowUs() - t0));
                    sFibLast = "last: " + (rc == FglInBoundCore.kNoGain ? "no gain" : "start tile not indoors in our tile pass") + ", lot " + w + " x " + h + ", " + marked + " tiles may be indoors";
                    position = p0; forward = f0;
                    return -1;
                }
                uint t2 = SrcNowUs();
                found = FibSearch(id, ids, p0, bools, radius, out position, out forward);
                scanUs = unchecked(SrcNowUs() - t2);
                c[kFibBounded]++;
                how = "lot " + w + " x " + h + ", " + marked + " tiles may be indoors (" + asked + " lookups), radius " + radius.ToString("0.0") + " m = " + within + " tiles";
            }
            uint us = unchecked(SrcNowUs() - t0);
            FibAddMs(kFibMs, ref sFibUsRest, us);
            string line = (us / 1000) + " ms (probe " + (probeUs / 1000) + ", tile pass " + (boundUs / 1000) + ", search " + (scanUs / 1000) + "), " + obj.GetType().Name
                + ", " + how + ", " + (found ? "found" : "no indoor spot");
            sFibLast = "last: " + line;
            if (Instantiator.kFglInBoundShadow)
            {
                Vector3 ep = p0, ef = f0;
                uint t3 = SrcNowUs();
                bool ea = RrNearbyHook.NearbyOrig(obj, ids, ref ep, ref ef, offset, (GlobalFunctions.FindGoodLocationStrategies)RouteRelaxCore.kIndoors, bools);
                uint eaUs = unchecked(SrcNowUs() - t3);
                FibAddMs(kFibEaMs, ref sFibEaUsRest, eaUs);
                c[kFibShadow]++;
                bool same = ea == found && (!ea || (Math.Abs(ep.x - position.x) < 0.05f && Math.Abs(ep.z - position.z) < 0.05f && Math.Abs(ep.y - position.y) < 0.5f));
                if (!same) c[kFibShadowDiff]++;
                HcLine("fglinbound: shadow " + (same ? "same" : "DIFFERENT") + ", ours " + line + (found ? " at " + FibPos(position) : "")
                    + "; EA " + (eaUs / 1000) + " ms " + (ea ? "found at " + FibPos(ep) : "no indoor spot"));
                position = ep; forward = ef;
                return ea ? 1 : 0;
            }
            if (us >= 20000) HcLine("fglinbound: " + line);
            return found ? 1 : 0;
        }

        static string FibPos(Vector3 p) { return p.x.ToString("0.00") + "," + p.y.ToString("0.00") + "," + p.z.ToString("0.00"); }

        // RrNearbyHook's branch. Returns 1 / 0 = the call's answer, -1 = not ours (the caller runs EA's body unchanged).
        static int FibNearby(IGameObject obj, ObjectGuid[] ids, ref Vector3 position, ref Vector3 forward, float offset, int s, FindGoodLocationBooleans bools)
        {
            if ((s & RouteRelaxCore.kIndoors) == 0) return -1;
            Vector3 p0 = position, f0 = forward;
            LotLocation loc = LotLocation.Invalid;
            ulong lot = World.GetLotLocation(p0, ref loc);
            if (lot == 0 || World.GetRoomId(lot, loc, eRoomDefinition.LightBlocking) <= 0) return -1;   // EA runs no indoors stage
            if (HouseholdExempt(RrTaskSim()) && !ShortcutOn("fglinboundhh")) { PatchCounters()[kFibExempt]++; return -1; }
            // the probes at the position, the same room and the adjoining rooms: EA's own body, those stages only
            int early = s & RouteRelaxCore.kEarly;
            if (early != 0 && RrNearbyHook.NearbyOrig(obj, ids, ref position, ref forward, offset, (GlobalFunctions.FindGoodLocationStrategies)early, bools)) return 1;
            int rest = s & ~RouteRelaxCore.kEarly;
            int res;
            try { res = FibStage(obj, ids, p0, f0, offset, bools, lot, loc, out position, out forward); }
            catch (ResetException) { throw; }
            catch (Exception) { PatchCounters()[kFibErrors]++; res = -1; }
            if (res == 1) return 1;
            if (res == 0) rest &= ~RouteRelaxCore.kIndoors;   // our stage ran and found nothing: the later stages are EA's
            if (rest == 0) return 0;
            position = p0; forward = f0;
            return RrNearbyHook.NearbyOrig(obj, ids, ref position, ref forward, offset, (GlobalFunctions.FindGoodLocationStrategies)rest, bools) ? 1 : 0;
        }

        // switch off: one line for a slow call, so a storm names the caller's object without [FglProf]
        static void FibEaSlow(IGameObject obj, uint us, bool found)
        {
            sFibEaSlow++;
            HcLine("fglinbound: EA's FindGoodLocationNearby " + (us / 1000) + " ms (switch off), " + (obj != null ? obj.GetType().Name : "null") + ", " + (found ? "found" : "nothing found"));
        }

        static string FibCounters()
        {
            int[] c = PatchCounters();
            return "fglinbound: indoors stages by our path " + kFibStages + "=" + c[kFibStages] + ", answered by the probe " + kFibProbe + "=" + c[kFibProbe]
                + ", bounded searches " + kFibBounded + "=" + c[kFibBounded] + ", left to EA: radius covers the lot " + kFibNoGain + "=" + c[kFibNoGain]
                + ", household or unknown Sim " + kFibExempt + "=" + c[kFibExempt] + ", shadow compares " + kFibShadow + "=" + c[kFibShadow]
                + ", shadow different " + kFibShadowDiff + "=" + c[kFibShadowDiff] + ", errors or start not indoors " + kFibErrors + "=" + c[kFibErrors]
                + ", our ms " + kFibMs + "=" + c[kFibMs] + ", EA's stage ms in shadow " + kFibEaMs + "=" + c[kFibEaMs]
                + ", slow EA calls with the switch off " + sFibEaSlow + "; " + sFibLast + "; " + GlMembers(CkLive("fglinbound"));
        }

        // FglInBoundCheck run: the logged shape (a 3 x 3 room on a 64 x 64 lot, 196 free spots, all outdoors) through the
        // model of EA's search and through the bounded one, the controls, and the tile pass on the selected Sim's lot.
        public static object FglInBoundCheck(string what)
        {
            if (what == "result") return FibCounters();
            if (what != "run") return "fglinbound <run|result>";
            bool on = ShortcutOn("fglinbound");
            const int w = 64, h = 64, x0 = 31, z0 = 54;
            byte[] cls = new byte[w * h];
            bool[] pass = new bool[w * h], inside = new bool[w * h];
            for (int z = z0 - 1; z <= z0 + 1; z++) for (int x = x0 - 1; x <= x0 + 1; x++) { cls[z * w + x] = 1; inside[z * w + x] = true; }
            for (int i = 0; i < 196; i++) pass[(8 + i / 14 * 3) * w + 6 + i % 14 * 4] = true;   // free spots, outdoors
            float radius;
            int marked, within, eaTested, ourTested;
            int rc = FglInBoundCore.Bound(cls, w, h, x0, z0, out radius, out marked, out within);
            int ea = FglInBoundCore.ModelScan(w, h, x0 + 0.1f, z0 + 0.6f, pass, inside, 10000000f, out eaTested);
            int mine = FglInBoundCore.ModelScan(w, h, x0 + 0.1f, z0 + 0.6f, pass, inside, rc == FglInBoundCore.kOk ? radius : 10000000f, out ourTested);
            bool shape = rc == FglInBoundCore.kOk && ea == -1 && mine == -1 && marked == 9 && ourTested * 8 < eaTested && eaTested == w * h;
            // controls: a second building with a free indoor spot is still found, the same tile as EA; a lot that is all
            // rooms is left to EA; a start that is not indoors is refused; the probe's ring rule
            int far = 10 * w + 50;
            cls[far] = 1; inside[far] = true; pass[far] = true;
            float r2;
            int m2, w2, t;
            bool controls = FglInBoundCore.Bound(cls, w, h, x0, z0, out r2, out m2, out w2) == FglInBoundCore.kOk
                && FglInBoundCore.ModelScan(w, h, x0 + 0.9f, z0 + 0.9f, pass, inside, r2, out t) == far
                && FglInBoundCore.ModelScan(w, h, x0 + 0.9f, z0 + 0.9f, pass, inside, 10000000f, out t) == far;
            byte[] all = new byte[16];
            for (int i = 0; i < 16; i++) all[i] = 1;
            controls &= FglInBoundCore.Bound(all, 4, 4, 1, 1, out r2, out m2, out w2) == FglInBoundCore.kNoGain;
            all[5] = 0;
            controls &= FglInBoundCore.Bound(all, 4, 4, 1, 1, out r2, out m2, out w2) == FglInBoundCore.kBadStart
                && FglInBoundCore.ProbeExact(3, 7f) && !FglInBoundCore.ProbeExact(4, 7f) && !FglInBoundCore.ProbeExact(-1, 7f);
            // live: the tile pass where the selected Sim stands (read only)
            string live = "live: no selected Sim";
            try
            {
                Sim a = Sim.ActiveActor;
                if (a != null)
                {
                    LotLocation loc = LotLocation.Invalid;
                    ulong lot = World.GetLotLocation(a.Position, ref loc);
                    byte[] lc = null;
                    int lw = 0, lh = 0, asked = 0, lm = 0, lin = 0;
                    float lr = 0f;
                    uint t0 = SrcNowUs();
                    bool ok = false;
                    if (lot != 0) ok = FibClass(lot, loc, out lc, out lw, out lh, out asked);
                    int lrc = ok ? FglInBoundCore.Bound(lc, lw, lh, loc.mX, loc.mZ, out lr, out lm, out lin) : -9;
                    live = "live: selected Sim's lot " + lw + " x " + lh + ", tile " + loc.mX + "," + loc.mZ + " level " + loc.mLevel + ", start room "
                        + World.GetRoomId(lot, loc, eRoomDefinition.LightBlocking) + ", " + lm + " tiles may be indoors, " + asked + " lookups in "
                        + (unchecked(SrcNowUs() - t0) / 1000) + " ms, bound " + lrc + " (1 ok, 0 covers the lot, -1 start not indoors), radius " + lr.ToString("0.0") + " = " + lin + " tiles";
                }
            }
            catch (ResetException) { throw; }
            catch (Exception e) { live = "live: threw " + e.GetType().Name; }
            bool fixedNow = on && shape;
            return B582Line("fglinbound", on, "room of 9 tiles, 196 free outdoor spots: " + (on ? ourTested : eaTested) + " of " + (w * h) + " tiles tested for 'no indoor spot'", fixedNow,
                "EA's search tests the whole lot (" + (eaTested == w * h) + "), a far indoor spot is still the one returned, a lot of rooms and a start outdoors are left to EA, probe ring rule",
                controls && eaTested == w * h, sRrNearbyReady ? "" : "FindGoodLocationNearby swap", FibCounters() + "; " + live);
        }
    }
}
