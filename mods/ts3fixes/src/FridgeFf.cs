using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Objects.FoodObjects;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects.Appliances;
using Sims3.Gameplay.Objects.CookingObjects;
using Sims3.Gameplay.Skills;
using Sims3.Gameplay.Socializing;
using Sims3.Gameplay.TuningValues;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // docs/686 §36.11: fridgeff (NPCs) / fridgeffunseen (household and selected Sims, the player's opt-in; never the Sim the
    // player has selected). A Sim whose Fridge_Have or Fridge_EatLeftovers starts while the fridge is busy (another Sim uses it
    // or holds its line) and that nobody sees takes its food at once instead of waiting: EA's own steps of the two Run bodies
    // (ingredients used up, the item created and initialised for its recipe, the cooking process moved to its next step, the
    // item in hand through CarrySystem.EnterWhileHolding, the follow-up EatHeldFood / FoodTray_Prepare / PutDown pushed as
    // EA pushes it, the skill element, the "Has Made Food" topic, EA's commodity updates of the leftovers grab), without the
    // walk to the fridge's slot, the line and the animations. The Sim stays where it stands, so it never steps onto a slot in
    // use. Camera: decided once, at the start, with elevskip's tests (an NPC: TownieUnseen; a household Sim: not selected and
    // off screen and away from viewers; for both the fridge off screen and its slot away from viewers); seen at the start =
    // EA's line. The grab takes no time, so a camera that arrives later finds the end state (item in hand).
    // Counters 4770-4776. Off = compare mode (4772).
    public static partial class Fixes
    {
        const int kFfHave = 4770, kFfLeftovers = 4771, kFfWould = 4772, kFfSeen = 4773, kFfExempt = 4774, kFfErrors = 4775, kFfBusy = 4776;
        static string sFfLast = "";
        static FieldInfo sFfThing;  // Fridge_Have.mThingToPrepareOrEat (private: the pet-watch helper reads it)
        static readonly Type sFfLeftType = typeof(Fridge).Assembly.GetType("Sims3.Gameplay.Objects.Appliances.Fridge_EatLeftovers");  // internal sealed
        static FieldInfo sFfChosen, sFfPushEat;  // its public fields ChosenContainer, PushEat

        static void ApplyFridgeFf()
        {
            Patch(typeof(Fridge_Have), "Run", Type.EmptyTypes, typeof(FfHaveHook), "RunHook", "RunOrig");
            if (sFfLeftType != null)
            {
                sFfChosen = sFfLeftType.GetField("ChosenContainer"); sFfPushEat = sFfLeftType.GetField("PushEat");
                if (sFfChosen != null && sFfPushEat != null) Patch(sFfLeftType, "Run", Type.EmptyTypes, typeof(FfLeftHook), "RunHook", "RunOrig");
            }
            sFfThing = typeof(Fridge_Have).GetField("mThingToPrepareOrEat", BindingFlags.Instance | BindingFlags.NonPublic);
        }

        static void FfLine(string s) { sFfLast = s; if (ShortcutOn("facilityplanlog") || ShortcutOn("fridgefflog")) DiagLine(s); }
        static void FfError(Exception e) { if (PatchCounters()[kFfErrors]++ < 5) DiagLine("fridgeff: error " + e.GetType().Name + ": " + e.Message + " " + e.StackTrace); }

        // Busy for this Sim: someone else uses the fridge, or another Sim heads its line.
        static bool FfBusy(Fridge f, Sim a)
        {
            if (f == null || a == null) return false;
            foreach (Sim u in f.ActorsUsingMe) if (u != null && u != a) return true;
            Sim head = f.Line == null ? null : f.Line.FirstSim;
            return head != null && head != a;
        }

        // Decided once, at the start of the Run: true = take the food now.
        static bool FfGo(Fridge f, Sim a, string what)
        {
            if (!a.IsHuman || a.SimDescription == null || !a.SimDescription.ChildOrAbove || a.ExitReason != ExitReason.None || !FfBusy(f, a)) return false;
            int[] c = PatchCounters();
            c[kFfBusy]++;
            bool npc = !IsPlayerSim(a);
            bool on = npc ? ShortcutOn("fridgeff") : ShortcutOn("fridgeffunseen");
            if (!on) { c[kFfWould]++; return false; }
            Vector3 slot = f.GetPositionOfSlot(Slot.RoutingSlot_0);
            bool selected = a == PlumbBob.SelectedActor;
            if (!npc && selected) { c[kFfExempt]++; return false; }  // the selected Sim never counts as unseen
            bool unseen = npc ? TownieUnseen(a, null) && !UcOnScreen(f) && FarFromViewers(slot)
                : !selected && !UcOnScreen(a) && FfFarFromCamera(a.Position) && !UcOnScreen(f) && FfFarFromCamera(slot);
            if (!unseen) { c[kFfSeen]++; return false; }
            FfLine("fridgeff: " + (npc ? "npc " : "household ") + a.FullName + " takes its " + what + " at once, " + f.GetType().Name + " busy (users " + f.ActorsUsingMe.Count
                + ", line " + (f.Line == null ? 0 : f.Line.MemberCount()) + "), " + (a.Position - slot).Length().ToString("0.0") + " m from the slot");
            return true;
        }

        // The camera half of FarFromViewers: FarFromViewers also counts every active-household Sim as a viewer within 80 m, so
        // for a household Sim at home (itself a viewer at distance 0) it is never true. The household opt-in asks only
        // whether the player can see the spot: off screen (UcOnScreen) and away from the camera's target and eye.
        static bool FfFarFromCamera(Vector3 p)
        {
            long now = Sims3.Gameplay.Utilities.SimClock.CurrentTicks;
            if (now != sViewTick) RefreshViewers(now);
            float dx = p.x - sCamTarget.x, dy = p.y - sCamTarget.y, dz = p.z - sCamTarget.z;
            if (dx * dx + dy * dy + dz * dz < sReach2) return false;
            dx = p.x - sCamPos.x; dy = p.y - sCamPos.y; dz = p.z - sCamPos.z;
            return dx * dx + dy * dy + dz * dz >= sReach2;
        }

        public abstract class FfHaveHook : Fridge_Have
        {
            public bool RunHook()
            {
                bool go = false;
                try { go = !CheckForCancelAndCleanup() && ChosenRecipe != null && FfGo(Target, Actor, "food"); }
                catch (ResetException) { throw; }
                catch (Exception e) { FfError(e); }
                if (!go) return RunOrig();
                return FfHave();
            }
            public bool RunOrig() { return false; }  // receives Fridge_Have.Run's original body

            // Fridge_Have.Run's own steps without RouteToOpen, the impassable region, the state machine and the animations.
            bool FfHave()
            {
                StandardEntry();
                if (Actor.SimDescription.TeenOrAbove && !ChosenRecipe.IsSnack) Actor.SkillManager.AddElement(SkillNames.Cooking);
                bool made = true;
                List<Ingredient> chosen = new List<Ingredient>();
                if (ChosenRecipe.UseUpIngredientsFrom(Actor, ref chosen, Quantity) || Actor.IsNPC)
                {
                    IRemovableFromFridgeAsInitialRecipeStep item = GlobalFunctions.CreateObjectOutOfWorld(ChosenRecipe.ObjectToCreateInFridge, ChosenRecipe.ModelCodeVersion) as IRemovableFromFridgeAsInitialRecipeStep;
                    if (item == null) { StandardExit(); return false; }
                    GameObject go = item as GameObject;
                    go.AddToUseList(Actor);
                    try
                    {
                        Target.PutOnFridgeShelf(go);
                        IPartOfCookingProcess part = item as IPartOfCookingProcess;
                        if (sFfThing != null) sFfThing.SetValue(this, part);
                        part.CookingProcess = new CookingProcess(ChosenRecipe, chosen, ObjectClickedOn, Target.LotCurrent, Destination, Quantity, Repetition, MenuText, MenuPath, part, Actor);
                        item.InitializeForRecipe(ChosenRecipe, false);
                        CookingProcess.MoveToNextStep(part, Actor);
                    }
                    catch (ResetException) { throw; }
                    catch { go.Destroy(); throw; }
                    CarrySystem.EnterWhileHolding(Actor, item, false);
                    if (CheckForCancelAndCleanup()) return false;
                    PushNextInteraction(item, go);
                    PatchCounters()[kFfHave]++;
                }
                else made = false;
                StandardExit();
                if (made) ActiveTopic.AddToSim(Actor, "Has Made Food");
                return made;
            }
        }

        public abstract class FfLeftHook : Interaction<Sim, Fridge>  // `this` is a Fridge_EatLeftovers (internal): its fields by reflection
        {
            ServingContainer ChosenContainer { get { return sFfChosen.GetValue(this) as ServingContainer; } set { sFfChosen.SetValue(this, value); } }
            bool PushEat { get { return (bool)sFfPushEat.GetValue(this); } }
            public bool RunHook()
            {
                bool go = false;
                try { go = ChosenContainer != null && ChosenContainer.HasFoodLeft() && FfGo(Target, Actor, "leftovers"); }
                catch (ResetException) { throw; }
                catch (Exception e) { FfError(e); }
                if (!go) return RunOrig();
                return FfLeft();
            }
            public bool RunOrig() { return false; }  // receives Fridge_EatLeftovers.Run's original body

            // Fridge_EatLeftovers.Run with GrabAServing / GrabContainer's own steps, no route, region, state machine or animation.
            bool FfLeft()
            {
                if (ChosenContainer.HasBeenDestroyed || ChosenContainer.ItemComp.InventoryParent != Target.FridgeInventory) return false;
                if (CheckForCancelAndCleanup()) return false;
                StandardEntry();
                BeginCommodityUpdates();
                if (Target is FridgeCheap) ChosenContainer.CookingProcess.IsLeftover = true;
                ChosenContainer.AddToUseList(Actor);
                ServingContainerSingle single = null;
                if (ChosenContainer is ServingContainerGroup)
                {
                    single = ChosenContainer.CookingProcess.CreateSingleServingOfFood(ChosenContainer, true, true);
                    single.AddToUseList(Actor);
                    try { Target.PutOnFridgeShelf(single); }
                    catch (ResetException) { throw; }
                    catch { single.Destroy(); throw; }
                    (ChosenContainer as ServingContainerGroup).DecrementServings();
                    ChosenContainer.RemoveFromUseList(Actor);
                    if (!ChosenContainer.HasFoodLeft())
                    {
                        SharedFridgeInventory shared = Target.LotCurrent.GetSharedFridgeInventory(Actor);
                        if (shared != null) { shared.Inventory.RemoveByForce(ChosenContainer); ChosenContainer.Destroy(); }
                    }
                }
                else if (ChosenContainer is ServingContainerSingle)
                {
                    SharedFridgeInventory shared = Target.LotCurrent.GetSharedFridgeInventory(Actor);
                    if (shared != null && shared.Inventory.TryToRemove(ChosenContainer))
                    {
                        single = ChosenContainer as ServingContainerSingle;
                        ChosenContainer.AddToWorld();
                        try { Target.PutOnFridgeShelf(single); }
                        catch (ResetException) { throw; }
                        catch { single.Destroy(); throw; }
                        ChosenContainer.RemoveFromUseList(Actor);
                    }
                }
                if (single != null)
                {
                    ChosenContainer = null;
                    CarrySystem.EnterWhileHolding(Actor, single, false);
                    if (PushEat) Actor.InteractionQueue.PushAsContinuation(EatHeldFood.Singleton, single, false);
                    PatchCounters()[kFfLeftovers]++;
                }
                else ChosenContainer.RemoveFromUseList(Actor);  // EA's Cleanup would, through its private flag
                EndCommodityUpdates(true);
                StandardExit();
                return single != null;
            }
        }

        static string FridgeFfCheck(string what)
        {
            int[] c = PatchCounters();
            return "fridgeff (npc " + (ShortcutOn("fridgeff") ? "on" : "off") + ", household unseen " + (ShortcutOn("fridgeffunseen") ? "on" : "off") + "): busy starts " + c[kFfBusy]
                + ", taken at once: food " + c[kFfHave] + ", leftovers " + c[kFfLeftovers] + "; would " + c[kFfWould] + ", seen (EA's line) " + c[kFfSeen] + ", exempt " + c[kFfExempt]
                + ", errors " + c[kFfErrors] + "; last: " + sFfLast;
        }
    }
}
