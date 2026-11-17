using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.EventSystem;
using Sims3.Gameplay.Objects.FoodObjects;
using Sims3.UI;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.ObjectComponents;
using Sims3.Gameplay.Objects.Fishing;
using Sims3.Gameplay.Objects.Gardening;
using Sims3.Gameplay.Opportunities;
using Sims3.Gameplay.Seasons;
using Sims3.Gameplay.Services;
using Sims3.Gameplay.Situations;
using Sims3.Gameplay.Skills;
using Sims3.Gameplay.TuningValues;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // butlerpayalarm (docs/593 V1), default off; an EA bug fix as EA intended, household included. Split out of the
    // docs/593 batch because serviceshowalarm's ServiceSituation.CleanUp hook (Batch582Logic.cs) calls ButlerCleanUp.
    public static partial class Fixes
    {
        // ---- B13 §16 butlerpayalarm (docs/593 V1) --------------------------------------------------------------------
        // [verified, decompiled; trigger inferred] ButlerSituation.OnArriveOnLot adds a persisted weekly PayButler alarm
        // owned by the butler; only EndService removes it. A butler situation that ends another way (fired, quit, the
        // situation exited) keeps the alarm on a Sim with a virtual home, and it charges the household every week. Fix:
        // ServiceSituation.CleanUp (the serviceshowalarm swap, which calls ButlerCleanUp after EA's CleanUp) removes it.
        // Counters: [2926] alarms removed, [2927] left (switch off), [2928] errors.
        static readonly FieldInfo sBpAlarm = B12F(typeof(ButlerSituation), "mPayButlerAlarm");
        static string sBpM13 = "";

        static void ApplyButlerPayAlarm()
        {
            sBpM13 = SpNames("ButlerSituation.mPayButlerAlarm", sBpAlarm);
            if (sBpM13.Length > 0) AppliedLog().Add("butlerpayalarm: not found: " + sBpM13);
        }

        internal static bool BpRemove(bool isButler, bool armed) { return isButler && armed; }

        // Called by BSsHook.CleanHook (Batch582Logic.cs) after ServiceSituation.CleanUp.
        static void ButlerCleanUp(ServiceSituation ss)
        {
            if (sBpAlarm == null || !(ss is ButlerSituation)) return;
            try
            {
                AlarmHandle h = (AlarmHandle)sBpAlarm.GetValue(ss);
                if (!BpRemove(true, h != AlarmHandle.kInvalidHandle)) return;
                if (!ShortcutOn("butlerpayalarm")) { PatchCounters()[2927]++; return; }
                if (ss.Worker != null) ss.Worker.RemoveAlarm(h); else AlarmManager.Global.RemoveAlarm(h);
                sBpAlarm.SetValue(ss, AlarmHandle.kInvalidHandle);
                PatchCounters()[2926]++;
            }
            catch (Exception) { PatchCounters()[2928]++; }
        }

        public static object ButlerPayAlarmCheck(string what)
        {
            bool f = false, c = false;
            try { f = BpRemove(true, true); c = !BpRemove(false, true) && !BpRemove(true, false); } catch (Exception) { }
            string miss = sBpM13; miss = EpJoin(miss, EpLive(typeof(BSsHook), 2));
            return B12Check(what, "butlerpayalarm", 2926, new string[] { "alarms removed", "left (switch off)", "errors" }, miss, "armed butler alarm removed", f, "other services / no alarm untouched", c);
        }
    }
}
