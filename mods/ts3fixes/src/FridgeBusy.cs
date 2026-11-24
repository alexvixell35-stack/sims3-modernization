using System;
using System.Collections.Generic;
using System.Text;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects.Appliances;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Agent checks for repro fridge-busy-queue (user 11-24: "Sims route failing while queuing up a fridge interaction while
    // another sim is already using it or about to"). Check-only, no switch, changes nothing but the one queued action:
    //   "push <full name>":  queues EA's Fridge_Have on the nearest fridge of the Sim's lot as a player's click would
    //                        (UserDirected, not autonomous), from EA's own Fridge.GetCookRandomRecipeInteraction (a random
    //                        single-serving recipe); the repro's do_interaction could not pick a Fridge_Have menu entry.
    //                        When EA's test refuses, the answer names the reason (the greyed-out tooltip).
    //   "state":             the fridge's users, its line, and for each Sim with a fridge action queued: what it runs now
    //                        and where it stands, one line in the log ("fridgebusy: ...") and the answer.
    // Only EA's own definition and instance go into the queue (a save keeps nothing of ours).
    public static partial class Fixes
    {
        public static string FridgeBusyCheck(string what)
        {
            string[] a = (what ?? "").Split(new char[] { ' ' }, 2);
            string r;
            try
            {
                if (a[0] == "push" && a.Length == 2) r = FbPush(a[1]);
                else if (a[0] == "state") r = FbState();
                else return "fridgebusy <push <full name>|state>";
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

        static string FbPush(string name)
        {
            Sim s = null;
            foreach (Sim x in Sims3.Gameplay.Queries.GetObjects<Sim>()) if (x.FullName == name) { s = x; break; }
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

        static string FbState()
        {
            StringBuilder b = new StringBuilder();
            foreach (Fridge f in Sims3.Gameplay.Queries.GetObjects<Fridge>())
            {
                if (f == null || f.HasBeenDestroyed || f.LotCurrent == null || !f.LotCurrent.IsResidentialLot) continue;
                StringBuilder u = new StringBuilder();
                foreach (Sim o in f.ActorsUsingMe) u.Append(u.Length > 0 ? ", " : "").Append(o == null ? "?" : o.FirstName);
                b.Append(f.GetType().Name).Append(" users [").Append(u).Append("] line [").Append(FbLine(f)).Append("]");
                foreach (Sim s in Sims3.Gameplay.Queries.GetObjects<Sim>())
                {
                    if (s == null || s.LotCurrent != f.LotCurrent || s.InteractionQueue == null) continue;
                    bool wants = false;
                    foreach (InteractionInstance i in s.InteractionQueue.InteractionList) if (i != null && i.Target == f) { wants = true; break; }
                    if (!wants) continue;
                    InteractionInstance cur = s.CurrentInteraction;
                    b.Append("; ").Append(s.FirstName).Append(": ").Append(cur == null ? "nothing" : cur.GetType().Name)
                        .Append(cur != null && cur.Target == f ? " (this fridge)" : "")
                        .Append(", ").Append((s.Position - f.Position).Length().ToString("0.0")).Append(" m");
                }
                b.Append(" | ");
            }
            return b.Length == 0 ? "no fridge on a residential lot" : b.ToString();
        }
    }
}
