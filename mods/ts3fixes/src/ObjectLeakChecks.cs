using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects.Appliances;
using Sims3.Gameplay.Objects.CookingObjects;
using Sims3.Gameplay.Objects.FoodObjects;
using Sims3.Gameplay.Objects.HobbiesSkills;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Harness checks for shuffleboardbcast (ShuffleboardBroadcast.cs) and cookclicklistener (CookClickListener.cs),
    // docs/546 §17; test only, called through the agent's reflect_call. Both run EA's own code path once, deterministically,
    // and end with "verdict: BROKEN|FIXED|N/A (...)".
    //
    // ShuffleboardCheck
    //   list    Shuffleboard broadcasters per lot (as the leak census counts them)
    //   run     an idle board (no teams, nobody using it) and one NPC: a Play instance is made (not queued), EA's
    //           TriggerReactions runs twice (the round-end sequence of Play.Run), then EA's Cleanup. Off arm: BROKEN = 2
    //           broadcasters of that board are left after Cleanup (the replaced pair). On arm: FIXED = none left, 2410 +2.
    //   clear   disposes the pair the off arm left
    //   result  counters 2409-2411
    // CookClickCheck
    //   list    kObjectStateChanged listeners held by cooking processes: owner destroyed / none / live; counters
    //   run     a fridge (active lot first, else any), a single-serving recipe: EA's CookingProcess constructor with that
    //           fridge as the clicked object and a new serving as Owner (AddObjectListeners hooks the fridge), then the
    //           serving is destroyed (the proxy goes a tick or more later)
    //   verify  (a few seconds after run) the fix's check runs without its 10-minute wait. Off arm: BROKEN = the process
    //           still listens on the fridge. On arm: FIXED = its listener is gone, 2412 +1. N/A while the serving lives.
    //   result  counters 2412-2414
    public static partial class Fixes
    {
        delegate void SbTriggerFn();
        static readonly List<ReactionBroadcaster> sSbCheckLeft = new List<ReactionBroadcaster>();

        public static object ShuffleboardCheck(string what)
        {
            int[] c = PatchCounters();
            if (what == "result") return "shuffleboardbcast: replaced 2409=" + c[2409] + ", disposed 2410=" + c[2410] + ", errors 2411=" + c[2411];
            if (what == "list") return SbList();
            if (what == "clear")
            {
                int n = 0;
                foreach (ReactionBroadcaster b in sSbCheckLeft) { try { b.Dispose(); n++; } catch (Exception) { } }
                sSbCheckLeft.Clear();
                return "shuffleboard check: " + n + " broadcasters disposed";
            }
            if (what == "run") return SbRun();
            return "shuffleboard <list|run|clear|result>";
        }

        static string SbList()
        {
            int total = 0, lots = 0;
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (object o in LotManager.AllLots)
            {
                Lot l = o as Lot;
                int n = l == null ? 0 : SbCount(l, null);
                if (n == 0) continue;
                total += n; lots++;
                if (lots <= 12) sb.Append("\n  ").Append(l.Name).Append(": ").Append(n);
            }
            return "shuffleboard broadcasters: " + total + " on " + lots + " lots" + sb;
        }

        // broadcasters on lot l whose object is a Shuffleboard (board == null) or that board
        static int SbCount(Lot l, Shuffleboard board)
        {
            int n = 0;
            foreach (ReactionBroadcaster b in SbOnLot(l))
            {
                IGameObject bo = b.BroadcastingObject;
                if (board != null ? ReferenceEquals(bo, board) : bo is Shuffleboard) n++;
            }
            return n;
        }

        static List<ReactionBroadcaster> SbOnLot(Lot l)
        {
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            List<ReactionBroadcaster> r = new List<ReactionBroadcaster>();
            FieldInfo sdf = typeof(Lot).GetField("mSavedData", inst);
            object sd = sdf == null ? null : sdf.GetValue(l);
            FieldInfo rf = sd == null ? null : sd.GetType().GetField("mReactions", inst);
            List<ReactionBroadcaster> rx = rf == null ? null : rf.GetValue(sd) as List<ReactionBroadcaster>;
            if (rx != null) foreach (ReactionBroadcaster b in rx) if (b != null) r.Add(b);
            return r;
        }

        static string SbRun()
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            Type play = typeof(Shuffleboard).GetNestedType("Play", BindingFlags.Public | BindingFlags.NonPublic);
            FieldInfo teams = typeof(Shuffleboard).GetField("mTeams", all);
            MethodInfo trigger = play == null ? null : play.GetMethod("TriggerReactions", all);
            FieldInfo single = play == null ? null : play.GetField("Singleton", all);
            if (trigger == null || single == null || teams == null || sSbBcast == null) return "verdict: N/A (Shuffleboard+Play members not found)";
            Shuffleboard board = null;
            Lot active = Sim.ActiveActor != null ? Sim.ActiveActor.LotCurrent : null;
            foreach (Shuffleboard s in Sims3.Gameplay.Queries.GetObjects<Shuffleboard>())
            {
                if (s == null || s.HasBeenDestroyed || s.LotCurrent == null || teams.GetValue(s) != null || s.ReferenceList.Count > 0) continue;
                if (board == null || s.LotCurrent == active) board = s;
            }
            if (board == null) return "verdict: N/A (no idle shuffleboard in the world)";
            Sim npc = null;
            foreach (Sim s in LotManager.Actors)
                if (s != null && !s.HasBeenDestroyed && s.InWorld && s.SimDescription != null && s.SimDescription.TeenOrAbove && IsNpcSim(s)) { npc = s; break; }
            if (npc == null) return "verdict: N/A (no NPC teen or older in the world)";
            Lot lot = board.LotCurrent;
            int before = SbCount(lot, board), disposed = PatchCounters()[2410];
            InteractionInstance ii = ((InteractionDefinition)single.GetValue(null)).CreateInstance(board, npc, new InteractionPriority(InteractionPriorityLevel.UserDirected), false, true);
            SbTriggerFn fire = (SbTriggerFn)Delegate.CreateDelegate(typeof(SbTriggerFn), ii, trigger);  // swapped: no MethodInfo.Invoke
            fire();  // after the throw
            ReactionBroadcaster first = sSbBcast.GetValue(ii) as ReactionBroadcaster, firstPet = sSbPetBcast.GetValue(ii) as ReactionBroadcaster;
            fire();  // round end: EA replaces the pair
            int during = SbCount(lot, board) - before;
            ii.Cleanup();
            int left = SbCount(lot, board) - before;
            List<ReactionBroadcaster> now = SbOnLot(lot);
            if (first != null && now.Contains(first)) sSbCheckLeft.Add(first);
            if (firstPet != null && now.Contains(firstPet)) sSbCheckLeft.Add(firstPet);
            string facts = "board on " + lot.Name + ", NPC " + npc.FullName + ", switch " + (ShortcutOn("shuffleboardbcast") ? "on" : "off")
                + ", broadcasters of the board: +" + during + " after two TriggerReactions, +" + left + " after Cleanup, disposed 2410 +" + (PatchCounters()[2410] - disposed);
            if (during < 2) return facts + "\nverdict: N/A (TriggerReactions made no broadcasters)";
            return facts + "\nverdict: " + (left > 0 ? "BROKEN (the replaced pair outlives the interaction)" : "FIXED (no broadcaster left after Cleanup)");
        }

        public static object CookClickCheck(string what)
        {
            int[] c = PatchCounters();
            if (what == "result") return "cookclicklistener: removed 2412=" + c[2412] + ", ObjectDisposed unhooked 2413=" + c[2413] + ", errors 2414=" + c[2414];
            if (what == "list")
            {
                int[] r = CcCensus();
                return "cooking listeners on clicked objects: owner destroyed " + r[0] + ", no owner " + r[1] + ", live owner " + r[2]
                    + "; noted by the fix " + sCcNoted.Count + "; 2412=" + c[2412] + " 2413=" + c[2413] + " 2414=" + c[2414];
            }
            if (what == "run") return CcRun();
            if (what == "verify") return CcVerify();
            return "cookclick <list|run|verify|result>";
        }

        static string CcRun()
        {
            Fridge fridge = null;
            Lot active = Sim.ActiveActor != null ? Sim.ActiveActor.LotCurrent : null;
            foreach (Fridge f in Sims3.Gameplay.Queries.GetObjects<Fridge>())
            {
                if (f == null || f.HasBeenDestroyed || f.LotCurrent == null) continue;
                if (fridge == null || f.LotCurrent == active) fridge = f;
            }
            if (fridge == null) return "verdict: N/A (no fridge in the world)";
            Recipe recipe = null;
            foreach (Recipe r in Recipe.NameToRecipeHash.Values)
                if (r != null && !r.IsSnack && !string.IsNullOrEmpty(r.SingleServingContainer)) { recipe = r; break; }
            if (recipe == null) return "verdict: N/A (no single-serving recipe)";
            IPartOfCookingProcess owner = GlobalFunctions.CreateObject(recipe.SingleServingContainer, Vector3.OutOfWorld, 0, Vector3.UnitZ) as IPartOfCookingProcess;
            if (owner == null) return "verdict: N/A (serving " + recipe.SingleServingContainer + " not created)";
            CookingProcess p = new CookingProcess(recipe, null, fridge, fridge.LotCurrent, Recipe.MealDestination.SurfaceOrEat, Recipe.MealQuantity.Single,
                Recipe.MealRepetition.MakeOne, null, null, owner, null);
            sCcCase = p;
            sCcCaseAdded = CcHasStateListener(p);
            sCcCaseRemoved = PatchCounters()[2412];
            sCcCaseFacts = "fridge on " + fridge.LotCurrent.Name + ", recipe " + recipe.Key;
            bool noted = CcFind(p) != null;
            // GameObject.Destroy takes the simulator proxy away later (HasBeenDestroyed turns true a tick or more after
            // the call; measured 2026-11-14: a same-call check still saw a live owner), so the verdict is a second call
            owner.Destroy();
            return sCcCaseFacts + ", listener after the constructor " + sCcCaseAdded + ", noted by the fix " + noted
                + ", serving destroyed at once " + owner.HasBeenDestroyed + "\nstarted: call 'verify' a few seconds later";
        }

        static CookingProcess sCcCase;
        static bool sCcCaseAdded;
        static int sCcCaseRemoved;
        static string sCcCaseFacts;

        static string CcVerify()
        {
            CookingProcess p = sCcCase;
            if (p == null) return "verdict: N/A (call 'run' first)";
            GameObject owner = p.Owner as GameObject;
            if (owner != null && !owner.HasBeenDestroyed) return sCcCaseFacts + "\nverdict: N/A (the serving is not destroyed yet; call 'verify' again)";
            sCcCase = null;
            if (ShortcutOn("cookclicklistener")) CcCheck(sCcNoted.Count, true);  // the fix's own check, without its 10-minute wait
            bool left = CcHasStateListener(p);
            if (left && sCcClear != null) CcClear(p);  // EA's own cleanup, so the check leaves nothing behind
            string facts = sCcCaseFacts + ", switch " + (ShortcutOn("cookclicklistener") ? "on" : "off") + ", listener after the constructor " + sCcCaseAdded
                + ", after the serving was destroyed " + left + ", 2412 +" + (PatchCounters()[2412] - sCcCaseRemoved);
            if (!sCcCaseAdded) return facts + "\nverdict: N/A (EA added no listener)";
            return facts + "\nverdict: " + (left ? "BROKEN (a destroyed serving's process still listens on the fridge)" : "FIXED (listener removed)");
        }
    }
}
