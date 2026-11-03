using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.SimIFace;


namespace Ts3Fixes
{
    // docs/188 (ts3native [UserDiag]): at every world load a few lines for ts3native.log, so a player's log answers the
    // first triage questions: this mod's version and settings in effect, world size, the script assemblies loaded.
    // Transport: ts3native's text channel, Route.GetOption2 with RouteOption2 bits 63 + 58 + 57 + 56 (top byte 0x87; 0x86 is the stuck-check PROBE mark), op in
    // bits 48-55 (0 begin, 1 four ASCII bytes in bits 0-31, 2 end). Without ts3native (or with [UserDiag] off) the first
    // message is answered false and nothing more is sent. Logging only: reads, never writes game state.
    public static partial class Fixes
    {
        const ulong kDiagBits = 0x8700000000000000UL;

        static bool DiagSend(int op, uint v) { return sYieldProbe.GetOption2((ScriptCore.Route.RouteOption2)(kDiagBits | (ulong)(uint)op << 48 | v)); }

        // One log line (ASCII; anything else becomes '?' on the native side), at most 470 characters.
        static bool DiagLine(string s)
        {
            if (!DiagSend(0, 0)) return false;
            int n = Math.Min(s.Length, 470);
            for (int i = 0; i < n; i += 4)
            {
                uint v = 0;
                for (int k = 0; k < 4 && i + k < n; k++) v |= (uint)(s[i + k] & 0xFF) << (8 * k);
                if (!DiagSend(1, v)) return false;
            }
            return DiagSend(2, 0);
        }

        // Items joined by ", " into lines that start with head (a long list becomes several lines).
        static bool DiagList(string head, List<string> items)
        {
            StringBuilder b = new StringBuilder(head);
            int start = b.Length;
            foreach (string it in items)
            {
                if (b.Length > start && b.Length + it.Length + 2 > 460)
                {
                    if (!DiagLine(b.ToString())) return false;
                    b.Length = start;
                }
                if (b.Length > start) b.Append(", ");
                b.Append(it);
            }
            return b.Length == start || DiagLine(b.ToString());
        }

        static readonly string[] kEaAssemblies = { "mscorlib", "System", "System.Xml", "ScriptCore", "SimIFace", "Sims3Metadata",
            "Sims3GameplaySystems", "Sims3GameplayObjects", "Sims3StoreObjects", "UI" };

        static string ShortMvid(Assembly a)  // AutShadow.cs Mvid, first 8 hex
        {
            try { return Mvid(a).Substring(0, 8); } catch (Exception) { return "?"; }
        }

        // Called from Instantiator's world-quit handler (menu, travel, another save, exit): ts3native [GC] BlackListCapKB
        // puts the lenient black-list cap back for the load screen (gcgrow.cpp; "world:" below makes it strict again).
        public static void DiagWorldQuit() { DiagLine("world quit"); }

        // Called from Instantiator's world-load handler.
        public static void DiagWorldLoad()
        {
            // settings in effect: every tunable of Instantiator (the player's tuning), then any runtime test switch
            List<string> on = new List<string>(), off = new List<string>();
            foreach (FieldInfo f in typeof(Instantiator).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (f.IsLiteral) continue;
                object v = f.GetValue(null);
                if (v is bool) ((bool)v ? on : off).Add(f.Name);
                else on.Add(f.Name + "=" + v);
            }
            if (!DiagLine("ts3fixes " + Instantiator.Version + " build " + ShortMvid(typeof(Fixes).Assembly) + ", " + on.Count + " settings on/valued, " + off.Count + " off")) return;
            DiagList("settings on: ", on);
            List<string> sw = new List<string>();
            Dictionary<string, bool> d = AppDomain.CurrentDomain.GetData("ts3fixes.switches") as Dictionary<string, bool>;
            if (d != null) foreach (KeyValuePair<string, bool> kv in d) sw.Add(kv.Key + "=" + (kv.Value ? "on" : "off"));
            if (sw.Count > 0) DiagList("test switches (override the settings): ", sw);

            // world size
            int lots = 0, residential = 0, occupied = 0;
            foreach (Lot lot in LotManager.AllLots)
            {
                lots++;
                if (lot.IsResidentialLot) residential++;
                if (lot.Household != null) occupied++;
            }
            int households = 0, members = 0, active = 0;
            foreach (Household h in Household.sHouseholdList) { households++; members += h.NumMembers; }
            Household ah = Household.ActiveHousehold;
            if (ah != null) active = ah.NumMembers;
            uint objects = 0;
            try { objects = Sims3.Gameplay.Queries.CountObjects<GameObject>(); } catch (Exception) { }
            string world = "?";
            try { world = Sims3.SimIFace.World.GetWorldFileName(); } catch (Exception) { }
            DiagLine("world: " + world + ", lots " + lots + " (residential " + residential + ", with a household " + occupied + "), households "
                + households + " with " + members + " Sims (active household " + active + "), Sims in the world " + LotManager.Actors.Count
                + ", objects " + objects);

            // script assemblies other than the game's (NRaas and other script mods), name version mvid
            List<string> asm = new List<string>();
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                AssemblyName n = a.GetName();
                if (Array.IndexOf(kEaAssemblies, n.Name) >= 0) continue;
                asm.Add(n.Name + " " + n.Version + " " + ShortMvid(a));
            }
            DiagList("script assemblies (" + asm.Count + "): ", asm);
        }
    }
}
