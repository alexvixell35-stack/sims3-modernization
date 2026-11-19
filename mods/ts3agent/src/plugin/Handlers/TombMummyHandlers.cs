using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;

namespace Ts3Agent
{
    // tomb_mummy (docs/607 §3, sarcmummynpc in-game repro): drives the Al Simhara sarcophagus nearest the selected Sim through
    // EA's own paths and reports where the tomb mummy's SimDescription ends up. Reflection only (no WA type reference):
    //   {action: "spawn", tag}  Sarcophagus.TombMummyCreate (the LookInside / ActivatedBehavior path: NpcHousehold.Add, Instantiate,
    //                           MummyExitSarcophagus push); the new mummy's SimDescriptionId is remembered under tag
    //   {action: "reset"}       GameObject.SetObjectToReset() on that sarcophagus (the player's Reset Object; EA's DoReset ->
    //                           CommonCleanup runs on the reset pass)
    //   {action: "state"}       (default) read only
    // Every answer: sarcophagus, mummy_out, mummy_in_npc, switch (ts3fixes sarcmummynpc), c3391/c3392/c3393 (removed / left
    // with the switch off / errors), hh_same (the active household has the same SimDescriptionIds as at the first tomb_mummy
    // call, all of them in it: household untouched), npc_no_sim (NpcHousehold descriptions without a Sim), and per tag <tag>_tracked,
    // <tag>_in_npc, <tag>_in_npc_no_sim (remembered ids still in NpcHousehold; the ids are SimDescriptionIds, so they survive a
    // save, quit to menu and load). Test only: nothing is saved; the ids live in the agent.
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
            GameObject sarc = NearestOfType("Sarcophagus");
            Type st = sarc.GetType();
            FieldInfo fMummy = null;
            for (Type t = st; t != null && fMummy == null; t = t.BaseType) fMummy = t.GetField("mTombMummy", kTmInst | BindingFlags.DeclaredOnly);
            if (fMummy == null) throw new McpException(st.FullName + " has no mTombMummy");
            if (action == "spawn")
            {
                if (fMummy.GetValue(sarc) != null) throw new McpException("mummy already out");
                MethodInfo create = null;
                for (Type t = st; t != null && create == null; t = t.BaseType) create = t.GetMethod("TombMummyCreate", kTmInst | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                if (create == null) throw new McpException(st.FullName + " has no TombMummyCreate()");
                try { create.Invoke(sarc, null); }
                catch (TargetInvocationException e) { throw e.InnerException ?? e; }
                Sim m = fMummy.GetValue(sarc) as Sim;
                if (m != null && m.SimDescription != null)
                {
                    string tag = a.Str("tag", "spawn");
                    List<ulong> ids;
                    if (!sTombMummyIds.TryGetValue(tag, out ids)) sTombMummyIds[tag] = ids = new List<ulong>();
                    ids.Add(m.SimDescription.SimDescriptionId);
                }
            }
            else if (action == "reset") sarc.SetObjectToReset();
            else if (action != "state") throw new McpException("action must be spawn, reset or state");
            return TombMummyState(sarc, fMummy.GetValue(sarc) as Sim);
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
