using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    public static partial class Fixes
    {
        // RhDirect answers: kRhdPass = not ours (the trip shortcut and EA's method run as before); else the result EA's
        // RouteNearEntranceAndEnterRabbitHole returns
        const int kRhdPass = -1, kRhdFailed = 0, kRhdEntered = 1;

        public partial class RabbitHoleHook
        {
            // ---- rabbitholedirect (default off; townies only) ----
            // An unseen townie entering a rabbit hole skips the trip, RouteOutside and the walk-up: it is placed on one of the
            // building's enter slots (EA's own random pick) and EA's post-route steps run as RouteNearEntranceAndEnterRabbitHole
            // runs them after a successful route [verified, IL 490562-491193]: the NumSuccess tally (unless HigherPriorityNext or
            // UserCanceled is set), beforeEntering (false = EA's RouteOutside from the door and false), then
            // AnimateEnterRabbitHole(a, slot, doRoute, meta): door, enter animation, fade, hide position, InRabbitHolePosture,
            // events 0x8c / 0x118 [verified, IL 491305-491759]. doRoute false when the Sim still stands on the slot (as
            // Subway.SubwayPortalComponent.OnPortalStart calls it), true when beforeEntering moved it (EA's own short route back).
            // Only the start must be unseen. A door in view is allowed only when nothing runs between the move and the enter
            // animation (no beforeEntering or the base no-op), with the Sim faded out first (EA's exit fades it in, IL ~491857).
            // Not ours (counted): no proxy or slots, followers, not standing / riding / carried / carrying / in a vehicle, seen or
            // out of scope (household, selected, no household), a door in view with a custom beforeEntering, an exit reason set,
            // the walk-time hold (unseenfbtriphold) given up. canUseCar needs nothing: no route, no car.
            // Counters 3496 asked, 3497 entered, 3498 no proxy/slots/lot, 3499 followers, 3500 posture/held, 3501 seen or scope,
            // 3502 door in view + custom beforeEntering, 3503 beforeEntering said no, 3504 moved by beforeEntering (doRoute),
            // 3505 exit reason or hold given up, 3506 enter animation failed or threw, 3507 faded (door in view).
            int RhDirect(Sim a, IRabbitHoleFollowers inst, BeforeEnteringRabbitHoleDelegate beforeEntering, Route.RouteMetaType routeMetaType)
            {
                int[] c = PatchCounters();
                c[3496]++;
                RabbitHole proxy = RabbitHoleProxy;
                Lot lot = proxy != null ? proxy.LotCurrent : null;
                if (a == null || proxy == null || lot == null || lot.IsWorldLot || proxy.EnterSlots.Count == 0 || proxy.ExitSlots.Count == 0) { c[3498]++; return kRhdPass; }
                List<Sim> fol = inst != null ? inst.SimFollowers : null;
                if (fol != null && fol.Count > 0) { c[3499]++; return kRhdPass; }
                if (a.Posture != a.Standing || UfHeld(a)) { c[3500]++; return kRhdPass; }
                if (IsPlayerSim(a) || a.Household == null || !TownieUnseen(a, null)) { c[3501]++; return kRhdPass; }
                if (a.HasExitReason(ExitReason.Default)) { c[3505]++; return kRhdPass; }
                Slot slot = RandomUtil.GetRandomObjectFromList(proxy.EnterSlots);
                Vector3 pos = proxy.GetPositionOfSlot(slot), fwd = proxy.GetForwardOfSlot(slot);
                if (UfPreCore.TripMode(false, false, ShortcutOn("unseenfbtriphold")) == 2)
                {
                    // the hold needs the door unseen (UfTripHold asks UfGoalUnseen), and the Sim may have been seen meanwhile
                    if (DoorSeen(proxy, pos) || !UfTripHold(a, lot, pos) || a.HasExitReason(ExitReason.Default)) { c[3505]++; return kRhdPass; }
                }
                int gate = RabbitHoleDirectCore.DirectGate(DoorSeen(proxy, pos), NoOpBefore(beforeEntering));
                if (gate == RabbitHoleDirectCore.kRhdRefuse) { c[3502]++; return kRhdPass; }
                bool fade = gate == RabbitHoleDirectCore.kRhdFade, ok = false;
                PlaceSim(a, pos, fwd, "rabbithole direct obj " + proxy.ObjectId.Value);
                try
                {
                    if (fade) { a.FadeOut(false, false, 0f); c[3507]++; }
                    if ((a.ExitReason & (ExitReason.HigherPriorityNext | ExitReason.UserCanceled)) == ExitReason.None) NumSuccess++;
                    if (beforeEntering != null && !beforeEntering())
                    {
                        c[3503]++;
                        if (fade) { a.FadeIn(false); fade = false; }
                        RouteOutside(a, null, RandomUtil.GetRandomObjectFromList(proxy.EnterSlots), false, false, true, true);  // as EA does
                        return kRhdFailed;
                    }
                    bool route = RabbitHoleDirectCore.DoRoute((a.Position - pos).Length());
                    if (route) c[3504]++;
                    ok = AnimateEnterRabbitHole(a, slot, route, routeMetaType);
                    if (!ok) { c[3506]++; return kRhdFailed; }
                    c[3497]++;
                    return kRhdEntered;
                }
                catch (Exception) { c[3506]++; throw; }  // a thread reset or a game error: as if EA's method threw
                finally { if (fade && !ok) a.FadeIn(false); }  // never leave a faded Sim outside
            }

            static bool DoorSeen(RabbitHole proxy, Vector3 door) { return UcOnScreen(proxy) || !FarFromViewers(door) || !UfGoalUnseen(door); }

            // ---- rhoutside (default off; townies only) ----
            // RabbitHole.RouteOutside (this 7-argument overload; the 2-argument one calls it) plans a whole trip to 2-4 m outside an
            // entrance slot and walks it. Callers that go there first (EA's EatOutside, and other mods' eat-outside interactions that
            // call it) fail before RouteNearEntranceAndEnterRabbitHole, so rabbitholedirect and the rabbithole shortcut never see
            // those trips. Same rule as the rabbithole shortcut: an unseen lone standing townie far from a building whose door is
            // unseen is placed at a safe spot on the building's lot near that slot (SafeSpot), then EA's RouteOutside runs unchanged
            // and plans only the last metres. Riding Sims, followers, household and seen Sims, world-lot buildings and a Sim already
            // on the lot keep EA's trip. Counters 3508 calls, 3509 placed, 3510 no safe spot.
            public bool OutsideHook(Sim s, List<Sim> followers, Slot slot, bool ignoreSlotFootprint, bool cancellable, bool faceaway, bool forceOnLot)
            {
                int[] c = PatchCounters();
                c[3508]++;
                if (ShortcutOn("rhoutside")) RhOutside(c, s, followers, slot);
                return OutsideOrig(s, followers, slot, ignoreSlotFootprint, cancellable, faceaway, forceOnLot);
            }
            public bool OutsideOrig(Sim s, List<Sim> followers, Slot slot, bool ignoreSlotFootprint, bool cancellable, bool faceaway, bool forceOnLot)
            { return false; }  // receives RabbitHole.RouteOutside's original header

            void RhOutside(int[] c, Sim s, List<Sim> followers, Slot slot)
            {
                try
                {
                    RabbitHole proxy = RabbitHoleProxy;
                    Lot lot = proxy != null ? proxy.LotCurrent : null;
                    if (s == null || lot == null || lot.IsWorldLot || s.LotCurrent == lot) return;
                    Vector3 door = proxy.GetPositionOfSlot(slot);
                    if (!TownieUnseen(s, followers) || (s.Position - door).Length() <= kShortcutMinDistance || UcOnScreen(proxy) || !FarFromViewers(door)) return;
                    Vector3 pos, fwd;
                    if (!SafeSpot(s, lot, door, out pos, out fwd)) { c[3510]++; return; }
                    PlaceSim(s, pos, fwd, "rhoutside " + proxy.CatalogName);
                    c[3509]++;
                }
                catch (Exception) { }  // any doubt: EA's whole trip, as before
            }

            // beforeEntering is the interaction's BeforeEnteringRabbitHole (RabbitHoleInteraction`2.RouteNearEntranceAndIntoBuilding
            // binds it with ldvirtftn [verified, IL 474405]); the no-op is the base one (returns true). The override is looked up on
            // the target's own type, so the answer does not depend on what Delegate.Method reports for a virtual binding.
            static readonly Dictionary<Type, bool> sRhdNoOp = new Dictionary<Type, bool>();
            const int kRhdNoOpCap = 512;
            static bool NoOpBefore(BeforeEnteringRabbitHoleDelegate d)
            {
                if (d == null) return true;
                try
                {
                    object target = d.Target;
                    if (target == null || d.Method == null || d.Method.Name != "BeforeEnteringRabbitHole") return false;
                    Type t = target.GetType();
                    bool b;
                    if (sRhdNoOp.TryGetValue(t, out b)) return b;
                    const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                    MethodInfo m = t.GetMethod("BeforeEnteringRabbitHole", inst, null, Type.EmptyTypes, null);
                    Type dt = m != null ? m.DeclaringType : null;
                    b = dt != null && dt.IsGenericType && dt.GetGenericTypeDefinition() == typeof(RabbitHole.RabbitHoleInteraction<,>);
                    if (sRhdNoOp.Count < kRhdNoOpCap) sRhdNoOp[t] = b;
                    return b;
                }
                catch (Exception) { return false; }  // unknown = custom: a door in view keeps the old path
            }
        }
    }
}
