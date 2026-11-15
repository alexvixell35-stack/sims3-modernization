using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.EventSystem;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Opportunities;
using Sims3.Gameplay.Roles;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using Sims3.SimIFace.CAS;
using Sims3.UI.CAS;

namespace Ts3Agent
{
    // Repro actions for designed-but-unbuilt checks (2026-11-15 lane L10): ilpatch B80 / B87 per-fix checks (docs/506 §8),
    // the bouncerskip venue setup (docs/170 §5), the one-sided relationship census (docs/535 §4), NRaas resets of
    // uncontrolled Sims for that census, and the EventTracker dispatch-cost probe (docs/545 §5, ts3fixes EventCost.cs).
    // Household rule: every action that touches Sims refuses household, selected and unknown-scope Sims (DrNpc).
    public static partial class Handlers
    {
        static void RegisterDesignedRepros()
        {
            Dispatcher.Register("ilp_remote_chat_check", IlpRemoteChatCheck);
            Dispatcher.Register("ilp_scavenger_check", IlpScavengerCheck);
            Dispatcher.Register("velvet_bouncer", VelvetBouncer);
            Dispatcher.Register("rel_onesided", RelOneSided);
            Dispatcher.Register("nraas_reset_sims", NraasResetSims);
            Dispatcher.Register("event_cost", EventCost);
        }

        // An uncontrolled Sim: has a household, not the active one, not selectable or selected. No household = unknown
        // scope = counts as household (CLAUDE.md).
        static bool DrNpc(SimDescription sd)
        {
            if (sd == null || !sd.IsValidDescription || sd.Household == null || sd.Household == Household.ActiveHousehold) return false;
            Sim s = sd.CreatedSim;
            return s == null || (s != PlumbBob.SelectedActor && !s.IsSelectable);
        }

        static void DrExpect(Dictionary<string, object> r, string expect, bool threw)
        {
            r["threw"] = threw;
            if (expect == null) return;
            if (expect != "throw" && expect != "ok") throw new McpException("expect must be throw or ok");
            if ((expect == "throw") != threw)
                throw new McpException("wrong outcome: expected " + expect + ", got " + (threw ? "throw (" + r["error"] + ")" : "ok") + " " + Json.Write(r));
        }

        // ilp_remote_chat_check {actor?, target?, expect?: "throw"|"ok"}: docs/506 §8 B80 repro. Sim.DoOneRoundOfRemoteChat
        // shows the target's portrait (kChanceOfDisplayingTargetForRemoteChat, forced to 2 for this call) from its first
        // Everyday outfit; with none, EA's ThumbnailKey(SimOutfit) reads outfit.Key of null and throws [verified decompiled].
        // The target's Everyday list is swapped for an empty one for this one synchronous call and put back in a finally,
        // the tuning too, so no tick sees either change. Actor and target are uncontrolled Sims only (the chat updates
        // their relationship). expect "throw" = ilpatch b80 off (EA), "ok" = ilp_remotechatnoeverydayoutfit on.
        static object IlpRemoteChatCheck(Args a)
        {
            RequireLive();
            Sim actor = null;
            SimDescription target = null;
            if (a.Has("actor")) actor = Util.FindSim(a, "actor");
            if (a.Has("target")) target = Util.FindSimDescription(a, "target");
            foreach (SimDescription sd in SimDescription.GetSimDescriptionsInWorld())
            {
                if (!DrNpc(sd) || sd.IsPet || !sd.TeenOrAbove) continue;
                if (actor == null && Util.InWorldNow(sd)) { actor = sd.CreatedSim; continue; }
                if (target == null && (actor == null || sd != actor.SimDescription) && !sd.IsUsingMaternityOutfits && sd.GetOutfit(OutfitCategories.Everyday, 0) != null) target = sd;
                if (actor != null && target != null) break;
            }
            if (actor == null || target == null) throw new McpException("no uncontrolled actor (in world) and target (not pregnant, with an Everyday outfit)");
            if (!DrNpc(actor.SimDescription) || !DrNpc(target)) throw new McpException("actor and target must be uncontrolled Sims (not household, selected or without a household)");
            FieldInfo chance = typeof(Sim).GetField("kChanceOfDisplayingTargetForRemoteChat", BindingFlags.NonPublic | BindingFlags.Static);
            if (chance == null) throw new McpException("Sim.kChanceOfDisplayingTargetForRemoteChat not found");
            Dictionary<string, object> r = Util.Obj();
            r["actor"] = actor.FullName;
            r["target"] = target.FullName;
            object oldChance = chance.GetValue(null);
            Hashtable outfits = target.Outfits;
            object oldEveryday = outfits[OutfitCategories.Everyday];
            bool threw = false;
            try
            {
                chance.SetValue(null, 2f);
                outfits[OutfitCategories.Everyday] = new ArrayList();
                r["outfit_hidden"] = target.GetOutfit(OutfitCategories.Everyday, 0) == null;
                actor.DoOneRoundOfRemoteChat(target);
            }
            catch (Exception e) { threw = true; r["error"] = DescribeError(e); }
            finally
            {
                outfits[OutfitCategories.Everyday] = oldEveryday;
                chance.SetValue(null, oldChance);
            }
            r["outfit_back"] = target.GetOutfit(OutfitCategories.Everyday, 0) != null;
            DrExpect(r, a.Str("expect", null), threw);
            return r;
        }

        // ilp_scavenger_check {expect?: "throw"|"ok"}: docs/506 §8 B87 repro. ScavengerOpportunity.OnAdded spawns the missing
        // gems / metals / insects on community lots and calls Simulator.Sleep(0) after each. A clone of each static scavenger
        // opportunity (attached to no Sim) runs OnAdded with Simulator.YieldingDisabled set, one of the two contexts where
        // CheckYieldingContext(false) is false; the first clone that spawns or throws decides. Every object it spawned is
        // destroyed again. [inferred] EA's Sleep under YieldingDisabled throws in native ("non-yielding context"): the off
        // arm (expect "throw") checks that; "ok" = ilp_scavengersleep on. Error (N/A) when no opportunity spawned anything.
        static object IlpScavengerCheck(Args a)
        {
            RequireLive();
            FieldInfo added = typeof(ScavengerOpportunity).GetField("mObjectsAdded", BindingFlags.NonPublic | BindingFlags.Instance);
            if (added == null) throw new McpException("ScavengerOpportunity.mObjectsAdded not found");
            Dictionary<string, object> r = Util.Obj();
            int tried = 0;
            // Force the condition: OnAdded only spawns what the world lacks, so first remove every loose gem / metal / insect
            // jig lying on a COMMUNITY lot (not the household lot, not inventories, not parented). Test world only, no save.
            int removed = 0;
            List<IGameObject> loose = new List<IGameObject>();
            foreach (IGameObject o in Sims3.Gameplay.Queries.GetObjects<IGem>()) loose.Add(o);
            foreach (IGameObject o in Sims3.Gameplay.Queries.GetObjects<IMetal>()) loose.Add(o);
            foreach (IGameObject o in Sims3.Gameplay.Queries.GetObjects<IInsectJig>()) loose.Add(o);
            foreach (IGameObject o in loose)
            {
                try
                {
                    Lot l = o.LotCurrent;
                    if (l == null || !l.IsCommunityLot || o.InInventory || o.Parent != null) continue;
                    o.Destroy();
                    removed++;
                }
                catch (Exception) { }
            }
            r["removed"] = removed;
            foreach (OpportunityNames n in Enum.GetValues(typeof(OpportunityNames)))
            {
                ScavengerOpportunity proto = OpportunityManager.GetStaticOpportunity(n) as ScavengerOpportunity;
                if (proto == null) continue;
                tried++;
                ScavengerOpportunity opp = proto.Clone() as ScavengerOpportunity;
                if (opp == null) continue;
                bool threw = false, oldYd = Simulator.YieldingDisabled;
                try
                {
                    Simulator.YieldingDisabled = true;
                    opp.OnAdded();
                }
                catch (Exception e) { threw = true; r["error"] = DescribeError(e); }
                finally { Simulator.YieldingDisabled = oldYd; }
                int spawned = 0;
                List<ObjectGuid> objs = added.GetValue(opp) as List<ObjectGuid>;
                if (objs != null)
                    foreach (ObjectGuid g in objs)
                    {
                        spawned++;
                        try { IScriptProxy p = Simulator.GetProxy(g); IGameObject go = p != null ? p.Target as IGameObject : null; if (go != null) go.Destroy(); }
                        catch (Exception) { }
                    }
                if (!threw && spawned == 0) continue;  // the world already holds enough of that item: no spawn, no sleep
                r["opportunity"] = n.ToString();
                r["spawned"] = spawned;
                r["tried"] = tried;
                DrExpect(r, a.Str("expect", null), threw);
                return r;
            }
            throw new McpException("N/A: none of " + tried + " scavenger opportunities spawned anything (the world holds enough of every item)");
        }

        // velvet_bouncer {lot?}: docs/170 §5 bouncerskip repro setup. Finds a velvet rope (IVelvetRopes; BP clubs), forces the
        // role manager to simulate, and once the Bouncer role has a Sim: teleports it to the rope's RoutingSlot_0 (EA's own
        // hidden-room move) and pushes WatchDoor (EA's PushWatchDoor), whose loop re-routes it to RoutingSlot_2 every 5 s
        // (VelvetRopes+WatchDoor.LoopDel, the bouncerskip site). Run the clock to night first (test_env {hour: 22}); poll this
        // step with until watching. Refuses a household or unknown-scope bouncer.
        static object VelvetBouncer(Args a)
        {
            RequireLive();
            IVelvetRopes[] all = Sims3.Gameplay.Queries.GetObjects<IVelvetRopes>();
            Dictionary<string, object> r = Util.Obj();
            r["ropes"] = all.Length;
            if (all.Length == 0) throw new McpException("no velvet rope in this world (use Bridgeport)");
            string want = a.Str("lot", null);
            List<object> lots = new List<object>();
            IVelvetRopes pick = null;
            foreach (IVelvetRopes v in all)
            {
                Lot l = v.LotCurrent;
                string name = l != null ? l.Name : "?";
                if (!lots.Contains(name)) lots.Add(name);
                if (pick != null) continue;
                if (want != null && (l == null || name.IndexOf(want, StringComparison.OrdinalIgnoreCase) < 0)) continue;
                IRoleGiver g = v as IRoleGiver;
                // the first rope with a bouncer Sim, else the first rope
                if (g != null && g.CurrentRole != null && g.CurrentRole.SimInRole != null) pick = v;
            }
            if (pick == null) foreach (IVelvetRopes v in all) if (want == null || (v.LotCurrent != null && v.LotCurrent.Name.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0)) { pick = v; break; }
            r["lots"] = lots;
            if (pick == null) throw new McpException("no velvet rope on a lot named like '" + want + "'");
            r["lot"] = pick.LotCurrent != null ? pick.LotCurrent.Name : null;
            r["id"] = Util.Id(pick.ObjectId);
            r["hour"] = Math.Round(SimClock.HoursPassedOfDay, 2);
            RoleManager.sForceSimulate = true;
            IRoleGiver giver = pick as IRoleGiver;
            Role role = giver != null ? giver.CurrentRole : null;
            Sim b = role != null ? role.SimInRole : null;
            r["role"] = role != null;
            r["role_active"] = role != null && role.IsActive;
            r["watching"] = false;
            if (b == null) { r["why"] = "no bouncer Sim yet (night hours, role manager pass)"; return r; }
            if (!DrNpc(b.SimDescription)) throw new McpException(b.FullName + " is household, selected or of unknown scope: not touched");
            r["bouncer"] = b.FullName;
            if (!role.IsActive) { r["why"] = "role not active yet"; return r; }
            InteractionInstance cur = b.CurrentInteraction;
            bool watching = cur != null && cur.Target == pick && cur.GetType().Name == "WatchDoor";
            if (!watching)
            {
                if (b.LotCurrent != pick.LotCurrent) b.SetPosition(pick, Slot.RoutingSlot_0);
                b.InteractionQueue.CancelAllInteractions();
                r["pushed"] = pick.PushWatchDoor(b, new InteractionPriority(InteractionPriorityLevel.High));
            }
            r["watching"] = watching;
            Sims3.Gameplay.Abstracts.GameObject pg = pick as Sims3.Gameplay.Abstracts.GameObject;
            if (pg != null) r["slot2_m"] = Math.Round((b.Position - pg.GetPositionOfSlot(Slot.RoutingSlot_2)).Length(), 2);
            return r;
        }

        // rel_onesided {sample?: 8}: docs/535 §4 census, light (one pass over Relationship.sAllRelationships). Pairs where only
        // one table holds the other (the state reltablesapart rejoins), pairs whose two tables hold different objects, and a
        // sample naming both Sims (household flag, instantiated, which side is missing). Reads only.
        static object RelOneSided(Args a)
        {
            int want = (int)a.Long("sample", 8);
            Dictionary<string, object> r = Util.Obj();
            int tables = 0, entries = 0, oneSided = 0, split = 0, noTable = 0, hhInvolved = 0;
            List<object> sample = new List<object>();
            Dictionary<SimDescription, Dictionary<SimDescription, Sims3.Gameplay.Socializing.Relationship>> rt = Sims3.Gameplay.Socializing.Relationship.sAllRelationships;
            foreach (KeyValuePair<SimDescription, Dictionary<SimDescription, Sims3.Gameplay.Socializing.Relationship>> kv in rt)
            {
                tables++;
                if (kv.Value == null || kv.Key == null) continue;
                foreach (KeyValuePair<SimDescription, Sims3.Gameplay.Socializing.Relationship> e in kv.Value)
                {
                    entries++;
                    Dictionary<SimDescription, Sims3.Gameplay.Socializing.Relationship> back;
                    if (e.Key == null || !rt.TryGetValue(e.Key, out back) || back == null) { noTable++; continue; }
                    Sims3.Gameplay.Socializing.Relationship b;
                    if (back.TryGetValue(kv.Key, out b)) { if (!object.ReferenceEquals(b, e.Value)) split++; continue; }
                    oneSided++;
                    bool hh = !DrNpc(kv.Key) || !DrNpc(e.Key);
                    if (hh) hhInvolved++;
                    if (sample.Count < want)
                        sample.Add(RelName(kv.Key) + " holds " + RelName(e.Key) + "; the other table lacks it" + (hh ? " (household or unknown scope)" : ""));
                }
            }
            r["sim_time"] = SimClock.CurrentTime().ToString();
            r["tables"] = tables; r["entries"] = entries;
            r["one_sided"] = oneSided; r["split_objects"] = split; r["no_table"] = noTable; r["one_sided_household"] = hhInvolved;
            r["sample"] = sample;
            return r;
        }

        static string RelName(SimDescription sd)
        {
            try
            {
                return sd.FullName + " (" + Util.Id(sd.SimDescriptionId) + (sd.IsValidDescription ? "" : ", invalid") + (sd.Household == null ? ", no household" : sd.Household == Household.ActiveHousehold ? ", household" : "")
                    + (sd.CreatedSim != null ? ", in world" : "") + ")";
            }
            catch (Exception) { return "?"; }
        }

        // nraas_reset_sims {count?: 5}: NRaas's own reset (NRaas.CommonSpace.Tasks.ResetSimTask.Perform(Sim, false), found by
        // reflection in any loaded NRaas assembly; no NRaas code copied) on up to count instantiated uncontrolled Sims, the
        // docs/535 §1 trigger without the household. Error without NRaas. Returns the Sims reset.
        static object NraasResetSims(Args a)
        {
            RequireLive();
            MethodInfo perform = null;
            string asmName = null;
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = asm.GetType("NRaas.CommonSpace.Tasks.ResetSimTask");
                if (t == null) continue;
                perform = t.GetMethod("Perform", BindingFlags.Public | BindingFlags.Static, null, new Type[] { typeof(Sim), typeof(bool) }, null);
                if (perform != null) { asmName = asm.GetName().Name; break; }
            }
            if (perform == null) throw new McpException("no NRaas ResetSimTask.Perform(Sim, bool) loaded");
            int count = (int)a.Long("count", 5);
            List<object> done = new List<object>(), failed = new List<object>();
            foreach (SimDescription sd in SimDescription.GetSimDescriptionsInWorld())
            {
                if (done.Count >= count) break;
                if (!DrNpc(sd) || !Util.InWorldNow(sd)) continue;
                try { perform.Invoke(null, new object[] { sd.CreatedSim, false }); done.Add(sd.FullName); }
                catch (Exception e) { failed.Add(sd.FullName + ": " + DescribeError(e)); }
            }
            Dictionary<string, object> r = Util.Obj();
            r["assembly"] = asmName;
            r["reset"] = done; r["count"] = done.Count; r["failed"] = failed;
            return r;
        }

        // event_cost {on?: bool, reset?: bool, top?: 15}: ts3fixes eventcost (EventCost.cs, docs/545 §5). on sets the switch
        // (off by default); reset empties the table; the rows: per event id calls, inclusive / exclusive us, max us, sorted by
        // exclusive time. hooked = the ProcessEvent patch went in (ts3fixes applied log).
        static object EventCost(Args a)
        {
            AppDomain d = AppDomain.CurrentDomain;
            Dictionary<string, bool> sw = d.GetData("ts3fixes.switches") as Dictionary<string, bool>;
            if (sw == null) { sw = new Dictionary<string, bool>(); d.SetData("ts3fixes.switches", sw); }
            if (a.Has("on")) sw["eventcost"] = a.Bool("on", false);
            Dictionary<ulong, long[]> t = d.GetData("ts3fixes.eventcost") as Dictionary<ulong, long[]>;
            Dictionary<string, object> r = Util.Obj();
            bool on;
            r["on"] = sw.TryGetValue("eventcost", out on) && on;
            bool hooked = false;
            IEnumerable applied = d.GetData("ts3fixes.applied") as IEnumerable;
            if (applied != null) foreach (object o in applied) if (o is string && ((string)o) == "EventTracker.ProcessEvent: ok") hooked = true;
            r["hooked"] = hooked;
            if (t == null) { r["error"] = "no ts3fixes eventcost table (older ts3fixes)"; return r; }
            List<KeyValuePair<ulong, long[]>> rows = new List<KeyValuePair<ulong, long[]>>(t);
            rows.Sort(delegate(KeyValuePair<ulong, long[]> x, KeyValuePair<ulong, long[]> y) { return y.Value[2].CompareTo(x.Value[2]); });
            long calls = 0, excl = 0;
            foreach (KeyValuePair<ulong, long[]> kv in rows) { calls += kv.Value[0]; excl += kv.Value[2]; }
            int top = (int)a.Long("top", 15);
            List<object> list = new List<object>();
            for (int i = 0; i < rows.Count && i < top; i++)
            {
                long[] v = rows[i].Value;
                Dictionary<string, object> o = Util.Obj();
                o["event"] = ((EventTypeId)(int)rows[i].Key).ToString();  // the number when the id has no name
                o["calls"] = v[0]; o["incl_us"] = v[1]; o["excl_us"] = v[2]; o["max_us"] = v[3];
                list.Add(o);
            }
            r["ids"] = rows.Count; r["calls"] = calls; r["excl_us"] = excl; r["top"] = list;
            if (a.Bool("reset", false)) t.Clear();
            return r;
        }
    }
}
