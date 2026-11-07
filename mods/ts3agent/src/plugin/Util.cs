using System;
using System.Collections.Generic;
using System.Globalization;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Agent
{
    // Shared lookups and formatting for handlers.
    public static class Util
    {
        public static Dictionary<string, object> Obj() { return new Dictionary<string, object>(); }

        public static string Id(ulong v) { return v.ToString(CultureInfo.InvariantCulture); }

        public static string Id(ObjectGuid g) { return g.Value.ToString(CultureInfo.InvariantCulture); }

        public static Sim ActiveSim()
        {
            Sim s = PlumbBob.SelectedActor;
            if (s == null) throw new McpException("no sim is selected (are you in live mode with a household?)");
            return s;
        }

        // Sims chosen by the last pick_sims (SimDescriptionIds, in pick order): "picked:N" (1-based) in any Sim arg.
        public static readonly List<ulong> Picked = new List<ulong>();
        // the last pick_sims filter: a picked Sim that left the world before a later step is replaced by another Sim that matches it
        // (2026-11-07: three jobs failed on 'not currently in the world (not instantiated)' a few seconds after pick_sims)
        public delegate bool SimFilter(SimDescription sd);  // the game's mscorlib 2.0 has no Func
        public static SimFilter PickFilter;
        public static bool InWorldNow(SimDescription sd) { return sd != null && sd.CreatedSim != null && !sd.CreatedSim.HasBeenDestroyed && sd.CreatedSim.InWorld; }

        // Resolve a sim from args[key]: SimDescriptionId (number or numeric string),
        // full name, unique first name, or "picked:N" (pick_sims). Missing => the selected sim.
        public static SimDescription FindSimDescription(Args a, string key)
        {
            if (!a.Has(key)) return ActiveSim().SimDescription;
            string q = a.Str(key).Trim();
            if (q == "active") return ActiveSim().SimDescription;  // lets repro entries filter by the active Sim
            if (q.StartsWith("picked:", StringComparison.OrdinalIgnoreCase))  // repro steps naming Sims chosen at runtime
            {
                int n;
                if (!int.TryParse(q.Substring(7), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) || n < 1 || n > Picked.Count)
                    throw new McpException("no Sim " + q + " (" + Picked.Count + " picked; run pick_sims first)");
                SimDescription p = SimDescription.Find(Picked[n - 1]);
                if (!InWorldNow(p) && PickFilter != null)
                    foreach (SimDescription c in SimDescription.GetSimDescriptionsInWorld())
                        if (InWorldNow(c) && !Picked.Contains(c.SimDescriptionId) && PickFilter(c)) { Picked[n - 1] = c.SimDescriptionId; p = c; break; }
                if (p == null) throw new McpException(q + " is no longer in the world");
                return p;
            }
            ulong id;
            if (ulong.TryParse(q, NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
            {
                SimDescription byId = SimDescription.Find(id);
                if (byId != null) return byId;
                GameObject obj = GameObject.GetObject(new ObjectGuid(id));
                Sim asSim = obj as Sim;
                if (asSim != null) return asSim.SimDescription;
                throw new McpException("no sim with id " + q);
            }
            List<SimDescription> first = new List<SimDescription>();
            foreach (SimDescription sd in SimDescription.GetSimDescriptionsInWorld())
            {
                if (sd == null) continue;
                if (string.Compare(sd.FullName, q, true, CultureInfo.InvariantCulture) == 0) return sd;
                if (string.Compare(sd.FirstName, q, true, CultureInfo.InvariantCulture) == 0) first.Add(sd);
            }
            if (first.Count == 1) return first[0];
            if (first.Count > 1)
            {
                // Prefer a member of the active household.
                Household hh = Household.ActiveHousehold;
                foreach (SimDescription sd in first) if (hh != null && sd.Household == hh) return sd;
                List<string> names = new List<string>();
                foreach (SimDescription sd in first) names.Add(sd.FullName + " (" + Id(sd.SimDescriptionId) + ")");
                throw new McpException("'" + q + "' is ambiguous: " + string.Join(", ", names.ToArray()));
            }
            throw new McpException("no sim named '" + q + "'");
        }

        public static Sim FindSim(Args a, string key)
        {
            SimDescription sd = FindSimDescription(a, key);
            if (sd.CreatedSim == null) throw new McpException(sd.FullName + " is not currently in the world (not instantiated)");
            return sd.CreatedSim;
        }

        public static GameObject FindObject(Args a, string key)
        {
            string q = a.Str(key).Trim();
            ulong id;
            if (!ulong.TryParse(q, NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
            {
                // A sim name is also a valid target.
                SimDescription sd = FindSimDescription(a, key);
                if (sd.CreatedSim == null) throw new McpException(sd.FullName + " is not in the world");
                return sd.CreatedSim;
            }
            GameObject obj = GameObject.GetObject(new ObjectGuid(id));
            if (obj == null)
            {
                SimDescription sd = SimDescription.Find(id);
                if (sd != null && sd.CreatedSim != null) return sd.CreatedSim;
                throw new McpException("no object with id " + q);
            }
            return obj;
        }

        public static T ParseEnum<T>(string value)
        {
            string v = value.Trim().Replace(" ", "");
            try { return (T)Enum.Parse(typeof(T), v, true); }
            catch (ArgumentException)
            {
                List<string> names = new List<string>();
                foreach (string n in Enum.GetNames(typeof(T)))
                    if (n.ToLowerInvariant().IndexOf(v.ToLowerInvariant()) >= 0) names.Add(n);
                if (names.Count == 1) return (T)Enum.Parse(typeof(T), names[0], true);
                throw new McpException("unknown " + typeof(T).Name + " '" + value + "'"
                    + (names.Count > 0 ? "; did you mean: " + string.Join(", ", names.GetRange(0, Math.Min(15, names.Count)).ToArray()) : ""));
            }
        }

        public static string Localize(bool female, string key, params object[] p)
        {
            if (string.IsNullOrEmpty(key)) return key;
            try
            {
                string s = Localization.LocalizeString(female, key, p);
                return string.IsNullOrEmpty(s) ? key : s;
            }
            catch (Exception) { return key; }
        }

        public static string ObjectName(GameObject o)
        {
            if (o == null) return null;
            Sim s = o as Sim;
            if (s != null) return s.FullName;
            try
            {
                string n = o.CatalogName;
                if (!string.IsNullOrEmpty(n)) return n;
            }
            catch (Exception) { }
            return o.GetType().Name;
        }

        public static Dictionary<string, object> SimSummary(SimDescription sd)
        {
            Dictionary<string, object> d = Obj();
            d["id"] = Id(sd.SimDescriptionId);
            d["name"] = sd.FullName;
            d["age"] = sd.Age.ToString();
            d["gender"] = sd.Gender.ToString();
            if (!sd.IsHuman) d["species"] = sd.Species.ToString();
            Sim s = sd.CreatedSim;
            d["in_world"] = s != null;
            if (s != null)
            {
                d["object_id"] = Id(s.ObjectId);
                d["selectable"] = s.IsSelectable;
                d["selected"] = s == PlumbBob.SelectedActor;
                if (s.LotCurrent != null) d["lot"] = LotName(s.LotCurrent);
            }
            if (sd.IsGhost) d["ghost"] = true;
            return d;
        }

        public static string LotName(Lot lot)
        {
            if (lot == null) return null;
            string n = null;
            try { n = lot.Name; } catch (Exception) { }
            if (string.IsNullOrEmpty(n)) try { n = lot.Address; } catch (Exception) { }
            return string.IsNullOrEmpty(n) ? "Lot " + Id(lot.LotId) : n;
        }

        public static float Round(float f) { return (float)Math.Round(f, 1); }
    }
}
