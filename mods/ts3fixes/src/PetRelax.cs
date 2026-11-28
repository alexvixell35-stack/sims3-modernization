using System;
using System.Collections.Generic;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.ObjectComponents;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // petrelax / petrelaxhh (docs/686 §11, default off). A player's wish: pets block and get blocked a lot, be less strict
    // with them; clipping is fine, waiting is not. No teleport: every Sim and pet walks its own route.
    //
    // [verified, decompiled Sims3.Gameplay] Pets are Sims in the routing code: same obstacle stand-and-wait, same
    // destination wait, same push rule, same portal lanes. What makes them worse than Sims:
    //   - RoutingComponent.PushSimsStatic pushes a Sim only when it has no interaction (or one of three
    //     IBePushedCanCancel classes, none of them a pet's). A pet that naps, sits, begs, follows or watches cannot be
    //     pushed, and pets are in such interactions most of the time.
    //   - PetSurfacePosture.LayDownHere (lie down / nap / sleep on the floor) takes the pet's own position when it runs
    //     autonomously (favourite spot and "next to my sleeping owner" aside) and places a PetSurfaceFloorJig there. A
    //     social jig locks its routing footprint (SocialJig.OnStartup, RoutingFootprintLocking MustHaveKey = itself), so
    //     a lying pet is an *object* in every other Sim's plan: a doorway or a stair foot it lies in is closed
    //     (NoPathToDestination, blocker Object), and neither simpass nor clipwait walk through it (both keep objects).
    //   - A running pet (PetRun, gallop) already walks through every Sim in the game's own obstacle handler and cannot
    //     be pushed: the game itself clips pets through Sims.
    // Built:
    //   1. Walk through (PetRelaxCore.Pass): a small pet walks through the Sims and small pets in its way, and a Sim
    //      walks through small pets when nothing else is in its way: the game's ignore-obstacles timer, as simpass.
    //      When (docs/686 §8.8, a player's rule: clipping is for doors and stairs, not the standard): at once, at the
    //      obstacle event or at a wait that still begins, only when the walker or one of those in its way is within
    //      kClipPortalNear of a portal lane; anywhere else the game's wait runs and its first cycle after
    //      kClipGraceMinutes ends it (the clipwait hooks; the clipwait switch is not needed). A destination wait takes
    //      the clipwait rule on the same timing (never into a Sim or pet using the object the walker goes to, never for
    //      a social, never at the end of a player's Go Here). Horses, foals and deer, as walker or in the way, are
    //      clipwait's alone.
    //   2. A lying pet's floor jig near a portal is not an obstacle: every route a Sim creates ignores the floor jigs of
    //      the lying pets in scope that lie within kClipPortalNear of a portal lane (Route.AddObjectToIgnoreForRoute,
    //      the game's own call for the pet itself). A pet that lies anywhere else is walked around as the game has it.
    //      In the two waits every lying pet's jig counts as its pet instead of as an object, on the timing of 1.
    //      A plan that fails with such a jig as its blocker (a pet lying across a hallway): the walker stands for
    //      kClipGraceMinutes where it is, then the route is planned again with the jig ignored and walked (PrxPlanFailed,
    //      from the DoRoute hook). The pet itself is then passed without a second grace.
    //   3. A pet about to lie down on the floor where it stands, within kPetRelaxPortalClear of a stair, door, gate,
    //      ladder or elevator lane end, first walks to a free spot 2-3 m away (a real route), then the game's lie-down
    //      runs from there. No free spot, or the route fails: the game's lie-down as it was.
    // Scope: petrelax covers pets and Sims the player does not control, as walker and as the one walked through;
    // petrelaxhh adds household and selected Sims and pets. Off = compare mode: the hooks count what the rule would do.
    // petrelaxlog writes one line per act. Counters 4430-4445. Check: PetRelaxCheck. Hooks of its own: the floor lie-down
    // and SimRoutingComponent.DoRoute (the failed plan of rule 2).
    public static partial class Fixes
    {
        const int kPrxPass = 4430, kPrxPassWait = 4431, kPrxClip = 4432, kPrxClipDest = 4433, kPrxJigRoutes = 4434, kPrxMoved = 4435, kPrxErrors = 4436,
            kPrxWould = 4437, kPrxExempt = 4438, kPrxKeptHorse = 4439, kPrxKeptObject = 4440, kPrxKeptDest = 4441, kPrxKeptNoSpot = 4442,
            kPrxLieDowns = 4443, kPrxLieAtPortal = 4444, kPrxHousehold = 4445;
        const int kPrxSitePass = 0, kPrxSiteWait = 1, kPrxSiteClip = 2, kPrxSiteRoute = 3, kPrxSiteLie = 4, kPrxSiteLieEnd = 5, kPrxSitePlan = 6;
        static readonly string[] kPrxSites = { "obstacle event", "obstacle wait", "wait cycle", "route create", "lie down", "lie down end", "failed plan" };
        static readonly int[] sPrxSiteErrors = new int[7];
        const int kPrxErrorLines = 3;      // lines per site
        const int kPrxMaxJigs = 64;        // lying pets tracked; more are not given the pass
        const int kPrxMaxPlans = 4;        // route plans per lie-down for a free spot
        const float kPrxEndSearch = 8f;    // lane ends farther than this from the pet are not collected

        class PrxJig { public GameObject Jig; public Sim Pet; public bool NearPortal; }  // NearPortal: read once when the pet lay down
        static readonly List<PrxJig> sPrxJigs = new List<PrxJig>();
        static readonly List<Vector3> sPrxEnds = new List<Vector3>();
        static readonly LineCapCore sPrxCap = new LineCapCore(20, 60000, 400);
        static string sPrxMissing = "", sPrxLast = "", sPrxLastError = "";
        static int sPrxDeferred, sPrxGrace;  // this session: waits left to the grace (not near a portal) / waits ended after it
        static int sPrxPlanWaits, sPrxPlanWalks, sPrxPlanStopped, sPrxPlanStill;  // this session, failed plans on a lying pet: stood the grace / planned through / cancelled meanwhile / failed without the jig too
        const int kPrxMaxPlanJigs = 3;          // lying pets passed per failed plan
        const float kPrxServedMinutes = 30f;    // sim-minutes a served grace stands for the pets it was served for
        class PrxServed { public DateAndTime At; public readonly List<Sim> Pets = new List<Sim>(2); }
        static readonly Dictionary<ulong, PrxServed> sPrxServed = new Dictionary<ulong, PrxServed>();  // by walker

        static void PrxError(int site, Exception e)
        {
            PatchCounters()[kPrxErrors]++;
            sPrxLastError = "petrelax: error at " + kPrxSites[site] + ": " + e.GetType().Name + ": " + e.Message;
            if (sPrxSiteErrors[site]++ < kPrxErrorLines) DiagLine(sPrxLastError);
        }

        static string PrxErrorSites()
        {
            string r = "";
            for (int i = 0; i < sPrxSiteErrors.Length; i++)
                if (sPrxSiteErrors[i] > 0) r += (r.Length > 0 ? ", " : "") + kPrxSites[i] + " " + sPrxSiteErrors[i];
            return r.Length > 0 ? " (" + r + "; last: " + sPrxLastError + ")" : "";
        }

        static void PrxLog(string line)
        {
            sPrxLast = line;
            if (ShortcutOn("petrelaxlog") && sPrxCap.Take(System.Environment.TickCount)) DiagLine(line);
        }

        // After ApplyShareLanes and ApplyClipWait: the obstacle event and the two waits are their hooks, the route hook is
        // RouteFixes.cs's (SetRouteOptions). Its own hook is the floor lie-down.
        static void ApplyPetRelax()
        {
            sPrxMissing = "";
            if (!Patch(typeof(PetSurfacePosture.LayDownHere), "Run", Type.EmptyTypes, typeof(PrxLieHook), "RunHook", "RunOrig")) sPrxMissing = "PetSurfacePosture.LayDownHere.Run";
            // the DoRoute step (PrxPlanFailed) runs in ApplyBlockedUse's hook, the only DoRoute hook (issue #92, docs/130 §11)
            if (sBuMissing.IndexOf("SimRoutingComponent.DoRoute", StringComparison.Ordinal) >= 0) sPrxMissing = (sPrxMissing + " SimRoutingComponent.DoRoute").Trim();
            if (sShlMissing.IndexOf("HandleEventEncounteredObstacles", StringComparison.Ordinal) >= 0 || sSpsSim == null || sSpsAction == null)
                sPrxMissing = (sPrxMissing + " obstacle event (sharelanes hooks)").Trim();
            if (sClwMissing.Length > 0) sPrxMissing = (sPrxMissing + " waits (clipwait hooks)").Trim();
            if (sPrxMissing.Length > 0) AppliedLog().Add("petrelax: not found: " + sPrxMissing);
        }

        // ---- who is who -------------------------------------------------------------------------------------------------

        static bool PrxScope(Sim s, bool hhOn) { return hhOn || !HouseholdExempt(s); }

        // Horse, foal, deer, or a Sim on or leading a horse: the large bodies that keep the clipwait grace.
        static bool PrxLarge(Sim s)
        {
            if (s.IsHorse || s.IsDeer) return true;
            Sim other = s.Posture == null ? null : s.Posture.Container as Sim;
            return other != null && other != s && other.IsHorse;
        }

        static bool PrxSmallPet(Sim s) { return (s.IsPet || s.IsRaccoon) && !PrxLarge(s); }

        // The pet lying in a floor jig; null = not a pet's floor jig (or its pet is gone).
        static Sim PrxJigPet(GameObject o)
        {
            PetSurfacePosture.PetSurfaceFloorJig jig = o as PetSurfacePosture.PetSurfaceFloorJig;
            if (jig == null || jig.HasBeenDestroyed) return null;
            Sim pet = jig.ContainedSim;
            return ClwIsWalkable(pet) ? pet : null;
        }

        // ClipWait.cs: a lying pet's floor jig in a waiting Sim's way counts as that pet when the rule may pass it.
        static Sim PrxJigPetInScope(GameObject o, Sim walker)
        {
            if (!ShortcutOn("petrelax")) return null;
            Sim pet = PrxJigPet(o);
            if (pet == null || pet == walker || PrxLarge(walker)) return null;
            bool hhOn = ShortcutOn("petrelaxhh");
            return PrxScope(pet, hhOn) && PrxScope(walker, hhOn) ? pet : null;
        }

        class PrxSeen
        {
            public PetRelaxCore.Case Case;
            public string Names = "";
            public int NameCount;
            public bool Household;
            public List<GameObject> Jigs;
            public readonly List<Sim> Sims = new List<Sim>(4);

            public PrxSeen(Sim walker, bool scopeAll)
            {
                Case.WalkerLarge = PrxLarge(walker);
                Case.WalkerSmallPet = PrxSmallPet(walker);
                Case.WalkerInScope = PrxScope(walker, scopeAll);
                Case.AllInScope = true;
                Household = IsPlayerSim(walker);
            }

            public void AddSim(Sim b, bool scopeAll)
            {
                if (Sims.Contains(b)) return;  // a lying pet and its jig are one
                Sims.Add(b);
                if (PrxLarge(b)) Case.Large++;
                else if (PrxSmallPet(b)) Case.SmallPets++;
                else Case.Humans++;
                if (!PrxScope(b, scopeAll)) Case.AllInScope = false;
                if (IsPlayerSim(b)) Household = true;
                if (NameCount++ < 3) Names += (Names.Length > 0 ? ", " : "") + b.FullName + " (" + SwfIa(b) + ")";
            }

            public void Add(Sim walker, GameObject o, bool scopeAll)
            {
                if (o == null || o == walker) return;
                Sim b = o as Sim;
                if (b != null)
                {
                    if (ClwIsWalkable(b)) AddSim(b, scopeAll); else Case.Others++;
                    return;
                }
                b = PrxJigPet(o);
                if (b == walker) return;  // its own jig, while it gets up
                if (b == null) { Case.Others++; return; }
                AddSim(b, scopeAll);
                if (Jigs == null) Jigs = new List<GameObject>(2);
                Jigs.Add(o);
            }

            public string Through() { return Names + (NameCount > 3 ? " +" + (NameCount - 3) : ""); }
        }

        static bool PrxAnyPet(IList<ObjectGuid> obstacles)
        {
            for (int i = 0; i < obstacles.Count; i++)
            {
                GameObject o = GameObject.GetObject(obstacles[i]);
                Sim b = o as Sim;
                if (b != null ? b.SimDescription != null && (b.IsPet || b.IsRaccoon) : o is PetSurfacePosture.PetSurfaceFloorJig) return true;
            }
            return false;
        }

        // Counts one judged case. true = go.
        static bool PrxCount(int d, int kept, int[] c)
        {
            if (kept == PetRelaxCore.kKeptHorse) c[kPrxKeptHorse]++;
            else if (kept == PetRelaxCore.kKeptObject) c[kPrxKeptObject]++;
            if (d == PetRelaxCore.kWould) c[kPrxWould]++;
            else if (d == PetRelaxCore.kExempt) c[kPrxExempt]++;
            return d == PetRelaxCore.kGo;
        }

        static void PrxIgnoreJigs(Route route, List<GameObject> jigs)
        {
            if (route == null || jigs == null) return;
            for (int i = 0; i < jigs.Count; i++) route.AddObjectToIgnoreForRoute(jigs[i].ObjectId);
        }

        // ---- 1. walk through: at once near a portal, elsewhere after the grace -------------------------------------------

        // ShareLanes.cs, before simpass: the obstacle event (action false) and the obstacle wait that still began (true).
        // true = the walker's ignore-obstacles timer runs. Near a portal only (ShareLanesCore.Timed); elsewhere the wait
        // begins and PrxClip ends it after the grace.
        static bool PrxPass(Sim sim, Route route, IList<ObjectGuid> obstacles, bool action)
        {
            try
            {
                if (sim == null || obstacles == null || obstacles.Count == 0 || sim.SimRoutingComponent == null || !ClwIsWalkable(sim)) return false;
                if (!PrxSmallPet(sim) && !PrxAnyPet(obstacles)) return false;  // the common case: no pet in it, nothing allocated
                bool on = ShortcutOn("petrelax"), hhOn = ShortcutOn("petrelaxhh");
                bool scopeAll = hhOn || !on;  // off: count what both switches would do
                PrxSeen s = new PrxSeen(sim, scopeAll);
                for (int i = 0; i < obstacles.Count; i++) s.Add(sim, GameObject.GetObject(obstacles[i]), scopeAll);
                int[] c = PatchCounters();
                int kept;
                int d = PetRelaxCore.Pass(on, s.Case, out kept);
                if (d != PetRelaxCore.kNone) d = ShareLanesCore.Timed(d, PrxGraceServed(sim, s.Sims) || ShlNearPortal(sim, s.Sims), 0f, HhGraceMin());
                if (d == ShareLanesCore.kDefer)
                {
                    if (action) sPrxDeferred++;
                    return false;
                }
                if (!PrxCount(d, kept, c)) return false;
                PrxIgnoreJigs(route, s.Jigs);  // a replan of this route passes the lying pets too
                sim.SimRoutingComponent.StartIgnoringObstacles(Math.Max(0.5f, Instantiator.kPetRelaxMinutes));  // the game's own timer
                c[action ? kPrxPassWait : kPrxPass]++;
                if (s.Household) c[kPrxHousehold]++;
                PrxLog("petrelax: " + sim.FullName + " " + ShlWho(sim) + ", " + SwfIa(sim) + ": walked through at the " + kPrxSites[action ? kPrxSiteWait : kPrxSitePass]
                    + ": " + s.Through() + ", at " + V(sim.Position));
                return true;
            }
            catch (ResetException) { throw; }
            catch (Exception e) { PrxError(action ? kPrxSiteWait : kPrxSitePass, e); return false; }
        }

        // ClipWait.cs, each cycle of an obstacle or destination wait the game wants to go on, before clipwait's own rule.
        // seen = what clipwait gathered (jigs of lying pets in scope are in it as their pets). true = the pet rule ends the
        // wait now (near a portal at once, elsewhere from kClipGraceMinutes on); destAccepted as ClipWaitCore.Decide.
        // counted keeps the "kept" and "would" counters to one per wait. afterGrace = the wait ran its grace.
        static bool PrxClip(Sim sim, ClipWaitCore.Case k, List<Sim> seen, ref bool counted, out bool destAccepted, out bool household, out bool afterGrace)
        {
            destAccepted = false; household = false; afterGrace = false;
            try
            {
                if (seen == null || seen.Count == 0) return false;
                bool on = ShortcutOn("petrelax"), hhOn = ShortcutOn("petrelaxhh");
                bool scopeAll = hhOn || !on;
                PrxSeen s = new PrxSeen(sim, scopeAll);
                for (int i = 0; i < seen.Count; i++) s.AddSim(seen[i], scopeAll);
                s.Case.Others = k.Others;
                int kept;
                int d = PetRelaxCore.Pass(on, s.Case, out kept);
                if (d != PetRelaxCore.kNone)
                {
                    afterGrace = ShareLanesCore.GraceDue(k.ElapsedMin, HhGraceMin());
                    if (!afterGrace && ShareLanesCore.Timed(d, PrxGraceServed(sim, seen) || ShlNearPortal(sim, seen), k.ElapsedMin, HhGraceMin()) == ShareLanesCore.kDefer) return false;
                }
                int[] c = PatchCounters();
                if (d != PetRelaxCore.kGo)
                {
                    if (!counted && (d != PetRelaxCore.kNone || kept != PetRelaxCore.kKeptNot)) { counted = true; PrxCount(d, kept, c); }
                    return false;
                }
                // where the walk may end: clipwait's rule, the timing and the scope already judged
                k.GraceMin = 0f; k.SimInScope = true; k.AllInScope = true;
                int keptDest;
                if (ClipWaitCore.Decide(true, k, out keptDest, out destAccepted) != ClipWaitCore.kGo)
                {
                    destAccepted = false;
                    if (!counted && keptDest != ClipWaitCore.kKeptNot) { counted = true; c[kPrxKeptDest]++; }
                    return false;
                }
                household = s.Household;
                return true;
            }
            catch (ResetException) { throw; }
            catch (Exception e) { PrxError(kPrxSiteClip, e); destAccepted = false; return false; }
        }

        // ---- 2. a lying pet's floor jig near a portal is not an obstacle ------------------------------------------------

        // RouteFixes.cs, SimRoutingComponent.SetRouteOptions (every route a Sim creates). The list is empty unless a pet
        // lay down on the floor since the world loaded. Only the pets that lie near a portal lane are passed (docs/686
        // §8.8): the others are walked around, and a Sim they keep waiting walks through them after the grace (PrxClip).
        static void PrxRoute(Sim sim, Route r)
        {
            if (sPrxJigs.Count == 0 || sim == null || r == null) return;
            try
            {
                bool on = ShortcutOn("petrelax"), hhOn = ShortcutOn("petrelaxhh");
                bool pass = on && PrxScope(sim, hhOn) && !PrxLarge(sim);
                Lot lot = sim.LotCurrent;
                int n = 0;
                for (int i = sPrxJigs.Count - 1; i >= 0; i--)
                {
                    PrxJig j = sPrxJigs[i];
                    if (j.Jig.HasBeenDestroyed || j.Pet.HasBeenDestroyed || j.Pet.Posture == null || !object.ReferenceEquals(j.Pet.Posture.Container, j.Jig))
                    {
                        sPrxJigs.RemoveAt(i);  // the pet got up
                        continue;
                    }
                    if (!pass || !j.NearPortal || j.Pet == sim || !PrxScope(j.Pet, hhOn) || j.Jig.LotCurrent != lot) continue;
                    r.AddObjectToIgnoreForRoute(j.Jig.ObjectId);
                    n++;
                }
                if (n > 0) PatchCounters()[kPrxJigRoutes]++;
            }
            catch (ResetException) { throw; }
            catch (Exception e) { PrxError(kPrxSiteRoute, e); }
        }

        // ---- 2b. a plan that failed on a lying pet: stand the grace, then plan through it -------------------------------

        // The walker stood its grace for these pets at a failed plan: meeting them on the walk needs no second one.
        static bool PrxGraceServed(Sim walker, List<Sim> inWay)
        {
            PrxServed v;
            if (sPrxServed.Count == 0 || inWay == null || inWay.Count == 0 || !sPrxServed.TryGetValue(walker.ObjectId.Value, out v)) return false;
            if (SimClock.ElapsedTime(TimeUnit.Minutes, v.At) > kPrxServedMinutes) { sPrxServed.Remove(walker.ObjectId.Value); return false; }
            for (int i = 0; i < inWay.Count; i++)
                if (!v.Pets.Contains(inWay[i])) return false;
            return true;
        }

        // The lying pet's floor jig that the failed plan of r names as its blocker, when the rule passes it; else null.
        static GameObject PrxPlanJig(Sim sim, Route r, GameObject not)
        {
            try
            {
                if (sim == null || r == null || !ClwIsWalkable(sim)) return null;
                RoutePlanResult res = r.PlanResult;
                if (res.Succeeded() || res.mBlockerObjectId == 0) return null;
                GameObject o = GameObject.GetObject(new ObjectGuid(res.mBlockerObjectId));
                Sim pet = PrxJigPet(o);
                if (pet == null || pet == sim || object.ReferenceEquals(o, not)) return null;
                bool on = ShortcutOn("petrelax"), hhOn = ShortcutOn("petrelaxhh");
                bool scopeAll = hhOn || !on;
                int kept;
                int d = PetRelaxCore.PlanJig(on, true, PrxLarge(sim), PrxScope(sim, scopeAll), PrxScope(pet, scopeAll), out kept);
                return PrxCount(d, kept, PatchCounters()) ? o : null;
            }
            catch (ResetException) { throw; }
            catch (Exception e) { PrxError(kPrxSitePlan, e); return null; }
        }

        // The walker stands where it is for the grace. No catch: a reset must unwind the Sim's stack. false = cancelled.
        static bool PrxPlanWait(Sim sim)
        {
            sPrxPlanWaits++;
            DateAndTime start = SimClock.CurrentTime();
            float grace = HhGraceMin();
            uint step = (uint)Math.Max(1L, SimClock.ConvertToTicks(0.5f, TimeUnit.Minutes));
            while (SimClock.ElapsedTime(TimeUnit.Minutes, start) < grace)
            {
                if (sim.HasBeenDestroyed || sim.HasExitReason()) return false;
                Simulator.Sleep(step);
            }
            return !sim.HasBeenDestroyed && !sim.HasExitReason();
        }

        // The jig leaves the route's plans and the route is planned again. ok = it plans now; else the answer is the next
        // lying pet the new plan names (null = something else blocks: the game's answer for the failed plan).
        static GameObject PrxPlanRetry(Sim sim, Route r, GameObject jig, bool waited, out bool ok)
        {
            ok = false;
            try
            {
                Sim pet = PrxJigPet(jig);
                if (!jig.HasBeenDestroyed) r.AddObjectToIgnoreForRoute(jig.ObjectId);
                ok = r.Replan().Succeeded();
                if (pet != null)
                {
                    PrxServed v;
                    ulong id = sim.ObjectId.Value;
                    if (!sPrxServed.TryGetValue(id, out v) || SimClock.ElapsedTime(TimeUnit.Minutes, v.At) > kPrxServedMinutes)
                    {
                        if (sPrxServed.Count > kPrxMaxJigs) sPrxServed.Clear();
                        v = new PrxServed();
                        sPrxServed[id] = v;
                    }
                    v.At = SimClock.CurrentTime();
                    if (!v.Pets.Contains(pet)) v.Pets.Add(pet);
                }
                if (!ok) return PrxPlanJig(sim, r, jig);
                sPrxPlanWalks++;
                if (IsPlayerSim(sim) || (pet != null && IsPlayerSim(pet))) PatchCounters()[kPrxHousehold]++;
                PrxLog("petrelax: " + sim.FullName + " " + ShlWho(sim) + ", " + SwfIa(sim) + ": plan failed on a lying pet, planned through it " + (waited ? "after the grace" : "at once (near a portal)")
                    + ": " + (pet == null ? "(up again)" : pet.FullName) + ", at " + V(sim.Position));
                return null;
            }
            catch (ResetException) { throw; }
            catch (Exception e) { PrxError(kPrxSitePlan, e); ok = false; return null; }
        }

        // The DoRoute hook, for a route whose plan failed: true = the route plans now (walk it). A jig near a portal needs
        // no grace (routes ignore those from their creation; this is for a pet that lay down after the route was made).
        static bool PrxPlanFailed(Sim sim, Route r)
        {
            GameObject jig = PrxPlanJig(sim, r, null);
            bool waited = false;
            for (int n = 0; n < kPrxMaxPlanJigs && jig != null; n++)
            {
                if (!waited && ShareLanesCore.Timed(PetRelaxCore.kGo, ShlNearPortal(sim.LotCurrent, jig.Position), 0f, HhGraceMin()) == ShareLanesCore.kDefer)
                {
                    if (!PrxPlanWait(sim)) { sPrxPlanStopped++; return false; }
                    waited = true;
                }
                bool ok;
                jig = PrxPlanRetry(sim, r, jig, waited, out ok);
                if (ok) return true;
            }
            if (waited) sPrxPlanStill++;
            return false;
        }

        // ---- 3. not on a lane end ---------------------------------------------------------------------------------------

        // The lane ends of the lot's portals near p, on p's floor.
        static void PrxLaneEnds(Lot lot, Vector3 p, List<Vector3> ends)
        {
            ends.Clear();
            foreach (GameObject o in lot.GetObjects<GameObject>())
            {
                PortalComponent pc = o == null ? null : o.PortalComponent;
                if (pc == null) continue;
                uint lanes = Math.Min(pc.GetNumLanes(), 8u);
                for (uint i = 0; i < lanes; i++)
                {
                    Slot[] s = pc.GetSlotsForLaneIndex(i);
                    for (int e = 0; s != null && e < s.Length && e < 2; e++)
                    {
                        Vector3 v = o.GetPositionOfSlot(s[e]);
                        if (PetRelaxCore.Near(p.x, p.y, p.z, v.x, v.y, v.z, kPrxEndSearch)) ends.Add(v);
                    }
                }
            }
        }

        static int PrxNearEnd(List<Vector3> ends, float x, float y, float z, float clear)
        {
            for (int i = 0; i < ends.Count; i++)
                if (PetRelaxCore.Near(x, y, z, ends[i].x, ends[i].y, ends[i].z, clear)) return i;
            return -1;
        }

        // Before the game's floor lie-down: a pet that would lie down where it stands, on a lane end, walks off it first.
        static void PrxBeforeLie(InteractionInstance ia, Sim pet)
        {
            try
            {
                if (ia == null || pet == null || !ia.Autonomous) return;  // the player's own choice of spot stays
                PetSurfacePosture.LayDownHere.Definition def = ia.InteractionDefinition as PetSurfacePosture.LayDownHere.Definition;
                if (def == null || def.LayDownPosition != Vector3.Invalid) return;
                int[] c = PatchCounters();
                c[kPrxLieDowns]++;
                Lot lot = pet.LotCurrent;
                if (lot == null || lot.IsWorldLot) return;  // portals are on lots; the world lot's object list is the whole town
                float clear = Instantiator.kPetRelaxPortalClear;
                Vector3 p = pet.Position;
                PrxLaneEnds(lot, p, sPrxEnds);
                int at = PrxNearEnd(sPrxEnds, p.x, p.y, p.z, clear);
                if (at < 0) return;
                c[kPrxLieAtPortal]++;
                bool on = ShortcutOn("petrelax"), hhOn = ShortcutOn("petrelaxhh");
                if (!PrxCount(PetRelaxCore.LieDown(on, PrxScope(pet, hhOn || !on), true), PetRelaxCore.kKeptNot, c)) return;
                Vector3 end = sPrxEnds[at];
                int plans = 0;
                for (int i = 0; i < PetRelaxCore.kCandidates && plans < kPrxMaxPlans; i++)
                {
                    float x, z;
                    if (!PetRelaxCore.Candidate(i, p.x, p.z, end.x, end.z, out x, out z) || PrxNearEnd(sPrxEnds, x, p.y, z, clear) >= 0) continue;
                    Vector3 spot = new Vector3(x, p.y, z);
                    Route r = pet.CreateRoute();
                    if (!r.IsPointRoutable(spot)) continue;
                    plans++;
                    r.DoRouteFail = false;
                    if (!r.PlanToPoint(spot).Succeeded()) continue;
                    bool ok = pet.DoRoute(r);
                    Vector3 now = pet.Position;
                    if (!ok || PrxNearEnd(sPrxEnds, now.x, now.y, now.z, clear) >= 0) break;  // stopped on the way: the game's lie-down as it was
                    c[kPrxMoved]++;
                    if (IsPlayerSim(pet)) c[kPrxHousehold]++;
                    PrxLog("petrelax: " + pet.FullName + " " + ShlWho(pet) + ", " + SwfIa(pet) + ": walked off a lane end before lying down, from " + V(p) + " to " + V(now));
                    return;
                }
                c[kPrxKeptNoSpot]++;
            }
            catch (ResetException) { throw; }
            catch (Exception e) { PrxError(kPrxSiteLie, e); }
        }

        // After the game's floor lie-down: the pet's jig goes on the list PrxRoute reads.
        static void PrxAfterLie(Sim pet)
        {
            try
            {
                if (pet == null || pet.HasBeenDestroyed || pet.Posture == null) return;
                GameObject jig = pet.Posture.Container as PetSurfacePosture.PetSurfaceFloorJig;
                if (jig == null || jig.HasBeenDestroyed) return;
                for (int i = sPrxJigs.Count - 1; i >= 0; i--)
                {
                    if (object.ReferenceEquals(sPrxJigs[i].Jig, jig)) return;
                    if (sPrxJigs[i].Jig.HasBeenDestroyed || sPrxJigs[i].Pet == pet) sPrxJigs.RemoveAt(i);  // its last jig
                }
                if (sPrxJigs.Count >= kPrxMaxJigs) return;
                PrxJig j = new PrxJig();
                j.Jig = jig; j.Pet = pet;
                j.NearPortal = ShlNearPortal(pet.LotCurrent, pet.Position);
                sPrxJigs.Add(j);
            }
            catch (ResetException) { throw; }
            catch (Exception e) { PrxError(kPrxSiteLieEnd, e); }
        }

        // `this` is the game's PetSurfacePosture.LayDownHere.
        public class PrxLieHook
        {
            public bool RunHook()
            {
                InteractionInstance ia = (object)this as InteractionInstance;
                Sim pet = ia == null ? null : ia.InstanceActor;
                PrxBeforeLie(ia, pet);
                bool r = RunOrig();
                PrxAfterLie(pet);
                return r;
            }
            public bool RunOrig() { return false; }  // receives LayDownHere.Run's original body
        }

        // ---- check ------------------------------------------------------------------------------------------------------

        static string PrxCounters()
        {
            int[] c = PatchCounters();
            return "petrelax: walked through at the obstacle event " + c[kPrxPass] + ", at the obstacle wait " + c[kPrxPassWait] + " (both near a portal), waits ended " + c[kPrxClip]
                + ", destination accepted " + c[kPrxClipDest] + " (this session: of both after the grace " + sPrxGrace + ", waits begun away from a portal and left to the grace " + sPrxDeferred
                + "), routes given the pass through pets lying near a portal " + c[kPrxJigRoutes]
                + ", this session, plans failed on a lying pet: stood the grace " + sPrxPlanWaits + ", planned through " + sPrxPlanWalks + ", cancelled meanwhile " + sPrxPlanStopped
                + ", failed without the pet too " + sPrxPlanStill
                + "; floor lie-downs (autonomous, where the pet stands or its own spot) " + c[kPrxLieDowns] + ", of them at a lane end " + c[kPrxLieAtPortal] + ", walked off it " + c[kPrxMoved]
                + ", kept: no free spot " + c[kPrxKeptNoSpot]
                + "; would " + c[kPrxWould] + ", exempt " + c[kPrxExempt] + "; kept: horse, foal or deer " + c[kPrxKeptHorse] + ", object in the way " + c[kPrxKeptObject]
                + ", route's end (in use, social, player Go Here) " + c[kPrxKeptDest] + "; acts with a household Sim or pet " + c[kPrxHousehold]
                + "; errors " + c[kPrxErrors] + PrxErrorSites() + ", lying pets tracked " + sPrxJigs.Count
                + "; " + GlMembers(sPrxMissing) + (sPrxLast.Length > 0 ? "; last: " + sPrxLast : "");
        }

        static PetRelaxCore.Case PrxCase(bool walkerPet, bool walkerLarge, int humans, int pets, int large, int others)
        {
            PetRelaxCore.Case k = new PetRelaxCore.Case();
            k.WalkerSmallPet = walkerPet; k.WalkerLarge = walkerLarge; k.Humans = humans; k.SmallPets = pets; k.Large = large; k.Others = others;
            k.WalkerInScope = true; k.AllInScope = true;
            return k;
        }

        // PetRelaxCheck run: the rules with controls (read-only, nothing in the world is touched). Bug case: a Sim meets a
        // napping cat in its way: the game waits (the cat cannot be pushed), petrelax walks through. result: the counters
        // and the last act's line. lot: the pets of the active lot, what they do, and the lying ones' distance to a lane end.
        public static object PetRelaxCheck(string what)
        {
            if (what == "result") return PrxCounters();
            if (what == "lot") return PrxLot();
            if (what != "run") return "petrelax <run|result|lot>";
            bool on = ShortcutOn("petrelax");
            int kept;
            bool fixedNow = PetRelaxCore.Pass(on, PrxCase(false, false, 0, 1, 0, 0), out kept) == PetRelaxCore.kGo;
            bool ctlPetWalker = PetRelaxCore.Pass(true, PrxCase(true, false, 2, 1, 0, 0), out kept) == PetRelaxCore.kGo;
            bool ctlNoPet = PetRelaxCore.Pass(true, PrxCase(false, false, 2, 0, 0, 0), out kept) == PetRelaxCore.kNone && kept == PetRelaxCore.kKeptNot;
            bool ctlMixed = PetRelaxCore.Pass(true, PrxCase(false, false, 1, 1, 0, 0), out kept) == PetRelaxCore.kNone && kept == PetRelaxCore.kKeptNot;
            bool ctlHorse = PetRelaxCore.Pass(true, PrxCase(true, false, 0, 0, 1, 0), out kept) == PetRelaxCore.kNone && kept == PetRelaxCore.kKeptHorse;
            bool ctlHorseWalker = PetRelaxCore.Pass(true, PrxCase(false, true, 0, 1, 0, 0), out kept) == PetRelaxCore.kNone && kept == PetRelaxCore.kKeptHorse;
            bool ctlObject = PetRelaxCore.Pass(true, PrxCase(false, false, 0, 1, 0, 1), out kept) == PetRelaxCore.kNone && kept == PetRelaxCore.kKeptObject;
            PetRelaxCore.Case k = PrxCase(false, false, 0, 1, 0, 0);
            k.AllInScope = false;
            bool ctlScope = PetRelaxCore.Pass(true, k, out kept) == PetRelaxCore.kExempt;
            bool ctlLie = PetRelaxCore.LieDown(true, true, true) == PetRelaxCore.kGo && PetRelaxCore.LieDown(true, true, false) == PetRelaxCore.kNone
                && PetRelaxCore.LieDown(true, false, true) == PetRelaxCore.kExempt && PetRelaxCore.LieDown(false, true, true) == PetRelaxCore.kWould;
            float cx, cz;
            bool ctlSpot = PetRelaxCore.Candidate(0, 10f, 10f, 9.5f, 10f, out cx, out cz) && Math.Abs(cx - 12f) < 0.01f && Math.Abs(cz - 10f) < 0.01f
                && !PetRelaxCore.Near(cx, 0f, cz, 9.5f, 0f, 10f, 1.25f) && PetRelaxCore.Near(10f, 0f, 10f, 9.5f, 0f, 10f, 1.25f) && !PetRelaxCore.Near(10f, 3f, 10f, 9.5f, 0f, 10f, 1.25f);
            float grace = HhGraceMin();
            bool ctlTimed = ShareLanesCore.Timed(PetRelaxCore.kGo, true, 0f, 2.5f) == PetRelaxCore.kGo && ShareLanesCore.Timed(PetRelaxCore.kGo, false, 1f, 2.5f) == ShareLanesCore.kDefer
                && ShareLanesCore.Timed(PetRelaxCore.kGo, false, 2.5f, 2.5f) == PetRelaxCore.kGo;
            ctlTimed = ctlTimed && PetRelaxCore.PlanJig(true, true, false, true, true, out kept) == PetRelaxCore.kGo && PetRelaxCore.PlanJig(true, false, false, true, true, out kept) == PetRelaxCore.kNone;
            bool controls = ctlPetWalker && ctlNoPet && ctlMixed && ctlHorse && ctlHorseWalker && ctlObject && ctlScope && ctlLie && ctlSpot && ctlTimed;
            return "petrelax check (switch " + (on ? "on" : "off") + ", household " + (ShortcutOn("petrelaxhh") ? "on" : "off") + ", timer " + Instantiator.kPetRelaxMinutes.ToString("0.0")
                + " sim-min, lane end clearance " + Instantiator.kPetRelaxPortalClear.ToString("0.00") + " m, grace " + grace.ToString("0.0") + " sim-min away from a portal, near = "
                + Instantiator.kClipPortalNear.ToString("0.0") + " m): a Sim with a napping cat in its way walks through=" + fixedNow
                + "; control: a pet walks through Sims and pets=" + ctlPetWalker + ", no pet in the case left alone=" + ctlNoPet + ", Sim with a pet and a Sim in its way left to simpass=" + ctlMixed
                + ", horse in the way kept=" + ctlHorse + ", horse walker kept=" + ctlHorseWalker + ", object in the way kept=" + ctlObject + ", out of scope exempt=" + ctlScope
                + ", at once only near a portal, elsewhere after the grace=" + ctlTimed + ", lie-down rule=" + ctlLie + ", free spot away from the lane end=" + ctlSpot + "; controls ok=" + controls
                + "; verdict: " + GlVerdict(on, fixedNow, controls, sPrxMissing.Length == 0) + "; " + PrxCounters();
        }

        static string PrxLot()
        {
            try
            {
                Lot lot = LotManager.ActiveLot;
                if (lot == null) return "petrelax lot: no active lot";
                System.Text.StringBuilder b = new System.Text.StringBuilder("petrelax lot: ");
                int pets = 0;
                foreach (Sim s in lot.GetObjects<Sim>())
                {
                    if (s == null || s.HasBeenDestroyed || s.SimDescription == null || !(s.IsPet || s.IsRaccoon || s.IsDeer)) continue;
                    pets++;
                    if (b.Length > 1500) continue;
                    Vector3 p = s.Position;
                    b.Append(s.FullName).Append(' ').Append(ShlWho(s)).Append(PrxLarge(s) ? " large" : " small").Append(", ").Append(SwfIa(s)).Append(" at ").Append(V(p));
                    if (s.Posture != null && s.Posture.Container is PetSurfacePosture.PetSurfaceFloorJig)
                    {
                        PrxLaneEnds(lot, p, sPrxEnds);
                        float best = -1f;
                        for (int i = 0; i < sPrxEnds.Count; i++)
                        {
                            float dx = sPrxEnds[i].x - p.x, dz = sPrxEnds[i].z - p.z;
                            float d = (float)Math.Sqrt(dx * dx + dz * dz);
                            if (best < 0f || d < best) best = d;
                        }
                        b.Append(", lies on the floor, nearest lane end ").Append(best < 0f ? "none within " + kPrxEndSearch.ToString("0") + " m" : best.ToString("0.00") + " m");
                    }
                    b.Append("; ");
                }
                return b.Append(pets).Append(" pets").ToString();
            }
            catch (ResetException) { throw; }
            catch (Exception e) { return "petrelax lot: " + e.GetType().Name + ": " + e.Message; }
        }
    }
}
