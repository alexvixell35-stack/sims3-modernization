using System;
using System.Collections.Generic;
using System.Text;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects.Appliances;
using Sims3.Gameplay.Objects.Plumbing;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // docs/686 §36.10: who stands on a fridge's or sink's routing slot after its use, for how long, doing what, and how long
    // another Sim waits for that slot meanwhile. Read-only probe (changes nothing), started by FacilityPlanCheck("slots
    // start") on the active household's home lot, every 3 ticks; "slots" prints the totals, "slots reset" clears them.
    // Lines "facilityslot: ..." (facilityplanlog or the probe's own cap).
    public static partial class Fixes
    {
        sealed class FsObj
        {
            public GameObject Obj; public Vector3 Slot; public SimQueue Line;
            public List<Sim> Users = new List<Sim>(); public List<float> UseStart = new List<float>(); public List<bool> UseNoted = new List<bool>();
            public Sim After; public float AfterStart; public string AfterWhat;  // a user just done, still on the slot
            public Sim Blocker, Waiter; public float StallStart; public string StallWhat;
        }

        static int sFsGen, sFsLines;
        static int sFsStands, sFsStalls, sFsUses;
        static float sFsUseMin, sFsUseMax;
        static float sFsStandMin, sFsStandMax, sFsStallMin, sFsStallMax;
        static readonly Dictionary<string, float[]> sFsByWhat = new Dictionary<string, float[]>();  // what the Sim did while it stood: n, sum

        static void FsLine(string s) { if (sFsLines++ < 400) DiagLine(s); }

        static string FsDoing(Sim s)
        {
            if (s == null) return "?";
            InteractionInstance cur = s.CurrentInteraction;
            StringBuilder b = new StringBuilder(cur == null ? "nothing" : cur.GetType().Name);
            InteractionInstance next = null;
            int i = 0;
            foreach (InteractionInstance x in s.InteractionQueue.InteractionList) { if (i++ == 1) { next = x; break; } }
            if (next != null) b.Append(" then ").Append(next.GetType().Name);
            if (s.Posture != null) b.Append(", ").Append(s.Posture.GetType().Name);
            if (s.IsHoldingAnything()) b.Append(", holding");
            if (s.SimRoutingComponent != null && s.SimRoutingComponent.IsRouting) b.Append(", routing");
            return b.ToString();
        }

        static bool FsNear(Sim s, Vector3 slot) { return s != null && !s.HasBeenDestroyed && (s.Position - slot).LengthSqr() < 0.49f; }

        static void FsLoop(int gen, Lot lot)
        {
            List<FsObj> objs = new List<FsObj>();
            foreach (Fridge f in lot.GetObjects<Fridge>()) objs.Add(new FsObj { Obj = f, Slot = f.GetPositionOfSlot(Slot.RoutingSlot_0), Line = f.Line });
            foreach (Sink k in lot.GetObjects<Sink>()) objs.Add(new FsObj { Obj = k, Slot = k.GetPositionOfSlot(Slot.RoutingSlot_0), Line = k.Line });
            FsLine("facilityslot: watching " + objs.Count + " fridges and sinks on " + lot.Name);
            while (gen == sFsGen)
            {
                Simulator.Sleep(3);
                try
                {
                    float now = SimClock.ElapsedTime(TimeUnit.Minutes);
                    foreach (FsObj o in objs)
                    {
                        if (o.Obj == null || o.Obj.HasBeenDestroyed) continue;
                        // users that stopped using: start their post-use stand while they are on the slot
                        for (int u = o.Users.Count - 1; u >= 0; u--)
                        {
                            Sim s = o.Users[u];
                            float ud = now - o.UseStart[u];
                            if (s != null && !s.HasBeenDestroyed && o.Obj.IsActorUsingMe(s))
                            {
                                if (ud >= 5f && !o.UseNoted[u]) { o.UseNoted[u] = true; FsLine("facilityslot: " + s.FullName + " uses " + o.Obj.GetType().Name + " for 5 sim-min now, doing " + FsDoing(s) + ", " + (s.Position - o.Slot).Length().ToString("0.0") + " m from the slot"); }
                                continue;
                            }
                            sFsUses++; sFsUseMin += ud; if (ud > sFsUseMax) sFsUseMax = ud;
                            if (ud >= 5f) FsLine("facilityslot: " + (s == null ? "?" : s.FullName) + " used " + o.Obj.GetType().Name + " " + ud.ToString("0.0") + " sim-min, left doing " + FsDoing(s));
                            o.Users.RemoveAt(u); o.UseStart.RemoveAt(u); o.UseNoted.RemoveAt(u);
                            if (FsNear(s, o.Slot) && o.After == null) { o.After = s; o.AfterStart = now; o.AfterWhat = FsDoing(s); }
                        }
                        foreach (Sim s in o.Obj.ActorsUsingMe) if (s != null && !o.Users.Contains(s)) { o.Users.Add(s); o.UseStart.Add(now); o.UseNoted.Add(false); }
                        if (o.After != null && (!FsNear(o.After, o.Slot) || o.Obj.IsActorUsingMe(o.After)))
                        {
                            float d = now - o.AfterStart;
                            sFsStands++; sFsStandMin += d; if (d > sFsStandMax) sFsStandMax = d;
                            float[] w; if (!sFsByWhat.TryGetValue(o.AfterWhat, out w)) sFsByWhat[o.AfterWhat] = w = new float[2];
                            w[0]++; w[1] += d;
                            if (d >= 0.3f) FsLine("facilityslot: " + o.After.FullName + " stood " + d.ToString("0.0") + " sim-min on " + o.Obj.GetType().Name + "'s slot after its use, doing " + o.AfterWhat + "; left doing " + FsDoing(o.After));
                            o.After = null;
                        }
                        // a stall: a Sim that does not use the object stands on its slot while another wants it
                        Sim blocker = null, waiter = null;
                        foreach (Sim s in lot.GetSims())
                        {
                            if (s == null || s.HasBeenDestroyed || o.Obj.IsActorUsingMe(s)) continue;
                            if (blocker == null && FsNear(s, o.Slot)) { blocker = s; continue; }
                            InteractionInstance cur = s.CurrentInteraction;
                            if (waiter == null && ((cur != null && cur.Target == o.Obj) || (o.Line != null && o.Line.SimsInQueue != null && o.Line.SimsInQueue.Contains(s)))) waiter = s;
                        }
                        bool stall = blocker != null && waiter != null && !o.Obj.InUse;
                        if (stall && o.Blocker == null)
                        {
                            o.Blocker = blocker; o.Waiter = waiter; o.StallStart = now; o.StallWhat = FsDoing(blocker);
                            FsLine("facilityslot: stall at " + o.Obj.GetType().Name + ": " + blocker.FullName + " on the slot (" + o.StallWhat + "), " + waiter.FullName + " waits (" + FsDoing(waiter) + ")");
                        }
                        else if (!stall && o.Blocker != null)
                        {
                            float d = now - o.StallStart;
                            sFsStalls++; sFsStallMin += d; if (d > sFsStallMax) sFsStallMax = d;
                            string how = !FsNear(o.Blocker, o.Slot) ? "blocker left (" + FsDoing(o.Blocker) + ")" : o.Obj.InUse ? "object in use (" + (o.Obj.IsActorUsingMe(o.Waiter) ? "the waiter" : "someone") + ")" : "waiter gone (" + FsDoing(o.Waiter) + ")";
                            FsLine("facilityslot: stall at " + o.Obj.GetType().Name + " ended after " + d.ToString("0.0") + " sim-min: " + how);
                            o.Blocker = null; o.Waiter = null;
                        }
                    }
                }
                catch (ResetException) { throw; }
                catch (Exception e) { FsLine("facilityslot: error " + e.GetType().Name + ": " + e.Message); }
            }
        }

        static string FacilitySlotCheck(string what)
        {
            if (what == "start")
            {
                Lot lot = Sims3.Gameplay.CAS.Household.ActiveHousehold == null ? null : Sims3.Gameplay.CAS.Household.ActiveHousehold.LotHome;
                if (lot == null) return "facilityslot: no home lot";
                int gen = ++sFsGen;
                Simulator.AddObject(new LoopTask(delegate { FsLoop(gen, lot); }));
                return "facilityslot: probe started on " + lot.Name;
            }
            if (what == "stop") { sFsGen++; return "facilityslot: probe stopped"; }
            if (what == "reset")
            {
                sFsStands = sFsStalls = sFsUses = 0; sFsUseMin = sFsUseMax = 0f; sFsStandMin = sFsStandMax = sFsStallMin = sFsStallMax = 0f; sFsByWhat.Clear();
                return "facilityslot: totals cleared";
            }
            StringBuilder b = new StringBuilder("facilityslot: uses " + sFsUses + ", " + sFsUseMin.ToString("0.0") + " sim-min (max " + sFsUseMax.ToString("0.0") + "); post-use stands " + sFsStands + ", " + sFsStandMin.ToString("0.0") + " sim-min (max " + sFsStandMax.ToString("0.0")
                + "); stalls " + sFsStalls + ", " + sFsStallMin.ToString("0.0") + " sim-min (max " + sFsStallMax.ToString("0.0") + "); by what:");
            foreach (KeyValuePair<string, float[]> kv in sFsByWhat) b.Append(" [").Append(kv.Key).Append(": ").Append(kv.Value[0]).Append(" x, ").Append(kv.Value[1].ToString("0.0")).Append(" sim-min]");
            string r = b.ToString(); DiagLine(r); return r;
        }
    }
}
