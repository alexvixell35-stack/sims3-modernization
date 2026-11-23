using System;
using Sims3.Gameplay;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Interfaces;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // fglinbound (docs/682 §9, design 3). The sim-thread frames of 350-460 ms whose stall leaf is World_FindGoodLocation
    // [fgl prof pairs of five storms, s208, s222]: GlobalFunctions.FindGoodLocationNearby's "indoors" stage, a lot-wide
    // native search whose managed check IsInside (light-blocking room id of the spot's tile > 0) is asked only for a spot
    // that already passed the placement test. On a lot with free ground outdoors and no free spot indoors the search
    // places the object on every tile of the lot (3344 tiles, 185-270 ms) and has each passing spot refused.
    //
    // Designs 1 and 2 (a radius around the start, searches per cluster of indoor tiles) could not help on lots whose
    // rooms spread over the lot (s208, s222) and are gone. Design 3 changes the order, natively: ts3native [FglInside]
    // (fglprof.cpp) answers a facing test of this one search "no" without the call when the tile has no light-blocking
    // room on the search's level or one either side, which is when IsInside refuses every spot of the tile. Same tiles
    // accepted in the same order: EA's answer.
    //
    // This file is the managed half. In FibNearbyHook (its own swap of GlobalFunctions.FindGoodLocationNearby), when the
    // indoors stage would run: EA's own body runs the early
    // stages; then the stage's search is made here, word for word EA's (same object, ignore list, start, constraints,
    // IsInside as the check), between two messages to ts3native (op 48: "the next search of this thread is the indoors
    // stage" / "over"). EA's body runs the later stages. Without ts3native or with [FglInside] off the first message is
    // answered false and EA's body runs everything.
    //
    // Household and selected Sims (and an unknown task Sim) keep EA's body unless fglinboundhh is on as well.
    // kFglInBoundShadow: EA's stage runs too (not armed), its answer is returned and a difference is counted and logged.
    // Counters 1508-1517.
    public static partial class Fixes
    {
        const int kFibStages = 1496, kFibNoNative = 1497, kFibFound = 1498, kFibNone = 1499, kFibExempt = 1500, kFibShadow = 1501, kFibShadowDiff = 1502,
            kFibErrors = 1503, kFibMs = 1504, kFibEaMs = 1505;
        const int kFglInsideOp = 48;
        // GlobalFunctions.FindGoodLocationStrategies bits [verified, ilspycmd]: same position, same room, adjoining rooms (the
        // early stages), indoors (the lot-wide stage with IsInside)
        const int kFibEarly = 1 | 2 | 4, kFibIndoors = 8;
        static bool sFibNearbyReady;
        static readonly Type[] sFibNearbyArgs = new Type[] { typeof(IGameObject), typeof(ObjectGuid[]), typeof(Vector3).MakeByRefType(), typeof(Vector3).MakeByRefType(),
            typeof(float), typeof(GlobalFunctions.FindGoodLocationStrategies), typeof(FindGoodLocationBooleans) };

        static void ApplyFglInBound()
        {
            sFibNearbyReady = Patch(typeof(GlobalFunctions), "FindGoodLocationNearby", sFibNearbyArgs, typeof(FibNearbyHook), "NearbyHook", "NearbyOrig");
        }

        // the Sim whose task is running (an interaction's placement search runs on its Sim's task); null = unknown
        static Sim FibTaskSim()
        {
            try
            {
                IScriptProxy p = Simulator.GetProxy(Simulator.CurrentTask);
                return p != null ? p.Target as Sim : null;
            }
            catch (ResetException) { throw; }
            catch (Exception) { return null; }
        }

        public class FibNearbyHook
        {
            public static bool NearbyHook(IGameObject obj, ObjectGuid[] objIdsToIgnore, ref Vector3 position, ref Vector3 forward, float offset,
                GlobalFunctions.FindGoodLocationStrategies searches, FindGoodLocationBooleans fglBools)
            {
                // fglnonescan / fglnnearspot (RouteRelax.cs, docs/675) answer first when on; with both off this returns -1 at once
                int rr = RrNearby(obj, objIdsToIgnore, ref position, ref forward, offset, searches, fglBools);
                if (rr >= 0) return rr == 1;
                if (obj != null && ShortcutOn("fglinbound"))
                {
                    Vector3 bp = position, bf = forward;
                    int r = -1;
                    try { r = FibNearby(obj, objIdsToIgnore, ref position, ref forward, offset, (int)searches, fglBools); }
                    catch (ResetException) { throw; }
                    catch (Exception) { PatchCounters()[kFibErrors]++; }
                    if (r >= 0) return r == 1;
                    position = bp; forward = bf;
                    return NearbyOrig(obj, objIdsToIgnore, ref position, ref forward, offset, searches, fglBools);
                }
                uint ft0 = SrcNowUs();
                bool eaFound = NearbyOrig(obj, objIdsToIgnore, ref position, ref forward, offset, searches, fglBools);
                uint fus = unchecked(SrcNowUs() - ft0);
                if (fus >= 100000) FibEaSlow(obj, fus, eaFound);
                return eaFound;
            }
            public static bool NearbyOrig(IGameObject obj, ObjectGuid[] objIdsToIgnore, ref Vector3 position, ref Vector3 forward, float offset,
                GlobalFunctions.FindGoodLocationStrategies searches, FindGoodLocationBooleans fglBools) { return false; } // receives GlobalFunctions.FindGoodLocationNearby (7 arguments)
        }
        static uint sFibUsRest, sFibEaUsRest;   // sub-millisecond remainders of the two ms counters
        static int sFibEaSlow;                  // switch off: FindGoodLocationNearby calls of 100 ms or more
        static string sFibLast = "";

        // FglInBoundCheck live only: the tiles of the lot with a light-blocking room on the start's level or the one above
        // (1), or cut by a diagonal wall (2). asked = native lookups made.
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

        // EA's indoors stage, word for word (GlobalFunctions.FindGoodLocationNearby)
        static bool FibSearch(ObjectGuid id, ObjectGuid[] ids, Vector3 start, FindGoodLocationBooleans bools, out Vector3 position, out Vector3 forward)
        {
            World.FindGoodLocationParams prm = new World.FindGoodLocationParams(start, bools);
            prm.BooleanConstraints &= ~FindGoodLocationBooleans.StayInRoom;
            prm.RequiredRoomID = -1;
            prm.StartPosition = start;
            prm.PlacementCheckFunc = GlobalFunctions.IsInside;
            return World.FindGoodLocation(id, prm, ids, out position, out forward);
        }

        static void FibAddMs(int slot, ref uint rest, uint us)
        {
            rest += us;
            PatchCounters()[slot] += (int)(rest / 1000u);
            rest %= 1000u;
        }

        // The stage's search with ts3native told about it. Returns 1 found, 0 no indoor spot, -1 ts3native does not take it.
        static int FibArmed(IGameObject obj, ObjectGuid[] ids, Vector3 p0, Vector3 f0, FindGoodLocationBooleans bools, out Vector3 position, out Vector3 forward, out uint us)
        {
            position = p0; forward = f0; us = 0;
            if (!DiagSend(kFglInsideOp, 1)) return -1;
            uint t0 = SrcNowUs();
            bool found;
            try { found = FibSearch(obj.ObjectId, ids, p0, bools, out position, out forward); }
            finally { DiagSend(kFglInsideOp, 0); }
            us = unchecked(SrcNowUs() - t0);
            return found ? 1 : 0;
        }

        static bool FibSame(bool ea, Vector3 ep, bool found, Vector3 mp)
        {
            if (ea != found) return false;
            return !ea || (Math.Abs(ep.x - mp.x) < 0.05f && Math.Abs(ep.z - mp.z) < 0.05f && Math.Abs(ep.y - mp.y) < 0.5f);
        }

        // The indoors stage by our path. Returns 1 found (position / forward set), 0 nothing found, -1 run EA's stage
        // (nothing was changed). Called only when EA would run the stage: lot != 0, start room > 0, Indoors asked for.
        static int FibStage(IGameObject obj, ObjectGuid[] ids, Vector3 p0, Vector3 f0, float offset, FindGoodLocationBooleans bools, out Vector3 position, out Vector3 forward)
        {
            int[] c = PatchCounters();
            c[kFibStages]++;
            uint us;
            int res = FibArmed(obj, ids, p0, f0, bools, out position, out forward, out us);
            if (res < 0) { c[kFibNoNative]++; sFibLast = "last: ts3native [FglInside] did not answer, EA's stage"; return -1; }
            c[res == 1 ? kFibFound : kFibNone]++;
            FibAddMs(kFibMs, ref sFibUsRest, us);
            string line = (us / 1000) + " ms, " + obj.GetType().Name + ", " + (res == 1 ? "found at " + FibPos(position) : "no indoor spot");
            sFibLast = "last: " + line;
            if (Instantiator.kFglInBoundShadow)
            {
                Vector3 ep = p0, ef = f0;
                uint t3 = SrcNowUs();
                bool ea = FibNearbyHook.NearbyOrig(obj, ids, ref ep, ref ef, offset, (GlobalFunctions.FindGoodLocationStrategies)kFibIndoors, bools);
                uint eaUs = unchecked(SrcNowUs() - t3);
                FibAddMs(kFibEaMs, ref sFibEaUsRest, eaUs);
                c[kFibShadow]++;
                bool same = FibSame(ea, ep, res == 1, position);
                if (!same) c[kFibShadowDiff]++;
                HcLine("fglinbound: shadow " + (same ? "same" : "DIFFERENT") + ", ours " + line + "; EA " + (eaUs / 1000) + " ms " + (ea ? "found at " + FibPos(ep) : "no indoor spot"));
                position = ep; forward = ef;
                return ea ? 1 : 0;
            }
            if (us >= 20000) HcLine("fglinbound: " + line);
            return res;
        }

        static string FibPos(Vector3 p) { return p.x.ToString("0.00") + "," + p.y.ToString("0.00") + "," + p.z.ToString("0.00"); }

        // FibNearbyHook's branch. Returns 1 / 0 = the call's answer, -1 = not ours (the caller runs EA's body unchanged).
        static int FibNearby(IGameObject obj, ObjectGuid[] ids, ref Vector3 position, ref Vector3 forward, float offset, int s, FindGoodLocationBooleans bools)
        {
            if ((s & kFibIndoors) == 0) return -1;
            Vector3 p0 = position, f0 = forward;
            LotLocation loc = LotLocation.Invalid;
            ulong lot = World.GetLotLocation(p0, ref loc);
            if (lot == 0 || World.GetRoomId(lot, loc, eRoomDefinition.LightBlocking) <= 0) return -1;   // EA runs no indoors stage
            if (HouseholdExempt(FibTaskSim()) && !ShortcutOn("fglinboundhh")) { PatchCounters()[kFibExempt]++; return -1; }
            // the probes at the position, the same room and the adjoining rooms: EA's own body, those stages only
            int early = s & kFibEarly;
            if (early != 0 && FibNearbyHook.NearbyOrig(obj, ids, ref position, ref forward, offset, (GlobalFunctions.FindGoodLocationStrategies)early, bools)) return 1;
            int rest = s & ~kFibEarly;
            int res;
            try { res = FibStage(obj, ids, p0, f0, offset, bools, out position, out forward); }
            catch (ResetException) { throw; }
            catch (Exception) { PatchCounters()[kFibErrors]++; res = -1; }
            if (res == 1) return 1;
            if (res == 0) rest &= ~kFibIndoors;   // our stage ran and found nothing: the later stages are EA's
            if (rest == 0) return 0;
            position = p0; forward = f0;
            return FibNearbyHook.NearbyOrig(obj, ids, ref position, ref forward, offset, (GlobalFunctions.FindGoodLocationStrategies)rest, bools) ? 1 : 0;
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
            return "fglinbound: indoors stages by our path " + kFibStages + "=" + c[kFibStages] + ", ts3native [FglInside] not answering (EA's stage) " + kFibNoNative + "=" + c[kFibNoNative]
                + ", found " + kFibFound + "=" + c[kFibFound] + ", no indoor spot " + kFibNone + "=" + c[kFibNone]
                + ", household or unknown Sim " + kFibExempt + "=" + c[kFibExempt] + ", shadow compares " + kFibShadow + "=" + c[kFibShadow]
                + ", shadow different " + kFibShadowDiff + "=" + c[kFibShadowDiff] + ", errors " + kFibErrors + "=" + c[kFibErrors]
                + ", our ms " + kFibMs + "=" + c[kFibMs] + ", EA's stage ms in shadow " + kFibEaMs + "=" + c[kFibEaMs]
                + ", slow EA calls with the switch off " + sFibEaSlow + "; " + sFibLast + "; " + GlMembers(CkLive("fglinbound"));
        }

        // FglInBoundCheck live / live:big: the real searches, no Sim needed. A GuitarJig (the logged object) is created on
        // up to three indoor tiles of a lot, our armed stage search and EA's stage run from there, the answers are
        // compared (slots 1501 / 1502 as in shadow), the jig is destroyed. Nothing is placed or moved: both paths only
        // answer with a position. Ours runs first, so what [FglMemo] carries over can only help EA's time.
        // live: from the selected Sim's tile. live:big: from the centre of the largest lot with indoor tiles (the logged
        // 64 x 64 shape), so the slow case is checked without a Sim there; one tile pass per lot, read-only.
        static string FibLive(bool big)
        {
            if (!sFibNearbyReady) return "live: FindGoodLocationNearby swap missing";
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
            int marked = 0, onLevel = 0;
            for (int i = 0; i < cls.Length; i++)
            {
                if (cls[i] != 1) continue;
                marked++;
                if (World.GetRoomId(lot, new LotLocation(i % w, i / w, loc.mLevel, 0), eRoomDefinition.LightBlocking) > 0) onLevel++;
            }
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
            System.Text.StringBuilder b = new System.Text.StringBuilder("live: lot " + w + " x " + h + ", level " + loc.mLevel + ": " + onLevel + " indoor tiles on it, "
                + marked + " with the level above");
            bool different = false;
            foreach (Vector3 p0 in starts)
            {
                LotLocation sl = LotLocation.Invalid;
                World.GetLotLocation(p0, ref sl);
                IGameObject jig = GlobalFunctions.CreateObject("GuitarJig", p0, sl.mLevel, Vector3.UnitZ);
                if (jig == null) return b.Append("; GuitarJig could not be created").ToString();
                try
                {
                    Vector3 ep = p0, ef = Vector3.UnitZ, mp, mf;
                    uint us;
                    int res = FibArmed(jig, null, p0, Vector3.UnitZ, bools, out mp, out mf, out us);
                    uint t1 = SrcNowUs();
                    bool ea = FibNearbyHook.NearbyOrig(jig, null, ref ep, ref ef, 0f, (GlobalFunctions.FindGoodLocationStrategies)kFibIndoors, bools);
                    uint eaUs = unchecked(SrcNowUs() - t1);
                    b.Append("; tile " + sl.mX + "," + sl.mZ + " level " + sl.mLevel + ": EA " + (eaUs / 1000) + " ms " + (ea ? "found at " + FibPos(ep) : "no indoor spot"));
                    if (res < 0) { b.Append(", ours: ts3native [FglInside] did not answer"); continue; }
                    bool same = FibSame(ea, ep, res == 1, mp);
                    c[kFibShadow]++;
                    if (!same) { c[kFibShadowDiff]++; different = true; }
                    b.Append(", ours " + (us / 1000) + " ms " + (res == 1 ? "found at " + FibPos(mp) : "no indoor spot") + " -> " + (same ? "same" : "DIFFERENT"));
                }
                finally { jig.Destroy(); }
            }
            return b.Append(different ? "; live verdict: DIFFERENT" : "; live verdict: ok").ToString();
        }

        // FglInBoundCheck run: is the native half there (op 48 answered for arm and end)? live / live:big: FibLive.
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
            bool arm = false, end = false;
            try { arm = DiagSend(kFglInsideOp, 1); } finally { end = DiagSend(kFglInsideOp, 0); }
            bool fixedNow = on && arm && end && sFibNearbyReady;
            return B582Line("fglinbound", on, "indoors stage searched with ts3native [FglInside] (op 48 answered: arm " + arm + ", end " + end + ")", fixedNow,
                "the two messages are answered alike (no arm left behind), the stage's search is EA's own call", arm == end,
                sFibNearbyReady ? "" : "FindGoodLocationNearby swap", FibCounters());
        }
    }
}
