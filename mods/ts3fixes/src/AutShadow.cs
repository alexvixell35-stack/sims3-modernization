using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.ChildAndTeenUpdates;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.ObjectComponents;
using Sims3.Gameplay.Objects.Miscellaneous;
using Sims3.Gameplay.Pools;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using AutonomyT = Sims3.Gameplay.Autonomy.Autonomy;

namespace Ts3Fixes
{
    // ts3native's [AutShadow] internal calls (docs/122 part 2). ts3native registers them as "Ts3Fixes.AutShadowNative::<Name>"
    // at the first Simulate tick; without ts3native the first call throws MissingMethodException (probed once per world load).
    public static class AutShadowNative
    {
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern int Version();
        // contract 11 (docs/218 §5): SHA-256 of a loaded module's bytes into sha (byte[32]); returns the bytes hashed, 0 = no hash
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern int ImageSha(IntPtr monoImage, byte[] sha);
        // 0 Mode (-1 off/no cut hook, 0 count only, 1 R, 2 W), 1 Sample, 2 WarmIn, 3 DumpOnMismatch, 4 worker ready 1/0
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern int Config(int key);
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern bool IsWorker();
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern void Abort(int reason);
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern int Aborted();
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern void ResetAbort();
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern bool Stopped();
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern bool Begin();
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern void AddRow(ulong objectId, int kind);
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern void Post(int requestId);
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern int LastRun();
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern void SetMethods(IntPtr finish, IntPtr reference, IntPtr work);
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern void SetRefs(string empty, Exception fmtException);
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern void Log(string jsonLine);
        // version 3 (docs/125): a new RNG unit; the worker and the sim-thread reference draw the unit's own stream
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern void RngUnit(int unit);
        // version 4 (docs/125 addendum §4.1): 1 opens, 0 closes a bracket in which the reference draws from a scratch stream
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern void RngPause(int on);
        // version 5 (docs/122 §14.12): RuntimeMethod rotation for a worker shim whose target was transformed before ts3fixes
        // loaded; Mode 1/2 only. 0 done, -1 mode or state, -2 no RuntimeMethod, -3 not transformed, -4 layout mismatch
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern int Redirect(IntPtr target, IntPtr hook, IntPtr stub);
        // version 6 (docs/122 §14.18): Begin joins a posted request's cut (shared cut, up to kMaxReq); Note counts every game
        // B13 call (the go-live rates); ListTypeNext/ListTypeSet classify each list query type once (can a Sim be one?)
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern void Note();
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern object ListTypeNext();
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern void ListTypeSet(object type, int simCapable);
        // version 7 (docs/122 §14.20): typed marks. The list types by native index, the object ids waiting for a class, and
        // each object's class (bit i = it is of list type i, n = types tested; n 0 = no script object yet)
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern int ListTypeCount();
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern object ListTypeAt(int i);
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern int ObjClassTake(ulong[] ids);
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern void ObjClassSet(ulong id, int mask, int n);
        // version 8 (docs/122 §14.21, docs/124 §15): Fresh(1/0) brackets a fresh sim-thread re-score at the finish (script RNG
        // saved and put back, per-unit streams with the cut's seed); Stat(which, add) adds to ts3native's consume-side counters
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern void Fresh(int on);
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern void Stat(int which, int add);
        // version 9 (docs/122 §14.23): Stat 7-11 (answered-only top compare, reference vs fresh top, reference passes among
        // worker-A4 pairs); PairT0() = a timestamp, PairT1(kind, t0) adds the time since t0 to the C2 per-pair timer of kind
        // (0 allow-listed pass, 1 allow-listed fail, 2 off-list pass, 3 off-list fail)
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern int PairT0();
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern void PairT1(int kind, int t0);
        // version 10 (docs/124 §17 go-live, Mode 3): LiveStat adds to a go-live counter slot (AutLive.cs kS*); LiveWatch
        // watches a meta object (isLot 0, object id) or a lot (1, LotId) of a live request; ObjStale answers 1 when it had an
        // event after the cut frame (or is not watched). Config 13-18 settings, 19 frame, 20 epoch, 21 cut epoch, 22 cut frame
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern void LiveStat(int which, int add);
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern void LiveWatch(ulong id, int isLot);
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern int ObjStale(ulong id, int isLot, int cutFrame);
        // 0.36.148, Config(32) == 1 (docs/127 §4.1 H11 shelter answer, worker only, -1 elsewhere): def != null arms the next
        // callvirt of site on def (not made, null result), 1 armed / 0 not; def null disarms and answers the calls skipped
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern int RainArm(object def, IntPtr site);
        // 0.36.148, Config(34) == 1: the last search's request id, feature mask (AutLiveCore.Feat) and top definition for the
        // crash log; Config(33) = the last finished job's native feature bits
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern void SearchMark(int req, int mask, string def);
        // P6, Config(35) == 1 (docs/data/tier2-rows35-handoff.md): the request the worker scores next has a household, selected
        // or unknown actor (1) or not (0); the Query_CountObjectsInRange row answers only at 0. Worker only, no-op elsewhere
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern void ActorScope(int hh);
    }

    // [AutShadow] pilot part 2, managed half (docs/122 §1.2, §3.5, §4, §5, §12.5). Shadow only: the worker's scores are
    // compared, never used. Switch "autshadow" (tuning kAutShadow, default off): off = every hook runs the game's method
    // unchanged. Flow per sampled meta search (B13):
    //   1. sim, B13 prefix (MetaHook): request = actor + the meta objects B13 scores, in its order; rows to ts3native; Post.
    //   2. sim, during the game's own B13: the game's per-pair scores are recorded (CalculateScore wrapper) for the L report.
    //   3. native cut at the end of the tick; Modes 1/2: AutReference() on the sim thread (RNG saved/restored by native),
    //      then AutWork() on the M5 worker in the quiescent window; then AutFinish() on the sim thread: compare, counters, JSONL.
    // Counters: [106-111] A0..A5, [112-115] L pairs / equal / Test agree / top-1 agree, [116] skips, [117] warm-in
    // reference runs, [118] requests posted, [119] Begin busy, [120] mod-guard skips, [121] managed exceptions in the driver,
    // [122] scoring-function skips (G-M2, code sf), [123] closure methods L3-checked by the last sweep (G-M4), [124] its time
    // (us), [125] foreign swaps it found, [126] mod types overriding a closure virtual (G-M5).
    // Abort reasons per pair: AppDomain data ts3fixes.autshadow.reasons (int[23], native reason order; 20 rain, 21 posture,
    // 22 route: a ScriptCore.Route IC on the worker, H12).
    public static partial class Fixes
    {
        const int kMaxObjs = 1024, kMaxPairs = 8192, kMaxLogPerLoad = 200;
        const int kCRainWhy = 607;  // 607-610: `rain` aborts by path (Result.RainWhy)
        const int kCT2Ab = 884, kCT2Drag = 891;  // 884-890 tier-2 P1 aborts by class (Result.T2Ab), 891 drag pre-warm fills
        const int kCT2H = 898;  // 898-902 tier-2 H7-H10 (Result.T2H, AutShadowCore.kT2H*)
        const int kReasonOther = 19;  // native AS_AB_OTHER: P1 aborts (`inuse`, `fire` by class only in 884-890, no native reason)
        const int kReasonExc = 12, kReasonStop = 13, kReasonDesire = 15, kReasonRain = 20, kReasonPosture = 21;

        public sealed class Request
        {
            public int Id = -1, Mode, Objs, Pairs;
            public Sim Actor;
            public bool Hh = true;  // household, selected or unknown actor (sim thread, at the post): the P6 row's gate
            public AutonomyT Aut;
            public CommodityKind C;
            public AutonomySearchType SearchType;
            public InteractionFlags Flags;
            public float Hours;
            public readonly IMetaObject[] Obj = new IMetaObject[kMaxObjs];
            public readonly int[] ObjImpl = new int[kMaxObjs];      // implementor index; -1 not allow-listed, -2 mod guard
            public readonly int[] Start = new int[kMaxObjs + 1];    // first pair of each object; Start[Objs] = Pairs
            public readonly InteractionObjectPair[] Pair = new InteractionObjectPair[kMaxPairs];
            public readonly int[] PairObj = new int[kMaxPairs];
            public readonly int[] Def = new int[kMaxPairs];         // definition index; -1 not allow-listed, -2 mod guard, -3 sf
            public readonly bool[] Acb = new bool[kMaxPairs];       // the pair's tuning has AlwaysChooseBest (docs/124 rule 3 side list)
            public readonly byte[] Shelter = new byte[kMaxObjs];   // H11 shelter bit per meta object (AutShadowCore.kShelter*, docs/127 §4.1)
            // per request (docs/122 §14.18: a shared cut holds several): scored lots and bake sales (Reachable, worker hooks),
            // the pair index and the game's own scores (L report, first request of a batch only)
            public readonly Dictionary<ulong, bool> Lots = new Dictionary<ulong, bool>();
            public readonly List<Vector3> BakeSales = new List<Vector3>();
            public readonly Dictionary<InteractionObjectPair, int> PairIndex = new Dictionary<InteractionObjectPair, int>();
            public readonly float[] GameScore = new float[kMaxPairs];
            public readonly bool[] GameHas = new bool[kMaxPairs];
            public LiveSearch Live;  // Mode 3: the go-live search this request serves (AutLive.cs), else null
            // tier-2 P1 (T2P1Fill, sim thread at the request): the actor's household scope and the hook switches, so the
            // worker reads only these fields. T2Hh defaults to household until filled.
            public bool T2Hh = true, T2InUse, T2Fire;
            // tier-2 H7-H10 (T2HFill, sim thread at the request, after T2P1Fill): switches, the dragged object (H7, cached
            // value if the game's search already filled it, else the scene window's), the actor's cache flag (H8) and tick (H10)
            public bool T2Drag, T2Cache, T2Tuning, T2Lod, T2CacheActive;
            public IGameObject T2DragCached, T2DragTop;
            public long T2Tick;
        }

        public sealed class Result
        {
            public int Id = -1, Exceptions, DesireDriver, RainUsed;  // RainUsed: pairs answered from the cut's shelter bit (feature mask)  // DesireDriver: objects whose first abort was desirefn in the driver's meta score (docs/216 §5)
            public bool Done, Partial;  // Partial: stopped at a window close; Done then means "the finished objects are valid" (KeepPartial)
            public readonly float[] Score = new float[kMaxPairs];
            public readonly int[] Code = new int[kMaxPairs];
            public readonly int[] Reason = new int[kMaxPairs];
            public readonly float[] Meta = new float[kMaxObjs];
            // rain-h11 chain8 triage: `rain` aborts by path (worker-written, summed into counters 607-610 at the finish):
            // 0 arm refused, RainArm 0 (no Satb act span / no site) or not called; 1 arm refused, RainArm -1 (not the worker
            // by ts3native's thread id); 2 hook abort (branch reached unarmed, no bit, or a real instance); 3 done rule
            public readonly int[] RainWhy = new int[4];
            // tier-2 P1 aborts by class (worker-written, summed into counters 884-890 at the finish): 0-5 get_InUse overrides
            // (AutShadowCore.kT2InUseTypes), 6 fire
            public readonly int[] T2Ab = new int[AutShadowCore.kT2N];
            // tier-2 H7-H10 (worker-written, summed into counters 898-902 at the finish; AutShadowCore.kT2H* order)
            public readonly int[] T2H = new int[AutShadowCore.kT2HN];
        }

        // Rooted requests and results (docs/122 §4.2), allocated at startup on the sim thread and reused. A shared cut (docs/122
        // §14.18, kMaxReq = ts3native AS_BATCH_MAX) serves sReqN requests; sReq/sRef/sWork point at the one being built,
        // scored or compared (the worker hooks read sReq).
        const int kMaxReq = 4;
        static readonly Request[] sReqs = { new Request(), new Request(), new Request(), new Request() };
        static readonly Result[] sRefs = { new Result(), new Result(), new Result(), new Result() };
        static readonly Result[] sWorks = { new Result(), new Result(), new Result(), new Result() };
        static Request sReq = sReqs[0];
        static Result sRef = sRefs[0], sWork = sWorks[0];
        static int sReqN;
        static readonly string sEmpty = "";
        static readonly Exception sFmtException = new FormatException("autshadow");
        static readonly int[] sReasons = new int[23];

        // State. sPosted: a request is with ts3native until AutFinish. sInWork: the worker is inside AutWork (worker hooks gate).
        // sInRef: the sim thread is inside AutReference (hooked calls draw from a scratch stream, docs/125 addendum §4.1).
        static bool sPosted, sInWork, sInRef, sSessionOff, sHooksOk, sCalcPatched, sWorkReady, sGameDone;
        static AutonomyT sGameAut;           // the autonomy whose B13 is being recorded (sim thread)
        static int sProbe;                   // per load: 0 not probed, 1 on, -1 off
        static int sNextId, sCalls, sSample = 4, sWarmIn, sWarmInMax = 20, sLogged, sGardening, sFreshEvery;
        static bool sKeepPartial;
        static int sForceJitBytes = -1;

        // Allow-list (docs/data/autonomy-allow.csv 'in' rows; assembly 0 = Sims3GameplaySystems, 1 = Sims3GameplayObjects).
        static readonly string[] kDefNames = {
            "Sims3.Gameplay.Core.VisitCommunityLot+Definition", "Sims3.Gameplay.Core.VisitLot+Definition",
            "Sims3.Gameplay.Autonomy.GoToGarden+Definition", "Sims3.Gameplay.Core.GoToCommunityLot+Definition",
            "Sims3.Gameplay.Core.VisitCommunityLot+DateDefinition", "Sims3.Gameplay.Core.VisitLotWithDate+Definition",
            "Sims3.Gameplay.Abstracts.ShoppingRabbitHole+AutonomouslyShopInRabbitHole+Definition",
            "Sims3.Gameplay.Abstracts.ShowVenue+AutonomouslyAttendShow+Definition",
            "Sims3.Gameplay.Objects.RabbitHoles.AutonomouslyGoForMealInRestaurant+Definition",
            "Sims3.Gameplay.Objects.RabbitHoles.CityHall+AutonomouslyGoToCityHall+Definition",
            "Sims3.Gameplay.Objects.RabbitHoles.Hospital+AutonomouslyVisitHospital+Definition",
            "Sims3.Gameplay.Objects.RabbitHoles.Stadium+AutonomouslyAttendGame+Definition" };
        static readonly int[] kDefAsm = { 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1 };
        // G-M1 (docs/128 §5): accepted (Sims3GameplaySystems, Sims3GameplayObjects) MVID pairs, raw metadata hex. Each pair
        // goes on only after the docs/128 §7 re-run gave the same verdicts on its assemblies. 0 EA (Steam 1.67), 1 simler90
        // Gameplay Core Mod UPDATE242 (Steam; docs/128 §2.1: same verdicts for the pilot union and all 251 tier-2 rows).
        static readonly string[][] kMvidSets = {
            new string[] { "3681889397f4aa44b399a2926b2454fc", "b2cc44b06fee39449dab2fb6b07e9995" },
            new string[] { "bb8cb38b0d83b44490103053df82374c", "a95a143fe9314a44805eb847548ffe7f" } };
        // ScriptCore: EA, NRaas ErrorTrap (15 changed methods, none in a closure: docs/128 §2.2). Not a session gate: an
        // unknown ScriptCore is logged and covered only by the per-method L3 of the closure sweep (docs/132 §3, G-M4).
        static readonly string[] kScriptCoreMvids = { "ee57f83a5f56384f9ede12b34a086c39", "99b5bd68c981aa42a5f220f2cf7ae4d2" };
        // docs/218 §5 (round 7 task 2): the accept list by content, SHA-256 of each module's bytes (the S3SA image as Mono
        // holds it). An MVID is not content: tiny-ui-fix keeps the MVIDs of the modules it rewrites (docs/218 §1).
        // G-M1 pairs (Sims3GameplaySystems, Sims3GameplayObjects), same builds and order as kMvidSets.
        static readonly string[][] kShaSets = {
            new string[] { "1e323c53399b18cc1fadb1b4a737534904f5eefd97c4b75cdff31dbfe8bb9ac5", "ac78fb17e0f63092948622d4e7f17423f1df69d6215f605a0e7732e24edae2e2" },
            new string[] { "77e0cdab38dd74a81ff5bcfb48bb59e2c93b8a22670d0d5644ccec082ad32882", "e65f65c8773f936b3307410633706c3d28adec74da2eafff1eb2a27aed60b4e1" } };
        // the other game modules: EA (Steam 1.67) ScriptCore, SimIFace, Sims3Metadata, Sims3StoreObjects, UI; NRaas ErrorTrap
        // ScriptCore (VersionStamp 100, MVID 99b5bd68...)
        static readonly string[] kShaOther = { "d819df03e3e6d28c5c79334f7a69d92cef80fe6a98411d1f98f21b6169677f68", "f810e3c2e8bed69c70eb8467b0e579fac7e06034d6002acab845a03c309922e8",
            "da2711bff553038886199e97e2f6a25477521c34b42169465c92d9908f21b7c3", "8ff310620877086e42055b080c85d6141ed1f0f484addb05bb58e532ab8c85ab",
            "212c119ecb7e57d72e4340975fd2cc17e6a6c9ae06d4a4e6aa9a63bdfe90c1f5", "03389aa74092c0c12d04930dce8698bbdf59418dffc4bbacaca4d4f44e18463d" };
        // Game script modules: their types are not part of the G-M5 mod census (mod copies of them are MVID-gated above).
        static readonly string[] kGameModules = { "mscorlib", "System", "System.Xml", "ScriptCore", "SimIFace", "Sims3GameplaySystems",
            "Sims3GameplayObjects", "Sims3Metadata", "Sims3StoreObjects", "UI" };
        // G-M2: scoring functions whose closures are ready (docs/128 §3; docs/126 sampled), as "Declaring.Type::Method".
        // Accepted only from a game module (EA or a G-M1 pair) and when the method passes L3.
        static readonly string[] kSfAllow = {
            "Sims3.Gameplay.Objects.Electronics.TV+WatchTVAutonomously::WatchTVScoringFunction",
            "Sims3.Store.Objects.MovieScreen+WatchMovieAutonomously::WatchTVScoringFunction",
            "Sims3.Gameplay.Core.LotManager::ScoreTeenHangoutForRabbitHole",
            "Sims3.Gameplay.ActorSystems.Children.ScoringFunctionsForBabiesAndToddlers::PlayWithMultiplier",
            "Sims3.Gameplay.Objects.Seating.HighChair::GetChildOutOfChairModifier",
            "Sims3.Gameplay.Abstracts.Windows+LookOutWindow+Definition::LookOutWindowFunScoring",
            "Sims3.Gameplay.ObjectComponents.CleanableComponent::CleaningScoringFunction",
            "Sims3.Gameplay.ActorSystems.PetSurfacePosture+LayDownHere::LayDownScoringFunction",
            "Sims3.Gameplay.Objects.CookingObjects.SitHereForEating::EatHereScoringFunction",
            "Sims3.Gameplay.Abstracts.GameObject::SitInteractionModifier", "Sims3.Gameplay.Objects.Plumbing.Sink::GetBrushTeethScore" };
        static int sMvidSet = -1;            // accepted pair this session
        // docs/218 §5: every game module hashed (else the MVID gate decides, as before); modules not accepted by content
        static bool sModulesChecked, sContentOk;
        static readonly Dictionary<Assembly, bool> sUnknownMods = new Dictionary<Assembly, bool>();
        static FieldInfo sModuleImpl;
        static readonly Dictionary<IntPtr, Type> sPatched = new Dictionary<IntPtr, Type>();  // our own swaps (Fixes.Patch), by MonoMethod*
        static bool[] sImplGuardHit = new bool[7];  // G-M5: an implementor closure dispatches through an overridden slot
        static readonly Type[] kImpl = { typeof(Lot), typeof(RabbitHole), typeof(HomeMetaObject), typeof(WorkMetaObject),
                                         typeof(GardeningMetaObject), typeof(WorldPondHelper), typeof(BakeSaleTable) };
        const int kImplRabbitHole = 1;
        static readonly Dictionary<Type, int> sDefIndex = new Dictionary<Type, int>();   // sim thread only
        static readonly Dictionary<Type, int> sImplVerdict = new Dictionary<Type, int>(); // sim thread only
        static bool[] sDefGuardHit = new bool[12];
        static bool sSharedGuardHit;

        // Private game members, read on the sim thread (request) and, for reference fields only, by the worker shims.
        static FieldInfo fLotsKnown, fAllowedRooms, fRoomCallbacks, fDatingRecords, fAcceptedDates, fPostureList;
        static List<InteractionObjectPair> sNoPostureList;  // the worker's answer when it aborts on an unfilled posture list
        static MethodInfo mCanAnyMeta, mCanFullMeta;

        // Localization.LocalizeString shims, installed first in Fixes.Apply (after the TaskCount hook): anything that localizes
        // a string before ApplyAutShadow compiles them, and Patch refuses a compiled method (0.36.106: the (String,Object[])
        // and (Boolean,String,Object[]) overloads were refused in every job, so Modes 1/2 stayed off). Inert until the
        // switch is on: the hooks call the original unless on the worker or inside a reference run.
        static readonly List<string> sLocFailed = new List<string>();
        static bool sLocOk, sShimsOk, sWarmOk;
        static readonly Type[][] kLocSigs = { new Type[] { typeof(ulong) }, new Type[] { typeof(string), typeof(object[]) },
            new Type[] { typeof(bool), typeof(string), typeof(object[]) }, new Type[] { typeof(bool[]), typeof(string), typeof(object[]) } };
        static readonly string[] kLocHooks = { "KeyHook", "StrHook", "FemHook", "FemsHook" }, kLocStubs = { "KeyOrig", "StrOrig", "FemOrig", "FemsOrig" };
        static readonly bool[] sLocPatched = new bool[4];
        internal static void ApplyAutShadowLocHooks()
        {
            sLocOk = true;
            for (int i = 0; i < 4; i++)
                sLocOk &= sLocPatched[i] = Hook(sLocFailed, typeof(Localization), "LocalizeString", kLocSigs[i], typeof(AutLocHook), kLocHooks[i], kLocStubs[i]);
            if (!sLocOk) AppliedLog().Add("AutShadow: LocalizeString compiled before ts3fixes loaded (another mod or the game localized first)");
        }

        // docs/122 §14.12: an overload mint transformed before ts3fixes loaded cannot take the header swap (its callers are
        // bound to its RuntimeMethod). With Mode 1/2 only, ts3native rotates the RuntimeMethod bodies (target -> stub,
        // hook -> target) once per session. Hook and stub are transformed first by one call (off the worker the hook calls the
        // stub, whose own body returns null); a target not transformed yet (-3) is called once with an empty key.
        static void LocRedirect()
        {
            if (sLocOk || AutShadowNative.Config(0) < 1) return;
            const BindingFlags all = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            object[] none = new object[0];
            object[][] args = { new object[] { 0UL }, new object[] { "", none }, new object[] { false, "", none }, new object[] { new bool[1], "", none } };
            bool ok = true;
            for (int i = 0; i < 4; i++)
            {
                if (sLocPatched[i]) continue;
                MethodInfo t = typeof(Localization).GetMethod("LocalizeString", all, null, kLocSigs[i], null);
                MethodInfo h = typeof(AutLocHook).GetMethod(kLocHooks[i], all), st = typeof(AutLocHook).GetMethod(kLocStubs[i], all);
                h.Invoke(null, args[i]);
                int r = AutShadowNative.Redirect(t.MethodHandle.Value, h.MethodHandle.Value, st.MethodHandle.Value);
                if (r == -3) { t.Invoke(null, args[i]); r = AutShadowNative.Redirect(t.MethodHandle.Value, h.MethodHandle.Value, st.MethodHandle.Value); }
                sLocPatched[i] = r == 0;
                ok &= r == 0;
                string sig = "";
                foreach (Type a in kLocSigs[i]) sig += (sig.Length > 0 ? "," : "") + a.Name;
                AppliedLog().Add("AutShadow: Localization.LocalizeString(" + sig + ") " + (r == 0 ? "redirected (RuntimeMethod)" : "redirect refused " + r));
            }
            sLocOk = ok;
            sHooksOk = sShimsOk && sLocOk && sCalcPatched;
        }

        static void ApplyAutShadow()
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                fLotsKnown = typeof(AutonomyT).GetField("mLotsKnownAbout", all);
                fAllowedRooms = typeof(Sim).GetField("mAllowedRooms", all);
                fRoomCallbacks = typeof(Lot).GetField("mRoomRestrictionCheckCallbacks", all);
                fDatingRecords = typeof(OnlineDatingManager).GetField("mActiveRecords", all);
                Type rec = typeof(OnlineDatingManager).GetNestedType("OnlineDatingRecord", BindingFlags.NonPublic);
                fAcceptedDates = rec != null ? rec.GetField("mAcceptedDates", all) : null;
                mCanAnyMeta = typeof(AutonomyT).GetMethod("CanRunAnyMetaAutonomy", all);
                mCanFullMeta = typeof(AutonomyT).GetMethod("CanRunFullMetaAutonomy", all);
                fPostureList = typeof(Sims3.Gameplay.ActorSystems.Posture).GetField("mInteractions", all);
                sNoPostureList = new List<InteractionObjectPair>();
                if (fLotsKnown == null || fAllowedRooms == null || fRoomCallbacks == null || fDatingRecords == null
                    || fAcceptedDates == null || mCanAnyMeta == null || mCanFullMeta == null || fPostureList == null)
                    throw new MissingMemberException("private member not found");
            }
            catch (Exception e) { AppliedLog().Add("AutShadow: off: " + e.Message); sSessionOff = true; return; }
            // Worker shims: Modes 1/2 need every one (TryRequest refuses them otherwise); Mode 0 (count only) runs no managed
            // code off the sim thread and needs none. docs/51 §188: one failed shim used to keep MetaHook out as well, which
            // silenced the requests and the ledger scope with no line saying which patch failed.
            List<string> failed = new List<string>(sLocFailed);
            bool ok = Hook(failed, typeof(Sim), "IsAllowedInRoom", new Type[] { typeof(ulong), typeof(int) }, typeof(AutRoomHook), "AllowedHook", "AllowedOrig")
                & Hook(failed, typeof(Sim), "get_NumSimsDoingGardening", null, typeof(AutGardenHook), "GardenHook", "GardenOrig")
                & Hook(failed, typeof(OnlineDatingManager), "DidSimsMeetOnline", new Type[] { typeof(ulong), typeof(ulong) }, typeof(AutDatingHook), "MetHook", "MetOrig")
                & Hook(failed, typeof(MetaAutonomyTuning), "GetSimCountForVenueWithCustomDesireFunction", null, typeof(AutDesireHook), "DesireHook", "DesireOrig")
                & Hook(failed, typeof(Punishment), "GetPunishmentManagerFromSim", new Type[] { typeof(Sim) }, typeof(AutPunishHook), "PunishHook", "PunishOrig")
                & Hook(failed, typeof(Sims3.Gameplay.ActorSystems.Posture), "get_Interactions", null, typeof(AutPostureHook), "ListHook", "ListOrig")
                & Hook(failed, typeof(SimRoutingComponent), "CreateRoute", Type.EmptyTypes, typeof(AutRouteHook), "RouteHook", "RouteOrig")
                & Hook(failed, typeof(SimRoutingComponent), "CreateRoute", new Type[] { typeof(Sims3.SimIFace.CAS.CASAgeGenderFlags) }, typeof(AutRouteHook), "AgsHook", "AgsOrig")
                & Hook(failed, typeof(SimRoutingComponent), "CreateRouteAsAdult", null, typeof(AutRouteHook), "AdultHook", "AdultOrig");
            sCalcPatched = Hook(failed, typeof(AutonomyT), "CalculateScore", new Type[] { typeof(InteractionObjectPair) }, typeof(AutScoreHook), "ScoreHook", "ScoreOrig");
            // H11 shelter answer (docs/127 §4.1): optional; without it rain pairs abort `rain` as before
            sShelterHooked = Hook(failed, typeof(Sims3.Gameplay.Seasons.SeasonsManager), "IsShelteredFromPrecipitation",
                new Type[] { typeof(IGameObject), typeof(InteractionInstance) }, typeof(AutShelterHook), "ShelterHook", "ShelterOrig");
            T2P1Hooks(failed);
            T2HHooks(failed);
            MethodInfo site = typeof(InteractionDefinition).GetMethod("CreateInstance",
                new Type[] { typeof(IGameObject), typeof(IActor), typeof(InteractionPriority), typeof(bool), typeof(bool) });
            sCreateSite = site != null ? site.MethodHandle.Value : IntPtr.Zero;
            sShimsOk = ok;
            sHooksOk = ok && sLocOk && sCalcPatched;
            // B13: requests (any mode) and the ledger scope (autledger) both enter here; it passes through while both are off
            if (!Hook(failed, typeof(AutonomyT), "ScoreInteractionsForMetaAutonomy", null, typeof(AutMetaHook), "MetaHook", "MetaOrig"))
                AppliedLog().Add("AutShadow: B13 hook (Autonomy.ScoreInteractionsForMetaAutonomy) not patched: no requests, no ledger scopes");
            // tier-2 L0 local-phase probe (AutLocalProbe.cs, switch autt2l0): B7, passes through while the switch is off
            Hook(failed, typeof(AutonomyT), "ScoreInteractionsOnObjects", new Type[] { typeof(CommodityKind), typeof(bool) }, typeof(AutLocalHook), "B7Hook", "B7Orig");
            if (failed.Count > 0)
                AppliedLog().Add("AutShadow: not patched: " + string.Join(", ", failed.ToArray()) + (sHooksOk ? "" : "; Modes 1/2 off, Mode 0 (count only) still runs"));
            AppDomain.CurrentDomain.SetData("ts3fixes.autshadow.reasons", sReasons);
            // the module check (docs/218 §5) runs at the first GuardSweep: ImageSha is a ts3native internal call, registered
            // at the first Simulate tick, after this
        }

        // docs/218 §5 (round 7 task 2): game modules accepted by content. Every module hashed: none turns the session off;
        // a module not on the list is checked per definition by G-M6 (closure methods and their class-hierarchy overrides,
        // ClosureSweep). A module that cannot be hashed (no ImageSha, an unreadable image): the MVID gate as before.
        static void ModuleCheck()
        {
            int[] c = PatchCounters();
            sUnknownMods.Clear();
            sContentOk = false;
            List<Assembly> mods = new List<Assembly>();
            List<string> names = new List<string>(), shas = new List<string>();
            string why = null;
            foreach (string n in kGameModules)
            {
                if (n == "mscorlib" || n == "System" || n == "System.Xml") continue;  // not in a closure list (G-M4 leaves BCL out)
                Assembly a = GameAssembly(n);
                if (a == null) continue;
                string sha = null;
                try { sha = ModuleSha(a); }
                catch (Exception e) { if (why == null) why = n + ": " + e.GetType().Name + " " + e.Message; }
                if (sha == null && why == null) why = n + ": image not readable";
                mods.Add(a); names.Add(n); shas.Add(sha);
            }
            int set = -1;
            bool[] acc = why == null ? AutShadowCore.ContentAccepted(names.ToArray(), shas.ToArray(), kShaSets, kShaOther, out set) : null;
            if (acc == null)
            {
                c[2022] = -1;
                AppliedLog().Add("AutShadow: module content not checked (" + (why ?? "no game modules") + "): MVID gate");
                MvidCheck();
                return;
            }
            sContentOk = true;
            sMvidSet = set;
            List<string> unk = new List<string>();
            for (int i = 0; i < mods.Count; i++)
                if (!acc[i]) { sUnknownMods[mods[i]] = true; unk.Add(names[i] + " " + shas[i].Substring(0, 16)); }
            c[2022] = mods.Count; c[2023] = unk.Count;
            if (set >= 0) AppliedLog().Add("AutShadow: game assemblies = accepted set " + set + " (by content)");
            if (unk.Count > 0)
                AppliedLog().Add("AutShadow: game modules not on the content accept list: " + string.Join(", ", unk.ToArray())
                    + ": G-M6 checks their closure and hierarchy methods per definition (docs/218 §5)");
        }

        // MonoImage* of a loaded module: Mono's Module._impl [inferred, Mono 2.x layout]; ImageSha checks "MZ" at its raw data
        static string ModuleSha(Assembly a)
        {
            if (sModuleImpl == null) sModuleImpl = typeof(Module).GetField("_impl", BindingFlags.Instance | BindingFlags.NonPublic);
            if (sModuleImpl == null) return null;
            object v = sModuleImpl.GetValue(a.ManifestModule);
            if (!(v is IntPtr) || (IntPtr)v == IntPtr.Zero) return null;
            byte[] sha = new byte[32];
            return AutShadowNative.ImageSha((IntPtr)v, sha) > 0 ? AutShadowCore.Hex(sha) : null;
        }

        // docs/122 §4.1 step 4: game script assemblies must be the ones the allow-list was built from, else off for the session.
        // G-M1: the pair must match one accepted set as a whole (no mixing an EA SGS with a mod SGO). Since docs/218 §5 only
        // when the modules cannot be hashed (ModuleCheck).
        static void MvidCheck()
        {
            // the game's mscorlib has no Guid.ToByteArray: compare in "N" format
            string sgs = Mvid(typeof(Lot).Assembly), sgo = Mvid(typeof(BakeSaleTable).Assembly);
            sMvidSet = -1;
            for (int s = 0; s < kMvidSets.Length && sMvidSet < 0; s++)
                if (sgs == AutShadowCore.GuidNFromRaw(kMvidSets[s][0]) && sgo == AutShadowCore.GuidNFromRaw(kMvidSets[s][1])) sMvidSet = s;
            if (sMvidSet < 0)
            {
                sSessionOff = true;
                AppliedLog().Add("AutShadow: off for the session: Sims3GameplaySystems/Sims3GameplayObjects MVIDs " + sgs + "/" + sgo + " not on the accept list");
            }
            else if (sMvidSet > 0) AppliedLog().Add("AutShadow: game assemblies = accepted set " + sMvidSet);
            Assembly core = GameAssembly("ScriptCore");
            string sc = core != null ? Mvid(core) : "(not loaded)";
            bool scOk = false;
            foreach (string raw in kScriptCoreMvids) scOk |= sc == AutShadowCore.GuidNFromRaw(raw);
            if (!scOk) AppliedLog().Add("AutShadow: ScriptCore MVID " + sc + " unknown: per-method L3 only (G-M4 sweep)");
        }

        // Fixes.Patch, plus the exact overload in `failed` when it is refused (its own line names only Type.Method)
        static bool Hook(List<string> failed, Type target, string method, Type[] args, Type hook, string replacement, string stub)
        {
            if (Patch(target, method, args, hook, replacement, stub)) return true;
            string sig = "";
            if (args != null) foreach (Type a in args) sig += (sig.Length > 0 ? "," : "") + a.Name;
            failed.Add(TypeName(target) + "." + method + "(" + (args == null ? "" : sig) + ")");
            return false;
        }

        static string Mvid(Assembly a) { return a.ManifestModule.ModuleVersionId.ToString("N"); }
        static Assembly GameAssembly(string name)
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies()) if (a.GetName().Name == name) return a;
            return null;
        }

        // ---- mono ledger probe (docs/51 I-121-5/7/9/12, I-119-44; ts3native [AutShadow] Ledger=1, autledger.cpp) ----
        // Switch "autledger" (no tuning, default off; test jobs turn it on with profile.fixesOn). Each game B13 is one meta
        // scope: Config(5) before the game's method, Config(6) after it; ts3native counts the watched ICs by task inside it.
        // Once per load, one task marks the native tick count around 64 Simulator.Sleep(0) and 64 Sleep(1) calls.
        static int sLedger; // per load: 0 not probed, 1 on, -1 off

        static bool LedgerOpen()
        {
            if (sLedger < 0 || !ShortcutOn("autledger")) return false;
            try
            {
                if (sLedger == 0)
                {
                    sLedger = AutShadowNative.Config(7) == 1 ? 1 : -1;
                    if (sLedger < 0) { AppliedLog().Add("AutLedger: ts3native Ledger=0 this load (off)"); return false; }
                    Simulator.AddObject(new LoopTask(SleepProbe));
                }
                return AutShadowNative.Config(5) == 1;
            }
            catch (Exception) { sLedger = -1; AppliedLog().Add("AutLedger: no ts3native internal calls this load (off)"); return false; }
        }

        static void LedgerClose()
        {
            try { AutShadowNative.Config(6); } catch (Exception) { }
        }

        static void SleepProbe()
        {
            for (int i = 0; i <= 64; i++) { AutShadowNative.Config(9); if (i < 64) Simulator.Sleep(0); }
            for (int i = 0; i <= 64; i++) { AutShadowNative.Config(10); if (i < 64) Simulator.Sleep(1); }
        }

        // World load finished (Instantiator): per-load state; ts3native is probed again at the next sampled search. With the
        // switch on, the mod guard sweep (G-M4 L3 over ~690 methods, ~65-110 ms, docs/128 §5.1) runs here, under the load,
        // instead of as a hitch inside the first sampled search; Ready() sweeps only if this did not (switch turned on later).
        internal static void AutShadowWorldLoad()
        {
            sLedger = 0;
            sProbe = 0; sPosted = false; sInWork = false; sGameAut = null; sWarmIn = 0; sLogged = 0; sWorkReady = sGameDone = false; sReqN = 0;
            sSweptAtLoad = false;
            sLiveCur = null;  // docs/124 §17: searches parked across the load never resume
            foreach (LiveSearch L in sLive) if (L != null && L.InUse) LiveRelease(L);
            if (sSessionOff || !ShortcutOn("autshadow")) return;
            try { sSweptAtLoad = GuardSweep(); }
            catch (Exception e) { AutNote("load sweep: " + e.Message); }
        }
        static bool sSweptAtLoad;
        static bool sReqLotRows;
        static bool sActorScope;  // ts3native has ActorScope (Config(35)); else its P6 row stays shut for every actor

        // docs/124 §17: B14, the go-live consumer (passes through unless a Mode 3 search of that autonomy is in its game B13).
        // kAutLive packages only (default True since O6): the pass-through costs one interpreter frame under every meta B14 (docs/130 §11 budget).
        // Patched by Fixes.ApplyTuned (app startup or the first world load), not in Fixes.Apply: Apply runs in Instantiator's
        // static constructor, which the tuning loader triggers before it sets the [Tunable] fields, so a tuning read there is
        // always the C# default (golive-g7 2026-10-30: "B14 hook missing" with the kAutLive package). No meta search has run
        // before the first load finishes [inferred: autonomy runs in the world]; a refusal ("already compiled") is logged and
        // go-live stays off.
        static void ApplyAutLiveB14()
        {
            if (!Instantiator.kAutLive) return;
            sB14Ok = Patch(typeof(AutonomyT), "ScoreInteractionsForMetaObject", new Type[] { typeof(IMetaObject), typeof(CommodityKind) },
                typeof(AutB14Hook), "B14Hook", "B14Orig");
            if (!sB14Ok) AppliedLog().Add("AutShadow: go-live B14 hook not patched once the tuning was set: go-live off for the session");
            TunedLog("kAutLive", sB14Ok, "go-live B14 hook");
            // docs/124 §8 G8 counters (AutLive.cs AutPushHook): pass-through on the push decision, both autlive arms
            bool push = Patch(typeof(AutonomyT), "ConsiderPerformingAutonomousAction", new Type[] { typeof(InteractionInstance) },
                typeof(AutPushHook), "PushHook", "PushOrig");
            TunedLog("kAutLive", push, "G8 push counter hook");
            // docs/222 §11.8 enqueue tick for the queue-wait counters
            bool enq = Patch(typeof(Sims3.Gameplay.Autonomy.AutonomyManager), "Add", new Type[] { typeof(AutonomyT) }, typeof(AutQueueHook), "AddHook", "AddOrig");
            TunedLog("kAutLive", enq, "G8 queue wait hook");
            // docs/222 unseen-direct: D2 hook (pass-through unless the autonomy's meta search was direct; switch autunseen)
            sChooseOk = sB14Ok && Patch(typeof(AutonomyT), "AssignProbabilitiesAndChooseInteraction", Type.EmptyTypes,
                typeof(AutChooseHook), "ChooseHook", "ChooseOrig");
            TunedLog("kAutLive", sChooseOk, "unseen-direct D2 hook");
        }

        // ---- probe, warm-up, mod guard (sim thread) ----

        static bool Ready()
        {
            if (sProbe != 0) return sProbe > 0;
            sProbe = -1;
            int v;
            try { v = AutShadowNative.Version(); }
            catch (Exception) { AppliedLog().Add("AutShadow: no ts3native internal calls this load (off)"); return false; }
            if (v != 11) { AppliedLog().Add("AutShadow: ts3native contract version " + v + ", want 11 (off)"); return false; }
            try
            {
                if (!sSweptAtLoad && !GuardSweep()) return false;
                // Warm-up: nothing is pending, so each returns at once; it transforms them on the sim thread first.
                AutFinish(); AutReference(); AutWork();
                ColdWarm();
                sSample = Math.Max(1, AutShadowNative.Config(1));
                sFreshEvery = AutShadowNative.Config(11);
                sKeepPartial = AutShadowNative.Config(12) != 0;
                sWarmInMax = AutShadowNative.Config(2);
                sReqLotRows = AutShadowNative.Config(31) == 1;  // ts3native 0.36.147+: AddRow kind 3 (older: would read it as a Sim)
                // ts3native 0.36.148+ with Satb=2 (act): rain pairs answered from the cut's shelter bit (older or Satb off: abort)
                sRainAnswer = sShelterHooked && sCreateSite != IntPtr.Zero && AutShadowNative.Config(32) == 1;
                AppliedLog().Add("AutShadow: H11 shelter answer " + (sRainAnswer ? "on" : "off (rain pairs abort)"));
                sFeatNative = AutShadowNative.Config(34) == 1;  // 0.36.148+: Config(33) job bits and SearchMark
                sActorScope = AutShadowNative.Config(35) == 1;
                if (sActorScope) AutShadowNative.ActorScope(1);  // sim thread: a no-op that resolves the icall before the worker needs it
                AutShadowNative.SetRefs(sEmpty, sFmtException);
                const BindingFlags st = BindingFlags.Static | BindingFlags.NonPublic;
                AutShadowNative.SetMethods(typeof(Fixes).GetMethod("AutFinish", st).MethodHandle.Value,
                    typeof(Fixes).GetMethod("AutReference", st).MethodHandle.Value, typeof(Fixes).GetMethod("AutWork", st).MethodHandle.Value);
            }
            catch (Exception e) { AppliedLog().Add("AutShadow: warm-up failed: " + e.Message); return false; }
            // Worker shims (Modes 1/2 only): a failed warm-up keeps those modes off for the load, Mode 0 still counts
            // (mono-cost3: the dating warm-up threw and no request was ever posted, docs/122 §14.12).
            sWarmOk = WarmShim("room", AutRoomHook.Warm) & WarmShim("dating", AutDatingHook.Warm) & WarmShim("route", AutRouteHook.Warm)
                & WarmShim("rain", delegate { sRain.TargetIsSim(); AutShadowCore.RainShelter(false, sRain, 0, 0); if (Sims3.Gameplay.Seasons.SeasonsManager.Enabled) sRain.Weather(); });
            try { LiveReady(); }
            catch (Exception e) { AppliedLog().Add("AutShadow: go-live setup failed: " + e.Message); sLiveMode = false; }
            try { LocRedirect(); }
            catch (Exception e) { AppliedLog().Add("AutShadow: LocalizeString redirect failed: " + e.Message); }
            sProbe = 1;
            return true;
        }
        delegate void WarmFn();
        static bool WarmShim(string name, WarmFn f)
        {
            try { f(); return true; }
            catch (Exception e) { AppliedLog().Add("AutShadow: warm-up of " + name + " failed: " + e.Message + "; Modes 1/2 off, Mode 0 (count only) still runs"); return false; }
        }

        // docs/104 L3: MonoMethod+0x08 (klass) equals the declaring class, read as MonoType.data.klass from TypeHandle.Value
        // [inferred layout; checked below against our own method and our own patch before any verdict is trusted].
        // MethodInfo.DeclaringType comes from MonoMethod.klass itself (IL: MonoMethod.get_DeclaringType calls the
        // MonoMethodInfo.get_method_info internal call [verified]; it fills the parent from method->klass [inferred, Mono
        // 1.2 source]), so after a header swap DeclaringType names the swapped-in class and the klass compare alone always
        // passed: the self-check's patched CalculateScore looked unswapped (0.36.106, every shadow job off, docs/67). A
        // method is also required to be one of its declaring class's own MonoMethods: a swapped header points klass at a
        // class whose method table does not hold this MonoMethod.
        // shortcut: methods of generic classes pass unchecked (an inflated method's klass is the inflated class); add an
        // inflated-class compare if a generic base's Test ever matters.
        static bool L3Ok(MethodBase m)
        {
            Type d = m.DeclaringType;
            if (d == null || d.IsGenericType) return true;
            return KlassOf(m) == Marshal.ReadIntPtr(d.TypeHandle.Value) && OwnHandles(d).ContainsKey(m.MethodHandle.Value);
        }
        static readonly Dictionary<Type, Dictionary<IntPtr, bool>> sOwnHandles = new Dictionary<Type, Dictionary<IntPtr, bool>>();
        static Dictionary<IntPtr, bool> OwnHandles(Type d)
        {
            Dictionary<IntPtr, bool> h;
            if (sOwnHandles.TryGetValue(d, out h)) return h;
            const BindingFlags own = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            h = new Dictionary<IntPtr, bool>();
            foreach (MethodInfo x in d.GetMethods(own)) h[x.MethodHandle.Value] = true;
            foreach (ConstructorInfo x in d.GetConstructors(own)) h[x.MethodHandle.Value] = true;
            sOwnHandles[d] = h;
            return h;
        }
        static IntPtr KlassOf(MethodBase m) { return Marshal.ReadIntPtr(new IntPtr(m.MethodHandle.Value.ToInt32() + 8)); }

        // names[i] with parameter types sigs[i], resolved exactly (docs/124 §13.5): another overload with the same name is
        // not covered, so our own hook on Autonomy.CalculateScore(InteractionObjectPair) never vouches for its sibling.
        // A signature t does not have counts as a guard hit (a wrong list must not pass silently).
        // our own swap (Fixes.Patch recorded it in sPatched, and the method's class is still our hook type): not a foreign mod.
        // AllL3Ok and ClosureSweep both accept it, so [AutShadow] keeps Lot meta objects with the venuememo hook installed.
        static bool OwnSwap(MethodBase m)
        {
            Type hook;
            // golive-g7 2026-10-30: keyed by the MethodInfo object, every lookup from GetMethods missed (a new object), so our B13 swap read as a mod
            return sPatched.TryGetValue(m.MethodHandle.Value, out hook) && KlassOf(m) == Marshal.ReadIntPtr(hook.TypeHandle.Value);
        }
        static bool AllL3Ok(Type t, string[] names, Type[][] sigs)
        {
            for (int i = 0; i < names.Length; i++)
            {
                MethodInfo m = AutShadowCore.FindExact(t, names[i], sigs[i]);
                if (m == null) { AppliedLog().Add("AutShadow: mod guard: " + t.FullName + "." + names[i] + " signature not found"); return false; }
                if (!L3Ok(m) && !OwnSwap(m)) { AppliedLog().Add("AutShadow: mod guard: " + m.DeclaringType.FullName + "." + m.Name + " replaced"); return false; }
            }
            return true;
        }

        static readonly Type kTipRef = typeof(GreyedOutTooltipCallback).MakeByRefType();
        // Definition callees: Test(actor, target, bool, ref tip) with the target of kDefTarget (kDefNames order), the
        // Test(ref parameters, ref tip) wrapper, MetaAdSatisfiesCommodity and CalculateScore (MetaInteractionDefinition`3's
        // or an override).
        static readonly string[] kDefCallees = { "Test", "Test", "MetaAdSatisfiesCommodity", "CalculateScore" };
        static readonly Type[] kDefTarget = { typeof(Lot), typeof(Lot), typeof(GardeningMetaObject), typeof(Lot), typeof(Lot), typeof(Lot),
            typeof(ShoppingRabbitHole), typeof(ShowVenue), typeof(Sims3.Gameplay.Objects.RabbitHoles.Restaurant),
            typeof(Sims3.Gameplay.Objects.RabbitHoles.CityHall), typeof(Sims3.Gameplay.Objects.RabbitHoles.Hospital), typeof(RabbitHole) };
        static Type[][] DefSigs(int k)
        {
            return new Type[][] { new Type[] { typeof(Sim), kDefTarget[k], typeof(bool), kTipRef },
                new Type[] { typeof(InteractionInstanceParameters).MakeByRefType(), kTipRef },
                new Type[] { typeof(Lot), typeof(CommodityKind) }, new Type[] { typeof(InteractionObjectPair), typeof(AutonomyT) } };
        }
        static readonly string[] kImplCallees = { "HowMuchItWantsSimToCome", "get_MetaAds" };
        static readonly Type[][] kImplSigs = { new Type[] { typeof(Sim), typeof(float) }, Type.EmptyTypes };
        // Lot's public HowMuchItWantsSimToCome calls its private per-venue-type overload (docs/124 §13.2).
        static readonly string[] kLotCallees = { "HowMuchItWantsSimToCome", "HowMuchItWantsSimToCome", "get_MetaAds" };
        static readonly Type[][] kLotSigs = { new Type[] { typeof(Sim), typeof(float) },
            new Type[] { typeof(MetaAutonomyVenueType), typeof(Sim), typeof(float) }, Type.EmptyTypes };

        // Per load and whenever MonoPatcher's ForceJIT bytes change: definitions and implementors whose direct callees were
        // swapped are skipped; the shared C2 callees hit = every definition skipped. L2 bytes are logged only (docs/68 M2-3).
        static bool GuardSweep()
        {
            if (!sModulesChecked) { sModulesChecked = true; ModuleCheck(); }
            if (sSessionOff) return false;
            const BindingFlags st = BindingFlags.Static | BindingFlags.NonPublic;
            bool ownOk = L3Ok(typeof(Fixes).GetMethod("AutFinish", st));
            bool calcSeen = !sCalcPatched || !L3Ok(typeof(AutonomyT).GetMethod("CalculateScore", new Type[] { typeof(InteractionObjectPair) }));
            if (!ownOk || !calcSeen)
            {
                AppliedLog().Add("AutShadow: off for the session: L3 klass self-check failed (" + (ownOk ? "" : "own method AutFinish reads as swapped; ")
                    + (calcSeen ? "" : "our patched Autonomy.CalculateScore reads as unswapped") + ")");
                sSessionOff = true;
                return false;
            }
            sForceJitBytes = ForceJitBytes();
            if (Marshal.ReadInt32(new IntPtr(0xE5CA90)) != 0x246C8D55 || Marshal.ReadInt32(new IntPtr(0xE5CA94)) != 0x08EC8198)
                AppliedLog().Add("AutShadow: L2: GenerateCode 0xE5CA90 hooked (ReplaceIL possible; log only)");
            if (sForceJitBytes != 0x4C742675) AppliedLog().Add("AutShadow: L2: ForceJIT bytes changed (log only)");
            sSharedGuardHit = !AllL3Ok(typeof(InteractionObjectPair), new string[] { "get_Tradeoff", "get_InteractionDefinition", "get_Target", "TestAndCalculateScore" },
                    new Type[][] { Type.EmptyTypes, Type.EmptyTypes, Type.EmptyTypes, new Type[] { typeof(AutonomyT), typeof(AutonomySearchType), typeof(InteractionFlags) } })
                || !AllL3Ok(typeof(Tradeoff), new string[] { "SatisfiesCommodity" }, new Type[][] { new Type[] { typeof(CommodityKind) } })
                || !AllL3Ok(typeof(AutonomyT), new string[] { "CalculateScoreForObjectInteraction", "CalculateScoreForObjectInteraction" },
                    new Type[][] { new Type[] { typeof(InteractionObjectPair) }, new Type[] { typeof(InteractionObjectPair), typeof(ScoreDebugInfo).MakeByRefType() } });
            Assembly[] asm = { typeof(Lot).Assembly, typeof(BakeSaleTable).Assembly };
            sDefIndex.Clear();
            for (int k = 0; k < kDefNames.Length; k++)
            {
                Type t = asm[kDefAsm[k]].GetType(kDefNames[k]);
                if (t == null) { AppliedLog().Add("AutShadow: allow-list type missing: " + kDefNames[k]); continue; }
                sDefIndex[t] = k;
                sDefGuardHit[k] = sSharedGuardHit || !AllL3Ok(t, kDefCallees, DefSigs(k));
            }
            if (!ClosureSweep()) for (int k = 0; k < sDefGuardHit.Length; k++) sDefGuardHit[k] = true;
            sLiveGuardOk = LiveGuard();
            Census();
            ApplyIlOwners();
            sImplVerdict.Clear();
            return true;
        }

        // G-M4 (docs/128 §5): L3 over every managed method of the allow-listed closures (AutShadowLists.Methods, generated),
        // not only the direct callees above. A method whose MonoMethod klass is neither its own class nor the hook class of
        // our own swap on it (Fixes.Patch records them) was swapped by someone else: every definition is skipped (one
        // closure method can sit under all of them). Time and counts in [123]-[125].
        static bool ClosureSweep()
        {
            int[] c = PatchCounters();
            StopWatch sw = StopWatch.Create(StopWatch.TickStyles.Microseconds);
            sw.Start();
            List<Assembly> asms = new List<Assembly>();
            foreach (string n in kGameModules) { Assembly a = GameAssembly(n); if (a != null) asms.Add(a); }
            Dictionary<string, MethodBase> got = AutShadowCore.Resolve(asms.ToArray(), AutShadowLists.Methods);
            int foreign = 0, hashed = 0, ilBad = 0, knownBad = 0, maskAll = 0, maskUnknown = 0;
            long ilUs = 0;
            Dictionary<Module, Dictionary<int, string>> names = new Dictionary<Module, Dictionary<int, string>>();
            Dictionary<string, bool> known = new Dictionary<string, bool>();
            foreach (string raw in AutShadowIlHashes.KnownMvids) known[AutShadowCore.GuidNFromRaw(raw)] = true;
            foreach (KeyValuePair<string, MethodBase> e in got)
            {
                if (!L3Ok(e.Value))
                {
                    // shortcut: our own swaps are not hashed (the body read is ours); the original under it is not checked
                    if (!OwnSwap(e.Value) && foreign++ < 10) AppliedLog().Add("AutShadow: mod guard G-M4: " + e.Key + " swapped");
                    continue;
                }
                // G-M6: the body must equal the one in a checked build (MVIDs do not prove it: PR #112, docs/215 §4)
                long t0 = (long)sw.GetElapsedTime();
                ulong h = 0;
                bool ok;
                try { h = AutShadowCore.MethodIlHash(e.Key, e.Value, names); ok = Array.BinarySearch<ulong>(AutShadowIlHashes.Known, h) >= 0; }
                catch (Exception) { ok = false; }
                ilUs += (long)sw.GetElapsedTime() - t0;
                hashed++;
                if (ok) continue;
                Assembly a = e.Value.DeclaringType.Assembly;
                string mv = Mvid(a);
                int mi = Array.IndexOf(AutShadowLists.Methods, e.Key), om = mi >= 0 ? AutShadowLists.MethodOwners[mi] : -1;
                maskAll |= om;
                // a checked build's bytes (by content; MVID when the modules could not be hashed) cannot differ: our hash
                if (sContentOk ? !sUnknownMods.ContainsKey(a) : known.ContainsKey(mv)) knownBad++; else maskUnknown |= om;
                if (ilBad++ >= 10) continue;
                AppliedLog().Add("AutShadow: mod guard G-M6: " + e.Key + " in " + a.GetName().Name + " (MVID " + mv + ") differs from every checked build (IL hash "
                    + h.ToString("x16") + "): skips the definitions whose closure reaches it");
                if (ilBad > 3 && !ShortcutOn("gm6trace")) continue;
                // token names as the runtime sees them; compare with ILH_TRACE=<key> node tools/autshadow-il-hashes.ts --one <dir>
                List<string> tr = new List<string>();
                try { AutShadowCore.MethodIlHash(e.Key, e.Value, new Dictionary<Module, Dictionary<int, string>>(), tr); } catch (Exception x) { tr.Add("threw " + x.Message); }
                AppliedLog().Add("AutShadow: G-M6 trace " + e.Key + ": " + string.Join("; ", tr.ToArray()));
            }
            // docs/218 §5 item 2: in a module not accepted by content, also the class-hierarchy overrides the closures can
            // dispatch to (a changed override changes what the worker runs; G-M5 sees only mod assemblies). [2024] hashed,
            // [2025] differing from every checked build.
            int chaN = 0, chaBad = 0;
            if (sContentOk && sUnknownMods.Count > 0)
            {
                Dictionary<string, MethodBase> cha = AutShadowCore.Resolve(new List<Assembly>(sUnknownMods.Keys).ToArray(), AutShadowChaLists.Methods);
                foreach (KeyValuePair<string, MethodBase> e in cha)
                {
                    long t0 = (long)sw.GetElapsedTime();
                    ulong h = 0;
                    bool ok;
                    try { h = AutShadowCore.MethodIlHash(e.Key, e.Value, names); ok = Array.BinarySearch<ulong>(AutShadowIlHashes.Known, h) >= 0; }
                    catch (Exception) { ok = false; }
                    ilUs += (long)sw.GetElapsedTime() - t0;
                    chaN++;
                    if (ok) continue;
                    int ci = Array.IndexOf(AutShadowChaLists.Methods, e.Key), om = ci >= 0 ? AutShadowChaLists.Owners[ci] : -1;
                    maskAll |= om; maskUnknown |= om;
                    if (chaBad++ < 10)
                        AppliedLog().Add("AutShadow: mod guard G-M6 (hierarchy): " + e.Key + " in " + e.Value.DeclaringType.Assembly.GetName().Name
                            + " differs from every checked build (IL hash " + h.ToString("x16") + "): skips the definitions whose closure can dispatch to it");
                }
            }
            c[2024] = chaN; c[2025] = chaBad;
            // many mismatches on modules whose bytes (or MVID) a checked build has = our hash disagrees with the runtime
            bool fault = knownBad > 3;
            if (fault)
            {
                c[605]++;
                AppliedLog().Add("AutShadow: G-M6 GUARD FAULT: " + knownBad + " closure methods on checked-build MVIDs hash differently (hash/runtime disagreement, not a mod): "
                    + "IL check ignored this load, MVID + L3 guards only; report it");
            }
            sw.Stop();
            c[123] = got.Count; c[124] = (int)sw.GetElapsedTime(); c[125] = foreign;
            c[602] = hashed; c[603] = ilBad; c[604] = (int)ilUs;
            sw.Dispose();
            if (got.Count != AutShadowLists.Methods.Length)
                AppliedLog().Add("AutShadow: G-M4 sweep: " + got.Count + " of " + AutShadowLists.Methods.Length + " closure methods found (rest not in these assemblies)");
            // applied after Census (it resets the implementor marks): only the owners whose closure reaches a miss
            sIlOwnerMask = fault ? maskUnknown : maskAll;
            return foreign == 0;
        }

        // G-M6: definitions / implementors whose closure reaches a method not from a checked build; [606] = owners skipped
        static int sIlOwnerMask;
        static void ApplyIlOwners()
        {
            int n = 0;
            for (int o = 0; o < AutShadowLists.Owners.Length; o++)
            {
                if ((sIlOwnerMask & (1 << o)) == 0) continue;
                MarkOwner(o);
                n++;
                AppliedLog().Add("AutShadow: G-M6 skips " + AutShadowLists.Owners[o] + " (its closure reaches a method not from a checked build)");
            }
            PatchCounters()[606] = n;
        }
        static void MarkOwner(int o)
        {
            int k = Array.IndexOf(kDefNames, AutShadowLists.Owners[o]);
            if (k >= 0) sDefGuardHit[k] = true;
            for (int i = 0; i < kImpl.Length; i++) if (kImpl[i].FullName == AutShadowLists.Owners[o]) sImplGuardHit[i] = true;
        }

        // G-M5 (docs/128 §5): types from assemblies that are not game modules (and not ts3fixes: our hook holders are never
        // instantiated, their swaps are G-M4's) that override a closure virtual or implement a closure interface method:
        // every definition or implementor whose closure dispatches through that slot is skipped. [126] = such types.
        static void Census()
        {
            Assembly self = typeof(Fixes).Assembly;
            List<Type> mod = new List<Type>();
            Dictionary<Assembly, bool> isMod = new Dictionary<Assembly, bool>();
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (a == self || Array.IndexOf(kGameModules, a.GetName().Name) >= 0) continue;
                isMod[a] = true;
                try { mod.AddRange(a.GetTypes()); }
                catch (ReflectionTypeLoadException e) { foreach (Type t in e.Types) if (t != null) mod.Add(t); }
                catch (Exception e) { AppliedLog().Add("AutShadow: G-M5: " + a.GetName().Name + " types unreadable: " + e.Message); }
            }
            bool[] hit = new bool[AutShadowLists.Slots.Length];
            List<string> log = new List<string>();
            int exempt;
            Type[] modTypes = mod.ToArray();
            // docs/122 §14.13: definition-receiver slots are covered by the exact-type check (mono-cost4: NRaas' own
            // BaseDefinition implementing InteractionDefinition.Test skipped all 12 v0 definitions); subclasses skip instead
            PatchCounters()[126] = AutShadowCore.OverrideCensus(modTypes, delegate(Type t) { return t != null && isMod.ContainsKey(t.Assembly); },
                AutShadowLists.Slots, hit, log, AutShadowCore.OnScoredDefinition, out exempt);
            for (int i = 0; i < log.Count && i < 10; i++) AppliedLog().Add("AutShadow: mod guard G-M5: " + log[i]);
            if (exempt > 0) AppliedLog().Add("AutShadow: G-M5: " + exempt + " mod overrides of definition slots covered by the exact-type check (not skipped)");
            Type[] owners = new Type[AutShadowLists.Owners.Length];
            foreach (KeyValuePair<Type, int> e in sDefIndex)
            {
                int o = Array.IndexOf(AutShadowLists.Owners, kDefNames[e.Value]);
                if (o >= 0) owners[o] = e.Key;
            }
            bool[] derived = new bool[owners.Length];
            List<string> dlog = new List<string>();
            AutShadowCore.DerivedOwners(modTypes, owners, derived, dlog);
            Array.Clear(sImplGuardHit, 0, sImplGuardHit.Length);
            for (int o = 0; o < AutShadowLists.Owners.Length; o++)
            {
                bool any = false;
                foreach (string s in AutShadowLists.OwnerSlots[o].Split(','))
                    if (s.Length > 0 && hit[int.Parse(s)]) any = true;
                if (derived[o])
                {
                    foreach (string l in dlog) if (l.StartsWith(AutShadowLists.Owners[o] + " ")) AppliedLog().Add("AutShadow: G-M5 skips " + l);
                    any = true;
                }
                else if (any) AppliedLog().Add("AutShadow: G-M5 skips " + AutShadowLists.Owners[o] + " (overridden closure slot)");
                if (any) MarkOwner(o);
            }
        }

        // ForceJIT sites 0xE5637D (vanilla 75 26) and 0xE648DC (vanilla 74 4C) as one int.
        static int ForceJitBytes()
        {
            return Marshal.ReadByte(new IntPtr(0xE5637D)) | Marshal.ReadByte(new IntPtr(0xE5637E)) << 8
                | Marshal.ReadByte(new IntPtr(0xE648DC)) << 16 | Marshal.ReadByte(new IntPtr(0xE648DD)) << 24;
        }

        // Implementor index of a runtime type: exact type in the 7, or an EA rabbit-hole subclass (RabbitHole itself is
        // abstract; every real one is a subclass in the two MVID-checked assemblies). -1 not allowed, -2 mod guard.
        static int ImplVerdict(Type t)
        {
            int v;
            if (sImplVerdict.TryGetValue(t, out v)) return v;
            v = Array.IndexOf(kImpl, t);
            if (v < 0 && t.IsSubclassOf(typeof(RabbitHole)) && (t.Assembly == typeof(Lot).Assembly || t.Assembly == typeof(BakeSaleTable).Assembly))
                v = kImplRabbitHole;
            if (v >= 0 && (sImplGuardHit[v] || !(t == typeof(Lot) ? AllL3Ok(t, kLotCallees, kLotSigs) : AllL3Ok(t, kImplCallees, kImplSigs)))) v = -2;
            sImplVerdict[t] = v;
            return v;
        }

        // ---- request (sim thread, B13 prefix) ----

        // live (docs/124 §17, Mode 3): the request also gets a go-live search (AutLive.cs) and returns it in q.Live; null =
        // nothing posted
        static Request TryRequest(AutonomyT a, CommodityKind c, bool live)
        {
            if (sProbe > 0) AutShadowNative.Note();  // every B13 call: the go-live request rate (docs/122 §14.18)
            // a posted request's cut is shared by every later B13 before it (docs/122 §14.18); a new batch is sampled
            bool join = sPosted;
            if (join ? sReqN >= kMaxReq || a == sGameAut : (++sCalls % sSample != 0 || sGameAut != null)) return null;
            if (!Ready()) return null;
            int mode = AutShadowNative.Config(0);
            if (mode < 0 || (mode > 0 && (!sHooksOk || !sWarmOk))) return null;  // Modes 1-3 run managed code on the worker: every shim in and warm
            if (live != (mode == 3)) return null;  // Mode 3 posts only for a live search; shadow modes never do
            sSample = Math.Max(1, AutShadowNative.Config(1));
            // G-M4: again after a MonoPatcher ForceJIT byte change or a ts3agent patch_* swap (it sets the flag)
            bool resweep = AppDomain.CurrentDomain.GetData("ts3fixes.autshadow.resweep") != null;
            if (resweep) AppDomain.CurrentDomain.SetData("ts3fixes.autshadow.resweep", null);
            if ((resweep || ForceJitBytes() != sForceJitBytes) && !GuardSweep()) return null;
            Sim actor = a.Actor;
            if (actor == null || !(bool)mCanAnyMeta.Invoke(a, null)) return null;
            int[] cnt = PatchCounters();
            LiveSearch L = live ? LiveAcquire() : null;
            if (live && L == null) return null;  // 8 searches in flight
            if (!AutShadowNative.Begin()) { cnt[119]++; if (L != null) LiveRelease(L); return null; }
            if (!join) sReqN = 0;
            int at = sReqN;
            Request q = sReq = sReqs[at];
            q.Live = L;
            q.Id = ++sNextId; q.Mode = mode; q.Actor = actor; q.Aut = a; q.C = c; q.Hh = HhScope(actor);
            q.SearchType = a.CurrentSearchType; q.Flags = a.GetInteractionFlagsForCurrentSearch(); q.Hours = SimClock.HoursPassedOfDay;
            q.Objs = q.Pairs = 0;
            sRefs[at].Id = sWorks[at].Id = -1;
            q.PairIndex.Clear(); q.Lots.Clear(); q.BakeSales.Clear();
            Array.Clear(q.GameHas, 0, q.GameHas.Length);
            try
            {
                // through the getter (the game's own, our hook passes through off the worker): it recounts when the count is
                // older than kMinutesbetweenNumGardeningsimsUpdates. The raw field read here (0.36.110) gave the worker the
                // old count while the game's B13 or the reference recounted (mono-cost6: GoToGarden meta 300 vs 259, docs/122 §14.15).
                // Mode R reads it again at the cut (AutReference, docs/122 §14.18); this read serves Mode W.
                sGardening = Sim.NumSimsDoingGardening;
                AutShadowNative.AddRow(actor.ObjectId.Value, 1);
                if ((bool)mCanFullMeta.Invoke(a, null))
                {
                    foreach (IMetaObject m in AutonomyT.PublicMetaObjects) AddObj(q, m);
                    List<IMetaObject> known = fLotsKnown.GetValue(a) as List<IMetaObject>;
                    if (known != null) foreach (IMetaObject m in known) AddObj(q, m);
                }
                if (a.CanRunLocalAutonomy())
                {
                    if (actor.LotCurrent != null) AddObj(q, actor.LotCurrent);
                    if (actor.LotCurrent != actor.LotHome && actor.LotHome != null) AddObj(q, actor.LotHome);
                    if (actor.CareerLocation != null) AddObj(q, actor.CareerLocation);
                }
                foreach (Sim s in LotManager.Actors)
                    if (s != null && s != actor) AutShadowNative.AddRow(s.ObjectId.Value, Reachable(s) ? 1 : 2);
            }
            catch (Exception e) { cnt[121]++; AutNote("request: " + e.Message); }
            PreWarm(actor);
            T2P1Fill(q, actor);
            T2HFill(q);
            ClassifyListTypes();
            ClassifyObjects();
            q.Start[q.Objs] = q.Pairs;
            ShelterFill(q);
            if (L != null) LiveFill(L, q);
            sPosted = true;
            sReqN = at + 1;
            if (at == 0 && !live) { sGameAut = a; sGameDone = sWorkReady = false; }  // L report: the batch's first request only
            cnt[118]++;
            if (at == 0) OffRequest();  // docs/208: env / broadcaster items ride the batch's cut (AutOffload.cs)
            AutShadowNative.Post(q.Id);
            return q;
        }

        // docs/122 §14.18 list kinds: each list query type ts3native has seen, once, sim thread. A Sim can be one when the type
        // is Sim or a base or interface of it, or derives from it (a mod subclass); then Sim moves refresh its lists.
        static void ClassifyListTypes()
        {
            try
            {
                for (int n = 0; n < 64; n++)
                {
                    object o = AutShadowNative.ListTypeNext();
                    if (o == null) return;
                    Type t = o as Type;
                    AutShadowNative.ListTypeSet(o, t == null || t.IsAssignableFrom(typeof(Sim)) || typeof(Sim).IsAssignableFrom(t) ? 1 : 0);
                }
            }
            catch (Exception e) { PatchCounters()[121]++; AutNote("list types: " + e.Message); }
        }

        // docs/122 §14.20 typed marks: objects ts3native queued (new ones, and those whose move marked every list type) are
        // tested once against each list type, as the list ICs filter: the object's script target by IsInstanceOfType
        // [inferred, I-396-1]. Sim thread, before the cut; an object with no target yet stays unknown (every type).
        static readonly List<Type> sListTypes = new List<Type>();
        // one batch of 32 per request (newest first: ids a cut's marks queued): the sim-thread cost per request stays bounded
        static readonly ulong[] sClsIds = new ulong[32];
        static void ClassifyObjects()
        {
            try
            {
                int nt = Math.Min(AutShadowNative.ListTypeCount(), 32);
                while (sListTypes.Count < nt) sListTypes.Add(AutShadowNative.ListTypeAt(sListTypes.Count) as Type);
                int n = AutShadowNative.ObjClassTake(sClsIds);
                for (int j = 0; j < n; j++)
                {
                    IScriptProxy p = Simulator.GetProxy(new ObjectGuid(sClsIds[j]));
                    object o = p != null ? p.Target : null;
                    if (o == null) { AutShadowNative.ObjClassSet(sClsIds[j], 0, 0); continue; }
                    int mask = 0;
                    for (int i = 0; i < nt; i++)
                    {
                        Type t = sListTypes[i];
                        if (t == null || t.IsInstanceOfType(o)) mask |= 1 << i;
                    }
                    AutShadowNative.ObjClassSet(sClsIds[j], mask, nt);
                }
            }
            catch (Exception e) { PatchCounters()[121]++; AutNote("object classes: " + e.Message); }
        }

        static void AddObj(Request q, IMetaObject m)
        {
            if (m == null || m.HasBeenDestroyed || q.Objs >= kMaxObjs) return;
            int o = q.Objs++;
            q.Obj[o] = m;
            q.Start[o] = q.Pairs;
            q.ObjImpl[o] = ImplVerdict(m.GetType());
            AutShadowNative.AddRow(m.ObjectId.Value, 0);
            Lot lot = m as Lot ?? m.LotCurrent;
            if (lot != null)
            {
                // docs/216 simstale: the scored lot to ts3native (kind 3 = LotId; its tier-2 Sims refresh at this cut)
                if (sReqLotRows && !q.Lots.ContainsKey(lot.LotId)) AutShadowNative.AddRow(lot.LotId, 3);
                q.Lots[lot.LotId] = true;
                if (lot != m) AutShadowNative.AddRow(lot.ObjectId.Value, 0);
            }
            if (m is BakeSaleTable) q.BakeSales.Add(m.Position);
            ArrayList ads = m.MetaAds;
            if (ads == null) return;
            for (int i = 0; i < ads.Count && q.Pairs < kMaxPairs; i++)
            {
                InteractionObjectPair iop = ads[i] as InteractionObjectPair;
                if (iop == null) continue;
                int p = q.Pairs++, k;
                q.Pair[p] = iop;
                q.PairObj[p] = o;
                q.Def[p] = iop.InteractionDefinition != null && sDefIndex.TryGetValue(iop.InteractionDefinition.GetType(), out k) ? (sDefGuardHit[k] ? -2 : k) : -1;
                // G-M2 (docs/128 §4): the tuning's scoring function, read here on the sim thread (only LoadTuning writes it)
                InteractionTuning tun = iop.Tuning;
                q.Acb[p] = tun != null && tun.AlwaysChooseBest;
                if (q.Def[p] >= 0 && tun != null && tun.ScoringFunction != null && !SfAllowed(tun.ScoringFunction)) { q.Def[p] = -3; PatchCounters()[122]++; }
                q.PairIndex[iop] = p;
            }
        }

        // An allow-listed scoring function from a game module (G-M1 accepted, so EA's or an accepted pair's code) whose
        // method is not swapped (L3). A multicast delegate or an instance target of a mod type fails.
        static bool SfAllowed(Delegate f)
        {
            MethodInfo m = f.Method;
            if (m == null || f.GetInvocationList().Length != 1 || m.DeclaringType == null) return false;
            if (Array.IndexOf(kGameModules, m.DeclaringType.Assembly.GetName().Name) < 0) return false;
            if (f.Target != null && Array.IndexOf(kGameModules, f.Target.GetType().Assembly.GetName().Name) < 0) return false;
            return Array.IndexOf(kSfAllow, AutShadowCore.TypeKey(m.DeclaringType) + "::" + m.Name) >= 0 && L3Ok(m);
        }

        // docs/125 addendum §4.2/§4.3, sim thread at the request: refresh the actor's room cache for every lot the search
        // can score (so neither the reference nor the worker recalculates it) and fill the posture interaction lists the
        // way the game's first read would (Mode W: the worker must never be the first reader; it aborts on a null list).
        static void PreWarm(Sim actor)
        {
            try
            {
                if (actor.LotCurrent != null) actor.IsAllowedInRoom(actor.LotCurrent.LotId, 0);
                foreach (ulong lot in sReq.Lots.Keys) actor.IsAllowedInRoom(lot, 0);
                int n = 0;
                for (Sims3.Gameplay.ActorSystems.Posture p = actor.Posture; p != null && n++ < 8; p = p.PreviousPosture)
                    if (p.Interactions == null) break;
            }
            catch (Exception e) { PatchCounters()[121]++; AutNote("pre-warm: " + e.Message); }
        }

        // Kind 1 Sims (docs/122 §2.3): on a scored lot or within a bake sale table's meta radius; the rest are kind 2.
        static bool Reachable(Sim s)
        {
            Lot l = s.LotCurrent;
            if (l != null && sReq.Lots.ContainsKey(l.LotId)) return true;
            if (sReq.BakeSales.Count == 0) return false;
            Vector3 p = s.Position;
            float r2 = BakeSaleTable.kMetaAutonomyDesiredRadius * BakeSaleTable.kMetaAutonomyDesiredRadius;
            foreach (Vector3 b in sReq.BakeSales) if ((p - b).LengthSqr() <= r2) return true;
            return false;
        }

        // ---- native entry points (no arguments; request and results live in the statics above) ----

        static void AutReference()
        {
            if (!sPosted) return;
            if (sReqs[0].Mode == 2)
            {
                if (sWarmIn >= sWarmInMax) return;
                sWarmIn++;
                PatchCounters()[117]++;
            }
            // mono-cost9 BP A2 (docs/122 §14.18): GoToGarden meta 300 (worker) vs 250.2 (reference). The worker used the count
            // read at the request (B13 prefix: no Sim gardening); by the cut at the end of the frame the reference read a count
            // > 0. Both now use the count at the cut, the instant the snapshot stands for (through the getter: a recount it
            // does happens here, and the reference's own calls find it fresh).
            if (sReqs[0].Mode == 1 || sReqs[0].Mode == 3) sGardening = Sim.NumSimsDoingGardening;  // Mode 3: verify cuts only
            for (int i = 0; i < sReqN; i++) ShelterFill(sReqs[i]);  // H11: the bit at the cut, the instant the reference reads
            sInRef = true;
            try { for (int i = 0; i < sReqN; i++) { sReq = sReqs[i]; sRef = sRefs[i]; ScoreMeta(sReq, sRef); } OffScore(false); }
            finally { sInRef = false; }
        }

        // Worker thread: no counters, no logs, no Simulator calls; everything it learns goes into sWorks. A stop leaves the
        // rest of the batch unscored (Done stays false: no compare).
        static void AutWork()
        {
            if (!sPosted) return;
            sInWork = true;
            try
            {
                for (int i = 0; i < sReqN; i++)
                {
                    sWorks[i].Id = -1; sWorks[i].Done = false;
                    if (i > 0 && AutShadowNative.Stopped()) continue;
                    sReq = sReqs[i]; sWork = sWorks[i];
                    ScoreMeta(sReq, sWork);
                }
                if (sActorScope) AutShadowNative.ActorScope(sReqN > 0 && !sReqs[0].Hh ? 0 : 1);  // the offload items ride request 0
                OffScore(true);  // docs/208 (AutOffload.cs)
            }
            finally { sInWork = false; }
        }

        static void AutFinish()
        {
            if (!sPosted) return;
            sPosted = false;
            sInWork = false;
            int lr = AutShadowNative.LastRun(), n = sReqN;
            sReqN = 0;
            int[] c = PatchCounters();
            if (lr == 5) c[121]++;
            for (int k = 0; k < n; k++)  // each request against its own rows and its own reference (docs/122 §14.18)
            {
                sReq = sReqs[k]; sRef = sRefs[k]; sWork = sWorks[k];
                c[121] += (sRef.Id == sReq.Id ? sRef.Exceptions : 0) + (sWork.Id == sReq.Id ? sWork.Exceptions : 0);
                if (sWork.Id == sReq.Id) for (int w = 0; w < 4; w++) c[kCRainWhy + w] += sWork.RainWhy[w];  // fixes-607
                if (sWork.Id == sReq.Id) for (int w = 0; w < AutShadowCore.kT2N; w++) c[kCT2Ab + w] += sWork.T2Ab[w];  // fixes-884
                if (sWork.Id == sReq.Id) for (int w = 0; w < AutShadowCore.kT2HN; w++) c[kCT2H + w] += sWork.T2H[w];  // fixes-898
                // KeepPartial (docs/122 §14.21): an overrun (LastRun 1) whose worker result says Done kept the objects it
                // finished before the stop; those are compared, the rest are A4 `stop`. An epoch drop (2) stays dropped.
                bool partial = lr == 1 && sKeepPartial && sWork.Done && sWork.Partial && sWork.Id == sReq.Id;
                // docs/124 §17 go-live: the answers go to the waiting search (LiveFinish); valid = OK and done, or the kept part
                if (sReq.Mode == 3) LiveFinish(sReq.Live, sReq, sWork, lr, partial || (lr == 0 && sWork.Done));
                if (!partial && (lr == 1 || lr == 2 || (lr == 0 && !sWork.Done))) { c[111]++; continue; }
                if ((lr != 0 && !partial) || sWork.Id != sReq.Id) continue;
                if (partial) AutShadowNative.Stat(6, 1);
                CountAborts();
                for (int i = 0; i < sReq.Pairs; i++)
                    if (sWork.Code[i] == AutShadowCore.kSkip) { c[116]++; if (sReq.Def[i] == -2 || sReq.ObjImpl[sReq.PairObj[i]] == -2) c[120]++; }
                if (sReq.Mode == 1 && sRef.Id == sReq.Id && sRef.Done) { CompareR(c); FreshCheck(); }
                // Mode 3 VerifyEvery: the reference ran at this cut (sRef holds this request's answers only then)
                if (sReq.Mode == 3 && sRef.Id == sReq.Id && sRef.Done) { int a23 = c[108] + c[109]; CompareR(c); LiveVerify(a23); }
                if (k == 0 && sReq.Mode != 3) { sWorkReady = true; TryLReport(); }
            }
            OffFinish(lr);  // docs/208 (AutOffload.cs)
            sReq = sReqs[0]; sRef = sRefs[0]; sWork = sWorks[0];
        }

        // docs/216 §5 abort record: aborted pairs of every used worker result (both modes) per (reason, definition) and per
        // (reason, implementor), plus the desirefn split (objects aborted in the driver's meta score vs pairs); one capped
        // autshadow.jsonl line ({"t":"abortdef"}, cumulative per session) every 250 used results. Sim thread only.
        static readonly Dictionary<int, int> sAbortDef = new Dictionary<int, int>(), sAbortImpl = new Dictionary<int, int>();
        static int sAbortRecN, sDesireDriver, sDesirePairs;

        static void CountAborts()
        {
            sDesireDriver += sWork.DesireDriver;
            int n;
            for (int i = 0; i < sReq.Pairs; i++)
            {
                if (sWork.Code[i] != AutShadowCore.kAbort || sReq.Def[i] < 0) continue;
                int r = sWork.Reason[i] & 0xFF;
                if (r == kReasonDesire) sDesirePairs++;
                int kd = r << 16 | sReq.Def[i], ki = r << 16 | (sReq.ObjImpl[sReq.PairObj[i]] & 0xFFFF);
                sAbortDef[kd] = sAbortDef.TryGetValue(kd, out n) ? n + 1 : 1;
                sAbortImpl[ki] = sAbortImpl.TryGetValue(ki, out n) ? n + 1 : 1;
            }
            if (++sAbortRecN % 250 == 0) LogAbortRec();
        }

        static void LogAbortRec()
        {
            StringBuilder b = new StringBuilder(1300);
            b.Append("{\"t\":\"abortdef\",\"n\":").Append(sAbortRecN).Append(",\"desire\":[").Append(sDesireDriver).Append(',').Append(sDesirePairs).Append("],\"def\":[");
            AppendTopAborts(b, sAbortDef, true, 900);
            b.Append("],\"impl\":[");
            AppendTopAborts(b, sAbortImpl, false, 1250);  // native line limit 1300 (AsIcLog)
            b.Append("]}");
            AutShadowNative.Log(b.ToString());
        }

        // [reason, short type name, pairs], most first, until the line would pass cap
        static void AppendTopAborts(StringBuilder b, Dictionary<int, int> d, bool def, int cap)
        {
            List<KeyValuePair<int, int>> l = new List<KeyValuePair<int, int>>(d);
            l.Sort(delegate(KeyValuePair<int, int> x, KeyValuePair<int, int> y) { return y.Value.CompareTo(x.Value); });
            int start = b.Length;
            foreach (KeyValuePair<int, int> e in l)
            {
                int k = e.Key & 0xFFFF;
                string name = def ? (k < kDefNames.Length ? kDefNames[k] : "?") : (k < kImpl.Length ? kImpl[k].Name : "?");
                name = name.Substring(name.LastIndexOf('.') + 1);
                string item = (b.Length > start ? "," : "") + "[" + (e.Key >> 16) + ",\"" + name + "\"," + e.Value + "]";
                if (b.Length + item.Length > cap) break;
                b.Append(item);
            }
        }

        // docs/124 §15 stale-score check (Mode R, FreshEvery = 1 in N finishes): the same request scored again now, on the sim
        // thread at the finish (>= 1 tick after the cut; go-live's consume point is inside the next tick), against the
        // worker's cut-time result: pairs both answered, pairs whose code or score differ, searches whose top passing pair or
        // top-10 passing pairs (identity and order) differ. Counts only; the bit-exact A0-A3 compare is untouched.
        static readonly Result sFresh = new Result();
        static int sFreshN;
        static void FreshCheck()
        {
            int every = sFreshEvery;
            if (every <= 0 || ++sFreshN % every != 0) return;
            AutShadowNative.Fresh(1);
            sInRef = true;
            try { sGardening = Sim.NumSimsDoingGardening; ScoreMeta(sReq, sFresh); }
            catch (Exception) { sFresh.Done = false; }
            finally { sInRef = false; AutShadowNative.Fresh(0); }
            if (!sFresh.Done) return;
            int both = 0, diff = 0;
            bool[] ans = new bool[sReq.Pairs];
            for (int i = 0; i < sReq.Pairs; i++)
            {
                if (sReq.Def[i] < 0 || sReq.ObjImpl[sReq.PairObj[i]] < 0) continue;
                int a = sWork.Code[i], b = sFresh.Code[i];
                if ((a != AutShadowCore.kPass && a != AutShadowCore.kTestFailed) || (b != AutShadowCore.kPass && b != AutShadowCore.kTestFailed)) continue;
                both++;
                ans[i] = true;
                if (a != b || AutShadowCore.Bits(sWork.Score[i]) != AutShadowCore.Bits(sFresh.Score[i])) diff++;
            }
            int[] tw = Top(sWork, 10, null), tf = Top(sFresh, 10, null);
            bool top1 = tw[0] != tf[0], top10 = false;
            for (int j = 0; j < 10; j++) if (tw[j] != tf[j]) top10 = true;
            AutShadowNative.Stat(2, both); AutShadowNative.Stat(3, diff);
            AutShadowNative.Stat(4, top1 ? 1 : 0); AutShadowNative.Stat(5, top10 ? 1 : 0);
            // docs/122 §14.23: (a) the same compare over the pairs both sides answered only (worker aborts drop out), the top-10
            // as sets over the shorter list; (b) the cut-time reference against fresh over all pairs (both on the sim thread, no
            // aborts: the one-tick staleness of the whole set go-live would see), top-10 as sets incl. padding
            int[] twa = Top(sWork, 10, ans), tfa = Top(sFresh, 10, ans);
            int k = Math.Min(Count(twa), Count(tfa));
            AutShadowNative.Stat(7, k > 0 && twa[0] != tfa[0] ? 1 : 0);
            AutShadowNative.Stat(8, SameSet(twa, tfa, k) ? 0 : 1);
            if (sRef.Id == sReq.Id && sRef.Done)
            {
                int[] tr = Top(sRef, 10, null);
                AutShadowNative.Stat(9, tr[0] != tf[0] ? 1 : 0);
                AutShadowNative.Stat(10, SameSet(tr, tf, 10) ? 0 : 1);
            }
        }
        static int Count(int[] t) { int n = 0; while (n < t.Length && t[n] >= 0) n++; return n; }
        static bool SameSet(int[] a, int[] b, int k)
        {
            int[] x = new int[k], y = new int[k];
            Array.Copy(a, x, k); Array.Copy(b, y, k);
            Array.Sort(x); Array.Sort(y);
            for (int j = 0; j < k; j++) if (x[j] != y[j]) return false;
            return true;
        }
        // indexes of the n best passing allow-listed pairs by score (ties: lower index), -1 padded; only = pairs to consider (null: all)
        static int[] Top(Result r, int n, bool[] only)
        {
            int[] t = new int[n];
            for (int j = 0; j < n; j++) t[j] = -1;
            for (int i = 0; i < sReq.Pairs; i++)
            {
                if (sReq.Def[i] < 0 || sReq.ObjImpl[sReq.PairObj[i]] < 0 || r.Code[i] != AutShadowCore.kPass || (only != null && !only[i])) continue;
                int at = n;
                while (at > 0 && (t[at - 1] < 0 || r.Score[i] > r.Score[t[at - 1]])) at--;
                if (at >= n) continue;
                for (int j = n - 1; j > at; j--) t[j] = t[j - 1];
                t[at] = i;
            }
            return t;
        }

        static void CompareR(int[] c)
        {
            int[] n = new int[6];
            int pass = 0, cls = 0, refPassA4 = 0;
            for (int i = 0; i < sReq.Pairs; i++)
            {
                int o = sReq.PairObj[i];
                // docs/122 §5.1: A0-A5 are over allow-listed pairs; a pair off the allow-list (definition or implementor -1)
                // is skipped on both sides by design and counted only in [116] (0.36.110 counted it as A4: 24,355 of 30,395 BP)
                if (sReq.Def[i] == -1 || sReq.ObjImpl[o] == -1) continue;
                int k = AutShadowCore.Classify(sRef.Code[i], sRef.Score[i], sRef.Meta[o], sWork.Code[i], sWork.Score[i], sWork.Meta[o]);
                if (k < 0) continue;
                c[106 + k]++; n[k]++;
                cls++;
                if (sWork.Code[i] == AutShadowCore.kPass) pass++;
                if (k == AutShadowCore.A4)
                {
                    if (sRef.Code[i] == AutShadowCore.kPass) refPassA4++;  // docs/122 §14.23: the true pass share and the abort bias
                    int r =sWork.Code[i] == AutShadowCore.kAbort ? sWork.Reason[i] : sRef.Code[i] == AutShadowCore.kAbort ? sRef.Reason[i] : -1;
                    if (r >= 0 && r < sReasons.Length) sReasons[r]++;
                }
                else if (k != AutShadowCore.A0 && sLogged < kMaxLogPerLoad) { sLogged++; LogPair(i, k); }
            }
            AutShadowNative.Stat(0, pass); AutShadowNative.Stat(1, cls);  // docs/124 §15 pass share
            AutShadowNative.Stat(11, refPassA4);
            if (ShortcutOn("autshadowverify"))
                AutShadowNative.Log("{\"t\":\"req\",\"req\":" + sReq.Id + ",\"actor\":" + sReq.Actor.ObjectId.Value + ",\"objs\":" + sReq.Objs + ",\"pairs\":" + sReq.Pairs
                    + ",\"a\":[" + n[0] + "," + n[1] + "," + n[2] + "," + n[3] + "," + n[4] + "]}");
        }

        static void LogPair(int i, int k)
        {
            try
            {
                InteractionObjectPair p = sReq.Pair[i];
                int o = sReq.PairObj[i];
                StringBuilder s = new StringBuilder(320);
                s.Append("{\"t\":\"A").Append(k).Append("\",\"req\":").Append(sReq.Id).Append(",\"actor\":").Append(sReq.Actor.ObjectId.Value)
                 .Append(",\"meta\":").Append(sReq.Obj[o].ObjectId.Value).Append(",\"def\":\"").Append(p.InteractionDefinition.GetType().FullName)
                 .Append("\",\"target\":").Append(p.Target != null ? p.Target.ObjectId.Value : 0UL);
                AutNum(s, "ref", sRef.Score[i]); AutNum(s, "work", sWork.Score[i]); AutNum(s, "refMeta", sRef.Meta[o]); AutNum(s, "workMeta", sWork.Meta[o]);
                s.Append(",\"refCode\":").Append(sRef.Code[i]).Append(",\"workCode\":").Append(sWork.Code[i]).Append('}');
                AutShadowNative.Log(s.ToString());
            }
            catch (Exception) { }
        }

        static void AutNum(StringBuilder s, string name, float f)
        {
            s.Append(",\"").Append(name).Append("\":");
            if (float.IsNaN(f) || float.IsInfinity(f)) s.Append('"').Append(f.ToString(CultureInfo.InvariantCulture)).Append('"');
            else s.Append(f.ToString("R", CultureInfo.InvariantCulture));
            s.Append(",\"").Append(name).Append("Bits\":\"").Append(AutShadowCore.Bits(f).ToString("X8")).Append('"');
        }

        // L report once both the worker result (AutFinish) and the game's own B13 (MetaHook exit) are in for the request.
        static void TryLReport()
        {
            if (!sWorkReady || !sGameDone) return;
            sWorkReady = sGameDone = false;
            AutShadowCore.LReport r = AutShadowCore.Report(sReqs[0].GameScore, sReqs[0].GameHas, sWorks[0].Score, sWorks[0].Code, sReqs[0].Pairs);
            int[] c = PatchCounters();
            c[112] += r.Pairs; c[113] += r.Equal; c[114] += r.TestAgree;
            if (r.Top1Agree) c[115]++;
        }

        static void AutNote(string what)
        {
            List<object> log = AppliedLog();
            if (log.Count < 400) log.Add("AutShadow: " + what);
        }

        // ---- driver (sim thread for the reference, worker for the run) ----

        // Our own B14/C1/C2 per meta object (docs/122 §3.6): commodity filter, then the game's Test and score on the game's
        // pair objects (S1: C2 writes nothing into them). No BestScores, no InteractionCheckTable, no yield, no stopwatch,
        // no events. Abort reasons are read after each meta object; a stop from the sim abandons the rest (A5).
        public static void ScoreMeta(Request q, Result r)
        {
            r.Id = q.Id; r.Exceptions = 0; r.DesireDriver = 0; r.RainUsed = 0; r.Done = false; r.Partial = false;
            Array.Clear(r.RainWhy, 0, r.RainWhy.Length);
            Array.Clear(r.T2Ab, 0, r.T2Ab.Length);
            Array.Clear(r.T2H, 0, r.T2H.Length);
            sCurRes = r;
            if (sActorScope && OnWorker()) AutShadowNative.ActorScope(q.Hh ? 1 : 0);
            for (int o = 0; o < q.Objs; o++)
            {
                int s = q.Start[o], e = q.Start[o + 1];
                r.Meta[o] = 0f;
                if (AutShadowNative.Stopped())
                {
                    for (int i = s; i < q.Pairs; i++) { r.Code[i] = AutShadowCore.kAbort; r.Reason[i] = kReasonStop; r.Score[i] = 0f; }
                    r.Partial = true; r.Done = sKeepPartial;
                    return;
                }
                for (int i = s; i < e; i++) { r.Score[i] = 0f; r.Code[i] = AutShadowCore.kSkip; r.Reason[i] = -1; }
                if (q.ObjImpl[o] < 0) continue;
                try
                {
                    AutShadowNative.RngUnit(o << 16);  // docs/125: one RNG stream per meta score and per pair
                    // docs/216 fix #2: go-live (Mode 3) never reads Meta[] (only Mode R's Classify does), so neither side scores
                    // it there: the H5 desirefn aborts it caused go and the worker job is ~45% shorter (docs/122 §14.23)
                    if (q.Mode != 3)
                    {
                        r.Meta[o] = q.Obj[o].HowMuchItWantsSimToCome(q.Actor, q.Hours);
                        if (AutShadowNative.Aborted() == kReasonDesire) r.DesireDriver++;
                    }
                    for (int i = s; i < e; i++)
                        if (q.Def[i] >= 0) { AutShadowNative.RngUnit((o << 16) | (i - s + 1)); r.Code[i] = ScorePair(q, o, q.Pair[i], out r.Score[i]); }
                }
                catch (Exception)
                {
                    r.Exceptions++;
                    for (int i = s; i < e; i++) if (q.Def[i] >= 0) { r.Code[i] = AutShadowCore.kAbort; r.Reason[i] = kReasonExc; }
                }
                int ab = AutShadowNative.Aborted();
                if (ab >= 0)
                {
                    for (int i = s; i < e; i++) if (q.Def[i] >= 0) { r.Code[i] = AutShadowCore.kAbort; r.Reason[i] = ab; }
                    AutShadowNative.ResetAbort();
                }
                // docs/122 §14.21 KeepPartial: an object is valid only if the stop was still clear after its last read (x86:
                // loads stay in order, the sim sets stop before it writes anything), so the stop test comes after the object;
                // the object it was inside of is aborted `stop` with the rest. Off the worker Stopped() is always false.
                if (sKeepPartial && AutShadowNative.Stopped())
                {
                    for (int i = s; i < q.Pairs; i++) { r.Code[i] = AutShadowCore.kAbort; r.Reason[i] = kReasonStop; r.Score[i] = 0f; }
                    r.Meta[o] = 0f;
                    r.Partial = true; r.Done = true;
                    return;
                }
            }
            r.Done = true;
        }

        static int ScorePair(Request q, int o, InteractionObjectPair iop, out float score)
        {
            score = 0f;
            Tradeoff t = iop.Tradeoff;
            if (t == null) return AutShadowCore.kFiltered;
            bool want = q.C == CommodityKind.None || t.SatisfiesCommodity(q.C);
            if (!want)
            {
                IMetaInteractionDefinition md = iop.InteractionDefinition as IMetaInteractionDefinition;
                if (md != null) want = md.MetaAdSatisfiesCommodity(iop.Target as Lot, q.C);
            }
            if (!want) return AutShadowCore.kFiltered;
            InteractionFlags flags = q.Flags;
            if (q.Aut.OverrideInteractionParameters.HasValue) flags |= q.Aut.OverrideInteractionParameters.Value.Flags;
            InteractionInstanceParameters p = new InteractionInstanceParameters(iop, q.Actor, new InteractionPriority(InteractionPriorityLevel.Autonomous), q.SearchType, flags);
            GreyedOutTooltipCallback tip = null;
            if (iop.InteractionDefinition.Test(ref p, ref tip) != InteractionTestResult.Pass) return AutShadowCore.kTestFailed;
            bool rain = false;
            if (OnWorker() && RainShelterBranch(q, iop))
            {
                if (!RainArmPair(q, o, iop)) { RainWhy(sRainArmRet == -1 ? 1 : 0); AutShadowNative.Abort(kReasonRain); return AutShadowCore.kAbort; }
                rain = true;
            }
            bool ok = true;
            IOverridesCalculateScore ov = iop.InteractionDefinition as IOverridesCalculateScore;
            try { score = ov != null ? ov.CalculateScore(iop, q.Aut) : q.Aut.CalculateScoreForObjectInteraction(iop); }
            finally { if (rain) ok = RainDisarm(); }  // always disarmed: an armed receiver must never outlive its pair
            if (!ok) { score = 0f; RainWhy(3); AutShadowNative.Abort(kReasonRain); return AutShadowCore.kAbort; }
            return AutShadowCore.kPass;
        }

        static bool OnWorker() { return sInWork && AutShadowNative.IsWorker(); }

        // household rule (CLAUDE.md): a Sim the player controls, or one we cannot classify, counts as household
        static bool HhScope(Sim s)
        {
            try { return s == null || s.HasBeenDestroyed || s.SimDescription == null || s.IsSelectable || s.Household == Sims3.Gameplay.CAS.Household.ActiveHousehold; }
            catch (Exception) { return true; }
        }

        // H11 (docs/127 §4): Autonomy.CalculateScoreForObjectInteraction, with Seasons on, rain or hail, an autonomy search
        // and an outdoor non-Sim target, creates a throwaway InteractionInstance (InteractionInstance.sNextId++, a static
        // write the IC guard cannot see) to ask IsShelteredFromPrecipitation. The worker aborts those pairs before the
        // score (weather does not change inside a search); the sim thread scores them as the game does.
        static bool RainShelterBranch(Request q, InteractionObjectPair iop)
        {
            if (iop == null) return false;  // ColdWarm's call
            sRain.Target = iop.Target;
            if (sRain.Target == null) return false;
            return AutShadowCore.RainShelter(q.SearchType == AutonomySearchType.Autonomy, sRain,
                (int)Sims3.SimIFace.Enums.Weather.Rain, (int)Sims3.SimIFace.Enums.Weather.Hail);
        }
        sealed class RainProbe : AutShadowCore.IRainProbe
        {
            public IGameObject Target;
            public bool SeasonsOn() { return Sims3.Gameplay.Seasons.SeasonsManager.Enabled; }
            public int Weather() { return (int)Sims3.Gameplay.Seasons.SeasonsManager.CurrentWeather; }
            public bool TargetIsSim() { return Target is Sim; }
            public bool TargetOutside() { return Target.IsOutside; }
        }
        static readonly RainProbe sRain = new RainProbe();  // worker only (one run at a time)

        // H11 shelter answer (docs/127 §4.1). Sim thread, at the request and again at the cut (AutReference): one bit per meta
        // object = what IsShelteredFromPrecipitation(target, instance) answers for it (the position test; Sims, indoors and
        // ICanCountAsBeingIndoors targets get none). Worker: ScorePair arms ts3native on the pair's definition, the game's
        // CreateInstance call in the branch is not made (null instance, no InteractionInstance.sNextId++ on the worker),
        // and AutShelterHook answers from the bit. Anything off that path aborts `rain` as before.
        static bool sShelterHooked, sRainAnswer;
        static int sRainArmRet = 9;  // worker: the last RainArm answer (9 = not called yet)
        static void RainWhy(int path) { if (sCurRes != null) sCurRes.RainWhy[path]++; }
        static Result sCurRes;      // the result ScoreMeta fills (worker: RainUsed for the feature mask)
        static IntPtr sCreateSite;  // InteractionDefinition.CreateInstance(IGameObject, IActor, InteractionPriority, bool, bool)
        static object sRainObj;     // worker only: the armed pair's target
        static byte sRainBit;
        static bool sRainUsed;

        static void ShelterFill(Request q)
        {
            if (!sRainAnswer) return;
            Array.Clear(q.Shelter, 0, q.Objs);
            try
            {
                if (!Sims3.Gameplay.Seasons.SeasonsManager.Enabled) return;
                Sims3.SimIFace.Enums.Weather w = Sims3.Gameplay.Seasons.SeasonsManager.CurrentWeather;
                if (w != Sims3.SimIFace.Enums.Weather.Rain && w != Sims3.SimIFace.Enums.Weather.Hail) return;
                for (int o = 0; o < q.Objs; o++)
                {
                    IMetaObject m = q.Obj[o];
                    if (m == null || m.HasBeenDestroyed) continue;
                    bool sim = m is Sim, indoorsCapable = m is ICanCountAsBeingIndoors, outside = !sim && !indoorsCapable && m.IsOutside;
                    q.Shelter[o] = AutShadowCore.ShelterBit(true, sim, indoorsCapable, outside,
                        outside && Sims3.Gameplay.Seasons.SeasonsManager.IsShelteredFromPrecipitation(m.Position));
                }
            }
            catch (Exception e) { Array.Clear(q.Shelter, 0, q.Objs); PatchCounters()[121]++; AutNote("shelter: " + e.Message); }
        }

        // worker: arm the pair's CreateInstance skip (false = abort `rain`)
        static bool RainArmPair(Request q, int o, InteractionObjectPair iop)
        {
            if (!sRainAnswer || q == null || iop == null || o < 0 || o >= q.Objs) return false;
            // armed even with no bit (rain-h11 bp-rain-moder: rainArm 2203, rainSkip 0): the meta definitions' CalculateScore
            // returns 0 outside local autonomy and never reaches the branch [verified, decompiled MetaInteractionDefinition],
            // so most rain pairs need no answer at all; the hook aborts `rain` only if the branch is reached with no bit
            sRainObj = iop.Target; sRainUsed = false;
            sRainBit = (object)q.Obj[o] == (object)iop.Target ? q.Shelter[o] : AutShadowCore.kShelterNone;
            sRainArmRet = AutShadowNative.RainArm(iop.InteractionDefinition, sCreateSite);
            if (sRainArmRet == 1) return true;
            sRainObj = null;
            return false;
        }
        static bool RainDisarm()
        {
            bool ok = AutShadowCore.ShelterDone(AutShadowNative.RainArm(null, IntPtr.Zero), sRainUsed);
            if (ok && sCurRes != null) sCurRes.RainUsed++;
            sRainObj = null; sRainBit = 0; sRainUsed = false;
            return ok;
        }

        // SeasonsManager.IsShelteredFromPrecipitation(IGameObject, InteractionInstance): the game's method off the worker; on
        // the worker the armed target's skipped call (null instance) gets the cut bit, anything else aborts `rain`.
        public static class AutShelterHook
        {
            public static bool ShelterHook(IGameObject obj, InteractionInstance ii)
            {
                if (!OnWorker()) return ShelterOrig(obj, ii);
                int a = AutShadowCore.ShelterAnswer(ii == null, obj != null && (object)obj == sRainObj, sRainBit);
                if (a < 0) { RainWhy(2); AutShadowNative.Abort(kReasonRain); return false; }
                sRainUsed = true;
                return a == 1;
            }
            public static bool ShelterOrig(IGameObject obj, InteractionInstance ii) { return false; } // receives the game's original header
        }

        // docs/206 §15 (PR #107 review, tier A): the methods only the worker reached first. satb5/satb6 runs gave up on these 7
        // every session (cold transform / native-call thunk on the worker): run each once here on the sim thread at the
        // warm-up, where they do nothing. Abort / ResetAbort / IsWorker act only on the worker (ts3native 0.36.143: Abort too).
        // The Dictionary`2.get_Count instance is not named by the log yet: not warmed here.
        static void ColdWarm()
        {
            try
            {
                AutShadowNative.IsWorker();
                AutShadowNative.ResetAbort();
                AutShadowNative.Abort(0);
                RainShelterBranch(null, null);
                RainArmPair(null, -1, null);
                RainDisarm();  // RainArm on the sim thread: -1, a no-op (transforms the IC thunk here)
                RainWhy(0); sCurRes = null;  // worker-only callee (sCurRes null here: no write)
                AutShadowCore.ShelterAnswer(true, false, 0);  // worker-only callee of the hook
                sRain.SeasonsOn();
                foreach (Sim s in LotManager.Actors)
                    if (s != null && s.InWorld)
                    {
                        sRain.Target = s; sRain.TargetOutside();
                        if (sShelterHooked) Sims3.Gameplay.Seasons.SeasonsManager.IsShelteredFromPrecipitation(s, null);  // hook body, sim side
                        break;
                    }
            }
            catch (Exception) { }
            finally { sRain.Target = null; }
            // docs/214 + golive-dfn cold log: Mode 3 runs the reference only on VerifyEvery cuts, so the load warm-up's
            // AutReference returns before ScoreMeta / ScorePair / OffScore and the worker transformed them first. Each runs
            // once here with nothing to score (an empty request; ScorePair's null pair throws before any game call).
            try
            {
                AutShadowNative.Stopped();
                AutShadowNative.Aborted();
                AutShadowNative.RngUnit(-1);  // negative unit: a no-op on every thread
                Request q = new Request();
                q.Objs = q.Pairs = 0;
                ScoreMeta(q, new Result());
                if (!sOffPosted) OffScore(false);  // returns at once with nothing posted
                float s;
                try { ScorePair(q, 0, null, out s); } catch (NullReferenceException) { }
            }
            catch (Exception) { }
            // the AutRoomHook.Cached closed Dictionary`2.get_Count instance (a lot's mRoomRestrictionCheckCallbacks through
            // ICollection.Count): AutRoomHook.Warm returns before it (lot 0)
            try
            {
                foreach (Lot lot in LotManager.AllLots)
                {
                    ICollection cb = lot != null ? fRoomCallbacks.GetValue(lot) as ICollection : null;
                    if (cb != null) { int n = cb.Count; break; }
                }
            }
            catch (Exception) { }
        }

        // ---- hooks ----

        // B13 (private; its YieldResult is a private enum, so int stands in for it). Request prefix, then the game's own
        // method, recorded for the L report while it runs (it may yield across ticks).
        public class AutMetaHook
        {
            public int MetaHook(CommodityKind c)
            {
                AutonomyT a = (AutonomyT)(object)this;
                L0Close(a);  // tier-2 L0: B9 is over when B13 starts (AutLocalProbe.cs)
                bool mine = false;
                if (!sSessionOff && ShortcutOn("autshadow"))
                {
                    int live = LiveWanted();  // docs/124 §17: Mode 3 = go-live (autlive on) or the game's search (off), never a shadow request
                    if (live != 0)
                    {
                        MetaStart(a);  // G8 request-to-push latency (AutLive.cs)
                        bool sc = LedgerOpen();
                        try { return live > 0 ? LiveMeta(this, a, c) : MetaOrig(c); }
                        finally { if (sc) LedgerClose(); }
                    }
                    try { TryRequest(a, c, false); } catch (Exception) { PatchCounters()[121]++; }
                    mine = sGameAut == a && sPosted;
                }
                bool scope = LedgerOpen();
                if (!mine && !scope) return MetaOrig(c);
                try { return MetaOrig(c); }
                finally
                {
                    if (scope) LedgerClose();
                    if (mine && sGameAut == a) { sGameAut = null; sGameDone = true; TryLReport(); }
                }
            }
            public int MetaOrig(CommodityKind c) { return 0; } // receives Autonomy.ScoreInteractionsForMetaAutonomy's original header
        }

        // C2 entry used by C1: records what the game scored for the sampled search (same answer returned), and times each
        // pair (docs/122 §14.23): allow-listed (in the request with an allow-listed definition and implementor) or off-list,
        // pass (score != 0) or fail (a failed Test scores 0 in C2) [inferred: a passing pair can also score 0 and counts as fail]
        public class AutScoreHook
        {
            public float ScoreHook(InteractionObjectPair iop)
            {
                if (sL0Aut != null && (object)this == (object)sL0Aut) sL0Pairs++;  // tier-2 L0 pair count (AutLocalProbe.cs)
                LiveSearch L = LiveOf(this);  // docs/124 §17: a go-live search's live scores (drift, dry compare)
                if (L != null)
                {
                    if (L != sLiveCur) LiveStat(kSC2Rehomed);
                    float ls = ScoreOrig(iop);
                    LiveScored(L, iop, ls);
                    return ls;
                }
                if (sGameAut == null || (object)this != sGameAut) return ScoreOrig(iop);
                int t0 = AutShadowNative.PairT0();
                float s = ScoreOrig(iop);
                int i;
                Request q = sReqs[0];  // the batch's first request (sGameAut's)
                bool listed = false;
                if (iop != null && q.PairIndex.TryGetValue(iop, out i))
                {
                    q.GameScore[i] = s; q.GameHas[i] = true;
                    listed = q.Def[i] >= 0 && q.ObjImpl[q.PairObj[i]] >= 0;
                }
                AutShadowNative.PairT1((listed ? 0 : 2) + (s != 0f ? 0 : 1), t0);
                return s;
            }
            public float ScoreOrig(InteractionObjectPair iop) { return 0f; } // receives Autonomy.CalculateScore(iop)'s original header
        }

        // Sim.IsAllowedInRoom(lotId, roomId) on the worker: read the Sim's room cache, never recalculate it (a W write).
        public class AutRoomHook
        {
            public bool AllowedHook(ulong lotId, int roomId)
            {
                if (!OnWorker())
                {   // docs/125 addendum §4.1: a recalculation inside the reference draws from a scratch stream
                    if (!sInRef) return AllowedOrig(lotId, roomId);
                    AutShadowNative.RngPause(1);
                    try { return AllowedOrig(lotId, roomId); }
                    finally { AutShadowNative.RngPause(0); }
                }
                Sim s = (Sim)(object)this;
                Lot cur = s.LotCurrent;
                if (cur == null) { AutShadowNative.Abort(10); return false; }
                if (lotId != cur.LotId && !Cached(s, cur.LotId, 0, false)) return false;
                return Cached(s, lotId, roomId == -1 ? 0 : roomId, false);
            }
            public bool AllowedOrig(ulong lotId, int roomId) { return false; } // receives Sim.IsAllowedInRoom(ulong, int)'s original header

            // I-122-37: mint transforms a method on its first call; warm Cached on the sim thread (lot 0 -> early return, no abort).
            internal static void Warm() { Cached(null, 0, 0, true); }

            static bool Cached(Sim s, ulong lotId, int roomId, bool warm)
            {
                Lot lot = LotManager.GetLot(lotId);
                if (lot == null) { if (!warm) AutShadowNative.Abort(10); return false; }
                ICollection cb = fRoomCallbacks.GetValue(lot) as ICollection;
                if (cb != null && cb.Count > 0) { AutShadowNative.Abort(9); return false; }
                Dictionary<ulong, Pair<int, Dictionary<int, bool>>> rooms = fAllowedRooms.GetValue(s) as Dictionary<ulong, Pair<int, Dictionary<int, bool>>>;
                Pair<int, Dictionary<int, bool>> e;
                if (rooms == null || !rooms.TryGetValue(lotId, out e) || e.Second == null || lot.GetObjectCacheVersion<IPortalConnectionObject>() != e.First)
                { AutShadowNative.Abort(10); return false; }
                bool v;
                return e.Second.TryGetValue(roomId, out v) && v;
            }
        }

        // Sim.NumSimsDoingGardening on the worker: the count read at the request, no clock write (I-121-15).
        public static class AutGardenHook
        {
            public static int GardenHook() { return OnWorker() ? sGardening : GardenOrig(); }
            public static int GardenOrig() { return 0; } // receives Sim.get_NumSimsDoingGardening's original header
        }

        // Localization.LocalizeString on the worker: the rooted empty string (no EAText, no FillInTokens RNG draw).
        public static class AutLocHook
        {
            public static string KeyHook(ulong stringKey) { if (OnWorker()) return sEmpty; if (!sInRef) return KeyOrig(stringKey); AutShadowNative.RngPause(1); try { return KeyOrig(stringKey); } finally { AutShadowNative.RngPause(0); } }
            public static string KeyOrig(ulong stringKey) { return null; }
            public static string StrHook(string entryKey, object[] parameters) { if (OnWorker()) return sEmpty; if (!sInRef) return StrOrig(entryKey, parameters); AutShadowNative.RngPause(1); try { return StrOrig(entryKey, parameters); } finally { AutShadowNative.RngPause(0); } }
            public static string StrOrig(string entryKey, object[] parameters) { return null; }
            public static string FemHook(bool isFemale, string entryKey, object[] parameters) { if (OnWorker()) return sEmpty; if (!sInRef) return FemOrig(isFemale, entryKey, parameters); AutShadowNative.RngPause(1); try { return FemOrig(isFemale, entryKey, parameters); } finally { AutShadowNative.RngPause(0); } }
            public static string FemOrig(bool isFemale, string entryKey, object[] parameters) { return null; }
            public static string FemsHook(bool[] isFemale, string entryKey, object[] parameters) { if (OnWorker()) return sEmpty; if (!sInRef) return FemsOrig(isFemale, entryKey, parameters); AutShadowNative.RngPause(1); try { return FemsOrig(isFemale, entryKey, parameters); } finally { AutShadowNative.RngPause(0); } }
            public static string FemsOrig(bool[] isFemale, string entryKey, object[] parameters) { return null; }
        }

        // H4: OnlineDatingManager.DidSimsMeetOnline on the worker reads the records and never creates one (a missing record
        // answers as a fresh one: no accepted dates, I-122-15).
        public static class AutDatingHook
        {
            public static bool MetHook(ulong a, ulong b)
            {
                if (!OnWorker()) return ShortcutOn("datingprofile") ? MetPeek(a, b) : MetOrig(a, b);
                if (!OnlineDatingManager.IsActive) return false;
                IDictionary recs = fDatingRecords.GetValue(OnlineDatingManager.Instance) as IDictionary;
                return recs != null && (Accepted(recs, a, b) || Accepted(recs, b, a));
            }
            public static bool MetOrig(ulong a, ulong b) { return false; } // receives OnlineDatingManager.DidSimsMeetOnline's original header

            // datingprofile (DatingProfile.cs, docs/528): the sim thread's answer without EA's GetRecordForSim, which adds an
            // empty record for each Sim it is asked about. EA's body answers false for a Sim with no accepted dates, as a
            // missing record does here. [2052] calls where EA's body would have made a record.
            static bool MetPeek(ulong a, ulong b)
            {
                if (!OnlineDatingManager.IsActive) return false;
                IDictionary recs = fDatingRecords.GetValue(OnlineDatingManager.Instance) as IDictionary;
                if (recs == null) return false;
                if (!recs.Contains(a) || !recs.Contains(b)) PatchCounters()[2052]++;
                return Accepted(recs, a, b) || Accepted(recs, b, a);
            }

            // I-122-37: the game never calls IDictionary.Contains/indexer on this dictionary type; run them once on the sim thread.
            // Dictionary<ulong, OnlineDatingRecord>'s IDictionary.Add refuses a null value ("not of type", mono-cost3) and its
            // indexer throws on a missing key, so the warm-up holds one real record (private parameterless constructor).
            internal static void Warm()
            {
                IDictionary d = Activator.CreateInstance(fDatingRecords.FieldType) as IDictionary;
                if (d == null) return;
                d.Add(1UL, Activator.CreateInstance(fAcceptedDates.DeclaringType, true));
                Accepted(d, 1UL, 2UL);
                Accepted(d, 2UL, 1UL);
                new List<ulong>(1).Contains(2UL);
            }

            static bool Accepted(IDictionary recs, ulong owner, ulong other)
            {
                object rec = recs.Contains(owner) ? recs[owner] : null;
                List<ulong> dates = rec != null ? fAcceptedDates.GetValue(rec) as List<ulong> : null;
                return dates != null && dates.Contains(other);
            }
        }

        // H5: the tuning's reflection desire function is unknown to the closure: abort on the worker.
        public class AutDesireHook
        {
            public float DesireHook(IMetaObject venue, float hours24, bool bAtleastOneActiveSimAtVenue)
            {
                if (!OnWorker()) return DesireOrig(venue, hours24, bAtleastOneActiveSimAtVenue);
                AutShadowNative.Abort(15);
                return 0f;
            }
            public float DesireOrig(IMetaObject venue, float hours24, bool bAtleastOneActiveSimAtVenue) { return 0f; }
        }

        // docs/125 addendum §4.3: Posture.Interactions fills its list lazily on first read (TryPopulateInteractionList, which
        // for SwimmingInPool calls Pool.AddSkinnyDipper: world writes, reactions, RNG). The request pre-warms the actor's
        // postures on the sim thread; on the worker an unfilled list aborts (posture) and answers an empty list.
        public class AutPostureHook
        {
            public List<InteractionObjectPair> ListHook()
            {
                if (!OnWorker()) return ListOrig();
                List<InteractionObjectPair> l = fPostureList.GetValue(this) as List<InteractionObjectPair>;
                if (l != null) return l;
                AutShadowNative.Abort(kReasonPosture);
                return sNoPostureList;
            }
            public List<InteractionObjectPair> ListOrig() { return null; } // receives Posture.get_Interactions' original header
        }
        // H12 (docs/127 §7.2): SimRoutingComponent.CreateRoute(), CreateRoute(flags), CreateRouteAsAdult() on the worker
        // return a handle-0 ScriptCore.Route from its public parameterless constructor (no Route_CreateRoute, no IC, no
        // static write [verified: methods.csv]; its finalizer skips Route_DestroyRoute for handle 0) and skip
        // SetRouteOptions (it reaches the UIManager wrapper cache). Any route IC the Test then calls aborts `route` in the
        // ts3native guard (never the body). Sim thread and reference: the game's method.
        public class AutRouteHook
        {
            public Route RouteHook() { return OnWorker() ? Stub() : RouteOrig(); }
            public Route RouteOrig() { return null; } // receives SimRoutingComponent.CreateRoute()'s original header
            public Route AgsHook(Sims3.SimIFace.CAS.CASAgeGenderFlags ags) { return OnWorker() ? Stub() : AgsOrig(ags); }
            public Route AgsOrig(Sims3.SimIFace.CAS.CASAgeGenderFlags ags) { return null; } // CreateRoute(CASAgeGenderFlags)'s header
            public Route AdultHook() { return OnWorker() ? Stub() : AdultOrig(); }
            public Route AdultOrig() { return null; } // receives SimRoutingComponent.CreateRouteAsAdult()'s original header
            static Route Stub() { return new ScriptCore.Route(); }
            // I-122-37: the constructor is transformed on the sim thread first (the handle-0 route is dropped at once)
            internal static void Warm() { Stub(); }
        }

        // H6: Punishment.GetPunishmentManagerFromSim on the worker: an existing manager is read; creating one aborts.
        public static class AutPunishHook
        {
            public static Punishment PunishHook(Sim sim)
            {
                if (!OnWorker()) return PunishOrig(sim);
                if (sim == null || !sim.CanBePunished) return null;
                if (sim.Punishment == null) AutShadowNative.Abort(16);
                return sim.Punishment;
            }
            public static Punishment PunishOrig(Sim sim) { return null; } // receives Punishment.GetPunishmentManagerFromSim's original header
        }

        // ---- tier-2 P1 (docs/219 §3.1, docs/data/tier2-rows35-handoff.md) ----
        // Worker guards for the CalculateScoreForObjectInteraction base path. The six get_InUse overrides that can destroy
        // their prop abort (`inuse`, switch autt2inuse); FireManager.HasOnlyLeafPileFires with fires on the lot aborts
        // (`fire`, switch autt2fire: its leaf-pile test reaches the placement IC); the drag model singleton is created on
        // the sim thread at the request (switch autt2dragwarm). All three default off; off = the game's methods unchanged.
        // Native reason `other`; by class in counters 884-890.
        // Household Sims: no worker branch here answers anything a household Sim could get; every one aborts, so the unit
        // goes back to the sim thread. A household, selected or unknown-scope actor (Request.T2Hh) takes its own abort
        // branch first (AutShadowCore.T2Site 2), also where the fire hook would otherwise answer from the empty fire list.
        static bool sT2InUseHooked, sT2FireHooked;
        static FieldInfo fFires, fDragSingleton;

        static void T2P1Hooks(List<string> failed)
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            Type[] tt = new Type[AutShadowCore.kT2InUseTypes.Length];
            int present = 0;
            for (int i = 0; i < tt.Length; i++)
            {
                foreach (string n in kGameModules) { Assembly a = GameAssembly(n); if (a != null && (tt[i] = a.GetType(AutShadowCore.kT2InUseTypes[i], false)) != null) break; }
                if (tt[i] != null) present++;  // null: its pack is not installed, no such prop exists
            }
            // one call per type, literal names (tools/swap-stack-audit.ts reads them; docs/242)
            Type soil = tt[0], hoop = tt[1], arcade = tt[2], skewer = tt[3], cone = tt[4], prop = tt[5];
            bool allIn = (soil == null || Hook(failed, soil, "get_InUse", null, typeof(AutInUseHook), "SoilHook", "SoilOrig"))
                & (hoop == null || Hook(failed, hoop, "get_InUse", null, typeof(AutInUseHook), "HoopHook", "HoopOrig"))
                & (arcade == null || Hook(failed, arcade, "get_InUse", null, typeof(AutInUseHook), "ArcadeHook", "ArcadeOrig"))
                & (skewer == null || Hook(failed, skewer, "get_InUse", null, typeof(AutInUseHook), "SkewerHook", "SkewerOrig"))
                & (cone == null || Hook(failed, cone, "get_InUse", null, typeof(AutInUseHook), "ConeHook", "ConeOrig"))
                & (prop == null || Hook(failed, prop, "get_InUse", null, typeof(AutInUseHook), "PropHook", "PropOrig"));
            sT2InUseHooked = allIn && present > 0;  // one unpatched present override = the switch stays off
            fFires = typeof(Sims3.Gameplay.Controllers.FireManager).GetField("mFires", all);
            if (fFires == null) failed.Add("FireManager.mFires");
            else sT2FireHooked = Hook(failed, typeof(Sims3.Gameplay.Controllers.FireManager), "HasOnlyLeafPileFires", null, typeof(AutFireHook), "FireHook", "FireOrig");
            fDragSingleton = typeof(Sims3.Gameplay.UI.LiveDragHelperModel).GetField("gSingleton", all);
            if (fDragSingleton == null) failed.Add("LiveDragHelperModel.gSingleton");
        }

        // Sim thread at the request (after PreWarm): latch the actor's scope and the switches into the request.
        static void T2P1Fill(Request q, Sim actor)
        {
            q.T2Hh = true; q.T2InUse = q.T2Fire = false;
            try
            {
                Sims3.Gameplay.CAS.SimDescription d = actor != null ? actor.SimDescription : null;
                Sims3.Gameplay.CAS.Household h = d != null ? d.Household : null;
                q.T2Hh = AutShadowCore.T2Household(actor == null, actor != null && actor.HasBeenDestroyed, d == null,
                    actor != null && actor.IsSelectable, h != null && h == Sims3.Gameplay.CAS.Household.ActiveHousehold);
                q.T2InUse = sT2InUseHooked && ShortcutOn("autt2inuse");
                q.T2Fire = sT2FireHooked && ShortcutOn("autt2fire");
                // a UI model with no Sim state (docs/126 §3 pre-warm): made for any actor's request, as the game's first read would
                if (fDragSingleton != null && ShortcutOn("autt2dragwarm") && fDragSingleton.GetValue(null) == null
                    && Sims3.Gameplay.UI.LiveDragHelperModel.Singleton != null) PatchCounters()[kCT2Drag]++;
            }
            catch (Exception e) { PatchCounters()[121]++; AutNote("t2 p1 fill: " + e.Message); }
        }

        // Worker: true = aborted (the hook returns its abort answer), false = run the game's method.
        static bool T2Cut(int slot, bool on, bool household)
        {
            int s = AutShadowCore.T2Site(true, on, household);
            if (s == 0) return false;
            if (s == 1 && sCurRes != null) sCurRes.T2Ab[slot]++;
            AutShadowNative.Abort(kReasonOther);
            return true;
        }

        // The six get_InUse overrides (AutShadowCore.kT2InUseTypes order): abort answer `in use`.
        public class AutInUseHook
        {
            static bool Cut(int slot) { return OnWorker() && T2Cut(slot, sReq.T2InUse, sReq.T2Hh); }
            public bool SoilHook() { return Cut(0) || SoilOrig(); }
            public bool SoilOrig() { return false; } // receives Soil.get_InUse's original header
            public bool HoopHook() { return Cut(1) || HoopOrig(); }
            public bool HoopOrig() { return false; } // BBallHoop+BBallHoopBall.get_InUse
            public bool ArcadeHook() { return Cut(2) || ArcadeOrig(); }
            public bool ArcadeOrig() { return false; } // BBallArcade+BBallArcadeBall.get_InUse
            public bool SkewerHook() { return Cut(3) || SkewerOrig(); }
            public bool SkewerOrig() { return false; } // ChocolateFountain+ChocolateFountainSkewer.get_InUse
            public bool ConeHook() { return Cut(4) || ConeOrig(); }
            public bool ConeOrig() { return false; } // IceCreamMaker+IceCreamCone.get_InUse
            public bool PropHook() { return Cut(5) || PropOrig(); }
            public bool PropOrig() { return false; } // GardenStation+GardenStationProp.get_InUse
        }

        // FireManager.HasOnlyLeafPileFires: with no fire list or an empty one the game's answer needs no leaf-pile test
        // (false / true, the game's own early outs); a fire aborts `fire` with the answer `not only leaf piles`.
        public class AutFireHook
        {
            public bool FireHook()
            {
                if (!OnWorker() || !sReq.T2Fire) return FireOrig();
                if (sReq.T2Hh) { T2Cut(AutShadowCore.kT2Fire, true, true); return false; }  // household: abort, uncounted
                ICollection f = fFires.GetValue(this) as ICollection;
                if (f == null) return false;
                if (f.Count == 0) return true;
                T2Cut(AutShadowCore.kT2Fire, true, false);
                return false;
            }
            public bool FireOrig() { return false; } // receives FireManager.HasOnlyLeafPileFires' original header
        }

        // ---- tier-2 H7-H10 (docs/126 §3, docs/data/tier2-rows35-handoff.md) ----
        // Worker hooks for the local-phase test closures; the sim thread (and the Mode R reference) runs the game's method.
        // Each has its own switch, default off; off = the game's method unchanged on every thread.
        //   H7 LiveDragHelperModel.get_CachedTopDraggedObject / get_TopDraggedObject (autt2drag): the object latched at the
        //      request; the game's getter fills a static cache and asks the scene window (UIManager ICs).
        //   H8 Autonomy.UpdateCacheIfNeeded (autt2cache): the request's autonomy with its search cache on at the request =
        //      no-op (the game's own answer inside a search); anything else aborts `cache`.
        //   H9 InteractionObjectPair.AddTuning (autt2tuning): both static caches hit = the cached tuning on the pair (the
        //      worker-built pair's own field); a miss would add to a static Dictionary: abort `tuning`. The sim thread is
        //      quiescent while the worker runs (docs/122), so the reads see no concurrent add.
        //   H10 Autonomy.get_IsSufficientlyOnScreenForHighLODSimulation (autt2lod): the request's autonomy answers its stored
        //      mbHighLOD, no recompute (the recompute can set a posture state-machine parameter); others abort.
        // Native reason `other` (no native change); counters 898-902 (fixes-898).
        // Household Sims: a household, selected or unknown-scope actor (Request.T2Hh, T2P1Fill) never gets a worker answer:
        // every H7-H10 site takes an explicit abort branch for it first (uncounted). H8 and H10 answer only for the
        // request's own autonomy, whose actor is that NPC; any other Sim's autonomy aborts.
        static bool sT2DragHooked, sT2CacheHooked, sT2TuningHooked, sT2LodHooked;
        static FieldInfo fDragCached, fDragCache, fUseCached, fHighLod, fLodTick, fReqTuning, fTuning, fTargetType, fTuningMember;

        static void T2HHooks(List<string> failed)
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            Type drag = typeof(Sims3.Gameplay.UI.LiveDragHelperModel), iop = typeof(InteractionObjectPair);
            fDragCached = drag.GetField("sTopDraggedObjectCached", all); fDragCache = drag.GetField("sTopDraggedObjectCache", all);
            fUseCached = typeof(AutonomyT).GetField("mUseCachedValues", all);
            fHighLod = typeof(AutonomyT).GetField("mbHighLOD", all); fLodTick = typeof(AutonomyT).GetField("mLODCheckTick", all);
            fReqTuning = iop.GetField("sRequiresTuningCache", all); fTargetType = iop.GetField("mTargetType", all);
            fTuningMember = iop.GetField("mTuning", all);
            if (fDragCached == null || fDragCache == null) failed.Add("LiveDragHelperModel.sTopDraggedObjectCache*");
            else sT2DragHooked = Hook(failed, drag, "get_CachedTopDraggedObject", null, typeof(AutDragHook), "CachedHook", "CachedOrig")
                & Hook(failed, drag, "get_TopDraggedObject", null, typeof(AutDragHook), "TopHook", "TopOrig");
            if (fUseCached == null) failed.Add("Autonomy.mUseCachedValues");
            else sT2CacheHooked = Hook(failed, typeof(AutonomyT), "UpdateCacheIfNeeded", new Type[] { typeof(IGameObject) }, typeof(AutCacheHook), "CacheHook", "CacheOrig");
            if (fReqTuning == null || fTargetType == null || fTuningMember == null) failed.Add("InteractionObjectPair tuning fields");
            else sT2TuningHooked = Hook(failed, iop, "AddTuning", null, typeof(AutTuningHook), "TuningHook", "TuningOrig");
            if (fHighLod == null || fLodTick == null) failed.Add("Autonomy.mbHighLOD/mLODCheckTick");
            else sT2LodHooked = Hook(failed, typeof(AutonomyT), "get_IsSufficientlyOnScreenForHighLODSimulation", null, typeof(AutLodHook), "LodHook", "LodOrig");
        }

        // Sim thread at the request (B13 prefix, inside the game's search): latch the switches and the values the worker
        // answers from. Reads only; the drag read may create the drag UI model singleton (no Sim state, as P1's pre-warm).
        static void T2HFill(Request q)
        {
            q.T2Drag = q.T2Cache = q.T2Tuning = q.T2Lod = q.T2CacheActive = false;
            q.T2DragCached = q.T2DragTop = null; q.T2Tick = 0;
            try
            {
                q.T2Drag = sT2DragHooked && ShortcutOn("autt2drag");
                q.T2Cache = sT2CacheHooked && ShortcutOn("autt2cache");
                q.T2Tuning = sT2TuningHooked && ShortcutOn("autt2tuning");
                q.T2Lod = sT2LodHooked && ShortcutOn("autt2lod");
                if (q.T2Drag && !q.T2Hh)
                {
                    q.T2DragTop = Sims3.Gameplay.UI.LiveDragHelperModel.Singleton.TopDraggedObject;  // our hook passes through here
                    q.T2DragCached = (bool)fDragCached.GetValue(null) ? fDragCache.GetValue(null) as IGameObject : q.T2DragTop;
                }
                if (q.T2Cache && q.Aut != null) q.T2CacheActive = (bool)fUseCached.GetValue(q.Aut);
                if (q.T2Lod) q.T2Tick = Simulator.TicksElapsed();
            }
            catch (Exception e) { q.T2Drag = q.T2Cache = q.T2Tuning = q.T2Lod = false; PatchCounters()[121]++; AutNote("t2 h fill: " + e.Message); }
        }

        // Worker, switch on: 0 answer (count `answered` if >= 0), else abort (household uncounted, else `abortSlot`).
        static bool T2HAbort(bool household, int abortSlot)
        {
            if (!household && abortSlot >= 0 && sCurRes != null) sCurRes.T2H[abortSlot]++;
            AutShadowNative.Abort(kReasonOther);
            return true;
        }
        static void T2HCount(int slot) { if (sCurRes != null) sCurRes.T2H[slot]++; }

        // H7 worker answer: the dragged object latched at the request, for the static and the instance getter (abort answer null)
        public class AutDragHook
        {
            static bool Cut()
            {
                if (!sReq.T2Hh) { T2HCount(AutShadowCore.kT2HDrag); return false; }
                return T2HAbort(true, -1);
            }
            public static IGameObject CachedHook()
            {
                if (!OnWorker() || !sReq.T2Drag) return CachedOrig();
                return Cut() ? null : sReq.T2DragCached;
            }
            public static IGameObject CachedOrig() { return null; } // receives LiveDragHelperModel.get_CachedTopDraggedObject's original header
            public IGameObject TopHook()
            {
                if (!OnWorker() || !sReq.T2Drag) return TopOrig();
                return Cut() ? null : sReq.T2DragTop;
            }
            public IGameObject TopOrig() { return null; } // LiveDragHelperModel.get_TopDraggedObject
        }

        // H8 Autonomy.UpdateCacheIfNeeded(obj): worker no-op inside the request's search, else abort `cache`
        public class AutCacheHook
        {
            public void CacheHook(IGameObject obj)
            {
                if (!OnWorker() || !sReq.T2Cache) { CacheOrig(obj); return; }
                if (sReq.T2Hh) { T2HAbort(true, -1); return; }
                if (AutShadowCore.T2CacheNoop((object)this == (object)sReq.Aut, sReq.T2CacheActive)) return;
                T2HAbort(false, AutShadowCore.kT2HCache);
            }
            public void CacheOrig(IGameObject obj) { } // receives Autonomy.UpdateCacheIfNeeded's original header
        }

        // H9 InteractionObjectPair.AddTuning: worker reads both static caches (the game's body), a miss aborts `tuning`
        public class AutTuningHook
        {
            public void TuningHook()
            {
                if (!OnWorker() || !sReq.T2Tuning) { TuningOrig(); return; }
                if (sReq.T2Hh) { T2HAbort(true, -1); return; }
                InteractionObjectPair p = (InteractionObjectPair)(object)this;
                if (p.InteractionDefinition == null) return;
                Type type = p.InteractionDefinition.GetType();
                bool needs;
                if (!((Dictionary<Type, bool>)fReqTuning.GetValue(null)).TryGetValue(type, out needs)) { T2HAbort(false, AutShadowCore.kT2HTuning); return; }
                if (!needs) return;
                Type target = p.Target == null ? fTargetType.GetValue(p) as Type : p.Target.GetType();
                if (target == null) return;
                InteractionTuning t;
                if (!InteractionObjectPair.sTuningCache.TryGetValue(new Sims3.Gameplay.Utilities.Pair<Type, Type>(type, target), out t))
                { T2HAbort(false, AutShadowCore.kT2HTuning); return; }
                fTuningMember.SetValue(p, t);  // the pair's own field, as the game's hit path
            }
            public void TuningOrig() { } // receives InteractionObjectPair.AddTuning's original header
        }

        // H10 Autonomy.get_IsSufficientlyOnScreenForHighLODSimulation: worker answers the stored mbHighLOD, no recompute
        public class AutLodHook
        {
            public bool LodHook()
            {
                if (!OnWorker() || !sReq.T2Lod) return LodOrig();
                if (sReq.T2Hh || (object)this != (object)sReq.Aut) { T2HAbort(sReq.T2Hh, -1); return false; }
                T2HCount(AutShadowCore.kT2HLod);
                if (AutShadowCore.T2LodStale(sReq.T2Tick, (long)fLodTick.GetValue(this))) T2HCount(AutShadowCore.kT2HLodStale);
                return (bool)fHighLod.GetValue(this);
            }
            public bool LodOrig() { return false; } // receives Autonomy.get_IsSufficientlyOnScreenForHighLODSimulation's original header
        }
    }
}
