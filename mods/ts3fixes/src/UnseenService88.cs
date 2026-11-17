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
        const int kSvcNone = 0, kSvcConv = 1, kSvcDrink = 2, kSvcFood = 3;
        static readonly FieldInfo sSvcBartender = typeof(BarAdvanced).GetField("mBartender", kInstAll);
        static readonly FieldInfo sSvcQueue = typeof(BarAdvanced).GetField("mInteractions", kInstAll);
        static readonly FieldInfo sSvcDest = typeof(BarAdvanced.BarInteraction).GetField("mDestination", kInstAll);
        static readonly MethodInfo sSvcRunBehavior = typeof(BarAdvanced.BarInteraction).GetMethod("RunBehavior", kInstAll, null, Type.EmptyTypes, null);
        static readonly MethodInfo sSvcRunOrder = typeof(BarAdvanced.BarInteraction).GetMethod("RunOrderBehavior", kInstAll, null, Type.EmptyTypes, null);
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
            if (player || !community || owned) return 0;  // cheap tests first
            bool lowLod = RhxUnseen(c) && RhxUnseen(b);
            if (!lowLod) return 0;
            bool unseen = SafeUfWhy(c) == 0 && SafeUfWhy(b) == 0;
            bool offOn = !unseen && ShortcutOn("unseenservice_offscreen");
            bool offscreen = offOn && !UcOnScreen(c.ObjectId) && !UcOnScreen(b.ObjectId)
                && (dest.Object == null || !UcOnScreen(dest.Object.ObjectId)) && !UcOnScreen(bar.ObjectId);
            return SvcTier(true, offOn, player, community, owned, lowLod, unseen, offscreen);
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
            bool serverIdle = b.CurrentInteraction != null && b.CurrentInteraction.GetType() == sSvcIdleTend && b.InteractionQueue.Count <= 1;
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
            if (!SvcReady(kind, serverIdle, handsFree, slotEmpty, single, canMake)) { PatchCounters()[3607]++; return false; }
            if (tier == 2 && (UcOnScreen(c.ObjectId) || UcOnScreen(b.ObjectId) || (dest.Object != null && UcOnScreen(dest.Object.ObjectId))))
            {
                PatchCounters()[3608]++;   // re-check at completion
                return false;
            }
            InteractionInstance mk = ia.CreateBartenderInteractionInstance(b);   // EA's Enqueue: the server's instance, linked
            mk.LinkedInteractionInstance = ia;
            bool res;
            try
            {
                if (kind == kSvcConv)
                {
                    ia.BeginCommodityUpdates();   // OrderConversation.RunOrderBehavior without its DoTimedLoop
                    ia.EndCommodityUpdates(true);
                    mk.BeginCommodityUpdates();   // BartenderInteraction.Run around MakeConversation.RunMakeBehavior (true)
                    mk.EndCommodityUpdates(true);
                }
                else
                {
                    sSvcRunOrder.Invoke(ia, null);   // EA: hands description / recipe to the server's instance
                    mk.BeginCommodityUpdates();
                    GameObject made;
                    if (kind == kSvcDrink)
                    {
                        Bartending sk = (Bartending)b.SkillManager.AddElement(SkillNames.Bartending);
                        Bartending.DrinkDescription d = (Bartending.DrinkDescription)sSvcMakeDesc.GetValue(mk);
                        if (ia.Autonomous && sk != null)   // MakeDrink.RunMakeBehavior's unique-drink pick
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
                        object ingr = (d.Data == null && d.CustomDrinkData == null && d.Mood == Bartending.DrinkMood.None)
                            ? Activator.CreateInstance(sSvcCreateDrink.GetParameters()[4].ParameterType) : sSvcUseIngr.Invoke(bar, new object[] { b });
                        Glass g = (Glass)sSvcCreateDrink.Invoke(bar, new object[] { b, d.Data, d.CustomDrinkData, d.Mood, ingr });
                        g.IsPoolbarDrink = bar is PoolBar;
                        g.OnPoured();   // the anim event 600 handler; EA's CreateRoundsOfDrinks calls it directly
                        mk.EndCommodityUpdates(true);
                        bool flame = sk != null && sk.ShouldMakeFlamingDrink(g.ContainedDrink);   // PutDownCarriedObject
                        if (!g.ParentToSlot(dest.Object, dest.PlacementSlot)) { g.Destroy(); made = null; }
                        else { if (flame) g.SetOnFire(); made = g; }
                    }
                    else
                    {
                        IFoodContainer f = (IFoodContainer)sSvcCreateFood.Invoke(mk, null);
                        if (f is ServingContainer) ((ServingContainer)f).StartAutoDestroyAlarm();
                        mk.EndCommodityUpdates(f != null);
                        made = f as GameObject;
                        if (made != null && !made.ParentToSlot(dest.Object, dest.PlacementSlot)) { made.Destroy(); made = null; }
                    }
                    if (made == null) { PatchCounters()[3610]++; sSvcDone[ia] = false; return true; }   // EA: the order fails
                }
                res = SvcRunBehavior(ia);
            }
            finally { ia.LinkedInteractionInstance = null; }   // the server's instance never ran
            sSvcDone[ia] = res;
            if (!res) PatchCounters()[3609]++;
            PatchCounters()[(tier == 2 ? 3603 : 3600) + kind - 1]++;
            return true;
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
                q.Add(interaction);
                while (!interaction.InstanceActor.HasExitReason())
                {
                    if (q[0] == interaction)
                    {
                        Sim b = sSvcBartender.GetValue(bar) as Sim;
                        if (b != null && !b.Posture.HasBeenCanceled)
                        {
                            try
                            {
                                BarAdvanced.BarDestination dest = (BarAdvanced.BarDestination)sSvcDest.GetValue(interaction);
                                int tier = SvcLiveTier(interaction.Actor, b, bar, dest);
                                if (tier == 0) PatchCounters()[3606]++;
                                else if (SvcServe(bar, interaction, b, kind, tier)) { q.Remove(interaction); return false; }
                            }
                            catch (ResetException) { throw; }
                            catch (Exception) { PatchCounters()[3610]++; }
                            InteractionInstance ii = interaction.CreateBartenderInteractionInstance(b);
                            ii.LinkedInteractionInstance = interaction;
                            return b.InteractionQueue.Add(ii);
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
