using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Services;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Agent
{
    // service-leave-reset repro, live arms (ts3fixes ServiceLeave.cs, serviceleave; docs/564 §1, docs/357 §4): a repairman
    // called to the active household's home lot is reset by EA's SetObjectToReset once on the world lot and once on the lot.
    // Only the service worker is touched (re-checked: never the active household, a selectable Sim or unknown scope).
    //   service_leave_live {op: "request"}: EA's Repairman.MakeServiceRequest for the home lot (a player's phone call).
    //   service_leave_live {op: "street"}: the waiting worker onto a world-lot land spot (Teleport Me Here) if it is on a lot.
    //   service_leave_live {op: "reset"}: notes the state's timeout handle, its time left and ts3fixes [1832] [2517] [2518],
    //     then resets the worker.
    //   service_leave_live {op: "status"}: situation, state, worker lot, the state's armed timeout (mAlarmHandle /
    //     mTimeUpHandle, EA's GetTimeLeft) and, against the last "reset": same_handle, time left, counter deltas.
    //     street_ready / lot_ready: the arm can start. street_pass: the handle changed and [2517] rose (re-armed).
    //     lot_pass: same handle, time left not above the noted one, [1832] rose, [2517] and [2518] did not.
    //   service_leave_live {op: "verdict", arm: "street" | "lot"}: the arm's result as one "serviceleave-live:" log line.
    //   service_leave_live {op: "dismiss"}: the request withdrawn, the worker sent away (EA's SetToLeave).
    public static partial class Handlers
    {
        static void RegisterServiceLeaveLive()
        {
            Dispatcher.Register("service_leave_live", ServiceLeaveLive);
        }

        static bool sSllNoted;
        static AlarmHandle sSllHandle;
        static float sSllTime;
        static string sSllState;
        static readonly int[] sSllBase = new int[3];

        static int[] SllCounters()
        {
            int[] c = AppDomain.CurrentDomain.GetData("ts3fixes.counters") as int[];
            return c != null && c.Length > 2518 ? new int[] { c[1832], c[2517], c[2518] } : new int[3];
        }

        static Lot SllLot()
        {
            Household hh = Household.ActiveHousehold;
            if (hh == null || hh.LotHome == null) throw new McpException("service_leave_live: the active household has no home lot");
            return hh.LotHome;
        }

        static RepairmanSituation SllSituation(Lot lot)
        {
            foreach (RepairmanSituation s in Situation.GetSituations<RepairmanSituation>())
                if (s != null && s.Lot == lot && !s.SituationExited && s.Worker != null && !s.Worker.HasBeenDestroyed) return s;
            return null;
        }

        // the state's armed timeout: field name and handle (null = the state holds none)
        static string SllArmed(object state, out AlarmHandle h)
        {
            h = AlarmHandle.kInvalidHandle;
            if (state == null) return null;
            foreach (string f in new string[] { "mAlarmHandle", "mTimeUpHandle" })
            {
                FieldInfo fi = state.GetType().GetField(f, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (fi == null || fi.FieldType != typeof(AlarmHandle)) continue;
                AlarmHandle v = (AlarmHandle)fi.GetValue(state);
                if (v == AlarmHandle.kInvalidHandle) continue;
                h = v;
                return f;
            }
            return null;
        }

        static object ServiceLeaveLive(Args a)
        {
            RequireLive();
            string op = a.Str("op", "status");
            Lot lot = SllLot();
            Dictionary<string, object> r = Util.Obj();
            if (op == "request")
            {
                if (Repairman.Instance == null) throw new McpException("service_leave_live: no Repairman service in this world");
                Repairman.Instance.MakeServiceRequest(lot, true, ObjectGuid.InvalidObjectGuid);
                r["requested"] = lot.Name;
                return r;
            }
            RepairmanSituation svc = SllSituation(lot);
            if (op == "dismiss")
            {
                if (Repairman.Instance != null) Repairman.Instance.MakeServiceRequest(lot, false, ObjectGuid.InvalidObjectGuid);
                if (svc != null) svc.SetToLeave();
                sSllNoted = false;
                r["dismissed"] = svc != null;
                return r;
            }
            r["found"] = svc != null;
            if (svc == null)
            {
                if (op != "status") throw new McpException("service_leave_live: no repairman situation on " + lot.Name + " (op request first)");
                return r;
            }
            Sim w = svc.Worker;
            string why = DtScope(w);
            if (why != null) throw new McpException("service_leave_live: refused, " + w.FullName + " is " + why);
            object state = svc.Child;
            AlarmHandle h;
            string field = SllArmed(state, out h);
            string name = state == null ? "none" : state.GetType().Name;
            Lot cur = w.LotCurrent;
            bool world = cur != null && cur.IsWorldLot, onLot = cur == lot;
            if (op == "street")
            {
                if (!world) r["setup"] = SetupSimsAt(new List<Sim> { w }, null, SrStreetSpot(lot), true, false, false, true);
            }
            else if (op == "reset")
            {
                if (field == null) throw new McpException("service_leave_live: state " + name + " holds no armed timeout");
                int[] b = SllCounters();
                for (int i = 0; i < 3; i++) sSllBase[i] = b[i];
                sSllHandle = h;
                sSllTime = AlarmManager.Global.GetTimeLeft(h, TimeUnit.Minutes);
                sSllState = name + "." + field;
                sSllNoted = true;
                w.SetObjectToReset();
            }
            else if (op != "status" && op != "verdict") throw new McpException("service_leave_live: op request, street, reset, status, verdict or dismiss");
            int[] now = SllCounters();
            float left = field == null ? 0f : AlarmManager.Global.GetTimeLeft(h, TimeUnit.Minutes);
            int skips = now[0] - sSllBase[0], wait = now[1] - sSllBase[1], work = now[2] - sSllBase[2];
            bool same = sSllNoted && field != null && h == sSllHandle, sameState = sSllNoted && sSllState == name + "." + field;
            bool streetPass = sSllNoted && sameState && !same && left > 0f && wait >= 1 && skips == 0;
            bool lotPass = sSllNoted && sameState && same && left > 0f && left <= sSllTime + 0.01f && skips >= 1 && wait == 0 && work == 0;
            r["worker"] = w.FullName;
            r["state"] = name;
            r["field"] = field;
            r["lot"] = cur != null ? cur.Name : null;
            r["world"] = world;
            r["on_lot"] = onLot;
            r["time_left_min"] = Math.Round(left, 2);
            r["street_ready"] = name == "WaitToRoute" && field != null && world;
            r["lot_ready"] = (name == "Fixing" || name == "HangAroundBeforeLeaving") && field != null && onLot;
            r["same_handle"] = same;
            r["live_skips"] = skips;
            r["rearm_wait"] = wait;
            r["rearm_work"] = work;
            r["street_pass"] = streetPass;
            r["lot_pass"] = lotPass;
            if (op == "verdict")
            {
                string arm = a.Str("arm");
                if (arm != "street" && arm != "lot") throw new McpException("service_leave_live: arm street or lot");
                bool pass = arm == "street" ? streetPass : lotPass;
                r["pass"] = pass;
                DiagLine("serviceleave-live: arm=" + arm + " state=" + (sSllState ?? "none") + " now=" + name + "." + (field ?? "none") + " same_handle=" + (same ? 1 : 0)
                    + " time=" + sSllTime.ToString("0.00", CultureInfo.InvariantCulture) + ">" + left.ToString("0.00", CultureInfo.InvariantCulture) + " skips=+" + skips + " rearm_wait=+" + wait + " rearm_work=+" + work
                    + " verdict=" + (pass ? "PASS" : "FAIL"));
            }
            return r;
        }
    }
}
