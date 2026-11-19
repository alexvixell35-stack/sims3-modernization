using System;
using System.Collections.Generic;
using Sims3.Gameplay;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects.Elevator;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Agent
{
    // route_storm (docs/668): forces routing hard so a routing test needs ~4 min per arm instead of 12-15 min of idle play.
    // It picks resident NPC Sims already in the world (never the active household unless household: true; no babies or
    // toddlers; pets and horses only for kind "pets") and pushes the game's own NON-user-directed route interactions, the
    // way EA's autonomy and situations push them, so every plan goes through the same code paths as natural play:
    //   lot      VisitCommunityLot (Autonomous priority, autonomous: a townie's own outing) to a far community lot;
    //   home     Sim.MakeSimGoHome at NonCriticalNPCBehavior (Occupation.SendSimHome, situations' "go home");
    //   boat     a lot trip across water: houseboat lots, dive / resort / port lots, lots behind >= 60 m of sea;
    //   meet     VisitLot / VisitCommunityLot onto the lot another picked NPC is on (or its home), so arrivals converge;
    //   nopath   Terrain.GoHere at NonCriticalNPCBehavior to a spot nobody can reach (a sealed spot, else open sea);
    //   elevator residents of elevator lots go home (Bridgeport high-rises), others visit an elevator lot;
    //   pets     GoToLot at NonCriticalNPCBehavior, autonomous, to a far lot (WildHorsePool's own push).
    // A refused visit falls back to GoToLot (fewer Test gates). Deterministic: candidates and lots are sorted by id, then
    // shuffled with Random(seed, wave). repeatS > 0 re-issues a wave every repeatS wall-clock seconds from a game task until
    // {stop: true}, which cancels the interactions the storm pushed that are still queued (cancel: false keeps them).
    //   route_storm {count?: 40, kinds?: [...all], repeatS?: 0, seed?: 1, household?: false} -> the first wave
    //   route_storm {status: true} -> totals; route_storm {stop: true, cancel?: true}
    // Seen / unseen per pushed Sim: ts3fixes UfWhy (0 = unseen) when reachable, else camera distance > 60 m.
    public static partial class Handlers
    {
        static void RegisterRouteStorm() { Dispatcher.Register("route_storm", RouteStorm); }

        static readonly string[] kStormKinds = { "lot", "home", "boat", "meet", "nopath", "elevator", "pets" };
        static int sStormGen, sStormWaves, sStormSeen, sStormUnseen, sStormCancelled;
        static bool sStormRunning;
        static string sStormError, sStormSeenBy;
        static readonly Dictionary<string, int> sStormPushed = new Dictionary<string, int>(), sStormFailed = new Dictionary<string, int>();
        static readonly List<string> sStormFailures = new List<string>();
        static readonly List<KeyValuePair<Sim, InteractionInstance>> sStormInst = new List<KeyValuePair<Sim, InteractionInstance>>();
        // world targets, found once per world (lot ids are stable within a world)
        static string sStormWorld;
        static List<Lot> sStormCommunity, sStormBoat, sStormElevator;
        static List<Vector3> sStormNoPath;

        static object RouteStorm(Args a)
        {
            if (!a.Has("stop") && !a.Has("status")) RequireLive();  // stop / status answer in any mode
            Dictionary<string, object> r = Util.Obj();
            if (a.Has("stop"))
            {
                sStormGen++;
                sStormRunning = false;
                int n = a.Bool("cancel", true) ? StormCancel() : 0;
                DiagLine("route_storm: stop after " + sStormWaves + " waves, cancelled " + n);
                StormTotals(r);
                r["stopped"] = true;
                r["cancelled_now"] = n;
                return r;
            }
            if (a.Has("status")) { StormTotals(r); return r; }
            int count = Math.Max(1, Math.Min(200, (int)a.Long("count", 40)));
            int seed = (int)a.Long("seed", 1);
            double repeatS = a.Double("repeatS", 0);
            bool hh = a.Bool("household", false);
            List<string> kinds = StormKinds(a);
            int gen = ++sStormGen;
            sStormWaves = sStormSeen = sStormUnseen = sStormCancelled = 0;
            sStormPushed.Clear(); sStormFailed.Clear(); sStormFailures.Clear(); sStormError = null;
            sStormInst.Clear();
            StormTargets(false);
            Dictionary<string, object> wave = StormWave(kinds, count, seed, hh);
            DiagLine("route_storm: start, kinds " + string.Join(",", kinds.ToArray()) + ", count " + count + ", seed " + seed + ", repeatS " + repeatS);
            if (repeatS > 0)
            {
                int everyMs = (int)Math.Max(5000, repeatS * 1000);
                sStormRunning = true;
                Simulator.AddObject(new OneShotFunctionTask(delegate
                {
                    try
                    {
                        while (sStormGen == gen)
                        {
                            int t0 = System.Environment.TickCount;
                            while (sStormGen == gen && System.Environment.TickCount - t0 < everyMs) Simulator.Sleep(0);
                            if (sStormGen != gen) break;
                            if (SubState() != "LiveModeState") continue;  // no pushes in Build / Buy / CAS or while loading
                            StormWave(kinds, count, seed, hh);
                        }
                    }
                    catch (ResetException) { throw; }
                    catch (Exception e) { sStormError = e.GetType().Name + ": " + e.Message; DiagLine("route_storm: error " + sStormError); }
                    finally { if (sStormGen == gen) sStormRunning = false; }
                }));
            }
            r["wave"] = wave;
            r["repeat_ms"] = repeatS > 0 ? (object)(int)Math.Max(5000, repeatS * 1000) : null;
            r["kinds"] = kinds;
            r["seed"] = seed;
            return r;
        }

        static List<string> StormKinds(Args a)
        {
            List<string> kinds = new List<string>();
            if (a.Has("kinds"))
            {
                // a JSON list or one comma / space separated string ("lot,boat nopath"); repeats weight a kind
                List<object> l = a.Raw("kinds") as List<object>;
                IEnumerable<object> src = l != null ? (IEnumerable<object>)l : (IEnumerable<object>)a.Str("kinds").Split(new char[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (object o in src)
                {
                    string k = Convert.ToString(o).Trim().ToLowerInvariant();
                    if (Array.IndexOf(kStormKinds, k) < 0) throw new McpException("route_storm: unknown kind '" + k + "' (" + string.Join(", ", kStormKinds) + ")");
                    kinds.Add(k);  // a kind may repeat: ["boat","boat","lot"] gives boat 2 of every 3 pushes
                }
            }
            if (kinds.Count == 0) kinds.AddRange(kStormKinds);
            return kinds;
        }

        static void StormTotals(Dictionary<string, object> r)
        {
            r["running"] = sStormRunning;
            r["waves"] = sStormWaves;
            r["pushed"] = StormCopy(sStormPushed);
            r["failed"] = StormCopy(sStormFailed);
            r["seen"] = sStormSeen; r["unseen"] = sStormUnseen; r["seen_by"] = sStormSeenBy;
            r["cancelled"] = sStormCancelled;
            r["failures"] = new List<object>(sStormFailures.ConvertAll(s => (object)s));
            r["error"] = sStormError;
        }

        static Dictionary<string, object> StormCopy(Dictionary<string, int> d)
        {
            Dictionary<string, object> o = Util.Obj();
            foreach (KeyValuePair<string, int> kv in d) o[kv.Key] = kv.Value;
            return o;
        }

        static void StormAdd(Dictionary<string, int> d, string k) { int v; d.TryGetValue(k, out v); d[k] = v + 1; }

        // Cancels the storm's interactions that are still in their Sim's queue; drops the rest from the list.
        static int StormCancel()
        {
            int n = 0;
            foreach (KeyValuePair<Sim, InteractionInstance> kv in sStormInst)
            {
                Sim s = kv.Key;
                try
                {
                    if (s == null || s.HasBeenDestroyed || s.InteractionQueue == null) continue;
                    foreach (InteractionInstance ii in s.InteractionQueue.InteractionList)
                        if (ii == kv.Value) { s.InteractionQueue.CancelInteraction(ii.Id); n++; break; }
                }
                catch (ResetException) { throw; }
                catch (Exception) { }
            }
            sStormInst.Clear();
            sStormCancelled += n;
            return n;
        }

        // World targets: community lots, boat lots (houseboats, IP's dive / resort / port lots), lots with elevators, and
        // unreachable spots (sealed spots on up to 8 lots near the camera, then open sea).
        static void StormTargets(bool force)
        {
            string w = GameUtils.GetCurrentWorld().ToString();
            if (!force && w == sStormWorld && sStormCommunity != null) return;
            sStormWorld = w;
            List<Lot> all = new List<Lot>();
            foreach (Lot l in LotManager.AllLots) if (l != null && !l.IsWorldLot) all.Add(l);
            all.Sort(delegate(Lot x, Lot y) { return x.LotId.CompareTo(y.LotId); });
            sStormCommunity = new List<Lot>(); sStormBoat = new List<Lot>(); sStormElevator = new List<Lot>(); sStormNoPath = new List<Vector3>();
            foreach (Lot l in all)
            {
                if (l.IsCommunityLot) sStormCommunity.Add(l);
                CommercialLotSubType t = l.CommercialLotSubType;
                if (l.IsHouseboatLot() || t == CommercialLotSubType.kEP10_Diving || t == CommercialLotSubType.kEP10_Resort || t == CommercialLotSubType.kEP10_Port) sStormBoat.Add(l);
                try { if (l.GetObjects<ElevatorDoors>().Length > 0) sStormElevator.Add(l); }
                catch (ResetException) { throw; }
                catch (Exception) { }
            }
            Sim probe = Util.ActiveSim();
            if (probe != null)
            {
                Vector3 p = SealedPoint(probe);
                if (p != Vector3.Invalid) sStormNoPath.Add(p);
                Vector3 sea = SeaPoint(probe);
                // a spot well out to sea: 150 m past the first sea point, along the same line, if that is still sea
                if (sea != Vector3.Invalid)
                {
                    Vector3 d = sea - probe.Position; d.y = 0f;
                    float len = d.Length();
                    Vector3 deep = len > 1f ? sea + d * (150f / len) : sea;
                    sStormNoPath.Add((TerrainType)World.GetTerrainType(deep.x, deep.z, 0) == TerrainType.WorldSea ? deep : sea);
                }
            }
        }

        // Resident NPCs in the world, sorted by SimDescriptionId: humans (child and up) or, for "pets", pets and horses.
        static List<Sim> StormCandidates(bool pets, bool hh)
        {
            List<Sim> c = new List<Sim>();
            Household act = Household.ActiveHousehold;
            foreach (Sim s in Sims3.Gameplay.Queries.GetObjects<Sim>())
            {
                try
                {
                    if (s == null || s.HasBeenDestroyed || !s.InWorld || s.SimDescription == null || s.InteractionQueue == null) continue;
                    SimDescription sd = s.SimDescription;
                    if (sd.ToddlerOrBelow || sd.IsPet != pets) continue;
                    if (!hh && (s.IsSelectable || s == PlumbBob.SelectedActor || (act != null && s.Household == act))) continue;
                    Household h = s.Household;
                    if (h == null) continue;
                    if (!pets && h.IsSpecialHousehold) continue;  // service, tourist, alien ... households are not residents
                    if (s.Service != null) continue;  // a Sim in a rabbit hole is not InWorld
                    c.Add(s);
                }
                catch (ResetException) { throw; }
                catch (Exception) { }
            }
            c.Sort(delegate(Sim x, Sim y) { return x.SimDescription.SimDescriptionId.CompareTo(y.SimDescription.SimDescriptionId); });
            return c;
        }

        static void StormShuffle<T>(List<T> l, Random rng)
        {
            for (int i = l.Count - 1; i > 0; i--) { int j = rng.Next(i + 1); T t = l[i]; l[i] = l[j]; l[j] = t; }
        }

        // Drops pushes that left their Sim's queue (ran, failed, cancelled; Sim gone) and returns the Sims that still have one:
        // one pass over the pushes, so the list stays bounded by what is queued now.
        static Dictionary<Sim, bool> StormPrune()
        {
            Dictionary<Sim, bool> busy = new Dictionary<Sim, bool>();
            sStormInst.RemoveAll(delegate(KeyValuePair<Sim, InteractionInstance> kv)
            {
                Sim s = kv.Key;
                if (s == null || s.HasBeenDestroyed || s.InteractionQueue == null) return true;
                foreach (InteractionInstance ii in s.InteractionQueue.InteractionList)
                    if (ii == kv.Value) { busy[s] = true; return false; }
                return true;
            });
            return busy;
        }

        // One wave: count pushes spread round-robin over kinds; each Sim at most once per wave and never while an
        // earlier storm push is still in its queue.
        static Dictionary<string, object> StormWave(List<string> kinds, int count, int seed, bool hh)
        {
            int wave = ++sStormWaves;
            Random rng = new Random(seed * 7919 + wave);
            StormTargets(false);
            Dictionary<Sim, bool> busy = StormPrune();
            List<Sim> humans = StormCandidates(false, hh), pets = kinds.Contains("pets") ? StormCandidates(true, hh) : new List<Sim>();
            StormShuffle(humans, rng); StormShuffle(pets, rng);
            ShLookUp();
            sStormSeenBy = sShUfWhy != null ? "UfWhy" : "camera";
            Dictionary<string, int> pushed = new Dictionary<string, int>(), failed = new Dictionary<string, int>();
            List<object> used = new List<object>(), fails = new List<object>();
            Dictionary<Sim, bool> taken = new Dictionary<Sim, bool>();
            int hi = 0, pi = 0, seen = 0, unseen = 0, misses = 0;
            for (int n = 0; n < count && misses < kinds.Count * 3; n++)
            {
                string kind = kinds[n % kinds.Count];
                List<Sim> pool = kind == "pets" ? pets : humans;
                Sim s = null;
                while (true)
                {
                    int i = kind == "pets" ? pi++ : hi++;
                    if (i >= pool.Count) break;
                    Sim c = pool[i];
                    if (taken.ContainsKey(c) || busy.ContainsKey(c)) continue;
                    s = c; break;
                }
                if (s == null) { misses++; StormAdd(failed, kind); fails.Add(kind + ": no free Sim"); continue; }
                taken[s] = true;
                string why = null;
                List<InteractionInstance> before = new List<InteractionInstance>(s.InteractionQueue.InteractionList);
                try { why = StormPush(kind, s, humans, rng); }
                catch (ResetException) { throw; }
                catch (Exception e) { why = "threw " + e.GetType().Name + ": " + e.Message; }
                if (why != null) { StormAdd(failed, kind); StormAdd(sStormFailed, kind); string f = s.FullName + " " + kind + ": " + why; fails.Add(f); if (sStormFailures.Count < 40) sStormFailures.Add(f); continue; }
                foreach (InteractionInstance ii in s.InteractionQueue.InteractionList)
                    if (!before.Contains(ii)) sStormInst.Add(new KeyValuePair<Sim, InteractionInstance>(s, ii));
                StormAdd(pushed, kind); StormAdd(sStormPushed, kind);
                bool isUnseen = StormUnseen(s);
                if (isUnseen) { unseen++; sStormUnseen++; } else { seen++; sStormSeen++; }
                used.Add(s.FullName + " (" + kind + (isUnseen ? ", unseen)" : ", seen)"));
            }
            Dictionary<string, object> r = Util.Obj();
            r["wave"] = wave;
            r["pushed"] = StormCopy(pushed);
            r["failed"] = StormCopy(failed);
            r["seen"] = seen; r["unseen"] = unseen; r["seen_by"] = sStormSeenBy;
            r["candidates"] = humans.Count; r["pet_candidates"] = pets.Count;
            r["sims"] = used;
            r["failures"] = fails;
            int tot = 0; foreach (int v in pushed.Values) tot += v;
            DiagLine("route_storm: wave " + wave + " pushed " + tot + " (" + seen + " seen, " + unseen + " unseen), failed " + fails.Count);
            return r;
        }

        static bool StormUnseen(Sim s)
        {
            object w = ShUfWhy(s);
            if (w is int) return (int)w == 0;
            return (s.Position - CameraController.GetPosition()).Length() > 60f;
        }

        static Lot StormPick(List<Lot> lots, Sim s, Random rng, bool far)
        {
            List<Lot> c = new List<Lot>();
            foreach (Lot l in lots) if (l != s.LotCurrent) c.Add(l);
            if (c.Count == 0) return null;
            if (far)  // a random lot of the farthest half from the Sim
            {
                Vector3 p = s.Position;
                c.Sort(delegate(Lot x, Lot y) { return (y.GetCenterPosition() - p).LengthSqr().CompareTo((x.GetCenterPosition() - p).LengthSqr()); });
                c.RemoveRange((c.Count + 1) / 2, c.Count / 2);
            }
            return c[rng.Next(c.Count)];
        }

        // Pushes one route of `kind` for s; null on success, else why it failed.
        static string StormPush(string kind, Sim s, List<Sim> others, Random rng)
        {
            InteractionPriority npc = new InteractionPriority(InteractionPriorityLevel.NonCriticalNPCBehavior);
            switch (kind)
            {
                case "lot":
                {
                    Lot l = StormPick(sStormCommunity, s, rng, true);
                    return l == null ? "no community lot" : StormVisit(s, l);
                }
                case "home":
                    if (s.LotHome == null) return "no home lot";
                    if (s.LotCurrent == s.LotHome) return "already home";
                    return Sim.MakeSimGoHome(s, false, npc) ? null : "MakeSimGoHome refused";
                case "boat":
                {
                    if (sStormBoat.Count == 0 && sStormCommunity.Count == 0) return "no lots";
                    // half of the time a lot behind open sea (up to 4 random community lots tried), else a boat lot
                    Lot l = null;
                    if (rng.Next(2) == 0 || sStormBoat.Count == 0)
                        for (int t = 0; t < 4 && l == null; t++)
                        {
                            Lot c = StormPick(sStormCommunity, s, rng, true);
                            if (c != null && StormSeaBetween(s.Position, c.GetCenterPosition()) >= 60f) l = c;
                        }
                    if (l == null) l = StormPick(sStormBoat, s, rng, false);
                    return l == null ? "no lot across water in this world" : StormVisit(s, l);
                }
                case "meet":
                {
                    Sim o = null;
                    for (int t = 0; t < 6 && o == null; t++)
                    {
                        Sim c = others[rng.Next(others.Count)];
                        if (c != s && c.LotCurrent != null && (!c.LotCurrent.IsWorldLot || c.LotHome != null)) o = c;
                    }
                    if (o == null) return "no other NPC to meet";
                    Lot l = o.LotCurrent != null && !o.LotCurrent.IsWorldLot ? o.LotCurrent : o.LotHome;
                    if (l == s.LotCurrent) return "already on " + o.FullName + "'s lot";
                    return StormVisit(s, l);
                }
                case "nopath":
                {
                    if (sStormNoPath.Count == 0) return "no unreachable spot found in this world";
                    Vector3 p = sStormNoPath[rng.Next(sStormNoPath.Count)];
                    InteractionInstance ii = Terrain.GoHere.GetSingleton(s, p).CreateInstance(Terrain.Singleton, s, npc, false, true);
                    Terrain.GoHere gh = ii as Terrain.GoHere;
                    if (gh != null) gh.SetDestination(p, false);
                    return s.InteractionQueue.Add(ii) ? null : "Go Here not queued";
                }
                case "elevator":
                {
                    if (sStormElevator.Count == 0) return "no elevator lot in this world";
                    if (s.LotHome != null && s.LotCurrent != s.LotHome && sStormElevator.Contains(s.LotHome))
                        return Sim.MakeSimGoHome(s, false, npc) ? null : "MakeSimGoHome refused";
                    Lot l = StormPick(sStormElevator, s, rng, false);
                    return l == null ? "already on the only elevator lot" : StormVisit(s, l);
                }
                case "pets":
                {
                    Lot l = StormPick(sStormCommunity, s, rng, true);
                    if (l == null) return "no community lot";
                    return s.InteractionQueue.Add(GoToLot.Singleton.CreateInstance(l, s, npc, true, true)) ? null : "GoToLot not queued";
                }
            }
            return "unknown kind";
        }

        // A townie's own lot trip: VisitCommunityLot / VisitLot at Autonomous priority, autonomous; a refused visit (Test
        // gates: hours, greeted, visit frequency) falls back to GoToLot at NonCriticalNPCBehavior, as situations push it.
        static string StormVisit(Sim s, Lot l)
        {
            InteractionDefinition def = l.IsCommunityLot ? VisitCommunityLot.Singleton : VisitLot.Singleton;
            if (!l.IsHouseboatLot() && s.InteractionQueue.Add(def.CreateInstance(l, s, new InteractionPriority(InteractionPriorityLevel.Autonomous), true, true))) return null;
            if (s.InteractionQueue.Add(GoToLot.Singleton.CreateInstance(l, s, new InteractionPriority(InteractionPriorityLevel.NonCriticalNPCBehavior), true, true))) return null;
            return "visit and GoToLot not queued (" + l.Name + ")";
        }

        // metres of open sea on the straight line a -> b (10 m samples)
        static float StormSeaBetween(Vector3 a, Vector3 b)
        {
            Vector3 d = b - a;
            float len = d.Length(), sea = 0f;
            for (float t = 0f; t < len; t += 10f)
            {
                Vector3 p = a + d * (t / len);
                if ((TerrainType)World.GetTerrainType(p.x, p.z, 0) == TerrainType.WorldSea) sea += 10f;
            }
            return sea;
        }
    }
}
