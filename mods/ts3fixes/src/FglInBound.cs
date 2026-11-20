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
    // passed, all outdoors, all refused; 230-270 ms to learn there is no indoor spot. The lot has 283 tiles that may be
    // indoors, spread over it (s208), so one radius around the start covers the lot (design 1, no gain).
    //
    // Switch on (design 2, docs/682 §8): a tile pass marks the tiles that may be indoors (room id > 0 on the start's
    // level or the one above, or a diagonal wall); their 8-connected groups become at most kFglInBoundMaxSearches
    // clusters; per cluster one native search with EA's object and constraints, started on the cluster's centre tile
    // with MaximumSearchRadius = farthest member + 1.5. The native search steps over tiles outside the radius without
    // testing them [verified, disassembly 0xB475C7], so each search tests the cluster's tiles and a rim. Its check
    // accepts nothing and keeps, of the spots that passed the placement test and are indoors, the one on the nearest
    // square ring around the Sim's own start (EA's search walks those rings), then the nearest. Clusters are taken
    // nearest first and left out once the kept spot's ring cannot be beaten. The kept spot is returned through a last
    // search that starts on it and accepts only it. Every tile IsInside can accept is in a cluster, so an indoor spot
    // EA finds is found; the spot is on the same ring as EA's and can be another tile of that ring.
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
        static int sFibSearches, sFibSameRing, sFibConfirmFail;   // cluster searches run / shadow: EA's ring, another tile / kept spot not returned again

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

        static bool FibSearch(ObjectGuid id, ObjectGuid[] ids, Vector3 start, FindGoodLocationBooleans bools, float radius, ValidatePlacementDelegate check,
            out Vector3 position, out Vector3 forward)
        {
            // EA's indoors stage, word for word, plus the start, the radius and the check
            World.FindGoodLocationParams prm = new World.FindGoodLocationParams(start, bools);
            prm.BooleanConstraints &= ~FindGoodLocationBooleans.StayInRoom;
            prm.RequiredRoomID = -1;
            prm.StartPosition = start;
            prm.PlacementCheckFunc = check;
            prm.MaximumSearchRadius = radius;
            return World.FindGoodLocation(id, prm, ids, out position, out forward);
        }

        static void FibAddMs(int slot, ref uint rest, uint us)
        {
            rest += us;
            PatchCounters()[slot] += (int)(rest / 1000u);
            rest %= 1000u;
        }

        // the check of a cluster search: accepts nothing, keeps the best indoor spot offered (IsInside's own test)
        sealed class FibRec
        {
            public ulong Lot; public int X0, Z0;
            public int Ring = int.MaxValue, Offers, Indoor;
            public float D2 = float.MaxValue, X, Y, Z;
            public bool Check(float x, float y, float z, float absAngle)
            {
                Offers++;
                LotLocation l = LotLocation.Invalid;
                ulong lot = World.GetLotLocation(new Vector3(x, y, z), ref l);
                if (World.GetRoomId(lot, l, eRoomDefinition.LightBlocking) <= 0) return false;
                Indoor++;
                if (lot != Lot) return false;
                int ring = FglInBoundCore.Ring(l.mX, l.mZ, X0, Z0);
                float dx = l.mX - X0, dz = l.mZ - Z0;
                if (FglInBoundCore.Better(ring, dx * dx + dz * dz, Ring, D2)) { Ring = ring; D2 = dx * dx + dz * dz; X = x; Y = y; Z = z; }
                return false;
            }
        }

        // the last search's check: only the kept spot (and still indoors)
        sealed class FibOnly
        {
            public float X, Z;
            public bool Check(float x, float y, float z, float absAngle)
            {
                return Math.Abs(x - X) < 0.05f && Math.Abs(z - Z) < 0.05f && GlobalFunctions.IsInside(x, y, z, absAngle);
            }
        }

        // Our indoors stage. Returns 1 found (position / forward set), 0 no indoor spot, -1 not ours (EA's stage must run).
        static int FibOurs(IGameObject obj, ObjectGuid[] ids, Vector3 p0, Vector3 f0, FindGoodLocationBooleans bools, ulong lot, LotLocation loc,
            out Vector3 position, out Vector3 forward, out string line, out int ring)
        {
            int[] c = PatchCounters();
            position = p0; forward = f0; ring = -1;
            uint t0 = SrcNowUs();
            ObjectGuid id = obj.ObjectId;
            uint probeUs = 0;
            float probeR = Instantiator.kFglInBoundProbeRadius;
            if (probeR >= 1f && probeR <= 64f)
            {
                bool hit = FibSearch(id, ids, p0, bools, probeR, GlobalFunctions.IsInside, out position, out forward);
                probeUs = unchecked(SrcNowUs() - t0);
                if (hit)
                {
                    LotLocation at = LotLocation.Invalid;
                    ulong lot2 = World.GetLotLocation(position, ref at);
                    ring = lot2 == lot ? FglInBoundCore.Ring(at.mX, at.mZ, loc.mX, loc.mZ) : -1;
                    if (FglInBoundCore.ProbeExact(ring, probeR))
                    {
                        c[kFibProbe]++;
                        uint pus = unchecked(SrcNowUs() - t0);
                        FibAddMs(kFibMs, ref sFibUsRest, pus);
                        line = (pus / 1000) + " ms, " + obj.GetType().Name + ", probe spot on ring " + ring + ", found";
                        return 1;
                    }
                    position = p0; forward = f0; ring = -1;   // a nearer ring may hold a spot the radius cut: the clusters decide
                }
            }
            uint t1 = SrcNowUs();
            byte[] cls;
            int w, h, asked, marked, cost;
            FglInBoundCore.Cluster[] list = null;
            int rc = FglInBoundCore.kBadStart;
            marked = cost = 0;
            if (FibClass(lot, loc, out cls, out w, out h, out asked))
                rc = FglInBoundCore.Clusters(cls, w, h, loc.mX, loc.mZ, Math.Max(1, Instantiator.kFglInBoundMaxSearches), out list, out marked, out cost);
            uint passUs = unchecked(SrcNowUs() - t1);
            if (rc != FglInBoundCore.kOk)
            {
                c[rc == FglInBoundCore.kNoGain ? kFibNoGain : kFibErrors]++;
                FibAddMs(kFibMs, ref sFibUsRest, unchecked(SrcNowUs() - t0));
                line = (rc == FglInBoundCore.kNoGain ? "no gain" : "start tile not indoors in our tile pass") + ", lot " + w + " x " + h + ", " + marked
                    + " tiles may be indoors in " + (list != null ? list.Length : 0) + " clusters, " + cost + " tiles to test";
                return -1;
            }
            uint t2 = SrcNowUs();
            FibRec rec = new FibRec();
            rec.Lot = lot; rec.X0 = loc.mX; rec.Z0 = loc.mZ;
            int searches = 0, tiles = 0;
            for (int i = 0; i < list.Length; i++)
            {
                FglInBoundCore.Cluster k = list[i];
                if (FglInBoundCore.Skip(k.MinRing, rec.Ring)) break;
                // the cluster's centre tile on the start's level, as a world position
                Vector3 start = p0;
                World.GetWorldPosition(lot, new LotLocation(k.Cx, k.Cz, loc.mLevel, 0), ref start);
                LotLocation at = LotLocation.Invalid;
                ulong lot2 = World.GetLotLocation(start, ref at);
                if (lot2 != lot || at.mLevel != loc.mLevel || Math.Abs(at.mX - k.Cx) > 1 || Math.Abs(at.mZ - k.Cz) > 1)
                {
                    c[kFibErrors]++;
                    FibAddMs(kFibMs, ref sFibUsRest, unchecked(SrcNowUs() - t0));
                    line = "cluster centre " + k.Cx + "," + k.Cz + " came back as tile " + at.mX + "," + at.mZ + " level " + at.mLevel + ": EA's stage";
                    position = p0; forward = f0;
                    return -1;
                }
                float radius = FglInBoundCore.RadiusFor(k.Members, w, at.mX, at.mZ, FglInBoundCore.kClusterMargin);
                Vector3 np, nf;
                FibSearch(id, ids, start, bools, radius, rec.Check, out np, out nf);
                searches++; tiles += FglInBoundCore.CostFor(w, h, at.mX, at.mZ, radius);
            }
            sFibSearches += searches;
            uint scanUs = unchecked(SrcNowUs() - t2);
            int res = 0;
            string end = "no indoor spot";
            if (rec.Ring != int.MaxValue)
            {
                FibOnly only = new FibOnly();
                only.X = rec.X; only.Z = rec.Z;
                if (FibSearch(id, ids, new Vector3(rec.X, rec.Y, rec.Z), bools, 2f, only.Check, out position, out forward)) { res = 1; ring = rec.Ring; end = "found on ring " + rec.Ring; }
                else { sFibConfirmFail++; c[kFibErrors]++; res = -1; position = p0; forward = f0; end = "the kept spot was not returned again: EA's stage"; }
            }
            if (res >= 0) c[kFibBounded]++;
            uint us = unchecked(SrcNowUs() - t0);
            FibAddMs(kFibMs, ref sFibUsRest, us);
            line = (us / 1000) + " ms (probe " + (probeUs / 1000) + ", tile pass " + (passUs / 1000) + ", searches " + (scanUs / 1000) + "), " + obj.GetType().Name
                + ", lot " + w + " x " + h + ", " + marked + " tiles may be indoors (" + asked + " lookups) in " + list.Length + " clusters, " + searches + " searches over "
                + tiles + " tiles, " + rec.Offers + " spots passed (" + rec.Indoor + " indoors), " + end;
            return res;
        }

        // 0 same spot, 1 EA's ring but another tile, 2 different (found or not, or another ring)
        static int FibCompare(bool ea, Vector3 ep, int found, Vector3 mp, int ring, ulong lot, LotLocation loc)
        {
            if (ea != (found == 1)) return 2;
            if (!ea) return 0;
            if (Math.Abs(ep.x - mp.x) < 0.05f && Math.Abs(ep.z - mp.z) < 0.05f && Math.Abs(ep.y - mp.y) < 0.5f) return 0;
            LotLocation at = LotLocation.Invalid;
            ulong lot2 = World.GetLotLocation(ep, ref at);
            return lot2 == lot && FglInBoundCore.Ring(at.mX, at.mZ, loc.mX, loc.mZ) == ring ? 1 : 2;
        }

        static string FibVerdict(int cmp) { return cmp == 0 ? "same" : cmp == 1 ? "same ring, other tile" : "DIFFERENT"; }

        // The indoors stage by our path. Returns 1 found (position / forward set), 0 nothing found, -1 run EA's stage
        // (nothing was changed). Called only when EA would run the stage: lot != 0, start room > 0, Indoors asked for.
        static int FibStage(IGameObject obj, ObjectGuid[] ids, Vector3 p0, Vector3 f0, float offset, FindGoodLocationBooleans bools, ulong lot, LotLocation loc,
            out Vector3 position, out Vector3 forward)
        {
            int[] c = PatchCounters();
            c[kFibStages]++;
            string line;
            int ring;
            uint t0 = SrcNowUs();
            int res = FibOurs(obj, ids, p0, f0, bools, lot, loc, out position, out forward, out line, out ring);
            uint us = unchecked(SrcNowUs() - t0);
            sFibLast = "last: " + line;
            if (res < 0) { HcLine("fglinbound: left to EA: " + line); return -1; }
            if (Instantiator.kFglInBoundShadow)
            {
                Vector3 ep = p0, ef = f0;
                uint t3 = SrcNowUs();
                bool ea = RrNearbyHook.NearbyOrig(obj, ids, ref ep, ref ef, offset, (GlobalFunctions.FindGoodLocationStrategies)RouteRelaxCore.kIndoors, bools);
                uint eaUs = unchecked(SrcNowUs() - t3);
                FibAddMs(kFibEaMs, ref sFibEaUsRest, eaUs);
                c[kFibShadow]++;
                int cmp = FibCompare(ea, ep, res, position, ring, lot, loc);
                if (cmp == 1) sFibSameRing++;
                if (cmp == 2) c[kFibShadowDiff]++;
                HcLine("fglinbound: shadow " + FibVerdict(cmp) + ", ours " + line + (res == 1 ? " at " + FibPos(position) : "")
                    + "; EA " + (eaUs / 1000) + " ms " + (ea ? "found at " + FibPos(ep) : "no indoor spot"));
                position = ep; forward = ef;
                return ea ? 1 : 0;
            }
            if (us >= 20000) HcLine("fglinbound: " + line);
            return res;
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
                + ", answered by the cluster searches " + kFibBounded + "=" + c[kFibBounded] + " (" + sFibSearches + " searches), left to EA: no gain " + kFibNoGain + "=" + c[kFibNoGain]
                + ", household or unknown Sim " + kFibExempt + "=" + c[kFibExempt] + ", shadow compares " + kFibShadow + "=" + c[kFibShadow]
                + " (EA's ring, another tile " + sFibSameRing + "), shadow different " + kFibShadowDiff + "=" + c[kFibShadowDiff]
                + ", errors " + kFibErrors + "=" + c[kFibErrors] + " (kept spot not returned again " + sFibConfirmFail + ")"
                + ", our ms " + kFibMs + "=" + c[kFibMs] + ", EA's stage ms in shadow " + kFibEaMs + "=" + c[kFibEaMs]
                + ", slow EA calls with the switch off " + sFibEaSlow + "; " + sFibLast + "; " + GlMembers(CkLive("fglinbound"));
        }

        // FglInBoundCheck live: the real searches, no Sim needed. A GuitarJig (the logged object) is created on up to
        // three indoor tiles of the selected Sim's lot (the Sim's own tile first when it is indoors), EA's indoors stage
        // and ours run from there, the answers are compared (slots 4255 / 4256 as in shadow), the jig is destroyed.
        // Nothing is placed or moved: both paths only answer with a position.
        // live: from the selected Sim's tile. live:big: from the centre of the largest lot with indoor tiles (the logged
        // 64 x 64 shape), so the slow case is checked without a Sim there; one tile pass per lot, read-only.
        static string FibLive(bool big)
        {
            if (!sRrNearbyReady) return "live: FindGoodLocationNearby swap missing";
            Vector3 at0;
            if (!big)
            {
                Sim a = Sim.ActiveActor;
                if (a == null) return "live: no selected Sim";
                at0 = a.Position;
            }
            else
            {
                at0 = Vector3.Invalid;
                int best = 0;
                foreach (Sims3.Gameplay.Core.Lot l in Sims3.Gameplay.Core.LotManager.AllLots)
                {
                    if (l == null || l.IsWorldLot) continue;
                    Vector3 cp = l.GetCenterPosition();
                    LotLocation ll = LotLocation.Invalid;
                    ulong id = World.GetLotLocation(cp, ref ll);
                    byte[] lc; int lw, lh, la;
                    if (id == 0 || !FibClass(id, ll, out lc, out lw, out lh, out la)) continue;
                    int any = 0;
                    for (int i = 0; i < lc.Length && any == 0; i++) if (lc[i] == 1) any = 1;
                    if (any == 1 && lw * lh > best) { best = lw * lh; at0 = cp; }
                }
                if (best == 0) return "live:big: no lot with indoor tiles";
            }
            int[] c = PatchCounters();
            LotLocation loc = LotLocation.Invalid;
            ulong lot = World.GetLotLocation(at0, ref loc);
            byte[] cls;
            int w, h, asked;
            if (lot == 0 || !FibClass(lot, loc, out cls, out w, out h, out asked)) return "live: no lot grid at the start";
            System.Collections.Generic.List<Vector3> starts = new System.Collections.Generic.List<Vector3>();
            if (World.GetRoomId(lot, loc, eRoomDefinition.LightBlocking) > 0) starts.Add(at0);
            int marked = 0;
            for (int i = 0; i < cls.Length; i++) if (cls[i] == 1) marked++;
            int[] want = new int[] { 0, marked / 2, marked - 1 };
            for (int k = 0; k < want.Length && starts.Count < 3 && marked > 0; k++)
            {
                int seen = 0;
                for (int i = 0; i < cls.Length; i++)
                {
                    if (cls[i] != 1 || seen++ != want[k]) continue;
                    Vector3 p = at0;
                    World.GetWorldPosition(lot, new LotLocation(i % w, i / w, loc.mLevel, 0), ref p);
                    LotLocation at = LotLocation.Invalid;
                    if (World.GetLotLocation(p, ref at) == lot && World.GetRoomId(lot, at, eRoomDefinition.LightBlocking) > 0) starts.Add(p);
                    break;
                }
            }
            if (starts.Count == 0) return "live: lot " + w + " x " + h + " has no indoor tile on the start's level";
            FindGoodLocationBooleans bools = FindGoodLocationBooleans.PreferEmptyTiles | FindGoodLocationBooleans.Routable;
            System.Text.StringBuilder b = new System.Text.StringBuilder("live: lot " + w + " x " + h + ", " + marked + " indoor tiles");
            int worst = 0;
            foreach (Vector3 p0 in starts)
            {
                LotLocation sl = LotLocation.Invalid;
                World.GetLotLocation(p0, ref sl);
                IGameObject jig = GlobalFunctions.CreateObject("GuitarJig", p0, sl.mLevel, Vector3.UnitZ);
                if (jig == null) return b.Append("; GuitarJig could not be created").ToString();
                try
                {
                    Vector3 ep = p0, ef = Vector3.UnitZ, mp, mf;
                    uint t0 = SrcNowUs();
                    bool ea = RrNearbyHook.NearbyOrig(jig, null, ref ep, ref ef, 0f, (GlobalFunctions.FindGoodLocationStrategies)RouteRelaxCore.kIndoors, bools);
                    uint t1 = SrcNowUs();
                    string line;
                    int ring;
                    int res = FibOurs(jig, null, p0, Vector3.UnitZ, bools, lot, sl, out mp, out mf, out line, out ring);
                    uint t2 = SrcNowUs();
                    int cmp = res < 0 ? -1 : FibCompare(ea, ep, res, mp, ring, lot, sl);
                    if (res >= 0)
                    {
                        c[kFibShadow]++;
                        if (cmp == 1) sFibSameRing++;
                        if (cmp == 2) c[kFibShadowDiff]++;
                        if (cmp > worst) worst = cmp;
                    }
                    b.Append("; tile " + sl.mX + "," + sl.mZ + ": EA " + (unchecked(t1 - t0) / 1000) + " ms " + (ea ? "found at " + FibPos(ep) : "no indoor spot") + ", ours "
                        + (unchecked(t2 - t1) / 1000) + " ms " + (res == 1 ? "at " + FibPos(mp) : "") + " [" + line + "] -> " + (res < 0 ? "left to EA" : FibVerdict(cmp)));
                }
                finally { jig.Destroy(); }
            }
            return b.Append(worst == 2 ? "; live verdict: DIFFERENT" : "; live verdict: ok").ToString();
        }

        // FglInBoundCheck run: the logged shape (a 3 x 3 room and four other buildings on a 64 x 64 lot, 196 free spots,
        // all outdoors) through the model of EA's search and through the cluster searches, and the controls.
        // FglInBoundCheck live: FibLive.
        public static object FglInBoundCheck(string what)
        {
            if (what == "result") return FibCounters();
            if (what == "live" || what == "live:big")
            {
                try { return "fglinbound " + FibLive(what == "live:big"); }
                catch (ResetException) { throw; }
                catch (Exception e) { return "fglinbound live: threw " + e.GetType().Name + " " + e.Message; }
            }
            if (what != "run") return "fglinbound <run|live|live:big|result>";
            bool on = ShortcutOn("fglinbound");
            const int w = 64, h = 64, x0 = 31, z0 = 54;
            byte[] cls = new byte[w * h];
            bool[] pass = new bool[w * h], inside = new bool[w * h];
            int[] bx = new int[] { x0 - 1, 4, 50, 8, 44 }, bz = new int[] { z0 - 1, 4, 6, 40, 30 }, bs = new int[] { 3, 8, 7, 6, 9 };
            for (int k = 0; k < bx.Length; k++)
                for (int z = bz[k]; z < bz[k] + bs[k]; z++) for (int x = bx[k]; x < bx[k] + bs[k]; x++) { cls[z * w + x] = 1; inside[z * w + x] = true; }
            for (int i = 0; i < 196; i++) { int t = (14 + i / 14 * 3) * w + 16 + i % 14 * 2; if (!inside[t]) pass[t] = true; }   // free spots, outdoors
            FglInBoundCore.Cluster[] list;
            int marked, cost, eaTested, ourTested, searches;
            int rc = FglInBoundCore.Clusters(cls, w, h, x0, z0, 8, out list, out marked, out cost);
            int ea = FglInBoundCore.ModelScan(w, h, x0 + 0.1f, z0 + 0.6f, pass, inside, 10000000f, out eaTested);
            int mine = -2;
            ourTested = eaTested; searches = 0;
            if (rc == FglInBoundCore.kOk) mine = FglInBoundCore.ModelClusters(list, w, h, 0.3f, 0.7f, FglInBoundCore.kClusterMargin, pass, inside, x0, z0, out ourTested, out searches);
            bool shape = rc == FglInBoundCore.kOk && ea == -1 && mine == -1 && list.Length == 5 && ourTested * 3 < eaTested && eaTested == w * h;
            // controls: a free indoor spot in a far building is found, EA's tile; one on the start's ring too; a lot of
            // rooms and a start outdoors are left to EA
            int far = 8 * w + 53, t2, s2;
            pass[far] = true;
            bool controls = FglInBoundCore.ModelClusters(list, w, h, 0.9f, 0.1f, FglInBoundCore.kClusterMargin, pass, inside, x0, z0, out t2, out s2) == far
                && FglInBoundCore.ModelScan(w, h, x0 + 0.9f, z0 + 0.9f, pass, inside, 10000000f, out t2) == far;
            int near = (z0 + 1) * w + x0;
            pass[near] = true;
            controls &= FglInBoundCore.ModelClusters(list, w, h, 0.5f, 0.5f, FglInBoundCore.kClusterMargin, pass, inside, x0, z0, out t2, out s2) == near && s2 == 1;
            byte[] all = new byte[16];
            for (int i = 0; i < 16; i++) all[i] = 1;
            FglInBoundCore.Cluster[] l2;
            int m2, c2;
            controls &= FglInBoundCore.Clusters(all, 4, 4, 1, 1, 8, out l2, out m2, out c2) == FglInBoundCore.kNoGain;
            all[5] = 0;
            controls &= FglInBoundCore.Clusters(all, 4, 4, 1, 1, 8, out l2, out m2, out c2) == FglInBoundCore.kBadStart
                && FglInBoundCore.ProbeExact(3, 7f) && !FglInBoundCore.ProbeExact(4, 7f) && !FglInBoundCore.ProbeExact(-1, 7f);
            bool fixedNow = on && shape;
            return B582Line("fglinbound", on, "five buildings, 196 free outdoor spots: " + (on ? ourTested : eaTested) + " of " + (w * h) + " tiles tested for 'no indoor spot'"
                + (on ? " in " + searches + " searches" : ""), fixedNow,
                "EA's search tests the whole lot (" + (eaTested == w * h) + "), a far indoor spot is still the one returned, a near one ends after one search, a lot of rooms and a start outdoors are left to EA",
                controls && eaTested == w * h, sRrNearbyReady ? "" : "FindGoodLocationNearby swap", FibCounters());
        }
    }
}
