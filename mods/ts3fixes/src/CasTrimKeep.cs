using System;
using System.Collections;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.UI;
using Sims3.SimIFace;
using Sims3.SimIFace.CAS;

namespace Ts3Fixes
{
    // [CasTrim] Mode 3 / 4 (docs/539 §6, native castrim.cpp): the script half of the save sweep. Before a save: the keep list
    // (outfit keys of the active household, the selected Sim and Sims of unknown scope, with CurrentOutfit and maternity
    // maps); after it: the pump that lets native erase a batch per tick. Text channel ops: 14 a save starts, 15 one key word,
    // 16 list ended, 17 the save returned, 18 one batch, 19 are the save hooks in. No hook at all without native's
    // save hooks; every op answers false below Mode 3. Anything that goes wrong here marks the list incomplete: native then
    // sweeps nothing. Nothing goes into the save.
    public static partial class Fixes
    {
        static readonly FieldInfo sCtMaternity = typeof(SimDescriptionCore).GetField("mMaternityOutfits", BindingFlags.Instance | BindingFlags.NonPublic);

        static void ApplyCasTrimKeep()
        {
            try { if (!DiagSend(19, 0)) return; } catch (Exception) { return; }
            Patch(typeof(OptionsModel), "SaveGameSynchronous", new Type[] { typeof(bool), typeof(bool) }, typeof(CtKeepSaveHook), "SaveHook", "SaveOrig");
        }

        static bool CtStandDown() { return GameStates.IsCasState || GameStates.IsEditTownState; }

        static void CtKeepKey(ResourceKey k)
        {
            DiagSend(15, (uint)k.InstanceId);
            DiagSend(15, (uint)(k.InstanceId >> 32));
            DiagSend(15, k.TypeId);
            DiagSend(15, k.GroupId);
        }
        static void CtKeepMap(OutfitCategoryMap map)
        {
            if (map == null) return;
            foreach (DictionaryEntry e in map)
            {
                ArrayList list = e.Value as ArrayList;
                if (list == null) continue;
                foreach (SimOutfit o in list) if (o != null) CtKeepKey(o.Key);
            }
        }
        static void CtKeepSim(SimDescription d)
        {
            CtKeepMap(d.Outfits);
            CtKeepMap(sCtMaternity.GetValue(d) as OutfitCategoryMap);
            Sim s = d.CreatedSim;
            if (s != null && s.Proxy != null) CtKeepKey(s.CurrentOutfit.Key);
        }
        static bool CtKeepList()
        {
            Household active = Household.ActiveHousehold;
            if (active != null) foreach (SimDescription d in active.AllSimDescriptions) CtKeepSim(d);
            Sim selected = PlumbBob.SelectedActor;
            if (selected != null) CtKeepSim(selected.SimDescription);
            foreach (Sim s in Sims3.Gameplay.Queries.GetObjects<Sim>())
            {
                if (s.SimDescription == null) return false;  // a Sim whose outfits cannot be named: unknown scope, no sweep
                if (s.Household == null || s.IsSelectable) CtKeepSim(s.SimDescription);  // unknown scope counts as household
            }
            return true;
        }
        static void CtKeepBegin()
        {
            bool stand = CtStandDown(), ok = false;
            if (!DiagSend(14, stand ? 1u : 0u)) return;
            try { ok = !stand && CtKeepList(); } catch (Exception) { }
            DiagSend(16, ok ? 1u : 0u);
        }
        static void CtSweepPump()
        {
            Simulator.Sleep(0);  // SaveGameTask clears its in-progress flag after the hook returns
            while (!WorldGone() && !CtStandDown() && !SaveInProgress() && DiagSend(18, 0)) Simulator.Sleep(0);
        }

        public class CtKeepSaveHook
        {
            public bool SaveHook(bool forceUseSaveName, bool forceSaveAs)
            {
                try { CtKeepBegin(); } catch (Exception) { }  // never block a save
                bool saved = SaveOrig(forceUseSaveName, forceSaveAs);
                try { if (DiagSend(17, saved ? 1u : 0u)) Simulator.AddObject(new LoopTask(CtSweepPump)); } catch (Exception) { }
                return saved;
            }
            public bool SaveOrig(bool forceUseSaveName, bool forceSaveAs) { return false; } // OptionsModel.SaveGameSynchronous(bool, bool)
        }
    }
}
