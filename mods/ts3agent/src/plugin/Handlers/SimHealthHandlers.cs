using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Agent
{
    // sim_health {watch?: true, sample_ticks?: 2}: one row per instantiated Sim (LotManager.Actors) for soak health checks
    // (tools/harness/sim-health-report.ts): species, age, occults, scope (selected / household / npc), lot and room, a
    // hidden room it cannot leave (ts3fixes HrReachesOutside, docs/552), current interaction and how long it has been the
    // head, queue length, position, unseen state (ts3fixes UfWhy, 0 = unseen; the unseenfb walk-time hold), and counters
    // since the previous call:
    //   route_fails / route_locked: failed route plans and FailedBecauseDestinationPortalLocked ones (ts3fixes routefaillog
    //     rows, per SimDescription; only while switch routefaillog is on, at most 512 Sims a session);
    //   ended / failed / game_cancelled / user_cancelled: head interactions that left the queue, as the watcher saw them.
    //     failed = the Sim still carried a failure exit reason (FailedToStart, RouteFailed, ObjectInUse, SynchronizationFailed,
    //     NullValueFound, FailedToRemoveFromInventory, BuffFailureState, TimedOut) when the watcher saw the head go;
    //     game_cancelled = the instance was cancelled without UserCanceled. Approximate: EA clears the exit reasons when the
    //     next interaction starts, so a failure followed at once by the next head can read as a plain end; short = (< 1 sim
    //     minute) non-user ends that are not ours;
    //   spam: interaction definitions that failed (a failure exit reason above) >= 3 times since the last call; game
    //     cancels without one and short ends are by design often (SwitchToOutfit, LunacyFreakOut's reactions, IdleTend
    //     cancelled by an order) and are not spam;
    //   short_ends: definitions that ended short (< 1 sim minute, not user-cancelled, not ours) >= 3 times, lower ranked;
    //   ours / ours_ends: ends ts3fixes' own shortcuts cut (the Sim was in or just left an unseenfb hold, or ts3fixes.moved
    //     got a new move for it while the head ran, and the head then failed or was cancelled; a short end alone is the
    //     shortcut skipping the walk, helped): never spam;
    //   ours_why: the same ends by "definition: reason" (failure exit reason, or cancelled with its CancelledReason);
    //     a head cancelled only for an EA hand-off (HigherPriorityNext, PriorityOverride, StageComplete, Finished) is helped;
    //   helped / helped_ends: the same shortcut touched the head and it then completed (no cancel, no failure exit
    //     reason, not short; a rabbit-hole placement before EA's meal ran): normal ends, listed to judge the shortcut;
    //   hunger / energy: the Sim's motive values (-100..100; null when it has none), for judging meal and sleep loops;
    //   since_min (top level): sim minutes since the previous call, the span the counters cover;
    //   holds / hold_ends / after_hold: ts3fixes unseenfb walk-time holds started and ended, and the Sim's posture / walk
    //     style / terrain when one ended (oddities after a hold: a ridden horse, a mermaid off water, a flying vampire);
    //   moved / moved_new: ts3fixes' last shortcut move of the Sim (teleport, placement) and whether it is new.
    // oid is the object id in hex as ts3native's "follower" / "sim" fields print it, so the report joins its log lines
    // ("routefail job" cuts, "routefail retry" outcomes, "KD NPC CEILING CUT") to a Sim's species and occults.
    // Pure read except our own tables: nothing runs until the first call, which starts the watcher (a task that samples
    // each Sim's head interaction every sample_ticks sim ticks: one queue head read per Sim, no allocation unless a head
    // changed). watch: false stops it. The watcher ends by itself at world quit; the next call starts it again.
    public static partial class Handlers
    {
        static void RegisterSimHealth() { Dispatcher.Register("sim_health", SimHealth); }

        const ExitReason kShFail = ExitReason.FailedToStart | ExitReason.RouteFailed | ExitReason.ObjectInUse | ExitReason.SynchronizationFailed
            | ExitReason.NullValueFound | ExitReason.FailedToRemoveFromInventory | ExitReason.BuffFailureState | ExitReason.TimedOut;
        const int kShSpam = 3;
        // cancel reasons that are EA's normal hand-off to the next interaction, not a broken one
        const ExitReason kShHandOff = ExitReason.HigherPriorityNext | ExitReason.PriorityOverride | ExitReason.StageComplete | ExitReason.Finished;

        sealed class ShTrack
        {
            public WeakReference Head;             // weak: a Sim's ended interaction must not stay reachable through us
            public ulong HeadId;
            public string HeadName, HeadDef;
            public long HeadSince, PosSince;       // sim ticks
            public Vector3 Pos;
            public int Ended, Failed, GameCancelled, UserCancelled, Short;
            public int RfSeen, RfLocked;           // routefaillog totals at the previous call
            public bool Held;                      // in ts3fixes' unseenfb walk-time hold at the last sample
            public int Holds, HoldEnds;
            public readonly List<object> AfterHold = new List<object>();  // the Sim's state when a hold ended (at most 3 a call)
            public string Moved;                   // ts3fixes' last shortcut move of this Sim (ts3fixes.moved, by full name)
            public readonly Dictionary<string, int> Bad = new Dictionary<string, int>();     // failed, by definition (spam)
            public readonly Dictionary<string, int> ShortDefs = new Dictionary<string, int>();
            public readonly Dictionary<string, int> OursDefs = new Dictionary<string, int>();
            public readonly Dictionary<string, int> OursWhy = new Dictionary<string, int>();   // "definition: exit / cancel reason"
            public int Ours, Helped;
            public readonly Dictionary<string, int> HelpedDefs = new Dictionary<string, int>();
            public string HeadMoved;               // ts3fixes.moved for this Sim when the head started
            public bool HeadMovedKnown;
        }

        static readonly Dictionary<ulong, ShTrack> sShTrack = new Dictionary<ulong, ShTrack>();  // by Sim object id
        static bool sShWatching;
        static double sShLastMin = -1;         // sim_minutes at the previous call
        static int sShGen, sShSampleTicks = 2;
        static FieldInfo sShRflSims, sShRflSeen, sShRflUnseen, sShRflLocked, sShHolding;
        static MethodInfo sShUfWhy, sShHrOut;
        static bool sShLooked;

        static object SimHealth(Args a)
        {
            ShLookUp();
            sShSampleTicks = (int)Math.Max(1, Math.Min(60, a.Long("sample_ticks", sShSampleTicks)));
            bool watch = a.Bool("watch", true);
            if (!watch) { sShGen++; sShWatching = false; }
            else if (!sShWatching) { sShWatching = true; int gen = ++sShGen; Simulator.AddObject(new ShTask(gen)); }
            long now = SimClock.CurrentTicks;
            IDictionary rfl = sShRflSims != null ? sShRflSims.GetValue(null) as IDictionary : null;
            Household active = Household.ActiveHousehold;
            IActor selected = Sims3.Gameplay.Core.PlumbBob.SelectedActor;
            List<object> rows = new List<object>();
            Dictionary<ulong, bool> live = new Dictionary<ulong, bool>();
            foreach (Sim s in new List<Sim>(LotManager.Actors))
            {
                if (s == null || s.HasBeenDestroyed || s.SimDescription == null) continue;
                try { rows.Add(ShRow(s, now, rfl, active, selected)); }
                catch (Exception e) { Dictionary<string, object> bad = Util.Obj(); bad["id"] = Util.Id(s.ObjectId); bad["error"] = e.GetType().Name + ": " + e.Message; rows.Add(bad); }
                live[s.ObjectId.Value] = true;
            }
            List<ulong> gone = new List<ulong>();
            foreach (ulong id in sShTrack.Keys) if (!live.ContainsKey(id)) gone.Add(id);
            foreach (ulong id in gone) sShTrack.Remove(id);
            Dictionary<string, object> r = Util.Obj();
            r["tick"] = now;
            double simMin = Math.Round(SimClock.ElapsedTime(TimeUnit.Minutes), 1);
            r["sim_minutes"] = simMin;
            r["since_min"] = sShLastMin >= 0 && simMin >= sShLastMin ? Math.Round(simMin - sShLastMin, 1) : 0;  // 0: first call (or clock reset)
            sShLastMin = simMin;
            r["hour"] = Math.Round(SimClock.HoursPassedOfDay, 2);
            r["live"] = Sims3.Gameplay.GameStates.IsLiveState;
            r["watching"] = sShWatching;
            r["routefaillog"] = rfl != null;
            r["count"] = rows.Count;
            r["sims"] = rows;
            return r;
        }

        static Dictionary<string, object> ShRow(Sim s, long now, IDictionary rfl, Household active, IActor selected)
        {
            SimDescription d = s.SimDescription;
            ShTrack t = ShTrackOf(s, now);
            Dictionary<string, object> o = Util.Obj();
            o["id"] = Util.Id(s.ObjectId);
            o["oid"] = s.ObjectId.Value.ToString("X16");  // as ts3native's "follower" / "sim" fields and ts3fixes' "follower" in its log lines
            o["desc"] = Util.Id(d.SimDescriptionId);
            o["name"] = s.FullName;
            o["species"] = d.IsHorse ? "horse" : d.IsCat ? "cat" : d.IsADogSpecies ? "dog" : d.IsHuman ? "human" : "other";
            o["age"] = d.Age.ToString().ToLowerInvariant();
            o["occults"] = ShOccults(s, d);
            o["scope"] = s == selected ? "selected" : active != null && d.Household == active ? "household" : d.Household == null ? "no_household" : "npc";
            Lot lot = s.LotCurrent;
            int room = s.RoomId;
            o["lot"] = lot != null ? Util.LotName(lot) : null;
            o["lot_id"] = lot != null ? Util.Id(lot.LotId) : null;
            o["room"] = room;
            bool hidden = lot != null && !lot.IsWorldLot && !LotManager.RoomIdIsOutside(room) && lot.IsRoomHidden(room);
            o["hidden_room"] = hidden;
            o["trapped"] = ShTrapped(s, lot, room);  // true / false / null (outside, world lot, unknown)
            Vector3 p = s.Position;
            o["x"] = Math.Round(p.x, 1);
            o["z"] = Math.Round(p.z, 1);
            o["still_min"] = Math.Round(SimClock.ConvertFromTicks(now - t.PosSince, TimeUnit.Minutes), 1);
            o["interaction"] = t.HeadName;
            o["definition"] = t.HeadDef;
            o["interaction_min"] = t.HeadName != null ? Math.Round(SimClock.ConvertFromTicks(now - t.HeadSince, TimeUnit.Minutes), 1) : 0.0;
            o["queue"] = s.InteractionQueue != null ? s.InteractionQueue.Count : 0;
            bool routing = false;
            try { routing = s.SimRoutingComponent != null && s.SimRoutingComponent.IsRouting; } catch (Exception) { }
            o["routing"] = routing;
            o["posture"] = ShPosture(s);
            o["hunger"] = ShMotive(s, CommodityKind.Hunger); o["energy"] = ShMotive(s, CommodityKind.Energy);
            o["walk_style"] = ShWalkStyle(s);
            o["terrain"] = ShTerrain(p);
            o["unseen_why"] = ShUfWhy(s);
            o["uf_hold"] = ShHeld(s, now);
            // ts3fixes autlastsearch (docs/584 §4): the last autonomy search's result and how long ago (null: none recorded / switch off)
            Dictionary<ulong, string> ls = AppDomain.CurrentDomain.GetData("ts3fixes.lastsearch") as Dictionary<ulong, string>;
            string lsv;
            object lsWhat = null, lsMin = null;
            if (ls != null && ls.TryGetValue(d.SimDescriptionId, out lsv))
            {
                int bar = lsv.IndexOf('|');
                long at;
                if (bar > 0 && long.TryParse(lsv.Substring(0, bar), out at)) { lsWhat = lsv.Substring(bar + 1); lsMin = Math.Round(SimClock.ConvertFromTicks(now - at, TimeUnit.Minutes), 1); }
            }
            o["last_search"] = lsWhat;
            o["last_search_min"] = lsMin;
            // counters since the last call
            int seen = 0, locked = 0;
            object row = rfl != null && rfl.Contains(d.SimDescriptionId) ? rfl[d.SimDescriptionId] : null;
            if (row != null)
            {
                seen = (int)sShRflSeen.GetValue(row) + (int)sShRflUnseen.GetValue(row);
                locked = sShRflLocked != null ? (int)sShRflLocked.GetValue(row) : 0;
            }
            o["route_fails"] = Math.Max(0, seen - t.RfSeen);
            o["route_locked"] = Math.Max(0, locked - t.RfLocked);
            t.RfSeen = seen; t.RfLocked = locked;
            o["ended"] = t.Ended; o["failed"] = t.Failed; o["game_cancelled"] = t.GameCancelled; o["user_cancelled"] = t.UserCancelled; o["short"] = t.Short;
            o["spam"] = ShDefs(t.Bad, kShSpam);
            o["short_ends"] = ShDefs(t.ShortDefs, kShSpam);
            o["ours"] = t.Ours; o["ours_ends"] = ShDefs(t.OursDefs, 1); o["ours_why"] = ShDefs(t.OursWhy, 1);
            o["helped"] = t.Helped; o["helped_ends"] = ShDefs(t.HelpedDefs, 1);
            o["holds"] = t.Holds; o["hold_ends"] = t.HoldEnds; o["after_hold"] = new List<object>(t.AfterHold);
            t.Holds = t.HoldEnds = 0; t.AfterHold.Clear();
            Dictionary<string, object> moved = AppDomain.CurrentDomain.GetData("ts3fixes.moved") as Dictionary<string, object>;
            object mv;
            string m = moved != null && moved.TryGetValue(s.FullName, out mv) ? mv as string : null;
            o["moved"] = m;
            o["moved_new"] = m != null && m != t.Moved;  // a shortcut moved this Sim since the last call (the last move only)
            t.Moved = m;
            t.Ended = t.Failed = t.GameCancelled = t.UserCancelled = t.Short = t.Ours = t.Helped = 0;
            t.Bad.Clear(); t.ShortDefs.Clear(); t.OursDefs.Clear(); t.OursWhy.Clear(); t.HelpedDefs.Clear();
            return o;
        }

        static object ShMotive(Sim s, CommodityKind k)
        {
            return s.Motives != null && s.Motives.HasMotive(k) ? (object)Math.Round(s.Motives.GetMotiveValue(k), 1) : null;
        }

        static List<object> ShDefs(Dictionary<string, int> defs, int min)
        {
            List<object> l = new List<object>();
            foreach (KeyValuePair<string, int> kv in defs)
                if (kv.Value >= min) { Dictionary<string, object> sp = Util.Obj(); sp["definition"] = kv.Key; sp["count"] = kv.Value; l.Add(sp); }
            return l;
        }

        static void ShBump(Dictionary<string, int> defs, string def)
        {
            if (def == null) return;
            int n;
            defs.TryGetValue(def, out n);
            defs[def] = n + 1;
        }

        static List<object> ShOccults(Sim s, SimDescription d)
        {
            List<object> l = new List<object>();
            OccultManager m = d.OccultManager;
            if (m != null)
            {
                if (m.HasOccultType(Sims3.UI.Hud.OccultTypes.Vampire)) l.Add("vampire");
                if (m.HasOccultType(Sims3.UI.Hud.OccultTypes.Werewolf)) l.Add("werewolf");
                if (m.HasOccultType(Sims3.UI.Hud.OccultTypes.Witch)) l.Add("witch");
                if (m.HasOccultType(Sims3.UI.Hud.OccultTypes.Fairy)) l.Add("fairy");
                if (m.HasOccultType(Sims3.UI.Hud.OccultTypes.Genie)) l.Add("genie");
                if (m.HasOccultType(Sims3.UI.Hud.OccultTypes.Mermaid)) l.Add("mermaid");
                if (m.HasOccultType(Sims3.UI.Hud.OccultTypes.PlantSim)) l.Add("plantsim");
                if (m.HasOccultType(Sims3.UI.Hud.OccultTypes.Robot)) l.Add("plumbot");
                if (m.HasOccultType(Sims3.UI.Hud.OccultTypes.Frankenstein)) l.Add("simbot");
                if (m.HasOccultType(Sims3.UI.Hud.OccultTypes.Mummy)) l.Add("mummy");
                if (m.HasOccultType(Sims3.UI.Hud.OccultTypes.Unicorn)) l.Add("unicorn");
                if (m.HasOccultType(Sims3.UI.Hud.OccultTypes.ImaginaryFriend)) l.Add("imaginary_friend");
            }
            if (d.IsGhost) l.Add("ghost");
            if (d.IsZombie) l.Add("zombie");
            if (s.BuffManager != null && s.BuffManager.HasElement(BuffNames.PermaZombie)) l.Add("permazombie");
            return l;
        }

        static ShTrack ShTrackOf(Sim s, long now)
        {
            ShTrack t;
            if (sShTrack.TryGetValue(s.ObjectId.Value, out t)) return t;
            t = new ShTrack();
            t.Pos = s.Position; t.PosSince = now;
            InteractionInstance h = s.InteractionQueue != null ? s.InteractionQueue.GetHeadInteraction() : null;
            ShSetHead(t, h, now);
            sShTrack[s.ObjectId.Value] = t;
            return t;
        }

        static void ShSetHead(ShTrack t, InteractionInstance h, long now)
        {
            t.Head = h != null ? new WeakReference(h) : null;
            t.HeadId = h != null ? h.Id : 0;
            t.HeadSince = now;
            t.HeadName = null; t.HeadDef = null;
            if (h == null) return;
            try { t.HeadName = h.GetInteractionName(); } catch (Exception) { t.HeadName = h.GetType().Name; }
            t.HeadDef = h.InteractionDefinition != null ? h.InteractionDefinition.GetType().FullName : h.GetType().FullName;
        }

        // one watcher step: every Sim's head compared with the one seen before; a head that left the queue is classified
        static void ShSample()
        {
            long now = SimClock.CurrentTicks, shortTicks = SimClock.ConvertToTicks(1f, TimeUnit.Minutes);
            Dictionary<ulong, long> holding = AppDomain.CurrentDomain.GetData("ts3fixes.unseenfb.holding") as Dictionary<ulong, long>;
            Dictionary<string, object> moved = AppDomain.CurrentDomain.GetData("ts3fixes.moved") as Dictionary<string, object>;
            foreach (Sim s in LotManager.Actors)
            {
                if (s == null || s.HasBeenDestroyed || s.InteractionQueue == null) continue;
                ShTrack t;
                if (!sShTrack.TryGetValue(s.ObjectId.Value, out t)) continue;  // new Sims join at the next call
                Vector3 p = s.Position;
                if ((p - t.Pos).LengthSqr() > 0.25f) { t.Pos = p; t.PosSince = now; }
                long until;
                bool held = holding != null && holding.TryGetValue(s.ObjectId.Value, out until) && now <= until;
                bool holdChanged = held != t.Held;
                if (held != t.Held)
                {
                    t.Held = held;
                    if (held) t.Holds++;
                    else
                    {
                        t.HoldEnds++;
                        if (t.AfterHold.Count < 3)
                            t.AfterHold.Add(ShPosture(s) + " / " + ShWalkStyle(s) + " / " + ShTerrain(p) + (s.SimRoutingComponent != null && s.SimRoutingComponent.IsRouting ? " / routing" : ""));
                    }
                }
                InteractionInstance h = s.InteractionQueue.GetHeadInteraction();
                ulong id = h != null ? h.Id : 0;
                if (id == t.HeadId) continue;
                InteractionInstance old = t.Head != null ? t.Head.Target as InteractionInstance : null;
                if (old != null && s.InteractionQueue.HasInteraction(old)) { ShSetHead(t, h, now); continue; }  // pushed back, not ended
                object mv;
                string m = moved != null && moved.TryGetValue(s.FullName, out mv) ? mv as string : null;  // only on a head change
                if (t.HeadId != 0)
                {
                    t.Ended++;
                    ExitReason why = s.ExitReason;
                    bool user = (why & ExitReason.UserCanceled) != 0, cancelled = old != null && old.Cancelled;
                    bool touched = !user && (held || holdChanged || (t.HeadMovedKnown && m != null && m != t.HeadMoved));
                    bool fail = (why & kShFail) != 0, isShort = now - t.HeadSince < shortTicks;
                    // EA's own hand-off: the head was cancelled because the next one outranks it (AfterExitingRabbitHole pushes
                    // GoHome at High priority once FinishWorking ran, so the work head ends HigherPriorityNext): completed,
                    // not ended by us. A cancel with no reason recorded stays ours.
                    ExitReason cr = old != null ? old.CancelledReason : ExitReason.None;
                    bool handOff = cancelled && !fail && cr != ExitReason.None && (cr & ~kShHandOff) == 0;
                    if (user) t.UserCancelled++;
                    // a touched head that ended short with no failure and no cancel finished early because the shortcut skipped its
                    // walk (an unseen GoHome placed at home ends at once): completed, not ours
                    else if (touched && (fail || (cancelled && !handOff)))
                    {
                        t.Ours++; ShBump(t.OursDefs, t.HeadDef);  // ended by us
                        ShBump(t.OursWhy, t.HeadDef + ": " + (fail ? "failed " + (why & kShFail) : "cancelled " + cr) + (isShort && (fail || cancelled) ? ", short" : ""));
                    }
                    else if (touched) { t.Helped++; ShBump(t.HelpedDefs, t.HeadDef); }  // helped by us: completed
                    else
                    {
                        if (fail) { t.Failed++; ShBump(t.Bad, t.HeadDef); }
                        else if (cancelled) t.GameCancelled++;
                        if (isShort) { t.Short++; ShBump(t.ShortDefs, t.HeadDef); }
                    }
                }
                t.HeadMoved = m; t.HeadMovedKnown = true;
                ShSetHead(t, h, now);
            }
        }

        sealed class ShTask : Task
        {
            readonly int mGen;
            public ShTask(int gen) { mGen = gen; }
            public override void Simulate()
            {
                try
                {
                    while (mGen == sShGen && !Sims3.Gameplay.GameStates.IsWorldShuttingDown)
                    {
                        if (Sims3.Gameplay.GameStates.IsLiveState) { try { ShSample(); } catch (Exception) { } }
                        Simulator.Sleep((uint)sShSampleTicks);
                    }
                }
                finally { if (mGen == sShGen) sShWatching = false; Simulator.DestroyObject(ObjectId); }
            }
        }

        static string ShPosture(Sim s)
        {
            try { return s.Posture != null ? s.Posture.GetType().Name : "none"; } catch (Exception) { return "?"; }
        }
        static string ShWalkStyle(Sim s)
        {
            try { return s.SimRoutingComponent != null ? s.SimRoutingComponent.CurrentWalkStyle.ToString() : "none"; } catch (Exception) { return "?"; }
        }
        static string ShTerrain(Vector3 p)
        {
            try { return World.GetTerrainType(p).ToString(); } catch (Exception) { return "?"; }
        }

        // ts3fixes members, found once (null when that build lacks them)
        static void ShLookUp()
        {
            if (sShLooked) return;
            sShLooked = true;
            const BindingFlags st = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type fx = null;
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                fx = asm.GetType("Ts3Fixes.Fixes");
                if (fx != null) break;
            }
            if (fx == null) return;
            sShRflSims = fx.GetField("sRflSims", st);
            Type row = fx.GetNestedType("RflRow", BindingFlags.Public | BindingFlags.NonPublic);
            if (row != null) { sShRflSeen = row.GetField("Seen", inst); sShRflUnseen = row.GetField("Unseen", inst); sShRflLocked = row.GetField("Locked", inst); }
            if (sShRflSeen == null || sShRflUnseen == null) sShRflSims = null;
            sShUfWhy = fx.GetMethod("UfWhy", st, null, new Type[] { typeof(Sim) }, null);
            sShHrOut = fx.GetMethod("HrReachesOutside", st, null, new Type[] { typeof(Lot), typeof(int), typeof(Sim), typeof(int).MakeByRefType() }, null);
        }

        static object ShUfWhy(Sim s)
        {
            if (sShUfWhy == null) return null;
            try { return (int)sShUfWhy.Invoke(null, new object[] { s }); } catch (Exception) { return null; }
        }

        static bool ShHeld(Sim s, long now)
        {
            Dictionary<ulong, long> d = AppDomain.CurrentDomain.GetData("ts3fixes.unseenfb.holding") as Dictionary<ulong, long>;
            long until;
            return d != null && d.TryGetValue(s.ObjectId.Value, out until) && now <= until;
        }

        // no portal the Sim may pass leads from its room to the outside (docs/552, EA's own door rules for this Sim: one
        // bounded room flood per indoor Sim per call); null = outside, world lot or unknown
        static bool? ShTrapped(Sim s, Lot lot, int room)
        {
            if (sShHrOut == null || lot == null || lot.IsWorldLot || LotManager.RoomIdIsOutside(room)) return null;
            try
            {
                object v = sShHrOut.Invoke(null, new object[] { lot, room, s, 0 });
                return v == null ? (bool?)null : !(bool)v;
            }
            catch (Exception) { return null; }
        }
    }
}
