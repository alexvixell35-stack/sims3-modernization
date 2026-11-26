using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.ObjectComponents;
using Sims3.SimIFace;

namespace Ts3Agent
{
    // tomb_mummy (docs/607 §3, sarcmummynpc in-game repro): drives the Al Simhara sarcophagus nearest the selected Sim through
    // EA's own paths and reports where the tomb mummy's SimDescription ends up. Reflection only (no WA type reference):
    //   {action: "spawn", tag}  Sarcophagus.TombMummyCreate (the LookInside / ActivatedBehavior path: NpcHousehold.Add, Instantiate,
    //                           MummyExitSarcophagus push); the new mummy's SimDescriptionId is remembered under tag
    //   {action: "spawn", tag, reset: true}  spawn, then Reset Object at once (the mummy is still out: CommonCleanup runs)
    //   {action: "reset"}       GameObject.SetObjectToReset() on that sarcophagus (the player's Reset Object; EA's DoReset ->
    //                           CommonCleanup runs on the reset pass)
    //   {action: "state"}       (default) read only
    // Every answer: sarcophagus, mummy_out, mummy_in_npc, switch (ts3fixes sarcmummynpc), c3391/c3392/c3393 (removed / left
    // with the switch off / errors), hh_same (the active household has the same SimDescriptionIds as at the first tomb_mummy
    // call, all of them in it: household untouched), npc_no_sim (NpcHousehold descriptions without a Sim), and per tag <tag>_tracked,
    // <tag>_in_npc, <tag>_in_npc_no_sim (remembered ids still in NpcHousehold; the ids are SimDescriptionIds, so they survive a
    // save, quit to menu and load). Test only: nothing is saved; the ids live in the agent.
    // spawn tries the sarcophagi nearest-first (eg-sarcmummy-save-1119, 2026-11-19: the nearest one's TombMummyCreate pushed
    // no MummyExitSarcophagus, so EA's own TombMummyDestroy undid it, mummy_out stayed false): a sarcophagus whose create
    // leaves no mummy is listed in "tried" with exit_test (EA's MummyExitSarcophagus test, the selected Sim standing in for
    // the mummy) and the next one is tried; the one that spawned is used by later calls (by ObjectId, so it survives a
    // reload). No sarcophagus spawns: error, so the repro step fails at once with the reasons.
    public static partial class Handlers
    {
        static void RegisterTombMummy()
        {
            Dispatcher.Register("tomb_mummy", TombMummy);
        }

        static readonly Dictionary<string, List<ulong>> sTombMummyIds = new Dictionary<string, List<ulong>>();
        static List<ulong> sTombHh;
        const BindingFlags kTmInst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        static object TombMummy(Args a)
        {
            RequireLive();
            string action = a.Str("action", "state");
            GameObject sarc = TombSarc();
            FieldInfo fMummy = TombMummyField(sarc);
            List<object> tried = null;
            if (action == "spawn")
            {
                if (fMummy.GetValue(sarc) != null) throw new McpException("mummy already out");
                tried = new List<object>();
                Sim me = PlumbBob.SelectedActor;
                List<GameObject> all = new List<GameObject>();
                foreach (GameObject o in Sims3.Gameplay.Queries.GetObjects<GameObject>())
                    if (o != null && o.InWorld && !o.HasBeenDestroyed && IsOfTypeName(o.GetType(), "Sarcophagus")) all.Add(o);
                if (me != null) all.Sort((x, y) => (x.Position - me.Position).LengthSqr().CompareTo((y.Position - me.Position).LengthSqr()));
                Sim m = null;
                foreach (GameObject o in all)
                {
                    if (tried.Count >= 8) break;
                    FieldInfo f = TombMummyField(o);
                    if (f.GetValue(o) != null) continue;
                    MethodInfo create = null;
                    for (Type t = o.GetType(); t != null && create == null; t = t.BaseType) create = t.GetMethod("TombMummyCreate", kTmInst | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                    if (create == null) throw new McpException(o.GetType().FullName + " has no TombMummyCreate()");
                    try { create.Invoke(o, null); }
                    catch (TargetInvocationException e) { throw e.InnerException ?? e; }
                    m = f.GetValue(o) as Sim;
                    if (m != null) { sarc = o; fMummy = f; sTombSarcId = o.ObjectId.Value; break; }
                    Dictionary<string, object> row = Util.Obj();
                    row["sarcophagus"] = o.ObjectId.Value;
                    row["distance"] = me != null ? (o.Position - me.Position).Length() : -1f;
                    row["exit_test"] = TombExitTest(o, me);
                    tried.Add(row);
                }
                if (m == null)
                    throw new McpException("no sarcophagus spawned its mummy (" + all.Count + " in the world): " + Json.Write(tried));
                if (m.SimDescription != null)
                {
                    string tag = a.Str("tag", "spawn");
                    List<ulong> ids;
                    if (!sTombMummyIds.TryGetValue(tag, out ids)) sTombMummyIds[tag] = ids = new List<ulong>();
                    ids.Add(m.SimDescription.SimDescriptionId);
                }
                // reset: true = the player's Reset Object in the same call, while the mummy is still out. Needed: EA's own
                // flow (MummyWander route fail -> MummyReturnToSarcophagus -> TombMummyDestroy, the clean path) removed the
                // mummy within 3 s in the rerun1 run (on_tracked 1, on_in_npc 0, 3391-3393 0), before a separate reset step
                if (a.Bool("reset", false))
                {
                    if (m.HasBeenDestroyed || fMummy.GetValue(sarc) != m) throw new McpException("mummy gone before the reset");
                    sarc.SetObjectToReset();
                }
            }
            else if (action == "reset") sarc.SetObjectToReset();
            else if (action != "state") throw new McpException("action must be spawn, reset or state");
            Dictionary<string, object> res = TombMummyState(sarc, fMummy.GetValue(sarc) as Sim);
            if (tried != null) res["tried"] = tried;
            return res;
        }

        static ulong sTombSarcId;

        // the sarcophagus that spawned last (same ObjectId after a reload), else the one nearest the selected Sim
        static GameObject TombSarc()
        {
            if (sTombSarcId != 0)
                foreach (GameObject o in Sims3.Gameplay.Queries.GetObjects<GameObject>())
                    if (o != null && o.InWorld && !o.HasBeenDestroyed && o.ObjectId.Value == sTombSarcId && IsOfTypeName(o.GetType(), "Sarcophagus")) return o;
            return NearestOfType("Sarcophagus");
        }

        static FieldInfo TombMummyField(GameObject sarc)
        {
            FieldInfo f = null;
            for (Type t = sarc.GetType(); t != null && f == null; t = t.BaseType) f = t.GetField("mTombMummy", kTmInst | BindingFlags.DeclaredOnly);
            if (f == null) throw new McpException(sarc.GetType().FullName + " has no mTombMummy");
            return f;
        }

        // EA's MummyExitSarcophagus test on this sarcophagus with the selected Sim as actor (the mummy is gone by now):
        // the target-side reasons (interactions disabled, hand tool), not mummy-specific ones
        static string TombExitTest(GameObject sarc, Sim actor)
        {
            try
            {
                Type exit = null;
                for (Type t = sarc.GetType(); t != null && exit == null; t = t.BaseType) exit = t.GetNestedType("MummyExitSarcophagus", BindingFlags.Public | BindingFlags.NonPublic);
                FieldInfo single = exit != null ? exit.GetField("Singleton", BindingFlags.Public | BindingFlags.Static) : null;
                InteractionDefinition def = single != null ? single.GetValue(null) as InteractionDefinition : null;
                if (def == null || actor == null) return "?";
                GameObjectHit hit = new GameObjectHit(GameObjectHitType.Object);
                hit.mPoint = sarc.Position;
                hit.mId = sarc.ObjectId.Value;
                InteractionInstanceParameters p = new InteractionInstanceParameters(new InteractionObjectPair(def, sarc), actor,
                    new InteractionPriority(InteractionPriorityLevel.MaxDeath), AutonomySearchType.None, InteractionFlags.None, hit, null, false);
                GreyedOutTooltipCallback tip = null;
                return def.Test(ref p, ref tip).ToString() + (sarc.InteractionsAreDisabled(InteractionsDisabledType.UserDirected) ? " (interactions disabled)" : "");
            }
            catch (Exception e) { return "error: " + e.GetType().Name + ": " + e.Message; }
        }

        static Dictionary<string, object> TombMummyState(GameObject sarc, Sim mummy)
        {
            Household npc = Household.NpcHousehold;
            Dictionary<string, object> r = Util.Obj();
            r["sarcophagus"] = sarc.GetType().Name + " " + sarc.ObjectId.Value;
            r["mummy_out"] = mummy != null && !mummy.HasBeenDestroyed;
            r["mummy_in_npc"] = mummy != null && mummy.SimDescription != null && npc != null && mummy.SimDescription.Household == npc;
            bool? on = ModOn("sarcmummynpc");
            r["switch"] = on.HasValue ? (object)on.Value : "?";
            int[] c = AppDomain.CurrentDomain.GetData("ts3fixes.counters") as int[];
            for (int i = 3391; i <= 3393; i++) r["c" + i] = c != null && c.Length > i ? c[i] : -1;
            Dictionary<ulong, SimDescription> inNpc = new Dictionary<ulong, SimDescription>();
            int noSim = 0;
            if (npc != null)
                foreach (SimDescription d in npc.AllSimDescriptions)
                {
                    if (d == null) continue;
                    inNpc[d.SimDescriptionId] = d;
                    if (d.CreatedSim == null) noSim++;
                }
            r["npc_no_sim"] = noSim;
            List<ulong> hh = new List<ulong>();
            if (Household.ActiveHousehold != null)
                foreach (SimDescription d in Household.ActiveHousehold.AllSimDescriptions)
                    if (d != null && d.Household == Household.ActiveHousehold) hh.Add(d.SimDescriptionId);
            hh.Sort();
            if (sTombHh == null) sTombHh = hh;
            r["hh_members"] = hh.Count;
            r["hh_same"] = hh.Count > 0 && string.Join(",", Array.ConvertAll(hh.ToArray(), x => x.ToString())) == string.Join(",", Array.ConvertAll(sTombHh.ToArray(), x => x.ToString()));
            foreach (KeyValuePair<string, List<ulong>> kv in sTombMummyIds)
            {
                int n = 0, nNoSim = 0;
                foreach (ulong id in kv.Value)
                {
                    SimDescription d;
                    if (!inNpc.TryGetValue(id, out d)) continue;
                    n++;
                    if (d.CreatedSim == null) nNoSim++;
                }
                r[kv.Key + "_tracked"] = kv.Value.Count;
                r[kv.Key + "_in_npc"] = n;
                r[kv.Key + "_in_npc_no_sim"] = nNoSim;
            }
            return r;
        }
    }
}
