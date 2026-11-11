using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Ts3Fixes
{
    // Game-free part of the wrapper restart (Restart.cs, docs/530 step 1), shared with the offline self-check
    // (tools/selftest/restart): the state record as text lines for the handoff file, read back strictly (one bad line and
    // nothing is restored), the save-name match and the texts. The record holds what Compaction's CpRecord holds, in plain
    // values: speed, HUD panel, selected Sim, camera, and per Sim the queue entries with their definition's static field.
    //   R1|speed|panel|selected|hasCamera|camPos x y z|camTarget x y z      (first line, once)
    //   S|simDescriptionId                                                  (a Sim; its I lines follow)
    //   I|kind|id|level|value|autonomous|cancellable|x|y|z|definitionType|definitionField|objectType
    // Types as "Namespace.Type+Nested, Assembly"; floats round-trip ("R", invariant). ASCII only (the native text channel).
    public sealed class RsItem
    {
        public int Kind;                 // 0 Sim, 1 object, 2 lot (CpItem.Kind)
        public ulong Id;
        public int Level;                // InteractionPriority.Level
        public float Value;              // InteractionPriority.Value
        public bool Autonomous, Cancellable;
        public float X, Y, Z;            // object position
        public string DefType = "", DefField = "", ObjType = "";
    }
    public sealed class RsSim { public ulong SimId; public readonly List<RsItem> Items = new List<RsItem>(); }
    public sealed class RsRecord
    {
        public int Speed, Panel;
        public ulong Selected;
        public bool HasCamera;
        public readonly float[] Cam = new float[6];  // position x y z, target x y z
        public readonly List<RsSim> Sims = new List<RsSim>();
    }

    public static class RestartCore
    {
        public const int kLineMax = 460;     // one native text line ("wraprec " + line, 470 at most)
        public const int kTotalMax = 60000;  // the native record buffer is 64 KB
        public const string kAskText = "Memory is getting low. Refresh memory now? The game restarts into this save (your game is saved).";
        public const string kDoneText = "Memory refreshed: the game restarted into your save.";
        public const string kSkippedText = "Memory refresh skipped; your game is saved. It is off until the game restarts.";

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        static string F(float f) { return f.ToString("R", Inv); }

        public static List<string> Lines(RsRecord r, out int dropped)
        {
            dropped = 0;
            List<string> o = new List<string>();
            StringBuilder b = new StringBuilder();
            b.Append("R1|").Append(r.Speed.ToString(Inv)).Append('|').Append(r.Panel.ToString(Inv)).Append('|').Append(r.Selected.ToString(Inv))
             .Append('|').Append(r.HasCamera ? '1' : '0');
            for (int i = 0; i < 6; i++) b.Append('|').Append(F(r.Cam[i]));
            o.Add(b.ToString());
            int total = b.Length + 1;
            foreach (RsSim s in r.Sims)
            {
                List<string> items = new List<string>();
                foreach (RsItem it in s.Items)
                {
                    string l = "I|" + it.Kind.ToString(Inv) + "|" + it.Id.ToString(Inv) + "|" + it.Level.ToString(Inv) + "|" + F(it.Value) + "|" +
                               (it.Autonomous ? "1" : "0") + "|" + (it.Cancellable ? "1" : "0") + "|" + F(it.X) + "|" + F(it.Y) + "|" + F(it.Z) + "|" +
                               it.DefType + "|" + it.DefField + "|" + it.ObjType;
                    if (l.Length > kLineMax || !Clean(l)) { dropped++; continue; }
                    items.Add(l);
                }
                if (items.Count == 0) continue;
                string head = "S|" + s.SimId.ToString(Inv);
                int n = head.Length + 1;
                foreach (string l in items) n += l.Length + 1;
                if (total + n > kTotalMax) { dropped += items.Count; continue; }
                o.Add(head);
                o.AddRange(items);
                total += n;
            }
            return o;
        }

        // ASCII printable only (the native text channel turns anything else into '?'); type names never hold '|'
        static bool Clean(string l)
        {
            foreach (char c in l) if (c < 0x20 || c > 0x7E) return false;
            return true;
        }

        // null: not a record of this schema, or any line malformed (nothing is restored then)
        public static RsRecord Parse(string text)
        {
            if (text == null) return null;
            string[] lines = text.Split('\n');
            RsRecord r = null;
            RsSim cur = null;
            try
            {
                foreach (string raw in lines)
                {
                    if (raw.Length == 0) continue;
                    string[] f = raw.Split('|');
                    if (r == null)
                    {
                        if (f.Length != 11 || f[0] != "R1") return null;
                        r = new RsRecord();
                        r.Speed = int.Parse(f[1], NumberStyles.AllowLeadingSign, Inv);
                        r.Panel = int.Parse(f[2], NumberStyles.AllowLeadingSign, Inv);
                        r.Selected = ulong.Parse(f[3], NumberStyles.None, Inv);
                        if (f[4] != "0" && f[4] != "1") return null;
                        r.HasCamera = f[4] == "1";
                        for (int i = 0; i < 6; i++) r.Cam[i] = float.Parse(f[5 + i], NumberStyles.Float, Inv);
                        continue;
                    }
                    if (f[0] == "S" && f.Length == 2)
                    {
                        cur = new RsSim();
                        cur.SimId = ulong.Parse(f[1], NumberStyles.None, Inv);
                        r.Sims.Add(cur);
                        continue;
                    }
                    if (f[0] != "I" || f.Length != 13 || cur == null) return null;
                    RsItem it = new RsItem();
                    it.Kind = int.Parse(f[1], NumberStyles.None, Inv);
                    if (it.Kind < 0 || it.Kind > 2) return null;
                    it.Id = ulong.Parse(f[2], NumberStyles.None, Inv);
                    it.Level = int.Parse(f[3], NumberStyles.AllowLeadingSign, Inv);
                    it.Value = float.Parse(f[4], NumberStyles.Float, Inv);
                    if ((f[5] != "0" && f[5] != "1") || (f[6] != "0" && f[6] != "1")) return null;
                    it.Autonomous = f[5] == "1";
                    it.Cancellable = f[6] == "1";
                    it.X = float.Parse(f[7], NumberStyles.Float, Inv);
                    it.Y = float.Parse(f[8], NumberStyles.Float, Inv);
                    it.Z = float.Parse(f[9], NumberStyles.Float, Inv);
                    it.DefType = f[10];
                    it.DefField = f[11];
                    it.ObjType = f[12];
                    if (it.DefType.Length == 0 || it.DefField.Length == 0 || (it.Kind == 1) != (it.ObjType.Length > 0)) return null;
                    cur.Items.Add(it);
                }
            }
            catch (Exception) { return null; }  // FormatException / OverflowException: a bad number
            return r;
        }

        // The save the boot loaded is the one the handoff file names (GameStates.LoadFileName may carry a folder)
        public static bool SameSave(string loadFileName, string handoffName)
        {
            if (string.IsNullOrEmpty(loadFileName) || string.IsNullOrEmpty(handoffName)) return false;
            int k = loadFileName.LastIndexOfAny(new char[] { '\\', '/' });
            string n = k >= 0 ? loadFileName.Substring(k + 1) : loadFileName;
            return string.Equals(n, handoffName, StringComparison.OrdinalIgnoreCase);
        }

        // native bit answers (op 40, 0x10-0x12) -> value, bit 0 first
        public static uint FromBits(bool[] bits)
        {
            uint v = 0;
            for (int i = 0; i < bits.Length && i < 32; i++) if (bits[i]) v |= 1u << i;
            return v;
        }
    }
}
