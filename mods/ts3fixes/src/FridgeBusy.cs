using System;
using System.Collections.Generic;
using System.Text;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects.Appliances;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Agent checks for repro fridge-busy-queue (user 11-24: "Sims route failing while queuing up a fridge interaction while
    // another sim is already using it or about to"). Check-only, no switch, changes nothing but the one queued action:
    //   "push <full name>":  queues EA's Fridge_Have on the nearest fridge of the Sim's lot as a player's click would
    //                        (UserDirected, not autonomous), from EA's own Fridge.GetCookRandomRecipeInteraction (a random
    //                        single-serving recipe); the repro's do_interaction could not pick a Fridge_Have menu entry.
    //                        When EA's test refuses, the answer names the reason (the greyed-out tooltip).
    //   "state <full name>": the state of the fridge nearest the Sim (FbDescribe), one line in the log and the answer.
    //   "watch <full name>": logs that state each time it changes (every 3 ticks, with the sim-minute), up to kFbWatchMax
    //                        lines; a new watch ends the last one. The 11-24 run's per-5-s snapshots of every residential
    //                        fridge were cut off before the household's own and too coarse to see who blocks the slot.
    // Only EA's own definition and instance go into the queue (a save keeps nothing of ours). The watch is static state on
    // a Simulator task (gone with the world), reads only.
    public static partial class Fixes
    {
        public static string FridgeBusyCheck(string what)
        {
            string[] a = (what ?? "").Split(new char[] { ' ' }, 2);
            string r;
            try
            {
                if (a.Length < 2) return "fridgebusy <push|state|watch> <full name>";
                if (a[0] == "push") r = FbPush(a[1]);
                else if (a[0] == "state" || a[0] == "watch")
                {
                    Sim s = FbSim(a[1]);
                    Fridge f = s == null ? null : FbFridge(s);
                    if (f == null) r = s == null ? "no Sim " + a[1] : "no fridge on " + a[1] + "'s lot";
                    else if (a[0] == "state") r = FbDescribe(f);
                    else
                    {
                        int gen = ++sFbWatchGen;
                        Simulator.AddObject(new LoopTask(delegate { FbWatchLoop(f, gen); }));
                        r = "watching " + f.GetType().Name + ": " + FbDescribe(f);
                    }
                }
                else return "fridgebusy <push|state|watch> <full name>";
            }
            catch (ResetException) { throw; }
            catch (Exception e) { r = "error " + e.GetType().Name + ": " + e.Message; }
            DiagLine("fridgebusy: " + what + ": " + r);
            return r;
        }

        static Fridge FbFridge(Sim s)
        {
            Fridge best = null;
            float bestD = float.MaxValue;
            if (s.LotCurrent == null) return null;
            foreach (Fridge f in s.LotCurrent.GetObjects<Fridge>())
            {
                if (f == null || f.HasBeenDestroyed) continue;
                float d = (f.Position - s.Position).LengthSqr();
                if (d < bestD) { bestD = d; best = f; }
            }
            return best;
        }

        static Sim FbSim(string name)
        {
            foreach (Sim x in Sims3.Gameplay.Queries.GetObjects<Sim>()) if (x.FullName == name) return x;
            return null;
        }

        static string FbPush(string name)
        {
            Sim s = FbSim(name);
            if (s == null) return "no Sim " + name;
            Fridge f = FbFridge(s);
            if (f == null) return "no fridge on " + name + "'s lot";
            InteractionDefinition def = f.GetCookRandomRecipeInteraction(s.LotCurrent, s, s, false);
            Fridge_Have.Definition fd = def as Fridge_Have.Definition;
            string recipe = fd != null && fd.ChosenRecipe != null ? fd.ChosenRecipe.Key : "?";
            InteractionPriority pr = new InteractionPriority(InteractionPriorityLevel.UserDirected);
            InteractionObjectPair iop = new InteractionObjectPair(def, f);
            InteractionInstanceParameters p = new InteractionInstanceParameters(iop, s, pr, false, true);
            GreyedOutTooltipCallback cb = null;
            if (!InteractionDefinitionUtilities.IsPass(def.Test(ref p, ref cb)))
            {
                string why = "?";
                try { if (cb != null) why = cb(); } catch (Exception) { }
                return "refused " + recipe + " (" + why + ")";
            }
            InteractionInstance ii = def.CreateInstance(f, s, pr, false, true);
            if (ii == null) return "no instance for " + recipe;
            if (!s.InteractionQueue.Add(ii)) return "queue refused " + recipe;
            return "queued " + recipe + " on " + f.GetType().Name + ", fridge in use " + f.InUse + ", line " + FbLine(f);
        }

        static string FbLine(Fridge f)
        {
            List<Sim> line = f.Line == null ? null : f.Line.SimsInQueue;
            if (line == null || line.Count == 0) return "empty";
            StringBuilder b = new StringBuilder();
            foreach (Sim o in line) b.Append(b.Length > 0 ? ", " : "").Append(o == null ? "?" : o.FirstName);
            return b.ToString();
        }

        const int kFbWatchMax = 800;
        static int sFbWatchGen, sFbWatchN;

        static void FbWatchLoop(Fridge f, int gen)
        {
            string last = null;
            while (gen == sFbWatchGen && sFbWatchN < kFbWatchMax && !f.HasBeenDestroyed)
            {
                Simulator.Sleep(3);
                string st;
                try { st = FbDescribe(f); }
                catch (ResetException) { throw; }
                catch (Exception e) { st = "error " + e.GetType().Name + ": " + e.Message; }
                if (st == last) continue;
                last = st;
                ++sFbWatchN;
                DiagLine("fridgebusy: watch " + SimClock.ElapsedTime(TimeUnit.Minutes).ToString("0.0") + ": " + st);
            }
        }

        // users, the line in order, Sims on the routing slot, and Sims with this fridge queued elsewhere; each Sim as
        // name(running interaction, exit reasons when set, metres from the slot)
        static string FbDescribe(Fridge f)
        {
            Vector3 slot = f.GetPositionOfSlot(Slot.RoutingSlot_0);
            List<Sim> line = f.Line == null ? null : f.Line.SimsInQueue;
            StringBuilder u = new StringBuilder(), l = new StringBuilder(), on = new StringBuilder(), q = new StringBuilder();
            foreach (Sim o in f.ActorsUsingMe) FbSimText(u, o, slot);
            if (line != null) foreach (Sim o in line) FbSimText(l, o, slot);
            foreach (Sim s in Sims3.Gameplay.Queries.GetObjects<Sim>())
            {
                if (s == null || s.LotCurrent != f.LotCurrent || s.InteractionQueue == null) continue;
                if ((s.Position - slot).LengthSqr() < 0.81f && !f.IsActorUsingMe(s)) FbSimText(on, s, slot);
                if (f.IsActorUsingMe(s) || (line != null && line.Contains(s))) continue;
                foreach (InteractionInstance i in s.InteractionQueue.InteractionList)
                    if (i != null && i.Target == f) { FbSimText(q, s, slot); break; }
            }
            return "users [" + u + "] line [" + l + "] on slot [" + on + "] queued [" + q + "]";
        }

        static void FbSimText(StringBuilder b, Sim s, Vector3 slot)
        {
            if (b.Length > 0) b.Append(", ");
            if (s == null) { b.Append("?"); return; }
            InteractionInstance cur = s.CurrentInteraction;
            b.Append(s.FirstName).Append('(').Append(cur == null ? "nothing" : cur.GetType().Name);
            if (s.ExitReason != ExitReason.None) b.Append(' ').Append(s.ExitReason.ToString());
            b.Append(' ').Append((s.Position - slot).Length().ToString("0.0")).Append(')');
        }
    }
}
