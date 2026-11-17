using System;
using System.Reflection;
using Sims3.UI;
using Sims3.UI.Hud;

namespace Ts3Fixes
{
    // hudunsub (issue #40; UI only, not save data): the HUD model (Responder.HudModel) lives from the start of a world
    // until InWorldState.Shutdown, but the HUD panels are loaded and unloaded every time live mode is entered
    // (LiveModeState.LoadUI / UnloadUI: build mode, buy mode, map view, and so on). Three panels add handlers to that
    // model in Init and their Dispose does not remove all of them, so every live-mode entry leaves the previous panel
    // (with its windows) behind, rooted by the model's event lists, and the model keeps calling it:
    //   SimDisplay: OccultUpdated, MagicMotiveChanged, LunarUpdate
    //   HudCompetitionPanel: ShowCompetitionPanelEvent, HideCompetitionPanelEvent
    //   HUDBotCompetitionPanel: ShowBotCompetitionPanelEvent, HideBotCompetitionPanelEvent
    // With the switch on, each of the three Dispose methods first removes exactly those handlers, built from the
    // disposing panel itself (a delegate is removed only when its target is that panel), then runs the game's Dispose.
    // Counters: [2108] panels whose handlers were removed, [2109] handlers found missing or errors (panel left as the game has it).
    public static partial class Fixes
    {
        static bool sHudUnsubReady;

        static void ApplyHudUnsub()
        {
            HudUnsubHook.sModel = typeof(SimDisplay).GetField("mHudModel", BindingFlags.Instance | BindingFlags.NonPublic);
            HudUnsubHook.sCompModel = typeof(HudCompetitionPanel).GetField("mHudModel", BindingFlags.Instance | BindingFlags.NonPublic);
            HudUnsubHook.sBotModel = typeof(HUDBotCompetitionPanel).GetField("mHudModel", BindingFlags.Instance | BindingFlags.NonPublic);
            bool a = HudUnsubHook.IsModel(HudUnsubHook.sModel) && HudUnsubHook.IsModel(HudUnsubHook.sCompModel) && HudUnsubHook.IsModel(HudUnsubHook.sBotModel);
            bool b = a && Patch(typeof(SimDisplay), "Dispose", Type.EmptyTypes, typeof(HudUnsubHook), "SimDisplayHook", "SimDisplayOrig");
            bool c = b && Patch(typeof(HudCompetitionPanel), "Dispose", Type.EmptyTypes, typeof(HudUnsubHook), "CompetitionHook", "CompetitionOrig");
            sHudUnsubReady = c && Patch(typeof(HUDBotCompetitionPanel), "Dispose", Type.EmptyTypes, typeof(HudUnsubHook), "BotHook", "BotOrig");
            if (!sHudUnsubReady) AppliedLog().Add("hudunsub: off (model field " + a + ")");
        }

        // The hook methods run with the panel as `this` (the swap puts them in place of the panel's Dispose); the hook class is not
        // a subclass, so the panel is only used through reflection.
        public class HudUnsubHook
        {
            // One FieldInfo per class. GetValue does not check the object's class here: SimDisplay's field (its 42nd) read on a
            // competition panel (11 fields) is a word past the object, and invoking on that word crashed the game at world quit.
            internal static FieldInfo sModel, sCompModel, sBotModel;
            internal static bool IsModel(FieldInfo f) { return f != null && f.FieldType == typeof(IHudModel); }
            static bool sThrew;

            public void SimDisplayHook()
            {
                Remove(this, sModel, "OccultUpdated", "OnOccultChanged", "MagicMotiveChanged", "OnMagicMotiveChange", "LunarUpdate", "OnLunarCycleUpdate");
                SimDisplayOrig();
            }
            public void SimDisplayOrig() { } // receives SimDisplay.Dispose's original header

            public void CompetitionHook()
            {
                Remove(this, sCompModel, "ShowCompetitionPanelEvent", "OnShowPanel", "HideCompetitionPanelEvent", "OnHidePanel");
                CompetitionOrig();
            }
            public void CompetitionOrig() { } // receives HudCompetitionPanel.Dispose's original header

            public void BotHook()
            {
                Remove(this, sBotModel, "ShowBotCompetitionPanelEvent", "OnShowPanel", "HideBotCompetitionPanelEvent", "OnHidePanel");
                BotOrig();
            }
            public void BotOrig() { } // receives HUDBotCompetitionPanel.Dispose's original header

            // pairs of (event name on IHudModel, handler method name on the panel)
            static void Remove(object panel, FieldInfo field, params string[] pairs)
            {
                if (!sHudUnsubReady || !ShortcutOn("hudunsub")) return;
                try
                {
                    object model = field.GetValue(panel);
                    if (model == null) return;  // already disposed
                    int[] c = PatchCounters();
                    for (int i = 0; i < pairs.Length; i += 2)
                    {
                        EventInfo ev = typeof(IHudModel).GetEvent(pairs[i]);
                        MethodInfo h = panel.GetType().GetMethod(pairs[i + 1], BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                        if (ev == null || h == null) { c[2109]++; continue; }
                        ev.RemoveEventHandler(model, Delegate.CreateDelegate(ev.EventHandlerType, panel, h));
                    }
                    c[2108]++;
                }
                catch (Exception e)
                {
                    PatchCounters()[2109]++;
                    if (sThrew) return;
                    sThrew = true;
                    AppliedLog().Add("hudunsub: threw " + (e.InnerException ?? e).Message + " (the game's own Dispose ran)");
                }
            }
        }
    }
}
