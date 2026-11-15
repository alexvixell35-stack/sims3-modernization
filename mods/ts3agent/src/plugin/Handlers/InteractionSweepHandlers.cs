using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Socializing;
using Sims3.Gameplay.UI;
using Sims3.SimIFace;
using Sims3.SimIFace.BuildBuy;

namespace Ts3Agent
{
    // interaction_sweep (docs/268): a data-driven forced sweep of every interaction definition a pack brings. The harness passes
    // the pack's script types (docs/data/ep-pack-types.csv, repros.json sweep-* entries made by interaction-sweep-report.ts
    // --gen-repros). Targets, in a fixed order (the row index is resumable): pack object types (an in-world instance, else one
    // bought from the catalogue and placed near the sweep spot), then one instance of every other in-world object type (rabbit
    // holes included; only pack definitions), then terrain, the actor's lot, the actor itself and a social partner (pack
    // definitions only). Each definition is tested with EA's own Test for every actor (NPCs by age, pets; the active Sim only
    // with household: true, test saves only), user-directed first, autonomous when tuning disallows user-directed. The first
    // pass is pushed: teleport + push in one task slice with no yield (nothing moves in between, same as pause/act/unpause),
    // runs `secsPerRun` real seconds, then cancel and, if the queue does not empty, a Sim reset. One row per definition:
    // to the result (cursor) and as an "isweep row {json}" line to ts3native.log (survives a crash). Destructive definitions
    // (death, moving, selling, travel, ...: kIswDestructive) are skipped unless destructive: true (throwaway save copies only).
    // Harness actions only: no ts3fixes behaviour changes, nothing written to the save beyond what the forced interactions do.
    public static partial class Handlers
    {
        static void RegisterInteractionSweep()
        {
            Dispatcher.Register("interaction_sweep", InteractionSweep);
        }

        // definition type-name segments (case-insensitive prefix of a '.'/'+' segment) that end, move or remove Sims, objects or lots
        internal static readonly string[] kIswDestructive = {
            "Death", "Die", "Dying", "Kill", "Murder", "Euthan", "Reaper", "Disintegrat", "Sell", "Delete", "Destroy", "Bulldoze",
            "Demolish", "Scrap", "Recycle", "MoveOut", "MoveIn", "AskToMoveIn", "KickOut", "Evict", "Disown", "Adopt", "Abandon",
            "Emigrat", "Travel", "Vacation", "GoToUniversity", "GoToSchoolAbroad", "GoToFuture", "GoBackToPresent", "ReturnHome",
            "TimeTravel", "LeaveWorld", "Quit", "AgeUp", "Propose", "Marry", "Wedding", "Elope", "BreakUp", "Divorce", "TryForBaby",
            "Abduct", "SetOnFire", "Arson", "Burn", "Pregnan", "SaveGame", "BuyLot", "SellLot", "ChangeLot" };

        class IswTarget { public GameObject Obj; public string Kind; public string Cls; public bool PackOnly; }
        class IswActor { public Sim S; public string Kind; }
        class IswCand { public string Key, DefId, Label; public List<MenuEntry> Per = new List<MenuEntry>(); public List<int> Who = new List<int>(); }

        static int sIswGen, sIswFrom, sIswIdx, sIswSent, sIswMaxPerDef, sIswSecs, sIswMaxMin, sIswObjects, sIswScanMax;
        static int sIswTested, sIswPass, sIswPushed, sIswRan, sIswFailed, sIswSkipped, sIswDup, sIswErrors, sIswExceptions, sIswResets, sIswBought, sIswScanned;
        static string sIswState = "idle", sIswStop, sIswPack, sIswLast, sIswSpotName;
        static bool sIswDestructive, sIswWorldDefs, sIswBuy, sIswAutoRetry;
        static int sIswT0;  // TickCount: int differences stay right across its wrap
        static Vector3 sIswSpot;
        static readonly List<object> sIswRows = new List<object>();
        static readonly List<object> sIswNotes = new List<object>();
        static readonly List<string> sIswSkip = new List<string>();
        static Dictionary<string, bool> sIswPackTypes = new Dictionary<string, bool>();
        static List<IswTarget> sIswTargets = new List<IswTarget>();
        static List<IswActor> sIswActors = new List<IswActor>();
        static Sim sIswPartner;
        static readonly List<GameObject> sIswPlaced = new List<GameObject>();
        static readonly Dictionary<string, int> sIswPushedBy = new Dictionary<string, int>(), sIswTriedBy = new Dictionary<string, int>();
        static readonly Dictionary<string, string> sIswProduct = new Dictionary<string, string>();  // pack type full name -> product key (kept per session)
        // script error trap (NRaas ErrorTrap's ScriptCore.ExceptionTrap.OnScriptError, as ReproHandlers SgTrapOn)
        static FieldInfo sIswTrapField;
        static Delegate sIswTrapHook;
        static string sIswTrap = "off";
        static volatile bool sIswInRun;
        static int sIswRunErrors;
        static string sIswRunError;

        // interaction_sweep {start: true, pack, types: [full type names], maxPerDef?: 1, secsPerRun?: 4, actors?: 5 | [sim refs], pets?: true,
        //   household?: false, objects?: 1, buy?: true, scanMax?: 3000, worldDefs?: true, near?: "@far", from?: 0, maxMinutes?: 40,
        //   destructive?: false, skip?: [segment prefixes], autonomousRetry?: true, cleanup?: true}
        //   | {result: true, since?: N, peek?: true, require?: true} | {stop: true}; result rows come from a cursor (each row once, at most
        //   1500 per answer; since: N rewinds), peek: no rows; require: error unless the sweep finished (state done) with >= 1 row
        static object InteractionSweep(Args a)
        {
            RequireLive();
            Dictionary<string, object> r = Util.Obj();
            if (a.Has("start"))
            {
                if (sIswState == "setup" || sIswState == "running") throw new McpException("interaction_sweep: already " + sIswState + " (stop first)");
                IswReset();
                sIswPack = a.Str("pack");
                foreach (object o in a.List("types")) sIswPackTypes[Convert.ToString(o)] = true;
                if (sIswPackTypes.Count == 0) throw new McpException("interaction_sweep: types is empty");
                sIswMaxPerDef = Math.Max(1, (int)a.Long("maxPerDef", 1));
                sIswSecs = Math.Max(1, (int)a.Long("secsPerRun", 4));
                sIswObjects = Math.Max(1, (int)a.Long("objects", 1));
                sIswScanMax = (int)a.Long("scanMax", 3000);
                sIswMaxMin = Math.Max(1, (int)a.Long("maxMinutes", 40));
                sIswFrom = Math.Max(0, (int)a.Long("from", 0));
                sIswBuy = a.Bool("buy", true);
                sIswWorldDefs = a.Bool("worldDefs", true);
                sIswDestructive = a.Bool("destructive", false);
                sIswAutoRetry = a.Bool("autonomousRetry", true);
                List<object> sk = a.List("skip");
                if (sk != null) foreach (object o in sk) sIswSkip.Add(Convert.ToString(o));
                GameObject near = FindTarget(new Args(new Dictionary<string, object> { { "near", a.Str("near", "@far") } }), "near");
                sIswSpot = near is Lot ? ((Lot)near).GetCenterPosition() : near.Position;
                sIswSpotName = Util.ObjectName(near);
                IswPickActors(a);
                bool cleanup = a.Bool("cleanup", true);
                int gen = ++sIswGen;
                sIswT0 = System.Environment.TickCount;
                sIswState = "setup";
                IswTrapOn();
                Simulator.AddObject(new Sims3.Gameplay.OneShotFunctionTask(delegate
                {
                    try
                    {
                        IswSetup(gen);
                        if (gen == sIswGen && sIswStop == null) { sIswState = "running"; IswLoop(gen); }
                        if (gen == sIswGen && sIswState == "running") sIswState = "done";
                    }
                    catch (Exception e) { sIswState = "error"; sIswStop = "sweep error: " + DescribeError(e); }
                    finally
                    {
                        if (gen == sIswGen) { IswTrapOff(); if (cleanup) IswCleanup(); }
                        // queue logRequire reads this: the sweep ended and its rows are in this log
                        try { DiagLine("isweep done " + sIswPack + " state " + sIswState + " rows " + sIswRows.Count + " next " + sIswIdx + " pushed " + sIswPushed + " ran " + sIswRan + " se " + sIswErrors + (sIswStop != null ? " stop " + IswCut(sIswStop, 120) : "")); } catch (Exception) { }
                    }
                }));
                r["started"] = true;
                r["pack"] = sIswPack;
                r["types"] = sIswPackTypes.Count;
                r["spot"] = sIswSpotName;
                r["actors"] = IswActorNames();
                r["trap"] = sIswTrap;
                return r;
            }
            if (a.Has("stop"))
            {
                if (sIswState == "setup" || sIswState == "running") { sIswStop = "stopped"; sIswState = "stopped"; }
                sIswGen++;
                IswTrapOff();
                IswCleanup();
                r["stopped"] = true;
            }
            if (a.Has("since")) sIswSent = Math.Max(0, Math.Min(sIswRows.Count, (int)a.Long("since")));
            List<object> rows = new List<object>();
            // peek: counts only, the cursor stays (a step's `until` poll is not logged, its rows would be lost)
            if (!a.Bool("peek", false)) lock (sIswRows) { for (; sIswSent < sIswRows.Count && rows.Count < 1500; sIswSent++) rows.Add(sIswRows[sIswSent]); }
            r["state"] = sIswState;
            r["finished"] = sIswState != "setup" && sIswState != "running";
            r["pack"] = sIswPack;
            r["stop_reason"] = sIswStop;
            r["next"] = sIswIdx;  // resume index: start again with from = next
            r["from"] = sIswFrom;
            r["secs"] = sIswT0 != 0 ? (System.Environment.TickCount - sIswT0) / 1000 : 0;
            r["targets"] = sIswTargets.Count;
            r["logged"] = sIswRows.Count;
            r["tested"] = sIswTested; r["pass"] = sIswPass; r["pushed"] = sIswPushed; r["ran"] = sIswRan; r["failed"] = sIswFailed;
            r["skipped"] = sIswSkipped; r["dup"] = sIswDup; r["script_errors"] = sIswErrors; r["exceptions"] = sIswExceptions; r["resets"] = sIswResets;
            r["bought"] = sIswBought; r["scanned"] = sIswScanned; r["trap"] = sIswTrap; r["last"] = sIswLast;
            r["actors"] = IswActorNames();
            r["notes"] = sIswNotes;
            r["rows"] = rows;
            r["sent"] = sIswSent;
            if (a.Bool("require", false))
            {
                string bad = sIswState != "done" ? "sweep not finished: " + sIswState + (sIswStop != null ? " (" + sIswStop + ")" : "") + ", resume with from " + sIswIdx
                    : sIswRows.Count == 0 ? "no definition row logged (pack types not in this world or install?)" : null;
                if (bad != null) throw new McpException("interaction_sweep " + sIswPack + ": " + bad);
            }
            return r;
        }

        static void IswReset()
        {
            sIswStop = null; sIswLast = null; sIswIdx = 0; sIswSent = 0;
            sIswTested = sIswPass = sIswPushed = sIswRan = sIswFailed = sIswSkipped = sIswDup = sIswErrors = sIswExceptions = sIswResets = sIswBought = sIswScanned = 0;
            lock (sIswRows) sIswRows.Clear();
            sIswNotes.Clear(); sIswSkip.Clear(); sIswPackTypes = new Dictionary<string, bool>(); sIswTargets = new List<IswTarget>();
            sIswActors = new List<IswActor>(); sIswPartner = null; sIswPushedBy.Clear(); sIswTriedBy.Clear();
        }

        static List<object> IswActorNames()
        {
            List<object> n = new List<object>();
            foreach (IswActor x in sIswActors) n.Add(x.Kind + " " + (x.S != null ? x.S.FullName : "?"));
            if (sIswPartner != null) n.Add("partner " + sIswPartner.FullName);
            return n;
        }

        // NPC actors: one per age (youngadult, adult, elder, teen, child) from known non-active, non-service households, pets (one per
        // species) and a youngadult/adult social partner; the active Sim only with household: true (a dedicated test Sim of a test save)
        static void IswPickActors(Args a)
        {
            Household active = Household.ActiveHousehold;
            Dictionary<ulong, bool> used = new Dictionary<ulong, bool>();
            List<object> refs = a.Raw("actors") as List<object>;
            if (refs != null)
                foreach (object o in refs)
                {
                    Sim s = Util.FindSim(new Args(new Dictionary<string, object> { { "sim", Convert.ToString(o) } }), "sim");
                    SimDescription d = s.SimDescription;
                    if (active != null && d.Household == active) throw new McpException("interaction_sweep: actor " + s.FullName + " is in the active household (use household: true)");
                    IswAddActor(s, "npc-" + (d.IsPet ? d.Species.ToString() : d.Age.ToString()).ToLowerInvariant(), used);
                }
            else
            {
                int n = (int)a.Long("actors", 5);
                string[] ages = { "YoungAdult", "Adult", "Elder", "Teen", "Child" };
                for (int k = 0; k < ages.Length && k < n; k++)
                {
                    Sim s = IswFindNpc(ages[k], false, used, active);
                    if (s != null) IswAddActor(s, "npc-" + ages[k].ToLowerInvariant(), used); else sIswNotes.Add("no NPC actor of age " + ages[k]);
                }
            }
            if (a.Bool("pets", true))
                foreach (string sp in new string[] { "Dog", "Cat", "Horse" })
                {
                    Sim s = IswFindNpc(sp, true, used, active);
                    if (s != null) IswAddActor(s, "npc-" + sp.ToLowerInvariant(), used);
                }
            sIswPartner = IswFindNpc("YoungAdult", false, used, active) ?? IswFindNpc("Adult", false, used, active);
            if (sIswPartner != null) used[sIswPartner.SimDescription.SimDescriptionId] = true; else sIswNotes.Add("no social partner NPC");
            if (a.Bool("household", false))
            {
                Sim me = PlumbBob.SelectedActor;
                if (me == null) throw new McpException("interaction_sweep: household: true needs a selected Sim");
                sIswActors.Add(new IswActor { S = me, Kind = "household" });  // last: only definitions no NPC can run reach it
            }
            if (sIswActors.Count == 0) throw new McpException("interaction_sweep: no actor (no NPC in the world matches)");
        }

        static void IswAddActor(Sim s, string kind, Dictionary<ulong, bool> used)
        {
            used[s.SimDescription.SimDescriptionId] = true;
            sIswActors.Add(new IswActor { S = s, Kind = kind });
        }

        // what: an age name (humans) or a species name (pets)
        static Sim IswFindNpc(string what, bool pet, Dictionary<ulong, bool> used, Household active)
        {
            foreach (SimDescription sd in SimDescription.GetSimDescriptionsInWorld())
            {
                if (!Util.InWorldNow(sd) || sd.IsPet != pet || used.ContainsKey(sd.SimDescriptionId)) continue;
                Household h = sd.Household;
                if (h == null || h == active || h.IsServiceNpcHousehold || sd.IsDead) continue;
                if ((pet ? sd.Species.ToString() : sd.Age.ToString()) != what) continue;
                Sim s = sd.CreatedSim;
                if (s.Posture != null && s.Posture.GetType().Name.IndexOf("RabbitHole", StringComparison.Ordinal) >= 0) continue;
                return s;
            }
            return null;
        }

        static Type IswType(string name)
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = null;
                try { t = asm.GetType(name, false); } catch (Exception) { }
                if (t != null) return t;
            }
            return null;
        }

        static string IswOuter(Type t)
        {
            while (t.DeclaringType != null) t = t.DeclaringType;
            return t.FullName;
        }

        static bool IswIsPackDef(InteractionDefinition d) { return d != null && sIswPackTypes.ContainsKey(IswOuter(d.GetType())); }

        // the pack type (or base) an object belongs to, null when none
        static string IswPackTypeOf(Type t)
        {
            for (; t != null && t != typeof(object); t = t.BaseType) if (sIswPackTypes.ContainsKey(t.FullName)) return t.FullName;
            return null;
        }

        // ------------------------------------------------------------ setup: targets
        static void IswSetup(int gen)
        {
            List<string> objTypes = new List<string>();
            int unresolved = 0;
            foreach (string n in sIswPackTypes.Keys)
            {
                Type t = IswType(n);
                if (t == null) { unresolved++; continue; }
                if (!t.IsAbstract && typeof(GameObject).IsAssignableFrom(t) && !typeof(Sim).IsAssignableFrom(t) && !typeof(Lot).IsAssignableFrom(t) && !typeof(Terrain).IsAssignableFrom(t)) objTypes.Add(n);
            }
            if (unresolved > 0) sIswNotes.Add(unresolved + " pack types not loaded (pack not installed or renamed)");
            // one pass over the world: pack instances per pack type, one instance per other concrete type
            Dictionary<string, List<GameObject>> packInst = new Dictionary<string, List<GameObject>>();
            Dictionary<string, GameObject> other = new Dictionary<string, GameObject>();
            int seen = 0;
            foreach (GameObject o in Sims3.Gameplay.Queries.GetObjects<GameObject>())
            {
                if (++seen % 500 == 0) { Simulator.Sleep(1); if (gen != sIswGen) return; }
                try
                {
                    if (o == null || o.HasBeenDestroyed || !o.InWorld || o is Sim || o is Lot || o is Terrain) continue;
                    string ct = o.GetType().FullName;
                    if (IswPackTypeOf(o.GetType()) != null)  // a pack type or a subclass of one: keyed by its concrete class
                    {
                        List<GameObject> l;
                        if (!packInst.TryGetValue(ct, out l)) { packInst[ct] = l = new List<GameObject>(); if (!objTypes.Contains(ct)) objTypes.Add(ct); }
                        if (l.Count < sIswObjects) l.Add(o);
                    }
                    else if (sIswWorldDefs && !other.ContainsKey(ct)) other[ct] = o;
                }
                catch (Exception) { }
            }
            objTypes.Sort(StringComparer.Ordinal);
            if (sIswBuy) IswBuyMissing(gen, objTypes, packInst);
            if (gen != sIswGen) return;
            foreach (string n in objTypes)
            {
                List<GameObject> l;
                if (!packInst.TryGetValue(n, out l)) { sIswNotes.Add("no instance: " + n); continue; }
                foreach (GameObject o in l) sIswTargets.Add(new IswTarget { Obj = o, Kind = o is Sims3.Gameplay.Abstracts.RabbitHole ? "rabbithole" : "object", Cls = n });
            }
            List<string> otherNames = new List<string>(other.Keys);
            otherNames.Sort(StringComparer.Ordinal);  // fixed order: the row index is resumable
            foreach (string n in otherNames)
                sIswTargets.Add(new IswTarget { Obj = other[n], Kind = other[n] is Sims3.Gameplay.Abstracts.RabbitHole ? "rabbithole" : "world", Cls = n, PackOnly = true });
            sIswTargets.Add(new IswTarget { Obj = Terrain.Singleton, Kind = "terrain", Cls = "Terrain", PackOnly = true });
            sIswTargets.Add(new IswTarget { Kind = "lot", Cls = "Lot", PackOnly = true });
            sIswTargets.Add(new IswTarget { Kind = "self", Cls = "Sim", PackOnly = true });
            if (sIswPartner != null) sIswTargets.Add(new IswTarget { Obj = sIswPartner, Kind = "social", Cls = "Sim", PackOnly = true });
            // actors to the spot once: queues cancelled, motives maxed, autonomy off (tracked; restored at cleanup)
            IswMoveNear(gen, IswAllSims(), sIswSpot);
        }

        static List<Sim> IswAllSims()
        {
            List<Sim> l = new List<Sim>();
            foreach (IswActor x in sIswActors) if (x.Kind != "household") l.Add(x.S);
            if (sIswPartner != null) l.Add(sIswPartner);
            return l;
        }

        // catalogue scan (CreateObjectOutOfWorld per product, as buy_object type): products whose names hint at a wanted class first
        static void IswBuyMissing(int gen, List<string> objTypes, Dictionary<string, List<GameObject>> packInst)
        {
            List<string> want = new List<string>();
            foreach (string n in objTypes)
            {
                if (packInst.ContainsKey(n) || sIswProduct.ContainsKey(n)) continue;
                Type t = IswType(n);
                if (t == null || typeof(Sims3.Gameplay.Abstracts.RabbitHole).IsAssignableFrom(t)) continue;  // rabbit holes: in-world only
                want.Add(n);
            }
            if (want.Count > 0)
            {
                List<string> shortNames = new List<string>();
                foreach (string n in want) shortNames.Add(n.Substring(n.LastIndexOf('.') + 1).ToLowerInvariant());
                List<BuildBuyProduct> first = new List<BuildBuyProduct>(), rest = new List<BuildBuyProduct>();
                foreach (BuildBuyProduct p in Catalog())
                {
                    string nm = ((p.ObjectInstanceName ?? "") + "|" + (p.CatalogName ?? "")).ToLowerInvariant();
                    bool hint = false;
                    foreach (string s in shortNames) if (nm.IndexOf(s, StringComparison.Ordinal) >= 0) { hint = true; break; }
                    (hint ? first : rest).Add(p);
                }
                first.AddRange(rest);
                int left = want.Count;
                Dictionary<string, bool> wanted = new Dictionary<string, bool>();
                foreach (string n in want) wanted[n] = true;
                foreach (BuildBuyProduct p in first)
                {
                    if (left == 0 || sIswScanned >= sIswScanMax || gen != sIswGen) break;
                    if (++sIswScanned % 10 == 0) Simulator.Sleep(1);
                    GameObject g = null;
                    try { g = GlobalFunctions.CreateObjectOutOfWorld(p.ProductResourceKey) as GameObject; } catch (Exception) { }
                    if (g == null) continue;
                    try
                    {
                        for (Type t = g.GetType(); t != null && t != typeof(object); t = t.BaseType)
                            if (wanted.ContainsKey(t.FullName) && !sIswProduct.ContainsKey(t.FullName)) { sIswProduct[t.FullName] = KeyString(p.ProductResourceKey); left--; }
                    }
                    finally { try { g.Destroy(); } catch (Exception) { } }
                }
                if (left > 0) sIswNotes.Add(left + " pack object types with no catalogue product in " + sIswScanned + " scanned (world-only, not buyable, or scanMax)");
            }
            // place one of each found product around the spot (2 m grid)
            int k = 0;
            foreach (string n in objTypes)
            {
                string key;
                if (packInst.ContainsKey(n) || !sIswProduct.TryGetValue(n, out key)) continue;
                GameObject go = null;
                try
                {
                    BuildBuyProduct prod = null;
                    foreach (BuildBuyProduct p in Catalog()) if (KeyString(p.ProductResourceKey) == key) { prod = p; break; }
                    if (prod == null) continue;
                    go = GlobalFunctions.CreateObjectOutOfWorld(prod.ProductResourceKey) as GameObject;
                    if (go == null) continue;
                    Vector3 pos = new Vector3(sIswSpot.x + 2f * (k % 6 - 2.5f), sIswSpot.y, sIswSpot.z + 2f * (k / 6 % 6 - 2.5f)), fwd = Vector3.UnitZ;
                    k++;
                    if (!GlobalFunctions.FindGoodLocationNearby(go, ref pos, ref fwd)) { go.Destroy(); sIswNotes.Add("no spot for " + n); continue; }
                    go.SetPosition(pos);
                    go.SetForward(fwd);
                    go.AddToWorld();
                    sIswPlaced.Add(go);
                    CreatedObjects().Add(go.ObjectId.Value);
                    sIswBought++;
                    packInst[n] = new List<GameObject> { go };
                }
                catch (Exception e) { sIswNotes.Add("place " + n + ": " + e.GetType().Name); try { if (go != null && !go.InWorld) go.Destroy(); } catch (Exception) { } }
                if (k % 5 == 0) { Simulator.Sleep(1); if (gen != sIswGen) return; }
            }
        }

        // cancel + teleport (setup_sims rules) and wait until the teleports ran (<= 3 s)
        static void IswMoveNear(int gen, List<Sim> sims, Vector3 at)
        {
            List<Sim> live = sims.FindAll(delegate(Sim s) { return s != null && !s.HasBeenDestroyed; });
            if (live.Count == 0) return;
            try { SetupSimsAt(live, null, at, true, true, false, false); }
            catch (Exception e) { sIswLast = "teleport: " + e.GetType().Name; }
            int t0 = System.Environment.TickCount;
            while (System.Environment.TickCount - t0 < 3000 && gen == sIswGen)
            {
                Simulator.Sleep(1);
                bool busy = false;
                foreach (Sim s in live) if (!s.HasBeenDestroyed && s.InteractionQueue != null && s.InteractionQueue.Count > 0) busy = true;
                if (!busy) break;
            }
        }

        // ------------------------------------------------------------ the sweep
        static void IswLoop(int gen)
        {
            for (int ti = 0; ti < sIswTargets.Count; ti++)
            {
                if (gen != sIswGen || sIswStop != null) return;
                if (System.Environment.TickCount - sIswT0 > sIswMaxMin * 60000L) { sIswStop = "time cap " + sIswMaxMin + " min"; sIswState = "capped"; return; }
                sIswActors.RemoveAll(delegate(IswActor x) { return x.S == null || x.S.HasBeenDestroyed || x.S.SimDescription == null || x.S.SimDescription.IsDead; });
                if (sIswActors.Count == 0) { sIswStop = "no actor left"; sIswState = "error"; return; }
                IswTarget t = sIswTargets[ti];
                try { IswTargetRows(gen, t); }
                catch (Exception e) { sIswExceptions++; sIswLast = "target " + t.Cls + ": " + DescribeError(e); }
                Simulator.Sleep(1);
            }
        }

        static GameObject IswTargetFor(IswTarget t, Sim actor)
        {
            if (t.Kind == "self") return actor;
            if (t.Kind == "lot") return actor.LotCurrent;
            return t.Obj;
        }

        static GameObjectHit IswHit(GameObject target, Sim actor)
        {
            if (target is Terrain || target is Lot)
            {
                GameObjectHit h = new GameObjectHit(target is Lot ? GameObjectHitType.LotTerrain : GameObjectHitType.Terrain);
                h.mPoint = actor.Position;
                Lot at = target as Lot ?? LotManager.GetLotAtPoint(actor.Position);
                h.mId = at != null ? at.LotId : 0;
                return h;
            }
            GameObjectHit oh = new GameObjectHit(GameObjectHitType.Object);
            oh.mPoint = target.Position;
            oh.mId = target.ObjectId.Value;
            return oh;
        }

        // every pair on the target for this actor, tested (BuildMenu without its "hidden when no tooltip" cut)
        static List<MenuEntry> IswEntries(Sim actor, GameObject target, bool packOnly, bool autonomous)
        {
            List<MenuEntry> result = new List<MenuEntry>();
            if (target == null || target.HasBeenDestroyed) return result;
            GameObjectHit hit = IswHit(target, actor);
            List<InteractionObjectPair> iops = target.GetAllInteractionsForActor(actor);
            if (iops == null) return result;
            foreach (InteractionObjectPair iop in iops)
            {
                if (iop == null || iop.InteractionDefinition == null || (packOnly && !IswIsPackDef(iop.InteractionDefinition))) continue;
                MenuEntry e = new MenuEntry();
                e.Iop = iop;
                try
                {
                    if (!iop.CheckIfInteractionValid()) { e.Reason = "code version"; e.Label = iop.InteractionDefinition.GetType().Name; result.Add(e); continue; }
                    InteractionInstanceParameters p = IswParams(iop, actor, hit, autonomous, null);
                    GreyedOutTooltipCallback tip = null;
                    InteractionTestResult r = iop.InteractionDefinition.Test(ref p, ref tip);
                    e.Enabled = IUtil.IsPass(r);
                    if (!e.Enabled)
                    {
                        string why = r.ToString();
                        if (tip != null) try { why += ": " + tip(); } catch (Exception) { }
                        e.Reason = why;
                    }
                    try { e.Label = InteractionDefinitionUtilities.GetInteractionNameForUI(ref p); } catch (Exception) { e.Label = iop.InteractionDefinition.GetType().Name; }
                    e.Params = p;
                }
                catch (Exception x) { e.Enabled = false; e.Reason = "test threw " + x.GetType().Name; if (e.Label == null) e.Label = iop.InteractionDefinition.GetType().Name; }
                result.Add(e);
            }
            return result;
        }

        static InteractionInstanceParameters IswParams(InteractionObjectPair iop, Sim actor, GameObjectHit hit, bool autonomous, List<object> picked)
        {
            return new InteractionInstanceParameters(iop, actor,
                new InteractionPriority(autonomous ? InteractionPriorityLevel.Autonomous : InteractionPriorityLevel.UserDirected),
                autonomous ? AutonomySearchType.Autonomy : AutonomySearchType.None, autonomous ? InteractionFlags.Autonomous : InteractionFlags.UserDirected, hit, picked, false);
        }

        static string IswDefId(InteractionDefinition d)
        {
            SocialInteractionA.Definition sd = d as SocialInteractionA.Definition;
            return d.GetType().FullName + (sd != null ? ":" + sd.ActionKey : "");
        }

        internal static string IswDestructiveMatch(string defFullName, List<string> extra)
        {
            foreach (string seg in defFullName.Split('.', '+'))
            {
                foreach (string p in kIswDestructive) if (seg.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return p;
                if (extra != null) foreach (string p in extra) if (seg.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return p;
            }
            return null;
        }

        static void IswTargetRows(int gen, IswTarget t)
        {
            // candidates: union over actors by def id + label, first-seen order (actor 0 first)
            List<IswCand> cands = new List<IswCand>();
            Dictionary<string, IswCand> byKey = new Dictionary<string, IswCand>();
            for (int ai = 0; ai < sIswActors.Count; ai++)
            {
                Sim actor = sIswActors[ai].S;
                GameObject target = IswTargetFor(t, actor);
                if (target == null || (target == actor && t.Kind != "self")) continue;
                List<MenuEntry> menu;
                try { menu = IswEntries(actor, target, t.PackOnly, false); }
                catch (Exception e) { sIswExceptions++; sIswLast = "menu " + t.Cls + ": " + e.GetType().Name; continue; }
                foreach (MenuEntry e in menu)
                {
                    string id = IswDefId(e.Iop.InteractionDefinition), key = id + "|" + e.Label;
                    IswCand c;
                    if (!byKey.TryGetValue(key, out c)) { c = new IswCand { Key = key, DefId = id, Label = e.Label }; byKey[key] = c; cands.Add(c); }
                    c.Per.Add(e); c.Who.Add(ai);
                }
            }
            foreach (IswCand c in cands)
            {
                int idx = sIswIdx++;
                if (idx < sIswFrom) continue;
                if (gen != sIswGen || sIswStop != null) return;
                if (System.Environment.TickCount - sIswT0 > sIswMaxMin * 60000L) { sIswIdx = idx; sIswStop = "time cap " + sIswMaxMin + " min"; sIswState = "capped"; return; }
                int pushed, tried;
                sIswPushedBy.TryGetValue(c.DefId, out pushed);
                sIswTriedBy.TryGetValue(c.DefId, out tried);
                if (pushed >= sIswMaxPerDef || tried >= 3 * sIswMaxPerDef) { sIswDup++; continue; }
                sIswTriedBy[c.DefId] = tried + 1;
                Dictionary<string, object> row = Util.Obj();
                row["i"] = idx; row["pack"] = sIswPack; row["cls"] = t.Cls; row["tk"] = t.Kind; row["def"] = c.DefId; row["name"] = c.Label;
                try { IswRow(gen, t, c, row); }
                catch (Exception e)
                {
                    sIswExceptions++;
                    row["ex"] = DescribeError(e);
                    IswResetActors();
                    row["reset"] = true;
                }
                IswLog(row);
            }
        }

        static void IswRow(int gen, IswTarget t, IswCand c, Dictionary<string, object> row)
        {
            string destr = sIswDestructive ? null : IswDestructiveMatch(c.DefId, sIswSkip);
            if (destr != null) { row["test"] = "skipped: destructive (" + destr + ")"; sIswSkipped++; return; }
            sIswTested++;
            MenuEntry pick = null;
            int who = -1;
            bool auto = false;
            List<string> fails = new List<string>();
            for (int k = 0; k < c.Per.Count; k++)
            {
                MenuEntry e = c.Per[k];
                if (e.Enabled) { pick = e; who = c.Who[k]; break; }
                if (fails.Count < 3) fails.Add(sIswActors[c.Who[k]].Kind + ": " + IswCut(e.Reason ?? "fail", 90));
            }
            // tuning-only (autonomous) definitions: retest as autonomous when user-directed is disallowed by tuning
            if (pick == null && sIswAutoRetry)
                for (int k = 0; k < c.Per.Count && pick == null; k++)
                {
                    MenuEntry e = c.Per[k];
                    if (e.Reason == null || e.Reason.IndexOf("DisallowUserDirected", StringComparison.Ordinal) < 0) continue;
                    Sim actor = sIswActors[c.Who[k]].S;
                    InteractionInstanceParameters p = IswParams(e.Iop, actor, IswHit(IswTargetFor(t, actor), actor), true, null);
                    GreyedOutTooltipCallback tip = null;
                    if (IUtil.IsPass(e.Iop.InteractionDefinition.Test(ref p, ref tip))) { e.Params = p; pick = e; who = c.Who[k]; auto = true; }
                }
            if (fails.Count > 0) row["fails"] = fails;
            if (pick == null) { row["test"] = "fail"; sIswFailed++; return; }
            sIswPass++;
            IswActor sa = sIswActors[who];
            Sim s = sa.S;
            GameObject target = IswTargetFor(t, s);
            row["test"] = auto ? "pass-auto" : "pass";
            row["actor"] = sa.Kind;
            // to the target first (its own slice), then push: Test again after the move (the position can change the answer)
            List<Sim> movers = new List<Sim> { s };
            if (t.Kind == "social" && sIswPartner != null) movers.Add(sIswPartner);
            Vector3 at = target is Lot || target is Terrain || target == s ? s.Position : target.Position;
            if ((s.Position - at).Length() > 8f || s.InteractionQueue.Count > 0) IswMoveNear(gen, movers, at);
            List<object> picked = null;
            try { List<KeyValuePair<string, object>> rows = PickerRows(pick); if (rows.Count > 0) { picked = new List<object> { rows[0].Value }; row["picked"] = IswCut(rows[0].Key, 40); } }
            catch (Exception) { }
            InteractionInstanceParameters pp = IswParams(pick.Iop, s, IswHit(target, s), auto, picked);
            GreyedOutTooltipCallback tip2 = null;
            InteractionTestResult again = pick.Iop.InteractionDefinition.Test(ref pp, ref tip2);
            if (!IUtil.IsPass(again)) { row["pushed"] = false; row["why"] = "retest after move: " + again; return; }
            InteractionInstance inst = pick.Iop.InteractionDefinition.CreateInstanceFromParameters(ref pp);
            if (inst == null) { row["pushed"] = false; row["why"] = "CreateInstance null"; return; }
            sIswRunErrors = 0; sIswRunError = null; sIswInRun = true;
            int t0 = System.Environment.TickCount;
            bool ran = false, imm = inst is IImmediateInteraction, queued;
            try
            {
                if (imm) { Simulator.AddObject(new ImmediateInteractionTask(s, inst)); queued = true; }
                else queued = s.InteractionQueue.Add(inst);
                row["pushed"] = queued;
                if (!queued) { row["why"] = "queue refused"; return; }
                sIswPushed++;
                int pushed;
                sIswPushedBy.TryGetValue(c.DefId, out pushed);
                sIswPushedBy[c.DefId] = pushed + 1;
                int ranAt = 0;
                while (System.Environment.TickCount - t0 < sIswSecs * 1000L && gen == sIswGen)
                {
                    Simulator.Sleep(1);
                    if (s.HasBeenDestroyed) break;
                    if (!ran && s.CurrentInteraction == inst) { ran = true; ranAt = System.Environment.TickCount; }
                    if (imm) continue;
                    if (!s.InteractionQueue.HasInteraction(inst) && (ran || System.Environment.TickCount - t0 > 1500)) break;
                }
                row["ran"] = ran;
                if (imm) row["imm"] = true;
                if (ran) { sIswRan++; row["ms"] = (int)(System.Environment.TickCount - ranAt); }
            }
            finally
            {
                sIswInRun = false;
                if (sIswRunErrors > 0) { row["se"] = sIswRunErrors; row["err"] = sIswRunError; }
                if (Sims3.UI.UIManager.GetModalWindow() != null) row["dialog"] = true;
                if (IswEndRun(gen, movers)) { row["reset"] = true; }
            }
        }

        // cancel; a Sim whose queue does not empty in 3 s is reset (EA's reset end state)
        static bool IswEndRun(int gen, List<Sim> sims)
        {
            foreach (Sim s in sims) try { if (!s.HasBeenDestroyed) s.InteractionQueue.CancelAllInteractions(); } catch (Exception) { }
            int t0 = System.Environment.TickCount;
            bool busy = true;
            while (busy && System.Environment.TickCount - t0 < 3000 && gen == sIswGen)
            {
                Simulator.Sleep(1);
                busy = false;
                foreach (Sim s in sims) if (!s.HasBeenDestroyed && (s.InteractionQueue.Count > 0 || s.CurrentInteraction != null)) busy = true;
            }
            if (!busy) return false;
            foreach (Sim s in sims) try { if (!s.HasBeenDestroyed) { s.SetObjectToReset(); sIswResets++; } } catch (Exception) { }
            return true;
        }

        static void IswResetActors()
        {
            foreach (Sim s in IswAllSims()) try { if (!s.HasBeenDestroyed) { s.SetObjectToReset(); sIswResets++; } } catch (Exception) { }
        }

        static string IswCut(string s, int n) { return s == null ? null : s.Length <= n ? s : s.Substring(0, n); }

        static void IswLog(Dictionary<string, object> row)
        {
            lock (sIswRows) sIswRows.Add(row);
            // the log copy fits the 470-character diag line: long fields cut first
            Dictionary<string, object> c = new Dictionary<string, object>(row);
            if (c.ContainsKey("name")) c["name"] = IswCut((string)c["name"], 40);
            if (c.ContainsKey("err")) c["err"] = IswCut((string)c["err"], 90);
            if (c.ContainsKey("ex")) c["ex"] = IswCut((string)c["ex"], 90);
            string line = "isweep row " + Json.Write(c);
            if (line.Length > 470) { c.Remove("fails"); line = "isweep row " + Json.Write(c); }
            try { DiagLine(IswCut(line, 470)); } catch (Exception) { }
        }

        static void IswCleanup()
        {
            foreach (GameObject g in sIswPlaced) try { if (!g.HasBeenDestroyed) g.Destroy(); } catch (Exception) { }
            sIswPlaced.Clear();
            foreach (Sim s in IswAllSims())
                try { if (!s.HasBeenDestroyed) AutonomyCmd(new Args(new Dictionary<string, object> { { "sim", s.SimDescription.SimDescriptionId.ToString() }, { "enabled", true } })); }
                catch (Exception) { }
        }

        static void IswTrapOn()
        {
            try
            {
                Type t = Type.GetType("ScriptCore.ExceptionTrap, ScriptCore");
                sIswTrapField = t != null ? t.GetField("OnScriptError", BindingFlags.Public | BindingFlags.Static) : null;
                if (sIswTrapField == null) { sIswTrap = "none: EA's ScriptCore (ErrorTrap not installed), per-row script errors not counted"; return; }
                if (sIswTrapHook != null) return;
                sIswTrapHook = Delegate.CreateDelegate(sIswTrapField.FieldType, typeof(Handlers).GetMethod("OnIswScriptError", BindingFlags.Public | BindingFlags.Static));
                sIswTrapField.SetValue(null, Delegate.Combine((Delegate)sIswTrapField.GetValue(null), sIswTrapHook));
                sIswTrap = "hooked";
            }
            catch (Exception x) { sIswTrapHook = null; sIswTrap = "hook failed: " + x.GetType().Name; }
        }

        static void IswTrapOff()
        {
            if (sIswTrapHook == null) return;
            try { sIswTrapField.SetValue(null, Delegate.Remove((Delegate)sIswTrapField.GetValue(null), sIswTrapHook)); sIswTrap = "off"; }
            catch (Exception x) { sIswTrap = "unhook failed: " + x.GetType().Name; return; }
            sIswTrapHook = null;
        }

        // never throws (game error path): every script error while a forced run is open counts for that row (any task: the
        // interaction may fail on another Sim's task or a one-shot it started)
        public static void OnIswScriptError(ScriptCore.ScriptProxy proxy, Exception e)
        {
            try
            {
                sIswErrors++;
                if (!sIswInRun) return;
                sIswRunErrors++;
                if (sIswRunError != null || e == null) return;
                string st = e.StackTrace ?? "";
                int nl = st.IndexOf('\n');
                sIswRunError = e.GetType().Name + " @ " + (nl > 0 ? st.Substring(0, nl) : st).Trim();
            }
            catch (Exception) { }
        }
    }
}
