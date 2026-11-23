using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects;
using Sims3.Gameplay.Objects.CookingObjects;
using Sims3.Gameplay.Objects.Counters;
using Sims3.Gameplay.Objects.FoodObjects;
using Sims3.Gameplay.Skills;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Bar service for Sims nobody can see (docs/628). Default off. An order at a Late Night bar (BarAdvanced: OrderDrink,
    // OrderFood, OrderConversation) is served one customer at a time, and each service holds both Sims in linked sync waits
    // with no deadline on the server's side; with both Sims unseen the order completes at once through EA's own end steps.
    public static partial class Fixes
    {
        // ---- docs/628 unseenservice ---------------------------------------------------------------------------------------
        // [verified, ilspycmd BarAdvanced::EnqueueInteractionAndWait] customers queue in mInteractions; only the head gets a
        // bartender instance (CreateBartenderInteractionInstance, linked, added to the bartender's queue). [verified,
        // BarInteraction::Run] the customer then waits Routed and Completed (GetGiveupTime) while the bartender runs; [verified,
        // BartenderInteraction::Run] the bartender waits Committed with float.MaxValue while the customer runs RunOrderBehavior
        // (OrderConversation: DoTimedLoop 5-10 sim-min), then routes to the bar, RunMakeBehavior (MakeDrink: DoTimedLoop
        // GetTimeToMakeDrink, ~15 sim-min), routes back and puts the drink or plate into the customer's BarDestination slot;
        // the customer's RunBehavior then pays, credits the bartender (Bartending.OnServedSim + tip) and picks up the drink or
        // pushes Eat. Census (phase-20261117-1237/1155): MakeConversation server waits 567 sim-min x34, OrderDrink 346 x30,
        // OrderFood 99 x16, and the queue behind each served customer waits the whole service too.
        // Fix: at the queue head, for an in-scope pair, run EA's end steps with no animation and no sync wait:
        //   conversation: customer Begin/EndCommodityUpdates(true) (RunOrderBehavior minus its DoTimedLoop), bartender
        //     MakeConversation Begin/EndCommodityUpdates(true) (RunMakeBehavior is `return true`), customer RunBehavior (buff
        //     + LTR).
        //   drink (one glass): customer RunOrderBehavior (EA: hands the description to MakeDrink), MakeDrink's non-animation
        //     steps (Bartending AddElement, the autonomous unique-drink pick, UseIngredients + CreateDrink as
        //     CreateDrinksAndSetActors, OnPoured as EA's no-animation CreateRoundsOfDrinks does), PutDownCarriedObject's steps
        //     (ShouldMakeFlamingDrink -> SetOnFire, ParentToSlot to the destination), then the customer's RunBehavior.
        //   food (single serving): customer RunOrderBehavior, MakeFood.CreateFood (+ StartAutoDestroyAlarm), ParentToSlot to
        //     the destination, customer RunBehavior (pay, kOrderedFood, Eat continuation).
        // Commodity updates over the skipped loops: both Sims must be low LOD (EA's IsSufficientlyOnScreenForHighLODSimulation
        // false). [verified, SimUpdate::Simulate] a low-LOD Sim skips Autonomy.Update, the only caller of UpdateMotives and
        // UpdateSkills, so the time-rated part of these interactions' commodity changes is zero in EA's own path too; the
        // begin/end parts run through EA's Begin/EndCommodityUpdates on the same instances.
        // Scope: tier 1 `unseenservice`: customer and server both UfWhy 0 (unseen) and low LOD. Tier 2 `unseenservice_offscreen`
        // (own switch, needs tier 1's on too): not a player or selected Sim, customer, server and the bar slot not on screen
        // (World.IsObjectOnScreen, re-checked at completion), both low LOD. Both tiers: community lot not owned by the active
        // household, server idle at the bar (IdleTend), empty right hands, empty destination slot; else EA's hand-off.
        // Skipped: rounds and trays (OrderRound, group drinks: WaitWithOrderedDrink waits on the tray), group food (grab-a-
        // serving per participant), OrderDrinkOnResidentalLot / OrderFreeDrink / pizza / cake / nectar (other payment and
        // role paths), PracticeAsCustomer (practice skill loop is the outcome).
        // Counters: [3600-3602] tier 1 conversation / drink / food, [3603-3605] tier 2 the same, [3606] out of scope (seen,
        // player, owned, high LOD), [3607] precondition kept EA's hand-off, [3608] tier 2 refused at the on-screen re-check,
        // [3609] RunBehavior false after an instant service (payment refused), [3610] errors, [3611] Check live mismatches.
        // docs/651 §2: [4112] made glasses / plates destroyed in the finally after a failed service, [4113] commodity updates
        // ended there after a throw, [4114] failed services logged by step (SvcErrLog).
        const int kSvcNone = 0, kSvcConv = 1, kSvcDrink = 2, kSvcFood = 3;
        static readonly FieldInfo sSvcBartender = typeof(BarAdvanced).GetField("mBartender", kInstAll);
        static readonly FieldInfo sSvcQueue = typeof(BarAdvanced).GetField("mInteractions", kInstAll);
        static readonly FieldInfo sSvcDest = typeof(BarAdvanced.BarInteraction).GetField("mDestination", kInstAll);
        static readonly MethodInfo sSvcRunBehavior = typeof(BarAdvanced.BarInteraction).GetMethod("RunBehavior", kInstAll, null, Type.EmptyTypes, null);
        static readonly MethodInfo sSvcRunOrder = typeof(BarAdvanced.BarInteraction).GetMethod("RunOrderBehavior", kInstAll, null, Type.EmptyTypes, null);
        delegate bool SvcRunOrderFn();  // RunOrderBehavior through a delegate: MethodInfo.Invoke throws on a swapped method
        // BarInteraction.RunOrderBehavior is abstract. A delegate made from the abstract MethodInfo is not dispatched to the
        // override by the game's interpreter: it transforms the abstract method itself, which has no IL header, and the game
        // dies at 0xE6463F (2026-11-18, 3 of 3 BP runs ~2 min after load). So: the override declared on the instance's own
        // class chain, null when there is none.
        static MethodInfo SvcRunOrderOf(Type t)
        {
            for (; t != null; t = t.BaseType)
            {
                MethodInfo m = t.GetMethod("RunOrderBehavior", kInstAll | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                if (m != null) return m.IsAbstract ? null : m;
            }
            return null;
        }
        static readonly MethodInfo sSvcUseIngr = typeof(BarAdvanced).GetMethod("UseIngredients", kInstAll);
        static readonly MethodInfo sSvcCreateDrink = typeof(BarAdvanced).GetMethod("CreateDrink", kInstAll);
        static readonly Type sSvcOrderFood = typeof(BarAdvanced).GetNestedType("OrderFood", BindingFlags.Public | BindingFlags.NonPublic);
        static readonly Type sSvcOrderConv = typeof(BarAdvanced).GetNestedType("OrderConversation", BindingFlags.Public | BindingFlags.NonPublic);
        static readonly Type sSvcMakeDrink = typeof(BarAdvanced).GetNestedType("MakeDrink", BindingFlags.Public | BindingFlags.NonPublic);
        static readonly Type sSvcMakeFood = typeof(BarAdvanced).GetNestedType("MakeFood", BindingFlags.Public | BindingFlags.NonPublic);
        static readonly Type sSvcMakeConv = typeof(BarAdvanced).GetNestedType("MakeConversation", BindingFlags.Public | BindingFlags.NonPublic);
        static readonly Type sSvcIdleTend = typeof(BarAdvanced).GetNestedType("IdleTend", BindingFlags.Public | BindingFlags.NonPublic);
        static readonly FieldInfo sSvcDrinkCount = typeof(BarAdvanced.OrderDrink).GetField("mNumberOfDrinks", kInstAll);
        static readonly FieldInfo sSvcFoodRecipe = sSvcOrderFood == null ? null : sSvcOrderFood.GetField("mRecipe", kInstAll);
        static readonly FieldInfo sSvcMakeDesc = sSvcMakeDrink == null ? null : sSvcMakeDrink.GetField("mDescription", kInstAll);
        static readonly MethodInfo sSvcCreateFood = sSvcMakeFood == null ? null : sSvcMakeFood.GetMethod("CreateFood", kInstAll, null, Type.EmptyTypes, null);
        static string sSvcMissing = "";
        // customer instance -> its RunBehavior result, for the Run hook (set and taken on the customer's own thread)
        static readonly Dictionary<object, bool> sSvcDone = new Dictionary<object, bool>();

        static void ApplyUnseenService()
        {
            List<string> miss = new List<string>();
            if (sSvcBartender == null) miss.Add("BarAdvanced.mBartender");
            if (sSvcQueue == null) miss.Add("BarAdvanced.mInteractions");
            if (sSvcDest == null) miss.Add("BarInteraction.mDestination");
            if (sSvcRunBehavior == null || sSvcRunOrder == null) miss.Add("BarInteraction.RunBehavior/RunOrderBehavior");
            if (sSvcUseIngr == null || sSvcCreateDrink == null) miss.Add("BarAdvanced.UseIngredients/CreateDrink");
            if (sSvcOrderFood == null || sSvcOrderConv == null || sSvcMakeDrink == null || sSvcMakeFood == null || sSvcMakeConv == null || sSvcIdleTend == null) miss.Add("BarAdvanced nested interactions");
            if (sSvcDrinkCount == null || sSvcFoodRecipe == null || sSvcMakeDesc == null || sSvcCreateFood == null) miss.Add("order/make members");
            sSvcMissing = string.Join(", ", miss.ToArray());
            if (sSvcMissing.Length > 0) { AppliedLog().Add("unseenservice: not found: " + sSvcMissing); return; }
            Patch(typeof(BarAdvanced), "EnqueueInteractionAndWait", new Type[] { typeof(BarAdvanced.BarInteraction), typeof(float) }, typeof(SvcEnqHook), "EnqHook", "EnqOrig");
            Patch(typeof(BarAdvanced.BarInteraction), "Run", Type.EmptyTypes, typeof(SvcRunHook), "RunHook", "RunOrig");
        }

        // Which covered order is this? Exact types only: subclasses carry other payment or role paths.
        static int SvcKind(BarAdvanced.BarInteraction ia)
        {
            if (ia == null) return kSvcNone;
            Type t = ia.GetType();
            if (t == sSvcOrderConv) return kSvcConv;
            if (t == typeof(BarAdvanced.OrderDrink)) return kSvcDrink;
            if (t == sSvcOrderFood) return kSvcFood;
            return kSvcNone;
        }

        // Pure: 0 = EA's hand-off, 1 = unseen tier, 2 = off-screen tier.
        internal static int SvcTier(bool on, bool offOn, bool player, bool community, bool owned, bool lowLodBoth, bool unseenBoth, bool offscreenAll)
        {
            if (!on || player || !community || owned || !lowLodBoth) return 0;
            if (unseenBoth) return 1;
            return offOn && offscreenAll ? 2 : 0;
        }

        // Pure: EA's preconditions for the steps we run (else EA's hand-off).
        internal static bool SvcReady(int kind, bool serverIdle, bool handsFree, bool slotEmpty, bool single, bool canMake)
        {
            if (kind == kSvcNone || !serverIdle || !handsFree) return false;
            if (kind == kSvcConv) return true;
            return slotEmpty && single && canMake;
        }

        static bool SvcOwned(Lot lot)
        {
            Household h = Household.ActiveHousehold;
            return h != null && h.RealEstateManager != null && h.RealEstateManager.FindProperty(lot) != null;
        }

        static bool SvcPlayer(Sim s)
        {
            return s == null || IsPlayerSim(s) || s.IsSelectable || s == PlumbBob.SelectedActor;
        }

        static int SvcLiveTier(Sim c, Sim b, BarAdvanced bar, BarAdvanced.BarDestination dest)
        {
            Lot lot = bar.LotCurrent;
            bool player = SvcPlayer(c) || SvcPlayer(b);
            bool community = lot != null && lot.IsCommunityLot;
            bool owned = community && SvcOwned(lot);
            if (player || !community || owned)  // cheap tests first; docs/635 per-reason counters
            {
                if (!sSvcQuiet) PatchCounters()[player ? 3780 : !community ? 3781 : 3782]++;
                return 0;
            }
            bool lowLod = RhxUnseen(c) && RhxUnseen(b);
            if (!lowLod) { if (!sSvcQuiet) PatchCounters()[3783]++; return 0; }
            bool unseen = SafeUfWhy(c) == 0 && SafeUfWhy(b) == 0;
            bool offOn = !unseen && ShortcutOn("unseenservice_offscreen");
            bool offscreen = offOn && !UcOnScreen(c.ObjectId) && !UcOnScreen(b.ObjectId)
                && (dest.Object == null || !UcOnScreen(dest.Object.ObjectId)) && !UcOnScreen(bar.ObjectId);
            int tier = SvcTier(true, offOn, player, community, owned, lowLod, unseen, offscreen);
            if (tier == 0 && !sSvcQuiet) PatchCounters()[3784]++;
            return tier;
        }

        // ---- docs/635 unseenservicewait (default off) ----------------------------------------------------------------------------
        // EA's BartenderInteraction.Cleanup dequeues its order first (IL_0007 DequeueInteraction) and pushes IdleTend only when
        // mInteractions is then empty (IL_001f get_Count, IL_0051 AddNext): with a line at the bar the next order reaches the head
        // while the server still runs (or has just left) the last Make*, never IdleTend. SvcServe's server-idle test (IdleTend
        // only) then sends every queued order to EA's linked hand-off: s84 (phase-20261117-1604) 70 OrderDrink waits, 787
        // sim-min, all at busy Bridgeport bars. With the switch on an in-scope order at the head waits (EA's own loop, Sleep(0))
        // while the server is between orders (no interaction, or a BartenderInteraction whose order left the queue), and a
        // server tending with no interaction counts as idle; after a served order EA's Cleanup step re-arms IdleTend when the
        // queue is empty. Bounded by EA's giveupTime and kSvcWaitMaxMin; anything else keeps EA's hand-off.
        const float kSvcWaitMaxMin = 10f;
        static bool sSvcQuiet;   // set while an order waits: its repeat passes skip the per-reason counters
        // Pure: the server-idle test. EA's state after its Cleanup with a line (no interaction, empty queue) is idle too with the switch.
        internal static bool SvcServerIdle(bool waitOn, bool curNull, bool curIdleTend, int queueCount)
        {
            if (curIdleTend && queueCount <= 1) return true;
            return waitOn && curNull && queueCount == 0;
        }
        // Pure: 0 = EA's hand-off, 1 = keep waiting in EA's loop. tier 0 (seen, player, ...) never waits.
        internal static int SvcWaitDecision(bool waitOn, int tier, bool betweenOrders, bool capPassed)
        {
            return waitOn && tier > 0 && betweenOrders && !capPassed ? 1 : 0;
        }
        static bool SvcBetweenOrders(Sim b, List<BarAdvanced.BarInteraction> q)
        {
            if (b.InteractionQueue.Count > 1) return false;
            InteractionInstance cur = b.CurrentInteraction;
            if (cur == null) return b.InteractionQueue.Count == 0;
            if (cur.GetType().BaseType == null || cur.GetType().BaseType.Name != "BartenderInteraction") return false;
            BarAdvanced.BarInteraction linked = cur.LinkedInteractionInstance as BarAdvanced.BarInteraction;
            return linked == null || !q.Contains(linked);   // its order already left the queue: Cleanup runs or ran
        }
        static bool SvcWaitHere(Sim b, List<BarAdvanced.BarInteraction> q, int tier, ref bool waited, float t0)
        {
            bool cap = SimClock.ElapsedTime(TimeUnit.Minutes) - t0 > kSvcWaitMaxMin;
            if (SvcWaitDecision(ShortcutOn("unseenservicewait"), tier, SvcBetweenOrders(b, q), cap) == 0) return false;
            if (!waited) { waited = true; PatchCounters()[3791]++; }
            return true;
        }
        // EA's BartenderInteraction.Cleanup tail for an order served without a server instance: queue empty -> IdleTend next
        static void SvcRearmIdle(BarAdvanced bar, Sim b, List<BarAdvanced.BarInteraction> q)
        {
            if (!ShortcutOn("unseenservicewait") || q.Count != 0 || b.CurrentInteraction != null || b.InteractionQueue.Count != 0) return;
            if (b.Posture == null || b.Posture.Container != bar || b.Posture.HasBeenCanceled) return;
            b.InteractionQueue.AddNext(BarAdvanced.GetIdleTendInteraction(bar, b, new InteractionPriority(InteractionPriorityLevel.Autonomous), true, true));
            PatchCounters()[3794]++;
        }

        static bool SvcRunBehavior(BarAdvanced.BarInteraction ia)
        {
            return (bool)sSvcRunBehavior.Invoke(ia, null);   // virtual: the order's own RunBehavior
        }

        // EA's end steps for one order, no animation, no sync wait. True when served (sSvcDone holds RunBehavior's answer);
        // false before any change = EA's hand-off.
        static bool SvcServe(BarAdvanced bar, BarAdvanced.BarInteraction ia, Sim b, int kind, int tier)
        {
            Sim c = ia.Actor;
            BarAdvanced.BarDestination dest = (BarAdvanced.BarDestination)sSvcDest.GetValue(ia);
            InteractionInstance cur = b.CurrentInteraction;
            bool serverIdle = SvcServerIdle(ShortcutOn("unseenservicewait"), cur == null, cur != null && cur.GetType() == sSvcIdleTend, b.InteractionQueue.Count);
            bool handsFree = b.GetObjectInRightHand() == null && c.GetObjectInRightHand() == null;
            bool slotEmpty = dest.Object != null && dest.Object.GetContainedObject(dest.PlacementSlot) == null;
            bool single = true, canMake = true;
            BarAdvanced.OrderDrink od = ia as BarAdvanced.OrderDrink;
            if (kind == kSvcDrink)
            {
                single = (int)sSvcDrinkCount.GetValue(od) == 1 && od.Description != null;
                canMake = single && Bartending.CanMakeDrink(b, od.Description);   // MakeDrink.RunResponseBehavior's test
            }
            else if (kind == kSvcFood)
            {
                object gs = ia.InteractionDefinition.GetType().GetProperty("IsGroupServing", kInstAll) == null ? null
                    : ia.InteractionDefinition.GetType().GetProperty("IsGroupServing", kInstAll).GetValue(ia.InteractionDefinition, null);
                single = gs is bool && !(bool)gs && sSvcFoodRecipe.GetValue(ia) != null;
            }
            if (!SvcReady(kind, serverIdle, handsFree, slotEmpty, single, canMake))
            {
                if (sSvcQuiet) return false;
                int[] pc = PatchCounters();
                pc[3607]++;
                // docs/635: which precondition kept EA's hand-off
                pc[!serverIdle ? (cur != null && cur.GetType() != sSvcIdleTend ? 3785 : 3786) : !handsFree ? 3787 : !slotEmpty ? 3788 : !single ? 3789 : 3790]++;
                return false;
            }
            if (tier == 2 && (UcOnScreen(c.ObjectId) || UcOnScreen(b.ObjectId) || (dest.Object != null && UcOnScreen(dest.Object.ObjectId))))
            {
                PatchCounters()[3608]++;   // re-check at completion
                return false;
            }
            InteractionInstance mk = ia.CreateBartenderInteractionInstance(b);   // EA's Enqueue: the server's instance, linked
            mk.LinkedInteractionInstance = ia;
            bool res = false;
            // docs/651 §2: every step is named for the error log; a throw anywhere ends the commodity updates begun and
            // destroys a made object that never reached the destination slot.
            int step = OwnBugsCore.kSvcStepStart;
            bool iaBegun = false, mkBegun = false, placed = false;
            GameObject made = null;
            try
            {
                if (kind == kSvcConv)
                {
                    step = OwnBugsCore.kSvcStepCommodity;
                    ia.BeginCommodityUpdates(); iaBegun = true;   // OrderConversation.RunOrderBehavior without its DoTimedLoop
                    ia.EndCommodityUpdates(true); iaBegun = false;
                    mk.BeginCommodityUpdates(); mkBegun = true;   // BartenderInteraction.Run around MakeConversation.RunMakeBehavior (true)
                    mk.EndCommodityUpdates(true); mkBegun = false;
                }
                else
                {
                    step = OwnBugsCore.kSvcStepOrder;
                    MethodInfo runOrder = SvcRunOrderOf(ia.GetType());
                    if (runOrder == null) throw new InvalidOperationException("no RunOrderBehavior body on " + ia.GetType().Name);
                    ((SvcRunOrderFn)Fixes.Bind(typeof(SvcRunOrderFn), ia, runOrder))();   // EA: hands description / recipe to the server's instance (a delegate: barorderlog swaps RunOrderBehavior, see PrefRemoveFn)
                    step = OwnBugsCore.kSvcStepCommodity;
                    mk.BeginCommodityUpdates(); mkBegun = true;
                    TransformParentingReturnCode rc = TransformParentingReturnCode.Unknown;
                    if (kind == kSvcDrink)
                    {
                        Bartending sk = (Bartending)b.SkillManager.AddElement(SkillNames.Bartending);
                        Bartending.DrinkDescription d = (Bartending.DrinkDescription)sSvcMakeDesc.GetValue(mk);
                        if (ia.Autonomous && sk != null && sk.UniqueDrinks != null)   // MakeDrink.RunMakeBehavior's unique-drink pick
                        {
                            Sims3.Gameplay.Objects.Quality q = Bartending.GetRequiredQualityForVenue(bar.LotCurrent.GetMetaAutonomyType);
                            foreach (Bartending.CustomDrinkData u in sk.UniqueDrinks)
                            {
                                if (u.IsAvailable() && QualityHelper.ComparesTo(u.Quality, q, QualityComparison.EqualOrBetter))
                                {
                                    u.Use();
                                    d = new Bartending.DrinkDescription(u);
                                    break;
                                }
                            }
                        }
                        // CreateDrinksAndSetActors, one glass, without the state machine actor
                        step = OwnBugsCore.kSvcStepCreate;
                        object ingr = (d.Data == null && d.CustomDrinkData == null && d.Mood == Bartending.DrinkMood.None)
                            ? Activator.CreateInstance(sSvcCreateDrink.GetParameters()[4].ParameterType) : sSvcUseIngr.Invoke(bar, new object[] { b });
                        Glass g = (Glass)sSvcCreateDrink.Invoke(bar, new object[] { b, d.Data, d.CustomDrinkData, d.Mood, ingr });
                        made = g;
                        g.IsPoolbarDrink = bar is PoolBar;
                        // EA's order (CreateRoundsOfDrinks, PutDownCarriedObjectHelper): the glass is placed in the world first and
                        // poured there. The old code poured it while still out of world (CreateObjectOutOfWorld), which EA never does.
                        step = OwnBugsCore.kSvcStepPlace;
                        placed = g.ParentToSlot(dest.Object, dest.PlacementSlot, ref rc);
                        if (placed)
                        {
                            step = OwnBugsCore.kSvcStepPour;
                            g.OnPoured();   // the anim event 600 handler; EA's CreateRoundsOfDrinks calls it directly
                            if (sk != null && sk.ShouldMakeFlamingDrink(g.ContainedDrink)) g.SetOnFire();   // PutDownCarriedObject
                        }
                        step = OwnBugsCore.kSvcStepCommodity;
                        mk.EndCommodityUpdates(true); mkBegun = false;
                    }
                    else
                    {
                        step = OwnBugsCore.kSvcStepCreate;
                        IFoodContainer f = (IFoodContainer)sSvcCreateFood.Invoke(mk, null);
                        made = f as GameObject;
                        if (f is ServingContainer) ((ServingContainer)f).StartAutoDestroyAlarm();
                        step = OwnBugsCore.kSvcStepCommodity;
                        mk.EndCommodityUpdates(f != null); mkBegun = false;
                        step = OwnBugsCore.kSvcStepPlace;
                        placed = made != null && made.ParentToSlot(dest.Object, dest.PlacementSlot, ref rc);
                    }
                    if (!placed)   // EA: the order fails
                    {
                        SvcErrLog(kind, made == null ? OwnBugsCore.kSvcStepCreate : OwnBugsCore.kSvcStepPlace, made == null ? "nothing made" : "ParentToSlot " + rc, null);
                        sSvcDone[ia] = false;
                        return true;
                    }
                }
                step = OwnBugsCore.kSvcStepBehavior;
                res = SvcRunBehavior(ia);
                step = OwnBugsCore.kSvcStepDone;
            }
            catch (ResetException) { throw; }
            catch (Exception e)
            {
                // EA's hand-off follows (the caller counts [3610]); nothing of ours is left half done
                SvcErrLog(kind, step, null, e);
                sSvcLogged = e;
                throw;
            }
            finally
            {
                if (OwnBugsCore.SvcEndUpdates(iaBegun)) { try { ia.EndCommodityUpdates(false); PatchCounters()[4113]++; } catch (Exception) { } }
                if (OwnBugsCore.SvcEndUpdates(mkBegun)) { try { mk.EndCommodityUpdates(false); PatchCounters()[4113]++; } catch (Exception) { } }
                if (OwnBugsCore.SvcDestroyMade(made != null, placed, step))
                {
                    try { if (!made.HasBeenDestroyed) made.Destroy(); PatchCounters()[4112]++; } catch (Exception) { }
                }
                ia.LinkedInteractionInstance = null;   // the server's instance never ran
            }
            sSvcDone[ia] = res;
            if (!res) PatchCounters()[3609]++;
            PatchCounters()[(tier == 2 ? 3603 : 3600) + kind - 1]++;
            return true;
        }

        // docs/651 §2: the step and type of the first errors ([3610] alone never said which), capped.
        static int sSvcErrLines;
        static Exception sSvcLogged;   // already logged by SvcServe, rethrown to the caller
        static void SvcErrLog(int kind, int step, string what, Exception e)
        {
            if (e == null) PatchCounters()[3610]++;   // the caller's catch counts a throw
            PatchCounters()[4114]++;
            if (!OwnBugsCore.LogMore(sSvcErrLines, 16)) return;
            sSvcErrLines++;
            Exception inner = e != null && e.InnerException != null ? e.InnerException : e;   // reflection wraps EA's throw
            AppliedLog().Add("unseenservice: " + (kind == kSvcConv ? "conversation" : kind == kSvcDrink ? "drink" : "food") + " failed at "
                + OwnBugsCore.SvcStepName(step) + ": " + (inner != null ? OwnBugsCore.Brief(inner.GetType().Name, inner.StackTrace) : what));
        }

        public class SvcEnqHook
        {
            // EA's loop (EnqueueInteractionAndWait) with one branch at the head: an in-scope pair is served at once.
            public bool EnqHook(BarAdvanced.BarInteraction interaction, float giveupTime)
            {
                int kind = SvcKind(interaction);
                if (sSvcMissing.Length > 0 || kind == kSvcNone || !ShortcutOn("unseenservice")) return EnqOrig(interaction, giveupTime);  // off: EA only
                BarAdvanced bar = (object)this as BarAdvanced;
                List<BarAdvanced.BarInteraction> q = (List<BarAdvanced.BarInteraction>)sSvcQueue.GetValue(bar);
                float t0 = SimClock.ElapsedTime(TimeUnit.Minutes);
                bool waited = false;
                q.Add(interaction);
                while (!interaction.InstanceActor.HasExitReason())
                {
                    if (q[0] == interaction)
                    {
                        Sim b = sSvcBartender.GetValue(bar) as Sim;
                        if (b != null && !b.Posture.HasBeenCanceled)
                        {
                            bool hold = false;
                            try
                            {
                                BarAdvanced.BarDestination dest = (BarAdvanced.BarDestination)sSvcDest.GetValue(interaction);
                                sSvcQuiet = waited;   // a waiting order's repeat passes are not counted again
                                int tier = SvcLiveTier(interaction.Actor, b, bar, dest);
                                if (tier == 0) { if (!waited) PatchCounters()[3606]++; }
                                else if (SvcServe(bar, interaction, b, kind, tier))
                                {
                                    q.Remove(interaction);
                                    if (waited) PatchCounters()[3792]++;
                                    SvcRearmIdle(bar, b, q);
                                    return false;
                                }
                                else hold = SvcWaitHere(b, q, tier, ref waited, t0);   // docs/635: server between orders
                            }
                            catch (ResetException) { throw; }
                            catch (Exception e) { PatchCounters()[3610]++; if (e != sSvcLogged) SvcErrLog(kind, OwnBugsCore.kSvcStepScope, null, e); }
                            finally { sSvcQuiet = false; }
                            if (!hold)
                            {
                                if (waited) PatchCounters()[3793]++;
                                InteractionInstance ii = interaction.CreateBartenderInteractionInstance(b);
                                ii.LinkedInteractionInstance = interaction;
                                return b.InteractionQueue.Add(ii);
                            }
                        }
                    }
                    if (SimClock.ElapsedTime(TimeUnit.Minutes) - t0 > giveupTime)
                    {
                        interaction.InstanceActor.AddExitReason(ExitReason.FailedToStart);
                        break;
                    }
                    Simulator.Sleep(0u);
                }
                q.Remove(interaction);
                return false;
            }
            public bool EnqOrig(BarAdvanced.BarInteraction interaction, float giveupTime) { return false; } // receives BarAdvanced.EnqueueInteractionAndWait
        }

        public class SvcRunHook
        {
            // EA's Run returns false right after a served Enqueue; the served order's answer is its RunBehavior's, as in EA.
            public bool RunHook()
            {
                object self = this;
                bool r = RunOrig();
                bool served;
                if (sSvcDone.Count > 0 && sSvcDone.TryGetValue(self, out served)) { sSvcDone.Remove(self); return served; }
                return r;
            }
            public bool RunOrig() { return false; } // receives BarAdvanced+BarInteraction.Run
        }

        // UnseenServiceOffscreenCheck run: the tier-2 sub-key's own decision (docs/628 tier 2). The case is an off-screen
        // order near viewers (low LOD, not unseen): EA's wait (tier 0) with unseenservice_offscreen off, BROKEN; tier 2 with
        // it on, FIXED. The main switch is taken as on so the verdict follows the sub-key only (s89 read the main tier).
        // Controls: unseen stays tier 1, on screen / player / main switch off keep EA. Read-only.
        public static object UnseenServiceOffscreenCheck(string what)
        {
            string counters = B582Counters("unseenservice_offscreen", 3603, new[] { "conv offscreen", "drinks offscreen", "food offscreen" }, sSvcMissing);
            if (what == "result") return counters;
            if (what != "run") return "unseenservice_offscreen <run|result>";
            bool offOn = ShortcutOn("unseenservice_offscreen"), mainOn = ShortcutOn("unseenservice");
            bool eaBug = SvcTier(true, false, false, true, false, true, false, true) == 0;   // sub-key off: EA's wait
            bool fixedPure = SvcTier(true, offOn, false, true, false, true, false, true) == 2;
            bool controls = eaBug
                && SvcTier(true, true, false, true, false, true, true, true) == 1     // unseen stays tier 1
                && SvcTier(true, true, false, true, false, true, false, false) == 0   // on screen
                && SvcTier(true, true, true, true, false, true, false, true) == 0     // player / selected Sim
                && SvcTier(false, true, false, true, false, true, false, true) == 0;  // main switch off
            return B582Line("unseenservice_offscreen", offOn, "an off-screen, near-viewer bar order is served by the tier-2 path", fixedPure && sSvcMissing.Length == 0,
                "EA waits with the sub-key off (" + eaBug + "), unseen stays tier 1, on screen / player / main switch off keep EA; live needs unseenservice (" + mainOn + ")",
                controls, sSvcMissing, counters);
        }

        // UnseenServiceWaitCheck run (docs/635): the s84 case is an in-scope order at the queue head while the server is between
        // orders (EA's Cleanup left no IdleTend because the line was not empty). Off: EA's hand-off (decision 0), BROKEN; on: the
        // order waits in EA's loop and is then served (decision 1, and the server's empty state counts as idle), FIXED.
        // Controls: tier 0 (seen / player / owned / high LOD) never waits; a server busy with anything else, or past the cap,
        // keeps EA's hand-off; IdleTend with an empty queue is idle in both arms; with the switch off an empty server is not.
        // Live: a census of bar servers now (between orders / idle / other). Read-only. Counters: the per-reason split of
        // unseenservice's out-of-scope [3606] and precondition [3607] counters too.
        public static object UnseenServiceWaitCheck(string what)
        {
            string counters = B582Counters("unseenservicewait", 3780, new[] { "out of scope: player", "not community", "owned venue", "high LOD", "seen (no tier 2)",
                "kept EA: server busy", "server queue", "hands full", "slot taken", "not single", "cannot make", "waits for a server between orders",
                "served after a wait", "waits handed off", "IdleTend re-armed", "check mismatches" }, sSvcMissing);
            if (what == "result") return counters;
            if (what != "run") return "unseenservicewait <run|result>";
            bool on = ShortcutOn("unseenservicewait");
            bool eaBug = SvcWaitDecision(false, 1, true, false) == 0 && !SvcServerIdle(false, true, false, 0);   // EA: hand-off, empty server not idle
            bool fixedPure = SvcWaitDecision(on, 1, true, false) == 1 && SvcServerIdle(on, true, false, 0);
            bool controls = eaBug
                && SvcWaitDecision(true, 0, true, false) == 0      // seen / player / owned / high LOD
                && SvcWaitDecision(true, 1, false, false) == 0     // server busy with something else
                && SvcWaitDecision(true, 2, true, true) == 0       // past the cap
                && SvcWaitDecision(true, 2, true, false) == 1      // off-screen tier waits too
                && SvcServerIdle(false, false, true, 1) && SvcServerIdle(true, false, true, 1)   // IdleTend: idle in both arms
                && !SvcServerIdle(true, true, false, 1) && !SvcServerIdle(true, false, false, 0);  // queued work / another interaction
            string live = "no bar with a server";
            bool liveOk = true;
            try
            {
                int between = 0, idle = 0, other = 0;
                if (sSvcMissing.Length == 0)
                    foreach (BarAdvanced bar in Sims3.Gameplay.Queries.GetObjects<BarAdvanced>())
                    {
                        Sim b = bar == null ? null : sSvcBartender.GetValue(bar) as Sim;
                        if (b == null) continue;
                        List<BarAdvanced.BarInteraction> q = (List<BarAdvanced.BarInteraction>)sSvcQueue.GetValue(bar);
                        InteractionInstance cur = b.CurrentInteraction;
                        if (cur != null && cur.GetType() == sSvcIdleTend) idle++;
                        else if (q != null && SvcBetweenOrders(b, q)) between++;
                        else other++;
                    }
                live = "servers idle " + idle + ", between orders " + between + ", other " + other;
            }
            catch (Exception e) { liveOk = false; PatchCounters()[3795]++; live = "live probe threw " + (e.InnerException ?? e).GetType().Name; }
            return B582Line("unseenservicewait", on, "an in-scope order waits for a server between orders and is served (no linked hand-off)", fixedPure && sSvcMissing.Length == 0,
                "EA hands off (" + eaBug + "), seen / busy server / cap keep EA, IdleTend idle in both arms; needs unseenservice (" + ShortcutOn("unseenservice") + "); " + live,
                controls && liveOk, sSvcMissing, counters);
        }

        // UnseenServiceCheck run: pure scope decision with controls (off: EA's long-wait hand-off, BROKEN; on: served at once,
        // FIXED), then live: every order type maps to EA's server type through CreateBartenderInteractionInstance on a world
        // bar with a bartender (nothing runs), and a census of bar orders and bartenders waiting now.
        public static object UnseenServiceCheck(string what)
        {
            string counters = B582Counters("unseenservice", 3600, new[] { "conv unseen", "drinks unseen", "food unseen", "conv offscreen", "drinks offscreen", "food offscreen",
                "out of scope", "precondition kept EA", "offscreen re-check refused", "RunBehavior false", "errors", "check mismatches" }, sSvcMissing);
            if (what == "result") return counters;
            if (what != "run") return "unseenservice <run|result>";
            bool on = ShortcutOn("unseenservice"), offOn = ShortcutOn("unseenservice_offscreen");
            bool eaBug = SvcTier(false, false, false, true, false, true, true, true) == 0;   // EA: always the linked wait
            bool fixedPure = on && SvcTier(on, offOn, false, true, false, true, true, false) == 1;
            bool controls = eaBug
                && SvcTier(true, true, true, true, false, true, true, true) == 0       // player / selected Sim
                && SvcTier(true, true, false, false, false, true, true, true) == 0     // residential lot
                && SvcTier(true, true, false, true, true, true, true, true) == 0       // active household's venue
                && SvcTier(true, true, false, true, false, false, true, true) == 0     // high LOD
                && SvcTier(true, true, false, true, false, true, false, false) == 0    // seen and on screen
                && SvcTier(true, false, false, true, false, true, false, true) == 0    // off-screen tier needs its own switch
                && SvcTier(true, true, false, true, false, true, false, true) == 2
                && SvcReady(kSvcConv, true, true, false, false, false) && !SvcReady(kSvcDrink, true, true, true, false, true)
                && !SvcReady(kSvcDrink, true, true, false, true, true) && !SvcReady(kSvcFood, false, true, true, true, true)
                && !SvcReady(kSvcDrink, true, false, true, true, true);
            string live = "no bar with a bartender in the world (pure only)";
            bool liveOk = true;
            int orders = 0, serving = 0, bars = 0;
            try
            {
                Sim probe = null;
                foreach (Sim s in Sims3.Gameplay.Queries.GetObjects<Sim>())
                {
                    if (s == null) continue;
                    InteractionInstance cur = s.CurrentInteraction;
                    if (cur is BarAdvanced.BarInteraction) orders++;
                    else if (cur != null && cur.GetType().BaseType != null && cur.GetType().BaseType.Name == "BartenderInteraction") serving++;
                    if (probe == null && !SvcPlayer(s)) probe = s;
                }
                if (sSvcMissing.Length == 0)
                    foreach (BarAdvanced bar in Sims3.Gameplay.Queries.GetObjects<BarAdvanced>())
                    {
                        Sim b = bar == null ? null : sSvcBartender.GetValue(bar) as Sim;
                        if (b == null) continue;
                        bars++;
                        if (bars > 1 || probe == null) continue;
                        InteractionPriority p = new InteractionPriority(InteractionPriorityLevel.Autonomous);
                        Type[] want = { sSvcMakeConv, sSvcMakeDrink, sSvcMakeFood };
                        InteractionDefinition[] defs = { (InteractionDefinition)sSvcOrderConv.GetField("Singleton").GetValue(null), BarAdvanced.OrderDrink.Singleton,
                            (InteractionDefinition)sSvcOrderFood.GetField("Singleton").GetValue(null) };
                        System.Text.StringBuilder sb = new System.Text.StringBuilder();
                        for (int i = 0; i < defs.Length; i++)
                        {
                            BarAdvanced.BarInteraction o = defs[i].CreateInstance(bar, probe, p, true, true) as BarAdvanced.BarInteraction;
                            InteractionInstance mk = o == null ? null : o.CreateBartenderInteractionInstance(b);
                            bool ok = o != null && SvcKind(o) == i + 1 && mk != null && mk.GetType() == want[i];
                            if (!ok) { liveOk = false; PatchCounters()[3611]++; }
                            sb.Append((i == 0 ? "" : ", ") + (o == null ? "null" : o.GetType().Name) + "->" + (mk == null ? "null" : mk.GetType().Name) + "=" + ok);
                        }
                        live = "bar " + bar.GetType().Name + " " + bar.ObjectId.Value + " server types: " + sb;
                    }
            }
            catch (Exception e) { liveOk = false; live = "live probe threw " + (e.InnerException ?? e).GetType().Name; }
            return B582Line("unseenservice", on, "an unseen bar order is served at once with EA's end steps", fixedPure && liveOk && sSvcMissing.Length == 0,
                "EA waits (" + eaBug + "), player / residential / owned / high LOD / seen keep EA, off-screen tier only with unseenservice_offscreen ("
                + offOn + "), trays and groups keep EA, " + live + ", bars with a bartender " + bars + ", Sims ordering now " + orders + ", serving now " + serving,
                controls && liveOk, sSvcMissing, counters);
        }
    }
}
