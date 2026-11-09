using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Interactions;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // outfitwaitsaved (issue #40; runs in errortrapsims' hourly alarm, own switch; not save data): the outfit-wait hook (and
    // its stale-wait checks, outfitwaitpoll [933]) only sees waits that began through the hook. A MagicMirror MagicalMakeover.ChangeOutfitCB alarm
    // (AlarmOneShot, ShouldSaveStack) that was saved inside EA's Wait loop resumes at load in EA's original Wait, so the hook
    // never runs for it, and its frames keep the makeover's actor alive for the session and in every later save. al358
    // window 3 (3 trips, the save's own stacks): 5 to 8 of 30 sampled dead Sims were held by yielded frames
    // AlarmOneShot.Simulate > Function.Invoke > AlarmTimerCallback.Invoke > ChangeOutfitCB > SwitchOutfitHelper.ChangeOutfit >
    // Wait, with MagicalMakeover.mInstanceActor = the dead Sim. The pass destroys such a task when the interaction's actor is
    // gone (destroyed, or its description names another Sim object), as NRaas ErrorTrap does at load for a OneShotFunction
    // whose target object is destroyed. The Sim it would dress is gone, so no outfit change is lost. Household rule: tasks
    // of household, selected or unknown-scope owners stay (GoneOwnerHh, DeadSimHolders.cs); no active household, nothing
    // runs. [2083] tasks ended, [936] kept by the rule.
    public static partial class Fixes
    {
        static FieldInfo sOwObjHash, sOwOsfFunction;
        static bool sOwSavedTried, sOwSavedThrew;
        static int sOwSavedLines;
        const string kMakeoverType = "Sims3.Gameplay.Objects.Decorations.MagicMirror+MagicalMakeover";

        static void OutfitWaitSavedPass()
        {
            try
            {
                if (!ShortcutOn("outfitwaitsaved")) return;
                if (!sOwSavedTried)
                {
                    sOwSavedTried = true;
                    Type sim = Type.GetType("ScriptCore.Simulator, ScriptCore");
                    sOwObjHash = sim != null ? sim.GetField("mObjHash", BindingFlags.NonPublic | BindingFlags.Static) : null;
                    sOwOsfFunction = typeof(OneShotFunction).GetField("mFunction", kInstAll);
                    if (sOwOsfFunction == null || sAlarmOneShot == null) sOwObjHash = null;
                    AppliedLog().Add("outfitwaitsaved: " + (sOwObjHash != null ? "ok" : "ScriptCore.Simulator or OneShotFunction differs from the known build, off"));
                }
                if (sOwObjHash == null) return;
                IDictionary tasks = sOwObjHash.GetValue(null) as IDictionary;
                if (tasks == null) return;
                // household rule (CLAUDE.md): no active household = unknown scope for every Sim, so the pass waits
                Sims3.Gameplay.CAS.Household hh = Sims3.Gameplay.CAS.Household.ActiveHousehold;
                if (hh == null) return;
                Sims3.Gameplay.Interfaces.IActor sel = Sims3.Gameplay.Core.PlumbBob.SelectedActor;
                List<ulong> end = new List<ulong>();
                List<string> who = new List<string>();
                foreach (DictionaryEntry kv in tasks)
                {
                    object t = kv.Value;
                    IScriptProxy p = t as IScriptProxy;  // the hash holds the task or its proxy
                    if (p != null) t = p.Target;
                    if (t == null || !sAlarmOneShot.IsInstanceOfType(t)) continue;
                    Delegate f = sOwOsfFunction.GetValue(t) as Delegate;   // Function: AlarmTimerCallback.Invoke
                    Delegate cb = f != null ? f.Target as Delegate : null; // AlarmTimerCallback: ChangeOutfitCB
                    InteractionInstance mm = cb != null ? cb.Target as InteractionInstance : null;
                    if (mm == null || mm.GetType().FullName != kMakeoverType) continue;
                    Sim actor = mm.InstanceActor as Sim;
                    if (actor == null || !EtSimGone(actor)) continue;
                    if (GoneOwnerHh(actor, hh, sel)) { PatchCounters()[936]++; continue; }
                    end.Add((ulong)kv.Key);
                    who.Add(actor.SimDescription != null ? actor.SimDescription.FullName : "?");
                }
                for (int i = 0; i < end.Count; i++)
                {
                    Simulator.DestroyObject(new ObjectGuid(end[i]));
                    PatchCounters()[2083]++;
                    if (sOwSavedLines++ < 10) ShortcutLog("outfitwaitsaved: saved makeover wait for gone Sim " + who[i] + " ended (task 0x" + end[i].ToString("X") + ")");
                }
            }
            catch (Exception e)
            {
                if (!sOwSavedThrew) { sOwSavedThrew = true; AppliedLog().Add("outfitwaitsaved: pass threw " + e.Message); }
            }
        }
    }
}
