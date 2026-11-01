using System;
using System.Reflection;
using System.Runtime.Serialization;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Careers;
using Sims3.Gameplay.Interactions;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // graduationtable (docs/508, core Build 18): the waiting loops of School.GraduateInCityHall and School.AttendGraduation
    // (WaitingLoopDel) read School.sGraduatingSims without a null check. The table is null until a graduation is scheduled
    // and after the ceremony's clean-up, yet a Sim can still be inside the rabbit hole in the waiting stage (seen: an
    // attendee whose graduate was dropped). The NullReferenceException ends the interaction through ErrorTrap. EA's own
    // WatchingLoopDel of the same class checks the table.
    // Fix, switch on (whole-method wraps): with a null table nothing can be en route, so the loop takes EA's own "nothing to
    // wait for" exit (the actor gets ExitReason.StageComplete, as EA's code does when no graduate is en route) instead of
    // running EA's body. With a table, EA's body runs as it is.
    // Not ported (mid-method, docs/506): Build 18's check of AttendGraduation's ActiveStage cast.
    // [2030] waiting-loop ticks that found no graduation table.
    public static partial class Fixes
    {
        public class GraduationTableHook
        {
            public void CityHallWaitHook(StateMachineClient smc, InteractionInstance.LoopData loopData)
            {
                if (GraduationNoTable(this)) return;
                CityHallWaitOrig(smc, loopData);
            }
            public void AttendWaitHook(StateMachineClient smc, InteractionInstance.LoopData loopData)
            {
                if (GraduationNoTable(this)) return;
                AttendWaitOrig(smc, loopData);
            }
            public void CityHallWaitOrig(StateMachineClient smc, InteractionInstance.LoopData loopData) { }  // School.GraduateInCityHall.WaitingLoopDel
            public void AttendWaitOrig(StateMachineClient smc, InteractionInstance.LoopData loopData) { }    // School.AttendGraduation.WaitingLoopDel
        }
        // The check calls the swapped WaitingLoopDel through a delegate: MethodInfo.Invoke fails on a swapped method
        // ("Unable to invoke an invalid target", EaFixes.cs PrefRemoveFn).
        delegate void GradWaitFn(StateMachineClient smc, InteractionInstance.LoopData loopData);
        static readonly FieldInfo sGradTable = typeof(School).GetField("sGraduatingSims", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        static readonly FieldInfo sInstanceActor = typeof(InteractionInstance).GetField("mInstanceActor", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        static int sGradLines;

        static bool GraduationNoTable(object interaction)
        {
            if (!ShortcutOn("graduationtable") || sGradTable == null) return false;
            try
            {
                if (sGradTable.GetValue(null) != null) return false;
                InteractionInstance ii = interaction as InteractionInstance;
                Sim a = ii != null ? ii.InstanceActor as Sim : null;
                if (a == null) return false;
                a.AddExitReason(ExitReason.StageComplete);
                PatchCounters()[2030]++;
                if (sGradLines++ < 5) ShortcutLog("graduationtable: " + interaction.GetType().Name + " of " + a.FullName + ": no graduation table, the wait ends");
                return true;
            }
            catch (Exception e)
            {
                if (sGradLines++ < 5) ShortcutLog("graduationtable: hook: " + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        // Repro check (tools/harness/repros.json graduation-table-check). Needs no graduation: the table must be null (no
        // graduation scheduled; the check throws otherwise). For each of the two interaction types, an object (not
        // constructed) gets a Sim as its actor and its WaitingLoopDel is called. "broken" (switch off) throws unless both calls
        // throw NullReferenceException. "fixed" (switch on) throws unless both return, the actor has StageComplete, and [2030]
        // went up by 2. The actor's StageComplete exit reason is removed again in both arms (only when the check added it).
        public static string GraduationTableCheck(string what)
        {
            string state = "switch " + ShortcutOn("graduationtable") + ", table " + (sGradTable == null ? "field not found" : sGradTable.GetValue(null) == null ? "null" : "set")
                + ", no-table ticks so far " + PatchCounters()[2030];
            if (what == "state") return state;
            if (what != "broken" && what != "fixed") return "unknown mode " + what + " (state, broken, fixed)";
            if (sGradTable == null || sGradTable.GetValue(null) != null) throw new InvalidOperationException("graduationtable " + what + ": precondition: the table must be null; " + state);
            Sim actor = null;
            foreach (Sim s in Sims3.Gameplay.Queries.GetObjects<Sim>())
                if (s != null && !s.HasBeenDestroyed && !s.HasExitReason(ExitReason.StageComplete)) { actor = s; break; }
            if (actor == null) throw new InvalidOperationException("graduationtable " + what + ": no Sim to use as actor; " + state);
            int c0 = PatchCounters()[2030];
            string res = "";
            int nre = 0, returned = 0;
            foreach (Type t in new Type[] { typeof(School.GraduateInCityHall), typeof(School.AttendGraduation) })
            {
                object ii = FormatterServices.GetUninitializedObject(t);
                FieldInfo fa = t.GetField("Actor", BindingFlags.Instance | BindingFlags.Public);
                MethodInfo wait = t.GetMethod("WaitingLoopDel", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (fa == null || wait == null) throw new InvalidOperationException("graduationtable: " + t.Name + ".Actor or WaitingLoopDel not found");
                fa.SetValue(ii, actor);
                if (sInstanceActor == null) throw new InvalidOperationException("graduationtable: InteractionInstance.mInstanceActor not found");
                sInstanceActor.SetValue(ii, actor);  // InstanceActor (the hook's read) is set by Init, which this object never ran
                GradWaitFn call = (GradWaitFn)Delegate.CreateDelegate(typeof(GradWaitFn), ii, wait);
                try { call(null, new InteractionInstance.LoopData()); returned++; res += t.Name + " returned; "; }
                catch (NullReferenceException) { nre++; res += t.Name + " threw NullReferenceException; "; }
                catch (Exception e) { res += t.Name + " threw " + e.GetType().Name + "; "; }
            }
            bool exitSet = actor.HasExitReason(ExitReason.StageComplete);
            if (exitSet) actor.RemoveExitReason(ExitReason.StageComplete);
            res += "actor " + actor.FullName + " StageComplete " + exitSet + ", ticks +" + (PatchCounters()[2030] - c0);
            bool ok = what == "fixed" ? returned == 2 && exitSet && PatchCounters()[2030] - c0 == 2 : nre == 2 && !exitSet;
            if (!ok) throw new InvalidOperationException("graduationtable " + what + ": wrong outcome: " + res + "; " + state);
            return res;
        }
    }
}
