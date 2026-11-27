using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Interactions;
using Sims3.Store.Objects;

namespace Ts3Fixes
{
    // surfline (2026-11-27, switch off by default): the surf area's waiting line (SurfArea.mSimQueue, Sim ids; store object
    // "Surf's Up Sun & Fun Wave Station"). A Sim that arrives while the area is in use queues and waits inside Surf.Run until
    // it heads the line and the area is free, for at most kMaxWaitDuration (6 h). A time-out or a reset (InteractionQueue.OnReset
    // runs Cleanup) leaves the Sim's own id in the line, and a cancel while waiting Dequeue()s the HEAD instead (the Sim in
    // front loses its turn) and also leaves the canceller's id. Nothing but the area's DoReset clears the line, so every later
    // surfer waits the full 6 h behind the leftover id and gives up. Same EA pattern as rinkexit (RinkExit.cs) and statueline
    // (StatueLine.cs). Fix: in Surf.Cleanup the leaving Sim's own id, and ids whose Sim is not in a Surf on this area, are removed
    // (order kept), so a line already blocked in a save clears with the next surfer's cleanup.
    // shortcut: the turn lost by the Sim in front on a cancel is not given back (the wrong Dequeue is inside Surf.Run, no hook
    // point short of a Run swap); upgrade only if lines at surf areas turn out common.
    // Counters: [4009] leaving Sim's own id removed (EA leaves it), [4010] ids of Sims not surfing dropped, [4011] errors (or a
    // field not found: the line is left to EA).
    public static partial class Fixes
    {
        static readonly Type sSuType = typeof(SurfArea).GetNestedType("Surf", BindingFlags.NonPublic);  // private sealed
        static readonly FieldInfo sSuQueue = typeof(SurfArea).GetField("mSimQueue", BindingFlags.Instance | BindingFlags.NonPublic);

        static void ApplySurfLine()
        {
            if (sSuType == null || sSuQueue == null) { PatchCounters()[4011]++; return; }  // another game version: leave EA's code alone
            Patch(sSuType, "Cleanup", Type.EmptyTypes, typeof(SurfLineHook), "CleanupHook", "CleanupOrig");
        }

        // true when the Sim behind `id` is in a Surf on `area` (a waiting Sim is: the wait loop runs inside Surf.Run)
        static bool SuSurfing(SurfArea area, ulong id)
        {
            SimDescription d = SimDescription.Find(id);
            Sim s = d != null ? d.CreatedSim : null;
            if (s == null || s.HasBeenDestroyed) return false;
            InteractionInstance c = s.CurrentInteraction;
            return c != null && c.GetType() == sSuType && object.ReferenceEquals(c.Target, area);
        }

        public class SurfLineHook
        {
            public void CleanupHook()
            {
                if (ShortcutOn("surfline"))
                {
                    try
                    {
                        InteractionInstance self = (InteractionInstance)(object)this;
                        SurfArea area = self.Target as SurfArea;
                        Queue<ulong> q = area != null ? sSuQueue.GetValue(area) as Queue<ulong> : null;
                        Sim actor = self.InstanceActor as Sim;
                        ulong me = actor != null && actor.SimDescription != null ? actor.SimDescription.SimDescriptionId : 0UL;
                        int n = q != null ? q.Count : 0;
                        for (int i = 0; i < n; i++)
                        {
                            ulong x = q.Dequeue();
                            if (x == me) PatchCounters()[4009]++;
                            else if (!SuSurfing(area, x)) PatchCounters()[4010]++;
                            else q.Enqueue(x);
                        }
                    }
                    catch (Exception) { PatchCounters()[4011]++; }
                }
                CleanupOrig();
            }
            public void CleanupOrig() { } // receives SurfArea.Surf.Cleanup's original header
        }
    }
}
