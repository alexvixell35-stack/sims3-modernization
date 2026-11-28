using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interfaces;

namespace Ts3Fixes
{
    // etderefdiag (logging only, ON by default like alarmkilllog; docs/552 §11). NRaas ErrorTrap's load-up scan (one sim-minute
    // after a world load) writes "Destroyed Object Found: <type>" when a script object whose simulator proxy is gone
    // (HasBeenDestroyed) is still referenced from some field, then clears those references; its ScriptError file names only
    // the type (the reference chain is logged only with ErrorTrap's debugging on). Seen 2026-11-14: four
    // DoorSingleColonialContemporary at every Bridgeport new-game load since 19:11 (docs/552 §11).
    // This hook on ErrorTrap's DereferenceManager.Perform(obj, references, perform, reportFound) writes, for the first
    // (perform == false) call on a destroyed GameObject, one "etderef:" line: the object's type and id and every holder
    // (holder type, field, and for a live GameObject holder its lot and position). Read-only: it reads the list ErrorTrap
    // already built and then runs ErrorTrap's own Perform unchanged. At most kEtdLines lines per session.
    // Counters: [2415] lines written, [2416] errors (registry fixes-2415).
    public static partial class Fixes
    {
        static bool sEtdTried;
        static int sEtdLines;
        const int kEtdLines = 24, kEtdHolders = 8;

        // World load (Instantiator): ErrorTrap's assembly is loaded by then; its scan starts one sim-minute later.
        internal static void ApplyEtDerefDiag()
        {
            if (sEtdTried) return;
            sEtdTried = true;
            Assembly et = null;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                if (a.GetName().Name == "NRaasErrorTrap") { et = a; break; }
            Type dm = et == null ? null : et.GetType("NRaas.ErrorTrapSpace.Dereferences.DereferenceManager");
            Type gr = et == null ? null : et.GetType("NRaas.ErrorTrapSpace.GameObjectReference");
            if (dm == null || gr == null) { AppliedLog().Add("etderefdiag: ErrorTrap's DereferenceManager not found, off"); return; }
            Patch(dm, "Perform", new Type[] { typeof(object), gr, typeof(bool), typeof(bool) }, typeof(EtdHook), "PerformHook", "PerformOrig");
        }

        public class EtdHook
        {
            public static bool PerformHook(object obj, object references, bool perform, bool reportFound)
            {
                if (!perform && sEtdLines < kEtdLines && ShortcutOn("etderefdiag"))
                {
                    try { EtdLog(obj, references as IEnumerable); } catch (Exception) { PatchCounters()[2416]++; }
                }
                return PerformOrig(obj, references, perform, reportFound);
            }
            public static bool PerformOrig(object obj, object references, bool perform, bool reportFound) { return false; } // receives ErrorTrap's DereferenceManager.Perform
        }

        static void EtdLog(object obj, IEnumerable references)
        {
            IScriptObject so = obj as IScriptObject;
            if (so == null || !so.HasBeenDestroyed) return;
            StringBuilder sb = new StringBuilder("etderef: destroyed ");
            sb.Append(obj.GetType().Name).Append(" 0x").Append(so.ObjectId.Value.ToString("X"));
            int n = 0, shown = 0;
            if (references != null)
                foreach (object o in references)
                {
                    if (!(o is KeyValuePair<object, FieldInfo>)) continue;
                    KeyValuePair<object, FieldInfo> kv = (KeyValuePair<object, FieldInfo>)o;
                    n++;
                    if (shown++ >= kEtdHolders) continue;
                    object h = kv.Key;
                    sb.Append(shown == 1 ? ", held by " : "; ").Append(h == null ? "null" : h.GetType().FullName)
                      .Append('.').Append(kv.Value == null ? "?" : kv.Value.Name);
                    GameObject g = h as GameObject;
                    if (g != null && g.HasBeenDestroyed) sb.Append(" (destroyed 0x").Append(g.ObjectId.Value.ToString("X")).Append(')');
                    if (g != null && !g.HasBeenDestroyed)
                    {
                        Lot lot = g.LotCurrent;
                        sb.Append(" (").Append(lot == null ? "no lot" : lot.Name + " " + lot.LotId.ToString("X"))
                          .Append(" @").Append(Math.Round(g.Position.x, 1)).Append(',').Append(Math.Round(g.Position.z, 1)).Append(" L").Append(g.Level).Append(')');
                    }
                    Lot hl = h as Lot;
                    if (hl != null) sb.Append(" (lot ").Append(hl.Name).Append(' ').Append(hl.LotId.ToString("X")).Append(')');
                }
            sb.Append(", ").Append(n).Append(" holders");
            sEtdLines++;
            PatchCounters()[2415]++;
            DiagLine(sb.ToString());
        }
    }
}
