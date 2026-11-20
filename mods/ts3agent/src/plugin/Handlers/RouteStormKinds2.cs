using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems.Children;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects.Electronics;
using Sims3.Gameplay.Objects.HobbiesSkills;
using Sims3.Gameplay.Objects.Vehicles;
using Sims3.Gameplay.PetSystems;
using Sims3.Gameplay.Services;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Agent
{
    // route_storm opt-in kinds (docs/668 §1.1): the game's own calls behind fixes the seven route kinds never reach.
    //   taxi     CarNpcManager.CreateServiceCar + DriveToLotInServiceCar (forceCar, the Sim walks to the car) to a far lot
    //            without valid street parking or a houseboat lot, so CarNpcManager.InitCarForLotRoute asks
    //            LotManager.GetNearestLot(pos, null, requireStreetParking: true) (ts3fixes nearestlotlazy, docs/677)
    //   jam      Guitar's own Play on a guitar in the Sim's inventory (made with the game's create-out-of-world +
    //            Inventory.TryToAdd if it has none): ReadyToPlay -> RouteBeforePlaying -> BandInstrument.MoveJigToGoodSpot
    //            -> GlobalFunctions.FindGoodLocationNearby; half the pushes prefer Sims indoors (little free space)
    //   laptop   Sim.UseLaptopHere on a laptop in inventory (made the same way if needed): FindGoodLocationNearby
    //   putdown  PickUpChild on a toddler of the Sim's household on the same lot, then PutDownChild once carried
    //   follow   GoToLot with Followers = [a nearby NPC]: DoRouteWithFollowers -> LeaderGoHereWith -> GoHereWithSituation
    //            -> FollowerMeetUpAtPoint on the follower
    //   stray    StrayPets.StrayPetHelper (the hourly alarm's body, which another modder's core replaces with a loop over
    //            StrayPetInactiveLotCheck), once per wave; skipped without Pets, strays or that mod
    // Objects the storm made (cars, guitars, laptops) are removed when their push is over and on stop; one still in use
    // at stop is removed by a cleanup task once free (60 s at most).
    public static partial class Handlers
    {
        static int sStormMade, sStormCleaned, sStormCleanGen;
        static readonly List<KeyValuePair<Sim, IGameObject>> sStormObjs = new List<KeyValuePair<Sim, IGameObject>>();
        static List<Lot> sStormNoParking;
        static Dictionary<Sim, bool> sStormTaken, sStormBusy;
        static List<Sim> sStormPool;
        static string sStormStrayMod;  // null = not looked up in this world, "" = absent, else the type that has it

        const float kStormFollowNear = 30f;

        static void StormNote(string kind, string why, string who, Dictionary<string, int> failed, List<object> fails, Dictionary<string, int> skipped, List<object> skips)
        {
            bool skip = why.StartsWith("skip: ");
            string f = (who != null ? who + " " : "") + kind + ": " + (skip ? why.Substring(6) : why);
            if (skip)
            {
                StormAdd(skipped, kind); StormAdd(sStormSkipped, kind); skips.Add(f);
                if (sStormSkips.Count < 40) sStormSkips.Add(f);
            }
            else
            {
                StormAdd(failed, kind); StormAdd(sStormFailed, kind); fails.Add(f);
                if (sStormFailures.Count < 40) sStormFailures.Add(f);
            }
        }

        static bool StormNeedsFit(string kind) { return kind == "jam" || kind == "laptop" || kind == "putdown" || kind == "follow" || kind == "taxi"; }

        static string StormNoFitWhy(string kind)
        {
            switch (kind)
            {
                case "putdown": return "skip: no free NPC teen or older with a toddler of its household on the same lot";
                case "follow": return "skip: no free NPC with another free NPC within " + kStormFollowNear + " m";
                case "taxi": return "skip: no free NPC on a lot";
                default: return "skip: no free NPC on a lot";
            }
        }

        // strict = the first pass: jam alternates indoors / outdoors Sims; the second pass takes any Sim on a lot.
        static bool StormFits(string kind, Sim c, int n, bool strict)
        {
            Lot l = c.LotCurrent;
            switch (kind)
            {
                case "jam":
                case "laptop":
                    if (l == null || l.IsWorldLot) return false;  // world lot = FindGoodLocation, not the staged Nearby search
                    return !strict || kind == "laptop" || c.IsOutside == ((n / 2) % 2 == 1);
                case "taxi":
                    return l != null;
                case "putdown":
                    return c.SimDescription.TeenOrAbove && c.CarryingChildPosture == null && StormToddler(c) != null;
                case "follow":
                    return l != null && StormFollower(c, strict) != null;
            }
            return true;
        }

        static Sim StormToddler(Sim p)
        {
            Household h = p.Household;
            if (h == null) return null;
            foreach (Sim t in h.Sims)
            {
                if (t == null || t == p || t.HasBeenDestroyed || !t.InWorld || t.SimDescription == null || !t.SimDescription.Toddler) continue;
                if (t.LotCurrent != p.LotCurrent || sStormTaken.ContainsKey(t) || t.Posture is BeingCarriedPosture) continue;
                if (!StormHhAllowed(t)) continue;
                return t;
            }
            return null;
        }

        static bool StormHhAllowed(Sim t)
        {
            Household act = Household.ActiveHousehold;
            if (sStormHhOn) return true;
            return !(t.IsSelectable || t == PlumbBob.SelectedActor || (act != null && t.Household == act));
        }
        static bool sStormHhOn;

        // strict: same household first (a family outing); else any free NPC from the pool on the same lot within range
        static Sim StormFollower(Sim s, bool strict)
        {
            foreach (Sim c in sStormPool)
            {
                if (c == s || sStormTaken.ContainsKey(c) || sStormBusy.ContainsKey(c) || c.LotCurrent != s.LotCurrent) continue;
                if (strict && c.Household != s.Household) continue;
                if ((c.Position - s.Position).LengthSqr() <= kStormFollowNear * kStormFollowNear) return c;
            }
            return null;
        }

        static string StormPush2(string kind, Sim s, Random rng)
        {
            InteractionPriority npc = new InteractionPriority(InteractionPriorityLevel.NonCriticalNPCBehavior);
            InteractionPriority auto = new InteractionPriority(InteractionPriorityLevel.Autonomous);
            switch (kind)
            {
                case "taxi":
                {
                    List<Lot> targets = sStormNoParking.Count > 0 ? sStormNoParking : sStormCommunity;
                    Lot l = StormPick(targets, s, rng, true);
                    if (l == null) return "skip: no lot to drive to";
                    if (CarNpcManager.Singleton == null) return "skip: no CarNpcManager";
                    CarService car = CarNpcManager.Singleton.CreateServiceCar(ServiceType.None);
                    if (car == null) return "service car not made";
                    InteractionDefinition def = new DriveToLotInServiceCar.Definition(car, false, true, true, null, 30f, true);
                    if (!s.InteractionQueue.Add(def.CreateInstance(l, s, npc, false, true)))
                    {
                        car.Destroy();
                        return "DriveToLotInServiceCar not queued (" + l.Name + ")";
                    }
                    StormKeep(s, car);
                    return null;
                }
                case "jam":
                {
                    InteractionDefinition play = StormNestedSingleton(typeof(Guitar), "Play");
                    if (play == null) return "skip: Guitar.Play not found";
                    Guitar g = s.Inventory.Find<Guitar>(true);
                    if (g == null)
                    {
                        g = StormMakeInInventory(s, GlobalFunctions.CreateObjectOutOfWorld("musicalInstrumentGuitar", ProductVersion.BaseGame)) as Guitar;
                        if (g == null) return "guitar not made or not added to the inventory";
                    }
                    return StormAddOr(s, play, g, auto, npc) ? null : "Guitar.Play not queued";
                }
                case "laptop":
                {
                    if (!GameUtils.IsInstalled(ProductVersion.EP9)) return "skip: University (EP9) not installed (UseLaptopHere's jig)";
                    ComputerLaptop lt = s.Inventory.Find<ComputerLaptop>(true);
                    if (lt == null)
                    {
                        lt = StormMakeInInventory(s, GlobalFunctions.CreateObjectOutOfWorld("ComputerLaptop", ProductVersion.BaseGame, "Sims3.Gameplay.Objects.Electronics.ComputerLaptop", null)) as ComputerLaptop;
                        if (lt == null) return "laptop not made or not added to the inventory";
                    }
                    if (lt.Repairable != null && lt.Repairable.Broken) return "skip: the Sim's laptop is broken";
                    return StormAddOr(s, Sim.UseLaptopHere.Singleton, lt, auto, npc) ? null : "UseLaptopHere not queued";
                }
                case "putdown":
                {
                    Sim t = StormToddler(s);
                    if (t == null) return "skip: no toddler";
                    if (!StormAddOr(s, PickUpChild.Singleton, t, auto, npc)) return "PickUpChild not queued";
                    sStormTaken[t] = true;
                    StormPutDownLater(s, t, sStormGen);
                    return null;
                }
                case "follow":
                {
                    Sim f = StormFollower(s, true) ?? StormFollower(s, false);
                    if (f == null) return "skip: no follower near";
                    Lot l = StormPick(sStormCommunity, s, rng, true);
                    if (l == null) return "skip: no community lot";
                    GoToLot g = GoToLot.Singleton.CreateInstance(l, s, npc, true, true) as GoToLot;
                    if (g == null) return "GoToLot not made";
                    g.Followers = new List<Sim>(new Sim[] { f });
                    if (!s.InteractionQueue.Add(g)) return "GoToLot with follower not queued";
                    sStormTaken[f] = true;
                    return null;
                }
            }
            return "unknown kind";
        }

        // Autonomous first (autonomy's own push), then at NonCriticalNPCBehavior non-autonomous (fewer Test gates).
        static bool StormAddOr(Sim s, InteractionDefinition def, IGameObject target, InteractionPriority auto, InteractionPriority npc)
        {
            if (s.InteractionQueue.Add(def.CreateInstance(target, s, auto, true, true))) return true;
            return s.InteractionQueue.Add(def.CreateInstance(target, s, npc, false, true));
        }

        static IGameObject StormMakeInInventory(Sim s, IGameObject o)
        {
            if (o == null) return null;
            if (o is FailureObject || !s.Inventory.TryToAdd(o)) { o.Destroy(); return null; }
            StormKeep(s, o);
            return o;
        }

        static void StormKeep(Sim s, IGameObject o) { sStormObjs.Add(new KeyValuePair<Sim, IGameObject>(s, o)); sStormMade++; }

        static InteractionDefinition StormNestedSingleton(Type owner, string nested)
        {
            Type t = owner.GetNestedType(nested, BindingFlags.NonPublic | BindingFlags.Public);
            FieldInfo f = t == null ? null : t.GetField("Singleton", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            return f == null ? null : f.GetValue(null) as InteractionDefinition;
        }

        // Waits (up to ~60 s of sim time) until s carries t, then pushes EA's PutDownChild, the staged FindGoodLocationNearby.
        static void StormPutDownLater(Sim s, Sim t, int gen)
        {
            Simulator.AddObject(new OneShotFunctionTask(delegate
            {
                // after a stop no more waiting, but a toddler already carried is still put down (else the NPC keeps it)
                for (int i = 0; i < 120; i++)
                {
                    Simulator.Sleep(30);
                    if (s.HasBeenDestroyed || t.HasBeenDestroyed || s.InteractionQueue == null) return;
                    CarryingChildPosture cp = s.CarryingChildPosture;
                    if (cp == null || cp.Child != t) { if (sStormGen != gen) return; continue; }
                    InteractionInstance ii = PutDownChild.Singleton.CreateInstance(t, s, new InteractionPriority(InteractionPriorityLevel.NonCriticalNPCBehavior), false, true);
                    if (s.InteractionQueue.Add(ii)) sStormInst.Add(new KeyValuePair<Sim, InteractionInstance>(s, ii));
                    return;
                }
            }));
        }

        // One StrayPetHelper call. Unless household is set, the "active household not visited for too long" branch is held
        // off for this call (sLastActiveHouseholdVisit = now, restored if the helper did not set it itself).
        static string StormStray(bool hh)
        {
            if (!GameUtils.IsInstalled(ProductVersion.EP5)) return "skip: Pets (EP5) not installed";
            if (sStormStrayMod == null) sStormStrayMod = StormFindMethod("StrayPetInactiveLotCheck");
            if (sStormStrayMod.Length == 0) return "skip: no loaded type has StrayPetInactiveLotCheck (the other modder's core is absent)";
            List<SimDescription> strays = StrayPets.GetStrayPets(false);
            if (strays == null || strays.Count == 0) return "skip: no stray pets in this world";
            if (Household.ActiveHousehold == null) return "skip: no active household (the helper returns at once)";
            MethodInfo m = typeof(StrayPets).GetMethod("StrayPetHelper", BindingFlags.NonPublic | BindingFlags.Static);
            if (m == null) return "skip: StrayPets.StrayPetHelper not found";
            DateAndTime keep = StrayPets.sLastActiveHouseholdVisit, now = SimClock.CurrentTime();
            if (!hh) StrayPets.sLastActiveHouseholdVisit = now;
            try { m.Invoke(null, null); }
            catch (TargetInvocationException e)
            {
                if (e.InnerException is ResetException) throw e.InnerException;
                return "StrayPetHelper threw " + (e.InnerException != null ? e.InnerException.GetType().Name + ": " + e.InnerException.Message : e.Message);
            }
            finally { if (!hh && StrayPets.sLastActiveHouseholdVisit == now) StrayPets.sLastActiveHouseholdVisit = keep; }
            DiagLine("route_storm: stray helper called (" + strays.Count + " strays, " + sStormStrayMod + ")");
            return null;
        }

        // "" when no loaded type declares a static method of that name, else "Type.Method"; one scan per world.
        static string StormFindMethod(string name)
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types; }
                catch (Exception) { continue; }
                foreach (Type t in types)
                {
                    if (t == null) continue;
                    try
                    {
                        if (t.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly) != null)
                            return t.FullName + "." + name;
                    }
                    catch (AmbiguousMatchException) { return t.FullName + "." + name; }
                    catch (Exception) { }
                }
            }
            return "";
        }

        // Removes the storm's objects whose push is over: not in use, no occupants (cars), not being held. all: also those
        // whose Sim still has a storm push queued (stop); objects still in use then go to a cleanup task (60 s at most).
        static int StormCleanup(bool all)
        {
            Dictionary<Sim, bool> queued = new Dictionary<Sim, bool>();
            if (!all)
                foreach (KeyValuePair<Sim, InteractionInstance> kv in sStormInst)
                {
                    Sim s = kv.Key;
                    try
                    {
                        if (s == null || s.HasBeenDestroyed || s.InteractionQueue == null) continue;
                        foreach (InteractionInstance ii in s.InteractionQueue.InteractionList)
                            if (ii == kv.Value) { queued[s] = true; break; }
                    }
                    catch (ResetException) { throw; }
                    catch (Exception) { }
                }
            int n = 0;
            sStormObjs.RemoveAll(delegate(KeyValuePair<Sim, IGameObject> kv)
            {
                if (queued.ContainsKey(kv.Key)) return false;
                int r = StormRemove(kv.Key, kv.Value);
                if (r > 0) n++;
                return r >= 0;
            });
            sStormCleaned += n;
            if (all && sStormObjs.Count > 0)
            {
                int cg = ++sStormCleanGen;
                Simulator.AddObject(new OneShotFunctionTask(delegate
                {
                    int t0 = System.Environment.TickCount;
                    while (cg == sStormCleanGen && sStormObjs.Count > 0 && System.Environment.TickCount - t0 < 60000)
                    {
                        Simulator.Sleep(30);
                        int m = 0;
                        sStormObjs.RemoveAll(delegate(KeyValuePair<Sim, IGameObject> kv) { int r = StormRemove(kv.Key, kv.Value); if (r > 0) m++; return r >= 0; });
                        sStormCleaned += m;
                    }
                    if (cg == sStormCleanGen && sStormObjs.Count > 0) DiagLine("route_storm: cleanup gave up on " + sStormObjs.Count + " objects still in use");
                }));
            }
            return n;
        }

        // 1 removed, 0 already gone, -1 still in use (keep it)
        static int StormRemove(Sim s, IGameObject o)
        {
            try
            {
                GameObject go = o as GameObject;
                if (o == null || go == null || go.HasBeenDestroyed) return 0;
                Vehicle v = o as Vehicle;
                if (v != null && v.HasAnyOccupants()) return -1;
                if (go.InUse || (s != null && !s.HasBeenDestroyed && go.Parent == s)) return -1;
                if (s != null && !s.HasBeenDestroyed && s.Inventory != null && s.Inventory.Contains(o)) s.Inventory.TryToRemove(o);
                o.Destroy();
                return 1;
            }
            catch (ResetException) { throw; }
            catch (Exception) { return 0; }
        }
    }
}
